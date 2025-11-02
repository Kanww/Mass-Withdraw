﻿using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MassWithdraw.Windows;

/// <summary>
/// MassWithdraw – primary ImGui window.
/// Clean, focused UX:
///  - Auto-anchors beside the Retainer Inventory (locked position).
///  - Idle preview: retainer stacks, bag free slots, ETA.
///  - One big action (Transfer) + Close or Stop while running.
///  - Skips Unique items already owned by the player.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    // =============================
    // Constants / Layout
    // =============================
    private const string WindowTitle = "Mass Withdraw";

    private const int DefaultMoveDelayMs = 400;         // Pacing between moves
    private const float MinWidth = 310f;
    private const float MinHeight = 170f;
    private const float ButtonWidth = 150f;
    private const float ButtonSpacing = 12f;
    private const float AnchorGap = 8f;                 // Gap between Retainer window and ours

    private static readonly string[] RetainerInventoryAddons =
        { "InventoryRetainer", "InventoryRetainerLarge" };

    // =============================
    // State
    // =============================
    private bool _isMovingAll;                          // true while async transfer is active
    private CancellationTokenSource? _moveCts;
    private int _movedCount;                            // total items moved in current run

    // =============================
    // Ctor / Dtor
    // =============================
    public MainWindow()
        : base(WindowTitle,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize)
    {
        RespectCloseHotkey = false;                     // ESC won’t close unexpectedly

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(0f, 0f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    // =============================
    // Addon helpers
    // =============================
    private static unsafe bool IsAnyAddonVisible(ReadOnlySpan<string> names, int instances = 2)
    {
        try
        {
            for (int i = 0; i < instances; i++)
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
        }
        catch { /* fall through */ }
        return false;
    }

    private static unsafe (bool ok, Vector2 pos, Vector2 size) TryGetAddonRect(ReadOnlySpan<string> names, int instances = 2)
    {
        try
        {
            for (int i = 0; i < instances; i++)
            {
                foreach (var n in names)
                {
                    var addon = Plugin.GameGui.GetAddonByName(n, i);
                    if (addon == null || addon.Address == nint.Zero)
                        continue;

                    var unit = (AtkUnitBase*)addon.Address;
                    if (!unit->IsVisible)
                        continue;

                    var pos = new Vector2(unit->X, unit->Y);
                    var size = new Vector2(unit->RootNode->Width, unit->RootNode->Height);
                    return (true, pos, size);
                }
            }
        }
        catch { /* fall through */ }
        return (false, Vector2.Zero, Vector2.Zero);
    }

    /// <summary>Exposed for the watcher; returns true if Retainer Inventory is open.</summary>
    public static unsafe bool IsInventoryRetainerOpenForWatcher()
        => IsAnyAddonVisible(RetainerInventoryAddons);

    private unsafe bool IsInventoryRetainerOpen()
        => IsAnyAddonVisible(RetainerInventoryAddons);

    // =============================
    // Positioning – lock beside Retainer Inventory
    // =============================
    public override void PreDraw()
    {
        // Window padding: 8px on each side
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f));
        var rect = TryGetAddonRect(RetainerInventoryAddons);
        if (!rect.ok)
            return;

        // Lock to the right side of the Retainer Inventory window.
        var anchorPos = new Vector2(rect.pos.X + rect.size.X + AnchorGap, rect.pos.Y);

        // We explicitly set the next window position before ImGui.Begin so it sticks.
        ImGui.SetNextWindowPos(anchorPos, ImGuiCond.Always);
    }

    // =============================
    // Item helpers
    // =============================
    private static bool IsUnique(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var row = sheet?.GetRow(itemId);
        return row?.IsUnique ?? false;
    }

    private static unsafe bool PlayerHasItem(uint itemId)
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

    // =============================
    // Preview / ETA
    // =============================
    private static unsafe (int retStacks, int bagFree, int willMove, TimeSpan eta) ComputePreview(int delayMs)
    {
        int retStacks = 0;
        int bagFree = 0;

        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return (0, 0, 0, TimeSpan.Zero);

        // Count retainer stacks across pages 1–7
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

        // Count free slots in Inventory1–4
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

    // =============================
    // Transfer logic
    // =============================
    private void StartMoveAllRetainerItemsAsync(int delayMs)
    {
        if (_isMovingAll) return;

        _isMovingAll = true;
        _movedCount = 0;
        _moveCts = new CancellationTokenSource();
        var token = _moveCts.Token;

        _ = Task.Run(() =>
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

                            _ = inv->MoveItemSlot(
                                srcType,
                                (ushort)slot,
                                (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)dstTypeInt,
                                (ushort)dstSlot,
                                true
                            );

                            _movedCount++;

                            Task.Delay(delayMs, token).Wait(token);
                        }
                    }

                    Plugin.ChatGui.Print($"[MassWithdraw] Done. Moved total: {_movedCount} item(s).");
                }
                catch (OperationCanceledException)
                {
                    Plugin.ChatGui.Print($"[MassWithdraw] Cancelled. Moved so far: {_movedCount} item(s).");
                }
                catch (Exception ex)
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

    // =============================
    // UI
    // =============================
    public override void Draw()
    {
        const int DelayMs = DefaultMoveDelayMs;
        bool hasInventoryRetainer = IsInventoryRetainerOpen();

        int retStacks = 0, bagFree = 0, willMove = 0;
        TimeSpan eta = TimeSpan.Zero;

        if (!_isMovingAll && hasInventoryRetainer)
        {
            var p = ComputePreview(DelayMs);
            retStacks = p.retStacks;
            bagFree   = p.bagFree;
            willMove  = p.willMove;
            eta       = p.eta;

            // Pretty, centered preview
            string label1 = "Items in Retainer:";
            string label2 = "Player Inventory (Free Slots):";
            int pad = Math.Max(label1.Length, label2.Length);

            string line1 = $"{label1.PadRight(pad)} {retStacks}";
            string line2 = $"{label2.PadRight(pad)} {bagFree}";
            string line3 = $"Will move: {willMove} items (ETA ~ {Math.Max(0, (int)eta.TotalSeconds)}s)";

            CenteredText(line1);
            CenteredText(line2);
            CenteredText(line3);
            ImGui.Spacing();
        }

        float contentWidth = ImGui.GetContentRegionAvail().X;

        if (!_isMovingAll)
        {
            // Two centered buttons: Begin / Close
            float groupWidth = (ButtonWidth * 2f) + ButtonSpacing;
            ImGui.SetCursorPosX(MathF.Max(8f, (contentWidth - groupWidth) * 0.5f + 8f));

            bool disableBegin = !hasInventoryRetainer || willMove <= 0;

            if (disableBegin)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Transfer", new Vector2(ButtonWidth, 0));
                ImGui.EndDisabled();
            }
            else if (ImGui.Button("Transfer", new Vector2(ButtonWidth, 0)))
            {
                StartMoveAllRetainerItemsAsync(DelayMs);
            }

            ImGui.SameLine(0, ButtonSpacing);

            if (ImGui.Button("Close", new Vector2(ButtonWidth, 0)))
                IsOpen = false;

            if (disableBegin)
            {
                ImGui.Spacing();
                var msg = hasInventoryRetainer
                    ? "Retainer inventory is empty or your bags are full."
                    : "Open your Retainer’s inventory window first.";
                CenteredColoredText(msg, new Vector4(1f, 0.8f, 0.3f, 1f));
            }
        }
        else
        {
            // Progress + centered Stop button
            CenteredColoredText($"Moving items... {_movedCount} transferred so far.", new Vector4(0.4f, 1f, 0.4f, 1f));
            ImGui.Spacing();

            float stopWidth = 160f;
            ImGui.SetCursorPosX(MathF.Max(0f, (contentWidth - stopWidth) * 0.5f));
            if (ImGui.Button("Stop Transfer", new Vector2(stopWidth, 0)))
                _moveCts?.Cancel();
        }
        }

    public override void PostDraw()
    {
        // Pop our WindowPadding style var
        ImGui.PopStyleVar();
    }

    // =============================
    // Small UI helpers
    // =============================
    private static void CenteredText(string text)
    {
        float contentW = ImGui.GetContentRegionAvail().X;
        var w = ImGui.CalcTextSize(text).X;
        var x = MathF.Max(0, (contentW - w) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + x);
        ImGui.TextUnformatted(text);
    }

    private static void CenteredColoredText(string text, Vector4 color)
    {
        float contentW = ImGui.GetContentRegionAvail().X;
        var w = ImGui.CalcTextSize(text).X;
        var x = MathF.Max(0, (contentW - w) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + x);
        ImGui.TextColored(color, text);
    }
}