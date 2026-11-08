using System;
using System.Numerics;

namespace XcpCam.Core;

public enum CurveInterpolation { Bezier, Linear, Constant }
public enum HandleMode { AutoClamped, Auto, Aligned, Free, Vector }

/// <summary>A scalar key. Handles are (frame, value) offsets, not absolute positions.</summary>
public class CurveKey
{
    public Guid Id = Guid.NewGuid();
    public Guid? NodeId;
    public int Frame;
    public double Value;
    public CurveInterpolation Interpolation = CurveInterpolation.Bezier;
    public HandleMode Mode = HandleMode.AutoClamped;
    public bool AutomaticTangent = true;
    public Vector2 Left = new(-10, 0);
    public Vector2 Right = new(10, 0);

    /// <summary>Identity is retained; the returned mutable key is independent.</summary>
    public CurveKey Clone() => (CurveKey)MemberwiseClone();
}
