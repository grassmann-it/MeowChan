using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;

namespace MeowChan;

public partial class MainWindow
{
    private Button? _updateNotice;
    private MenuItem? _checkUpdatesMenu;

    private void InitializeUpdates()
    {
        // A compact, keyboard-accessible status action is present in every workspace mode.
        if (this.FindControl<TextBlock>("WorkspaceStatus")?.Parent is Grid status)
        {
            status.ColumnDefinitions.Insert(1, new ColumnDefinition(GridLength.Auto));
            if (this.FindControl<TextBlock>("WorkspaceCounts") is { } counts) Grid.SetColumn(counts, 2);
            _updateNotice = new Button
            {
                Name = "UpdateNoticeButton", Padding = new Thickness(4, 0), MinHeight = 0,
                FontSize = 11, IsVisible = false, Classes = { "subtle" }
            };
            Grid.SetColumn(_updateNotice, 1);
            _updateNotice.Click += OnUpdateNoticeClick;
            status.Children.Add(_updateNotice);
        }
        if (this.FindControl<MenuItem>("WorkspaceMenuItem") is { } menu)
        {
            _checkUpdatesMenu = new MenuItem { Name = "CheckUpdatesMenuItem" };
            _checkUpdatesMenu.Click += OnCheckUpdatesMenuClick;
            int index = menu.Items.IndexOf(this.FindControl<MenuItem>("AboutMenuItem"));
            menu.Items.Insert(Math.Max(0, index), _checkUpdatesMenu);
        }
        _session.Updates.Changed += OnUpdatesChanged;
        Texts.Changed += OnUpdateTextsChanged;
        RefreshUpdates();
    }

    private void OnUpdatesChanged() => Dispatcher.UIThread.Post(() => { if (!_isClosing) RefreshUpdates(); });
    private void OnUpdateTextsChanged(object? sender, EventArgs e) => OnUpdatesChanged();

    private void RefreshUpdates()
    {
        UpdateView view = _session.Updates.View;
        if (_updateNotice != null)
        {
            _updateNotice.IsVisible = view.Update != null && !view.EnvironmentDisabled;
            string label = view.Update == null ? string.Empty : Texts.T("updates.available", view.Update.Version);
            _updateNotice.Content = new TextBlock { Text = label, MaxWidth = 220, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            AutomationProperties.SetName(_updateNotice, label);
            ToolTip.SetTip(_updateNotice, label);
        }
        if (_checkUpdatesMenu != null)
        {
            _checkUpdatesMenu.Header = Texts.T("updates.check");
            ToolTip.SetTip(_checkUpdatesMenu, view.EnvironmentDisabled ? Texts.T("updates.disabled") : null);
            // The entry stays reachable when packaging disabled checks: About explains the reason.
            _checkUpdatesMenu.IsEnabled = !view.IsChecking;
        }
    }

    private async void OnUpdateNoticeClick(object? sender, RoutedEventArgs e) => await ShowUpdatesAsync(false);
    private async void OnCheckUpdatesMenuClick(object? sender, RoutedEventArgs e) => await ShowUpdatesAsync(true);

    private async Task ShowUpdatesAsync(bool check)
    {
        if (_isClosing) return;
        AboutWindow about = new(_session.Updates);
        Task dialog = about.ShowDialog(this);
        if (check) await about.CheckUpdatesAsync();
        await dialog;
    }

    private void DisposeUpdates()
    {
        _session.Updates.Changed -= OnUpdatesChanged;
        Texts.Changed -= OnUpdateTextsChanged;
        if (_updateNotice != null) _updateNotice.Click -= OnUpdateNoticeClick;
        if (_checkUpdatesMenu != null) _checkUpdatesMenu.Click -= OnCheckUpdatesMenuClick;
    }
}
