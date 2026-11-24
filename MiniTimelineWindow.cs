using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// Ultra-slim fixed bottom timeline bar (Style 2): locked to the bottom-center of the screen with no move/resize
/// chrome so GPose camera work never accidentally drags it. Position, size, and opacity are edited only in Settings.
/// Shares the exact timeline, selection, playback, curve anchors, and shortcut stack with MainWindow.
/// </summary>
public sealed class MiniTimelineWindow : Window
{
    private readonly MainWindow _main;
    private readonly IClientState _clientState;
    private readonly TimelineWidget _tlw = new();
    private IDisposable? _styleScope;
    private bool _needsInitialFit = true;

    public MiniTimelineWindow(MainWindow main, IClientState clientState)
        : base("##xcpcam_mini_timeline",
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoSavedSettings)
    {
        _main = main;
        _clientState = clientState;
        _tlw.BeforeTrackEdit = main.Timeline.Snapshot;
        _tlw.Curves = main.CurveTool;
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public override bool DrawConditions()
    {
        var cfg = _main.Config;
        return _clientState.IsGPosing
            && cfg.ShowMiniTimeline
            && (!cfg.HideMiniWhenMainExpanded || !_main.IsExpanded);
    }

    public override void PreDraw()
    {
        var cfg = _main.Config;
        var vp = ImGui.GetMainViewport();
        float s = XcpStyle.Scale;

        float maxW = MathF.Max(420f * s, vp.Size.X - 16f * s);
        float maxH = MathF.Max(88f * s, MathF.Min(360f * s, vp.Size.Y * 0.45f));
        float w = Math.Clamp(cfg.MiniTimelineWidth * s, 420f * s, maxW);
        float h = Math.Clamp(cfg.MiniTimelineHeight * s, 88f * s, maxH);
        float x = Math.Clamp(
            vp.Pos.X + (vp.Size.X - w) * 0.5f + cfg.MiniTimelineOffsetX * s,
            vp.Pos.X + 4f * s,
            MathF.Max(vp.Pos.X + 4f * s, vp.Pos.X + vp.Size.X - w - 4f * s));
        float y = Math.Clamp(
            vp.Pos.Y + vp.Size.Y - h - cfg.MiniTimelineBottomMargin * s,
            vp.Pos.Y + 4f * s,
            MathF.Max(vp.Pos.Y + 4f * s, vp.Pos.Y + vp.Size.Y - h - 4f * s));

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(w, h), ImGuiCond.Always);
        _styleScope = XcpStyle.PushWindow(cfg.UseBrioTheme, floatingToolbar: true, bgOpacityOverride: cfg.MiniTimelineOpacity);
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
        float s = XcpStyle.Scale;
        var tl = _main.Timeline;

        _main.HandleExternalShortcuts(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows));

        float h = ImGui.GetFrameHeight();
        float iconW = MathF.Max(h, 24f * s);
        var sq = new Vector2(iconW, h);
        float startX = ImGui.GetCursorPosX();
        float barW = ImGui.GetContentRegionAvail().X;

        // ---- Left: compact track selector ----
        float trackW = 108f * s;
        ImGui.SetNextItemWidth(trackW);
        int trackIndex = tl.ActiveTrackIndex;
        var trackNames = tl.Tracks.Select(t => t.Name).ToArray();
        if(ImGui.Combo("##mini_track", ref trackIndex, trackNames, trackNames.Length))
        {
            tl.ActiveTrackIndex = trackIndex;
            _tlw.ScrollToTrack(trackIndex, MathF.Max(52f * s, ImGui.GetContentRegionAvail().Y));
        }
        UiWidgets.Tooltip(I18n.T("mini.track_tip"));

        // ---- Center: transport controls + frame readout ----
        string tailText = $"/ {tl.Length} ({tl.Playhead / tl.Fps:0.00}s)";
        float frameInW = 54f * s;
        float tailW = ImGui.CalcTextSize(tailText).X;
        float transportW = iconW * 7f + 2f * s * 6f + 8f * s + frameInW + 6f * s + tailW;
        float rightW = iconW * 5f + 3f * s * 4f;

        ImGui.SameLine(0, 8f * s);
        float idealCenterX = startX + (barW - transportW) * 0.5f;
        float minCenterX = startX + trackW + 8f * s;
        float maxCenterX = startX + barW - rightW - transportW - 8f * s;
        if(maxCenterX >= minCenterX)
            ImGui.SetCursorPosX(Math.Clamp(idealCenterX, minCenterX, maxCenterX));

        if(UiWidgets.IconButton("mt_first", FontAwesomeIcon.FastBackward, I18n.T("transport.first_tip"), sq, subtle: true, brioTheme: brio))
            _main.TriggerSeekPlayhead(0);
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("mt_prev", FontAwesomeIcon.StepBackward, I18n.T("transport.prev_tip"), sq, subtle: true, brioTheme: brio))
            _main.TriggerStepPlayhead(-1);
        ImGui.SameLine(0, 2f * s);
        bool playing = _main.IsTransportPlaying;
        if(UiWidgets.IconButton("mt_play", playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
            _main.TransportPlayTooltip, sq, active: playing, subtle: !playing, brioTheme: brio))
            _main.TriggerTogglePlay();
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("mt_stop", FontAwesomeIcon.Stop, I18n.T("transport.stop_tip"), sq, subtle: true, brioTheme: brio))
            _main.TriggerStopTransport();
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("mt_next", FontAwesomeIcon.StepForward, I18n.T("transport.next_tip"), sq, subtle: true, brioTheme: brio))
            _main.TriggerStepPlayhead(1);
        ImGui.SameLine(0, 2f * s);
        if(UiWidgets.IconButton("mt_last", FontAwesomeIcon.FastForward, I18n.T("transport.last_tip", tl.Length), sq, subtle: true, brioTheme: brio))
            _main.TriggerSeekPlayhead(tl.Length);
        ImGui.SameLine(0, 4f * s);
        bool loop = _main.IsCurrentLoop;
        if(UiWidgets.IconButton("mt_loop", FontAwesomeIcon.Retweet, loop ? I18n.T("transport.loop_on_tip") : I18n.T("transport.loop_off_tip"),
            sq, active: loop, subtle: !loop, brioTheme: brio))
            _main.TriggerSetLoop(!loop);

        ImGui.SameLine(0, 8f * s);
        ImGui.SetNextItemWidth(frameInW);
        int ph = (int)MathF.Round(tl.Playhead);
        if(ImGui.InputInt("##mt_ph", ref ph, 0)) _main.TriggerSeekPlayhead(ph);
        UiWidgets.Tooltip(I18n.T("mini.frame_input_tip"));
        ImGui.SameLine(0, 5f * s);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(tailText);

        // ---- Right: Overlay + Live Preview + Fit All + Expand Main + Mini Settings ----
        ImGui.SameLine(0, 8f * s);
        float availRight = ImGui.GetContentRegionAvail().X;
        if(availRight > rightW) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availRight - rightW);

        var overlay = _main.Overlay;
        if(UiWidgets.IconButton("mt_ov", overlay.Enabled ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
            overlay.Enabled ? I18n.T("mini.overlay_on_tip") : I18n.T("mini.overlay_off_tip"),
            sq, active: overlay.Enabled, subtle: !overlay.Enabled, brioTheme: brio))
        {
            overlay.Enabled = !overlay.Enabled;
            _main.SaveConfig();
        }

        ImGui.SameLine(0, 3f * s);
        ImGui.BeginDisabled(!_main.CanLookThrough);
        if(UiWidgets.IconButton("mt_prev_cam", FontAwesomeIcon.Video, _main.LookThroughTooltip,
            sq, active: _main.LookThrough, subtle: !_main.LookThrough, activeColor: XcpStyle.PreviewAmber, brioTheme: brio))
            _main.SetLookThrough(!_main.LookThrough);
        ImGui.EndDisabled();

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("mt_fit", FontAwesomeIcon.Expand, I18n.T("mini.fit_all_tip"), sq, subtle: true, brioTheme: brio))
            _tlw.FitAll(tl, barW, includeHeader: false);

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("mt_main", FontAwesomeIcon.WindowRestore, I18n.T("mini.expand_main_tip"), sq, subtle: true, brioTheme: brio))
            _main.ExpandAndFocus();

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("mt_cfg", FontAwesomeIcon.Cog, I18n.T("mini.settings_tip"), sq, subtle: true, brioTheme: brio))
            _main.OnOpenMiniTimelineSettings?.Invoke();

        // ---- Full-width headerless TimelineWidget canvas ----
        if(_needsInitialFit)
        {
            _tlw.FitAll(tl, barW, includeHeader: false);
            _needsInitialFit = false;
        }

        _tlw.Selected = _main.SelectedNode;
        float canvasH = ImGui.GetContentRegionAvail().Y - 1f;
        float canvasAlpha = Math.Clamp(cfg.MiniTimelineOpacity, 0.25f, 1f);
        _tlw.Draw(tl, _main.Composer, cfg.Shortcuts, _main.Overlay.SpeedColors, canvasH, brio, showHeaders: false, bgAlpha: canvasAlpha);

        if(_tlw.NodesClicked || _tlw.CurvePointsClicked || !ReferenceEquals(_tlw.Selected, _main.SelectedNode))
            _main.SelectedNode = _tlw.Selected;
        if(_tlw.PlayheadChanged) _main.TriggerSeekPlayhead(tl.Playhead);
    }
}
