using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// In-game curve objects, modelled on Blender's curve editing: add Bezier curve / circle / NURBS path, object
/// mode (move / rotate / scale the whole curve) and edit mode (control points and Bezier handles, extrude,
/// subdivide, delete, cyclic, switch direction, handle types, tilt, radius). The active track's own node path
/// can be edited the same way: its control points are the camera nodes, its handles shape the curvature.
/// </summary>
public sealed class CurveTool
{
    private readonly CamTimeline _tl;
    private readonly Overlay _overlay;
    private readonly GizmoEditor _gizmo;
    private readonly Func<Keyframe, ShotCurves?> _shotFor;
    private readonly Action<Keyframe> _writeNodePosition;
    private readonly Action<string, bool> _status;

    /// <summary>Shows the selected curve's path handles/control points in game (handles, object mode, node path handles).
    /// Picking and the gizmo follow whatever was selected last.</summary>
    public bool Active = true;
    /// <summary>Track whose followed curve the selected points were picked from (anchor context).</summary>
    public CamTrack? PointTrack;
    /// <summary>Control points of the active track's followed curve, always pickable: (point index, screen).</summary>
    public readonly List<(int point, Vector2 screen)> FollowCandidates = [];
    public int? HoverFollow;
    /// <summary>Selected user curve; null with <see cref="NodePath"/> edits the active track's node path.</summary>
    public Guid? CurveId;
    public bool NodePath = true;
    /// <summary>Blender edit mode (points / handles) vs object mode (whole curve).</summary>
    public bool EditMode = true;
    public readonly HashSet<CurveSelection> Selection = [];
    public readonly List<(CurveSelection sel, Vector2 screen)> Candidates = [];
    public CurveSelection? Hover;
    private int _handleType = 1;

    public CurveTool(CamTimeline tl, Overlay overlay, GizmoEditor gizmo, Func<Keyframe, ShotCurves?> shotFor,
        Action<Keyframe> writeNodePosition, Action<string, bool> status)
    {
        _tl = tl; _overlay = overlay; _gizmo = gizmo; _shotFor = shotFor; _writeNodePosition = writeNodePosition; _status = status;
    }

    public CurveObject? Curve => CurveId is Guid id ? _tl.Curves.FirstOrDefault(c => c.Id == id) : null;
    private bool EditingNodePath => Curve == null && NodePath;
    public bool HasGizmoTarget => EditMode ? Selection.Count > 0 : Curve != null;

    private List<Keyframe> PathNodes => _tl.ActiveTrack.Keys.OrderBy(k => k.Frame).ToList();
    /// <summary>Already-built shot of a node (per-frame drawing must not rebuild the curve model).</summary>
    private ShotCurves? ShotOf(Keyframe node) => _tl.ActiveTrack.Shots.FirstOrDefault(s => s.SeededNodes.Contains(node.Id)) ?? _shotFor(node);

    // ---------------------------------------------------------------- scene

    private static uint HandleColor(CurveHandle type) => type switch
    {
        CurveHandle.Auto => 0xFF00E0FF,    // Blender: auto = yellow
        CurveHandle.Vector => 0xFF40E060,  // vector = green
        CurveHandle.Aligned => 0xFFD080F0, // aligned = pink
        _ => 0xFF404040,                   // free = dark
    };

    /// <summary>Draws every curve object, the edit cage of the edited curve, and collects pick candidates.</summary>
    public void DrawScene()
    {
        Candidates.Clear();
        FollowCandidates.Clear();
        if(!_overlay.Enabled) return;
        foreach(var curve in _tl.Curves)
        {
            bool selected = curve.Id == CurveId;
            bool followed = _tl.Tracks.Any(t => t.FollowPath?.CurveId == curve.Id);
            uint color = selected ? 0xFF3399FF : followed ? 0xFFDDDDDD : 0xFF999999;
            foreach(var spline in curve.Splines)
                if(CurvePath.Create(spline, curve.Matrix) is { } path)
                    _overlay.DrawPolyline(path.Polyline(), color, selected ? 2.5f : 1.5f);
            if(_overlay.ShowLabels && curve.PathSpline is { } first && first.Points.Count > 0)
                _overlay.DrawLabel(curve.ToWorld(first.Points[0].Co), curve.Name, color);
        }
        DrawFollowedPoints();
        if(!Active) return;
        if(Curve is { } c && EditMode) DrawCage(c);
        else if(Curve is { } o && !EditMode && _overlay.ToScreen(Pivot(o), out var s))
            Candidates.Add((new CurveSelection(-1, -1), s));
        else if(EditingNodePath && _tl.ActiveTrack.FollowPath == null) DrawNodePathCage();
    }

    private void DrawFollowedPoints()
    {
        var track = _tl.ActiveTrack;
        if(track.FollowPath is not { } follow || track.FollowCurve is not { } curve || curve.PathSpline is not { } spline) return;
        if(Active && EditMode && CurveId == curve.Id) return;
        int si = curve.Splines.IndexOf(spline);
        bool context = CurveId == curve.Id && EditMode;
        for(int i = 0; i < spline.Points.Count; i++)
        {
            var w = curve.ToWorld(spline.Points[i].Co);
            bool sel = context && Selection.Contains(new CurveSelection(si, i));
            bool anchored = follow.TimeAnchors.ContainsKey(spline.Points[i].Id);
            _overlay.DrawMarker(w, sel ? 0xFF33AAFF : anchored ? 0xFFFFCC66 : 0xFFFFFFFF, anchored, sel);
            if(_overlay.ToScreen(w, out var s)) FollowCandidates.Add((i, s));
        }
    }

    /// <summary>Selects a control point of a track's followed curve (timeline lane or game view).</summary>
    public void SelectFollowedPoint(CamTrack track, int point, bool additive)
    {
        if(track.FollowCurve is not { } curve || curve.PathSpline is not { } spline || point < 0 || point >= spline.Points.Count) return;
        if(CurveId != curve.Id || !EditMode || NodePath && CurveId == null) { Selection.Clear(); additive = false; }
        CurveId = curve.Id; NodePath = false; EditMode = true; PointTrack = track;
        var s = new CurveSelection(curve.Splines.IndexOf(spline), point);
        if(additive) { if(!Selection.Add(s)) Selection.Remove(s); }
        else { Selection.Clear(); Selection.Add(s); }
    }

    /// <summary>Box selection adds all hit control points without toggling previously selected points.</summary>
    public void SelectFollowedPoints(CamTrack track, IEnumerable<int> points, bool additive)
    {
        if(track.FollowCurve is not { } curve || curve.PathSpline is not { } spline) return;
        if(!additive || CurveId != curve.Id || !EditMode) Selection.Clear();
        CurveId = curve.Id; NodePath = false; EditMode = true; PointTrack = track;
        int si = curve.Splines.IndexOf(spline);
        foreach(int point in points)
            if(point >= 0 && point < spline.Points.Count) Selection.Add(new CurveSelection(si, point));
    }

    /// <summary>The following track the selected points belong to (for anchors), if any.</summary>
    public CamTrack? AnchorTrack
    {
        get
        {
            if(Curve is not { } curve) return null;
            bool Follows(CamTrack t) => t.FollowPath?.CurveId == curve.Id;
            if(PointTrack != null && _tl.Tracks.Contains(PointTrack) && Follows(PointTrack)) return PointTrack;
            return Follows(_tl.ActiveTrack) ? _tl.ActiveTrack : _tl.Tracks.FirstOrDefault(Follows);
        }
    }

    /// <summary>Selected control points of the edited curve (handles excluded), in curve order.</summary>
    public List<(int spline, int index, CurvePoint point)> SelectedPoints()
    {
        if(!EditMode || Curve is not { } curve) return [];
        return Selection.Where(s => s.Part == CurvePart.Point && ValidPart(curve, s))
            .Select(s => (spline: s.Spline, index: s.Point, point: curve.Splines[s.Spline].Points[s.Point]))
            .Distinct().OrderBy(x => x.spline).ThenBy(x => x.index).ToList();
    }

    /// <summary>Tilt / radius of the selected points (path tab and N-Panel inspector).</summary>
    public void DrawPointProperties(CurveObject c, List<CurvePoint> selectedPoints, bool compact = false)
    {
        if(selectedPoints.Count == 0) return;
        float tilt = selectedPoints.Average(p => p.Tilt) * 180 / MathF.PI, radius = selectedPoints.Average(p => p.Radius);
        if(compact)
        {
            float half = MathF.Max(60f, (UiWidgets.InnerAvailWidth - 6f * XcpStyle.Scale) * 0.5f);
            ImGui.SetNextItemWidth(half);
            if(ImGui.DragFloat("##tilt", ref tilt, .5f, -3600f, 3600f, I18n.T("curve.tilt_fmt"))) { foreach(var p in selectedPoints) p.Tilt = tilt * MathF.PI / 180; c.Touch(); _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            UiWidgets.Tooltip(I18n.T("curve.tilt_tip"));
            ImGui.SameLine(0, 6f * XcpStyle.Scale);
            ImGui.SetNextItemWidth(half);
            if(ImGui.DragFloat("##radius", ref radius, .01f, 0f, 100f, I18n.T("curve.radius_fmt"))) { foreach(var p in selectedPoints) p.Radius = MathF.Max(0, radius); c.Touch(); _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            UiWidgets.Tooltip(I18n.T("curve.radius_tip"));
        }
        else
        {
            ImGui.SetNextItemWidth(120f * XcpStyle.Scale);
            if(ImGui.DragFloat($"{I18n.T("curve.tilt_label")}##tilt_f", ref tilt, .5f)) { foreach(var p in selectedPoints) p.Tilt = tilt * MathF.PI / 180; c.Touch(); _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            UiWidgets.Tooltip(I18n.T("curve.tilt_tip"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f * XcpStyle.Scale);
            if(ImGui.DragFloat($"{I18n.T("curve.radius_label")}##rad_f", ref radius, .01f, 0f, 100f)) { foreach(var p in selectedPoints) p.Radius = MathF.Max(0, radius); c.Touch(); _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            UiWidgets.Tooltip(I18n.T("curve.radius_tip"));
        }
    }

    /// <summary>Handle type of the selected points (path tab and N-Panel inspector).</summary>
    public void DrawHandleTypeControl(bool brioTheme = true, bool compact = false)
    {
        float s = XcpStyle.Scale;
        string btnLabel = compact ? I18n.T("curve.btn_set_handle_compact") : I18n.T("curve.btn_set_handle");
        float btnW = MathF.Max((compact ? 74f : 90f) * s, ImGui.CalcTextSize(btnLabel).X + 16f * s);
        float comboW = compact ? MathF.Max(70f * s, UiWidgets.InnerAvailWidth - btnW - 5f * s) : 115f * s;
        ImGui.SetNextItemWidth(comboW);
        ImGui.Combo("##htype", ref _handleType, I18n.Combo("curve.handle.free", "curve.handle.auto", "curve.handle.vector", "curve.handle.aligned"));
        ImGui.SameLine(0, 5f * s);
        if(ImGui.Button($"{btnLabel}##ht", new Vector2(btnW, 0)))
            Edit(I18n.T("curve.status_handle_set"), curve => { foreach(var (sp, _, points) in SelectedSplines(curve)) CurveEdit.SetHandleType(sp, points, (CurveHandle)_handleType); });
        UiWidgets.Tooltip(I18n.T("curve.handle_tip"));
    }

    private void DrawCage(CurveObject curve)
    {
        for(int si = 0; si < curve.Splines.Count; si++)
        {
            var spline = curve.Splines[si];
            for(int i = 0; i < spline.Points.Count; i++)
            {
                var p = spline.Points[i];
                var co = curve.ToWorld(p.Co);
                bool pointSelected = Selection.Contains(new CurveSelection(si, i));
                if(spline.Type == SplineType.Bezier)
                {
                    foreach(var (part, local, type) in new[] { (CurvePart.Left, p.Left, p.LeftType), (CurvePart.Right, p.Right, p.RightType) })
                    {
                        var hw = curve.ToWorld(local);
                        bool hs = pointSelected || Selection.Contains(new CurveSelection(si, i, part));
                        _overlay.DrawLine(co, hw, HandleColor(type), 1.2f);
                        _overlay.DrawMarker(hw, hs ? 0xFF33AAFF : HandleColor(type), false, hs);
                        if(_overlay.ToScreen(hw, out var hsPos)) Candidates.Add((new CurveSelection(si, i, part), hsPos));
                    }
                }
                else if(i + 1 < spline.Points.Count || spline.Cyclic)
                    _overlay.DrawLine(co, curve.ToWorld(spline.Wrap(i + 1).Co), 0xFF808080, 1, true);
                _overlay.DrawMarker(co, pointSelected ? 0xFF33AAFF : 0xFF101010, true, pointSelected);
                if(_overlay.ToScreen(co, out var sc)) Candidates.Add((new CurveSelection(si, i), sc));
            }
        }
    }

    private void DrawNodePathCage()
    {
        var nodes = PathNodes;
        for(int i = 0; i < nodes.Count; i++)
        {
            if(ShotOf(nodes[i]) is not { } shot || shot.PathHandles(nodes[i]) is not { } h) continue;
            var co = shot.NodePose(nodes[i]).Pos;
            bool pointSelected = Selection.Contains(new CurveSelection(0, i));
            var color = HandleColor(h.type);
            bool first = i == 0, last = i == nodes.Count - 1;
            foreach(var (part, offset) in new[] { (CurvePart.Left, h.left), (CurvePart.Right, h.right) })
            {
                if(part == CurvePart.Left && first || part == CurvePart.Right && last) continue;
                var hw = co + offset;
                bool hs = Selection.Contains(new CurveSelection(0, i, part));
                _overlay.DrawLine(co, hw, color, 1.2f);
                _overlay.DrawMarker(hw, hs ? 0xFF33AAFF : color, false, hs);
                if(_overlay.ToScreen(hw, out var s)) Candidates.Add((new CurveSelection(0, i, part), s));
            }
            if(_overlay.ToScreen(co, out var sc)) Candidates.Add((new CurveSelection(0, i), sc));
            if(pointSelected) _overlay.DrawMarker(co, 0xFF33AAFF, true, true);
        }
    }

    public void Click(CurveSelection s, bool additive)
    {
        if(s.Spline < 0) return;
        if(additive) { if(!Selection.Add(s)) Selection.Remove(s); }
        else { Selection.Clear(); Selection.Add(s); }
        if(EditingNodePath && s.Part == CurvePart.Point)
        {
            var nodes = PathNodes;
            var ids = Selection.Where(x => x.Part == CurvePart.Point && x.Point < nodes.Count).Select(x => nodes[x.Point].Id);
            _tl.SelectNodes(ids);
        }
    }

    public void ClearSelection() => Selection.Clear();

    // ---------------------------------------------------------------- gizmo

    public bool OwnsGizmo => Selection.Count > 0 || Active && !EditMode && Curve != null;

    public void DrawGizmo()
    {
        if(!OwnsGizmo) return;
        if(Curve is { } curve && !EditMode) { ObjectGizmo(curve); return; }
        if(Selection.Count == 0) return;
        if(EditingNodePath) { NodePathGizmo(); return; }
        if(Curve is { } c) PointGizmo(c);
    }

    private void Started() { if(_gizmo.DragStarted) _tl.Snapshot(); }
    private void LimitNote() { if(_gizmo.IsUsing && _gizmo.Limited) _status(I18n.T("curve.gizmo_limited", $"{GizmoEditor.MaxRange:0}"), true); }

    public static Vector3 Pivot(CurveObject curve)
    {
        var points = curve.Splines.SelectMany(s => s.Points).ToList();
        if(points.Count == 0) return curve.Location;
        return curve.ToWorld(points.Aggregate(Vector3.Zero, (sum, p) => sum + p.Co) / points.Count);
    }

    private void ObjectGizmo(CurveObject curve)
    {
        var pivot = Pivot(curve);
        var local = curve.ToLocal(pivot);
        var p = pivot; var r = curve.Rotation; var s = curve.Scale;
        bool changed = _gizmo.DrawTransform(ref p, ref r, ref s, _gizmo.Operation);
        Started();
        if(changed)
        {
            curve.Rotation = r; curve.Scale = s;
            curve.Location = p - Vector3.Transform(local * s, r);
            curve.Touch(); _tl.Touch();
        }
        LimitNote();
    }

    private Vector3 PartWorld(CurveObject curve, CurveSelection s)
    {
        var p = curve.Splines[s.Spline].Points[s.Point];
        return curve.ToWorld(s.Part switch { CurvePart.Left => p.Left, CurvePart.Right => p.Right, _ => p.Co });
    }

    private Vector3 _pointScale = Vector3.One;

    private void PointGizmo(CurveObject curve)
    {
        Selection.RemoveWhere(s => !ValidPart(curve, s));
        if(Selection.Count == 0) return;
        var center = Selection.Aggregate(Vector3.Zero, (sum, s) => sum + PartWorld(curve, s)) / Selection.Count;
        var p = center; var r = Quaternion.Identity; var scale = Vector3.One;
        var op = _gizmo.Operation;
        bool changed = _gizmo.DrawTransform(ref p, ref r, ref scale, op);
        Started();
        if(_gizmo.DragStarted || !_gizmo.IsUsing) _pointScale = Vector3.One;
        if(changed && op == GizmoEditor.Op.Scale)
        {
            var total = scale;
            scale /= _pointScale;
            _pointScale = total;
        }
        if(changed)
        {
            Vector3 Map(Vector3 world) => op switch
            {
                GizmoEditor.Op.Rotate => center + Vector3.Transform(world - center, r),
                GizmoEditor.Op.Scale => center + (world - center) * scale,
                _ => world + (p - center),
            };
            foreach(var group in Selection.GroupBy(s => s.Spline))
            {
                var spline = curve.Splines[group.Key];
                var points = group.Where(s => s.Part == CurvePart.Point).Select(s => s.Point).ToHashSet();
                foreach(int i in points)
                {
                    var pt = spline.Points[i];
                    pt.Co = curve.ToLocal(Map(curve.ToWorld(pt.Co)));
                    pt.Left = curve.ToLocal(Map(curve.ToWorld(pt.Left)));
                    pt.Right = curve.ToLocal(Map(curve.ToWorld(pt.Right)));
                }
                foreach(var s in group.Where(s => s.Part != CurvePart.Point && !points.Contains(s.Point)))
                {
                    var pt = spline.Points[s.Point];
                    bool left = s.Part == CurvePart.Left;
                    CurveEdit.MoveHandle(spline, s.Point, left, curve.ToLocal(Map(curve.ToWorld(left ? pt.Left : pt.Right))));
                }
                spline.CalcHandles();
            }
            curve.Touch(); _tl.Touch();
        }
        LimitNote();
    }

    private void NodePathGizmo()
    {
        var nodes = PathNodes;
        Selection.RemoveWhere(s => s.Point >= nodes.Count);
        if(Selection.Count == 0) return;
        Vector3 World(CurveSelection s)
        {
            var node = nodes[s.Point];
            if(ShotOf(node) is not { } shot) return node.Pos;
            var co = shot.NodePose(node).Pos;
            if(s.Part == CurvePart.Point || shot.PathHandles(node) is not { } h) return co;
            return co + (s.Part == CurvePart.Left ? h.left : h.right);
        }
        var center = Selection.Aggregate(Vector3.Zero, (sum, s) => sum + World(s)) / Selection.Count;
        var p = center; var r = Quaternion.Identity; var scale = Vector3.One;
        bool changed = _gizmo.DrawTransform(ref p, ref r, ref scale, GizmoEditor.Op.Translate);
        Started();
        if(changed)
        {
            var delta = p - center;
            foreach(var s in Selection)
            {
                var node = nodes[s.Point];
                if(ShotOf(node) is not { } shot) continue;
                if(s.Part == CurvePart.Point)
                {
                    node.Pos = GizmoEditor.ClampRange(shot.NodePose(node).Pos + delta);
                    _writeNodePosition(node);
                }
                else if(!Selection.Contains(s with { Part = CurvePart.Point }) && shot.PathHandles(node) is { } h)
                {
                    bool left = s.Part == CurvePart.Left;
                    shot.SetPathHandle(node, left, (left ? h.left : h.right) + delta);
                    _tl.Touch();
                }
            }
        }
        LimitNote();
    }

    // ---------------------------------------------------------------- commands

    private Vector3 AddLocation()
    {
        if(GizmoEditor.TryGetCameraMatrices(out var view, out _) && Matrix4x4.Invert(view, out var world))
        {
            var eye = new Vector3(world.M41, world.M42, world.M43);
            var forward = -Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));
            if(XcpMath.IsFinite(eye) && XcpMath.IsFinite(forward)) return GizmoEditor.ClampRange(eye + forward * 3);
        }
        return NodeRange.Center ?? Vector3.Zero;
    }

    public void Add(CurvePrimitive kind)
    {
        _tl.Snapshot();
        var curve = CurveEdit.Create(kind, AddLocation());
        int n = 1; string baseName = curve.Name;
        while(_tl.Curves.Any(c => c.Name == curve.Name)) curve.Name = $"{baseName}.{n++:000}";
        _tl.Curves.Add(curve);
        CurveId = curve.Id; NodePath = false; EditMode = true; Selection.Clear(); Active = true;
        _tl.Touch();
        _status(I18n.T("curve.status_added", curve.Name), false);
    }

    private List<(Spline spline, int index, HashSet<int> points)> ActivePointGroups()
        => EditMode && !NodePath && Curve is { } curve ? SelectedSplines(curve, pointsOnly: true).ToList() : [];

    public bool CanExtrude => ActivePointGroups().Any(g => !g.spline.Cyclic && (g.points.Contains(0) || g.points.Contains(g.spline.Points.Count - 1)));
    public bool CanSubdivide => ActivePointGroups().Any(g => g.spline.Points.Count > 1 && g.points.Any(i => i < g.spline.SegmentCount && g.points.Contains((i + 1) % g.spline.Points.Count)));
    public bool CanToggleCyclic => ActivePointGroups().Any(g => g.spline.Points.Count >= 2);
    public bool CanSwitchDirection => ActivePointGroups().Sum(g => g.points.Count) > 0;

    public void ExtrudeSelected()
    {
        var groups = ActivePointGroups();
        if(!groups.Any(g => !g.spline.Cyclic && (g.points.Contains(0) || g.points.Contains(g.spline.Points.Count - 1)))) return;
        Edit(I18n.T("curve.status_extruded"), _ =>
        {
            Selection.Clear();
            foreach(var (s, si, points) in groups.Where(g => !g.spline.Cyclic))
            {
                var ends = points.Where(p => p == 0 || p == s.Points.Count - 1).OrderDescending().ToArray();
                var added = new List<CurvePoint>();
                foreach(int end in ends) added.Add(s.Points[CurveEdit.Extrude(s, end)]);
                foreach(var point in added) Selection.Add(new CurveSelection(si, s.Points.IndexOf(point)));
            }
        });
    }

    public void SubdivideSelected()
    {
        var groups = ActivePointGroups();
        if(!groups.Any(g => g.spline.Points.Count > 1 && g.points.Any(i => i < g.spline.SegmentCount && g.points.Contains((i + 1) % g.spline.Points.Count)))) return;
        Edit(I18n.T("curve.status_subdivided"), _ =>
        {
            Selection.Clear();
            foreach(var (s, si, points) in groups)
                foreach(int i in CurveEdit.Subdivide(s, points)) Selection.Add(new CurveSelection(si, i));
        });
    }

    public void ToggleCyclicSelected()
    {
        var groups = ActivePointGroups();
        if(!groups.Any(g => g.spline.Points.Count >= 2)) return;
        Edit(I18n.T("curve.status_cyclic"), _ => { foreach(var g in groups) CurveEdit.ToggleCyclic(g.spline); });
    }

    public void SwitchDirectionSelected()
    {
        var groups = ActivePointGroups();
        if(groups.Sum(g => g.points.Count) == 0) return;
        Edit(I18n.T("curve.status_direction"), _ => { foreach(var g in groups) CurveEdit.SwitchDirection(g.spline); Selection.Clear(); });
    }

    /// <summary>Deletes the selected curve control points (if any). Returns true when handled.</summary>
    public bool DeleteSelected()
    {
        if(!EditMode || NodePath || Curve is not { } curve || Selection.Count == 0) return false;
        var groups = SelectedSplines(curve, pointsOnly: true).OrderByDescending(g => g.index).ToList();
        if(groups.Count == 0) return false;
        bool removesAll = curve.Splines.Select((s, i) => s.Points.Count - (groups.FirstOrDefault(g => g.index == i).points?.Count ?? 0)).All(n => n <= 0);
        if(removesAll)
        {
            _tl.RemoveCurve(curve);
            CurveId = null;
            _status(I18n.T("curve.status_empty_deleted"), false);
        }
        else
        {
            _tl.Snapshot();
            foreach(var (spline, index, points) in groups)
                if(!CurveEdit.Delete(spline, points)) curve.Splines.RemoveAt(index);
            curve.Touch(); _tl.Touch();
        }
        Selection.Clear();
        ClearPickCache();
        return true;
    }

    private void ClearPickCache()
    {
        Candidates.Clear(); FollowCandidates.Clear();
        Hover = null; HoverFollow = null;
    }

    private static bool ValidPart(CurveObject curve, CurveSelection s) =>
        s.Spline >= 0 && s.Spline < curve.Splines.Count && s.Point >= 0 && s.Point < curve.Splines[s.Spline].Points.Count
        && (s.Part == CurvePart.Point || (s.Part is CurvePart.Left or CurvePart.Right) && curve.Splines[s.Spline].Type == SplineType.Bezier);

    private IEnumerable<(Spline spline, int index, HashSet<int> points)> SelectedSplines(CurveObject curve, bool pointsOnly = false) =>
        Selection.Where(s => (!pointsOnly || s.Part == CurvePart.Point) && ValidPart(curve, s)).GroupBy(s => s.Spline)
            .Select(g => (curve.Splines[g.Key], g.Key, g.Select(s => s.Point).ToHashSet()));

    private void Edit(string what, Action<CurveObject> action)
    {
        if(Curve is not { } curve) return;
        _tl.Snapshot();
        action(curve);
        curve.Touch(); _tl.Touch();
        _status(what, false);
    }

    // ---------------------------------------------------------------- panel

    private void DrawAddCurvePopup(string popupId)
    {
        if(!ImGui.BeginPopup(popupId)) return;
        if(ImGui.MenuItem(I18n.T("curve.prim.bezier"))) Add(CurvePrimitive.Bezier);
        if(ImGui.MenuItem(I18n.T("curve.prim.circle"))) Add(CurvePrimitive.Circle);
        if(ImGui.MenuItem(I18n.T("curve.prim.nurbs"))) Add(CurvePrimitive.Path);
        ImGui.EndPopup();
    }

    /// <summary>Compact path list card rendered inside the right N-Panel.</summary>
    public void DrawSidebarSummary(bool gpose, bool brioTheme = true, bool windowOpen = false, Action? onToggleWindow = null)
    {
        float s = XcpStyle.Scale;
        string title = _tl.Curves.Count > 0 ? I18n.T("curve.list_title_count", _tl.Curves.Count) : I18n.T("curve.list_title");
        if(UiWidgets.SectionHeaderAction(title, FontAwesomeIcon.ExternalLinkAlt, "goto_path",
            windowOpen ? I18n.T("curve.win_close_tip") : I18n.T("curve.win_open_tip"), windowOpen, brioTheme, FontAwesomeIcon.Route))
            onToggleWindow?.Invoke();

        // Compact quick controls: wide Add Curve button + eye icon toggle for Path Gizmo
        float h = ImGui.GetFrameHeight();
        float eyeW = MathF.Max(h, 28f * s);
        float addW = MathF.Max(100f * s, UiWidgets.InnerAvailWidth - eyeW - 5f * s);
        ImGui.BeginDisabled(!gpose);
        if(UiWidgets.IconTextButton("np_add_curve", FontAwesomeIcon.Plus, I18n.T("curve.btn_add"),
            I18n.T("curve.btn_add_tip"), size: new Vector2(addW, h), brioTheme: brioTheme, dropdownCaret: true))
            ImGui.OpenPopup("##np_add_curve_popup");
        ImGui.EndDisabled();
        DrawAddCurvePopup("##np_add_curve_popup");

        ImGui.SameLine(0, 5f * s);
        if(UiWidgets.IconButton("np_path_axis", Active ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
            Active ? I18n.T("curve.axis_on_tip") : I18n.T("curve.axis_off_tip"),
            new Vector2(eyeW, h), active: Active, brioTheme: brioTheme))
        {
            Active = !Active;
            Selection.Clear();
        }

        ImGui.Spacing();

        void DrawEntries(float itemW)
        {
            bool following = _tl.ActiveTrack.FollowPath != null;
            string nodePathLabel = following
                ? I18n.T("curve.node_path_overridden", _tl.ActiveTrack.Name)
                : I18n.T("curve.node_path_item", _tl.ActiveTrack.Name);
            if(ImGui.Selectable($"{nodePathLabel}##np_nodepath", EditingNodePath,
                ImGuiSelectableFlags.None, new Vector2(itemW, 0)))
            { CurveId = null; NodePath = true; EditMode = true; Selection.Clear(); }
            UiWidgets.Tooltip(I18n.T("curve.node_path_tip"));

            foreach(var curve in _tl.Curves)
            {
                var users = _tl.Tracks.Where(t => t.FollowPath?.CurveId == curve.Id).Select(t => t.Name).ToList();
                string summary = SplineSummary(curve);
                string suffix = users.Count > 0 ? $" · {I18n.T("curve.following_tag")}" : $" · {summary}";
                if(ImGui.Selectable($"{curve.Name}{suffix}##np_{curve.Id}", curve.Id == CurveId,
                    ImGuiSelectableFlags.None, new Vector2(itemW, 0)))
                { CurveId = curve.Id; NodePath = false; Selection.Clear(); }
                UiWidgets.Tooltip($"{curve.Name} ({summary})" + (users.Count > 0 ? "\n" + I18n.T("curve.followed_by_tip", string.Join(I18n.T("common.list_sep"), users)) : ""));
            }
        }

        if(_tl.Curves.Count > 4)
        {
            if(ImGui.BeginChild("##np_curve_scroll", new Vector2(UiWidgets.InnerAvailWidth, 118f * s), false))
                DrawEntries(0f);
            ImGui.EndChild();
        }
        else
            DrawEntries(UiWidgets.InnerAvailWidth);
    }

    public void DrawPanel(bool gpose, bool brioTheme = true)
    {
        float s = XcpStyle.Scale;

        // ---- top bar ----
        ImGui.BeginDisabled(!gpose);
        if(UiWidgets.IconTextButton("add_curve_btn", FontAwesomeIcon.Plus, I18n.T("curve.btn_add"),
            I18n.T("curve.btn_add_tip"), brioTheme: brioTheme, dropdownCaret: true))
            ImGui.OpenPopup("##add_curve_popup");
        ImGui.EndDisabled();
        DrawAddCurvePopup("##add_curve_popup");

        ImGui.SameLine();
        if(UiWidgets.TogglePill("edit_cage", I18n.T("curve.axis_toggle"), Active,
            I18n.T("curve.axis_panel_tip"),
            FontAwesomeIcon.DrawPolygon, brioTheme: brioTheme))
        {
            Active = !Active;
            Selection.Clear();
        }

        ImGui.SameLine();
        UiWidgets.HelpMarker(I18n.T("curve.panel_help"));

        ImGui.Spacing();

        // ---- split: curve list (left) + properties (right) ----
        float availH = MathF.Max(180f * s, ImGui.GetContentRegionAvail().Y);
        float listW = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.34f, 170f * s, 260f * s);

        if(ImGui.BeginChild("##curve_list_pane", new Vector2(listW, availH), true, ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            UiWidgets.SectionHeader(I18n.T("curve.list_title"), brioTheme, FontAwesomeIcon.Route);
            bool following = _tl.ActiveTrack.FollowPath != null;
            string npLabel = following
                ? I18n.T("curve.node_path_overridden", _tl.ActiveTrack.Name)
                : I18n.T("curve.node_path_item", _tl.ActiveTrack.Name);
            if(ImGui.Selectable($"{npLabel}##nodepath", EditingNodePath))
            { CurveId = null; NodePath = true; EditMode = true; Selection.Clear(); }
            UiWidgets.Tooltip(I18n.T("curve.node_path_tip"));

            if(_tl.Curves.Count > 0) ImGui.Separator();
            foreach(var curve in _tl.Curves)
            {
                var users = _tl.Tracks.Where(t => t.FollowPath?.CurveId == curve.Id).Select(t => t.Name).ToList();
                string summary = SplineSummary(curve);
                string tagText = users.Count > 0 ? I18n.T("curve.following_tag") : summary.Split(' ')[0];
                if(ImGui.Selectable($"{curve.Name}##{curve.Id}", curve.Id == CurveId))
                { CurveId = curve.Id; NodePath = false; Selection.Clear(); }
                UiWidgets.Tooltip($"{summary}" + (users.Count > 0 ? "\n" + I18n.T("curve.followed_by_tip", string.Join(I18n.T("common.list_sep"), users)) : ""));
                float tagW = ImGui.CalcTextSize(tagText).X + 8f * s;
                ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX() + 6f * s, ImGui.GetContentRegionAvail().X - tagW));
                ImGui.TextDisabled(tagText);
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if(ImGui.BeginChild("##curve_prop_pane", new Vector2(0, availH), false))
        {
            if(EditingNodePath) DrawNodePathPanel(brioTheme);
            else if(Curve is not { } c) ImGui.TextDisabled(I18n.T("curve.select_hint"));
            else DrawCurveProperties(c, brioTheme);
        }
        ImGui.EndChild();
    }

    private void DrawCurveProperties(CurveObject c, bool brioTheme)
    {
        float s = XcpStyle.Scale;

        // ---- card 1: identity & mode ----
        using(UiWidgets.Card("curve_hdr", brioTheme))
        {
            string name = c.Name;
            string delLabel = I18n.T("common.delete");
            float delW = MathF.Max(72f * s, ImGui.CalcTextSize(delLabel).X + 30f * s);
            ImGui.SetNextItemWidth(MathF.Max(100f * s, UiWidgets.InnerAvailWidth - delW - 6f * s));
            if(ImGui.InputText("##cname", ref name, 64)) c.Name = name;
            ImGui.SameLine(0, 6f * s);
            if(UiWidgets.DangerButton("del_curve", FontAwesomeIcon.TrashAlt, delLabel, I18n.T("curve.del_tip"), new Vector2(delW, 0), brioTheme))
            { _tl.RemoveCurve(c); CurveId = null; Selection.Clear(); return; }

            ImGui.Spacing();
            int mode = EditMode ? 1 : 0;
            if(UiWidgets.Segmented("curve_mode", ref mode, [I18n.T("curve.mode.object"), I18n.T("curve.mode.edit")], brioTheme, UiWidgets.InnerAvailWidth,
                [I18n.T("curve.mode.object_tip"), I18n.T("curve.mode.edit_tip")]))
            {
                EditMode = mode == 1;
                if(!EditMode) Selection.Clear();
            }
        }

        ImGui.Spacing();

        // ---- card 2: object transform & spline ----
        using(UiWidgets.Card("curve_xform", brioTheme))
        {
            UiWidgets.SectionHeader(I18n.T("curve.xform_title"), brioTheme, FontAwesomeIcon.Cube);
            string posLbl = I18n.T("inspector.pos_m"), rotLbl = I18n.T("inspector.rot_deg"), scLbl = I18n.T("common.scale");
            float maxLblW = MathF.Max(MathF.Max(ImGui.CalcTextSize(posLbl).X, ImGui.CalcTextSize(rotLbl).X), ImGui.CalcTextSize(scLbl).X);
            float fieldW = MathF.Max(120f * s, UiWidgets.InnerAvailWidth - maxLblW - 14f * s);

            var loc = c.Location;
            ImGui.SetNextItemWidth(fieldW);
            if(ImGui.DragFloat3($"{posLbl}##cpos", ref loc, .01f)) { c.Location = GizmoEditor.ClampRange(loc); c.Touch(); _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();

            var euler = ShotCurves.Euler(c.Rotation);
            ImGui.SetNextItemWidth(fieldW);
            if(ImGui.DragFloat3($"{rotLbl}##crot", ref euler, .25f))
            {
                const float rad = MathF.PI / 180;
                c.Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(euler.X * rad, euler.Y * rad, euler.Z * rad));
                c.Touch(); _tl.Touch();
            }
            if(ImGui.IsItemActivated()) _tl.Snapshot();

            var scale = c.Scale;
            ImGui.SetNextItemWidth(fieldW);
            if(ImGui.DragFloat3($"{scLbl}##csc", ref scale, .01f, .001f, 1000f)) { c.Scale = scale; c.Touch(); _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();

            var spline = c.Splines.Count > 0 ? c.Splines[Math.Clamp(Selection.FirstOrDefault().Spline, 0, c.Splines.Count - 1)] : null;
            if(spline != null)
            {
                ImGui.Spacing();
                UiWidgets.SectionHeader(I18n.T("curve.spline_header", TypeName(spline.Type), spline.Points.Count), brioTheme, FontAwesomeIcon.BezierCurve);
                int res = spline.Resolution;
                ImGui.SetNextItemWidth(90f * s);
                if(ImGui.InputInt($"{I18n.T("curve.resolution")}##cres", ref res)) Edit(I18n.T("curve.status_resolution"), _ => spline.Resolution = Math.Clamp(res, 1, 64));
                if(spline.Type == SplineType.Nurbs)
                {
                    ImGui.SameLine();
                    int order = spline.Order;
                    ImGui.SetNextItemWidth(80f * s);
                    if(ImGui.InputInt($"{I18n.T("curve.order")}##cord", ref order)) Edit(I18n.T("curve.status_order"), _ => spline.Order = Math.Clamp(order, 2, 6));
                    ImGui.SameLine();
                    bool endpoint = spline.Endpoint;
                    if(ImGui.Checkbox($"{I18n.T("curve.endpoint")}##cend", ref endpoint)) Edit(I18n.T("curve.status_endpoint"), _ => spline.Endpoint = endpoint);
                }
            }
        }

        if(!EditMode) return;
        ImGui.Spacing();

        // ---- card 3: edit mode actions ----
        using(UiWidgets.Card("curve_edit", brioTheme))
        {
            UiWidgets.SectionHeader(I18n.T("curve.edit_header", Selection.Count), brioTheme, FontAwesomeIcon.Pen);
            ImGui.BeginDisabled(Selection.Count == 0);
            DrawPointCommands(false, brioTheme);
            ImGui.Spacing();
            DrawHandleTypeControl(brioTheme);
            var selectedPoints = SelectedSplines(c).SelectMany(x => x.points.Where(i => i < x.spline.Points.Count).Select(i => x.spline.Points[i])).Distinct().ToList();
            if(selectedPoints.Count > 0)
            {
                ImGui.Spacing();
                DrawPointProperties(c, selectedPoints);
            }
            ImGui.EndDisabled();
        }
    }

    public void DrawPointContextCommands() => DrawPointCommands(true, true);

    private void DrawPointCommands(bool menu, bool brioTheme)
    {
        if(!EditMode || NodePath || Curve is not { } curve) return;
        var groups = ActivePointGroups();
        int count = groups.Sum(g => g.points.Count);
        if(menu && count == 0) return;
        if(menu) ImGui.Separator();

        bool Item(string id, FontAwesomeIcon icon, string label, bool enabled, string help)
        {
            ImGui.BeginDisabled(!enabled);
            bool clicked = menu
                ? ImGui.MenuItem(label)
                : UiWidgets.IconTextButton(id, icon, label, help, brioTheme: brioTheme);
            ImGui.EndDisabled();
            if(menu) UiWidgets.Tooltip(help);
            return clicked;
        }

        if(Item("c_ext", FontAwesomeIcon.PlusCircle, I18n.T("curve.cmd.extrude"), CanExtrude, I18n.T("curve.cmd.extrude_tip")))
            ExtrudeSelected();
        if(!menu) ImGui.SameLine();
        if(Item("c_sub", FontAwesomeIcon.Stream, I18n.T("curve.cmd.subdivide"), CanSubdivide, I18n.T("curve.cmd.subdivide_tip")))
            SubdivideSelected();
        if(!menu) ImGui.SameLine();
        if(Item("c_cyc", FontAwesomeIcon.Sync, I18n.T("curve.cmd.toggle_cyclic"), CanToggleCyclic, I18n.T("curve.cmd.toggle_cyclic_tip")))
            ToggleCyclicSelected();
        if(!menu) ImGui.SameLine();
        if(Item("c_dir", FontAwesomeIcon.ExchangeAlt, I18n.T("curve.cmd.switch_dir"), CanSwitchDirection, I18n.T("curve.cmd.switch_dir_tip")))
            SwitchDirectionSelected();
        if(!menu) ImGui.SameLine();
        if(Item("c_del", FontAwesomeIcon.TrashAlt, count > 1 ? I18n.T("inspector.delete_count", count) : I18n.T("common.delete"), count > 0, I18n.T("curve.cmd.delete_tip")))
            DeleteSelected();
    }

    private void DrawNodePathPanel(bool brioTheme)
    {
        using var card = UiWidgets.Card("node_path_card", brioTheme);
        UiWidgets.SectionHeader(I18n.T("curve.node_path_title", _tl.ActiveTrack.Name), brioTheme, FontAwesomeIcon.BezierCurve,
            I18n.T("curve.node_path_help"));

        if(_tl.ActiveTrack.FollowPath != null)
        {
            ImGui.TextColored(XcpStyle.Warning, I18n.T("curve.node_path_overridden_warn"));
            return;
        }

        var nodes = PathNodes;
        var points = Selection.Select(s => s.Point).Where(i => i < nodes.Count)
            .Concat(nodes.Select((n, idx) => (n, idx)).Where(x => _tl.SelectedNodes.Contains(x.n.Id)).Select(x => x.idx))
            .Distinct().ToList();
        ImGui.TextDisabled(points.Count == 0 ? I18n.T("curve.node_path_empty_hint") : I18n.T("curve.node_path_selected_count", points.Count));
        ImGui.Spacing();
        ImGui.BeginDisabled(points.Count == 0);
        ImGui.SetNextItemWidth(115f * XcpStyle.Scale);
        ImGui.Combo("##nodehtype", ref _handleType, I18n.Combo("curve.handle.free", "curve.handle.auto", "curve.handle.vector", "curve.handle.aligned"));
        ImGui.SameLine();
        if(ImGui.Button($"{I18n.T("curve.node_path_apply_handle")}##np_apply_h"))
        {
            _tl.Snapshot();
            foreach(int i in points) ShotOf(nodes[i])?.SetPathHandleType(nodes[i], (CurveHandle)_handleType);
            _tl.Touch();
            _status(I18n.T("curve.node_path_handle_set", points.Count), false);
        }
        ImGui.EndDisabled();
    }

    private static string TypeName(SplineType t) => t switch { SplineType.Bezier => I18n.T("curve.type.bezier"), SplineType.Nurbs => I18n.T("curve.type.nurbs"), _ => I18n.T("curve.type.poly") };
    private static string SplineSummary(CurveObject c) => c.Splines.Count == 0 ? I18n.T("curve.summary.empty") :
        string.Join(" + ", c.Splines.Select(s => I18n.T(s.Cyclic ? "curve.summary.part_cyclic" : "curve.summary.part", TypeName(s.Type), s.Points.Count)));
}
