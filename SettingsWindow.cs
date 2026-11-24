using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// Standalone settings window opened from the main header gear icon or Dalamud's OpenConfigUi.
/// Hosts language/theme options, viewport overlay display toggles, movement range limits,
/// fixed mini timeline position/size settings, and shortcut bindings.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly Action<bool> _setToolbarOpen;
    private IDisposable? _styleScope;
    private string _shortcutMessage = "";
    private bool _selectMiniTab;

    public SettingsWindow(MainWindow main, Action<bool> setToolbarOpen)
        : base($"{I18n.T("window.settings")}###xcpcam_settings", ImGuiWindowFlags.None)
    {
        _main = main;
        _setToolbarOpen = setToolbarOpen;
        Size = new Vector2(560, 510);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 360),
            MaximumSize = new Vector2(1200, 1000),
        };
    }

    /// <summary>Opens the settings window directly on the Mini Timeline tab.</summary>
    public void OpenMiniTimelineTab()
    {
        IsOpen = true;
        _selectMiniTab = true;
    }

    public override void PreDraw()
    {
        WindowName = $"{I18n.T("window.settings")}###xcpcam_settings";
        _styleScope = XcpStyle.PushWindow(_main.Config.UseBrioTheme);
    }

    public override void PostDraw()
    {
        _styleScope?.Dispose();
        _styleScope = null;
    }

    public override void Draw()
    {
        var cfg = _main.Config;
        bool brio = cfg.UseBrioTheme;
        bool changed = false;

        if(ImGui.BeginTabBar("##settings_tabs"))
        {
            if(ImGui.BeginTabItem($"{I18n.T("settings.tab.general")}###settings_tab_general"))
            {
                ImGui.Spacing();
                changed |= DrawGeneralCard(cfg, brio);
                ImGui.Spacing();
                changed |= DrawOverlayCard(brio);
                ImGui.Spacing();
                changed |= DrawRangeCard(brio);
                ImGui.EndTabItem();
            }

            var miniFlags = _selectMiniTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            _selectMiniTab = false;
            if(ImGui.BeginTabItem($"{I18n.T("settings.tab.mini_timeline")}###settings_tab_mini", miniFlags))
            {
                ImGui.Spacing();
                changed |= DrawMiniTimelineCard(cfg, brio);
                ImGui.EndTabItem();
            }

            if(ImGui.BeginTabItem($"{I18n.T("settings.tab.shortcuts")}###settings_tab_shortcuts"))
            {
                ImGui.Spacing();
                changed |= cfg.Shortcuts.Draw(ref _shortcutMessage, brio);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        if(changed) _main.SaveConfig();
    }

    private static float MeasureLabelColumn(float minPx, params string[] labels)
    {
        float max = 0f;
        foreach(var label in labels) max = MathF.Max(max, ImGui.CalcTextSize(label).X);
        return MathF.Max(minPx * XcpStyle.Scale, max + 16f * XcpStyle.Scale);
    }

    private bool DrawGeneralCard(Configuration cfg, bool brio)
    {
        bool changed = false;
        float s = XcpStyle.Scale;
        using var card = UiWidgets.Card("settings_general", brio);
        UiWidgets.SectionHeader(I18n.T("settings.general.title"), brio, FontAwesomeIcon.SlidersH);

        string langLabel = I18n.T("settings.general.language");
        string themeLabel = I18n.T("settings.general.theme");
        float labelX = MeasureLabelColumn(110f, langLabel, themeLabel);

        // ---- Language selector (default: follow game client language) ----
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(langLabel);
        ImGui.SameLine(labelX);
        string autoLabel = I18n.T("settings.general.language_auto", I18n.NativeName(I18n.DetectedGameLanguage));
        string pref = I18n.NormalizePreference(cfg.Language);
        string currentDisplay = pref == I18n.Auto ? autoLabel : I18n.NativeName(pref);
        float langW = MathF.Max(220f * s, MathF.Min(290f * s, UiWidgets.InnerAvailWidth - labelX - 4f * s));
        ImGui.SetNextItemWidth(langW);
        if(ImGui.BeginCombo("##settings_lang", currentDisplay))
        {
            if(ImGui.Selectable($"{autoLabel}##lang_auto", pref == I18n.Auto))
            {
                cfg.Language = I18n.Auto;
                I18n.SetPreference(I18n.Auto);
                _shortcutMessage = "";
                changed = true;
            }
            ImGui.Separator();
            foreach(var (code, nativeName) in I18n.SupportedLanguages)
            {
                if(ImGui.Selectable($"{nativeName}##lang_{code}", pref == code))
                {
                    cfg.Language = code;
                    I18n.SetPreference(code);
                    _shortcutMessage = "";
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        UiWidgets.Tooltip(I18n.T("settings.general.language_tip"));

        // ---- UI Theme ----
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(themeLabel);
        ImGui.SameLine(labelX);
        int theme = cfg.UseBrioTheme ? 0 : 1;
        string brioLabel = I18n.T("settings.general.theme_brio");
        string dalLabel = I18n.T("settings.general.theme_dalamud");
        float segW = MathF.Max(220f * s, MathF.Max(ImGui.CalcTextSize(brioLabel).X, ImGui.CalcTextSize(dalLabel).X) * 2f + 32f * s);
        segW = MathF.Min(segW, MathF.Max(180f * s, UiWidgets.InnerAvailWidth - labelX - 4f * s));
        if(UiWidgets.Segmented("theme_seg", ref theme, [brioLabel, dalLabel], brio, segW,
            [I18n.T("settings.general.theme_brio_tip"), I18n.T("settings.general.theme_dalamud_tip")]))
        {
            cfg.UseBrioTheme = theme == 0;
            changed = true;
        }

        ImGui.Spacing();
        changed |= ImGui.Checkbox($"{I18n.T("settings.general.open_on_gpose")}##open_gpose", ref cfg.OpenOnGPose);
        UiWidgets.Tooltip(I18n.T("settings.general.open_on_gpose_tip"));

        bool toolbar = cfg.ShowViewportToolbar;
        if(ImGui.Checkbox($"{I18n.T("settings.general.show_toolbar")}##show_tb", ref toolbar))
        {
            cfg.ShowViewportToolbar = toolbar;
            _setToolbarOpen(toolbar);
            changed = true;
        }
        UiWidgets.Tooltip(I18n.T("settings.general.show_toolbar_tip"));

        ImGui.Spacing();
        if(UiWidgets.IconTextButton("reset_sidebar", FontAwesomeIcon.Undo, I18n.T("settings.general.reset_sidebar"),
            I18n.T("settings.general.reset_sidebar_tip"), brioTheme: brio))
        {
            cfg.SidebarOpen = true;
            cfg.SidebarWidth = 290f;
            changed = true;
        }

        return changed;
    }

    private bool DrawMiniTimelineCard(Configuration cfg, bool brio)
    {
        bool changed = false;
        float s = XcpStyle.Scale;

        using(UiWidgets.Card("settings_mini_switch", brio))
        {
            UiWidgets.SectionHeader(I18n.T("settings.mini.switch_title"), brio, FontAwesomeIcon.Stream,
                I18n.T("settings.mini.switch_tip"));

            changed |= ImGui.Checkbox($"{I18n.T("settings.mini.show")}##mini_show", ref cfg.ShowMiniTimeline);
            changed |= ImGui.Checkbox($"{I18n.T("settings.mini.hide_when_expanded")}##mini_hide_exp", ref cfg.HideMiniWhenMainExpanded);
            UiWidgets.Tooltip(I18n.T("settings.mini.hide_when_expanded_tip"));

            if(cfg.ShowMiniTimeline && cfg.HideMiniWhenMainExpanded && _main.IsExpanded)
            {
                ImGui.Spacing();
                UiWidgets.StatusDot(XcpStyle.Info, I18n.T("settings.mini.expanded_notice"));
            }
        }

        ImGui.Spacing();

        using(UiWidgets.Card("settings_mini_geom", brio))
        {
            UiWidgets.SectionHeader(I18n.T("settings.mini.geom_title"), brio, FontAwesomeIcon.ExpandAlt,
                I18n.T("settings.mini.geom_tip"));

            string lW = I18n.T("settings.mini.width");
            string lH = I18n.T("settings.mini.height");
            string lX = I18n.T("settings.mini.offset_x");
            string lB = I18n.T("settings.mini.bottom_margin");
            string lO = I18n.T("settings.mini.opacity");
            float labelX = MeasureLabelColumn(110f, lW, lH, lX, lB, lO);
            float fieldW = MathF.Max(140f * s, UiWidgets.InnerAvailWidth - labelX - 4f * s);

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(lW);
            ImGui.SameLine(labelX);
            ImGui.SetNextItemWidth(fieldW);
            ImGui.SliderFloat("##mini_w", ref cfg.MiniTimelineWidth, 420f, 1800f, "%.0f px");
            changed |= ImGui.IsItemDeactivatedAfterEdit();

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(lH);
            ImGui.SameLine(labelX);
            ImGui.SetNextItemWidth(fieldW);
            ImGui.SliderFloat("##mini_h", ref cfg.MiniTimelineHeight, 90f, 320f, "%.0f px");
            changed |= ImGui.IsItemDeactivatedAfterEdit();

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(lX);
            ImGui.SameLine(labelX);
            ImGui.SetNextItemWidth(fieldW);
            ImGui.SliderFloat("##mini_ox", ref cfg.MiniTimelineOffsetX, -600f, 600f, "%.0f px");
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            UiWidgets.Tooltip(I18n.T("settings.mini.offset_x_tip"));

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(lB);
            ImGui.SameLine(labelX);
            ImGui.SetNextItemWidth(fieldW);
            ImGui.SliderFloat("##mini_bm", ref cfg.MiniTimelineBottomMargin, 0f, 400f, "%.0f px");
            changed |= ImGui.IsItemDeactivatedAfterEdit();

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(lO);
            ImGui.SameLine(labelX);
            ImGui.SetNextItemWidth(fieldW);
            ImGui.SliderFloat("##mini_op", ref cfg.MiniTimelineOpacity, 0.25f, 1.00f, "%.2f");
            changed |= ImGui.IsItemDeactivatedAfterEdit();

            ImGui.Spacing();
            if(UiWidgets.IconTextButton("reset_mini", FontAwesomeIcon.Undo, I18n.T("settings.mini.reset"),
                I18n.T("settings.mini.reset_tip"), brioTheme: brio))
            {
                cfg.MiniTimelineWidth = 900f;
                cfg.MiniTimelineHeight = 115f;
                cfg.MiniTimelineOffsetX = 0f;
                cfg.MiniTimelineBottomMargin = 20f;
                cfg.MiniTimelineOpacity = 0.88f;
                changed = true;
            }
        }

        return changed;
    }

    private bool DrawOverlayCard(bool brio)
    {
        bool changed = false;
        float s = XcpStyle.Scale;
        var overlay = _main.Overlay;
        using var card = UiWidgets.Card("settings_overlay", brio);
        UiWidgets.SectionHeader(I18n.T("settings.overlay.title"), brio, FontAwesomeIcon.Eye,
            I18n.T("settings.overlay.tip"));

        if(UiWidgets.TogglePill("ov_en",
            overlay.Enabled ? I18n.T("settings.overlay.enabled") : I18n.T("settings.overlay.disabled"),
            overlay.Enabled, I18n.T("settings.overlay.toggle_tip"),
            overlay.Enabled ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash, brioTheme: brio))
        {
            overlay.Enabled = !overlay.Enabled;
            changed = true;
        }

        ImGui.BeginDisabled(!overlay.Enabled);
        ImGui.Spacing();
        string showPathLabel = I18n.T("settings.overlay.show_path");
        string showFrustumsLabel = I18n.T("settings.overlay.show_frustums");
        float col2X = MathF.Max(155f * s, MathF.Max(ImGui.CalcTextSize(showPathLabel).X, ImGui.CalcTextSize(showFrustumsLabel).X) + 46f * s);

        changed |= ImGui.Checkbox($"{showPathLabel}##ov_path", ref overlay.ShowPath);
        ImGui.SameLine(col2X);
        changed |= ImGui.Checkbox($"{I18n.T("settings.overlay.speed_colors")}##ov_spd", ref overlay.SpeedColors);
        UiWidgets.Tooltip(I18n.T("settings.overlay.speed_colors_tip"));

        changed |= ImGui.Checkbox($"{showFrustumsLabel}##ov_fru", ref overlay.ShowFrustums);
        ImGui.SameLine(col2X);
        changed |= ImGui.Checkbox($"{I18n.T("settings.overlay.show_labels")}##ov_lbl", ref overlay.ShowLabels);

        ImGui.Spacing();
        string sizeLabel = I18n.T("settings.overlay.frustum_size");
        float labelX = MeasureLabelColumn(110f, sizeLabel);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(sizeLabel);
        ImGui.SameLine(labelX);
        ImGui.SetNextItemWidth(170f * s);
        ImGui.SliderFloat("##frustum_size", ref overlay.FrustumSize, 0.05f, 3.0f, "%.2f");
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        ImGui.EndDisabled();
        return changed;
    }

    private bool DrawRangeCard(bool brio)
    {
        bool changed = false;
        float s = XcpStyle.Scale;
        using var card = UiWidgets.Card("settings_range", brio);
        UiWidgets.SectionHeader(I18n.T("settings.range.title"), brio, FontAwesomeIcon.Compass,
            I18n.T("settings.range.tip", $"{NodeRange.MinimumRange:0}", $"{NodeRange.MaximumRange:0}", $"{NodeRange.DefaultRange:0}"));

        float limit = NodeRange.MaxRange;
        string radiusLabel = I18n.T("settings.range.radius");
        float labelX = MeasureLabelColumn(110f, radiusLabel);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(radiusLabel);
        ImGui.SameLine(labelX);
        ImGui.SetNextItemWidth(140f * s);
        if(ImGui.InputFloat("##move_limit", ref limit, 100f, 1000f, "%.0f")) NodeRange.MaxRange = limit;
        changed |= ImGui.IsItemDeactivatedAfterEdit();

        ImGui.SameLine();
        if(UiWidgets.IconTextButton("reset_limit", FontAwesomeIcon.Undo, I18n.T("common.reset_default"),
            I18n.T("settings.range.reset_tip", $"{NodeRange.DefaultRange:0}"), brioTheme: brio))
        {
            NodeRange.MaxRange = NodeRange.DefaultRange;
            changed = true;
        }
        return changed;
    }
}
