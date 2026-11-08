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
        double incoming=x.Keys[1].Left.Y/(track.Keys[1].Frame-track.Keys[0].Frame)*-3;
        double outgoing=x.Keys[1].Right.Y/(track.Keys[2].Frame-track.Keys[1].Frame)*3;
        check(Math.Abs(incoming-outgoing)<1e-7,"Nonuniform frame-aware Auto tangents share physical node velocity");
        var outside=x.Keys[0].Clone(); var end=x.Keys[^1].Clone();
        SpatialTangents.Apply(space,track.Keys,SpatialChannel.PositionX,track.Keys.Skip(1).Take(2).Select(k=>k.Id).ToHashSet(),true);
        check(x.Keys[0].Left==outside.Left && x.Keys[0].Right==outside.Right && x.Keys[^1].Left==end.Left && x.Keys[^1].Right==end.Right,"Selected continuous tangent group never rewrites outside handles");
    }
}
