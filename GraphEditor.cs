using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>One continuous shot in real frame/global progress coordinates. Never crosses hard cuts.</summary>
public sealed class GraphEditor
{
    public bool PlayheadChanged { get; private set; }
    public string Status { get; private set; } = "";
    private readonly SpatialGraphEditor spatial = new();
    private bool fit = true, snapshot;
    private Guid shotId;
    private readonly HashSet<CameraChannel> visible = [CameraChannel.Position];
    private readonly HashSet<CameraChannel> locked = [];
    private readonly HashSet<Guid> selected = [];
    private int interpolation, handleMode = 3, scope, linkSource;
    private double left, span = 100, bottom = -.1, height = 1.2;
    private Ref? drag;
    private bool dragHandle, dragRight, scrubbing, panning, blankClick;
    private Vector2 press, lastMouse;
    private Dictionary<Guid, int> nodeFrames = new();
    private int originalFrame;
    private double originalValue;
    private Vector2 originalHandle;
    private Vector2? box;
    private long selectionRevision = -1;
    private readonly record struct Ref(MotionInterval Leg, CameraChannel Channel, CurveKey Key);
    private readonly record struct Hit(Ref Item, bool Handle, bool Right, Vector2 Point);
    private static readonly uint[] Colors = [0xFF6666EF, 0xFF70CE70, 0xFFEF9870];

    public void Draw(ShotCurves shot, CamTimeline timeline, ShortcutBindings shortcuts)
    {
        int timing = shot.FollowTimingSignature(), count = shot.SeededNodes.Count;
        DrawGraph(shot, timeline, shortcuts);
        if(count == shot.SeededNodes.Count && timing != shot.FollowTimingSignature() && timeline.SyncCurveAnchorsFromEditor(shot))
            Status = "速度曲线已生效，曲线节点的时间轴位置已按实际经过帧同步。";
    }

    private void DrawGraph(ShotCurves shot, CamTimeline timeline, ShortcutBindings shortcuts)
    {
        PlayheadChanged = false;
        if(ImGui.Button(shot.EditorMode == CurveEditorMode.Advanced ? "高级曲线 · 当前" : "使用高级曲线"))
        { End(); spatial.EndGesture(); if(shot.EditorMode != CurveEditorMode.Advanced) { timeline.Snapshot(); shot.SetEditorMode(CurveEditorMode.Advanced); timeline.Touch(); } }
        ImGui.SameLine();
        if(ImGui.Button(shot.EditorMode == CurveEditorMode.Simple ? "简单曲线 · 当前" : "使用简单曲线"))
        { End(); spatial.EndGesture(); if(shot.EditorMode != CurveEditorMode.Simple) { timeline.Snapshot(); shot.SetEditorMode(CurveEditorMode.Simple); timeline.Touch(); } }
        ImGui.TextDisabled(shot.EditorMode == CurveEditorMode.Advanced ? "高级曲线生效：直接控制数值、速度和节点帧；简单曲线锁定。" : "简单曲线生效：高级曲线锁定且保留。切换不会重建高级数据。");
        if(shot.UsesTimeAnchors)
            ImGui.TextDisabled("曲线已锚定时间轴：编辑速度会同步曲线节点的经过帧；拖动曲线节点则按新锚点重新定时。");
        if(shot.EditorMode == CurveEditorMode.Advanced) { spatial.Draw(shot, timeline, shortcuts); Status = spatial.Status; return; }
        if(shotId != shot.Id) { shotId = shot.Id; selected.Clear(); fit = true; End(); selectionRevision = -1; }
        if(shot.Intervals.Count == 0) { ImGui.TextDisabled("此连续段只有一个节点，没有运动曲线。"); return; }
        bool linked = shot.Intervals.All(i => i.Linked);
        if(linked) { visible.Clear(); visible.Add(CameraChannel.Position); }
        IEnumerable<CameraChannel> channels = linked ? [CameraChannel.Position] : Enum.GetValues<CameraChannel>();
        var refs = channels.SelectMany(c => shot.Intervals.SelectMany(i => i.Curve(c).Keys.Select(k => new Ref(i, c, k)))).ToList();
        if(selectionRevision != timeline.SelectionRevision)
        {
            selected.Clear();
            foreach(var r in refs.Where(r => r.Key.NodeId.HasValue && timeline.SelectedNodes.Contains(r.Key.NodeId.Value))) selected.Add(r.Key.Id);
            selectionRevision = timeline.SelectionRevision;
        }
        selected.IntersectWith(refs.Select(r => r.Key.Id));
        ImGui.PushID("WholeShot" + shot.Id);
        ImGui.TextDisabled($"整条轨道的连续段 F{shot.Start}–F{shot.End}（切镜为恒定插值，不拆段） · 已选节点 {timeline.SelectedNodes.Count}");
        if(timeline.Tracks.FirstOrDefault(t => t.Shots.Contains(shot)) is { RotationDriven: true } driven)
            ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.9f, 1, 1), driven.FovDriven
                ? "旋转与 FoV 由追踪约束控制：这里的旋转 / FoV 节奏只影响节点自身的值，最终朝向和 FoV 由约束决定。"
                : "旋转由追踪约束控制：这里的旋转节奏只影响节点自身的旋转，最终朝向由约束决定。");
        foreach(var c in channels)
        {
            ImGui.PushID((int)c);
            bool show = visible.Contains(c), isLocked = locked.Contains(c);
            if(ImGui.Checkbox(linked ? "共享进度" : Label(c), ref show)) { if(show) visible.Add(c); else visible.Remove(c); }
            ImGui.SameLine();
            if(ImGui.Checkbox("锁定", ref isLocked)) { if(isLocked) locked.Add(c); else locked.Remove(c); End(); }
            ImGui.SameLine(); ImGui.PopID();
        }
        ImGui.NewLine();
        if(linked)
        {
            ImGui.BeginDisabled(locked.Count > 0);
            if(ImGui.Button("解除联动")) { End(); timeline.Snapshot(); foreach(var i in shot.Intervals) i.Unlink(); visible.UnionWith(Enum.GetValues<CameraChannel>()); timeline.Touch(); }
            ImGui.EndDisabled();
        }
        else
        {
            ImGui.SetNextItemWidth(100); ImGui.Combo("联动来源", ref linkSource, "位置\0旋转\0FoV\0"); ImGui.SameLine();
            ImGui.BeginDisabled(locked.Count > 0);
            if(ImGui.Button("联动并替换其他节奏"))
            { End(); timeline.Snapshot(); foreach(var i in shot.Intervals) { i.Link((CameraChannel)linkSource); i.MarkManual(); } timeline.Touch(); }
            ImGui.EndDisabled();
            ImGui.TextDisabled("联动会用来源替换另外两组节奏，可撤销。");
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(locked.Count > 0);
        if(ImGui.Button("恢复按时间平滑")) { End(); timeline.Snapshot(); SmoothTiming.Apply(shot, true); shot.ActivateSimpleTiming(); timeline.Touch(); Status = "已恢复整段按时间平滑：首尾缓入缓出，中间节点平滑通过。"; }
        ImGui.EndDisabled();
        ImGui.SameLine(); if(ImGui.Button("适配整段")) fit = true;
        ImGui.SetNextItemWidth(100); ImGui.Combo("插值", ref interpolation, "贝塞尔\0线性\0恒定\0"); ImGui.SameLine();
        ImGui.SetNextItemWidth(100); ImGui.Combo("范围", ref scope, "所选关键帧\0可见时间组\0整段\0"); ImGui.SameLine();
        if(ImGui.Button("应用插值")) Apply(shot, timeline, refs, false);
        ImGui.BeginDisabled(interpolation != 0);
        ImGui.SetNextItemWidth(120); ImGui.Combo("手柄模式", ref handleMode, "自动钳制\0自动\0对齐\0自由\0向量\0"); ImGui.SameLine();
        if(ImGui.Button("应用手柄")) Apply(shot, timeline, refs, true);
        ImGui.EndDisabled();
        ImGui.TextDisabled("贝塞尔只是一种插值；手柄模式独立设置。默认节奏按整段计算，不在每个节点重新缓入缓出。");
        int cursor = (int)Math.Round(timeline.Playhead);
        ImGui.SetNextItemWidth(90); if(ImGui.InputInt("播放帧", ref cursor)) { timeline.Playhead = Math.Clamp(cursor, 0, timeline.Length); PlayheadChanged = true; }
        ImGui.SameLine(); if(ImGui.Button("添加关键帧/相机节点"))
        {
            if(locked.Count > 0) Status = "添加节点会同步全部通道，请先解除锁定。";
            else try { timeline.AddSampledNode(shot, cursor); selectionRevision = -1; }
                catch(InvalidOperationException ex) { Status = ex.Message; }
            ImGui.PopID(); return;
        }
        ImGui.SameLine(); if(ImGui.Button("删除所选节点")) { Delete(timeline, refs); ImGui.PopID(); return; }
        var primary = refs.FirstOrDefault(r => selected.Contains(r.Key.Id));
        if(primary.Key != null)
        {
            int f = primary.Key.Frame; double value = Global(shot, primary, primary.Key.Value) * 100;
            ImGui.SetNextItemWidth(90); bool changed = ImGui.InputInt("关键帧", ref f, 1, 10, "%d", ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine(); ImGui.BeginDisabled(primary.Key.NodeId.HasValue); ImGui.SetNextItemWidth(110);
            bool valueChanged = ImGui.InputDouble("整段进度 %", ref value, 0, 0, "%.4f", ImGuiInputTextFlags.EnterReturnsTrue); ImGui.EndDisabled();
            if(changed || valueChanged) Move(shot, timeline, primary, f, Local(shot, primary, value / 100), false);
        }
        var origin = ImGui.GetCursorScreenPos(); float width = Math.Max(300, ImGui.GetContentRegionAvail().X), h = 340;
        var plot = origin + new Vector2(0, 24); var end = origin + new Vector2(width, h);
        if(fit) { left = shot.Start - Math.Max(2, (shot.End - shot.Start) * .04); span = Math.Max(4, (shot.End - shot.Start) * 1.08); bottom = -.1; height = 1.2; fit = false; }
        float X(double f) => origin.X + (float)((f - left) / span * width);
        float Y(double v) => end.Y - (float)((v - bottom) / height * (h - 24));
        double F(float x) => left + (x - origin.X) / width * span;
        double V(float y) => bottom + (end.Y - y) / (h - 24) * height;
        ImGui.InvisibleButton("##shotcanvas", new Vector2(width, h), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered(); var io = ImGui.GetIO(); var mouse = io.MousePos;
        if(hovered && io.MouseWheel != 0)
        {
            ViewportMath.Wheel(ref left, ref span, ref bottom, ref height, io.MouseWheel,
                shortcuts.Held(Shortcut.ZoomX) || shortcuts.Held(Shortcut.ZoomY),
                shortcuts.Held(Shortcut.ScrollY) || shortcuts.Held(Shortcut.ZoomY), (mouse.X - origin.X) / width, (end.Y - mouse.Y) / (h - 24));
        }
        var dl = ImGui.GetWindowDrawList(); dl.PushClipRect(origin, end, true); dl.AddRectFilled(origin, end, 0xFF202020);
        double step = Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(span / 10))));
        for(double f = Math.Ceiling(left / step) * step; f <= left + span; f += step)
        { dl.AddLine(new(X(f), plot.Y), new(X(f), end.Y), 0x304F4F4F); dl.AddText(new(X(f) + 2, origin.Y + 2), 0xFFAAAAAA, f.ToString("0")); }
        for(int n = 0; n <= 4; n++) { float y = Y(n / 4.0); dl.AddLine(new(origin.X, y), new(end.X, y), 0x304F4F4F); dl.AddText(new(origin.X + 2, y), 0xFF888888, $"{n * 25}%"); }
        foreach(var hold in shot.Holds) dl.AddRectFilled(new(X(hold.start), plot.Y), new(X(hold.end), end.Y), 0x3040B0E0);
        Hit? hit = null; float nearest = 9;
        void Candidate(Hit item) { float d = Vector2.Distance(item.Point, mouse); if(hovered && d < nearest) { nearest = d; hit = item; } }
        foreach(var c in channels.Where(visible.Contains))
        {
            uint color = Colors[(int)c];
            var drawnNodes = new HashSet<Guid>();
            foreach(var leg in shot.Intervals)
            {
                var curve = leg.Curve(c); var dummy = new Ref(leg, c, curve.Keys[0]);
                Vector2? previous = null;
                int samples = Math.Clamp((int)((leg.End - leg.Start) / span * width), 12, 1600);
                for(int j = 0; j <= samples; j++)
                {
                    double f = leg.Start + (leg.End - leg.Start) * j / (double)samples;
                    var p = new Vector2(X(f), Y(Global(shot, dummy, leg.Evaluate(c, f))));
                    if(previous is Vector2 p0) dl.AddLine(p0, p, color, 2); previous = p;
                }
                curve.Sort();
                foreach(var key in curve.Keys)
                {
                    var r = new Ref(leg, c, key); var p = new Vector2(X(key.Frame), Y(Global(shot, r, key.Value)));
                    bool isSelected = selected.Contains(key.Id) || key.NodeId is Guid node && timeline.SelectedNodes.Contains(node);
                    if(!key.NodeId.HasValue || drawnNodes.Add(key.NodeId.Value))
                    {
                        dl.AddCircleFilled(p, key.NodeId.HasValue ? 5 : 4, isSelected ? 0xFF00FFFF : color);
                        Candidate(new(r, false, false, p));
                    }
                    if(isSelected)
                        foreach(bool right in new[] { false, true })
                        {
                            int index = curve.Keys.IndexOf(key);
                            if(right ? index + 1 >= curve.Keys.Count || key.Interpolation != CurveInterpolation.Bezier : index == 0 || curve.Keys[index - 1].Interpolation != CurveInterpolation.Bezier) continue;
                            var point = curve.Handle(key, right); var hp = new Vector2(X(point.X), Y(Global(shot, r, point.Y)));
                            dl.AddLine(p, hp, 0xFF888888); dl.AddCircleFilled(hp, 4, 0xFFFFCC77); Candidate(new(r, true, right, hp));
                        }
                }
            }
        }
        dl.AddLine(new(X(timeline.Playhead), origin.Y), new(X(timeline.Playhead), end.Y), 0xFF5555FF, 2);
        if(box is Vector2 b0) dl.AddRect(Vector2.Min(b0, mouse), Vector2.Max(b0, mouse), 0xFFFFFF55);
        dl.PopClipRect();
        if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) { panning = true; lastMouse = mouse; }
        if(panning && (ImGui.IsMouseDown(ImGuiMouseButton.Middle) || ImGui.IsMouseDown(ImGuiMouseButton.Left)))
        { left -= (mouse.X - lastMouse.X) / width * span; bottom += (mouse.Y - lastMouse.Y) / (h - 24) * height; lastMouse = mouse; }
        if(!ImGui.IsMouseDown(ImGuiMouseButton.Middle) && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) panning = false;
        if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            snapshot = false; press = mouse;
            if(hit is Hit target && !locked.Contains(target.Item.Channel))
            {
                drag = target.Item; dragHandle = target.Handle; dragRight = target.Right;
                if(!target.Handle)
                {
                    bool add = shortcuts.Held(Shortcut.Select) || shortcuts.Held(Shortcut.ExtendSelection);
                    if(!add && !selected.Contains(target.Item.Key.Id)) { selected.Clear(); timeline.SelectNodes([]); }
                    if(add && !selected.Add(target.Item.Key.Id)) selected.Remove(target.Item.Key.Id); else selected.Add(target.Item.Key.Id);
                    if(target.Item.Key.NodeId is Guid id)
                    {
                        if(add) timeline.ToggleNode(id); else if(!timeline.SelectedNodes.Contains(id)) timeline.SelectNodes([id]);
                        foreach(var r in refs.Where(r => r.Key.NodeId == id)) selected.Add(r.Key.Id);
                    }
                    selectionRevision = timeline.SelectionRevision;
                }
                nodeFrames = timeline.AllKeys.Where(k => timeline.SelectedNodes.Contains(k.Id) && shot.SeededNodes.Contains(k.Id)).ToDictionary(k => k.Id, k => k.Frame);
                originalFrame = target.Item.Key.Frame; originalValue = target.Item.Key.Value;
                originalHandle = target.Item.Leg.Curve(target.Item.Channel).Handle(target.Item.Key, target.Right);
            }
            else if(shortcuts.Held(Shortcut.Box)) box = mouse;
            else if(mouse.Y < plot.Y) { scrubbing = true; timeline.Playhead = (float)Math.Clamp(Math.Round(F(mouse.X)), 0, timeline.Length); PlayheadChanged = true; }
            else { panning = true; blankClick = true; lastMouse = mouse; }
        }
        if(ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if(drag is Ref r && Vector2.DistanceSquared(mouse, press) > 2)
            {
                if(dragHandle)
                {
                    Before(timeline); shot.ActivateSimpleTiming(); var curve = r.Leg.Curve(r.Channel);
                    double value = Local(shot, r, Global(shot, r, originalHandle.Y) + (press.Y - mouse.Y) / (h - 24) * height);
                    curve.SetHandle(r.Key, dragRight, new Vector2(originalHandle.X + (mouse.X - press.X) / width * (float)span, (float)value));
                    ProgressCurve.Constrain(curve, r.Leg.Start, r.Leg.End); r.Leg.MarkManual(); timeline.Touch();
                }
                else
                {
                    int target = originalFrame + (int)Math.Round((mouse.X - press.X) / width * span);
                    double value = originalValue + (V(mouse.Y) - V(press.Y)) / Weight(shot, r.Leg, r.Channel).size;
                    Move(shot, timeline, r, target, value, true);
                }
            }
            if(scrubbing) { timeline.Playhead = (float)Math.Clamp(Math.Round(F(mouse.X)), 0, timeline.Length); PlayheadChanged = true; }
        }
        else
        {
            if(box is Vector2 a)
            {
                var lo = Vector2.Min(a, mouse); var hi = Vector2.Max(a, mouse);
                var ids = new List<Guid>();
                foreach(var r in refs.Where(r => visible.Contains(r.Channel)))
                { var p = new Vector2(X(r.Key.Frame), Y(Global(shot, r, r.Key.Value))); if(p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y) { selected.Add(r.Key.Id); if(r.Key.NodeId is Guid id) ids.Add(id); } }
                timeline.SelectNodes(ids, true); selectionRevision = timeline.SelectionRevision;
            }
            // short left click on empty plot space (no drag) clears the selection, like the camera timeline
            if(blankClick && Vector2.DistanceSquared(mouse, press) <= 9)
            { selected.Clear(); timeline.SelectNodes([]); selectionRevision = timeline.SelectionRevision; }
            blankClick = false;
            End();
        }
        if(hovered && hit is Hit hover) ImGui.SetTooltip($"{Label(hover.Item.Channel)} · F{hover.Item.Key.Frame} · 整段进度 {Global(shot, hover.Item, hover.Item.Key.Value) * 100:0.###}%");
        ImGui.TextWrapped(Status);
        ImGui.PopID();
    }

    private void Move(ShotCurves shot, CamTimeline timeline, Ref item, int frame, double value, bool gesture)
    {
        if(locked.Contains(item.Channel)) return;
        if(item.Key.NodeId is Guid id)
        {
            int delta = frame - (gesture ? originalFrame : item.Key.Frame);
            var starts = gesture ? nodeFrames : timeline.AllKeys.Where(k => timeline.SelectedNodes.Contains(k.Id) && shot.SeededNodes.Contains(k.Id)).ToDictionary(k => k.Id, k => k.Frame);
            if(starts.Count == 0) starts = new() { [id] = item.Key.Frame };
            var targets = starts.ToDictionary(p => p.Key, p => p.Value + delta);
            if(targets.All(p => timeline.AllKeys.Any(k => k.Id == p.Key && k.Frame == p.Value))) return;
            if(gesture) Before(timeline); else timeline.Snapshot();
            if(!timeline.MoveNodes(targets)) Status = "移动被拒绝：节点顺序、时间范围或进度键碰撞。";
            return;
        }
        var curve = item.Leg.Curve(item.Channel);
        var previous = curve.Keys.Where(k => k.Frame < item.Key.Frame).LastOrDefault();
        var next = curve.Keys.FirstOrDefault(k => k.Frame > item.Key.Frame);
        if(previous == null || next == null || frame <= previous.Frame || frame >= next.Frame || !double.IsFinite(value)) return;
        value = Math.Clamp(value, previous.Value, next.Value);
        if(frame == item.Key.Frame && value == item.Key.Value) return;
        if(gesture) Before(timeline); else timeline.Snapshot();
        curve.MoveKey(item.Key, frame, value); ProgressCurve.Constrain(curve, item.Leg.Start, item.Leg.End); item.Leg.MarkManual(); timeline.Touch();
    }

    private void Apply(ShotCurves shot, CamTimeline timeline, List<Ref> refs, bool handles)
    {
        var targets = refs.Where(r => !locked.Contains(r.Channel) && (scope == 2 || scope == 1 && visible.Contains(r.Channel) || scope == 0 && selected.Contains(r.Key.Id))).DistinctBy(r => r.Key).ToList();
        if(targets.Count == 0) return;
        End(); timeline.Snapshot(); shot.ActivateSimpleTiming();
        foreach(var r in targets)
        {
            if(!handles) r.Key.Interpolation = (CurveInterpolation)interpolation;
            else
            {
                r.Key.Interpolation = CurveInterpolation.Bezier; r.Key.Mode = (HandleMode)handleMode;
                var curve = r.Leg.Curve(r.Channel);
                if(r.Key.Mode is HandleMode.Auto or HandleMode.AutoClamped or HandleMode.Vector)
                { var copy = curve.Clone(); copy.RecalculateHandles(); var key = copy.Keys.First(k => k.Id == r.Key.Id); r.Key.Left = key.Left; r.Key.Right = key.Right; }
                else if(r.Key.Mode == HandleMode.Aligned)
                    curve.SetHandle(r.Key, true, new Vector2(r.Key.Frame, (float)r.Key.Value) + r.Key.Right);
            }
            r.Leg.MarkManual();
        }
        foreach(var r in targets.DistinctBy(r => r.Leg.Curve(r.Channel))) ProgressCurve.Constrain(r.Leg.Curve(r.Channel), r.Leg.Start, r.Leg.End);
        timeline.Touch();
    }
    private void Delete(CamTimeline timeline, List<Ref> refs)
    {
        if(locked.Count > 0) { Status = "删除节点会同步全部通道，请先解除锁定。"; return; }
        var ids = refs.Where(r => selected.Contains(r.Key.Id) && r.Key.NodeId.HasValue).Select(r => r.Key.NodeId!.Value)
            .Concat(timeline.SelectedNodes).Distinct().ToArray();
        End(); timeline.DeleteNodes(ids); selected.Clear(); selectionRevision = -1;
    }
    private static (double start, double size) Weight(ShotCurves shot, MotionInterval leg, CameraChannel channel)
    {
        var weights = shot.Intervals.Select(i => channel == CameraChannel.Position ? SmoothTiming.Distance(i) : Math.Abs(i.Ranges[channel].end - i.Ranges[channel].start)).ToArray();
        double total = weights.Sum();
        if(total <= 1e-12) { Array.Fill(weights, 1); total = weights.Length; }
        int index = shot.Intervals.IndexOf(leg); double start = weights.Take(index).Sum() / total;
        return (start, Math.Max(1e-12, weights[index] / total));
    }
    private static double Global(ShotCurves shot, Ref item, double local) { var w = Weight(shot, item.Leg, item.Channel); return w.start + local * w.size; }
    private static double Local(ShotCurves shot, Ref item, double global) { var w = Weight(shot, item.Leg, item.Channel); return (global - w.start) / w.size; }
    private void Before(CamTimeline timeline) { if(!snapshot) { timeline.Snapshot(); snapshot = true; } }
    private void End() { drag = null; scrubbing = false; box = null; snapshot = false; }
    private static string Label(CameraChannel channel) => channel switch { CameraChannel.Position => "位置", CameraChannel.Rotation => "旋转", _ => "FoV" };
}
