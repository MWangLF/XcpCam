using System;
using System.Collections.Generic;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>
/// Arc-length table over a curve object's path spline in world space, the equivalent of Blender's
/// anim path cache (BKE_anim_path_calc_data / BKE_where_on_path). A length factor 0..1 maps to a
/// position at that fraction of the total length. Unlike Blender, positions are evaluated exactly on
/// the spline instead of interpolating between cached polyline points.
/// </summary>
public sealed class CurvePath
{
    private readonly Spline _spline;
    private readonly Matrix4x4 _matrix;
    private readonly double[] _param;
    private readonly double[] _length;
    public float Length { get; }
    public bool Cyclic => _spline.Cyclic;
    public Spline Spline => _spline;

    private CurvePath(Spline spline, Matrix4x4 matrix, double[] param, double[] length)
    {
        _spline = spline; _matrix = matrix; _param = param; _length = length;
        Length = (float)length[^1];
    }

    public static CurvePath? Create(CurveObject curve) => curve.PathSpline is { } s ? Create(s, curve.Matrix) : null;

    public static CurvePath? Create(Spline spline, Matrix4x4 matrix)
    {
        if(!spline.CanEvaluate) return null;
        var (start, end) = spline.Domain();
        if(!(end > start)) return null;
        int pieces = Math.Max(1, spline.SegmentCount);
        int steps = Math.Clamp(pieces * Math.Max(spline.Resolution, 4) * 4, 64, 8192);
        var param = new double[steps + 1]; var length = new double[steps + 1];
        var prev = Vector3.Transform(spline.Evaluate(start), matrix);
        param[0] = start;
        for(int i = 1; i <= steps; i++)
        {
            double t = start + (end - start) * i / steps;
            var p = Vector3.Transform(spline.Evaluate(t), matrix);
            param[i] = t; length[i] = length[i - 1] + Vector3.Distance(prev, p);
            prev = p;
        }
        return new CurvePath(spline, matrix, param, length);
    }

    /// <summary>Spline parameter at a length factor. Cyclic curves wrap; open curves clamp (CU_PATH_CLAMP).</summary>
    public double Parameter(double factor)
    {
        if(Cyclic) factor -= Math.Floor(factor);
        else factor = Math.Clamp(factor, 0, 1);
        if(Length <= 0) return _param[0];
        double target = factor * Length;
        int lo = 0, hi = _length.Length - 1;
        while(hi - lo > 1) { int mid = (lo + hi) / 2; if(_length[mid] < target) lo = mid; else hi = mid; }
        double span = _length[hi] - _length[lo];
        double f = span > 0 ? (target - _length[lo]) / span : 0;
        return _param[lo] + (_param[hi] - _param[lo]) * f;
    }

    /// <summary>Length factor of a spline parameter.</summary>
    public double Factor(double parameter)
    {
        if(Length <= 0) return 0;
        int lo = 0, hi = _param.Length - 1;
        if(parameter <= _param[0]) return 0;
        if(parameter >= _param[^1]) return 1;
        while(hi - lo > 1) { int mid = (lo + hi) / 2; if(_param[mid] < parameter) lo = mid; else hi = mid; }
        double f = (parameter - _param[lo]) / (_param[hi] - _param[lo]);
        return (_length[lo] + (_length[hi] - _length[lo]) * f) / Length;
    }

    public Vector3 Position(double factor) => Vector3.Transform(_spline.Evaluate(Parameter(factor)), _matrix);

    public PathPoint Sample(double factor)
    {
        double t = Parameter(factor);
        var (pos, tilt, radius) = _spline.Sample(t);
        var tangent = Vector3.TransformNormal(_spline.Tangent(t), _matrix);
        if(tangent.LengthSquared() < 1e-14f)
        {
            // degenerate (zero-length handle at an end): use the chord towards a nearby point
            var (s, e) = _spline.Domain();
            double other = t + (t < (s + e) / 2 ? 1 : -1) * (e - s) * 1e-3;
            tangent = Vector3.Transform(_spline.Evaluate(other), _matrix) - Vector3.Transform(pos, _matrix);
            if(other < t) tangent = -tangent;
        }
        return new PathPoint(Vector3.Transform(pos, _matrix), tangent.LengthSquared() > 0 ? Vector3.Normalize(tangent) : Vector3.UnitZ, tilt, radius);
    }

    /// <summary>Length factor of the point on the path closest to <paramref name="world"/>.</summary>
    public double Project(Vector3 world)
    {
        int best = 0; float bestDist = float.MaxValue;
        for(int i = 0; i < _param.Length; i += Math.Max(1, _param.Length / 512))
        {
            float d = Vector3.DistanceSquared(Vector3.Transform(_spline.Evaluate(_param[i]), _matrix), world);
            if(d < bestDist) { bestDist = d; best = i; }
        }
        // refine on the fine table around the coarse hit, then by bisection on the parameter
        int step = Math.Max(1, _param.Length / 512);
        int lo = Math.Max(0, best - step), hi = Math.Min(_param.Length - 1, best + step);
        for(int i = lo; i <= hi; i++)
        {
            float d = Vector3.DistanceSquared(Vector3.Transform(_spline.Evaluate(_param[i]), _matrix), world);
            if(d < bestDist) { bestDist = d; best = i; }
        }
        double a = _param[Math.Max(0, best - 1)], b = _param[Math.Min(_param.Length - 1, best + 1)];
        float D(double t) => Vector3.DistanceSquared(Vector3.Transform(_spline.Evaluate(t), _matrix), world);
        for(int n = 0; n < 40; n++)
        {
            double m1 = a + (b - a) / 3, m2 = b - (b - a) / 3;
            if(D(m1) < D(m2)) b = m2; else a = m1;
        }
        return Factor((a + b) / 2);
    }

    /// <summary>Polyline for drawing, in world space.</summary>
    public List<Vector3> Polyline(int maxPoints = 512)
    {
        var result = new List<Vector3>();
        int step = Math.Max(1, (_param.Length - 1) / Math.Max(2, maxPoints));
        for(int i = 0; i < _param.Length; i += step) result.Add(Vector3.Transform(_spline.Evaluate(_param[i]), _matrix));
        var last = Vector3.Transform(_spline.Evaluate(_param[^1]), _matrix);
        if(result.Count == 0 || result[^1] != last) result.Add(last);
        return result;
    }

    /// <summary>
    /// Orientation of the path at a point (Blender FOLLOWPATH_FOLLOW): <paramref name="forward"/> is aligned to
    /// the tangent, <paramref name="up"/> towards world up, then rolled by the curve tilt around the tangent.
    /// </summary>
    public static Quaternion Frame(PathPoint point, Vector3 forward, Vector3 up, Vector3 worldUp)
    {
        var t = point.Tangent;
        var u = worldUp - Vector3.Dot(worldUp, t) * t;
        if(u.LengthSquared() < 1e-8f) u = MathF.Abs(t.X) < .9f ? Vector3.UnitX : Vector3.UnitZ;
        u = Vector3.Normalize(u - Vector3.Dot(u, t) * t);
        // rotation taking the local (forward, up) basis to the world (tangent, u) basis
        var local = Basis(forward, up); var world = Basis(t, u);
        var q = Quaternion.Normalize(world * Quaternion.Inverse(local));
        return point.Tilt == 0 ? q : Quaternion.Normalize(Quaternion.CreateFromAxisAngle(t, point.Tilt) * q);
    }

    private static Quaternion Basis(Vector3 f, Vector3 u)
    {
        f = Vector3.Normalize(f); u = Vector3.Normalize(u - Vector3.Dot(u, f) * f);
        var r = Vector3.Cross(u, f);
        var m = new Matrix4x4(r.X, r.Y, r.Z, 0, u.X, u.Y, u.Z, 0, f.X, f.Y, f.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }
}

public readonly record struct PathPoint(Vector3 Position, Vector3 Tangent, float Tilt, float Radius);
