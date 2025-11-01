using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

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
            Plugin.ChatGui.Print("[MassWithdraw] Starting mass withdraw (coming soon).");
        }

        ImGui.SameLine();

        if (ImGui.Button("Close"))
        {
            this.IsOpen = false;
        }
    }
}
