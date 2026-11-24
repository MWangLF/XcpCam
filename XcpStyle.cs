using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System;
using System.Numerics;

namespace XcpCam;

/// <summary>
/// Brio-inspired dark theme and shared semantic palette for XcpCam.
/// When <c>useBrioTheme</c> is false, only clean geometry/rounding tweaks and theme-aware semantic helpers apply,
/// leaving the user's Dalamud color scheme intact.
/// </summary>
public static class XcpStyle
{
    public static float Scale => ImGuiHelpers.GlobalScale;
    public static float CardRounding => 6f * Scale;

    private static Vector4 Rgba(byte r, byte g, byte b, byte a = 255)
        => new(r / 255f, g / 255f, b / 255f, a / 255f);

    // ---- Brio Dark palette (aligned with Brio ThemeManager.BrioDark) ----
    public static readonly Vector4 Accent = Rgba(98, 75, 224);
    public static readonly Vector4 AccentHover = Rgba(118, 96, 240);
    public static readonly Vector4 AccentActive = Rgba(82, 61, 200);
    public static readonly Vector4 AccentSoft = Rgba(98, 75, 224, 105);

    public static readonly Vector4 Success = new(0.40f, 0.86f, 0.54f, 1f);
    public static readonly Vector4 Warning = new(0.96f, 0.72f, 0.28f, 1f);
    public static readonly Vector4 Danger = new(0.90f, 0.34f, 0.36f, 1f);
    public static readonly Vector4 DangerHover = new(0.96f, 0.42f, 0.44f, 1f);
    public static readonly Vector4 Info = new(0.46f, 0.78f, 1.00f, 1f);
    public static readonly Vector4 PreviewAmber = Rgba(224, 146, 52);
    public static readonly Vector4 PreviewAmberHover = Rgba(238, 164, 72);
    public static readonly Vector4 MuteAmber = Rgba(212, 136, 40);
    public static readonly Vector4 SoloBlue = Rgba(64, 146, 232);

    private static readonly Vector4 WindowBg = Rgba(25, 25, 27, 248);
    private static readonly Vector4 ToolbarBg = Rgba(22, 22, 26, 232);
    private static readonly Vector4 ChildBg = Rgba(31, 31, 36, 255);
    private static readonly Vector4 CardBg = Rgba(35, 35, 41, 255);
    private static readonly Vector4 PopupBg = Rgba(28, 28, 33, 252);
    private static readonly Vector4 Border = Rgba(56, 56, 66, 220);
    private static readonly Vector4 TitleBg = Rgba(20, 20, 24, 255);
    private static readonly Vector4 TitleBgActive = Rgba(28, 26, 40, 255);
    private static readonly Vector4 Text = Rgba(244, 244, 248);
    private static readonly Vector4 TextDisabled = Rgba(150, 150, 165);
    private static readonly Vector4 FrameBg = Rgba(46, 46, 54, 255);
    private static readonly Vector4 FrameBgHovered = Rgba(58, 58, 70, 255);
    private static readonly Vector4 FrameBgActive = Rgba(68, 64, 92, 255);
    private static readonly Vector4 Button = Rgba(49, 49, 58, 255);
    private static readonly Vector4 ButtonHovered = Rgba(68, 66, 86, 255);
    private static readonly Vector4 Header = Rgba(50, 48, 66, 255);
    private static readonly Vector4 HeaderHovered = Rgba(74, 64, 128, 255);
    private static readonly Vector4 Separator = Rgba(62, 62, 74, 200);
    private static readonly Vector4 Tab = Rgba(38, 38, 46, 255);
    private static readonly Vector4 TabHovered = Rgba(84, 68, 184, 255);
    private static readonly Vector4 TabUnfocused = Rgba(32, 32, 38, 255);
    private static readonly Vector4 TabUnfocusedActive = Rgba(70, 56, 152, 255);
    private static readonly Vector4 CheckMark = Rgba(156, 138, 255);
    private static readonly Vector4 ScrollbarBg = Rgba(20, 20, 24, 180);
    private static readonly Vector4 ScrollbarGrab = Rgba(64, 64, 76, 255);
    private static readonly Vector4 ScrollbarGrabHovered = Rgba(86, 86, 102, 255);
    private static readonly Vector4 TableHeaderBg = Rgba(38, 38, 46, 255);
    private static readonly Vector4 TableBorderStrong = Rgba(60, 60, 72, 220);
    private static readonly Vector4 TableBorderLight = Rgba(48, 48, 58, 150);
    private static readonly Vector4 TableRowAlt = Rgba(255, 255, 255, 9);

    public static Vector4 ActiveAccent(bool brioTheme)
        => brioTheme ? Accent : ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];

    public static Vector4 ActiveAccentHover(bool brioTheme)
        => brioTheme ? AccentHover : ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];

    public static uint CardBgU32(bool brioTheme)
    {
        if(brioTheme) return ImGui.ColorConvertFloat4ToU32(CardBg);
        var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        var f = ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg];
        return ImGui.ColorConvertFloat4ToU32(Vector4.Lerp(bg, f, 0.35f) with { W = MathF.Max(bg.W, 0.85f) });
    }

    public static uint CardBorderU32(bool brioTheme)
        => brioTheme ? ImGui.ColorConvertFloat4ToU32(Border) : ImGui.GetColorU32(ImGuiCol.Border);

    public static uint SectionLineU32(bool brioTheme)
        => brioTheme ? ImGui.ColorConvertFloat4ToU32(Separator) : ImGui.GetColorU32(ImGuiCol.Separator);

    public static uint AccentU32(bool brioTheme)
        => ImGui.ColorConvertFloat4ToU32(ActiveAccent(brioTheme));

    // ---- Canvas palette (ABGR uints) for Timeline & Graph Editors ----
    public static uint CanvasBg(bool brio) => brio ? 0xFF1A1A1E : 0xFF1E1E1E;
    public static uint CanvasRuler(bool brio) => brio ? 0xFF26252C : 0xFF2A2A2A;
    public static uint CanvasHeaderBg(bool brio) => brio ? 0xFF222228 : 0xFF262626;
    public static uint CanvasLaneActive(bool brio) => brio ? 0xFF33293A : 0xFF2C2C34;
    public static uint CanvasLaneEven(bool brio) => brio ? 0xFF212127 : 0xFF222222;
    public static uint CanvasLaneOdd(bool brio) => brio ? 0xFF1C1C21 : 0xFF1B1B1B;
    public static uint CanvasGrid(bool brio) => brio ? 0x36605A6Eu : 0x304F4F4Fu;
    public static uint CanvasGridMajor(bool brio) => brio ? 0x40FFFFFFu : 0x30FFFFFFu;
    public static uint CanvasPlayhead(bool brio) => brio ? 0xFF5A5AFF : 0xFF4444FF;
    public static uint CanvasBorder(bool brio) => brio ? 0xFF423838 : 0xFF404040;

    /// <summary>Scales the alpha byte of an ABGR packed ImGui color.</summary>
    public static uint ScaleAlpha(uint abgr, float factor)
    {
        uint a = (uint)Math.Clamp((int)MathF.Round(((abgr >> 24) & 0xFF) * Math.Clamp(factor, 0f, 1f)), 0, 255);
        return (abgr & 0x00FFFFFFu) | (a << 24);
    }

    /// <summary>Pushes window geometry and (when enabled) the Brio Dark palette. Dispose in PostDraw().</summary>
    public static IDisposable PushWindow(bool brioTheme, bool floatingToolbar = false, float? bgOpacityOverride = null)
    {
        float s = Scale;
        int vars = 0, cols = 0;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, (floatingToolbar ? 8f : 7f) * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 5f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, floatingToolbar ? new Vector2(6f * s, 6f * s) : new Vector2(8f * s, 6f * s)); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f * s, 3f * s)); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(5f * s, 4f * s)); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(4f * s, 3f * s)); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 9f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f); vars++;
        if(floatingToolbar) { ImGui.PushStyleVar(ImGuiStyleVar.WindowTitleAlign, new Vector2(0.5f, 0.5f)); vars++; }

        if(brioTheme)
        {
            cols = PushBrioColors(floatingToolbar, bgOpacityOverride);
        }
        else if(floatingToolbar || bgOpacityOverride.HasValue)
        {
            var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            float alpha = bgOpacityOverride.HasValue ? Math.Clamp(bgOpacityOverride.Value, 0.1f, 1f) : MathF.Min(bg.W, 0.90f);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, bg with { W = alpha });
            cols++;
        }

        return new Scope(vars, cols);
    }

    /// <summary>Pushes compact styling for standalone popups rendered from the transparent overlay window.</summary>
    public static IDisposable PushPopup(bool brioTheme)
    {
        float s = Scale;
        int vars = 0, cols = 0;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * s); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f * s, 6f * s)); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * s, 4f * s)); vars++;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f); vars++;
        if(brioTheme) cols = PushBrioColors(false);
        return new Scope(vars, cols);
    }

    private static int PushBrioColors(bool floatingToolbar, float? bgOpacityOverride = null)
    {
        int n = 0;
        void C(ImGuiCol col, Vector4 value) { ImGui.PushStyleColor(col, value); n++; }

        C(ImGuiCol.Text, Text);
        C(ImGuiCol.TextDisabled, TextDisabled);
        var winBg = floatingToolbar ? ToolbarBg : WindowBg;
        if(bgOpacityOverride is float op) winBg = winBg with { W = Math.Clamp(op, 0.1f, 1f) };
        C(ImGuiCol.WindowBg, winBg);
        C(ImGuiCol.ChildBg, ChildBg);
        C(ImGuiCol.PopupBg, PopupBg);
        C(ImGuiCol.Border, Border);
        C(ImGuiCol.BorderShadow, Vector4.Zero);
        C(ImGuiCol.FrameBg, FrameBg);
        C(ImGuiCol.FrameBgHovered, FrameBgHovered);
        C(ImGuiCol.FrameBgActive, FrameBgActive);
        C(ImGuiCol.TitleBg, TitleBg);
        C(ImGuiCol.TitleBgActive, TitleBgActive);
        C(ImGuiCol.TitleBgCollapsed, TitleBg);
        C(ImGuiCol.MenuBarBg, TitleBg);
        C(ImGuiCol.ScrollbarBg, ScrollbarBg);
        C(ImGuiCol.ScrollbarGrab, ScrollbarGrab);
        C(ImGuiCol.ScrollbarGrabHovered, ScrollbarGrabHovered);
        C(ImGuiCol.ScrollbarGrabActive, Accent);
        C(ImGuiCol.CheckMark, CheckMark);
        C(ImGuiCol.SliderGrab, Accent);
        C(ImGuiCol.SliderGrabActive, AccentHover);
        C(ImGuiCol.Button, Button);
        C(ImGuiCol.ButtonHovered, ButtonHovered);
        C(ImGuiCol.ButtonActive, Accent);
        C(ImGuiCol.Header, Header);
        C(ImGuiCol.HeaderHovered, HeaderHovered);
        C(ImGuiCol.HeaderActive, Accent);
        C(ImGuiCol.Separator, Separator);
        C(ImGuiCol.SeparatorHovered, Accent);
        C(ImGuiCol.SeparatorActive, AccentHover);
        C(ImGuiCol.ResizeGrip, Rgba(98, 75, 224, 65));
        C(ImGuiCol.ResizeGripHovered, Rgba(98, 75, 224, 175));
        C(ImGuiCol.ResizeGripActive, AccentHover);
        C(ImGuiCol.Tab, Tab);
        C(ImGuiCol.TabHovered, TabHovered);
        C(ImGuiCol.TabActive, Accent);
        C(ImGuiCol.TabUnfocused, TabUnfocused);
        C(ImGuiCol.TabUnfocusedActive, TabUnfocusedActive);
        C(ImGuiCol.TableHeaderBg, TableHeaderBg);
        C(ImGuiCol.TableBorderStrong, TableBorderStrong);
        C(ImGuiCol.TableBorderLight, TableBorderLight);
        C(ImGuiCol.TableRowBg, Vector4.Zero);
        C(ImGuiCol.TableRowBgAlt, TableRowAlt);
        C(ImGuiCol.TextSelectedBg, AccentSoft);
        return n;
    }

    private sealed class Scope(int vars, int cols) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if(_disposed) return;
            _disposed = true;
            if(cols > 0) ImGui.PopStyleColor(cols);
            if(vars > 0) ImGui.PopStyleVar(vars);
        }
    }
}
