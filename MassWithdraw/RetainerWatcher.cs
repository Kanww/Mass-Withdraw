using System;
using Dalamud.Plugin.Services;

namespace MassWithdraw;

public sealed class RetainerWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly Func<bool> isEnabled;
    private readonly Func<bool> isRetainerOpen;
    private readonly Action<bool> setMainWindowOpen;

    private bool lastVisible = false;
    private DateTime nextPoll = DateTime.MinValue;
    private readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(150);

    /**
     * * Initializes a watcher that monitors the Retainer inventory window state.
     *   Automatically opens or closes the main plugin window based on visibility.
     * <param name="framework">Dalamud framework instance used for periodic updates</param>
     * <param name="isRetainerOpen">Function returning whether the Retainer UI is currently open</param>
     * <param name="setMainWindowOpen">Action to update the open state of the main window</param>
     * <param name="isEnabled">Function returning whether automatic monitoring is enabled</param>
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

    /**
     * * Periodically checks the Retainer UI visibility at a fixed polling interval.
     *   Toggles the main window’s visibility when the Retainer window opens or closes.
     * <param name="_">Unused framework update argument</param>
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

    /**
     * * Disposes of the RetainerWatcher instance by detaching its update handler.
     */
    public void Dispose() => framework.Update -= OnUpdate;
}