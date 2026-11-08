using System.Numerics;

namespace XcpCam.Core;

/// <summary>View-space camera geometry: visible points lie along local -Z.</summary>
public static class FrustumGeometry
{
    public static Vector3 Point(Vector3 eye, Quaternion rotation, float x, float y, float depth)
        => eye + Vector3.Transform(new Vector3(x, y, -depth), rotation);
}
