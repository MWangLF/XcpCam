using Dalamud.Plugin.Services;
using System;
using System.IO;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.BrioInterop;

/// <summary>
/// Mode 1: play the .xcp in Brio's own XAT player whenever the bound clock plays — either Brio's Vivacity
/// timeline or the target's animation scrub bar (see <see cref="ClockSource"/>).
/// Brio's XAT player runs on its own Stopwatch and cannot seek, so we can only start it from the beginning:
/// when the clock starts / crosses <see cref="StartFrame"/> / wraps around (loop), and stop it when the clock
/// stops or pauses.
/// </summary>
public sealed class TimelineSync
{
    private readonly BrioBridge _brio;
    private readonly IPluginLog _log;

    public bool Bound { get; private set; }
    /// <summary>Start position on the bound clock, in that clock's frames (timeline frames at the clock's fps).</summary>
    public int StartFrame;
    public string Status { get; private set; } = "";
    public string TempFile { get; } = Path.Combine(Path.GetTempPath(), "XcpCam_timeline_sync.xcp");

    /// <summary>Supplies (isPlaying, timeInFrames, fps) of whatever the camera timeline is bound to.</summary>
    public Func<(bool playing, double frame, int fps)>? Clock;
    public Func<string>? ClockNameProvider;
    public string ClockName => ClockNameProvider?.Invoke() ?? I18n.T("topbar.tab_timeline");
    public Func<bool>? ClockCycle;
    public Func<long>? ClockGeneration;
    private long _lastGeneration;

    private bool _wasPlaying;
    private double _lastTime;
    private bool _startedThisRun;

    /// <summary>Path time (seconds) currently being played through the sync, or null.</summary>
    public float? PlayheadSec { get; private set; }
    private DateTime _cutsceneStartUtc;

    public TimelineSync(BrioBridge brio, IPluginLog log) { _brio = brio; _log = log; }

    private (bool playing, double frame, int fps) Read() =>
        Clock?.Invoke() ?? (_brio.TimelineIsPlaying, _brio.TimelineTime, _brio.TimelineFps);

    /// <summary>Current GPose target (world position / rotation); .xcp frames are written relative to it.</summary>
    public Func<(Vector3 pos, Quaternion rot)?>? Base;

    public void Bind(ShotComposer comp)
    {
        var target = Base?.Invoke() ?? throw new InvalidOperationException(I18n.T("brio.sync.need_gpose_target"));
        var frames = XcpMath.ToRelative(comp.BakeXcp(PathBuilder.Fps), target.pos, target.rot);
        XcpWriter.Write(TempFile, frames);
        _brio.LoadCutsceneFile(TempFile);
        _brio.SetCutsceneLoop(false);
        _brio.SetCutsceneFov(true);
        Bound = true;
        var c = Read();
        _wasPlaying = c.playing;
        _lastTime = c.frame;
        _lastGeneration = ClockGeneration?.Invoke() ?? 0;
        _startedThisRun = false;
        Status = I18n.T("brio.sync.bound", frames.Count, $"{comp.DurationSec:0.00}", comp.CutFrames.Count, ClockName, StartFrame);
        _log.Info($"TimelineSync bound to {ClockName}, {frames.Count} frames, start frame {StartFrame}");
    }

    /// <summary>Re-bake after the keys changed while bound.</summary>
    public void Refresh(ShotComposer comp)
    {
        if(!Bound) return;
        bool wasRunning = _brio.CutsceneIsRunning;
        if(wasRunning) _brio.StopCutscene();
        Bind(comp);
    }

    public void Unbind()
    {
        if(Bound && _brio.CutsceneIsRunning) _brio.StopCutscene();
        Bound = false;
        PlayheadSec = null;
        Status = I18n.T("brio.sync.unbound");
    }

    /// <summary>Call every framework tick.</summary>
    public void Tick(float pathDuration)
    {
        if(!Bound) return;
        if(!_brio.IsConnected || !_brio.CanSync)
        {
            Status = I18n.T("brio.sync.paused_disconnected");
            return;
        }
        if(!_brio.HasCutsceneFile)
        {
            Status = I18n.T("brio.sync.replaced");
            Bound = false;
            return;
        }

        var (playing, time, fps) = Read();
        long generation = ClockGeneration?.Invoke() ?? 0;
        bool seeked = generation != _lastGeneration;
        if(seeked)
        {
            if(_brio.CutsceneIsRunning) _brio.StopCutscene();
            _startedThisRun = false;
            _lastTime = time;
        }
        _lastGeneration = generation;

        if(playing)
        {
            bool wrapped = !seeked && ((ClockCycle?.Invoke() ?? false) || (_wasPlaying && time + 0.5 < _lastTime));
            if(wrapped) _startedThisRun = false;

            bool crossed = TransportMath.CrossedXatStart(wrapped, _wasPlaying, _startedThisRun,
                _lastTime, time, StartFrame, fps);

            if(wrapped && _brio.CutsceneIsRunning)
                _brio.StopCutscene();

            if(crossed && !_startedThisRun)
            {
                if(_brio.CutsceneIsRunning) _brio.StopCutscene();
                _brio.StartCutscene();
                _startedThisRun = true;
                _cutsceneStartUtc = DateTime.UtcNow;
            }
        }
        else if(_wasPlaying)
        {
            if(_brio.CutsceneIsRunning) _brio.StopCutscene();
            _startedThisRun = false;
        }

        PlayheadSec = playing && _startedThisRun
            ? Math.Clamp((float)(DateTime.UtcNow - _cutsceneStartUtc).TotalSeconds, 0f, pathDuration)
            : null;

        if(playing && _brio.CurrentCameraIsFree)
            Status = I18n.T("brio.sync.free_cam_warn");
        else
            Status = playing
                ? (_startedThisRun ? I18n.T("brio.sync.playing_started") : I18n.T("brio.sync.waiting_start", ClockName, StartFrame))
                : I18n.T("brio.sync.waiting_play", ClockName);

        _wasPlaying = playing;
        _lastTime = time;
    }
}
