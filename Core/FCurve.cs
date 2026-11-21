using System;
using System.Collections.Generic;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Independent scalar animation curve. Call RecalculateHandles after edits to automatic keys.</summary>
public class FCurve
{
    public List<CurveKey> Keys = new();

    public FCurve Clone()
    {
        var copy = new FCurve();
        foreach (var key in Keys) copy.Keys.Add(key.Clone());
        return copy;
    }

    public void Sort()
    {
        // Read paths also call Sort. Do not invalidate active enumerators when already ordered.
        for(int i = 1; i < Keys.Count; i++)
            if(Keys[i - 1].Time > Keys[i].Time)
            {
                Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                return;
            }
    }

    // Double control points avoid losing precision by adding float offsets to large key times.
    private readonly record struct Point(double X, double Y)
    {
        public static Point Lerp(Point a, Point b, double t) =>
            new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    private static Point Position(CurveKey key) => new(key.Time, key.Value);
    private static Point Offset(CurveKey key, bool right)
    {
        var v = right ? key.Right : key.Left;
        // Sampling repairs direction only, never stored manual handles. Invalid public-field
        // values cannot be allowed to poison the inversion or graph geometry.
        double x = float.IsFinite(v.X) ? v.X : 0;
        double y = float.IsFinite(v.Y) ? v.Y : 0;
        return new(right ? Math.Max(0, x) : Math.Min(0, x), y);
    }

    private (Point Out, Point In) SegmentHandles(int i)
    {
        var a = Keys[i];
        var b = Keys[i + 1];
        var outgoing = Offset(a, true);
        var incoming = Offset(b, false);
        double span = (double)b.Time - a.Time;
        double reach = outgoing.X - incoming.X;
        // Shorten both complete vectors by the same ratio, retaining their slopes.
        double scale = reach > span && reach > 0 ? Math.Max(0, span) / reach : 1;
        return (new(a.Time + outgoing.X * scale, a.Value + outgoing.Y * scale),
                new(b.Time + incoming.X * scale, b.Value + incoming.Y * scale));
    }

    private Point Effective(int i, bool right)
    {
        if (right && i + 1 < Keys.Count) return SegmentHandles(i).Out;
        if (!right && i > 0) return SegmentHandles(i - 1).In;
        var p = Position(Keys[i]);
        var v = Offset(Keys[i], right);
        return new(p.X + v.X, p.Y + v.Y);
    }

    /// <summary>Absolute effective control point, identical to the evaluator's control polygon.</summary>
    public Vector2 Handle(CurveKey key, bool right)
    {
        Sort();
        int i = Keys.IndexOf(key);
        if (i < 0) throw new ArgumentException("Key does not belong to this curve.", nameof(key));
        var p = Effective(i, right);
        return new Vector2((float)p.X, (float)p.Y);
    }

    private static Point Cubic(Point a, Point b, Point c, Point d, double t)
    {
        var ab = Point.Lerp(a, b, t);
        var bc = Point.Lerp(b, c, t);
        var cd = Point.Lerp(c, d, t);
        return Point.Lerp(Point.Lerp(ab, bc, t), Point.Lerp(bc, cd, t), t);
    }

    private static double Parameter(Point a, Point b, Point c, Point d, double frame)
    {
        double lo = 0, hi = 1;
        double t = Math.Clamp((frame - a.X) / (d.X - a.X), 0, 1);
        for (int n = 0; n < 80; n++)
        {
            double error = Cubic(a, b, c, d, t).X - frame;
            if (Math.Abs(error) <= 1e-12 * Math.Max(1, d.X - a.X)) return t;
            if (error > 0) hi = t; else lo = t;
            double u = 1 - t;
            double derivative = 3 * (u * u * (b.X - a.X) +
                2 * u * t * (c.X - b.X) + t * t * (d.X - c.X));
            double next = derivative > 1e-15 ? t - error / derivative : double.NaN;
            t = double.IsFinite(next) && next > lo && next < hi ? next : (lo + hi) * .5;
        }
        return (lo + hi) * .5;
    }

    public double Evaluate(double frame)
    {
        if (double.IsNaN(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        Sort();
        return EvaluateOrdered(frame);
    }

    internal double EvaluateOrdered(double frame)
    {
        if (Keys.Count == 0) return 0;
        if (frame <= Keys[0].Time) return Keys[0].Value;
        if (frame >= Keys[^1].Time) return Keys[^1].Value;
        int lo = 0, hi = Keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (Keys[mid].Time <= frame) lo = mid; else hi = mid;
        }
        var a = Keys[lo];
        var b = Keys[hi];
        if (frame == a.Time) return a.Value;
        if (a.Interpolation == CurveInterpolation.Constant) return a.Value;
        if (a.Interpolation == CurveInterpolation.Linear)
            return a.Value + (b.Value - a.Value) * ((frame - a.Time) / ((double)b.Time - a.Time));
        var controls = SegmentHandles(lo);
        var p = Position(a);
        var q = Position(b);
        return Cubic(p, controls.Out, controls.In, q,
            Parameter(p, controls.Out, controls.In, q, frame)).Y;
    }

    private static Vector2 ToOffset(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(y), "Handle is outside the finite Vector2 range.");
        return new Vector2((float)x, (float)y);
    }

    /// <summary>Converts a linear segment to a Bézier segment with the same shape and timing.</summary>
    public void SetInterpolation(CurveKey key, CurveInterpolation interpolation)
    {
        Sort();
        SetInterpolationAt(Keys.IndexOf(key), interpolation);
    }

    public void MakeLinearSegmentsEditable()
    {
        Sort();
        for(int i = 0; i < Keys.Count; i++)
            if(Keys[i].Interpolation == CurveInterpolation.Linear) SetInterpolationAt(i, CurveInterpolation.Bezier);
    }

    private void SetInterpolationAt(int index, CurveInterpolation interpolation)
    {
        var key = Keys[index];
        if(interpolation == CurveInterpolation.Bezier && key.Interpolation == CurveInterpolation.Linear && index + 1 < Keys.Count)
        {
            var next = Keys[index + 1];
            var third = ToOffset((next.Time - key.Time) / 3, (next.Value - key.Value) / 3);
            key.Right = third;
            next.Left = -third;
            key.Mode = next.Mode = HandleMode.Free;
            key.AutomaticTangent = next.AutomaticTangent = false;
        }
        key.Interpolation = interpolation;
    }

    public void RecalculateHandles(ISet<Guid>? selected = null)
    {
        Sort();
        for (int i = 0; i < Keys.Count; i++)
        {
            var key = Keys[i];
            if (key.Mode is HandleMode.Free or HandleMode.Aligned || selected != null && !selected.Contains(key.Id)) continue;
            if (Keys.Count == 1)
            {
                key.Left = new Vector2(-10, 0);
                key.Right = new Vector2(10, 0);
                continue;
            }
            double h0 = i > 0 ? (double)key.Time - Keys[i - 1].Time : (double)Keys[1].Time - key.Time;
            double h1 = i + 1 < Keys.Count ? (double)Keys[i + 1].Time - key.Time : h0;
            if (h0 <= 0 || h1 <= 0) throw new InvalidOperationException("Curve key frames must be unique.");
            double d0 = i > 0 ? (key.Value - Keys[i - 1].Value) / h0 : (Keys[1].Value - key.Value) / h1;
            double d1 = i + 1 < Keys.Count ? (Keys[i + 1].Value - key.Value) / h1 : d0;
            double tangent;
            if (key.Mode == HandleMode.AutoClamped)
            {
                // Shape-preserving, nonuniform cubic Hermite derivative (weighted harmonic mean).
                if (d0 == 0 || d1 == 0 || Math.Sign(d0) != Math.Sign(d1)) tangent = 0;
                else
                {
                    double w0 = 2 * h1 + h0, w1 = h1 + 2 * h0;
                    tangent = (w0 + w1) / (w0 / d0 + w1 / d1);
                }
            }
            else tangent = (h1 * d0 + h0 * d1) / (h0 + h1); // Bessel derivative
            key.Left = ToOffset(-h0 / 3, -h0 / 3 * (key.Mode == HandleMode.Vector ? d0 : tangent));
            key.Right = ToOffset(h1 / 3, h1 / 3 * (key.Mode == HandleMode.Vector ? d1 : tangent));
        }
    }

    /// <summary>Subdivide without refitting. All automatic keys are frozen to preserve neighboring intervals.</summary>
    public CurveKey InsertPreserving(double frame, Guid? nodeId = null)
    {
        Sort();
        foreach (var existing in Keys)
            if (existing.Time == frame)
            {
                if (nodeId.HasValue) existing.NodeId = nodeId;
                return existing;
            }
        double value = Evaluate(frame);
        int index = Keys.FindIndex(k => k.Time > frame);
        if (index < 0) index = Keys.Count;
        var inserted = new CurveKey { Value = value, NodeId = nodeId, Mode = HandleMode.Free };
        inserted.SetTime(frame);

        Point p = default, b = default, c = default, q = default;
        double t = 0;
        bool split = index > 0 && index < Keys.Count;
        if (split)
        {
            inserted.Interpolation = Keys[index - 1].Interpolation;
            p = Position(Keys[index - 1]);
            q = Position(Keys[index]);
            (b, c) = SegmentHandles(index - 1);
            if (inserted.Interpolation == CurveInterpolation.Bezier) t = Parameter(p, b, c, q, frame);
        }
        // Snapshot the entire effective polygon BEFORE changing any offsets. Even free
        // controls need baking: otherwise removing a pair constraint can lengthen a neighbor.
        var left = new Vector2[Keys.Count];
        var right = new Vector2[Keys.Count];
        for (int i = 0; i < Keys.Count; i++)
        {
            var l = Effective(i, false);
            var r = Effective(i, true);
            left[i] = ToOffset(l.X - Keys[i].Time, l.Y - Keys[i].Value);
            right[i] = ToOffset(r.X - Keys[i].Time, r.Y - Keys[i].Value);
        }
        for (int i = 0; i < Keys.Count; i++)
        {
            Keys[i].Left = left[i];
            Keys[i].Right = right[i];
            if (Keys[i].Mode is HandleMode.Auto or HandleMode.AutoClamped or HandleMode.Vector)
                Keys[i].Mode = HandleMode.Free;
        }
        if (split && inserted.Interpolation == CurveInterpolation.Bezier)
        {
            var ab = Point.Lerp(p, b, t);
            var bc = Point.Lerp(b, c, t);
            var cd = Point.Lerp(c, q, t);
            var abc = Point.Lerp(ab, bc, t);
            var bcd = Point.Lerp(bc, cd, t);
            var s = Point.Lerp(abc, bcd, t);
            inserted.Value = s.Y;
            Keys[index - 1].Right = ToOffset(ab.X - p.X, ab.Y - p.Y);
            Keys[index].Left = ToOffset(cd.X - q.X, cd.Y - q.Y);
            inserted.Left = ToOffset(abc.X - frame, abc.Y - s.Y);
            inserted.Right = ToOffset(bcd.X - frame, bcd.Y - s.Y);
        }
        else if (!split && Keys.Count > 0)
        {
            // Extrapolation was constant; the new exterior interval must remain constant.
            if (index == 0) inserted.Interpolation = CurveInterpolation.Constant;
            else Keys[^1].Interpolation = CurveInterpolation.Constant;
        }
        Keys.Insert(index, inserted);
        return inserted;
    }

    public bool MoveKey(CurveKey key, double frame, double value)
    {
        if (!Keys.Contains(key)) throw new ArgumentException("Key does not belong to this curve.", nameof(key));
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        foreach (var other in Keys) if (!ReferenceEquals(other, key) && other.Time == frame) return false;
        if (key.Time != frame) key.NodeId = null;
        key.SetTime(frame);
        key.Value = value;
        Sort();
        return true;
    }

    public void SetHandle(CurveKey key, bool right, Vector2 absolute)
    {
        if (!Keys.Contains(key)) throw new ArgumentException("Key does not belong to this curve.", nameof(key));
        if (!float.IsFinite(absolute.X) || !float.IsFinite(absolute.Y)) throw new ArgumentOutOfRangeException(nameof(absolute));
        var v = ToOffset(right ? Math.Max(0, (double)absolute.X - key.Time) : Math.Min(0, (double)absolute.X - key.Time),
            (double)absolute.Y - key.Value);
        key.AutomaticTangent = false;
        Vector2 opposite = right ? key.Left : key.Right;
        if (key.Mode == HandleMode.Aligned)
        {
            double length = Math.Sqrt((double)opposite.X * opposite.X + (double)opposite.Y * opposite.Y);
            double movedLength = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y);
            if (movedLength > 0) opposite = ToOffset(-v.X / movedLength * length, -v.Y / movedLength * length);
        }
        else if (key.Mode is HandleMode.Auto or HandleMode.AutoClamped or HandleMode.Vector) key.Mode = HandleMode.Free;
        if (right) { key.Right = v; key.Left = opposite; }
        else { key.Left = v; key.Right = opposite; }
    }

    /// <summary>Positive time scaling about frame zero, rounded away from zero. Failure is atomic.</summary>
    public void Rescale(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        var frames = new int[Keys.Count];
        var left = new Vector2[Keys.Count];
        var right = new Vector2[Keys.Count];
        var seen = new HashSet<int>();
        for (int i = 0; i < Keys.Count; i++)
        {
            double time = Math.Round(Keys[i].Time * factor, MidpointRounding.AwayFromZero);
            if (!double.IsFinite(time) || time < int.MinValue || time > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(factor));
            frames[i] = (int)time;
            if (!seen.Add(frames[i])) throw new InvalidOperationException("Time scaling would create duplicate key frames.");
            left[i] = ToOffset(Keys[i].Left.X * factor, Keys[i].Left.Y);
            right[i] = ToOffset(Keys[i].Right.X * factor, Keys[i].Right.Y);
        }
        for (int i = 0; i < Keys.Count; i++)
        {
            Keys[i].SetTime(frames[i]);
            Keys[i].Left = left[i];
            Keys[i].Right = right[i];
        }
        Sort();
    }
}
