using System;
using System.Collections.Generic;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>
/// SQUAD quaternion spline (Shoemake) with non-uniform knot spacing: separate outgoing/incoming control
/// quaternions built from a Bessel-weighted angular velocity, so angular speed is continuous across keys
/// even when the legs have different durations.
/// </summary>
public sealed class SquadCurve
{
    private readonly float[] _k;
    private readonly Quaternion[] _q, _a, _b;
    private readonly bool[] _constant;

    /// <param name="knots">Strictly non-decreasing parameter per key.</param>
    /// <param name="zeroVel">Per key: angular velocity forced to zero (stop / hold neighbour).</param>
    /// <param name="constantSeg">Per segment: rotation held constant (hold).</param>
    public SquadCurve(IReadOnlyList<float> knots, IReadOnlyList<Quaternion> rots, IReadOnlyList<bool> zeroVel,
        IReadOnlyList<bool>? constantSeg, bool zeroStart, bool zeroEnd)
    {
        int n = rots.Count;
        _k = [.. knots];
        _q = new Quaternion[n];
        _a = new Quaternion[n];
        _b = new Quaternion[n];
        _constant = new bool[Math.Max(0, n - 1)];
        if(constantSeg != null)
            for(int i = 0; i < n - 1; i++) _constant[i] = constantSeg[i];

        _q[0] = Quaternion.Normalize(rots[0]);
        for(int i = 1; i < n; i++)
        {
            var q = Quaternion.Normalize(rots[i]);
            if(Quaternion.Dot(_q[i - 1], q) < 0) q = Quaternion.Negate(q);
            _q[i] = q;
        }

        for(int i = 0; i < n; i++)
        {
            var inv = Quaternion.Inverse(_q[i]);
            Vector3 lPlus = i < n - 1 ? Log(inv * _q[i + 1]) : Vector3.Zero;
            Vector3 lMinus = i > 0 ? Log(inv * _q[i - 1]) : Vector3.Zero;
            float hOut = i < n - 1 ? MathF.Max(_k[i + 1] - _k[i], 1e-6f) : 0;
            float hIn = i > 0 ? MathF.Max(_k[i] - _k[i - 1], 1e-6f) : 0;

            Vector3 v; // angular velocity per unit knot, in q_i's local frame (log space)
            bool zero = zeroVel[i]
                || (i == 0 && zeroStart) || (i == n - 1 && zeroEnd)
                || (i < n - 1 && _constant[i]) || (i > 0 && _constant[i - 1]);
            if(zero || n == 1)
                v = Vector3.Zero;
            else if(i == 0)
                v = lPlus / hOut;
            else if(i == n - 1)
                v = -lMinus / hIn;
            else
                v = (hIn * (lPlus / hOut) + hOut * (-lMinus / hIn)) / (hIn + hOut);

            var tOut = v * hOut;
            var tIn = v * hIn;
            _a[i] = _q[i] * Exp((tOut - lPlus) / 2f);
            _b[i] = _q[i] * Exp((-lMinus - tIn) / 2f);
        }
    }

    public Quaternion Evaluate(float k)
    {
        int n = _q.Length;
        if(n == 1 || k <= _k[0]) return _q[0];
        if(k >= _k[^1]) return _q[^1];

        int lo = 0, hi = n - 1;
        while(hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if(_k[mid] <= k) lo = mid; else hi = mid;
        }
        int i = lo;
        if(_constant[i]) return _q[i];
        float span = _k[i + 1] - _k[i];
        if(span <= 1e-6f) return _q[i + 1];
        float h = Math.Clamp((k - _k[i]) / span, 0f, 1f);

        var outer = SlerpNoFlip(_q[i], _q[i + 1], h);
        var inner = SlerpNoFlip(_a[i], _b[i + 1], h);
        return Quaternion.Normalize(SlerpNoFlip(outer, inner, 2f * h * (1f - h)));
    }

    public static Vector3 Log(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var v = new Vector3(q.X, q.Y, q.Z);
        float s = v.Length();
        if(s < 1e-7f) return Vector3.Zero;
        float angle = MathF.Atan2(s, q.W);
        return v * (angle / s);
    }

    public static Quaternion Exp(Vector3 v)
    {
        float a = v.Length();
        if(a < 1e-7f) return Quaternion.Identity;
        float s = MathF.Sin(a) / a;
        return new Quaternion(v.X * s, v.Y * s, v.Z * s, MathF.Cos(a));
    }

    /// <summary>Slerp that does not take the short path automatically (SQUAD needs the raw interpolation).</summary>
    public static Quaternion SlerpNoFlip(Quaternion a, Quaternion b, float t)
    {
        float dot = Math.Clamp(Quaternion.Dot(a, b), -1f, 1f);
        if(dot > 0.9995f || dot < -0.9995f)
            return Quaternion.Normalize(new Quaternion(
                a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t));
        float theta = MathF.Acos(dot);
        float sinT = MathF.Sin(theta);
        float wa = MathF.Sin((1 - t) * theta) / sinT, wb = MathF.Sin(t * theta) / sinT;
        return new Quaternion(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
    }
}
