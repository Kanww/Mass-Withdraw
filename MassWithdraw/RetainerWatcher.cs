using System;
using Dalamud.Plugin.Services;

namespace MassWithdraw;

public sealed class RetainerWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly Func<bool> isEnabled;        // NEW
    private readonly Func<bool> isRetainerOpen;
    private readonly Action<bool> setMainWindowOpen;

    private bool lastVisible = false;
    private DateTime nextPoll = DateTime.MinValue;
    private readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(150);

    public RetainerWatcher(
        IFramework framework,
        Func<bool> isRetainerOpen,
        Action<bool> setMainWindowOpen,
        Func<bool> isEnabled)                     // NEW
    {
        this.framework = framework;
        this.isRetainerOpen = isRetainerOpen;
        this.setMainWindowOpen = setMainWindowOpen;
        this.isEnabled = isEnabled;               // NEW

        framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll) return;
        nextPoll = now + pollInterval;

        // If disabled, do nothing (no auto-open/close)
        if (!isEnabled()) return;

        bool visible = isRetainerOpen();
        if (visible != lastVisible)
        {
            setMainWindowOpen(visible);
            lastVisible = visible;
        }
    }

    public void Dispose() => framework.Update -= OnUpdate;
}