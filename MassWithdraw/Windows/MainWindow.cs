using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MassWithdraw.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string goatImagePath;
    private readonly Plugin plugin;

    // We give this window a hidden ID using ##.
    // The user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    public MainWindow(Plugin plugin, string goatImagePath)
        : base("Mass Withdraw", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.goatImagePath = goatImagePath;
        this.plugin = plugin;
    }

    public void Dispose() { }

    private unsafe bool IsRetainerListOpen()
    {
        try
        {
            return Plugin.GameGui.GetAddonByName("RetainerList", 1, out var addonPtr)
                && addonPtr != nint.Zero
                && ((AtkUnitBase*)addonPtr)->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Mass Withdraw v0.0.1");
        ImGui.Separator();
        ImGui.TextWrapped("Instructions:");
        ImGui.BulletText("1. Talk to a Retainer Bell.");
        ImGui.BulletText("2. Open your Retainer List window.");
        ImGui.BulletText("3. Type /masswithdraw to start.");
        ImGui.Spacing();

        if (ImGui.Button("Start Mass Withdraw"))
        {
            if (IsRetainerListOpen())
                Plugin.ChatGui.Print("[MassWithdraw] Retainer List detected — ready for next step!");
            else
                Plugin.ChatGui.Print("[MassWithdraw] Please open the Retainer List at a bell first.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Close"))
        {
            this.IsOpen = false;
        }
    }
}
