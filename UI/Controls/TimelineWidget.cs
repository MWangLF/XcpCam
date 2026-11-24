using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// Frame-accurate multi-track camera timeline: ruler, an "output" row showing which track is on screen and where
/// the cuts are, one lane per track with draggable camera nodes (nodes stay in their own lane), hold bars,
/// speed tint, scrubbable playhead, mouse-wheel zoom and horizontal scroll.
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

    private float RulerH => 23f * XcpStyle.Scale;
    private float OutputH => 15f * XcpStyle.Scale;
    private float NodeR => 7f * XcpStyle.Scale;
    private float HeaderW => 196f * XcpStyle.Scale;
    private float LaneH = 34f, trackScroll;
    private bool _panning;

    public void Draw(CamTimeline tl, ShotComposer? comp, ShortcutBindings shortcuts, bool speedColors,
        float viewHeight = 260f, bool brioTheme = true, bool showHeaders = true, float bgAlpha = 1f)
    {
        PlayheadChanged = false; KeysChanged = false; DragStarted = false; TracksEdited = false;
        CurvePointsClicked = false; NodesClicked = false;
        if(Selected != null && tl.SelectedNodes.Count > 0 && !tl.SelectedNodes.Contains(Selected.Id))
            Selected = tl.AllKeys.FirstOrDefault(k => tl.SelectedNodes.Contains(k.Id));

        float s = XcpStyle.Scale;
        var start = ImGui.GetCursorScreenPos();
        float totalW = MathF.Max(showHeaders ? 300f * s : 180f * s, ImGui.GetContentRegionAvail().X);
        float height = MathF.Max(showHeaders ? 150f * s : 52f * s, viewHeight);
        float rulerH = RulerH, outputH = OutputH, headerW = showHeaders ? HeaderW : 0f, nodeR = NodeR;
        float topArea = rulerH + outputH + 4f * s;
        float trackHeight = MathF.Max(1f, height - topArea);
        trackScroll = Math.Clamp(trackScroll, 0, Math.Max(0, tl.Tracks.Count * LaneH - trackHeight));

        // ---- track headers (left column) ----
        if(showHeaders) DrawHeaders(tl, start, height, headerW, rulerH, outputH, topArea, brioTheme);
        int lanes = tl.Tracks.Count;
        if(TracksEdited || (comp != null && comp.TrackCount != lanes)) comp = null;

        // ---- timeline area ----
        var origin = new Vector2(start.X + headerW, start.Y);
        float width = totalW - headerW;
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
            if(shortcuts.Held(Shortcut.ZoomY))
            {
                float at = (trackScroll + mouse.Y - origin.Y - topArea) / LaneH;
                LaneH = Math.Clamp(LaneH * MathF.Pow(1.2f, io.MouseWheel), 28f * s, 92f * s);
                trackScroll = at * LaneH - (mouse.Y - origin.Y - topArea);
            }
            else if(shortcuts.Held(Shortcut.ScrollY)) trackScroll -= io.MouseWheel * LaneH * 2;
            else if(shortcuts.Held(Shortcut.ZoomX))
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
        float LaneTop(int i) => origin.Y + topArea + i * LaneH - trackScroll;

        // background
        dl.PushClipRect(origin, origin + size, true);
        dl.AddRectFilled(origin, origin + size, XcpStyle.ScaleAlpha(XcpStyle.CanvasBg(brioTheme), bgAlpha));
        dl.AddRectFilled(origin, origin + new Vector2(width, rulerH), XcpStyle.ScaleAlpha(XcpStyle.CanvasRuler(brioTheme), bgAlpha));
        for(int i = 0; i < lanes; i++)
        {
            if(LaneTop(i) + LaneH <= origin.Y + topArea || LaneTop(i) >= origin.Y + height) continue;
            uint bg = i == tl.ActiveTrackIndex ? XcpStyle.CanvasLaneActive(brioTheme)
                : (i % 2 == 0 ? XcpStyle.CanvasLaneEven(brioTheme) : XcpStyle.CanvasLaneOdd(brioTheme));
            dl.AddRectFilled(new Vector2(origin.X, Math.Max(origin.Y + topArea, LaneTop(i))),
                new Vector2(origin.X + width, Math.Min(origin.Y + height, LaneTop(i) + LaneH)), XcpStyle.ScaleAlpha(bg, bgAlpha));
            dl.AddLine(new Vector2(origin.X, LaneTop(i) + LaneH), new Vector2(origin.X + width, LaneTop(i) + LaneH), 0x18FFFFFF);
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
            dl.AddLine(new Vector2(x, origin.Y + rulerH - (isMajor ? 10f * s : 4f * s)), new Vector2(x, origin.Y + rulerH), 0xFF888894);
            if(isMajor)
            {
                dl.AddLine(new Vector2(x, origin.Y + rulerH), new Vector2(x, origin.Y + height), XcpStyle.CanvasGridMajor(brioTheme));
                dl.AddText(new Vector2(x + 3f * s, origin.Y + 2f * s), 0xFFC8C8D2, f % tl.Fps == 0 ? $"{f}  {f / tl.Fps}s" : $"{f}");
            }
        }
        dl.AddLine(new Vector2(origin.X, origin.Y + rulerH), new Vector2(origin.X + width, origin.Y + rulerH), XcpStyle.CanvasBorder(brioTheme));

        // output row: which track is on screen
        float outY = origin.Y + rulerH + 2f * s;
        if(comp != null)
        {
            foreach(var run in comp.Runs)
            {
                if(run.End + 1 < ScrollFrame || run.Start > ScrollFrame + visibleFrames) continue;
                uint c = tl.Tracks[Math.Clamp(run.Track, 0, lanes - 1)].Color;
                dl.AddRectFilled(new Vector2(X(run.Start), outY), new Vector2(X(run.End + 1), outY + outputH - 2f * s), c, 3f * s);
            }
            foreach(int cf in comp.CutFrames)
            {
                float x = X(cf);
                if(x < origin.X - 4 || x > origin.X + width + 4) continue;
                dl.AddLine(new Vector2(x, outY - 2f * s), new Vector2(x, origin.Y + height), 0xC0FFFFFF, 1.5f);
                dl.AddTriangleFilled(new Vector2(x - 4f * s, outY - 2f * s), new Vector2(x + 4f * s, outY - 2f * s), new Vector2(x, outY + 4f * s), 0xFFFFFFFF);
            }
        }
        else
            dl.AddText(new Vector2(origin.X + 6f * s, outY - 1f * s), 0xFF888894, I18n.T("timeline.output_empty"));

        // lanes: segments, speed tint, holds, nodes
        Keyframe? hoveredKey = null;
        (int lane, CurvePointTime point)? hoveredPoint = null;
        dl.PushClipRect(new Vector2(origin.X, origin.Y + topArea), origin + size, true);
        for(int ti = 0; ti < lanes; ti++)
        {
            if(LaneTop(ti) + LaneH <= origin.Y + topArea || LaneTop(ti) >= origin.Y + height) continue;
            var track = tl.Tracks[ti];
            var curveTimes = CurveTimes(track, comp);
            float midY = curveTimes.Count > 0 ? LaneTop(ti) + LaneH * 0.36f : LaneTop(ti) + LaneH / 2;
            var seq = comp?.SequenceOf(ti);
            bool active = comp?.IsActive(ti) ?? !track.Muted;

            if(seq != null)
            {
                foreach(var seg in seq.Segments)
                {
                    float a = X(seg.Start), b = X(seg.End);
                    dl.AddRectFilled(new Vector2(a, midY - 3f), new Vector2(MathF.Max(b, a + 2f), midY + 3f), active ? 0xFF52525E : 0xFF34343C, 3f);
                    if(speedColors && seg.Path != null && active)
                    {
                        float stepPx = 3f;
                        for(float x = MathF.Max(a, origin.X); x < MathF.Min(b, origin.X + width); x += stepPx)
                        {
                            float f = FrameAt(x);
                            if(comp!.DisplaySpeedAt(ti, f) is float v)
                            {
                                bool shown = comp!.TrackAt(f) == ti;
                                uint col = SpeedColor(v / comp.DisplaySpeedPeak);
                                if(!shown) col = (col & 0x00FFFFFF) | 0x50000000;
                                dl.AddRectFilled(new Vector2(x, midY - 3f), new Vector2(x + stepPx, midY + 3f), col, 0f);
                            }
                        }
                    }
                }
            }

            foreach(var (k, idx) in track.Keys.Select((key, index) => (key, index + 1)))
            {
                float x = X(k.Frame);
                if(x > origin.X + width + 24 || X(k.Frame + k.HoldFrames) < origin.X - 24) continue;
                if(k.HoldFrames > 0)
                    dl.AddRectFilled(new Vector2(x, midY - 6f), new Vector2(X(k.Frame + k.HoldFrames), midY + 6f), 0xA03399FF, 3f);
                if(k.CutIn)
                {
                    dl.AddLine(new Vector2(x, LaneTop(ti) + 2f), new Vector2(x, LaneTop(ti) + LaneH - 2f), 0xFFFFFFFF, 1.5f);
                    dl.AddText(new Vector2(x - 14f, LaneTop(ti)), 0xFFFFFFFF, "✂");
                }
                bool over = hovered && mouse.Y >= origin.Y + topArea &&
                    Vector2.Distance(mouse, new Vector2(x, midY)) <= nodeR + 3f;
                if(over) hoveredKey = k;
                bool sel = tl.SelectedNodes.Contains(k.Id) || k == Selected;
                uint fill = track.Color;
                if(!active) fill = (fill & 0x00FFFFFF) | 0x70000000;
                DrawDiamond(dl, new Vector2(x, midY), nodeR + (sel ? 2f : 0f), sel ? 0xFF00FFFF : fill, over || sel ? 0xFFFFFFFF : 0xFF101014);
                if(curveTimes.Count > 0) dl.AddText(new Vector2(x + nodeR + 2f, midY - 7f), 0xFFDDDDDD, $"{idx}");
                else dl.AddText(new Vector2(x - 4f, midY + nodeR - 1f), 0xFFDDDDDD, $"{idx}");
            }

            if(curveTimes.Count > 0 && track.FollowCurve is { PathSpline: { } spline } curve)
            {
                float py = LaneTop(ti) + LaneH - 7f;
                int si = curve.Splines.IndexOf(spline);
                bool context = Curves != null && Curves.CurveId == curve.Id && Curves.EditMode;
                foreach(var pt in curveTimes)
                {
                    if(pt.Frame is not double f) continue;
                    float x = X((float)f);
                    bool sel = context && Curves!.Selection.Contains(new CurveSelection(si, pt.Index));
                    bool over = hovered && Vector2.Distance(mouse, new Vector2(x, py)) <= 6f;
                    if(over) hoveredPoint = (ti, pt);
                    uint col = sel ? 0xFF00FFFF : pt.Repeat && !pt.Anchored ? 0xFF888888 : pt.Anchored ? 0xFFFFCC66 : 0xFFDDDDDD;
                    if(pt.Repeat && !pt.Anchored) dl.AddCircle(new Vector2(x, py), over || sel ? 5f : 4f, col, 12, 1.2f);
                    else if(pt.Anchored)
                    {
                        dl.AddRectFilled(new Vector2(x - 4f, py - 4f), new Vector2(x + 4f, py + 4f), col);
                        if(over || sel) dl.AddRect(new Vector2(x - 5f, py - 5f), new Vector2(x + 5f, py + 5f), 0xFFFFFFFF);
                    }
                    else dl.AddCircle(new Vector2(x, py), over || sel ? 5.5f : 4.5f, col, 12, 2f);
                    dl.AddText(new Vector2(x + 6f, py - 8f), 0xFF9999A8, $"P{pt.Index + 1}");
                }
            }
        }

        dl.PopClipRect();
        // playhead
        float px = X(tl.Playhead);
        uint playCol = XcpStyle.CanvasPlayhead(brioTheme);
        dl.AddLine(new Vector2(px, origin.Y), new Vector2(px, origin.Y + height), playCol, 2f);
        dl.AddTriangleFilled(new Vector2(px - 6f * s, origin.Y), new Vector2(px + 6f * s, origin.Y), new Vector2(px, origin.Y + 8f * s), playCol);
        dl.PopClipRect();

        // outer crisp frame border around header + canvas
        dl.AddRect(start, start + new Vector2(totalW, height), XcpStyle.CardBorderU32(brioTheme), 4f * s, ImDrawFlags.None, 1f);

        // ---- interaction ----
        if(hoveredPoint != null) hoveredKey = null;
        if(ImGui.IsItemActivated() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if(hoveredPoint is { } hp && Curves != null)
            {
                bool additive = shortcuts.Held(Shortcut.Select) || shortcuts.Held(Shortcut.ExtendSelection);
                var track = tl.Tracks[hp.lane];
                Curves.SelectFollowedPoint(track, hp.point.Index, additive);
                Selected = null; tl.SelectNodes([]);
                tl.ActiveTrackIndex = hp.lane;
                CurvePointsClicked = true;
                _dragPoint = additive || hp.point.Repeat && !hp.point.Anchored || hp.point.Frame is not double f0
                    ? null : (track, hp.point.Id, f0, hp.point.Repeat);
                _pointDragged = false;
                _dragStartFrame = FrameAt(mouse.X);
            }
            else if(hoveredKey != null)
            {
                Curves?.ClearSelection();
                NodesClicked = true;
                bool additive = shortcuts.Held(Shortcut.Select) || shortcuts.Held(Shortcut.ExtendSelection);
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
                bool box = shortcuts.Held(Shortcut.Box);
                _boxStart = box ? mouse : null; _boxAdd = box;
                _scrubbing = !box && mouse.Y < origin.Y + rulerH;
                _panning = !box && !_scrubbing;
                float laneY = mouse.Y - (origin.Y + topArea) + trackScroll;
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
                int frame = (int)MathF.Round((float)dp.start + FrameAt(mouse.X) - _dragStartFrame);
                if(_pointDragged || frame != (int)Math.Round(dp.start))
                {
                    tl.SetCurvePointAnchor(dp.track, dp.id, frame, snapshot: !_pointDragged, loopEnd: dp.loopEnd);
                    _pointDragged = true;
                    KeysChanged = true;
                }
                int clampedFrame = Math.Clamp(frame, 0, tl.Length);
                ImGui.SetTooltip(I18n.T("timeline.drag_anchor_tip", clampedFrame, $"{clampedFrame / (float)tl.Fps:0.00}"));
            }
            else if(_dragKey != null)
            {
                int delta = (int)MathF.Round(FrameAt(mouse.X) - _dragStartFrame);
                var targets = _dragFrames.ToDictionary(p => p.Key, p => p.Value + delta);
                if(targets.Any(p => tl.AllKeys.Any(k => k.Id == p.Key && k.Frame != p.Value)) && tl.MoveNodes(targets)) KeysChanged = true;
                ImGui.SetTooltip(I18n.T("timeline.drag_key_tip", _dragKey.Frame, $"{_dragKey.Frame / (float)tl.Fps:0.00}"));
            }
            else if(_boxStart is Vector2 boxStart)
            {
                dl.AddRectFilled(Vector2.Min(boxStart, mouse), Vector2.Max(boxStart, mouse), 0x26E04B62);
                dl.AddRect(Vector2.Min(boxStart, mouse), Vector2.Max(boxStart, mouse), 0xFFE096FF);
            }
            else if(_scrubbing)
            {
                tl.Playhead = Math.Clamp(MathF.Round(FrameAt(mouse.X)), 0, tl.Length);
                PlayheadChanged = true;
            }
        }
        else if(_dragKey != null || _dragPoint != null || _scrubbing || _panning || _boxStart != null)
        {
            if(_boxStart is Vector2 boxStart)
            {
                var lo = Vector2.Min(boxStart, mouse); var hi = Vector2.Max(boxStart, mouse);
                float clipTop = origin.Y + topArea, clipBottom = origin.Y + height;
                var hitNodes = new List<Guid>();
                var hitCurvePoints = new Dictionary<int, List<int>>();
                float bestNodeDy = float.PositiveInfinity, bestCurveDy = float.PositiveInfinity;
                int bestCurveLane = -1;
                for(int i = 0; i < tl.Tracks.Count; i++)
                {
                    var track = tl.Tracks[i];
                    var curveTimes = CurveTimes(track, comp);
                    float y = curveTimes.Count > 0 ? LaneTop(i) + LaneH * 0.36f : LaneTop(i) + LaneH * 0.5f;
                    if(y >= clipTop && y <= clipBottom && y >= lo.Y && y <= hi.Y)
                    {
                        foreach(var k in track.Keys)
                        {
                            float x = X(k.Frame);
                            if(x >= lo.X && x <= hi.X)
                            {
                                hitNodes.Add(k.Id);
                                bestNodeDy = MathF.Min(bestNodeDy, MathF.Abs(y - boxStart.Y));
                            }
                        }
                    }
                    if(curveTimes.Count > 0 && Curves != null)
                    {
                        float py = LaneTop(i) + LaneH - 7f;
                        if(py >= clipTop && py <= clipBottom && py >= lo.Y && py <= hi.Y)
                        {
                            var pts = new List<int>();
                            foreach(var pt in curveTimes)
                            {
                                if(pt.Frame is not double f) continue;
                                float x = X((float)f);
                                if(x >= lo.X && x <= hi.X) pts.Add(pt.Index);
                            }
                            if(pts.Count > 0)
                            {
                                hitCurvePoints[i] = pts;
                                float dy = MathF.Abs(py - boxStart.Y);
                                if(dy < bestCurveDy) { bestCurveDy = dy; bestCurveLane = i; }
                            }
                        }
                    }
                }
                if(bestCurveLane >= 0 && (hitNodes.Count == 0 || bestCurveDy < bestNodeDy))
                {
                    Curves!.SelectFollowedPoints(tl.Tracks[bestCurveLane], hitCurvePoints[bestCurveLane].Distinct(), _boxAdd);
                    Selected = null; tl.SelectNodes([]);
                    tl.ActiveTrackIndex = bestCurveLane;
                    CurvePointsClicked = true;
                }
                else if(hitNodes.Count > 0)
                {
                    Curves?.ClearSelection();
                    tl.SelectNodes(hitNodes, _boxAdd);
                    Selected ??= tl.AllKeys.FirstOrDefault(k => tl.SelectedNodes.Contains(k.Id));
                    NodesClicked = true;
                }
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
                ? I18n.T("timeline.point_loop_end_anchored_tip", hc.Name, p.Index + 1, $"{p.Frame:0.###}")
                : p.Repeat
                ? I18n.T("timeline.point_loop_end_tip", hc.Name, p.Index + 1, $"{p.Frame:0.###}")
                : I18n.T("timeline.point_tip", hc.Name, p.Index + 1,
                    p.Anchored ? I18n.T("anchor.timeline") : I18n.T("anchor.camera"),
                    $"{p.Frame:0.###}", $"{p.Factor * 100:0.0}",
                    p.Anchored ? I18n.T("timeline.point_drag_anchored") : I18n.T("timeline.point_drag_camera")));
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
            ImGui.SetTooltip(I18n.T("timeline.node_tip", owner?.Name ?? "", (owner?.Keys.IndexOf(k) ?? 0) + 1,
                k.Frame, $"{k.Frame / (float)tl.Fps:0.00}", k.CutIn ? I18n.T("timeline.node_cut_badge") : ""));
            if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                tl.Playhead = k.Frame;
                PlayheadChanged = true;
            }
        }
        else if(hovered && comp != null && mouse.Y >= outY && mouse.Y <= outY + outputH)
        {
            int f = (int)MathF.Round(FrameAt(mouse.X));
            int t = comp.TrackAt(f);
            if(t >= 0 && t < lanes) ImGui.SetTooltip(I18n.T("timeline.output_frame_tip", f, tl.Tracks[t].Name));
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height + 2f * s));
        ImGui.Dummy(new Vector2(1, 1));
    }

    private void DrawHeaders(CamTimeline tl, Vector2 start, float height, float headerW, float rulerH, float outputH, float topArea, bool brioTheme)
    {
        float s = XcpStyle.Scale;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(start, start + new Vector2(headerW - 1f, height), XcpStyle.CanvasHeaderBg(brioTheme));
        dl.AddLine(start + new Vector2(headerW - 1f, 0), start + new Vector2(headerW - 1f, height), XcpStyle.CanvasBorder(brioTheme));

        ImGui.SetCursorScreenPos(start + new Vector2(5f * s, 2f * s));
        if(UiWidgets.IconTextButton("add_trk", FontAwesomeIcon.Plus, I18n.T("common.track"),
            I18n.T("timeline.add_track_tip"),
            size: new Vector2(headerW - 12f * s, rulerH - 4f * s), brioTheme: brioTheme))
        {
            BeforeTrackEdit?.Invoke();
            tl.AddTrack(); trackScroll = 0;
            Selected = null;
            TracksEdited = true;
        }

        ImGui.SetCursorScreenPos(start + new Vector2(8f * s, rulerH + 1f * s));
        ImGui.TextDisabled(I18n.T("timeline.output_mix"));

        float btnSz = MathF.Min(20f * s, LaneH - 8f * s);
        var sq = new Vector2(btnSz, btnSz);
        int moveIdx = -1, moveDir = 0, removeIdx = -1;

        for(int i = 0; i < tl.Tracks.Count; i++)
        {
            var t = tl.Tracks[i];
            float y = start.Y + topArea + i * LaneH - trackScroll;
            if(y < start.Y + topArea - 1f || y + LaneH > start.Y + height + 1f) continue;

            bool isActive = i == tl.ActiveTrackIndex;
            if(isActive)
                dl.AddRectFilled(new Vector2(start.X + 1f, y), new Vector2(start.X + headerW - 2f, y + LaneH), XcpStyle.CanvasLaneActive(brioTheme));
            dl.AddRectFilled(new Vector2(start.X + 3f * s, y + 4f * s), new Vector2(start.X + 6.5f * s, y + LaneH - 4f * s), t.Color, 2f * s);
            dl.AddLine(new Vector2(start.X, y + LaneH), new Vector2(start.X + headerW - 1f, y + LaneH), 0x18FFFFFF);

            ImGui.PushID($"trk{i}");
            float rowMid = y + (LaneH - btnSz) * 0.5f;
            float nameW = MathF.Max(46f * s, headerW - 12f * s - (btnSz + 2f * s) * 5f - 6f * s);

            ImGui.SetCursorScreenPos(new Vector2(start.X + 10f * s, y + (LaneH - ImGui.GetTextLineHeight()) * 0.5f));
            if(ImGui.Selectable($"{t.Name}##sel", isActive, ImGuiSelectableFlags.None, new Vector2(nameW, 0)))
                tl.ActiveTrackIndex = i;
            UiWidgets.Tooltip(isActive ? I18n.T("timeline.track_active_tip") : I18n.T("timeline.track_select_tip"));

            float bx = start.X + headerW - (btnSz + 2f * s) * 5f - 4f * s;
            ImGui.SetCursorScreenPos(new Vector2(bx, rowMid));

            ImGui.BeginDisabled(i == 0);
            if(UiWidgets.IconButton("up", FontAwesomeIcon.ChevronUp, I18n.T("timeline.track_up_tip"), sq, subtle: true, brioTheme: brioTheme))
            { moveIdx = i; moveDir = -1; }
            ImGui.EndDisabled();

            ImGui.SameLine(0, 2f * s);
            ImGui.BeginDisabled(i == tl.Tracks.Count - 1);
            if(UiWidgets.IconButton("dn", FontAwesomeIcon.ChevronDown, I18n.T("timeline.track_down_tip"), sq, subtle: true, brioTheme: brioTheme))
            { moveIdx = i; moveDir = 1; }
            ImGui.EndDisabled();

            ImGui.SameLine(0, 2f * s);
            if(ToggleBadge("M", t.Muted, XcpStyle.MuteAmber, sq, I18n.T("timeline.track_mute_tip")))
            { BeforeTrackEdit?.Invoke(); t.Muted = !t.Muted; TracksEdited = true; }

            ImGui.SameLine(0, 2f * s);
            if(ToggleBadge("S", t.Solo, XcpStyle.SoloBlue, sq, I18n.T("timeline.track_solo_tip")))
            { BeforeTrackEdit?.Invoke(); t.Solo = !t.Solo; TracksEdited = true; }

            ImGui.SameLine(0, 2f * s);
            ImGui.BeginDisabled(tl.Tracks.Count <= 1);
            if(UiWidgets.IconButton("del", FontAwesomeIcon.Times,
                tl.Tracks.Count <= 1 ? I18n.T("timeline.track_del_last_tip") : I18n.T("timeline.track_del_tip"), sq, subtle: true, brioTheme: brioTheme))
                removeIdx = i;
            ImGui.EndDisabled();

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

    private static bool ToggleBadge(string label, bool on, Vector4 color, Vector2 size, string tip)
    {
        if(on)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, color);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Min(Vector4.One, color * 1.12f) with { W = 1f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        }
        bool clicked = ImGui.Button($"##b{label}", size);
        ImGui.PopStyleColor(on ? 3 : 1);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var ts = ImGui.CalcTextSize(label);
        uint textCol = on
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, ImGui.GetStyle().Alpha))
            : ImGui.GetColorU32(ImGuiCol.Text);
        ImGui.GetWindowDrawList().AddText(
            new Vector2(MathF.Round((min.X + max.X - ts.X) * 0.5f), MathF.Round((min.Y + max.Y - ts.Y) * 0.5f)),
            textCol, label);

        UiWidgets.Tooltip(tip);
        return clicked;
    }

    public void ScrollToTrack(int index, float viewHeight)
    {
        float topArea = RulerH + OutputH + 4f * XcpStyle.Scale;
        float trackHeight = MathF.Max(LaneH, viewHeight - topArea);
        float top = index * LaneH;
        if(top < trackScroll) trackScroll = top;
        else if(top + LaneH > trackScroll + trackHeight) trackScroll = top + LaneH - trackHeight;
        trackScroll = MathF.Max(0f, trackScroll);
    }

    public void EnsureVisible(float frame, float width, bool includeHeader = true)
    {
        float headerW = includeHeader ? HeaderW : 0f;
        float visible = MathF.Max(20f, (width - headerW) / PixelsPerFrame);
        if(frame < ScrollFrame) ScrollFrame = frame - visible * 0.1f;
        else if(frame > ScrollFrame + visible) ScrollFrame = frame - visible * 0.9f;
    }

    public void FitAll(CamTimeline tl, float width, bool includeHeader = true)
    {
        float headerW = includeHeader ? HeaderW : 0f;
        int span = Math.Max(tl.Length, 30);
        PixelsPerFrame = Math.Clamp((width - headerW - 20f) / span, 0.5f, 30f);
        ScrollFrame = -5f;
    }

    private static int PickStep(float ppf, int fps)
    {
        int[] choices = [1, 5, 10, 15, fps, fps * 2, fps * 5, fps * 10, fps * 30, fps * 60];
        foreach(int c in choices)
            if(c > 0 && c * ppf >= 55f) return c;
        return fps * 60;
    }

    private static void DrawDiamond(ImDrawListPtr dl, Vector2 c, float r, uint fill, uint border)
    {
        dl.AddQuadFilled(new(c.X, c.Y - r), new(c.X + r, c.Y), new(c.X, c.Y + r), new(c.X - r, c.Y), fill);
        dl.AddQuad(new(c.X, c.Y - r), new(c.X + r, c.Y), new(c.X, c.Y + r), new(c.X - r, c.Y), border, 1.5f);
    }

    private static uint SpeedColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float r, g, b;
        if(t < 0.5f) { float k = t * 2f; r = 0.2f * k; g = 0.6f + 0.4f * k; b = 1f - 0.5f * k; }
        else { float k = (t - 0.5f) * 2f; r = 0.2f + 0.8f * k; g = 1f - 0.7f * k; b = 0.5f * (1f - k); }
        return ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, 1f));
    }
}
