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
/// Network and disk statistics in the tray, text only: the tooltip (rates while downloads run, today's total otherwise) and three
/// enabled lines in the tray menu. The presenter owns no timer. It holds the hub claim of <see cref="TrayStatsClaim"/>
/// (window hidden and tooltip on), refreshes the tooltip on each hub sample while the claim is held and otherwise only when the
/// window's own tray refresh asks (throttled to one reading per second). Menu lines also follow download state and language changes.
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
    private readonly NativeMenuItem _ratesItem = new();
    private readonly NativeMenuItem _diskItem = new();
    private readonly NativeMenuItem _detailItem = new();
    private readonly NativeMenuItemBase[] _group;
    private readonly int _groupIndex;
    private bool _menuVisible;
    private (bool Paused, int Active, string Language)? _menuState;
    private string _key = string.Empty;
    private long _stamp;
    private bool _disposed;

    /// <summary>Appends the statistics group and listens for the menu's update request.</summary>
    internal TrayStatsPresenter(IoStatsHub hub, NativeMenu menu, TrayStatsHost host, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(host);
        _hub = hub;
        _menu = menu;
        _host = host;
        _time = time ?? TimeProvider.System;
        _sampled = new CoalescedUiUpdate(OnSampled, TimeSpan.Zero, null, (action, _) => _host.Post(action));
        _claim = new TrayStatsClaim(hub, _sampled.Request);
        _ratesItem.Click += OnClick;
        _diskItem.Click += OnClick;
        _detailItem.Click += OnClick;
        _group = [new NativeMenuItemSeparator(), _ratesItem, _diskItem, _detailItem, new NativeMenuItemSeparator()];
        _groupIndex = menu.Items.Count;
        if (host.MenuEnabled()) SetMenuVisibility(true);
        menu.NeedsUpdate += OnNeedsUpdate;
    }

    /// <summary>Line one: "Net ↓ x" while rates show, else the state.</summary>
    internal NativeMenuItem RatesItem => _ratesItem;
    /// <summary>Line two: "Disk y" (written) while rates show, else today's network total.</summary>
    internal NativeMenuItem DiskItem => _diskItem;
    internal NativeMenuItem DetailItem => _detailItem;
    internal bool ClaimHeld => _claim.Held;

    /// <summary>The tray's own refresh: claim, menu visibility and tooltip. Cheap when nothing changed for less than a second.</summary>
    internal void Refresh(bool force = false) => Update(force, fillMenu: false, refreshMenuState: true);

    // The menu is opening: current numbers into the menu lines and the tooltip.
    private void OnNeedsUpdate(object? sender, EventArgs e)
    {
        if (_disposed || _host.Closing()) return;
        Update(force: true, fillMenu: true);
    }

    // The hub samples on a pool thread; the coalesced update arrives here on the UI thread.
    private void OnSampled()
    {
        if (_disposed || _host.Closing()) return;
        Update(force: true, fillMenu: false);
    }

    private void OnClick(object? sender, EventArgs e) => _host.OpenStatistics();

    private void Update(bool force, bool fillMenu, bool refreshMenuState = false)
    {
        if (_disposed) return;
        bool shown = _host.WindowShown();
        bool tooltipOn = _host.TooltipEnabled();
        bool menuOn = _host.MenuEnabled();
        _claim.Update(TrayStatsClaim.Wanted(_host.Closing(), shown, tooltipOn));
        if (_menuVisible != menuOn)
        {
            SetMenuVisibility(menuOn);
            force = fillMenu = true;
        }
        (bool paused, int active) = _host.Downloads();
        // The Linux exporter does not raise the menu update event when the host opens the menu.
        fillMenu |= _menuState == null || (refreshMenuState && _menuState != (paused, active, Texts.Language));
        string tip;
        if (tooltipOn)
        {
            string key = string.Join('|', paused, active, shown, _claim.Held, Texts.Language, menuOn);
            long now = _time.GetTimestamp();
            if (!force && !fillMenu && key == _key && _time.GetElapsedTime(_stamp, now) < MinimumInterval) return;
            _key = key;
            _stamp = now;
            TrayStatsInput state = StateOf(paused, active);
            tip = TrayStatsModel.Tooltip(state);
            if (menuOn && fillMenu) ApplyLines(state);
        }
        else
        {
            _key = string.Empty;
            tip = TrayMenuModel.Tooltip(paused, active);
            if (menuOn && fillMenu) ApplyLines(StateOf(paused, active));
        }
        _host.SetTooltip(tip);
    }

    private void SetMenuVisibility(bool visible)
    {
        _menuVisible = visible;
        // The Linux exporter always exports separators that belong to the menu.
        if (visible)
        {
            for (int i = 0; i < _group.Length; i++) _menu.Items.Insert(_groupIndex + i, _group[i]);
        }
        else
        {
            foreach (NativeMenuItemBase item in _group) _menu.Items.Remove(item);
        }
    }

    private TrayStatsInput StateOf(bool paused, int active) =>
        TrayStatsInput.From(_hub.Peek(), paused, active, _hub.SamplingMode != IoStatsVisibility.None);

    private void ApplyLines(in TrayStatsInput state)
    {
        _menuState = (state.Paused, state.Active, Texts.Language);
        (string net, string disk, string detail) = TrayStatsModel.MenuLines(state);
        if (!Equals(_ratesItem.Header, net)) _ratesItem.Header = net;
        if (!Equals(_diskItem.Header, disk)) _diskItem.Header = disk;
        if (!Equals(_detailItem.Header, detail)) _detailItem.Header = detail;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menu.NeedsUpdate -= OnNeedsUpdate;
        _ratesItem.Click -= OnClick;
        _diskItem.Click -= OnClick;
        _detailItem.Click -= OnClick;
        _claim.Dispose();
        _sampled.Dispose();
    }
}
