using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MassWithdraw.Windows;

public partial class MainWindow
{
    // Cache last snapped position so we don't spam Position every frame.
    private Vector2 _lastAnchor = new(float.NaN, float.NaN);

    // The two possible retainer inventory add-ons (small / large).
    private static readonly string[] RetainerAddonNames = { "InventoryRetainer", "InventoryRetainerLarge" };

    /// <summary>
    /// Returns true if any “Retainer Inventory” addon (small/large, any index 0..1) is visible.
    /// </summary>
    internal unsafe bool IsInventoryRetainerOpen()
        => TryGetVisibleRetainerRect(out _, out _);

    /// <summary>
    /// Try to locate a visible Retainer Inventory addon and return its screen rect.
    /// </summary>
    /// <param name="pos">Top-left position in screen space.</param>
    /// <param name="size">Width/height of the root node.</param>
    /// <returns>True when a visible addon is found and rect is valid.</returns>
    private static unsafe bool TryGetVisibleRetainerRect(out Vector2 pos, out Vector2 size)
    {
        // Defaults
        pos  = Vector2.Zero;
        size = Vector2.Zero;

        // Dalamud keeps two “UI roots” (0 = main, 1 = overlay). Search both.
        for (int rootIndex = 0; rootIndex <= 1; rootIndex++)
        {
            foreach (var name in RetainerAddonNames)
            {
                var addon = Plugin.GameGui.GetAddonByName(name, rootIndex);
                if (addon == null || addon.Address == nint.Zero)
                    continue;

                var unit = (AtkUnitBase*)addon.Address;
                if (unit == null || !unit->IsVisible || unit->RootNode == null)
                    continue;

                var w = unit->RootNode->Width;
                var h = unit->RootNode->Height;
                if (w <= 0 || h <= 0)
                    continue;

                pos  = new Vector2(unit->X, unit->Y);
                float sx = unit->RootNode->ScaleX <= 0f ? 1f : unit->RootNode->ScaleX;
                float sy = unit->RootNode->ScaleY <= 0f ? 1f : unit->RootNode->ScaleY;
                size = new Vector2(w * sx, h * sy);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Snap this window to the right of the visible Retainer Inventory addon.
    /// Writes <see cref="Position"/> only when the target position actually changes,
    /// to avoid redundant window updates and flicker.
    /// </summary>
    private void AnchorToRetainer()
    {
        if (!TryGetVisibleRetainerRect(out var pos, out var size))
            return;

        float gap = AnchorGapX * ImGui.GetIO().FontGlobalScale;
        var target = new Vector2(pos.X + size.X + gap, pos.Y);

        const float SnapThreshold = 1f; // px
        bool haveLast = !float.IsNaN(_lastAnchor.X);

        if (haveLast && Vector2.DistanceSquared(target, _lastAnchor) < SnapThreshold * SnapThreshold)
            return; // no meaningful movement; keep current Position

        Position    = target;
        _lastAnchor = target;
    }

    /// <summary>
    /// Call this if you let the user drag the window manually and want to
    /// allow the next <see cref="AnchorToRetainer"/> to re-snap immediately.
    /// </summary>
    private void ResetAnchorCache() => _lastAnchor = new(float.NaN, float.NaN);
}