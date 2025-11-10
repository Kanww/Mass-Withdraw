/*
===============================================================================
  MassWithdraw – Plugin.cs
===============================================================================

  Overview
  ---------------------------------------------------------------------------
  This is the *main entry point* of the MassWithdraw Dalamud plugin.
  It initializes all Dalamud services, registers commands, creates UI windows,
  and manages the plugin’s lifecycle (startup and shutdown).

  • Implements Dalamud’s `IDalamudPlugin` interface.
  • Provides access to Dalamud services (chat, game UI, data, etc.) through
    dependency injection via `[PluginService]` attributes.
  • Registers the `/masswithdraw` command.
  • Initializes and manages two main UI windows:
      - ConfigWindow — for user preferences and settings.
      - MainWindow  — the primary interface for withdrawing items.
  • Sets up automatic behavior (auto-open when retainer inventory is opened)
    through the `RetainerWatcher` helper.
  • Handles proper cleanup and unregistration when the plugin is disposed.
  
===============================================================================
*/

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

    /*
     * ---------------------------------------------------------------------------
     *  Plugin constructor and initialization
     * ---------------------------------------------------------------------------
     *  Loads configuration, registers commands, initializes windows, and sets up
     *  automatic retainer monitoring behavior.
     * ---------------------------------------------------------------------------
     */
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

        // Hook UI drawing and open buttons into Dalamud’s UI builder.
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // Retainer watcher handles auto-opening when a retainer inventory is detected.
        this.retainerWatcher = new RetainerWatcher(
            framework: Framework,
            isRetainerOpen: () => this.MainWindow.IsInventoryRetainerOpen(),
            setMainWindowOpen: open => this.MainWindow.IsOpen = open,
            isEnabled: () => this.Configuration.AutoOpenOnRetainer
        );

        Log.Information($"[MassWithdraw] Plugin initialized successfully. Ready for /masswithdraw command.");
    }

    /*
     * ---------------------------------------------------------------------------
     *  Plugin disposal and cleanup
     * ---------------------------------------------------------------------------
     *  Ensures that all event handlers, windows, and command registrations are
     *  properly released when the plugin is unloaded or Dalamud shuts down.
     * ---------------------------------------------------------------------------
     */
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

    /*
     * ---------------------------------------------------------------------------
     *  Command handling and UI toggling
     * ---------------------------------------------------------------------------
     *  Handles `/masswithdraw` commands:
     *    • /masswithdraw transfer → Start transfer immediately (if possible)
     *    • /masswithdraw config   → Open configuration window
     * ---------------------------------------------------------------------------
     */
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