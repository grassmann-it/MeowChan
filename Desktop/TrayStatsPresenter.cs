using System;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop;

/// <summary>What the presenter asks of the window; everything is read on the UI thread, <see cref="Post"/> hands work to it.</summary>
internal sealed record TrayStatsHost(
    Func<bool> TooltipEnabled,
    Func<bool> MenuEnabled,
    Func<bool> WindowShown,
    Func<bool> Closing,
    Func<(bool Paused, int Active)> Downloads,
    Action<string> SetTooltip,
    Action OpenStatistics,
    Action<Action> Post);

/// <summary>
/// Network and disk statistics in the tray tooltip and a menu shortcut or three statistics lines. The presenter owns no
/// timer. It holds the hub claim of <see cref="TrayStatsClaim"/>
/// (window hidden and tooltip on), refreshes the tooltip on each hub sample while the claim is held and otherwise only when the
/// window's own tray refresh asks (throttled to one reading per second). Menu lines follow the same readings outside menu tracking.
/// </summary>
internal sealed class TrayStatsPresenter : IDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private readonly IoStatsHub _hub;
    private readonly NativeMenu _menu;
    private readonly TrayStatsHost _host;
    private readonly TimeProvider _time;
    private readonly TrayStatsClaim _claim;
    private readonly CoalescedUiUpdate _sampled;
    private readonly bool _useStatisticsShortcut;
    private readonly NativeMenuItem[] _items;
    private bool _menuOpen;
    private (bool Paused, int Active, bool Shown, bool ClaimHeld, string Language, bool Menu, bool Tooltip)? _state;
    private long _stamp;
    private bool _disposed;

    /// <summary>Appends the statistics group and listens for the menu's update request.</summary>
    internal TrayStatsPresenter(IoStatsHub hub, NativeMenu menu, TrayStatsHost host, bool useStatisticsShortcut, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(host);
        _hub = hub;
        _menu = menu;
        _host = host;
        _time = time ?? TimeProvider.System;
        _useStatisticsShortcut = useStatisticsShortcut;
        _sampled = new CoalescedUiUpdate(OnSampled, TimeSpan.Zero, null, (action, _) => _host.Post(action));
        _claim = new TrayStatsClaim(hub, _sampled.Request);
        // Both the Linux exporter and the Windows tray presenter ignore separator visibility.
        _items = useStatisticsShortcut ? [new(Texts.T("stats.settings.title"))] : [new(), new(), new()];
        bool visible = host.MenuEnabled();
        foreach (NativeMenuItem item in _items)
        {
            item.IsVisible = visible;
            item.Click += OnClick;
            menu.Items.Add(item);
        }
        menu.NeedsUpdate += OnNeedsUpdate;
        menu.Opening += OnOpening;
        menu.Closed += OnClosed;
    }

    /// <summary>Line one: "Net ↓ x" while rates show, else the state.</summary>
    internal NativeMenuItem RatesItem => _items[0];
    /// <summary>Line two: "Disk y" (written) while rates show, else today's network total.</summary>
    internal NativeMenuItem DiskItem => _items[1];
    internal NativeMenuItem DetailItem => _items[2];
    internal bool ClaimHeld => _claim.Held;

    /// <summary>The tray's own refresh: claim, menu visibility and tooltip. Cheap when nothing changed for less than a second.</summary>
    internal void Refresh(bool force = false) => Update(force);

    // Native exporters that support NeedsUpdate can read current numbers before opening the menu.
    private void OnNeedsUpdate(object? sender, EventArgs e)
    {
        if (_disposed || _host.Closing()) return;
        Update(force: true);
    }

    private void OnOpening(object? sender, EventArgs e) => _menuOpen = true;

    private void OnClosed(object? sender, EventArgs e)
    {
        _menuOpen = false;
        _state = null; // Apply changes on the next refresh, outside the native close callback.
    }

    // The hub samples on a pool thread; the coalesced update arrives here on the UI thread.
    private void OnSampled()
    {
        if (_disposed || _host.Closing()) return;
        Update(force: true);
    }

    private void OnClick(object? sender, EventArgs e) => _host.OpenStatistics();

    private void Update(bool force)
    {
        if (_disposed) return;
        bool shown = _host.WindowShown();
        bool tooltipOn = _host.TooltipEnabled();
        bool menuOn = _host.MenuEnabled();
        _claim.Update(TrayStatsClaim.Wanted(_host.Closing(), shown, tooltipOn));
        (bool paused, int active) = _host.Downloads();
        var key = (paused, active, shown, _claim.Held, Texts.Language, menuOn, tooltipOn);
        long now = _time.GetTimestamp();
        if (!force && key == _state && _time.GetElapsedTime(_stamp, now) < MinimumInterval) return;
        _state = key;
        _stamp = now;
        bool showMenuValues = !_menuOpen && menuOn && !_useStatisticsShortcut;
        TrayStatsInput state = tooltipOn || showMenuValues ? StateOf(paused, active) : default;
        if (!_menuOpen) UpdateMenu(menuOn, state);
        _host.SetTooltip(tooltipOn ? TrayStatsModel.Tooltip(state) : TrayMenuModel.Tooltip(paused, active));
    }

    private void UpdateMenu(bool visible, in TrayStatsInput state)
    {
        if (visible)
        {
            if (_useStatisticsShortcut)
                SetHeader(_items[0], Texts.T("stats.settings.title"));
            else
                ApplyLines(state);
        }

        foreach (NativeMenuItem item in _items)
            if (item.IsVisible != visible) item.IsVisible = visible;
    }

    private TrayStatsInput StateOf(bool paused, int active) =>
        TrayStatsInput.From(_hub.Peek(), paused, active, _hub.SamplingMode != IoStatsVisibility.None);

    private void ApplyLines(in TrayStatsInput state)
    {
        (string net, string disk, string detail) = TrayStatsModel.MenuLines(state);
        SetHeader(RatesItem, net);
        SetHeader(DiskItem, disk);
        SetHeader(DetailItem, detail);
    }

    private static void SetHeader(NativeMenuItem item, string text)
    {
        if (!Equals(item.Header, text)) item.Header = text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menu.NeedsUpdate -= OnNeedsUpdate;
        _menu.Opening -= OnOpening;
        _menu.Closed -= OnClosed;
        foreach (NativeMenuItem item in _items) item.Click -= OnClick;
        _claim.Dispose();
        _sampled.Dispose();
    }
}
