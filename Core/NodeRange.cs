using System;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Safety limits for editing camera nodes in the world.</summary>
public static class NodeRange
{
    public const float DefaultRange = 2000f, MinimumRange = 10f, MaximumRange = 100000f;
    private static float _maxRange = DefaultRange;
    /// <summary>User-adjustable movement limit: nodes stay within this distance of the GPose target.</summary>
    public static float MaxRange
    {
        get => _maxRange;
        set => _maxRange = float.IsFinite(value) ? Math.Clamp(value, MinimumRange, MaximumRange) : DefaultRange;
    }
    /// <summary>Largest believable gizmo move in one frame; bigger steps come from near-parallel drag rays.</summary>
    public const float MaxStep = 25f;

    /// <summary>Limits a target-relative position to <see cref="MaxRange"/>; non-finite input returns zero.</summary>
    public static Vector3 Clamp(Vector3 rel)
    {
        if(!XcpMath.IsFinite(rel)) return Vector3.Zero;
        float length = rel.Length();
        return length > MaxRange ? rel * (MaxRange / length) : rel;
    }

    /// <summary>World position of the current GPose target; the limit is measured from it. Null = no target, no limit.</summary>
    public static Vector3? Center;

    /// <summary>Limits a world position to <see cref="MaxRange"/> around <see cref="Center"/>.</summary>
    public static Vector3 ClampWorld(Vector3 world)
    {
        if(Center is not Vector3 c) return XcpMath.IsFinite(world) ? world : Vector3.Zero;
        return XcpMath.IsFinite(world) ? c + Clamp(world - c) : c;
    }

    /// <summary>In front of the camera and near the viewport (row-vector convention, like ImGuizmo).</summary>
    public static bool Visible(Vector3 world, Matrix4x4 view, Matrix4x4 proj)
    {
        var clip = Vector4.Transform(new Vector4(world, 1), view * proj);
        if(!float.IsFinite(clip.W) || clip.W < 0.05f) return false;
        float x = clip.X / clip.W, y = clip.Y / clip.W;
        return float.IsFinite(x) && float.IsFinite(y) && MathF.Abs(x) < 1.5f && MathF.Abs(y) < 1.5f;
    }
}
