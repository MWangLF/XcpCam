using System;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>
/// Brio free-camera parameterisation (Brio/Game/Camera/VirtualCameraManager.cs UpdateMatrix):
///   look  = (sin yaw·cos pitch, sin pitch, cos yaw·cos pitch)
///   right = normalize(cross(Up, look)), up = cross(look, right)
///   view  = [right up look | -P·…] * Rz(PivotRotation)
/// Rotation.X = yaw, Rotation.Y = pitch, PivotRotation = roll.
/// </summary>
public static class FreeCamMath
{
    public static Matrix4x4 BuildView(Vector3 position, float yaw, float pitch, float roll)
    {
        var look = Vector3.Normalize(new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, look));
        var up = Vector3.Cross(look, right);
        var m = new Matrix4x4(
            right.X, up.X, look.X, 0,
            right.Y, up.Y, look.Y, 0,
            right.Z, up.Z, look.Z, 0,
            -Vector3.Dot(position, right), -Vector3.Dot(position, up), -Vector3.Dot(position, look), 1);
        return Matrix4x4.Transform(m, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll));
    }

    /// <summary>
    /// Converts a camera world rotation (as produced by <see cref="XcpMath.DecomposeView"/>) into Brio free-cam
    /// yaw / pitch / roll. <paramref name="previousYaw"/> is used to unwrap yaw so consecutive frames stay continuous.
    /// </summary>
    public static (float yaw, float pitch, float roll) FromWorldRotation(Quaternion worldRot, float? previousYaw = null)
    {
        var fwd = Vector3.Transform(Vector3.UnitZ, worldRot);
        var rightActual = Vector3.Transform(Vector3.UnitX, worldRot);

        float pitch = MathF.Asin(Math.Clamp(fwd.Y, -1f, 1f));
        float yaw = MathF.Atan2(fwd.X, fwd.Z);
        if(previousYaw is float py)
        {
            while(yaw - py > MathF.PI) yaw -= MathF.Tau;
            while(yaw - py < -MathF.PI) yaw += MathF.Tau;
        }

        var look = Vector3.Normalize(new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, look));
        var up = Vector3.Cross(look, right);
        // view = M * Rz(roll)  ->  actual right axis = cos(roll)·right - sin(roll)·up
        float roll = MathF.Atan2(-Vector3.Dot(rightActual, up), Vector3.Dot(rightActual, right));
        return (yaw, pitch, roll);
    }
}
