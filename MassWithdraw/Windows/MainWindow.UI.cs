﻿/*
===============================================================================
  MassWithdraw – MainWindow.UI.cs
===============================================================================

  Overview
  ---------------------------------------------------------------------------
  Defines the ImGui-based user interface for the MassWithdraw plugin.

  This window handles:
    • Visual layout and sizing
    • User input and interaction
    • Real-time feedback during item transfers

  The logic for filtering, preview computation, and transfer execution
  resides in MainWindow.Logic.cs.

===============================================================================
*/

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MassWithdraw.Windows;

public partial class MainWindow : Window, IDisposable
{
    // Layout & timing
    private const float AnchorGapX = 8f, FilterPanelHeight = 200f, ButtonWidth = 150f, HeaderIconTextSpacing = 6f;
    private const int   DelayMsDefault = 400;

   // Defines initial window style flags and sizing constraints.
    public MainWindow()
        : base("Mass Withdraw",
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize)
    {
        RespectCloseHotkey = false;
        SizeConstraints = new()
        {
            MinimumSize = new(280f, 0f),
            MaximumSize = new(float.MaxValue, float.MaxValue),
        };
    }

    //
    public void Dispose() { }

    /*
     * ---------------------------------------------------------------------------
     *  Draw() – Main render entry point
     * ---------------------------------------------------------------------------
     *  Called every frame by Dalamud’s window system. Anchors the window to the
     *  retainer inventory UI, determines current transfer state, and dispatches
     *  to either the idle or running UI modes.
     * ---------------------------------------------------------------------------
    */
    public override void Draw()
    {
        AnchorToRetainer();
        if (!IsInventoryRetainerOpen()) { IsOpen = false; return; }

        var running = _transfer.Running;

        int retStacks = 0, bagFree = 0, willMove = 0;
        TimeSpan eta = TimeSpan.Zero;

        if (!running)
            (retStacks, bagFree, willMove, eta) = ComputeTransferPreview(DelayMsDefault);

        float w = ImGui.GetContentRegionAvail().X;
        if (running) DrawRunningState(w);
        else         DrawIdleState(willMove, retStacks, bagFree, eta, w);
    }

    /*
     * ---------------------------------------------------------------------------
     *  DrawIdleState()
     * ---------------------------------------------------------------------------
     *  Displays the main “ready” interface when no transfer is in progress.
     *  Includes transfer button, preview info, filter section, and feedback text.
     * ---------------------------------------------------------------------------
    */
    private void DrawIdleState(int willMove, int retStacks, int bagFree, TimeSpan eta, float contentWidth)
    {
        if (willMove > 0)
        {
            CenteredText($"Will move {willMove} item{(willMove == 1 ? "" : "s")} (ETA ~ {Math.Max(0, (int)eta.TotalSeconds)}s)",
                new Vector4(0.8f, 0.9f, 1f, 1f));
            ImGui.Spacing();
        }

        ImGui.SetCursorPosX(MathF.Max(8f, (contentWidth - ButtonWidth) * 0.5f + 8f));

        bool canTransfer = willMove > 0 && (!_isFilterEnabled || _selectedCategoryIds.Count > 0);

        if (!canTransfer) ImGui.BeginDisabled();
        if (ImGui.Button("Transfer", new Vector2(ButtonWidth, 0)))
            StartMoveAllRetainerItems(DelayMsDefault);
        if (!canTransfer) ImGui.EndDisabled();

        if (!canTransfer)
        {
            ImGui.Spacing();
            string msg =
                (_isFilterEnabled && _selectedCategoryIds.Count == 0) ? "Select at least one category." :
                (retStacks == 0) ? (_isFilterEnabled ? "No items match the current filters." : "Retainer inventory is empty.") :
                (bagFree == 0) ? "Inventory full." :
                "Unable to transfer items. Check your filters or inventory.";
            CenteredText(msg, new Vector4(1f, 0.8f, 0.3f, 1f));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (DrawFilterHeaderButton(contentWidth)) _showFilterPanel = !_showFilterPanel;
        if (_showFilterPanel) DrawFiltersPanel();
    }

    /*
     * ---------------------------------------------------------------------------
     *  DrawRunningState()
     * ---------------------------------------------------------------------------
     *  Displays progress bar and Stop button while a transfer is active.
     * ---------------------------------------------------------------------------
    */
    private void DrawRunningState(float w)
    {
        DrawProgress(w);
        const float stopW = 160f;
        ImGui.SetCursorPosX(MathF.Max(0f, (w - stopW) * 0.5f));
        if (ImGui.Button("Stop Transfer", new Vector2(stopW, 0))) _cts?.Cancel();
    }

    /*
     * ---------------------------------------------------------------------------
     *  DrawFilterHeaderButton()
     * ---------------------------------------------------------------------------
     *  Renders the header toggle for the filter section, including arrow icon and
     *  current selection count.
     * ---------------------------------------------------------------------------
    */
    private bool DrawFilterHeaderButton(float width)
    {
        var style    = ImGui.GetStyle();
        string icon  = (_showFilterPanel ? FontAwesomeIcon.AngleDown : FontAwesomeIcon.AngleRight).ToIconString();
        string label = $"  Filters ({_selectedCategoryIds.Count})";

        float h      = MathF.Max(ImGui.GetTextLineHeight() + style.FramePadding.Y * 2f, ImGui.GetFrameHeight());
        bool clicked = ImGui.Button("##filterBtn", new Vector2(width, h));

        var dl   = ImGui.GetWindowDrawList();
        var min  = ImGui.GetItemRectMin();
        var max  = ImGui.GetItemRectMax();
        float y  = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) * 0.5f;
        uint col = ImGui.GetColorU32(ImGuiCol.Text);

        ImGui.PushFont(UiBuilder.IconFont);
        dl.AddText(new Vector2(min.X + style.FramePadding.X, y), col, icon);
        ImGui.PopFont();

        dl.AddText(new Vector2(min.X + style.FramePadding.X + ImGui.CalcTextSize(icon).X + HeaderIconTextSpacing, y), col, label);
        return clicked;
    }

    /*
     * ---------------------------------------------------------------------------
     *  DrawFiltersPanel()
     * ---------------------------------------------------------------------------
     *  Renders the filter selection panel with checkboxes for each category.
     * ---------------------------------------------------------------------------
    */
    private void DrawFiltersPanel()
    {
        RecomputeRetainerCategoryCounts();

        ImGui.BeginChild(
            "filterPanel",
            new Vector2(0, FilterPanelHeight * ImGui.GetIO().FontGlobalScale),
            true,
            ImGuiWindowFlags.AlwaysUseWindowPadding
        );

        bool hasSel = _selectedCategoryIds.Count > 0;
        if (!hasSel) ImGui.BeginDisabled();
        if (ImGui.Button("Clear")) _selectedCategoryIds.Clear();
        if (!hasSel) ImGui.EndDisabled();

        ImGui.Spacing();

        var cats = new (uint id, string label)[]
        {
            (CatAllGear,      "All gear"),
            (CatNonWhiteGear, "Non-white gear"),
            (CatMateria,      "Materia"),
            (CatConsumables,  "Consumables"),
            (CatCraftingMats, "Crafting mats"),
        };

        foreach (var (id, label) in cats)
            DrawOneFilterCheckbox(id, label);

        _isFilterEnabled = hasSel;

        ImGui.EndChild();
        ImGui.Spacing();
    }

    /*
     * ---------------------------------------------------------------------------
     *  DrawProgress()
     * ---------------------------------------------------------------------------
     *  Renders the animated progress bar during a running transfer, including
     *  percentage and ETA text overlay.
     * ---------------------------------------------------------------------------
    */
    private void DrawProgress(float w)
    {
        int moved = _transfer.Moved, total = _transfer.Total;
        float frac = total > 0 ? Math.Clamp((float)moved / total, 0f, 1f) : 0f;

        string overlay = total > 0
            ? $"{moved}/{total}  ({(int)(frac * 100 + 0.5f)}%)" +
            ((total - moved) > 0
                ? $"  •  ~{Math.Max(0, (int)(TimeSpan.FromMilliseconds((long)(total - moved) * DelayMsDefault).TotalSeconds))}s"
                : "")
            : $"{moved} moved";

        ImGui.ProgressBar(frac, new Vector2(w, 22f), overlay);
        ImGui.Spacing();
    }

    /*
     * ---------------------------------------------------------------------------
     *  DrawOneFilterCheckbox()
     * ---------------------------------------------------------------------------
     *  Renders a single filter checkbox with dynamic count and dimming.
     * ---------------------------------------------------------------------------
    */
    private void DrawOneFilterCheckbox(uint id, string name)
    {
        bool enabled = _selectedCategoryIds.Contains(id);
        int count = _retainerCountsByCategory.TryGetValue(id, out var c) ? c : 0;
        string label = count > 0 ? $"{name} ({count})" : name;

        bool dim = count == 0 && !enabled;
        if (dim) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.72f, 0.72f, 0.72f, 1f));

        if (ImGui.Checkbox($"{label}##cat{id}", ref enabled))
            if (enabled) _selectedCategoryIds.Add(id);
            else _selectedCategoryIds.Remove(id);

        if (dim) ImGui.PopStyleColor();
    }

    /*
     * ---------------------------------------------------------------------------
     *  CenteredText()
     * ---------------------------------------------------------------------------
     *  Utility helper that draws horizontally centered text within the current
     *  ImGui content region, optionally with a custom color.
     * ---------------------------------------------------------------------------
    */
    private static void CenteredText(string text, Vector4? color = null)
    {
        float x = MathF.Max(8f, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X) * 0.5f);
        ImGui.SetCursorPosX(x);
        if (color is { } c) ImGui.TextColored(c, text);
        else ImGui.TextUnformatted(text);
    }
}