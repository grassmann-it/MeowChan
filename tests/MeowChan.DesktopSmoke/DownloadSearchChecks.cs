using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using System.ComponentModel;
using MeowChan.Persistence;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class DownloadSearchChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        await ActionToolBarFocusChecks.RunAsync(window);
        ThreadRow[] rows = Enumerable.Range(2000, 300).Select(value => new ThreadRow(SnapshotFixtures.Thread(value,
            value.ToString(), value % 2 == 0 ? "Alpha local fixture" : "Beta local fixture", board: "local"))).ToArray();
        ThreadRow complete = rows[0], waiting = rows[1];
        MediaWork Saved(int value) => new() { ResourceUrl = complete.Url, Generation = 1, MediaIdentifier = value.ToString(),
            DestinationPath = "/never/read/" + value, DownloadUrl = "https://fixture.invalid/" + value,
            OriginalFileName = value + ".jpg", Status = MediaWorkStatus.Succeeded };
        ThreadWorkSnapshot completeWork = new(complete.Url, WorkIntent.Watching, ThreadSourceStateValue.Active, null, false,
            Enumerable.Range(1, 5).Select(Saved).ToArray());
        complete.UpdateSnapshot(new DesktopThreadEntry(complete.WatchId, complete.Snapshot with { KnownMedia = 10, CompletedMedia = 5,
            Content = new MeowChan.Archive.ThreadContentStatus(true, null, null, null, 5), Work = completeWork }, complete.HostName, complete.BoardName));
        ThreadWorkSnapshot waitingWork = new(waiting.Url, WorkIntent.Watching, ThreadSourceStateValue.NotFound, DateTimeOffset.UtcNow, false,
            new[] { Saved(1), Saved(2) with { Status = MediaWorkStatus.Waiting, HttpStatus = 404, DueAt = DateTimeOffset.UtcNow.AddMinutes(15) } });
        waiting.UpdateSnapshot(new DesktopThreadEntry(waiting.WatchId, waiting.Snapshot with { KnownMedia = 2, CompletedMedia = 1,
            Work = waitingWork }, waiting.HostName, waiting.BoardName));
        using BoardRow board = new(SnapshotFixtures.Board(2900, "local"));
        TextBox input = window.FindControl<TextBox>("DownloadSearchInput")!;
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        TabControl tabs = window.FindControl<TabControl>("DownloadsTabs")!;
        try
        {
            Require(complete.CompletedMedia == 5 && complete.SelectedMedia == 5 && complete.ExcludedMedia == 5 && complete.Completion == 100,
                "The 10 known / 5 excluded / 5 saved fixture did not present 5/5 at 100 percent.");
            Require(!complete.HasExceptionHint && waiting.HasExceptionHint && waiting.MediaErrorCount == 0,
                "Normal success or waiting media were projected as an error.");
            foreach (ThreadRow row in rows) window.UrlList.Add(row);
            window.BoardsList.Add(board);
            tabs.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => window.VisibleThreads.Contains(rows[^1]));
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(rows[0]); grid.SelectedItems.Add(rows[1]);
            window.FocusDownloadSearch();
            input.Text = "aLPHa local";
            await Program.WaitUntilAsync(() => window.VisibleThreads.Count == 150);
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], rows[0]), "Filtering did not remove only the excluded selection.");
            grid.ScrollIntoView(rows[^2], null);
            await Task.Delay(40);
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], rows[0]), "Scrolling changed the filtered selection.");
            input.Text = "";
            await Program.WaitUntilAsync(() => window.VisibleThreads.Contains(rows[^1]));
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], rows[0]), "Clearing the filter restored an excluded selection.");
            Require(window.VisibleThreads.Where(rows.Contains).SequenceEqual(rows), "Clearing the filter changed the original unsorted row order.");
            input.Text = "Beta local";
            await Program.WaitUntilAsync(() => window.VisibleThreads.Count == 150);
            Require(grid.SelectedItems.Count == 0, "Removing the selected row automatically selected a remaining result.");
            input.Text = "no-local-result";
            await Program.WaitUntilAsync(() => window.VisibleThreads.Count == 0);
            Require(grid.SelectedItems.Count == 0 && window.VisibleResultCount == 0, "Zero results retained hidden batch targets.");
            input.Text = "local";
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => window.VisibleBoards.Contains(board));
            Require(window.VisibleResultCount == window.VisibleBoards.Count, "Board count did not follow the active list.");
            Click(window.FindControl<Button>("DownloadSearchClose")!);
            Require(window.FindControl<Grid>("DownloadSearchBar")!.IsVisible && window.FindControl<Button>("DownloadSearchClear")!.IsVisible,
                "Collapsed active search lost its filter summary or clear action.");
            Click(window.FindControl<Button>("DownloadSearchClear")!);
            await Program.WaitUntilAsync(() => input.Text == "" && !window.FindControl<Grid>("DownloadSearchBar")!.IsVisible);
            tabs.SelectedIndex = 0;
            TextBox url = window.FindControl<TextBox>("UrlInput")!;
            url.Text = "not-a-valid-url";
            Button add = window.FindControl<Button>("AddUrlButton")!;
            Click(add);
            // A previous admission can already have left this panel visible.
            // Wait for this submission's completed feedback, not the old panel.
            await Program.WaitUntilAsync(() => add.IsEnabled
                && window.FindControl<Control>("AdmissionRepairPanel")!.IsVisible
                && AutomationProperties.GetName(window.FindControl<SelectableTextBlock>("AdmissionRepairText")!)
                    ?.StartsWith("not-a-valid-url · ", StringComparison.Ordinal) == true);
            Require(url.Text == "not-a-valid-url", "Rejected input was lost.");
            Require(window.FindControl<SelectableTextBlock>("AdmissionRepairText")!.Text?.Contains("HTTP") == true,
                "URL rejection did not expose the cause and repair form.");
            Program.CaptureWindow(window, "smoke-download-url-repair.png");
            window.FindControl<Control>("AdmissionRepairPanel")!.IsVisible = false;
            window.FindControl<Border>("AdmissionResultsPanel")!.IsVisible = false;
            url.Text = "";
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            DataGridColumn missingColumn = grid.Columns.Single(column => column.SortMemberPath == nameof(ThreadRow.MissingMedia));
            DataGridColumn retryColumn = grid.Columns.Single(column => column.SortMemberPath == nameof(ThreadRow.EarliestRetry));
            Require(!missingColumn.IsVisible && !retryColumn.IsVisible, "Optional work columns were visible by default.");
            Button columnsButton = window.FindControl<Button>("DownloadsColumnsButton")!;
            ActionToolBar columnActions = window.FindControl<ActionToolBar>("WorkspaceListActions")!;
            window.UpdateLayout();
            if (columnActions.OverflowHost?.Children.Contains(columnsButton) == true)
            {
                Require(columnActions.More?.IsEffectivelyVisible == true, "The columns overflow action is not reachable.");
                columnActions.More!.IsChecked = true;
            }
            await Program.WaitUntilAsync(() => columnsButton.IsEffectivelyVisible && columnsButton.IsEffectivelyEnabled);
            SmokeActionClick.Execute(columnsButton);
            MenuItem missingItem = columnsButton.ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, Texts.T("column.missingMedia")));
            missingItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            columnsButton.ContextMenu.Close();
            if (columnActions.More != null) columnActions.More.IsChecked = false;
            await Program.WaitUntilAsync(() => missingColumn.IsVisible);
            grid.CollectionView.SortDescriptions.Clear();
            missingColumn.Sort(ListSortDirection.Ascending);
            await Program.WaitUntilAsync(() => ReferenceEquals(grid.CollectionView.Cast<ThreadRow>().First(), complete));
            retryColumn.Sort(ListSortDirection.Descending);
            await Program.WaitUntilAsync(() => ReferenceEquals(grid.CollectionView.Cast<ThreadRow>().First(), waiting));
            grid.CollectionView.SortDescriptions.Clear();
            grid.ScrollIntoView(complete, null);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
                {
                    await PresentationChecks.SwitchModeAsync(window, mode);
                    Texts.SetLanguage(language);
                    Application.Current!.RequestedThemeVariant = theme;
                    Require(missingColumn.IsVisible == (mode == "Advanced") && !retryColumn.IsVisible,
                        "Optional work columns did not preserve their Advanced-only preference.");
                    window.Width = mode == "Barebones" ? 640 : 760;
                    window.Height = mode == "Barebones" ? 480 : 500;
                    await Task.Delay(80);
                    window.UpdateLayout();
                    Control actions = window.FindControl<ActionToolBar>("WorkspaceListActions")!;
                    Point? actionStart = actions.TranslatePoint(default, window);
                    Point? tabEnd = tabs.TranslatePoint(new Point(tabs.Bounds.Width, 0), window);
                    Require(actionStart != null && tabEnd != null && tabEnd.Value.X <= actionStart.Value.X + 1,
                        "Download actions overlap the independently measured tabs at " + mode + "/" + language);
                    foreach (string tabName in new[] { "ThreadsTab", "BoardsTab" })
                    {
                        TabItem item = window.FindControl<TabItem>(tabName)!;
                        Point? itemEnd = item.TranslatePoint(new Point(item.Bounds.Width, 0), window);
                        Require(item.IsEffectivelyVisible && item.Bounds.Width > 0 && itemEnd != null && itemEnd.Value.X <= actionStart!.Value.X + 1,
                            "A download tab or count is covered by list actions.");
                    }
                    Require(window.FindControl<Button>("DetailsToggleButton")!.IsEffectivelyVisible, "Compact header hid the direct details action.");
                    foreach (double width in new[] { window.Width, 1380d })
                    {
                        window.Width = width;
                        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, window.Height));
                        window.UpdateLayout();
                        Button columns = window.FindControl<Button>("DownloadsColumnsButton")!;
                        Button copy = window.FindControl<Button>("CopyAllUrlsButton")!;
                        ActionToolBar toolbar = window.FindControl<ActionToolBar>("WorkspaceListActions")!;
                        var more = toolbar.More!;
                        bool columnsOverflow = toolbar.OverflowHost!.Children.Contains(columns);
                        bool copyOverflow = toolbar.OverflowHost!.Children.Contains(copy);
                        Require(mode != "Barebones" || more.IsEffectivelyVisible && columnsOverflow && copyOverflow,
                            "Barebones exposed rare actions outside More after resizing.");
                        Require(mode == "Barebones" || width < 1380 || !columnsOverflow && !copyOverflow && !more.IsVisible,
                            "A wide Simple/Advanced header retained its overflow menu.");
                        Require(window.FindControl<Button>("DownloadSearchButton")!.IsEffectivelyVisible
                            && window.FindControl<Button>("DetailsToggleButton")!.IsEffectivelyVisible,
                            "Header resizing hid a primary action.");
                        if (more.IsVisible)
                        {
                            var command = columns.Command;
                            more.IsChecked = true;
                            await Task.Delay(40);
                            Require(toolbar.OverflowHost!.Children.Contains(columns) == columnsOverflow
                                && toolbar.OverflowHost.Children.Contains(copy) == copyOverflow
                                && ReferenceEquals(command, columns.Command), "Overflow duplicated or replaced action commands.");
                            if (columnsOverflow)
                            {
                                SmokeActionClick.Execute(columns);
                                Require(columns.ContextMenu!.Items.OfType<MenuItem>().Any(), "Overflow did not open column choices.");
                                columns.ContextMenu.Close();
                            }
                            toolbar.OverflowHost.RaiseEvent(new KeyEventArgs
                                { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                            await Task.Delay(40);
                            Require(more.IsFocused, "Closing overflow lost its return focus.");
                        }
                        Program.CaptureWindow(window, "smoke-download-header-" + mode.ToLowerInvariant() + "-" + language
                            + "-" + theme.Key.ToString()!.ToLowerInvariant() + "-" + (width == 1380 ? "wide" : "minimum") + ".png");
                    }
                    // The wide layout probe may exceed a small hosted desktop.
                    // Restore its bounded geometry before the next mode switch;
                    // Simple -> Advanced intentionally preserves the live size.
                    window.Width = mode == "Barebones" ? 640 : 760;
                    await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, window.Width, window.Height));
                }
            }
            ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
            double footerHeight = window.FindControl<Border>("WorkspaceNavigation")!.Bounds.Height;
            modes.IsDropDownOpen = true;
            await Task.Delay(60);
            Require(modes.Items.OfType<ComboBoxItem>().All(item => item.Content is StackPanel), "Opened modes did not explain their detail level.");
            Require(Math.Abs(window.FindControl<Border>("WorkspaceNavigation")!.Bounds.Height - footerHeight) < 1,
                "Opening mode descriptions enlarged the footer.");
            modes.IsDropDownOpen = false;
            await Task.Delay(40);
            Require(modes.Items.OfType<ComboBoxItem>().All(item => item.Content is string), "Closed mode selector retained permanent explanation blocks.");
            Console.WriteLine("SMOKE download search: 300 passive rows, literal case-insensitive filter, result-bound selection, no restoration, scrolling, boards, collapsed clear, independently measured EN/DE minimum headers passed.");
        }
        finally
        {
            input.Text = "";
            foreach (ThreadRow row in rows) { window.UrlList.Remove(row); row.Dispose(); }
            window.BoardsList.Remove(board);
            Texts.SetLanguage("en");
            tabs.SelectedIndex = 0;
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            window.Width = 1380; window.Height = 850;
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
