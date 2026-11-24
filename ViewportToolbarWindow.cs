using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// Compact floating viewport toolbar modeled after Brio's PosingOverlayToolbarWindow.
/// Synchronizes directly with MainWindow's overlay, gizmo, preview, and selection actions.
/// Hidden automatically outside GPose without losing the user's open preference.
/// </summary>
public sealed class ViewportToolbarWindow : Window
{
    private readonly MainWindow _main;
    private readonly IClientState _clientState;
    private IDisposable? _styleScope;

    public ViewportToolbarWindow(MainWindow main, IClientState clientState)
        : base($"{I18n.T("window.viewport_toolbar")}###xcpcam_viewport_toolbar",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        _main = main;
        _clientState = clientState;
        RespectCloseHotkey = false;
        IsOpen = main.Config.ShowViewportToolbar;
    }

    public override bool DrawConditions() => _clientState.IsGPosing;

    public override void OnClose()
    {
        if(_main.Config.ShowViewportToolbar)
        {
            _main.Config.ShowViewportToolbar = false;
            _main.SaveConfig();
        }
    }

    public override void PreDraw()
    {
        WindowName = $"{I18n.T("window.viewport_toolbar")}###xcpcam_viewport_toolbar";
        _styleScope = XcpStyle.PushWindow(_main.Config.UseBrioTheme, floatingToolbar: true);
    }

    public override void PostDraw()
    {
        _styleScope?.Dispose();
        _styleScope = null;
    }

    public override void Draw()
    {
        bool brio = _main.Config.UseBrioTheme;
        float s = XcpStyle.Scale;
        float cell = MathF.Round(2.25f * ImGui.GetTextLineHeight());
        var btnSize = new Vector2(cell, cell);
        float gap = 4f * s;

        var overlay = _main.Overlay;
        var gizmo = _main.Gizmo;

        // ---- Row 1: Viewport utilities (4 columns) ----
        if(UiWidgets.IconButton("tb_ov", overlay.Enabled ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
            overlay.Enabled ? I18n.T("toolbar.overlay_on_tip") : I18n.T("toolbar.overlay_off_tip"),
            btnSize, active: overlay.Enabled, subtle: true, brioTheme: brio))
        {
            overlay.Enabled = !overlay.Enabled;
            _main.SaveConfig();
        }

        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(!_main.CanLookThrough);
        if(UiWidgets.IconButton("tb_prev", FontAwesomeIcon.Video, _main.LookThroughTooltip,
            btnSize, active: _main.LookThrough, subtle: true, activeColor: XcpStyle.PreviewAmber, brioTheme: brio))
            _main.SetLookThrough(!_main.LookThrough);
        ImGui.EndDisabled();

        ImGui.SameLine(0, gap);
        if(UiWidgets.IconButton("tb_giz", FontAwesomeIcon.Crosshairs,
            gizmo.Enabled ? I18n.T("toolbar.gizmo_on_tip") : I18n.T("toolbar.gizmo_off_tip"),
            btnSize, active: gizmo.Enabled, subtle: true, brioTheme: brio))
            gizmo.Enabled = !gizmo.Enabled;

        ImGui.SameLine(0, gap);
        if(UiWidgets.IconButton("tb_main", _main.IsOpen ? FontAwesomeIcon.Compress : FontAwesomeIcon.WindowRestore,
            _main.IsOpen ? I18n.T("toolbar.collapse_main_tip") : I18n.T("toolbar.expand_main_tip"),
            btnSize, active: _main.IsOpen, subtle: true, brioTheme: brio))
            _main.Toggle();

        // ---- Section 2: Gizmo mode + Local/World toggle in header ----
        ImGui.Spacing();
        if(UiWidgets.SectionHeaderAction(I18n.T("toolbar.gizmo_section"),
            gizmo.LocalSpace ? FontAwesomeIcon.Cube : FontAwesomeIcon.Globe,
            "tb_space",
            gizmo.LocalSpace ? I18n.T("toolbar.space_local_tip") : I18n.T("toolbar.space_world_tip"),
            gizmo.LocalSpace, brio))
            gizmo.LocalSpace = !gizmo.LocalSpace;

        bool curveTarget = _main.CurveOwnsGizmo;
        bool rotateLocked = _main.GizmoRotateLocked;
        ImGui.BeginDisabled(!gizmo.Enabled);

        if(UiWidgets.IconButton("tb_tr", FontAwesomeIcon.ArrowsUpDownLeftRight, I18n.T("toolbar.op_translate"),
            btnSize, active: gizmo.Enabled && gizmo.Operation == GizmoEditor.Op.Translate, subtle: true, brioTheme: brio))
            gizmo.Operation = GizmoEditor.Op.Translate;

        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(rotateLocked);
        if(UiWidgets.IconButton("tb_rot", FontAwesomeIcon.ArrowsSpin,
            rotateLocked ? I18n.T("toolbar.op_rotate_locked") : I18n.T("toolbar.op_rotate"),
            btnSize, active: gizmo.Enabled && gizmo.Operation == GizmoEditor.Op.Rotate && !rotateLocked, subtle: true, brioTheme: brio))
            gizmo.Operation = GizmoEditor.Op.Rotate;
        ImGui.EndDisabled();

        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(!curveTarget);
        if(UiWidgets.IconButton("tb_sc", FontAwesomeIcon.ExpandAlt,
            curveTarget ? I18n.T("toolbar.op_scale") : I18n.T("toolbar.op_scale_curve_only"),
            btnSize, active: gizmo.Enabled && curveTarget && gizmo.Operation == GizmoEditor.Op.Scale, subtle: true, brioTheme: brio))
            gizmo.Operation = GizmoEditor.Op.Scale;
        ImGui.EndDisabled();

        ImGui.SameLine(0, gap);
        if(UiWidgets.IconButton("tb_snap", FontAwesomeIcon.Magnet,
            gizmo.Snap ? I18n.T("toolbar.snap_on_tip", $"{gizmo.SnapTranslate:0.##}", $"{gizmo.SnapRotateDeg:0.#}") : I18n.T("toolbar.snap_off_tip"),
            btnSize, active: gizmo.Snap, subtle: true, brioTheme: brio))
            gizmo.Snap = !gizmo.Snap;

        ImGui.EndDisabled();

        // ---- Section 3: Context quick actions ----
        ImGui.Spacing();
        UiWidgets.SectionHeader(_main.ToolbarContextHeader, brio);

        var ct = _main.CurveTool;
        if(ct.OwnsGizmo && ct.EditMode && !ct.NodePath && ct.Curve != null)
        {
            ImGui.BeginDisabled(!ct.CanExtrude);
            if(UiWidgets.IconButton("tb_ext", FontAwesomeIcon.PlusCircle, I18n.T("toolbar.curve_extrude_tip"), btnSize, subtle: true, brioTheme: brio))
                ct.ExtrudeSelected();
            ImGui.EndDisabled();

            ImGui.SameLine(0, gap);
            ImGui.BeginDisabled(!ct.CanSubdivide);
            if(UiWidgets.IconButton("tb_sub", FontAwesomeIcon.Stream, I18n.T("toolbar.curve_subdivide_tip"), btnSize, subtle: true, brioTheme: brio))
                ct.SubdivideSelected();
            ImGui.EndDisabled();

            ImGui.SameLine(0, gap);
            ImGui.BeginDisabled(!ct.CanToggleCyclic);
            if(UiWidgets.IconButton("tb_cyc", FontAwesomeIcon.Sync, I18n.T("toolbar.curve_cyclic_tip"), btnSize, subtle: true, brioTheme: brio))
                ct.ToggleCyclicSelected();
            ImGui.EndDisabled();

            ImGui.SameLine(0, gap);
            ImGui.BeginDisabled(!_main.CanDeleteSelection);
            if(UiWidgets.IconButton("tb_del_c", FontAwesomeIcon.TrashAlt, I18n.T("toolbar.curve_delete_tip"), btnSize, subtle: true, brioTheme: brio))
                _main.TriggerDelete();
            ImGui.EndDisabled();
        }
        else
        {
            if(UiWidgets.IconButton("tb_key", FontAwesomeIcon.Plus, I18n.T("toolbar.key_capture_tip"), btnSize, subtle: true, brioTheme: brio))
                _main.TriggerCaptureAtPlayhead();

            ImGui.SameLine(0, gap);
            ImGui.BeginDisabled(!_main.CanCutAtPlayhead);
            if(UiWidgets.IconButton("tb_cut", FontAwesomeIcon.Cut, I18n.T("toolbar.key_cut_tip"), btnSize, subtle: true, brioTheme: brio))
                _main.TriggerCutAtPlayhead();
            ImGui.EndDisabled();

            ImGui.SameLine(0, gap);
            ImGui.BeginDisabled(!_main.CanUndo);
            if(UiWidgets.IconButton("tb_undo", FontAwesomeIcon.Reply, I18n.T("toolbar.undo_tip"), btnSize, subtle: true, brioTheme: brio))
                _main.TriggerUndo();
            ImGui.EndDisabled();

            ImGui.SameLine(0, gap);
            ImGui.BeginDisabled(!_main.CanDeleteSelection);
            if(UiWidgets.IconButton("tb_del", FontAwesomeIcon.TrashAlt, I18n.T("toolbar.key_delete_tip"), btnSize, subtle: true, brioTheme: brio))
                _main.TriggerDelete();
            ImGui.EndDisabled();
        }
    }
}
