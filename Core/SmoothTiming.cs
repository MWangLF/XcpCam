using System;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Shot-wide timed PCHIP speeds, stored as editable interval Bezier polygons.</summary>
public static class SmoothTiming
{
    public static double Distance(MotionInterval interval)
    {
        if(interval.FollowDistance is double follow) return follow;
        if(interval.Path == null || !interval.Ranges.TryGetValue(CameraChannel.Position, out var range)) return 0;
        return Math.Max(0, interval.Path.Distance(range.end) - interval.Path.Distance(range.start));
    }

    public static void Apply(ShotCurves shot, bool reset = false)
    {
        var legs = shot.Intervals;
        int n = legs.Count;
        if(n == 0) return;
        var distance = legs.Select(Distance).ToArray();
        var duration = legs.Select(i => (double)Math.Max(0, i.End - i.Start - i.HoldFrames)).ToArray();
        var secant = Enumerable.Range(0, n).Select(i => duration[i] > 0 ? distance[i] / duration[i] : 0).ToArray();
        var speed = new double[n + 1]; // old Timed defaults ease only at whole-shot endpoints
        for(int i = 1; i < n; i++)
        {
            if(ConstantCuts.IsCut(shot, legs[i - 1]) || ConstantCuts.IsCut(shot, legs[i]) ||
                secant[i - 1] <= 1e-12 || secant[i] <= 1e-12 || legs[i].HoldFrames > 0) continue;
            double a = 2 * duration[i] + duration[i - 1], b = duration[i] + 2 * duration[i - 1];
            speed[i] = (a + b) / (a / secant[i - 1] + b / secant[i]);
        }
        // Limit shared physical knot speeds, never each leg independently: continuity survives
        // even when the monotone control-polygon bound is active on extreme duration ratios.
        for(int i = 0; i < n; i++)
        {
            if(distance[i] <= 1e-12 || duration[i] <= 0) continue;
            double ratio = (speed[i] + speed[i + 1]) * duration[i] / distance[i];
            if(ratio > 3) { double scale = 3 / ratio; speed[i] *= scale; speed[i + 1] *= scale; }
        }
        for(int i = 0; i < n; i++)
        {
            var leg = legs[i];
            if(ConstantCuts.IsCut(shot, leg)) continue;
            if(leg.AutoTiming && leg.AutoSnapshot != null && Enum.GetValues<CameraChannel>().Any(c =>
                !ProgressCurve.Same(leg.Curve(c), leg.AutoSnapshot[c]))) leg.MarkManual();
            if(!reset && !leg.AutoTiming) continue;
            double h = leg.End - leg.Start;
            double outgoing = leg.HoldFrames > 0 ? 0 : speed[i], incoming = speed[i + 1];
            double a = distance[i] > 1e-12 ? outgoing * duration[i] / distance[i] : 0;
            double b = distance[i] > 1e-12 ? incoming * duration[i] / distance[i] : 0;
            a = Math.Clamp(a, 0, 3); b = Math.Clamp(b, 0, 3);
            // The weighted harmonic default has a+b <= 3, hence an ordered control polygon.
            if(a + b > 3) { double scale = 3 / (a + b); a *= scale; b *= scale; }
            FCurve Make() => new() { Keys = [
                new CurveKey { Frame = leg.Start, Value = 0, NodeId = leg.StartNodeId,
                    Mode = HandleMode.Free, Interpolation = CurveInterpolation.Bezier,
                    Right = new Vector2((float)(h / 3), (float)(a / 3)) },
                new CurveKey { Frame = leg.End, Value = 1, NodeId = leg.EndNodeId,
                    Mode = HandleMode.Free, Interpolation = CurveInterpolation.Bezier,
                    Left = new Vector2((float)(-h / 3), (float)(-b / 3)) }] };
            if(!reset && leg.AutoSnapshot != null && Enum.GetValues<CameraChannel>().All(c => ProgressCurve.Same(leg.Curve(c), Make())))
                continue;
            if(leg.Linked)
            {
                var curve = Make();
                foreach(var channel in Enum.GetValues<CameraChannel>()) leg.Channels[channel] = curve;
            }
            else foreach(var channel in Enum.GetValues<CameraChannel>()) leg.Channels[channel] = Make();
            leg.splitTiming = null; leg.splitCurves = null; leg.splitValues = null;
            leg.AutoTiming = true;
            leg.AutoSnapshot = leg.Channels.ToDictionary(p => p.Key, p => p.Value.Clone());
        }
    }
}
