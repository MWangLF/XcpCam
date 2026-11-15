using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class CurveObjectTests
{
    public static void Run(Action<bool,string> check)
    {
        // ---- Blender primitives ----
        var circle = CurveEdit.Create(CurvePrimitive.Circle, Vector3.Zero).Splines[0];
        float handle = Vector3.Distance(circle.Points[0].Co, circle.Points[0].Right);
        check(Math.Abs(handle - .5523f) < 2e-3f, $"Curve: Bezier circle Auto handles have Blender length 0.552 ({handle:0.0000})");
        float worst = 0;
        for(double t = 0; t < 4; t += .01) worst = MathF.Max(worst, MathF.Abs(circle.Evaluate(t).Length() - 1));
        check(worst < 3e-3f && Vector3.Distance(circle.Evaluate(0), circle.Evaluate(4)) < 1e-5f, $"Curve: Bezier circle is round and closed (max radius error {worst:0.0000})");
        check(circle.Points.All(p => Math.Abs(p.Co.Y) < 1e-6f), "Curve: primitives lie in the game ground plane (XZ)");

        var bez = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero).Splines[0];
        bool colinear = bez.Points.All(p => Vector3.Cross(Vector3.Normalize(p.Co - p.Left), Vector3.Normalize(p.Right - p.Co)).Length() < 1e-5f);
        check(bez.Points.Count == 2 && colinear && Vector3.Distance(bez.Evaluate(0), CurveEdit.FromBlender(-1, 0)) < 1e-6f
            && Vector3.Distance(bez.Evaluate(1), CurveEdit.FromBlender(1, 0)) < 1e-6f, "Curve: Bezier primitive matches Blender's Aligned 2-point curve");

        var path = CurveEdit.Create(CurvePrimitive.Path, Vector3.Zero).Splines[0];
        var (ps, pe) = path.Domain();
        check(path.Type == SplineType.Nurbs && path.Order == 5 && path.Points.Count == 5 && path.Endpoint, "Curve: Path primitive is a 5-point order-5 endpoint NURBS");
        check(Vector3.Distance(path.Evaluate(ps), path.Points[0].Co) < 1e-5f && Vector3.Distance(path.Evaluate(pe), path.Points[^1].Co) < 1e-5f,
            "Curve: endpoint NURBS reaches its end control points");
        check(Vector3.Distance(path.Evaluate((ps + pe) / 2), Vector3.Zero) < 1e-5f, "Curve: straight NURBS path is symmetric about its middle");

        // NURBS partition of unity and rational weights
        var same = new Spline { Type = SplineType.Nurbs, Order = 4 };
        for(int i = 0; i < 6; i++) same.Points.Add(new CurvePoint { Co = new Vector3(2, 3, 4), W = 1 + i * .3f });
        var (ss, se) = same.Domain();
        check(Enumerable.Range(0, 21).All(i => Vector3.Distance(same.Evaluate(ss + (se - ss) * i / 20), new Vector3(2, 3, 4)) < 1e-5f),
            "Curve: NURBS basis is a partition of unity (weighted)");
        var ring = new Spline { Type = SplineType.Nurbs, Order = 4, Cyclic = true };
        for(int i = 0; i < 6; i++) ring.Points.Add(new CurvePoint { Co = new Vector3(MathF.Cos(i * MathF.PI / 3), 0, MathF.Sin(i * MathF.PI / 3)) });
        var (rs, re) = ring.Domain();
        check(Vector3.Distance(ring.Evaluate(rs), ring.Evaluate(re)) < 1e-5f, "Curve: cyclic NURBS closes");

        // ---- handle rules ----
        var v = new Spline();
        foreach(var x in new[] { 0f, 3f, 9f }) v.Points.Add(new CurvePoint { Co = new Vector3(x, x * .5f, 0), LeftType = CurveHandle.Vector, RightType = CurveHandle.Vector });
        v.CalcHandles();
        check(Vector3.Distance(v.Points[1].Right, v.Points[1].Co + (v.Points[2].Co - v.Points[1].Co) / 3) < 1e-5f
            && Vector3.Distance(v.Points[1].Left, v.Points[1].Co - (v.Points[1].Co - v.Points[0].Co) / 3) < 1e-5f, "Curve: Vector handles point at 1/3 of the neighbour chord");

        var al = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero).Splines[0];
        float rightLen = Vector3.Distance(al.Points[0].Co, al.Points[0].Right);
        CurveEdit.MoveHandle(al, 0, true, al.Points[0].Co + new Vector3(0, 2, 0));
        var p0 = al.Points[0];
        check(Vector3.Cross(Vector3.Normalize(p0.Co - p0.Left), Vector3.Normalize(p0.Right - p0.Co)).Length() < 1e-5f
            && Math.Abs(Vector3.Distance(p0.Co, p0.Right) - rightLen) < 1e-5f, "Curve: Aligned partner follows the grabbed handle and keeps its length");

        var auto = CurveEdit.Create(CurvePrimitive.Circle, Vector3.Zero).Splines[0];
        CurveEdit.MoveHandle(auto, 1, false, auto.Points[1].Right + new Vector3(.3f, .2f, 0));
        check(auto.Points[1].LeftType == CurveHandle.Aligned && auto.Points[1].RightType == CurveHandle.Aligned,
            "Curve: grabbing an Auto handle turns the pair Aligned (Blender handle test)");

        var moved = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero).Splines[0];
        var offset = moved.Points[1].Right - moved.Points[1].Co;
        CurveEdit.MovePoint(moved, 1, moved.Points[1].Co + new Vector3(1, 2, 3));
        check(Vector3.Distance(moved.Points[1].Right - moved.Points[1].Co, offset) < 1e-5f, "Curve: moving a point carries its handles");

        // ---- topology operators ----
        var sub = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero).Splines[0];
        var before = sub.Clone();
        var created = CurveEdit.Subdivide(sub, new HashSet<int> { 0, 1 });
        float dev = 0;
        for(double u = 0; u <= 1; u += .01)
        {
            double t = u < .5 ? u * 2 : 1 + (u * 2 - 1);
            dev = MathF.Max(dev, Vector3.Distance(before.Evaluate(u), sub.Evaluate(t)));
        }
        check(created.SequenceEqual(new[] { 1 }) && sub.Points.Count == 3 && dev < 1e-4f, $"Curve: Subdivide splits exactly (max deviation {dev:0.0e0})");

        var multi = CurveEdit.Create(CurvePrimitive.Circle, Vector3.Zero).Splines[0];
        var made = CurveEdit.Subdivide(multi, new HashSet<int> { 0, 1, 2, 3 });
        check(multi.Points.Count == 8 && made.SequenceEqual(new[] { 1, 3, 5, 7 }), $"Curve: Subdivide of a closed loop cuts every segment ({string.Join(",", made)})");

        var ext = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero).Splines[0];
        int added = CurveEdit.Extrude(ext, 1);
        check(added == 2 && ext.Points.Count == 3 && Vector3.Distance(ext.Points[2].Co, CurveEdit.FromBlender(3, 0)) < 1e-5f, "Curve: Extrude adds a point past the open end");
        check(CurveEdit.Extrude(ext, 1) == -1 && CurveEdit.Extrude(CurveEdit.Create(CurvePrimitive.Circle, Vector3.Zero).Splines[0], 0) == -1,
            "Curve: Extrude only works on open ends");
        int front = CurveEdit.Extrude(ext, 0);
        check(front == 0 && ext.Points.Count == 4 && Vector3.Distance(ext.Points[0].Co, CurveEdit.FromBlender(-3, 0)) < 1e-5f, "Curve: Extrude from the start prepends");

        var del = CurveEdit.Create(CurvePrimitive.Circle, Vector3.Zero).Splines[0];
        bool keep = CurveEdit.Delete(del, new HashSet<int> { 0, 2 });
        check(keep && del.Points.Count == 2 && !del.Cyclic, "Curve: Delete removes points (a 2-point loop opens)");
        check(!CurveEdit.Delete(del, new HashSet<int> { 0, 1 }), "Curve: deleting every point empties the spline");

        var cyc = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero).Splines[0];
        CurveEdit.ToggleCyclic(cyc);
        check(cyc.Cyclic && cyc.SegmentCount == 2 && Vector3.Distance(cyc.Evaluate(0), cyc.Evaluate(2)) < 1e-5f, "Curve: Toggle Cyclic closes the spline");

        var dir = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero).Splines[0];
        dir.Points[0].Tilt = .4f;
        var orig = dir.Clone();
        CurveEdit.SwitchDirection(dir);
        float rev = 0;
        for(double t = 0; t <= 1; t += .02) rev = MathF.Max(rev, Vector3.Distance(orig.Evaluate(t), dir.Evaluate(1 - t)));
        check(rev < 1e-5f && Math.Abs(dir.Points[1].Tilt + .4f) < 1e-6f, "Curve: Switch Direction reverses the shape and negates tilt");

        // ---- arc length / Follow Path sampling ----
        var straight = CurveEdit.Create(CurvePrimitive.Path, new Vector3(10, 0, 0));
        var cp = CurvePath.Create(straight)!;
        check(Math.Abs(cp.Length - 4) < 1e-3f && Vector3.Distance(cp.Position(.25), new Vector3(9, 0, 0)) < 2e-3f,
            $"Curve path: length factor is arc length (length {cp.Length:0.0000})");
        bool mono = true; double last = -1;
        for(double f = 0; f <= 1; f += .01) { double t = cp.Parameter(f); mono &= t >= last; last = t; }
        check(mono && Math.Abs(cp.Factor(cp.Parameter(.37)) - .37) < 1e-6, "Curve path: factor ↔ parameter is monotone and invertible");
        var sharp = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero);
        var sp = CurvePath.Create(sharp)!;
        var probe = sp.Position(.61) + new Vector3(0, .2f, 0);
        check(Math.Abs(sp.Project(probe) - .61) < 2e-3, "Curve path: projection finds the nearest length factor");
        var loop = CurvePath.Create(CurveEdit.Create(CurvePrimitive.Circle, Vector3.Zero))!;
        check(Vector3.Distance(loop.Position(1.25), loop.Position(.25)) < 1e-4f && Vector3.Distance(cp.Position(1.5), cp.Position(1)) < 1e-6f,
            "Curve path: cyclic paths wrap, open paths clamp");

        var mid = cp.Sample(.5);
        var q = CurvePath.Frame(mid, -Vector3.UnitZ, Vector3.UnitY, Vector3.UnitY);
        check(Vector3.Distance(Vector3.Transform(-Vector3.UnitZ, q), mid.Tangent) < 1e-4f && Vector3.Transform(Vector3.UnitY, q).Y > .999f,
            "Follow curve: forward axis follows the tangent, up stays up");
        var tilted = CurvePath.Frame(mid with { Tilt = MathF.PI / 2 }, -Vector3.UnitZ, Vector3.UnitY, Vector3.UnitY);
        check(MathF.Abs(Vector3.Transform(Vector3.UnitY, tilted).Y) < 1e-4f, "Follow curve: tilt rolls around the tangent");

        var xf = CurveEdit.Create(CurvePrimitive.Circle, new Vector3(5, 1, -2), 2);
        xf.Rotation = Quaternion.CreateFromYawPitchRoll(.7f, .2f, -.1f);
        var lp = new Vector3(.3f, -.4f, 1.1f);
        check(Vector3.Distance(xf.ToLocal(xf.ToWorld(lp)), lp) < 1e-5f && Math.Abs(CurvePath.Create(xf)!.Length - 2 * MathF.PI * 2) < .05f,
            "Curve object: transform round-trips and scales the path");
    }
}
