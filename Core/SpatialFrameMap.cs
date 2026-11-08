using System;
using System.Linq;

namespace XcpCam.Core;

/// <summary>Piecewise frame/geometry chart anchored to camera nodes, independent of speed easing.</summary>
public static class SpatialFrameMap
{
    public static CameraChannel Group(SpatialChannel channel) => (int)channel < 3 ? CameraChannel.Position : (int)channel < 6 ? CameraChannel.Rotation : CameraChannel.Fov;
    public static double Parameter(ShotCurves shot, SpatialChannel channel, double frame)
    {
        var group = Group(channel);
        var leg = shot.Intervals.FirstOrDefault(i => frame <= i.End) ?? shot.Intervals.LastOrDefault();
        if(leg == null) return shot.Space.Start;
        var range = leg.Ranges[group];
        return range.start + (range.end - range.start) * ((frame - leg.Start) / (leg.End - leg.Start));
    }
    public static double Frame(ShotCurves shot, SpatialChannel channel, double parameter)
    {
        var group = Group(channel);
        var leg = shot.Intervals.FirstOrDefault(i => parameter <= i.Ranges[group].end) ?? shot.Intervals.LastOrDefault();
        if(leg == null) return shot.Start;
        var range = leg.Ranges[group];
        return range.end - range.start > 1e-12 ? leg.Start + (parameter - range.start) / (range.end - range.start) * (leg.End - leg.Start) : leg.Start;
    }
}
