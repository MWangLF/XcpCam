using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace XcpCam;

/// <summary>
/// Transparent full-screen window that hosts the path overlay and the node gizmo. It ignores input unless the
/// mouse is over / dragging the gizmo, in which case it takes the mouse so the game camera does not move.
/// Same technique as Brio's PosingOverlayWindow.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly Action _draw;
    private readonly Func<bool> _wantsInput;

    public OverlayWindow(Action draw, Func<bool> wantsInput)
        : base("##xcpcam_overlay", ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNav, true)
    {
        _draw = draw;
        _wantsInput = wantsInput;
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public override void PreDraw()
    {
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero, ImGuiCond.Always);
        Size = ImGui.GetIO().DisplaySize / ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.Always;

        Flags |= ImGuiWindowFlags.NoInputs;
        if(_wantsInput()) Flags &= ~ImGuiWindowFlags.NoInputs;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    public override void Draw() => _draw();
}
