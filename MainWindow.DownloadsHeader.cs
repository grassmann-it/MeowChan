using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private bool _measuringDownloadsHeader;

    private void InitializeDownloadsHeader()
    {
        WorkspaceControl<Border>("DownloadsPanel").SizeChanged += (_, _) => UpdateDownloadsHeaderLayout();
        WorkspaceControl<ComboBox>("WorkspaceModeComboBox").SelectionBoxItemTemplate = new FuncDataTemplate<object>((value, _) => new TextBlock
        {
            Text = value is StackPanel panel ? panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text
                : value is ComboBoxItem item ? Texts.T("mode." + (item.Tag as string)?.ToLowerInvariant()) : value?.ToString()
        });
        WorkspaceControl<ComboBox>("WorkspaceModeComboBox").DropDownOpened += (_, _) => RefreshWorkspaceModeDescriptions();
        WorkspaceControl<ComboBox>("WorkspaceModeComboBox").DropDownClosed += (_, _) => RefreshWorkspaceModeDescriptions();
    }

    private void RefreshWorkspaceModeDescriptions()
    {
        bool open = WorkspaceControl<ComboBox>("WorkspaceModeComboBox").IsDropDownOpen;
        foreach ((string name, string mode) in new[] { ("BarebonesModeItem", "barebones"), ("SimpleModeItem", "simple"), ("AdvancedModeItem", "advanced") })
        {
            ComboBoxItem item = WorkspaceControl<ComboBoxItem>(name);
            string title = Texts.T("mode." + mode);
            if (open)
            {
                item.Content = new StackPanel { Spacing = 3, MaxWidth = 320, Children =
                {
                    new TextBlock { Text = title },
                    new TextBlock { Text = Texts.T("mode." + mode + "Description"), FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = Texts.T("mode.sameSession"), FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                }};
            }
            else item.Content = title;
            AutomationProperties.SetName(item, open ? title + ". " + Texts.T("mode." + mode + "Description") + ". " + Texts.T("mode.sameSession") : title);
        }
    }

    private void UpdateDownloadsHeaderLayout()
    {
        if (!_workspaceInitialized || _measuringDownloadsHeader) return;
        _measuringDownloadsHeader = true;
        try
        {
            double width = WorkspaceControl<Border>("DownloadsPanel").Bounds.Width;
            if (width <= 0) return;
            Button columns = WorkspaceControl<Button>("DownloadsColumnsButton");
            Button copy = WorkspaceControl<Button>("CopyAllUrlsButton");
            Button more = WorkspaceControl<Button>("DownloadsMoreButton");
            columns.IsVisible = true; copy.IsVisible = true; more.IsVisible = false;
            Size unconstrained = new(double.PositiveInfinity, double.PositiveInfinity);
            TabControl tabs = WorkspaceControl<TabControl>("DownloadsTabs");
            StackPanel actions = WorkspaceControl<StackPanel>("WorkspaceListActions");
            double tabWidth = tabs.Padding.Left + tabs.Padding.Right;
            foreach (TabItem item in tabs.Items.OfType<TabItem>())
            {
                item.Measure(unconstrained);
                tabWidth += item.DesiredSize.Width;
            }
            Control[] fullActions = actions.Children.Where(item => item.IsVisible).ToArray();
            double actionWidth = actions.Margin.Left + actions.Margin.Right + Math.Max(0, fullActions.Length - 1) * actions.Spacing;
            foreach (Control action in fullActions)
            {
                action.Measure(unconstrained);
                actionWidth += action.DesiredSize.Width;
            }
            // Barebones keeps its quiet header at every width; the other modes
            // retain the measured overflow behavior. Both actions remain in More.
            bool compact = _config.WorkspaceMode == "Barebones" || tabWidth + actionWidth + 8 > width;
            columns.IsVisible = !compact; copy.IsVisible = !compact; more.IsVisible = compact;
        }
        finally { _measuringDownloadsHeader = false; }
    }

    private void OnDownloadsMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        ContextMenu menu = new();
        MenuItem columns = new() { Header = Texts.T("downloads.columns") };
        // Use the same column menu and commands as the full-width header.
        columns.Click += (_, args) => OnDownloadsColumnsClick(button, args);
        MenuItem copy = new() { Header = Texts.T("downloads.copySessionUrls", UrlList.Count + BoardsList.Count), IsEnabled = WorkspaceControl<Button>("CopyAllUrlsButton").IsEnabled };
        copy.Click += async (_, _) => await CopyUrlsAsync(UrlList.Select(row => row.Url).Concat(BoardsList.Select(row => row.Url)));
        ToolTip.SetTip(copy, Texts.T("downloads.copySessionUrlsHint"));
        AutomationProperties.SetHelpText(copy, Texts.T("downloads.copySessionUrlsHint"));
        menu.Items.Add(columns); menu.Items.Add(copy);
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private static bool IsAdvancedWorkColumn(DataGridColumn column) => column.SortMemberPath is nameof(ThreadRow.MissingMedia) or nameof(ThreadRow.EarliestRetry);
}
