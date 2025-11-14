using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel;
using GameStructs = FFXIVClientStructs.FFXIV.Client.Game;
using ItemRow = Lumina.Excel.Sheets.Item;


namespace MassWithdraw.Windows;

public partial class MainWindow
{

#region Config & IDs
    private const uint
        RareGearId           = 999001,
        GearId               = 999002,
        MateriaId            = 999003,
        ConsumablesId        = 999004,
        CraftingMaterialsId  = 999005;
    private readonly Dictionary<uint, Func<ItemRow, bool>> categoryFilters = new()
    {
        [RareGearId]          = IsRareGear,
        [GearId]              = IsGear,
        [MateriaId]           = IsMateria,
        [ConsumablesId]       = IsConsumable,
        [CraftingMaterialsId] = IsCraftingMaterial,
    };
    private static readonly HashSet<uint> materiaCategoryIds   = [57];
    private static readonly HashSet<uint> materialsCategoryIds = [44, 47, 48, 49, 50, 51, 52, 53, 54, 55, 58, 59];
    private static readonly GameStructs.InventoryType[] RetainerPages =
    {
        GameStructs.InventoryType.RetainerPage1,
        GameStructs.InventoryType.RetainerPage2,
        GameStructs.InventoryType.RetainerPage3,
        GameStructs.InventoryType.RetainerPage4,
        GameStructs.InventoryType.RetainerPage5,
        GameStructs.InventoryType.RetainerPage6,
        GameStructs.InventoryType.RetainerPage7,
    };
    private static readonly GameStructs.InventoryType[] PlayerInventoryPages =
    {
        GameStructs.InventoryType.Inventory1,
        GameStructs.InventoryType.Inventory2,
        GameStructs.InventoryType.Inventory3,
        GameStructs.InventoryType.Inventory4,
    };
#endregion

#region Types
    private sealed class TransferState
    {
        public volatile bool Running;
        public volatile int Moved;
        public volatile int Total;
    }
    private readonly record struct TransferPreview(
        int totalStacks,
        int transferStacks,
        int inventoryFreeSlots,
        int itemsToMove
    );
#endregion

#region Fields
    private static ExcelSheet<ItemRow>? itemSheetCache;
    private bool isFilterPanelVisible = false;
    private readonly HashSet<uint> selectedCategoryIds = new();
    private readonly Dictionary<uint, int> retainerCategoryCounts = new();
    private readonly TransferState transferSession = new();
    private CancellationTokenSource? cancellationTokenSource;
    private static readonly Random delayRandom = new();
    private int inventoryContainerOffset = 0;
    private int inventorySlotIndex = 0;
#endregion

#region Data Access & Timing

    /**
     * * Retrieves an item’s data row from the Excel sheet using its ID
     * <param name="itemId">The unique ID of the item to retrieve</param>
     * <return type="ItemRow?">The matching ItemRow, or null if not found</return>
    */
    private static ItemRow? GetItemRowById(uint itemId)
    {
        if (itemSheetCache == null)
            itemSheetCache = Plugin.DataManager.GetExcelSheet<ItemRow>();

        return itemSheetCache?.GetRow(itemId);
    }

    /**
     * * Resets the internal inventory navigation pointers
     * Used before starting a new transfer or inventory scan
     */
    private void ResetInventoryCursor()
    {
        inventoryContainerOffset = 0;
        inventorySlotIndex = 0;
    }

    /**
     * * Produces a randomized delay value around a given base delay
     * <param name="baseDelay">The base delay in milliseconds</param>
     * <return type="int">A humanized delay value in milliseconds</return>
     */
    private static int GenerateHumanizedDelay(int baseDelay)
    {
        if (baseDelay <= 0)
            return 0;

        int randomRange = Math.Max(20, (int)(baseDelay * 0.25));

        int delayOffset;
        lock (delayRandom) 
        {
            delayOffset = delayRandom.Next(-randomRange, randomRange + 1);
        }

        return Math.Max(20, baseDelay + delayOffset);
    }

#endregion

#region Category Filters

    /**
     * * Determines whether the given item is gear
     * <param name="item">The item row to evaluate</param>
     * <return type="bool">True if the item is equippable gear; otherwise, false</return>
     */
    private static bool IsGear(ItemRow item)
    {
        bool isGear = item.EquipSlotCategory.RowId > 0;
        return isGear;
    }

    /**
     * * Determines whether the given item is rare gear
     * <param name="item">The item row to evaluate</param>
     * <return type="bool">True if the item is gear and has rarity above normal</return>
     */
    private static bool IsRareGear(ItemRow item)
    {
        bool isGear = item.EquipSlotCategory.RowId > 0;
        bool isRare = item.Rarity > 1;

        return isGear && isRare;
    }

    /**
     * * Determines whether the given item is classified as materia
     * <param name="item">The item row to evaluate</param>
     * <return type="bool">True if the item belongs to a known materia search category</return>
     */
    private static bool IsMateria(ItemRow item)
    {
        bool isMateria = materiaCategoryIds.Contains(item.ItemSearchCategory.RowId);
        return isMateria;
    }

    /**
     * * Determines whether the given item is a consumable
     * <param name="item">The item row to evaluate</param>
     * <return type="bool">True if the item is non-gear and has an associated action</return>
     */
    private static bool IsConsumable(ItemRow item)
    {
        bool isNotGear = item.EquipSlotCategory.RowId == 0;
        bool hasAction = item.ItemAction.RowId > 0;

        return isNotGear && hasAction;
    }

    /**
     * * Determines whether the given item is a crafting material
     * <param name="item">The item row to evaluate</param>
     * <return type="bool">True if the item is in materials categories and has no action</return>
     */
    private static bool IsCraftingMaterial(ItemRow item)
    {
        bool isMaterial = materialsCategoryIds.Contains(item.ItemSearchCategory.RowId);
        bool hasNoAction = item.ItemAction.RowId == 0;

        return isMaterial && hasNoAction;
    }
#endregion

#region Filters & Counts

    /**
     * * Increases the stored count for the specified category by one
     * <param name="categoryId">The ID of the category to increment</param>
     */
    private void IncrementCategory(uint categoryId)
    {
        retainerCategoryCounts[categoryId] = retainerCategoryCounts.GetValueOrDefault(categoryId) + 1;
    }

    /**
     * * Clears and rebuilds category count data for the retainer’s inventory
     */
    private unsafe void RecountRetainerCategoryCounts()
    {
        retainerCategoryCounts.Clear();

        var inv = GameStructs.InventoryManager.Instance();
        if (inv == null)
            return;

        foreach (var page in RetainerPages)
        {
            var container = inv->GetInventoryContainer(page);
            if (container == null)
                continue;

            for (int slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                    continue;

                var itemRow = GetItemRowById(slot->ItemId);
                if (itemRow == null)
                    continue;

                var row = itemRow.Value;

                foreach (var (categoryId, filter) in categoryFilters)
                {
                    if (filter(row))
                        IncrementCategory(categoryId);
                }
            }
        }
    }

    /**
     * * Determines whether the given item passes the currently selected category filters
     * <param name="item">The item row to evaluate</param>
     * <return type="bool">True if the item matches at least one selected filter; otherwise, false</return>
     */
    private bool MatchesTransferFilters(ItemRow item)
    {
        if (selectedCategoryIds.Count == 0)
            return true;

        foreach (var categoryId in selectedCategoryIds)
        {
            if (!categoryFilters.TryGetValue(categoryId, out var filter))
                continue;

            if (filter(item))
                return true;
        }

        return false;
    }

#endregion

#region Inventory Analysis

    /**
     * * Scans the player’s inventory to count free slots and stack capacities
     * <param name="inv">Pointer to the InventoryManager instance</param>
     * <param name="stackSpace">Dictionary updated with remaining stack space per item ID</param>
     * <param name="inventoryItems">Set updated with all unique item IDs found in inventory</param>
     * <return type="int">The total number of free slots in the player’s inventory</return>
     */
    private unsafe int CountInventory(
        GameStructs.InventoryManager* inv, 
        Dictionary<uint, int> stackSpace, 
        HashSet<uint> inventoryItems)
    {
        int freeSlots = 0;

        foreach (var page in PlayerInventoryPages)
        {
            var container = inv->GetInventoryContainer(page);
            if (container == null)
                continue;

            for (int s = 0; s < container->Size; s++)
            {
                var slot = container->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0)
                {
                    freeSlots++;
                    continue;
                }

                inventoryItems.Add(slot->ItemId);

                var row = GetItemRowById(slot->ItemId);
                if (row == null)
                    continue;

                int maxStack = (int)row.Value.StackSize;
                if (maxStack <= 1 || slot->Quantity >= maxStack)
                    continue;

                int remaining = Math.Max(0, maxStack - slot->Quantity);
                if (remaining > 0)
                    stackSpace[slot->ItemId] =
                        stackSpace.GetValueOrDefault(slot->ItemId) + remaining;
            }
        }

        return freeSlots;
    }

    /**
     * * Scans all retainer inventory pages and counts item stacks
     * <param name="inv">Pointer to the InventoryManager instance</param>
     * <param name="items">Set of item IDs currently in the player’s inventory</param>
     * <param name="moveStacks">Dictionary mapping item IDs to their stack quantities for transfer</param>
     * <param name="totalStacks">Reference counter incremented for every stack found</param>
     * <param name="transferStacks">Reference counter incremented for stacks eligible for transfer</param>
     */
    private unsafe void CountRetainerStacks(
        GameStructs.InventoryManager* inv,
        HashSet<uint> items,
        Dictionary<uint, List<int>> moveStacks,
        ref int totalStacks,
        ref int transferStacks)
    {
        var seenUniqueFromRetainer = new HashSet<uint>();

        foreach (var page in RetainerPages)
        {
            var container = inv->GetInventoryContainer(page);
            if (container == null)
                continue;

            for (int s = 0; s < container->Size; s++)
            {
                var slot = container->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                    continue;

                totalStacks++;

                var row = GetItemRowById(slot->ItemId);
                if (row == null || !MatchesTransferFilters(row.Value))
                    continue;

                if (row.Value.IsUnique)
                {
                    if (items.Contains(slot->ItemId))
                        continue;
                    if (!seenUniqueFromRetainer.Add(slot->ItemId))
                        continue;
                }

                transferStacks++;

                if (!moveStacks.TryGetValue(slot->ItemId, out var list))
                {
                    list = new List<int>();
                    moveStacks[slot->ItemId] = list;
                }
                list.Add(slot->Quantity);
            }
        }
    }

    /**
     * * Calculates how many retainer stacks can be merged into existing inventory stacks
     * <param name="moveStacks">Dictionary of item IDs mapped to their retainer stack quantities</param>
     * <param name="stackSpace">Dictionary of item IDs with available stacking capacity in inventory</param>
     * <return type="int">The total number of stacks that can be merged into existing ones</return>
     */
    private static int CountMergeableStacks(
        Dictionary<uint, List<int>> moveStacks,
        Dictionary<uint, int> stackSpace)
    {
        int mergeable = 0;

        foreach (var (itemId, stacks) in moveStacks)
        {
            int remainingCap = stackSpace.GetValueOrDefault(itemId);
            if (remainingCap <= 0)
                continue;

            stacks.Sort();

            foreach (var qty in stacks)
            {
                if (qty <= remainingCap)
                {
                    mergeable++;
                    remainingCap -= qty;
                }
                else
                {
                    break;
                }
            }
        }

        return mergeable;
    }

    /**
     * * Checks if a specific item exists in the player’s inventory
     * <param name="itemId">The unique ID of the item to check for</param>
     * <return type="bool">True if the item is present in the player’s inventory; otherwise, false</return>
     */
    private static unsafe bool HasItemInInventory(uint itemId)
    {
        if (itemId == 0)
            return false;

        var inv = GameStructs.InventoryManager.Instance();
        if (inv == null)
            return false;

        foreach (var page in PlayerInventoryPages)
        {
            var container = inv->GetInventoryContainer(page);
            if (container == null)
                continue;

            for (int slot = 0; slot < container->Size; slot++)
            {
                var currentSlot = container->GetInventorySlot(slot);
                if (currentSlot == null)
                    continue;

                bool isSameItem = currentSlot->ItemId == itemId;
                bool hasQuantity = currentSlot->Quantity > 0;

                if (isSameItem && hasQuantity)
                    return true;
            }
        }

        return false;
    }

#endregion

#region Slot Selection

    /**
     * * Searches the player’s inventory for an existing partial stack of the specified item
     * <param name="itemId">The ID of the item to find a stackable slot for</param>
     * <param name="targetContainer">Outputs the inventory container where stacking is possible</param>
     * <param name="targetSlot">Outputs the slot index of the stackable item</param>
     * <return type="bool">True if a valid stackable slot is found; otherwise, false</return>
     */
    private unsafe bool FindStackableSlot(uint itemId, out GameStructs.InventoryType targetContainer, out int targetSlot)
    {
        targetContainer = default;
        targetSlot = -1;

        if (itemId == 0)
            return false;

        var inv = GameStructs.InventoryManager.Instance();
        if (inv == null)
            return false;

        var row = GetItemRowById(itemId);
        if (row == null)
            return false;

        int maxStack = (int)row.Value.StackSize;
        if (maxStack <= 1)
            return false;

        foreach (var page in PlayerInventoryPages)
        {
            var container = inv->GetInventoryContainer(page);
            if (container == null)
                continue;

            for (int slot = 0; slot < container->Size; slot++)
            {
                var s = container->GetInventorySlot(slot);
                if (s == null)
                    continue;
                
                if (s->ItemId == itemId && s->Quantity > 0 && s->Quantity < maxStack)
                {
                    targetContainer = page;
                    targetSlot = slot;
                    return true;
                }
            }
        }

        return false;
    }

    /**
     * * Searches the player’s inventory for the next available empty slot
     * <param name="targetContainer">Outputs the container type where a free slot was found</param>
     * <param name="targetSlot">Outputs the index of the available inventory slot</param>
     * <return type="bool">True if a free slot is found; otherwise, false</return>
     */
    private unsafe bool FindFreeBagSlot(out GameStructs.InventoryType targetContainer, out int targetSlot)
    {
        targetContainer = default;
        targetSlot = -1;

        var inv = GameStructs.InventoryManager.Instance();
        if (inv == null)
            return false;

        const int bagCount = 4;

        for (int checkedContainers = 0; checkedContainers < bagCount; checkedContainers++)
        {
            var containerType = PlayerInventoryPages[inventoryContainerOffset];
            var container     = inv->GetInventoryContainer(containerType);
            int size          = container != null ? container->Size : 0;

            if (size > 0 && container != null)
            {
                int startSlot = inventorySlotIndex;

                for (int s = startSlot; s < size; s++)
                {
                    var slot = container->GetInventorySlot(s);
                    if (slot == null || slot->ItemId == 0)
                    {
                        targetContainer = containerType;
                        targetSlot      = s;

                        if (s + 1 < size)
                        {
                            inventorySlotIndex = s + 1;
                        }
                        else
                        {
                            inventorySlotIndex = 0;
                            inventoryContainerOffset = (inventoryContainerOffset + 1) % bagCount;
                        }

                        return true;
                    }
                }
            }

            inventorySlotIndex = 0;
            inventoryContainerOffset = (inventoryContainerOffset + 1) % bagCount;
        }

        return false;
    }

#endregion

#region Transfer

    /**
     * * Computes a summary of a potential transfer from retainer to inventory
     * <return type="object">A summary containing total stacks, eligible stacks, free inventory slots, and movable items</return>
     */
    private unsafe TransferPreview GenerateTransferPreview()
    {
        var inv = GameStructs.InventoryManager.Instance();
        if (inv == null)
            return new TransferPreview(0, 0, 0, 0);

        var stackSpace = new Dictionary<uint, int>();
        var inventoryItems = new HashSet<uint>();
        int inventoryFreeSlots = CountInventory(inv, stackSpace, inventoryItems);

        var moveStacks = new Dictionary<uint, List<int>>();
        int totalStacks = 0;
        int transferStacks = 0;
        CountRetainerStacks(inv, inventoryItems, moveStacks, ref totalStacks, ref transferStacks);

        int mergeable = CountMergeableStacks(moveStacks, stackSpace);

        int unmergedStacks = Math.Max(0, transferStacks - mergeable);
        int itemsToMove = mergeable + Math.Min(unmergedStacks, inventoryFreeSlots);

        return new TransferPreview(totalStacks, transferStacks, inventoryFreeSlots, itemsToMove);
    }

    /**
     * * Starts an item transfer session if one is not already running
     * <param name="transferDelayMs">Base delay (ms) between moves for throttling</param>
     */
    private void StartTransfer(int transferDelayMs)
    {
        if (transferSession.Running)
            return;

        var preview = GenerateTransferPreview();
        if (preview.itemsToMove <= 0)
        {
            Plugin.ChatGui.Print("[MassWithdraw] Nothing to transfer.");
            return;
        }

        transferSession.Moved = 0;
        transferSession.Total = preview.itemsToMove;
        transferSession.Running = true;

        ResetInventoryCursor();

        cancellationTokenSource = new CancellationTokenSource();
        _ = RunTransfer(transferDelayMs, cancellationTokenSource.Token);
    }

    /**
     * * Executes the asynchronous transfer loop across all retainer pages
     * <param name="transferDelayMs">Base delay (ms) between item moves for throttling</param>
     * <param name="cancellationToken">Token used to cancel the running transfer</param>
     * <return type="Task">A task representing the asynchronous transfer operation</return>
     */
    private async Task RunTransfer(int transferDelayMs, CancellationToken cancellationToken)
    {
        try
        {
            var seenUnique = new HashSet<uint>();

            unsafe
            {
                if (GameStructs.InventoryManager.Instance() == null)
                {
                    Plugin.ChatGui.Print("[MassWithdraw] InventoryManager not available.");
                    return;
                }
            }

            foreach (var page in RetainerPages)
            {
                if (!IsRetainerUIOpen())
                {
                    Plugin.ChatGui.Print($"[MassWithdraw] Stopped: retainer closed. Moved {transferSession.Moved} item(s).");
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                int pageSize;
                unsafe
                {
                    var inv = GameStructs.InventoryManager.Instance();
                    var container = inv->GetInventoryContainer(page);
                    if (container == null) continue;
                    pageSize = container->Size;
                }

                for (int slot = 0; slot < pageSize; slot++)
                {
                    if (!IsRetainerUIOpen())
                    {
                        Plugin.ChatGui.Print($"[MassWithdraw] Stopped: retainer closed. Moved {transferSession.Moved} item(s).");
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    uint itemId = 0;
                    int quantity = 0;

                    unsafe
                    {
                        var inv = GameStructs.InventoryManager.Instance();
                        var container = inv->GetInventoryContainer(page);
                        if (container == null) continue;

                        var it = container->GetInventorySlot(slot);
                        if (it == null || it->ItemId == 0 || it->Quantity == 0) continue;

                        itemId = it->ItemId;
                        quantity = it->Quantity;
                    }

                    var row = GetItemRowById(itemId);
                    if (row == null || !MatchesTransferFilters(row.Value)) continue;

                    if (row.Value.IsUnique && (!seenUnique.Add(itemId) || HasItemInInventory(itemId))) continue;

                    bool moved = false;

                    unsafe
                    {
                        GameStructs.InventoryType targetContainer;
                        int targetSlot;

                        if (!FindStackableSlot(itemId, out targetContainer, out targetSlot) &&
                            !FindFreeBagSlot(out targetContainer, out targetSlot))
                        {
                            Plugin.ChatGui.Print($"[MassWithdraw] Stopped: no free bag space. Moved {transferSession.Moved} item(s).");
                            return;
                        }

                        var inv = GameStructs.InventoryManager.Instance();
                        var container = inv->GetInventoryContainer(page);
                        if (container == null)
                        {
                            Plugin.ChatGui.Print($"[MassWithdraw] Stopped: retainer container unavailable.");
                            return;
                        }

                        var currentSlot = container->GetInventorySlot(slot);
                        if (currentSlot == null || currentSlot->ItemId != itemId || currentSlot->Quantity != quantity)
                            continue;

                        _ = inv->MoveItemSlot(page, (ushort)slot, targetContainer, (ushort)targetSlot, true);
                        moved = true;
                    }

                    if (moved)
                    {
                        System.Threading.Interlocked.Increment(ref transferSession.Moved);
                        await ThrottleTransfer(transferDelayMs, cancellationToken);
                    }
                }
            }

            Plugin.ChatGui.Print($"[MassWithdraw] Done. Moved total: {transferSession.Moved} item(s).");
        }
        catch (OperationCanceledException)
        {
            Plugin.ChatGui.Print($"[MassWithdraw] Cancelled. Moved so far: {transferSession.Moved} item(s).");
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.Print($"[MassWithdraw] Error in async move: {ex.Message}");
        }
        finally
        {
            transferSession.Running = false;
            var cts = cancellationTokenSource;
            cancellationTokenSource = null;
            cts?.Dispose();
        }
    }



    /**
     * * Applies a variable delay between item transfers to simulate human-like timing
     * <param name="transferDelayMs">Base delay (ms) between transfers.</param>
     * <param name="token">Cancellation token to interrupt the delay if needed</param>
     * <return type="Task">A task that completes after the computed delay or cancellation</return>
     */
    private static async Task ThrottleTransfer(int transferDelayMs, CancellationToken token)
    {
        if (transferDelayMs <= 0) return;

        int extra = 0;
        int roll;
        lock (delayRandom)
        {
            roll = delayRandom.Next(0, 100);
            if (roll < 7) extra = delayRandom.Next(100, 250);
        }

        if (extra > 0)
            await Task.Delay(GenerateHumanizedDelay(extra), token);

        await Task.Delay(GenerateHumanizedDelay(transferDelayMs), token);
    }

#endregion

#region Commands

    /**
     * * Entry point triggered by the user command to begin item transfer
     */
    public void StartTransferFromCommand()
    {
        if (!IsRetainerUIOpen())
        {
            Plugin.ChatGui.PrintError("[MassWithdraw] Open your Retainer’s inventory window first.");
            return;
        }

        var preview = GenerateTransferPreview();

        if (preview.itemsToMove <= 0)
        {
            var msg =
                preview.totalStacks == 0        ? "Retainer inventory is empty."
              : preview.transferStacks == 0     ? "No items match the selected filters."
              : preview.inventoryFreeSlots == 0 ? "Inventory full."
                                                : "Nothing to transfer.";

            Plugin.ChatGui.PrintError($"[MassWithdraw] {msg}");
            return;
        }

        StartTransfer(transferDelayMs);
        Plugin.ChatGui.Print("[MassWithdraw] Transfer started…");
    }

#endregion
}