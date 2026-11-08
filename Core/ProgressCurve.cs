using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Bounded, non-decreasing timing curves. Generic FCurve remains a general scalar curve.</summary>
public static class ProgressCurve
{
    public static FCurve Linear(int start, int end)
    {
        if(end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        return new FCurve { Keys = [
            new CurveKey { Frame = start, Value = 0, Interpolation = CurveInterpolation.Linear },
            new CurveKey { Frame = end, Value = 1, Interpolation = CurveInterpolation.Linear }] };
    }

    /// <summary>Pin endpoints, clamp ordered keys and bake bounded effective control polygons.
    /// Automatic modes are recalculated first; free split polygons are never refitted.</summary>
    public static void Constrain(FCurve curve, int start, int end, bool recalculate = false)
    {
        if(end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        curve.Keys.RemoveAll(k => k.Frame < start || k.Frame > end);
        curve.Sort();
        if(curve.Keys.GroupBy(k => k.Frame).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Progress key frames must be unique.");
        if(!curve.Keys.Any(k => k.Frame == start)) curve.Keys.Insert(0, new CurveKey { Frame = start, Value = 0, Interpolation = CurveInterpolation.Linear });
        if(!curve.Keys.Any(k => k.Frame == end)) curve.Keys.Add(new CurveKey { Frame = end, Value = 1, Interpolation = CurveInterpolation.Linear });
        curve.Keys[0].Value = 0;
        double previous = 0;
        foreach(var key in curve.Keys)
        {
            key.Value = double.IsFinite(key.Value) ? Math.Clamp(key.Value, previous, 1) : previous;
            previous = key.Value;
        }
        curve.Keys[^1].Value = 1;
        if(recalculate) curve.RecalculateHandles();
        // Project offsets directly in double precision. Converting through Handle's float
        // absolute coordinates loses low bits on every call and is not idempotent.
        for(int i = 0; i + 1 < curve.Keys.Count; i++)
        {
            var a = curve.Keys[i]; var b = curve.Keys[i + 1];
            double x1 = float.IsFinite(a.Right.X) ? Math.Max(0, a.Right.X) : 0;
            double x2 = float.IsFinite(b.Left.X) ? Math.Min(0, b.Left.X) : 0;
            double span = (double)b.Frame - a.Frame;
            if(x1 - x2 > span)
            {
                double scale = span / (x1 - x2); x1 *= scale; x2 *= scale;
            }
            float rx = (float)x1, lx = (float)x2;
            // Float rounding may otherwise leave a polygon infinitesimally too wide.
            if((double)rx - lx > span)
            {
                if(rx >= -lx) rx = MathF.BitDecrement(rx);
                else lx = MathF.BitIncrement(lx);
            }
            double delta = b.Value - a.Value;
            float ry = (float)Math.Clamp(float.IsFinite(a.Right.Y) ? a.Right.Y : 0, 0, delta);
            float ly = (float)Math.Clamp(float.IsFinite(b.Left.Y) ? b.Left.Y : 0, -delta, 0);
            if(a.Value + ry > b.Value + ly)
            {
                double middle = (a.Value + ry + b.Value + ly) * .5;
                ry = (float)(middle - a.Value); ly = (float)(middle - b.Value);
                while(a.Value + ry > b.Value + ly)
                {
                    if(ry > 0) ry = MathF.BitDecrement(ry);
                    else if(ly < 0) ly = MathF.BitIncrement(ly);
                    else break;
                }
            }
            a.Right = new Vector2(rx, ry); b.Left = new Vector2(lx, ly);
        }
    }

    internal static bool Same(FCurve a, FCurve b)
    {
        if(a.Keys.Count != b.Keys.Count) return false;
        for(int i = 0; i < a.Keys.Count; i++)
        {
            var x = a.Keys[i]; var y = b.Keys[i];
            if(x.Frame != y.Frame || x.Value != y.Value || x.Mode != y.Mode || x.Interpolation != y.Interpolation ||
                x.Left != y.Left || x.Right != y.Right) return false;
        }
        return true;
    }

    public static double Evaluate(FCurve curve, int start, int end, double frame)
    {
        var effective = curve.Clone();
        Constrain(effective, start, end);
        return Math.Clamp(effective.Evaluate(frame), 0, 1);
    }

    internal static FCurve Slice(FCurve curve, int start, int end, int oldStart, int oldEnd)
    {
        var result = curve.Clone();
        Constrain(result, oldStart, oldEnd);
        result.InsertPreserving(start);
        result.InsertPreserving(end);
        double a = result.Evaluate(start), b = result.Evaluate(end), span = b - a;
        result.Keys.RemoveAll(k => k.Frame < start || k.Frame > end);
        if(span <= 1e-14) return Linear(start, end);
        foreach(var k in result.Keys)
        {
            k.Value = Math.Clamp((k.Value - a) / span, 0, 1);
            k.Left = new Vector2(k.Left.X, (float)(k.Left.Y / span));
            k.Right = new Vector2(k.Right.X, (float)(k.Right.Y / span));
            k.Mode = HandleMode.Free;
        }
        result.Keys[0].Value = 0; result.Keys[^1].Value = 1;
        return result;
    }

    internal static int Round(double value)
    {
        value = Math.Round(value, MidpointRounding.AwayFromZero);
        if(!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (int)value;
    }
}
