using System;
using System.Collections.Generic;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Blender's tracking constraints (Object Constraints → Tracking).</summary>
public enum TrackType { Damped, Locked, TrackTo }

/// <summary>
/// A tracking constraint of a camera track: aims the camera at a character's bone. Evaluated after Follow Path,
/// in list order; drives the camera rotation (and the FoV with <see cref="KeepFraming"/>), so those values are
/// locked on the nodes while the constraint is enabled.
/// </summary>
public sealed class TrackConstraint
{
    public Guid Id = Guid.NewGuid();
    public TrackType Type = TrackType.TrackTo;
    public bool Enabled = true;
    public float Influence = 1;
    /// <summary>Character name; empty follows the current GPose target.</summary>
    public string TargetName = "";
    /// <summary>Object-table index hint for same-named characters (runtime only).</summary>
    public int TargetIndex = -1;
    public string Bone = "j_kao";
    /// <summary>World-space offset added to the bone position (metres).</summary>
    public Vector3 TargetOffset;
    /// <summary>Where the target appears on screen, -1..1 (0,0 = centre, +X right, +Y up).</summary>
    public Vector2 ScreenOffset;
    /// <summary>Camera axis that points at the target (the game camera looks down -Z).</summary>
    public PathAxis TrackAxis = PathAxis.NegZ;
    /// <summary>Track To: camera axis kept closest to world up.</summary>
    public PathAxis UpAxis = PathAxis.Y;
    /// <summary>Locked Track: camera axis the rotation is locked around.</summary>
    public PathAxis LockAxis = PathAxis.Y;
    /// <summary>Keep the target's size on screen: FoV follows the distance (size taken at the track's first node).</summary>
    public bool KeepFraming;

    /// <summary>Recorded world bone positions per timeline frame (without <see cref="TargetOffset"/>).</summary>
    public SortedList<int, Vector3> Recorded = new();
    /// <summary><see cref="TargetKey"/> the recording was made for.</summary>
    public string RecordedFor = "";

    public string TargetKey => $"{TargetName}|{Bone}";
    public bool DrivesFov => Enabled && KeepFraming;

    public static string TypeName(TrackType t) => t switch
    {
        TrackType.Damped => I18n.T("constraint.type.damped"),
        TrackType.Locked => I18n.T("constraint.type.locked"),
        _ => I18n.T("constraint.type.track_to"),
    };

    public TrackConstraint Clone()
    {
        var c = (TrackConstraint)MemberwiseClone();
        c.Recorded = new SortedList<int, Vector3>(Recorded);
        return c;
    }

    /// <summary>The recording is missing, made for another target / bone, or does not cover [first, last].</summary>
    public bool RecordingStale(int first, int last)
        => Recorded.Count == 0 || RecordedFor != TargetKey || Recorded.Keys[0] > first || Recorded.Keys[^1] < last;

    /// <summary>Recorded position at a frame: linear between recorded frames, held past the ends.</summary>
    public Vector3? RecordedAt(float frame)
    {
        var keys = Recorded.Keys;
        if(keys.Count == 0) return null;
        if(frame <= keys[0]) return Recorded.Values[0];
        if(frame >= keys[^1]) return Recorded.Values[^1];
        int lo = 0, hi = keys.Count - 1;
        while(hi - lo > 1) { int mid = (lo + hi) / 2; if(keys[mid] <= frame) lo = mid; else hi = mid; }
        float t = (frame - keys[lo]) / (keys[hi] - keys[lo]);
        return Vector3.Lerp(Recorded.Values[lo], Recorded.Values[hi], t);
    }
}

/// <summary>Where a tracking constraint's target is (world space, before <see cref="TrackConstraint.TargetOffset"/>).</summary>
public interface ITrackTargetSource
{
    Vector3? Position(TrackConstraint c, float frame);
    /// <summary>Viewport width / height, for the screen offset.</summary>
    float Aspect { get; }
}

/// <summary>Recorded trajectories (exports); constraints without a recording use the fallback (current position).</summary>
public sealed class RecordedTargetSource(ITrackTargetSource? fallback) : ITrackTargetSource
{
    public bool UsedFallback { get; private set; }
    public float Aspect => fallback?.Aspect ?? TrackSolver.DefaultAspect;
    public Vector3? Position(TrackConstraint c, float frame)
    {
        if(c.Recorded.Count > 0) return c.RecordedAt(frame);
        UsedFallback = true;
        return fallback?.Position(c, frame);
    }
}

/// <summary>Fixed / scripted targets (tests, headless use).</summary>
public sealed class FuncTargetSource(Func<TrackConstraint, float, Vector3?> position, float aspect = TrackSolver.DefaultAspect) : ITrackTargetSource
{
    public float Aspect => aspect;
    public Vector3? Position(TrackConstraint c, float frame) => position(c, frame);
}

/// <summary>
/// Blender's tracking constraint solvers (blenkernel/intern/constraint.cc damptrack_evaluate, locktrack_evaluate,
/// trackto_evaluate) for a camera pose, plus a screen offset (the target lands at a given point of the frame
/// instead of the centre) and an optional FoV that keeps the target's size on screen.
/// </summary>
public static class TrackSolver
{
    public const float DefaultAspect = 16f / 9f;
    private const float MinFov = MathF.PI / 180, MaxFov = 175 * MathF.PI / 180;

    /// <summary>Shortest-arc rotation taking unit vector a to unit vector b.</summary>
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if(d > 1 - 1e-7f) return Quaternion.Identity;
        if(d < -1 + 1e-6f)
        {
            var axis = Vector3.Cross(a, Vector3.UnitX);
            if(axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(a, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + d));
    }

    /// <summary>Rotation about <paramref name="axis"/> that brings <paramref name="from"/> as close as possible to <paramref name="to"/>.</summary>
    public static Quaternion Around(Vector3 axis, Vector3 from, Vector3 to)
    {
        var p = from - axis * Vector3.Dot(from, axis);
        var q = to - axis * Vector3.Dot(to, axis);
        if(p.LengthSquared() < 1e-10f || q.LengthSquared() < 1e-10f) return Quaternion.Identity;
        float angle = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(p, q)), Vector3.Dot(p, q));
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }

    /// <summary>Camera-space direction that must point at the target: the track axis, tilted so the target
    /// projects to <see cref="TrackConstraint.ScreenOffset"/> (vertical FoV <paramref name="fov"/>).</summary>
    public static Vector3 AimDirection(TrackConstraint c, float fov, float aspect)
    {
        float t = MathF.Tan(Math.Clamp(fov, MinFov, MaxFov) / 2);
        var view = Vector3.Normalize(new Vector3(c.ScreenOffset.X * t * aspect, c.ScreenOffset.Y * t, -1));
        var axis = FollowPathConstraint.Axis(c.TrackAxis);
        return Vector3.Normalize(Vector3.Transform(view, FromTo(-Vector3.UnitZ, axis)));
    }

    /// <summary>Framing constant tan(fov/2)·distance at a reference pose (used by <see cref="TrackConstraint.KeepFraming"/>).</summary>
    public static float? Framing(XcpFrame reference, Vector3 target)
    {
        float d = Vector3.Distance(reference.Pos, target);
        return d > 1e-4f && float.IsFinite(d) ? MathF.Tan(Math.Clamp(reference.Fov, MinFov, MaxFov) / 2) * d : null;
    }

    /// <summary>Applies one constraint to a pose; <paramref name="target"/> already includes the target offset.</summary>
    public static XcpFrame Apply(TrackConstraint c, XcpFrame pose, Vector3 target, float aspect, float? framing = null)
    {
        var toTarget = target - pose.Pos;
        float dist = toTarget.Length();
        if(!c.Enabled || c.Influence <= 0 || dist < 1e-4f || !float.IsFinite(dist)) return pose;
        var t = toTarget / dist;

        float fov = pose.Fov;
        if(c.KeepFraming && framing is float k) fov = Math.Clamp(2 * MathF.Atan(k / dist), MinFov, MaxFov);
        var aim = AimDirection(c, fov, aspect);
        var own = Quaternion.Normalize(pose.Rot);

        Quaternion rot;
        switch(c.Type)
        {
            case TrackType.Damped:
                // shortest arc from the current aim to the target: keeps the node's own roll
                rot = FromTo(Vector3.Transform(aim, own), t) * own;
                break;
            case TrackType.Locked:
            {
                // only rotate around the lock axis (camera space), like Blender's Locked Track
                var lockAxis = Vector3.Transform(FollowPathConstraint.Axis(c.LockAxis), own);
                rot = Around(lockAxis, Vector3.Transform(aim, own), t) * own;
                break;
            }
            default:
            {
                // Track To: track axis on the target, up axis as close to world up as possible (no own roll)
                var axis = FollowPathConstraint.Axis(c.TrackAxis);
                var up = FollowPathConstraint.Axis(c.UpAxis);
                if(MathF.Abs(Vector3.Dot(axis, up)) > .9f) up = MathF.Abs(axis.Y) > .9f ? -Vector3.UnitZ : Vector3.UnitY;
                var q1 = FromTo(axis, t);
                var worldUp = MathF.Abs(t.Y) > .999f ? -Vector3.UnitZ * MathF.Sign(t.Y) : Vector3.UnitY;
                var q0 = Around(t, Vector3.Transform(up, q1), worldUp) * q1;
                // q0 aims the track axis; turn it so the offset aim direction lands on the target instead
                rot = q0 * Quaternion.Inverse(FromTo(axis, aim));
                break;
            }
        }
        rot = Quaternion.Normalize(rot);
        float w = Math.Clamp(c.Influence, 0, 1);
        if(w < 1)
        {
            rot = Quaternion.Normalize(Quaternion.Slerp(own, rot, w));
            fov = pose.Fov + (fov - pose.Fov) * w;
        }
        return new XcpFrame(pose.Pos, rot, fov);
    }
}
