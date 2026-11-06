using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XcpCam;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xcpcam";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commands;
    private readonly WindowSystem _windows = new("XcpCam");
    private readonly MainWindow _main;

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, ITargetManager targets, IClientState clientState, IPluginLog log)
    {
        _pi = pi;
        _commands = commands;

        _main = new MainWindow(targets, clientState, log);
        _windows.AddWindow(_main);

        _commands.AddHandler(Command, new CommandInfo((_, _) => _main.Toggle())
        {
            HelpMessage = "打开 XcpCam：抓取多个镜头关键帧并导出 XAT .xcp",
        });

        _pi.UiBuilder.DisableGposeUiHide = true;
        _pi.UiBuilder.Draw += Draw;
        _pi.UiBuilder.OpenMainUi += _main.Toggle;
    }

    private void Draw()
    {
        _windows.Draw();
        _main.FileDialog.Draw();
    }

    public void Dispose()
    {
        _pi.UiBuilder.Draw -= Draw;
        _pi.UiBuilder.OpenMainUi -= _main.Toggle;
        _commands.RemoveHandler(Command);
        _windows.RemoveAllWindows();
    }
}
