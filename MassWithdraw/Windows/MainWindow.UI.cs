﻿using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MassWithdraw.Windows;

public partial class MainWindow : Window, IDisposable
{
    // Layout tokens
    private const float AnchorGapX = 8f;                // Gap between the Retainer addon and this window (X).
    private const int DelayMsDefault = 400;             // Delay (ms) between item moves.
    private const float FilterPanelHeight = 72f;        // Max height for the filters child; content auto-sizes up to this (then scrolls).
    private const float FilterRowSpacing = 10f;         // Vertical spacing between filter rows (used for auto height calc).
    private const float ButtonWidth = 150f;             // Standard button width.
    private const float ButtonSpacing = 12f;            // Horizontal spacing between buttons.
    private const float HeaderIconTextSpacing = 6f;     // Space between arrow icon and “Filters (N)” text.

    /// <summary>
    /// Configures a compact, auto-resizing window.
    /// </summary>
    public MainWindow()
        : base("Mass Withdraw",
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize)
    {
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(0f, 0f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    /// <summary>
    /// No unmanaged resources.
    /// </summary>
    public void Dispose() { /* no-op */ }

    /// <summary>
    /// Main UI composition: preview, action buttons, filters, and progress bar.
    /// </summary>
    public override void Draw()
    {

        // ------------------------------------------------------------------------
        // Snap the window next to the Retainer Inventory UI (if visible).
        // ------------------------------------------------------------------------
        AnchorToRetainer();

        // ------------------------------------------------------------------------
        // Cache transfer status and preview stats to avoid redundant field reads.
        // ------------------------------------------------------------------------
        bool retainerOpen = IsInventoryRetainerOpen();
        bool running      = _transfer.Running;

        int retStacks = 0, bagFree = 0, willMove = 0;
        TimeSpan eta = TimeSpan.Zero;

        if (!running && retainerOpen)
        {
            var p = ComputeTransferPreview(DelayMsDefault);
            retStacks = p.retStacks;
            bagFree   = p.bagFree;
            willMove  = p.willMove;
            eta       = p.eta;
        }

        float contentWidth = ImGui.GetContentRegionAvail().X;

        // ------------------------------------------------------------------------
        // Content rendering
        // ------------------------------------------------------------------------

        if (!running)
            DrawIdleState(retainerOpen, willMove, eta, contentWidth);
        else
            DrawRunningState(contentWidth);
    }

    /// <summary>
    /// Renders the static idle UI: preview, transfer button, and filters.
    /// </summary>
    private void DrawIdleState(bool retainerOpen, int willMove, TimeSpan eta, float contentWidth)
    {
        // ------------------------------------------------------------------------
        // Preview summary
        // ------------------------------------------------------------------------
        if (willMove > 0)
        {
            string line = $"Will move {willMove} item{(willMove == 1 ? "" : "s")} (ETA ~ {Math.Max(0, (int)eta.TotalSeconds)}s)";
            CenteredText(line, new Vector4(0.8f, 0.9f, 1f, 1f));
            ImGui.Spacing();
        }

        // ------------------------------------------------------------------------
        // Transfer button
        // ------------------------------------------------------------------------
         float buttonX = MathF.Max(8f, (contentWidth - ButtonWidth) * 0.5f + 8f);
         ImGui.SetCursorPosX(buttonX);

        bool disableTransfer =
            !retainerOpen ||
            willMove <= 0 ||
            (_isFilterEnabled && _selectedCategoryIds.Count == 0);

        if (disableTransfer)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Transfer", new Vector2(ButtonWidth, 0));
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("Transfer", new Vector2(ButtonWidth, 0)))
        {
            StartMoveAllRetainerItems(DelayMsDefault);
        }

        // ------------------------------------------------------------------------
        // Disabled state message
        // ------------------------------------------------------------------------
        if (disableTransfer)
        {
            ImGui.Spacing();
            string msg;

            if (!retainerOpen)
                msg = "Open your Retainer’s inventory window first.";
            else if (_isFilterEnabled && _selectedCategoryIds.Count == 0)
                msg = "Select at least one category.";
            else if (_isFilterEnabled && willMove == 0)
                msg = "No items match the current filters.";
            else
                msg = "Retainer inventory is empty or your bags are full.";

            CenteredText(msg, new Vector4(1f, 0.8f, 0.3f, 1f));
        }

        // ------------------------------------------------------------------------
        // Filter panel
        // ------------------------------------------------------------------------
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (DrawFilterHeaderButton(contentWidth))
            _showFilterPanel = !_showFilterPanel;

        if (_showFilterPanel)
            DrawFiltersPanel();
    }

    /// <summary>
    /// Renders the “in-progress” state UI: progress bar and Stop button.
    /// </summary>
    private void DrawRunningState(float contentWidth)
    {
        // ------------------------------------------------------------------------
        // Progress bar
        // ------------------------------------------------------------------------
        DrawProgress(contentWidth);

        // ------------------------------------------------------------------------
        // Stop button
        // ------------------------------------------------------------------------
        float stopWidth = 160f;
        ImGui.SetCursorPosX(MathF.Max(0f, (contentWidth - stopWidth) * 0.5f));

        if (ImGui.Button("Stop Transfer", new Vector2(stopWidth, 0)))
            _cts?.Cancel();
    }

    /// <summary>
    /// Draws the filter section header — an expandable button with arrow icon and “Filters (N)” label.
    /// </summary>
    private bool DrawFilterHeaderButton(float availableWidth)
    {
        // ------------------------------------------------------------------------
        // Style and layout references
        // ------------------------------------------------------------------------
        var style = ImGui.GetStyle();
        float btnW = availableWidth;

        // ------------------------------------------------------------------------
        // Compute icon and label
        // ------------------------------------------------------------------------
        string iconStr = (_showFilterPanel ? FontAwesomeIcon.AngleDown : FontAwesomeIcon.AngleRight).ToIconString();
        string labelStr = $"  Filters ({_selectedCategoryIds.Count})";

        // Compute total button height to fit either icon or text comfortably
        float btnH = MathF.Max(
            ImGui.GetTextLineHeight() + (style.FramePadding.Y * 2f),
            ImGui.GetFrameHeight()
        );

        // ------------------------------------------------------------------------
        // Create the invisible clickable area
        // ------------------------------------------------------------------------
        bool clicked = ImGui.Button("##filterBtn", new Vector2(btnW, btnH));

        // ------------------------------------------------------------------------
        // Positioning for overlayed text and icon
        // ------------------------------------------------------------------------
        var drawList = ImGui.GetWindowDrawList();
        var btnMin = ImGui.GetItemRectMin();
        var btnMax = ImGui.GetItemRectMax();
        float centerY = btnMin.Y + (btnMax.Y - btnMin.Y - ImGui.GetTextLineHeight()) * 0.5f;

        // ------------------------------------------------------------------------
        // Draw icon
        // ------------------------------------------------------------------------
        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(
            new Vector2(btnMin.X + style.FramePadding.X, centerY),
            ImGui.GetColorU32(ImGuiCol.Text),
            iconStr
        );
        ImGui.PopFont();

        // ------------------------------------------------------------------------
        // Draw label (after icon)
        // ------------------------------------------------------------------------
        float textX = btnMin.X + style.FramePadding.X + ImGui.CalcTextSize(iconStr).X + HeaderIconTextSpacing;
        drawList.AddText(
            new Vector2(textX, centerY),
            ImGui.GetColorU32(ImGuiCol.Text),
            labelStr
        );

        // ------------------------------------------------------------------------
        // Return click result
        // ------------------------------------------------------------------------
        return clicked;
    }

    /// <summary>
    /// Draws the filter panel contents.
    /// </summary>
    private void DrawFiltersPanel()
    {
        // ------------------------------------------------------------------------
        // Data preparation
        // ------------------------------------------------------------------------
        RecomputeRetainerCategoryCounts();

        // ------------------------------------------------------------------------
        // Panel sizing (scaled for DPI)
        // ------------------------------------------------------------------------
        float scaledHeight = FilterPanelHeight * ImGui.GetIO().FontGlobalScale;
        ImGui.BeginChild("filterPanel", new Vector2(0, scaledHeight), true, ImGuiWindowFlags.AlwaysUseWindowPadding);

        // ------------------------------------------------------------------------
        // Header actions
        // ------------------------------------------------------------------------
        if (ImGui.Button("Clear") && _selectedCategoryIds.Count > 0)
            _selectedCategoryIds.Clear();

        ImGui.Spacing();

        // ========================================================================
        // BEGIN: Non-white Gear Filter
        // ------------------------------------------------------------------------
        bool isEnabled = _selectedCategoryIds.Contains(CatNonWhiteGear);
        int  count     = _retainerCountsByCategory.TryGetValue(CatNonWhiteGear, out var c) ? c : 0;

        string labelBase = "Non-white gear";
        string label     = count > 0 ? $"{labelBase} ({count})" : labelBase;

        bool shouldDim = count == 0 && !isEnabled;
        if (shouldDim)
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.72f, 0.72f, 0.72f, 1f));

        if (ImGui.Checkbox($"{label}##cat{CatNonWhiteGear}", ref isEnabled))
        {
            if (isEnabled)
                _selectedCategoryIds.Add(CatNonWhiteGear);
            else
                _selectedCategoryIds.Remove(CatNonWhiteGear);
        }

        if (shouldDim)
            ImGui.PopStyleColor();
        // ========================================================================
        // END: Non-white Gear Filter
        // ========================================================================

        _isFilterEnabled = _selectedCategoryIds.Count > 0;   // State synchronization

        ImGui.EndChild();
        ImGui.Spacing();
    }

    /// <summary>
    /// Draws the transfer progress bar with percentage and ETA.
    /// </summary>
    private void DrawProgress(float contentWidth)
    {
        // ------------------------------------------------------------------------
        // Data snapshot (avoid redundant field reads during UI drawing)
        // ------------------------------------------------------------------------
        int moved = _transfer.Moved;
        int total = _transfer.Total;

        // ------------------------------------------------------------------------
        // Compute current completion ratio and clamp it safely to valid bounds
        // ------------------------------------------------------------------------
        float frac = (total > 0) ? (float)moved / total : 0f;
        frac = Math.Clamp(frac, 0f, 1f);

        // ------------------------------------------------------------------------
        // Displays context information above the bar
        // ------------------------------------------------------------------------
        string overlay;
        if (total > 0)
        {
            int pct = (int)(frac * 100f + 0.5f);
            int remaining = Math.Max(0, total - moved);

            // Estimate ETA using configured pacing (DelayMsDefault)
            var eta = TimeSpan.FromMilliseconds((long)remaining * DelayMsDefault);
            int etaSec = Math.Max(0, (int)eta.TotalSeconds);

            overlay = etaSec > 0
                ? $"{moved}/{total}  ({pct}%)  •  ~{etaSec}s"
                : $"{moved}/{total}  ({pct}%)";
        } else { overlay = $"{moved} moved"; }

        ImGui.ProgressBar(frac, new Vector2(contentWidth, 22f), overlay);   // Draw bar (fixed height: 22f).
        ImGui.Spacing();
    }

    // ============================================================================
    // UI HELPERS
    // ============================================================================

    /// <summary>
    /// Draws text horizontally centered in the current window.
    /// Optionally applies a custom color.
    /// </summary>
    private static void CenteredText(string text, Vector4? color = null)
    {
        // ------------------------------------------------------------------------
        // Layout computation
        // ------------------------------------------------------------------------
        // Compute available width (respects internal padding)
        float regionWidth = ImGui.GetContentRegionAvail().X;
        float textWidth   = ImGui.CalcTextSize(text).X;

        // Center text horizontally with a minimal safety margin
        float xOffset = MathF.Max(8f, (regionWidth - textWidth) * 0.5f);
        ImGui.SetCursorPosX(xOffset);

        // ------------------------------------------------------------------------
        // Render text (colored or default)
        // ------------------------------------------------------------------------
        if (color.HasValue)
            ImGui.TextColored(color.Value, text);
        else
            ImGui.TextUnformatted(text);
    }

}