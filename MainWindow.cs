using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

public sealed partial class MainWindow : Window, IDisposable
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
    private bool _importedVivacityPending;
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
    private bool _openedByGpose;
    private AnimationIdentity _lastAnimationIdentity;
    /// <summary>Character whose animation bar drives the timeline; empty = the current GPose target.</summary>
    private string _animActorName = "";
    private int _animActorIndex = -1;
    /// <summary>Brio's "animation applied" state of the bound character in the previous tick.</summary>
    private bool? _lastBaseOverride;

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? BoundActor() => _bones.Resolve(_animActorName, ref _animActorIndex);

    private static string SourceLabel(BindSource s) => s == BindSource.Vivacity ? I18n.T("brio.source.vivacity") : I18n.T("brio.source.animation");

    private enum MainTab { Timeline, Graph }
    private MainTab _activeTab = MainTab.Timeline;
    private IDisposable? _styleScope;
    private bool _sidebarResizing;
    private int _lastDrawnFrame = -10;
    private bool _forceExpand;

    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly bool[] _shortcutDown = new bool[Enum.GetValues<Shortcut>().Length];
    private long _seenSelection = -1;

    // ---- Public bridge surface for SettingsWindow, ViewportToolbarWindow, SubWindows & MiniTimelineWindow ----
    public Configuration Config => _config;
    public CamTimeline Timeline => _tl;
    public ShotComposer? Composer => _comp;
    public Overlay Overlay => _overlay;
    public GizmoEditor Gizmo => _gizmo;
    public CurveTool CurveTool => _curveTool;
    public Keyframe? SelectedNode { get => Selected; set => Selected = value; }
    public bool IsExpanded => IsOpen && _lastDrawnFrame >= ImGui.GetFrameCount() - 1;
    public bool IsTransportPlaying => TransportPlaying;
    public bool IsCurrentLoop => CurrentLoop;
    public string TransportPlayTooltip => _source == BindSource.Animation ? I18n.T("transport.play_anim_tip") : I18n.T("transport.play_vivacity_tip");
    public bool LookThrough => _lookThrough;
    public bool CanLookThrough => _brio.IsConnected && _comp != null;
    public string LookThroughTooltip => !_brio.IsConnected ? I18n.T("preview.need_brio")
        : _comp == null ? I18n.T("preview.need_node")
        : I18n.T("preview.tip");

    public Action? OnOpenSettings;
    public Action? OnOpenMiniTimelineSettings;
    public Action? OnToggleViewportToolbar;
    public Func<bool>? IsPathWindowOpen;
    public Action<bool?>? SetPathWindowOpen;
    public Func<bool>? IsConstraintsWindowOpen;
    public Action<bool?>? SetConstraintsWindowOpen;
    public Func<bool>? IsBrioWindowOpen;
    public Action<bool?>? SetBrioWindowOpen;
    public Action? OnGposeAutoOpened;
    public Action? OnGposeAutoClose;

    /// <summary>Manual open/close trigger (/xcpcam, OpenMainUi): opening manually clears GPose auto-open ownership.</summary>
    public void ToggleManual()
    {
        if(!IsOpen) _openedByGpose = false;
        Toggle();
    }

    public void ExpandAndFocus()
    {
        IsOpen = true;
        _forceExpand = true;
    }

    public void TriggerSeekPlayhead(float frame) => SeekPlayhead(frame);
    public void TriggerStepPlayhead(int dir) => StepPlayhead(dir);
    public void TriggerTogglePlay() => TogglePlay();
    public void TriggerStopTransport() => StopTransport();
    public void TriggerSetLoop(bool loop) => Try(() => SetLoop(loop));
    public void HandleExternalShortcuts(bool focused) => HandleShortcuts(focused);

    public void DrawPathWindow()
    {
        HandleShortcuts(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows));
        _curveTool.DrawPanel(_clientState.IsGPosing, _config.UseBrioTheme);
    }

    public void DrawConstraintsWindow()
    {
        HandleShortcuts(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows));
        _constraints.Draw(_clientState.IsGPosing, _config.UseBrioTheme);
    }

    public void DrawBrioWindow()
    {
        HandleShortcuts(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows));
        DrawBrioTab(_clientState.IsGPosing, _config.UseBrioTheme);
    }

    public bool CurveOwnsGizmo => _curveTool.OwnsGizmo;
    public bool GizmoRotateLocked => !_curveTool.OwnsGizmo && _tl.SelectedNodes.Count <= 1 && Selected is { } locked && RotationLocked(locked);
    public bool CanUndo => _tl.CanUndo;
    public bool CanRedo => _tl.CanRedo;
    public bool CanCutAtPlayhead => _clientState.IsGPosing && _tl.Keys.Count > 0;
    public bool CanDeleteSelection => _curveTool.Selection.Count > 0 || _tl.SelectedNodes.Count > 0 || Selected != null;

    public string ToolbarContextHeader
    {
        get
        {
            if(_curveTool.OwnsGizmo && _curveTool.Curve is { } curve)
                return _curveTool.EditMode ? I18n.T("toolbar.ctx.curve_points", _curveTool.Selection.Count) : curve.Name;
            var group = SelectedGroup();
            if(group.Count > 1) return I18n.T("toolbar.ctx.camera_group", group.Count);
            if(Selected is { } k)
            {
                var owner = _tl.TrackOf(k) ?? _tl.ActiveTrack;
                return I18n.T("toolbar.ctx.camera_node", owner.Keys.IndexOf(k) + 1, k.Frame);
            }
            return I18n.T("toolbar.ctx.quick_actions");
        }
    }

    public void SetLookThrough(bool enabled)
    {
        if(!CanLookThrough) { _lookThrough = false; return; }
        _lookThrough = enabled;
        _lookSynced = null;
        if(_lookThrough && !_brio.CurrentCameraIsFree)
        {
            _lookThrough = false;
            SetStatus(I18n.T("preview.need_free_cam"), true);
        }
    }

    public void TriggerCaptureAtPlayhead() => CaptureAtPlayhead();
    public void TriggerCutAtPlayhead() => CutAtPlayhead();
    public void TriggerUndo() { if(_tl.CanUndo) RestoreHistory(redo: false); }
    public void TriggerRedo() { if(_tl.CanRedo) RestoreHistory(redo: true); }
    public void TriggerDelete() { if(!_curveTool.DeleteSelected()) DeleteSelectedNodes(); }

    /// <summary>Opens the standalone settings window (Dalamud OpenConfigUi).</summary>
    public void OpenSettings() => OnOpenSettings?.Invoke();

    public MainWindow(ITargetManager targets, IClientState clientState, IPluginLog log, BrioBridge brio, Overlay overlay,
        BoneSource bones, Configuration config, Action saveConfig)
        : base("XcpCam###xcpcam_main")
    {
        _targets = targets;
        _bones = bones;
        _clientState = clientState;
        _log = log;
        _brio = brio;
        _overlay = overlay;
        _anim = new AnimationClock(BoundActor);
        _animationSpeed = new AnimationSpeedLease(brio);
        _sync = new TimelineSync(brio, log)
        {
            Clock = ReadBoundClock,
            ClockNameProvider = () => SourceName,
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
        _tlw.Curves = _curveTool;
        _constraints = new ConstraintPanel(_tl, bones, SetStatus, StartRecording, () => _recording != null, StopRecording);
        Size = new Vector2(1060, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 440),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        I18n.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        _status = "";
        _statusIsError = false;
    }

    public override void PreDraw()
    {
        if(_forceExpand)
        {
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
            ImGui.SetNextWindowFocus();
            _forceExpand = false;
        }
        _styleScope = XcpStyle.PushWindow(_config.UseBrioTheme);
    }

    public override void PostDraw()
    {
        _styleScope?.Dispose();
        _styleScope = null;
    }

    // ---------------- bound clock abstraction ----------------

    private string SourceName => SourceLabel(_source);
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
    private bool LengthFromAnimation => _syncLengthWithSource && SourceAvailable && _source == BindSource.Animation;
    private bool LengthFromVivacity => _syncLengthWithSource && SourceAvailable && _source == BindSource.Vivacity;

    private void SyncLengthFromSource()
    {
        if(SourceLengthFrames is int length && length > 0) _tl.Length = Math.Max(_tl.LastFrame, length);
    }

    /// <summary>Sets the timeline length; when bound to Vivacity also writes Brio's FrameMax.</summary>
    private void SetTimelineLength(int frames)
    {
        if(LengthFromAnimation) return; // the animation's own duration is authoritative
        frames = Math.Clamp(frames, 1, Math.Max(100000, _tl.Length));
        _tl.Length = frames;
        if(LengthFromVivacity) WriteVivacityLength(frames);
    }

    private void WriteVivacityLength(int frames)
    {
        int brioFrames = (int)Math.Round(TransportMath.ConvertFrame(frames, _tl.Fps, _brio.TimelineRate));
        _brio.FrameMax = Math.Max(_brio.FrameMin + 1, brioFrames);
    }

    private void AlignToVivacity()
    {
        int fps = _brio.TimelineFps;
        _tl.SetFps(fps);
        if(LengthFromVivacity) WriteVivacityLength(_tl.Length);
        SetStatus(I18n.T("status.vivacity_fps_synced", fps), false);
    }

    private void SyncImportedVivacityLength()
    {
        if(!_importedVivacityPending || !LengthFromVivacity) return;
        WriteVivacityLength(_tl.Length);
        _importedVivacityPending = false;
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
        throw new InvalidOperationException(I18n.T("status.anim_changed_retry"));
    }

    private void DriveSourceTo(float frame)
    {
        if(!SourceAvailable) throw new InvalidOperationException(I18n.T("status.source_unavailable", SourceName));
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
            if(!_animationSpeed.Pause(_anim.Sample.Identity.Actor)) throw new InvalidOperationException(I18n.T("status.anim_freeze_failed"));
            _anim.Pause();
        }
        if(!_anim.Seek(frame / _tl.Fps)) throw new InvalidOperationException(I18n.T("status.anim_seek_failed"));
    }

    private void ReleaseAnimFreeze() => _animationSpeed.Release();

    private void SetSourcePlaying(bool play)
    {
        if(!SourceAvailable) throw new InvalidOperationException(I18n.T("status.source_unavailable", SourceName));
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
                throw new InvalidOperationException(I18n.T("status.anim_play_failed"));
            _anim.Play();
        }
        else
        {
            if(!_animationSpeed.Pause(_anim.Sample.Identity.Actor)) throw new InvalidOperationException(I18n.T("status.anim_pause_failed"));
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
        if(!SourceAvailable) throw new InvalidOperationException(I18n.T("status.source_unavailable_short", SourceName));
        if(_source == BindSource.Animation) ValidateAnimationCommand();
        if(_source == BindSource.Vivacity)
        {
            _brio.StopTimeline();
            _clockEpoch++;
        }
        else
        {
            if(!_animationSpeed.Pause(_anim.Sample.Identity.Actor)) throw new InvalidOperationException(I18n.T("status.anim_freeze_failed_short"));
            if(!_anim.Seek(0)) throw new InvalidOperationException(I18n.T("status.anim_stop_failed"));
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

    /// <summary>Which character's animation bar drives the timeline. Brio's "all actors" play / freeze / stop
    /// act on every character; the timeline follows this one only.</summary>
    private void DrawAnimationActorPicker(bool compact = false)
    {
        string label = string.IsNullOrEmpty(_animActorName)
            ? (_targets.GPoseTarget is { } t ? I18n.T("brio.actor_current_gpose_named", t.Name.TextValue) : I18n.T("brio.actor_current_gpose"))
            : _animActorName;
        string comboLbl = compact ? I18n.T("brio.actor_label_compact") : I18n.T("brio.actor_label");
        ImGui.SetNextItemWidth(compact ? MathF.Max(100f * XcpStyle.Scale, UiWidgets.InnerAvailWidth - ImGui.CalcTextSize(comboLbl).X - 14f * XcpStyle.Scale) : 240f * XcpStyle.Scale);
        if(ImGui.BeginCombo(compact ? $"{comboLbl}##anim_act_c" : $"{comboLbl}##anim_act", label))
        {
            if(ImGui.Selectable($"{I18n.T("brio.actor_current_gpose")}##gpose_act", string.IsNullOrEmpty(_animActorName))) SetAnimationActor("", -1);
            foreach(var (index, name) in _bones.Characters())
                if(ImGui.Selectable($"{name}##{index}", _animActorName == name && _animActorIndex == index)) SetAnimationActor(name, index);
            ImGui.EndCombo();
        }
        UiWidgets.Tooltip(I18n.T("brio.actor_tip"));
    }

    private void SetAnimationActor(string name, int index)
    {
        if(name == _animActorName && index == _animActorIndex) return;
        if(_sync.Bound) _sync.Unbind();
        ReleaseAnimFreeze();
        _anim.Reset();
        _lastAnimationIdentity = default;
        _lastBaseOverride = null;
        _transportOwned = false;
        _playing = false;
        _clockEpoch++;
        _animActorName = name; _animActorIndex = index;
        SetStatus(I18n.T("status.anim_actor_changed", name.Length > 0 ? name : I18n.T("brio.actor_current_gpose")), false);
    }

    private void SetSource(BindSource s)
    {
        if(s == _source) return;
        if(_sync.Bound) _sync.Unbind();
        ReleaseAnimFreeze();
        _anim.Reset();
        _lastAnimationIdentity = default;
        _lastBaseOverride = null;
        _transportOwned = false;
        _playing = false;
        _clockEpoch++;
        _source = s;
        SetStatus(I18n.T("status.source_changed", SourceName), false);
    }

    public void Dispose()
    {
        I18n.LanguageChanged -= OnLanguageChanged;
        _importCancel?.Cancel();
        _importCancel?.Dispose();
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
                if(_openedByGpose && _config.OpenOnGPose)
                {
                    IsOpen = false;
                    OnGposeAutoClose?.Invoke();
                }
                _openedByGpose = false;
                ReleaseAnimFreeze();
                _anim.Reset();
                _lastAnimationIdentity = default;
                _lastBaseOverride = null;
                _transportOwned = false;
                _clockEpoch++;
                if(_sync.Bound) _sync.Unbind();
            }
            _wasGpose = false;
            if(_recording != null) { _recording = null; SetStatus(I18n.T("status.left_gpose_rec_aborted"), true); }
            return;
        }
        if(!_wasGpose && _config.OpenOnGPose && !IsOpen)
        {
            OnGposeAutoOpened?.Invoke();
            IsOpen = true;
            _openedByGpose = true;
        }
        _wasGpose = true;

        if(_source == BindSource.Animation)
        {
            // An external speed edit (Brio freeze / un-freeze, also "all actors") ends only our speed lease; the
            // timeline keeps following the animation, so it pauses and resumes with it.
            if(_animationSpeed.Active) _animationSpeed.OwnsCurrentValue();
            bool mayAdvance = true;
            bool? baseOverride = null;
            var bound = BoundActor();
            var actor = bound == null ? null : _brio.GetAnimationActor(bound.Address);
            if(actor != null)
            {
                if(_brio.TryAnimationSpeed(actor, out _, out float speed)) mayAdvance = speed > 0;
                baseOverride = _brio.HasBaseOverride(actor);
            }
            _anim.Update(dt, _animationLoop, mayAdvance);
            if(_lastAnimationIdentity != default && (!_anim.Available || _lastAnimationIdentity != _anim.Sample.Identity))
            {
                ReleaseAnimFreeze();
                _clockEpoch++;
                if(_anim.Available && baseOverride == true)
                {
                    // Brio "Play all Animations" / Play restarted the bound character's animation: follow it from its start
                    _transportOwned = true;
                }
                else
                {
                    _transportOwned = false;
                    if(baseOverride == false && _lastBaseOverride == true)
                    {
                        // Brio "Stop all Animations" / Stop: back to the start, ready to play again
                        _playing = false;
                        _tl.Playhead = 0;
                        SetStatus(I18n.T("status.brio_stopped_anim"), false);
                    }
                }
            }
            // the bound character's animation started moving (Brio play / un-freeze): the timeline follows it
            else if(!_transportOwned && baseOverride == true && _anim.IsPlaying)
                _transportOwned = true;
            _lastAnimationIdentity = _anim.Available ? _anim.Sample.Identity : default;
            if(baseOverride != null) _lastBaseOverride = baseOverride;
            if(_anim.MustFreeze)
            {
                if(_animationSpeed.Pause(_anim.Sample.Identity.Actor) && _anim.FreezeAtEnd())
                    SetStatus(I18n.T("status.anim_ended_frozen"), false);
                else SetStatus(I18n.T("status.anim_ended_freeze_failed"), true);
            }
            else if(_anim.Ended && _animationSpeed.Frozen && _animationSpeed.OwnsCurrentValue())
                _anim.FreezeAtEnd();
        }

        // fps only follows the Vivacity timeline; the animation bar is in seconds and has no fps of its own
        if(_source == BindSource.Vivacity && _syncFpsWithBrio && _brio.IsConnected)
        {
            if(_brio.TimelineFps != _tl.Fps)
            {
                try { AlignToVivacity(); }
                catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or OverflowException)
                {
                    _syncFpsWithBrio = false;
                    SetStatus(I18n.T("status.vivacity_fps_sync_paused", ex.Message), true);
                }
            }
        }

        // Publish an imported length before polling the previous source length.
        SyncImportedVivacityLength();
        if(_syncLengthWithSource && SourceAvailable) SyncLengthFromSource();

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
            SetStatus(I18n.T("preview.need_free_cam_live"), true);
        }
    }

    // ------------------------------------------------------------------ overlay (drawn in OverlayWindow)

    private bool GizmoBusy => _gizmo.Enabled && (_curveTool.OwnsGizmo ? _curveTool.HasGizmoTarget : Selected != null || _tl.SelectedNodes.Count > 1)
        && (_gizmo.IsOver || _gizmo.IsUsing);
    // the overlay only takes the mouse over the gizmo or a pickable node / curve point, so the game camera keeps working elsewhere
    public bool OverlayWantsInput => _nodeMenuOpen || GizmoBusy || _hoverNode != null || _curveTool.Hover != null || _curveTool.HoverFollow != null;
    private Keyframe? _hoverNode;
    private bool _overlayFocused;

    /// <summary>Click a node camera in the game view to select it; Ctrl+click adds/removes it from the selection.</summary>
    private int _shortcutFrame = -1;
    private int _focusedFrame = -1;

    private void RestoreHistory(bool redo)
    {
        long revision = _tl.Revision;
        int length = _tl.Length;
        if(redo) _tl.Redo(); else _tl.Undo();
        if(_tl.Revision == revision) return;
        Selected = null;
        _importedVivacityPending = false;
        if(LengthFromVivacity && length != _tl.Length) WriteVivacityLength(_tl.Length);
    }

    private void HandleShortcuts(bool? windowFocused = null)
    {
        int frame = ImGui.GetFrameCount();
        if(windowFocused == true) _focusedFrame = frame;
        if(frame == _shortcutFrame) return;
        _shortcutFrame = frame;
        bool pluginFocused = _focusedFrame >= frame - 1;
        static bool Down(int key) => (Win32.GetAsyncKeyState(key) & 0x8000) != 0;
        int mods = (Down(0x11) ? 1 : 0) | (Down(0x10) ? 2 : 0) | (Down(0x12) ? 4 : 0);
        bool accepted = Win32.GameIsForeground() && !ImGui.GetIO().WantTextInput
            && (!ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow) || pluginFocused || _overlayFocused)
            && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel);
        for(int i = 0; i < _shortcutDown.Length; i++)
        {
            var action = (Shortcut)i;
            if(!ShortcutBindings.IsKeyboard(action)) continue;
            int chord = _config.Shortcuts.Get(action), key = chord & 255;
            bool down = key != 0 && Down(key);
            bool pressed = down && !_shortcutDown[i];
            _shortcutDown[i] = down;
            if(!pressed || !accepted || mods != (chord >> 8)) continue;
            switch(action)
            {
                case Shortcut.Undo when _tl.CanUndo: RestoreHistory(redo: false); break;
                case Shortcut.Redo when _tl.CanRedo: RestoreHistory(redo: true); break;
                case Shortcut.ToggleCut when !_curveTool.OwnsGizmo && _tl.SelectedNodes.Count <= 1 && Selected is { } node && CanToggleCut(node):
                    ToggleNodeCut(node); break;
                case Shortcut.Delete: if(!_curveTool.DeleteSelected()) DeleteSelectedNodes(); break;
                case Shortcut.AnchorTimeline or Shortcut.AnchorCamera when _curveTool.SelectedPoints().Count > 0:
                    AnchorSelectedCurve(action == Shortcut.AnchorTimeline); break;
            }
        }
    }

    private void AnchorSelectedCurve(bool timeline)
    {
        if(_curveTool.AnchorTrack is not { } track) { SetStatus(I18n.T("anchor.err_unfollowed"), true); return; }
        if(_tl.AnchorCurve(track, timeline))
            SetStatus(timeline
                ? I18n.T("anchor.ok_timeline", track.FollowCurve?.Name ?? "", CamTimeline.TimelineAnchorNotice)
                : I18n.T("anchor.ok_camera", track.FollowCurve?.Name ?? ""), false);
        else if(timeline && !CamTimeline.CurveAnchoredToTimeline(track)) SetStatus(I18n.T("anchor.err_no_visit"), true);
        else SetStatus(timeline ? I18n.T("anchor.already_timeline") : I18n.T("anchor.already_camera"), false);
    }

    /// <summary>Anchor mode of the whole followed curve (anchoring is per curve, never per point).</summary>
    private void DrawCurveAnchorMode(CamTrack track, bool brioTheme)
    {
        bool anchored = CamTimeline.CurveAnchoredToTimeline(track);
        int mode = anchored ? 1 : 0;
        string note = I18n.T("anchor.scope_note");
        if(UiWidgets.Segmented("curve_anchor_seg", ref mode, [I18n.T("anchor.camera"), I18n.T("anchor.timeline")], brioTheme, UiWidgets.InnerAvailWidth,
            [I18n.T("anchor.camera_tip", note),
             I18n.T("anchor.timeline_tip", CamTimeline.TimelineAnchorNotice, note)]))
        {
            AnchorSelectedCurve(mode == 1);
        }
    }

    /// <summary>Inspector for curve points selected on a following track's lane or in the game view (N-Panel).</summary>
    private void DrawCurvePointInspector(CurveObject curve, System.Collections.Generic.List<(int spline, int index, CurvePoint point)> points, bool brioTheme)
    {
        float s = XcpStyle.Scale;
        var track = _curveTool.AnchorTrack;
        var times = (track?.Shots.FirstOrDefault()?.CurvePointTimes() ?? []).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        double? FrameOf(Guid id) => track?.FollowPath?.TimeAnchors.TryGetValue(id, out double f) == true ? f
            : times.TryGetValue(id, out var t) ? t.Frame : null;

        if(points.Count == 1)
        {
            var (si, index, point) = points[0];
            UiWidgets.SectionHeader($"{curve.Name} · P{index + 1}", brioTheme, FontAwesomeIcon.BezierCurve);

            if(track?.FollowPath == null)
                ImGui.TextDisabled(I18n.T("inspector.cp_unfollowed"));
            else
            {
                bool anchored = CamTimeline.CurveAnchoredToTimeline(track);
                DrawCurveAnchorMode(track, brioTheme);
                ImGui.Spacing();
                if(FrameOf(point.Id) is double frame)
                {
                    double f = frame;
                    string jumpLbl = I18n.T("common.jump");
                    float jumpW = MathF.Max(52f * s, ImGui.CalcTextSize(jumpLbl).X + 28f * s);
                    string frameLbl = I18n.T("common.frame");
                    ImGui.SetNextItemWidth(MathF.Max(70f * s, UiWidgets.InnerAvailWidth - jumpW - ImGui.CalcTextSize(frameLbl).X - 18f * s));
                    if(ImGui.InputDouble($"{frameLbl}##curvepoint", ref f, 1, 10, "%.3f")) _tl.SetCurvePointAnchor(track, point.Id, f);
                    UiWidgets.Tooltip(anchored ? I18n.T("inspector.cp_frame_anchored_tip") : I18n.T("inspector.cp_frame_camera_tip"));
                    ImGui.SameLine(0, 4f * s);
                    if(UiWidgets.IconTextButton("cp_jump", FontAwesomeIcon.ArrowRight, jumpLbl, I18n.T("inspector.cp_jump_tip"), size: new Vector2(jumpW, 0), brioTheme: brioTheme))
                        SeekPlayhead((float)f);

                    if(times.TryGetValue(point.Id, out var t))
                        ImGui.TextDisabled(I18n.T("inspector.cp_factor", $"{t.Factor * 100:0.00}"));

                    if(track.FollowPath!.LoopEndFrame is double loopEnd && index == 0 && anchored)
                    {
                        double e = loopEnd;
                        string loopLbl = I18n.T("inspector.cp_loop_end");
                        ImGui.SetNextItemWidth(MathF.Max(70f * s, UiWidgets.InnerAvailWidth - jumpW - ImGui.CalcTextSize(loopLbl).X - 18f * s));
                        if(ImGui.InputDouble($"{loopLbl}##curvepoint", ref e, 1, 10, "%.3f")) _tl.SetCurvePointAnchor(track, point.Id, e, loopEnd: true);
                        UiWidgets.Tooltip(I18n.T("inspector.cp_loop_end_tip"));
                        ImGui.SameLine(0, 4f * s);
                        if(UiWidgets.IconTextButton("cp_jump_e", FontAwesomeIcon.ArrowRight, jumpLbl, I18n.T("inspector.cp_jump_loop_tip"), size: new Vector2(jumpW, 0), brioTheme: brioTheme))
                            SeekPlayhead((float)loopEnd);
                    }
                }
                else ImGui.TextColored(XcpStyle.Warning, I18n.T("inspector.cp_unvisited"));
            }

            ImGui.Spacing();
            var world = curve.ToWorld(point.Co);
            string posLbl = I18n.T("inspector.cp_pos");
            ImGui.SetNextItemWidth(MathF.Max(100f * s, UiWidgets.InnerAvailWidth - ImGui.CalcTextSize(posLbl).X - 14f * s));
            bool moved = ImGui.DragFloat3($"{posLbl}##curvepoint", ref world, 0.01f);
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            if(moved) { CurveEdit.MovePoint(curve.Splines[si], index, curve.ToLocal(world)); curve.Touch(); _tl.Touch(); }
            if(curve.Splines[si].Type == SplineType.Bezier) _curveTool.DrawHandleTypeControl(brioTheme, compact: true);
            _curveTool.DrawPointProperties(curve, [point], compact: true);

            ImGui.Spacing();
            float halfW = MathF.Max(60f * s, (UiWidgets.InnerAvailWidth - 6f * s) * 0.5f);
            if(UiWidgets.IconTextButton("cp_clr", FontAwesomeIcon.Times, I18n.T("common.deselect"), size: new Vector2(halfW, 0), brioTheme: brioTheme))
                _curveTool.ClearSelection();
            ImGui.SameLine(0, 6f * s);
            if(UiWidgets.DangerButton("cp_del", FontAwesomeIcon.TrashAlt, I18n.T("common.delete"), I18n.T("inspector.cp_delete_tip"), new Vector2(halfW, 0), brioTheme))
                _curveTool.DeleteSelected();
            return;
        }

        UiWidgets.SectionHeader(I18n.T("inspector.cps_title", curve.Name, points.Count), brioTheme, FontAwesomeIcon.ObjectGroup);
        var frames = points.Select(p => FrameOf(p.point.Id)).OfType<double>().ToList();
        if(frames.Count > 0)
            ImGui.TextDisabled(I18n.T("inspector.time_range", Math.Round(frames.Min()), Math.Round(frames.Max())) +
                (frames.Count < points.Count ? I18n.T("inspector.cps_unvisited_suffix", points.Count - frames.Count) : ""));

        var center = points.Aggregate(Vector3.Zero, (sum, p) => sum + curve.ToWorld(p.point.Co)) / points.Count;
        var pos = center;
        string ctrLbl = I18n.T("inspector.group_center");
        ImGui.SetNextItemWidth(MathF.Max(100f * s, UiWidgets.InnerAvailWidth - ImGui.CalcTextSize(ctrLbl).X - 14f * s));
        bool changed = ImGui.DragFloat3($"{ctrLbl}##curvepoints", ref pos, 0.01f);
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(changed)
        {
            var delta = pos - center;
            foreach(var (si, index, point) in points) CurveEdit.MovePoint(curve.Splines[si], index, curve.ToLocal(curve.ToWorld(point.Co) + delta));
            curve.Touch(); _tl.Touch();
        }
        _curveTool.DrawPointProperties(curve, points.Select(p => p.point).ToList(), compact: true);
        ImGui.Spacing();
        if(track != null) DrawCurveAnchorMode(track, brioTheme);
        else ImGui.TextDisabled(I18n.T("inspector.cps_unfollowed"));

        ImGui.Spacing();
        float btnHalf = MathF.Max(60f * s, (UiWidgets.InnerAvailWidth - 6f * s) * 0.5f);
        if(UiWidgets.IconTextButton("cps_clr", FontAwesomeIcon.Times, I18n.T("common.deselect"), size: new Vector2(btnHalf, 0), brioTheme: brioTheme))
            _curveTool.ClearSelection();
        ImGui.SameLine(0, 6f * s);
        if(UiWidgets.DangerButton("cps_del", FontAwesomeIcon.TrashAlt, I18n.T("inspector.delete_count", points.Count), I18n.T("inspector.cps_delete_tip"), new Vector2(btnHalf, 0), brioTheme))
            _curveTool.DeleteSelected();
    }

    private const string NodeMenu = "##xcpcam_node_menu";
    private readonly record struct NodeHit(Guid Id, CurveSelection? Part, Guid? CurveId);
    private NodeHit? _rightPress;
    private bool _nodeMenuOpen;
    private Func<bool>? _menuSelectionValid;

    private NodeHit? CurveHit(CurveSelection part, CurveObject? curve)
    {
        if(part.Spline < 0 || part.Point < 0) return null;
        Guid? id = curve != null
            ? curve.Splines.ElementAtOrDefault(part.Spline)?.Points.ElementAtOrDefault(part.Point)?.Id
            : _tl.ActiveTrack.Keys.OrderBy(k => k.Frame).ElementAtOrDefault(part.Point)?.Id;
        return id is Guid value ? new NodeHit(value, part, curve?.Id) : null;
    }

    private NodeHit? HoverHit()
    {
        if(_hoverNode is { } key) return new NodeHit(key.Id, null, null);
        if(_curveTool.Hover is { } part) return CurveHit(part, _curveTool.Curve);
        if(_curveTool.HoverFollow is int index && _tl.ActiveTrack.FollowCurve is { PathSpline: { } spline } curve)
            return CurveHit(new CurveSelection(curve.Splines.IndexOf(spline), index), curve);
        return null;
    }

    private void OpenNodeMenu(NodeHit hit)
    {
        if(hit.Part is { } part)
        {
            bool member = _curveTool.EditMode && _curveTool.CurveId == hit.CurveId && _curveTool.Selection.Contains(part);
            if(_curveTool.HoverFollow is int index)
            {
                if(member) _curveTool.PointTrack = _tl.ActiveTrack;
                else _curveTool.SelectFollowedPoint(_tl.ActiveTrack, index, false);
            }
            else if(!member) _curveTool.Click(part, false);
            if(hit.CurveId != null) { _tl.SelectNodes([]); Selected = null; }
            var parts = _curveTool.Selection.Select(p => CurveHit(p, _curveTool.Curve)).ToHashSet();
            var track = _tl.ActiveTrack;
            _menuSelectionValid = () => _curveTool.EditMode && track == _tl.ActiveTrack && _curveTool.CurveId == hit.CurveId
                && parts.SetEquals(_curveTool.Selection.Select(p => CurveHit(p, _curveTool.Curve)));
        }
        else if(_hoverNode is { } k)
        {
            SelectCameraNode(k, false, preserveGroup: true);
            var ids = _tl.SelectedNodes.ToHashSet();
            _menuSelectionValid = () => ids.SetEquals(_tl.SelectedNodes) && Selected?.Id == k.Id && _curveTool.Selection.Count == 0;
        }
        else return;
        ImGui.OpenPopup(NodeMenu);
    }

    private void DrawNodeMenu()
    {
        using var style = XcpStyle.PushPopup(_config.UseBrioTheme);
        _nodeMenuOpen = ImGui.BeginPopup(NodeMenu);
        if(!_nodeMenuOpen) { _menuSelectionValid = null; return; }
        if(!_clientState.IsGPosing || _menuSelectionValid?.Invoke() != true) ImGui.CloseCurrentPopup();
        else if(_curveTool.Selection.Count > 0)
        {
            int count = _curveTool.Selection.Count;
            bool points = _curveTool.Selection.All(p => p.Part == CurvePart.Point);
            string kind = points ? I18n.T("ctx.curve_points") : count == 1 ? I18n.T("ctx.curve_handle") : I18n.T("ctx.curve_selection");
            ImGui.TextDisabled(I18n.T("ctx.curve_header", kind, count));
            ImGui.Separator();
            MenuOperation(I18n.T("common.move"), GizmoEditor.Op.Translate);
            if(_curveTool.Curve != null && (points || count > 1))
            {
                MenuOperation(I18n.T("common.rotate"), GizmoEditor.Op.Rotate);
                MenuOperation(I18n.T("common.scale"), GizmoEditor.Op.Scale);
            }
            _curveTool.DrawPointContextCommands();
        }
        else if(Selected is { } k)
        {
            int count = _tl.SelectedNodes.Count;
            ImGui.TextDisabled(count == 1 ? I18n.T("ctx.camera_single", k.Frame) : I18n.T("ctx.camera_group", count));
            ImGui.Separator();
            MenuOperation(I18n.T("common.move"), GizmoEditor.Op.Translate);
            MenuOperation(I18n.T("common.rotate"), GizmoEditor.Op.Rotate, count > 1 || !RotationLocked(k));
            if(ImGui.MenuItem(I18n.T("common.local_space"), "", _gizmo.LocalSpace)) _gizmo.LocalSpace = !_gizmo.LocalSpace;
            if(ImGui.MenuItem(I18n.T("common.snap"), "", _gizmo.Snap)) _gizmo.Snap = !_gizmo.Snap;
            ImGui.Separator();
            if(count == 1)
            {
                if(ImGui.MenuItem(I18n.T("ctx.jump_to_frame", k.Frame))) SeekPlayhead(k.Frame);
                if(CanToggleCut(k) && ImGui.MenuItem(I18n.T("ctx.hard_cut"), "", k.CutIn)) ToggleNodeCut(k);
                if(ImGui.MenuItem(I18n.T("common.duplicate"))) Duplicate(k);
            }
            if(ImGui.MenuItem(count == 1 ? I18n.T("common.delete") : I18n.T("ctx.delete_selected", count))) DeleteSelectedNodes();
        }
        ImGui.EndPopup();
    }

    private void MenuOperation(string label, GizmoEditor.Op op, bool enabled = true)
    {
        if(ImGui.MenuItem(label, "", enabled && _gizmo.Enabled && _gizmo.Operation == op, enabled))
        { _gizmo.Operation = op; _gizmo.Enabled = true; }
    }

    private void PickNodeInOverlay()
    {
        // picking a node focuses the overlay window; that still counts as "the game view has focus" for Delete
        _overlayFocused = ImGui.IsWindowFocused();
        HandleShortcuts();
        _hoverNode = null; _curveTool.Hover = null; _curveTool.HoverFollow = null;
        bool rightDown = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        bool rightUp = ImGui.IsMouseReleased(ImGuiMouseButton.Right);
        bool blank = PickNodeUnderMouse(out var at, rightDown || _rightPress != null);
        bool previouslyOpen = _nodeMenuOpen;
        if(!previouslyOpen && ImGui.IsWindowHovered() && Win32.GameIsForeground())
        {
            if(rightDown) _rightPress = HoverHit();
            if(_rightPress is { } pressed)
            {
                if(ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.GetIO().MouseDragMaxDistanceSqr[1] >= MathF.Pow(ImGui.GetIO().MouseDragThreshold, 2)) _rightPress = null;
                else if(rightUp && HoverHit() == pressed) OpenNodeMenu(pressed);
            }
        }
        else _rightPress = null;
        if(rightUp) _rightPress = null;
        DrawNodeMenu();
        if(previouslyOpen || _nodeMenuOpen)
        {
            _blankPress = null;
            TrackBlankClick(false);
            return;
        }
        if(GizmoBusy) return;
        TrackBlankClick(blank);
        // act only once the overlay actually owns the mouse, so the click never also reaches the game
        bool clicked = ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if(_curveTool.Hover is { } part)
        {
            ImGui.GetBackgroundDrawList().AddCircle(at, 10, 0xFFFFFFFF, 16, 2f);
            ImGui.SetTooltip(part.Spline < 0 ? I18n.T("overlay.tooltip_object_mode")
                : part.Part == CurvePart.Point ? I18n.T("overlay.tooltip_pick_point") : I18n.T("overlay.tooltip_pick_handle"));
            if(clicked)
            {
                _curveTool.Click(part, _config.Shortcuts.Held(Shortcut.Select));
                // a user curve's points replace the camera node selection (node path points are camera nodes)
                if(_curveTool.Curve != null) { _tl.SelectNodes([]); Selected = null; }
            }
            return;
        }
        if(_curveTool.HoverFollow is int point)
        {
            ImGui.GetBackgroundDrawList().AddCircle(at, 10, 0xFFFFFFFF, 16, 2f);
            var track = _tl.ActiveTrack;
            bool anchored = track.FollowCurve?.PathSpline is { } spline && point < spline.Points.Count
                && track.FollowPath!.TimeAnchors.ContainsKey(spline.Points[point].Id);
            ImGui.SetTooltip(I18n.T("overlay.tooltip_pick_follow_point", track.FollowCurve?.Name ?? "", point + 1,
                anchored ? I18n.T("anchor.timeline") : I18n.T("anchor.camera")));
            if(clicked)
            {
                _curveTool.SelectFollowedPoint(track, point, _config.Shortcuts.Held(Shortcut.Select));
                _tl.SelectNodes([]); Selected = null;
            }
            return;
        }
        if(_hoverNode is not { } k) return;
        ImGui.GetBackgroundDrawList().AddCircle(at, 10, 0xFFFFFFFF, 16, 2f);
        ImGui.SetTooltip(I18n.T("overlay.tooltip_pick_camera"));
        if(!clicked) return;
        SelectCameraNode(k, _config.Shortcuts.Held(Shortcut.Select));
    }

    private void SelectCameraNode(Keyframe k, bool additive, bool preserveGroup = false)
    {
        _curveTool.ClearSelection();
        if(preserveGroup && _tl.SelectedNodes.Contains(k.Id)) Selected = k;
        else if(additive)
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
    private bool PickNodeUnderMouse(out Vector2 at, bool rightPicking)
    {
        at = default;
        if(!_clientState.IsGPosing || GizmoBusy && (!rightPicking || _gizmo.IsUsing)) return false;
        // another ImGui window (this panel, Brio, ...) under the mouse keeps the click
        if(ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.IsWindowHovered()) return false;
        var mouse = ImGui.GetIO().MousePos;
        float best = 12f * 12f;
        // camera nodes, the followed curve's points and the curve edit cage are picked together: nearest wins
        foreach(var (part, screen) in _curveTool.Candidates)
        {
            float d = Vector2.DistanceSquared(screen, mouse);
            if(d <= best) { best = d; _curveTool.Hover = part; at = screen; }
        }
        foreach(var (point, screen) in _curveTool.FollowCandidates)
        {
            float d = Vector2.DistanceSquared(screen, mouse);
            if(d < best) { best = d; _curveTool.Hover = null; _curveTool.HoverFollow = point; at = screen; }
        }
        foreach(var (key, screen) in _overlay.NodeScreens)
        {
            float d = Vector2.DistanceSquared(screen, mouse);
            bool replacesNodePathPoint = _curveTool.Curve == null && _curveTool.Hover?.Part == CurvePart.Point && d <= best + 0.5f;
            if(d < best || replacesNodePathPoint) { best = d; _curveTool.Hover = null; _curveTool.HoverFollow = null; _hoverNode = key; at = screen; }
        }
        return _hoverNode == null && _curveTool.Hover == null && _curveTool.HoverFollow == null;
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
                _curveTool.ClearSelection();
                _tl.SelectNodes([]); Selected = null;
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

    private Quaternion? _groupDragRotation;

    public void DrawOverlay()
    {
        if(!_clientState.IsGPosing || !ImGui.IsMouseDown(ImGuiMouseButton.Left) || _curveTool.OwnsGizmo || _tl.SelectedNodes.Count < 2)
            _groupDragRotation = null;
        float? previewFrame = _comp != null && !_lookThrough && (_playing || _followBrioPlayhead || _tl.Playhead != _tl.FirstFrame)
            ? _tl.Playhead : null;
        float? syncFrame = _comp != null && _sync.PlayheadSec is float sec ? _comp.FirstFrame + sec * _tl.Fps : null;
        _overlay.Draw(_comp, _tl, previewFrame, syncFrame, Selected);
        if(_clientState.IsGPosing) _curveTool.DrawScene();
        bool menuWasOpen = _nodeMenuOpen;
        PickNodeInOverlay();
        if(menuWasOpen || _nodeMenuOpen) return;
        if(_curveTool.OwnsGizmo)
        {
            // the last selection was curve points / handles (or a curve in object mode): the gizmo edits the curve
            if(_clientState.IsGPosing) _curveTool.DrawGizmo();
            return;
        }

        var sel = Selected;
        var group = SelectedGroup();
        if(group.Count > 1 && _clientState.IsGPosing)
        {
            var center = GroupCenter(group);
            var basis = _groupDragRotation ?? (sel ?? group[0]).Rot;
            var proxy = new Keyframe { Pos = center, Rot = basis };
            bool changed = _gizmo.Draw(proxy, Vector3.Zero, Quaternion.Identity);
            _groupDragRotation = _gizmo.IsUsing ? proxy.Rot : null;
            if(_gizmo.DragStarted) _tl.Snapshot();
            if(changed && _gizmo.Operation == GizmoEditor.Op.Rotate)
            {
                var delta = Quaternion.Normalize(proxy.Rot * Quaternion.Inverse(basis));
                foreach(var k in group)
                {
                    k.Pos = GizmoEditor.ClampRange(center + Vector3.Transform(k.Pos - center, delta));
                    bool turnCamera = !RotationLocked(k);
                    if(turnCamera) k.Rot = Quaternion.Normalize(delta * k.Rot);
                    WriteNodePose(k, true, turnCamera, false);
                }
            }
            else if(changed) MoveGroup(group, proxy.Pos - center);
            if(_gizmo.IsUsing && _gizmo.Limited)
                SetStatus(I18n.T("status.gizmo_limited", $"{GizmoEditor.MaxRange:0}"), true);
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
                SetStatus(I18n.T("status.gizmo_limited", $"{GizmoEditor.MaxRange:0}"), true);
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
        if(limited) SetStatus(I18n.T("status.group_limited", $"{GizmoEditor.MaxRange:0}"), true);
    }

    /// <summary>Deletes the multi-selection, or the primary node when nothing is multi-selected.</summary>
    private void DeleteSelectedNodes()
    {
        var ids = _tl.SelectedNodes.Count > 0 ? _tl.SelectedNodes.ToArray() : Selected != null ? new[] { Selected.Id } : [];
        if(ids.Length == 0) return;
        _tl.DeleteNodes(ids); Selected = null;
        SetStatus(I18n.T("status.deleted_nodes", ids.Length), false);
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
        if(stale > 0) warning = I18n.T("status.rec_stale_warning", stale);
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
        if(track.Keys.Count == 0) { SetStatus(I18n.T("status.rec_no_keys"), true); return; }
        if(!SourceAvailable) { SetStatus(I18n.T("status.rec_need_source", SourceName), true); return; }
        int start = track.Keys.Min(k => k.Frame), end = CamTimeline.TrackLastFrame(track);
        _tl.Snapshot();
        foreach(var c in constraints) { c.Recorded.Clear(); c.RecordedFor = c.TargetKey; }
        _recording = new Recording { Constraints = constraints.ToList(), Start = start, End = end };
        try
        {
            _tl.Playhead = start;
            SetSourcePlaying(true);
            SetStatus(I18n.T("status.rec_started", start, end), false);
        }
        catch(Exception ex)
        {
            _recording = null;
            SetStatus(I18n.T("status.rec_start_failed", ex.Message), true);
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
        SetStatus(complete ? I18n.T("status.rec_complete", r.Constraints.Count, covered, r.Start, r.End)
            : I18n.T("status.rec_incomplete", r.Start, r.End, covered), !complete);
    }

    private void RebuildPathIfNeeded()
    {
        int hash = ComputeHash();
        // also rebuild whenever the composer was built for a different number of tracks
        bool structureChanged = _comp != null && _comp.TrackCount != _tl.Tracks.Count;
        if(hash == _pathHash && !structureChanged) return;
        _tl.RefreshAutoCuts();
        _pathHash = ComputeHash();
        try
        {
            _tl.SyncSeconds();
            _comp = ShotComposer.Create(_tl, _settings, _bones);
            if(_sync.Bound && ExportComposer(out _) is { } export)
            {
                _sync.StartFrame = export.FirstFrame;
                _sync.Refresh(export);
            }
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
        _lastDrawnFrame = ImGui.GetFrameCount();
        bool gpose = _clientState.IsGPosing;
        bool brio = _config.UseBrioTheme;
        float s = XcpStyle.Scale;

        HandleShortcuts(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows));
        if(_seenSelection != _tl.SelectionRevision)
        {
            _seenSelection = _tl.SelectionRevision;
            if(_tl.SelectedNodes.Count == 0) Selected = null;
        }

        // ---- Unified Brio/Blender Header Bar ----
        DrawHeaderBar(gpose, brio);

        if(!gpose)
        {
            UiWidgets.Icon(FontAwesomeIcon.ExclamationTriangle, XcpStyle.Warning);
            ImGui.SameLine(0, 5f * s);
            ImGui.TextColored(XcpStyle.Warning, I18n.T("topbar.not_gpose_warn"));
        }

        // ---- Main Split Body: Left Workspace + Right Collapsible N-Panel ----
        float footerH = ImGui.GetFrameHeight() + 8f * s;
        float bodyH = MathF.Max(200f * s, ImGui.GetContentRegionAvail().Y - footerH);
        float availW = ImGui.GetContentRegionAvail().X;

        if(_config.SidebarOpen)
        {
            float sideW = Math.Clamp(_config.SidebarWidth * s, 240f * s, MathF.Max(250f * s, availW * 0.45f));
            float splitW = 6f * s;
            float leftW = MathF.Max(320f * s, availW - sideW - splitW - 4f * s);

            if(ImGui.BeginChild("##main_left", new Vector2(leftW, bodyH), false))
                DrawActiveWorkspace(gpose, brio);
            ImGui.EndChild();

            ImGui.SameLine(0, 2f * s);
            float unscaled = _config.SidebarWidth;
            if(UiWidgets.VerticalSplitter("##npanel_split", bodyH, ref unscaled, 240f, MathF.Max(260f, availW / s * 0.45f), brio))
            {
                _config.SidebarWidth = unscaled;
                _sidebarResizing = true;
            }
            if(_sidebarResizing && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _sidebarResizing = false;
                SaveConfig();
            }

            ImGui.SameLine(0, 2f * s);
            if(ImGui.BeginChild("##main_sidebar", new Vector2(0, bodyH), false))
                DrawSidebar(gpose, brio);
            ImGui.EndChild();
        }
        else
        {
            if(ImGui.BeginChild("##main_left", new Vector2(0, bodyH), false))
                DrawActiveWorkspace(gpose, brio);
            ImGui.EndChild();
        }

        // ---- Compact Bottom Status Strip ----
        DrawStatusBar(gpose, brio);

        // ---- Standalone Import Preview Modal ----
        DrawImportModal(brio);
    }

    private void DrawHeaderBar(bool gpose, bool brio)
    {
        float s = XcpStyle.Scale;
        float h = ImGui.GetFrameHeight();
        float iconW = MathF.Max(h, 24f * s);
        var sq = new Vector2(iconW, h);

        // Left: File dropdown + Undo / Redo
        if(UiWidgets.IconTextButton("hdr_file", FontAwesomeIcon.FolderOpen, I18n.T("topbar.file"),
            I18n.T("topbar.file_tip"), brioTheme: brio, dropdownCaret: true))
            ImGui.OpenPopup("##xcp_file_menu");

        if(ImGui.BeginPopup("##xcp_file_menu"))
        {
            DrawFileMenuItems();
            ImGui.EndPopup();
        }

        ImGui.SameLine(0, 4f * s);
        ImGui.BeginDisabled(!_tl.CanUndo);
        if(UiWidgets.IconButton("hdr_undo", FontAwesomeIcon.Reply, I18n.T("common.undo"), sq, subtle: true, brioTheme: brio))
            RestoreHistory(redo: false);
        ImGui.EndDisabled();

        ImGui.SameLine(0, 2f * s);
        ImGui.BeginDisabled(!_tl.CanRedo);
        if(UiWidgets.IconButton("hdr_redo", FontAwesomeIcon.Share, I18n.T("common.redo"), sq, subtle: true, brioTheme: brio))
            RestoreHistory(redo: true);
        ImGui.EndDisabled();

        // Divider + Primary Workspace Tabs (Paths / Constraints / Brio Sync open as standalone floating windows)
        ImGui.SameLine(0, 8f * s);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("|");

        void PrimaryTab(MainTab tab, string label, string tip)
        {
            ImGui.SameLine(0, 3f * s);
            bool active = _activeTab == tab;
            if(UiWidgets.TogglePill($"tab_{tab}", label, active, tip, brioTheme: brio))
                _activeTab = tab;
        }
        PrimaryTab(MainTab.Timeline, I18n.T("topbar.tab_timeline"), I18n.T("topbar.tab_timeline_tip"));
        PrimaryTab(MainTab.Graph, I18n.T("topbar.tab_graph"), I18n.T("topbar.tab_graph_tip"));

        // Right cluster: Live Preview + Mini Timeline + Viewport Toolbar + Settings + Sidebar toggle
        string previewLabel = I18n.T("topbar.live_preview");
        float previewW = ImGui.CalcTextSize(previewLabel).X + 34f * s;
        float rightW = previewW + (iconW + 3f * s) * 4f + 4f * s;

        ImGui.SameLine(0, 8f * s);
        float availRight = ImGui.GetContentRegionAvail().X;
        if(availRight > rightW) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availRight - rightW);

        ImGui.BeginDisabled(!CanLookThrough);
        if(UiWidgets.TogglePill("hdr_prev", previewLabel, _lookThrough, LookThroughTooltip,
            FontAwesomeIcon.Video, new Vector2(previewW, h), XcpStyle.PreviewAmber, brio))
            SetLookThrough(!_lookThrough);
        ImGui.EndDisabled();

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("hdr_mini", FontAwesomeIcon.Stream,
            _config.ShowMiniTimeline
                ? (_config.HideMiniWhenMainExpanded ? I18n.T("topbar.mini_on_auto_tip") : I18n.T("topbar.mini_on_tip"))
                : I18n.T("topbar.mini_off_tip"),
            sq, active: _config.ShowMiniTimeline, subtle: !_config.ShowMiniTimeline, brioTheme: brio))
        {
            _config.ShowMiniTimeline = !_config.ShowMiniTimeline;
            SaveConfig();
        }

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("hdr_tb", FontAwesomeIcon.Toolbox,
            _config.ShowViewportToolbar ? I18n.T("topbar.toolbar_hide_tip") : I18n.T("topbar.toolbar_show_tip"),
            sq, active: _config.ShowViewportToolbar, subtle: !_config.ShowViewportToolbar, brioTheme: brio))
            OnToggleViewportToolbar?.Invoke();

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("hdr_cfg", FontAwesomeIcon.Cog, I18n.T("topbar.settings_tip"),
            sq, subtle: true, brioTheme: brio))
            OnOpenSettings?.Invoke();

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("hdr_side", FontAwesomeIcon.Columns,
            _config.SidebarOpen ? I18n.T("topbar.sidebar_collapse_tip") : I18n.T("topbar.sidebar_expand_tip"),
            sq, active: _config.SidebarOpen, subtle: !_config.SidebarOpen, brioTheme: brio))
        {
            _config.SidebarOpen = !_config.SidebarOpen;
            SaveConfig();
        }

        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawActiveWorkspace(bool gpose, bool brio)
    {
        if(_activeTab == MainTab.Timeline) DrawTimelineTab(gpose, brio);
        else DrawGraphTab(brio);
    }

    // ---------------- Right Collapsible N-Panel ----------------

    private void DrawSidebar(bool gpose, bool brio)
    {
        DrawViewportGizmoCard(brio);
        ImGui.Spacing();
        DrawInspectorCard(gpose, brio);
        ImGui.Spacing();
        using(UiWidgets.Card("np_paths", brio))
            _curveTool.DrawSidebarSummary(gpose, brio, IsPathWindowOpen?.Invoke() == true, () => SetPathWindowOpen?.Invoke(null));
        ImGui.Spacing();
        using(UiWidgets.Card("np_constraints", brio))
            _constraints.DrawSummary(_tl.ActiveTrack, brio, IsConstraintsWindowOpen?.Invoke() == true, open => SetConstraintsWindowOpen?.Invoke(open));
        ImGui.Spacing();
        DrawBrioSidebarCard(brio);
    }

    private void DrawBrioSidebarCard(bool brio)
    {
        using var card = UiWidgets.Card("np_brio", brio);
        bool winOpen = IsBrioWindowOpen?.Invoke() == true;
        if(UiWidgets.SectionHeaderAction(I18n.T("brio.sidebar_title"), FontAwesomeIcon.ExternalLinkAlt, "goto_brio",
            winOpen ? I18n.T("brio.sidebar_close_tip") : I18n.T("brio.sidebar_open_tip"),
            winOpen, brio, FontAwesomeIcon.Link))
            SetBrioWindowOpen?.Invoke(null);

        DrawBrioSyncContent(brio, compact: true);
        if(_sync.Bound)
        {
            ImGui.Spacing();
            UiWidgets.StatusDot(XcpStyle.Info, _sync.Status);
        }
    }

    private void DrawViewportGizmoCard(bool brio)
    {
        float s = XcpStyle.Scale;
        using var card = UiWidgets.Card("np_gizmo", brio);

        if(UiWidgets.SectionHeaderAction(I18n.T("sidebar.gizmo_title"),
            _gizmo.LocalSpace ? FontAwesomeIcon.Cube : FontAwesomeIcon.Globe,
            "np_space",
            _gizmo.LocalSpace ? I18n.T("toolbar.space_local_tip") : I18n.T("toolbar.space_world_tip"),
            _gizmo.LocalSpace, brio, FontAwesomeIcon.Crosshairs, _gizmo.LocalSpace ? I18n.T("common.local") : I18n.T("common.world")))
            _gizmo.LocalSpace = !_gizmo.LocalSpace;

        bool curveTarget = _curveTool.OwnsGizmo;
        bool rotateLocked = GizmoRotateLocked;
        if(!curveTarget && _gizmo.Operation == GizmoEditor.Op.Scale)
            _gizmo.Operation = GizmoEditor.Op.Translate;

        float gap = 3f * s;
        float btnW = MathF.Max(28f * s, (UiWidgets.InnerAvailWidth - gap * 4f) / 5f);
        var btnSz = new Vector2(btnW, 26f * s);

        if(UiWidgets.IconButton("np_ov", _overlay.Enabled ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
            _overlay.Enabled ? I18n.T("sidebar.gizmo_overlay_on") : I18n.T("sidebar.gizmo_overlay_off"), btnSz, active: _overlay.Enabled, brioTheme: brio))
        { _overlay.Enabled = !_overlay.Enabled; SaveConfig(); }

        ImGui.SameLine(0, gap);
        if(UiWidgets.IconButton("np_giz", FontAwesomeIcon.Crosshairs,
            _gizmo.Enabled ? I18n.T("sidebar.gizmo_on") : I18n.T("sidebar.gizmo_off"), btnSz, active: _gizmo.Enabled, brioTheme: brio))
            _gizmo.Enabled = !_gizmo.Enabled;

        ImGui.BeginDisabled(!_gizmo.Enabled);

        ImGui.SameLine(0, gap);
        if(UiWidgets.IconButton("np_tr", FontAwesomeIcon.ArrowsUpDownLeftRight, I18n.T("toolbar.op_translate"),
            btnSz, active: _gizmo.Enabled && _gizmo.Operation == GizmoEditor.Op.Translate, brioTheme: brio))
            _gizmo.Operation = GizmoEditor.Op.Translate;

        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(rotateLocked);
        if(UiWidgets.IconButton("np_rot", FontAwesomeIcon.ArrowsSpin,
            rotateLocked ? I18n.T("sidebar.gizmo_rotate_locked") : I18n.T("toolbar.op_rotate"),
            btnSz, active: _gizmo.Enabled && _gizmo.Operation == GizmoEditor.Op.Rotate && !rotateLocked, brioTheme: brio))
            _gizmo.Operation = GizmoEditor.Op.Rotate;
        ImGui.EndDisabled();

        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(!curveTarget);
        if(UiWidgets.IconButton("np_sc", FontAwesomeIcon.ExpandAlt,
            curveTarget ? I18n.T("toolbar.op_scale") : I18n.T("toolbar.op_scale_curve_only"),
            btnSz, active: _gizmo.Enabled && curveTarget && _gizmo.Operation == GizmoEditor.Op.Scale, brioTheme: brio))
            _gizmo.Operation = GizmoEditor.Op.Scale;
        ImGui.EndDisabled();

        ImGui.Spacing();

        if(UiWidgets.TogglePill("np_snap", I18n.T("common.snap"), _gizmo.Snap, I18n.T("sidebar.gizmo_snap_tip"), FontAwesomeIcon.Magnet, brioTheme: brio))
            _gizmo.Snap = !_gizmo.Snap;

        if(_gizmo.Snap)
        {
            ImGui.SameLine(0, 4f * s);
            ImGui.SetNextItemWidth(76f * s);
            if(_gizmo.Operation == GizmoEditor.Op.Translate)
                ImGui.DragFloat("##snapT", ref _gizmo.SnapTranslate, 0.01f, 0.01f, 5f, "%.2f m");
            else
                ImGui.DragFloat("##snapR", ref _gizmo.SnapRotateDeg, 0.5f, 0.5f, 90f, "%.1f°");
        }
        ImGui.EndDisabled();

        ImGui.SameLine(0, 6f * s);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(curveTarget ? I18n.T("sidebar.gizmo_target_curve") : I18n.T("sidebar.gizmo_target_camera"));
        UiWidgets.Tooltip(I18n.T("sidebar.gizmo_target_tip"));
    }

    private void DrawInspectorCard(bool gpose, bool brio)
    {
        using var card = UiWidgets.Card("np_inspector", brio);
        DrawInspector(gpose, brio);
    }

    private void DrawStatusBar(bool gpose, bool brio)
    {
        float s = XcpStyle.Scale;
        ImGui.Separator();

        // Left: Brio connection & source pill
        var dotCol = _brio.IsConnected ? (SourceAvailable ? XcpStyle.Success : XcpStyle.Warning) : XcpStyle.Warning;
        string connText = !_brio.IsConnected ? I18n.T("status.brio_disconnected")
            : _source == BindSource.Vivacity ? I18n.T("status.brio_vivacity", _tl.Fps)
            : I18n.T("status.brio_anim", _tl.Fps);
        UiWidgets.StatusDot(dotCol, connText, I18n.T("status.brio_pill_tip"));

        // Middle: Status message or live preview indicator
        ImGui.SameLine(0, 12f * s);
        ImGui.TextDisabled("|");
        ImGui.SameLine(0, 8f * s);

        if(!string.IsNullOrEmpty(_status))
        {
            ImGui.TextColored(_statusIsError ? XcpStyle.Danger : XcpStyle.Success, _status);
        }
        else if(_lookThrough)
        {
            ImGui.TextColored(XcpStyle.PreviewAmber, I18n.T("status.live_preview_active"));
        }
        else
        {
            ImGui.TextDisabled(_tl.AllKeys.Any()
                ? I18n.T("status.summary", _tl.Tracks.Count, _tl.AllKeys.Count(), _tl.Length)
                : I18n.T("status.ready_hint"));
        }

        // Right: missing GPose target warning chip
        if(gpose && _tl.AllKeys.Any() && !CameraCapture.TryGetBase(_targets, out _, out _, out _, out _))
        {
            string warnText = I18n.T("status.no_gpose_target");
            float warnW = ImGui.CalcTextSize(warnText).X + 26f * s;
            ImGui.SameLine();
            float avail = ImGui.GetContentRegionAvail().X;
            if(avail > warnW) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - warnW);
            UiWidgets.Chip("no_target_warn", warnText, XcpStyle.Warning,
                I18n.T("status.no_gpose_target_tip"));
        }
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
        if(_tl.TrackOf(node)?.Shots.FirstOrDefault(s => s.SeededNodes.Contains(node.Id)) is not { } shot) return;
        var pose = shot.NodePose(node);
        node.Pos = pose.Pos; node.Rot = pose.Rot; node.Fov = pose.Fov;
    }

    private void WriteNodePose(Keyframe node, bool position = true, bool rotation = true, bool fov = true)
    {
        if(ShotFor(node) is not { } shot) return;
        try
        {
            shot.WritePose(node, new XcpFrame(node.Pos, node.Rot, node.Fov), position, rotation, fov, _tl.PathInfluence);
            if(shot.RequestedFrame is int requested) MoveNodeToFrame(node, requested);
            _tl.Touch();
        }
        catch(InvalidOperationException ex) { RefreshNodePose(node); SetStatus(ex.Message, true); }
    }

    /// <summary>Moves a node towards <paramref name="frame"/>, staying strictly between its neighbours and on a free frame.</summary>
    private void MoveNodeToFrame(Keyframe node, int frame)
    {
        if(_tl.TrackOf(node) is not { } owner || frame == node.Frame) return;
        int previous = owner.Keys.Where(n => n.Frame < node.Frame).Select(n => n.Frame).DefaultIfEmpty(-1).Max();
        int next = owner.Keys.Where(n => n.Frame > node.Frame).Select(n => n.Frame).DefaultIfEmpty(_tl.Length + 1).Min();
        if(next - previous < 2) return;
        frame = Math.Clamp(frame, previous + 1, next - 1);
        frame = _tl.NearestFreeFrame(frame, node, owner);
        if(frame > previous && frame < next) _tl.MoveKey(node, frame);
        RefreshNodePose(node);
    }

    private void DrawGraphTab(bool brio)
    {
        float s = XcpStyle.Scale;
        var selected = Selected;
        if(selected != null && selected.Id != _graphNode)
        {
            _graphNode = selected.Id;
            var owner = _tl.TrackOf(selected);
            if(owner != null)
            {
                _tl.ActiveTrackIndex = _tl.Tracks.IndexOf(owner);
                _graphShot = owner.Shots.FindIndex(shot => shot.SeededNodes.Contains(selected.Id));
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(I18n.T("common.track"));
        ImGui.SameLine(0, 5f * s);
        ImGui.SetNextItemWidth(140f * s);
        int trackIndex = _tl.ActiveTrackIndex;
        var trackNames = _tl.Tracks.Select(t => t.Name).ToArray();
        if(ImGui.Combo("##g_track", ref trackIndex, trackNames, trackNames.Length))
        { _tl.ActiveTrackIndex = trackIndex; _graphShot = 0; Selected = null; }

        var track = _tl.ActiveTrack;
        if(track.Shots.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(I18n.T("graph.no_keys_hint"));
            return;
        }
        _graphShot = Math.Clamp(_graphShot, 0, track.Shots.Count - 1);

        ImGui.SameLine(0, 8f * s);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(I18n.T("common.shot"));
        ImGui.SameLine(0, 5f * s);
        ImGui.SetNextItemWidth(200f * s);
        var names = track.Shots.Select((sh, idx) => I18n.T("core.shot_name", idx + 1, sh.Start, sh.End)).ToArray();
        ImGui.Combo("##g_shot", ref _graphShot, names, names.Length);

        ImGui.Spacing();
        var shot = track.Shots[_graphShot];
        _graph.Draw(shot, _tl, _config.Shortcuts, brio);
        if(_graph.PlayheadChanged) SeekPlayhead(_tl.Playhead);
    }

    // ---------------- timeline tab ----------------

    private void DrawTimelineTab(bool gpose, bool brio)
    {
        float s = XcpStyle.Scale;
        float h = ImGui.GetFrameHeight();
        float iconW = MathF.Max(h, 24f * s);
        var sq = new Vector2(iconW, h);

        // ---- Row 1: Capture actions + Brio-style Transport ----
        ImGui.BeginDisabled(!gpose);
        if(UiWidgets.IconTextButton("tl_ins", FontAwesomeIcon.Plus, I18n.T("timeline.btn_insert"),
            I18n.T("timeline.btn_insert_tip"), active: gpose, brioTheme: brio))
            CaptureAtPlayhead();
        ImGui.SameLine(0, 4f * s);
        if(UiWidgets.IconTextButton("tl_app", FontAwesomeIcon.PlusCircle, I18n.T("timeline.btn_append"),
            I18n.T("timeline.btn_append_tip", _defaultGapFrames), brioTheme: brio))
            CaptureAppend();
        ImGui.EndDisabled();

        ImGui.SameLine(0, 4f * s);
        ImGui.BeginDisabled(!gpose || _tl.Keys.Count == 0);
        if(UiWidgets.IconTextButton("tl_cut", FontAwesomeIcon.Cut, I18n.T("timeline.btn_cut"),
            I18n.T("timeline.btn_cut_tip"), brioTheme: brio))
            CutAtPlayhead();
        ImGui.EndDisabled();

        ImGui.SameLine(0, 4f * s);
        var activeSeq = _comp?.SequenceOf(_tl.ActiveTrackIndex);
        bool inPath = activeSeq != null && activeSeq.Segments.Any(seg => seg.Path != null && _tl.Playhead >= seg.Start && _tl.Playhead <= seg.End);
        ImGui.BeginDisabled(!inPath);
        if(UiWidgets.IconTextButton("tl_sample", FontAwesomeIcon.Magic, I18n.T("timeline.btn_sample"),
            inPath ? I18n.T("timeline.btn_sample_tip")
                : I18n.T("timeline.btn_sample_disabled_tip"), brioTheme: brio))
            InsertKeyFromPath();
        ImGui.EndDisabled();

        // Transport cluster (aligned to Brio TimelineWindow)
        ImGui.SameLine(0, 12f * s);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("|");
        ImGui.SameLine(0, 8f * s);

        if(UiWidgets.IconButton("tr_first", FontAwesomeIcon.FastBackward, I18n.T("transport.first_tip"), sq, subtle: true, brioTheme: brio))
            SeekPlayhead(0);
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("tr_prev", FontAwesomeIcon.StepBackward, I18n.T("transport.prev_tip"), sq, subtle: true, brioTheme: brio))
            StepPlayhead(-1);
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("tr_play", TransportPlaying ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
            TransportPlayTooltip,
            sq, active: TransportPlaying, subtle: !TransportPlaying, brioTheme: brio))
            TogglePlay();
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("tr_stop", FontAwesomeIcon.Stop, I18n.T("transport.stop_tip"), sq, subtle: true, brioTheme: brio))
            StopTransport();
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("tr_next", FontAwesomeIcon.StepForward, I18n.T("transport.next_tip"), sq, subtle: true, brioTheme: brio))
            StepPlayhead(1);
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("tr_last", FontAwesomeIcon.FastForward, I18n.T("transport.last_tip", _tl.Length), sq, subtle: true, brioTheme: brio))
            SeekPlayhead(_tl.Length);
        ImGui.SameLine(0, 4f * s);
        bool loop = CurrentLoop;
        if(UiWidgets.IconButton("tr_loop", FontAwesomeIcon.Retweet, loop ? I18n.T("transport.loop_on_tip") : I18n.T("transport.loop_off_tip"),
            sq, active: loop, subtle: !loop, brioTheme: brio))
            Try(() => SetLoop(!loop));

        // ---- Row 2: Frame / Length / FPS + Fit & Options Popup ----
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(I18n.T("common.frame"));
        ImGui.SameLine(0, 4f * s);
        ImGui.SetNextItemWidth(62f * s);
        int ph = (int)MathF.Round(_tl.Playhead);
        if(ImGui.InputInt("##tl_ph", ref ph, 0)) SeekPlayhead(ph);

        ImGui.SameLine(0, 4f * s);
        ImGui.TextDisabled("/");
        ImGui.SameLine(0, 4f * s);
        ImGui.SetNextItemWidth(66f * s);
        int len = _tl.Length;
        ImGui.BeginDisabled(LengthFromAnimation);
        if(ImGui.InputInt("##tl_len", ref len, 0)) SetTimelineLength(Math.Max(len, Math.Max(1, _tl.LastFrame)));
        ImGui.EndDisabled();
        UiWidgets.Tooltip(LengthFromAnimation ? I18n.T("timeline.length_anim_tip")
            : LengthFromVivacity ? I18n.T("timeline.length_vivacity_tip")
            : I18n.T("timeline.length_manual_tip"));

        ImGui.SameLine(0, 6f * s);
        ImGui.TextDisabled($"({_tl.Playhead / _tl.Fps:0.00}s)");

        ImGui.SameLine(0, 10f * s);
        if(_source == BindSource.Vivacity && _syncFpsWithBrio && _brio.IsConnected)
            UiWidgets.Chip("tl_fps_chip", $"{_tl.Fps} fps · Brio", XcpStyle.Info, I18n.T("timeline.fps_brio_tip"));
        else
        {
            ImGui.SetNextItemWidth(54f * s);
            int fps = _tl.Fps;
            if(ImGui.InputInt("fps##tl_fps", ref fps, 0)) Try(() => _tl.SetFps(fps, snapshot: true));
        }

        // Right-aligned Fit + Options menu
        string fitLbl = I18n.T("common.fit_all");
        float fitW = MathF.Max(78f * s, ImGui.CalcTextSize(fitLbl).X + 32f * s);
        float rightTools = fitW + iconW + 4f * s;
        ImGui.SameLine();
        float availR = ImGui.GetContentRegionAvail().X;
        if(availR > rightTools) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availR - rightTools);

        float timelineAreaW = ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX();
        if(UiWidgets.IconTextButton("tl_fit", FontAwesomeIcon.Expand, fitLbl, I18n.T("timeline.fit_all_tip"),
            size: new Vector2(fitW, h), brioTheme: brio))
            _tlw.FitAll(_tl, MathF.Max(300f * s, timelineAreaW));

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("tl_more", FontAwesomeIcon.EllipsisH, I18n.T("timeline.more_tip"),
            sq, subtle: true, brioTheme: brio))
            ImGui.OpenPopup("##tl_more_opts");

        if(ImGui.BeginPopup("##tl_more_opts"))
        {
            ImGui.TextDisabled(I18n.T("timeline.more_title"));
            ImGui.Separator();
            ImGui.SetNextItemWidth(90f * s);
            ImGui.InputInt($"{I18n.T("timeline.append_gap")}##tl_gap", ref _defaultGapFrames, 5);
            _defaultGapFrames = Math.Clamp(_defaultGapFrames, 1, 10000);
            UiWidgets.Tooltip(I18n.T("timeline.append_gap_tip"));

            ImGui.SetNextItemWidth(90f * s);
            int influence = _tl.PathInfluence;
            if(ImGui.InputInt($"{I18n.T("timeline.tangent_range")}##tl_inf", ref influence)) _tl.PathInfluence = Math.Clamp(influence, 0, 50);
            UiWidgets.Tooltip(I18n.T("timeline.tangent_range_tip"));

            ImGui.Separator();
            ImGui.TextDisabled(I18n.T("timeline.usage_hint"));
            ImGui.EndPopup();
        }

        // ---- Full-height Timeline Canvas ----
        ImGui.Spacing();
        float availH = ImGui.GetContentRegionAvail().Y - 2f * s;
        _tlw.Draw(_tl, _comp, _config.Shortcuts, _overlay.SpeedColors, availH, brio);
        if(_tlw.TracksEdited)
        {
            if(Selected == null) _tlw.Selected = null;
            RebuildPathIfNeeded();
        }
        if(_tlw.PlayheadChanged) SeekPlayhead(_tl.Playhead);
    }

    private void StepPlayhead(int dir)
    {
        _playing = false;
        var frames = _tl.Keys.Select(k => k.Frame).Concat(_comp?.CutFrames ?? []).Distinct().OrderBy(f => f).ToList();
        int cur = (int)MathF.Round(_tl.Playhead);
        int? next = dir > 0 ? frames.Where(f => f > cur).Cast<int?>().FirstOrDefault() : frames.Where(f => f < cur).Cast<int?>().LastOrDefault();
        SeekPlayhead(next ?? Math.Clamp(cur + dir, 0, _tl.Length));
        var k = _tl.Keys.FirstOrDefault(x => x.Frame == (int)_tl.Playhead);
        if(k != null) Selected = k;
    }

    /// <summary>Multi-selection inspector inside the right N-Panel.</summary>
    private void DrawGroupInspector(System.Collections.Generic.IReadOnlyList<Keyframe> group, bool brio)
    {
        float s = XcpStyle.Scale;
        UiWidgets.SectionHeader(I18n.T("inspector.group_title", group.Count), brio, FontAwesomeIcon.ObjectGroup);
        ImGui.TextDisabled(I18n.T("inspector.time_range", group.Min(k => k.Frame), group.Max(k => k.Frame)));

        ImGui.Spacing();
        var center = GroupCenter(group);
        var pos = center;
        string ctrLbl = I18n.T("inspector.group_center");
        ImGui.SetNextItemWidth(MathF.Max(100f * s, UiWidgets.InnerAvailWidth - ImGui.CalcTextSize(ctrLbl).X - 14f * s));
        bool changed = ImGui.DragFloat3($"{ctrLbl}##grp_pos", ref pos, 0.01f);
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(changed) MoveGroup(group, pos - center);
        UiWidgets.Tooltip(I18n.T("inspector.group_center_tip"));

        ImGui.Spacing();
        float halfW = MathF.Max(60f * s, (UiWidgets.InnerAvailWidth - 6f * s) * 0.5f);
        if(UiWidgets.IconTextButton("grp_clr", FontAwesomeIcon.Times, I18n.T("common.deselect"), size: new Vector2(halfW, 0), brioTheme: brio))
        { _tl.SelectNodes([]); Selected = null; }
        ImGui.SameLine(0, 6f * s);
        if(UiWidgets.DangerButton("grp_del", FontAwesomeIcon.TrashAlt, I18n.T("inspector.delete_count", group.Count), I18n.T("inspector.group_delete_tip"), new Vector2(halfW, 0), brio))
            DeleteSelectedNodes();
    }

    private void DrawInspector(bool gpose, bool brio)
    {
        float s = XcpStyle.Scale;
        if(_curveTool.OwnsGizmo && _curveTool.Curve is { } curve && _curveTool.SelectedPoints() is { Count: > 0 } points)
        {
            DrawCurvePointInspector(curve, points, brio);
            return;
        }
        var k = Selected;
        if(k == null)
        {
            UiWidgets.SectionHeader(I18n.T("inspector.title"), brio, FontAwesomeIcon.SlidersH);
            ImGui.TextDisabled(!_tl.AllKeys.Any()
                ? I18n.T("inspector.empty_no_nodes")
                : I18n.T("inspector.empty_hint"));
            return;
        }
        var group = SelectedGroup();
        if(group.Count > 1) { DrawGroupInspector(group, brio); return; }

        var owner = _tl.TrackOf(k) ?? _tl.ActiveTrack;
        int idx = owner.Keys.IndexOf(k) + 1;
        string title = _tl.Tracks.Count > 1 ? I18n.T("inspector.node_title_track", idx, owner.Name) : I18n.T("inspector.node_title", idx);
        bool ok = k.SelfCheckError < 1e-3f;
        UiWidgets.SectionHeader(title, brio, FontAwesomeIcon.Camera,
            I18n.T("inspector.node_meta_tip",
                ok ? I18n.T("inspector.self_check_pass") : I18n.T("inspector.self_check_err", $"{k.SelfCheckError:0.0e0}"),
                string.IsNullOrEmpty(k.TargetName) ? I18n.T("common.none") : k.TargetName));

        // ---- Timing row: Frame + Hold + Cut ----
        string frameShort = I18n.T("common.frame");
        string holdShort = I18n.T("inspector.hold_short");
        string cutLabel = I18n.T("ctx.hard_cut");
        float cutW = MathF.Max(60f * s, ImGui.CalcTextSize(cutLabel).X + 28f * s);
        float labelsW = ImGui.CalcTextSize(frameShort).X + ImGui.CalcTextSize(holdShort).X + 20f * s;
        float numW = MathF.Max(42f * s, (UiWidgets.InnerAvailWidth - cutW - labelsW) * 0.5f);

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(frameShort);
        ImGui.SameLine(0, 3f * s);
        ImGui.SetNextItemWidth(numW);
        int frame = k.Frame;
        if(ImGui.InputInt("##node_f", ref frame, 0))
        {
            _tl.Snapshot();
            int got = _tl.MoveKey(k, frame);
            if(got != frame) SetStatus(I18n.T("status.node_move_blocked"), true);
        }

        ImGui.SameLine(0, 6f * s);
        ImGui.TextDisabled(holdShort);
        ImGui.SameLine(0, 3f * s);
        ImGui.SetNextItemWidth(numW);
        int hold = k.HoldFrames;
        if(ImGui.InputInt("##node_h", ref hold, 0))
        {
            hold = Math.Clamp(hold, 0, 100000);
            _tl.Snapshot(); k.HoldFrames = hold; _tl.Touch();
        }
        UiWidgets.Tooltip(I18n.T("inspector.hold_tip"));

        ImGui.SameLine(0, 6f * s);
        bool isFirst = !CanToggleCut(k);
        ImGui.BeginDisabled(isFirst);
        if(UiWidgets.TogglePill("node_cut", cutLabel, k.CutIn,
            isFirst ? I18n.T("inspector.cut_first_tip") : I18n.T("inspector.cut_tip"),
            FontAwesomeIcon.Cut, new Vector2(cutW, ImGui.GetFrameHeight()), XcpStyle.PreviewAmber, brio))
            ToggleNodeCut(k);
        ImGui.EndDisabled();

        ImGui.Spacing();
        RefreshNodePose(k);

        // ---- Transform fields ----
        string pfLbl = I18n.T("inspector.path_factor");
        string posLbl = I18n.T("inspector.pos_m");
        string rotLbl = I18n.T("inspector.rot_deg");
        string fovLbl = I18n.T("inspector.fov_deg");
        float maxFieldLblW = MathF.Max(MathF.Max(ImGui.CalcTextSize(pfLbl).X, ImGui.CalcTextSize(posLbl).X),
            MathF.Max(ImGui.CalcTextSize(rotLbl).X, ImGui.CalcTextSize(fovLbl).X));
        float fieldW = MathF.Max(105f * s, UiWidgets.InnerAvailWidth - maxFieldLblW - 14f * s);

        var followShot = ShotFor(k);
        if(followShot?.NodeFactor(k) is double factor)
        {
            float percent = (float)(factor * 100);
            ImGui.SetNextItemWidth(fieldW);
            bool factorChanged = ImGui.DragFloat($"{pfLbl}##nf", ref percent, 0.1f, 0f, 100f, "%.2f%%");
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            if(factorChanged)
            {
                if(followShot.UsesTimeAnchors)
                {
                    if(followShot.SetAnchoredNodeFactor(k, percent / 100.0) is double target) { MoveNodeToFrame(k, (int)Math.Round(target)); _tl.Touch(); }
                }
                else if(followShot.SetNodeFactor(k, percent / 100.0)) { RefreshNodePose(k); _tl.Touch(); }
            }
            UiWidgets.Tooltip(followShot.UsesTimeAnchors
                ? I18n.T("inspector.path_factor_anchored_tip")
                : I18n.T("inspector.path_factor_tip"));
        }

        var pos = k.Pos;
        bool positionChanged = false;
        if(followShot?.IsFollowing != true)
        {
            ImGui.SetNextItemWidth(fieldW);
            positionChanged = ImGui.DragFloat3($"{posLbl}##np", ref pos, 0.01f);
            if(ImGui.IsItemActivated()) _tl.Snapshot();
        }
        if(positionChanged)
        {
            var limited = GizmoEditor.ClampRange(pos);
            if(limited != pos) SetStatus(I18n.T("status.pos_limited", $"{GizmoEditor.MaxRange:0}"), true);
            k.Pos = limited; WriteNodePose(k, true, false, false);
        }

        var nodeShot = ShotFor(k);
        bool rotationLocked = RotationLocked(k), fovLocked = FovLocked(k);
        var driven = rotationLocked || fovLocked ? ConstrainedNodePose(k) : default;
        var euler = rotationLocked ? QuatToEulerDeg(driven.Rot) : nodeShot?.NodeEuler(k) ?? QuatToEulerDeg(k.Rot);
        ImGui.SetNextItemWidth(fieldW);
        ImGui.BeginDisabled(rotationLocked);
        bool rotationChanged = ImGui.DragFloat3($"{rotLbl}##nr", ref euler, 0.25f) && !rotationLocked;
        ImGui.EndDisabled();
        if(rotationLocked) UiWidgets.Tooltip(I18n.T("inspector.rot_locked_tip"));
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(rotationChanged)
        {
            if(nodeShot != null) Try(() => { nodeShot.WriteNodeEuler(k, euler, _tl.PathInfluence); _tl.Touch(); });
            else { k.Rot = EulerDegToQuat(euler); WriteNodePose(k, false, true, false); }
        }

        float fovDeg = (fovLocked ? driven.Fov : k.Fov) * 180f / MathF.PI;
        ImGui.SetNextItemWidth(fieldW);
        ImGui.BeginDisabled(fovLocked);
        bool fovChanged = ImGui.DragFloat($"{fovLbl}##nfov", ref fovDeg, 0.1f, 1f, 175f, "%.1f") && !fovLocked;
        ImGui.EndDisabled();
        if(fovLocked) UiWidgets.Tooltip(I18n.T("inspector.fov_locked_tip"));
        else if(_tl.TrackOf(k)?.FovDriven == true) UiWidgets.Tooltip(I18n.T("inspector.fov_base_tip"));
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(fovChanged) { k.Fov = fovDeg * MathF.PI / 180f; WriteNodePose(k, false, false, true); }

        if(rotationLocked)
            UiWidgets.Chip("n_locked", fovLocked ? I18n.T("inspector.locked_rot_fov") : I18n.T("inspector.locked_rot"), XcpStyle.Info);

        // ---- 2×2 Action Grid ----
        ImGui.Spacing();
        float halfW = MathF.Max(64f * s, (UiWidgets.InnerAvailWidth - 6f * s) * 0.5f);
        var actSz = new Vector2(halfW, 0);

        ImGui.BeginDisabled(!gpose);
        if(UiWidgets.IconTextButton("n_recap", FontAwesomeIcon.Camera, I18n.T("inspector.recapture"), I18n.T("inspector.recapture_tip"), size: actSz, brioTheme: brio))
            Recapture(k);
        ImGui.EndDisabled();

        ImGui.SameLine(0, 6f * s);
        if(UiWidgets.IconTextButton("n_jump", FontAwesomeIcon.ArrowRight, I18n.T("common.jump"), I18n.T("inspector.jump_tip", k.Frame), size: actSz, brioTheme: brio))
            SeekPlayhead(k.Frame);

        if(UiWidgets.IconTextButton("n_dup", FontAwesomeIcon.Clone, I18n.T("common.duplicate"), I18n.T("inspector.duplicate_tip", _defaultGapFrames), size: actSz, brioTheme: brio))
            Duplicate(k);

        ImGui.SameLine(0, 6f * s);
        if(UiWidgets.DangerButton("n_del", FontAwesomeIcon.TrashAlt, I18n.T("common.delete"), I18n.T("inspector.delete_node_tip"), actSz, brio))
        {
            var ids = _tl.SelectedNodes.Count > 0 ? _tl.SelectedNodes.ToHashSet() : new System.Collections.Generic.HashSet<Guid> { k.Id };
            _tl.DeleteNodes(ids); Selected = null;
        }
    }

    private static Vector3 QuatToEulerDeg(Quaternion q)
    {
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

    public void SaveConfig()
    {
        _config.MovementLimit = NodeRange.MaxRange;
        _config.OverlayEnabled = _overlay.Enabled; _config.ShowPath = _overlay.ShowPath; _config.ShowFrustums = _overlay.ShowFrustums;
        _config.ShowLabels = _overlay.ShowLabels; _config.SpeedColors = _overlay.SpeedColors; _config.FrustumSize = _overlay.FrustumSize;
        _saveConfig();
    }

    // ---------------- Brio Sync Content (shared by N-Panel card & Brio secondary page) ----------------

    private void DrawBrioSyncContent(bool brio, bool compact)
    {
        float s = XcpStyle.Scale;
        UiWidgets.StatusDot(_brio.IsConnected ? XcpStyle.Success : XcpStyle.Warning, _brio.Status);
        if(_brio.Missing.Count > 0)
        {
            ImGui.SameLine();
            UiWidgets.Chip(compact ? "brio_miss_c" : "brio_miss", I18n.T("brio.missing_chip", _brio.Missing.Count), XcpStyle.Warning, string.Join("\n", _brio.Missing));
        }
        if(!_brio.IsConnected) return;

        ImGui.Spacing();
        int src = (int)_source;
        string vivLbl = I18n.T("brio.source.vivacity");
        string animLbl = I18n.T("brio.source.animation");
        if(compact)
        {
            if(UiWidgets.Segmented("brio_src_c", ref src, [vivLbl, animLbl], brio, UiWidgets.InnerAvailWidth,
                [I18n.T("brio.source.vivacity_tip"), I18n.T("brio.source.animation_tip")]))
                SetSource((BindSource)src);
        }
        else
        {
            string syncLbl = I18n.T("brio.sync_source");
            float lblX = MathF.Max(72f * s, ImGui.CalcTextSize(syncLbl).X + 16f * s);
            float segW = MathF.Max(220f * s, MathF.Max(ImGui.CalcTextSize(vivLbl).X, ImGui.CalcTextSize(animLbl).X) * 2f + 28f * s);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(syncLbl);
            ImGui.SameLine(lblX);
            if(UiWidgets.Segmented("brio_src", ref src, [vivLbl, animLbl], brio, MathF.Min(segW, MathF.Max(180f * s, UiWidgets.InnerAvailWidth - lblX - 4f * s)),
                [I18n.T("brio.source.vivacity_tip"), I18n.T("brio.source.animation_tip")]))
                SetSource((BindSource)src);
        }

        if(_source == BindSource.Animation)
        {
            ImGui.Spacing();
            DrawAnimationActorPicker(compact);
        }

        ImGui.Spacing();
        if(_source == BindSource.Vivacity)
        {
            string state = _brio.TimelineIsPlaying ? I18n.T("common.playing") : I18n.T("common.stopped");
            ImGui.TextDisabled(compact
                ? I18n.T("brio.vivacity_state_compact", state, $"{_brio.TimelineTime:0}", _brio.FrameMax, _brio.TimelineFps)
                : I18n.T("brio.vivacity_state_full", state, $"{_brio.TimelineTime:0}", _brio.FrameMax, _brio.TimelineFps));
        }
        else if(_anim.Available)
        {
            string state = _anim.IsPlaying ? I18n.T("common.playing") : I18n.T("common.paused");
            ImGui.TextDisabled(compact
                ? I18n.T("brio.anim_state_compact", state, $"{_anim.Time:0.00}", $"{_anim.Duration:0.00}", _tl.Fps)
                : I18n.T("brio.anim_state_full", BoundActor()?.Name.TextValue ?? "", state, $"{_anim.Time:0.00}", $"{_anim.Duration:0.00}",
                    $"{_anim.Time * _tl.Fps:0}", $"{_anim.Duration * _tl.Fps:0}", _tl.Fps));
        }
        else
            ImGui.TextColored(XcpStyle.Warning, I18n.T("brio.anim_unavailable", _anim.Status));

        ImGui.Spacing();
        if(compact)
        {
            float halfW = MathF.Max(64f * s, (UiWidgets.InnerAvailWidth - 4f * s) * 0.5f);
            var cell = new Vector2(halfW, 0);
            if(_source == BindSource.Vivacity)
            {
                if(UiWidgets.TogglePill("bc_fps", I18n.T("brio.opt_fps_compact"), _syncFpsWithBrio,
                    I18n.T("brio.opt_fps_tip"), size: cell, brioTheme: brio))
                {
                    _syncFpsWithBrio = !_syncFpsWithBrio;
                    if(!_syncFpsWithBrio) SetStatus(I18n.T("status.fps_sync_stopped"), false);
                }
                ImGui.SameLine(0, 4f * s);
                if(UiWidgets.TogglePill("bc_follow", I18n.T("brio.opt_follow_compact"), _followBrioPlayhead,
                    I18n.T("brio.opt_follow_tip", SourceName), size: cell, brioTheme: brio))
                    _followBrioPlayhead = !_followBrioPlayhead;

                if(UiWidgets.TogglePill("bc_drive", I18n.T("brio.opt_drive_compact"), _drivePlayheadToBrio,
                    I18n.T("brio.opt_drive_tip", SourceName), size: cell, brioTheme: brio))
                    _drivePlayheadToBrio = !_drivePlayheadToBrio;
                ImGui.SameLine(0, 4f * s);
                if(UiWidgets.TogglePill("bc_len", I18n.T("brio.opt_len"), _syncLengthWithSource,
                    I18n.T("brio.opt_len_tip"), size: cell, brioTheme: brio))
                    _syncLengthWithSource = !_syncLengthWithSource;
            }
            else
            {
                if(UiWidgets.TogglePill("bc_follow", I18n.T("brio.opt_follow_compact"), _followBrioPlayhead,
                    I18n.T("brio.opt_follow_tip", SourceName), size: cell, brioTheme: brio))
                    _followBrioPlayhead = !_followBrioPlayhead;
                ImGui.SameLine(0, 4f * s);
                if(UiWidgets.TogglePill("bc_drive", I18n.T("brio.opt_drive_compact"), _drivePlayheadToBrio,
                    I18n.T("brio.opt_drive_tip", SourceName), size: cell, brioTheme: brio))
                    _drivePlayheadToBrio = !_drivePlayheadToBrio;

                if(UiWidgets.TogglePill("bc_frz", I18n.T("brio.opt_freeze_compact"), _freezeAnimWhileScrubbing,
                    I18n.T("brio.opt_freeze_tip"), size: cell, brioTheme: brio))
                    _freezeAnimWhileScrubbing = !_freezeAnimWhileScrubbing;
                ImGui.SameLine(0, 4f * s);
                if(UiWidgets.TogglePill("bc_len", I18n.T("brio.opt_len"), _syncLengthWithSource,
                    I18n.T("brio.opt_len_tip"), size: cell, brioTheme: brio))
                    _syncLengthWithSource = !_syncLengthWithSource;
            }
        }
        else
        {
            if(_source == BindSource.Vivacity)
            {
                if(UiWidgets.TogglePill("b_fps", I18n.T("brio.opt_fps_full"), _syncFpsWithBrio,
                    I18n.T("brio.opt_fps_tip"), brioTheme: brio))
                {
                    _syncFpsWithBrio = !_syncFpsWithBrio;
                    if(!_syncFpsWithBrio) SetStatus(I18n.T("status.fps_sync_stopped"), false);
                }
                ImGui.SameLine();
            }
            if(UiWidgets.TogglePill("b_follow", I18n.T("brio.opt_follow_full"), _followBrioPlayhead,
                I18n.T("brio.opt_follow_tip", SourceName), brioTheme: brio))
                _followBrioPlayhead = !_followBrioPlayhead;
            ImGui.SameLine();
            if(UiWidgets.TogglePill("b_drive", I18n.T("brio.opt_drive_full"), _drivePlayheadToBrio,
                I18n.T("brio.opt_drive_tip", SourceName), brioTheme: brio))
                _drivePlayheadToBrio = !_drivePlayheadToBrio;
            ImGui.SameLine();
            if(UiWidgets.TogglePill("b_len", I18n.T("brio.opt_len"), _syncLengthWithSource,
                I18n.T("brio.opt_len_tip"), brioTheme: brio))
                _syncLengthWithSource = !_syncLengthWithSource;
            if(_source == BindSource.Animation)
            {
                ImGui.SameLine();
                if(UiWidgets.TogglePill("b_frz", I18n.T("brio.opt_freeze_full"), _freezeAnimWhileScrubbing,
                    I18n.T("brio.opt_freeze_tip"), brioTheme: brio))
                    _freezeAnimWhileScrubbing = !_freezeAnimWhileScrubbing;
            }
        }
    }

    // ---------------- Brio Integration Secondary Page ----------------

    private void DrawBrioTab(bool gpose, bool brio)
    {
        float s = XcpStyle.Scale;

        // ---- Card 1: Connection & Sync Source ----
        using(UiWidgets.Card("brio_sync_card", brio))
        {
            UiWidgets.SectionHeader(I18n.T("brio.card_sync_title"), brio, FontAwesomeIcon.Link);
            DrawBrioSyncContent(brio, compact: false);
        }

        if(!_brio.IsConnected) return;
        ImGui.Spacing();

        // ---- Lower Dual Cards: XAT Sync (left) + Bake to Free Cam (right) ----
        float availW = ImGui.GetContentRegionAvail().X;
        bool sideBySide = availW >= 500f * s;
        float colW = sideBySide ? (availW - 8f * s) * 0.5f : availW;
        float childH = MathF.Max(220f * s, ImGui.GetContentRegionAvail().Y);

        if(sideBySide)
        {
            if(ImGui.BeginChild("##brio_xat_col", new Vector2(colW, childH), true, ImGuiWindowFlags.AlwaysUseWindowPadding))
                DrawBrioXatBody(brio);
            ImGui.EndChild();
            ImGui.SameLine(0, 8f * s);
            if(ImGui.BeginChild("##brio_bake_col", new Vector2(0, childH), true, ImGuiWindowFlags.AlwaysUseWindowPadding))
                DrawBrioBakeBody(gpose, brio);
            ImGui.EndChild();
        }
        else
        {
            using(UiWidgets.Card("brio_xat_card", brio)) DrawBrioXatBody(brio);
            ImGui.Spacing();
            using(UiWidgets.Card("brio_bake_card", brio)) DrawBrioBakeBody(gpose, brio);
        }
    }

    private void DrawBrioXatBody(bool brio)
    {
        UiWidgets.SectionHeader(I18n.T("brio.xat_title"), brio, FontAwesomeIcon.Play,
            I18n.T("brio.xat_tip", SourceName));

        _sync.StartFrame = _tl.Keys.Count > 0 ? _tl.FirstFrame : 0;
        ImGui.TextDisabled(_source == BindSource.Vivacity
            ? I18n.T("brio.xat_start_vivacity", _sync.StartFrame)
            : I18n.T("brio.xat_start_anim", _sync.StartFrame, $"{_sync.StartFrame / (float)_tl.Fps:0.00}"));

        ImGui.Spacing();
        ImGui.BeginDisabled(!_brio.CanSync || _comp == null || !SourceAvailable);
        if(!_sync.Bound)
        {
            if(UiWidgets.IconTextButton("xat_bind", FontAwesomeIcon.Link, I18n.T("brio.xat_bind"),
                I18n.T("brio.xat_bind_tip", SourceName), active: true, brioTheme: brio))
                Try(() =>
                {
                    if(_source == BindSource.Vivacity && _syncFpsWithBrio && _tl.Fps != _brio.TimelineFps)
                    {
                        AlignToVivacity();
                        RebuildPathIfNeeded();
                    }
                    SyncImportedVivacityLength();
                    _sync.StartFrame = _tl.FirstFrame;
                    _sync.Bind(ExportComposer(out var warning)!);
                    SetStatus(I18n.T("brio.xat_bound_status", SourceName) + (warning.Length > 0 ? " " + warning : ""), warning.Length > 0);
                });
        }
        else if(UiWidgets.DangerButton("xat_unbind", FontAwesomeIcon.Unlink, I18n.T("brio.xat_unbind"), I18n.T("brio.xat_unbind_tip"), brioTheme: brio))
        {
            Try(() =>
            {
                _sync.Unbind();
                ReleaseAnimFreeze();
                _transportOwned = false;
                _anim.Reset();
                SetStatus(I18n.T("brio.xat_unbound_status"), false);
            });
        }
        ImGui.EndDisabled();

        if(_sync.Bound)
        {
            ImGui.Spacing();
            UiWidgets.StatusDot(XcpStyle.Info, _sync.Status);
        }
    }

    private void DrawBrioBakeBody(bool gpose, bool brio)
    {
        float s = XcpStyle.Scale;
        UiWidgets.SectionHeader(I18n.T("brio.bake_title"), brio, FontAwesomeIcon.Fire,
            I18n.T("brio.bake_tip"));

        if(_source != BindSource.Vivacity)
        {
            ImGui.TextColored(XcpStyle.Warning, I18n.T("brio.bake_vivacity_only"));
            return;
        }

        var cams = _brio.CanBake ? _brio.GetFreeCameras() : [];
        if(cams.Count == 0)
        {
            ImGui.TextColored(XcpStyle.Warning, I18n.T("brio.bake_no_free_cam"));
            return;
        }

        _bakeCamIndex = Math.Clamp(_bakeCamIndex, 0, cams.Count - 1);
        var names = cams.Select(c => c.Name + (c.InTimeline ? "" : I18n.T("brio.bake_not_in_timeline"))).ToArray();
        string camLbl = I18n.T("common.camera");
        ImGui.SetNextItemWidth(MathF.Max(140f * s, UiWidgets.InnerAvailWidth - ImGui.CalcTextSize(camLbl).X - 16f * s));
        ImGui.Combo($"{camLbl}##bake_cam", ref _bakeCamIndex, names, names.Length);

        ImGui.SetNextItemWidth(72f * s);
        ImGui.InputInt($"{I18n.T("brio.bake_step")}##bake_step", ref _bake.StepFrames);
        _bake.StepFrames = Math.Clamp(_bake.StepFrames, 1, 30);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(72f * s);
        float baseDeg = _bake.BaseFov * 180f / MathF.PI;
        if(ImGui.DragFloat($"{I18n.T("brio.bake_base_fov")}##bake_fov", ref baseDeg, 0.1f, 20f, 120f, "%.1f")) _bake.BaseFov = baseDeg * MathF.PI / 180f;
        UiWidgets.Tooltip(I18n.T("brio.bake_base_fov_tip"));

        ImGui.Checkbox($"{I18n.T("brio.bake_clear_existing")}##bake_clr", ref _bake.ClearExisting);

        if(_tl.Fps != _brio.TimelineFps)
            ImGui.TextColored(XcpStyle.Warning, I18n.T("brio.bake_fps_diff", _tl.Fps, _brio.TimelineFps));

        ImGui.Spacing();
        ImGui.BeginDisabled(_comp == null || !gpose);
        if(UiWidgets.IconTextButton("bake_go", FontAwesomeIcon.Fire, I18n.T("brio.bake_btn"),
            I18n.T("brio.bake_btn_tip"), active: true, brioTheme: brio))
            Try(() =>
            {
                _bake.Bake(cams[_bakeCamIndex], ExportComposer(out var warning)!, Vector3.Zero, Quaternion.Identity);
                SetStatus(_bake.Status + (warning.Length > 0 ? " " + warning : ""), warning.Length > 0);
            });
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!_bake.CanUndo);
        if(UiWidgets.IconTextButton("bake_undo", FontAwesomeIcon.Reply, I18n.T("brio.bake_undo"), I18n.T("brio.bake_undo_tip"), brioTheme: brio))
            Try(() => { _bake.Undo(); SetStatus(_bake.Status, false); });
        ImGui.EndDisabled();

        if(!string.IsNullOrEmpty(_bake.Status))
        {
            ImGui.Spacing();
            UiWidgets.StatusDot(XcpStyle.Info, _bake.Status);
        }
    }

    private void Try(Action a)
    {
        try { a(); }
        catch(Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            SetStatus(I18n.T("status.action_failed", inner.Message), true);
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
            SetStatus(I18n.T("status.capture_nan"), true);
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
            SetStatus(I18n.T("status.capture_replaced", frame), false);
            return;
        }
        k.Frame = frame;
        EnsureLength(k.Frame);
        _tl.AddCapturedNode(k, snapshot: false);
        if(_tl.ActiveTrack.FollowPath != null) ApplyCapture(k);
        Selected = k;
        SetStatus(I18n.T("status.capture_added", _tl.ActiveTrack.Name, frame, _tl.Keys.Count), false);
    }

    /// <summary>Captures the current camera at the playhead as the start of a new shot (hard cut from the previous node).</summary>
    private bool CanToggleCut(Keyframe node) => _tl.TrackOf(node)?.Keys.Any(k => k.Frame < node.Frame) == true;

    private void ToggleNodeCut(Keyframe node)
    {
        bool cut = !node.CutIn;
        _tl.Snapshot();
        if(ShotFor(node) is { } shot) ConstantCuts.Set(shot, node.Id, cut);
        node.CutIn = cut;
        node.CutAuto = false;
        _tl.Touch();
    }

    private void CutAtPlayhead()
    {
        int frame = (int)MathF.Round(_tl.Playhead);
        if(!_tl.Keys.Any(x => x.Frame < frame))
        {
            SetStatus(I18n.T("status.cut_no_prev"), true);
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
        SetStatus(I18n.T("status.cut_added", frame), false);
    }

    private void CaptureAppend()
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        k.Frame = _tl.Keys.Count == 0 ? (int)MathF.Round(_tl.Playhead) : CamTimeline.TrackLastFrame(_tl.ActiveTrack) + _defaultGapFrames;
        EnsureLength(k.Frame);
        _tl.AddCapturedNode(k, snapshot: false);
        if(_tl.ActiveTrack.FollowPath != null) ApplyCapture(k);
        Selected = k;
        _tl.Playhead = k.Frame;
        _tlw.EnsureVisible(k.Frame, 700);
        SetStatus(I18n.T("status.append_added", k.Frame, _tl.Keys.Count), false);
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
            SetStatus(I18n.T("status.sample_exists", frame, _tl.Keys.IndexOf(existing) + 1), true);
            return;
        }

        var segment = seq.Segments[seq.SegmentAt(frame)];
        if(!segment.Curves.Intervals.Any(i => frame > i.Start && frame < i.End))
        { SetStatus(I18n.T("status.sample_need_between"), true); return; }
        var (s, _) = seq.Sample(frame);
        if(!XcpMath.IsFinite(s.Pos) || !XcpMath.IsFinite(s.Rot) || !float.IsFinite(s.Fov))
        {
            SetStatus(I18n.T("status.sample_invalid"), true);
            return;
        }

        try
        {
            Selected = _tl.AddSampledNode(segment.Curves, frame);
            SetStatus(I18n.T("status.sample_added", frame), false);
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
        SetStatus(I18n.T("status.recaptured", (owner?.Keys.IndexOf(target) ?? 0) + 1), false);
    }

    /// <summary>
    /// Writes a captured camera into a node. On a track with a Follow Path constraint only rotation and FoV come
    /// from the camera: inserted nodes inherit the current path progress and offset; nodes outside the shot
    /// use the automatic endpoint rules. The player camera position is ignored. Values driven by tracking constraints
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
        _tl.SelectNodes([c.Id]);
        Selected = c;
    }

    private void OpenSaveDialog()
    {
        FileDialog.SaveFileDialog($"{I18n.T("xcp.dialog.export")}###xcpcam_save", "XAT Camera File{.xcp}", "camera", ".xcp",
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
            if(_comp == null) throw new InvalidOperationException(I18n.T("status.export_need_node"));
            // .xcp is relative to the GPose target: convert the world-space path with the current target
            if(!CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out var err))
                throw new InvalidOperationException(I18n.T("status.export_need_target", err));
            var export = ExportComposer(out var warning)!;
            var frames = XcpMath.ToRelative(export.BakeXcp(PathBuilder.Fps), bp, br);
            XcpFile.Write(path, frames, _embedProject ? ProjectCodec.Encode(_tl) : null, bp, br);
            _lastDir = Path.GetDirectoryName(path) ?? _lastDir;
            string cutNote = _comp.CutFrames.Count > 0 ? I18n.T("status.export_cut_note", _comp.CutFrames.Count) : "";
            SetStatus(I18n.T("status.export_ok", frames.Count, cutNote, path) + (warning.Length > 0 ? " " + warning : ""), warning.Length > 0);
            _log.Info($"exported {frames.Count} frames to {path}");
        }
        catch(Exception ex)
        {
            SetStatus(I18n.T("status.export_failed", ex.Message), true);
            _log.Error(ex, "export failed");
        }
    }

    private void SetStatus(string s, bool error)
    {
        _status = s;
        _statusIsError = error;
    }
}
