using System;
using System.Globalization;

namespace XcpCam.Core;

public readonly record struct AnimationIdentity(nint Actor, ulong ActorId, nint Control, nint Binding, nint Animation);

public readonly record struct AnimationSample(AnimationIdentity Identity, double Time, double Duration, uint OverflowCount, uint UnderflowCount);

public enum PlaybackState { Stopped, Starting, Playing, Paused, Ended }

/// <summary>
/// Pure, single-threaded transport observation; never writes an animation or advances time itself.
/// Call Reset when the animation becomes unavailable. Nonfinite time or nonpositive/nonfinite
/// duration is also treated as unavailable. Identity addresses are opaque, not dereferenced.
/// </summary>
/// <remarks>
/// Pause/Stop describe intent, not a native freeze. Subsequent real forward movement with
/// canAdvance=true takes over as external playback, even from Ended. A stationary terminal
/// sample (including the caller's terminal seek) leaves Ended latched. Call Seek for an
/// explicit scrub, and Play to start again; neither a backward scrub nor elapsed dt is playback.
/// A true Observe result is a one-shot request to freeze and seek to the sample's duration.
/// </remarks>
public sealed class TransportState
{
    private const double MovementEpsilon = 1e-8;
    private const double StartupTimeout = 1.0;
    private const double PauseTimeout = 0.15;

    private AnimationSample _previous;
    private bool _hasSample;
    private bool _hasProgress;
    private double _stationarySeconds;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public bool IsPlaying => State is PlaybackState.Starting or PlaybackState.Playing;
    public bool Cycle { get; private set; }
    public bool Discontinuity { get; private set; }
    public long Generation { get; private set; }

    public void Reset()
    {
        Generation++;
        Cycle = false;
        Discontinuity = true;
        _hasSample = false;
        _previous = default;
        SetIdle(PlaybackState.Stopped);
    }

    public void Play(AnimationSample sample)
    {
        if(!IsValid(sample)) { Reset(); return; }
        Cycle = false;
        Discontinuity = _hasSample && sample.Identity != _previous.Identity;
        if(Discontinuity) Generation++;
        SetBaseline(sample, PlaybackState.Starting);
    }

    public void Pause()
    {
        Cycle = Discontinuity = false;
        // Only Play, Seek, Reset, identity replacement or external motion clears Ended.
        SetIdle(State == PlaybackState.Ended ? PlaybackState.Ended : PlaybackState.Paused);
    }

    public void Stop()
    {
        Cycle = Discontinuity = false;
        SetIdle(State == PlaybackState.Ended ? PlaybackState.Ended : PlaybackState.Stopped);
    }

    public void Seek(AnimationSample sample)
    {
        if(!IsValid(sample)) { Reset(); return; }
        Generation++;
        Cycle = false;
        Discontinuity = true;
        SetBaseline(sample, PlaybackState.Paused);
    }

    /// <summary>
    /// dt is finite positive elapsed seconds used only for stationary debounce. canAdvance
    /// must be false at zero/negative/nonfinite effective speed or native freeze. Frozen
    /// observations still update the baseline, but cannot finish or leave stale progress.
    /// </summary>
    public bool Observe(AnimationSample sample, double dt, bool loop, bool canAdvance)
    {
        Cycle = Discontinuity = false;
        if(!IsValid(sample))
        {
            // Repeated unavailable observations do not continually invalidate consumers.
            if(_hasSample || State != PlaybackState.Stopped) Reset();
            return false;
        }

        if(!_hasSample)
        {
            SetBaseline(sample, PlaybackState.Stopped);
            return false;
        }
        if(sample.Identity != _previous.Identity)
        {
            Generation++;
            Discontinuity = true;
            SetBaseline(sample, PlaybackState.Stopped);
            return false;
        }

        var previous = _previous;
        _previous = sample;
        // Serial-number arithmetic accepts uint wrap and multiple cycles, but rejects a
        // counter rollback/reset. Exactly half the uint range is ambiguous and rejected.
        uint overflowDelta = unchecked(sample.OverflowCount - previous.OverflowCount);
        Cycle = overflowDelta > 0 && overflowDelta < 0x80000000u;
        bool counterReset = overflowDelta != 0 && !Cycle;
        bool reverse = sample.UnderflowCount != previous.UnderflowCount;
        double movement = sample.Time - previous.Time;
        if(counterReset || reverse || (!Cycle && movement < -MovementEpsilon))
        {
            Generation++;
            Discontinuity = true;
            SetIdle(State == PlaybackState.Ended ? PlaybackState.Ended : PlaybackState.Paused);
            return false;
        }

        if(!canAdvance)
        {
            SetIdle(State is PlaybackState.Starting or PlaybackState.Playing ? PlaybackState.Paused : State);
            return false;
        }

        // The caller may correct a wrapped sample to the terminal pose after our true
        // result. That forward seek is not external playback. Ended takeover requires
        // a forward sample below the endpoint, or an actual forward overflow.
        if(State == PlaybackState.Ended && !Cycle && sample.Time >= sample.Duration) return false;

        bool forward = Cycle || movement > MovementEpsilon;
        if(forward)
        {
            _hasProgress = true;
            _stationarySeconds = 0;
            State = PlaybackState.Playing;
        }
        else if(State == PlaybackState.Ended)
        {
            return false;
        }

        if(!loop && _hasProgress && (Cycle || sample.Time >= sample.Duration))
        {
            SetIdle(PlaybackState.Ended);
            return true;
        }

        if(!forward)
        {
            if(double.IsFinite(dt) && dt > 0) _stationarySeconds += dt;
            double timeout = State == PlaybackState.Starting ? StartupTimeout : PauseTimeout;
            if(IsPlaying && _stationarySeconds >= timeout) SetIdle(PlaybackState.Paused);
        }
        return false;
    }

    private static bool IsValid(AnimationSample sample) =>
        double.IsFinite(sample.Time) && double.IsFinite(sample.Duration) && sample.Duration > 0;

    private void SetBaseline(AnimationSample sample, PlaybackState state)
    {
        _previous = sample;
        _hasSample = true;
        SetIdle(state);
    }

    private void SetIdle(PlaybackState state)
    {
        State = state;
        _hasProgress = false;
        _stationarySeconds = 0;
    }
}

public static class TransportMath
{
    public static double ConvertFrame(double frame, double fromFps, double toFps) =>
        frame / ValidFps(fromFps) * ValidFps(toFps);

    /// <summary>Clamp to the inclusive range; a play command at/past end restarts at start.</summary>
    public static double PlayPosition(double frame, double start, double end)
    {
        if(!double.IsFinite(start) || !double.IsFinite(end) || end < start)
            throw new ArgumentOutOfRangeException(nameof(end), "A finite, ordered frame range is required.");
        if(!double.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        return frame >= end ? start : Math.Clamp(frame, start, end);
    }

    /// <summary>Accept numeric FPS only, without rounding or silently substituting 60.</summary>
    public static double ValidFps(object? value)
    {
        double fps = value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            nint signed => (double)signed,
            nuint unsigned => (double)unsigned,
            Half half => (double)half,
            _ => throw new ArgumentException("FPS must be numeric.", nameof(value)),
        };
        if(!double.IsFinite(fps) || fps <= 0 || fps > 240)
            throw new ArgumentOutOfRangeException(nameof(value), "FPS must be finite and in (0, 240].");
        return fps;
    }

    /// <summary>A confirmed cycle crosses the start even if the next observation is outside the startup window.</summary>
    public static bool CrossedXatStart(bool wrapped, bool wasPlaying, bool started, double previous,
        double current, double start, double fps) =>
        (wrapped && current >= start)
        || (wasPlaying && !wrapped && previous < start && current >= start)
        || (!started && current >= start && current - start < Math.Max(2, fps * 0.1));

    public static float ResumeSpeed(float? previousOverride, float effectiveSpeed)
    {
        if(previousOverride is float speed && float.IsFinite(speed) && speed > 0) return speed;
        return float.IsFinite(effectiveSpeed) && effectiveSpeed > 0 ? effectiveSpeed : 1f;
    }
}
