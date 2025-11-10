/*
===============================================================================
  MassWithdraw – RetainerWatcher.cs
===============================================================================

  Overview
  ---------------------------------------------------------------------------
  Handles automatic detection of the Retainer Inventory window and synchronizes
  the visibility of the plugin’s main window accordingly.

  • Subscribes to Dalamud’s `IFramework.Update` event.
  • Periodically checks whether a Retainer Inventory window is open.
  • Opens or closes the plugin’s main window automatically based on that state.
  • Uses a lightweight polling system to avoid performance overhead.

  Behavior
  ---------------------------------------------------------------------------
  - Polls every ~150ms (configurable via `pollInterval`).
  - Only runs while `isEnabled()` returns true (user preference).
  - Detects visibility transitions and notifies the main window handler.

===============================================================================
*/

using System;
using Dalamud.Plugin.Services;

namespace MassWithdraw;

public sealed class RetainerWatcher : IDisposable
{
    /*
     * ---------------------------------------------------------------------------
     *  Dependencies and callbacks
     * ---------------------------------------------------------------------------
     *  These function pointers are injected by the main plugin to keep the watcher
     *  lightweight and independent of UI or configuration logic.
     * ---------------------------------------------------------------------------
    */
    private readonly IFramework framework;
    private readonly Func<bool> isEnabled;
    private readonly Func<bool> isRetainerOpen;
    private readonly Action<bool> setMainWindowOpen;

    /*
     * ---------------------------------------------------------------------------
     *  Internal state tracking
     * ---------------------------------------------------------------------------
     *  Tracks previous visibility to detect transitions and avoid redundant UI
     *  updates. Uses a timed polling interval to balance responsiveness and
     *  performance.
     * ---------------------------------------------------------------------------
    */
    private bool lastVisible = false;
    private DateTime nextPoll = DateTime.MinValue;
    private readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(150);

    /*
     * ---------------------------------------------------------------------------
     *  Constructor
     * ---------------------------------------------------------------------------
     *  Registers the update callback and stores references to plugin delegates.
     * ---------------------------------------------------------------------------
    */
    public RetainerWatcher(
        IFramework framework,
        Func<bool> isRetainerOpen,
        Action<bool> setMainWindowOpen,
        Func<bool> isEnabled)
    {
        this.framework = framework;
        this.isRetainerOpen = isRetainerOpen;
        this.setMainWindowOpen = setMainWindowOpen;
        this.isEnabled = isEnabled;

        framework.Update += OnUpdate;
    }

    /*
     * ---------------------------------------------------------------------------
     *  Periodic update callback
     * ---------------------------------------------------------------------------
     *  Executes every game frame (~60 FPS). Internally throttled to poll once
     *  every 150ms. Detects changes in Retainer Inventory visibility and updates
     *  the main plugin window accordingly.
     * ---------------------------------------------------------------------------
    */
    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll) return;
        nextPoll = now + pollInterval;

        if (!isEnabled()) return;

        bool visible = isRetainerOpen();
        if (visible != lastVisible)
        {
            setMainWindowOpen(visible);
            lastVisible = visible;
        }
    }

    /*
     * ---------------------------------------------------------------------------
     *  Cleanup
     * ---------------------------------------------------------------------------
     *  Unsubscribes from the framework update event to prevent leaks or crashes
     *  when the plugin is unloaded.
     * ---------------------------------------------------------------------------
    */
    public void Dispose() => framework.Update -= OnUpdate;
}