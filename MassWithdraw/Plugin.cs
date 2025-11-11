using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MassWithdraw.Windows;
using MassWithdraw;

namespace MassWithdraw;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/masswithdraw";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("MassWithdraw");

    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private RetainerWatcher? retainerWatcher;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow();

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Mass Withdraw window to transfer all items from your retainer to your inventory."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        this.retainerWatcher = new RetainerWatcher(
            framework: Framework,
            isRetainerOpen: () => this.MainWindow.IsRetainerUIOpen(),
            setMainWindowOpen: open => this.MainWindow.IsOpen = open,
            isEnabled: () => this.Configuration.AutoOpenOnRetainer
        );

        Log.Information($"[MassWithdraw] Plugin initialized successfully. Ready for /masswithdraw command.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        this.retainerWatcher?.Dispose();
        this.retainerWatcher = null;
    }

    private void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim();

        if (a.Length == 0)
            return;

        if (a.StartsWith("transfer", StringComparison.OrdinalIgnoreCase))
        {
            this.MainWindow.StartTransferFromCommand();
            return;
        }
        if (a.StartsWith("config", StringComparison.OrdinalIgnoreCase))
        {
            this.ToggleConfigUi();
            return;
        }

        Plugin.ChatGui.Print("[MassWithdraw] Unknown subcommand. Available options:");
        Plugin.ChatGui.Print("[MassWithdraw] /masswithdraw transfer  → Trigger the mass withdraw transfer if possible");
        Plugin.ChatGui.Print("[MassWithdraw] /masswithdraw config    → Open the configuration window");
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}