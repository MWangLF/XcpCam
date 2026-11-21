using System;

namespace XcpCam.Core;

/// <summary>Piecewise frame/geometry chart anchored to camera nodes, independent of speed easing.</summary>
public static class SpatialFrameMap
{
    public static CameraChannel Group(SpatialChannel channel) => (int)channel < 3 ? CameraChannel.Position : (int)channel < 6 ? CameraChannel.Rotation : CameraChannel.Fov;
    public static double Parameter(ShotCurves shot, SpatialChannel channel, double frame)
    {
        var group = Group(channel);
        var leg = FindLeg(shot, group, frame, spatial: false);
        if(leg == null) return shot.Space.Start;
        var range = leg.Ranges[group];
        return range.start + (range.end - range.start) * ((frame - leg.Start) / (leg.End - leg.Start));
    }
    public static double Frame(ShotCurves shot, SpatialChannel channel, double parameter)
    {
        var group = Group(channel);
        var leg = FindLeg(shot, group, parameter, spatial: true);
        if(leg == null) return shot.Start;
        var range = leg.Ranges[group];
        return range.end - range.start > 1e-12 ? leg.Start + (parameter - range.start) / (range.end - range.start) * (leg.End - leg.Start) : leg.Start;
    }
    private static MotionInterval? FindLeg(ShotCurves shot, CameraChannel group, double value, bool spatial)
    {
        int lo = 0, hi = shot.Intervals.Count;
        if(hi == 0) return null;
        while(lo < hi)
        {
            int mid = (lo + hi) / 2;
            var leg = shot.Intervals[mid];
            if(value <= (spatial ? leg.Ranges[group].end : leg.End)) hi = mid; else lo = mid + 1;
        }
        return shot.Intervals[Math.Min(lo, shot.Intervals.Count - 1)];
    }
}
