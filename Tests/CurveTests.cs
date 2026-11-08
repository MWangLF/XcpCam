using System;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class CurveTests
{
    public static void Run(Action<bool, string> check)
    {
        void Near(double actual, double expected, string name, double tolerance = 1e-6) =>
            check(Math.Abs(actual - expected) <= tolerance, name + $" ({actual:G12} vs {expected:G12})");
        CurveKey Key(int frame, double value, CurveInterpolation interpolation = CurveInterpolation.Bezier) =>
            new() { Frame = frame, Value = value, Interpolation = interpolation };
        double[] Sample(FCurve curve)
        {
            var samples = new double[961];
            for (int i = 0; i < samples.Length; i++) samples[i] = curve.Evaluate(-10 + i / 8.0);
            return samples;
        }
        void Unchanged(FCurve curve, double[] before, string name)
        {
            double maximum = 0;
            for (int i = 0; i < before.Length; i++) maximum = Math.Max(maximum, Math.Abs(before[i] - curve.Evaluate(-10 + i / 8.0)));
            check(maximum < 2e-5, name + $" (maximum error {maximum:G6})");
        }

        foreach(var rotation in new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(1.2f, -.4f, .7f) })
        {
            var eye = new Vector3(3, -2, 5);
            var center = FrustumGeometry.Point(eye, rotation, 0, 0, 2);
            check(Vector3.Distance(center, eye - Vector3.Transform(Vector3.UnitZ, rotation) * 2) < 1e-6f,
                "Frustum points toward local negative Z without moving eye");
            var upMark = FrustumGeometry.Point(eye, rotation, 0, 1, 2);
            check(Vector3.Distance(upMark - center, Vector3.Transform(Vector3.UnitY, rotation)) < 1e-6f,
                "Frustum up marker preserves camera roll");
            var view = XcpMath.BrioPlaybackView(eye, rotation, Vector3.Zero, Quaternion.Identity);
            check(Vector3.Distance(Vector3.Transform(center, view), new Vector3(0, 0, -2)) < 2e-6f,
                "Frustum center lies in front of the playback view");
        }

        var empty = new FCurve();
        Near(empty.Evaluate(0), 0, "Empty curve");
        empty.Keys.Add(Key(5, 7));
        Near(empty.Evaluate(-100), 7, "Single-key left extrapolation");
        Near(empty.Evaluate(100), 7, "Single-key right extrapolation");
        var scalar = new FCurve();
        scalar.Keys.Add(Key(20, 10));
        scalar.Keys.Add(Key(0, 0, CurveInterpolation.Linear));
        Near(scalar.Evaluate(5), 2.5, "Unsorted linear interpolation");
        bool enumerationStable = true;
        int visited = 0;
        try
        {
            foreach(var key in scalar.Keys)
            {
                scalar.Handle(key, false);
                scalar.Handle(key, true);
                scalar.Evaluate(key.Frame);
                scalar.Sort();
                visited++;
            }
        }
        catch(InvalidOperationException) { enumerationStable = false; }
        check(enumerationStable && visited == scalar.Keys.Count,
            "Graph render: reading both handles/evaluation preserves active key enumeration");
        Near(scalar.Evaluate(-1), 0, "Constant left extrapolation");
        Near(scalar.Evaluate(21), 10, "Constant right extrapolation");
        scalar.Keys[0].Interpolation = CurveInterpolation.Constant;
        Near(scalar.Evaluate(19.999), 0, "Constant interval");
        Near(scalar.Evaluate(20), 10, "Constant exact boundary");

        var inversion = new FCurve();
        var a = Key(0, 0); var b = Key(30, 9);
        a.Mode = b.Mode = HandleMode.Free;
        a.Right = new Vector2(1, 12); b.Left = new Vector2(-8, -15);
        inversion.Keys.Add(a); inversion.Keys.Add(b);
        // Independent parametric Bernstein reference, deliberately nonuniform time handles.
        for (int i = 1; i < 20; i++)
        {
            double t = i / 20.0, u = 1 - t;
            double x = 3 * u * u * t + 3 * u * t * t * 22 + t * t * t * 30;
            double y = 3 * u * u * t * 12 - 3 * u * t * t * 6 + t * t * t * 9;
            Near(inversion.Evaluate(x), y, "Nonuniform Bezier inversion " + i, 1e-8);
        }
        a.Right = new Vector2(0, 12); b.Left = new Vector2(-30, 4);
        Near(inversion.Evaluate(.03), 3 * .9 * .9 * .1 * 12 + 3 * .9 * .1 * .1 * 13 + .001 * 9,
            "Flat time derivative uses bracketed inversion", 1e-8);
        a.Right = new Vector2(100, 80); b.Left = new Vector2(-50, -20);
        var outPoint = inversion.Handle(a, true); var inPoint = inversion.Handle(b, false);
        Near(outPoint.X, 20, "Proportional outgoing time shortening");
        Near(inPoint.X, 20, "Proportional incoming time shortening");
        Near(outPoint.Y, 16, "Proportional value shortening");
        check(a.Right == new Vector2(100, 80), "Sampling leaves manual offsets untouched");
        a.Right = new Vector2(-2, 3); b.Left = new Vector2(2, -3);
        check(inversion.Handle(a, true).X == 0 && inversion.Handle(b, false).X == 30, "Invalid time directions repaired effectively");
        a.Right = new Vector2(10, 30); b.Left = new Vector2(-10, 30);
        check(inversion.Evaluate(15) > 9, "Free handles permit value overshoot");

        var monotone = new FCurve();
        monotone.Keys.Add(Key(0, 0)); monotone.Keys.Add(Key(2, 10));
        monotone.Keys.Add(Key(70, 10.1)); monotone.Keys.Add(Key(100, 4));
        monotone.RecalculateHandles();
        bool bounded = true;
        for (int segment = 0; segment < 3; segment++)
        {
            var l = monotone.Keys[segment]; var r = monotone.Keys[segment + 1];
            for (int i = 0; i <= 100; i++)
            {
                double y = monotone.Evaluate(l.Frame + (r.Frame - l.Frame) * i / 100.0);
                bounded &= y >= Math.Min(l.Value, r.Value) - 1e-6 && y <= Math.Max(l.Value, r.Value) + 1e-6;
            }
        }
        check(bounded, "AutoClamped bounds every nonuniform increasing/decreasing interval");
        Near(monotone.Keys[2].Right.Y, 0, "AutoClamped extremum tangent is zero");
        monotone.Keys[1].Mode = HandleMode.Auto;
        monotone.RecalculateHandles();
        double slope = (68 * 5 + 2 * (.1 / 68)) / 70;
        Near(monotone.Keys[1].Right.Y / (double)monotone.Keys[1].Right.X, slope, "Bessel tangent on unequal spans");
        monotone.Keys[1].Mode = HandleMode.Vector;
        monotone.RecalculateHandles();
        Near(monotone.Keys[1].Left.Y / (double)monotone.Keys[1].Left.X, 5, "Vector incoming adjacent direction");
        Near(monotone.Keys[1].Right.Y / (double)monotone.Keys[1].Right.X, .1 / 68, "Vector outgoing adjacent direction");

        var whole = new FCurve();
        whole.Keys.Add(Key(0, 0)); whole.Keys.Add(Key(20, 12));
        whole.Keys.Add(Key(50, -4)); whole.Keys.Add(Key(100, 8));
        whole.RecalculateHandles();
        // Deliberately force global effective-handle baking, not only automatic freezing.
        whole.Keys[0].Mode = HandleMode.Free;
        whole.Keys[0].Right = new Vector2(60, 22);
        var baseline = Sample(whole);
        var inserted = whole.InsertPreserving(31, Guid.NewGuid());
        check(inserted.Mode == HandleMode.Free && inserted.NodeId.HasValue, "Inserted subdivision key has free handles and node identity");
        whole.RecalculateHandles();
        Unchanged(whole, baseline, "de Casteljau preserves entire curve including adjacent constrained intervals");
        check(whole.InsertPreserving(31) == inserted && whole.Keys.Count == 5, "Same-frame insertion reuses key");
        whole.InsertPreserving(-5); whole.InsertPreserving(105);
        Unchanged(whole, baseline, "Exterior insertions preserve constant extrapolation");
        foreach (var mode in new[] { CurveInterpolation.Linear, CurveInterpolation.Constant })
        {
            var curve = new FCurve(); curve.Keys.Add(Key(0, 3, mode)); curve.Keys.Add(Key(80, 15));
            var samples = Sample(curve); curve.InsertPreserving(27); curve.RecalculateHandles();
            Unchanged(curve, samples, mode + " insertion invariance");
        }

        var clone = whole.Clone();
        check(clone.Keys != whole.Keys && clone.Keys[0] != whole.Keys[0] && clone.Keys[0].Id == whole.Keys[0].Id,
            "Clone has separate list/keys with preserved identities");
        clone.Keys[0].Value += 100;
        Unchanged(whole, baseline, "Clone edits do not leak into original curve");
        var originalNode = Guid.NewGuid(); inserted.NodeId = originalNode;
        check(whole.MoveKey(inserted, 31, 2) && inserted.NodeId == originalNode, "Value-only edit retains node link");
        check(!whole.MoveKey(inserted, 20, 999) && inserted.Frame == 31 && inserted.Value == 2 && inserted.NodeId == originalNode,
            "Duplicate move rejected atomically");
        var oldOffset = inserted.Right;
        check(whole.MoveKey(inserted, 32, 3) && inserted.NodeId == null && inserted.Right == oldOffset, "Time edit detaches node and retains offsets");
        inserted.Mode = HandleMode.Auto;
        whole.SetHandle(inserted, true, new Vector2(20, 7));
        check(inserted.Mode == HandleMode.Free && inserted.Right.X == 0 && inserted.Right.Y == 4, "Handle edit frees automatic mode and clamps direction");
        inserted.Mode = HandleMode.Aligned; inserted.Left = new Vector2(-3, -4);
        whole.SetHandle(inserted, true, new Vector2(36, 6));
        Near(inserted.Left.Length(), 5, "Aligned opposite length preserved");
        Near(inserted.Left.X, -4, "Aligned opposite rotation X");
        Near(inserted.Left.Y, -3, "Aligned opposite rotation Y");
        var leftBefore = inserted.Left; var rightBefore = inserted.Right;
        whole.RecalculateHandles();
        check(inserted.Left == leftBefore && inserted.Right == rightBefore, "Recalculation does not touch aligned handles");
        bool rejected = false;
        try { whole.SetHandle(inserted, true, new Vector2(40, float.NaN)); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected && inserted.Right == rightBefore, "Nonfinite handle rejected without mutation");

        var scale = new FCurve(); scale.Keys.Add(Key(1, 3)); scale.Keys.Add(Key(2, 4));
        rejected = false;
        try { scale.Rescale(.1); } catch (InvalidOperationException) { rejected = true; }
        check(rejected && scale.Keys.Count == 2 && scale.Keys[0].Frame == 1 && scale.Keys[1].Frame == 2 && scale.Keys[0].Left.X == -10,
            "Rescale collision throws atomically and keeps all keys");
        scale.Rescale(2.5);
        check(scale.Keys[0].Frame == 3 && scale.Keys[1].Frame == 5 && scale.Keys[0].Right.X == 25 && scale.Keys[0].Value == 3,
            "Rescale rounds integer times and scales float offset times only");
        rejected = false;
        try { scale.Rescale(double.MaxValue); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected && scale.Keys[0].Frame == 3 && scale.Keys[0].Right.X == 25, "Rescale overflow atomic");
        var independent = new FCurve(); independent.Keys.Add(Key(0, -123));
        Near(independent.Evaluate(4), -123, "Independent curve state");
        Near(empty.Evaluate(4), 7, "No shared mutable curve state");
    }
}
