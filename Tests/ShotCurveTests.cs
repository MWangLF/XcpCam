using System;
using System.IO;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

/// <summary>Contracts for node-authoritative geometry and bounded motion-progress timing.</summary>
public static class ShotCurveTests
{
    private const float Rad = MathF.PI / 180;

    public static void Run(Action<bool, string> check)
    {
        void Scenario(string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { check(false, name + ": unexpected " + ex.GetType().Name + " — " + ex.Message); }
        }
        void Near(double actual, double expected, string name, double tolerance = 2e-5) =>
            check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
                name + $" ({actual:G12} vs {expected:G12})");
        void Pose(XcpFrame actual, XcpFrame expected, string name)
        {
            check(XcpMath.IsFinite(actual.Pos) && Vector3.Distance(actual.Pos, expected.Pos) < 3e-4f, name + ": position");
            check(XcpMath.IsFinite(actual.Rot) && Math.Abs(actual.Rot.LengthSquared() - 1) < 2e-5f &&
                Math.Abs(Quaternion.Dot(actual.Rot, Quaternion.Normalize(expected.Rot))) > 1 - 2e-5f,
                name + ": orientation (quaternion sign independent)");
            Near(actual.Fov, expected.Fov, name + ": FoV");
        }
        XcpFrame[][] Geometry(ShotCurves shot) => shot.Intervals.Select((_, i) =>
            Enumerable.Range(0, 65).Select(j => shot.SampleGeometry(i, j / 64.0)).ToArray()).ToArray();
        void GeometryEqual(ShotCurves shot, XcpFrame[][] saved, string label)
        {
            check(shot.Intervals.Count == saved.Length, label + ": interval count");
            for (int i = 0; i < Math.Min(shot.Intervals.Count, saved.Length); i++)
                for (int j = 0; j < saved[i].Length; j++)
                    Pose(shot.SampleGeometry(i, j / 64.0), saved[i][j], label + $" leg {i} u={j}/64");
        }

        Scenario("Captured endpoints and default linked progress", () =>
        {
            check(Enum.GetValues<CameraChannel>().Contains(CameraChannel.Position) && Enum.GetValues<CameraChannel>().Contains(CameraChannel.Rotation) &&
                Enum.GetValues<CameraChannel>().Contains(CameraChannel.Fov), "Time layer exposes Position / Rotation / FoV progress groups alongside spatial curves");
            var track = CurvedTrack();
            var sequence = RequiredSequence(track);
            var shot = track.Shots.Single();
            check(sequence.Segments.Count == 1 && shot.Intervals.Count == track.Keys.Count - 1,
                "One fixed-geometry motion interval is created per adjacent node pair");
            foreach (var interval in shot.Intervals)
            {
                check(interval.Linked && ReferenceEquals(interval.Curve(CameraChannel.Position), interval.Curve(CameraChannel.Rotation)) &&
                    ReferenceEquals(interval.Curve(CameraChannel.Position), interval.Curve(CameraChannel.Fov)),
                    "Default motion shares one progress curve across all three groups");
                var a = track.Keys.Single(n => n.Id == interval.StartNodeId);
                var b = track.Keys.Single(n => n.Id == interval.EndNodeId);
                check(interval.Start == a.Frame && interval.End == b.Frame, "Interval identity follows node IDs and absolute frames");
                foreach (var channel in Enum.GetValues<CameraChannel>())
                {
                    Near(interval.Evaluate(channel, interval.Start), 0, "Progress starts at zero");
                    Near(interval.Evaluate(channel, interval.End), 1, "Progress ends at one");
                    check(interval.Curve(channel).Keys[0].Interpolation == CurveInterpolation.Bezier, "Default progress uses shot-wide smooth Bezier timing");
                }
            }
            foreach (var node in track.Keys) Pose(sequence.Sample(node.Frame).pose, Captured(node), "Exact authoritative node F" + node.Frame);
            Pose(sequence.Sample(-50).pose, Captured(track.Keys[0]), "Before shot clamps to first pose");
            Pose(sequence.Sample(1000).pose, Captured(track.Keys[^1]), "After shot clamps to final pose");
            var saved = shot.Clone(); RequiredSequence(track);
            check(SameShot(shot, saved), "Repeated synchronization does not regenerate timing curves or identities");
        });

        Scenario("Seven spatial curves coexist with independent time progress", () =>
        {
            var tl = new CamTimeline();
            tl.Keys.AddRange([Node(0, Vector3.Zero, 0, 0, 0, 40), Node(80, new Vector3(8, 4, -2), 0, 0, 0, 80)]);
            RequiredSequence(tl.ActiveTrack); var shot = tl.ActiveTrack.Shots.Single(); var space = shot.Space;
            int a = space.Start, b = space.End, middle = a + (b - a) / 2;
            check(space.Channels.Count == 7 && middle > a && middle < b, "Spatial layer exposes seven independent curves on a stable parameter domain");
            var timing = shot.Intervals.Select(i => i.Clone()).ToArray();
            SetSpatial(shot, SpatialChannel.PositionX, (a, 0), (middle, 15), (b, 8));
            SetSpatial(shot, SpatialChannel.PositionY, (a, 2), (b, 10));
            SetSpatial(shot, SpatialChannel.PositionZ, (a, -4), (b, 4));
            SetSpatial(shot, SpatialChannel.Yaw, (a, 0), (b, 720));
            SetSpatial(shot, SpatialChannel.Pitch, (a, 0), (b, 0));
            SetSpatial(shot, SpatialChannel.Roll, (a, 0), (b, 0));
            SetSpatial(shot, SpatialChannel.Fov, (a, 40), (b, 80));
            check(shot.Intervals.Zip(timing).All(p => SameInterval(p.First, p.Second)), "Seven-channel geometry edits do not mutate time progress");
            check(space.Channels[SpatialChannel.PositionY].Keys.All(k => k.Frame != middle), "Independent spatial X key does not create Y key");
            var independentId = space.Channels[SpatialChannel.PositionX].Keys[1].Id;
            RequiredSequence(tl.ActiveTrack);
            check(space.Channels.Values.All(c => c.Keys.Count == tl.Keys.Count && c.Keys.All(k => k.NodeId.HasValue)) && tl.Keys.Count == 2,
                "Synchronization removes orphan keys and keeps exactly one key per camera node");
            Near(space.Channels[SpatialChannel.Yaw].Keys[^1].Value, 720, "Spatial yaw preserves authored full revolutions");
            shot.Intervals[0].Unlink();
            SetProgress(shot.Intervals[0], CameraChannel.Rotation, (0, 0), (40, .2), (80, 1));
            SetProgress(shot.Intervals[0], CameraChannel.Fov, (0, 0), (40, .8), (80, 1)); shot.Intervals[0].Constrain();
            for (int i = 0; i <= 320; i++)
            {
                float frame = i / 4f;
                Pose(shot.Sample(frame), SpatialExpected(shot, frame), "Time-to-space composition including 720-degree yaw F" + frame);
            }
            var nodePose = shot.NodePose(tl.Keys[^1]);
            Pose(nodePose, new XcpFrame(new Vector3(8, 10, 4), Quaternion.Identity, 80 * Rad), "NodePose reads edited spatial anchor rather than stale capture");
            var snapshot = shot.Clone(); var geometry = Geometry(shot);
            // Selecting/inspecting either layer is read-only: this is a model contract, not a GUI toggle test.
            var samples = Enumerable.Range(0, 161).Select(i => shot.Sample(i / 2f)).ToArray();
            foreach (var channel in Enum.GetValues<SpatialChannel>()) _ = space.Channels[channel].Evaluate(middle);
            foreach (var channel in Enum.GetValues<CameraChannel>()) _ = shot.ParameterAt(channel, 31.25);
            check(SameShot(shot, snapshot), "Inspecting both editor layers does not mutate either model");
            for (int i = 0; i < samples.Length; i++) Pose(shot.Sample(i / 2f), samples[i], "Layer inspection preserves sampled output");
            SetEase(shot.Intervals[0], CameraChannel.Position);
            check(SameSpace(shot, snapshot), "Time handle edit preserves every spatial key, ID, handle and parameter bound");
            GeometryEqual(shot, geometry, "Time handle edit preserves spatial geometry");
            check(tl.MoveKey(tl.Keys[^1], 100) == 100, "Dual-layer terminal retime succeeds"); RequiredSequence(tl.ActiveTrack);
            check(SameSpace(shot, snapshot), "Node retime leaves spatial domain and seven curves unchanged");
            tl.SetFps(60); RequiredSequence(tl.ActiveTrack, 60);
            check(SameSpace(shot, snapshot), "FPS conversion leaves spatial parameters, values and handles unchanged");
            GeometryEqual(shot, geometry, "Retime and FPS preserve edited seven-curve geometry");
        });

        Scenario("Spatial FoV clamp, full-turn undo and selective spatial writes", () =>
        {
            var tl = new CamTimeline(); tl.Keys.AddRange([Node(0, Vector3.Zero), Node(80, Vector3.One)]);
            RequiredSequence(tl.ActiveTrack); var shot = tl.ActiveTrack.Shots.Single(); int a = shot.Space.Start, b = shot.Space.End;
            SetSpatial(shot, SpatialChannel.Yaw, (a, 0), (b, 720));
            SetSpatial(shot, SpatialChannel.Fov, (a, -20), (b, 200));
            Near(shot.Sample(0).Fov, Rad, "Spatial FoV lower output bound");
            Near(shot.Sample(80).Fov, 175 * Rad, "Spatial FoV upper output bound");
            Near(shot.Space.Channels[SpatialChannel.Fov].Keys[0].Value, -20, "FoV clamp does not rewrite spatial keys");
            var curve = shot.Space.Channels[SpatialChannel.Fov];
            curve.Keys[0].Value = curve.Keys[1].Value = 80; curve.Keys[0].Interpolation = CurveInterpolation.Bezier;
            curve.Keys[0].Mode = curve.Keys[1].Mode = HandleMode.Free;
            curve.Keys[0].Right = new Vector2((b - a) / 3f, 300); curve.Keys[1].Left = new Vector2(-(b - a) / 3f, 300); shot.Space.Touch();
            Near(shot.Sample(40).Fov, 175 * Rad, "Spatial FoV Bezier overshoot is safely bounded");
            Near(curve.Keys[0].Right.Y, 300, "Output FoV clamp preserves authored spatial handles");
            var baseline = tl.ActiveTrack.Clone(); var poses = Enumerable.Range(0, 161).Select(i => shot.Sample(i / 2f)).ToArray();
            tl.Snapshot();
            var timing = shot.Intervals[0].Clone();
            var unaffected = new[] { SpatialChannel.Yaw, SpatialChannel.Pitch, SpatialChannel.Roll, SpatialChannel.Fov }
                .ToDictionary(c => c, c => shot.Space.Channels[c].Clone());
            shot.WritePose(tl.Keys[^1], new XcpFrame(new Vector3(12, 3, -7), Quaternion.Identity, 15 * Rad), position: true, rotation: false, fov: false);
            RequiredSequence(tl.ActiveTrack);
            check(unaffected.All(p => SameCurve(shot.Space.Channels[p.Key], p.Value)), "Position-only node write preserves unwrapped rotation and FoV keys/handles");
            check(SameInterval(shot.Intervals[0], timing), "Spatial anchor write preserves all time keys and handles");
            var edited = tl.ActiveTrack.Clone(); tl.Undo(); RequiredSequence(tl.ActiveTrack);
            check(SameTrack(tl.ActiveTrack, baseline), "Undo restores both layers including 720-degree values and spatial handles");
            for (int i = 0; i < poses.Length; i++) Pose(tl.ActiveTrack.Shots[0].Sample(i / 2f), poses[i], "Dual-layer undo restores dense sampled path");
            shot.Space.Channels[SpatialChannel.Yaw].Keys[^1].Value = -999; shot.Space.Touch();
            tl.Redo(); RequiredSequence(tl.ActiveTrack);
            check(SameTrack(tl.ActiveTrack, edited), "Redo owns spatial curves independently of stale shot references");
            var clone = tl.ActiveTrack.Clone(); clone.Shots[0].Space.Channels[SpatialChannel.Yaw].Keys[^1].Value = 123;
            clone.Shots[0].Space.Touch();
            Near(tl.ActiveTrack.Shots[0].Space.Channels[SpatialChannel.Yaw].Keys[^1].Value, 720, "Track clone never aliases spatial curve keys");
        });

        Scenario("Progress constraints are bounded and monotone", () =>
        {
            var interval = new MotionInterval(Guid.NewGuid(), Guid.NewGuid(), 10, 90);
            var curve = interval.Curve(CameraChannel.Position);
            SetProgress(interval, CameraChannel.Position, (10, -5), (30, .8), (50, .2), (70, 4), (90, -1));
            foreach (var key in curve.Keys)
            {
                key.Interpolation = CurveInterpolation.Bezier; key.Mode = HandleMode.Free;
                key.Left = new Vector2(500, 100); key.Right = new Vector2(-500, -100);
            }
            ProgressCurve.Constrain(curve, 10, 90);
            Near(curve.Keys[0].Value, 0, "Constrain pins first endpoint");
            Near(curve.Keys[^1].Value, 1, "Constrain pins final endpoint");
            double previous = -1;
            for (int i = 0; i <= 640; i++)
            {
                double value = interval.Evaluate(CameraChannel.Position, 10 + i / 8.0);
                check(double.IsFinite(value) && value >= 0 && value <= 1 && value + 1e-8 >= previous,
                    "Progress cannot reverse or overshoot at sample " + i);
                previous = value;
            }
            foreach (var key in curve.Keys)
                check(float.IsFinite(key.Left.X) && float.IsFinite(key.Left.Y) && float.IsFinite(key.Right.X) && float.IsFinite(key.Right.Y),
                    "Constrained handles remain finite");
            var stable = curve.Clone(); ProgressCurve.Constrain(curve, 10, 90);
            check(SameCurve(curve, stable), "Constraining free progress handles is idempotent");
            // Evaluating a curve must not rewrite the authored state behind undo or a graph drag.
            var authored = curve.Clone(); interval.Evaluate(CameraChannel.Position, 42.25);
            check(SameCurve(curve, authored), "Evaluation is read-only");
        });

        Scenario("Timing edits never alter fixed geometric samples", () =>
        {
            var tl = new CamTimeline { Length = 500 };
            tl.Keys.AddRange(CurvedTrack().Keys);
            RequiredSequence(tl.ActiveTrack);
            var shot = tl.ActiveTrack.Shots.Single();
            foreach(var key in shot.Space.Channels.Values.SelectMany(c => c.Keys)) key.AutomaticTangent = false;
            var geometry = Geometry(shot); var spatialBaseline = shot.Clone();
            var ids = shot.Intervals.Select(i => i.Id).ToArray();
            SetEase(shot.Intervals[0], CameraChannel.Position);
            shot.Intervals[1].Unlink();
            SetProgress(shot.Intervals[1], CameraChannel.Rotation, (40, 0), (55, .85), (100, 1));
            shot.Intervals[1].Constrain();
            GeometryEqual(shot, geometry, "Progress key / handle edits leave geometry fixed");
            check(tl.MoveKey(tl.Keys[1], 65) == 65, "Node timing move succeeds without reordering controls");
            RequiredSequence(tl.ActiveTrack);
            GeometryEqual(shot, geometry, "Nonuniform node retiming leaves geometric shape fixed");
            tl.Keys[0].HoldFrames = 17; tl.Keys[1].HoldFrames = 11;
            RequiredSequence(tl.ActiveTrack);
            GeometryEqual(shot, geometry, "Holds leave geometry fixed");
            tl.SetFps(60); RequiredSequence(tl.ActiveTrack, 60);
            GeometryEqual(shot, geometry, "FPS change leaves geometric shape fixed");
            check(shot.Intervals.Select(i => i.Id).SequenceEqual(ids), "Timing edits retain motion interval identities");
            check(SameSpace(shot, spatialBaseline), "Progress edits, holds, nonuniform retime and FPS preserve all seven spatial curves exactly");
            var before = tl.ActiveTrack.Clone(); long revision = tl.Revision;
            check(tl.MoveKey(tl.Keys[1], tl.Keys[2].Frame + 10) == 130 && SameTrack(tl.ActiveTrack, before) && tl.Revision == revision,
                "Timing move cannot cross another geometry control; refusal is atomic");
        });

        Scenario("Linear progress approximates uniform distance on each curved leg", () =>
        {
            var track = CurvedTrack(); RequiredSequence(track); var shot = track.Shots.Single();
            foreach(var interval in shot.Intervals) SetProgress(interval, CameraChannel.Position, (interval.Start, 0), (interval.End, 1));
            bool bent = false;
            for (int leg = 0; leg < shot.Intervals.Count; leg++)
            {
                const int count = 32;
                var points = Enumerable.Range(0, count + 1).Select(j => shot.SampleGeometry(leg, j / (double)count).Pos).ToArray();
                var distances = Enumerable.Range(0, count).Select(j => (double)Vector3.Distance(points[j], points[j + 1])).ToArray();
                double mean = distances.Average();
                check(mean > 1e-4 && distances.All(d => Math.Abs(d - mean) / mean < .12),
                    "Equal progress increments give approximately equal distance on leg " + leg);
                bent |= Vector3.Distance(points[count / 2], Vector3.Lerp(points[0], points[^1], .5f)) > .05f;
                var interval = shot.Intervals[leg];
                for (int j = 0; j <= count; j++)
                    Pose(shot.Sample(interval.Start + (interval.End - interval.Start) * j / (float)count),
                        shot.SampleGeometry(leg, j / (double)count), "Linear time maps to distance progress");
            }
            check(bent, "Uniform-speed fixture exercises a genuinely curved route, not only straight segments");
        });

        Scenario("Unlink, relink and clone alias ownership", () =>
        {
            var track = CurvedTrack(); RequiredSequence(track); var shot = track.Shots.Single(); var interval = shot.Intervals[0];
            SetEase(interval, CameraChannel.Position);
            var samples = Enumerable.Range(0, 161).Select(i => shot.Sample(i / 4f)).ToArray();
            var linkedCopy = shot.Clone();
            check(linkedCopy.Intervals[0].Linked && ReferenceEquals(linkedCopy.Intervals[0].Curve(CameraChannel.Position), linkedCopy.Intervals[0].Curve(CameraChannel.Rotation)) &&
                !ReferenceEquals(linkedCopy.Intervals[0].Curve(CameraChannel.Position), interval.Curve(CameraChannel.Position)),
                "Linked clone preserves intra-clone sharing but owns an independent curve");
            interval.Unlink();
            check(!interval.Linked && !ReferenceEquals(interval.Curve(CameraChannel.Position), interval.Curve(CameraChannel.Rotation)) &&
                !ReferenceEquals(interval.Curve(CameraChannel.Rotation), interval.Curve(CameraChannel.Fov)), "Unlink creates three independent curves");
            for (int i = 0; i < samples.Length; i++) Pose(shot.Sample(i / 4f), samples[i], "Unlink alone preserves pose F" + i / 4f);
            var rotation = interval.Curve(CameraChannel.Rotation).Clone(); var fov = interval.Curve(CameraChannel.Fov).Clone();
            SetProgress(interval, CameraChannel.Position, (0, 0), (20, .9), (40, 1)); interval.Constrain();
            check(SameCurve(interval.Curve(CameraChannel.Rotation), rotation) && SameCurve(interval.Curve(CameraChannel.Fov), fov),
                "Editing unlinked position timing cannot mutate rotation or FoV");
            var copy = shot.Clone(); var saved = Geometry(shot); var original = shot.Clone();
            copy.Intervals[0].Curve(CameraChannel.Position).Keys[1].Value = .1;
            copy.Intervals[0].Curve(CameraChannel.Rotation).Keys[0].Right = new Vector2(2, .25f);
            copy.WritePose(track.Keys[1].Clone(), new XcpFrame(new Vector3(80, -20, 10), Quaternion.Identity, 80 * Rad));
            check(SameShot(shot, original), "Unlinked clone does not alias source timing keys or handles");
            GeometryEqual(shot, saved, "Editing cloned node geometry does not mutate source path");
            interval.Link(CameraChannel.Rotation);
            check(interval.Linked && ReferenceEquals(interval.Curve(CameraChannel.Position), interval.Curve(CameraChannel.Fov)),
                "Relink shares the explicitly chosen curve");
            check(SameCurve(interval.Curve(CameraChannel.Position), rotation), "Relink adopts chosen rotation timing, not stale position timing");
        });

        Scenario("Rotation-only motion and selective pose writes", () =>
        {
            var track = new CamTrack(); var p = new Vector3(3, 4, -2);
            track.Keys.AddRange([Node(0, p, 0, 5, -10, 50), Node(60, p, 140, -25, 35, 90)]);
            RequiredSequence(track); var shot = track.Shots.Single(); var interval = shot.Intervals.Single();
            interval.Unlink(); SetProgress(interval, CameraChannel.Rotation, (0, 0), (30, .85), (60, 1)); interval.Constrain();
            for (int i = 0; i <= 120; i++)
            {
                var pose = shot.Sample(i / 2f);
                check(Vector3.Distance(pose.Pos, p) < 1e-6 && XcpMath.IsFinite(pose.Rot), "Zero-length position leg remains finite and stationary");
            }
            check(Math.Abs(Quaternion.Dot(shot.Sample(30).Rot, track.Keys[0].Rot)) < .99, "Rotation progresses even with zero position length");
            Near(shot.Sample(30).Fov, 70 * Rad, "Unlinked FoV keeps its own linear progress");
            var timing = interval.Clone(); var old = Captured(track.Keys[1]);
            var replacement = new XcpFrame(new Vector3(99), Quaternion.CreateFromYawPitchRoll(70 * Rad, 10 * Rad, -5 * Rad), 15 * Rad);
            shot.WritePose(track.Keys[1], replacement, position: false, rotation: true, fov: false);
            RequiredSequence(track);
            Pose(shot.Sample(60), new XcpFrame(old.Pos, replacement.Rot, old.Fov), "Rotation-only WritePose preserves node position and FoV");
            check(SameInterval(interval, timing), "Pose edit never rewrites progress keys, identities or handles");
        });

        Scenario("Dense exact insertion, including independent progress and holds", () =>
        {
            foreach (bool unlinked in new[] { false, true })
            foreach (int hold in new[] { 0, 12 })
            foreach (int frame in new[] { 8, 24 })
            {
                var track = CurvedTrack(); track.Keys[0].HoldFrames = hold;
                RequiredSequence(track); var shot = track.Shots.Single(); var interval = shot.Intervals[0];
                SetEase(interval, CameraChannel.Position);
                if (unlinked)
                {
                    interval.Unlink();
                    SetProgress(interval, CameraChannel.Rotation, (0, 0), (16, .75), (40, 1));
                    SetProgress(interval, CameraChannel.Fov, (0, 0), (28, .3), (40, 1)); interval.Constrain();
                }
                var samples = Enumerable.Range(0, 641).Select(i => shot.Sample(i / 4f)).ToArray();
                var expected = shot.Sample(frame); var inserted = Node(frame, new Vector3(999), 170, 0, 0, 10);
                shot.InsertNode(inserted); track.Keys.Add(inserted);
                var sequence = RequiredSequence(track);
                string label = $"Insert F{frame} linked={!unlinked} hold={hold}";
                check(shot.Intervals.Count == 4 && sequence.Segments.Count == 1 && !sequence.CutFrames.Any(), label + ": exactly one extra motion interval, no cut");
                Pose(Captured(inserted), expected, label + ": inserted node is authoritative sampled pose");
                check(shot.Intervals.Count(i => i.EndNodeId == inserted.Id) == 1 && shot.Intervals.Count(i => i.StartNodeId == inserted.Id) == 1,
                    label + ": subdivision binds both sides to inserted identity");
                check(shot.Intervals.Take(2).All(i => i.Linked == !unlinked), label + ": link mode survives subdivision");
                foreach(var curve in shot.Space.Channels.Values)
                    check(curve.Keys.Count == track.Keys.Count && curve.Keys.All(k => k.NodeId.HasValue), label + ": one spatial key per node");
                Pose(sequence.Sample(frame).pose, expected, label + ": new node frame reaches sampled pose");
                var after = Enumerable.Range(0, samples.Length).Select(i => shot.Sample(i / 4f)).ToArray();
                RequiredSequence(track);
                for (int i = 0; i < after.Length; i += 7) Pose(shot.Sample(i / 4f), after[i], label + ": repeated bind remains stable F" + i / 4f);
            }
        });

        Scenario("Held subdivision survives clone, undo, FPS and affine retime", () =>
        {
            foreach (int insertionFrame in new[] { 8, 24 })
            {
                var tl = new CamTimeline { Length = 200 }; tl.Keys.AddRange(CurvedTrack().Keys); tl.Keys[0].HoldFrames = 12;
                RequiredSequence(tl.ActiveTrack); var shot = tl.ActiveTrack.Shots.Single();
                SetEase(shot.Intervals[0], CameraChannel.Position); shot.Intervals[0].Unlink();
                SetProgress(shot.Intervals[0], CameraChannel.Rotation, (0, 0), (16, .75), (40, 1)); shot.Intervals[0].Constrain();
                var before = Enumerable.Range(0, 641).Select(i => shot.Sample(i / 4f)).ToArray();
                tl.Snapshot(); var inserted = Node(insertionFrame, Vector3.Zero); shot.InsertNode(inserted); tl.Keys.Add(inserted);
                RequiredSequence(tl.ActiveTrack);
                var afterInsertion = Enumerable.Range(0, 641).Select(i => shot.Sample(i / 4f)).ToArray();
                var clone = shot.Clone(); clone.Rescale(2);
                for (int i = 0; i < before.Length; i++) Pose(clone.Sample(i / 2f), afterInsertion[i], "Held insertion clone + Rescale preserves source-time pose");
                // Exercise interval-level affine mapping as well as whole-shot FPS conversion.
                var retimed = shot.Clone();
                foreach (var leg in retimed.Intervals) leg.Retime(leg.Start * 2, leg.End * 2);
                for (int i = 0; i < before.Length; i++) Pose(retimed.Sample(i / 2f), afterInsertion[i], "Held insertion interval Retime preserves split timing");
                tl.Undo(); RequiredSequence(tl.ActiveTrack);
                check(tl.Keys.Count == 4, "Undo insertion removes inserted authoritative node");
                for (int i = 0; i < before.Length; i += 3) Pose(tl.ActiveTrack.Shots[0].Sample(i / 4f), before[i], "Held insertion undo exact pose");
                tl.Redo(); RequiredSequence(tl.ActiveTrack);
                check(tl.Keys.Count == 5, "Redo insertion restores split node and timing");
                for (int i = 0; i < before.Length; i += 3) Pose(tl.ActiveTrack.Shots[0].Sample(i / 4f), afterInsertion[i], "Held insertion redo restores node-linked pose");
                tl.SetFps(60); RequiredSequence(tl.ActiveTrack, 60);
                for (int i = 0; i < before.Length; i++) Pose(tl.ActiveTrack.Shots[0].Sample(i / 2f), afterInsertion[i], "Held insertion FPS preserves node-linked source-time pose");
            }
        });

        Scenario("Whole-pose holds change timing only", () =>
        {
            var track = CurvedTrack(); RequiredSequence(track); var shot = track.Shots.Single();
            foreach(var key in shot.Space.Channels.Values.SelectMany(c => c.Keys)) key.AutomaticTangent = false;
            var geometry = Geometry(shot);
            track.Keys[0].HoldFrames = 12; RequiredSequence(track);
            foreach (float frame in new[] { 0f, 1f, 8.75f, 11.9f, 12f })
                Pose(shot.Sample(frame), Captured(track.Keys[0]), "Hold freezes position, rotation and FoV F" + frame);
            var progress = shot.Intervals[0].Evaluate(CameraChannel.Position, 26);
            Pose(shot.Sample(26), shot.SampleGeometry(0, progress), "Remaining duration traverses unchanged leg using smooth progress after release");
            GeometryEqual(shot, geometry, "Adding a hold cannot reshape neighboring legs");
            track.Keys[0].HoldFrames = 1000; RequiredSequence(track);
            Pose(shot.Sample(39.99f), Captured(track.Keys[0]), "Overlong hold is clipped before next node");
            Pose(shot.Sample(40), Captured(track.Keys[1]), "Next node wins at full-duration hold boundary");
            check(shot.Holds.Contains((0, 40)) && shot.End == 160, "Earlier hold cannot extend shot bounds");
            GeometryEqual(shot, geometry, "Clipped hold still leaves geometry unchanged");
        });

        Scenario("Undo rebind restores authoritative geometry and progress", () =>
        {
            var tl = new CamTimeline(); tl.Keys.AddRange(CurvedTrack().Keys); RequiredSequence(tl.ActiveTrack);
            var staleShot = tl.ActiveTrack.Shots.Single(); var staleNode = tl.Keys[1];
            staleShot.Intervals[0].Unlink(); SetEase(staleShot.Intervals[0], CameraChannel.Rotation);
            var baseline = tl.ActiveTrack.Clone(); var before = Enumerable.Range(0, 161).Select(i => staleShot.Sample(i)).ToArray();
            tl.Snapshot();
            staleShot.WritePose(staleNode, new XcpFrame(new Vector3(25, 18, -9), Quaternion.Identity, 95 * Rad));
            SetProgress(staleShot.Intervals[0], CameraChannel.Fov, (0, 0), (20, .95), (40, 1));
            staleShot.Intervals[0].Constrain(); RequiredSequence(tl.ActiveTrack);
            var edited = tl.ActiveTrack.Clone(); var after = Enumerable.Range(0, 161).Select(i => staleShot.Sample(i)).ToArray();
            tl.Undo(); RequiredSequence(tl.ActiveTrack);
            check(SameTrack(tl.ActiveTrack, baseline), "Undo restores IDs, progress modes/handles, link mode, holds and authoritative nodes");
            check(!ReferenceEquals(tl.ActiveTrack.Shots[0], staleShot) && !ReferenceEquals(tl.Keys[1], staleNode), "Undo replaces mutable graph and node references");
            for (int i = 0; i < before.Length; i++) Pose(tl.ActiveTrack.Shots[0].Sample(i), before[i], "Undo rebind geometry F" + i);
            staleNode.Pos = new Vector3(-999); staleShot.Intervals[0].Curve(CameraChannel.Position).Keys[0].Right = Vector2.Zero;
            tl.Redo(); RequiredSequence(tl.ActiveTrack);
            check(SameTrack(tl.ActiveTrack, edited), "Redo is independent of stale pre-undo references");
            for (int i = 0; i < after.Length; i++) Pose(tl.ActiveTrack.Shots[0].Sample(i), after[i], "Redo rebind geometry F" + i);
            tl.Undo(); RequiredSequence(tl.ActiveTrack);
            var restored = tl.Keys[1]; var changed = Captured(restored) with { Pos = restored.Pos + new Vector3(1, 2, 3) };
            tl.ActiveTrack.Shots[0].WritePose(restored, changed); RequiredSequence(tl.ActiveTrack);
            Pose(tl.ActiveTrack.Shots[0].Sample(restored.Frame), changed, "Restored shot binds to current track nodes, not cloned stale nodes");
        });

        Scenario("Retime collisions refuse all channels atomically", () =>
        {
            var interval = new MotionInterval(Guid.NewGuid(), Guid.NewGuid(), 0, 100); interval.Unlink();
            SetProgress(interval, CameraChannel.Fov, (0, 0), (10, .2), (11, .3), (100, 1));
            interval.Constrain(); var before = interval.Clone(); bool rejected = false;
            try { interval.Retime(0, 2); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && SameInterval(interval, before), "Late-channel rounding collision rolls back every curve and interval bound");
            var tl = new CamTimeline { Length = 200 }; tl.Keys.AddRange([Node(0, Vector3.Zero), Node(100, Vector3.One), Node(180, Vector3.One * 2)]);
            RequiredSequence(tl.ActiveTrack); var shot = tl.ActiveTrack.Shots.Single();
            SetProgress(shot.Intervals[0], CameraChannel.Position, (0, 0), (10, .2), (11, .3), (100, 1));
            shot.Intervals[0].Constrain(); var saved = tl.ActiveTrack.Clone(); long revision = tl.Revision;
            check(tl.MoveKey(tl.Keys[1], 2) == 100 && SameTrack(tl.ActiveTrack, saved) && tl.Revision == revision,
                "Node move collision rolls back node, both neighboring intervals and revision");
            check(tl.MoveKey(tl.Keys[1], 180) == 100 && SameTrack(tl.ActiveTrack, saved), "Occupied node frame is refused atomically");
        });

        Scenario("FPS scales timing but not progress or geometry", () =>
        {
            var tl = new CamTimeline { Length = 200, Playhead = 17.5f }; tl.Keys.AddRange(CurvedTrack().Keys); tl.Keys[0].HoldFrames = 8;
            RequiredSequence(tl.ActiveTrack); var shot = tl.ActiveTrack.Shots.Single();
            SetEase(shot.Intervals[0], CameraChannel.Position); shot.Intervals[1].Unlink(); SetEase(shot.Intervals[1], CameraChannel.Fov);
            var saved = shot.Clone(); var geometry = Geometry(shot);
            var samples = Enumerable.Range(0, 321).Select(i => shot.Sample(i / 2f)).ToArray();
            tl.SetFps(60);
            check(tl.Fps == 60 && tl.Length == 400 && tl.Playhead == 35 && tl.Keys[1].Frame == 80 && tl.Keys[0].HoldFrames == 16,
                "FPS conversion preserves seconds for nodes, holds, length and playhead");
            for (int i = 0; i < saved.Intervals.Count; i++)
            {
                var old = saved.Intervals[i]; var current = shot.Intervals[i];
                check(current.Start == old.Start * 2 && current.End == old.End * 2 && current.HoldFrames == old.HoldFrames * 2 && current.Linked == old.Linked,
                    "FPS conversion scales interval timing and retains link mode");
                foreach (var channel in Enum.GetValues<CameraChannel>())
                    foreach (var key in old.Curve(channel).Keys)
                    {
                        var now = current.Curve(channel).Keys.Single(k => k.Id == key.Id);
                        check(now.Frame == key.Frame * 2 && now.Value == key.Value && now.Left == new Vector2(key.Left.X * 2, key.Left.Y) &&
                            now.Right == new Vector2(key.Right.X * 2, key.Right.Y), "FPS scales only key and handle time coordinates");
                    }
            }
            RequiredSequence(tl.ActiveTrack, 60); GeometryEqual(shot, geometry, "FPS conversion preserves fixed geometry");
            for (int i = 0; i < samples.Length; i++) Pose(shot.Sample(i), samples[i], "FPS same-time pose F" + i);
            var frameSaved = tl.ActiveTrack.Clone(); float playhead = tl.Playhead; int length = tl.Length;
            tl.SetFps(24, keepSeconds: false);
            check(tl.Fps == 24 && tl.Playhead == playhead && tl.Length == length &&
                tl.Keys.Select(n => (n.Frame, n.HoldFrames)).SequenceEqual(frameSaved.Keys.Select(n => (n.Frame, n.HoldFrames))) &&
                shot.Intervals.Zip(frameSaved.Shots[0].Intervals).All(p => SameInterval(p.First, p.Second)),
                "Frame-preserving FPS change leaves all node, interval, key and handle coordinates untouched");
        });

        Scenario("FPS rounding and cross-track collision atomicity", () =>
        {
            var rounding = new CamTimeline(); rounding.Keys.AddRange([Node(1, Vector3.Zero), Node(61, Vector3.One)]);
            RequiredSequence(rounding.ActiveTrack); rounding.SetFps(15); RequiredSequence(rounding.ActiveTrack, 15);
            var leg = rounding.ActiveTrack.Shots[0].Intervals[0];
            check(leg.Start == rounding.Keys[0].Frame && leg.End == rounding.Keys[1].Frame && leg.Start == 1 && leg.End == 31,
                "Half-frame FPS rounding uses one convention for nodes and interval endpoints");
            foreach (bool nodeCollision in new[] { false, true })
            {
                var tl = new CamTimeline { Length = 120, Playhead = 11.5f }; var lower = tl.ActiveTrack;
                lower.Keys.AddRange([Node(0, Vector3.Zero), Node(60, Vector3.One)]);
                var upper = tl.AddTrack(); upper.Keys.AddRange(nodeCollision
                    ? new[] { Node(10, Vector3.Zero), Node(11, Vector3.One) }
                    : new[] { Node(0, Vector3.Zero), Node(60, Vector3.One) });
                RequiredSequence(lower); RequiredSequence(upper);
                if (!nodeCollision)
                {
                    var interval = upper.Shots[0].Intervals[0]; interval.Unlink();
                    SetProgress(interval, CameraChannel.Fov, (0, 0), (10, .2), (11, .3), (60, 1)); interval.Constrain();
                }
                var saved = tl.Tracks.Select(t => t.Clone()).ToArray(); long revision = tl.Revision; bool rejected = false;
                try { tl.SetFps(1); } catch (InvalidOperationException) { rejected = true; }
                check(rejected && tl.Fps == 30 && tl.Length == 120 && tl.Playhead == 11.5f && tl.Revision == revision &&
                    tl.Tracks.Select((t, i) => SameTrack(t, saved[i])).All(x => x),
                    "FPS " + (nodeCollision ? "node" : "progress key") + " collision leaves all tracks and timeline state unchanged");
            }
        });

        Scenario("Hard cuts isolate geometry and preserve retained interval timing", () =>
        {
            var track = CurvedTrack(); RequiredSequence(track); var shot = track.Shots.Single();
            SetEase(shot.Intervals[0], CameraChannel.Position); shot.Intervals[2].Unlink(); SetEase(shot.Intervals[2], CameraChannel.Rotation);
            var outgoingTiming = shot.Intervals[0].Clone(); var incomingTiming = shot.Intervals[2].Clone();
            track.Keys[2].CutIn = true; var sequence = RequiredSequence(track);
            check(sequence.Segments.Count == 1 && sequence.CutFrames.SequenceEqual(new[] { 100 }), "Constant cut keeps one editor domain and exact cut frame");
            check(SameInterval(track.Shots[0].Intervals[0], outgoingTiming) && SameInterval(track.Shots[0].Intervals[2], incomingTiming),
                "Cut retains timing on unaffected node pairs instead of reseeding curves");
            var outgoing = track.Shots[0];
            outgoing.WritePose(track.Keys[1], new XcpFrame(new Vector3(-200, 7, 3), Quaternion.Identity, 75 * Rad)); RequiredSequence(track);
            Pose(sequence.Sample(99.99f).pose, Captured(track.Keys[1]), "Gap before cut holds outgoing terminal pose");
            Pose(sequence.Sample(100).pose, Captured(track.Keys[2]), "Exact cut uses incoming pose without blending");
            check(sequence.SpeedAt(99.75f) == null, "Speed across hard cut is undefined");
            track.Keys[2].CutIn = false; var merged = RequiredSequence(track);
            check(merged.Segments.Count == 1 && !merged.CutFrames.Any() && track.Shots[0].Intervals.Count == 3, "Cancelling cut restores one shot and connecting leg");
            check(SameInterval(track.Shots[0].Intervals[0], outgoingTiming) && SameInterval(track.Shots[0].Intervals[2], incomingTiming),
                "Cancelling cut preserves authored timing on both retained legs");
            var stable = track.Shots[0].Clone(); RequiredSequence(track);
            check(SameShot(track.Shots[0], stable), "Repeated synchronization after cut merge is idempotent");
        });

        Scenario("Target-relative transform round trip and output covariance", () =>
        {
            var track = CurvedTrack(); RequiredSequence(track); var shot = track.Shots.Single(); var saved = shot.Clone();
            foreach (var basePos in new[] { Vector3.Zero, new Vector3(100, -12, 37) })
            foreach (var baseRot in new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(1.2f, -.3f, .4f) })
                for (int i = 0; i <= 160; i += 5)
                {
                    var local = shot.Sample(i); var view = XcpMath.BrioPlaybackView(local.Pos, local.Rot, basePos, baseRot);
                    check(XcpMath.DecomposeView(view, out var worldPos, out var worldRot), "Relative playback produces decomposable world view");
                    Pose(new XcpFrame(worldPos, worldRot, local.Fov),
                        new XcpFrame(basePos + Vector3.Transform(local.Pos, baseRot), Quaternion.Normalize(baseRot * local.Rot), local.Fov),
                        "Changing target applies rigid world transform, not a geometry rebuild");
                    var roundTrip = XcpMath.WorldToRelative(worldPos, worldRot, basePos, baseRot);
                    Pose(new XcpFrame(roundTrip.pos, roundTrip.rot, local.Fov), local, "Target transform relative round trip");
                }
            check(SameShot(shot, saved), "Target-relative playback never rewrites local geometry or progress");
        });

        Scenario("Composition holds uncovered gaps", () =>
        {
            var tl = new CamTimeline(); var first = tl.ActiveTrack;
            first.Keys.AddRange([Node(10, new Vector3(1, 2, 3), 20, 10, -5, 45), Node(20, new Vector3(4, 5, 6), 70, -10, 15, 80)]);
            var second = tl.AddTrack(); second.Keys.AddRange([Node(40, new Vector3(20, 30, 40), 120, 15, 20, 90), Node(50, new Vector3(50, 60, 70), 150, 20, 30, 100)]);
            var composer = ShotComposer.Create(tl, new PathSettings()) ?? throw new InvalidOperationException("Missing gap composer");
            check(composer.TopTrackAt(30) == -1 && composer.TrackAt(30) == 1, "Gap reports no covering track and retains outgoing ownership");
            Pose(composer.Sample(0), Captured(first.Keys[0]), "Before coverage uses earliest pose");
            foreach (float f in new[] { 20.5f, 25f, 39.99f }) Pose(composer.Sample(f), Captured(first.Keys[1]), "Gap holds complete outgoing pose");
            Pose(composer.Sample(40), Captured(second.Keys[0]), "Gap ends at exact incoming pose");
            check(composer.CutFrames.SequenceEqual(new[] { 40 }), "Entering uncovered hold creates no spurious cut");
            var bake = composer.BakeForTimeline(30, 13);
            check(bake.Any(k => k.Frame == 39 && !k.Step) && bake.Any(k => k.Frame == 40 && k.Step), "Sparse bake brackets hard cut after gap");
        });

        Scenario("Multi-track composition, bake and serialized export", () =>
        {
            var tl = new CamTimeline(); var lower = tl.ActiveTrack;
            lower.Keys.AddRange([Node(0, new Vector3(0, 4, -2), 0, 0, 0, 50), Node(90, new Vector3(9, 4, -2), 90, 0, 0, 80)]);
            RequiredSequence(lower); SetEase(lower.Shots[0].Intervals[0], CameraChannel.Position);
            var lowerShot = lower.Shots[0];
            int sa = lowerShot.Space.Start, sb = lowerShot.Space.End;
            SetSpatial(lowerShot, SpatialChannel.Yaw, (sa, 0), (sb, 720));
            SetSpatial(lowerShot, SpatialChannel.PositionX, (sa, 0), (sa + (sb - sa) / 2, 12), (sb, 9));
            lowerShot.Intervals[0].Unlink();
            SetProgress(lowerShot.Intervals[0], CameraChannel.Rotation, (0, 0), (45, .3), (90, 1)); lowerShot.Intervals[0].Constrain();
            var upper = tl.AddTrack(); var cut = Node(30, new Vector3(200, 0, 0), 110); cut.CutIn = true;
            upper.Keys.AddRange([Node(20, new Vector3(100, 0, 0), 10), Node(29, new Vector3(109, 0, 0), 70), cut, Node(40, new Vector3(210, 0, 0), 160)]);
            var low = RequiredSequence(lower); var high = RequiredSequence(upper);
            XcpFrame Expected(float f) => f >= 20 && f <= 40 ? high.Sample(f).pose : SpatialExpected(lowerShot, f);
            var composer = ShotComposer.Create(tl, new PathSettings()) ?? throw new InvalidOperationException("Missing composer");
            check(composer.FirstFrame == 0 && composer.LastFrame == 90 && composer.CutFrames.SequenceEqual(new[] { 20, 30, 41 }),
                "Composition cuts on overlay entry, internal cut and lower-track return");
            check(composer.Runs.SequenceEqual(new[] { new OutputRun(0, 19, 1), new OutputRun(20, 40, 0), new OutputRun(41, 90, 1) }),
                "Output ownership follows track priority");
            foreach (float f in new[] { 0f, 10.5f, 19f, 20f, 22.25f, 29.5f, 30f, 35.5f, 40f, 41f, 70.25f, 90f })
                Pose(composer.Sample(f), Expected(f), "Composition uses progress geometry sampler F" + f);
            check(composer.SpeedAt(19.75f) == null && composer.SpeedAt(29.75f) == null, "Composed speed is undefined across either kind of cut");
            var bake = composer.BakeForTimeline(60, 17);
            check(bake.Select(k => k.Frame).Distinct().Count() == bake.Count && bake[0].Frame == 0 && bake[^1].Frame == 180,
                "Target-FPS timeline bake includes ordered unique endpoints");
            check(bake.Where(k => k.Step).Select(k => k.Frame).SequenceEqual(new[] { 40, 60, 81 }), "Target-FPS bake preserves exact sampled transitions");
            foreach (int f in new[] { 40, 60, 81 }) check(bake.Any(k => k.Frame == f - 1), "Sparse bake brackets cut F" + f);
            foreach (var key in bake) Pose(key.Pose, Expected(key.Frame / 2f), "Timeline bake matches source sampler");
            var xcp = composer.BakeXcp(30); check(xcp.Count == 91, "30-FPS XCP includes both duration endpoints");
            for (int i = 0; i < xcp.Count; i++)
            {
                Pose(xcp[i], Expected(i), "Uniform XCP sample F" + i);
                if (i > 0) check(Quaternion.Dot(xcp[i - 1].Rot, xcp[i].Rot) >= -1e-6, "Export normalizes quaternion hemisphere");
            }
            var twice = composer.BakeXcp(60); check(twice.Count == 181, "Export FPS controls uniform sample count");
            for (int i = 0; i < twice.Count; i++) Pose(twice[i], Expected(i / 2f), "Fractional source-frame XCP sample");
            string path = Path.Combine(Path.GetTempPath(), "XcpCam-ShotCurveTests-" + Guid.NewGuid().ToString("N") + ".xcp");
            try
            {
                XcpWriter.Write(path, xcp); using var reader = new BinaryReader(File.OpenRead(path));
                check(new string(reader.ReadChars(4)) == "XCP1" && reader.ReadUInt32() == xcp.Count + 1, "Serialized XCP header includes Brio sentinel");
                for (int i = 0; i <= xcp.Count; i++)
                {
                    var value = new XcpFrame(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle());
                    Pose(value, Expected(Math.Min(i, xcp.Count - 1)), "Serialized XCP pose record " + i);
                }
                check(reader.BaseStream.Position == reader.BaseStream.Length, "Export contains exactly expected records");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            upper.Muted = true; var muted = ShotComposer.Create(tl, new PathSettings()) ?? throw new InvalidOperationException("Missing muted composer");
            Pose(muted.Sample(25), low.Sample(25).pose, "Muted overlay exposes lower motion sampler");
            upper.Muted = false; upper.Solo = true;
            var solo = ShotComposer.Create(tl, new PathSettings()) ?? throw new InvalidOperationException("Missing solo composer");
            check(solo.FirstFrame == 20 && solo.LastFrame == 40 && solo.IsActive(0) && !solo.IsActive(1), "Solo restricts composition and export coverage");
            Pose(solo.Sample(-100), Expected(20), "Before solo coverage clamps to first pose");
            Pose(solo.Sample(100), Expected(40), "After solo coverage clamps to final pose");
        });
    }

    private static Keyframe Node(int frame, Vector3 position, float yaw = 0, float pitch = 0, float roll = 0, float fov = 60) =>
        new() { Frame = frame, Pos = position, Rot = Quaternion.CreateFromYawPitchRoll(yaw * Rad, pitch * Rad, roll * Rad), Fov = fov * Rad };
    private static XcpFrame Captured(Keyframe node) => new(node.Pos, node.Rot, node.Fov);
    private static CamTrack CurvedTrack()
    {
        var track = new CamTrack();
        track.Keys.AddRange([Node(0, new Vector3(-4, 1, 0), -20, 5, -10, 40), Node(40, new Vector3(0, 4, 7), 35, -15, 20, 65),
            Node(100, new Vector3(8, -2, 3), 110, 20, -15, 90), Node(160, new Vector3(12, 3, -5), 150, -10, 25, 55)]);
        return track;
    }
    private static ShotSequence RequiredSequence(CamTrack track, int fps = 30) =>
        ShotSequence.Create(track, fps) ?? throw new InvalidOperationException("Expected a nonempty shot sequence");
    private static void SetProgress(MotionInterval interval, CameraChannel channel, params (int frame, double progress)[] values)
    {
        interval.MarkManual();
        var curve = interval.Curve(channel); curve.Keys.Clear();
        foreach (var (frame, progress) in values)
            curve.Keys.Add(new CurveKey { Frame = frame, Value = progress,
                NodeId = frame == interval.Start ? interval.StartNodeId : frame == interval.End ? interval.EndNodeId : null,
                Interpolation = CurveInterpolation.Linear, Mode = HandleMode.Free });
        curve.Sort();
    }
    private static void SetEase(MotionInterval interval, CameraChannel channel)
    {
        SetProgress(interval, channel, (interval.Start, 0), (interval.End, 1));
        var curve = interval.Curve(channel); float duration = interval.End - interval.Start;
        curve.Keys[0].Interpolation = CurveInterpolation.Bezier;
        curve.Keys[0].Right = new Vector2(duration * .2f, .04f);
        curve.Keys[^1].Left = new Vector2(-duration * .35f, -.1f);
        interval.Constrain();
    }
    private static void SetSpatial(ShotCurves shot, SpatialChannel channel, params (int parameter, double value)[] values)
    {
        var curve = shot.Space.Channels[channel];
        var owners = curve.Keys.ToDictionary(k => k.Frame, k => k.NodeId);
        curve.Keys.Clear();
        foreach (var (parameter, value) in values)
            curve.Keys.Add(new CurveKey { Frame = parameter, Value = value, NodeId = owners.TryGetValue(parameter, out var owner) ? owner : null,
                Interpolation = CurveInterpolation.Linear, Mode = HandleMode.Free });
        curve.Sort(); shot.Space.Touch();
    }
    private static XcpFrame SpatialExpected(ShotCurves shot, double frame)
    {
        double p = shot.ParameterAt(CameraChannel.Position, frame), r = shot.ParameterAt(CameraChannel.Rotation, frame),
            f = shot.ParameterAt(CameraChannel.Fov, frame);
        float V(SpatialChannel channel, double parameter) => (float)shot.Space.Channels[channel].Evaluate(parameter);
        return new XcpFrame(new Vector3(V(SpatialChannel.PositionX, p), V(SpatialChannel.PositionY, p), V(SpatialChannel.PositionZ, p)),
            Quaternion.CreateFromYawPitchRoll(V(SpatialChannel.Yaw, r) * Rad, V(SpatialChannel.Pitch, r) * Rad, V(SpatialChannel.Roll, r) * Rad),
            Math.Clamp(V(SpatialChannel.Fov, f), 1, 175) * Rad);
    }
    private static bool SameSpace(ShotCurves a, ShotCurves b) => a.Space.Start == b.Space.Start && a.Space.End == b.Space.End &&
        a.Space.Channels.Count == b.Space.Channels.Count &&
        a.Space.Channels.All(p => b.Space.Channels.TryGetValue(p.Key, out var c) && SameCurve(p.Value, c));
    private static bool SameCurve(FCurve a, FCurve b) => a.Keys.Count == b.Keys.Count &&
        a.Keys.Zip(b.Keys).All(p => p.First.Id == p.Second.Id && p.First.NodeId == p.Second.NodeId &&
            p.First.Frame == p.Second.Frame && p.First.Value == p.Second.Value && p.First.Left == p.Second.Left &&
            p.First.Right == p.Second.Right && p.First.Mode == p.Second.Mode && p.First.Interpolation == p.Second.Interpolation);
    private static bool SameInterval(MotionInterval a, MotionInterval b) => a.Id == b.Id && a.StartNodeId == b.StartNodeId &&
        a.EndNodeId == b.EndNodeId && a.Start == b.Start && a.End == b.End && a.HoldFrames == b.HoldFrames && a.Linked == b.Linked &&
        a.Channels.Count == b.Channels.Count && a.Channels.All(p => b.Channels.TryGetValue(p.Key, out var c) && SameCurve(p.Value, c));
    private static bool SameShot(ShotCurves a, ShotCurves b) => a.Id == b.Id && a.AnchorNodeId == b.AnchorNodeId &&
        a.Start == b.Start && a.End == b.End && a.Name == b.Name && SameSpace(a, b) && a.Holds.SequenceEqual(b.Holds) &&
        a.Intervals.Count == b.Intervals.Count && a.Intervals.Zip(b.Intervals).All(p => SameInterval(p.First, p.Second));
    private static bool SameTrack(CamTrack a, CamTrack b) => a.Name == b.Name && a.Color == b.Color && a.Muted == b.Muted &&
        a.Solo == b.Solo && a.Keys.Count == b.Keys.Count && a.Shots.Count == b.Shots.Count &&
        a.Keys.Zip(b.Keys).All(p => p.First.Id == p.Second.Id && p.First.Frame == p.Second.Frame &&
            p.First.HoldFrames == p.Second.HoldFrames && p.First.Pos == p.Second.Pos && p.First.Rot == p.Second.Rot &&
            p.First.Fov == p.Second.Fov && p.First.CutIn == p.Second.CutIn && p.First.CutAuto == p.Second.CutAuto &&
            p.First.LastGap == p.Second.LastGap && p.First.TimeSec == p.Second.TimeSec && p.First.HoldSec == p.Second.HoldSec) &&
        a.Shots.Zip(b.Shots).All(p => SameShot(p.First, p.Second));
}
