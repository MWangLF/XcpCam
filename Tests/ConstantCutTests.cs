using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class ConstantCutTests
{
    public static void Run(Action<bool,string> check)
    {
        foreach(var mode in Enum.GetValues<CurveEditorMode>())
        {
            var timeline=new CamTimeline { Length=180 };
            timeline.Keys.AddRange(new[] {
                new Keyframe { Frame=0, Pos=Vector3.Zero, Rot=Quaternion.Identity, Fov=.7f },
                new Keyframe { Frame=60, Pos=new(8,2,3), Rot=Quaternion.CreateFromYawPitchRoll(1,0,0), Fov=.9f },
                new Keyframe { Frame=120, Pos=new(9,4,0), Rot=Quaternion.Identity, Fov=.8f } });
            var sequence=ShotSequence.Create(timeline.ActiveTrack,30)!; var shot=timeline.ActiveTrack.Shots[0]; shot.SetEditorMode(mode);
            var a=shot.NodePose(timeline.Keys[0]); var b=shot.NodePose(timeline.Keys[1]);
            ConstantCuts.Set(shot,timeline.Keys[1].Id,true); timeline.Keys[1].CutIn=true;
            sequence=ShotSequence.Create(timeline.ActiveTrack,30)!;
            check(sequence.Segments.Count==1 && timeline.ActiveTrack.Shots.Count==1,"Cut does not split editor track "+mode);
            check(timeline.Keys[1].CutIn && sequence.CutFrames.SequenceEqual(new[]{60}),"Constant cut frame is recognized "+mode);
            check(Vector3.Distance(shot.Sample(59.99f).Pos,a.Pos)<1e-5 && Math.Abs(Quaternion.Dot(shot.Sample(59.99f).Rot,a.Rot))> .99999,
                "All-channel constant holds entire previous pose "+mode);
            check(Vector3.Distance(shot.Sample(60).Pos,b.Pos)<1e-5,"Cut arrives at exact node frame "+mode);
            var comp=ShotComposer.Create(timeline,new PathSettings())!;
            check(comp.CutFrames.Contains(60) && comp.BakeForTimeline(30,10).Any(k=>k.Frame==60 && k.Step),"Composer and Step bake retain curve-defined cut "+mode);
            ConstantCuts.Set(shot,timeline.Keys[1].Id,false); timeline.Keys[1].CutIn=false;
            CurveModel.Ensure(timeline.ActiveTrack);
            check(!timeline.Keys[1].CutIn,"Uncheck restores continuous interpolation "+mode);
            if(mode==CurveEditorMode.Advanced)
                foreach(var curve in shot.Space.Channels.Values) curve.Keys[0].Interpolation=CurveInterpolation.Constant;
            else
                foreach(var curve in shot.Intervals[0].Channels.Values) curve.Keys[0].Interpolation=CurveInterpolation.Constant;
            CurveModel.Ensure(timeline.ActiveTrack);
            check(timeline.Keys[1].CutIn,"Manual all-channel constant auto-enables cut "+mode);
            if(mode==CurveEditorMode.Advanced) shot.Space.Channels[SpatialChannel.PositionX].Keys[0].Interpolation=CurveInterpolation.Linear;
            else { shot.Intervals[0].Unlink(); shot.Intervals[0].Curve(CameraChannel.Position).Keys[0].Interpolation=CurveInterpolation.Linear; }
            CurveModel.Ensure(timeline.ActiveTrack);
            check(!timeline.Keys[1].CutIn,"Partial constant is not a full cut "+mode);
        }
    }
}
