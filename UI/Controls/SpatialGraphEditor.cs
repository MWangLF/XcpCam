using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>Shot-local graph editor. Selection and locks are editor state, not timeline mutations.</summary>
public sealed class SpatialGraphEditor
{
    public string Status { get; private set; } = "";

    /// <summary>
    /// Editor channels, following Blender's Follow Path layering: the path shape is the 3D curve edited in the
    /// game view; here position is the path progress (length factor) and a location offset, both over frames.
    /// Rotation and FoV stay the node-keyed spatial curves.
    /// </summary>
    private enum GraphChannel { Progress, OffsetX, OffsetY, OffsetZ, Yaw, Pitch, Roll, Fov }
    private static readonly GraphChannel[] Channels = Enum.GetValues<GraphChannel>();
    private static readonly uint[] Colors = { 0xFF70CE70, 0xFF6666EF, 0xFF70CE70, 0xFFEF9870, 0xFF60BFFF, 0xFFE080D0, 0xFFCFA060, 0xFF60DFDF };
    private static bool IsTime(GraphChannel ch) => ch <= GraphChannel.OffsetZ;
    private static SpatialChannel Spatial(GraphChannel ch) => ch switch
    {
        GraphChannel.Yaw => SpatialChannel.Yaw, GraphChannel.Pitch => SpatialChannel.Pitch, GraphChannel.Roll => SpatialChannel.Roll,
        GraphChannel.Fov => SpatialChannel.Fov, _ => throw new ArgumentOutOfRangeException(nameof(ch)),
    };
    private readonly HashSet<GraphChannel> _visible = new() { GraphChannel.Progress };
    private SpatialCurves _space = null!;
    private readonly Dictionary<GraphChannel, FCurve> _curves = new();
    private Dictionary<GraphChannel, FCurve> Curves => _curves;
    private void BindCurves(ShotCurves shot)
    {
        _curves.Clear();
        _curves[GraphChannel.Progress] = shot.PathProgress;
        _curves[GraphChannel.OffsetX] = shot.Offset[0]; _curves[GraphChannel.OffsetY] = shot.Offset[1]; _curves[GraphChannel.OffsetZ] = shot.Offset[2];
        foreach(var ch in Channels.Where(c => !IsTime(c))) _curves[ch] = shot.Space.Channels[Spatial(ch)];
    }
    /// <summary>Path progress values are the nodes' own positions on the node path; only their timing is edited.</summary>
    private bool ValueLocked(KeyId id) => id.Channel == GraphChannel.Progress && !_shot.IsFollowing && Resolve(_shot, id)?.NodeId != null || _driven.Contains(id.Channel);
    /// <summary>Channels a tracking constraint drives: values and handles are not editable (keys can still retime nodes).</summary>
    private readonly HashSet<GraphChannel> _driven = new();
    private double DomainStart(GraphChannel ch) => IsTime(ch) ? _shot.Start : _space.Start;
    private double DomainEnd(GraphChannel ch) => IsTime(ch) ? _shot.End : _space.End;
    private IEnumerable<GraphChannel> DisplayChannels => Channels;
    private int _cursor;
    private bool _normalize;
    private readonly Dictionary<GraphChannel, (double center, double scale)> _normalization = new();
    private readonly HashSet<GraphChannel> _locked = new();
    private readonly HashSet<KeyId> _selected = new();
    private GraphChannel _active = GraphChannel.Progress;
    private KeyId? _primary;
    private Guid _shotId;
    private long _selectionRevision = -1;
    private bool _blankClick;

    /// <summary>Mirrors the key selection to the shared node selection (camera timeline / inspector / gizmo).</summary>
    private void PushSelection(ShotCurves shot, CamTimeline timeline)
    {
        var nodes = _selected.Select(id => Resolve(shot, id)?.NodeId).OfType<Guid>().Distinct().ToList();
        timeline.SelectNodes(nodes);
        _selectionRevision = timeline.SelectionRevision;
    }
    private ShotCurves? _lastShot;
    private bool _fit = true;
    private int _scope, _preset, _handleMode = 1;
    private bool _applyHandles;
    private ShotCurves _shot = null!;
    private double ToFrame(GraphChannel channel, double parameter) => IsTime(channel) ? parameter : SpatialFrameMap.Frame(_shot, Spatial(channel), parameter);
    private double ToParameter(GraphChannel channel, double frame) => IsTime(channel) ? frame : SpatialFrameMap.Parameter(_shot, Spatial(channel), frame);
    private double _left, _span = 100, _bottom = -1, _height = 2;
    private Gesture _gesture;
    private Vector2 _press, _boxEnd;
    private List<Anchor> _anchors = new();
    private KeyId? _handleKey;
    private bool _rightHandle, _snapshot;
    private Vector2 _handleOrigin;
    private double _dragSpan, _dragHeight;
    private float HeaderWidth => 162f * XcpStyle.Scale;
    private float RulerHeight => 23f * XcpStyle.Scale;
    private enum Gesture { None, Keys, Handle, Box, Scrub, Pan }
    private readonly record struct KeyId(GraphChannel Channel, Guid Id);
    private readonly record struct Anchor(KeyId Id, double Value, double DisplayFrame);
    private ShotCurves? _dragMap;
    private double _handleDisplayFrame;
    private double FrozenParameter(GraphChannel channel, double frame) => IsTime(channel) ? frame : SpatialFrameMap.Parameter(_dragMap ?? _shot, Spatial(channel), frame);
    private double FrozenFrame(GraphChannel channel, double parameter) => IsTime(channel) ? parameter : SpatialFrameMap.Frame(_dragMap ?? _shot, Spatial(channel), parameter);
    private readonly record struct Hit(KeyId Id, bool Handle, bool Right, Vector2 Position);

    public void Draw(ShotCurves shot, CamTimeline timeline, ShortcutBindings shortcuts, bool brioTheme = true)
    {
        if(shot.EditorMode != CurveEditorMode.Advanced) { EndGesture(); ImGui.TextDisabled(I18n.T("graph.sg_locked")); return; }
        _shot = shot;
        if (_shotId != shot.Id)
        {
            _shotId = shot.Id;
            _cursor = shot.Start;
            _selected.Clear();
            _primary = null;
            _fit = true;
            EndGesture();
        }
        // Undo commonly replaces curves/shot objects. Never keep key references across frames.
        if (!ReferenceEquals(_lastShot, shot)) EndGesture();
        _lastShot = shot;
        if (!ReferenceEquals(_space, shot.Space)) EndGesture();
        _space = shot.Space;
        BindCurves(shot);
        if(_fit && shot.Space.PreserveIntermediateKeys && _visible.SetEquals([GraphChannel.Progress]) && !shot.PathProgress.Keys.Any(k => k.NodeId == null))
        {
            _active = Channels.FirstOrDefault(ch => ch != GraphChannel.Progress && Curves[ch].Keys.Any(k => k.NodeId == null), GraphChannel.OffsetX);
            _visible.Clear();
            _visible.Add(_active);
        }
        _driven.Clear();
        if(timeline.Tracks.FirstOrDefault(t => t.Shots.Contains(shot)) is { } owner)
        {
            if(owner.RotationDriven) _driven.UnionWith([GraphChannel.Yaw, GraphChannel.Pitch, GraphChannel.Roll]);
            if(owner.FovDriven) _driven.Add(GraphChannel.Fov);
        }
        if(_selectionRevision != timeline.SelectionRevision)
        {
            _selected.Clear();
            foreach(var pair in Curves)
                foreach(var key in pair.Value.Keys)
                    if(key.NodeId is Guid node && timeline.SelectedNodes.Contains(node)) _selected.Add(new KeyId(pair.Key, key.Id));
            _primary = _selected.Count > 0 ? _selected.First() : null;
            _selectionRevision = timeline.SelectionRevision;
        }
        _cursor = Math.Clamp(_cursor, shot.Start, shot.End);
        _selected.RemoveWhere(id => Resolve(shot, id) == null);
        if (_primary is { } p && !_selected.Contains(p)) _primary = null;
        ImGui.PushID("SpatialGraph" + shot.Id);
        long beforeToolbar = timeline.Revision;
        DrawToolbar(shot, timeline, brioTheme);
        if(timeline.Revision != beforeToolbar) { ImGui.PopID(); return; }
        DrawInspector(shot, timeline);

        float s = XcpStyle.Scale;
        float statusReserve = string.IsNullOrEmpty(Status) ? 4f * s : ImGui.GetTextLineHeightWithSpacing() + 8f * s;
        float headerW = HeaderWidth, rulerH = RulerHeight;
        var start = ImGui.GetCursorScreenPos();
        float width = MathF.Max(210f * s, ImGui.GetContentRegionAvail().X - headerW);
        float graphHeight = MathF.Max(265f * s, ImGui.GetContentRegionAvail().Y - statusReserve);
        DrawChannels(start, headerW, graphHeight, shortcuts, brioTheme);
        var origin = start + new Vector2(headerW, 0);
        var plot = origin + new Vector2(0, rulerH);
        var end = origin + new Vector2(width, graphHeight);
        float plotHeight = graphHeight - rulerH;
        if (_gesture == Gesture.None) UpdateNormalization();
        if (_fit) { Fit(shot, false); _fit = false; }
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##canvas", new Vector2(width, graphHeight),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        bool insidePlot = Inside(mouse, plot, end);

        float X(double frame) => origin.X + (float)((frame - _left) / _span * width);
        float Y(GraphChannel ch, double value) => end.Y - (float)((Display(ch, value) - _bottom) / _height * plotHeight);
        double Frame(float x) => _left + (x - origin.X) / width * _span;
        Vector2 Point(GraphChannel ch, double frame, double value) => new(X(ToFrame(ch, frame)), Y(ch, value));

        if (hovered && _gesture == Gesture.None && io.MouseWheel != 0)
        {
            ViewportMath.Wheel(ref _left, ref _span, ref _bottom, ref _height, io.MouseWheel,
                shortcuts.Held(Shortcut.ZoomX) || shortcuts.Held(Shortcut.ZoomY),
                shortcuts.Held(Shortcut.ScrollY) || shortcuts.Held(Shortcut.ZoomY), (mouse.X - origin.X) / width, (end.Y - mouse.Y) / plotHeight);
        }

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(origin, end, true);
        dl.AddRectFilled(origin, end, XcpStyle.CanvasBg(brioTheme));
        dl.AddRectFilled(origin, new Vector2(end.X, plot.Y), XcpStyle.CanvasRuler(brioTheme));
        double step = NiceStep(_span * 65 / width, true);
        double first = Math.Ceiling(_left / step) * step;
        for (double f = first; f <= _left + _span; f += step)
        {
            float x = X(f);
            dl.AddLine(new Vector2(x, plot.Y - 6f * s), new Vector2(x, end.Y), XcpStyle.CanvasGrid(brioTheme));
            dl.AddText(new Vector2(x + 3f * s, origin.Y + 3f * s), 0xFFC4C4D0, f.ToString("0"));
        }
        dl.PushClipRect(plot, end, true);
        double vs = NiceStep(_height * 50 / plotHeight, false);
        for (double v = Math.Ceiling(_bottom / vs) * vs; v <= _bottom + _height; v += vs)
        {
            float y = end.Y - (float)((v - _bottom) / _height * plotHeight);
            dl.AddLine(new Vector2(plot.X, y), new Vector2(end.X, y), XcpStyle.CanvasGrid(brioTheme));
            dl.AddText(new Vector2(plot.X + 3f * s, y + 2f * s), 0xFF8C8C9A, v.ToString("0.###"));
        }
        float cutA = Math.Clamp(X(shot.Start), plot.X, end.X);
        float cutB = Math.Clamp(X(shot.End), plot.X, end.X);
        dl.AddRectFilled(plot, new Vector2(cutA, end.Y), 0x80000000);
        dl.AddRectFilled(new Vector2(cutB, plot.Y), end, 0x80000000);
        dl.PopClipRect();

        Hit? hit = null;
        float best = 10;
        bool InShot(Vector2 pt) => Inside(pt, new Vector2(MathF.Max(plot.X, X(shot.Start)), plot.Y),
            new Vector2(MathF.Min(end.X, X(shot.End)), end.Y));
        void Candidate(Hit h, float radius)
        {
            float distance = Vector2.Distance(mouse, h.Position);
            if (hovered && insidePlot && InShot(h.Position) && distance <= radius && distance < best)
            { best = distance; hit = h; }
        }
        // Curves, keys and handles are clipped at both shot boundaries; no extrapolation into another shot.
        var clipA = new Vector2(MathF.Max(plot.X, X(shot.Start)), plot.Y);
        var clipB = new Vector2(MathF.Min(end.X, X(shot.End)), end.Y);
        if (clipB.X >= clipA.X)
        {
            dl.PushClipRect(clipA, clipB, true);
            foreach (var ch in DisplayChannels.Where(_visible.Contains))
            {
                if (!Curves.TryGetValue(ch, out var storedCurve)) continue;
                var curve = storedCurve;
                curve.Sort();
                uint color = Color(ch);
                double from = ToParameter(ch, Math.Max(shot.Start, _left)), to = ToParameter(ch, Math.Min(shot.End, _left + _span));
                if (to >= from)
                {
                    // Preserve vertical jumps explicitly even when their neighbours share a screen pixel.
                    var times = new SortedSet<double> { from, to };
                    int samples = Math.Clamp((int)width, 2, 2400);
                    for (int i = 1; i < samples; i++) times.Add(from + (to - from) * i / samples);
                    for(int i = 1; i < curve.Keys.Count; i++)
                    {
                        var key = curve.Keys[i];
                        if(key.Time <= from || key.Time > to || curve.Keys[i - 1].Interpolation != CurveInterpolation.Constant) continue;
                        times.Add(key.Time);
                        times.Add(Math.Max(from, Math.BitDecrement(key.Time)));
                    }
                    Vector2? previous = null;
                    foreach (double f in times)
                    {
                        double value = curve.EvaluateOrdered(f);
                        if (!double.IsFinite(value)) { previous = null; continue; }
                        var pt = Point(ch, f, value);
                        if (previous is { } prev) dl.AddLine(prev, pt, color, ch == _active ? 2 : 1.3f);
                        previous = pt;
                    }
                }
                var ordered = curve.Keys;
                for (int i = 0; i < ordered.Count; i++)
                {
                    var key = ordered[i];
                    if (key.Time < DomainStart(ch) || key.Time > DomainEnd(ch)) continue;
                    var id = new KeyId(ch, key.Id);
                    bool selected = _selected.Contains(id);
                    if(!selected && (key.Time < from || key.Time > to)) continue;
                    var pt = Point(ch, key.Time, key.Value);
                    if (selected)
                    {
                        // Interpolation belongs to the outgoing segment, including for the next key's left handle.
                        for (int side = 0; side < 2; side++)
                        {
                            bool right = side == 1;
                            bool bezier = right ? i + 1 < ordered.Count && key.Interpolation.ToString() == "Bezier"
                                : i > 0 && ordered[i - 1].Interpolation.ToString() == "Bezier";
                            if (!bezier) continue;
                            var h = curve.Handle(key, right);
                            var hp = Point(ch, h.X, h.Y);
                            dl.AddLine(pt, hp, 0xFF909090);
                            dl.AddCircleFilled(hp, 4, _locked.Contains(ch) ? 0xFF777777 : 0xFF70CFFF);
                            Candidate(new Hit(id, true, right, hp), 7);
                        }
                    }
                    uint fill = selected ? 0xFF60CFFF : color;
                    if (_locked.Contains(ch)) fill = 0xFF777777;
                    Diamond(dl, pt, selected ? 6 : 4, fill);
                    Candidate(new Hit(id, false, false, pt), 8);
                }
            }
            dl.PopClipRect();
        }
        foreach (int boundary in new[] { shot.Start, shot.End })
        {
            float x = X(boundary);
            dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, end.Y), 0xFFAAAAFF, 2);
            dl.AddText(new Vector2(x + 3, plot.Y + 20), 0xFFAAAAFF, boundary == shot.Start ? I18n.T("graph.boundary_start") : I18n.T("graph.boundary_end"));
        }
        foreach (var channel in Enum.GetValues<CameraChannel>())
        {
            float x = X(timeline.Playhead);
            uint color = channel == CameraChannel.Position ? 0xFF70CE70 : channel == CameraChannel.Rotation ? 0xFFE080D0 : 0xFF60DFDF;
            string label = channel == CameraChannel.Position ? I18n.T("graph.ch.pos") : channel == CameraChannel.Rotation ? I18n.T("graph.ch.rot") : I18n.T("graph.ch.fov");
            dl.AddLine(new Vector2(x, plot.Y), new Vector2(x, end.Y), color, 1);
            dl.AddText(new Vector2(x + 3, plot.Y + 4 + (int)channel * 16), color, I18n.T("graph.map_suffix", label));
        }
        float cursorX = X(_cursor);
        dl.AddLine(new Vector2(cursorX, origin.Y), new Vector2(cursorX, end.Y), 0xFF5555EE, 2);
        dl.AddTriangleFilled(new Vector2(cursorX - 5, origin.Y), new Vector2(cursorX + 5, origin.Y), new Vector2(cursorX, origin.Y + 9), 0xFF5555EE);
        if (_gesture == Gesture.Box)
        {
            var a = Vector2.Min(_press, _boxEnd); var b = Vector2.Max(_press, _boxEnd);
            dl.AddRectFilled(a, b, 0x3050A0FF); dl.AddRect(a, b, 0xFFC09050);
        }
        dl.PopClipRect();
        dl.AddRect(start, end, XcpStyle.CardBorderU32(brioTheme), 3f * s, ImDrawFlags.None, 1f);

        if (hovered && _gesture == Gesture.None)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
            { _gesture = Gesture.Pan; _press = mouse; _blankClick = false; }
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _press = mouse; _snapshot = false;
                _dragSpan = _span; _dragHeight = _height;
                _dragMap = shot.Clone();
                if (mouse.Y < plot.Y) _gesture = Gesture.Scrub;
                else if (hit is { } h)
                {
                    _active = h.Id.Channel;
                    if (shortcuts.Held(Shortcut.ExtendSelection) && !h.Handle)
                    {
                        if (!_selected.Add(h.Id)) _selected.Remove(h.Id);
                        _primary = _selected.Contains(h.Id) ? h.Id : null;
                        PushSelection(shot, timeline);
                    }
                    else
                    {
                        if (!_selected.Contains(h.Id)) { _selected.Clear(); _selected.Add(h.Id); PushSelection(shot, timeline); }
                        _primary = h.Id;
                        if (!_locked.Contains(h.Id.Channel))
                        {
                            if (h.Handle && _driven.Contains(h.Id.Channel))
                                Status = I18n.T("graph.driven_handle_locked");
                            else if (h.Handle)
                            {
                                _gesture = Gesture.Handle; _handleKey = h.Id; _rightHandle = h.Right;
                                var effectiveCurve = Curves[h.Id.Channel];
                                _handleOrigin = effectiveCurve.Handle(effectiveCurve.Keys.First(k => k.Id == h.Id.Id), h.Right);
                                _handleDisplayFrame = ToFrame(h.Id.Channel, _handleOrigin.X);
                            }
                            else
                            {
                                _gesture = Gesture.Keys;
                                _anchors = SelectedKeys(shot).Where(x => _visible.Contains(x.id.Channel) && !_locked.Contains(x.id.Channel)
                                        && Editable(x.id.Channel, x.key.Time))
                                    .Select(x => new Anchor(x.id, x.key.Value, ToFrame(x.id.Channel, x.key.Time))).ToList();
                            }
                        }
                        else Status = I18n.T("graph.ch_locked");
                    }
                }
                else if (shortcuts.Held(Shortcut.Box)) { _gesture = Gesture.Box; _boxEnd = mouse; }
                else { _gesture = Gesture.Pan; _blankClick = true; }
            }
        }
        if (_gesture == Gesture.Pan)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Middle) || ImGui.IsMouseDown(ImGuiMouseButton.Left))
            { if(Vector2.DistanceSquared(mouse, _press) >= 9) { _left -= io.MouseDelta.X / width * _span; _bottom += io.MouseDelta.Y / plotHeight * _height; } }
            else
            {
                // short left click on empty plot space (no drag) clears the selection, like the camera timeline
                if (_blankClick && Vector2.DistanceSquared(mouse, _press) < 9)
                { _selected.Clear(); _primary = null; PushSelection(shot, timeline); }
                _blankClick = false;
                EndGesture();
            }
        }
        else if (_gesture != Gesture.None)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta = mouse - _press;
                if (_gesture == Gesture.Scrub)
                {
                    _cursor = (int)Math.Clamp(Math.Round(Frame(mouse.X)), shot.Start, shot.End);
                }
                else if (_gesture == Gesture.Box) _boxEnd = Vector2.Clamp(mouse, plot, end);
                else if (delta.LengthSquared() >= 4 || _snapshot)
                {
                    if (_gesture == Gesture.Keys)
                    {
                        int dt = (int)Math.Clamp(Math.Round(delta.X / width * _dragSpan), int.MinValue / 2, int.MaxValue / 2);
                        var moves = _anchors.Select(a => (a.Id, frame: EditTime(a.Id, FrozenParameter(a.Id.Channel, a.DisplayFrame + dt)),
                            value: a.Value - delta.Y / plotHeight * _dragHeight * Scale(a.Id.Channel))).ToList();
                        ApplyMoves(shot, timeline, moves, true);
                    }
                    else if (_gesture == Gesture.Handle && _handleKey is { } id && Resolve(shot, id) is { } key)
                    {
                        var target = new Vector2((float)FrozenParameter(id.Channel, _handleDisplayFrame + delta.X / width * _dragSpan),
                            _handleOrigin.Y + (float)(-delta.Y / plotHeight * _dragHeight * Scale(id.Channel)));
                        if (!_locked.Contains(id.Channel) && float.IsFinite(target.X) && float.IsFinite(target.Y))
                        {
                            var curve = Curves[id.Channel];
                            if (Vector2.DistanceSquared(curve.Handle(key, _rightHandle), target) > .0000001f)
                            {
                                BeforeGesture(timeline);
                                if (key.Mode.ToString() is "Auto" or "AutoClamped" or "Vector") key.Mode = Named(key.Mode, "Free");
                                curve.SetHandle(key, _rightHandle, target);
                                shot.Space.Touch(); timeline.Touch(); Status = I18n.T("graph.handle_edited");
                            }
                        }
                        else Status = I18n.T("graph.handle_edit_failed");
                    }
                }
            }
            else
            {
                if (_gesture == Gesture.Box)
                {
                    var a = Vector2.Min(_press, _boxEnd); var b = Vector2.Max(_press, _boxEnd);
                    foreach (var ch in _visible)
                        if (Curves.TryGetValue(ch, out var curve))
                            foreach (var key in curve.Keys)
                                if (key.Time >= DomainStart(ch) && key.Time <= DomainEnd(ch) && Inside(Point(ch, key.Time, key.Value), a, b))
                                { var id = new KeyId(ch, key.Id); _selected.Add(id); _primary = id; }
                }
                EndGesture();
            }
        }
        if (hovered && hit is { } hover && Resolve(shot, hover.Id) is { } hoveredKey)
            ImGui.SetTooltip(I18n.T("graph.hover_channel", ChannelLabel(hover.Id.Channel), $"{ToFrame(hover.Id.Channel, hoveredKey.Time):0.###}", $"{hoveredKey.Value:G8}") +
                (_locked.Contains(hover.Id.Channel) ? I18n.T("graph.hover_locked_suffix") : ""));
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 4f * s));
        if(!string.IsNullOrEmpty(Status)) ImGui.TextDisabled(Status);
        else ImGui.Dummy(new Vector2(1, 1));
        ImGui.PopID();
    }

    private void DrawToolbar(ShotCurves shot, CamTimeline timeline, bool brioTheme)
    {
        float s = XcpStyle.Scale;

        // ---- Row 1: cursor + keyframe edit + view controls ----
        int cursor = _cursor;
        ImGui.SetNextItemWidth(74f * s);
        if(ImGui.InputInt("##sg_cur", ref cursor, 0)) _cursor = Math.Clamp(cursor, shot.Start, shot.End);
        UiWidgets.Tooltip(I18n.T("graph.cursor_tip"));

        ImGui.SameLine(0, 4f * s);
        if(UiWidgets.IconTextButton("sg_add", FontAwesomeIcon.Plus, I18n.T("graph.insert_node"), I18n.T("graph.sg_insert_node_tip"), brioTheme: brioTheme))
            Add(shot, timeline);
        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconTextButton("sg_key", FontAwesomeIcon.PlusCircle, I18n.T("graph.sg_insert_ch_key"), I18n.T("graph.sg_insert_ch_key_tip"), brioTheme: brioTheme))
            InsertChannelKeys(shot, timeline);
        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("sg_del", FontAwesomeIcon.TrashAlt, I18n.T("graph.sg_del_tip"), brioTheme: brioTheme))
            Delete(shot, timeline);

        ImGui.SameLine(0, 10f * s);
        if(UiWidgets.IconTextButton("sg_fit_all", FontAwesomeIcon.Expand, I18n.T("common.fit_all"), I18n.T("graph.sg_fit_all_tip"), brioTheme: brioTheme))
        { UpdateNormalization(); Fit(shot, false); }
        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconTextButton("sg_fit_sel", FontAwesomeIcon.Crosshairs, I18n.T("graph.sg_fit_sel"), I18n.T("graph.sg_fit_sel_tip"), brioTheme: brioTheme))
        { UpdateNormalization(); Fit(shot, true); }
        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.TogglePill("sg_norm", I18n.T("graph.sg_normalize"), _normalize, I18n.T("graph.sg_normalize_tip"), brioTheme: brioTheme))
        { _normalize = !_normalize; UpdateNormalization(); Fit(shot, false); }

        ImGui.SameLine();
        UiWidgets.HelpMarker(I18n.T("graph.sg_help_base") +
            (shot.IsFollowing ? I18n.T("graph.sg_help_follow") : I18n.T("graph.sg_help_nodepath")));

        if(_driven.Count > 0)
        {
            ImGui.SameLine();
            UiWidgets.Chip("sg_driven", _driven.Contains(GraphChannel.Fov) ? I18n.T("graph.driven_rot_fov") : I18n.T("graph.driven_rot"), XcpStyle.Warning,
                I18n.T("graph.driven_channel_tip"));
        }

        // ---- Row 2: interpolation & handles ----
        ImGui.SetNextItemWidth(92f * s); ImGui.Combo("##sg_interp", ref _preset, I18n.Combo("graph.interp.bezier", "graph.interp.linear", "graph.interp.constant"));
        UiWidgets.Tooltip(I18n.T("graph.interp_out_tip"));
        ImGui.SameLine(0, 3f * s);
        ImGui.SetNextItemWidth(116f * s); ImGui.Combo("##sg_scope", ref _scope, I18n.Combo("graph.scope.selected", "graph.scope.active_ch", "graph.scope.all_ch"));
        UiWidgets.Tooltip(I18n.T("graph.scope_batch_tip"));
        ImGui.SameLine(0, 3f * s);
        if(ImGui.Button($"{I18n.T("graph.apply_interp")}##sg_app_i")) { _applyHandles = false; ApplyPreset(shot, timeline); }

        ImGui.SameLine(0, 8f * s);
        ImGui.BeginDisabled(_preset != 0);
        ImGui.SetNextItemWidth(108f * s); ImGui.Combo("##sg_hmode", ref _handleMode, I18n.Combo("graph.hmode.auto_clamped", "graph.hmode.auto", "graph.hmode.aligned", "graph.hmode.free", "graph.hmode.vector"));
        UiWidgets.Tooltip(I18n.T("graph.hmode_both_tip"));
        ImGui.SameLine(0, 3f * s);
        if(ImGui.Button($"{I18n.T("graph.apply_handle")}##sg_app_h")) { _applyHandles = true; ApplyPreset(shot, timeline); }
        ImGui.EndDisabled();

        ImGui.SameLine(0, 10f * s);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(I18n.T("graph.sg_selected_count", _selected.Count));
    }

    private void DrawChannels(Vector2 start, float headerW, float graphHeight, ShortcutBindings shortcuts, bool brioTheme)
    {
        float s = XcpStyle.Scale;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(start, start + new Vector2(headerW - 1f, graphHeight), XcpStyle.CanvasHeaderBg(brioTheme));
        dl.AddLine(start + new Vector2(headerW - 1f, 0), start + new Vector2(headerW - 1f, graphHeight), XcpStyle.CanvasBorder(brioTheme));

        ImGui.SetCursorScreenPos(start + new Vector2(8f * s, 3f * s));
        ImGui.TextDisabled(I18n.T("graph.sg_channels_header"));

        float footerH = 28f * s;
        float listTop = start.Y + RulerHeight + 4f * s;
        float rowH = MathF.Min(30f * s, MathF.Max(22f * s, (graphHeight - RulerHeight - footerH - 8f * s) / Channels.Length));
        float btnSz = MathF.Min(20f * s, rowH - 4f * s);
        var sq = new Vector2(btnSz, btnSz);

        int i = 0;
        foreach(var ch in DisplayChannels)
        {
            float y = listTop + i++ * rowH;
            bool visible = _visible.Contains(ch), locked = _locked.Contains(ch), driven = _driven.Contains(ch);
            uint col = Color(ch);

            if(_active == ch)
                dl.AddRectFilled(new Vector2(start.X + 1f, y), new Vector2(start.X + headerW - 2f, y + rowH), XcpStyle.CanvasLaneActive(brioTheme));
            dl.AddRectFilled(new Vector2(start.X + 4f * s, y + 4f * s), new Vector2(start.X + 7f * s, y + rowH - 4f * s), visible ? col : 0x55777777, 2f * s);

            ImGui.PushID(ch.ToString());
            float midY = y + (rowH - btnSz) * 0.5f;
            ImGui.SetCursorScreenPos(new Vector2(start.X + 11f * s, midY));
            if(UiWidgets.IconButton("v", visible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                visible ? I18n.T("graph.hide_ch_tip") : I18n.T("graph.show_ch_tip"), sq, active: false, subtle: true, brioTheme: brioTheme))
            { if(visible) _visible.Remove(ch); else _visible.Add(ch); EndGesture(); }

            ImGui.SameLine(0, 2f * s);
            if(UiWidgets.IconButton("l", locked || driven ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen,
                driven ? I18n.T("graph.driven_lock_tip") : locked ? I18n.T("graph.locked_tip") : I18n.T("graph.lock_tip"),
                sq, active: locked, subtle: !locked, activeColor: XcpStyle.Warning, brioTheme: brioTheme))
            { if(locked) _locked.Remove(ch); else _locked.Add(ch); EndGesture(); }

            ImGui.SameLine(0, 4f * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (btnSz - ImGui.GetTextLineHeight()) * 0.5f);
            float labelW = MathF.Max(40f * s, headerW - (11f * s + btnSz * 2f + 12f * s));
            if(ImGui.Selectable(ChannelLabel(ch), _active == ch, ImGuiSelectableFlags.None, new Vector2(labelW, 0)))
            {
                _active = ch;
                if(!shortcuts.Held(Shortcut.ExtendSelection)) _visible.Clear();
                _visible.Add(ch); EndGesture(); _fit = true;
            }
            ImGui.PopID();
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X + 6f * s, start.Y + graphHeight - footerH + 2f * s));
        if(UiWidgets.IconTextButton("show_all_ch", FontAwesomeIcon.Eye, I18n.T("graph.sg_show_all"), I18n.T("graph.sg_show_all_tip"),
            size: new Vector2(headerW - 14f * s, 22f * s), brioTheme: brioTheme))
        { foreach(var ch in Channels) _visible.Add(ch); EndGesture(); }
    }

    private void DrawInspector(ShotCurves shot, CamTimeline timeline)
    {
        float s = XcpStyle.Scale;
        if(_primary is not { } id || Resolve(shot, id) is not { } key) return;
        bool editable = !_locked.Contains(id.Channel) && !_driven.Contains(id.Channel) && Editable(id.Channel, key.Time) && _gesture == Gesture.None;
        ImGui.BeginDisabled(!editable);
        double frame = ToFrame(id.Channel, key.Time);
        double value = key.Value;
        ImGui.SetNextItemWidth(92f * s);
        bool moved = ImGui.InputDouble($"{I18n.T("common.frame")}##sgkf", ref frame, 0, 0, "%.6g", ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine(); ImGui.SetNextItemWidth(120f * s);
        bool changed = ImGui.InputDouble($"{I18n.T("graph.sg_value_label")}##sgkv", ref value, 0, 0, "%.3f", ImGuiInputTextFlags.EnterReturnsTrue);
        if((moved || changed) && editable) ApplyMoves(shot, timeline, [(id, moved ? EditTime(id, ToParameter(id.Channel, frame)) : key.Time, value)], false);
        ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled($"· {ChannelLabel(id.Channel)}");
    }

    private void Add(ShotCurves shot, CamTimeline timeline)
    {
        EndGesture();
        if(_locked.Count > 0) { Status = I18n.T("graph.sg_add_locked"); return; }
        try
        {
            var node = timeline.AddSampledNode(shot, _cursor);
            _selectionRevision = -1;
            Status = I18n.T("graph.sg_add_ok", node.Frame);
        }
        catch(InvalidOperationException ex) { Status = ex.Message; }
    }

    private void InsertChannelKeys(ShotCurves shot, CamTimeline timeline)
    {
        EndGesture();
        var additions = _visible.Where(ch => (ch != GraphChannel.Progress || shot.Space.PreserveIntermediateKeys && !shot.IsFollowing) && !_locked.Contains(ch) && !_driven.Contains(ch))
            .Select(ch => (ch, time: IsTime(ch) ? _cursor : Math.Round(ToParameter(ch, _cursor))))
            .Where(x => Editable(x.ch, x.time) && Curves[x.ch].Keys.All(k => k.Time != x.time)).ToArray();
        if(additions.Length == 0) { Status = I18n.T("graph.sg_insert_ch_none"); return; }
        timeline.Snapshot();
        shot.Space.PreserveIntermediateKeys = true;
        _selected.Clear();
        foreach(var (ch, time) in additions)
        {
            var key = Curves[ch].InsertPreserving(time);
            _selected.Add(new KeyId(ch, key.Id));
        }
        _primary = _selected.First(); PushSelection(shot, timeline);
        shot.Space.Touch(); timeline.Touch();
        Status = I18n.T("graph.sg_insert_ch_ok", additions.Length);
    }

    private void Delete(ShotCurves shot, CamTimeline timeline)
    {
        EndGesture();
        var targets = SelectedKeys(shot).Where(x => !_locked.Contains(x.id.Channel) && !_driven.Contains(x.id.Channel) && Editable(x.id.Channel, x.key.Time)).ToList();
        var ids = targets.Where(x => x.key.NodeId.HasValue).Select(x => x.key.NodeId!.Value)
            .Concat(timeline.SelectedNodes.Where(shot.SeededNodes.Contains)).Distinct().ToArray();
        if(ids.Length > 0 && _locked.Count > 0) { Status = I18n.T("graph.sg_del_locked"); return; }
        var independent = targets.Where(x => !x.key.NodeId.HasValue).ToArray();
        if(ids.Length == 0 && independent.Length == 0) return;
        timeline.Snapshot();
        foreach(var (id, key) in independent) Curves[id.Channel].Keys.Remove(key);
        foreach(var ch in independent.Select(x => x.id.Channel).Distinct()) Curves[ch].RecalculateHandles();
        if(ids.Length > 0) timeline.DeleteNodes(ids, snapshot: false);
        else { shot.Space.Touch(); timeline.Touch(); }
        _selected.Clear(); _primary = null;
        Status = I18n.T("graph.sg_del_ok", ids.Length, independent.Length);
    }

    private void ApplyPreset(ShotCurves shot, CamTimeline timeline)
    {
        EndGesture();
        var targets = (_scope == 0 ? SelectedKeys(shot) : Curves
            .Where(x => _scope == 2 || x.Key == _active)
            .SelectMany(x => x.Value.Keys.Select(k => (id: new KeyId(x.Key, k.Id), key: k))))
            .Where(x => !_locked.Contains(x.id.Channel) && !_driven.Contains(x.id.Channel) && Editable(x.id.Channel, x.key.Time)).ToList();
        var interpolation = (CurveInterpolation)_preset;
        var mode = (HandleMode)_handleMode;
        targets = targets.Where(x => _applyHandles || x.key.Interpolation != interpolation).ToList();
        if (targets.Count == 0) { Status = I18n.T("graph.sg_preset_none"); return; }
        timeline.Snapshot();
        foreach (var x in targets)
        {
            if(!_applyHandles) Curves[x.id.Channel].SetInterpolation(x.key, interpolation);
            if (_applyHandles)
            {
                x.key.Mode = mode;
                if(x.key.Mode == HandleMode.Aligned)
                {
                    var curve = Curves[x.id.Channel];
                    bool right = x.key.Right.LengthSquared() > 0;
                    curve.SetHandle(x.key, right, new Vector2((float)x.key.Time, (float)x.key.Value) + (right ? x.key.Right : x.key.Left));
                }
            }
        }
        var owner = timeline.Tracks.FirstOrDefault(t => t.Shots.Contains(shot));
        if(owner != null && _applyHandles)
            foreach(var ch in targets.Select(x => x.id.Channel).Distinct())
            {
                // time channels are ordinary frame-domain F-curves; spatial channels use the node-run tangents
                var selected = targets.Where(x => x.id.Channel == ch).Select(x => x.key.Id).ToHashSet();
                if(IsTime(ch) || shot.Space.PreserveIntermediateKeys) { Curves[ch].RecalculateHandles(selected); continue; }
                var ids = targets.Where(x => x.id.Channel == ch && x.key.NodeId.HasValue).Select(x => x.key.NodeId!.Value).ToHashSet();
                SpatialTangents.Apply(shot.Space, owner.Keys.OrderBy(n => n.Frame).ToList(), Spatial(ch), ids, true);
            }
        // Applied automatic modes stay automatic, so later node edits keep refitting them within the
        // influence range; only Free/Aligned handles are frozen as hand-authored.
        if(_applyHandles)
            foreach(var target in targets) target.key.AutomaticTangent = target.key.Mode is not (HandleMode.Free or HandleMode.Aligned);
        shot.Space.Touch(); timeline.Touch(); Status = I18n.T("graph.sg_preset_ok", targets.Count);
    }

    private bool ApplyMoves(ShotCurves shot, CamTimeline timeline, List<(KeyId id, double frame, double value)> moves, bool gesture)
    {
        if (moves.Count == 0 || shot.EditorMode != CurveEditorMode.Advanced) return false;
        if(moves.Any(m => Resolve(shot, m.id) == null || _locked.Contains(m.id.Channel) || _driven.Contains(m.id.Channel) || !double.IsFinite(m.frame) || !double.IsFinite(m.value))) return false;
        var nodeTargets = new Dictionary<Guid, int>();
        foreach(var move in moves)
        {
            var key = Resolve(shot, move.id);
            if(key?.NodeId is Guid node)
            {
                int target = (int)Math.Round(gesture && _dragMap != null
                    ? FrozenFrame(move.id.Channel, move.frame) : ToFrame(move.id.Channel, move.frame));
                if(timeline.AllKeys.Any(k => k.Id == node && k.Frame != target)) nodeTargets[node] = target;
            }
        }
        moves = moves.Select(m => Resolve(shot, m.id)?.NodeId.HasValue == true ? (m.id, Resolve(shot, m.id)!.Time, m.value) : m).ToList();
        // path progress of the node path is fixed by the node positions: keep the value, only retime
        moves = moves.Select(m => ValueLocked(m.id) ? (m.id, m.frame, Resolve(shot, m.id)!.Value) : m).ToList();
        var moving = moves.Select(x => x.id).ToHashSet();
        foreach (var m in moves)
        {
            var key = Resolve(shot, m.id);
            if (key == null || _locked.Contains(m.id.Channel) || !Editable(m.id.Channel, key.Time)
                || !Editable(m.id.Channel, m.frame)
                || !double.IsFinite(m.value)
                || Curves[m.id.Channel].Keys.Any(k => k.Time == m.frame && !moving.Contains(new KeyId(m.id.Channel, k.Id))))
            { Status = I18n.T("graph.sg_move_invalid"); return false; }
        }
        if (moves.GroupBy(m => (m.id.Channel, m.frame)).Any(g => g.Count() > 1))
        { Status = I18n.T("graph.sg_move_dup"); return false; }
        // Free keys may be retimed by a bound-node move in this same channel. Edit them in a separate gesture.
        if(nodeTargets.Count > 0 && moves.Any(m => Resolve(shot, m.id)?.NodeId == null))
        { Status = I18n.T("graph.sg_move_mixed"); return false; }
        if(nodeTargets.Count > 0)
        {
            if(gesture) BeforeGesture(timeline); else timeline.Snapshot();
            if(!timeline.MoveNodes(nodeTargets)) { Status = I18n.T("graph.sg_move_retime_blocked"); return false; }
        }
        if (!moves.Any(m => Resolve(shot, m.id) is { } k && (k.Time != m.frame || k.Value != m.value))) return nodeTargets.Count > 0;
        if (gesture) BeforeGesture(timeline); else timeline.Snapshot();
        // Order by direction so group translations never collide with a selected key's old position.
        foreach (var group in moves.GroupBy(m => m.id.Channel))
        {
            bool forward = group.First().frame > Resolve(shot, group.First().id)!.Time;
            var ordered = forward ? group.OrderByDescending(m => Resolve(shot, m.id)!.Time) : group.OrderBy(m => Resolve(shot, m.id)!.Time);
            foreach (var m in ordered) Curves[m.id.Channel].MoveKey(Resolve(shot, m.id)!, m.frame, m.value);
            Curves[group.Key].Sort();
            // Only the moved nodes (plus the influence range) are refitted on the next sync.
            foreach (var m in group) if (Resolve(shot, m.id)?.NodeId is Guid node)
            {
                shot.Space.MarkTangentsDirty(node, timeline.PathInfluence);
                if (m.id.Channel == GraphChannel.Progress) shot.MarkFactorManual(node); // keep the hand-set curve position
            }
        }
        shot.Space.Touch(); timeline.Touch(); Status = I18n.T("graph.sg_move_ok"); return true;
    }

    private void BeforeGesture(CamTimeline timeline)
    { if (!_snapshot) { timeline.Snapshot(); _snapshot = true; } }
    public void EndGesture()
    { _gesture = Gesture.None; _snapshot = false; _handleKey = null; _anchors.Clear(); }
    private double EditTime(KeyId id, double time) => IsTime(id.Channel) && Resolve(_shot, id)?.NodeId == null ? time : Math.Round(time);
    private bool Editable(GraphChannel ch, double frame) => frame >= DomainStart(ch) && frame <= DomainEnd(ch);
    private CurveKey? Resolve(ShotCurves shot, KeyId id) => Curves.TryGetValue(id.Channel, out var curve)
        ? curve.Keys.FirstOrDefault(k => k.Id == id.Id) : null;
    private IEnumerable<(KeyId id, CurveKey key)> SelectedKeys(ShotCurves shot)
    {
        foreach (var id in _selected) if (Resolve(shot, id) is { } key) yield return (id, key);
    }
    private static string ChannelLabel(GraphChannel ch) => ch switch
    {
        GraphChannel.Progress => I18n.T("graph.ch.progress"), GraphChannel.OffsetX => I18n.T("graph.ch.offset_x"), GraphChannel.OffsetY => I18n.T("graph.ch.offset_y"), GraphChannel.OffsetZ => I18n.T("graph.ch.offset_z"),
        GraphChannel.Yaw => I18n.T("graph.ch.yaw"), GraphChannel.Pitch => I18n.T("graph.ch.pitch"), GraphChannel.Roll => I18n.T("graph.ch.roll"), GraphChannel.Fov => I18n.T("graph.ch.fov"),
        _ => I18n.T("graph.ch.unknown")
    };
    private static T Named<T>(T _, string name) where T : struct, Enum => Enum.Parse<T>(name);
    private double Scale(GraphChannel ch) => _normalize && _normalization.TryGetValue(ch, out var n) ? n.scale : 1;
    private double Display(GraphChannel ch, double value) => _normalize && _normalization.TryGetValue(ch, out var n) ? (value - n.center) / n.scale : value;
    private void UpdateNormalization()
    {
        _normalization.Clear();
        foreach (var ch in _visible)
        {
            if (!Curves.TryGetValue(ch, out var curve)) continue;
            var values = curve.Keys.Select(k => k.Value).Where(double.IsFinite).ToList();
            for (int i = 0; i <= 128; i++)
            {
                double v = curve.Evaluate(DomainStart(ch) + (DomainEnd(ch) - DomainStart(ch)) * i / 128);
                if (double.IsFinite(v)) values.Add(v);
            }
            if (values.Count > 0)
                _normalization[ch] = ((values.Min() + values.Max()) / 2, Math.Max((values.Max() - values.Min()) / 2, .000001));
        }
    }
    private void Fit(ShotCurves shot, bool selectedOnly)
    {
        var keys = Curves.Where(x => _visible.Contains(x.Key)).SelectMany(x => x.Value.Keys.Select(k => (ch: x.Key, key: k)))
            .Where(x => x.key.Time >= DomainStart(x.ch) && x.key.Time <= DomainEnd(x.ch) && (!selectedOnly || _selected.Contains(new KeyId(x.ch, x.key.Id))))
            .ToList();
        if (selectedOnly && keys.Count == 0) { Status = I18n.T("graph.sg_fit_sel_none"); return; }
        double a = selectedOnly ? keys.Min(x => ToFrame(x.ch, x.key.Time)) : shot.Start;
        double b = selectedOnly ? keys.Max(x => ToFrame(x.ch, x.key.Time)) : shot.End;
        double pad = Math.Max(2, (b - a) * .06);
        _left = a - pad; _span = Math.Max(4, b - a + 2 * pad);
        var values = keys.Select(x => Display(x.ch, x.key.Value)).Where(double.IsFinite).ToList();
        if (!selectedOnly)
            foreach (var ch in _visible)
                if (Curves.TryGetValue(ch, out var curve))
                    for (int i = 0; i <= 256; i++)
                    {
                        double value = Display(ch, curve.Evaluate(DomainStart(ch) + (DomainEnd(ch) - DomainStart(ch)) * i / 256));
                        if (double.IsFinite(value)) values.Add(value);
                    }
        double lo = values.Count > 0 ? values.Min() : -1;
        double hi = values.Count > 0 ? values.Max() : 1;
        double vp = Math.Max(.1, (hi - lo) * .12);
        _bottom = lo - vp; _height = Math.Max(.2, hi - lo + 2 * vp);
    }
    private static double NiceStep(double target, bool integer)
    {
        target = Math.Max(integer ? 1 : 1e-12, target);
        double power = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double n = target / power;
        double result = (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * power;
        return integer ? Math.Max(1, Math.Ceiling(result)) : result;
    }
    private static bool Inside(Vector2 p, Vector2 a, Vector2 b) => p.X >= a.X && p.Y >= a.Y && p.X <= b.X && p.Y <= b.Y;
    private static uint Color(GraphChannel ch) => Colors[Math.Clamp((int)ch, 0, Colors.Length - 1)];
    private static void Diamond(ImDrawListPtr dl, Vector2 p, float r, uint color)
    {
        var a = p + new Vector2(0, -r); var b = p + new Vector2(r, 0);
        var c = p + new Vector2(0, r); var d = p + new Vector2(-r, 0);
        dl.AddQuadFilled(a, b, c, d, color); dl.AddQuad(a, b, c, d, 0xFF101010);
    }
}
