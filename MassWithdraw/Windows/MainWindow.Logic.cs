using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MassWithdraw.Windows;

public partial class MainWindow
{
    private bool _showFilterPanel = false;
    private bool _isFilterEnabled = false;
    private readonly HashSet<uint> _selectedCategoryIds = new();
    private readonly Dictionary<uint, string> _catNames = new();
    private readonly Dictionary<uint, int> _retainerCountsByCategory = new();
    private const uint CatNonWhiteGear = 999001;
    private sealed class TransferState
    {
        public volatile bool Running;
        public int Moved;
        public int Total;
    }
    private TransferState _transfer = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Determines whether the specified <paramref name="itemId"/> refers to an item
    /// flagged as “Unique” in the Item sheet (cannot be held in duplicate).
    /// </summary>
    private static bool IsUnique(uint itemId)
    {
        // ------------------------------------------------------------------------
        // Retrieve the item row safely from Lumina’s Item sheet
        // ------------------------------------------------------------------------
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var row   = sheet?.GetRow(itemId);

        // ------------------------------------------------------------------------
        // Return the Unique flag (defaults to false when missing)
        // ------------------------------------------------------------------------
        return row?.IsUnique ?? false;
    }

    /// <summary>
    /// Determines whether the given <paramref name="itemId"/> corresponds to an equippable
    /// gear piece of rarity higher than “white” (rarity > 1).
    /// </summary>
    private static bool IsNonWhiteGear(uint itemId)
    {
        // ------------------------------------------------------------------------
        // Retrieve the Item sheet (read-only data from Lumina)
        // ------------------------------------------------------------------------
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (sheet == null)
            return false;

        // GetRow is non-nullable in your Lumina; use a cheap guard instead of null checks
        var row = sheet.GetRow(itemId);

        // A valid gear item has an EquipSlotCategory > 0
        // “Non-white” means rarity strictly greater than 1
        return row.EquipSlotCategory.RowId > 0 && row.Rarity > 1;
    }

    /// <summary>
    /// Rebuilds the per-retainer counts used by the filter panel.
    /// Currently tracks only the “Non-white gear” synthetic category.
    /// </summary>
    private unsafe void RecomputeRetainerCategoryCounts()
    {
        // ------------------------------------------------------------------------
        // Reset and early exit if inventory is unavailable
        // ------------------------------------------------------------------------
        _retainerCountsByCategory.Clear();

        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null)
            return;

        // ------------------------------------------------------------------------
        // Scan all retainer pages once; accumulate locally for minimal dict churn
        // ------------------------------------------------------------------------
        const int RetainerFirst = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
        const int RetainerLast  = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7;

        int nonWhiteCount = 0;

        for (int t = RetainerFirst; t <= RetainerLast; t++)
        {
            var cont = inv->GetInventoryContainer((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t);
            if (cont == null)
                continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var slot = cont->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                    continue;

                if (IsNonWhiteGear(slot->ItemId))
                    nonWhiteCount++;
            }
        }

        // ------------------------------------------------------------------------
        // Publish result only if there’s something to show
        // ------------------------------------------------------------------------
        if (nonWhiteCount > 0)
            _retainerCountsByCategory[CatNonWhiteGear] = nonWhiteCount;
    }

    /// <summary>
    /// Determines whether an item should be transferred based on active filters.
    /// </summary>
    private bool ShouldTransfer(uint itemId)
    => !_isFilterEnabled
       || (_selectedCategoryIds.Contains(CatNonWhiteGear) && IsNonWhiteGear(itemId));

    /// <summary>
    /// Quick preview of a pending transfer:
    /// - retStacks : eligible non-empty stacks in the open retainer (honors filters + unique-skip)
    /// - bagFree   : free slots across Inventory1–Inventory4
    /// - willMove  : min(retStacks, bagFree)
    /// - eta       : rough time at <paramref name="delayMs"/> per moved stack
    /// </summary>
    private unsafe (int retStacks, int bagFree, int willMove, TimeSpan eta) ComputeTransferPreview(int delayMs)
    {
        // ------------------------------------------------------------------------
        // Data source
        // ------------------------------------------------------------------------
        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null)
            return (0, 0, 0, TimeSpan.Zero);

        // ------------------------------------------------------------------------
        // Index ranges (explicit to avoid magic numbers)
        // ------------------------------------------------------------------------
        const int RetainerFirst = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1; // <-- if you saw a typo earlier, fix to FFXIV
        const int RetainerLast  = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7;

        const int BagFirst      = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1;
        const int BagPages      = 4;

        int retStacks = 0;
        int bagFree   = 0;

        // ------------------------------------------------------------------------
        // Count eligible retainer stacks
        // - non-empty slots
        // - pass current category filter via ShouldTransfer
        // - skip Unique if player already holds one (to match real move logic)
        // ------------------------------------------------------------------------
        for (int t = RetainerFirst; t <= RetainerLast; t++)
        {
            var srcType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t;
            var cont    = inv->GetInventoryContainer(srcType);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var it = cont->GetInventorySlot(s);
                if (it == null || it->ItemId == 0 || it->Quantity == 0)
                    continue;

                if (!ShouldTransfer(it->ItemId))
                    continue;

                if (IsUnique(it->ItemId) && PlayerHasItem(it->ItemId))
                    continue;

                retStacks++;
            }
        }

        // ------------------------------------------------------------------------
        // Count free bag slots (Inventory1–4)
        // A slot counts as free when null OR ItemId == 0.
        // ------------------------------------------------------------------------
        for (int i = 0; i < BagPages; i++)
        {
            var bagType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)(BagFirst + i);
            var cont    = inv->GetInventoryContainer(bagType);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var slot = cont->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0)
                    bagFree++;
            }
        }

        // ------------------------------------------------------------------------
        // Result synthesis
        // ------------------------------------------------------------------------
        int willMove = Math.Min(retStacks, bagFree);

        // Protect against negative/overflow and respect 0 delay gracefully
        int safeDelay = Math.Max(0, delayMs);
        var eta       = TimeSpan.FromMilliseconds((long)willMove * safeDelay);

        return (retStacks, bagFree, willMove, eta);
    }

    /// <summary>
    /// Starts an asynchronous transfer of all eligible items from the currently open
    /// retainer inventory into the player's bags. Honors filters, skips Unique items
    /// already owned, and paces via <paramref name="delayMs"/> per move.
    /// </summary>
    private void StartMoveAllRetainerItems(int delayMs)
    {
        // ------------------------------------------------------------------------
        // Concurrency guard + initial snapshot
        // ------------------------------------------------------------------------
        if (_transfer.Running)
            return;

        var preview = ComputeTransferPreview(delayMs);
        if (preview.willMove <= 0)
        {
            Plugin.ChatGui.Print("[MassWithdraw] Nothing to transfer.");
            return;
        }

        _transfer = new TransferState
        {
            Running = true,
            Moved   = 0,
            Total   = preview.willMove
        };

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // ------------------------------------------------------------------------
        // Background worker
        // ------------------------------------------------------------------------
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

                    // ------------------------------------------------------------
                    // Retainer containers range
                    // ------------------------------------------------------------
                    const int RetainerFirst = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
                    const int RetainerLast  = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7;

                    // ------------------------------------------------------------
                    // Local helper: find first free player bag slot (Inventory1..4)
                    // ------------------------------------------------------------
                    bool TryFindFreeBagSlot(out FFXIVClientStructs.FFXIV.Client.Game.InventoryType dstType, out int dstSlot)
                    {
                        const int FirstBag = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1;
                        const int BagPages = 4;

                        for (int i = 0; i < BagPages; i++)
                        {
                            var type = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)(FirstBag + i);
                            var cont = inv->GetInventoryContainer(type);
                            if (cont == null) continue;

                            for (int s = 0; s < cont->Size; s++)
                            {
                                var slot = cont->GetInventorySlot(s);
                                if (slot == null || slot->ItemId == 0)
                                {
                                    dstType = type;
                                    dstSlot = s;
                                    return true;
                                }
                            }
                        }

                        dstType = default;
                        dstSlot = -1;
                        return false;
                    }

                    // ------------------------------------------------------------
                    // Main transfer loop
                    // ------------------------------------------------------------
                    for (int t = RetainerFirst; t <= RetainerLast; t++)
                    {
                        token.ThrowIfCancellationRequested();

                        var srcType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t;
                        var cont    = inv->GetInventoryContainer(srcType);
                        if (cont == null)
                            continue;

                        for (int slot = 0; slot < cont->Size; slot++)
                        {
                            token.ThrowIfCancellationRequested();

                            var item = cont->GetInventorySlot(slot);
                            if (item == null || item->ItemId == 0 || item->Quantity == 0)
                                continue;

                            // Respect user filter
                            if (!ShouldTransfer(item->ItemId))
                                continue;

                            // Unique safeguard: skip if already owned in player bags
                            if (IsUnique(item->ItemId) && PlayerHasItem(item->ItemId))
                                continue;

                            // Destination search
                            if (!TryFindFreeBagSlot(out var dstType, out var dstSlot))
                            {
                                Plugin.ChatGui.Print($"[MassWithdraw] Stopped: no free bag space. Moved {_transfer.Moved} item(s).");
                                return;
                            }

                            // Move full stack (ignore return; pace via delay)
                            _ = inv->MoveItemSlot(
                                srcType,
                                (ushort)slot,
                                dstType,
                                (ushort)dstSlot,
                                true
                            );

                            _transfer.Moved++;

                            // Pace the queue (async-friendly)
                            Task.Delay(delayMs, token).Wait(token);
                        }
                    }

                    Plugin.ChatGui.Print($"[MassWithdraw] Done. Moved total: {_transfer.Moved} item(s).");
                }
                catch (OperationCanceledException)
                {
                    Plugin.ChatGui.Print($"[MassWithdraw] Cancelled. Moved so far: {_transfer.Moved} item(s).");
                }
                catch (Exception ex)
                {
                    Plugin.ChatGui.Print($"[MassWithdraw] Error in async move: {ex.Message}");
                }
                finally
                {
                    // Reset state
                    _transfer.Running = false;
                    _cts?.Dispose();
                    _cts = null;
                }
            }
        });
    }

    /// <summary>
    /// Returns true if the player currently has at least one item with the given
    /// <paramref name="itemId"/> in their main inventory (Inventory1–Inventory4).
    /// </summary>
    private static unsafe bool PlayerHasItem(uint itemId)
    {
        // ------------------------------------------------------------------------
        // Early validation
        // ------------------------------------------------------------------------
        if (itemId == 0)
            return false;

        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null)
            return false;


        // ------------------------------------------------------------------------
        // Inventory range setup (Inventory1..Inventory4)
        // ------------------------------------------------------------------------
        const int FirstBag = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1;
        const int BagPages = 4;

        // ------------------------------------------------------------------------
        // Scan all bag containers for matching item IDs
        // ------------------------------------------------------------------------
        for (int i = 0; i < BagPages; i++)
        {
            var type = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)(FirstBag + i);
            var cont = inv->GetInventoryContainer(type);
            if (cont == null || cont->Size == 0)
                continue;

            // --------------------------------------------------------------------
            // Iterate each slot and check for matching item IDs
            // --------------------------------------------------------------------
            for (int s = 0; s < cont->Size; s++)
            {
                var slot = cont->GetInventorySlot(s);
                if (slot == null)
                    continue;

                // Match found — quantity check avoids transient empty slots
                if (slot->ItemId == itemId && slot->Quantity > 0)
                    return true;
            }
        }

        // ------------------------------------------------------------------------
        // No matching item found in any container
        // ------------------------------------------------------------------------
        return false;
    }
}