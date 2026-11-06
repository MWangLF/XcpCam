using System;
using System.Collections.Generic;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>
/// Centripetal Catmull-Rom curve through the key positions, with an arc-length table so the curve can be
/// evaluated by distance travelled (constant speed along the path).
/// Approach after psenxiv/vista's ArcLengthTable (https://github.com/psenxiv/vista).
/// </summary>
public sealed class ArcLengthCurve
{
    private const int SamplesPerSegment = 64;

    private readonly Vector3[] _pts;
    private readonly float[] _cum;      // cumulative length at each sample, global
    private readonly float[] _segStart; // cumulative length at each key

    public int SegmentCount => _pts.Length - 1;
    public float TotalLength => _cum[^1];

    public ArcLengthCurve(IReadOnlyList<Vector3> points)
    {
        if(points.Count == 0) throw new ArgumentException("need at least one point");
        _pts = [.. points];

        int segs = Math.Max(0, _pts.Length - 1);
        _cum = new float[segs * SamplesPerSegment + 1];
        _segStart = new float[_pts.Length];

        float total = 0;
        var prev = _pts[0];
        for(int s = 0; s < segs; s++)
        {
            _segStart[s] = total;
            for(int j = 1; j <= SamplesPerSegment; j++)
            {
                var p = Evaluate(s, (float)j / SamplesPerSegment);
                total += Vector3.Distance(prev, p);
                prev = p;
                _cum[s * SamplesPerSegment + j] = total;
            }
        }
        _segStart[^1] = total;
    }

    /// <summary>Arc length from the start to key <paramref name="key"/>.</summary>
    public float LengthAtKey(int key) => _segStart[key];

    /// <summary>Position after travelling distance <paramref name="s"/> along the curve.</summary>
    public Vector3 PositionAt(float s)
    {
        if(SegmentCount == 0) return _pts[0];
        s = Math.Clamp(s, 0, TotalLength);

        int lo = 0, hi = _cum.Length - 1;
        while(hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if(_cum[mid] <= s) lo = mid; else hi = mid;
        }

        float span = _cum[hi] - _cum[lo];
        float frac = span > 1e-9f ? (s - _cum[lo]) / span : 0f;
        float g = (lo + frac) / SamplesPerSegment;
        int seg = Math.Min((int)g, SegmentCount - 1);
        return Evaluate(seg, Math.Clamp(g - seg, 0f, 1f));
    }

    /// <summary>Position on segment <paramref name="seg"/> at curve parameter <paramref name="u"/> (0..1).</summary>
    public Vector3 Evaluate(int seg, float u)
    {
        var p1 = _pts[seg];
        var p2 = _pts[seg + 1];
        var p0 = seg > 0 ? _pts[seg - 1] : p1;
        var p3 = seg + 2 < _pts.Length ? _pts[seg + 2] : p2;
        return CentripetalCatmullRom(p0, p1, p2, p3, u);
    }

    /// <summary>Centripetal (alpha=0.5) Catmull-Rom between p1 and p2 (Barry-Goldman). Duplicated endpoints are mirrored.</summary>
    public static Vector3 CentripetalCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        const float eps = 1e-4f;
        float d12 = Vector3.Distance(p1, p2);
        if(d12 < eps)
            return Vector3.Lerp(p1, p2, t);

        if(Vector3.Distance(p0, p1) < eps) p0 = p1 - (p2 - p1);
        if(Vector3.Distance(p3, p2) < eps) p3 = p2 + (p2 - p1);

        float k0 = 0f;
        float k1 = k0 + MathF.Sqrt(Vector3.Distance(p0, p1));
        float k2 = k1 + MathF.Sqrt(d12);
        float k3 = k2 + MathF.Sqrt(Vector3.Distance(p2, p3));
        float s = k1 + (k2 - k1) * t;

        var a1 = (k1 - s) / (k1 - k0) * p0 + (s - k0) / (k1 - k0) * p1;
        var a2 = (k2 - s) / (k2 - k1) * p1 + (s - k1) / (k2 - k1) * p2;
        var a3 = (k3 - s) / (k3 - k2) * p2 + (s - k2) / (k3 - k2) * p3;
        var b1 = (k2 - s) / (k2 - k0) * a1 + (s - k0) / (k2 - k0) * a2;
        var b2 = (k3 - s) / (k3 - k1) * a2 + (s - k1) / (k3 - k1) * a3;
        return (k2 - s) / (k2 - k1) * b1 + (s - k1) / (k2 - k1) * b2;
    }
}
