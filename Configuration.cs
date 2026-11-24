using Dalamud.Configuration;
using XcpCam.Core;

namespace XcpCam;

/// <summary>Persistent plugin settings (camera nodes themselves are still session-only).</summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string Language = I18n.Auto;
    public bool OpenOnGPose = true;
    public float MovementLimit = NodeRange.DefaultRange;
    public bool OverlayEnabled = true, ShowPath = true, ShowFrustums = true, ShowLabels = true, SpeedColors = true;
    public ShortcutBindings Shortcuts { get; set; } = new();
    public float FrustumSize = 0.35f;

    // UI layout & theme preferences (defaults chosen for backward-compatible deserialization)
    public bool UseBrioTheme = true;
    public bool ShowViewportToolbar = false;
    public bool SidebarOpen = true;
    public float SidebarWidth = 290f;

    // Fixed bottom mini timeline preferences (position & size are editable only via Settings)
    public bool ShowMiniTimeline = false;
    public bool HideMiniWhenMainExpanded = true;
    public float MiniTimelineWidth = 900f;
    public float MiniTimelineHeight = 115f;
    public float MiniTimelineOffsetX = 0f;
    public float MiniTimelineBottomMargin = 20f;
    public float MiniTimelineOpacity = 0.88f;
}
