/*
===============================================================================
  MassWithdraw – ConfigWindow.cs
===============================================================================

  Overview
  ---------------------------------------------------------------------------
  Defines the configuration window UI for MassWithdraw.
  This window allows the user to change persistent settings, such as whether
  the plugin’s main window should automatically open when a Retainer Inventory
  is visible.

===============================================================================
*/

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MassWithdraw.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    // Reference to the shared plugin configuration instance.
    private readonly Configuration configuration;

    /*
     * ---------------------------------------------------------------------------
     *  Constructor
     * ---------------------------------------------------------------------------
     *  Initializes the configuration window and sets fixed UI flags.
     * ---------------------------------------------------------------------------
    */
    public ConfigWindow(Plugin plugin)
        : base("Mass Withdraw — Settings###MassWithdrawConfig")
    {
        Flags = ImGuiWindowFlags.NoResize
              | ImGuiWindowFlags.NoCollapse
              | ImGuiWindowFlags.NoScrollbar
              | ImGuiWindowFlags.NoScrollWithMouse
              | ImGuiWindowFlags.AlwaysAutoResize;

        configuration = plugin.Configuration;
    }

    public void Dispose() { /* No cleanup required */ }

    /*
     * ---------------------------------------------------------------------------
     *  Draw()
     * ---------------------------------------------------------------------------
     *  Renders the configuration UI. Contains a single toggle option:
     *      [x] Auto-open when Retainer Inventory is visible
     *  Updates the configuration in real-time and persists it immediately.
     * ---------------------------------------------------------------------------
    */
    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f));

        var autoOpen = configuration.AutoOpenOnRetainer;
        if (ImGui.Checkbox("Auto-open when Retainer Inventory is visible", ref autoOpen))
        {
            configuration.AutoOpenOnRetainer = autoOpen;
            configuration.Save();
        }

        ImGui.PopStyleVar();
    }
}