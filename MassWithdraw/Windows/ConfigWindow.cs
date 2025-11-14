using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MassWithdraw.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;

    /**
     * * Initializes the configuration window with layout flags and plugin configuration reference.
     * <param name="plugin">The parent plugin instance providing access to configuration data</param>
     */
    public ConfigWindow(Plugin plugin): base("Mass Withdraw — Settings###MassWithdrawConfig")
    {
        Flags = ImGuiWindowFlags.NoResize
              | ImGuiWindowFlags.NoCollapse
              | ImGuiWindowFlags.NoScrollbar
              | ImGuiWindowFlags.NoScrollWithMouse
              | ImGuiWindowFlags.AlwaysAutoResize;

        this.plugin = plugin; 
        configuration = plugin.Configuration;
    }

    /**
     * * Disposes of resources used by the ConfigWindow instance.
     *   Currently performs no cleanup actions.
     */
    public void Dispose() { }

    /**
     * * Renders the configuration window UI.
     *   Displays toggles and settings allowing user customization of plugin behavior.
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var anchor = configuration.AnchorWindow;
        if (ImGui.Checkbox("Anchor window next to Retainer Inventory", ref anchor))
        {
            configuration.AnchorWindow = anchor;
            configuration.Save();

            if (!anchor)
                plugin.MainWindow.ClearAnchor();
        }

        ImGui.PopStyleVar();
    }
}