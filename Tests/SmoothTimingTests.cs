using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class SmoothTimingTests
{
    public static void Run(Action<bool,string> check)
    {
        var tl = new CamTimeline { Length = 500 };
        foreach(var pair in new[] { (0,0f), (60,3f), (120,9f), (180,12f) })
            tl.Keys.Add(new Keyframe { Frame = pair.Item1, Pos = new Vector3(pair.Item2,0,0), Rot = Quaternion.Identity, Fov = .78f });
        CurveModel.Ensure(tl.ActiveTrack);
        var shot = tl.ActiveTrack.Shots[0];
        check(shot.Intervals.All(i => i.Curve(CameraChannel.Position).Keys[0].Interpolation == CurveInterpolation.Bezier), "smooth default: Bezier, not linear");
        check(shot.Intervals[0].Curve(CameraChannel.Position).Keys[0].Right.Y == 0 && shot.Intervals[^1].Curve(CameraChannel.Position).Keys[^1].Left.Y == 0, "smooth default: only whole-shot start/end ease to rest");
        for(int i=0;i+1<shot.Intervals.Count;i++)
        {
            var a=shot.Intervals[i]; var b=shot.Intervals[i+1];
            var ka=a.Curve(CameraChannel.Position).Keys[^1]; var kb=b.Curve(CameraChannel.Position).Keys[0];
            double va=SmoothTiming.Distance(a)*ka.Left.Y/ka.Left.X;
            double vb=SmoothTiming.Distance(b)*kb.Right.Y/kb.Right.X;
            check(Math.Abs(va-vb)<1e-6 && va>0, "smooth default: physical node speed continuous without stop "+i);
        }
        var space=shot.Space.Clone();
        tl.SelectNodes([tl.Keys[1].Id,tl.Keys[2].Id]);
        check(tl.MoveNodes(tl.Keys.Skip(1).Take(2).ToDictionary(k=>k.Id,k=>k.Frame+10)), "multi-node retime succeeds atomically");
        CurveModel.Ensure(tl.ActiveTrack);
        check(shot.Space.Channels.All(p=>p.Value.Keys.Select(k=>(k.Frame,k.Value)).SequenceEqual(space.Channels[p.Key].Keys.Select(k=>(k.Frame,k.Value)))), "multi-node retime preserves spatial data");
        var before=tl.Keys.Select(k=>k.Frame).ToArray();
        check(!tl.MoveNodes(tl.Keys.Skip(1).Take(2).ToDictionary(k=>k.Id,k=>180)) && before.SequenceEqual(tl.Keys.Select(k=>k.Frame)), "multi-node collision rejects entire group");
        var leg=shot.Intervals[1]; leg.Curve(CameraChannel.Position).Keys[0].Right=new Vector2(9,.17f); leg.MarkManual();
        var saved=leg.Curve(CameraChannel.Position).Clone();
        CurveModel.Ensure(tl.ActiveTrack);
        check(saved.Keys.Select(k=>(k.Frame,k.Value,k.Left,k.Right)).SequenceEqual(leg.Curve(CameraChannel.Position).Keys.Select(k=>(k.Frame,k.Value,k.Left,k.Right))), "manual timing survives synchronization");
        SmoothTiming.Apply(shot,true);
        check(shot.Intervals.All(i=>i.AutoTiming), "explicit smooth reset restores automatic timing");
    }
}
