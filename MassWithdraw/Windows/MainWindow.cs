﻿/*
 * MassWithdraw – Retainer Item Transfer Tool
 * ------------------------------------------------------------
 * File:        MainWindow.cs
 * Description: Primary ImGui window and UX for MassWithdraw.
 *              Provides preview (counts + ETA) and an async,
 *              cancellable “withdraw all items” action.
 *
 * Author:      Kanwa
 * Repository:  https://github.com/Kanww/Mass-Withdraw
 * Version:     0.1.0
 *
 * Notes:
 * - Uses FFXIVClientStructs for direct inventory access (unsafe).
 * - UI built with Dalamud ImGui helpers.
 */

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MassWithdraw.Windows;

/// <summary>
/// Main UI window for MassWithdraw. Provides:
/// - A compact preview (retainer stacks, free bag slots, ETA)
/// - A single action to withdraw all items asynchronously
/// - Cancellation while running
/// </summary>
public class MainWindow : Window, IDisposable
{
    // ------------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------------

    private bool _isMovingAll; // true while async transfer is active
    private System.Threading.CancellationTokenSource? _moveCts;
    private int _movedCount;   // total items moved this run

    /// <summary>
    /// Initialize window chrome and layout constraints.
    /// </summary>
    public MainWindow()
        : base("Mass Withdraw",
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse)
    {
        // Keep ESC from closing the window unexpectedly.
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(310, 170),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    // ------------------------------------------------------------------------
    // Addon Visibility Helpers
    // ------------------------------------------------------------------------

    /// <summary>
    /// True if the Retainer List window (at the bell) is visible.
    /// </summary>
    private unsafe bool IsRetainerListOpen()
    {
        try
        {
            for (int i = 0; i <= 1; i++)
            {
                var addon = Plugin.GameGui.GetAddonByName("RetainerList", i);
                if (addon != null && addon.Address != nint.Zero &&
                    ((AtkUnitBase*)addon.Address)->IsVisible)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True if the retainer inventory window is open (normal or large).
    /// </summary>
    private unsafe bool IsInventoryRetainerOpen()
    {
        try
        {
            string[] names = { "InventoryRetainer", "InventoryRetainerLarge" };
            for (int i = 0; i <= 1; i++)
            {
                foreach (var n in names)
                {
                    var addon = Plugin.GameGui.GetAddonByName(n, i);
                    if (addon != null && addon.Address != nint.Zero &&
                        ((AtkUnitBase*)addon.Address)->IsVisible)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------------
    // Item Helpers
    // ------------------------------------------------------------------------

    /// <summary>
    /// Returns true if <paramref name="itemId"/> is marked as Unique in the Item sheet.
    /// </summary>
    private static bool IsUnique(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var row = sheet?.GetRow(itemId);
        return row?.IsUnique ?? false;
    }

    /// <summary>
    /// Returns true if the player currently holds an item with <paramref name="itemId"/>
    /// in Inventory1–4 (main bag pages).
    /// </summary>
    private unsafe static bool PlayerHasItem(uint itemId)
    {
        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return false;

        for (int i = 0; i < 4; i++)
        {
            var invType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)
                ((int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1 + i);

            var cont = inv->GetInventoryContainer(invType);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var slot = cont->GetInventorySlot(s);
                if (slot != null && slot->ItemId == itemId)
                    return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------------
    // Preview / ETA
    // ------------------------------------------------------------------------

    /// <summary>
    /// Compute a quick preview:
    /// - retStacks: non-empty retainer stacks
    /// - bagFree:   free slots in player inventory
    /// - willMove:  stacks that can actually be moved (no merge logic yet)
    /// - eta:       estimated duration at the given delay per move
    /// </summary>
    private unsafe (int retStacks, int bagFree, int willMove, TimeSpan eta) ComputePreview(int delayMs)
    {
        int retStacks = 0;
        int bagFree = 0;

        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return (0, 0, 0, TimeSpan.Zero);

        // Count retainer stacks
        int tStart = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
        int tEnd   = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7;

        for (int t = tStart; t <= tEnd; t++)
        {
            var cont = inv->GetInventoryContainer((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var it = cont->GetInventorySlot(s);
                if (it != null && it->ItemId != 0 && it->Quantity > 0)
                    retStacks++;
            }
        }

        // Count free player bag slots
        for (int i = 0; i < 4; i++)
        {
            var invType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)
                ((int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1 + i);

            var cont = inv->GetInventoryContainer(invType);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var it = cont->GetInventorySlot(s);
                if (it == null || it->ItemId == 0)
                    bagFree++;
            }
        }

        int willMove = Math.Min(retStacks, bagFree);
        var eta = TimeSpan.FromMilliseconds((long)willMove * delayMs);

        return (retStacks, bagFree, willMove, eta);
    }

    // ------------------------------------------------------------------------
    // Transfer Logic
    // ------------------------------------------------------------------------

    /// <summary>
    /// Asynchronously transfer all items from the currently open retainer to player inventory.
    /// - Skips Unique items if the player already holds one
    /// - Honors a per-move delay to avoid UI/network pressure
    /// - Cancellable via Stop button
    /// </summary>
    private void StartMoveAllRetainerItemsAsync(int delayMs)
    {
        if (_isMovingAll) return;

        _isMovingAll = true;
        _movedCount = 0;
        _moveCts = new System.Threading.CancellationTokenSource();
        var token = _moveCts.Token;

        System.Threading.Tasks.Task.Run(() =>
        {
            unsafe
            {
                try
                {
                    var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                    if (inv == null)
                    {
                        Plugin.ChatGui.Print("[MassWithdraw] InventoryManager not available.");
                        return;
                    }

                    int tStart = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
                    int tEnd   = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7;

                    for (int t = tStart; t <= tEnd; t++)
                    {
                        token.ThrowIfCancellationRequested();

                        var srcType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t;
                        var cont = inv->GetInventoryContainer(srcType);
                        if (cont == null) continue;

                        for (int slot = 0; slot < cont->Size; slot++)
                        {
                            token.ThrowIfCancellationRequested();

                            var item = cont->GetInventorySlot(slot);
                            if (item == null || item->ItemId == 0 || item->Quantity == 0)
                                continue;

                            // Unique safeguard: skip if already owned
                            if (IsUnique(item->ItemId) && PlayerHasItem(item->ItemId))
                            {
                                Plugin.ChatGui.Print($"[MassWithdraw] Skipped unique item (ItemId {item->ItemId}) — already in player inventory.");
                                continue;
                            }

                            // Find first free player slot (Inventory1–4)
                            int dstTypeInt = -1, dstSlot = -1;
                            for (int i = 0; i < 4 && dstSlot < 0; i++)
                            {
                                var invType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)
                                    ((int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1 + i);
                                var dstCont = inv->GetInventoryContainer(invType);
                                if (dstCont == null) continue;

                                for (int s = 0; s < dstCont->Size; s++)
                                {
                                    var d = dstCont->GetInventorySlot(s);
                                    if (d == null || d->ItemId == 0)
                                    {
                                        dstTypeInt = (int)invType;
                                        dstSlot = s;
                                        break;
                                    }
                                }
                            }

                            if (dstSlot < 0)
                            {
                                Plugin.ChatGui.Print($"[MassWithdraw] Stopped: no free bag space. Moved {_movedCount} item(s).");
                                return;
                            }

                            // Move full stack. We don’t trust return code; we pace via delay.
                            _ = inv->MoveItemSlot(
                                srcType,
                                (ushort)slot,
                                (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)dstTypeInt,
                                (ushort)dstSlot,
                                true
                            );

                            _movedCount++;

                            // Pace the queue; prevents UI hitching and server churn.
                            System.Threading.Tasks.Task
                                .Delay(delayMs, token)
                                .Wait(token);
                        }
                    }

                    Plugin.ChatGui.Print($"[MassWithdraw] Done. Moved total: {_movedCount} item(s).");
                }
                catch (System.OperationCanceledException)
                {
                    Plugin.ChatGui.Print($"[MassWithdraw] Cancelled. Moved so far: {_movedCount} item(s).");
                }
                catch (System.Exception ex)
                {
                    Plugin.ChatGui.Print($"[MassWithdraw] Error in async move: {ex.Message}");
                }
                finally
                {
                    _isMovingAll = false;
                    _moveCts?.Dispose();
                    _moveCts = null;
                }
            }
        });
    }

    // ------------------------------------------------------------------------
    // UI
    // ------------------------------------------------------------------------

    /// <summary>
    /// Draw ImGui layout: preview (when idle) + Begin/Close or Stop while running.
    /// </summary>
    public override void Draw()
    {
        const int DelayMs = 400; // default pacing; tweak via config later
        ImGui.SetWindowSize(new Vector2(310, 170), ImGuiCond.Once);

        bool hasInventoryRetainer = IsInventoryRetainerOpen();

        // Preview (computed only when idle and inventory is open)
        int retStacks = 0, bagFree = 0, willMove = 0;
        TimeSpan eta = TimeSpan.Zero;

        if (!_isMovingAll && hasInventoryRetainer)
        {
            var p = ComputePreview(DelayMs);
            retStacks = p.retStacks;
            bagFree   = p.bagFree;
            willMove  = p.willMove;
            eta       = p.eta;

            // Align labels by padding to same width
            string label1 = "Items in Retainer:";
            string label2 = "Player Inventory (Free Slots):";
            int pad = Math.Max(label1.Length, label2.Length);

            string line1 = $"{label1.PadRight(pad)} {retStacks}";
            string line2 = $"{label2.PadRight(pad)} {bagFree}";
            string line3 = $"Will move: {willMove} items (ETA ~ {Math.Max(0, (int)eta.TotalSeconds)}s)";

            float winW = ImGui.GetWindowSize().X;
            var w1 = ImGui.CalcTextSize(line1).X; ImGui.SetCursorPosX(MathF.Max(0, (winW - w1) * 0.5f)); ImGui.TextUnformatted(line1);
            var w2 = ImGui.CalcTextSize(line2).X; ImGui.SetCursorPosX(MathF.Max(0, (winW - w2) * 0.5f)); ImGui.TextUnformatted(line2);
            var w3 = ImGui.CalcTextSize(line3).X; ImGui.SetCursorPosX(MathF.Max(0, (winW - w3) * 0.5f)); ImGui.TextUnformatted(line3);
            ImGui.Spacing();
        }
        else if (!_isMovingAll && !hasInventoryRetainer)
        {
            // Gentle reminder if user hasn't opened the retainer inventory yet
            var warn = "Open your Retainer’s inventory window first.";
            var warnW = ImGui.CalcTextSize(warn).X;
            ImGui.SetCursorPosX(MathF.Max(0, (ImGui.GetWindowSize().X - warnW) * 0.5f));
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), warn);
            ImGui.Spacing();
        }

        float windowWidth = ImGui.GetWindowSize().X;

        if (!_isMovingAll)
        {
            // Centered button group: Begin / Close
            float buttonWidth = 150f;
            float spacing = 12f;
            float groupWidth = (buttonWidth * 2f) + spacing;
            ImGui.SetCursorPosX(MathF.Max(0f, (windowWidth - groupWidth) * 0.5f));

            bool disableBegin = !hasInventoryRetainer || willMove <= 0;

            if (disableBegin)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Begin Withdraw", new Vector2(buttonWidth, 0));
                ImGui.EndDisabled();
            }
            else if (ImGui.Button("Begin Withdraw", new Vector2(buttonWidth, 0)))
            {
                StartMoveAllRetainerItemsAsync(DelayMs);
            }

            ImGui.SameLine(0, spacing);

            if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
                IsOpen = false;

            if (disableBegin)
            {
                ImGui.Spacing();
                var msg = hasInventoryRetainer
                    ? "Retainer inventory is empty or your bags are full."
                    : "Open your Retainer’s inventory window first.";
                var msgW = ImGui.CalcTextSize(msg).X;
                ImGui.SetCursorPosX(MathF.Max(0f, (windowWidth - msgW) * 0.5f));
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), msg);
            }
        }
        else
        {
            // Progress + centered Stop button
            var msg = $"Moving items... {_movedCount} transferred so far.";
            var msgW = ImGui.CalcTextSize(msg).X;
            ImGui.SetCursorPosX(MathF.Max(0f, (windowWidth - msgW) * 0.5f));
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), msg);
            ImGui.Spacing();

            float stopWidth = 160f;
            ImGui.SetCursorPosX(MathF.Max(0f, (windowWidth - stopWidth) * 0.5f));
            if (ImGui.Button("Stop Transfer", new Vector2(stopWidth, 0)))
                _moveCts?.Cancel();
        }
    }
}