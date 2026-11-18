using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

/// <summary>Curve points of a followed curve on the timeline: camera anchors (default) and timeline anchors.</summary>
public static class CurveAnchorTests
{
    public static void Run(Action<bool,string> check)
    {
        PointTimes(check);
        CircleFactors(check);
        RepeatedCircleTiming(check);
        TimelineAnchors(check);
        DragAlongAnchoredCurve(check);
        PointIds(check);
        CapturedNodeProgress(check);
        FractionalAnchorFrames(check);
    }

    // straight NURBS path x = 8..12, nodes at frames 0 / 60 / 120 (0 %, 50 %, 100 %)
    private static (CamTimeline tl, CurveObject curve) Setup()
    {
        var tl = new CamTimeline { Length = 300 };
        foreach(var (f, x) in new[] { (0, 8f), (60, 10f), (120, 12f) })
            tl.Keys.Add(new Keyframe { Frame = f, Pos = new(x, 0, 0), Rot = Quaternion.Identity, Fov = .8f });
        CurveModel.Ensure(tl.ActiveTrack, 30);
        var curve = CurveEdit.Create(CurvePrimitive.Path, new Vector3(10, 0, 0));
        tl.Curves.Add(curve);
        tl.SetFollowPath(tl.ActiveTrack, curve);
        return (tl, curve);
    }

    private static ShotCurves Shot(CamTimeline tl) { CurveModel.Ensure(tl.ActiveTrack, 30); return tl.ActiveTrack.Shots[0]; }

    private static double[] Factors(CamTimeline tl)
    {
        var shot = Shot(tl);
        return tl.ActiveTrack.Keys.OrderBy(k => k.Frame).Select(k => shot.NodeFactor(k) ?? double.NaN).ToArray();
    }

    private static void PointTimes(Action<bool,string> check)
    {
        var (tl, curve) = Setup();
        var times = Shot(tl).CurvePointTimes();
        int n = curve.PathSpline!.Points.Count;
        check(times.Count == n && Math.Abs(times[0].Factor) < 1e-6 && Math.Abs(times[^1].Factor - 1) < 1e-6
            && times.Zip(times.Skip(1)).All(p => p.Second.Factor > p.First.Factor),
            $"Curve anchors: path point factors run 0 → 100 % along the curve ({string.Join(", ", times.Select(t => t.Factor.ToString("0.000")))})");
        var half = times.OrderBy(t => Math.Abs(t.Factor - .5)).First();
        check(Math.Abs(half.Factor - .5) < 1e-3 && half.Frame is double f && Math.Abs(f - 60) < 1,
            $"Curve anchors: the 50 % curve point shows at the middle node's frame (F{half.Frame:0.0})");
        check(times.All(t => !t.Anchored && t.Frame is double fr && fr >= -1e-6 && fr <= 120 + 1e-6)
            && times.Zip(times.Skip(1)).All(p => p.Second.Frame > p.First.Frame),
            "Curve anchors: camera-anchored points sit in order between the first and last node");

        // a single camera node: no curve point times
        tl.Keys.RemoveRange(1, 2);
        check(Shot(tl).CurvePointTimes().Count == 0, "Curve anchors: fewer than 2 camera nodes shows no curve points");
    }

    private static void CircleFactors(Action<bool,string> check)
    {
        var tl = new CamTimeline { Length = 300 };
        foreach(var f in new[] { 0, 120 })
            tl.Keys.Add(new Keyframe { Frame = f, Pos = Vector3.Zero, Rot = Quaternion.Identity, Fov = .8f });
        CurveModel.Ensure(tl.ActiveTrack, 30);
        var circle = CurveEdit.Create(CurvePrimitive.Circle, Vector3.Zero, 3);
        tl.Curves.Add(circle);
        tl.SetFollowPath(tl.ActiveTrack, circle);
        var all = Shot(tl).CurvePointTimes();
        var times = all.Where(t => !t.Repeat).ToList();
        var expected = Enumerable.Range(0, times.Count).Select(i => (double)i / times.Count).ToArray();
        check(times.Count == 4 && times.Select(t => t.Factor).Zip(expected).All(p => Math.Abs(p.First - p.Second) < 1e-3),
            $"Curve anchors: Bezier circle points are evenly spaced on the loop ({string.Join(", ", times.Select(t => t.Factor.ToString("0.000")))})");
        check(all.Count(t => t.Repeat) == 1 && all.First(t => t.Repeat) is { Index: 0, Frame: double end } && Math.Abs(end - 120) < 1
            && times[0].Frame is double start && Math.Abs(start) < 1,
            "Curve anchors: a closed curve's start point shows at both the start and the end of the loop");

        // anchoring the whole circle: every point plus the loop's return to P1, motion forward
        var track = tl.ActiveTrack;
        var pts = circle.PathSpline!.Points;
        bool Forward()
        {
            var shot = Shot(tl);
            double last = double.NegativeInfinity; bool ok = true;
            for(int f = 0; f <= 120; f++) { double v = shot.FollowFactorAt(f)!.Value; ok &= v >= last - 1e-9; last = v; }
            return ok;
        }
        // the camera is exactly on every anchored point at its anchor frame (and on P1 again at the loop end)
        bool Exact()
        {
            var comp = ShotComposer.Create(tl, new PathSettings())!;
            var follow = tl.ActiveTrack.FollowPath!;
            var stops = follow.TimeAnchors.Select(a => (frame: a.Value, point: pts.First(p => p.Id == a.Key))).ToList();
            if(follow.LoopEndFrame is double le) stops.Add((le, pts[0]));
            return stops.All(s => Vector3.Distance(comp.Sample((float)s.frame).Pos, circle.ToWorld(s.point.Co)) < 2e-3f);
        }
        check(tl.AnchorCurve(track, true) && track.FollowPath!.TimeAnchors.Count == 4 && track.FollowPath.LoopEndFrame is double loop && Math.Abs(loop - 120) <= 1,
            "Curve anchors (closed): anchoring the circle anchors every point and the loop's return to P1");
        var loopTimes = Shot(tl).CurvePointTimes();
        check(loopTimes.Count(t => t.Index == 0 && t.Anchored) == 2 && loopTimes.Count(t => t.Repeat && !t.Anchored) == 0,
            "Curve anchors (closed): P1 is anchored at the start and at the end");
        var factors = track.Keys.Select(k => Shot(tl).NodeFactor(k)!.Value).ToArray();
        check(Math.Abs(factors[0]) < 1e-6 && Math.Abs(factors[1] - 1) < 1e-6 && Forward() && Exact(),
            $"Curve anchors (closed): the loop runs 0 → 100 % and stops exactly on every anchor ({string.Join(", ", factors.Select(f => f.ToString("0.000")))})");

        // anchor frames set the timing between the camera nodes
        tl.SetCurvePointAnchor(track, pts[1].Id, 10);
        tl.SetCurvePointAnchor(track, pts[2].Id, 20);
        tl.SetCurvePointAnchor(track, pts[3].Id, 100);
        check(track.FollowPath!.TimeAnchors[pts[0].Id] == 0 && Forward() && Exact(),
            "Curve anchors (closed): retimed anchors move the camera between nodes so it is on each point at its frame");
        tl.SetCurvePointAnchor(track, pts[0].Id, 110, loopEnd: true);
        var retimed = track.Keys.Select(k => Shot(tl).NodeFactor(k)!.Value).ToArray();
        check(track.FollowPath.LoopEndFrame == 110 && retimed[1] > 1 + 1e-3 && Forward() && Exact(),
            "Curve anchors (closed): moving the loop end earlier makes the camera reach P1 at that frame");
        tl.Undo();
        track = tl.ActiveTrack; // undo restores cloned tracks
        check(track.FollowPath!.LoopEndFrame is double restored && Math.Abs(restored - 120) <= 1, "Curve anchors (closed): undo restores the loop end frame");

        // partial anchors (only reachable through data) never turn the camera back
        track.FollowPath.TimeAnchors = new() { [pts[0].Id] = 0, [pts[3].Id] = 90 };
        track.FollowPath.LoopEndFrame = null;
        check(Forward(), "Curve anchors (closed): P1 and P4 alone still run forward round the loop");
        // dragging the end node onto P1 stays on the same turn instead of jumping to the start
        var endNode = track.Keys[1];
        var drag = Shot(tl);
        drag.WritePose(endNode, new XcpFrame(circle.ToWorld(pts[0].Co), endNode.Rot, endNode.Fov), true, false, false);
        double expectedEnd = drag.AnchorFrameOf(1, 120)!.Value;
        check(drag.RequestedFrame is int r && Math.Abs(r - expectedEnd) <= 1 && r > 90,
            $"Curve anchors (closed): moving the end node onto the start point stays on the same turn (F{drag.RequestedFrame})");
    }

    private static void RepeatedCircleTiming(Action<bool,string> check)
    {
        var tl = new CamTimeline { Length = 3000 };
        foreach(int f in new[] { 0, 1500 })
            tl.Keys.Add(new Keyframe { Frame = f, Pos = Vector3.Zero, Rot = Quaternion.Identity, Fov = .8f });
        CurveModel.Ensure(tl.ActiveTrack, 30);
        var circle = CurveEdit.Create(CurvePrimitive.Circle, Vector3.Zero, 3);
        tl.Curves.Add(circle);
        tl.SetFollowPath(tl.ActiveTrack, circle);
        var track = tl.ActiveTrack;
        var pts = circle.PathSpline!.Points;
        var follow = track.FollowPath!;
        follow.TimeAnchors = new() { [pts[0].Id] = 600, [pts[1].Id] = 610, [pts[2].Id] = 630, [pts[3].Id] = 690 };
        follow.LoopEndFrame = 720;
        var shot = Shot(tl);
        var stops = new[] { (0, 0.0), (10, .25), (30, .5), (90, .75), (120, 1.0) };
        bool exact = true, inverse = true;
        for(int turn = -5; turn <= 6; turn++)
            foreach(var (offset, factor) in stops)
            {
                double frame = 600 + 120 * turn + offset;
                exact &= Math.Abs(shot.FollowFactorAt(frame)!.Value - (turn + factor)) < 1e-6;
                inverse &= shot.AnchorFrameOf(factor, frame) is double mapped && Math.Abs(mapped - frame) < 1e-5;
            }
        check(exact, "Curve loops: every turn before and after the anchors repeats the 10 / 20 / 60 / 30 frame arc durations");
        check(inverse, "Curve loops: inverse mapping finds the nearby turn even far from the anchored range");
        bool periodic = true, forward = true;
        double last = double.NegativeInfinity;
        for(int f = 0; f <= 1380; f++)
        {
            double value = shot.FollowFactorAt(f)!.Value;
            periodic &= Math.Abs(shot.FollowFactorAt(f + 120)!.Value - value - 1) < 1e-6;
            forward &= value >= last - 1e-9; last = value;
        }
        check(periodic && forward, "Curve loops: smooth motion repeats throughout each turn without reversing");
        var times = shot.CurvePointTimes();
        check(times.Any(t => t.Index == 1 && t.Repeat && t.Frame is double f && Math.Abs(f - 1450) < 1e-3),
            "Curve loops: timeline repeat markers use the anchored rhythm in later turns");
        double originalFrame = shot.AnchorFrameOf(.25)!.Value;
        double earlierSample = shot.FollowFactorAt(1495)!.Value;
        track.Keys[^1].Frame = 2700;
        check(Math.Abs(shot.FollowFactorAt(2530)!.Value - 16.25) < 1e-6
            && Math.Abs(shot.FollowFactorAt(1495)!.Value - earlierSample) < 1e-6
            && Math.Abs(shot.AnchorFrameOf(.25)!.Value - originalFrame) < 1e-6,
            "Curve loops: extending the node range refreshes repeats without changing existing timing or the default inverse turn");
        shot.WritePose(track.Keys[^1], new XcpFrame(circle.ToWorld(pts[1].Co), Quaternion.Identity, .8f), true, false, false);
        check(shot.RequestedFrame == 2650, "Curve loops: dragging a late camera node stays on its nearby turn");

        follow.TimeAnchors[pts[1].Id] = 620;
        shot = Shot(tl);
        check(Math.Abs(shot.FollowFactorAt(740)!.Value - 1.25) < 1e-6
            && Math.Abs(shot.FollowFactorAt(500)!.Value + .75) < 1e-6,
            "Curve loops: retiming P2 changes its arrival in both earlier and later turns");

        follow.TimeAnchors = new() { [pts[0].Id] = 600 };
        shot = Shot(tl);
        check(Math.Abs(shot.FollowFactorAt(720)!.Value - 1) < 1e-6
            && Math.Abs(shot.FollowFactorAt(840)!.Value - 2) < 1e-6,
            "Curve loops: P1 and its loop end alone describe complete turns instead of a stationary point");
        follow.LoopEndFrame = null;
        shot = Shot(tl);
        check(!shot.UsesTimeAnchors, "Curve loops: a single anchor without a loop end does not enable repeated timing");
    }

    private static void TimelineAnchors(Action<bool,string> check)
    {
        var (tl, curve) = Setup();
        var pts = curve.PathSpline!.Points;
        var factors = Shot(tl).CurvePointTimes().Select(t => t.Factor).ToArray();

        // anchoring is per curve: editing one point's frame anchors the whole curve to the timeline
        tl.SetCurvePointAnchor(tl.ActiveTrack, pts[2].Id, 30);
        check(tl.ActiveTrack.FollowPath!.TimeAnchors.Count == pts.Count && tl.ActiveTrack.FollowPath.TimeAnchors[pts[2].Id] == 30,
            "Curve anchors: moving one camera-anchored point anchors the whole curve to the timeline");
        var times = Shot(tl).CurvePointTimes();
        var p3 = times.First(t => t.Index == 2 && !t.Repeat);
        check(p3.Anchored && p3.Frame == 30 && times.Where(t => !t.Repeat).All(t => t.Anchored),
            "Curve anchors: every point of an anchored curve shows at its anchor frame");
        // the camera is on every anchored point's path progress at its anchor frame
        var exact = Shot(tl);
        check(tl.ActiveTrack.FollowPath.TimeAnchors.All(a => Math.Abs(exact.FollowFactorAt(a.Value)!.Value - factors[pts.FindIndex(p => p.Id == a.Key)]) < 1e-6),
            "Curve anchors: the camera reaches every anchored point exactly at its anchor frame");
        tl.Undo();
        check(!CamTimeline.CurveAnchoredToTimeline(tl.ActiveTrack), "Curve anchors: undo returns the whole curve to the camera");

        // two anchors remap every node (extrapolated outside, clamped to the open curve)
        tl.ActiveTrack.FollowPath!.TimeAnchors = new() { [pts[1].Id] = 60, [pts[2].Id] = 30 };
        tl.SetCurvePointAnchor(tl.ActiveTrack, pts[2].Id, 90);
        check(tl.ActiveTrack.FollowPath.TimeAnchors.Count == 2 && tl.ActiveTrack.FollowPath.TimeAnchors[pts[1].Id] == 60,
            "Curve anchors: on a timeline-anchored curve, moving one point leaves the others where they are");
        var two = Factors(tl);
        double At(double frame) => Math.Clamp(factors[1] + (factors[2] - factors[1]) * (frame - 60) / 30, 0, 1);
        check(Shot(tl).UsesTimeAnchors && two.Zip(new[] { At(0), At(60), At(120) }).All(p => Math.Abs(p.First - p.Second) < 1e-6),
            $"Curve anchors: two timeline anchors drive the nodes' progress ({string.Join(", ", two.Select(f => f.ToString("0.000")))})");
        check(Shot(tl).AnchorFrameOf(factors[1]) is double back && Math.Abs(back - 60) < 1e-6
            && Shot(tl).AnchorFrameOf(factors[2] + (factors[2] - factors[1])) is double beyond && Math.Abs(beyond - 120) < 1e-6,
            "Curve anchors: progress maps back to the anchored frame (and extrapolates past the last anchor)");
        var comp = ShotComposer.Create(tl, new PathSettings())!;
        var on = curve.ToWorld(pts[1].Co);
        check(Math.Abs(comp.Sample(60).Pos.X - (8 + 4 * factors[1])) < 2e-3f, $"Curve anchors: the camera is on the anchored point at its frame ({on})");

        // undo restores the previous anchors
        tl.Undo();
        check(tl.ActiveTrack.FollowPath!.TimeAnchors.Count == 2 && tl.ActiveTrack.FollowPath.TimeAnchors[pts[2].Id] == 30,
            "Curve anchors: undo restores the previous anchors");
        tl.Redo();

        // Alt+C: the whole curve back to the camera
        bool removed = tl.AnchorCurve(tl.ActiveTrack, false);
        var back0 = Factors(tl);
        check(removed && tl.ActiveTrack.FollowPath!.TimeAnchors.Count == 0 && Math.Abs(back0[1] - .5) < 1e-9 && !tl.AnchorCurve(tl.ActiveTrack, false),
            "Curve anchors: anchoring the curve to the camera restores the default 0–100 % spread");
        // Alt+T: every point at the frame the camera reaches it
        var passes = Shot(tl).CurvePointTimes().Where(t => !t.Repeat).ToDictionary(t => t.Id, t => t.Frame!.Value);
        bool added = tl.AnchorCurve(tl.ActiveTrack, true);
        var anchors = tl.ActiveTrack.FollowPath!.TimeAnchors;
        check(added && anchors.Count == pts.Count && Math.Abs(anchors[pts[2].Id] - 60) < 1e-3
            && passes.All(p => Math.Abs(anchors[p.Key] - p.Value) < 1e-8)
            && Math.Abs(Factors(tl)[1] - .5) < 1e-3,
            "Curve anchors: anchoring the curve to the timeline fixes every point at the frame the camera reaches it");

        // deleting an anchored point drops its anchor (undo / redo restored a cloned curve: edit the live one)
        var live = tl.ActiveTrack.FollowCurve!;
        var deleted = live.PathSpline!.Points[2].Id;
        int before = anchors.Count;
        CurveEdit.Delete(live.PathSpline!, new HashSet<int> { 2 }); live.Touch();
        Shot(tl);
        check(!tl.ActiveTrack.FollowPath!.TimeAnchors.ContainsKey(deleted) && tl.ActiveTrack.FollowPath.TimeAnchors.Count == before - 1,
            "Curve anchors: deleting a curve point removes its anchor");
    }

    private static void DragAlongAnchoredCurve(Action<bool,string> check)
    {
        var (tl, curve) = Setup();
        var pts = curve.PathSpline!.Points;
        // linear anchors 0 % @ F0 → 100 % @ F120 (set directly: the UI always anchors the whole curve)
        tl.ActiveTrack.FollowPath!.TimeAnchors = new() { [pts[0].Id] = 0, [pts[^1].Id] = 120 };
        var shot = Shot(tl);
        var mid = tl.ActiveTrack.Keys.First(k => k.Frame == 60);
        shot.WritePose(mid, new XcpFrame(new Vector3(11, 2, 0), mid.Rot, mid.Fov), true, false, false);
        check(shot.RequestedFrame == 90 && Math.Abs(shot.Offset[1].Keys.First(k => k.NodeId == mid.Id).Value - 2) < 2e-3,
            $"Curve anchors: dragging a node along a timeline-anchored curve requests a new frame (F{shot.RequestedFrame}) and keeps the height as offset");
        int moved = tl.MoveKey(mid, shot.RequestedFrame ?? mid.Frame);
        shot = Shot(tl);
        check(moved == 90 && Vector3.Distance(shot.NodePose(mid).Pos, new Vector3(11, 2, 0)) < 2e-3f,
            $"Curve anchors: the moved node lands on the dragged position ({shot.NodePose(mid).Pos})");
        shot.WritePose(mid, new XcpFrame(shot.NodePose(mid).Pos, Quaternion.CreateFromYawPitchRoll(.3f, 0, 0), mid.Fov), false, true, false);
        check(shot.RequestedFrame == null, "Curve anchors: rotation-only edits request no frame change");
    }

    private static void CapturedNodeProgress(Action<bool,string> check)
    {
        foreach(bool anchored in new[] { false, true })
            foreach(var mode in new[] { CurveEditorMode.Simple, CurveEditorMode.Advanced })
            foreach(bool manual in new[] { false, true })
            {
                var (tl, _) = Setup();
                var shot = Shot(tl);
                if(manual) shot.SetNodeFactor(tl.Keys[1], .8);
                tl.Keys[0].HoldFrames = 7;
                shot.SetEditorMode(mode);
                shot = Shot(tl);
                if(anchored) { tl.AnchorCurve(tl.ActiveTrack, true); shot = Shot(tl); }
                const int frame = 23;
                double expected = shot.FollowFactorAt(frame)!.Value;
                var existingFactors = tl.Keys.ToDictionary(k => k.Id, k => shot.NodeFactor(k)!.Value);
                var pose = shot.Sample(frame);
                var captured = new Keyframe { Frame = frame, Pos = new(50, 9, -30),
                    Rot = Quaternion.CreateFromYawPitchRoll(.2f, .3f, .4f), Fov = .55f };
                var rotation = captured.Rot;
                tl.AddCapturedNode(captured);
                shot = Shot(tl);
                shot.WritePose(captured, new XcpFrame(captured.Pos, captured.Rot, captured.Fov), false, true, true);
                shot = Shot(tl);
                check(Math.Abs(shot.NodeFactor(captured)!.Value - expected) < 1e-8
                    && Vector3.Distance(shot.NodePose(captured).Pos, pose.Pos) < 2e-3
                    && tl.Keys.Where(k => k != captured).All(k => Math.Abs(shot.NodeFactor(k)!.Value - existingFactors[k.Id]) < 1e-8),
                    $"Follow capture ({mode}, timeline={anchored}, manual={manual}): insertion inherits sampled progress and preserves existing node factors");
                check(Math.Abs(Quaternion.Dot(shot.NodePose(captured).Rot, rotation)) > 1 - 1e-5
                    && Math.Abs(shot.NodePose(captured).Fov - .55f) < 1e-5,
                    $"Follow capture ({mode}, timeline={anchored}): rotation and FoV still come from the captured camera");
                tl.Undo();
                check(tl.Keys.Count == 3 && !tl.Keys.Any(k => k.Id == captured.Id),
                    $"Follow capture ({mode}, timeline={anchored}): undo removes the inserted node and its subdivision");
            }
    }

    private static void FractionalAnchorFrames(Action<bool,string> check)
    {
        foreach(bool cyclic in new[] { false, true })
        {
            var tl = new CamTimeline { Length = 300 };
            foreach(int frame in new[] { 0, 101 })
                tl.Keys.Add(new Keyframe { Frame = frame, Pos = Vector3.Zero, Rot = Quaternion.Identity, Fov = .8f });
            CurveModel.Ensure(tl.ActiveTrack, 30);
            var curve = CurveEdit.Create(cyclic ? CurvePrimitive.Circle : CurvePrimitive.Path, Vector3.Zero, 3);
            tl.Curves.Add(curve); tl.SetFollowPath(tl.ActiveTrack, curve);
            var shot = Shot(tl);
            shot.SetEditorMode(CurveEditorMode.Advanced);
            foreach(var key in shot.PathProgress.Keys) key.Interpolation = CurveInterpolation.Linear;
            shot = Shot(tl);
            var times = shot.CurvePointTimes().Where(t => !t.Repeat && t.Frame != null).ToList();
            check(times.Any(t => Math.Abs(t.Frame!.Value - Math.Round(t.Frame.Value)) > .01),
                $"Fractional anchors (closed={cyclic}): fixture reaches intermediate curve points between integer frames");
            tl.AnchorCurve(tl.ActiveTrack, true);
            shot = Shot(tl);
            check(times.All(t => Math.Abs(tl.ActiveTrack.FollowPath!.TimeAnchors[t.Id] - t.Frame!.Value) < 1e-9),
                $"Fractional anchors (closed={cyclic}): switching preserves every crossing time without quantization");
            check(Enumerable.Range(0, 102).All(f => Math.Abs(shot.FollowFactorAt(f)!.Value - f / 101.0) < 1e-6),
                $"Fractional anchors (closed={cyclic}): a linear motion remains unchanged when anchored to the timeline");
            var id = curve.PathSpline!.Points[1].Id;
            tl.SetCurvePointAnchor(tl.ActiveTrack, id, 25.125);
            check(tl.ActiveTrack.FollowPath!.TimeAnchors[id] == 25.125,
                $"Fractional anchors (closed={cyclic}): manually entered fractional frames remain exact");
            tl.Undo();
            check(Math.Abs(tl.ActiveTrack.FollowPath!.TimeAnchors[id] - times.First(t => t.Id == id).Frame!.Value) < 1e-9,
                $"Fractional anchors (closed={cyclic}): undo restores subframe precision");
        }
    }

    private static void PointIds(Action<bool,string> check)
    {
        var curve = CurveEdit.Create(CurvePrimitive.Bezier, Vector3.Zero);
        var spline = curve.Splines[0];
        var clone = curve.Clone();
        check(clone.Splines[0].Points.Select(p => p.Id).SequenceEqual(spline.Points.Select(p => p.Id)), "Curve anchors: point Ids survive cloning (undo)");
        CurveEdit.Subdivide(spline, Enumerable.Range(0, spline.Points.Count).ToHashSet());
        CurveEdit.Extrude(spline, spline.Points.Count - 1);
        CurveEdit.Extrude(spline, 0);
        var ids = spline.Points.Select(p => p.Id).ToList();
        check(ids.Distinct().Count() == ids.Count && clone.Splines[0].Points.All(p => ids.Contains(p.Id)),
            $"Curve anchors: subdivide / extrude give new points new Ids and keep the old ones ({ids.Count} points)");
    }
}
