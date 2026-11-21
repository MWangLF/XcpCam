using Dalamud.Bindings.ImGui;
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
    private const float HeaderWidth = 150, RulerHeight = 24;
    private enum Gesture { None, Keys, Handle, Box, Scrub, Pan }
    private readonly record struct KeyId(GraphChannel Channel, Guid Id);
    private readonly record struct Anchor(KeyId Id, double Value, double DisplayFrame);
    private ShotCurves? _dragMap;
    private double _handleDisplayFrame;
    private double FrozenParameter(GraphChannel channel, double frame) => IsTime(channel) ? frame : SpatialFrameMap.Parameter(_dragMap ?? _shot, Spatial(channel), frame);
    private double FrozenFrame(GraphChannel channel, double parameter) => IsTime(channel) ? parameter : SpatialFrameMap.Frame(_dragMap ?? _shot, Spatial(channel), parameter);
    private readonly record struct Hit(KeyId Id, bool Handle, bool Right, Vector2 Position);

    public void Draw(ShotCurves shot, CamTimeline timeline, ShortcutBindings shortcuts)
    {
        if(shot.EditorMode != CurveEditorMode.Advanced) { EndGesture(); ImGui.TextDisabled("高级曲线已锁定，请先切换模式。"); return; }
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
        DrawToolbar(shot, timeline);
        if(timeline.Revision != beforeToolbar) { ImGui.PopID(); return; }
        DrawInspector(shot, timeline);

        var start = ImGui.GetCursorScreenPos();
        float width = MathF.Max(210, ImGui.GetContentRegionAvail().X - HeaderWidth);
        const float graphHeight = 340;
        DrawChannels(start, shortcuts);
        var origin = start + new Vector2(HeaderWidth, 0);
        var plot = origin + new Vector2(0, RulerHeight);
        var end = origin + new Vector2(width, graphHeight);
        float plotHeight = graphHeight - RulerHeight;
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
        dl.AddRectFilled(origin, end, 0xFF202020);
        dl.AddRectFilled(origin, plot, 0xFF303030);
        double step = NiceStep(_span * 65 / width, true);
        double first = Math.Ceiling(_left / step) * step;
        for (double f = first; f <= _left + _span; f += step)
        {
            float x = X(f);
            dl.AddLine(new Vector2(x, plot.Y - 6), new Vector2(x, end.Y), 0x284F4F4F);
            dl.AddText(new Vector2(x + 3, origin.Y + 3), 0xFFC0C0C0, f.ToString("0"));
        }
        dl.PushClipRect(plot, end, true);
        double vs = NiceStep(_height * 50 / plotHeight, false);
        for (double v = Math.Ceiling(_bottom / vs) * vs; v <= _bottom + _height; v += vs)
        {
            float y = end.Y - (float)((v - _bottom) / _height * plotHeight);
            dl.AddLine(new Vector2(plot.X, y), new Vector2(end.X, y), 0x254F4F4F);
            dl.AddText(new Vector2(plot.X + 3, y + 2), 0xFF888888, v.ToString("0.###"));
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
            dl.AddText(new Vector2(x + 3, plot.Y + 20), 0xFFAAAAFF, boundary == shot.Start ? "起始帧" : "结束帧");
        }
        foreach (var channel in Enum.GetValues<CameraChannel>())
        {
            float x = X(timeline.Playhead);
            uint color = channel == CameraChannel.Position ? 0xFF70CE70 : channel == CameraChannel.Rotation ? 0xFFE080D0 : 0xFF60DFDF;
            string label = channel == CameraChannel.Position ? "位置" : channel == CameraChannel.Rotation ? "旋转" : "FoV";
            dl.AddLine(new Vector2(x, plot.Y), new Vector2(x, end.Y), color, 1);
            dl.AddText(new Vector2(x + 3, plot.Y + 4 + (int)channel * 16), color, label + "映射");
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
                                Status = "此通道由追踪约束控制，手柄不能编辑；关键帧仍可横向拖动重定时节点。";
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
                        else Status = "通道已锁定。";
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
                                shot.Space.Touch(); timeline.Touch(); Status = "已编辑手柄（支持小数路径参数）。";
                            }
                        }
                        else Status = "无法编辑手柄：通道锁定或数值无效。";
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
            ImGui.SetTooltip($"{ChannelLabel(hover.Id.Channel)} | 帧 {ToFrame(hover.Id.Channel, hoveredKey.Time):0.###} | 数值 {hoveredKey.Value:G8}" +
                (_locked.Contains(hover.Id.Channel) ? " | 已锁定" : ""));
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 4));
        ImGui.TextDisabled(Status);
        ImGui.PopID();
    }

    private void DrawToolbar(ShotCurves shot, CamTimeline timeline)
    {
        ImGui.TextUnformatted($"{shot.Name} · 帧数 → {ChannelLabel(_active)} 数值 | 已选 {_selected.Count} 个关键帧");
        if (ImGui.Button("适配全部")) { UpdateNormalization(); Fit(shot, false); }
        ImGui.SameLine();
        if (ImGui.Button("适配所选")) { UpdateNormalization(); Fit(shot, true); }
        ImGui.SameLine();
        if (ImGui.Checkbox("归一化可见通道", ref _normalize)) { UpdateNormalization(); Fit(shot, false); }
        ImGui.TextDisabled("横轴：实际帧。路径进度 = 沿路径的长度比例（0–1，同 Blender 跟随路径的偏移系数）；位置偏移叠加在路径点上（世界坐标米）；旋转/FoV为度。");
        ImGui.TextDisabled(shot.IsFollowing ? "当前轨道跟随曲线：路径进度可上下拖动改变节点在曲线上的位置，手柄调整快慢。"
            : "路径形状在游戏内用曲线手柄编辑；这里的路径进度数值由节点位置决定，只能用手柄调整快慢。");
        if(_driven.Count > 0)
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), _driven.Contains(GraphChannel.Fov)
                ? "偏航 / 俯仰 / 滚转与 FoV 由追踪约束控制：数值和手柄不能编辑，曲线显示的是节点自身的值。"
                : "偏航 / 俯仰 / 滚转由追踪约束控制：数值和手柄不能编辑，曲线显示的是节点自身的值。");
        int cursor = _cursor;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("帧数游标", ref cursor)) _cursor = Math.Clamp(cursor, shot.Start, shot.End);
        if (ImGui.Button("在游标帧添加镜头节点")) Add(shot, timeline);
        ImGui.SameLine();
        if (ImGui.Button("插入可见通道键")) InsertChannelKeys(shot, timeline);
        ImGui.SameLine();
        if (ImGui.Button("删除所选")) Delete(shot, timeline);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("跳过锁定通道；空间端点与中间关键帧均可编辑。");
        ImGui.SetNextItemWidth(180);
        ImGui.Combo("插值", ref _preset, "贝塞尔\0线性\0恒定\0");
        ImGui.SameLine(); ImGui.SetNextItemWidth(150);
        ImGui.Combo("作用范围", ref _scope, "所选关键帧\0当前通道\0当前镜头全部通道\0");
        ImGui.SameLine();
        if (ImGui.Button("应用插值")) { _applyHandles = false; ApplyPreset(shot, timeline); }
        ImGui.BeginDisabled(_preset != 0);
        ImGui.SetNextItemWidth(140); ImGui.Combo("手柄模式", ref _handleMode, "自动钳制\0自动\0对齐\0自由\0向量\0"); ImGui.SameLine();
        if(ImGui.Button("应用手柄")) { _applyHandles = true; ApplyPreset(shot, timeline); }
        ImGui.EndDisabled();
        ImGui.TextDisabled("插值作用于关键帧到下一关键帧的出段；贝塞尔手柄模式同时影响该关键帧两侧。锁定通道不修改。");
    }

    private void DrawChannels(Vector2 start, ShortcutBindings shortcuts)
    {
        ImGui.SetCursorScreenPos(start);
        ImGui.TextDisabled("通道  显示 / 锁定");
        int i = 0;
        foreach (var ch in DisplayChannels)
        {
            ImGui.PushID(ch.ToString());
            ImGui.SetCursorScreenPos(start + new Vector2(0, 27 + i++ * 36));
            bool visible = _visible.Contains(ch), locked = _locked.Contains(ch);
            if (ImGui.Checkbox("##visible", ref visible))
            { if (visible) _visible.Add(ch); else _visible.Remove(ch); EndGesture(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("显示或隐藏此通道；隐藏不等于锁定，批量预设和删除仍按作用范围处理。");
            ImGui.SameLine(0, 4);
            if (ImGui.Checkbox("##lock", ref locked))
            { if (locked) _locked.Add(ch); else _locked.Remove(ch); EndGesture(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("锁定：禁止曲线编辑器修改此通道（包括批量操作）。");
            ImGui.SameLine(0, 4);
            if (ImGui.Selectable(ChannelLabel(ch), _active == ch, ImGuiSelectableFlags.None, new Vector2(88, 0)))
            {
                _active = ch;
                if (!shortcuts.Held(Shortcut.ExtendSelection)) _visible.Clear();
                _visible.Add(ch); EndGesture(); _fit = true;
            }
            ImGui.PopID();
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0, 295));
        if (ImGui.SmallButton("显示全部")) { foreach (var ch in Channels) _visible.Add(ch); EndGesture(); }
    }

    private void DrawInspector(ShotCurves shot, CamTimeline timeline)
    {
        if (_primary is not { } id || Resolve(shot, id) is not { } key)
        { ImGui.TextDisabled("选择关键帧编辑所在帧与通道数值；节点锚点横移会重定时。"); return; }
        bool editable = !_locked.Contains(id.Channel) && !_driven.Contains(id.Channel) && Editable(id.Channel, key.Time) && _gesture == Gesture.None;
        ImGui.BeginDisabled(!editable);
        double frame = ToFrame(id.Channel, key.Time);
        double value = key.Value;
        ImGui.SetNextItemWidth(110);
        bool moved = ImGui.InputDouble("帧数", ref frame, 0, 0, "%.6g", ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine(); ImGui.SetNextItemWidth(155);
        bool changed = ImGui.InputDouble("原始值", ref value, 0, 0, "%.3f", ImGuiInputTextFlags.EnterReturnsTrue);
        if ((moved || changed) && editable) ApplyMoves(shot, timeline, [(id, moved ? EditTime(id, ToParameter(id.Channel, frame)) : key.Time, value)], false);
        ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextDisabled(ChannelLabel(id.Channel));
    }

    private void Add(ShotCurves shot, CamTimeline timeline)
    {
        EndGesture();
        if(_locked.Count > 0) { Status = "添加节点会同步全部通道，请先解除通道锁定。"; return; }
        try
        {
            var node = timeline.AddSampledNode(shot, _cursor);
            _selectionRevision = -1;
            Status = $"已选择帧 {node.Frame} 的相机节点；所有通道关键帧与节点同步。";
        }
        catch(InvalidOperationException ex) { Status = ex.Message; }
    }

    private void InsertChannelKeys(ShotCurves shot, CamTimeline timeline)
    {
        EndGesture();
        var additions = _visible.Where(ch => (ch != GraphChannel.Progress || shot.Space.PreserveIntermediateKeys && !shot.IsFollowing) && !_locked.Contains(ch) && !_driven.Contains(ch))
            .Select(ch => (ch, time: IsTime(ch) ? _cursor : Math.Round(ToParameter(ch, _cursor))))
            .Where(x => Editable(x.ch, x.time) && Curves[x.ch].Keys.All(k => k.Time != x.time)).ToArray();
        if(additions.Length == 0) { Status = "当前游标帧没有可插入的可见通道。"; return; }
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
        Status = $"已保形插入 {additions.Length} 个通道键。";
    }

    private void Delete(ShotCurves shot, CamTimeline timeline)
    {
        EndGesture();
        var targets = SelectedKeys(shot).Where(x => !_locked.Contains(x.id.Channel) && !_driven.Contains(x.id.Channel) && Editable(x.id.Channel, x.key.Time)).ToList();
        var ids = targets.Where(x => x.key.NodeId.HasValue).Select(x => x.key.NodeId!.Value)
            .Concat(timeline.SelectedNodes.Where(shot.SeededNodes.Contains)).Distinct().ToArray();
        if(ids.Length > 0 && _locked.Count > 0) { Status = "删除节点会同步全部通道，请先解除通道锁定。"; return; }
        var independent = targets.Where(x => !x.key.NodeId.HasValue).ToArray();
        if(ids.Length == 0 && independent.Length == 0) return;
        timeline.Snapshot();
        foreach(var (id, key) in independent) Curves[id.Channel].Keys.Remove(key);
        foreach(var ch in independent.Select(x => x.id.Channel).Distinct()) Curves[ch].RecalculateHandles();
        if(ids.Length > 0) timeline.DeleteNodes(ids, snapshot: false);
        else { shot.Space.Touch(); timeline.Touch(); }
        _selected.Clear(); _primary = null;
        Status = $"已删除 {ids.Length} 个镜头节点、{independent.Length} 个中间通道键。";
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
        if (targets.Count == 0) { Status = "预设：指定范围内没有可修改的关键帧。"; return; }
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
        shot.Space.Touch(); timeline.Touch(); Status = $"已对 {targets.Count} 个关键帧应用预设；自动手柄只考虑所选连续节点组，选区外不修改。";
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
            { Status = "无法移动：路径参数重复、通道锁定或超出镜头参数范围。"; return false; }
        }
        if (moves.GroupBy(m => (m.id.Channel, m.frame)).Any(g => g.Count() > 1))
        { Status = "无法移动：目标路径参数重复。"; return false; }
        // Free keys may be retimed by a bound-node move in this same channel. Edit them in a separate gesture.
        if(nodeTargets.Count > 0 && moves.Any(m => Resolve(shot, m.id)?.NodeId == null))
        { Status = "请分别拖动镜头节点与独立通道键的时间。"; return false; }
        if(nodeTargets.Count > 0)
        {
            if(gesture) BeforeGesture(timeline); else timeline.Snapshot();
            if(!timeline.MoveNodes(nodeTargets)) { Status = "节点重定时被拒绝：碰撞或越过邻居。"; return false; }
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
        shot.Space.Touch(); timeline.Touch(); Status = "已编辑关键帧。"; return true;
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
        GraphChannel.Progress => "路径进度", GraphChannel.OffsetX => "偏移 X", GraphChannel.OffsetY => "偏移 Y", GraphChannel.OffsetZ => "偏移 Z",
        GraphChannel.Yaw => "偏航", GraphChannel.Pitch => "俯仰", GraphChannel.Roll => "滚转", GraphChannel.Fov => "FoV",
        _ => "未知通道"
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
        if (selectedOnly && keys.Count == 0) { Status = "没有可用于适配视图的可见所选关键帧。"; return; }
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
