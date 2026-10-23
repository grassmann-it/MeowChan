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
using MeowChan.Desktop.Commands;
using Ursa.Controls;

namespace MeowChan;

public partial class MainWindow
{

    private void InitializeDownloadsHeader()
    {
        WorkspaceControl<Button>("DownloadsColumnsButton").Command = new DelegateCommand(
            () => OnDownloadsColumnsClick(WorkspaceControl<Button>("DownloadsColumnsButton"), new RoutedEventArgs()),
            () => !_isClosing);
        WorkspaceControl<Button>("CopyAllUrlsButton").Command = new DelegateCommand(
            async () => await CopyUrlsAsync(UrlList.Select(row => row.Url).Concat(BoardsList.Select(row => row.Url))),
            () => !_isClosing && (UrlList.Count > 0 || BoardsList.Count > 0));
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
        if (!_workspaceInitialized) return;
        OverflowMode mode = _config.WorkspaceMode == "Barebones" ? OverflowMode.Always : OverflowMode.AsNeeded;
        foreach (string name in new[] { "DownloadsColumnsButton", "CopyAllUrlsButton" })
        {
            Button button = WorkspaceControl<Button>(name);
            ToolBar.SetOverflowMode(button, mode);
            CommandBindings.Refresh(button);
        }
        ActionToolBar toolbar = WorkspaceControl<ActionToolBar>("WorkspaceListActions");
        AutomationProperties.SetName(toolbar, Texts.T("downloads.title"));
        toolbar.RefreshOverflowLabel();
        toolbar.InvalidateMeasure();
    }

    private static bool IsAdvancedWorkColumn(DataGridColumn column) => column.SortMemberPath is nameof(ThreadRow.MissingMedia) or nameof(ThreadRow.EarliestRetry);
}
