using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class AnchoredEditorTimingTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach(var mode in new[] { CurveEditorMode.Simple, CurveEditorMode.Advanced })
            foreach(bool cyclic in new[] { false, true }) EditTiming(check, mode, cyclic);
        UnreachablePoints(check);
        NonPositionEdits(check);
        HoldsAndRetime(check);
        DiscontinuousTiming(check);
        AdvancedHandles(check);
        DragAfterEditing(check);
    }

    private static (CamTimeline tl, CurveObject curve) Setup(CurveEditorMode mode, bool cyclic)
    {
        var tl = new CamTimeline { Length = 600 };
        foreach(int frame in new[] { 0, 120, 240 })
            tl.Keys.Add(new Keyframe { Frame = frame, Pos = Vector3.Zero, Rot = Quaternion.Identity, Fov = .8f });
        CurveModel.Ensure(tl.ActiveTrack, tl.Fps);
        var curve = CurveEdit.Create(cyclic ? CurvePrimitive.Circle : CurvePrimitive.Path, Vector3.Zero, 3);
        tl.Curves.Add(curve); tl.SetFollowPath(tl.ActiveTrack, curve);
        var shot = tl.ActiveTrack.Shots[0];
        shot.SetEditorMode(mode);
        if(cyclic) shot.SetNodeFactor(tl.Keys[^1], 2);
        CurveModel.Ensure(tl.ActiveTrack, tl.Fps);
        tl.AnchorCurve(tl.ActiveTrack, true);
        CurveModel.Ensure(tl.ActiveTrack, tl.Fps);
        return (tl, curve);
    }

    private static bool Exact(ShotCurves shot) => shot.CurvePointTimes().Where(t => t.Anchored)
        .All(t => t.Frame is double frame && shot.FollowFactorAt(frame) is double value &&
            Math.Abs(value - t.Factor - (shot.Follow!.Path.Cyclic ? Math.Round(value - t.Factor) : 0)) < 1e-6);

    private static void EditTiming(Action<bool, string> check, CurveEditorMode mode, bool cyclic)
    {
        var (tl, curve) = Setup(mode, cyclic);
        var track = tl.ActiveTrack;
        var shot = track.Shots[0];
        var id = curve.PathSpline!.Points[1].Id;
        double oldFrame = track.FollowPath!.TimeAnchors[id];
        double oldSample = shot.FollowFactorAt(60)!.Value;
        int signature = shot.FollowTimingSignature();
        tl.Snapshot();
        if(mode == CurveEditorMode.Simple)
        {
            shot.ActivateSimpleTiming();
            var leg = shot.Intervals[0];
            var timing = leg.Curve(CameraChannel.Position);
            timing.Keys[0].Mode = timing.Keys[^1].Mode = HandleMode.Free;
            timing.Keys[0].Right = new Vector2(40, 0);
            timing.Keys[^1].Left = new Vector2(-40, -.9f);
            leg.MarkManual();
        }
        else
        {
            foreach(var key in shot.PathProgress.Keys) key.Interpolation = CurveInterpolation.Linear;
            shot.PathProgress.Keys[1].Value = cyclic ? 1.6 : .8;
        }
        check(signature != shot.FollowTimingSignature(), $"Anchored editor ({mode}, closed={cyclic}): position edit is detected");
        check(tl.SyncCurveAnchorsFromEditor(shot), $"Anchored editor ({mode}, closed={cyclic}): graph edit commits without disabling timeline anchoring");
        shot = track.Shots[0];
        check(CamTimeline.CurveAnchoredToTimeline(track) && track.FollowPath.EditorTiming
            && Math.Abs(shot.FollowFactorAt(60)!.Value - oldSample) > .01
            && Math.Abs(track.FollowPath.TimeAnchors[id] - oldFrame) > 1,
            $"Anchored editor ({mode}, closed={cyclic}): speed changes and the curve point moves to its actual crossing frame");
        check(Exact(shot), $"Anchored editor ({mode}, closed={cyclic}): camera reaches every displayed anchor exactly");
        double frame = track.FollowPath.TimeAnchors[id];
        var point = shot.CurvePointTimes().First(t => t.Id == id && t.Anchored);
        check(shot.AnchorFrameOf(point.Factor, frame) is double inverse && Math.Abs(inverse - frame) < 1e-5,
            $"Anchored editor ({mode}, closed={cyclic}): inverse frame lookup follows the edited speed");
        double value = shot.FollowFactorAt(60)!.Value;
        for(int i = 0; i < 3; i++) CurveModel.Ensure(track, tl.Fps);
        check(Math.Abs(shot.FollowFactorAt(60)!.Value - value) < 1e-9 && Math.Abs(track.FollowPath.TimeAnchors[id] - frame) < 1e-9,
            $"Anchored editor ({mode}, closed={cyclic}): repeated rebuilds do not feed anchor times back into the curve");
        if(cyclic)
        {
            var repeat = shot.CurvePointTimes().First(t => t.Id == id && t.Repeat && !t.Anchored);
            check(repeat.Frame is double rf && shot.AnchorFrameOf(point.Factor, rf) is double found && Math.Abs(found - rf) < 1e-5
                && track.FollowPath.LoopEndFrame is double loop && Math.Abs(shot.FollowFactorAt(loop)!.Value - Math.Round(shot.FollowFactorAt(loop)!.Value)) < 1e-6,
                $"Anchored editor ({mode}, closed=true): repeat markers, nearby-turn lookup and loop end stay synchronized");
        }
        tl.Undo();
        track = tl.ActiveTrack; CurveModel.Ensure(track, tl.Fps); shot = track.Shots[0];
        check(!track.FollowPath!.EditorTiming && Math.Abs(track.FollowPath.TimeAnchors[id] - oldFrame) < 1e-9
            && Math.Abs(shot.FollowFactorAt(60)!.Value - oldSample) < 1e-9,
            $"Anchored editor ({mode}, closed={cyclic}): undo restores both speed and anchor frames");
        tl.Redo(); track = tl.ActiveTrack; CurveModel.Ensure(track, tl.Fps); shot = track.Shots[0];
        check(track.FollowPath!.EditorTiming && Math.Abs(shot.FollowFactorAt(60)!.Value - value) < 1e-9 && Exact(shot),
            $"Anchored editor ({mode}, closed={cyclic}): redo restores editor timing with synchronized anchors");
        tl.SetCurvePointAnchor(track, id, 90.125);
        CurveModel.Ensure(track, tl.Fps); shot = track.Shots[0];
        double anchoredValue = shot.FollowFactorAt(90.125)!.Value;
        check(!track.FollowPath.EditorTiming && Math.Abs(anchoredValue - point.Factor
            - (cyclic ? Math.Round(anchoredValue - point.Factor) : 0)) < 1e-6,
            $"Anchored editor ({mode}, closed={cyclic}): dragging an anchor retimes the camera to that exact fractional frame");
    }

    private static void UnreachablePoints(Action<bool, string> check)
    {
        var (tl, _) = Setup(CurveEditorMode.Advanced, false);
        var shot = tl.ActiveTrack.Shots[0];
        foreach(var key in shot.PathProgress.Keys) key.Value = .3;
        tl.SyncCurveAnchorsFromEditor(shot);
        check(CamTimeline.CurveAnchoredToTimeline(tl.ActiveTrack) && tl.ActiveTrack.FollowPath!.TimeAnchors.Count == 0
            && shot.CurvePointTimes().All(t => t.Frame == null) && shot.AnchorFrameOf(.5) == null,
            "Anchored editor: unreachable points have no stale anchor and do not silently switch anchoring mode");
        tl.AnchorCurve(tl.ActiveTrack, false);
        check(!CamTimeline.CurveAnchoredToTimeline(tl.ActiveTrack) && !tl.ActiveTrack.FollowPath!.EditorTiming,
            "Anchored editor: switching back to camera anchoring clears editor timing even without reachable points");
    }

    private static void HoldsAndRetime(Action<bool, string> check)
    {
        foreach(var mode in new[] { CurveEditorMode.Simple, CurveEditorMode.Advanced })
        {
            var (tl, curve) = Setup(mode, false);
            var shot = tl.ActiveTrack.Shots[0];
            var id = curve.PathSpline!.Points[1].Id;
            tl.Keys[0].HoldFrames = 20;
            if(mode == CurveEditorMode.Simple)
            {
                shot.ActivateSimpleTiming();
                foreach(var leg in shot.Intervals)
                {
                    foreach(var key in leg.Curve(CameraChannel.Position).Keys) key.Interpolation = CurveInterpolation.Linear;
                    leg.MarkManual();
                }
            }
            else foreach(var key in shot.PathProgress.Keys) key.Interpolation = CurveInterpolation.Linear;
            tl.SyncCurveAnchorsFromEditor(shot);
            double first = tl.ActiveTrack.FollowPath!.TimeAnchors[id];
            check(Math.Abs(shot.FollowFactorAt(10)!.Value) < 1e-9 && Math.Abs(first - 70) < .01
                && shot.CurvePointTimes().Count(t => t.Index == 0 && t.Frame != null) == 1,
                $"Anchored editor ({mode}): holds affect speed and arrival time without duplicate stationary markers");
            tl.MoveKey(tl.Keys[1], 160);
            CurveModel.Ensure(tl.ActiveTrack, tl.Fps);
            check(Math.Abs(tl.ActiveTrack.FollowPath.TimeAnchors[id] - 90) < .01 && Exact(shot),
                $"Anchored editor ({mode}): later camera-node retiming refreshes curve anchors automatically");
        }
    }

    private static void DiscontinuousTiming(Action<bool, string> check)
    {
        var (tl, _) = Setup(CurveEditorMode.Advanced, false);
        var shot = tl.ActiveTrack.Shots[0];
        foreach(var key in shot.PathProgress.Keys) key.Interpolation = CurveInterpolation.Constant;
        tl.SyncCurveAnchorsFromEditor(shot);
        check(shot.CurvePointTimes().Where(t => t.Index == 1 || t.Index == 3).All(t => t.Frame == null)
            && shot.AnchorFrameOf(.25) == null && Exact(shot),
            "Anchored editor: constant jumps do not invent crossings or inverse frames for skipped curve points");
    }

    private static void AdvancedHandles(Action<bool, string> check)
    {
        var (tl, _) = Setup(CurveEditorMode.Advanced, false);
        var shot = tl.ActiveTrack.Shots[0];
        double before = shot.FollowFactorAt(60)!.Value;
        shot.PathProgress.Keys[0].Mode = shot.PathProgress.Keys[1].Mode = HandleMode.Free;
        shot.PathProgress.Keys[0].Right = new Vector2(40, 0);
        shot.PathProgress.Keys[1].Left = new Vector2(-40, -.45f);
        tl.SyncCurveAnchorsFromEditor(shot);
        check(Math.Abs(shot.FollowFactorAt(60)!.Value - before) > .01 && Exact(shot)
            && shot.PathProgress.Keys[1].Left == new Vector2(-40, -.45f),
            "Anchored editor: advanced handle edits change actual speed and survive rebuilding");
    }

    private static void DragAfterEditing(Action<bool, string> check)
    {
        foreach(bool dragging in new[] { true, false })
        {
            var (tl, curve) = Setup(CurveEditorMode.Advanced, false);
            var shot = tl.ActiveTrack.Shots[0];
            foreach(var key in shot.PathProgress.Keys) key.Interpolation = CurveInterpolation.Linear;
            shot.PathProgress.Keys[1].Value = .8;
            tl.SyncCurveAnchorsFromEditor(shot);
            var node = tl.Keys[1];
            double expected = shot.AnchorFrameOf(.25, node.Frame)!.Value;
            var position = CurvePath.Create(curve)!.Position(.25);
            int? frame;
            if(dragging)
            {
                shot.WritePose(node, new XcpFrame(position, node.Rot, node.Fov), true, false, false);
                frame = shot.RequestedFrame;
            }
            else frame = shot.SetAnchoredNodeFactor(node, .25) is double target ? (int)Math.Round(target) : null;
            check(frame is int f && Math.Abs(f - expected) <= .51,
                $"Anchored editor (drag={dragging}): node lookup uses the old timing before modifying its factor");
            if(frame is int requested) tl.MoveKey(node, requested);
            CurveModel.Ensure(tl.ActiveTrack, tl.Fps);
            check(Vector3.Distance(shot.NodePose(node).Pos, position) < 2e-3 && Exact(shot),
                $"Anchored editor (drag={dragging}): node lands at the requested path position and curve anchors stay synchronized");
        }
    }

    private static void NonPositionEdits(Action<bool, string> check)
    {
        var (tl, _) = Setup(CurveEditorMode.Simple, false);
        var shot = tl.ActiveTrack.Shots[0];
        foreach(var leg in shot.Intervals) leg.Unlink();
        int before = shot.FollowTimingSignature();
        shot.Intervals[0].Curve(CameraChannel.Rotation).Keys[0].Right = new Vector2(20, .9f);
        shot.Offset[0].Keys[1].Value = 10;
        check(before == shot.FollowTimingSignature(), "Anchored editor: independent rotation and location offset edits leave path timing ownership unchanged");
        shot.SetEditorMode(CurveEditorMode.Advanced);
        check(before != shot.FollowTimingSignature(), "Anchored editor: changing the active curve mode also refreshes anchor times");
    }
}
