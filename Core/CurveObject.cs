using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

// Curve objects modelled on Blender's legacy Curve datablock (Nurb / BezTriple / BPoint).
// Algorithms are ported from Blender (GPL-2.0-or-later): blenkernel/intern/curve.cc,
// blenkernel/intern/anim_path.cc and editors/curve/editcurve*.cc.

/// <summary>Bezier handle types, same meaning as Blender's HD_FREE / HD_AUTO / HD_VECT / HD_ALIGN.</summary>
public enum CurveHandle { Free, Auto, Vector, Aligned }
public enum SplineType { Bezier, Nurbs, Poly }

/// <summary>A Bezier triple (Co + absolute handles) or a NURBS/poly control point (Co + weight).</summary>
public sealed class CurvePoint
{
    /// <summary>Identity kept through clones and topology edits (time anchors refer to it).</summary>
    public Guid Id = Guid.NewGuid();
    public Vector3 Co, Left, Right;
    public CurveHandle LeftType = CurveHandle.Auto, RightType = CurveHandle.Auto;
    public float W = 1, Tilt, Radius = 1;
    public CurvePoint Clone() => (CurvePoint)MemberwiseClone();
}

public sealed class Spline
{
    public SplineType Type = SplineType.Bezier;
    public bool Cyclic;
    public int Resolution = 12;
    /// <summary>NURBS order (Blender orderu); clamped to the point count when evaluating.</summary>
    public int Order = 4;
    /// <summary>NURBS endpoint knots (Blender CU_NURB_ENDPOINT): the curve reaches the end control points.</summary>
    public bool Endpoint;
    public List<CurvePoint> Points = [];

    public Spline Clone() { var s = (Spline)MemberwiseClone(); s.Points = Points.Select(p => p.Clone()).ToList(); return s; }

    public int SegmentCount => Points.Count < 2 ? 0 : Points.Count - 1 + (Cyclic ? 1 : 0);
    public bool CanEvaluate => Type == SplineType.Nurbs ? Points.Count >= 2 : SegmentCount > 0;

    public CurvePoint Wrap(int i) => Points[((i % Points.Count) + Points.Count) % Points.Count];

    // ---------------------------------------------------------------- evaluation

    /// <summary>Parameter domain. Bezier/poly: [0, segments]; NURBS: knot range.</summary>
    public (double start, double end) Domain()
    {
        if(Type != SplineType.Nurbs) return (0, SegmentCount);
        var (knots, order, count) = NurbsBasis();
        return (knots[order - 1], knots[count]);
    }

    public Vector3 Evaluate(double t) => Sample(t).pos;
    public float TiltAt(double t) => Sample(t).tilt;
    public float RadiusAt(double t) => Sample(t).radius;

    public (Vector3 pos, float tilt, float radius) Sample(double t)
    {
        if(Points.Count == 0) return (Vector3.Zero, 0, 1);
        if(Points.Count == 1) return (Points[0].Co, Points[0].Tilt, Points[0].Radius);
        if(Type == SplineType.Nurbs) return SampleNurbs(t);
        int segments = SegmentCount;
        t = Math.Clamp(t, 0, segments);
        int i = Math.Min((int)Math.Floor(t), segments - 1);
        float u = (float)(t - i);
        var a = Points[i]; var b = Wrap(i + 1);
        float tilt = a.Tilt + (b.Tilt - a.Tilt) * u, radius = a.Radius + (b.Radius - a.Radius) * u;
        if(Type == SplineType.Poly) return (Vector3.Lerp(a.Co, b.Co, u), tilt, radius);
        return (Bezier(a.Co, a.Right, b.Left, b.Co, u), tilt, radius);
    }

    /// <summary>Tangent (not normalized) by central difference in parameter space.</summary>
    public Vector3 Tangent(double t)
    {
        var (s, e) = Domain();
        double h = Math.Max(1e-5, (e - s) * 1e-5);
        double a = Math.Max(s, t - h), b = Math.Min(e, t + h);
        if(Cyclic && Type != SplineType.Nurbs)
        {
            // keep the derivative well defined across the closing seam
            if(t - h < s) a = t; if(t + h > e) b = t;
        }
        var d = Evaluate(b) - Evaluate(a);
        return b > a ? d / (float)(b - a) : Vector3.Zero;
    }

    public static Vector3 Bezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float s = 1 - t;
        return s * s * s * p0 + 3 * s * s * t * p1 + 3 * s * t * t * p2 + t * t * t * p3;
    }

    /// <summary>Knot vector following Blender's makeknots: uniform, endpoint, or cyclic (wrapped points).</summary>
    internal (double[] knots, int order, int count) NurbsBasis()
    {
        int n = Points.Count;
        int order = Math.Clamp(Order, 2, Math.Max(2, n));
        int count = Cyclic ? n + order - 1 : n; // cyclic repeats the first order-1 points
        var knots = new double[count + order];
        bool endpoint = Endpoint && !Cyclic;
        for(int i = 0; i < knots.Length; i++)
        {
            if(!endpoint) knots[i] = i;
            else knots[i] = i < order ? 0 : i >= count ? count - order + 1 : i - order + 1;
        }
        return (knots, order, count);
    }

    private (Vector3 pos, float tilt, float radius) SampleNurbs(double t)
    {
        var (knots, order, count) = NurbsBasis();
        double start = knots[order - 1], end = knots[count];
        t = Math.Clamp(t, start, end);
        // span index k with knots[k] <= t < knots[k+1]
        int k = order - 1;
        while(k < count - 1 && t >= knots[k + 1]) k++;
        var basis = new double[order];
        basis[0] = 1;
        var left = new double[order]; var right = new double[order];
        for(int j = 1; j < order; j++)
        {
            left[j] = t - knots[k + 1 - j]; right[j] = knots[k + j] - t;
            double saved = 0;
            for(int r = 0; r < j; r++)
            {
                double denom = right[r + 1] + left[j - r];
                double temp = denom == 0 ? 0 : basis[r] / denom;
                basis[r] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            basis[j] = saved;
        }
        double w = 0, tilt = 0, radius = 0; var pos = Vector3.Zero;
        for(int j = 0; j < order; j++)
        {
            var p = Wrap(k - order + 1 + j);
            double b = basis[j] * p.W;
            pos += p.Co * (float)b; w += b; tilt += b * p.Tilt; radius += b * p.Radius;
        }
        if(w == 0) return (Points[0].Co, Points[0].Tilt, Points[0].Radius);
        return (pos / (float)w, (float)(tilt / w), (float)(radius / w));
    }

    // ---------------------------------------------------------------- Bezier handles

    /// <summary>
    /// Port of Blender calchandlesNurb_intern / calchandleNurb_intern (3D curve branch).
    /// <paramref name="editedPoint"/>/<paramref name="editedLeft"/> play the role of the selected handle
    /// (f1 flag) when resolving Aligned pairs; otherwise the left handle follows the right one.
    /// </summary>
    public void CalcHandles(int editedPoint = -1, bool editedLeft = false)
    {
        if(Type != SplineType.Bezier || Points.Count < 2) return;
        int n = Points.Count;
        for(int i = 0; i < n; i++)
        {
            CurvePoint? prev = i > 0 ? Points[i - 1] : Cyclic ? Points[n - 1] : null;
            CurvePoint? next = i + 1 < n ? Points[i + 1] : Cyclic ? Points[0] : null;
            CalcHandle(Points[i], prev, next, i == editedPoint && editedLeft);
        }
    }

    /// <summary>Recomputes only the listed points (dirty-tangent model: neighbours keep their handles).</summary>
    public void CalcHandles(IEnumerable<int> points)
    {
        if(Type != SplineType.Bezier || Points.Count < 2) return;
        int n = Points.Count;
        foreach(int i in points.Distinct())
        {
            if(i < 0 || i >= n) continue;
            CurvePoint? prev = i > 0 ? Points[i - 1] : Cyclic ? Points[n - 1] : null;
            CurvePoint? next = i + 1 < n ? Points[i + 1] : Cyclic ? Points[0] : null;
            CalcHandle(Points[i], prev, next, false);
        }
    }

    internal static void CalcHandle(CurvePoint bezt, CurvePoint? prev, CurvePoint? next, bool leftSelected)
    {
        if(bezt.LeftType == CurveHandle.Free && bezt.RightType == CurveHandle.Free) return;
        if(prev == null && next == null) return;
        var p2 = bezt.Co;
        Vector3 p1 = prev?.Co ?? 2 * p2 - next!.Co;
        Vector3 p3 = next?.Co ?? 2 * p2 - p1;
        var dvecA = p2 - p1; var dvecB = p3 - p2;
        float lenA = dvecA.Length(), lenB = dvecB.Length();
        if(lenA == 0) lenA = 1;
        if(lenB == 0) lenB = 1;

        if(bezt.LeftType == CurveHandle.Auto || bezt.RightType == CurveHandle.Auto)
        {
            var tvec = dvecB / lenB + dvecA / lenA;
            float len = tvec.Length() * 2.5614f;
            if(len != 0)
            {
                lenA = MathF.Min(lenA, 5 * lenB);
                lenB = MathF.Min(lenB, 5 * lenA);
                if(bezt.LeftType == CurveHandle.Auto) bezt.Left = p2 - tvec * (lenA / len);
                if(bezt.RightType == CurveHandle.Auto) bezt.Right = p2 + tvec * (lenB / len);
            }
        }
        if(bezt.LeftType == CurveHandle.Vector) bezt.Left = p2 - dvecA / 3;
        if(bezt.RightType == CurveHandle.Vector) bezt.Right = p2 + dvecB / 3;

        if(bezt.LeftType == CurveHandle.Free || bezt.RightType == CurveHandle.Free ||
            bezt.LeftType != CurveHandle.Aligned && bezt.RightType != CurveHandle.Aligned) return;

        const float eps = 1e-5f;
        lenA = Vector3.Distance(p2, bezt.Left); lenB = Vector3.Distance(p2, bezt.Right);
        if(lenA == 0) lenA = 1;
        if(lenB == 0) lenB = 1;
        float ratio = lenA / lenB;
        if(leftSelected)
        {
            if(bezt.RightType == CurveHandle.Aligned && lenA > eps) bezt.Right = p2 + (p2 - bezt.Left) / ratio;
            if(bezt.LeftType == CurveHandle.Aligned && lenB > eps) bezt.Left = p2 + (p2 - bezt.Right) * ratio;
        }
        else
        {
            if(bezt.LeftType == CurveHandle.Aligned && lenB > eps) bezt.Left = p2 + (p2 - bezt.Right) * ratio;
            if(bezt.RightType == CurveHandle.Aligned && lenA > eps) bezt.Right = p2 + (p2 - bezt.Left) / ratio;
        }
    }
}

/// <summary>A curve object: object transform plus splines in object space (Blender Object + Curve).</summary>
public sealed class CurveObject
{
    public Guid Id = Guid.NewGuid();
    public string Name = "曲线";
    public Vector3 Location;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;
    public List<Spline> Splines = [];
    /// <summary>Track this curve was generated for (the default node path); null for user curves.</summary>
    public Guid? OwnerTrack;
    public long Revision { get; private set; }
    public void Touch() => Revision++;

    public CurveObject Clone()
    {
        var c = (CurveObject)MemberwiseClone();
        c.Splines = Splines.Select(s => s.Clone()).ToList();
        return c;
    }

    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Location);
    public Vector3 ToWorld(Vector3 local) => Vector3.Transform(local, Matrix);
    public Vector3 ToLocal(Vector3 world)
    {
        var p = Vector3.Transform(world - Location, Quaternion.Inverse(Rotation));
        return new Vector3(Scale.X == 0 ? 0 : p.X / Scale.X, Scale.Y == 0 ? 0 : p.Y / Scale.Y, Scale.Z == 0 ? 0 : p.Z / Scale.Z);
    }

    /// <summary>The spline the Follow Path constraint uses (Blender uses the first one).</summary>
    public Spline? PathSpline => Splines.FirstOrDefault(s => s.CanEvaluate);
}
