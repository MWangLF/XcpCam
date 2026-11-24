using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

    public void Draw(ShotCurves shot, CamTimeline timeline, ShortcutBindings shortcuts, bool brioTheme = true)
    {
        int timing = shot.FollowTimingSignature(), count = shot.SeededNodes.Count;
        DrawGraph(shot, timeline, shortcuts, brioTheme);
        if(count == shot.SeededNodes.Count && timing != shot.FollowTimingSignature() && timeline.SyncCurveAnchorsFromEditor(shot))
            Status = I18n.T("graph.status_anchors_synced");
    }

    private void DrawGraph(ShotCurves shot, CamTimeline timeline, ShortcutBindings shortcuts, bool brioTheme)
    {
        PlayheadChanged = false;
        float s = XcpStyle.Scale;

        int editorMode = shot.EditorMode == CurveEditorMode.Advanced ? 0 : 1;
        string modeCh = I18n.T("graph.mode.channel"), modeTm = I18n.T("graph.mode.timing");
        float modeW = MathF.Max(190f * s, MathF.Max(ImGui.CalcTextSize(modeCh).X, ImGui.CalcTextSize(modeTm).X) * 2f + 28f * s);
        if(UiWidgets.Segmented("ge_mode", ref editorMode, [modeCh, modeTm], brioTheme, modeW,
            [I18n.T("graph.mode.channel_tip"), I18n.T("graph.mode.timing_tip")]))
        {
            End(); spatial.EndGesture();
            var targetMode = editorMode == 0 ? CurveEditorMode.Advanced : CurveEditorMode.Simple;
            if(shot.EditorMode != targetMode) { timeline.Snapshot(); shot.SetEditorMode(targetMode); timeline.Touch(); }
        }
        ImGui.SameLine();
        UiWidgets.HelpMarker(I18n.T("graph.mode_help"));

        if(shot.UsesTimeAnchors)
        {
            ImGui.SameLine();
            UiWidgets.Chip("ge_anchor_badge", I18n.T("graph.badge_anchored"), XcpStyle.Info,
                I18n.T("graph.badge_anchored_tip"));
        }
        if(shot.EditorMode == CurveEditorMode.Advanced)
        {
            ImGui.Spacing();
            spatial.Draw(shot, timeline, shortcuts, brioTheme);
            Status = spatial.Status;
            return;
        }

        if(shotId != shot.Id) { shotId = shot.Id; selected.Clear(); fit = true; End(); selectionRevision = -1; }
        if(shot.Intervals.Count == 0) { ImGui.Spacing(); ImGui.TextDisabled(I18n.T("graph.single_node_empty")); return; }
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

        if(timeline.Tracks.FirstOrDefault(t => t.Shots.Contains(shot)) is { RotationDriven: true } driven)
        {
            ImGui.SameLine();
            UiWidgets.Chip("ge_drv", driven.FovDriven ? I18n.T("graph.driven_rot_fov") : I18n.T("graph.driven_rot"), XcpStyle.Warning,
                I18n.T("graph.driven_simple_tip"));
        }

        ImGui.Spacing();

        // ---- Row 1: channels + link/unlink + smooth + frame all ----
        float sqH = ImGui.GetFrameHeight();
        var sq = new Vector2(sqH, sqH);
        bool firstCh = true;
        foreach(var c in channels)
        {
            if(!firstCh) ImGui.SameLine(0, 6f * s);
            firstCh = false;
            ImGui.PushID((int)c);
            bool show = visible.Contains(c), isLocked = locked.Contains(c);
            var tint = ImGui.ColorConvertU32ToFloat4(Colors[(int)c]);
            if(UiWidgets.TogglePill("vis", linked ? I18n.T("graph.ch.shared") : Label(c), show, I18n.T("graph.vis_tip"),
                show ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash, activeColor: tint, brioTheme: brioTheme))
            { if(show) visible.Remove(c); else visible.Add(c); }
            ImGui.SameLine(0, 2f * s);
            if(UiWidgets.IconButton("lck", isLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen,
                isLocked ? I18n.T("graph.locked_tip") : I18n.T("graph.lock_tip"), sq, active: isLocked, subtle: !isLocked, activeColor: XcpStyle.Warning, brioTheme: brioTheme))
            { if(isLocked) locked.Remove(c); else locked.Add(c); End(); }
            ImGui.PopID();
        }

        ImGui.SameLine(0, 10f * s);
        if(linked)
        {
            ImGui.BeginDisabled(locked.Count > 0);
            if(UiWidgets.IconTextButton("unlink", FontAwesomeIcon.Unlink, I18n.T("graph.unlink"),
                I18n.T("graph.unlink_tip"), brioTheme: brioTheme))
            { End(); timeline.Snapshot(); foreach(var i in shot.Intervals) i.Unlink(); visible.UnionWith(Enum.GetValues<CameraChannel>()); timeline.Touch(); }
            ImGui.EndDisabled();
        }
        else
        {
            ImGui.SetNextItemWidth(86f * s);
            ImGui.Combo("##linksrc", ref linkSource, I18n.Combo("graph.ch.pos", "graph.ch.rot", "graph.ch.fov"));
            UiWidgets.Tooltip(I18n.T("graph.link_src_tip"));
            ImGui.SameLine(0, 3f * s);
            ImGui.BeginDisabled(locked.Count > 0);
            if(UiWidgets.IconTextButton("link", FontAwesomeIcon.Link, I18n.T("graph.link"),
                I18n.T("graph.link_tip"), brioTheme: brioTheme))
            { End(); timeline.Snapshot(); foreach(var i in shot.Intervals) { i.Link((CameraChannel)linkSource); i.MarkManual(); } timeline.Touch(); }
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(locked.Count > 0);
        if(UiWidgets.IconTextButton("smooth", FontAwesomeIcon.WaveSquare, I18n.T("graph.auto_smooth"),
            I18n.T("graph.auto_smooth_tip"), brioTheme: brioTheme))
        { End(); timeline.Snapshot(); SmoothTiming.Apply(shot, true); shot.ActivateSimpleTiming(); timeline.Touch(); Status = I18n.T("graph.status_smoothed"); }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if(UiWidgets.IconTextButton("fit_simple", FontAwesomeIcon.Expand, I18n.T("common.fit_all"), I18n.T("graph.fit_simple_tip"), brioTheme: brioTheme))
            fit = true;

        // ---- Row 2: interpolation + handles + insert/delete ----
        ImGui.SetNextItemWidth(92f * s); ImGui.Combo("##s_interp", ref interpolation, I18n.Combo("graph.interp.bezier", "graph.interp.linear", "graph.interp.constant"));
        UiWidgets.Tooltip(I18n.T("graph.interp_tip"));
        ImGui.SameLine(0, 3f * s);
        ImGui.SetNextItemWidth(116f * s); ImGui.Combo("##s_scope", ref scope, I18n.Combo("graph.scope.selected", "graph.scope.visible", "graph.scope.shot"));
        UiWidgets.Tooltip(I18n.T("graph.scope_tip"));
        ImGui.SameLine(0, 3f * s);
        if(ImGui.Button($"{I18n.T("graph.apply_interp")}##s_app_i")) Apply(shot, timeline, refs, false);

        ImGui.SameLine(0, 8f * s);
        ImGui.BeginDisabled(interpolation != 0);
        ImGui.SetNextItemWidth(108f * s); ImGui.Combo("##s_handle", ref handleMode, I18n.Combo("graph.hmode.auto_clamped", "graph.hmode.auto", "graph.hmode.aligned", "graph.hmode.free", "graph.hmode.vector"));
        UiWidgets.Tooltip(I18n.T("graph.hmode_tip"));
        ImGui.SameLine(0, 3f * s);
        if(ImGui.Button($"{I18n.T("graph.apply_handle")}##s_app_h")) Apply(shot, timeline, refs, true);
        ImGui.EndDisabled();

        ImGui.SameLine(0, 10f * s);
        int cursor = (int)Math.Round(timeline.Playhead);
        ImGui.SetNextItemWidth(68f * s);
        if(ImGui.InputInt("##s_cur", ref cursor, 0)) { timeline.Playhead = Math.Clamp(cursor, 0, timeline.Length); PlayheadChanged = true; }
        UiWidgets.Tooltip(I18n.T("graph.playhead_tip"));
        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconTextButton("s_ins", FontAwesomeIcon.Plus, I18n.T("graph.insert_node"), I18n.T("graph.insert_node_tip"), brioTheme: brioTheme))
        {
            if(locked.Count > 0) Status = I18n.T("graph.insert_node_locked");
            else try { timeline.AddSampledNode(shot, cursor); selectionRevision = -1; }
                catch(InvalidOperationException ex) { Status = ex.Message; }
            ImGui.PopID(); return;
        }
        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("s_del", FontAwesomeIcon.TrashAlt, I18n.T("graph.delete_node_tip"), brioTheme: brioTheme))
        { Delete(timeline, refs); ImGui.PopID(); return; }

        var primary = refs.FirstOrDefault(r => selected.Contains(r.Key.Id));
        if(primary.Key != null)
        {
            int f = primary.Key.Frame; double value = Global(shot, primary, primary.Key.Value) * 100;
            ImGui.SetNextItemWidth(76f * s); bool changed = ImGui.InputInt($"{I18n.T("common.frame")}##skf", ref f, 1, 10, "%d", ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine(); ImGui.BeginDisabled(primary.Key.NodeId.HasValue); ImGui.SetNextItemWidth(96f * s);
            bool valueChanged = ImGui.InputDouble($"{I18n.T("graph.progress_pct")}##skv", ref value, 0, 0, "%.3f", ImGuiInputTextFlags.EnterReturnsTrue); ImGui.EndDisabled();
            if(changed || valueChanged) Move(shot, timeline, primary, f, Local(shot, primary, value / 100), false);
            ImGui.SameLine(); ImGui.TextDisabled($"· {Label(primary.Channel)} · F{shot.Start}–F{shot.End}");
        }

        ImGui.Spacing();
        float statusReserve = string.IsNullOrEmpty(Status) ? 4f * s : ImGui.GetTextLineHeightWithSpacing() + 6f * s;
        var origin = ImGui.GetCursorScreenPos();
        float width = MathF.Max(260f * s, ImGui.GetContentRegionAvail().X);
        float h = MathF.Max(210f * s, ImGui.GetContentRegionAvail().Y - statusReserve);
        float rulerH = 23f * s;
        var plot = origin + new Vector2(0, rulerH); var end = origin + new Vector2(width, h);
        if(fit) { left = shot.Start - Math.Max(2, (shot.End - shot.Start) * .04); span = Math.Max(4, (shot.End - shot.Start) * 1.08); bottom = -.1; height = 1.2; fit = false; }
        float X(double f) => origin.X + (float)((f - left) / span * width);
        float Y(double v) => end.Y - (float)((v - bottom) / height * (h - rulerH));
        double F(float x) => left + (x - origin.X) / width * span;
        double V(float y) => bottom + (end.Y - y) / (h - rulerH) * height;
        ImGui.InvisibleButton("##shotcanvas", new Vector2(width, h), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered(); var io = ImGui.GetIO(); var mouse = io.MousePos;
        if(hovered && io.MouseWheel != 0)
        {
            ViewportMath.Wheel(ref left, ref span, ref bottom, ref height, io.MouseWheel,
                shortcuts.Held(Shortcut.ZoomX) || shortcuts.Held(Shortcut.ZoomY),
                shortcuts.Held(Shortcut.ScrollY) || shortcuts.Held(Shortcut.ZoomY), (mouse.X - origin.X) / width, (end.Y - mouse.Y) / (h - rulerH));
        }
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(origin, end, true);
        dl.AddRectFilled(origin, end, XcpStyle.CanvasBg(brioTheme));
        dl.AddRectFilled(origin, new Vector2(end.X, plot.Y), XcpStyle.CanvasRuler(brioTheme));
        double step = Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(span / 10))));
        for(double f = Math.Ceiling(left / step) * step; f <= left + span; f += step)
        { dl.AddLine(new(X(f), plot.Y), new(X(f), end.Y), XcpStyle.CanvasGrid(brioTheme)); dl.AddText(new(X(f) + 3, origin.Y + 2), 0xFFC0C0CC, f.ToString("0")); }
        for(int n = 0; n <= 4; n++) { float y = Y(n / 4.0); dl.AddLine(new(origin.X, y), new(end.X, y), XcpStyle.CanvasGrid(brioTheme)); dl.AddText(new(origin.X + 3, y + 1), 0xFF8C8C9A, $"{n * 25}%"); }
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
        dl.AddLine(new(X(timeline.Playhead), origin.Y), new(X(timeline.Playhead), end.Y), XcpStyle.CanvasPlayhead(brioTheme), 2);
        if(box is Vector2 b0) dl.AddRect(Vector2.Min(b0, mouse), Vector2.Max(b0, mouse), 0xFFFFFF55);
        dl.PopClipRect();
        dl.AddRect(origin, end, XcpStyle.CardBorderU32(brioTheme), 3f * s, ImDrawFlags.None, 1f);
        if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) { panning = true; lastMouse = mouse; }
        if(panning && (ImGui.IsMouseDown(ImGuiMouseButton.Middle) || ImGui.IsMouseDown(ImGuiMouseButton.Left)))
        { left -= (mouse.X - lastMouse.X) / width * span; bottom += (mouse.Y - lastMouse.Y) / (h - rulerH) * height; lastMouse = mouse; }
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
                    double value = Local(shot, r, Global(shot, r, originalHandle.Y) + (press.Y - mouse.Y) / (h - rulerH) * height);
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
        if(hovered && hit is Hit hover) ImGui.SetTooltip(I18n.T("graph.hover_simple", Label(hover.Item.Channel), hover.Item.Key.Frame, $"{Global(shot, hover.Item, hover.Item.Key.Value) * 100:0.###}"));
        if(!string.IsNullOrEmpty(Status)) ImGui.TextDisabled(Status);
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
            if(!timeline.MoveNodes(targets)) Status = I18n.T("graph.move_rejected");
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
        if(locked.Count > 0) { Status = I18n.T("graph.delete_locked"); return; }
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
    private static string Label(CameraChannel channel) => channel switch { CameraChannel.Position => I18n.T("graph.ch.pos"), CameraChannel.Rotation => I18n.T("graph.ch.rot"), _ => I18n.T("graph.ch.fov") };
}
