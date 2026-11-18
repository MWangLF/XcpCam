using System;
using System.Collections.Generic;
using System.Numerics;

namespace XcpCam.Core;

public enum PathAxis { X, Y, Z, NegX, NegY, NegZ }

/// <summary>
/// Blender's Follow Path object constraint (blenkernel/intern/constraint.cc followpath_get_tarmat /
/// followpath_evaluate) for a camera track. Timing is always "fixed position" with the offset factor
/// keyed at every node (<see cref="ShotCurves.PathProgress"/>); rotation and FoV stay on the node keys.
/// </summary>
public sealed class FollowPathConstraint
{
    public Guid CurveId;
    public bool Enabled = true;
    /// <summary>FOLLOWPATH_FOLLOW: orient the camera (and its offset) along the curve.</summary>
    public bool FollowCurve;
    /// <summary>Forward / up axes for Follow Curve. The game camera looks down its -Z axis with +Y up.</summary>
    public PathAxis Forward = PathAxis.NegZ, Up = PathAxis.Y;
    /// <summary>FOLLOWPATH_RADIUS: scale the offset by the curve radius.</summary>
    public bool UseRadius;
    /// <summary>
    /// Curve points anchored to the timeline: point <see cref="CurvePoint.Id"/> → frame. Anchoring is per curve:
    /// empty means the whole curve is anchored to the camera (points shown where the camera reaches them).
    /// Two or more anchors own the path progress: the camera is on every anchored point at its frame, and the
    /// first/last anchor replace the first/last camera node as 0 % / 100 %.
    /// </summary>
    public Dictionary<Guid, double> TimeAnchors = [];
    /// <summary>The curve editor last edited anchored timing. Anchors then record actual crossings rather
    /// than overriding the edited speed. Moving an anchor returns control to the anchor mapping.</summary>
    public bool EditorTiming;

    /// <summary>Closed curve anchored to the timeline: the frame the loop returns to its start point (100 %).
    /// Anchor frames retain subframe precision so switching modes does not quantize the motion.</summary>
    public double? LoopEndFrame;

    public FollowPathConstraint Clone()
    {
        var c = (FollowPathConstraint)MemberwiseClone();
        c.TimeAnchors = new(TimeAnchors);
        return c;
    }

    public static Vector3 Axis(PathAxis axis) => axis switch
    {
        PathAxis.X => Vector3.UnitX, PathAxis.Y => Vector3.UnitY, PathAxis.Z => Vector3.UnitZ,
        PathAxis.NegX => -Vector3.UnitX, PathAxis.NegY => -Vector3.UnitY, _ => -Vector3.UnitZ,
    };

    /// <summary>Path orientation at a point, or identity when Follow Curve is off.</summary>
    public Quaternion Orientation(PathPoint point)
    {
        if(!FollowCurve) return Quaternion.Identity;
        var forward = Axis(Forward); var up = Axis(Up);
        if(MathF.Abs(Vector3.Dot(forward, up)) > .9f) up = MathF.Abs(forward.Y) > .9f ? Vector3.UnitZ : Vector3.UnitY;
        return CurvePath.Frame(point, forward, up, Vector3.UnitY);
    }

    /// <summary>Owner transform in the path frame: position = point + R·(offset·radius), rotation = R·own.</summary>
    public XcpFrame Apply(PathPoint point, Vector3 offset, Quaternion rotation, float fov)
    {
        var q = Orientation(point);
        if(UseRadius) offset *= point.Radius;
        return new XcpFrame(point.Position + Vector3.Transform(offset, q), Quaternion.Normalize(q * rotation), fov);
    }
}

/// <summary>
/// Where a followed curve's control point is on the timeline: <paramref name="Anchored"/> points sit at their
/// anchor frame, the others at the frame the camera reaches them (<paramref name="Frame"/> null: never).
/// <paramref name="Repeat"/>: a later pass over the same point, e.g. a closed curve's start point at 100 %
/// (anchored when it is the loop end); the point's own entry is the non-repeat one.
/// </summary>
public readonly record struct CurvePointTime(int Index, Guid Id, double Factor, double? Frame, bool Anchored, bool Repeat = false);

/// <summary>Runtime binding of a shot to the evaluated target curve.</summary>
internal sealed class FollowBinding(CurvePath path, FollowPathConstraint constraint, Guid curveId)
{
    public CurvePath Path { get; } = path;
    public FollowPathConstraint Constraint { get; } = constraint;
    public Guid CurveId { get; } = curveId;
}
