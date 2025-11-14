using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MassWithdraw.Windows;

public partial class MainWindow
{

    private Vector2 lastAnchor = new(float.NaN, float.NaN);
    private static readonly string[] RetainerAddonNames =
    {
        "InventoryRetainer",
        "InventoryRetainerLarge"
    };

    /**
     * * Checks whether any retainer inventory window is currently open.
     *   Used to ensure retainer UI elements are available before performing actions.
     * <return type="bool">True if a retainer inventory window is open; otherwise, false</return>
     */
    internal unsafe bool IsRetainerUIOpen()
    {
        return TryGetRetainerUI(out _, out _);
    }

    /**
     * * Attempts to locate the active retainer inventory addon and retrieve its position and size.
     * <param name="topLeft">Outputs the top-left screen coordinates of the retainer UI</param>
     * <param name="size">Outputs the pixel dimensions of the retainer UI</param>
     * <return type="bool">True if a visible and valid retainer UI was found; otherwise, false</return>
     */
    private static unsafe bool TryGetRetainerUI(out Vector2 topLeft, out Vector2 size)
    {
        topLeft = size = Vector2.Zero;

        for (int rootIndex = 0; rootIndex < 2; rootIndex++)
        {
            foreach (var addonName in RetainerAddonNames)
            {
                var addon = Plugin.GameGui.GetAddonByName(addonName, rootIndex);
                if (addon == null || addon.Address == nint.Zero)
                    continue;

                var unit = (AtkUnitBase*)addon.Address;
                if (unit == null || !unit->IsVisible)
                    continue;

                var rootNode = unit->RootNode;
                if (rootNode == null)
                    continue;

                int width  = rootNode->Width;
                int height = rootNode->Height;
                if (width <= 0 || height <= 0)
                    continue;

                float sx = (rootNode->ScaleX > 0f && float.IsFinite(rootNode->ScaleX)) ? rootNode->ScaleX : 1f;
                float sy = (rootNode->ScaleY > 0f && float.IsFinite(rootNode->ScaleY)) ? rootNode->ScaleY : 1f;

                topLeft = new Vector2(unit->X, unit->Y);
                size    = new Vector2(width  * sx, height * sy);
                return true;
            }
        }

        return false;
    }

    /**
     * * Positions the MassWithdraw window next to the open retainer inventory window.
     *   Automatically aligns the UI when the retainer interface is visible.
     */
    private void AnchorToRetainer()
    {
        if (!TryGetRetainerUI(out var uiPos, out var uiSize))
            return;

        float scaledGap = AnchorGapX * ImGui.GetIO().FontGlobalScale;
        var targetPos = new Vector2(uiPos.X + uiSize.X + scaledGap, uiPos.Y);

        const float SNAP_DISTANCE_SQUARED = 1f;
        if (!float.IsNaN(lastAnchor.X) && Vector2.DistanceSquared(targetPos, lastAnchor) < SNAP_DISTANCE_SQUARED)
            return;

        Position = lastAnchor = targetPos;
    }
    
    /**
     * * Clears the current anchor state so the window can be moved freely.
     */
    public void ClearAnchor()
    {
        Position = null;
        lastAnchor = new(float.NaN, float.NaN);
    }
}