using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using XcpCam.BrioInterop;
using XcpCam.Core;

namespace XcpCam;

public sealed class MainWindow : Window, IDisposable
{
    private readonly ITargetManager _targets;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly BrioBridge _brio;
    private readonly Overlay _overlay;
    private readonly GizmoEditor _gizmo = new();
    private readonly TimelineWidget _tlw = new();
    private readonly TimelineSync _sync;
    private readonly TimelineBake _bake;

    public readonly FileDialogManager FileDialog = new();

    private readonly CamTimeline _tl = new();
    private readonly PathSettings _settings = new();
    private bool _useProjectionFov = true;
    private bool _syncFpsWithBrio = true;
    private bool _followBrioPlayhead;
    private bool _drivePlayheadToBrio;
    private bool _lookThrough;
    private int _defaultGapFrames = 60;
    private string _status = "";
    private bool _statusIsError;
    private string _lastDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>Final camera: all tracks composed, hard cuts included.</summary>
    private ShotComposer? _comp;
    private int _pathHash;

    private bool _playing;
    private bool _loop = true;

    private int _bakeCamIndex;

    /// <summary>What the camera timeline is bound to (one or the other).</summary>
    private enum BindSource { Vivacity, Animation }
    private BindSource _source = BindSource.Vivacity;
    private readonly AnimationClock _anim;
    private bool _freezeAnimWhileScrubbing = true;
    private bool _animFrozenByUs;

    private static readonly string[] TangentNames = ["自动(平滑)", "停顿", "匀速"];
    private static readonly string[] ModeNames = ["按时间平滑", "整体匀速"];
    private static readonly string[] SourceNames = ["灵动时间轴", "动画进度条"];

    public MainWindow(ITargetManager targets, IClientState clientState, IPluginLog log, BrioBridge brio, Overlay overlay)
        : base("XcpCam - 相机时间轴###xcpcam_main")
    {
        _targets = targets;
        _clientState = clientState;
        _log = log;
        _brio = brio;
        _overlay = overlay;
        _anim = new AnimationClock(targets);
        _sync = new TimelineSync(brio, log) { Clock = ReadBoundClock };
        _bake = new TimelineBake(brio, log);
        _tlw.BeforeTrackEdit = _tl.Snapshot;
        Size = new Vector2(900, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // ---------------- bound clock abstraction ----------------

    private string SourceName => SourceNames[(int)_source];

    /// <summary>Whether the selected binding source is currently usable.</summary>
    private bool SourceAvailable => _source == BindSource.Vivacity ? _brio.IsConnected : _anim.Available;

    /// <summary>Frame rate of the bound clock. The animation bar is in seconds, so it uses the camera timeline's own fps.</summary>
    private int SourceFps => _source == BindSource.Vivacity && _brio.IsConnected ? _brio.TimelineFps : _tl.Fps;

    /// <summary>(playing, time in camera-timeline frames, fps) of the bound clock.</summary>
    private (bool playing, double frame, int fps) ReadBoundClock()
    {
        if(_source == BindSource.Vivacity)
            return (_brio.TimelineIsPlaying, _brio.TimelineTime, _brio.TimelineFps);
        return (_anim.IsPlaying, _anim.Time * _tl.Fps, _tl.Fps);
    }

    private double SourceFrame => _source == BindSource.Vivacity ? _brio.TimelineTime : _anim.Time * _tl.Fps;

    /// <summary>Length of the bound clock in camera-timeline frames, or null if unknown.</summary>
    private int? SourceLengthFrames => _source == BindSource.Vivacity
        ? (_brio.IsConnected ? _brio.FrameMax : null)
        : (_anim.Available ? (int)MathF.Round(_anim.Duration * _tl.Fps) : null);

    /// <summary>Moves the bound clock to a camera-timeline frame.</summary>
    private void DriveSourceTo(float frame)
    {
        if(_source == BindSource.Vivacity)
        {
            if(_brio.IsConnected) _brio.SetTimelineTime(frame);
            return;
        }
        if(!_anim.Available) return;
        if(_freezeAnimWhileScrubbing && !_animFrozenByUs && _brio.IsConnected && _targets.GPoseTarget != null)
            _animFrozenByUs = _brio.SetTargetAnimationFrozen(_targets.GPoseTarget.Address, true);
        _anim.Seek(frame / _tl.Fps);
    }

    private void ReleaseAnimFreeze()
    {
        if(!_animFrozenByUs) return;
        if(_brio.IsConnected && _targets.GPoseTarget != null)
            _brio.SetTargetAnimationFrozen(_targets.GPoseTarget.Address, false);
        _animFrozenByUs = false;
    }

    /// <summary>The playhead is slaved to the bound source (Vivacity timeline or animation bar).</summary>
    private bool FollowingSource => _followBrioPlayhead && SourceAvailable;

    /// <summary>Is the bound source itself playing right now?</summary>
    private bool SourcePlaying => _source == BindSource.Vivacity ? _brio.TimelineIsPlaying : _anim.IsPlaying;

    /// <summary>What the play button shows: our own clock, or the source's when following it.</summary>
    private bool TransportPlaying => FollowingSource ? SourcePlaying || _animPlayRequested : _playing;

    /// <summary>Set while we asked Brio to play the animation and it has not visibly advanced yet (debounce for the button).</summary>
    private bool _animPlayRequested;
    private DateTime _animPlayRequestUtc;

    /// <summary>
    /// Plays / pauses the bound source from our transport buttons. For the animation bar this seeks the animation to
    /// the playhead and lets it run (or freezes it) through Brio's speed override, so Brio's own speed hook does the
    /// timing; the playhead then follows the animation.
    /// </summary>
    private void SetSourcePlaying(bool play)
    {
        if(_source == BindSource.Vivacity)
        {
            if(play) _brio.SetTimelineTime(_tl.Playhead);
            _brio.SetTimelinePlaying(play);
            return;
        }

        var target = _targets.GPoseTarget;
        if(!_anim.Available || target == null) return;
        if(play)
        {
            float f = _tl.Playhead;
            if(f >= _anim.Duration * _tl.Fps - 0.5f) f = 0; // at the end: start over
            _anim.Seek(f / _tl.Fps);
            // remove any freeze (ours or Brio's own) so the animation runs at its normal speed
            _brio.SetTargetAnimationSpeed(target.Address, null);
            _animFrozenByUs = false;
            _animPlayRequested = true;
            _animPlayRequestUtc = DateTime.UtcNow;
        }
        else
        {
            _animFrozenByUs = _brio.SetTargetAnimationFrozen(target.Address, true);
            _animPlayRequested = false;
        }
    }

    /// <summary>
    /// Moves the playhead from a user action (scrub, frame input, step, jump). When the playhead follows the source,
    /// the source is moved too — otherwise the follow would snap the playhead straight back.
    /// </summary>
    private void SeekPlayhead(float frame)
    {
        _playing = false;
        _tl.Playhead = Math.Clamp(frame, 0, _tl.Length);
        if((FollowingSource || _drivePlayheadToBrio) && SourceAvailable) DriveSourceTo(_tl.Playhead);
    }

    private void TogglePlay()
    {
        if(FollowingSource)
        {
            SetSourcePlaying(!TransportPlaying);
            return;
        }
        _playing = !_playing;
        if(_playing && _tl.Playhead >= _tl.LastFrame) _tl.Playhead = _tl.FirstFrame;
    }

    private void StopTransport()
    {
        if(FollowingSource)
        {
            SetSourcePlaying(false);
            _tl.Playhead = _tl.FirstFrame;
            DriveSourceTo(_tl.Playhead);
            return;
        }
        _playing = false;
        _tl.Playhead = _tl.FirstFrame;
    }

    private void SetSource(BindSource s)
    {
        if(s == _source) return;
        if(_sync.Bound) _sync.Unbind();
        ReleaseAnimFreeze();
        _animPlayRequested = false;
        _source = s;
        _sync.ClockName = SourceName;
        SetStatus($"相机时间轴已改为绑定「{SourceName}」", false);
    }

    public void Dispose()
    {
        try { _sync.Unbind(); } catch { /* Brio may already be gone */ }
        try { ReleaseAnimFreeze(); } catch { /* ditto */ }
    }

    private Keyframe? Selected
    {
        get => _tlw.Selected != null && _tl.AllKeys.Contains(_tlw.Selected) ? _tlw.Selected : null;
        set => _tlw.Selected = value;
    }

    // ------------------------------------------------------------------ ticking

    public void FrameworkTick(float dt)
    {
        _anim.Update();

        // fps only follows the Vivacity timeline; the animation bar is in seconds and has no fps of its own
        if(_source == BindSource.Vivacity && _syncFpsWithBrio && _brio.IsConnected)
        {
            int fps = _brio.TimelineFps;
            if(fps != _tl.Fps)
            {
                _tl.SetFps(fps);
                SetStatus($"已同步 Brio 灵动时间轴帧率：{fps}fps（镜头节点按秒保持不变）", false);
            }
        }

        // the user pressed play on the animation in Brio: give the animation back to Brio
        if(_source == BindSource.Animation && _animFrozenByUs && _anim.IsAdvancing)
            _animFrozenByUs = false;
        // our play request has taken effect (or timed out)
        if(_animPlayRequested && (_anim.IsAdvancing || (DateTime.UtcNow - _animPlayRequestUtc).TotalSeconds > 1))
            _animPlayRequested = false;

        RebuildPathIfNeeded();

        if(FollowingSource)
        {
            _playing = false; // our own clock is idle while the source drives the playhead
            _tl.Playhead = (float)Math.Clamp(SourceFrame, 0, _tl.Length);
        }
        else if(_playing)
        {
            _tl.Playhead += dt * _tl.Fps;
            float end = _comp != null ? _tl.LastFrame : _tl.Length;
            if(_tl.Playhead > end)
            {
                if(_loop) _tl.Playhead = _tl.FirstFrame;
                else { _tl.Playhead = end; _playing = false; }
            }
        }

        if(_lookThrough && _comp != null) LookThroughPlayhead();

        try { _sync.Tick(_comp?.DurationSec ?? 0); }
        catch(Exception ex) { _log.Warning(ex, "TimelineSync tick failed"); }
    }

    private void LookThroughPlayhead()
    {
        if(!_brio.IsConnected || _comp == null) return;
        if(!CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out _)) return;
        var s = _comp.Sample(_tl.Playhead);
        var wp = Vector3.Transform(s.Pos, br) + bp;
        var wr = Quaternion.Normalize(br * s.Rot);
        var (yaw, pitch, roll) = FreeCamMath.FromWorldRotation(wr);
        if(!_brio.SetCurrentFreeCamPose(wp, yaw, pitch, roll, s.Fov - _bake.BaseFov))
        {
            _lookThrough = false;
            SetStatus("镜头预览需要 Brio 当前相机是自由相机（Free）。", true);
        }
    }

    // ------------------------------------------------------------------ overlay (drawn in OverlayWindow)

    public bool OverlayWantsInput => _gizmo.Enabled && Selected != null && (_gizmo.IsOver || _gizmo.IsUsing);

    public void DrawOverlay()
    {
        float? previewFrame = _comp != null && !_lookThrough && (_playing || _followBrioPlayhead || _tl.Playhead != _tl.FirstFrame)
            ? _tl.Playhead : null;
        float? syncFrame = _comp != null && _sync.PlayheadSec is float sec ? _comp.FirstFrame + sec * _tl.Fps : null;
        _overlay.Draw(_comp, _tl, previewFrame, syncFrame, Selected);

        var sel = Selected;
        if(sel != null && _clientState.IsGPosing && CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out _))
        {
            var prePos = sel.Pos;
            var preRot = sel.Rot;
            _gizmo.Draw(sel, bp, br);
            if(_gizmo.DragStarted)
            {
                // snapshot the pose from before this drag, then re-apply the first frame of the drag
                var newPos = sel.Pos; var newRot = sel.Rot;
                sel.Pos = prePos; sel.Rot = preRot;
                _tl.Snapshot();
                sel.Pos = newPos; sel.Rot = newRot;
            }
        }
    }

    private int ComputeHash()
    {
        var h = new HashCode();
        h.Add(_settings.Mode); h.Add(_settings.EaseStart); h.Add(_settings.EaseEnd);
        h.Add(_settings.Duration); h.Add(_settings.EaseInFraction); h.Add(_settings.EaseOutFraction); h.Add(_settings.RotationWeight);
        h.Add(_tl.Fps);
        h.Add(_tl.Tracks.Count);
        foreach(var t in _tl.Tracks)
        {
            h.Add(t.GetHashCode()); h.Add(t.Muted); h.Add(t.Solo); h.Add(t.Keys.Count);
            foreach(var k in t.Keys) { h.Add(k.Frame); h.Add(k.Pos); h.Add(k.Rot); h.Add(k.Fov); h.Add(k.Tangent); h.Add(k.HoldFrames); h.Add(k.CutIn); }
        }
        return h.ToHashCode();
    }

    private void RebuildPathIfNeeded()
    {
        _tl.RefreshAutoCuts(); // node now 1 frame after its predecessor -> hard cut (unless switched off by hand)
        int hash = ComputeHash();
        // also rebuild whenever the composer was built for a different number of tracks
        bool structureChanged = _comp != null && _comp.TrackCount != _tl.Tracks.Count;
        if(hash == _pathHash && !structureChanged) return;
        _pathHash = hash;
        try
        {
            _tl.SyncSeconds();
            _comp = ShotComposer.Create(_tl, _settings);
            if(_sync.Bound && _comp != null) _sync.Refresh(_comp);
        }
        catch(Exception ex)
        {
            _comp = null;
            _log.Warning(ex, "path build failed");
        }
    }

    // ------------------------------------------------------------------ UI

    public override void Draw()
    {
        bool gpose = _clientState.IsGPosing;
        if(!gpose)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "需要在 GPose 中使用。");

        if(ImGui.BeginTabBar("##tabs"))
        {
            if(ImGui.BeginTabItem("相机时间轴"))
            {
                DrawTimelineTab(gpose);
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("Brio 联动"))
            {
                DrawBrioTab(gpose);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        if(!string.IsNullOrEmpty(_status))
            ImGui.TextColored(_statusIsError ? new Vector4(1, 0.4f, 0.4f, 1) : new Vector4(0.5f, 1, 0.5f, 1), _status);
    }

    // ---------------- timeline tab ----------------

    private void DrawTimelineTab(bool gpose)
    {
        // --- transport ---
        if(ImGui.Button(TransportPlaying ? "暂停###play" : "播放###play")) TogglePlay();
        if(ImGui.IsItemHovered() && FollowingSource)
            ImGui.SetTooltip(_source == BindSource.Animation
                ? "播放头正跟随动画进度条：从播放头位置播放 / 暂停 Brio 中目标的动画（暂停 = 通过 Brio 冻结动画）。"
                : "播放头正跟随灵动时间轴：从播放头位置播放 / 暂停 Brio 的灵动时间轴。");
        ImGui.SameLine();
        if(ImGui.Button("停止")) StopTransport();
        ImGui.SameLine();
        if(ImGui.ArrowButton("##prev", ImGuiDir.Left)) StepPlayhead(-1);
        ImGui.SameLine();
        if(ImGui.ArrowButton("##next", ImGuiDir.Right)) StepPlayhead(1);
        ImGui.SameLine();
        ImGui.Checkbox("循环", ref _loop);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        int ph = (int)MathF.Round(_tl.Playhead);
        if(ImGui.InputInt("帧", ref ph)) SeekPlayhead(ph);
        ImGui.SameLine();
        ImGui.TextDisabled($"{_tl.Playhead / _tl.Fps:0.00}s");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        int len = _tl.Length;
        if(ImGui.InputInt("总长(帧)", ref len, 10)) _tl.Length = Math.Clamp(len, Math.Max(1, _tl.LastFrame), 100000);
        ImGui.SameLine();
        if(_source == BindSource.Vivacity && _syncFpsWithBrio && _brio.IsConnected)
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), $"{_tl.Fps}fps (Brio)");
        else
        {
            ImGui.SetNextItemWidth(60);
            int fps = _tl.Fps;
            if(ImGui.InputInt("fps", ref fps, 0)) _tl.SetFps(fps);
        }

        // --- capture row ---
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("在播放头抓取镜头")) CaptureAtPlayhead();
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("在当前播放头所在帧添加镜头节点（该帧已有节点则替换它的镜头）");
        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("追加到末尾")) CaptureAppend();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose || _tl.Keys.Count == 0);
        if(ImGui.Button("在播放头切镜")) CutAtPlayhead();
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("在播放头所在帧抓取当前镜头，并从上一个节点「硬切」过来（瞬间切换，不移动过去）。\n之后的节点会接着这个新镜头继续运镜。");
        ImGui.SameLine();
        var activeSeq = _comp?.SequenceOf(_tl.ActiveTrackIndex);
        bool inPath = activeSeq != null && activeSeq.Segments.Any(s => s.Path != null && _tl.Playhead >= s.Start && _tl.Playhead <= s.End);
        ImGui.BeginDisabled(!inPath);
        if(ImGui.Button("从运镜生成关键帧")) InsertKeyFromPath();
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(inPath
                ? "在当前轨道的播放头所在帧插入一个镜头节点，位置/旋转/FoV 取自这条轨道在这一帧的运镜。\n之后可以单独拖动、用手柄调整它来修改这一段运镜。"
                : "播放头需要位于当前轨道某一段运镜（至少 2 个节点）之内。");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        ImGui.InputInt("间隔帧", ref _defaultGapFrames, 0);
        _defaultGapFrames = Math.Clamp(_defaultGapFrames, 1, 10000);
        ImGui.SameLine();
        ImGui.BeginDisabled(!_tl.CanUndo);
        if(ImGui.Button("撤销")) { _tl.Undo(); Selected = null; }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_tl.CanRedo);
        if(ImGui.Button("重做")) { _tl.Redo(); Selected = null; }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if(ImGui.Button("适配")) _tlw.FitAll(_tl, ImGui.GetContentRegionAvail().X + 300);
        ImGui.SameLine();
        bool canExport = _comp != null && _comp.LastFrame > _comp.FirstFrame;
        ImGui.BeginDisabled(!canExport);
        if(ImGui.Button("导出 .xcp")) OpenSaveDialog();
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && _comp != null && _comp.CutFrames.Count > 0)
            ImGui.SetTooltip(".xcp 是 30fps 的连续帧，Brio 播放时会在相邻两帧之间插值，\n所以每个切镜处会有约 1 帧（33ms）的快速过渡。\n需要完全瞬切请使用「Brio 联动」里的烘焙，或「预览镜头」。");

        // --- the timeline ---
        ImGui.Spacing();
        _tlw.Draw(_tl, _comp);
        if(_tlw.TracksEdited)
        {
            // tracks were added / removed / reordered inside the widget: keep the selection valid and
            // rebuild right away so nothing else this frame uses a composer for the old track list
            if(Selected == null) _tlw.Selected = null;
            RebuildPathIfNeeded();
        }
        if(_tlw.DragStarted) _tl.Snapshot();
        if(_tlw.PlayheadChanged) SeekPlayhead(_tl.Playhead);
        ImGui.TextDisabled("拖动◆移动镜头节点（精确到帧）· 点击空白处移动播放头 · 滚轮平移，Ctrl+滚轮缩放，中键拖动 · 双击节点跳转 · ✂ = 硬切 · 越靠上的轨道越优先显示");
        DrawGizmoBar();

        ImGui.Separator();
        DrawPacing();
        ImGui.Separator();
        DrawInspector(gpose);
        DrawTargetWarnings(gpose);
        ImGui.Spacing();
        DrawDisplayOptions();
    }

    private void StepPlayhead(int dir)
    {
        _playing = false;
        // stop at nodes of the active track and at every cut
        var frames = _tl.Keys.Select(k => k.Frame).Concat(_comp?.CutFrames ?? []).Distinct().OrderBy(f => f).ToList();
        int cur = (int)MathF.Round(_tl.Playhead);
        int? next = dir > 0 ? frames.Where(f => f > cur).Cast<int?>().FirstOrDefault() : frames.Where(f => f < cur).Cast<int?>().LastOrDefault();
        SeekPlayhead(next ?? Math.Clamp(cur + dir, 0, _tl.Length));
        var k = _tl.Keys.FirstOrDefault(x => x.Frame == (int)_tl.Playhead);
        if(k != null) Selected = k;
    }

    private void DrawPacing()
    {
        int mode = (int)_settings.Mode;
        ImGui.SetNextItemWidth(130);
        if(ImGui.Combo("运镜节奏", ref mode, ModeNames, ModeNames.Length))
            _settings.Mode = (TimingMode)mode;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("按时间平滑：每个镜头在它所在的帧到达，经过镜头时速度连续、不顿挫。\n整体匀速：首尾节点之间以恒定速度运动（中间节点的帧位置被忽略），只在首尾缓入缓出。");
        ImGui.SameLine();
        if(_settings.Mode == TimingMode.Timed)
        {
            ImGui.Checkbox("开头缓入", ref _settings.EaseStart);
            ImGui.SameLine();
            ImGui.Checkbox("结尾缓出", ref _settings.EaseEnd);
        }
        else
        {
            float a = _settings.EaseInFraction * 100, b = _settings.EaseOutFraction * 100;
            ImGui.SetNextItemWidth(60);
            if(ImGui.DragFloat("缓入%", ref a, 0.5f, 0f, 50f, "%.0f")) _settings.EaseInFraction = a / 100f;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            if(ImGui.DragFloat("缓出%", ref b, 0.5f, 0f, 50f, "%.0f")) _settings.EaseOutFraction = b / 100f;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            ImGui.DragFloat("转向权重", ref _settings.RotationWeight, 0.05f, 0f, 10f, "%.2f");
        }
        if(_comp != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"| 时长 {_comp.DurationSec:0.00}s · 路径 {_comp.TotalLength:0.0}y · {_comp.CutFrames.Count} 处切镜");
        }
    }

    private void DrawInspector(bool gpose)
    {
        var k = Selected;
        if(k == null)
        {
            ImGui.TextDisabled(!_tl.AllKeys.Any()
                ? "还没有镜头节点。在 Brio 里摆好镜头后点「在播放头抓取镜头」。"
                : _gizmo.Enabled
                    ? "点击时间轴上的◆选中一个镜头节点进行编辑；选中后可在游戏画面中用变换手柄直接移动/旋转它。"
                    : "点击时间轴上的◆选中一个镜头节点进行编辑。（勾选上方「变换手柄」可在游戏画面中直接调整）");
            return;
        }

        var owner = _tl.TrackOf(k) ?? _tl.ActiveTrack;
        int idx = owner.Keys.IndexOf(k) + 1;
        ImGui.Text(_tl.Tracks.Count > 1 ? $"{owner.Name} · 节点 {idx}" : $"镜头 {idx}");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        int frame = k.Frame;
        if(ImGui.InputInt("所在帧", ref frame))
        {
            _tl.Snapshot();
            int got = _tl.MoveKey(k, frame);
            if(got != frame) SetStatus($"第 {frame} 帧已有节点", true);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        int hold = k.HoldFrames;
        if(ImGui.InputInt("停留帧", ref hold, 0)) { _tl.Snapshot(); k.HoldFrames = Math.Clamp(hold, 0, 100000); }
        ImGui.SameLine();
        ImGui.BeginDisabled(_settings.Mode != TimingMode.Timed);
        ImGui.SetNextItemWidth(100);
        int tan = (int)k.Tangent;
        if(ImGui.Combo("经过方式", ref tan, TangentNames, TangentNames.Length)) { _tl.Snapshot(); k.Tangent = (TangentMode)tan; }
        ImGui.EndDisabled();
        ImGui.SameLine();
        bool isFirst = owner.Keys.All(o => o == k || o.Frame > k.Frame);
        ImGui.BeginDisabled(isFirst);
        bool cut = k.CutIn;
        if(ImGui.Checkbox("从上一个节点硬切", ref cut))
        {
            _tl.Snapshot();
            k.CutIn = cut;
            k.CutAuto = false; // a manual choice is never overridden by the auto rule
        }
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(isFirst ? "这是这条轨道的第一个节点。"
                : "勾选后，镜头在这一帧从上一个节点瞬间切换到这里，不再移动过去。\n节点放在上一个节点后 1 帧时会自动勾选；手动取消后不会再被自动勾上。");

        // transform fields (relative to target)
        var pos = k.Pos;
        ImGui.SetNextItemWidth(260);
        if(ImGui.DragFloat3("相对位置 (米)", ref pos, 0.01f))
            k.Pos = pos;
        if(ImGui.IsItemActivated()) _tl.Snapshot();

        var euler = QuatToEulerDeg(k.Rot);
        ImGui.SetNextItemWidth(260);
        if(ImGui.DragFloat3("旋转 偏航/俯仰/滚转 (°)", ref euler, 0.25f))
            k.Rot = EulerDegToQuat(euler);
        if(ImGui.IsItemActivated()) _tl.Snapshot();

        float fovDeg = k.Fov * 180f / MathF.PI;
        ImGui.SetNextItemWidth(120);
        if(ImGui.DragFloat("FoV (°)", ref fovDeg, 0.1f, 5f, 150f, "%.1f"))
            k.Fov = fovDeg * MathF.PI / 180f;
        if(ImGui.IsItemActivated()) _tl.Snapshot();

        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("用当前镜头替换")) Recapture(k);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if(ImGui.Button("跳到此帧")) SeekPlayhead(k.Frame);
        ImGui.SameLine();
        if(ImGui.Button("复制节点")) Duplicate(k);
        ImGui.SameLine();
        if(ImGui.Button("删除节点")) { _tl.Snapshot(); owner.Keys.Remove(k); Selected = null; }

        bool ok = k.SelfCheckError < 1e-3f;
        ImGui.TextColored(ok ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(1, 0.4f, 0.4f, 1),
            ok ? "自检 OK" : $"自检误差 {k.SelfCheckError:0.0e0}");
        ImGui.SameLine();
        ImGui.TextDisabled($"目标：{k.TargetName}");
    }

    private static Vector3 QuatToEulerDeg(Quaternion q)
    {
        // yaw (Y), pitch (X), roll (Z) matching Quaternion.CreateFromYawPitchRoll
        q = Quaternion.Normalize(q);
        float sinp = 2 * (q.W * q.X - q.Y * q.Z);
        float pitch = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);
        float yaw = MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        float roll = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z));
        const float d = 180f / MathF.PI;
        return new Vector3(yaw * d, pitch * d, roll * d);
    }

    private static Quaternion EulerDegToQuat(Vector3 e)
    {
        const float r = MathF.PI / 180f;
        return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(e.X * r, e.Y * r, e.Z * r));
    }

    private void DrawTargetWarnings(bool gpose)
    {
        if(!_tl.AllKeys.Any()) return;
        var names = _tl.AllKeys.Select(k => k.TargetName).Where(n => n.Length > 0).Distinct().Count();
        if(names > 1)
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "警告：镜头节点抓取时的 GPose 目标不一致，播放时会错位。");
        else if(gpose && CameraCapture.TryGetBase(_targets, out var bp, out var br, out var nm, out _))
        {
            var k0 = _tl.AllKeys.First();
            if(k0.TargetName.Length > 0 && (nm != k0.TargetName || Vector3.Distance(bp, k0.BasePos) > 0.01f || MathF.Abs(Quaternion.Dot(br, k0.BaseRot)) < 0.9999f))
                ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "提示：目标角色在抓取后移动/旋转或已更换。运镜是相对目标的，会跟随当前目标。");
        }
    }

    // ---------------- in-game display & gizmo (merged into the timeline tab) ----------------

    /// <summary>One row: overlay on/off + gizmo mode, shown right under the timeline.</summary>
    private void DrawGizmoBar()
    {
        ImGui.Checkbox("游戏内显示", ref _overlay.Enabled);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("在游戏画面中显示运镜路径（蓝=慢，红=快）和各镜头视锥。详细选项见下方「显示选项」。");
        ImGui.SameLine();
        ImGui.Checkbox("变换手柄", ref _gizmo.Enabled);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("选中一个镜头节点后，在游戏画面中该镜头处显示手柄，可直接拖动移动/旋转。");
        ImGui.BeginDisabled(!_gizmo.Enabled);
        ImGui.SameLine();
        int op = (int)_gizmo.Operation;
        if(ImGui.RadioButton("移动", ref op, 0)) _gizmo.Operation = GizmoEditor.Op.Translate;
        ImGui.SameLine();
        if(ImGui.RadioButton("旋转", ref op, 1)) _gizmo.Operation = GizmoEditor.Op.Rotate;
        ImGui.SameLine();
        ImGui.Checkbox("本地坐标", ref _gizmo.LocalSpace);
        ImGui.SameLine();
        ImGui.Checkbox("吸附", ref _gizmo.Snap);
        if(_gizmo.Snap)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            if(_gizmo.Operation == GizmoEditor.Op.Translate)
                ImGui.DragFloat("米##snapT", ref _gizmo.SnapTranslate, 0.01f, 0.01f, 5f, "%.2f");
            else
                ImGui.DragFloat("°##snapR", ref _gizmo.SnapRotateDeg, 0.5f, 0.5f, 90f, "%.1f");
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        DrawLookThroughToggle();
    }

    /// <summary>Collapsible detailed display options.</summary>
    private void DrawDisplayOptions()
    {
        if(!ImGui.CollapsingHeader("显示选项")) return;
        ImGui.BeginDisabled(!_overlay.Enabled);
        ImGui.Checkbox("运镜路径", ref _overlay.ShowPath);
        ImGui.SameLine();
        ImGui.Checkbox("按速度着色（蓝=慢，红=快）", ref _overlay.SpeedColors);
        ImGui.SameLine();
        ImGui.Checkbox("镜头视锥", ref _overlay.ShowFrustums);
        ImGui.SameLine();
        ImGui.Checkbox("编号/帧标签", ref _overlay.ShowLabels);
        ImGui.SetNextItemWidth(120);
        ImGui.DragFloat("视锥大小", ref _overlay.FrustumSize, 0.01f, 0.05f, 3f, "%.2f");
        ImGui.EndDisabled();
    }

    private void DrawLookThroughToggle()
    {
        ImGui.BeginDisabled(!_brio.IsConnected || _comp == null);
        if(ImGui.Checkbox("预览镜头", ref _lookThrough) && _lookThrough && !_brio.CurrentCameraIsFree)
        {
            _lookThrough = false;
            SetStatus("请先在 Brio 中切换到一个自由相机（Free）。", true);
        }
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!_brio.IsConnected ? "需要连接 Brio。"
                : _comp == null ? "需要至少一个镜头节点。"
                : "把 Brio 当前的自由相机放到播放头位置，直接看到这一帧的画面。\n播放或拖动时间轴即可预览运镜。");
        if(_lookThrough)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "预览镜头：Brio 自由相机正被运镜接管，抓取镜头前请先关闭。");
    }

    // ---------------- Brio tab ----------------

    private void DrawBrioTab(bool gpose)
    {
        ImGui.TextColored(_brio.IsConnected ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(1, 0.7f, 0.3f, 1), _brio.Status);
        if(_brio.Missing.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(缺少 {_brio.Missing.Count} 项)");
            if(ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", _brio.Missing));
        }
        if(!_brio.IsConnected) return;

        // ---- binding source (one or the other) ----
        ImGui.Separator();
        ImGui.Text("相机时间轴绑定到");
        ImGui.SameLine();
        int src = (int)_source;
        if(ImGui.RadioButton("灵动时间轴", ref src, (int)BindSource.Vivacity)) SetSource(BindSource.Vivacity);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Brio 的灵动时间轴（Vivacity）。帧率可跟随 Brio。");
        ImGui.SameLine();
        if(ImGui.RadioButton("动画进度条", ref src, (int)BindSource.Animation)) SetSource(BindSource.Animation);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("GPose 目标当前动画的进度（即 Brio 动画面板里的 Animation Scruber）。\n相机时间轴第 0 帧 = 动画第 0 秒。");

        if(_source == BindSource.Vivacity)
            ImGui.TextDisabled($"Brio 灵动时间轴：{(_brio.TimelineIsPlaying ? "播放中" : "已停止")}  第 {_brio.TimelineTime:0} 帧 / {_brio.FrameMax}  @ {_brio.TimelineFps}fps");
        else if(_anim.Available)
            ImGui.TextDisabled($"目标「{_targets.GPoseTarget?.Name.TextValue}」动画：{(_anim.IsPlaying ? "播放中" : "已暂停")}  {_anim.Time:0.00}s / {_anim.Duration:0.00}s  ≈ 第 {_anim.Time * _tl.Fps:0} 帧 / {_anim.Duration * _tl.Fps:0}（{_tl.Fps}fps）");
        else
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), $"动画进度条不可用：{_anim.Status}");

        ImGui.Separator();
        ImGui.Text("同步");
        if(_source == BindSource.Vivacity)
        {
            if(ImGui.Checkbox("帧率跟随 Brio 灵动时间轴", ref _syncFpsWithBrio) && !_syncFpsWithBrio)
                SetStatus("已停止跟随 Brio 帧率，可在时间轴上手动设置 fps。", false);
        }
        else
            ImGui.TextDisabled($"动画进度条以秒为单位，按相机时间轴的 {_tl.Fps}fps 换算成帧（可在时间轴上修改 fps）。");
        ImGui.Checkbox($"播放头跟随{SourceName}", ref _followBrioPlayhead);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip($"{SourceName}播放/拖动时，本插件的播放头同步移动（预览镜头和可视化跟着走）。");
        ImGui.Checkbox($"拖动本插件播放头时同时移动{SourceName}", ref _drivePlayheadToBrio);
        if(_source == BindSource.Animation)
        {
            ImGui.SameLine();
            ImGui.Checkbox("拖动时冻结动画", ref _freezeAnimWhileScrubbing);
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("拖动播放头带动动画时，通过 Brio 把目标动画速度设为 0，避免动画自己继续往前走。\n在 Brio 里重新播放动画即可恢复。");
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(SourceLengthFrames == null);
        if(ImGui.Button($"总长 = {SourceName}长度") && SourceLengthFrames is int srcLen) _tl.Length = Math.Max(srcLen, _tl.LastFrame);
        ImGui.EndDisabled();

        // ---- mode 1 ----
        ImGui.Separator();
        ImGui.Text("方式 1：联动 Brio 的 XAT 播放器");
        ImGui.TextDisabled($"{SourceName}播放到起始帧时从头播放运镜；停止/暂停时 XAT 也停止。\n限制：XAT 不能跳转；需要使用非自由相机。");
        ImGui.BeginDisabled(!_brio.CanSync || _comp == null || !SourceAvailable);
        _sync.StartFrame = _tl.Keys.Count > 0 ? _tl.FirstFrame : 0;
        _sync.ClockName = SourceName;
        ImGui.TextDisabled(_source == BindSource.Vivacity
            ? $"起始帧 = 第一个镜头节点所在帧（{_sync.StartFrame}）"
            : $"起始 = 第一个镜头节点所在帧（{_sync.StartFrame}，动画 {_sync.StartFrame / (float)_tl.Fps:0.00}s）");
        ImGui.SameLine();
        if(!_sync.Bound)
        {
            if(ImGui.Button("绑定"))
                Try(() => { _sync.Bind(_comp!); SetStatus($"已绑定到{SourceName}（XAT）", false); });
        }
        else if(ImGui.Button("解绑"))
            Try(() => { _sync.Unbind(); SetStatus("已解绑", false); });
        ImGui.EndDisabled();
        if(_sync.Bound) ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), _sync.Status);

        // ---- mode 2 ----
        ImGui.Separator();
        ImGui.Text("方式 2：烘焙进 Brio 自由相机的时间轴相机轨道");
        ImGui.TextDisabled("由 Brio 时间轴直接驱动自由相机，拖动/暂停/循环完全同步。节点帧位置原样对应 Brio 时间轴帧。");
        if(_source != BindSource.Vivacity)
        {
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "烘焙写入的是灵动时间轴的相机轨道，只能配合「灵动时间轴」绑定使用。当前绑定的是动画进度条，请用方式 1。");
            return;
        }

        var cams = _brio.CanBake ? _brio.GetFreeCameras() : [];
        if(cams.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "没有可用的 Brio 自由相机。请先在 Brio 相机窗口中新建一个 Free 相机。");
            return;
        }

        _bakeCamIndex = Math.Clamp(_bakeCamIndex, 0, cams.Count - 1);
        var names = cams.Select(c => c.Name + (c.InTimeline ? "" : "（未加入时间轴）")).ToArray();
        ImGui.SetNextItemWidth(200);
        ImGui.Combo("目标自由相机", ref _bakeCamIndex, names, names.Length);
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("每隔几帧一个关键帧", ref _bake.StepFrames);
        _bake.StepFrames = Math.Clamp(_bake.StepFrames, 1, 30);
        ImGui.SameLine();
        ImGui.Checkbox("清除该相机轨道原有关键帧", ref _bake.ClearExisting);
        ImGui.SetNextItemWidth(70);
        float baseDeg = _bake.BaseFov * 180f / MathF.PI;
        if(ImGui.DragFloat("基础 FoV°", ref baseDeg, 0.1f, 20f, 120f, "%.1f")) _bake.BaseFov = baseDeg * MathF.PI / 180f;
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Brio 自由相机的 FoV 是在游戏基础 FoV 上的偏移量（默认约 44.7°）。");

        bool fpsMismatch = _tl.Fps != _brio.TimelineFps;
        if(fpsMismatch)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), $"本时间轴 {_tl.Fps}fps 与 Brio {_brio.TimelineFps}fps 不同，烘焙时按秒换算到 Brio 帧。");

        ImGui.BeginDisabled(_comp == null || !gpose);
        if(ImGui.Button("烘焙到 Brio 时间轴"))
            Try(() =>
            {
                if(!CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out var err))
                    throw new InvalidOperationException(err);
                _bake.Bake(cams[_bakeCamIndex], _comp!, bp, br);
                SetStatus(_bake.Status, false);
            });
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_bake.CanUndo);
        if(ImGui.Button("撤销烘焙"))
            Try(() => { _bake.Undo(); SetStatus(_bake.Status, false); });
        ImGui.EndDisabled();
        if(!string.IsNullOrEmpty(_bake.Status)) ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), _bake.Status);
    }

    private void Try(Action a)
    {
        try { a(); }
        catch(Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            SetStatus($"失败：{inner.Message}", true);
            _log.Error(inner, "XcpCam action failed");
        }
    }

    // ---------------- capture / export ----------------

    private Keyframe? MakeKey()
    {
        if(_lookThrough)
        {
            SetStatus("请先关闭「预览镜头」再抓取新镜头。", true);
            return null;
        }
        if(!CameraCapture.TryCapture(_targets, out var s, out var err) || s is null)
        {
            SetStatus(err, true);
            return null;
        }

        var (pos, rot) = XcpMath.WorldToRelative(s.WorldPos, s.WorldRot, s.BasePos, s.BaseRot);
        var rebuilt = XcpMath.BrioPlaybackView(pos, rot, s.BasePos, s.BaseRot);
        var err2 = XcpMath.MaxAffineDiff(rebuilt, s.View);

        float fov = _useProjectionFov && s.FovProjection > 0.01f ? s.FovProjection : s.FovRender;
        _log.Info($"capture world={s.WorldPos} base={s.BasePos} rel={pos} rot={rot} fov={fov} selfCheck={err2}");

        if(!XcpMath.IsFinite(pos) || !XcpMath.IsFinite(rot) || !float.IsFinite(fov))
        {
            SetStatus("抓取结果无效（NaN），已丢弃。", true);
            return null;
        }

        return new Keyframe
        {
            Pos = pos, Rot = rot, Fov = fov, SelfCheckError = err2,
            TargetName = s.TargetName, BasePos = s.BasePos, BaseRot = s.BaseRot,
        };
    }

    private void CaptureAtPlayhead()
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        int frame = (int)MathF.Round(_tl.Playhead);
        var existing = _tl.Keys.FirstOrDefault(x => x.Frame == frame);
        if(existing != null)
        {
            CopyPose(k, existing);
            Selected = existing;
            SetStatus($"已替换第 {frame} 帧的镜头", false);
            return;
        }
        k.Frame = frame;
        if(k.Frame > _tl.Length) _tl.Length = k.Frame;
        _tl.Keys.Add(k);
        Selected = k;
        SetStatus($"已在「{_tl.ActiveTrack.Name}」第 {frame} 帧添加节点 {_tl.Keys.Count}", false);
    }

    /// <summary>Captures the current camera at the playhead as the start of a new shot (hard cut from the previous node).</summary>
    private void CutAtPlayhead()
    {
        int frame = (int)MathF.Round(_tl.Playhead);
        if(!_tl.Keys.Any(x => x.Frame < frame))
        {
            SetStatus("播放头之前没有节点，无法切镜。请把播放头移到已有镜头之后。", true);
            return;
        }
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        var existing = _tl.Keys.FirstOrDefault(x => x.Frame == frame);
        if(existing != null)
        {
            CopyPose(k, existing);
            k = existing;
        }
        else
        {
            k.Frame = frame;
            if(k.Frame > _tl.Length) _tl.Length = k.Frame;
            _tl.Keys.Add(k);
        }
        k.CutIn = true;
        k.CutAuto = false;
        Selected = k;
        SetStatus($"已在第 {frame} 帧切镜：之前的运镜到这里结束，镜头瞬间切到新镜头。", false);
    }

    private void CaptureAppend()
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        k.Frame = _tl.Keys.Count == 0 ? (int)MathF.Round(_tl.Playhead) : CamTimeline.TrackLastFrame(_tl.ActiveTrack) + _defaultGapFrames;
        if(k.Frame > _tl.Length) _tl.Length = k.Frame;
        _tl.Keys.Add(k);
        Selected = k;
        _tl.Playhead = k.Frame;
        _tlw.EnsureVisible(k.Frame, 700);
        SetStatus($"已在第 {k.Frame} 帧添加镜头 {_tl.Keys.Count}", false);
    }

    /// <summary>Inserts a node at the playhead whose pose is the active track's pose at that frame.</summary>
    private void InsertKeyFromPath()
    {
        var seq = _comp?.SequenceOf(_tl.ActiveTrackIndex);
        if(seq == null) return;
        int frame = (int)MathF.Round(_tl.Playhead);
        var existing = _tl.Keys.FirstOrDefault(x => x.Frame == frame);
        if(existing != null)
        {
            Selected = existing;
            SetStatus($"第 {frame} 帧已经是节点 {_tl.Keys.IndexOf(existing) + 1}，已选中它。", true);
            return;
        }

        var (s, _) = seq.Sample(frame);
        if(!XcpMath.IsFinite(s.Pos) || !XcpMath.IsFinite(s.Rot) || !float.IsFinite(s.Fov))
        {
            SetStatus("这一帧的运镜数据无效，未生成。", true);
            return;
        }

        // carry the target info of the node this frame belongs to
        var src = _tl.Sorted().LastOrDefault(k => k.Frame <= frame) ?? _tl.Sorted()[0];
        _tl.Snapshot();
        var k = new Keyframe
        {
            Frame = frame,
            Pos = s.Pos,
            Rot = Quaternion.Normalize(s.Rot),
            Fov = s.Fov,
            Tangent = TangentMode.Auto,
            TargetName = src.TargetName,
            BasePos = src.BasePos,
            BaseRot = src.BaseRot,
        };
        // a frame inside a hold: split the hold so the camera still stays put
        if(src.HoldFrames > 0 && frame < src.Frame + src.HoldFrames)
        {
            k.HoldFrames = src.Frame + src.HoldFrames - frame;
            src.HoldFrames = frame - src.Frame;
        }
        _tl.Keys.Add(k);
        Selected = k;
        SetStatus($"已从运镜在第 {frame} 帧生成镜头 {_tl.Keys.Count}。", false);
    }

    private void Recapture(Keyframe target)
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        CopyPose(k, target);
        var owner = _tl.TrackOf(target);
        SetStatus($"已用当前镜头替换节点 {(owner?.Keys.IndexOf(target) ?? 0) + 1}", false);
    }

    private static void CopyPose(Keyframe from, Keyframe to)
    {
        to.Pos = from.Pos; to.Rot = from.Rot; to.Fov = from.Fov; to.SelfCheckError = from.SelfCheckError;
        to.TargetName = from.TargetName; to.BasePos = from.BasePos; to.BaseRot = from.BaseRot;
    }

    private void Duplicate(Keyframe k)
    {
        var owner = _tl.TrackOf(k) ?? _tl.ActiveTrack;
        _tl.Snapshot();
        var c = k.Clone();
        c.CutIn = false; c.CutAuto = false; c.LastGap = 0;
        c.Frame = _tl.NearestFreeFrame(k.Frame + _defaultGapFrames, null, owner);
        if(c.Frame > _tl.Length) _tl.Length = c.Frame;
        owner.Keys.Add(c);
        Selected = c;
    }

    private void OpenSaveDialog()
    {
        FileDialog.SaveFileDialog("导出 XAT Camera###xcpcam_save", "XAT Camera File{.xcp}", "camera", ".xcp",
            (ok, path) =>
            {
                if(!ok) return;
                if(!path.EndsWith(".xcp", StringComparison.OrdinalIgnoreCase)) path += ".xcp";
                Export(path);
            }, _lastDir, true);
    }

    private void Export(string path)
    {
        try
        {
            if(_comp == null) throw new InvalidOperationException("至少需要一个镜头节点");
            var frames = _comp.BakeXcp(PathBuilder.Fps);
            XcpWriter.Write(path, frames);
            _lastDir = Path.GetDirectoryName(path) ?? _lastDir;
            string cutNote = _comp.CutFrames.Count > 0 ? $"，{_comp.CutFrames.Count} 处切镜（.xcp 切镜处约有 1 帧过渡）" : "";
            SetStatus($"已导出 {frames.Count} 帧 (30fps){cutNote} → {path}", false);
            _log.Info($"exported {frames.Count} frames to {path}");
        }
        catch(Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}", true);
            _log.Error(ex, "export failed");
        }
    }

    private void SetStatus(string s, bool error)
    {
        _status = s;
        _statusIsError = error;
    }
}
