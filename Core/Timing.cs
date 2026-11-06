using System;
using System.Collections.Generic;

namespace XcpCam.Core;

/// <summary>
/// Monotone piecewise cubic Hermite through (x_i, y_i) with separate in/out tangents per knot.
/// Auto tangents use Fritsch-Butland (weighted harmonic mean) with the slope/secant ratio clamped to [0,3],
/// so the curve never overshoots or goes backwards. Used for time -> distance (speed stays continuous at keys)
/// and for scalar channels such as FoV.
/// </summary>
public sealed class HermiteCurve
{
    private readonly float[] _x, _y, _mIn, _mOut;

    public float XMin => _x[0];
    public float XMax => _x[^1];

    /// <param name="stopAt">Per knot: force zero slope (stop) at this knot.</param>
    /// <param name="linearFrom">Per knot: the segment starting at this knot is linear (constant speed).</param>
    /// <param name="monotone">Clamp tangents for monotone data (time->distance). False for free scalar channels.</param>
    public HermiteCurve(IReadOnlyList<float> x, IReadOnlyList<float> y, IReadOnlyList<bool>? stopAt = null,
        IReadOnlyList<bool>? linearFrom = null, bool zeroStart = false, bool zeroEnd = false, bool monotone = true)
    {
        int n = x.Count;
        if(n == 0 || y.Count != n) throw new ArgumentException("bad knots");
        _x = [.. x]; _y = [.. y];
        _mIn = new float[n]; _mOut = new float[n];
        if(n == 1) return;

        var h = new float[n - 1];
        var d = new float[n - 1];
        for(int i = 0; i < n - 1; i++)
        {
            h[i] = MathF.Max(_x[i + 1] - _x[i], 1e-6f);
            d[i] = (_y[i + 1] - _y[i]) / h[i];
        }

        var m = new float[n];
        for(int i = 1; i < n - 1; i++)
        {
            if(monotone)
            {
                if(d[i - 1] * d[i] <= 0) { m[i] = 0; continue; }
                float w1 = 2 * h[i] + h[i - 1], w2 = h[i] + 2 * h[i - 1];
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
            else
            {
                // Bessel / non-uniform Catmull-Rom tangent
                m[i] = (h[i] * d[i - 1] + h[i - 1] * d[i]) / (h[i - 1] + h[i]);
            }
        }
        m[0] = zeroStart ? 0 : d[0];
        m[n - 1] = zeroEnd ? 0 : d[n - 2];

        for(int i = 0; i < n; i++) { _mIn[i] = m[i]; _mOut[i] = m[i]; }

        for(int i = 0; i < n; i++)
        {
            if(stopAt != null && stopAt[i]) { _mIn[i] = 0; _mOut[i] = 0; }
        }
        for(int i = 0; i < n - 1; i++)
        {
            if(linearFrom != null && linearFrom[i]) { _mOut[i] = d[i]; _mIn[i + 1] = d[i]; }
        }

        if(monotone)
        {
            for(int i = 0; i < n - 1; i++)
            {
                if(MathF.Abs(d[i]) < 1e-9f) { _mOut[i] = 0; _mIn[i + 1] = 0; continue; }
                float a = _mOut[i] / d[i], b = _mIn[i + 1] / d[i];
                if(a < 0) _mOut[i] = 0; else if(a > 3) _mOut[i] = 3 * d[i];
                if(b < 0) _mIn[i + 1] = 0; else if(b > 3) _mIn[i + 1] = 3 * d[i];
            }
        }
    }

    public int Segment(float x)
    {
        int lo = 0, hi = _x.Length - 1;
        if(x <= _x[0]) return 0;
        if(x >= _x[^1]) return Math.Max(0, _x.Length - 2);
        while(hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if(_x[mid] <= x) lo = mid; else hi = mid;
        }
        return lo;
    }

    public float Evaluate(float x)
    {
        if(_x.Length == 1) return _y[0];
        if(x <= _x[0]) return _y[0];
        if(x >= _x[^1]) return _y[^1];
        int i = Segment(x);
        float hh = _x[i + 1] - _x[i];
        if(hh <= 1e-6f) return _y[i + 1];
        float t = (x - _x[i]) / hh;
        float t2 = t * t, t3 = t2 * t;
        float h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
        return h00 * _y[i] + h10 * hh * _mOut[i] + h01 * _y[i + 1] + h11 * hh * _mIn[i + 1];
    }
}

/// <summary>Constant-speed progress with C2 smooth ease-in/ease-out ramps (smoothstep velocity profile).</summary>
public static class ConstantSpeedProfile
{
    /// <summary>Maps normalized time u (0..1) to normalized distance (0..1).</summary>
    public static float Progress(float u, float a, float b)
    {
        u = Math.Clamp(u, 0f, 1f);
        a = Math.Clamp(a, 0f, 0.5f);
        b = Math.Clamp(b, 0f, 0.5f);
        float v = 1f / (1f - a / 2f - b / 2f);
        if(a > 0 && u < a)
        {
            float x = u / a;
            return v * a * (x * x * x - x * x * x * x / 2f);
        }
        if(b > 0 && u > 1 - b)
        {
            float y = (1 - u) / b;
            return 1f - v * b * (y * y * y - y * y * y * y / 2f);
        }
        return v * (u - a / 2f);
    }
}
