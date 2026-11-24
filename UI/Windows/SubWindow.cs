using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace XcpCam;

/// <summary>
/// Standalone Brio-styled secondary window (used for Paths, Constraints, and Brio Sync),
/// opened from the right N-Panel cards without replacing the main timeline / graph workspace.
/// </summary>
public sealed class SubWindow : Window
{
    private readonly Func<string> _windowName;
    private readonly Func<bool> _useBrioTheme;
    private readonly Action _draw;
    private IDisposable? _styleScope;

    public SubWindow(Func<string> windowName, Vector2 defaultSize, Vector2 minSize, Func<bool> useBrioTheme, Action draw)
        : base(windowName())
    {
        _windowName = windowName;
        _useBrioTheme = useBrioTheme;
        _draw = draw;
        Size = defaultSize;
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = minSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw()
    {
        WindowName = _windowName();
        _styleScope = XcpStyle.PushWindow(_useBrioTheme());
    }

    public override void PostDraw()
    {
        _styleScope?.Dispose();
        _styleScope = null;
    }

    public override void Draw() => _draw();
}
