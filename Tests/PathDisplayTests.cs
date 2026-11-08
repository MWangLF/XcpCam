using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class PathDisplayTests
{
    public static void Run(Action<bool,string> check)
    {
        foreach(bool partial in new[] { false, true })
        {
            var track = new CamTrack(); track.Keys.AddRange(new[] {
                new Keyframe { Frame=0, Pos=Vector3.Zero, Rot=Quaternion.Identity, Fov=.7f },
                new Keyframe { Frame=60, Pos=new Vector3(6,3,2), Rot=Quaternion.Identity, Fov=.9f } });
            var shot=ShotSequence.Create(track,30)!.Segments[0].Curves; shot.SetEditorMode(CurveEditorMode.Advanced);
            foreach(var channel in partial ? new[] { SpatialChannel.PositionX } : new[] { SpatialChannel.PositionX,SpatialChannel.PositionY,SpatialChannel.PositionZ })
                shot.Space.Channels[channel].Keys[0].Interpolation=CurveInterpolation.Constant;
            shot.Space.Touch(); var saved=shot.Space.Clone();
            var display=PathDisplay.Build(shot,30);
            check(display.Jumps.Count==1 && display.Edges.Count(e=>e.Dashed)==1,"Constant display has one dashed jump, partial="+partial);
            var jump=display.Jumps.Single();
            check(Vector3.Distance(jump.After.Pos,new Vector3(6,3,2))<1e-5,"Jump dashed connector ends at actual target");
            check(Math.Abs(jump.Before.Pos.X)<1e-5 && (!partial || jump.Before.Pos.Y>2.9f),"Partial constant produces actual intermediate camera pose");
            check(display.Edges.Where(e=>!e.Dashed).All(e=>Math.Abs(e.Start.Pos.X-e.End.Pos.X)<1e-5),"Continuous line never bridges constant-axis jump");
            check(shot.Space.Channels.All(p=>p.Value.Keys.Select(k=>(k.Id,k.Frame,k.Value,k.Left,k.Right)).SequenceEqual(saved.Channels[p.Key].Keys.Select(k=>(k.Id,k.Frame,k.Value,k.Left,k.Right)))),"Building display never changes authored curves");
            shot.Space.Channels[SpatialChannel.PositionX].Keys[0].Interpolation=CurveInterpolation.Linear;
            shot.Space.Channels[SpatialChannel.PositionY].Keys[0].Interpolation=CurveInterpolation.Linear;
            shot.Space.Channels[SpatialChannel.PositionZ].Keys[0].Interpolation=CurveInterpolation.Linear; shot.Space.Touch();
            check(PathDisplay.Build(shot,30).Jumps.Count==0,"Fast continuous motion has no false jump");
            shot.SetEditorMode(CurveEditorMode.Simple); shot.ActivateSimpleTiming();
            shot.Intervals[0].Curve(CameraChannel.Position).Keys[0].Interpolation=CurveInterpolation.Constant; shot.Intervals[0].MarkManual();
            check(PathDisplay.Build(shot,30).Jumps.Count==1,"Simple constant timing creates a dashed jump");
            shot.SetEditorMode(CurveEditorMode.Advanced);
            shot.Space.Channels[SpatialChannel.Yaw].Keys[0].Interpolation=CurveInterpolation.Constant;
            shot.Space.Channels[SpatialChannel.Yaw].Keys[^1].Value=90; shot.Space.Touch();
            var rotationDisplay=PathDisplay.Build(shot,30);
            check(rotationDisplay.Jumps.Count==1 && Math.Abs(Quaternion.Dot(rotationDisplay.Jumps[0].Before.Rot,rotationDisplay.Jumps[0].After.Rot))<.8,
                "Rotation-only constant preserves distinct dashed camera orientations");
            shot.Space.Channels[SpatialChannel.Yaw].Keys[0].Interpolation=CurveInterpolation.Linear;
            shot.Space.Channels[SpatialChannel.Fov].Keys[0].Interpolation=CurveInterpolation.Constant; shot.Space.Touch();
            check(PathDisplay.Build(shot,30).Jumps.Count==1,"FoV-only constant preserves derived camera event");
        }
    }
}
