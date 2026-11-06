using System;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>
/// Conversions between the game's world view matrix and Brio's XAT playback space.
/// Mirrors Brio/Game/Cutscene/CutsceneManager.cs UpdateCamera():
///   view = T(-basePos) * T(-baseRot·pos) * inv(R(baseRot * rot))
/// </summary>
public static class XcpMath
{
    /// <summary>
    /// Decompose a world view matrix (row-vector convention: v_view = v_world * V, V = T(-P) * Rinv)
    /// into camera world position and rotation. Only the upper 3x3 and the translation row are used;
    /// the 4th column is ignored because the game does not keep it as (0,0,0,1).
    /// </summary>
    public static bool DecomposeView(Matrix4x4 view, out Vector3 worldPos, out Quaternion worldRot)
    {
        worldPos = default;
        worldRot = Quaternion.Identity;

        // Rinv rows -> R = Rinv^T (orthonormalise to be safe)
        var c0 = new Vector3(view.M11, view.M21, view.M31);
        var c1 = new Vector3(view.M12, view.M22, view.M32);
        var c2 = new Vector3(view.M13, view.M23, view.M33);
        if(c0.LengthSquared() < 1e-8f || c1.LengthSquared() < 1e-8f || c2.LengthSquared() < 1e-8f)
            return false;
        c2 = Vector3.Normalize(c2);
        c0 = Vector3.Normalize(c0 - Vector3.Dot(c0, c2) * c2);
        c1 = Vector3.Cross(c2, c0);

        var r = new Matrix4x4(
            c0.X, c0.Y, c0.Z, 0,
            c1.X, c1.Y, c1.Z, 0,
            c2.X, c2.Y, c2.Z, 0,
            0, 0, 0, 1);

        var t = new Vector3(view.M41, view.M42, view.M43);
        worldPos = -Vector3.Transform(t, r);
        worldRot = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r));
        return IsFinite(worldPos) && IsFinite(worldRot);
    }

    public static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static bool IsFinite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    /// <summary>Max difference over the parts that matter (upper 3x3 and translation row).</summary>
    public static float MaxAffineDiff(Matrix4x4 a, Matrix4x4 b)
    {
        float m = 0;
        for(int i = 0; i < 4; i++)
            for(int j = 0; j < 3; j++)
                m = MathF.Max(m, MathF.Abs(a[i, j] - b[i, j]));
        return m;
    }

    public static string Dump(Matrix4x4 m) =>
        $"[{m.M11:0.###} {m.M12:0.###} {m.M13:0.###} {m.M14:0.###} | {m.M21:0.###} {m.M22:0.###} {m.M23:0.###} {m.M24:0.###} | " +
        $"{m.M31:0.###} {m.M32:0.###} {m.M33:0.###} {m.M34:0.###} | {m.M41:0.###} {m.M42:0.###} {m.M43:0.###} {m.M44:0.###}]";

    public static (Vector3 pos, Quaternion rot) WorldToRelative(Vector3 worldPos, Quaternion worldRot, Vector3 basePos, Quaternion baseRot)
    {
        var inv = Quaternion.Inverse(Quaternion.Normalize(baseRot));
        var pos = Vector3.Transform(worldPos - basePos, inv);
        var rot = Quaternion.Normalize(inv * worldRot);
        return (pos, rot);
    }

    /// <summary>Exactly what Brio's cutscene player builds (Scale=1, Offset=0).</summary>
    public static Matrix4x4 BrioPlaybackView(Vector3 pos, Quaternion rot, Vector3 basePos, Quaternion baseRot)
    {
        Vector3 rotatedLocalPosition = Vector3.Transform(pos, baseRot);
        Quaternion localRotation = baseRot * rot;
        var localRotationMatrix = Matrix4x4.CreateFromQuaternion(localRotation);
        Matrix4x4.Invert(localRotationMatrix, out var invertedLocalRotationMatrix);
        var localTranslationMatrix = Matrix4x4.CreateTranslation(-rotatedLocalPosition);
        var basePositionMatrix = Matrix4x4.CreateTranslation(-basePos);
        return basePositionMatrix * (localTranslationMatrix * invertedLocalRotationMatrix);
    }

    public static float MaxAbsDiff(Matrix4x4 a, Matrix4x4 b)
    {
        float m = 0;
        for(int i = 0; i < 4; i++)
            for(int j = 0; j < 4; j++)
                m = MathF.Max(m, MathF.Abs(a[i, j] - b[i, j]));
        return m;
    }

    /// <summary>Vertical FoV (radians) from a D3D-style perspective projection matrix.</summary>
    public static float FovFromProjection(Matrix4x4 proj)
    {
        if(MathF.Abs(proj.M22) < 1e-6f) return 0;
        return 2f * MathF.Atan(1f / MathF.Abs(proj.M22));
    }
}
