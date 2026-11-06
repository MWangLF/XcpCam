using Dalamud.Bindings.ImGui;
using System;
using System.Linq;
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

    private Keyframe? _dragKey;
    private float _dragGrabOffset;
    private bool _scrubbing;

    private const float RulerH = 22f, OutputH = 14f, LaneH = 30f, NodeR = 7f, HeaderW = 190f;

    public void Draw(CamTimeline tl, ShotComposer? comp)
    {
        PlayheadChanged = false; KeysChanged = false; DragStarted = false; TracksEdited = false;

        var start = ImGui.GetCursorScreenPos();
        float totalW = MathF.Max(300, ImGui.GetContentRegionAvail().X);
        float height = RulerH + OutputH + 4 + tl.Tracks.Count * LaneH + 4;

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
            if(io.KeyCtrl)
            {
                float fAtMouse = ScrollFrame + (mouse.X - origin.X) / PixelsPerFrame;
                PixelsPerFrame = Math.Clamp(PixelsPerFrame * (io.MouseWheel > 0 ? 1.2f : 1f / 1.2f), 0.5f, 60f);
                ScrollFrame = fAtMouse - (mouse.X - origin.X) / PixelsPerFrame;
            }
            else
                ScrollFrame -= io.MouseWheel * visibleFrames * 0.1f;
        }
        if(ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            ScrollFrame -= io.MouseDelta.X / PixelsPerFrame;
        visibleFrames = width / PixelsPerFrame;
        ScrollFrame = Math.Clamp(ScrollFrame, -5, MathF.Max(-5, tl.Length + 5 - visibleFrames));

        float X(float frame) => origin.X + (frame - ScrollFrame) * PixelsPerFrame;
        float FrameAt(float x) => ScrollFrame + (x - origin.X) / PixelsPerFrame;
        float LaneTop(int i) => origin.Y + RulerH + OutputH + 4 + i * LaneH;

        // background
        dl.PushClipRect(origin, origin + size, true);
        dl.AddRectFilled(origin, origin + size, 0xFF1E1E1E);
        dl.AddRectFilled(origin, origin + new Vector2(width, RulerH), 0xFF2A2A2A);
        for(int i = 0; i < lanes; i++)
        {
            uint bg = i == tl.ActiveTrackIndex ? 0xFF2C2C34 : (i % 2 == 0 ? 0xFF222222 : 0xFF1B1B1B);
            dl.AddRectFilled(new Vector2(origin.X, LaneTop(i)), new Vector2(origin.X + width, LaneTop(i) + LaneH), bg);
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
        for(int ti = 0; ti < lanes; ti++)
        {
            var track = tl.Tracks[ti];
            float midY = LaneTop(ti) + LaneH / 2;
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
                bool over = hovered && Vector2.Distance(mouse, new Vector2(x, midY)) <= NodeR + 3;
                if(over) hoveredKey = k;
                bool sel = k == Selected;
                uint fill = k.Tangent switch { TangentMode.Stop => 0xFF3366FF, TangentMode.Linear => 0xFF33CCCC, _ => track.Color };
                if(!active) fill = (fill & 0x00FFFFFF) | 0x70000000;
                DrawDiamond(dl, new Vector2(x, midY), NodeR + (sel ? 2 : 0), sel ? 0xFF00FFFF : fill, over || sel ? 0xFFFFFFFF : 0xFF000000);
                int idx = track.Keys.IndexOf(k) + 1;
                dl.AddText(new Vector2(x - 4, midY + NodeR - 1), 0xFFDDDDDD, $"{idx}");
            }
        }

        // playhead
        float px = X(tl.Playhead);
        dl.AddLine(new Vector2(px, origin.Y), new Vector2(px, origin.Y + height), 0xFF4444FF, 2);
        dl.AddTriangleFilled(new Vector2(px - 6, origin.Y), new Vector2(px + 6, origin.Y), new Vector2(px, origin.Y + 8), 0xFF4444FF);
        dl.PopClipRect();

        // ---- interaction ----
        if(ImGui.IsItemActivated() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if(hoveredKey != null)
            {
                _dragKey = hoveredKey;
                Selected = _dragKey;
                var owner = tl.TrackOf(_dragKey);
                if(owner != null) tl.ActiveTrackIndex = tl.Tracks.IndexOf(owner);
                _dragGrabOffset = FrameAt(mouse.X) - _dragKey.Frame;
                DragStarted = true;
            }
            else
            {
                _scrubbing = true;
                float laneY = mouse.Y - (origin.Y + RulerH + OutputH + 4);
                if(laneY >= 0)
                {
                    int lane = (int)(laneY / LaneH);
                    if(lane >= 0 && lane < lanes) tl.ActiveTrackIndex = lane;
                    Selected = null;
                }
            }
        }

        if(ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if(_dragKey != null)
            {
                int target = (int)MathF.Round(FrameAt(mouse.X) - _dragGrabOffset);
                int before = _dragKey.Frame;
                if(tl.MoveKey(_dragKey, target) != before) KeysChanged = true;
                ImGui.SetTooltip($"第 {_dragKey.Frame} 帧  ({_dragKey.Frame / (float)tl.Fps:0.00}s)");
            }
            else if(_scrubbing)
            {
                tl.Playhead = Math.Clamp(MathF.Round(FrameAt(mouse.X)), 0, tl.Length);
                PlayheadChanged = true;
            }
        }
        else
        {
            _dragKey = null;
            _scrubbing = false;
        }

        if(hovered && hoveredKey != null && _dragKey == null)
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
            tl.AddTrack();
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
            float y = start.Y + RulerH + OutputH + 4 + i * LaneH;
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
