using System.Numerics;

namespace XcpCam.Core;

public enum Ease { Linear, Smooth, In, Out }

/// <summary>
/// One user keyframe. Pos/Rot are relative to the GPose target (Brio XAT playback space), Fov is absolute radians.
/// Ease applies to the segment that starts at this key.
/// </summary>
public sealed class Keyframe
{
    public float TimeSec;
    public Vector3 Pos;
    public Quaternion Rot;
    public float Fov;
    public Ease Ease = Ease.Smooth;

    // Diagnostics captured alongside
    public float SelfCheckError;
    public string TargetName = "";
    public Vector3 BasePos;
    public Quaternion BaseRot = Quaternion.Identity;

    public Keyframe Clone() => (Keyframe)MemberwiseClone();
}

/// <summary>One sampled .xcp frame.</summary>
public readonly record struct XcpFrame(Vector3 Pos, Quaternion Rot, float Fov);
