using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Numerics;
using XcpCam.BrioInterop;
using XcpCam.Core;

namespace XcpCam;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xcpcam";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commands;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly WindowSystem _windows = new("XcpCam");
    private readonly MainWindow _main;
    private readonly OverlayWindow _overlayWindow;
    private readonly ViewportToolbarWindow _viewportToolbar;
    private readonly MiniTimelineWindow _miniTimeline;
    private readonly SettingsWindow _settingsWindow;
    private readonly SubWindow _pathWindow;
    private readonly SubWindow _constraintsWindow;
    private readonly SubWindow _brioWindow;
    private readonly BrioBridge _brio;
    private bool _pathOpenBeforeGpose, _constraintsOpenBeforeGpose, _brioOpenBeforeGpose, _settingsOpenBeforeGpose;

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, ITargetManager targets, IClientState clientState,
        IGameGui gameGui, IFramework framework, IPluginLog log, IObjectTable objects)
    {
        _pi = pi;
        _commands = commands;
        _framework = framework;
        _clientState = clientState;

        var config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        I18n.Initialize(config.Language, clientState.ClientLanguage.ToString(), (int)clientState.ClientLanguage, pi.UiLanguage);

        _brio = new BrioBridge(log);
        var overlay = new Overlay(gameGui, clientState, targets);
        var bones = new BoneSource(objects, targets, clientState);
        _main = new MainWindow(targets, clientState, log, _brio, overlay, bones, config, () => pi.SavePluginConfig(config));
        _overlayWindow = new OverlayWindow(_main.DrawOverlay, () => _main.OverlayWantsInput);
        _viewportToolbar = new ViewportToolbarWindow(_main, clientState);
        _miniTimeline = new MiniTimelineWindow(_main, clientState);
        _settingsWindow = new SettingsWindow(_main, open => _viewportToolbar.IsOpen = open);
        _pathWindow = new SubWindow(() => $"{I18n.T("window.path")}###xcpcam_path_window", new Vector2(680, 500), new Vector2(500, 360),
            () => config.UseBrioTheme, _main.DrawPathWindow);
        _constraintsWindow = new SubWindow(() => $"{I18n.T("window.constraints")}###xcpcam_constraints_window", new Vector2(640, 540), new Vector2(480, 360),
            () => config.UseBrioTheme, _main.DrawConstraintsWindow);
        _brioWindow = new SubWindow(() => $"{I18n.T("window.brio")}###xcpcam_brio_window", new Vector2(620, 460), new Vector2(460, 340),
            () => config.UseBrioTheme, _main.DrawBrioWindow);

        _main.OnOpenSettings = () => _settingsWindow.IsOpen = true;
        _main.OnOpenMiniTimelineSettings = _settingsWindow.OpenMiniTimelineTab;
        _main.OnToggleViewportToolbar = () =>
        {
            config.ShowViewportToolbar = !config.ShowViewportToolbar;
            _viewportToolbar.IsOpen = config.ShowViewportToolbar;
            _main.SaveConfig();
        };
        _main.IsPathWindowOpen = () => _pathWindow.IsOpen;
        _main.SetPathWindowOpen = open => _pathWindow.IsOpen = open ?? !_pathWindow.IsOpen;
        _main.IsConstraintsWindowOpen = () => _constraintsWindow.IsOpen;
        _main.SetConstraintsWindowOpen = open => _constraintsWindow.IsOpen = open ?? !_constraintsWindow.IsOpen;
        _main.IsBrioWindowOpen = () => _brioWindow.IsOpen;
        _main.SetBrioWindowOpen = open => _brioWindow.IsOpen = open ?? !_brioWindow.IsOpen;
        _main.OnGposeAutoOpened = () =>
        {
            _pathOpenBeforeGpose = _pathWindow.IsOpen;
            _constraintsOpenBeforeGpose = _constraintsWindow.IsOpen;
            _brioOpenBeforeGpose = _brioWindow.IsOpen;
            _settingsOpenBeforeGpose = _settingsWindow.IsOpen;
        };
        _main.OnGposeAutoClose = () =>
        {
            if(!_pathOpenBeforeGpose) _pathWindow.IsOpen = false;
            if(!_constraintsOpenBeforeGpose) _constraintsWindow.IsOpen = false;
            if(!_brioOpenBeforeGpose) _brioWindow.IsOpen = false;
            if(!_settingsOpenBeforeGpose) _settingsWindow.IsOpen = false;
        };

        // overlay first, then main (so MiniTimeline sees current-frame IsExpanded), then floating/secondary/settings
        _windows.AddWindow(_overlayWindow);
        _windows.AddWindow(_main);
        _windows.AddWindow(_miniTimeline);
        _windows.AddWindow(_viewportToolbar);
        _windows.AddWindow(_pathWindow);
        _windows.AddWindow(_constraintsWindow);
        _windows.AddWindow(_brioWindow);
        _windows.AddWindow(_settingsWindow);

        _commands.AddHandler(Command, new CommandInfo((_, _) => _main.ToggleManual())
        {
            HelpMessage = I18n.T("plugin.command_help"),
        });

        _pi.LanguageChanged += OnDalamudLanguageChanged;
        _pi.UiBuilder.DisableGposeUiHide = true;
        _pi.UiBuilder.Draw += Draw;
        _pi.UiBuilder.OpenMainUi += _main.ToggleManual;
        _pi.UiBuilder.OpenConfigUi += OpenSettings;
        _framework.Update += OnUpdate;
    }

    private void OnDalamudLanguageChanged(string langCode)
        => I18n.UpdateGameLanguage(_clientState.ClientLanguage.ToString(), (int)_clientState.ClientLanguage, langCode);

    private void OpenSettings() => _settingsWindow.IsOpen = true;

    private void OnUpdate(IFramework framework)
    {
        _brio.Tick();
        _main.FrameworkTick((float)framework.UpdateDelta.TotalSeconds);
    }

    private void Draw()
    {
        _overlayWindow.IsOpen = _clientState.IsGPosing;
        _windows.Draw();
        _main.FileDialog.Draw();
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _pi.LanguageChanged -= OnDalamudLanguageChanged;
        _pi.UiBuilder.Draw -= Draw;
        _pi.UiBuilder.OpenMainUi -= _main.ToggleManual;
        _pi.UiBuilder.OpenConfigUi -= OpenSettings;
        _commands.RemoveHandler(Command);
        _main.Dispose();
        _windows.RemoveAllWindows();
    }
}
