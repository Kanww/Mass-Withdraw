﻿using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MassWithdraw.Windows;

public partial class MainWindow : Window, IDisposable
{
#region UI Constants & Flags

    private const float AnchorGapX             = 8f;
    private const float FilterPanelHeight      = 200f;
    private const float ButtonWidth            = 150f;
    private const float HeaderIconTextSpacing  = 6f;

    private const int   transferDelayMs        = 400;

    private const ImGuiWindowFlags WindowFlags =
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.AlwaysAutoResize;

#endregion

#region Lifecycle

    /**
     * * Initializes the main window with title, size constraints, and flags.
     */
    public MainWindow(): base("Mass Withdraw", WindowFlags)
    {
        RespectCloseHotkey = false;

        SizeConstraints = new()
        {
            MinimumSize = new(280f, 0f),
            MaximumSize = new(float.MaxValue, float.MaxValue),
        };
    }
    
    /**
     * * Disposes of resources used by the MainWindow instance.
     */
    public void Dispose() { }

#endregion

#region Draw Components


    /**
     * * Main rendering entry point for the window.
     *   Draws UI components based on transfer state.
     */
    public override void Draw()
    {
        AnchorToRetainer();
        if (!IsRetainerUIOpen())
        { 
            if (transferSession.Running)
                cancellationTokenSource?.Cancel();
                
            IsOpen = false;
            return;
        }

        bool isRunning = transferSession.Running;

        TransferPreview preview = default;
        if (!isRunning)
            preview = GenerateTransferPreview();

        float contentWidth = ImGui.GetContentRegionAvail().X;

        if (isRunning)
            DrawRunningState(contentWidth);
        else
            DrawIdleState(
                preview.itemsToMove,
                preview.transferStacks,
                preview.totalStacks,
                preview.inventoryFreeSlots,
                contentWidth
            );
    }

    /**
     * * Renders the UI elements shown when a transfer is currently running.
     * <param name="contentWidth">Horizontal space for layout</param>
     */
    private void DrawRunningState(float contentWidth)
    {
        DrawProgress(contentWidth);

        const float StopButtonWidth = 160f;

        ImGui.Spacing();
        ImGui.SetCursorPosX(MathF.Max(8f, (contentWidth - StopButtonWidth) * 0.5f + 8f));

        bool canStop = cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested;

        if (!canStop) ImGui.BeginDisabled();
        if (ImGui.Button("Stop Transfer", new Vector2(StopButtonWidth, 0)))
            cancellationTokenSource?.Cancel();
        if (!canStop) ImGui.EndDisabled();

        if (!canStop && ImGui.IsItemHovered())
            ImGui.SetTooltip("Stopping…");
    }

    /**
     * * Displays a progress bar and text showing transfer progress.
     * <param name="contentWidth">Horizontal space for the progress bar</param>
     */
    private void DrawProgress(float contentWidth)
    {
        int movedItems = transferSession.Moved;
        int totalItems = transferSession.Total;

        float progressFraction = totalItems > 0
            ? Math.Clamp((float)movedItems / totalItems, 0f, 1f)
            : 0f;

        string progressText = totalItems > 0
            ? $"{movedItems}/{totalItems}  ({(int)(progressFraction * 100 + 0.5f)}%)"
            : $"{movedItems} moved";

        ImGui.ProgressBar(progressFraction, new Vector2(contentWidth, 22f), progressText);
        ImGui.Spacing();
    }

    /**
     * * Renders the UI when no transfer is active.
     *   Shows transfer button, filter toggle, and informational messages.
     * <param name="itemsToMove">Number of items ready to be moved</param>
     * <param name="transferStacks">Number of stacks that will be transferred</param>
     * <param name="totalStacks">Total number of stacks in retainer inventory</param>
     * <param name="inventoryFreeSlots">Inventory slots in player inventory</param>
     * <param name="contentWidth">Horizontal space for layout</param>
     */
    private void DrawIdleState(int itemsToMove, int transferStacks, int totalStacks, int inventoryFreeSlots, float contentWidth)
    {
        var infoColor    = new Vector4(0.8f, 0.9f, 1f, 1f);
        var warningColor = new Vector4(1f, 0.8f, 0.3f, 1f);

        bool hasTransferableItems = itemsToMove > 0;

        if (hasTransferableItems)
        {
            CenteredText($"Will move {itemsToMove} item{(itemsToMove == 1 ? "" : "s")}", infoColor);
            ImGui.Spacing();
        }

        ImGui.SetCursorPosX(MathF.Max(8f, (contentWidth - ButtonWidth) * 0.5f + 8f));

        if (!hasTransferableItems) ImGui.BeginDisabled();
        if (ImGui.Button("Transfer", new Vector2(ButtonWidth, 0)))
            StartTransfer(transferDelayMs);
        if (!hasTransferableItems) ImGui.EndDisabled();

        if (!hasTransferableItems)
        {
            ImGui.Spacing();

            string msg = (totalStacks, transferStacks, inventoryFreeSlots) switch
            {
                (0, _, _) => "Retainer inventory is empty.",
                (_, 0, _) => "No items eligible to transfer.",
                (_, _, 0) => "Inventory full.",
                _         => "Nothing to transfer."
            };

            CenteredText(msg, warningColor);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (DrawFilterHeaderButton(contentWidth))
            isFilterPanelVisible = !isFilterPanelVisible;

        if (isFilterPanelVisible)
            DrawFiltersPanel();
    }

    /**
     * * Draws the collapsible filter header button and label.
     * <param name="contentWidth">Available horizontal space for layout</param>
     * <return type="bool">True if the filter header button was clicked</return>
     */
    private bool DrawFilterHeaderButton(float contentWidth)
    {
        var style = ImGui.GetStyle();

        string icon  = (isFilterPanelVisible ? FontAwesomeIcon.AngleDown : FontAwesomeIcon.AngleRight).ToIconString();
        string label = $"  Filters ({selectedCategoryIds.Count})";

        float buttonHeight = MathF.Max(ImGui.GetTextLineHeight() + style.FramePadding.Y * 2f, ImGui.GetFrameHeight());
        bool isClicked = ImGui.Button("##FilterHeaderButton", new Vector2(contentWidth, buttonHeight));

        var drawList   = ImGui.GetWindowDrawList();
        var rectMin    = ImGui.GetItemRectMin();
        var rectMax    = ImGui.GetItemRectMax();
        float textY    = rectMin.Y + (rectMax.Y - rectMin.Y - ImGui.GetTextLineHeight()) * 0.5f;
        uint textColor = ImGui.GetColorU32(ImGuiCol.Text);

        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(new Vector2(rectMin.X + style.FramePadding.X, textY), textColor, icon);
        ImGui.PopFont();

        float iconWidth = ImGui.CalcTextSize(icon).X;
        float textX     = rectMin.X + style.FramePadding.X + iconWidth + HeaderIconTextSpacing;
        drawList.AddText(new Vector2(textX, textY), textColor, label);

        return isClicked;
    }

    /**
     * * Displays the filter selection panel containing category checkboxes.
     */
    private void DrawFiltersPanel()
    {
        RecountRetainerCategoryCounts();

        float scaledHeight = FilterPanelHeight * ImGui.GetIO().FontGlobalScale;

        ImGui.BeginChild(
            "FilterPanel",
            new Vector2(0, scaledHeight),
            true,
            ImGuiWindowFlags.AlwaysUseWindowPadding
        );

        bool hasSelection = selectedCategoryIds.Count > 0;
        if (!hasSelection) ImGui.BeginDisabled();
        if (ImGui.Button("Clear")) selectedCategoryIds.Clear();
        if (!hasSelection) ImGui.EndDisabled();

        ImGui.Spacing();

        var categories = new (uint id, string label)[] {
            (GearId,              "Any gear"),
            (RareGearId,          "Rare gear"),
            (MateriaId,           "Materia"),
            (ConsumablesId,       "Consumables"),
            (CraftingMaterialsId, "Crafting mats"),
        };

        foreach (var (id, label) in categories)
            DrawOneFilterCheckbox(id, label);

        ImGui.EndChild();
        ImGui.Spacing();
    }

    /**
     * * Draws an individual checkbox for a given item category.
     * <param name="categoryId">Unique ID of the category</param>
     * <param name="labelText">Display label for the category</param>
     */
    private void DrawOneFilterCheckbox(uint categoryId, string labelText)
    {
        bool isSelected = selectedCategoryIds.Contains(categoryId);
        int itemCount   = retainerCategoryCounts.TryGetValue(categoryId, out var count) ? count : 0;

        string displayLabel = itemCount > 0
            ? $"{labelText} ({itemCount})"
            : labelText;

        bool shouldDim = itemCount == 0 && !isSelected;

        var dimColor = new Vector4(0.72f, 0.72f, 0.72f, 1f);

        if (shouldDim)
            ImGui.PushStyleColor(ImGuiCol.Text, dimColor);

        if (ImGui.Checkbox($"{displayLabel}##Category{categoryId}", ref isSelected))
        {
            if (isSelected)
                selectedCategoryIds.Add(categoryId);
            else
                selectedCategoryIds.Remove(categoryId);
        }

        if (shouldDim)
            ImGui.PopStyleColor();
    }

#endregion

#region Utilities

    /**
     * * Displays centered text with optional color tint in the current ImGui window.
     * <param name="text">Text to be displayed</param>
     * <param name="color">Optional color tint for the text</param>
     */
    private static void CenteredText(string text, Vector4? color = null)
    {
        float textWidth    = ImGui.CalcTextSize(text).X;
        float contentWidth = ImGui.GetContentRegionAvail().X;
        float posX         = MathF.Max(8f, (contentWidth - textWidth) * 0.5f);

        ImGui.SetCursorPosX(posX);

        if (color is { } tint)
            ImGui.TextColored(tint, text);
        else
            ImGui.TextUnformatted(text);
    }

#endregion
}