using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class TrackConstraintTests
{
    private const float Aspect = 16f / 9f;

    /// <summary>Where a world point lands on screen (-1..1, +Y up) for a camera looking down its -Z.</summary>
    private static Vector2 Project(XcpFrame cam, Vector3 point)
    {
        var local = Vector3.Transform(point - cam.Pos, Quaternion.Inverse(cam.Rot));
        float t = MathF.Tan(cam.Fov / 2);
        return new Vector2(local.X / -local.Z / (t * Aspect), local.Y / -local.Z / t);
    }

    private static float Angle(Quaternion a, Quaternion b) => 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a, b)), 0, 1));

    public static void Run(Action<bool, string> check)
    {
        var target = new Vector3(0, 1.5f, 0);
        var pose = new XcpFrame(new Vector3(3, 2.5f, 6), Quaternion.CreateFromYawPitchRoll(-1.1f, .4f, .25f), .8f);

        // Track To: target centred, camera kept upright (no roll)
        var trackTo = new TrackConstraint { Type = TrackType.TrackTo };
        var a = TrackSolver.Apply(trackTo, pose, target, Aspect);
        var right = Vector3.Transform(Vector3.UnitX, a.Rot);
        check(Project(a, target).Length() < 1e-4f && MathF.Abs(right.Y) < 1e-4f && a.Pos == pose.Pos && a.Fov == pose.Fov,
            "Track To: target in the centre, no roll, position / FoV untouched");

        // screen offset: the target lands at the requested point
        trackTo.ScreenOffset = new Vector2(-.33f, .2f);
        var offset = TrackSolver.Apply(trackTo, pose, target, Aspect);
        check(Vector2.Distance(Project(offset, target), trackTo.ScreenOffset) < 1e-3f,
            $"Track To: screen offset puts the target at (-33 %, 20 %) ({Project(offset, target)})");
        trackTo.ScreenOffset = default;

        // Damped Track: keeps the node's own roll; an already aimed camera is unchanged
        var damped = new TrackConstraint { Type = TrackType.Damped };
        var rolled = a with { Rot = Quaternion.Normalize(a.Rot * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f)) };
        var d0 = TrackSolver.Apply(damped, rolled, target, Aspect);
        check(Angle(d0.Rot, rolled.Rot) < 1e-3f, "Damped Track: an aimed camera keeps its own roll");
        var d1 = TrackSolver.Apply(damped, pose, target, Aspect);
        var dir = Vector3.Transform(-Vector3.UnitZ, pose.Rot);
        var shortest = TrackSolver.FromTo(dir, Vector3.Normalize(target - pose.Pos));
        check(Project(d1, target).Length() < 1e-4f && Angle(d1.Rot, shortest * pose.Rot) < 1e-4f,
            "Damped Track: shortest rotation onto the target");
        damped.ScreenOffset = new Vector2(.4f, -.25f);
        check(Vector2.Distance(Project(TrackSolver.Apply(damped, pose, target, Aspect), target), damped.ScreenOffset) < 1e-3f,
            "Damped Track: screen offset");

        // Locked Track (lock Y): only pans around the camera's own up axis
        var locked = new TrackConstraint { Type = TrackType.Locked, LockAxis = PathAxis.Y };
        var level = new XcpFrame(new Vector3(0, 1.5f, 6), Quaternion.CreateFromYawPitchRoll(.8f, -.15f, 0), .8f);
        var l = TrackSolver.Apply(locked, level, new Vector3(-2, 1.5f, 0), Aspect);
        var upBefore = Vector3.Transform(Vector3.UnitY, level.Rot);
        var upAfter = Vector3.Transform(Vector3.UnitY, l.Rot);
        check(Vector3.Distance(upBefore, upAfter) < 1e-4f && MathF.Abs(Project(l, new Vector3(-2, 1.5f, 0)).X) < 1e-3f,
            "Locked Track: rotates only around the lock axis; target centred horizontally");

        // influence
        trackTo.Influence = 0;
        check(Angle(TrackSolver.Apply(trackTo, pose, target, Aspect).Rot, pose.Rot) < 1e-6f, "Influence 0: no effect");
        trackTo.Influence = .5f;
        float half = Angle(TrackSolver.Apply(trackTo, pose, target, Aspect).Rot, pose.Rot), full = Angle(a.Rot, pose.Rot);
        check(MathF.Abs(half - full / 2) < 1e-3f, "Influence 0.5: halfway between own and tracked rotation");
        trackTo.Influence = 1;

        // keep framing: tan(fov/2) * distance stays constant
        var keep = new TrackConstraint { Type = TrackType.TrackTo, KeepFraming = true };
        var reference = new XcpFrame(new Vector3(0, 1.5f, 4), Quaternion.Identity, .8f);
        float framing = TrackSolver.Framing(reference, target)!.Value;
        var far = TrackSolver.Apply(keep, new XcpFrame(new Vector3(0, 1.5f, 8), Quaternion.Identity, .8f), target, Aspect, framing);
        check(MathF.Abs(MathF.Tan(far.Fov / 2) * 8 - MathF.Tan(.4f) * 4) < 1e-4f && far.Fov < .8f,
            $"Keep Framing: FoV narrows with distance so the target keeps its size ({far.Fov:0.000} rad)");

        // recorded trajectories
        var rec = new TrackConstraint();
        rec.Recorded[0] = Vector3.Zero; rec.Recorded[10] = new Vector3(10, 0, 0); rec.RecordedFor = rec.TargetKey;
        check(Vector3.Distance(rec.RecordedAt(5)!.Value, new Vector3(5, 0, 0)) < 1e-6f && rec.RecordedAt(-3) == Vector3.Zero
            && rec.RecordedAt(20) == new Vector3(10, 0, 0), "Recording: linear between frames, held past the ends");
        check(!rec.RecordingStale(0, 10) && rec.RecordingStale(0, 12), "Recording: stale when the track range grows");
        rec.Bone = "j_te_l";
        check(rec.RecordingStale(0, 10), "Recording: stale when the bone changes");
        var fallback = new RecordedTargetSource(new FuncTargetSource((_, _) => Vector3.One));
        check(fallback.Position(new TrackConstraint(), 3) == Vector3.One && fallback.UsedFallback,
            "Recording: constraints without a recording fall back to the live position (flagged)");
        var clone = rec.Clone(); clone.Recorded[5] = Vector3.UnitY;
        check(!rec.Recorded.ContainsKey(5), "Recording: clones do not share the recorded trajectory");

        // composer: Follow Path keeps the position, tracking aims at a moving target
        var tl = new CamTimeline { Length = 300 };
        foreach(var (f, x) in new[] { (0, 8.2f), (60, 10f), (120, 11.8f) })
            tl.Keys.Add(new Keyframe { Frame = f, Pos = new(x, 1, .5f), Rot = Quaternion.Identity, Fov = .8f });
        var curve = CurveEdit.Create(CurvePrimitive.Path, new Vector3(10, 0, 0));
        tl.Curves.Add(curve);
        tl.SetFollowPath(tl.ActiveTrack, curve);
        var plain = ShotComposer.Create(tl, new PathSettings())!;
        tl.Snapshot();
        tl.ActiveTrack.TrackConstraints.Add(new TrackConstraint { Type = TrackType.TrackTo });
        check(tl.ActiveTrack.RotationDriven && !tl.ActiveTrack.FovDriven, "Tracking: an enabled constraint drives the rotation only");
        Vector3 Moving(float f) => new(10, 1.6f, -5 + f * .02f);
        var source = new FuncTargetSource((_, f) => Moving(f), Aspect);
        var tracked = ShotComposer.Create(tl, new PathSettings(), source)!;
        check(tracked.Tracking && Enumerable.Range(0, 121).All(f =>
            {
                var s = tracked.Sample(f);
                return Vector3.Distance(s.Pos, plain.Sample(f).Pos) < 1e-5f && Project(s, Moving(f)).Length() < 1e-3f;
            }), "Tracking + Follow Path: position stays on the curve, the camera aims at the moving target every frame");
        var offline = ShotComposer.Create(tl, new PathSettings())!;
        check(!offline.Tracking && Angle(offline.Sample(30).Rot, plain.Sample(30).Rot) < 1e-6f, "Tracking: no target source leaves the rotation to the nodes");
        var xcp = tracked.BakeXcp(30);
        check(xcp.Count == plain.BakeXcp(30).Count && Project(xcp[45], Moving(45 * tl.Fps / 30f)).Length() < 1e-3f, "Tracking: .xcp bake uses the constrained rotation");

        // keep framing on a track: the first node sets the size
        tl.ActiveTrack.TrackConstraints[0].KeepFraming = true;
        var framed = ShotComposer.Create(tl, new PathSettings(), new FuncTargetSource((_, _) => new Vector3(10, 0, -4), Aspect))!;
        var first = framed.Sample(0); var last = framed.Sample(120);
        float k0 = MathF.Tan(first.Fov / 2) * Vector3.Distance(first.Pos, new Vector3(10, 0, -4));
        float k1 = MathF.Tan(last.Fov / 2) * Vector3.Distance(last.Pos, new Vector3(10, 0, -4));
        check(tl.ActiveTrack.FovDriven && MathF.Abs(first.Fov - .8f) < 1e-4f && MathF.Abs(k0 - k1) < 1e-4f,
            "Tracking (Keep Framing): first node keeps its FoV, later frames keep the same framing");

        // undo
        tl.Undo();
        check(tl.ActiveTrack.TrackConstraints.Count == 0 && !tl.ActiveTrack.RotationDriven, "Tracking: undo removes the constraint");
    }
}
