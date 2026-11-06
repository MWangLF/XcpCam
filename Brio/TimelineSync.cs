using Dalamud.Plugin.Services;
using System;
using System.IO;
using XcpCam.Core;

namespace XcpCam.BrioInterop;

/// <summary>
/// Mode 1: play the .xcp in Brio's own XAT player whenever the Vivacity timeline plays.
/// Brio's XAT player runs on its own Stopwatch and cannot seek, so we can only start it from the beginning:
/// when the timeline starts / crosses <see cref="StartFrame"/> / wraps around (loop), and stop it when the
/// timeline stops or pauses.
/// </summary>
public sealed class TimelineSync
{
    private readonly BrioBridge _brio;
    private readonly IPluginLog _log;

    public bool Bound { get; private set; }
    public int StartFrame;
    public string Status { get; private set; } = "";
    public string TempFile { get; } = Path.Combine(Path.GetTempPath(), "XcpCam_timeline_sync.xcp");

    private bool _wasPlaying;
    private double _lastTime;
    private bool _startedThisRun;

    /// <summary>Path time (seconds) currently being played through the sync, or null.</summary>
    public float? PlayheadSec { get; private set; }
    private DateTime _cutsceneStartUtc;

    public TimelineSync(BrioBridge brio, IPluginLog log) { _brio = brio; _log = log; }

    public void Bind(CameraPath path)
    {
        var frames = path.Bake(PathBuilder.Fps);
        XcpWriter.Write(TempFile, frames);
        _brio.LoadCutsceneFile(TempFile);
        _brio.SetCutsceneLoop(false);
        _brio.SetCutsceneFov(true);
        Bound = true;
        _wasPlaying = _brio.TimelineIsPlaying;
        _lastTime = _brio.TimelineTime;
        _startedThisRun = false;
        Status = $"已绑定：{frames.Count} 帧（{path.Duration:0.00}s），在时间轴第 {StartFrame} 帧开始";
        _log.Info($"TimelineSync bound, {frames.Count} frames, start frame {StartFrame}");
    }

    /// <summary>Re-bake after the keys changed while bound.</summary>
    public void Refresh(CameraPath path)
    {
        if(!Bound) return;
        bool wasRunning = _brio.CutsceneIsRunning;
        if(wasRunning) _brio.StopCutscene();
        Bind(path);
    }

    public void Unbind()
    {
        if(Bound && _brio.CutsceneIsRunning) _brio.StopCutscene();
        Bound = false;
        PlayheadSec = null;
        Status = "未绑定";
    }

    /// <summary>Call every framework tick.</summary>
    public void Tick(float pathDuration)
    {
        if(!Bound) return;
        if(!_brio.IsConnected || !_brio.CanSync)
        {
            Status = "Brio 未连接，联动暂停";
            return;
        }
        if(!_brio.HasCutsceneFile)
        {
            Status = "Brio 中的 XAT 文件已被替换，已解除绑定";
            Bound = false;
            return;
        }

        bool playing = _brio.TimelineIsPlaying;
        double time = _brio.TimelineTime;
        int fps = _brio.TimelineFps;

        if(playing)
        {
            bool wrapped = _wasPlaying && time + 0.5 < _lastTime;
            if(wrapped) _startedThisRun = false;

            bool crossed = (_wasPlaying && !wrapped && _lastTime < StartFrame && time >= StartFrame)
                || (!_startedThisRun && time >= StartFrame && time - StartFrame < Math.Max(2, fps * 0.1));

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
            Status = "⚠ 当前是 Brio 自由相机，XAT 播放不会生效。请切到默认/普通相机。";
        else
            Status = playing
                ? (_startedThisRun ? "播放中（XAT 已同步启动）" : $"等待时间轴到达第 {StartFrame} 帧")
                : "已绑定，等待时间轴播放";

        _wasPlaying = playing;
        _lastTime = time;
    }
}
