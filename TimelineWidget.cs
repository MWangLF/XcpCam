using Dalamud.Bindings.ImGui;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// Frame-accurate multi-track camera timeline: ruler, an "output" row showing which track is on screen and where
/// the cuts are, one lane per track with draggable camera nodes (nodes stay in their own lane), hold bars,
/// speed tint, scrubbable playhead, mouse-wheel zoom (Ctrl+wheel) and horizontal scroll (wheel / middle-drag).
/// Track headers (name, up/down, mute, solo, delete) are drawn to the left of the lanes.
/// </summary>
public sealed class TimelineWidget
{
    public float PixelsPerFrame = 6f;
    public float ScrollFrame;

    public Keyframe? Selected;
    public bool PlayheadChanged { get; private set; }
    public bool KeysChanged { get; private set; }
    /// <summary>Set once when a drag of a node begins (for undo snapshot).</summary>
    public bool DragStarted { get; private set; }
    /// <summary>Set when a track header button changed the track list / flags (for undo snapshot + rebuild).</summary>
    public bool TracksEdited { get; private set; }
    /// <summary>Snapshot request before a track header edit.</summary>
    public Action? BeforeTrackEdit;
    /// <summary>Curve selection shared with the game view (followed curve points are shown in their track's lane).</summary>
    public CurveTool? Curves;
    /// <summary>Set when a curve point was clicked (its selection replaced the camera node selection).</summary>
    public bool CurvePointsClicked { get; private set; }
    /// <summary>Set when a camera node was clicked (the curve point selection was cleared).</summary>
    public bool NodesClicked { get; private set; }

    private (CamTrack track, Guid id, double start, bool loopEnd)? _dragPoint;
    private bool _pointDragged;
    private ShotComposer? _timesFor;
    private readonly Dictionary<CamTrack, List<CurvePointTime>> _times = new();

    /// <summary>Followed curve points of a track on the timeline (cached per composer: any edit rebuilds it).</summary>
    private List<CurvePointTime> CurveTimes(CamTrack track, ShotComposer? comp)
    {
        if(comp == null || track.FollowPath == null || track.FollowCurve == null) return [];
        if(!ReferenceEquals(_timesFor, comp)) { _times.Clear(); _timesFor = comp; }
        if(!_times.TryGetValue(track, out var list))
            _times[track] = list = track.Shots.FirstOrDefault()?.CurvePointTimes() ?? [];
        return list;
    }

    private Keyframe? _dragKey;
    private bool _scrubbing;
    private Dictionary<Guid, int> _dragFrames = new();
    private float _dragStartFrame;
    private Vector2? _boxStart;
    private bool _boxAdd;

    private const float RulerH = 22f, OutputH = 14f, NodeR = 7f, HeaderW = 190f;
    private float LaneH = 30f, trackScroll;
    private bool _panning;
    private const float ViewHeight = 260f;

    public void Draw(CamTimeline tl, ShotComposer? comp)
    {
        PlayheadChanged = false; KeysChanged = false; DragStarted = false; TracksEdited = false;
        CurvePointsClicked = false; NodesClicked = false;
        if(Selected != null && tl.SelectedNodes.Count > 0 && !tl.SelectedNodes.Contains(Selected.Id))
            Selected = tl.AllKeys.FirstOrDefault(k => tl.SelectedNodes.Contains(k.Id));

        var start = ImGui.GetCursorScreenPos();
        float totalW = MathF.Max(300, ImGui.GetContentRegionAvail().X);
        float height = ViewHeight;
        float trackHeight = height - RulerH - OutputH - 4;
        trackScroll = Math.Clamp(trackScroll, 0, Math.Max(0, tl.Tracks.Count * LaneH - trackHeight));

        // ---- track headers (left column) ----
        // Header buttons can add / remove / reorder tracks, so everything below must use the track list as it is
        // *after* the headers, and a composer built for another track list must not be used for this frame.
        DrawHeaders(tl, start, height);
        int lanes = tl.Tracks.Count;
        if(TracksEdited || (comp != null && comp.TrackCount != lanes)) comp = null;

        // ---- timeline area ----
        var origin = new Vector2(start.X + HeaderW, start.Y);
        float width = totalW - HeaderW;
        ImGui.SetCursorScreenPos(origin);
        var dl = ImGui.GetWindowDrawList();
        var size = new Vector2(width, height);

        ImGui.InvisibleButton("##timeline", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = io.MousePos;

        // zoom / scroll
        float visibleFrames = width / PixelsPerFrame;
        if(hovered && io.MouseWheel != 0)
        {
            if(io.KeyCtrl && io.KeyAlt)
            {
                float at = (trackScroll + mouse.Y - origin.Y - RulerH - OutputH - 4) / LaneH;
                LaneH = Math.Clamp(LaneH * MathF.Pow(1.2f, io.MouseWheel), 26, 90);
                trackScroll = at * LaneH - (mouse.Y - origin.Y - RulerH - OutputH - 4);
            }
            else if(io.KeyAlt) trackScroll -= io.MouseWheel * LaneH * 2;
            else if(io.KeyCtrl)
            {
                float fAtMouse = ScrollFrame + (mouse.X - origin.X) / PixelsPerFrame;
                PixelsPerFrame = Math.Clamp(PixelsPerFrame * (io.MouseWheel > 0 ? 1.2f : 1f / 1.2f), 0.5f, 60f);
                ScrollFrame = fAtMouse - (mouse.X - origin.X) / PixelsPerFrame;
            }
            else
                ScrollFrame -= io.MouseWheel * visibleFrames * 0.1f;
        }
        if(ImGui.IsItemActive() && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || _panning && ImGui.IsMouseDragging(ImGuiMouseButton.Left)))
        { ScrollFrame -= io.MouseDelta.X / PixelsPerFrame; trackScroll -= io.MouseDelta.Y; }
        trackScroll = Math.Clamp(trackScroll, 0, Math.Max(0, tl.Tracks.Count * LaneH - trackHeight));
        visibleFrames = width / PixelsPerFrame;
        ScrollFrame = Math.Clamp(ScrollFrame, -5, MathF.Max(-5, tl.Length + 5 - visibleFrames));

        float X(float frame) => origin.X + (frame - ScrollFrame) * PixelsPerFrame;
        float FrameAt(float x) => ScrollFrame + (x - origin.X) / PixelsPerFrame;
        float LaneTop(int i) => origin.Y + RulerH + OutputH + 4 + i * LaneH - trackScroll;

        // background
        dl.PushClipRect(origin, origin + size, true);
        dl.AddRectFilled(origin, origin + size, 0xFF1E1E1E);
        dl.AddRectFilled(origin, origin + new Vector2(width, RulerH), 0xFF2A2A2A);
        for(int i = 0; i < lanes; i++)
        {
            if(LaneTop(i) + LaneH <= origin.Y + RulerH + OutputH + 4 || LaneTop(i) >= origin.Y + height) continue;
            uint bg = i == tl.ActiveTrackIndex ? 0xFF2C2C34 : (i % 2 == 0 ? 0xFF222222 : 0xFF1B1B1B);
            dl.AddRectFilled(new Vector2(origin.X, Math.Max(origin.Y + RulerH + OutputH + 4, LaneTop(i))), new Vector2(origin.X + width, Math.Min(origin.Y + height, LaneTop(i) + LaneH)), bg);
        }
        float xEnd = X(tl.Length);
        if(xEnd < origin.X + width) dl.AddRectFilled(new Vector2(xEnd, origin.Y), origin + size, 0x60000000);
        float x0 = X(0);
        if(x0 > origin.X) dl.AddRectFilled(origin, new Vector2(x0, origin.Y + height), 0x60000000);

        // ruler
        int major = PickStep(PixelsPerFrame, tl.Fps);
        int minor = Math.Max(1, major / 5);
        int first = (int)MathF.Floor(ScrollFrame / minor) * minor;
        for(int f = first; f <= ScrollFrame + visibleFrames; f += minor)
        {
            if(f < 0) continue;
            float x = X(f);
            bool isMajor = f % major == 0;
            dl.AddLine(new Vector2(x, origin.Y + RulerH - (isMajor ? 10 : 4)), new Vector2(x, origin.Y + RulerH), 0xFF888888);
            if(isMajor)
            {
                dl.AddLine(new Vector2(x, origin.Y + RulerH), new Vector2(x, origin.Y + height), 0x30FFFFFF);
                dl.AddText(new Vector2(x + 2, origin.Y + 1), 0xFFBBBBBB, f % tl.Fps == 0 ? $"{f}  {f / tl.Fps}s" : $"{f}");
            }
        }

        // output row: which track is on screen
        float outY = origin.Y + RulerH + 2;
        if(comp != null)
        {
            foreach(var run in comp.Runs)
            {
                uint c = tl.Tracks[Math.Clamp(run.Track, 0, lanes - 1)].Color;
                dl.AddRectFilled(new Vector2(X(run.Start), outY), new Vector2(X(run.End + 1), outY + OutputH - 2), c, 2);
            }
            foreach(int cf in comp.CutFrames)
            {
                float x = X(cf);
                dl.AddLine(new Vector2(x, outY - 2), new Vector2(x, origin.Y + height), 0xC0FFFFFF, 1.5f);
                dl.AddTriangleFilled(new Vector2(x - 4, outY - 2), new Vector2(x + 4, outY - 2), new Vector2(x, outY + 4), 0xFFFFFFFF);
            }
        }
        else
            dl.AddText(new Vector2(origin.X + 4, outY - 2), 0xFF777777, "输出：至少需要一个镜头节点");

        // lanes: segments, speed tint, holds, nodes
        Keyframe? hoveredKey = null;
        (int lane, CurvePointTime point)? hoveredPoint = null;
        dl.PushClipRect(new Vector2(origin.X, origin.Y + RulerH + OutputH + 4), origin + size, true);
        for(int ti = 0; ti < lanes; ti++)
        {
            if(LaneTop(ti) + LaneH <= origin.Y + RulerH + OutputH + 4 || LaneTop(ti) >= origin.Y + height) continue;
            var track = tl.Tracks[ti];
            var curveTimes = CurveTimes(track, comp);
            // a following track also shows its curve points: camera nodes move up, curve points go below
            float midY = curveTimes.Count > 0 ? LaneTop(ti) + LaneH * 0.36f : LaneTop(ti) + LaneH / 2;
            var seq = comp?.SequenceOf(ti);
            bool active = comp?.IsActive(ti) ?? !track.Muted;

            if(seq != null)
            {
                foreach(var seg in seq.Segments)
                {
                    float a = X(seg.Start), b = X(seg.End);
                    dl.AddRectFilled(new Vector2(a, midY - 3), new Vector2(MathF.Max(b, a + 2), midY + 3), active ? 0xFF4A4A4A : 0xFF303030, 3);
                    if(seg.Path != null && active)
                    {
                        float stepPx = 3f;
                        for(float x = MathF.Max(a, origin.X); x < MathF.Min(b, origin.X + width); x += stepPx)
                        {
                            float f = FrameAt(x);
                            if(seq.SpeedAt(f) is float v)
                            {
                                bool shown = comp!.TrackAt(f) == ti;
                                uint col = SpeedColor(v / MaxSpeed(comp));
                                if(!shown) col = (col & 0x00FFFFFF) | 0x50000000;
                                dl.AddRectFilled(new Vector2(x, midY - 3), new Vector2(x + stepPx, midY + 3), col, 0f);
                            }
                        }
                    }
                }
            }

            foreach(var k in track.Keys.OrderBy(k => k.Frame))
            {
                float x = X(k.Frame);
                if(k.HoldFrames > 0)
                    dl.AddRectFilled(new Vector2(x, midY - 6), new Vector2(X(k.Frame + k.HoldFrames), midY + 6), 0xA03399FF, 3);
                if(k.CutIn)
                {
                    dl.AddLine(new Vector2(x, LaneTop(ti) + 2), new Vector2(x, LaneTop(ti) + LaneH - 2), 0xFFFFFFFF, 1.5f);
                    dl.AddText(new Vector2(x - 14, LaneTop(ti) - 1), 0xFFFFFFFF, "✂");
                }
                bool over = hovered && mouse.Y >= origin.Y + RulerH + OutputH + 4 &&
                    Vector2.Distance(mouse, new Vector2(x, midY)) <= NodeR + 3;
                if(over) hoveredKey = k;
                bool sel = tl.SelectedNodes.Contains(k.Id) || k == Selected;
                uint fill = track.Color;
                if(!active) fill = (fill & 0x00FFFFFF) | 0x70000000;
                DrawDiamond(dl, new Vector2(x, midY), NodeR + (sel ? 2 : 0), sel ? 0xFF00FFFF : fill, over || sel ? 0xFFFFFFFF : 0xFF000000);
                int idx = track.Keys.IndexOf(k) + 1;
                if(curveTimes.Count > 0) dl.AddText(new Vector2(x + NodeR + 1, midY - 7), 0xFFDDDDDD, $"{idx}");
                else dl.AddText(new Vector2(x - 4, midY + NodeR - 1), 0xFFDDDDDD, $"{idx}");
            }

            if(curveTimes.Count > 0 && track.FollowCurve is { PathSpline: { } spline } curve)
            {
                // followed curve points: round = anchored to the camera, square = anchored to the timeline
                float py = LaneTop(ti) + LaneH - 7;
                int si = curve.Splines.IndexOf(spline);
                bool context = Curves != null && Curves.CurveId == curve.Id && Curves.EditMode;
                foreach(var pt in curveTimes)
                {
                    if(pt.Frame is not double f) continue;
                    float x = X((float)f);
                    bool sel = context && Curves!.Selection.Contains(new CurveSelection(si, pt.Index));
                    bool over = hovered && Vector2.Distance(mouse, new Vector2(x, py)) <= 6;
                    if(over) hoveredPoint = (ti, pt);
                    // grey ring: the camera passes the point again (e.g. a closed loop's start point at 100 %)
                    uint col = sel ? 0xFF00FFFF : pt.Repeat && !pt.Anchored ? 0xFF888888 : pt.Anchored ? 0xFFFFCC66 : 0xFFDDDDDD;
                    if(pt.Repeat && !pt.Anchored) dl.AddCircle(new Vector2(x, py), over || sel ? 5f : 4f, col, 12, 1.2f);
                    else if(pt.Anchored)
                    {
                        dl.AddRectFilled(new Vector2(x - 4, py - 4), new Vector2(x + 4, py + 4), col);
                        if(over || sel) dl.AddRect(new Vector2(x - 5, py - 5), new Vector2(x + 5, py + 5), 0xFFFFFFFF);
                    }
                    else dl.AddCircle(new Vector2(x, py), over || sel ? 5.5f : 4.5f, col, 12, 2f);
                    dl.AddText(new Vector2(x + 6, py - 8), 0xFF999999, $"P{pt.Index + 1}");
                }
            }
        }

        dl.PopClipRect();
        // playhead
        float px = X(tl.Playhead);
        dl.AddLine(new Vector2(px, origin.Y), new Vector2(px, origin.Y + height), 0xFF4444FF, 2);
        dl.AddTriangleFilled(new Vector2(px - 6, origin.Y), new Vector2(px + 6, origin.Y), new Vector2(px, origin.Y + 8), 0xFF4444FF);
        dl.PopClipRect();

        // ---- interaction ----
        if(hoveredPoint != null) hoveredKey = null; // the curve point strip has its own row
        if(ImGui.IsItemActivated() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if(hoveredPoint is { } hp && Curves != null)
            {
                bool additive = ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift;
                var track = tl.Tracks[hp.lane];
                Curves.SelectFollowedPoint(track, hp.point.Index, additive);
                Selected = null; tl.SelectNodes([]);
                tl.ActiveTrackIndex = hp.lane;
                CurvePointsClicked = true;
                // a camera pass repeat is not draggable; a closed loop's anchored return to the start point is
                _dragPoint = additive || hp.point.Repeat && !hp.point.Anchored || hp.point.Frame is not double f0
                    ? null : (track, hp.point.Id, f0, hp.point.Repeat);
                _pointDragged = false;
                _dragStartFrame = FrameAt(mouse.X);
            }
            else if(hoveredKey != null)
            {
                Curves?.ClearSelection();
                NodesClicked = true;
                bool additive = ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift;
                if(additive) tl.ToggleNode(hoveredKey.Id);
                else if(!tl.SelectedNodes.Contains(hoveredKey.Id)) tl.SelectNodes([hoveredKey.Id]);
                Selected = hoveredKey;
                var owner = tl.TrackOf(hoveredKey);
                if(owner != null) tl.ActiveTrackIndex = tl.Tracks.IndexOf(owner);
                _dragKey = additive ? null : hoveredKey;
                _dragStartFrame = FrameAt(mouse.X);
                _dragFrames = owner?.Keys.Where(k => tl.SelectedNodes.Contains(k.Id)).ToDictionary(k => k.Id, k => k.Frame) ?? new();
                if(_dragKey != null) { tl.Snapshot(); DragStarted = true; }
            }
            else
            {
                bool box = ImGui.GetIO().KeyCtrl;
                _boxStart = box ? mouse : null; _boxAdd = box;
                _scrubbing = !box && mouse.Y < origin.Y + RulerH;
                _panning = !box && !_scrubbing;
                float laneY = mouse.Y - (origin.Y + RulerH + OutputH + 4) + trackScroll;
                if(laneY >= 0)
                {
                    int lane = (int)(laneY / LaneH);
                    if(lane >= 0 && lane < lanes) tl.ActiveTrackIndex = lane;
                    if(!box) { Selected = null; tl.SelectNodes([]); Curves?.ClearSelection(); }
                }
            }
        }

        if(ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if(_dragPoint is { } dp)
            {
                // dragging a curve point anchors it to the timeline at the dropped frame
                int frame = (int)MathF.Round((float)dp.start + FrameAt(mouse.X) - _dragStartFrame);
                if(_pointDragged || frame != (int)Math.Round(dp.start))
                {
                    tl.SetCurvePointAnchor(dp.track, dp.id, frame, snapshot: !_pointDragged, loopEnd: dp.loopEnd);
                    _pointDragged = true;
                    KeysChanged = true;
                }
                ImGui.SetTooltip($"锚定时间轴：第 {Math.Clamp(frame, 0, tl.Length)} 帧  ({Math.Clamp(frame, 0, tl.Length) / (float)tl.Fps:0.00}s)");
            }
            else if(_dragKey != null)
            {
                int delta = (int)MathF.Round(FrameAt(mouse.X) - _dragStartFrame);
                var targets = _dragFrames.ToDictionary(p => p.Key, p => p.Value + delta);
                if(targets.Any(p => tl.AllKeys.Any(k => k.Id == p.Key && k.Frame != p.Value)) && tl.MoveNodes(targets)) KeysChanged = true;
                ImGui.SetTooltip($"第 {_dragKey.Frame} 帧  ({_dragKey.Frame / (float)tl.Fps:0.00}s)");
            }
            else if(_boxStart is Vector2 boxStart)
            {
                dl.AddRect(Vector2.Min(boxStart, mouse), Vector2.Max(boxStart, mouse), 0xFFFFFF55);
            }
            else if(_scrubbing)
            {
                tl.Playhead = Math.Clamp(MathF.Round(FrameAt(mouse.X)), 0, tl.Length);
                PlayheadChanged = true;
            }
        }
        else
        {
            if(_boxStart is Vector2 boxStart)
            {
                var a = Vector2.Min(boxStart, mouse); var b = Vector2.Max(boxStart, mouse);
                var ids = tl.Tracks.SelectMany((track, lane) => track.Keys.Where(k =>
                    X(k.Frame) >= a.X && X(k.Frame) <= b.X &&
                    LaneTop(lane) + LaneH * 0.5f >= a.Y && LaneTop(lane) + LaneH * 0.5f <= b.Y).Select(k => k.Id));
                tl.SelectNodes(ids, _boxAdd);
                Selected ??= tl.AllKeys.FirstOrDefault(k => tl.SelectedNodes.Contains(k.Id));
            }
            _boxStart = null;
            _panning = false;
            _dragKey = null;
            _dragPoint = null;
            _scrubbing = false;
        }

        if(hovered && hoveredPoint is { } hpt && _dragPoint == null && tl.Tracks[hpt.lane].FollowCurve is { } hc)
        {
            var p = hpt.point;
            ImGui.SetTooltip(p.Repeat && p.Anchored
                ? $"{hc.Name} · P{p.Index + 1}（闭合曲线回到起点，100%）：锚定时间轴  第 {p.Frame:0.###} 帧\n拖动改变绕完一圈回到起点的帧，双击跳转"
                : p.Repeat
                ? $"{hc.Name} · P{p.Index + 1}：镜头在第 {p.Frame:0.###} 帧再次经过它（闭合曲线的起点在 100% 处也是它）\n单击选中，双击跳转"
                : $"{hc.Name} · P{p.Index + 1}：{(p.Anchored ? "锚定时间轴" : "锚定镜头")}  第 {p.Frame:0.###} 帧  路径进度 {p.Factor * 100:0.0}%\n" +
                  (p.Anchored ? "拖动改变这个节点锚定的帧（镜头会在这一帧精确经过它）" : "镜头在这一帧经过它；拖动会把整条曲线改为锚定时间轴") +
                  "，双击跳转。\n锚定作用于整条曲线：Alt+T 锚定时间轴，Alt+C 锚定镜头");
            if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && p.Frame is double jump)
            {
                tl.Playhead = Math.Clamp((float)Math.Round(jump), 0, tl.Length);
                PlayheadChanged = true;
            }
        }
        else if(hovered && hoveredKey != null && _dragKey == null)
        {
            var k = hoveredKey;
            var owner = tl.TrackOf(k);
            ImGui.SetTooltip($"{owner?.Name} · 节点 {(owner?.Keys.IndexOf(k) ?? 0) + 1}：第 {k.Frame} 帧 ({k.Frame / (float)tl.Fps:0.00}s){(k.CutIn ? "\n✂ 从上一个节点硬切" : "")}\n拖动改变时间，双击跳转");
            if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                tl.Playhead = k.Frame;
                PlayheadChanged = true;
            }
        }
        else if(hovered && comp != null && mouse.Y >= outY && mouse.Y <= outY + OutputH)
        {
            int f = (int)MathF.Round(FrameAt(mouse.X));
            int t = comp.TrackAt(f);
            if(t >= 0 && t < lanes) ImGui.SetTooltip($"第 {f} 帧输出：{tl.Tracks[t].Name}");
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height + 2));
        ImGui.Dummy(new Vector2(1, 1));
    }

    private void DrawHeaders(CamTimeline tl, Vector2 start, float height)
    {
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(start, start + new Vector2(HeaderW - 4, height), 0xFF262626);

        ImGui.SetCursorScreenPos(start + new Vector2(4, 2));
        if(ImGui.SmallButton("+ 新镜头轨道"))
        {
            BeforeTrackEdit?.Invoke();
            tl.AddTrack(); trackScroll = 0;
            Selected = null;
            TracksEdited = true;
        }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("在最上面添加一条镜头轨道。越靠上的轨道越优先显示，\n两条轨道交接处就是一次硬切。");
        ImGui.SetCursorScreenPos(start + new Vector2(4, RulerH + 1));
        ImGui.TextDisabled("输出");

        int moveIdx = -1, moveDir = 0, removeIdx = -1;
        for(int i = 0; i < tl.Tracks.Count; i++)
        {
            var t = tl.Tracks[i];
            float y = start.Y + RulerH + OutputH + 4 + i * LaneH - trackScroll;
            if(y < start.Y + RulerH + OutputH + 4 || y + LaneH > start.Y + height) continue;
            ImGui.PushID($"trk{i}");
            dl.AddRectFilled(new Vector2(start.X + 2, y + 4), new Vector2(start.X + 6, y + LaneH - 4), t.Color);

            ImGui.SetCursorScreenPos(new Vector2(start.X + 10, y + 6));
            bool isActive = i == tl.ActiveTrackIndex;
            if(ImGui.Selectable($"{t.Name}##sel", isActive, ImGuiSelectableFlags.None, new Vector2(62, 0)))
                tl.ActiveTrackIndex = i;
            if(ImGui.IsItemHovered()) ImGui.SetTooltip(isActive ? "当前轨道：抓取的镜头会加到这里" : "点击设为当前轨道");

            ImGui.SameLine(0, 2);
            ImGui.BeginDisabled(i == 0);
            if(ImGui.ArrowButton("##up", ImGuiDir.Up)) { moveIdx = i; moveDir = -1; }
            ImGui.EndDisabled();
            ImGui.SameLine(0, 1);
            ImGui.BeginDisabled(i == tl.Tracks.Count - 1);
            if(ImGui.ArrowButton("##dn", ImGuiDir.Down)) { moveIdx = i; moveDir = 1; }
            ImGui.EndDisabled();
            ImGui.SameLine(0, 2);
            if(ToggleButton("M", t.Muted, "静音：这条轨道不参与输出")) { BeforeTrackEdit?.Invoke(); t.Muted = !t.Muted; TracksEdited = true; }
            ImGui.SameLine(0, 1);
            if(ToggleButton("S", t.Solo, "独奏：只输出独奏的轨道")) { BeforeTrackEdit?.Invoke(); t.Solo = !t.Solo; TracksEdited = true; }
            ImGui.SameLine(0, 1);
            ImGui.BeginDisabled(tl.Tracks.Count <= 1);
            if(ImGui.SmallButton("×")) removeIdx = i;
            ImGui.EndDisabled();
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tl.Tracks.Count <= 1 ? "至少保留一条轨道" : "删除这条轨道（可撤销）");
            ImGui.PopID();
        }

        if(moveIdx >= 0) { BeforeTrackEdit?.Invoke(); tl.MoveTrack(moveIdx, moveDir); TracksEdited = true; }
        if(removeIdx >= 0)
        {
            BeforeTrackEdit?.Invoke();
            if(Selected != null && tl.Tracks[removeIdx].Keys.Contains(Selected)) Selected = null;
            tl.RemoveTrack(removeIdx);
            TracksEdited = true;
        }
    }

    private static bool ToggleButton(string label, bool on, string tip)
    {
        if(on) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.55f, 0.1f, 1));
        bool clicked = ImGui.SmallButton(label);
        if(on) ImGui.PopStyleColor();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        return clicked;
    }

    // cached max speed per composer instance (for the speed tint)
    private ShotComposer? _speedFor;
    private float _maxSpeed = 1;
    private float MaxSpeed(ShotComposer comp)
    {
        if(ReferenceEquals(_speedFor, comp)) return _maxSpeed;
        _speedFor = comp;
        _maxSpeed = 1e-3f;
        for(int t = 0; t < comp.TrackCount; t++)
            if(comp.SequenceOf(t) is { } seq)
                for(float f = seq.Start; f < seq.End; f += MathF.Max(1, comp.Fps / 30f))
                    if(seq.SpeedAt(f) is float v) _maxSpeed = MathF.Max(_maxSpeed, v);
        return _maxSpeed;
    }

    public void EnsureVisible(float frame, float width)
    {
        float visible = (width - HeaderW) / PixelsPerFrame;
        if(frame < ScrollFrame) ScrollFrame = frame - visible * 0.1f;
        else if(frame > ScrollFrame + visible) ScrollFrame = frame - visible * 0.9f;
    }

    public void FitAll(CamTimeline tl, float width)
    {
        int len = Math.Max(10, tl.Length);
        PixelsPerFrame = Math.Clamp((width - HeaderW - 20) / len, 0.5f, 60f);
        ScrollFrame = -2;
    }

    private static int PickStep(float ppf, int fps)
    {
        int[] steps = [1, 2, 5, 10, Math.Max(1, fps / 2), fps, fps * 2, fps * 5, fps * 10, fps * 30, fps * 60];
        foreach(var s in steps.Distinct().OrderBy(s => s))
            if(s * ppf >= 60) return s;
        return fps * 60;
    }

    private static void DrawDiamond(ImDrawListPtr dl, Vector2 c, float r, uint fill, uint border)
    {
        var a = c + new Vector2(0, -r); var b = c + new Vector2(r, 0); var d = c + new Vector2(0, r); var e = c + new Vector2(-r, 0);
        dl.AddQuadFilled(a, b, d, e, fill);
        dl.AddQuad(a, b, d, e, border, 1.5f);
    }

    /// <summary>0..1 -> blue..green..red (ABGR).</summary>
    public static uint SpeedColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float r, g, b;
        if(t < 0.5f) { float u = t / 0.5f; r = 0.2f; g = 0.4f + 0.6f * u; b = 1f - 0.8f * u; }
        else { float u = (t - 0.5f) / 0.5f; r = 0.2f + 0.8f * u; g = 1f - 0.7f * u; b = 0.2f; }
        return 0xFF000000u | ((uint)(b * 255) << 16) | ((uint)(g * 255) << 8) | (uint)(r * 255);
    }
}
