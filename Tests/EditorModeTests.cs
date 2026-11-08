using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class EditorModeTests
{
    public static void Run(Action<bool,string> check)
    {
        var timeline = new CamTimeline { Length = 200 };
        timeline.Keys.AddRange(new[] {
            new Keyframe { Frame=0, Pos=Vector3.Zero, Rot=Quaternion.Identity, Fov=.7f },
            new Keyframe { Frame=60, Pos=new(6,2,1), Rot=Quaternion.Identity, Fov=.8f },
            new Keyframe { Frame=120, Pos=new(9,0,2), Rot=Quaternion.Identity, Fov=.9f } });
        CurveModel.Ensure(timeline.ActiveTrack); var shot = timeline.ActiveTrack.Shots[0];
        shot.SetEditorMode(CurveEditorMode.Advanced);
        var x = shot.Space.Channels[SpatialChannel.PositionX];
        x.Keys[0].Mode=HandleMode.Free; x.Keys[0].Right=new Vector2(100,0);
        x.Keys[1].Mode=HandleMode.Free; x.Keys[1].Left=new Vector2(-600,-1);
        shot.Space.Touch();
        double parameter=SpatialFrameMap.Parameter(shot,SpatialChannel.PositionX,15);
        check(Math.Abs(shot.Sample(15).Pos.X-x.Evaluate(parameter))<1e-6,"Advanced curve speed is not arc-length normalized");
        var frozen=shot.Clone(); var key=x.Keys[1]; int start=timeline.Keys[1].Frame;
        int target=start+7;
        for(int n=0;n<5;n++)
        {
            double p=SpatialFrameMap.Parameter(frozen,SpatialChannel.PositionX,target);
            int f=(int)Math.Round(SpatialFrameMap.Frame(frozen,SpatialChannel.PositionX,p));
            check(timeline.MoveNodes(new System.Collections.Generic.Dictionary<Guid,int>{{timeline.Keys[1].Id,f}}) && timeline.Keys[1].Frame==target,
                "Repeated frozen drag target does not drift "+n);
        }
        CurveModel.Ensure(timeline.ActiveTrack);
        var before=Enumerable.Range(0,241).Select(i=>shot.Sample(i/2f)).ToArray();
        var curves=shot.Space.Clone();
        shot.SetEditorMode(CurveEditorMode.Simple);
        check(shot.Space.Channels.All(p=>p.Value.Keys.Select(k=>(k.Id,k.Frame,k.Value,k.Left,k.Right)).SequenceEqual(curves.Channels[p.Key].Keys.Select(k=>(k.Id,k.Frame,k.Value,k.Left,k.Right)))),
            "Switching to simple retains all advanced data");
        float error=0;
        for(int n=0;n<before.Length;n++) error=Math.Max(error,Vector3.Distance(before[n].Pos,shot.Sample(n/2f).Pos));
        check(error<1e-5,"First simple activation preserves advanced motion with neutral mapping");
        var clone=shot.Clone(); shot.SetEditorMode(CurveEditorMode.Advanced);
        check(clone.EditorMode==CurveEditorMode.Simple && shot.EditorMode==CurveEditorMode.Advanced,"Editor mode clone is independent");
        timeline.Snapshot(); shot.SetEditorMode(CurveEditorMode.Simple); timeline.Undo();
        check(timeline.ActiveTrack.Shots[0].EditorMode==CurveEditorMode.Advanced,"Undo restores active curve mode");
    }
}
