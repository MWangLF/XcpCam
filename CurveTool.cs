using Dalamud.Bindings.ImGui;
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

    /// <summary>Shows the selected curve's full edit cage in game (handles, object mode, node path handles).
    /// Picking and the gizmo no longer depend on it: they follow whatever was selected last.</summary>
    public bool Active;
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
    private int _handleType = 1, _addKind;

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
        else if(EditingNodePath) DrawNodePathCage();
    }

    /// <summary>
    /// The active track's followed curve: its control points are always drawn and pickable (camera-anchored:
    /// round, timeline-anchored: square), unless the same curve already shows its full edit cage.
    /// </summary>
    private void DrawFollowedPoints()
    {
        FollowCandidates.Clear();
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
        return Selection.Where(s => s.Part == CurvePart.Point && s.Spline < curve.Splines.Count && s.Point < curve.Splines[s.Spline].Points.Count)
            .Select(s => (spline: s.Spline, index: s.Point, point: curve.Splines[s.Spline].Points[s.Point]))
            .Distinct().OrderBy(x => x.spline).ThenBy(x => x.index).ToList();
    }

    /// <summary>Tilt / radius of the selected points (curve page and timeline inspector).</summary>
    public void DrawPointProperties(CurveObject c, List<CurvePoint> selectedPoints)
    {
        if(selectedPoints.Count == 0) return;
        float tilt = selectedPoints.Average(p => p.Tilt) * 180 / MathF.PI, radius = selectedPoints.Average(p => p.Radius);
        ImGui.SetNextItemWidth(120);
        if(ImGui.DragFloat("倾斜 (°)", ref tilt, .5f)) { foreach(var p in selectedPoints) p.Tilt = tilt * MathF.PI / 180; c.Touch(); _tl.Touch(); }
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Blender Ctrl+T：沿曲线的滚转，只在约束开启「跟随曲线」时影响相机。");
        ImGui.SameLine(); ImGui.SetNextItemWidth(120);
        if(ImGui.DragFloat("半径", ref radius, .01f, 0f, 100f)) { foreach(var p in selectedPoints) p.Radius = MathF.Max(0, radius); c.Touch(); _tl.Touch(); }
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Blender Alt+S：约束开启「使用半径」时按半径缩放位置偏移。");
    }

    /// <summary>Handle type of the selected points (curve page and timeline inspector).</summary>
    public void DrawHandleTypeControl()
    {
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("##htype", ref _handleType, "自由\0自动\0矢量\0对齐\0");
        ImGui.SameLine();
        if(ImGui.Button("设置手柄类型"))
            Edit("已设置手柄类型。", curve => { foreach(var (s, _, points) in SelectedSplines(curve)) CurveEdit.SetHandleType(s, points, (CurveHandle)_handleType); });
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Blender V：自动（黄）、矢量（绿）、对齐（粉）、自由（黑）。拖动自动手柄会变为对齐，拖动矢量手柄会变为自由。");
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
                    _overlay.DrawLine(co, curve.ToWorld(spline.Wrap(i + 1).Co), 0xFF808080, 1, true); // NURBS/poly control polygon
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
                if(part == CurvePart.Left && first || part == CurvePart.Right && last) continue; // end handles shape nothing
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
        if(s.Spline < 0) return; // object mode: the object marker only shows the gizmo
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

    /// <summary>Whether the gizmo acts on curves: selected curve points / handles, or a curve in object mode.</summary>
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
    private void LimitNote() { if(_gizmo.IsUsing && _gizmo.Limited) _status($"变换手柄已限制：需保持在目标周围 {GizmoEditor.MaxRange:0} 米内，单帧跳动过大的拖动会被忽略。", true); }

    /// <summary>World median of all control points: the pivot for object-mode transforms. The origin
    /// (<see cref="CurveObject.Location"/>) stays behind when points are moved in edit mode, so pivoting on it
    /// would rotate/scale such a curve around an off-centre point.</summary>
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
            // rotate / scale about the pivot: keep the pivot's local point at the gizmo position
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

    /// <summary>Total scale already applied to the selected points during the current gizmo drag.</summary>
    private Vector3 _pointScale = Vector3.One;

    private void PointGizmo(CurveObject curve)
    {
        Selection.RemoveWhere(s => s.Spline >= curve.Splines.Count || s.Point >= curve.Splines[s.Spline].Points.Count
            || s.Part != CurvePart.Point && curve.Splines[s.Spline].Type != SplineType.Bezier);
        if(Selection.Count == 0) return;
        var center = Selection.Aggregate(Vector3.Zero, (sum, s) => sum + PartWorld(curve, s)) / Selection.Count;
        var p = center; var r = Quaternion.Identity; var scale = Vector3.One;
        var op = _gizmo.Operation;
        bool changed = _gizmo.DrawTransform(ref p, ref r, ref scale, op);
        Started();
        // ImGuizmo reports scale relative to the drag start, but the points are already scaled by the
        // previous frames: apply only this frame's ratio so the scale follows the mouse
        if(_gizmo.DragStarted || !_gizmo.IsUsing) _pointScale = Vector3.One;
        if(changed && op == GizmoEditor.Op.Scale)
        {
            var total = scale;
            scale /= _pointScale;
            _pointScale = total;
        }
        if(changed)
        {
            // Blender transforms points about the selection median: G translates, R rotates, S scales
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
                    var pt = spline.Points[s.Point]; bool left = s.Part == CurvePart.Left;
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
                    // handles are offsets from the node, so they move with it
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

    // ---------------------------------------------------------------- operators

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
        _status($"已添加「{curve.Name}」（当前镜头前方 3 米）。在游戏画面中点选控制点或手柄编辑。", false);
    }

    /// <summary>Delete key / button in edit mode: removes the selected control points.</summary>
    public bool DeleteSelected()
    {
        if(!EditMode || Curve is not { } curve || Selection.Count == 0 || EditingNodePath) return false;
        _tl.Snapshot();
        foreach(var group in Selection.Where(s => s.Part == CurvePart.Point).GroupBy(s => s.Spline).OrderByDescending(g => g.Key))
            if(!CurveEdit.Delete(curve.Splines[group.Key], group.Select(s => s.Point).ToHashSet())) curve.Splines.RemoveAt(group.Key);
        Selection.Clear();
        if(curve.Splines.Count == 0) { _tl.RemoveCurve(curve); CurveId = null; _status("曲线已没有控制点，已删除。", false); }
        else { curve.Touch(); _tl.Touch(); }
        return true;
    }

    private IEnumerable<(Spline spline, int index, HashSet<int> points)> SelectedSplines(CurveObject curve) =>
        Selection.Where(s => s.Spline < curve.Splines.Count).GroupBy(s => s.Spline)
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

    public void DrawPanel(bool gpose)
    {
        ImGui.TextDisabled("曲线与镜头节点都是世界坐标。参照 Blender：物体模式整体移动/旋转/缩放，编辑模式点选控制点与贝塞尔手柄。");
        if(ImGui.Checkbox("在游戏内显示编辑框", ref Active)) Selection.Clear();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("开启后在游戏内显示所选曲线的控制点（方块）和手柄（圆点）、物体模式的整体手柄、节点路径的手柄。\n变换手柄作用于最后选中的对象：点选曲线节点就编辑曲线，点选镜头节点就编辑镜头，Ctrl+单击多选，单击空白处取消选择。\n被当前轨道跟随的曲线，其控制点始终可以点选。");
        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose);
        ImGui.SetNextItemWidth(130);
        ImGui.Combo("##addkind", ref _addKind, "贝塞尔曲线\0贝塞尔圆环\0路径（NURBS）\0");
        ImGui.SameLine();
        if(ImGui.Button("添加曲线")) Add((CurvePrimitive)_addKind);
        ImGui.EndDisabled();

        if(ImGui.BeginListBox("##curves", new Vector2(-1, 110)))
        {
            bool following = _tl.ActiveTrack.FollowPath != null;
            if(ImGui.Selectable($"「{_tl.ActiveTrack.Name}」节点路径（自动生成）{(following ? " · 已跟随曲线" : "")}", EditingNodePath))
            { CurveId = null; NodePath = true; EditMode = true; Selection.Clear(); }
            foreach(var curve in _tl.Curves)
            {
                var users = _tl.Tracks.Where(t => t.FollowPath?.CurveId == curve.Id).Select(t => t.Name).ToList();
                string label = $"{curve.Name}  ({SplineSummary(curve)}){(users.Count > 0 ? " · 被 " + string.Join("、", users) + " 跟随" : "")}##{curve.Id}";
                if(ImGui.Selectable(label, curve.Id == CurveId)) { CurveId = curve.Id; NodePath = false; Selection.Clear(); }
            }
            ImGui.EndListBox();
        }

        if(EditingNodePath) { DrawNodePathPanel(); return; }
        if(Curve is not { } c) { ImGui.TextDisabled("选择一条曲线进行编辑，或添加新曲线。"); return; }

        string name = c.Name;
        ImGui.SetNextItemWidth(200);
        if(ImGui.InputText("名称", ref name, 64)) c.Name = name;
        ImGui.SameLine();
        if(ImGui.Button("删除曲线")) { _tl.RemoveCurve(c); CurveId = null; Selection.Clear(); return; }
        int mode = EditMode ? 1 : 0;
        if(ImGui.RadioButton("物体模式", ref mode, 0)) { EditMode = false; Selection.Clear(); }
        ImGui.SameLine();
        if(ImGui.RadioButton("编辑模式", ref mode, 1)) EditMode = true;
        ImGui.SameLine();
        ImGui.TextDisabled(EditMode ? "| 游戏内：点选控制点/手柄后用变换手柄移动（移动/旋转/缩放围绕所选中心）" : "| 游戏内：变换手柄整体移动/旋转/缩放");

        // object transform (Blender N panel)
        var loc = c.Location;
        ImGui.SetNextItemWidth(260);
        if(ImGui.DragFloat3("位置 (米)", ref loc, .01f)) { c.Location = GizmoEditor.ClampRange(loc); c.Touch(); _tl.Touch(); }
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        var euler = ShotCurves.Euler(c.Rotation);
        ImGui.SetNextItemWidth(260);
        if(ImGui.DragFloat3("旋转 偏航/俯仰/滚转 (°)", ref euler, .25f))
        {
            const float rad = MathF.PI / 180;
            c.Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(euler.X * rad, euler.Y * rad, euler.Z * rad)); c.Touch(); _tl.Touch();
        }
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        var scale = c.Scale;
        ImGui.SetNextItemWidth(260);
        if(ImGui.DragFloat3("缩放", ref scale, .01f, .001f, 1000f)) { c.Scale = scale; c.Touch(); _tl.Touch(); }
        if(ImGui.IsItemActivated()) _tl.Snapshot();

        var spline = c.Splines.Count > 0 ? c.Splines[Math.Clamp(Selection.FirstOrDefault().Spline, 0, c.Splines.Count - 1)] : null;
        if(spline != null)
        {
            ImGui.TextDisabled($"样条：{TypeName(spline.Type)}，{spline.Points.Count} 个控制点{(c.Splines.Count > 1 ? $"（共 {c.Splines.Count} 条样条，跟随路径只使用第一条）" : "")}");
            int res = spline.Resolution;
            ImGui.SetNextItemWidth(100);
            if(ImGui.InputInt("分辨率", ref res)) Edit("已修改分辨率。", _ => spline.Resolution = Math.Clamp(res, 1, 64));
            if(spline.Type == SplineType.Nurbs)
            {
                ImGui.SameLine(); int order = spline.Order; ImGui.SetNextItemWidth(100);
                if(ImGui.InputInt("阶数", ref order)) Edit("已修改 NURBS 阶数。", _ => spline.Order = Math.Clamp(order, 2, 6));
                ImGui.SameLine(); bool endpoint = spline.Endpoint;
                if(ImGui.Checkbox("端点", ref endpoint)) Edit("已修改 NURBS 端点。", _ => spline.Endpoint = endpoint);
            }
        }

        if(!EditMode) return;
        ImGui.Separator();
        ImGui.TextUnformatted($"编辑模式 · 已选 {Selection.Count}");
        ImGui.BeginDisabled(Selection.Count == 0);
        if(ImGui.Button("挤出"))
            Edit("已挤出新控制点并选中，可直接拖动。", curve =>
            {
                var created = new List<CurveSelection>();
                foreach(var (s, si, points) in SelectedSplines(curve).ToList())
                    foreach(int end in points.Where(p => p == 0 || p == s.Points.Count - 1).OrderByDescending(p => p).ToList())
                        if(CurveEdit.Extrude(s, end) is int added and >= 0) created.Add(new CurveSelection(si, added));
                Selection.Clear(); Selection.UnionWith(created);
                if(created.Count == 0) _status("挤出只能用于未闭合样条的端点。", true);
            });
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Blender E：从所选端点延伸出新的控制点。");
        ImGui.SameLine();
        if(ImGui.Button("细分"))
            Edit("已细分所选相邻控制点之间的线段（保持形状）。", curve =>
            {
                var created = new List<CurveSelection>();
                foreach(var (s, si, points) in SelectedSplines(curve).ToList())
                    created.AddRange(CurveEdit.Subdivide(s, points).Select(i => new CurveSelection(si, i)));
                Selection.Clear(); Selection.UnionWith(created);
            });
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("在每对相邻的已选控制点之间插入一个点。");
        ImGui.SameLine();
        if(ImGui.Button("删除点")) DeleteSelected();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("删除所选控制点（游戏内按 Delete 也可）。");
        ImGui.SameLine();
        if(ImGui.Button("切换闭合")) Edit("已切换闭合。", curve => { foreach(var (s, _, _) in SelectedSplines(curve)) CurveEdit.ToggleCyclic(s); });
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Blender Alt+C：闭合或打开所选样条。");
        ImGui.SameLine();
        if(ImGui.Button("切换方向")) Edit("已反转样条方向。", curve => { foreach(var (s, _, _) in SelectedSplines(curve)) CurveEdit.SwitchDirection(s); Selection.Clear(); });
        DrawHandleTypeControl();

        var selectedPoints = SelectedSplines(c).SelectMany(x => x.points.Where(i => i < x.spline.Points.Count).Select(i => x.spline.Points[i])).Distinct().ToList();
        DrawPointProperties(c, selectedPoints);
        ImGui.EndDisabled();
    }

    private void DrawNodePathPanel()
    {
        if(_tl.ActiveTrack.FollowPath != null)
        { ImGui.TextDisabled("当前轨道正在跟随曲线：节点位置由曲线上的路径进度决定（首 0%、末 100%），节点路径不参与输出。移除约束后可再编辑节点路径。"); return; }
        ImGui.TextDisabled("节点路径是由镜头节点自动生成的贝塞尔曲线：控制点就是镜头节点，默认自动（Auto）手柄。");
        ImGui.TextDisabled("在游戏内点选节点处的手柄（圆点）拖动调整曲率；拖动自动手柄会变为对齐。点选控制点会选中镜头节点。");
        var nodes = PathNodes;
        var points = Selection.Select(s => s.Point).Where(i => i < nodes.Count).Distinct().ToList();
        ImGui.BeginDisabled(points.Count == 0);
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("##nodehtype", ref _handleType, "自由\0自动\0矢量\0对齐\0");
        ImGui.SameLine();
        if(ImGui.Button("设置所选节点手柄类型"))
        {
            _tl.Snapshot();
            foreach(int i in points) ShotOf(nodes[i])?.SetPathHandleType(nodes[i], (CurveHandle)_handleType);
            _tl.Touch();
            _status($"已设置 {points.Count} 个节点的路径手柄类型。", false);
        }
        ImGui.EndDisabled();
    }

    private static string TypeName(SplineType t) => t switch { SplineType.Bezier => "贝塞尔", SplineType.Nurbs => "NURBS", _ => "多段线" };
    private static string SplineSummary(CurveObject c) => c.Splines.Count == 0 ? "空" :
        string.Join(" + ", c.Splines.Select(s => $"{TypeName(s.Type)}{(s.Cyclic ? "环" : "")} {s.Points.Count} 点"));
}
