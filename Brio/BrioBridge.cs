using Dalamud.Plugin.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace XcpCam.BrioInterop;

/// <summary>
/// Reflection bridge to Brio internals (Brio exposes no IPC for its timeline, cameras or XAT player).
/// All member names live here so a Brio update only needs fixes in one place. Every call must run on the
/// framework thread. Resolution is retried periodically; missing members disable the features using them.
/// Verified against Brio 0.8.0.12.
/// </summary>
public sealed class BrioBridge
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IPluginLog _log;
    private Assembly? _asm;
    private DateTime _nextResolve = DateTime.MinValue;

    public string Status { get; private set; } = "未连接";
    public string? Version { get; private set; }
    public bool IsConnected => _timeline != null;
    public List<string> Missing { get; } = [];

    // services
    private object? _timeline, _cutscene, _config, _vcm;

    // types
    private Type? _tTimelineSvc, _tCutscene, _tXatFile, _tCamTimelineCap, _tTrack, _tCamKeyframe, _tCamComponents, _tInterp, _tCameraEntity, _tEntity, _tVirtualCamera;

    public bool CanSync => _cutscene != null && _tXatFile != null && Has("CutsceneManager.CameraPath", "CutsceneManager.StartPlayback");
    public bool CanBake => _tCamTimelineCap != null && _tTrack != null && _tCamKeyframe != null && Has("TimelineTrack.AddCameraKeyframe");

    private readonly HashSet<string> _found = [];
    private bool Has(params string[] names) => names.All(_found.Contains);

    public BrioBridge(IPluginLog log) { _log = log; }

    /// <summary>Call every framework tick; cheap when already connected.</summary>
    public void Tick()
    {
        if(DateTime.UtcNow < _nextResolve) return;
        _nextResolve = DateTime.UtcNow.AddSeconds(IsConnected ? 10 : 3);

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "Brio")
                .OrderByDescending(a => a.GetName().Version)
                .FirstOrDefault(a => GetServices(a) != null);

            if(asm == null)
            {
                Reset("未找到已加载的 Brio");
                return;
            }

            // Re-resolve if Brio was reloaded (new assembly) or services went away
            if(!ReferenceEquals(asm, _asm) || _timeline == null || !ReferenceEquals(GetService(asm, _tTimelineSvc), _timeline))
                Resolve(asm);
        }
        catch(Exception ex)
        {
            Reset($"连接 Brio 失败：{ex.Message}");
            _log.Warning(ex, "BrioBridge resolve failed");
        }
    }

    private void Reset(string status)
    {
        _asm = null; _timeline = _cutscene = _config = _vcm = null;
        Status = status; Version = null;
    }

    private static IServiceProvider? GetServices(Assembly a)
    {
        var brio = a.GetType("Brio.Brio");
        return brio?.GetField("_services", Stat)?.GetValue(null) as IServiceProvider;
    }

    private static object? GetService(Assembly a, Type? t) => t == null ? null : GetServices(a)?.GetService(t);

    private void Resolve(Assembly asm)
    {
        _asm = asm;
        Missing.Clear();
        _found.Clear();
        Version = asm.GetName().Version?.ToString();

        T? Need<T>(T? v, string name) where T : class
        {
            if(v == null) Missing.Add(name); else _found.Add(name);
            return v;
        }

        _tTimelineSvc = Need(asm.GetType("Brio.Services.Timeline.TimelineService"), "TimelineService");
        _tCutscene = Need(asm.GetType("Brio.Game.Cutscene.CutsceneManager"), "CutsceneManager");
        _tXatFile = Need(asm.GetType("Brio.Files.XATCameraFile"), "XATCameraFile");
        _tCamTimelineCap = Need(asm.GetType("Brio.Capabilities.Timeline.CameraTimelineCapability"), "CameraTimelineCapability");
        _tTrack = Need(asm.GetType("Brio.Services.Timeline.TimelineTrack"), "TimelineTrack");
        _tCamKeyframe = Need(asm.GetType("Brio.Services.Timeline.CameraKeyframe"), "Timeline.CameraKeyframe");
        _tCamComponents = Need(asm.GetType("Brio.Services.Timeline.CameraComponents"), "CameraComponents");
        _tInterp = Need(asm.GetType("Brio.Services.Timeline.InterpolationMode"), "InterpolationMode");
        _tCameraEntity = Need(asm.GetType("Brio.Entities.Camera.CameraEntity"), "CameraEntity");
        _tEntity = Need(asm.GetType("Brio.Entities.Core.Entity"), "Entity");
        _tVirtualCamera = Need(asm.GetType("Brio.Game.Camera.VirtualCamera"), "VirtualCamera");
        var tConfigSvc = asm.GetType("Brio.Config.ConfigurationService");
        var tVcm = asm.GetType("Brio.Game.Camera.VirtualCameraManager");

        _timeline = GetService(asm, _tTimelineSvc);
        _cutscene = GetService(asm, _tCutscene);
        _config = tConfigSvc == null ? null : GetService(asm, tConfigSvc);
        _vcm = tVcm == null ? null : GetService(asm, tVcm);
        Need(_timeline, "service:TimelineService");
        Need(_cutscene, "service:CutsceneManager");
        Need(_config, "service:ConfigurationService");
        Need(_vcm, "service:VirtualCameraManager");

        void Member(Type? t, string member, string label, BindingFlags flags = Inst)
        {
            bool ok = t != null && (t.GetMember(member, flags).Length > 0);
            if(ok) _found.Add(label); else Missing.Add(label);
        }
        Member(_tTimelineSvc, "IsPlaying", "TimelineService.IsPlaying");
        Member(_tTimelineSvc, "_currentTime", "TimelineService._currentTime");
        Member(_tTimelineSvc, "FrameMin", "TimelineService.FrameMin");
        Member(_tTimelineSvc, "FrameMax", "TimelineService.FrameMax");
        Member(_tTimelineSvc, "ActiveHosts", "TimelineService.ActiveHosts");
        Member(_tTimelineSvc, "AddToTimeline", "TimelineService.AddToTimeline");
        Member(_tTimelineSvc, "ApplyCurrentFrame", "TimelineService.ApplyCurrentFrame");
        Member(_tCutscene, "CameraPath", "CutsceneManager.CameraPath");
        Member(_tCutscene, "StartPlayback", "CutsceneManager.StartPlayback");
        Member(_tCutscene, "StopPlayback", "CutsceneManager.StopPlayback");
        Member(_tCutscene, "IsRunning", "CutsceneManager.IsRunning");
        Member(_tCutscene, "CameraSettings", "CutsceneManager.CameraSettings");
        Member(_tCamTimelineCap, "_track", "CameraTimelineCapability._track");
        Member(_tTrack, "AddCameraKeyframe", "TimelineTrack.AddCameraKeyframe");
        Member(_tTrack, "Keyframes", "TimelineTrack.Keyframes");
        Member(_tEntity, "Capabilities", "Entity.Capabilities");
        Member(_tCameraEntity, "VirtualCamera", "CameraEntity.VirtualCamera");
        Member(_tCameraEntity, "CameraType", "CameraEntity.CameraType");
        Member(_tVirtualCamera, "FreeCamValues", "VirtualCamera.FreeCamValues");

        Status = IsConnected ? $"已连接 Brio {Version}" : "Brio 已加载，但未找到 TimelineService";
        _log.Info($"BrioBridge: {Status}; missing=[{string.Join(", ", Missing)}]");
    }

    // ---------------- generic helpers ----------------

    private static object? Get(object? o, string name)
    {
        if(o == null) return null;
        var t = o.GetType();
        var p = t.GetProperty(name, Inst);
        if(p != null) return p.GetValue(o);
        return t.GetField(name, Inst)?.GetValue(o);
    }

    private static void Set(object o, string name, object? value)
    {
        var t = o.GetType();
        var p = t.GetProperty(name, Inst);
        if(p != null && p.CanWrite) { p.SetValue(o, value); return; }
        var f = t.GetField(name, Inst);
        if(f != null) { f.SetValue(o, value); return; }
        // auto-property with private setter
        t.GetField($"<{name}>k__BackingField", Inst)?.SetValue(o, value);
    }

    private static object? Call(object? o, string name, params object?[] args)
    {
        if(o == null) return null;
        var m = o.GetType().GetMethods(Inst).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
        return m?.Invoke(o, args);
    }

    // ---------------- timeline ----------------

    public bool TimelineIsPlaying => Get(_timeline, "IsPlaying") is true;
    public double TimelineTime => Get(_timeline, "_currentTime") is double d ? d : 0;
    public int FrameMin => Get(_timeline, "FrameMin") is int i ? i : 0;
    public int FrameMax
    {
        get => Get(_timeline, "FrameMax") is int i ? i : 100;
        set { if(_timeline != null) Set(_timeline, "FrameMax", value); }
    }

    public int TimelineFps
    {
        get
        {
            var cfg = Get(Get(_config, "Configuration"), "Timeline");
            return Get(cfg, "PlaybackFramesPerSecond") is int fps && fps > 0 ? fps : 60;
        }
    }

    public bool TimelineLoop => Get(Get(Get(_config, "Configuration"), "Timeline"), "Loop") is true;

    /// <summary>
    /// Freezes / unfreezes the GPose target's animation through Brio's ActionTimelineCapability so Brio's own
    /// speed hook keeps it at that speed (needed when we scrub the animation ourselves).
    /// </summary>
    public bool SetTargetAnimationFrozen(nint gameObjectAddress, bool frozen)
        => SetTargetAnimationSpeed(gameObjectAddress, frozen ? 0f : null);

    /// <summary>
    /// Sets the GPose target's overall animation speed through Brio (same as Brio's speed / freeze controls).
    /// <paramref name="speed"/> null = remove the override (back to the game's own speed).
    /// </summary>
    public bool SetTargetAnimationSpeed(nint gameObjectAddress, float? speed)
    {
        if(_asm == null) return false;
        var em = GetService(_asm, _asm.GetType("Brio.Entities.EntityManager"));
        var tCap = _asm.GetType("Brio.Capabilities.Actor.ActionTimelineCapability");
        if(em == null || tCap == null) return false;
        if(Call(em, "TryGetAllActors") is not IEnumerable actors) return false;

        foreach(var actor in actors)
        {
            if(Get(actor, "GameObject") is not Dalamud.Game.ClientState.Objects.Types.IGameObject go || go.Address != gameObjectAddress)
                continue;
            if(Get(actor, "Capabilities") is not IEnumerable caps) return false;
            var cap = caps.Cast<object>().FirstOrDefault(c => tCap.IsInstanceOfType(c));
            if(cap == null) return false;
            if(speed is float s) Call(cap, "SetOverallSpeedOverride", s);
            else Call(cap, "ResetOverallSpeedOverride");
            return true;
        }
        return false;
    }

    /// <summary>Plays / pauses Brio's Vivacity timeline (TimelineService.TogglePlay only flips the state).</summary>
    public void SetTimelinePlaying(bool playing)
    {
        if(_timeline == null) return;
        if(TimelineIsPlaying != playing) Call(_timeline, "TogglePlay");
    }

    /// <summary>Moves Brio's Vivacity playhead (fractional frames allowed) and applies it to all timeline hosts.</summary>
    public void SetTimelineTime(double frame)
    {
        if(_timeline == null) return;
        _timeline.GetType().GetField("_currentTime", Inst)?.SetValue(_timeline, frame);
        Call(_timeline, "ApplyCurrentFrame", true);
    }

    /// <summary>
    /// Points Brio's current camera at a world pose, if it is a free camera (used to look through a node).
    /// Returns false if the current camera is not a free camera.
    /// </summary>
    public bool SetCurrentFreeCamPose(Vector3 position, float yaw, float pitch, float roll, float? fovOffset)
    {
        var vc = Get(_vcm, "CurrentCamera");
        if(vc == null || Get(vc, "IsFreeCamera") is not true) return false;
        Set(vc, "Position", position);
        Set(vc, "Rotation", new Vector3(yaw, pitch, 0));
        Set(vc, "PivotRotation", roll);
        if(fovOffset is float f) Set(vc, "FoV", f);
        return true;
    }

    // ---------------- XAT cutscene player ----------------

    public bool CutsceneIsRunning => Get(_cutscene, "IsRunning") is true;

    /// <summary>Loads an .xcp into Brio's XAT player exactly like Brio's own "Browse" button does.</summary>
    public void LoadCutsceneFile(string path)
    {
        if(_cutscene == null || _tXatFile == null) throw new InvalidOperationException("Brio XAT 播放器不可用");
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var file = Activator.CreateInstance(_tXatFile, br);
        Set(_cutscene, "CameraPath", file);
    }

    public void ClearCutsceneFile()
    {
        if(_cutscene != null) Set(_cutscene, "CameraPath", null);
    }

    public bool HasCutsceneFile => Get(_cutscene, "CameraPath") != null;

    public void SetCutsceneLoop(bool loop)
    {
        var s = Get(_cutscene, "CameraSettings");
        if(s != null) Set(s, "Loop", loop);
    }

    public void SetCutsceneFov(bool enable)
    {
        var s = Get(_cutscene, "CameraSettings");
        if(s != null) Set(s, "EnableFOV", enable);
    }

    public void StartCutscene() => Call(_cutscene, "StartPlayback");
    public void StopCutscene() => Call(_cutscene, "StopPlayback");

    /// <summary>True when Brio's current camera is a free camera (XAT playback is suppressed in that case).</summary>
    public bool CurrentCameraIsFree => Get(Get(_vcm, "CurrentCamera"), "IsFreeCamera") is true;

    // ---------------- cameras & timeline camera track ----------------

    public sealed record FreeCam(object Entity, object Capability, string Name, bool InTimeline);

    public List<FreeCam> GetFreeCameras()
    {
        var list = new List<FreeCam>();
        if(_vcm == null || _tCamTimelineCap == null) return list;

        var active = (Get(_timeline, "ActiveHosts") as IEnumerable)?.Cast<object>().ToList() ?? [];
        if(Call(_vcm, "GetAllCameras") is not IEnumerable cams) return list;

        foreach(var cam in cams)
        {
            if(Get(cam, "Capabilities") is not IEnumerable caps) continue;
            var cap = caps.Cast<object>().FirstOrDefault(c => _tCamTimelineCap.IsInstanceOfType(c));
            if(cap == null) continue;
            var name = Get(cam, "FriendlyName") as string ?? "Camera";
            list.Add(new FreeCam(cam, cap, name, active.Contains(cap)));
        }
        return list;
    }

    public void EnsureInTimeline(FreeCam cam)
    {
        if(!cam.InTimeline) Call(_timeline, "AddToTimeline", cam.Capability);
    }

    public bool GetDelimitAngle(FreeCam cam) => Get(Get(Get(cam.Entity, "VirtualCamera"), "FreeCamValues"), "DelimitAngle") is true;

    public void SetDelimitAngle(FreeCam cam, bool value)
    {
        var fcv = Get(Get(cam.Entity, "VirtualCamera"), "FreeCamValues");
        if(fcv != null) Set(fcv, "DelimitAngle", value);
    }

    /// <summary>Snapshot of the camera track's keyframe list (the same TrackKeyframe objects) for undo.</summary>
    public IList? GetTrackKeyframes(FreeCam cam) => Get(Get(cam.Capability, "_track"), "Keyframes") as IList;

    /// <summary>Replace the camera track's keyframes with a previously captured list.</summary>
    public void RestoreTrackKeyframes(FreeCam cam, IList snapshot)
    {
        var track = Get(cam.Capability, "_track");
        if(track == null) return;
        var listType = typeof(List<>).MakeGenericType(snapshot.GetType().GetGenericArguments()[0]);
        var copy = (IList)Activator.CreateInstance(listType)!;
        foreach(var k in snapshot) copy.Add(k);
        Set(track, "Keyframes", copy);
    }

    public IList CopyList(IList src)
    {
        var copy = (IList)Activator.CreateInstance(src.GetType())!;
        foreach(var k in src) copy.Add(k);
        return copy;
    }

    public void ClearTrack(FreeCam cam) => GetTrackKeyframes(cam)?.Clear();

    /// <param name="Step">Hard cut: Brio holds the previous key's value until this frame, then jumps (InterpolationMode.Step).</param>
    public readonly record struct FreeCamKey(int Frame, Vector3 Position, float Yaw, float Pitch, float Roll, float FovOffset, bool Step = false);

    /// <summary>Writes free-cam keyframes into the camera's timeline track (linear: Bezier with straight handles; Step for cuts).</summary>
    public void AddCameraKeys(FreeCam cam, IEnumerable<FreeCamKey> keys)
    {
        var track = Get(cam.Capability, "_track") ?? throw new InvalidOperationException("找不到相机轨道");
        var add = _tTrack!.GetMethod("AddCameraKeyframe", Inst) ?? throw new InvalidOperationException("找不到 AddCameraKeyframe");
        var all = Enum.Parse(_tCamComponents!, "All");
        var bezier = Enum.Parse(_tInterp!, "Bezier");
        var step = Enum.Parse(_tInterp!, "Step");
        var vc = Get(cam.Entity, "VirtualCamera");

        // keep lens fields Brio does not use for free cams as they currently are
        var angle = Get(vc, "Angle") is Vector2 a ? a : Vector2.Zero;
        var pan = Get(vc, "Pan") is Vector2 p ? p : Vector2.Zero;
        var zoom = Get(vc, "Zoom") is float z ? z : 0f;

        foreach(var k in keys)
        {
            var kf = Activator.CreateInstance(_tCamKeyframe!)!;
            SetStructField(ref kf, "Position", k.Position);
            SetStructField(ref kf, "Rotation", new Vector3(k.Yaw, k.Pitch, 0));
            SetStructField(ref kf, "Angle", angle);
            SetStructField(ref kf, "Pan", pan);
            SetStructField(ref kf, "Zoom", zoom);
            SetStructField(ref kf, "FoV", k.FovOffset);
            SetStructField(ref kf, "PivotRotation", k.Roll);

            var tk = add.Invoke(track, [k.Frame, kf, all, k.Step ? step : bezier]);
            if(tk != null)
            {
                // AddCameraKeyframe merges into an existing key without changing its mode, so set it explicitly
                Set(tk, "InterpolationMode", k.Step ? step : bezier);
                // straight easing handles = linear interpolation between dense samples
                Set(tk, "P1", new Vector2(1f / 3f, 1f / 3f));
                Set(tk, "P2", new Vector2(2f / 3f, 2f / 3f));
            }
        }
    }

    private static void SetStructField(ref object boxed, string name, object value)
        => boxed.GetType().GetField(name, Inst)?.SetValue(boxed, value);

    public void ApplyTimelineFrame() => Call(_timeline, "ApplyCurrentFrame", true);
}
