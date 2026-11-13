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
        DownloadsColumnsButton.Command = new DelegateCommand(
            () => OnDownloadsColumnsClick(DownloadsColumnsButton, new RoutedEventArgs()),
            () => !_isClosing);
        CopyAllUrlsButton.Command = new DelegateCommand(
            async () => await CopyUrlsAsync(UrlList.Select(row => row.Url).Concat(BoardsList.Select(row => row.Url))),
            () => !_isClosing && (UrlList.Count > 0 || BoardsList.Count > 0));
        ComboBox modes = WorkspaceModeComboBox;
        RefreshWorkspaceModeSelectionPresentation(modes);
        modes.DropDownOpened += (_, _) => RefreshWorkspaceModeDescriptions();
        modes.DropDownClosed += (_, _) => RefreshWorkspaceModeDescriptions();
    }

    internal static void RefreshWorkspaceModeSelectionPresentation(ComboBox modes) =>
        modes.SelectionBoxItemTemplate = new FuncDataTemplate<object>((value, _) => new TextBlock
        {
            Text = WorkspaceModeSelectionText(value)
        });

    private static string? WorkspaceModeSelectionText(object? value) => value switch
    {
        StackPanel panel => panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
        ComboBoxItem { Tag: string mode } => Texts.T("mode." + mode.ToLowerInvariant()),
        _ => value?.ToString()
    };

    private void RefreshWorkspaceModeDescriptions()
    {
        ComboBox modes = WorkspaceModeComboBox;
        bool open = modes.IsDropDownOpen;
        foreach ((string name, string mode) in new[] { ("BarebonesModeItem", "barebones"), ("SimpleModeItem", "simple"), ("AdvancedModeItem", "advanced") })
        {
            ComboBoxItem item = WorkspaceControl<ComboBoxItem>(name);
            string title = Texts.T("mode." + mode);
            if (open)
            {
                item.Content = new StackPanel { Spacing = 3, MaxWidth = 320, Children =
                {
                    new TextBlock { Text = title },
                    new TextBlock { Text = Texts.T("mode." + mode + "Description"), TextWrapping = Avalonia.Media.TextWrapping.Wrap }.MetaText(),
                    new TextBlock { Text = Texts.T("mode.sameSession"), TextWrapping = Avalonia.Media.TextWrapping.Wrap }.CaptionText()
                }};
            }
            else item.Content = title;
            AutomationProperties.SetName(item, open ? title + ". " + Texts.T("mode." + mode + "Description") + ". " + Texts.T("mode.sameSession") : title);
        }
        // The selected item object stays the same when its label is localized. Avalonia
        // can keep the TextBlock built by SelectionBoxItemTemplate, so replace that
        // template for both the open detail presentation and the closed title.
        RefreshWorkspaceModeSelectionPresentation(modes);
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
        ActionToolBar toolbar = WorkspaceListActions;
        AutomationProperties.SetName(toolbar, Texts.T("downloads.title"));
        toolbar.RefreshOverflowLabel();
        toolbar.InvalidateMeasure();
    }

    private static bool IsAdvancedWorkColumn(DataGridColumn column) => column.SortMemberPath is nameof(ThreadRow.MissingMedia) or nameof(ThreadRow.EarliestRetry);
}
