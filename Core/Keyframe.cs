using System.Numerics;

namespace XcpCam.Core;

/// <summary>How the speed behaves when passing through a key (timed mode).</summary>
public enum TangentMode
{
    /// <summary>Smooth, continuous speed through the key (monotone PCHIP).</summary>
    Auto,
    /// <summary>Come to a stop at the key (ease out of the previous leg and into the next one).</summary>
    Stop,
    /// <summary>The leg that starts at this key runs at constant speed.</summary>
    Linear,
}

public enum TimingMode
{
    /// <summary>Respect every key's time; speed changes smoothly between keys.</summary>
    Timed,
    /// <summary>Ignore key times; move along the whole path at constant speed with optional ease at both ends.</summary>
    ConstantSpeed,
}

public sealed class PathSettings
{
    public TimingMode Mode = TimingMode.Timed;

    // Timed mode
    public bool EaseStart = true;
    public bool EaseEnd = true;

    // Constant-speed mode
    public float Duration = 6f;
    public float EaseInFraction = 0.15f;
    public float EaseOutFraction = 0.15f;
    /// <summary>Yalms of travel that count as one radian of rotation when pacing pure-rotation legs.</summary>
    public float RotationWeight = 1.5f;

    public PathSettings Clone() => (PathSettings)MemberwiseClone();
}

/// <summary>
/// One user keyframe. Pos/Rot are relative to the GPose target (Brio XAT playback space), Fov is absolute radians.
/// </summary>
public sealed class Keyframe
{
    /// <summary>Timeline frame (source of truth in the UI). TimeSec is derived from it by <see cref="CamTimeline"/>.</summary>
    public int Frame;
    /// <summary>Hold length in timeline frames. HoldSec is derived from it.</summary>
    public int HoldFrames;

    public float TimeSec;
    public Vector3 Pos;
    public Quaternion Rot;
    public float Fov;
    public TangentMode Tangent = TangentMode.Auto;
    /// <summary>Stay on this key for this many seconds before moving on (timed mode).</summary>
    public float HoldSec;

    // Diagnostics captured alongside
    public float SelfCheckError;
    public string TargetName = "";
    public Vector3 BasePos;
    public Quaternion BaseRot = Quaternion.Identity;

    public Keyframe Clone() => (Keyframe)MemberwiseClone();
}

/// <summary>One sampled .xcp frame.</summary>
public readonly record struct XcpFrame(Vector3 Pos, Quaternion Rot, float Fov);
