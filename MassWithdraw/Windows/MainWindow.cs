﻿using System;
using System.Numerics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MassWithdraw.Windows;

public sealed class MainWindow : Window, IDisposable
{

    private const string WindowTitle = "Mass Withdraw";
    private const int DefaultMoveDelayMs = 400;
    private const float MinWidth = 310f;
    private const float MinHeight = 170f;
    private const float ButtonWidth = 150f;
    private const float ButtonSpacing = 12f;
    private const float AnchorGap = 8f;
    private static readonly string[] RetainerInventoryAddons =
        { "InventoryRetainer", "InventoryRetainerLarge" };
    private bool _isMovingAll;
    private CancellationTokenSource? _moveCts;
    private int _movedCount;
    private int _totalToMove = 0;
    private readonly HashSet<uint> _selectedCats = new();
    private readonly Dictionary<uint, string> _catNames = new();
    private bool _useCategoryFilter = false;
    private static string _catSearch = string.Empty;
    private readonly Dictionary<uint, int> _retCatCounts = new();
    private readonly List<uint> _favoriteCats = new();
    private bool _showFilterPanel = false;

    public MainWindow()
        : base(WindowTitle,
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
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

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

    public static unsafe bool IsInventoryRetainerOpenForWatcher()
        => IsAnyAddonVisible(RetainerInventoryAddons);

    private unsafe bool IsInventoryRetainerOpen()
        => IsAnyAddonVisible(RetainerInventoryAddons);

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 8f));
        var rect = TryGetAddonRect(RetainerInventoryAddons);
        if (!rect.ok)
            return;

        var anchorPos = new Vector2(rect.pos.X + rect.size.X + AnchorGap, rect.pos.Y);

        ImGui.SetNextWindowPos(anchorPos, ImGuiCond.Always);
    }

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

    private void EnsureCategoryCache()
    {
        if (_catNames.Count > 0) return;
        var uiCatSheet = Plugin.DataManager.GetExcelSheet<ItemUICategory>();
        if (uiCatSheet == null) return;

        foreach (var row in uiCatSheet)
        {
            try
            {
                var id = row.RowId;
                if (id == 0) continue;

                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) name = $"Category {id}";

                _catNames[id] = name;
            }
            catch
            {
                //
            }
        }
    }

    private static uint GetItemCategoryId(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (sheet == null) return 0;

        try
        {
            var it = sheet.GetRow(itemId);
            return it.ItemUICategory.RowId;
        }
        catch
        {
            return 0;
        }
    }

    private bool ShouldTransfer(uint itemId)
    {
        if (!_useCategoryFilter) return true;
        if (_selectedCats.Count == 0) return false;
        var catId = GetItemCategoryId(itemId);
        return catId != 0 && _selectedCats.Contains(catId);
    }

    private unsafe void RecomputeRetainerCategoryCounts()
    {
        _retCatCounts.Clear();

        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return;

        int tStart = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
        int tEnd   = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7;

        for (int t = tStart; t <= tEnd; t++)
        {
            var cont = inv->GetInventoryContainer((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var it = cont->GetInventorySlot(s);
                if (it == null || it->ItemId == 0 || it->Quantity == 0) continue;

                var catId = GetItemCategoryId(it->ItemId);
                if (catId == 0) continue;

                if (_retCatCounts.TryGetValue(catId, out var c)) _retCatCounts[catId] = c + 1;
                else _retCatCounts[catId] = 1;
            }
        }
    }

    private void UpdateFavoriteCats(int maxChips = 6)
    {
        _favoriteCats.Clear();
        foreach (var id in _retCatCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => _catNames.TryGetValue(kv.Key, out var nm) ? nm : kv.Key.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .Where(id => _catNames.ContainsKey(id))
            .Take(maxChips))
        {
            _favoriteCats.Add(id);
        }
    }

    private void SetCats(IEnumerable<uint> ids)
    {
        _useCategoryFilter = true;
        _selectedCats.Clear();
        foreach (var id in ids) _selectedCats.Add(id);
    }

    private unsafe (int retStacks, int bagFree, int willMove, TimeSpan eta) ComputePreview(int delayMs)
    {
        int retStacks = 0;
        int bagFree = 0;

        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null) return (0, 0, 0, TimeSpan.Zero);

        int tStart = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1;
        int tEnd   = (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage7;

        for (int t = tStart; t <= tEnd; t++)
        {
            var cont = inv->GetInventoryContainer((FFXIVClientStructs.FFXIV.Client.Game.InventoryType)t);
            if (cont == null) continue;

            for (int s = 0; s < cont->Size; s++)
            {
                var it = cont->GetInventorySlot(s);
                if (it != null && it->ItemId != 0 && it->Quantity > 0 && ShouldTransfer(it->ItemId))
                    retStacks++;
            }
        }

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

    private void StartMoveAllRetainerItemsAsync(int delayMs)
    {
        if (_isMovingAll) return;

        var prev = ComputePreview(delayMs);
        _totalToMove = prev.willMove;

        _isMovingAll = true;
        _movedCount = 0;
        _moveCts = new System.Threading.CancellationTokenSource();
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

                            if (!ShouldTransfer(item->ItemId))
                                continue;

                            if (IsUnique(item->ItemId) && PlayerHasItem(item->ItemId))
                            {
                                Plugin.ChatGui.Print($"[MassWithdraw] Skipped unique item (ItemId {item->ItemId}) — already in player inventory.");
                                continue;
                            }

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
                    _totalToMove = 0;
                }
            }
        });
    }

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

            if (willMove > 0)
            {
                var line = $"Will move {willMove} item{(willMove == 1 ? "" : "s")} (ETA ~ {Math.Max(0, (int)eta.TotalSeconds)}s)";
                CenteredColoredText(line, new Vector4(0.8f, 0.9f, 1f, 1f));
                ImGui.Spacing();
            }
        }

        float contentWidth = ImGui.GetContentRegionAvail().X;

        if (!_isMovingAll)
        {
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

                string msg;
                if (!hasInventoryRetainer)
                {
                    msg = "Open your Retainer’s inventory window first.";
                }
                else if (_useCategoryFilter && _selectedCats.Count == 0)
                {
                    msg = "Select at least one category.";
                }
                else if (_useCategoryFilter && _selectedCats.Count > 0 && retStacks == 0)
                {
                    msg = "No items match the selected category.";
                }
                else
                {
                    msg = "Retainer inventory is empty or your bags are full.";
                }

                CenteredColoredText(msg, new Vector4(1f, 0.8f, 0.3f, 1f));
            }
        }
        
        else
        {
            float frac = (_totalToMove > 0) ? (float)_movedCount / _totalToMove : 0f;
            if (frac < 0f) frac = 0f;
            if (frac > 1f) frac = 1f;

            string overlay = (_totalToMove > 0)
                ? $"{_movedCount}/{_totalToMove}  ({(int)(frac * 100f)}%)"
                : $"{_movedCount} moved";

            ImGui.ProgressBar(frac, new Vector2(contentWidth, 22f), overlay);
            ImGui.Spacing();

            float stopWidth = 160f;
            ImGui.SetCursorPosX(MathF.Max(0f, (contentWidth - stopWidth) * 0.5f));
            if (ImGui.Button("Stop Transfer", new Vector2(stopWidth, 0)))
                _moveCts?.Cancel();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        EnsureCategoryCache();
        RecomputeRetainerCategoryCounts();

        var style = ImGui.GetStyle();
        float iconTextSpacing = 6f;

        ImGui.PushFont(UiBuilder.IconFont);
        string iconStr = (_showFilterPanel ? FontAwesomeIcon.AngleDown : FontAwesomeIcon.AngleRight).ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        string textStr = $"Filters ({_selectedCats.Count})";
        var textSize = ImGui.CalcTextSize(textStr);


        float btnW = style.FramePadding.X * 2f + iconSize.X + iconTextSpacing + textSize.X;
        float btnH = MathF.Max(style.FramePadding.Y * 2f + MathF.Max(iconSize.Y, textSize.Y), ImGui.GetFrameHeight());

        bool clicked = ImGui.Button("##filterBtn", new Vector2(-1, btnH));
        if (clicked)
            _showFilterPanel = !_showFilterPanel;

        var pos = ImGui.GetItemRectMin();

        float yCenter = pos.Y + (btnH - iconSize.Y) * 0.5f;
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + style.FramePadding.X, yCenter), 
            ImGui.GetColorU32(ImGuiCol.Text), iconStr);
        ImGui.PopFont();

        string dynamicText = $"Filters ({_selectedCats.Count})";

        float textX = pos.X + style.FramePadding.X + iconSize.X + iconTextSpacing;
        ImGui.GetWindowDrawList().AddText(new Vector2(textX, yCenter),
            ImGui.GetColorU32(ImGuiCol.Text), dynamicText);

        ImGui.Spacing();

        if (_showFilterPanel)
        {
            ImGui.BeginChild("filterPanel", new Vector2(0, 220f), true);

            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 12f));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##catSearch", "Search categories…", ref _catSearch, 32);
            ImGui.PopStyleVar();

            ImGui.Separator();
            ImGui.Checkbox("Enable filter", ref _useCategoryFilter);

            ImGui.SameLine();
            if (ImGui.SmallButton("Select all"))
            {
                _selectedCats.Clear();
                foreach (var id in _catNames.Keys) _selectedCats.Add(id);
                _useCategoryFilter = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
            {
                _selectedCats.Clear();
                _useCategoryFilter = false;
            }

            ImGui.Separator();

            IEnumerable<KeyValuePair<uint, string>> catEnum = _catNames;

            if (!string.IsNullOrWhiteSpace(_catSearch))
            {
                var q = _catSearch.Trim();
                catEnum = catEnum.Where(kv => kv.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            foreach (var kv in catEnum.OrderBy(k => k.Value, StringComparer.OrdinalIgnoreCase))
            {
                bool on = _selectedCats.Contains(kv.Key);
                int count = _retCatCounts.TryGetValue(kv.Key, out var c) ? c : 0;

                string label = count > 0 ? $"{kv.Value} ({count})" : kv.Value;

                if (ImGui.Checkbox(label + $"##cat{kv.Key}", ref on))
                {
                    if (on) _selectedCats.Add(kv.Key);
                    else _selectedCats.Remove(kv.Key);

                    _useCategoryFilter = _selectedCats.Count > 0;
                }
            }

            ImGui.EndChild();
            ImGui.Spacing();
        }
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
    }

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