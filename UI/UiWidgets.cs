using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Numerics;

namespace XcpCam;

/// <summary>
/// Stateless Brio-inspired ImGui helpers: FontAwesome icon buttons, mixed icon+CJK buttons,
/// toggle pills, segmented controls, labeled section headers, status dots, and auto-height cards.
/// </summary>
public static class UiWidgets
{
    private static float Scale => ImGuiHelpers.GlobalScale;
    public static float CurrentRightPad { get; private set; }

    /// <summary>Available width inside the current window or <see cref="CardScope"/>.</summary>
    public static float InnerAvailWidth => MathF.Max(20f, ImGui.GetContentRegionAvail().X - CurrentRightPad);

    public static void Tooltip(string? text, bool allowDisabled = true)
    {
        if(string.IsNullOrEmpty(text)) return;
        var flags = allowDisabled ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None;
        if(ImGui.IsItemHovered(flags)) ImGui.SetTooltip(text);
    }

    /// <summary>Subtle FontAwesome question badge carrying long explanatory text in a tooltip.</summary>
    public static void HelpMarker(string tip)
    {
        using(ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextDisabled(FontAwesomeIcon.QuestionCircle.ToIconString());
        Tooltip(tip);
    }

    /// <summary>Draws a FontAwesome icon glyph inline (uses IconFont cleanly without affecting following CJK text).</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        if(color is { } c) ImGui.TextColored(c, icon.ToIconString());
        else ImGui.TextUnformatted(icon.ToIconString());
    }

    /// <summary>Colored indicator dot followed by a concise status string.</summary>
    public static void StatusDot(Vector4 color, string text, string? tooltip = null)
    {
        var pos = ImGui.GetCursorScreenPos();
        float h = ImGui.GetTextLineHeight();
        float r = 3.6f * Scale;
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(new Vector2(pos.X + r + 1f * Scale, pos.Y + h * 0.52f), r, ImGui.ColorConvertFloat4ToU32(color), 12);
        ImGui.Dummy(new Vector2(r * 2f + 4f * Scale, h));
        ImGui.SameLine(0, 3f * Scale);
        ImGui.TextUnformatted(text);
        Tooltip(tooltip);
    }

    /// <summary>
    /// Square or fixed-size icon-only button modeled after Brio's FontIconButton.
    /// Draws the icon via the window drawlist at the exact geometric center so FramePadding
    /// never pushes wider FontAwesome glyphs toward the right edge of compact buttons.
    /// </summary>
    public static bool IconButton(string id, FontAwesomeIcon icon, string? tooltip = null, Vector2? size = null,
        bool active = false, bool subtle = false, Vector4? activeColor = null, bool brioTheme = true)
    {
        float h = ImGui.GetFrameHeight();
        var btnSize = size ?? new Vector2(MathF.Max(h, 24f * Scale), h);
        string iconStr = icon.ToIconString();
        Vector2 iconSize;
        using(ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(iconStr);

        int pushed = PushButtonColors(active, subtle, activeColor, brioTheme);
        bool clicked = ImGui.Button($"##{id}", btnSize);
        if(pushed > 0) ImGui.PopStyleColor(pushed);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        uint col = active
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, ImGui.GetStyle().Alpha))
            : ImGui.GetColorU32(ImGuiCol.Text);
        var pos = new Vector2(
            MathF.Round((min.X + max.X - iconSize.X) * 0.5f),
            MathF.Round((min.Y + max.Y - iconSize.Y) * 0.5f));

        dl.PushClipRect(min + new Vector2(1f, 1f), max - new Vector2(1f, 1f), true);
        using(ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(pos, col, iconStr);
        dl.PopClipRect();

        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>
    /// Button combining a FontAwesome icon and CJK/Latin text. Renders via the drawlist so
    /// IconFont glyphs and default CJK characters never conflict.
    /// </summary>
    public static bool IconTextButton(string id, FontAwesomeIcon icon, string label, string? tooltip = null,
        bool active = false, bool subtle = false, Vector2? size = null, Vector4? activeColor = null,
        bool brioTheme = true, bool dropdownCaret = false)
    {
        string iconStr = icon.ToIconString();
        string caretStr = dropdownCaret ? FontAwesomeIcon.ChevronDown.ToIconString() : "";
        Vector2 iconSize, caretSize = Vector2.Zero;
        using(ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconStr);
            if(dropdownCaret) caretSize = ImGui.CalcTextSize(caretStr);
        }
        var textSize = label.Length > 0 ? ImGui.CalcTextSize(label) : Vector2.Zero;
        float gap = label.Length > 0 ? 5f * Scale : 0f;
        float caretGap = dropdownCaret ? 5f * Scale : 0f;
        float contentW = iconSize.X + gap + textSize.X + caretGap + caretSize.X;
        var pad = ImGui.GetStyle().FramePadding;
        float defaultH = ImGui.GetFrameHeight();
        var finalSize = size ?? new Vector2(contentW + pad.X * 2f + 2f * Scale, defaultH);
        if(finalSize.X <= 0f) finalSize.X = contentW + pad.X * 2f + 2f * Scale;
        if(finalSize.Y <= 0f) finalSize.Y = defaultH;

        int pushed = PushButtonColors(active, subtle, activeColor, brioTheme);
        bool clicked = ImGui.Button($"##{id}", finalSize);
        if(pushed > 0) ImGui.PopStyleColor(pushed);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        uint col = active ? ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, ImGui.GetStyle().Alpha)) : ImGui.GetColorU32(ImGuiCol.Text);

        float startX = MathF.Round(min.X + MathF.Max(2f * Scale, (max.X - min.X - contentW) * 0.5f));
        float centerY = (min.Y + max.Y) * 0.5f;

        dl.PushClipRect(min + new Vector2(2f, 1f), max - new Vector2(2f, 1f), true);
        using(ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(new Vector2(startX, MathF.Round(centerY - iconSize.Y * 0.5f)), col, iconStr);
        float x = startX + iconSize.X + gap;
        if(label.Length > 0)
        {
            dl.AddText(new Vector2(MathF.Round(x), MathF.Round(centerY - textSize.Y * 0.5f)), col, label);
            x += textSize.X + caretGap;
        }
        if(dropdownCaret)
        {
            using(ImRaii.PushFont(UiBuilder.IconFont))
                dl.AddText(new Vector2(MathF.Round(x), MathF.Round(centerY - caretSize.Y * 0.5f + 0.5f * Scale)), col, caretStr);
        }
        dl.PopClipRect();

        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>Pill-style toggle button (accent-highlighted when active).</summary>
    public static bool TogglePill(string id, string label, bool active, string? tooltip = null,
        FontAwesomeIcon? icon = null, Vector2? size = null, Vector4? activeColor = null, bool brioTheme = true)
    {
        if(icon is { } ic)
            return IconTextButton(id, ic, label, tooltip, active, false, size, activeColor, brioTheme);

        int pushed = PushButtonColors(active, false, activeColor, brioTheme);
        bool clicked = size is { } s ? ImGui.Button($"{label}##{id}", s) : ImGui.Button($"{label}##{id}");
        if(pushed > 0) ImGui.PopStyleColor(pushed);
        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>Small danger-tinted button for destructive actions like deleting a curve or node.</summary>
    public static bool DangerButton(string id, FontAwesomeIcon icon, string label, string? tooltip = null,
        Vector2? size = null, bool brioTheme = true)
    {
        int n = 0;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.19f, 0.21f, 0.85f)); n++;
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, XcpStyle.Danger); n++;
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.75f, 0.22f, 0.24f, 1f)); n++;
        bool clicked = IconTextButton(id, icon, label, tooltip, active: false, subtle: false, size: size, brioTheme: brioTheme);
        ImGui.PopStyleColor(n);
        return clicked;
    }

    /// <summary>
    /// Blender/Brio segmented switch (e.g. 物体模式 | 编辑模式, 通道曲线 | 节奏曲线).
    /// Returns true when the selected index changed.
    /// </summary>
    public static bool Segmented(string id, ref int current, ReadOnlySpan<string> labels,
        bool brioTheme = true, float totalWidth = 0f, ReadOnlySpan<string> tooltips = default)
    {
        if(labels.Length == 0) return false;
        bool changed = false;
        float eachW = totalWidth > 0f ? totalWidth / labels.Length : 0f;
        float h = ImGui.GetFrameHeight();
        var accent = XcpStyle.ActiveAccent(brioTheme);
        var accentHover = XcpStyle.ActiveAccentHover(brioTheme);

        ImGui.PushID(id);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(1f * Scale, ImGui.GetStyle().ItemSpacing.Y));
        for(int i = 0; i < labels.Length; i++)
        {
            if(i > 0) ImGui.SameLine();
            bool selected = current == i;
            int pushed = 0;
            if(selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, accent); pushed++;
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, accentHover); pushed++;
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, accent); pushed++;
                ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One); pushed++;
            }
            var sz = eachW > 0f ? new Vector2(eachW, h) : new Vector2(0f, h);
            if(ImGui.Button($"{labels[i]}##seg{i}", sz) && !selected)
            {
                current = i;
                changed = true;
            }
            if(pushed > 0) ImGui.PopStyleColor(pushed);
            if(i < tooltips.Length) Tooltip(tooltips[i]);
        }
        ImGui.PopStyleVar();
        ImGui.PopID();
        return changed;
    }

    /// <summary>
    /// Labeled section header with horizontal divider rule (replaces ImGui 1.90 SeparatorText cleanly in Dalamud 15).
    /// </summary>
    public static void SectionHeader(string title, bool brioTheme = true, FontAwesomeIcon? icon = null, string? helpTip = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float availW = InnerAvailWidth;
        float lineH = ImGui.GetTextLineHeight();

        if(icon is { } ic)
        {
            Icon(ic, brioTheme ? XcpStyle.Info : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.SameLine(0, 5f * Scale);
        }
        ImGui.TextUnformatted(title);
        float textEndX = ImGui.GetItemRectMax().X;
        if(!string.IsNullOrEmpty(helpTip))
        {
            ImGui.SameLine(0, 5f * Scale);
            HelpMarker(helpTip);
            textEndX = ImGui.GetItemRectMax().X;
        }
        float lineX = textEndX + 7f * Scale;
        float lineEndX = start.X + availW;
        if(lineEndX > lineX + 6f * Scale)
        {
            float y = start.Y + lineH * 0.52f;
            dl.AddLine(new Vector2(lineX, y), new Vector2(lineEndX, y), XcpStyle.SectionLineU32(brioTheme), 1f);
        }
        ImGui.Spacing();
    }

    /// <summary>
    /// Section header with a compact right-aligned icon toggle button (modeled after Brio's Gizmo header with Local/World toggle).
    /// </summary>
    public static bool SectionHeaderAction(string title, FontAwesomeIcon actionIcon, string actionId, string actionTooltip,
        bool actionActive, bool brioTheme = true, FontAwesomeIcon? titleIcon = null, string? actionLabel = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float availW = InnerAvailWidth;
        float frameH = ImGui.GetFrameHeight();

        ImGui.AlignTextToFramePadding();
        if(titleIcon is { } ic)
        {
            Icon(ic, brioTheme ? XcpStyle.Info : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.SameLine(0, 5f * Scale);
        }
        ImGui.TextUnformatted(title);
        float textEndX = ImGui.GetItemRectMax().X;

        float btnW = frameH;
        if(!string.IsNullOrEmpty(actionLabel))
        {
            Vector2 iconSz;
            using(ImRaii.PushFont(UiBuilder.IconFont)) iconSz = ImGui.CalcTextSize(actionIcon.ToIconString());
            btnW = iconSz.X + ImGui.CalcTextSize(actionLabel).X + ImGui.GetStyle().FramePadding.X * 2f + 8f * Scale;
        }
        float btnX = MathF.Max(textEndX + 8f * Scale, start.X + availW - btnW);

        if(btnX - textEndX > 14f * Scale)
        {
            float y = start.Y + frameH * 0.5f;
            dl.AddLine(new Vector2(textEndX + 6f * Scale, y), new Vector2(btnX - 6f * Scale, y), XcpStyle.SectionLineU32(brioTheme), 1f);
        }

        ImGui.SameLine();
        ImGui.SetCursorScreenPos(new Vector2(btnX, start.Y));
        bool clicked = string.IsNullOrEmpty(actionLabel)
            ? IconButton(actionId, actionIcon, actionTooltip, new Vector2(btnW, frameH), active: actionActive, subtle: !actionActive, brioTheme: brioTheme)
            : IconTextButton(actionId, actionIcon, actionLabel, actionTooltip, active: actionActive, subtle: false, size: new Vector2(btnW, frameH), brioTheme: brioTheme);
        return clicked;
    }

    /// <summary>Compact horizontal chip badge (used in constraint summaries and node inspection).</summary>
    public static bool Chip(string id, string text, Vector4 tint, string? tooltip = null)
    {
        var bg = tint with { W = 0.22f };
        var bgHover = tint with { W = 0.36f };
        var bgAct = tint with { W = 0.48f };
        ImGui.PushStyleColor(ImGuiCol.Button, bg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, bgHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, bgAct);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Lerp(tint, Vector4.One, 0.45f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10f * Scale);
        bool clicked = ImGui.SmallButton($"{text}##{id}");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>
    /// Auto-height rounded card using DrawList channel splitting.
    /// Avoid nesting <see cref="CardScope"/> or calling <see cref="ImGui.BeginTable"/> inside it.
    /// </summary>
    public static CardScope Card(string id, bool brioTheme, float padding = 8f)
        => new(id, brioTheme, padding);

    public readonly struct CardScope : IDisposable
    {
        private readonly ImDrawListPtr _dl;
        private readonly Vector2 _min;
        private readonly float _width;
        private readonly float _pad;
        private readonly float _prevRightPad;
        private readonly bool _brio;

        public CardScope(string id, bool brioTheme, float padding)
        {
            _brio = brioTheme;
            _pad = padding * Scale;
            _prevRightPad = CurrentRightPad;
            CurrentRightPad = _prevRightPad + _pad;
            _dl = ImGui.GetWindowDrawList();
            _min = ImGui.GetCursorScreenPos();
            _width = MathF.Max(40f, ImGui.GetContentRegionAvail().X - _prevRightPad);

            ImGui.PushID(id);
            _dl.ChannelsSplit(2);
            _dl.ChannelsSetCurrent(1);
            ImGui.BeginGroup();
            float topGap = MathF.Max(1f, _pad - ImGui.GetStyle().ItemSpacing.Y);
            ImGui.Dummy(new Vector2(_width, topGap));
            ImGui.Indent(_pad);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(40f, _width - _pad * 2f));
        }

        public void Dispose()
        {
            ImGui.PopTextWrapPos();
            ImGui.Unindent(_pad);
            float bottomGap = MathF.Max(1f, _pad - ImGui.GetStyle().ItemSpacing.Y);
            ImGui.Dummy(new Vector2(_width, bottomGap));
            ImGui.EndGroup();
            var max = new Vector2(_min.X + _width, ImGui.GetItemRectMax().Y);
            _dl.ChannelsSetCurrent(0);
            _dl.AddRectFilled(_min, max, XcpStyle.CardBgU32(_brio), XcpStyle.CardRounding);
            _dl.AddRect(_min, max, XcpStyle.CardBorderU32(_brio), XcpStyle.CardRounding, ImDrawFlags.None, 1f);
            _dl.ChannelsMerge();
            ImGui.PopID();
            CurrentRightPad = _prevRightPad;
        }
    }

    /// <summary>Vertical draggable splitter between left main view and right N-Panel.</summary>
    public static bool VerticalSplitter(string id, float height, ref float rightWidth, float minRight, float maxRight, bool brioTheme)
    {
        float w = 6f * Scale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(w, MathF.Max(20f, height)));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        if(hovered || active) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);

        var dl = ImGui.GetWindowDrawList();
        float midX = pos.X + w * 0.5f;
        uint lineCol = active ? XcpStyle.AccentU32(brioTheme)
            : hovered ? ImGui.ColorConvertFloat4ToU32(XcpStyle.ActiveAccentHover(brioTheme))
            : XcpStyle.SectionLineU32(brioTheme);
        dl.AddLine(new Vector2(midX, pos.Y + 4f * Scale), new Vector2(midX, pos.Y + height - 4f * Scale), lineCol, active || hovered ? 2f : 1f);

        if(active)
        {
            float delta = ImGui.GetIO().MouseDelta.X;
            if(MathF.Abs(delta) > 0.01f)
            {
                rightWidth = Math.Clamp(rightWidth - delta, minRight, maxRight);
                return true;
            }
        }
        return false;
    }

    private static int PushButtonColors(bool active, bool subtle, Vector4? activeColor, bool brioTheme)
    {
        int n = 0;
        if(active)
        {
            var c = activeColor ?? XcpStyle.ActiveAccent(brioTheme);
            var hover = activeColor.HasValue ? Vector4.Min(Vector4.One, c * 1.12f) with { W = c.W } : XcpStyle.ActiveAccentHover(brioTheme);
            ImGui.PushStyleColor(ImGuiCol.Button, c); n++;
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover); n++;
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, c); n++;
            ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One); n++;
        }
        else if(subtle)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero); n++;
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, brioTheme ? new Vector4(0.28f, 0.27f, 0.36f, 0.7f) : ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered]); n++;
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, XcpStyle.ActiveAccent(brioTheme)); n++;
        }
        return n;
    }
}
