using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

public enum CurvePrimitive { Bezier, Circle, Path }

/// <summary>Selected part of a control point: the point itself (with both handles) or one handle.</summary>
public enum CurvePart { Point, Left, Right }
public readonly record struct CurveSelection(int Spline, int Point, CurvePart Part = CurvePart.Point);

/// <summary>Primitives and edit operators, following Blender's editcurve_add.cc / editcurve.cc.</summary>
public static class CurveEdit
{
    /// <summary>Blender draws primitives in its XY ground plane (Z up); the game is Y up, so XY maps to XZ.</summary>
    public static Vector3 FromBlender(float x, float y, float z = 0) => new(x, z, -y);

    /// <summary>ED_curve_add_nurbs_primitive for CU_PRIM_CURVE / CU_PRIM_CIRCLE (Bezier) and CU_PRIM_PATH.</summary>
    public static CurveObject Create(CurvePrimitive kind, Vector3 location, float radius = 1)
    {
        var spline = new Spline();
        var obj = new CurveObject { Location = location, Scale = new Vector3(radius), Splines = [spline] };
        CurvePoint B(Vector3 left, Vector3 co, Vector3 right, CurveHandle type) =>
            new() { Left = left, Co = co, Right = right, LeftType = type, RightType = type };
        switch(kind)
        {
            case CurvePrimitive.Bezier:
                obj.Name = I18n.T("core.curve.bezier");
                spline.Points.Add(B(FromBlender(-1.5f, -.5f), FromBlender(-1, 0), FromBlender(-.5f, .5f), CurveHandle.Aligned));
                spline.Points.Add(B(FromBlender(0, 0), FromBlender(1, 0), FromBlender(2, 0), CurveHandle.Aligned));
                break;
            case CurvePrimitive.Circle:
                obj.Name = I18n.T("core.curve.circle");
                spline.Cyclic = true;
                foreach(var (x, y) in new[] { (-1f, 0f), (0f, 1f), (1f, 0f), (0f, -1f) })
                    spline.Points.Add(B(Vector3.Zero, FromBlender(x, y), Vector3.Zero, CurveHandle.Auto));
                break;
            case CurvePrimitive.Path:
                obj.Name = I18n.T("core.curve.path");
                spline.Type = SplineType.Nurbs; spline.Order = 5; spline.Endpoint = true;
                for(int i = -2; i <= 2; i++) spline.Points.Add(new CurvePoint { Co = FromBlender(i, 0) });
                break;
        }
        spline.CalcHandles();
        return obj;
    }

    // ---------------------------------------------------------------- point / handle transforms

    /// <summary>Moves a control point; its handles move with it (Blender selects them together).</summary>
    public static void MovePoint(Spline s, int i, Vector3 co)
    {
        var p = s.Points[i]; var d = co - p.Co;
        p.Co = co; p.Left += d; p.Right += d;
        s.CalcHandles();
    }

    /// <summary>
    /// Moves one handle. Like Blender's handle test: grabbing an Auto handle turns the pair into Aligned,
    /// grabbing a Vector handle frees that side; Aligned partners then follow the grabbed side.
    /// </summary>
    public static void MoveHandle(Spline s, int i, bool left, Vector3 position)
    {
        if(s.Type != SplineType.Bezier) return;
        var p = s.Points[i];
        var type = left ? p.LeftType : p.RightType;
        if(type == CurveHandle.Auto) { p.LeftType = CurveHandle.Aligned; p.RightType = CurveHandle.Aligned; }
        else if(type == CurveHandle.Vector) { if(left) p.LeftType = CurveHandle.Free; else p.RightType = CurveHandle.Free; }
        if(left) p.Left = position; else p.Right = position;
        s.CalcHandles(i, left);
    }

    public static void SetHandleType(Spline s, IEnumerable<int> points, CurveHandle type)
    {
        foreach(int i in points) { s.Points[i].LeftType = type; s.Points[i].RightType = type; }
        s.CalcHandles();
    }

    // ---------------------------------------------------------------- topology

    /// <summary>
    /// Extrude from an open end (Blender E on an end point). The new point continues the end tangent
    /// by the length of the last segment so it is visible right away; its handle types copy the end point.
    /// Returns the new point index, or -1 if <paramref name="end"/> is not an open end.
    /// </summary>
    public static int Extrude(Spline s, int end)
    {
        if(s.Cyclic || s.Points.Count == 0 || end != 0 && end != s.Points.Count - 1) return -1;
        var tip = s.Points[end];
        Vector3 dir;
        if(s.Points.Count == 1) dir = new Vector3(1, 0, 0);
        else
        {
            var neighbour = s.Points[end == 0 ? 1 : end - 1];
            dir = tip.Co - neighbour.Co;
            if(dir.LengthSquared() < 1e-10f) dir = new Vector3(1, 0, 0);
        }
        var p = tip.Clone();
        p.Id = Guid.NewGuid();
        p.Co = tip.Co + dir; p.Left = tip.Left + dir; p.Right = tip.Right + dir;
        int index = end == 0 ? 0 : s.Points.Count;
        s.Points.Insert(index, p);
        s.CalcHandles();
        return index;
    }

    /// <summary>
    /// Subdivide (Blender subdividenurb, 1 cut): a new point in every segment whose two ends are selected.
    /// Bezier segments split exactly by de Casteljau, so the shape is preserved; the new point is Aligned
    /// unless both neighbours are fully automatic. NURBS/poly points are inserted at control midpoints.
    /// Returns the indices of the new points.
    /// </summary>
    public static List<int> Subdivide(Spline s, ISet<int> selected)
    {
        var created = new List<int>();
        int n = s.Points.Count;
        if(n < 2) return created;
        var cuts = new List<int>(); // segment i is between point i and i+1 (wrapping)
        for(int i = 0; i < s.SegmentCount; i++) if(selected.Contains(i) && selected.Contains((i + 1) % n)) cuts.Add(i);
        for(int c = cuts.Count - 1; c >= 0; c--)
        {
            int i = cuts[c]; var a = s.Points[i]; var b = s.Wrap(i + 1);
            CurvePoint mid;
            if(s.Type == SplineType.Bezier)
            {
                var ab = Vector3.Lerp(a.Co, a.Right, .5f); var bc = Vector3.Lerp(a.Right, b.Left, .5f); var cd = Vector3.Lerp(b.Left, b.Co, .5f);
                var abc = Vector3.Lerp(ab, bc, .5f); var bcd = Vector3.Lerp(bc, cd, .5f);
                bool auto = a.RightType == CurveHandle.Auto && b.LeftType == CurveHandle.Auto;
                mid = new CurvePoint
                {
                    Co = Vector3.Lerp(abc, bcd, .5f), Left = abc, Right = bcd,
                    LeftType = auto ? CurveHandle.Auto : CurveHandle.Aligned, RightType = auto ? CurveHandle.Auto : CurveHandle.Aligned,
                };
                // keep neighbours exact: shorten their facing handles (free them if they were automatic)
                if(!auto)
                {
                    a.Right = ab; b.Left = cd;
                    if(a.RightType is CurveHandle.Auto or CurveHandle.Vector) { a.LeftType = a.RightType = CurveHandle.Aligned; }
                    if(b.LeftType is CurveHandle.Auto or CurveHandle.Vector) { b.LeftType = b.RightType = CurveHandle.Aligned; }
                }
            }
            else mid = new CurvePoint { Co = Vector3.Lerp(a.Co, b.Co, .5f), W = (a.W + b.W) / 2 };
            mid.Tilt = (a.Tilt + b.Tilt) / 2; mid.Radius = (a.Radius + b.Radius) / 2;
            s.Points.Insert(i + 1, mid);
            created.Add(i + 1);
        }
        // indices shift as later inserts land first; recompute final positions
        created = created.Select((index, k) => index + (cuts.Count - 1 - k)).OrderBy(x => x).ToList();
        if(s.Type == SplineType.Bezier)
        {
            // only automatic points refit; Aligned/Free handles keep the exact split
            s.CalcHandles();
        }
        return created;
    }

    /// <summary>Delete vertices. Returns false when the spline became empty (the caller removes it).</summary>
    public static bool Delete(Spline s, ISet<int> selected)
    {
        s.Points = s.Points.Where((_, i) => !selected.Contains(i)).ToList();
        if(s.Points.Count < 3) s.Cyclic = s.Cyclic && s.Points.Count >= 3;
        s.CalcHandles();
        return s.Points.Count > 0;
    }

    /// <summary>Toggle Cyclic (Blender Alt+C).</summary>
    public static void ToggleCyclic(Spline s)
    {
        if(s.Points.Count < 2) return;
        s.Cyclic = !s.Cyclic;
        s.CalcHandles();
    }

    /// <summary>Switch Direction: reverses the points, swapping each point's handles; tilt changes sign.</summary>
    public static void SwitchDirection(Spline s)
    {
        s.Points.Reverse();
        foreach(var p in s.Points)
        {
            (p.Left, p.Right) = (p.Right, p.Left);
            (p.LeftType, p.RightType) = (p.RightType, p.LeftType);
            p.Tilt = -p.Tilt;
        }
        s.CalcHandles();
    }
}
