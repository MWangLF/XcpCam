using System;
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

    public FollowPathConstraint Clone() => (FollowPathConstraint)MemberwiseClone();

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

/// <summary>Runtime binding of a shot to the evaluated target curve.</summary>
internal sealed class FollowBinding(CurvePath path, FollowPathConstraint constraint, Guid curveId)
{
    public CurvePath Path { get; } = path;
    public FollowPathConstraint Constraint { get; } = constraint;
    public Guid CurveId { get; } = curveId;
}
