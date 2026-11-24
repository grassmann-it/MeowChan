using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;
using Serilog;

namespace MeowChan;

public partial class MainWindow
{
    private const string FooterDisplaysKey = "settings.footer.displays";
    private ContextMenuHost? _footerNavigationMenu;
    private ContextMenuHost? _footerStatusMenu;
    private bool _footerDisplaySavePending;
    private FooterDisplayPreferences? _appliedFooterDisplays;

    internal bool UseMacFooterContextClick { get; set; } = OperatingSystem.IsMacOS();

    private bool FooterCovered => GalleryHost.IsVisible || ReaderHost.IsVisible;
    private bool FooterRatesShown => WorkspaceStatusBar.IsVisible && !FooterCovered
        && (FooterNetworkRate.IsVisible || FooterDiskRate.IsVisible);
    private bool CanChangeFooterDisplays => _restoreCompleted && !WorkspaceCommandBusy
        && !_footerDisplaySavePending && !FooterCovered && WorkspaceNavigation.IsVisible;

    private void InitializeFooterDisplays()
    {
        _footerNavigationMenu = new ContextMenuHost(WorkspaceNavigation, ComposeFooterDisplayMenu,
            acceptSource: source => IsFooterMenuSource(source, WorkspaceNavigation));
        _footerStatusMenu = new ContextMenuHost(WorkspaceStatusBar, ComposeFooterDisplayMenu,
            fallbackFocus: () => WorkspaceNavigation.Focus(),
            acceptSource: source => IsFooterMenuSource(source, WorkspaceStatusBar));
        WorkspaceNavigation.AddHandler(PointerReleasedEvent, OnFooterPointerReleased, RoutingStrategies.Bubble);
        WorkspaceStatusBar.AddHandler(PointerReleasedEvent, OnFooterPointerReleased, RoutingStrategies.Bubble);
        GalleryHost.PropertyChanged += OnFooterCoverChanged;
        ReaderHost.PropertyChanged += OnFooterCoverChanged;
    }

    private void DisposeFooterDisplays()
    {
        _footerNavigationMenu?.Dispose();
        _footerStatusMenu?.Dispose();
        WorkspaceNavigation.RemoveHandler(PointerReleasedEvent, OnFooterPointerReleased);
        WorkspaceStatusBar.RemoveHandler(PointerReleasedEvent, OnFooterPointerReleased);
        GalleryHost.PropertyChanged -= OnFooterCoverChanged;
        ReaderHost.PropertyChanged -= OnFooterCoverChanged;
    }

    private static bool IsFooterMenuSource(object? source, Control surface)
    {
        if (source is not Visual visual) return false;
        foreach (Visual ancestor in visual.GetSelfAndVisualAncestors())
        {
            if (ancestor == surface) return true;
            if (ancestor is Button or ComboBox or Control { ContextMenu: not null } or Control { ContextFlyout: not null })
                return false;
        }
        return false;
    }

    // The native secondary click uses ContextRequested. Also accept a Ctrl+left
    // click when macOS delivers it as a primary mouse gesture.
    private void OnFooterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!UseMacFooterContextClick || e.Pointer.Type != PointerType.Mouse || e.KeyModifiers != KeyModifiers.Control
            || e.InitialPressMouseButton != MouseButton.Left || sender is not Control surface
            || !IsFooterMenuSource(e.Source, surface) || !CanChangeFooterDisplays) return;
        e.Handled = true;
        ContextMenuHost? host = surface == WorkspaceNavigation ? _footerNavigationMenu : _footerStatusMenu;
        if (host?.OpenMenu == null) host?.Show(atPointer: true);
    }

    private ContextMenuBuilder? ComposeFooterDisplayMenu()
    {
        if (!CanChangeFooterDisplays) return null;
        bool network = _config.ShowFooterNetwork, disk = _config.ShowFooterDisk, status = _config.ShowFooterStatus;
        return new ContextMenuBuilder().Named(Texts.T(FooterDisplaysKey))
            .Check("footer.network", IoStatsDisplay.NetworkTitle(), network, () => SaveFooterDisplay(config => config.ShowFooterNetwork = !network))
            .Check("footer.disk", IoStatsDisplay.DiskTitle(), disk, () => SaveFooterDisplay(config => config.ShowFooterDisk = !disk))
            .Check("footer.status", Texts.T("settings.footer.statusLines"), status, () => SaveFooterDisplay(config => config.ShowFooterStatus = !status));
    }

    private async void SaveFooterDisplay(Action<Config> change)
    {
        if (!CanChangeFooterDisplays) return;
        _footerDisplaySavePending = true;
        try
        {
            await _session.SaveViewPreferencesAsync(change);
            if (_isClosing) return;
            ApplyFooterDisplayPreferences();
        }
        catch (Exception error)
        {
            Log.Error(error, "Could not save the footer displays.");
            if (!_isClosing)
                ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message),
                    NotificationType.Error, operation: "preferences.appearance");
        }
        finally { _footerDisplaySavePending = false; }
    }

    private void ApplyFooterDisplayPreferences()
    {
        FooterDisplayPreferences displays = FooterDisplayPreferences.FromConfig(_config);
        if (_appliedFooterDisplays == displays) return;
        _appliedFooterDisplays = displays;
        WorkspaceStatus.IsVisible = WorkspaceCounts.IsVisible = displays.Status;
        FooterNetworkRate.IsVisible = displays.Network;
        FooterDiskRate.IsVisible = displays.Disk;
        WorkspaceStatusRow.FillFirst = displays.Status;
        UpdateFooterStatusStrip();
        ApplyIoSnapshot(force: true);
        _settingsEditor?.RefreshConfirmedFooterDisplays();
    }

    private void UpdateFooterStatusStrip()
    {
        bool show = WorkspaceNavigation.IsVisible && (WorkspaceStatus.IsVisible || FooterNetworkRate.IsVisible
            || FooterDiskRate.IsVisible || WorkspaceStoreWarningButton.IsVisible || _updateNotice?.IsVisible == true);
        if (!show) _footerStatusMenu?.Close();
        WorkspaceStatusBar.IsVisible = show;
        WorkspaceRoot.RowDefinitions[2].Height = new GridLength(show ? StatusStripHeight : 0);
        UpdateSpeedSamplingState();
    }

    private void OnFooterCoverChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        if (FooterCovered) CloseFooterDisplayMenus();
        UpdateSpeedSamplingState();
    }

    private bool CloseFooterDisplayMenus()
    {
        bool wasOpen = _footerNavigationMenu?.OpenMenu != null || _footerStatusMenu?.OpenMenu != null;
        _footerNavigationMenu?.Close();
        _footerStatusMenu?.Close();
        return wasOpen;
    }

    private void ApplyFooterRates(IoRates rates)
    {
        if (!FooterRatesShown) return;
        if (FooterNetworkRate.IsVisible) FooterNetworkRate.Text = IoStatsDisplay.NetworkLine(rates);
        if (FooterDiskRate.IsVisible) FooterDiskRate.Text = IoStatsDisplay.DiskLine(rates);
    }
}
