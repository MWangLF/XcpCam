using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XcpCam.BrioInterop;

namespace XcpCam;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xcpcam";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commands;
    private readonly IFramework _framework;
    private readonly WindowSystem _windows = new("XcpCam");
    private readonly MainWindow _main;
    private readonly BrioBridge _brio;

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, ITargetManager targets, IClientState clientState,
        IGameGui gameGui, IFramework framework, IPluginLog log)
    {
        _pi = pi;
        _commands = commands;
        _framework = framework;

        _brio = new BrioBridge(log);
        var overlay = new Overlay(gameGui, clientState, targets);
        _main = new MainWindow(targets, clientState, log, _brio, overlay);
        _windows.AddWindow(_main);

        _commands.AddHandler(Command, new CommandInfo((_, _) => _main.Toggle())
        {
            HelpMessage = "打开 XcpCam：抓取多个镜头关键帧，生成平滑运镜并导出 XAT .xcp / 绑定 Brio 时间轴",
        });

        _pi.UiBuilder.DisableGposeUiHide = true;
        _pi.UiBuilder.Draw += Draw;
        _pi.UiBuilder.OpenMainUi += _main.Toggle;
        _framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        _brio.Tick();
        _main.FrameworkTick((float)framework.UpdateDelta.TotalSeconds);
    }

    private void Draw()
    {
        _main.DrawOverlay();
        _windows.Draw();
        _main.FileDialog.Draw();
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _pi.UiBuilder.Draw -= Draw;
        _pi.UiBuilder.OpenMainUi -= _main.Toggle;
        _commands.RemoveHandler(Command);
        _main.Dispose();
        _windows.RemoveAllWindows();
    }
}
