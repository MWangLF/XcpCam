using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class PathInfluenceTests
{
    public static void Run(Action<bool,string> check)
    {
        foreach(int influence in new[] { 0, 1 })
        {
            var timeline = new CamTimeline { Length = 300, PathInfluence = influence };
            foreach(var (f, x) in new[] { (0, 0f), (40, 3f), (80, 1f), (120, 6f), (160, 2f) })
                timeline.Keys.Add(new Keyframe { Frame = f, Pos = new(x, x * .5f, -x), Rot = Quaternion.Identity, Fov = .8f });
            CurveModel.Ensure(timeline.ActiveTrack, 30); var shot = timeline.ActiveTrack.Shots[0];
            double[] Leg(int leg) => Enumerable.Range(0, 65).Select(i => shot.Space.Channels[SpatialChannel.PositionX].Evaluate(leg * 1000 + i * 1000 / 64.0)).ToArray();
            var before = Enumerable.Range(0, 4).Select(Leg).ToArray();
            var node = timeline.Keys[2];
            shot.WritePose(node, new XcpFrame(new Vector3(-4, 1, 2), node.Rot, node.Fov), true, false, false, timeline.PathInfluence);
            CurveModel.Ensure(timeline.ActiveTrack, 30);
            bool Same(int leg) => Leg(leg).Zip(before[leg]).All(p => Math.Abs(p.First - p.Second) < 1e-9);
            check(!Same(1) && !Same(2), $"Influence {influence}: legs connected to the edited node change");
            if(influence == 0)
                check(Same(0) && Same(3), "Influence 0: default path only reshapes legs connected to the edited node");
            else
                check(!Same(0) && !Same(3), "Influence 1: neighbouring nodes are refitted, outer legs follow");
            check(Math.Abs(shot.NodePose(node).Pos.X + 4) < 1e-4, $"Influence {influence}: edited node reaches its new position");
            bool AllAuto() => shot.Space.Channels.Values.All(c => c.Keys.All(k => k.Mode == HandleMode.Auto));
            check(AllAuto(), $"Influence {influence}: default path keys stay Bezier Auto after edits");
            var sampled = shot.Sample(60);
            timeline.AddSampledNode(shot, 60); shot = timeline.ActiveTrack.Shots[0];
            check(AllAuto() && shot.Space.Channels.Values.All(c => c.Keys.Count == timeline.Keys.Count),
                $"Influence {influence}: inserted node keeps every key Auto, not Clamped/Free");
            check(Vector3.Distance(shot.Sample(60).Pos, sampled.Pos) < 1e-3f, $"Influence {influence}: insertion reaches sampled pose");
        }
        SequentialCaptureIsAuto(check);
        AppliedHandlesKeepInfluence(check);
    }

    private static readonly (int f, float x)[] Poses = [(0, 0f), (40, 3f), (80, 1f), (120, 6f), (160, 2f)];
    private static Keyframe Node(int f, float x) => new() { Frame = f, Pos = new(x, x * .5f, -x), Rot = Quaternion.Identity, Fov = .8f };

    // Capturing nodes one by one must give the same Bezier Auto path as fitting them all at once.
    private static void SequentialCaptureIsAuto(Action<bool,string> check)
    {
        var all = new CamTimeline { Length = 300 };
        foreach(var (f, x) in Poses) all.Keys.Add(Node(f, x));
        CurveModel.Ensure(all.ActiveTrack, 30);
        var one = new CamTimeline { Length = 300 };
        foreach(var (f, x) in Poses) { one.Keys.Add(Node(f, x)); CurveModel.Ensure(one.ActiveTrack, 30); }
        var a = all.ActiveTrack.Shots[0].Space.Channels[SpatialChannel.PositionX].Keys;
        var b = one.ActiveTrack.Shots[0].Space.Channels[SpatialChannel.PositionX].Keys;
        check(a.Count == b.Count && a.Zip(b).All(p => Vector2.Distance(p.First.Left, p.Second.Left) < 1e-4f && Vector2.Distance(p.First.Right, p.Second.Right) < 1e-4f),
            "Default path: nodes captured one at a time get Bezier Auto tangents without editing");
        check(b.Skip(1).Take(b.Count - 2).All(k => Math.Abs(k.Right.Y) > 1e-6f || Math.Abs(k.Left.Y) > 1e-6f) && b.All(k => k.Right.X > 0),
            "Default path: interior captured nodes are not left with flat/zero handles");
    }

    // After explicitly applying Auto handles, node edits must still refit neighbours within the influence range.
    private static void AppliedHandlesKeepInfluence(Action<bool,string> check)
    {
        var timeline = new CamTimeline { Length = 300, PathInfluence = 1 };
        foreach(var (f, x) in Poses) timeline.Keys.Add(Node(f, x));
        CurveModel.Ensure(timeline.ActiveTrack, 30); var shot = timeline.ActiveTrack.Shots[0];
        var ids = timeline.Keys.Select(k => k.Id).ToHashSet();
        SpatialTangents.Apply(shot.Space, timeline.Keys.OrderBy(k => k.Frame).ToList(), SpatialChannel.PositionX, ids, true);
        check(shot.Space.Channels[SpatialChannel.PositionX].Keys.All(k => k.AutomaticTangent), "Applied Auto handles stay automatic");
        var curve = shot.Space.Channels[SpatialChannel.PositionX];
        double outer = curve.Evaluate(500);
        var node = timeline.Keys[2];
        shot.WritePose(node, new XcpFrame(new Vector3(-4, 1, 2), node.Rot, node.Fov), true, false, false, timeline.PathInfluence);
        CurveModel.Ensure(timeline.ActiveTrack, 30);
        check(Math.Abs(timeline.ActiveTrack.Shots[0].Space.Channels[SpatialChannel.PositionX].Evaluate(500) - outer) > 1e-6,
            "Influence 1 after applying Auto handles: neighbouring legs are still refitted");
    }
}
