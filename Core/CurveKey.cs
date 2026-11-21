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
    // Only free time-domain keys need a fractional frame. Bound nodes and spatial keys stay integral.
    public double? Subframe;
    public double Time => Subframe ?? Frame;
    internal void SetTime(double time)
    {
        Frame = checked((int)System.Math.Round(time, System.MidpointRounding.AwayFromZero));
        Subframe = time == Frame ? null : time;
    }
    public double Value;
    public CurveInterpolation Interpolation = CurveInterpolation.Bezier;
    public HandleMode Mode = HandleMode.AutoClamped;
    public bool AutomaticTangent = true;
    public Vector2 Left = new(-10, 0);
    public Vector2 Right = new(10, 0);

    /// <summary>Identity is retained; the returned mutable key is independent.</summary>
    public CurveKey Clone() => (CurveKey)MemberwiseClone();
}
