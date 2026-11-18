using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class FollowPathTests
{
    public static void Run(Action<bool,string> check)
    {
        NodePathHandles(check);
        var tl = new CamTimeline { Length = 300 };
        var yaw = Quaternion.CreateFromYawPitchRoll(.4f, 0, 0);
        foreach(var (f, x) in new[] { (0, 8.2f), (60, 10f), (120, 11.8f) })
            tl.Keys.Add(new Keyframe { Frame = f, Pos = new(x, 1, .5f), Rot = yaw, Fov = .8f });
        CurveModel.Ensure(tl.ActiveTrack, 30);
        // straight NURBS path from x=8 to x=12 on the ground
        var curve = CurveEdit.Create(CurvePrimitive.Path, new Vector3(10, 0, 0));
        tl.Curves.Add(curve);
        tl.SetFollowPath(tl.ActiveTrack, curve);
        var shot = tl.ActiveTrack.Shots[0];
        var factors = tl.Keys.Select(k => shot.NodeFactor(k) ?? -1).ToArray();
        check(Math.Abs(factors[0]) < 1e-9 && Math.Abs(factors[1] - .5) < 1e-9 && Math.Abs(factors[2] - 1) < 1e-9,
            $"Follow Path: first node 0 %, last node 100 %, others spread by frame ({string.Join(", ", factors.Select(f => f.ToString("0.000")))})");
        var comp = ShotComposer.Create(tl, new PathSettings())!;
        check(Vector3.Distance(comp.Sample(60).Pos, new Vector3(10, 0, 0)) < 2e-3f, "Follow Path: node frame lands on the curve point");
        check(Enumerable.Range(0, 121).All(f => { var p = comp.Sample(f).Pos; return Math.Abs(p.Y) < 1e-4f && Math.Abs(p.Z) < 1e-4f && p.X >= 7.999f && p.X <= 12.001f; }),
            "Follow Path: the camera stays on the curve between nodes");
        bool forward = true; float last = -1;
        for(int f = 0; f <= 120; f++) { float x = comp.Sample(f).Pos.X; forward &= x >= last - 1e-5f; last = x; }
        check(forward, "Follow Path: default timing moves monotonically along the curve");
        check(Math.Abs(Math.Abs(Quaternion.Dot(comp.Sample(30).Rot, yaw)) - 1) < 1e-4, "Follow Path: rotation stays on the camera nodes (Follow Curve off)");

        // capturing on a following track: rotation / FoV from the camera, position ignored
        var roll = Quaternion.CreateFromYawPitchRoll(-1f, .3f, .2f);
        shot.WritePose(tl.Keys[1], new XcpFrame(new Vector3(50, 9, -30), roll, .5f), false, true, true);
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        var captured = shot.NodePose(tl.Keys[1]);
        check(Math.Abs(shot.NodeFactor(tl.Keys[1])!.Value - .5) < 1e-9 && Vector3.Distance(captured.Pos, new Vector3(10, 0, 0)) < 2e-3f
            && Math.Abs(Math.Abs(Quaternion.Dot(captured.Rot, roll)) - 1) < 1e-4 && Math.Abs(captured.Fov - .5f) < 1e-4,
            "Follow Path: capture sets rotation and FoV only; the player camera position is ignored");
        var added = new Keyframe { Frame = 240, Pos = new(-40, 5, 70), Rot = yaw, Fov = .8f };
        tl.Keys.Add(added); CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        var spread = tl.Keys.OrderBy(k => k.Frame).Select(k => shot.NodeFactor(k)!.Value).ToArray();
        check(spread.Zip(new[] { 0, .25, .5, 1 }).All(p => Math.Abs(p.First - p.Second) < 1e-9)
            && Vector3.Distance(shot.NodePose(added).Pos, new Vector3(12, 0, 0)) < 2e-3f,
            "Follow Path: a new end node becomes 100 % and the others re-spread by frame");
        tl.Keys.Remove(added); shot.WritePose(tl.Keys[1], new XcpFrame(Vector3.Zero, yaw, .8f), false, true, true);
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];

        // Follow Curve: the camera's -Z (view direction) follows the tangent
        // (the camera's own rotation is identity; Follow Curve then applies the path orientation on top)
        foreach(var k in tl.Keys) shot.WritePose(k, new XcpFrame(shot.NodePose(k).Pos, Quaternion.Identity, k.Fov), false, true, false);
        tl.ActiveTrack.FollowPath!.FollowCurve = true;
        CurveModel.Ensure(tl.ActiveTrack, 30); comp = ShotComposer.Create(tl, new PathSettings())!;
        var look = Vector3.Transform(-Vector3.UnitZ, comp.Sample(60).Rot);
        check(Vector3.Distance(look, Vector3.UnitX) < 1e-3f, $"Follow Curve: camera looks along the curve tangent ({look})");
        tl.ActiveTrack.FollowPath!.FollowCurve = false;
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];

        // moving a following node slides it along the curve; what the curve cannot reach becomes its offset
        var mid = tl.Keys[1];
        shot.WritePose(mid, new XcpFrame(new Vector3(10.5f, 3, 0), mid.Rot, mid.Fov), true, false, false);
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        check(Math.Abs((shot.NodeFactor(mid) ?? 0) - .625) < 2e-3 && Vector3.Distance(shot.NodePose(mid).Pos, new Vector3(10.5f, 3, 0)) < 2e-3f
            && Math.Abs(shot.Offset[1].Keys.First(k => k.NodeId == mid.Id).Value - 3) < 2e-3,
            "Follow Path: dragging a node slides it along the curve and keeps the height as its location offset");
        shot.Offset[1].Keys.First(k => k.NodeId == mid.Id).Value = 0;
        var late = new Keyframe { Frame = 180, Pos = Vector3.Zero, Rot = yaw, Fov = .8f };
        tl.Keys.Add(late); CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        double kept = shot.NodeFactor(mid)!.Value;
        check(Math.Abs(kept - .625) < 2e-3 && Math.Abs(shot.NodeFactor(tl.Keys[2])!.Value - (kept + (1 - kept) / 2)) < 1e-9,
            "Follow Path: hand-set positions are kept; automatic nodes spread between them");
        tl.Keys.Remove(late); CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];

        // location offset on top of the path point
        shot.Offset[1].Keys.First(k => k.NodeId == mid.Id).Value = 2;
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        check(Vector3.Distance(shot.NodePose(mid).Pos, new Vector3(10.5f, 2, 0)) < 2e-3f, "Follow Path: location offset is added to the path point");
        shot.Offset[1].Keys.First(k => k.NodeId == mid.Id).Value = 0;

        // timing uses the curve length, and the curve can be edited afterwards
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        check(Math.Abs(SmoothTiming.Distance(shot.Intervals[0]) - .625 * 4) < 1e-2, "Follow Path: timing distance is measured along the curve");
        tl.Snapshot();
        curve.Location += new Vector3(0, 0, 2); curve.Touch();
        CurveModel.Ensure(tl.ActiveTrack, 30); comp = ShotComposer.Create(tl, new PathSettings())!;
        check(Math.Abs(comp.Sample(60).Pos.Z - 2) < 1e-3f, "Follow Path: editing the curve moves the following camera");
        tl.Undo();
        comp = ShotComposer.Create(tl, new PathSettings())!;
        check(tl.Curves.Count == 1 && tl.ActiveTrack.FollowCurve == tl.Curves[0] && Math.Abs(comp.Sample(60).Pos.Z) < 1e-3f,
            "Follow Path: undo restores the curve and re-resolves the constraint");

        // advanced mode drives the factor directly from the path progress keys
        shot = tl.ActiveTrack.Shots[0];
        shot.SetEditorMode(CurveEditorMode.Advanced);
        foreach(var k in shot.PathProgress.Keys) k.Interpolation = CurveInterpolation.Linear;
        comp = ShotComposer.Create(tl, new PathSettings())!;
        double f0 = shot.PathProgress.Keys[0].Value, f1 = shot.PathProgress.Keys[1].Value;
        check(Math.Abs(comp.Sample(30).Pos.X - (8 + 4 * (f0 + f1) / 2)) < 2e-3f, "Follow Path (advanced): linear path progress is linear along the curve");

        // removing the constraint bakes the followed positions into the node path
        var before = tl.Keys.Select(k => tl.ActiveTrack.Shots[0].NodePose(k).Pos).ToArray();
        tl.ClearFollowPath(tl.ActiveTrack);
        shot = tl.ActiveTrack.Shots[0];
        check(!shot.IsFollowing && tl.Keys.Select((k, i) => Vector3.Distance(shot.NodePose(k).Pos, before[i])).Max() < 2e-3f,
            "Follow Path: removing the constraint keeps the nodes where they were");

        // deleting the target curve also releases the constraint
        var c2 = CurveEdit.Create(CurvePrimitive.Circle, new Vector3(0, 0, 0), 3); tl.Curves.Add(c2);
        tl.SetFollowPath(tl.ActiveTrack, c2);
        tl.RemoveCurve(c2);
        check(tl.ActiveTrack.FollowPath == null && !tl.ActiveTrack.Shots[0].IsFollowing && tl.Curves.Count == 1, "Follow Path: deleting the target curve removes the constraint");
    }

    // The default node path is a Blender-style Bezier curve whose handles are edited in the game view.
    private static void NodePathHandles(Action<bool,string> check)
    {
        var tl = new CamTimeline { Length = 300 };
        foreach(var (f, p) in new[] { (0, new Vector3(0, 1, 0)), (60, new Vector3(4, 2, 3)), (120, new Vector3(8, 1, 0)) })
            tl.Keys.Add(new Keyframe { Frame = f, Pos = p, Rot = Quaternion.Identity, Fov = .8f });
        CurveModel.Ensure(tl.ActiveTrack, 30); var shot = tl.ActiveTrack.Shots[0];
        var node = tl.Keys[1];
        var h = shot.PathHandles(node)!.Value;
        check(h.type == CurveHandle.Auto && Vector3.Cross(Vector3.Normalize(-h.left), Vector3.Normalize(h.right)).Length() < 1e-4f,
            "Node path: Auto handles are colinear (smooth through the node)");
        float rightLength = h.right.Length();
        var grabbed = new Vector3(-1, 2, .5f);
        shot.SetPathHandle(node, true, grabbed);
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        var a = shot.PathHandles(node)!.Value;
        check(a.type == CurveHandle.Aligned && Vector3.Distance(a.left, grabbed) < 1e-5f && Math.Abs(a.right.Length() - rightLength) < 1e-4f
            && Vector3.Distance(Vector3.Normalize(a.right), -Vector3.Normalize(grabbed)) < 1e-4f,
            "Node path: grabbing an Auto handle makes the pair Aligned, the partner keeps its length");
        var mid = shot.Sample(30).Pos;
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        check(Vector3.Distance(shot.Sample(30).Pos, mid) < 1e-6f && Vector3.Distance(shot.NodePose(node).Pos, new Vector3(4, 2, 3)) < 1e-4f,
            "Node path: edited handles survive synchronization and the node stays put");
        shot.SetPathHandle(node, false, new Vector3(3, 0, 0));
        shot.SetPathHandleType(node, CurveHandle.Free);
        shot.SetPathHandle(node, false, new Vector3(0, 0, 4));
        var free = shot.PathHandles(node)!.Value;
        check(free.type == CurveHandle.Free && Vector3.Distance(free.right, new Vector3(0, 0, 4)) < 1e-5f && free.left != -free.right,
            "Node path: Free handles move independently");
        shot.SetPathHandleType(node, CurveHandle.Auto);
        CurveModel.Ensure(tl.ActiveTrack, 30); shot = tl.ActiveTrack.Shots[0];
        check(Vector3.Distance(shot.PathHandles(node)!.Value.right, h.right) < 1e-4f, "Node path: switching back to Auto refits the Blender handles");
    }
}
