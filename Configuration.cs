using Dalamud.Configuration;
using XcpCam.Core;

namespace XcpCam;

/// <summary>Persistent plugin settings (camera nodes themselves are still session-only).</summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool OpenOnGPose = true;
    public float MovementLimit = NodeRange.DefaultRange;
    public bool OverlayEnabled = true, ShowPath = true, ShowFrustums = true, ShowLabels = true, SpeedColors = true;
    public ShortcutBindings Shortcuts { get; set; } = new();
    public float FrustumSize = 0.35f;
}
