using System;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class TransportTests
{
    // Intentionally no game dependencies or automatic entry point. The host runner calls Run.
    public static void Run(Action<bool, string> check)
    {
        var identity = new AnimationIdentity((nint)1, 2, (nint)3, (nint)4, (nint)5);
        AnimationSample Sample(double time, double duration = 1, uint overflow = 0, uint underflow = 0) =>
            new(identity, time, duration, overflow, underflow);
        void Check(bool ok, string name) => check(ok, "transport: " + name);
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        bool Rejects(Action action)
        {
            try { action(); return false; }
            catch(ArgumentException) { return true; }
        }

        Check(TransportMath.CrossedXatStart(true, true, false, 58, 12, 0, 60),
            "XAT restarts after native loop observed at 0.2s (outside startup window)");
        Check(!TransportMath.CrossedXatStart(true, true, false, 58, 2, 10, 60),
            "XAT waits for start frame after wrap when it has not been reached");
        Check(TransportMath.CrossedXatStart(false, true, false, 2, 12, 10, 60),
            "XAT starts when delayed start is crossed after wrap");

        var t = new TransportState();
        Check(t.State == PlaybackState.Stopped && !t.IsPlaying && t.Generation == 0,
            "initial state is stopped");
        t.Play(Sample(0));
        Check(t.State == PlaybackState.Starting && t.IsPlaying, "Play starts pending real progress");
        Check(!t.Observe(Sample(0.4), 0, false, true) && t.State == PlaybackState.Playing,
            "actual progress starts playback even with zero dt");
        Check(t.Observe(Sample(1), 0.01, false, true) && t.State == PlaybackState.Ended && !t.IsPlaying,
            "non-loop freezes at exact end without waiting for overflow");
        Check(!t.Observe(Sample(1), 100, false, true) && t.State == PlaybackState.Ended,
            "Ended is latched and completion is one-shot");
        t.Pause(); t.Stop();
        Check(t.State == PlaybackState.Ended, "Pause and Stop cannot clear an Ended latch");
        t.Play(Sample(1));
        Check(!t.Observe(Sample(1), 0.5, false, true) && t.State == PlaybackState.Starting,
            "starting at end is not completion without progress");
        Check(!t.Observe(Sample(1), 0.5, false, true) && t.State == PlaybackState.Paused,
            "one second stalled startup pauses rather than succeeds");
        t.Play(Sample(0.8));
        Check(t.Observe(Sample(1.1), 0, false, true), "progress beyond duration also completes");

        t.Play(Sample(0.005, 0.02, 8));
        Check(t.Observe(Sample(0.001, 0.02, 9), 0.016, false, true) && t.Cycle && !t.Discontinuity,
            "sub-50ms overflow completes even when local time moves backward");
        Check(!t.Observe(Sample(0.02, 0.02, 9), 0.016, false, true)
            && t.State == PlaybackState.Ended && !t.Cycle,
            "corrective terminal seek after overflow is not external playback");
        Check(!t.Observe(Sample(0.02, 0.02, 9), 5, false, true) && t.State == PlaybackState.Ended,
            "corrected endpoint stays latched");

        t.Play(Sample(0.003, 0.01, 10));
        Check(t.Observe(Sample(0.003, 0.01, 14), 0, false, true) && t.Cycle,
            "multiple cycles with identical local time complete");
        t.Play(Sample(0.003, 0.01, uint.MaxValue));
        Check(t.Observe(Sample(0.003, 0.01, 0), 0, false, true) && t.Cycle,
            "uint overflow counter wraps forward");
        t.Play(Sample(0.003, 0.01, uint.MaxValue - 1));
        Check(t.Observe(Sample(0.003, 0.01, 2), 0, false, true) && t.Cycle,
            "multiple forward cycles across uint wrap");

        t.Play(Sample(0.015, 0.02, 20));
        Check(!t.Observe(Sample(0.015, 0.02, 25), 10, true, true) && t.Cycle
            && t.State == PlaybackState.Playing, "loop never freezes even after multiple cycles");
        Check(!t.Observe(Sample(0.02, 0.02, 25), 0, true, true) && !t.Cycle && t.IsPlaying,
            "loop exact end does not freeze and Cycle lasts one observation");

        t.Pause();
        Check(t.State == PlaybackState.Paused && !t.IsPlaying && !t.Cycle, "Pause clears playing state");
        Check(!t.Observe(Sample(0.02, 0.02, 25), 10, false, true)
            && t.State == PlaybackState.Paused, "Pause clears pending completion");
        t.Stop();
        Check(t.State == PlaybackState.Stopped && !t.IsPlaying, "Stop clears playing state");
        long generation = t.Generation;
        t.Seek(Sample(0.25));
        Check(t.State == PlaybackState.Paused && t.Discontinuity && t.Generation == generation + 1,
            "Seek establishes a paused baseline and increments generation");
        Check(!t.Observe(Sample(0.25), 10, false, true) && !t.Discontinuity && !t.IsPlaying,
            "own seek is not playback and discontinuity is observation-local");
        Check(!t.Observe(Sample(0.3), 0, false, true) && t.State == PlaybackState.Playing,
            "real external progress takes over after Seek");
        t.Stop();
        Check(!t.Observe(Sample(0.4), 0, false, true) && t.State == PlaybackState.Playing,
            "real external progress takes over after Stop");
        t.Pause();
        Check(!t.Observe(Sample(0.5), 0, false, true) && t.State == PlaybackState.Playing,
            "real external progress takes over after Pause");

        // Each identity component matters, including ActorId for reused actor addresses.
        foreach(var changed in new[]
        {
            identity with { Actor = (nint)11 }, identity with { ActorId = 12 },
            identity with { Control = (nint)13 }, identity with { Binding = (nint)14 },
            identity with { Animation = (nint)15 },
        })
        {
            t.Play(Sample(0.9));
            generation = t.Generation;
            var replacement = Sample(1, overflow: 99) with { Identity = changed };
            Check(!t.Observe(replacement, 10, false, true) && t.Discontinuity && !t.Cycle
                && t.State == PlaybackState.Stopped && t.Generation == generation + 1,
                $"identity replacement resets rather than completes ({changed})");
            Check(!t.Observe(replacement with { Time = 0.2 }, 0, false, true) && t.Discontinuity,
                "replacement baseline is used on subsequent observation");
        }
        t.Play(Sample(0));
        generation = t.Generation;
        t.Reset();
        Check(t.State == PlaybackState.Stopped && t.Discontinuity && t.Generation == generation + 1,
            "unavailable Reset drops state and invalidates generation");
        Check(!t.Observe(Sample(1, overflow: 80), 100, false, true) && !t.IsPlaying && !t.Cycle,
            "first sample after Reset is baseline only");
        foreach(var invalid in new[]
        {
            Sample(double.NaN), Sample(double.PositiveInfinity), Sample(0, 0),
            Sample(0, -1), Sample(0, double.NaN), Sample(0, double.PositiveInfinity),
        })
        {
            t.Play(Sample(0));
            generation = t.Generation;
            Check(!t.Observe(invalid, 100, false, true) && t.State == PlaybackState.Stopped
                && t.Generation == generation + 1, "invalid sample is unavailable, never completion");
            Check(!t.Observe(invalid, 100, false, true) && t.Generation == generation + 1,
                "repeated unavailable samples do not churn generation");
        }

        t.Play(Sample(0));
        Check(!t.Observe(Sample(0), 0.75, false, true) && t.State == PlaybackState.Starting,
            "wall time does not turn Starting into Playing");
        Check(!t.Observe(Sample(0.000002), 100, false, true) && t.State == PlaybackState.Playing,
            "progress smaller than 1e-5 starts playback and resets startup debounce");
        Check(!t.Observe(Sample(0.000002), 0.1, false, true) && t.IsPlaying,
            "brief stationary interval is debounced");
        Check(!t.Observe(Sample(0.000002), 0.051, false, true) && t.State == PlaybackState.Paused,
            "stationary for 0.15 seconds clears inferred playback");
        t.Play(Sample(0, 0.000001));
        Check(t.Observe(Sample(0.000001, 0.000001), 0, false, true),
            "very short animation completes from actual movement");
        t.Play(Sample(0.3));
        Check(!t.Observe(Sample(0.3 + 1e-10), 0.1, false, true) && t.State == PlaybackState.Starting,
            "sub-epsilon noise is not playback");

        t.Play(Sample(0.8));
        Check(!t.Observe(Sample(0.9), 0, false, true), "progress before native freeze");
        Check(!t.Observe(Sample(1, overflow: 1), 100, false, false)
            && t.State == PlaybackState.Paused && !t.IsPlaying,
            "zero effective speed immediately clears playing and prevents completion");
        for(int i = 0; i < 5; i++)
            Check(!t.Observe(Sample(1, overflow: 1), 100, false, false), "frozen time never counts down");
        Check(!t.Observe(Sample(1, overflow: 1), 100, false, true)
            && t.State == PlaybackState.Paused, "unfreezing stationary endpoint cannot consume stale completion");
        t.Play(Sample(0));
        Check(!t.Observe(Sample(0), 100, false, false) && !t.IsPlaying,
            "frozen startup cannot run a wall-clock animation countdown");

        t.Play(Sample(0.9));
        t.Observe(Sample(1), 0, false, true);
        generation = t.Generation;
        Check(!t.Observe(Sample(0.2), 0, false, true) && t.State == PlaybackState.Ended
            && t.Discontinuity && t.Generation == generation + 1,
            "external backward scrub cannot complete or clear Ended");
        Check(!t.Observe(Sample(0.3), 0, false, true) && t.State == PlaybackState.Playing,
            "external real forward motion takes over Ended below terminal pose");
        Check(t.Observe(Sample(1), 0, false, true), "external takeover can reach a new terminal");
        t.Seek(Sample(0.2));
        Check(t.State == PlaybackState.Paused, "Seek explicitly clears Ended");
        t.Play(Sample(0));
        Check(t.State == PlaybackState.Starting, "Play explicitly clears Ended");

        t.Play(Sample(0.9));
        t.Observe(Sample(1), 0, false, true);
        Check(!t.Observe(Sample(0.1, overflow: 1), 0, true, true)
            && t.Cycle && t.State == PlaybackState.Playing,
            "real external forward overflow can take over Ended in loop mode");
        t.Reset();
        Check(!t.Observe(Sample(0.2), 50, false, true) && !t.IsPlaying,
            "external playback is not inferred from its initial baseline");
        Check(!t.Observe(Sample(0.2), 50, false, true) && !t.IsPlaying,
            "stationary external animation is never inferred playing");
        Check(!t.Observe(Sample(0.200002), 0, false, true) && t.IsPlaying,
            "tiny external forward progress without Play infers playback");

        t.Play(Sample(0.9, overflow: 20));
        generation = t.Generation;
        Check(!t.Observe(Sample(0.1, overflow: 20), 1, false, true)
            && t.Discontinuity && !t.Cycle && !t.IsPlaying && t.Generation == generation + 1,
            "backward scrub with unchanged counters is discontinuity, not loop/completion");
        t.Play(Sample(0.9, overflow: 20));
        Check(!t.Observe(Sample(0.1, overflow: 19), 1, false, true) && !t.Cycle && t.Discontinuity,
            "counter rollback is not a massive forward overflow");
        t.Play(Sample(0.9));
        Check(!t.Observe(Sample(1, overflow: 0x80000000u), 1, false, true)
            && !t.Cycle && t.Discontinuity, "half-range serial delta is ambiguous, not completion");
        t.Play(Sample(0.9, underflow: uint.MaxValue));
        Check(!t.Observe(Sample(1, underflow: 0), 1, false, true) && t.Discontinuity && !t.Cycle,
            "reverse underflow, including uint wrap, is not forward completion");
        t.Play(Sample(0));
        Check(!t.Observe(Sample(0), double.NaN, false, true)
            && !t.Observe(Sample(0), double.PositiveInfinity, false, true)
            && !t.Observe(Sample(0), -20, false, true) && t.State == PlaybackState.Starting,
            "invalid dt cannot poison debounce");
        Check(!t.Observe(Sample(0), 1, false, true) && t.State == PlaybackState.Paused,
            "valid dt still stalls startup after invalid dt");

        foreach(double fps in new[] { 24d, 30d, 60d, 120d })
        {
            Check(TransportMath.ValidFps(fps) == fps, $"boxed double FPS {fps} is preserved");
            Check(Near(TransportMath.ConvertFrame(fps * 2.5, fps, 30), 75),
                $"{fps}->30 frame conversion preserves seconds");
            Check(Near(TransportMath.ConvertFrame(75, 30, fps), fps * 2.5),
                $"30->{fps} frame conversion preserves seconds");
        }
        foreach(object numeric in new object[] { (byte)24, (sbyte)24, (short)24, (ushort)24,
            24, 24u, 24L, 24UL, 24f, 24m, (nint)24, (nuint)24, (Half)24 })
            Check(TransportMath.ValidFps(numeric) == 24, $"numeric FPS type {numeric.GetType().Name}");
        Check(TransportMath.ValidFps(240d) == 240 && TransportMath.ValidFps(0.5) == 0.5
            && TransportMath.ValidFps(29.97) == 29.97, "valid FPS boundaries and fractional rates");
        foreach(object? invalid in new object?[] { null, "60", true, '6', new object(), 0, -1,
            241d, double.NaN, double.PositiveInfinity, float.NegativeInfinity })
            Check(Rejects(() => TransportMath.ValidFps(invalid)), "invalid FPS throws, never silently uses 60");
        Check(Rejects(() => TransportMath.ConvertFrame(30, 0, 60))
            && Rejects(() => TransportMath.ConvertFrame(30, 30, double.NaN)),
            "frame conversion validates both FPS values");
        Check(TransportMath.PlayPosition(5, 10, 50) == 10
            && TransportMath.PlayPosition(10, 10, 50) == 10
            && TransportMath.PlayPosition(35.5, 10, 50) == 35.5
            && TransportMath.PlayPosition(50, 10, 50) == 10
            && TransportMath.PlayPosition(500, 10, 50) == 10,
            "nonzero FrameMin clamps and endpoint restarts at FrameMin");
        Check(TransportMath.PlayPosition(15, 10, 10) == 10, "zero-width frame range is stable");
        Check(Rejects(() => TransportMath.PlayPosition(0, 50, 10))
            && Rejects(() => TransportMath.PlayPosition(double.NaN, 10, 50)),
            "invalid frame ranges/positions throw");
        Check(TransportMath.ResumeSpeed(2, 3) == 2 && TransportMath.ResumeSpeed(null, 0.5f) == 0.5f,
            "resume prefers positive finite override, then effective speed");
        foreach(float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Check(TransportMath.ResumeSpeed(invalid, 0.5f) == 0.5f, "invalid override falls back to effective speed");
            Check(TransportMath.ResumeSpeed(null, invalid) == 1, "invalid effective speed resumes at one");
            Check(TransportMath.ResumeSpeed(2, invalid) == 2, "valid override wins over invalid effective speed");
        }
    }
}
