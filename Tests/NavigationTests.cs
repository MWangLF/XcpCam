using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class NavigationTests
{
    public static void Run(Action<bool,string> check)
    {
        double left=10,span=100,bottom=-5,height=20;
        ViewportMath.Wheel(ref left,ref span,ref bottom,ref height,1,true,false,.4,.3);
        check(bottom==-5 && height==20 && Math.Abs(left+.4*span-50)<1e-8,"Ctrl wheel affects horizontal axis only and preserves anchor");
        double oldLeft=left,oldSpan=span;
        ViewportMath.Wheel(ref left,ref span,ref bottom,ref height,1,true,true,.4,.3);
        check(left==oldLeft && span==oldSpan && Math.Abs(bottom+.3*height-1)<1e-8,"Ctrl Alt wheel affects vertical axis only and preserves anchor");
        double oldHeight=height;
        ViewportMath.Wheel(ref left,ref span,ref bottom,ref height,1,false,true,.4,.3);
        check(left==oldLeft && span==oldSpan && height==oldHeight,"Alt wheel translates vertical axis without zoom");
        var track=new CamTrack();
        foreach(var item in new[]{(0,0f),(20,2f),(100,8f),(140,9f)}) track.Keys.Add(new Keyframe { Frame=item.Item1,Pos=new(item.Item2,0,0),Rot=Quaternion.Identity,Fov=.7f });
        CurveModel.Ensure(track); var space=track.Shots[0].Space;
        check(space.Channels.Values.All(c=>c.Keys.All(k=>k.Mode==HandleMode.Auto)),"New spatial path defaults to Auto, not AutoClamped");
        var x=space.Channels[SpatialChannel.PositionX];
        // The position path is a 3D Bezier curve with Blender Auto handles: G1 through the node and the
        // same handles as Blender's calchandleNurb_intern (physical speed comes from the path progress).
        var spline=new Spline();
        foreach(var item in new[]{0f,2f,8f,9f}) spline.Points.Add(new CurvePoint { Co=new(item,0,0) });
        spline.CalcHandles();
        check(Math.Abs(x.Keys[1].Left.Y-(spline.Points[1].Left.X-2))<1e-5 && Math.Abs(x.Keys[1].Right.Y-(spline.Points[1].Right.X-2))<1e-5
            && x.Keys[1].Left.Y<0 && x.Keys[1].Right.Y>0,"Default position path uses Blender 3D Auto handles (G1 through the node)");
        check(Math.Abs(x.Keys[1].Left.X+(x.Keys[1].Frame-x.Keys[0].Frame)/3f)<1e-3 && Math.Abs(x.Keys[1].Right.X-(x.Keys[2].Frame-x.Keys[1].Frame)/3f)<1e-3,
            "Position keys keep handle X at a third of the parameter span (exact 3D Bezier)");
        var outside=x.Keys[0].Clone(); var end=x.Keys[^1].Clone();
        SpatialTangents.Apply(space,track.Keys,SpatialChannel.PositionX,track.Keys.Skip(1).Take(2).Select(k=>k.Id).ToHashSet(),true);
        check(x.Keys[0].Left==outside.Left && x.Keys[0].Right==outside.Right && x.Keys[^1].Left==end.Left && x.Keys[^1].Right==end.Right,"Selected continuous tangent group never rewrites outside handles");
    }
}
