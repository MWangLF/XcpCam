using Dalamud.Plugin.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.BrioInterop;

/// <summary>
/// Mode 2: bake the path into a Brio free camera's Vivacity timeline track, so the timeline itself drives the
/// camera (scrub / pause / loop all stay in sync). The relative path is placed in the world using the current
/// GPose target, then converted to Brio free-cam yaw / pitch / roll (see <see cref="FreeCamMath"/>).
/// </summary>
public sealed class TimelineBake
{
    private readonly BrioBridge _brio;
    private readonly IPluginLog _log;

    public int StartFrame;
    public int StepFrames = 1;
    public bool ClearExisting = true;
    /// <summary>Base vertical FoV the free camera's FoV offset is added to (Brio default 0.78).</summary>
    public float BaseFov = 0.78f;

    public string Status { get; private set; } = "";

    private BrioBridge.FreeCam? _undoCam;
    private IList? _undoKeys;
    private int _undoFrameMax;
    public bool CanUndo => _undoCam != null && _undoKeys != null;

    public TimelineBake(BrioBridge brio, IPluginLog log) { _brio = brio; _log = log; }

    public void Bake(BrioBridge.FreeCam cam, CameraPath path, Vector3 basePos, Quaternion baseRot)
    {
        int fps = _brio.TimelineFps;
        int step = Math.Max(1, StepFrames);
        int totalFrames = Math.Max(1, (int)MathF.Round(path.Duration * fps));

        var keys = new List<BrioBridge.FreeCamKey>();
        float? prevYaw = null;
        float maxPitch = 0;
        for(int f = 0; ; f += step)
        {
            if(f > totalFrames) f = totalFrames;
            var s = path.Sample(f / (float)fps);
            var worldPos = Vector3.Transform(s.Pos, baseRot) + basePos;
            var worldRot = Quaternion.Normalize(baseRot * s.Rot);
            var (yaw, pitch, roll) = FreeCamMath.FromWorldRotation(worldRot, prevYaw);
            prevYaw = yaw;
            maxPitch = MathF.Max(maxPitch, MathF.Abs(pitch));
            keys.Add(new BrioBridge.FreeCamKey(StartFrame + f, worldPos, yaw, pitch, roll, s.Fov - BaseFov));
            if(f == totalFrames) break;
        }

        // undo snapshot
        var current = _brio.GetTrackKeyframes(cam);
        _undoCam = cam;
        _undoKeys = current == null ? null : _brio.CopyList(current);
        _undoFrameMax = _brio.FrameMax;

        if(ClearExisting) _brio.ClearTrack(cam);
        _brio.AddCameraKeys(cam, keys);

        int lastFrame = StartFrame + totalFrames;
        if(_brio.FrameMax < lastFrame) _brio.FrameMax = lastFrame;

        string pitchNote = "";
        if(maxPitch > 1.5f && !_brio.GetDelimitAngle(cam))
        {
            _brio.SetDelimitAngle(cam, true);
            pitchNote = "；俯仰角超过 ±86°，已为该相机开启 Brio 的「解除角度限制」";
        }

        _brio.EnsureInTimeline(cam);
        _brio.ApplyTimelineFrame();

        Status = $"已烘焙 {keys.Count} 个关键帧到「{cam.Name}」（第 {StartFrame}–{lastFrame} 帧 @ {fps}fps）{pitchNote}";
        _log.Info($"TimelineBake: {keys.Count} keys into {cam.Name}, frames {StartFrame}-{lastFrame} @ {fps}fps, baseFov {BaseFov}");
    }

    public void Undo()
    {
        if(_undoCam == null || _undoKeys == null) return;
        _brio.RestoreTrackKeyframes(_undoCam, _undoKeys);
        _brio.FrameMax = _undoFrameMax;
        _brio.ApplyTimelineFrame();
        Status = $"已撤销对「{_undoCam.Name}」的烘焙";
        _undoCam = null;
        _undoKeys = null;
    }
}
