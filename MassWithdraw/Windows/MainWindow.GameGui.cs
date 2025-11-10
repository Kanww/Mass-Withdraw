/*
===============================================================================
  MassWithdraw – MainWindow.GameGui.cs
===============================================================================

  Overview
  ---------------------------------------------------------------------------
  Game-facing helpers for locating the Retainer Inventory UI (small/large)
  and anchoring the plugin window beside it. All interactions here read from
  FFXIV’s UI tree via FFXIVClientStructs and Dalamud’s GameGui.
  
===============================================================================
*/

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MassWithdraw.Windows;

public partial class MainWindow
{
    // Stores the last position we snapped to (prevents redundant Position writes).
    private Vector2 _lastAnchor = new(float.NaN, float.NaN);

    // Known retainer addon names
    private static readonly string[] RetainerAddonNames = { "InventoryRetainer", "InventoryRetainerLarge" };

    // True when any visible Retainer Inventory addon exists.
    internal unsafe bool IsInventoryRetainerOpen()
        => TryGetVisibleRetainerRect(out _, out _);

    /*
     * ---------------------------------------------------------------------------
     *  TryGetVisibleRetainerRect()
     * ---------------------------------------------------------------------------
     *  Attempts to find a visible Retainer Inventory addon and returns its
     *  top-left position and *scaled* size (ScaleX/ScaleY applied).
     *  Returns true on success, false otherwise.
     * ---------------------------------------------------------------------------
    */
    private static unsafe bool TryGetVisibleRetainerRect(out Vector2 pos, out Vector2 size)
    {
        pos = size = Vector2.Zero;

        for (int root = 0; root < 2; root++)
        {
            foreach (var name in RetainerAddonNames)
            {
                var addon = Plugin.GameGui.GetAddonByName(name, root);
                if (addon == null || addon.Address == nint.Zero) continue;

                var unit = (AtkUnitBase*)addon.Address;
                var node = unit == null ? null : unit->RootNode;
                if (unit == null || !unit->IsVisible || node == null) continue;

                int w = node->Width, h = node->Height;
                if (w <= 0 || h <= 0) continue;

                float sx = node->ScaleX > 0f ? node->ScaleX : 1f;
                float sy = node->ScaleY > 0f ? node->ScaleY : 1f;

                pos  = new(unit->X, unit->Y);
                size = new(w * sx, h * sy);
                return true;
            }
        }
        return false;
    }

    /*
     * ---------------------------------------------------------------------------
     *  AnchorToRetainer()
     * ---------------------------------------------------------------------------
     *  Snaps this window to the right edge of the visible Retainer Inventory.
     *  Applies DPI scale to the horizontal gap, and skips writing Position unless
     *  the target changed meaningfully (anti-jitter).
     * ---------------------------------------------------------------------------
    */
    private void AnchorToRetainer()
    {
        if (!TryGetVisibleRetainerRect(out var pos, out var size)) return;

        var target = new Vector2(
            pos.X + size.X + AnchorGapX * ImGui.GetIO().FontGlobalScale,
            pos.Y
        );

        const float snap2 = 1f * 1f; // squared threshold
        if (!float.IsNaN(_lastAnchor.X) && Vector2.DistanceSquared(target, _lastAnchor) < snap2) return;

        Position = _lastAnchor = target;
    }

    // If the user drags the window manually, call this to allow the next AnchorToRetainer() to snap immediately.
    private void ResetAnchorCache() => _lastAnchor = new(float.NaN, float.NaN);
}