using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

// Regression for replacing the temporary legacy-path default with node-linked Bezier curves.
public static class LegacySpatialTests
{
    public static void Run(Action<bool,string> check)
    {
        foreach(bool held in new[] { false, true })
        {
            var timeline = new CamTimeline { Length = 200 };
            timeline.Keys.AddRange(new[] {
                new Keyframe { Frame=0, Pos=new(-3,1,4), Rot=Quaternion.Identity, Fov=.7f, HoldFrames=held?12:0 },
                new Keyframe { Frame=45, Pos=new(2,3,0), Rot=Quaternion.CreateFromYawPitchRoll(.8f,.1f,.2f), Fov=.9f },
                new Keyframe { Frame=130, Pos=new(7,1,-4), Rot=Quaternion.CreateFromYawPitchRoll(1.6f,-.1f,0), Fov=.6f } });
            ShotSequence.Create(timeline.ActiveTrack,30); var shot=timeline.ActiveTrack.Shots[0];
            void Counts(string name)
            {
                var ids=timeline.Keys.Select(k=>k.Id).ToHashSet();
                foreach(var curve in shot.Space.Channels.Values)
                    check(curve.Keys.Count==ids.Count && curve.Keys.All(k=>k.NodeId.HasValue && ids.Contains(k.NodeId.Value)) && curve.Keys.Select(k=>k.NodeId).Distinct().Count()==ids.Count,name);
            }
            Counts("Bezier default has exactly one spatial key per node");
            check(shot.Space.Channels.Values.All(c=>c.Keys.All(k=>k.Interpolation==CurveInterpolation.Bezier)),"Default spatial interpolation is Bezier, not legacy fitted keys");
            var expected=shot.Sample(held?8:24);
            var added=timeline.AddSampledNode(shot,held?8:24); Counts("Graph insertion adds node and all seven channel keys");
            check(timeline.Keys.Count==4 && Vector3.Distance(shot.NodePose(added).Pos,expected.Pos)<.001f,"Inserted node retains sampled pose");
            var duplicate=timeline.AddSampledNode(shot,added.Frame);
            check(duplicate.Id==added.Id && timeline.Keys.Count==4,"Adding existing node frame selects without duplication");
            timeline.DeleteNodes([added.Id]); Counts("Graph deletion removes node and all seven keys");
            timeline.Undo(); shot=timeline.ActiveTrack.Shots[0]; Counts("Undo deletion restores matching node/key identities");
            foreach(var channel in Enum.GetValues<SpatialChannel>())
                foreach(double frame in new[] { 0d,15d,45d,75d,130d })
                {
                    double p=SpatialFrameMap.Parameter(shot,channel,frame);
                    check(Math.Abs(SpatialFrameMap.Frame(shot,channel,p)-frame)<1e-8,"Spatial frame/parameter roundtrip "+channel);
                }
        }
    }
}
