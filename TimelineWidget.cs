using Dalamud.Bindings.ImGui;
using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// Frame-accurate camera timeline: ruler, draggable camera nodes, hold bars, scrubbable playhead,
/// mouse-wheel zoom (Ctrl+wheel) and horizontal scroll (wheel / middle-drag).
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

    private Keyframe? _dragKey;
    private float _dragGrabOffset;
    private bool _scrubbing;

    private const float RulerH = 22f, TrackH = 34f, NodeR = 7f;

    public void Draw(CamTimeline tl, Func<float, float?>? speedAt = null)
    {
        PlayheadChanged = false; KeysChanged = false; DragStarted = false;

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float width = MathF.Max(100, ImGui.GetContentRegionAvail().X);
        float height = RulerH + TrackH + 8;
        var size = new Vector2(width, height);

        ImGui.InvisibleButton("##timeline", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = io.MousePos;

        // ---- zoom / scroll ----
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

        // ---- background ----
        dl.PushClipRect(origin, origin + size, true);
        dl.AddRectFilled(origin, origin + size, 0xFF1E1E1E);
        dl.AddRectFilled(origin, origin + new Vector2(width, RulerH), 0xFF2A2A2A);
        // out-of-range shading
        float xEnd = X(tl.Length);
        if(xEnd < origin.X + width) dl.AddRectFilled(new Vector2(xEnd, origin.Y), origin + size, 0x60000000);
        float x0 = X(0);
        if(x0 > origin.X) dl.AddRectFilled(origin, new Vector2(x0, origin.Y + height), 0x60000000);

        // ---- ruler ----
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

        // ---- path segment & speed tint ----
        var sorted = tl.Sorted();
        float trackY = origin.Y + RulerH + 4;
        float midY = trackY + TrackH / 2;
        if(sorted.Count >= 2)
        {
            float a = X(sorted[0].Frame), b = X(sorted[^1].Frame + sorted[^1].HoldFrames);
            dl.AddRectFilled(new Vector2(a, midY - 3), new Vector2(b, midY + 3), 0xFF4A4A4A, 3);
            if(speedAt != null)
            {
                float step = MathF.Max(2, 3f);
                for(float x = MathF.Max(a, origin.X); x < MathF.Min(b, origin.X + width); x += step)
                {
                    var s = speedAt(FrameAt(x));
                    if(s is float v) dl.AddRectFilled(new Vector2(x, midY - 3), new Vector2(x + step, midY + 3), SpeedColor(v), 0f);
                }
            }
        }

        // ---- holds + nodes ----
        int hoveredIdx = -1;
        for(int i = 0; i < sorted.Count; i++)
        {
            var k = sorted[i];
            float x = X(k.Frame);
            if(k.HoldFrames > 0)
                dl.AddRectFilled(new Vector2(x, midY - 6), new Vector2(X(k.Frame + k.HoldFrames), midY + 6), 0xA03399FF, 3);
            bool over = hovered && Vector2.Distance(mouse, new Vector2(x, midY)) <= NodeR + 3;
            if(over) hoveredIdx = i;
            bool sel = k == Selected;
            uint fill = k.Tangent switch { TangentMode.Stop => 0xFF3366FF, TangentMode.Linear => 0xFF33CCCC, _ => 0xFF33AAFF };
            DrawDiamond(dl, new Vector2(x, midY), NodeR + (sel ? 2 : 0), sel ? 0xFF00FFFF : fill, over || sel ? 0xFFFFFFFF : 0xFF000000);
            int idx = tl.Keys.IndexOf(k) + 1;
            dl.AddText(new Vector2(x - 4, midY + NodeR + 1), 0xFFDDDDDD, $"{idx}");
        }

        // ---- playhead ----
        float px = X(tl.Playhead);
        dl.AddLine(new Vector2(px, origin.Y), new Vector2(px, origin.Y + height), 0xFF4444FF, 2);
        dl.AddTriangleFilled(new Vector2(px - 6, origin.Y), new Vector2(px + 6, origin.Y), new Vector2(px, origin.Y + 8), 0xFF4444FF);
        dl.PopClipRect();

        // ---- interaction ----
        if(ImGui.IsItemActivated() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if(hoveredIdx >= 0)
            {
                _dragKey = sorted[hoveredIdx];
                Selected = _dragKey;
                _dragGrabOffset = FrameAt(mouse.X) - _dragKey.Frame;
                DragStarted = true;
            }
            else
            {
                _scrubbing = true;
                if(mouse.Y > origin.Y + RulerH) Selected = null;
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

        if(hovered && hoveredIdx >= 0 && _dragKey == null)
        {
            var k = sorted[hoveredIdx];
            ImGui.SetTooltip($"镜头 {tl.Keys.IndexOf(k) + 1}：第 {k.Frame} 帧 ({k.Frame / (float)tl.Fps:0.00}s)\n拖动改变时间，双击跳转");
            if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                tl.Playhead = k.Frame;
                PlayheadChanged = true;
            }
        }
    }

    public void EnsureVisible(float frame, float width)
    {
        float visible = width / PixelsPerFrame;
        if(frame < ScrollFrame) ScrollFrame = frame - visible * 0.1f;
        else if(frame > ScrollFrame + visible) ScrollFrame = frame - visible * 0.9f;
    }

    public void FitAll(CamTimeline tl, float width)
    {
        int len = Math.Max(10, tl.Length);
        PixelsPerFrame = Math.Clamp((width - 20) / len, 0.5f, 60f);
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
