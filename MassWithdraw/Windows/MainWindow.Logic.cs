/*
===============================================================================
  MassWithdraw – MainWindow.Logic.cs
===============================================================================

  Overview
  ---------------------------------------------------------------------------
  Core logic for the MassWithdraw window:
    • Filtering and category bookkeeping
    • Transfer preview (counts + ETA)
    • Asynchronous transfer execution with pacing and cancellation
    • Utility lookups (unique items, rarity checks, player inventory scan)
    • Category detection via ItemSearchCategory and simple heuristics

===============================================================================
*/

using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using ItemRow = Lumina.Excel.Sheets.Item;

namespace MassWithdraw.Windows;

public partial class MainWindow
{
    // Cached reference to the Item Excel sheet (lazy-loaded on first access)
    private static Lumina.Excel.ExcelSheet<ItemRow>? _itemSheet;

    // Helper to fetch an item row by its ID, initializing the sheet if needed
    private static ItemRow? GetItemRow(uint itemId) =>
        (_itemSheet ??= Plugin.DataManager.GetExcelSheet<ItemRow>())?.GetRow(itemId);

    // UI state flags
    private bool _showFilterPanel = false;
    private bool _isFilterEnabled = false;
    
    // User filter selections and computed category counts
    private readonly HashSet<uint> _selectedCategoryIds = new();
    private readonly Dictionary<uint, int> _retainerCountsByCategory = new();

    // Custom logical category identifiers (plugin-specific, not FFXIV constants)
    private const uint
        CatNonWhiteGear  = 999001,
        CatAllGear       = 999002,
        CatMateria       = 999003,
        CatConsumables   = 999004,
        CatCraftingMats  = 999005;
    
    // Maps category IDs to their classification predicates
    private readonly Dictionary<uint, Func<ItemRow, bool>> _categoryPredicatesRow = new()
    {
        [CatNonWhiteGear] = IsNonWhiteGearRow,
        [CatAllGear]      = IsAllGearRow,
        [CatMateria]      = IsMateriaRow,
        [CatConsumables]  = IsConsumableRow,
        [CatCraftingMats] = IsCraftMatRow,
    };

    // Search category sets
    private static readonly HashSet<uint> SearchCats_Materia   = [57];
    private static readonly HashSet<uint> SearchCats_Materials = [44, 47, 48, 49, 50, 51, 52, 53, 54, 55, 58, 59];
    
    // Transfer state
    private sealed class TransferState
    {
        public volatile bool Running;
        public int Moved, Total;
    }
    private TransferState _transfer = new();
    private CancellationTokenSource? _cts;

    /*
    * ---------------------------------------------------------------------------
    *  Category predicate functions
    * ---------------------------------------------------------------------------
    *  These helpers classify an ItemRow into its broad logical category.
    *  Used for filtering and previews.
    * ---------------------------------------------------------------------------
    */
    private static bool IsNonWhiteGearRow(ItemRow row) =>
        row.EquipSlotCategory.RowId > 0 && row.Rarity > 1;

    private static bool IsAllGearRow(ItemRow row) =>
        row.EquipSlotCategory.RowId > 0;

    private static bool IsMateriaRow(ItemRow row) =>
        SearchCats_Materia.Contains(row.ItemSearchCategory.RowId);

    private static bool IsConsumableRow(ItemRow row) =>
        row.EquipSlotCategory.RowId == 0 && row.ItemAction.RowId > 0;

    private static bool IsCraftMatRow(ItemRow row) =>
        SearchCats_Materials.Contains(row.ItemSearchCategory.RowId) && row.ItemAction.RowId == 0;
    

    /*
     * ---------------------------------------------------------------------------
     *  RecomputeRetainerCategoryCounts()
     * ---------------------------------------------------------------------------
     *  Scans the retainer inventory and updates the _retainerCountsByCategory
     *  dictionary with the current counts of items per category.
     * ---------------------------------------------------------------------------
    */
    private unsafe void RecomputeRetainerCategoryCounts()
    {
        _retainerCountsByCategory.Clear();

        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return;

        for (int t = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
                t <= (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7; t++)
        {
            var cont = inv->GetInventoryContainer((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var slot = cont->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0 || slot->Quantity == 0) continue;

                var row = GetItemRow(slot->ItemId);
                if (row == null) continue;

                foreach (var (catId, pred) in _categoryPredicatesRow)
                {
                    if (!pred(row.Value)) continue;
                    _retainerCountsByCategory.TryGetValue(catId, out var c);
                    _retainerCountsByCategory[catId] = c + 1;
                }
            }
        }
    }
    
    /*
     * ---------------------------------------------------------------------------
     *  ShouldTransfer()
     * ---------------------------------------------------------------------------
     *  Determines if the given ItemRow should be transferred based
     *  on the current filter settings.
     * ---------------------------------------------------------------------------
    */
    private bool ShouldTransfer(ItemRow row) =>
        !_isFilterEnabled ||
        (_selectedCategoryIds.Count > 0 &&
        _selectedCategoryIds.Any(id => _categoryPredicatesRow.TryGetValue(id, out var pred) && pred(row)));

    /*
     * ---------------------------------------------------------------------------
     *  ComputeTransferPreview()
     * ---------------------------------------------------------------------------
     *  Scans the retainer and player inventories to compute how many item stacks
     *  would be transferred, how much free space is available, and the estimated
     *  time to complete the transfer with the given delay.
     * ---------------------------------------------------------------------------
    */
    private unsafe (int retStacks, int bagFree, int willMove, TimeSpan eta) ComputeTransferPreview(int delayMs)
    {
        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return (0, 0, 0, TimeSpan.Zero);

        int retStacks = 0, bagFree = 0;

        // Count eligible stacks in retainer pages
        for (int t = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
                t <= (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7; t++)
        {
            var cont = inv->GetInventoryContainer((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var it = cont->GetInventorySlot(s);
                var row = (it == null || it->ItemId == 0 || it->Quantity == 0) ? null : GetItemRow(it->ItemId);
                if (row == null || !ShouldTransfer(row.Value) || (row.Value.IsUnique && PlayerHasItem(it->ItemId))) continue;
                retStacks++;
            }
        }

        // Count free bag slots
        for (int i = 0; i < 4; i++)
        {
            var cont = inv->GetInventoryContainer(
                (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)((int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1 + i));
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var slot = cont->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0) bagFree++;
            }
        }

        int willMove = Math.Min(retStacks, bagFree);
        var eta = TimeSpan.FromMilliseconds((long)Math.Max(0, delayMs) * willMove);
        return (retStacks, bagFree, willMove, eta);
    }

    /*
     * ---------------------------------------------------------------------------
     *  StartMoveAllRetainerItems()
     * ---------------------------------------------------------------------------
     *  Initiates the asynchronous transfer of all eligible items from the
     *  retainer to the player inventory with the specified delay.
     * ---------------------------------------------------------------------------
    */
    private void StartMoveAllRetainerItems(int delayMs)
    {
        if (_transfer.Running) return;

        var p = ComputeTransferPreview(delayMs);
        if (p.willMove <= 0) { Plugin.ChatGui.Print("[MassWithdraw] Nothing to transfer."); return; }

        _transfer = new TransferState { Running = true, Moved = 0, Total = p.willMove };

        _cts = new CancellationTokenSource();
        _ = RunTransferAsync(delayMs, _cts.Token);
    }

    /*
     * ---------------------------------------------------------------------------
     *  RunTransferAsync()
     * ---------------------------------------------------------------------------
     *  Core async loop that performs the item transfers with delays.
     * ---------------------------------------------------------------------------
    */
    private async Task RunTransferAsync(int delayMs, CancellationToken token)
    {
        try
        {
            // Avoid repeated bag scans for the same Unique itemId during this run
            var seenUnique = new HashSet<uint>();

            // Ensure InventoryManager exists
            unsafe
            {
                if (FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance() == null)
                {
                    Plugin.ChatGui.Print("[MassWithdraw] InventoryManager not available.");
                    return;
                }
            }

            for (int t = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
                    t <= (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7; t++)
            {
                token.ThrowIfCancellationRequested();

                for (int slot = 0; ; slot++)
                {
                    token.ThrowIfCancellationRequested();

                    // Read current slot -> itemId (unsafe, no await)
                    uint itemId;
                    unsafe
                    {
                        var inv  = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                        var type = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t;
                        var cont = inv->GetInventoryContainer(type);
                        if (cont == null) break;
                        if (slot >= cont->Size) break;

                        var it = cont->GetInventorySlot(slot);
                        if (it == null || it->ItemId == 0 || it->Quantity == 0) continue;
                        itemId = it->ItemId;
                    }

                    // Row checks (safe)
                    var row = GetItemRow(itemId);
                    if (row == null || !ShouldTransfer(row.Value))
                        continue;

                    if (row.Value.IsUnique && (!seenUnique.Add(itemId) || PlayerHasItem(itemId)))
                        continue;

                    // Find a free bag slot (unsafe helper, no await)
                    if (!FindFreeBagSlot(out var dstType, out var dstSlot))
                    {
                        Plugin.ChatGui.Print($"[MassWithdraw] Stopped: no free bag space. Moved {_transfer.Moved} item(s).");
                        return;
                    }

                    // Perform the move (unsafe, no await)
                    unsafe
                    {
                        var inv  = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                        var type = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t;
                        _ = inv->MoveItemSlot(type, (ushort)slot, dstType, (ushort)dstSlot, true);
                    }

                    _transfer.Moved++;
                    if (delayMs > 0) await Task.Delay(delayMs, token);
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
            _transfer.Running = false;
            _cts?.Dispose();
            _cts = null;
        }

        // Local helper: finds one free inventory slot (unsafe inside, no await)
        static bool FindFreeBagSlot(out FFXIVClientStructs.FFXIV.Client.Game.InventoryType dstType, out int dstSlot)
        {
            unsafe
            {
                var inv2 = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                if (inv2 == null) { dstType = default; dstSlot = -1; return false; }

                for (int i = 0; i < 4; i++)
                {
                    var type = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)
                            ((int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1 + i);
                    var cont = inv2->GetInventoryContainer(type);
                    if (cont == null) continue;

                    for (int bagSlot = 0; bagSlot < cont->Size; bagSlot++)
                    {
                        var free = cont->GetInventorySlot(bagSlot);
                        if (free == null || free->ItemId == 0)
                        {
                            dstType = type;
                            dstSlot = bagSlot;
                            return true;
                        }
                    }
                }

                dstType = default;
                dstSlot = -1;
                return false;
            }
        }
    }

    /*
     * ---------------------------------------------------------------------------
     *  PlayerHasItem()
     * ---------------------------------------------------------------------------
     *  Returns true if the player already has at least one of the given item
     *  across Inventory1–4. Used to prevent duplicate “Unique” items.
     * ---------------------------------------------------------------------------
    */
    private static unsafe bool PlayerHasItem(uint itemId)
    {
        if (itemId == 0) return false;

        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return false;

        for (int i = 0; i < 4; i++)
        {
            var cont = inv->GetInventoryContainer(
                (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)
                ((int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1 + i));
            if (cont == null) continue;

            for (int s = 0, n = cont->Size; s < n; s++)
            {
                var slot = cont->GetInventorySlot(s);
                if (slot != null && slot->ItemId == itemId && slot->Quantity > 0)
                    return true;
            }
        }
        return false;
    }

    /*
     * ---------------------------------------------------------------------------
     *  StartTransferFromCommand()
     * ---------------------------------------------------------------------------
     *  Entry point for the `/masswithdraw transfer` command.
     *  Verifies all preconditions and launches the async transfer if possible.
     * ---------------------------------------------------------------------------
    */
    public void StartTransferFromCommand()
    {
        if (!IsInventoryRetainerOpen())
        { Plugin.ChatGui.PrintError("[MassWithdraw] Open your Retainer’s inventory window first."); return; }

        var (retStacks, bagFree, willMove, _) = ComputeTransferPreview(DelayMsDefault);
        if (willMove <= 0)
        {
            var msg = (bagFree == 0 && retStacks > 0) ? "Inventory full."
                    : (retStacks == 0)                ? "No items eligible to transfer (check filters)."
                                                      : "Nothing to transfer.";
            Plugin.ChatGui.PrintError($"[MassWithdraw] {msg}");
            return;
        }

        StartMoveAllRetainerItems(DelayMsDefault);
        Plugin.ChatGui.Print("[MassWithdraw] Transfer started…");
    }
}