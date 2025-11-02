using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MassWithdraw.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

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

    public void Dispose() { }

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