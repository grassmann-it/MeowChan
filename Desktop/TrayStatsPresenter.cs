using System;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop;

/// <summary>What the presenter asks of the window; everything is read on the UI thread, <see cref="Post"/> hands work to it.</summary>
internal sealed record TrayStatsHost(
    Func<bool> TooltipEnabled,
    Func<bool> WindowShown,
    Func<bool> Closing,
    Func<(bool Paused, int Active)> Downloads,
    Action<string> SetTooltip,
    Action<Action> Post);

/// <summary>
/// Network and disk statistics in the tray tooltip, text only (rates while downloads run, today's total otherwise). The presenter owns no timer. It holds the hub claim of <see cref="TrayStatsClaim"/>
/// (window hidden and tooltip on), refreshes the tooltip on each hub sample while the claim is held and otherwise only when the
/// window's own tray refresh asks (throttled to one reading per second).
/// </summary>
internal sealed class TrayStatsPresenter : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private readonly IoStatsHub _hub;
    private readonly TrayStatsHost _host;
    private readonly TimeProvider _time;
    private readonly TrayStatsClaim _claim;
    private readonly CoalescedUiUpdate _sampled;
    private string _key = string.Empty;
    private long _stamp;
    private bool _disposed;

    internal TrayStatsPresenter(IoStatsHub hub, TrayStatsHost host, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(host);
        _hub = hub;
        _host = host;
        _time = time ?? TimeProvider.System;
        _sampled = new CoalescedUiUpdate(OnSampled, TimeSpan.Zero, null, (action, _) => _host.Post(action));
        _claim = new TrayStatsClaim(hub, _sampled.Request);
    }

    internal bool ClaimHeld => _claim.Held;

    /// <summary>The tray's own refresh: claim and tooltip. Cheap when nothing changed for less than a second.</summary>
    internal void Refresh(bool force = false) => Update(force);

    // The hub samples on a pool thread; the coalesced update arrives here on the UI thread.
    private void OnSampled()
    {
        if (_disposed || _host.Closing()) return;
        Update(force: true);
    }

    private void Update(bool force)
    {
        if (_disposed) return;
        bool shown = _host.WindowShown();
        bool tooltipOn = _host.TooltipEnabled();
        _claim.Update(TrayStatsClaim.Wanted(_host.Closing(), shown, tooltipOn));
        (bool paused, int active) = _host.Downloads();
        string tip;
        if (tooltipOn)
        {
            string key = string.Join('|', paused, active, shown, _claim.Held, Texts.Language);
            long now = _time.GetTimestamp();
            if (!force && key == _key && _time.GetElapsedTime(_stamp, now) < MinimumInterval) return;
            _key = key;
            _stamp = now;
            TrayStatsInput state = StateOf(paused, active);
            tip = TrayStatsModel.Tooltip(state);
        }
        else
        {
            _key = string.Empty;
            tip = TrayMenuModel.Tooltip(paused, active);
        }
        _host.SetTooltip(tip);
    }

    private TrayStatsInput StateOf(bool paused, int active) =>
        TrayStatsInput.From(_hub.Peek(), paused, active, _hub.SamplingMode != IoStatsVisibility.None);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _claim.Dispose();
        _sampled.Dispose();
    }
}
