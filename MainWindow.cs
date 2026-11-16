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
    private readonly CurveTool _curveTool;
    private readonly BoneSource _bones;
    private readonly ConstraintPanel _constraints;
    private readonly TimelineWidget _tlw = new();
    private readonly GraphEditor _graph = new();
    private int _graphShot;
    private Guid _graphNode;
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
    /// <summary>Playhead last pushed to the preview camera; null forces a sync.</summary>
    private float? _lookSynced;
    private int _defaultGapFrames = 60;
    private string _status = "";
    private bool _statusIsError;
    private string _lastDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>Final camera: all tracks composed, hard cuts included.</summary>
    private ShotComposer? _comp;
    private int _pathHash;

    private bool _playing;

    private int _bakeCamIndex;

    /// <summary>What the camera timeline is bound to (one or the other).</summary>
    private enum BindSource { Vivacity, Animation }
    private BindSource _source = BindSource.Vivacity;
    private readonly AnimationClock _anim;
    private bool _freezeAnimWhileScrubbing = true;
    private readonly AnimationSpeedLease _animationSpeed;
    private bool _animationLoop = true;
    private bool _transportOwned;
    private long _clockEpoch;
    private bool _wasGpose;
    private AnimationIdentity _lastAnimationIdentity;

    private static readonly string[] SourceNames = ["灵动时间轴", "动画进度条"];

    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private bool _deleteWasDown;
    private long _seenSelection = -1;
    private bool _selectSettings;

    /// <summary>Opens the window on the settings tab (Dalamud OpenConfigUi).</summary>
    public void OpenSettings() { IsOpen = true; _selectSettings = true; }

    public MainWindow(ITargetManager targets, IClientState clientState, IPluginLog log, BrioBridge brio, Overlay overlay,
        BoneSource bones, Configuration config, Action saveConfig)
        : base("XcpCam - 相机时间轴###xcpcam_main")
    {
        _targets = targets;
        _bones = bones;
        _clientState = clientState;
        _log = log;
        _brio = brio;
        _overlay = overlay;
        _anim = new AnimationClock(targets);
        _animationSpeed = new AnimationSpeedLease(brio);
        _sync = new TimelineSync(brio, log)
        {
            Clock = ReadBoundClock,
            ClockCycle = () => _source == BindSource.Animation && _anim.Wrapped,
            ClockGeneration = () => _clockEpoch + (_source == BindSource.Animation ? _anim.Generation : 0),
            Base = () => CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out _) ? (bp, br) : null,
        };
        _bake = new TimelineBake(brio, log);
        _config = config; _saveConfig = saveConfig;
        NodeRange.MaxRange = config.MovementLimit;
        overlay.Enabled = config.OverlayEnabled; overlay.ShowPath = config.ShowPath; overlay.ShowFrustums = config.ShowFrustums;
        overlay.ShowLabels = config.ShowLabels; overlay.SpeedColors = config.SpeedColors; overlay.FrustumSize = config.FrustumSize;
        _tlw.BeforeTrackEdit = _tl.Snapshot;
        _curveTool = new CurveTool(_tl, overlay, _gizmo, ShotFor, n => WriteNodePose(n, true, false, false), SetStatus);
        _constraints = new ConstraintPanel(_tl, bones, SetStatus, StartRecording, () => _recording != null, StopRecording);
        Size = new Vector2(900, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // ---------------- bound clock abstraction ----------------

    private string SourceName => SourceNames[(int)_source];
    private bool SourceAvailable => _clientState.IsGPosing && _brio.IsConnected
        && (_source == BindSource.Vivacity || _anim.Available);
    private bool FollowingSource => _followBrioPlayhead && SourceAvailable;
    private bool SourcePlaying => SourceAvailable && (_source == BindSource.Vivacity ? _brio.TimelineIsPlaying : _anim.IsPlaying);
    private bool TransportPlaying => SourcePlaying;
    private double SourceFrame => _source == BindSource.Vivacity
        ? TransportMath.ConvertFrame(_brio.TimelineTime, _brio.TimelineRate, _tl.Fps)
        : _anim.Time * _tl.Fps;
    private int? SourceLengthFrames => !SourceAvailable ? null : _source == BindSource.Vivacity
        ? (int)Math.Ceiling(TransportMath.ConvertFrame(_brio.FrameMax, _brio.TimelineRate, _tl.Fps))
        : (int)Math.Ceiling(_anim.Duration * _tl.Fps);

    /// <summary>Length follows the bound source: Vivacity two-way, the animation scrubber read-only.</summary>
    private bool LengthFromAnimation => SourceAvailable && _source == BindSource.Animation;
    private bool LengthFromVivacity => SourceAvailable && _source == BindSource.Vivacity;

    private void SyncLengthFromSource()
    {
        if(SourceLengthFrames is int length && length > 0) _tl.Length = Math.Min(length, 100000);
    }

    /// <summary>Sets the timeline length; when bound to Vivacity also writes Brio's FrameMax.</summary>
    private void SetTimelineLength(int frames)
    {
        if(LengthFromAnimation) return; // the animation's own duration is authoritative
        frames = Math.Clamp(frames, 1, 100000);
        _tl.Length = frames;
        if(LengthFromVivacity)
        {
            int brioFrames = (int)Math.Round(TransportMath.ConvertFrame(frames, _tl.Fps, _brio.TimelineRate));
            _brio.FrameMax = Math.Max(_brio.FrameMin + 1, brioFrames);
        }
    }

    /// <summary>Grows the timeline to include <paramref name="frame"/> (captures/duplicates past the end).</summary>
    private void EnsureLength(int frame)
    {
        if(frame > _tl.Length) SetTimelineLength(frame);
    }

    // XAT's start frame and clock use camera-editor frames, regardless of Brio's own rate.
    private (bool playing, double frame, int fps) ReadBoundClock()
        => (SourcePlaying, SourceAvailable ? SourceFrame : 0, _tl.Fps);

    private void ValidateAnimationCommand()
    {
        if(_anim.MatchesCurrent()) return;
        ReleaseAnimFreeze();
        _anim.Reset();
        _lastAnimationIdentity = default;
        _transportOwned = false;
        _clockEpoch++;
        throw new InvalidOperationException("目标或动画刚刚更换，请等待一帧后重试。");
    }

    private void DriveSourceTo(float frame)
    {
        if(!SourceAvailable) throw new InvalidOperationException($"{SourceName}不可用，请确认已进入 GPose 并选择目标。");
        if(_source == BindSource.Vivacity)
        {
            double f = TransportMath.ConvertFrame(frame, _tl.Fps, _brio.TimelineRate);
            _brio.SetTimelineTime(Math.Clamp(f, _brio.FrameMin, _brio.FrameMax));
            _clockEpoch++;
            return;
        }
        ValidateAnimationCommand();
        if(_freezeAnimWhileScrubbing)
        {
            if(!_animationSpeed.Pause(_anim.Sample.Identity.Actor)) throw new InvalidOperationException("无法通过 Brio 冻结目标动画");
            _anim.Pause();
        }
        if(!_anim.Seek(frame / _tl.Fps)) throw new InvalidOperationException("动画已更换，无法跳转");
    }

    private void ReleaseAnimFreeze() => _animationSpeed.Release();

    private void SetSourcePlaying(bool play)
    {
        if(!SourceAvailable) throw new InvalidOperationException($"{SourceName}不可用，请确认已进入 GPose 并选择目标。");
        if(_source == BindSource.Animation) ValidateAnimationCommand();
        _playing = false;
        if(_source == BindSource.Vivacity)
        {
            if(play)
            {
                double frame = TransportMath.ConvertFrame(_tl.Playhead, _tl.Fps, _brio.TimelineRate);
                _brio.SetTimelineTime(TransportMath.PlayPosition(frame, _brio.FrameMin, _brio.FrameMax));
                _clockEpoch++;
            }
            _brio.SetTimelinePlaying(play);
        }
        else if(play)
        {
            float seconds = (float)TransportMath.PlayPosition(_tl.Playhead / _tl.Fps, 0, _anim.Duration);
            if(!_anim.Seek(seconds) || !_animationSpeed.Play(_anim.Sample.Identity.Actor))
                throw new InvalidOperationException("无法通过 Brio 播放目标动画");
            _anim.Play();
        }
        else
        {
            if(!_animationSpeed.Pause(_anim.Sample.Identity.Actor)) throw new InvalidOperationException("无法通过 Brio 暂停目标动画");
            _anim.Pause();
        }
        _transportOwned = true;
        _tl.Playhead = (float)SourceFrame;
    }

    private void SeekPlayhead(float frame)
    {
        _playing = false;
        _tl.Playhead = Math.Clamp(frame, 0, _tl.Length);
        if((FollowingSource || _drivePlayheadToBrio || _transportOwned) && SourceAvailable)
            Try(() => DriveSourceTo(_tl.Playhead));
    }

    private void TogglePlay() => Try(() => SetSourcePlaying(!TransportPlaying));

    private void StopTransport() => Try(() =>
    {
        if(!SourceAvailable) throw new InvalidOperationException($"{SourceName}不可用");
        if(_source == BindSource.Animation) ValidateAnimationCommand();
        if(_source == BindSource.Vivacity)
        {
            _brio.StopTimeline();
            _clockEpoch++;
        }
        else
        {
            if(!_animationSpeed.Pause(_anim.Sample.Identity.Actor)) throw new InvalidOperationException("无法冻结目标动画");
            if(!_anim.Seek(0)) throw new InvalidOperationException("动画已更换，无法停止");
            _anim.Stop();
        }
        _playing = false;
        _transportOwned = true;
        _tl.Playhead = (float)SourceFrame;
    });

    private bool CurrentLoop => _source == BindSource.Vivacity && _brio.IsConnected ? _brio.TimelineLoop : _animationLoop;

    private void SetLoop(bool loop)
    {
        if(_source == BindSource.Vivacity) _brio.TimelineLoop = loop;
        else _animationLoop = loop;
    }

    private void SetSource(BindSource s)
    {
        if(s == _source) return;
        if(_sync.Bound) _sync.Unbind();
        ReleaseAnimFreeze();
        _anim.Reset();
        _lastAnimationIdentity = default;
        _transportOwned = false;
        _playing = false;
        _clockEpoch++;
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
        get => _tlw.Selected != null && _tl.AllKeys.Contains(_tlw.Selected) &&
            (_tl.SelectedNodes.Count == 0 || _tl.SelectedNodes.Contains(_tlw.Selected.Id))
            ? _tlw.Selected : _tl.AllKeys.FirstOrDefault(k => _tl.SelectedNodes.Contains(k.Id));
        set => _tlw.Selected = value;
    }

    // ------------------------------------------------------------------ ticking

    public void FrameworkTick(float dt)
    {
        bool gpose = _clientState.IsGPosing;
        _bones.NewTick();
        // the movement limit is measured from the current GPose target (no target: no limit)
        NodeRange.Center = gpose && CameraCapture.TryGetBase(_targets, out var targetPos, out _, out _, out _) ? targetPos : null;
        if(!gpose)
        {
            if(_wasGpose)
            {
                ReleaseAnimFreeze();
                _anim.Reset();
                _lastAnimationIdentity = default;
                _transportOwned = false;
                _clockEpoch++;
                if(_sync.Bound) _sync.Unbind();
            }
            _wasGpose = false;
            if(_recording != null) { _recording = null; SetStatus("已离开 GPose，录制目标轨迹已中止。", true); }
            return;
        }
        _wasGpose = true;

        if(_source == BindSource.Animation)
        {
            bool owned = _animationSpeed.Active;
            bool stillOwned = owned && _animationSpeed.OwnsCurrentValue();
            if(owned && !stillOwned) _transportOwned = false;
            bool mayAdvance = true;
            var actor = _targets.GPoseTarget == null ? null : _brio.GetAnimationActor(_targets.GPoseTarget.Address);
            if(actor != null && _brio.TryAnimationSpeed(actor, out _, out float speed)) mayAdvance = speed > 0;
            _anim.Update(dt, _animationLoop, mayAdvance);
            if(_lastAnimationIdentity != default && (!_anim.Available || _lastAnimationIdentity != _anim.Sample.Identity))
            {
                ReleaseAnimFreeze();
                _transportOwned = false;
                _clockEpoch++;
            }
            _lastAnimationIdentity = _anim.Available ? _anim.Sample.Identity : default;
            if(_anim.MustFreeze)
            {
                if(_animationSpeed.Pause(_anim.Sample.Identity.Actor) && _anim.FreezeAtEnd())
                    SetStatus("动画播放完成，已冻结在末尾（未开启循环）。", false);
                else SetStatus("动画已到末尾，但无法冻结目标，请检查 Brio 连接。", true);
            }
            else if(_anim.Ended && _animationSpeed.Frozen && _animationSpeed.OwnsCurrentValue())
                _anim.FreezeAtEnd();
        }

        // fps only follows the Vivacity timeline; the animation bar is in seconds and has no fps of its own
        if(_source == BindSource.Vivacity && _syncFpsWithBrio && _brio.IsConnected)
        {
            int fps = _brio.TimelineFps;
            if(fps != _tl.Fps)
            {
                try
                {
                    _tl.SetFps(fps);
                    SetStatus($"已同步 Brio 灵动时间轴帧率：{fps}fps（镜头节点按秒保持不变）", false);
                }
                catch(InvalidOperationException ex)
                {
                    _syncFpsWithBrio = false;
                    SetStatus(ex.Message + " 已暂停自动帧率同步。", true);
                }
            }
        }

        // Length always follows the bound source immediately (after fps sync, so frames match).
        if(SourceAvailable) SyncLengthFromSource();

        RebuildPathIfNeeded();

        if(SourceAvailable && (FollowingSource || _transportOwned || _sync.Bound))
        {
            _playing = false;
            _tl.Playhead = (float)Math.Clamp(SourceFrame, 0, _tl.Length);
        }

        TickRecording();
        if(_lookThrough && _comp != null) LookThroughPlayhead();

        try { _sync.Tick(_comp?.DurationSec ?? 0); }
        catch(Exception ex) { _log.Warning(ex, "TimelineSync tick failed"); }
    }

    private void LookThroughPlayhead()
    {
        if(!_brio.IsConnected || _comp == null) return;
        // Paused and not scrubbing: leave the free camera to the user (look around, change FoV).
        // Playing, moving the playhead or turning preview on re-syncs it to the path.
        // Tracking constraints own the rotation, so they keep following the (moving) target every frame.
        if(!_playing && !_comp.Tracking && _lookSynced is float last && last == _tl.Playhead) return;
        _lookSynced = _tl.Playhead;
        // node poses are world coordinates
        var s = _comp.Sample(_tl.Playhead);
        // Never push a non-finite or out-of-range pose into the live game camera.
        if(!XcpMath.IsFinite(s.Pos) || !XcpMath.IsFinite(s.Rot) || !float.IsFinite(s.Fov)) return;
        var wp = GizmoEditor.ClampRange(s.Pos);
        var wr = Quaternion.Normalize(s.Rot);
        var (yaw, pitch, roll) = FreeCamMath.FromWorldRotation(wr);
        if(!_brio.SetCurrentFreeCamPose(wp, yaw, pitch, roll, s.Fov - _bake.BaseFov))
        {
            _lookThrough = false;
            SetStatus("镜头预览需要 Brio 当前相机是自由相机（Free）。", true);
        }
    }

    // ------------------------------------------------------------------ overlay (drawn in OverlayWindow)

    private bool GizmoBusy => _gizmo.Enabled && (_curveTool.Active ? _curveTool.HasGizmoTarget : Selected != null || _tl.SelectedNodes.Count > 1)
        && (_gizmo.IsOver || _gizmo.IsUsing);
    // the overlay only takes the mouse over the gizmo or a pickable node / curve point, so the game camera keeps working elsewhere
    public bool OverlayWantsInput => GizmoBusy || _hoverNode != null || _curveTool.Hover != null;
    private Keyframe? _hoverNode;
    private bool _overlayFocused;

    /// <summary>Click a node camera in the game view to select it; Ctrl+click adds/removes it from the selection.</summary>
    private int _deleteFrame = -1;
    private bool _mainFocused;

    /// <summary>Delete removes the selected nodes. Called from the main window and the overlay (whichever is
    /// drawn), evaluated once per frame. The key is read from Windows: neither ImGui nor the game key state
    /// reliably reports it here. Accepted while this window, the game view or the node overlay has focus,
    /// never while another plugin window is focused or a text field is active.</summary>
    private void HandleDeleteKey(bool? mainFocused = null)
    {
        if(mainFocused is bool f) _mainFocused = f;
        if(!IsOpen) _mainFocused = false;
        int frame = ImGui.GetFrameCount();
        if(frame == _deleteFrame) return;
        _deleteFrame = frame;
        bool foreground = Win32.GameIsForeground();
        bool down = foreground && (Win32.GetAsyncKeyState(0x2E) & 0x8000) != 0; // VK_DELETE
        if(down && !_deleteWasDown)
        {
            bool otherFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow) && !_mainFocused && !_overlayFocused;
            // in curve edit mode Delete removes the selected control points instead of camera nodes
            if(!otherFocused && !ImGui.GetIO().WantTextInput && !_curveTool.DeleteSelected()) DeleteSelectedNodes();
        }
        _deleteWasDown = down;
    }

    private void PickNodeInOverlay()
    {
        // picking a node focuses the overlay window; that still counts as "the game view has focus" for Delete
        _overlayFocused = ImGui.IsWindowFocused();
        HandleDeleteKey();
        _hoverNode = null; _curveTool.Hover = null;
        bool blank = PickNodeUnderMouse(out var at);
        TrackBlankClick(blank);
        if(_curveTool.Hover is { } part)
        {
            ImGui.GetBackgroundDrawList().AddCircle(at, 10, 0xFFFFFFFF, 16, 2f);
            ImGui.SetTooltip(part.Spline < 0 ? "物体模式：用变换手柄整体移动/旋转/缩放曲线"
                : part.Part == CurvePart.Point ? "单击选中控制点，Ctrl+单击加选 / 取消" : "单击选中手柄，Ctrl+单击加选 / 取消");
            if(ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _curveTool.Click(part, ImGui.GetIO().KeyCtrl);
            return;
        }
        if(_hoverNode is not { } k) return;
        ImGui.GetBackgroundDrawList().AddCircle(at, 10, 0xFFFFFFFF, 16, 2f);
        ImGui.SetTooltip("单击选中镜头节点，Ctrl+单击加选 / 取消");
        // act only once the overlay actually owns the mouse, so the click never also reaches the game
        if(!ImGui.IsWindowHovered() || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if(ImGui.GetIO().KeyCtrl)
        {
            if(_tl.SelectedNodes.Count == 0 && Selected is { } primary) _tl.SelectNodes([primary.Id]);
            _tl.ToggleNode(k.Id);
            Selected = _tl.SelectedNodes.Contains(k.Id) ? k : null;
        }
        else { _tl.SelectNodes([k.Id]); Selected = k; }
        if(_tl.TrackOf(k) is { } owner) _tl.ActiveTrackIndex = _tl.Tracks.IndexOf(owner);
    }

    /// <summary>Sets <see cref="_hoverNode"/>; returns true when the mouse is over empty game space
    /// (no node, gizmo or ImGui window).</summary>
    private bool PickNodeUnderMouse(out Vector2 at)
    {
        at = default;
        if(!_clientState.IsGPosing || GizmoBusy) return false;
        // another ImGui window (this panel, Brio, ...) under the mouse keeps the click
        if(ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.IsWindowHovered()) return false;
        var mouse = ImGui.GetIO().MousePos;
        float best = 12f * 12f;
        if(_curveTool.Active)
        {
            // curve editing: control points and handles are picked instead of camera nodes
            foreach(var (part, screen) in _curveTool.Candidates)
            {
                float d = Vector2.DistanceSquared(screen, mouse);
                if(d <= best) { best = d; _curveTool.Hover = part; at = screen; }
            }
            return _curveTool.Hover == null;
        }
        foreach(var (key, screen) in _overlay.NodeScreens)
        {
            float d = Vector2.DistanceSquared(screen, mouse);
            if(d <= best) { best = d; _hoverNode = key; at = screen; }
        }
        return _hoverNode == null;
    }

    private bool _lmbWasDown;
    private Win32.Point? _blankPress;

    /// <summary>A short left click on empty game space clears the selection. The game owns the mouse there,
    /// so ImGui does not reliably see the click; the button and cursor are read from Windows instead. The game
    /// still receives the click, and dragging to rotate the camera (cursor moves) does not deselect.</summary>
    private void TrackBlankClick(bool blank)
    {
        bool down = Win32.GameIsForeground() && (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;
        Win32.GetCursorPos(out var cur);
        if(down && !_lmbWasDown) _blankPress = blank ? cur : null;
        else if(_blankPress is { } p)
        {
            int dx = cur.X - p.X, dy = cur.Y - p.Y;
            if(dx * dx + dy * dy > 16) _blankPress = null;
            else if(!down)
            {
                if(_curveTool.Active) _curveTool.ClearSelection();
                else { _tl.SelectNodes([]); Selected = null; }
                _blankPress = null;
            }
        }
        _lmbWasDown = down;
    }

    private static class Win32
    {
        public struct Point { public int X, Y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        public static bool GameIsForeground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
    }

    public void DrawOverlay()
    {
        float? previewFrame = _comp != null && !_lookThrough && (_playing || _followBrioPlayhead || _tl.Playhead != _tl.FirstFrame)
            ? _tl.Playhead : null;
        float? syncFrame = _comp != null && _sync.PlayheadSec is float sec ? _comp.FirstFrame + sec * _tl.Fps : null;
        _overlay.Draw(_comp, _tl, previewFrame, syncFrame, Selected);
        if(_clientState.IsGPosing) _curveTool.DrawScene();
        PickNodeInOverlay();
        if(_curveTool.Active)
        {
            // the gizmo edits curves (points / handles / whole curve) instead of camera nodes
            if(_clientState.IsGPosing) _curveTool.DrawGizmo();
            return;
        }

        var sel = Selected;
        var group = SelectedGroup();
        if(group.Count > 1 && _clientState.IsGPosing)
        {
            // Group: translate-only gizmo at the centre; every selected node moves by the same delta.
            var center = GroupCenter(group);
            var proxy = new Keyframe { Pos = center, Rot = Quaternion.Identity, Fov = sel?.Fov ?? 0.78f };
            var op = _gizmo.Operation; _gizmo.Operation = GizmoEditor.Op.Translate;
            bool moved = _gizmo.Draw(proxy, Vector3.Zero, Quaternion.Identity);
            _gizmo.Operation = op;
            if(_gizmo.DragStarted) _tl.Snapshot();
            if(moved) MoveGroup(group, proxy.Pos - center);
            if(_gizmo.IsUsing && _gizmo.Limited)
                SetStatus($"变换手柄已限制：节点需保持在目标周围 {GizmoEditor.MaxRange:0} 米内，单帧跳动过大的拖动会被忽略。", true);
            return;
        }
        if(sel != null && _clientState.IsGPosing)
        {
            RefreshNodePose(sel);
            var prePos = sel.Pos;
            var preRot = sel.Rot;
            // a tracking constraint owns the rotation: the gizmo only moves the node
            var userOp = _gizmo.Operation;
            if(RotationLocked(sel) && userOp == GizmoEditor.Op.Rotate) _gizmo.Operation = GizmoEditor.Op.Translate;
            bool changed = _gizmo.Draw(sel, Vector3.Zero, Quaternion.Identity); // world-space nodes
            bool translated = _gizmo.Operation == GizmoEditor.Op.Translate, rotated = _gizmo.Operation == GizmoEditor.Op.Rotate;
            _gizmo.Operation = userOp;
            if(_gizmo.DragStarted)
            {
                // snapshot the pose from before this drag, then re-apply the first frame of the drag
                var newPos = sel.Pos; var newRot = sel.Rot;
                sel.Pos = prePos; sel.Rot = preRot;
                _tl.Snapshot();
                sel.Pos = newPos; sel.Rot = newRot;
            }
            if(changed) WriteNodePose(sel, translated, rotated, false);
            if(_gizmo.IsUsing && _gizmo.Limited)
                SetStatus($"变换手柄已限制：节点需保持在目标周围 {GizmoEditor.MaxRange:0} 米内，单帧跳动过大的拖动会被忽略。", true);
        }
    }

    /// <summary>All currently multi-selected nodes, with cached poses refreshed from the curves.</summary>
    private System.Collections.Generic.List<Keyframe> SelectedGroup()
    {
        var group = _tl.AllKeys.Where(k => _tl.SelectedNodes.Contains(k.Id)).ToList();
        if(group.Count > 1) foreach(var k in group) RefreshNodePose(k);
        return group;
    }

    private static Vector3 GroupCenter(System.Collections.Generic.IReadOnlyList<Keyframe> group)
        => group.Aggregate(Vector3.Zero, (sum, k) => sum + k.Pos) / Math.Max(1, group.Count);

    /// <summary>Translates every node of the group by the same target-relative delta (position only).</summary>
    private void MoveGroup(System.Collections.Generic.IReadOnlyList<Keyframe> group, Vector3 delta)
    {
        if(!XcpMath.IsFinite(delta) || delta == Vector3.Zero) return;
        bool limited = false;
        foreach(var k in group)
        {
            var target = k.Pos + delta;
            var clamped = GizmoEditor.ClampRange(target);
            limited |= clamped != target;
            k.Pos = clamped;
            WriteNodePose(k, true, false, false);
        }
        if(limited) SetStatus($"部分节点已限制在目标周围 {GizmoEditor.MaxRange:0} 米内。", true);
    }

    /// <summary>Deletes the multi-selection, or the primary node when nothing is multi-selected.</summary>
    private void DeleteSelectedNodes()
    {
        var ids = _tl.SelectedNodes.Count > 0 ? _tl.SelectedNodes.ToArray() : Selected != null ? new[] { Selected.Id } : [];
        if(ids.Length == 0) return;
        _tl.DeleteNodes(ids); Selected = null;
        SetStatus($"已删除 {ids.Length} 个镜头节点（可撤销）。", false);
    }

    private int ComputeHash()
    {
        var h = new HashCode();
        h.Add(_tl.Revision);
        h.Add(_tl.Fps);
        h.Add(_tl.Tracks.Count);
        foreach(var t in _tl.Tracks)
        {
            h.Add(t.GetHashCode()); h.Add(t.Muted); h.Add(t.Solo); h.Add(t.Keys.Count);
            foreach(var k in t.Keys)
            { h.Add(k.Id); h.Add(k.Frame); h.Add(k.HoldFrames); h.Add(k.CutIn); }
        }
        return h.ToHashCode();
    }

    /// <summary>
    /// Composer for offline output (.xcp, XAT, bake): tracking constraints use their recorded target
    /// trajectories; constraints without a usable recording fall back to the target's current position.
    /// </summary>
    private ShotComposer? ExportComposer(out string warning)
    {
        warning = "";
        if(_comp == null || !_comp.Tracking) return _comp;
        int stale = _tl.Tracks.Where(t => t.Keys.Count > 0)
            .Sum(t => t.TrackConstraints.Count(c => c.Enabled && c.RecordingStale(t.Keys.Min(k => k.Frame), CamTimeline.TrackLastFrame(t))));
        if(stale > 0) warning = $"{stale} 个追踪约束没有录制目标轨迹或录制已过期，这部分按目标当前位置计算（可在「约束」页录制）。";
        return ShotComposer.Create(_tl, _settings, new RecordedTargetSource(_bones));
    }

    // ------------------------------------------------------------------ target trajectory recording

    private sealed class Recording
    {
        public required System.Collections.Generic.List<TrackConstraint> Constraints;
        public int Start, End, LastFrame = -1, Ticks;
    }
    private Recording? _recording;

    /// <summary>Plays the bound source over the active track's frames and records every constraint's bone each tick.</summary>
    private void StartRecording(System.Collections.Generic.IReadOnlyList<TrackConstraint> constraints)
    {
        var track = _tl.ActiveTrack;
        if(_recording != null || constraints.Count == 0) return;
        if(track.Keys.Count == 0) { SetStatus("当前轨道没有镜头节点，无法确定录制范围。", true); return; }
        if(!SourceAvailable) { SetStatus($"录制需要可用的{SourceName}：请进入 GPose、连接 Brio 并选中目标（「设置」页选择绑定）。", true); return; }
        int start = track.Keys.Min(k => k.Frame), end = CamTimeline.TrackLastFrame(track);
        _tl.Snapshot();
        foreach(var c in constraints) { c.Recorded.Clear(); c.RecordedFor = c.TargetKey; }
        _recording = new Recording { Constraints = constraints.ToList(), Start = start, End = end };
        try
        {
            _tl.Playhead = start;
            SetSourcePlaying(true);
            SetStatus($"正在录制目标轨迹：第 {start}–{end} 帧，请勿操作时间轴……", false);
        }
        catch(Exception ex)
        {
            _recording = null;
            SetStatus($"无法开始录制：{ex.Message}", true);
        }
    }

    private void TickRecording()
    {
        if(_recording is not { } r) return;
        r.Ticks++;
        int frame = (int)MathF.Round(_tl.Playhead);
        // looped back to the start: the range is done
        if(r.LastFrame >= 0 && frame < r.LastFrame - 1) { StopRecording(); return; }
        if(frame >= r.Start && frame <= r.End)
            foreach(var c in r.Constraints)
                if(_bones.Position(c, frame) is { } p) c.Recorded[frame] = p;
        r.LastFrame = Math.Max(r.LastFrame, frame);
        if(frame >= r.End || !SourcePlaying && r.Ticks > 10) StopRecording();
    }

    private void StopRecording()
    {
        if(_recording is not { } r) return;
        _recording = null;
        if(SourcePlaying) Try(() => SetSourcePlaying(false));
        int covered = r.Constraints.Count == 0 ? 0 : r.Constraints.Min(c => c.Recorded.Count);
        bool complete = r.Constraints.All(c => !c.RecordingStale(r.Start, r.End));
        _tl.Touch();
        SetStatus(complete ? $"已录制目标轨迹：{r.Constraints.Count} 个约束，每个 {covered} 帧（第 {r.Start}–{r.End} 帧）。导出、联动 XAT 与烘焙将使用录制结果。"
            : $"录制未覆盖第 {r.Start}–{r.End} 帧（已记录 {covered} 帧），请检查绑定的时间轴能否播放到末尾，或角色 / 骨骼是否存在。", !complete);
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
            _comp = ShotComposer.Create(_tl, _settings, _bones);
            if(_sync.Bound && ExportComposer(out _) is { } export) _sync.Refresh(export);
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
        HandleDeleteKey(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows));
        // a selection cleared elsewhere (curve editors' blank click) also drops the primary node
        if(_seenSelection != _tl.SelectionRevision)
        {
            _seenSelection = _tl.SelectionRevision;
            if(_tl.SelectedNodes.Count == 0) Selected = null;
        }
        if(!gpose)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "需要在 GPose 中使用。");

        if(ImGui.BeginTabBar("##tabs"))
        {
            if(ImGui.BeginTabItem("相机时间轴"))
            {
                DrawTimelineTab(gpose);
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("曲线编辑器"))
            {
                DrawGraphTab();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("曲线"))
            {
                _curveTool.DrawPanel(gpose);
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("约束"))
            {
                _constraints.Draw(gpose);
                ImGui.EndTabItem();
            }
            var settingsFlags = _selectSettings ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            _selectSettings = false;
            if(ImGui.BeginTabItem("设置", settingsFlags))
            {
                DrawSettingsTab(gpose);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        if(!string.IsNullOrEmpty(_status))
            ImGui.TextColored(_statusIsError ? new Vector4(1, 0.4f, 0.4f, 1) : new Vector4(0.5f, 1, 0.5f, 1), _status);
    }

    private ShotCurves? ShotFor(Keyframe node)
    {
        var track = _tl.TrackOf(node);
        if(track == null) return null;
        CurveModel.Ensure(track, _tl.Fps);
        var groups = CurveModel.Groups(track);
        int index = groups.FindIndex(g => g.Contains(node));
        return index < 0 ? null : track.Shots[index];
    }

    private void RefreshNodePose(Keyframe node)
    {
        if(ShotFor(node) is not { } shot) return;
        var pose = shot.NodePose(node);
        node.Pos = pose.Pos; node.Rot = pose.Rot; node.Fov = pose.Fov;
    }

    private void DrawInfluenceOption()
    {
        ImGui.SetNextItemWidth(90);
        int influence = _tl.PathInfluence;
        if(ImGui.InputInt("影响范围（节点）", ref influence)) _tl.PathInfluence = Math.Clamp(influence, 0, 50);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("修改节点位置/旋转/FoV/时间时，重新平滑自动切线的范围。\n0：只改变与当前节点相连的两段路径；N：同时重新平滑两侧各 N 个节点。\n手工拉过的手柄不受影响。");
    }

    private void WriteNodePose(Keyframe node, bool position = true, bool rotation = true, bool fov = true)
    {
        if(ShotFor(node) is not { } shot) return;
        try
        {
            shot.WritePose(node, new XcpFrame(node.Pos, node.Rot, node.Fov), position, rotation, fov, _tl.PathInfluence);
            _tl.Touch();
        }
        catch(InvalidOperationException ex) { RefreshNodePose(node); SetStatus(ex.Message, true); }
    }

    private void DrawGraphTab()
    {
        if(ImGui.SmallButton("撤销##graph")) { _tl.Undo(); Selected = null; }
        ImGui.SameLine();
        if(ImGui.SmallButton("重做##graph")) { _tl.Redo(); Selected = null; }
        var selected = Selected;
        if(selected != null && selected.Id != _graphNode)
        {
            _graphNode = selected.Id;
            var owner = _tl.TrackOf(selected);
            if(owner != null)
            {
                _tl.ActiveTrackIndex = _tl.Tracks.IndexOf(owner);
                _graphShot = CurveModel.Groups(owner).FindIndex(g => g.Contains(selected));
            }
        }
        ImGui.SetNextItemWidth(180);
        int trackIndex = _tl.ActiveTrackIndex;
        var trackNames = _tl.Tracks.Select(t => t.Name).ToArray();
        if(ImGui.Combo("轨道", ref trackIndex, trackNames, trackNames.Length))
        { _tl.ActiveTrackIndex = trackIndex; _graphShot = 0; Selected = null; }
        var track = _tl.ActiveTrack;
        CurveModel.Ensure(track, _tl.Fps);
        if(track.Shots.Count == 0) { ImGui.TextDisabled("请先在相机时间轴抓取镜头。"); return; }
        _graphShot = Math.Clamp(_graphShot, 0, track.Shots.Count - 1);
        ImGui.SameLine(); ImGui.SetNextItemWidth(250);
        var names = track.Shots.Select(s => s.Name).ToArray();
        ImGui.Combo("镜头段", ref _graphShot, names, names.Length);
        var shot = track.Shots[_graphShot];
        ImGui.TextDisabled($"{track.Name} → {shot.Name} · 高级/简单曲线互斥编辑");
        _graph.Draw(shot, _tl);
        if(_graph.PlayheadChanged) SeekPlayhead(_tl.Playhead);
        if(!string.IsNullOrEmpty(_graph.Status)) ImGui.TextWrapped(_graph.Status);
        ImGui.TextDisabled("高级曲线直接控制相对位置、姿态、速度与节点帧；简单曲线调整节奏。另一层锁定且保留，切换不重建高级数据。");
    }

    // ---------------- timeline tab ----------------

    private void DrawTimelineTab(bool gpose)
    {
        // --- transport ---
        if(ImGui.Button(TransportPlaying ? "暂停###play" : "播放###play")) TogglePlay();
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(_source == BindSource.Animation
                ? "从播放头位置播放 / 暂停 Brio 目标动画。关闭循环后，动画播完冻结在末尾。"
                : "从播放头位置播放 / 暂停 Brio 灵动时间轴；停止和循环同步控制 Brio，不要求开启播放头跟随。");
        ImGui.SameLine();
        if(ImGui.Button("停止")) StopTransport();
        ImGui.SameLine();
        if(ImGui.ArrowButton("##prev", ImGuiDir.Left)) StepPlayhead(-1);
        ImGui.SameLine();
        if(ImGui.ArrowButton("##next", ImGuiDir.Right)) StepPlayhead(1);
        ImGui.SameLine();
        bool loop = CurrentLoop;
        if(ImGui.Checkbox("循环", ref loop)) Try(() => SetLoop(loop));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        int ph = (int)MathF.Round(_tl.Playhead);
        if(ImGui.InputInt("帧", ref ph)) SeekPlayhead(ph);
        ImGui.SameLine();
        ImGui.TextDisabled($"{_tl.Playhead / _tl.Fps:0.00}s");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        int len = _tl.Length;
        ImGui.BeginDisabled(LengthFromAnimation);
        if(ImGui.InputInt("总长(帧)", ref len, 10)) SetTimelineLength(Math.Max(len, Math.Max(1, _tl.LastFrame)));
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(LengthFromAnimation ? "已绑定动画进度条：总长等于动画长度，不能修改。"
                : LengthFromVivacity ? "已绑定灵动时间轴：总长与 Brio 灵动时间轴的帧长度双向同步。\n不能短于最后一个镜头节点。"
                : "相机时间轴总长（帧）。不能短于最后一个镜头节点。");
        ImGui.SameLine();
        if(_source == BindSource.Vivacity && _syncFpsWithBrio && _brio.IsConnected)
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), $"{_tl.Fps}fps (Brio)");
        else
        {
            ImGui.SetNextItemWidth(60);
            int fps = _tl.Fps;
            if(ImGui.InputInt("fps", ref fps, 0)) Try(() => { _tl.Snapshot(); _tl.SetFps(fps); });
        }
        ImGui.SameLine();
        DrawInfluenceOption();

        // --- capture row ---
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("添加镜头关键帧")) CaptureAtPlayhead();
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("在当前播放头所在帧添加镜头节点（该帧已有节点则替换它的镜头）");
        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("追加到末尾")) CaptureAppend();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose || _tl.Keys.Count == 0);
        if(ImGui.Button("切镜")) CutAtPlayhead();
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
        // TimelineWidget snapshots before applying the first drag delta.
        if(_tlw.PlayheadChanged) SeekPlayhead(_tl.Playhead);
        ImGui.TextWrapped("拖节点改帧 · 标尺拖播放头 · 空白左键/中键拖视图 · Ctrl+滚轮缩放帧轴 · Ctrl+Alt+滚轮缩放轨道行 · Alt+滚轮滚轨道 · Ctrl+左键空白框选 · 双击节点跳转 · ✂=恒定切镜");
        DrawGizmoBar();
        _constraints.DrawSummary(_tl.ActiveTrack);

        ImGui.Separator();
        ImGui.TextDisabled("「曲线编辑器」可选择高级/简单曲线；高级直接调数值、速度和帧，简单调节奏，另一层锁定并保留。");
        ImGui.Separator();
        DrawInspector(gpose);
        DrawTargetWarnings(gpose);
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

    /// <summary>Multi-selection: only the shared centre position is editable; rotation/FoV stay per node.</summary>
    private void DrawGroupInspector(System.Collections.Generic.IReadOnlyList<Keyframe> group)
    {
        ImGui.Text($"镜头组合 · {group.Count} 个节点");
        ImGui.SameLine();
        ImGui.TextDisabled($"F{group.Min(k => k.Frame)}–F{group.Max(k => k.Frame)}");
        var center = GroupCenter(group);
        var pos = center;
        ImGui.SetNextItemWidth(260);
        bool changed = ImGui.DragFloat3("组合中心位置 (米)", ref pos, 0.01f);
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(changed) MoveGroup(group, pos - center);
        ImGui.TextDisabled("拖动中心位置或游戏内手柄会整体平移所有选中镜头；旋转和 FoV 需单独选中一个节点调整。");
        if(ImGui.Button($"删除所选 {group.Count} 节点")) DeleteSelectedNodes();
        ImGui.SameLine();
        if(ImGui.Button("取消选择")) { _tl.SelectNodes([]); Selected = null; }
    }

    private void DrawInspector(bool gpose)
    {
        var k = Selected;
        if(k == null)
        {
            ImGui.TextDisabled(!_tl.AllKeys.Any()
                ? "还没有镜头节点。在 Brio 里摆好镜头后点「添加镜头关键帧」。"
                : _gizmo.Enabled
                    ? "点击时间轴上的◆选中一个镜头节点进行编辑；选中后可在游戏画面中用变换手柄直接移动/旋转它。"
                    : "点击时间轴上的◆选中一个镜头节点进行编辑。（勾选上方「变换手柄」可在游戏画面中直接调整）");
            return;
        }
        var group = SelectedGroup();
        if(group.Count > 1) { DrawGroupInspector(group); return; }

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
            if(got != frame) SetStatus("无法移动：节点不能越过相邻节点，且区间进度关键帧取整后不能重叠。", true);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        int hold = k.HoldFrames;
        if(ImGui.InputInt("停留帧", ref hold, 0))
        {
            hold = Math.Clamp(hold, 0, 100000);
            _tl.Snapshot(); k.HoldFrames = hold; _tl.Touch();
        }
        ImGui.SameLine();
        bool isFirst = owner.Keys.All(o => o == k || o.Frame > k.Frame);
        ImGui.BeginDisabled(isFirst);
        bool cut = k.CutIn;
        if(ImGui.Checkbox("从上一个节点硬切", ref cut))
        {
            _tl.Snapshot();
            if(ShotFor(k) is { } shot) ConstantCuts.Set(shot, k.Id, cut);
            k.CutIn = cut;
            k.CutAuto = false; // a manual choice is never overridden by the auto rule
            _tl.Touch();
        }
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(isFirst ? "这是这条轨道的第一个节点。"
                : "勾选后把前一区间的全部通道设为恒定，保持前一姿态到此帧再瞬跳；不拆分轨道。\n全部通道手动设为恒定也会自动勾选，部分恒定只作局部跳变。");

        RefreshNodePose(k);
        ImGui.TextDisabled("节点姿态修改写回高级曲线；简单曲线的节奏数据独立保留。");

        // transform fields (world coordinates); a following node is positioned by its factor along the curve
        var followShot = ShotFor(k);
        if(followShot?.NodeFactor(k) is double factor)
        {
            float percent = (float)(factor * 100);
            ImGui.SetNextItemWidth(160);
            bool factorChanged = ImGui.DragFloat("路径进度 (%)", ref percent, 0.1f, 0f, 100f, "%.2f");
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            if(factorChanged && followShot.SetNodeFactor(k, percent / 100.0)) { RefreshNodePose(k); _tl.Touch(); }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("跟随路径：位置由节点在曲线上的进度决定，不受抓取时相机位置影响。\n默认首节点 0%、末节点 100%，其余按帧均分；手动修改或在游戏内沿曲线拖动后保持手动值。");
        }
        var pos = k.Pos;
        bool positionChanged = false;
        if(followShot?.IsFollowing != true)
        {
            ImGui.SetNextItemWidth(260);
            positionChanged = ImGui.DragFloat3("位置 (世界坐标, 米)", ref pos, 0.01f);
            if(ImGui.IsItemActivated()) _tl.Snapshot();
        }
        if(positionChanged)
        {
            var limited = GizmoEditor.ClampRange(pos);
            if(limited != pos) SetStatus($"位置已限制在目标周围 {GizmoEditor.MaxRange:0} 米内，超出后游戏无法正常渲染。", true);
            k.Pos = limited; WriteNodePose(k, true, false, false);
        }

        var nodeShot = ShotFor(k);
        bool rotationLocked = RotationLocked(k), fovLocked = FovLocked(k);
        var driven = rotationLocked || fovLocked ? ConstrainedNodePose(k) : default;
        var euler = rotationLocked ? QuatToEulerDeg(driven.Rot) : nodeShot?.NodeEuler(k) ?? QuatToEulerDeg(k.Rot);
        ImGui.SetNextItemWidth(260);
        ImGui.BeginDisabled(rotationLocked);
        bool rotationChanged = ImGui.DragFloat3("旋转 偏航/俯仰/滚转 (°)", ref euler, 0.25f) && !rotationLocked;
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && rotationLocked)
            ImGui.SetTooltip("由追踪约束控制（显示的是约束后的旋转），不能手动修改。可在「约束」页停用或删除约束。");
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(rotationChanged)
        {
            if(nodeShot != null) Try(() => { nodeShot.WriteNodeEuler(k, euler, _tl.PathInfluence); _tl.Touch(); });
            else { k.Rot = EulerDegToQuat(euler); WriteNodePose(k, false, true, false); }
        }

        float fovDeg = (fovLocked ? driven.Fov : k.Fov) * 180f / MathF.PI;
        ImGui.SetNextItemWidth(120);
        ImGui.BeginDisabled(fovLocked);
        bool fovChanged = ImGui.DragFloat("FoV (°)", ref fovDeg, 0.1f, 1f, 175f, "%.1f") && !fovLocked;
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if(fovLocked) ImGui.SetTooltip("由追踪约束的「保持取景大小」控制，不能手动修改。\n取景大小由这条轨道第一个节点的 FoV 决定。");
            else if(_tl.TrackOf(k)?.FovDriven == true) ImGui.SetTooltip("「保持取景大小」以这个首节点的 FoV 和到目标的距离为基准。");
        }
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(fovChanged) { k.Fov = fovDeg * MathF.PI / 180f; WriteNodePose(k, false, false, true); }
        if(rotationLocked) ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), fovLocked ? "旋转与 FoV 由追踪约束控制" : "旋转由追踪约束控制");

        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("用当前镜头替换")) Recapture(k);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if(ImGui.Button("跳到此帧")) SeekPlayhead(k.Frame);
        ImGui.SameLine();
        if(ImGui.Button("复制节点")) Duplicate(k);
        ImGui.SameLine();
        if(ImGui.Button(_tl.SelectedNodes.Count > 1 ? $"删除所选 {_tl.SelectedNodes.Count} 节点" : "删除节点"))
        {
            var ids = _tl.SelectedNodes.Count > 0 ? _tl.SelectedNodes.ToHashSet() : new System.Collections.Generic.HashSet<Guid> { k.Id };
            _tl.DeleteNodes(ids); Selected = null;
        }

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
        // Nodes and curves are world coordinates; only .xcp export / XAT sync need the target.
        if(gpose && !CameraCapture.TryGetBase(_targets, out _, out _, out _, out _))
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "提示：未选中 GPose 目标。镜头节点是世界坐标，可以照常编辑；导出 .xcp / 联动 XAT 需要目标。");
    }

    // ---------------- in-game display & gizmo (merged into the timeline tab) ----------------

    /// <summary>One row: overlay on/off + gizmo mode, shown right under the timeline.</summary>
    private void DrawGizmoBar()
    {
        if(ImGui.Checkbox("游戏内显示", ref _overlay.Enabled)) SaveConfig();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("在游戏画面中显示运镜路径（蓝=慢，红=快）和各镜头视锥。详细选项见「设置」页。");
        ImGui.SameLine();
        ImGui.Checkbox("变换手柄", ref _gizmo.Enabled);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("选中一个镜头节点后，在游戏画面中该镜头处显示手柄，可直接拖动移动/旋转。");
        ImGui.BeginDisabled(!_gizmo.Enabled);
        ImGui.SameLine();
        int target = _curveTool.Active ? 1 : 0;
        ImGui.SetNextItemWidth(70);
        if(ImGui.Combo("##gizmotarget", ref target, "节点\0曲线\0")) { _curveTool.Active = target == 1; _curveTool.ClearSelection(); }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("变换手柄作用对象：镜头节点，或曲线（控制点、贝塞尔曲率手柄、整条曲线；详见「曲线」页）。");
        ImGui.SameLine();
        int op = (int)_gizmo.Operation;
        if(ImGui.RadioButton("移动", ref op, 0)) _gizmo.Operation = GizmoEditor.Op.Translate;
        ImGui.SameLine();
        bool rotateLocked = !_curveTool.Active && Selected is { } locked && RotationLocked(locked);
        ImGui.BeginDisabled(rotateLocked);
        if(ImGui.RadioButton("旋转", ref op, 1)) _gizmo.Operation = GizmoEditor.Op.Rotate;
        ImGui.EndDisabled();
        if(rotateLocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("所选节点的旋转由追踪约束控制。");
        if(_curveTool.Active)
        {
            ImGui.SameLine();
            if(ImGui.RadioButton("缩放", ref op, 2)) _gizmo.Operation = GizmoEditor.Op.Scale;
        }
        else if(_gizmo.Operation == GizmoEditor.Op.Scale) _gizmo.Operation = GizmoEditor.Op.Translate;
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

    /// <summary>Settings tab: display options, movement limit, then Brio integration. Also the plugin's config UI.</summary>
    private void DrawSettingsTab(bool gpose)
    {
        bool changed = false;
        if(ImGui.CollapsingHeader("显示选项", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox("游戏内显示", ref _overlay.Enabled);
            ImGui.BeginDisabled(!_overlay.Enabled);
            changed |= ImGui.Checkbox("运镜路径", ref _overlay.ShowPath);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("按速度着色（蓝=慢，红=快）", ref _overlay.SpeedColors);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("镜头视锥", ref _overlay.ShowFrustums);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("编号/帧标签", ref _overlay.ShowLabels);
            ImGui.SetNextItemWidth(120);
            ImGui.DragFloat("视锥大小", ref _overlay.FrustumSize, 0.01f, 0.05f, 3f, "%.2f");
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.EndDisabled();
        }
        if(ImGui.CollapsingHeader("移动限制", ImGuiTreeNodeFlags.DefaultOpen))
        {
            float limit = NodeRange.MaxRange;
            ImGui.SetNextItemWidth(140);
            if(ImGui.InputFloat("节点移动范围（米）", ref limit, 100f, 1000f, "%.0f")) NodeRange.MaxRange = limit;
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine();
            if(ImGui.SmallButton("恢复默认")) { NodeRange.MaxRange = NodeRange.DefaultRange; changed = true; }
            ImGui.TextDisabled($"镜头节点距当前 GPose 目标不超过此距离（{NodeRange.MinimumRange:0}–{NodeRange.MaximumRange:0} 米，默认 {NodeRange.DefaultRange:0}）；没有目标时不限制。\n节点与曲线都是世界坐标，只在导出 .xcp / 联动 XAT 时换算为相对目标。\n作用于变换手柄、位置输入与预览镜头；已有节点不会被自动挪动，下次编辑时才被限制。\n超出游戏渲染范围时可能无法正常显示，可酌情调小。");
        }
        if(changed) SaveConfig();
        if(ImGui.CollapsingHeader("Brio 联动", ImGuiTreeNodeFlags.DefaultOpen))
            DrawBrioTab(gpose);
    }

    private void SaveConfig()
    {
        _config.MovementLimit = NodeRange.MaxRange;
        _config.OverlayEnabled = _overlay.Enabled; _config.ShowPath = _overlay.ShowPath; _config.ShowFrustums = _overlay.ShowFrustums;
        _config.ShowLabels = _overlay.ShowLabels; _config.SpeedColors = _overlay.SpeedColors; _config.FrustumSize = _overlay.FrustumSize;
        _saveConfig();
    }

    private void DrawLookThroughToggle()
    {
        ImGui.BeginDisabled(!_brio.IsConnected || _comp == null);
        if(ImGui.Checkbox("预览镜头", ref _lookThrough))
        {
            _lookSynced = null; // sync once right away
            if(_lookThrough && !_brio.CurrentCameraIsFree)
            {
                _lookThrough = false;
                SetStatus("请先在 Brio 中切换到一个自由相机（Free）。", true);
            }
        }
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!_brio.IsConnected ? "需要连接 Brio。"
                : _comp == null ? "需要至少一个镜头节点。"
                : "把 Brio 当前的自由相机放到播放头位置，直接看到这一帧的画面。\n暂停时可以自由旋转视角、调整 FoV；播放或移动播放头时重新同步到运镜。");
        if(_lookThrough)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "预览镜头：Brio 自由相机正被运镜接管；暂停时调整后的画面可直接添加关键帧或切镜。");
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
        ImGui.TextDisabled(_source == BindSource.Animation ? "总长自动等于动画长度。" : "总长与灵动时间轴双向同步。");

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
                Try(() =>
                {
                    _sync.Bind(ExportComposer(out var warning)!);
                    SetStatus($"已绑定到{SourceName}（XAT）" + (warning.Length > 0 ? "。" + warning : ""), warning.Length > 0);
                });
        }
        else if(ImGui.Button("解绑"))
            Try(() =>
            {
                _sync.Unbind();
                ReleaseAnimFreeze();
                _transportOwned = false;
                _anim.Reset();
                SetStatus("已解绑，已恢复插件接管前的动画速度。", false);
            });
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
                // the free camera is world space, like the nodes: no target conversion
                _bake.Bake(cams[_bakeCamIndex], ExportComposer(out var warning)!, Vector3.Zero, Quaternion.Identity);
                SetStatus(_bake.Status + (warning.Length > 0 ? " " + warning : ""), warning.Length > 0);
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
        // works while previewing too: the previewed (and, when paused, freely adjusted) view is what gets captured
        if(!CameraCapture.TryCapture(_targets, out var s, out var err) || s is null)
        {
            SetStatus(err, true);
            return null;
        }

        // nodes are stored in world coordinates; the self check rebuilds the view with an identity base
        var pos = s.WorldPos; var rot = Quaternion.Normalize(s.WorldRot);
        var rebuilt = XcpMath.BrioPlaybackView(pos, rot, Vector3.Zero, Quaternion.Identity);
        var err2 = XcpMath.MaxAffineDiff(rebuilt, s.View);

        float fov = _useProjectionFov && s.FovProjection > 0.01f ? s.FovProjection : s.FovRender;
        _log.Info($"capture world={pos} rot={rot} target={s.BasePos} fov={fov} selfCheck={err2}");

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
            ApplyCapture(existing);
            Selected = existing;
            SetStatus($"已替换第 {frame} 帧的镜头", false);
            return;
        }
        k.Frame = frame;
        EnsureLength(k.Frame);
        _tl.Keys.Add(k);
        if(_tl.ActiveTrack.FollowPath != null) ApplyCapture(k);
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
            EnsureLength(k.Frame);
            _tl.Keys.Add(k);
        }
        k.CutIn = true;
        k.CutAuto = false;
        ApplyCapture(k);
        Selected = k;
        SetStatus($"已在第 {frame} 帧切镜：之前的运镜到这里结束，镜头瞬间切到新镜头。", false);
    }

    private void CaptureAppend()
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        k.Frame = _tl.Keys.Count == 0 ? (int)MathF.Round(_tl.Playhead) : CamTimeline.TrackLastFrame(_tl.ActiveTrack) + _defaultGapFrames;
        EnsureLength(k.Frame);
        _tl.Keys.Add(k);
        if(_tl.ActiveTrack.FollowPath != null) ApplyCapture(k);
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

        var segment = seq.Segments[seq.SegmentAt(frame)];
        if(!segment.Curves.Intervals.Any(i => frame > i.Start && frame < i.End))
        { SetStatus("请将播放头放在两个镜头节点之间，停留尾部或镜头段空白区不能分割运动区间。", true); return; }
        var (s, _) = seq.Sample(frame);
        if(!XcpMath.IsFinite(s.Pos) || !XcpMath.IsFinite(s.Rot) || !float.IsFinite(s.Fov))
        {
            SetStatus("这一帧的运镜数据无效，未生成。", true);
            return;
        }

        try
        {
            Selected = _tl.AddSampledNode(segment.Curves, frame);
            SetStatus($"已在第 {frame} 帧添加节点，七通道关键帧已同步。", false);
        }
        catch(InvalidOperationException ex) { SetStatus(ex.Message, true); }
    }

    private void Recapture(Keyframe target)
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        CopyPose(k, target);
        ApplyCapture(target);
        var owner = _tl.TrackOf(target);
        SetStatus($"已用当前镜头替换节点 {(owner?.Keys.IndexOf(target) ?? 0) + 1}", false);
    }

    /// <summary>
    /// Writes a captured camera into a node. On a track with a Follow Path constraint only rotation and FoV come
    /// from the camera: the position is the node's factor along the curve (first node 0 %, last 100 %, the rest
    /// spread by frame), so the player camera position is ignored. Values driven by tracking constraints
    /// (rotation, and FoV with Keep Framing) are not written either.
    /// </summary>
    private void ApplyCapture(Keyframe node)
    {
        var track = _tl.TrackOf(node) ?? _tl.ActiveTrack;
        bool following = track.FollowPath != null;
        bool rotation = !track.RotationDriven, fov = !FovLocked(node);
        WriteNodePose(node, !following, rotation, fov);
        if(following || !rotation || !fov) RefreshNodePose(node);
    }

    /// <summary>A tracking constraint drives this node's rotation.</summary>
    private bool RotationLocked(Keyframe node) => _tl.TrackOf(node)?.RotationDriven == true;

    /// <summary>Keep Framing drives the FoV; the track's first node stays editable (it sets the framing size).</summary>
    private bool FovLocked(Keyframe node)
        => _tl.TrackOf(node) is { FovDriven: true } track && track.Keys.Min(k => k.Frame) != node.Frame;

    /// <summary>Node pose as output: with the tracking constraints of its track applied.</summary>
    private XcpFrame ConstrainedNodePose(Keyframe node)
    {
        var pose = new XcpFrame(node.Pos, node.Rot, node.Fov);
        var track = _tl.TrackOf(node);
        return track != null && _comp?.SequenceOf(_tl.Tracks.IndexOf(track)) is { Tracking: true } seq ? seq.Constrain(pose, node.Frame) : pose;
    }

    private static void CopyPose(Keyframe from, Keyframe to)
    {
        to.Pos = from.Pos; to.Rot = from.Rot; to.Fov = from.Fov; to.SelfCheckError = from.SelfCheckError;
        to.TargetName = from.TargetName; to.BasePos = from.BasePos; to.BaseRot = from.BaseRot;
    }

    private void Duplicate(Keyframe k)
    {
        var owner = _tl.TrackOf(k) ?? _tl.ActiveTrack;
        RefreshNodePose(k);
        _tl.Snapshot();
        var c = k.Clone();
        c.Id = Guid.NewGuid();
        c.CutIn = false; c.CutAuto = false; c.LastGap = 0;
        c.Frame = _tl.NearestFreeFrame(k.Frame + _defaultGapFrames, null, owner);
        EnsureLength(c.Frame);
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
            // .xcp is relative to the GPose target: convert the world-space path with the current target
            if(!CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out var err))
                throw new InvalidOperationException($"导出 .xcp 需要 GPose 目标（{err}）");
            var export = ExportComposer(out var warning)!;
            var frames = XcpMath.ToRelative(export.BakeXcp(PathBuilder.Fps), bp, br);
            XcpWriter.Write(path, frames);
            _lastDir = Path.GetDirectoryName(path) ?? _lastDir;
            string cutNote = _comp.CutFrames.Count > 0 ? $"，{_comp.CutFrames.Count} 处切镜（.xcp 切镜处约有 1 帧过渡）" : "";
            SetStatus($"已导出 {frames.Count} 帧 (30fps){cutNote} → {path}" + (warning.Length > 0 ? "。" + warning : ""), warning.Length > 0);
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
