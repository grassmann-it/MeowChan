using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
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
        using BoardRow boardTemplate = new(SnapshotFixtures.Board(2900, "local"));
        ThreadRow[] templates = rows;
        SyntheticShellRows synthetic;
        try { synthetic = await SyntheticShellRows.FromRowsAsync(window, templates, [boardTemplate]); }
        finally { foreach (ThreadRow template in templates) template.Dispose(); }
        await using SyntheticShellRows fixtureLifetime = synthetic;
        rows = synthetic.Threads; complete = rows[0]; waiting = rows[1];
        BoardRow board = synthetic.Boards[0];
        TextBox input = window.FindControl<TextBox>("DownloadSearchInput")!;
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        TabControl tabs = window.FindControl<TabControl>("DownloadsTabs")!;
        try
        {
            Require(complete.CompletedMedia == 5 && complete.SelectedMedia == 5 && complete.ExcludedMedia == 5 && complete.Completion == 100,
                "The 10 known / 5 excluded / 5 saved fixture did not present 5/5 at 100 percent.");
            Require(!complete.HasExceptionHint && waiting.HasExceptionHint && waiting.MediaErrorCount == 0,
                "Normal success or waiting media were projected as an error.");
            tabs.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => window.VisibleThreads.Contains(rows[^1]));
            CheckRowFocus(window, grid, complete, waiting);
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(rows[0]); grid.SelectedItems.Add(rows[1]);
            window.FocusDownloadSearch();
            input.Text = "aLPHa local";
            await synthetic.WaitForAppliedAsync();
            await Program.WaitUntilAsync(() => window.VisibleThreads.Count == 150);
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], rows[0]), "Filtering did not remove only the excluded selection.");
            grid.ScrollIntoView(rows[^2], null);
            await Task.Delay(40);
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], rows[0]), "Scrolling changed the filtered selection.");
            input.Text = "";
            await synthetic.WaitForAppliedAsync();
            await Program.WaitUntilAsync(() => window.VisibleThreads.Contains(rows[^1]));
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], rows[0]), "Clearing the filter restored an excluded selection.");
            Require(window.VisibleThreads.Where(rows.Contains).SequenceEqual(rows), "Clearing the filter changed the original unsorted row order.");
            input.Text = "Beta local";
            await synthetic.WaitForAppliedAsync();
            await Program.WaitUntilAsync(() => window.VisibleThreads.Count == 150);
            Require(grid.SelectedItems.Count == 0, "Removing the selected row automatically selected a remaining result.");
            input.Text = "no-local-result";
            await synthetic.WaitForAppliedAsync();
            await Program.WaitUntilAsync(() => window.VisibleThreads.Count == 0);
            Require(grid.SelectedItems.Count == 0 && window.VisibleResultCount == 0, "Zero results retained hidden batch targets.");
            input.Text = "local";
            await synthetic.WaitForAppliedAsync();
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => window.VisibleBoards.Contains(board));
            Require(window.VisibleResultCount == window.VisibleBoards.Count, "Board count did not follow the active list.");
            Click(window.FindControl<Button>("DownloadSearchClose")!);
            Require(window.FindControl<Control>("DownloadSearchBar")!.IsVisible && window.FindControl<Button>("DownloadSearchClear")!.IsVisible,
                "Collapsed active search lost its filter summary or clear action.");
            Click(window.FindControl<Button>("DownloadSearchClear")!);
            await Program.WaitUntilAsync(() => input.Text == "" && !window.FindControl<Control>("DownloadSearchBar")!.IsVisible);
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
                    double minimumWidth = mode == "Barebones" ? 640 : 760;
                    double requestedHeight = mode == "Barebones" ? 480 : 500;
                    window.Width = minimumWidth;
                    window.Height = requestedHeight;
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
                    foreach (double width in new[] { minimumWidth, 1380d })
                    {
                        window.Width = width;
                        window.Height = requestedHeight;
                        try
                        {
                            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, requestedHeight));
                        }
                        finally
                        {
                            Console.WriteLine($"SMOKE download header resize: mode={mode}, width={width}, requested={window.Width}x{window.Height}, client={window.Bounds.Size}, frame={window.FrameSize}, minimum={window.MinWidth}x{window.MinHeight}, scale={window.RenderScaling}, screen={window.Screens.ScreenFromWindow(window)?.WorkingArea}.");
                        }
                        window.UpdateLayout();
                        Button columns = window.FindControl<Button>("DownloadsColumnsButton")!;
                        Button copy = window.FindControl<Button>("CopyAllUrlsButton")!;
                        ActionToolBar toolbar = window.FindControl<ActionToolBar>("WorkspaceListActions")!;
                        var more = toolbar.More!;
                        bool columnsOverflow = toolbar.OverflowHost!.Children.Contains(columns);
                        bool copyOverflow = toolbar.OverflowHost!.Children.Contains(copy);
                        Require(mode != "Barebones" || more.IsEffectivelyVisible && columnsOverflow && copyOverflow,
                            "Barebones exposed rare actions outside More after resizing.");
                        // WV1 (46d79bf3): Columns and Copy URLs are Direct only in Advanced; Simple keeps them under More at any width.
                        Require(mode == "Barebones" || width < 1380 || (mode == "Simple"
                                ? columnsOverflow && copyOverflow && more.IsEffectivelyVisible
                                : !columnsOverflow && !copyOverflow && !more.IsVisible),
                            "A wide " + mode + " header does not follow the work-view matrix (Simple: Columns and Copy URLs under More; Advanced: no overflow menu).");
                        Require(window.FindControl<Button>("DownloadSearchButton")!.IsEffectivelyVisible
                            && window.FindControl<Button>("DetailsToggleButton")!.IsEffectivelyVisible,
                            "Header resizing hid a primary action.");
                        CheckHeaderAlignment(tabs, toolbar, window);
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
                    window.Width = minimumWidth;
                    window.Height = requestedHeight;
                    await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, minimumWidth, requestedHeight));
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
            await synthetic.WaitForAppliedAsync();
            Texts.SetLanguage("en");
            tabs.SelectedIndex = 0;
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            window.Width = 1380; window.Height = 850;
            await synthetic.DisposeAsync();
        }
    }

    private static void CheckRowFocus(MainWindow window, DataGrid grid, ThreadRow first, ThreadRow second)
    {
        grid.SelectedItem = first;
        grid.ScrollIntoView(first, null);
        Require(grid.Focus(NavigationMethod.Tab), "The Downloads table did not take keyboard focus.");
        window.UpdateLayout();

        DataGridRow Row(ThreadRow item) => grid.GetVisualDescendants().OfType<DataGridRow>()
            .Single(row => ReferenceEquals(row.DataContext, item));
        Border RowBackground(DataGridRow row) => row.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == "BackgroundBorder");
        void AssertRowFocus(DataGridRow row)
        {
            DataGridCell cell = row.GetVisualDescendants().OfType<DataGridCell>()
                .Single(cell => cell.Classes.Contains(":current"));
            foreach (string part in new[] { "CurrencyVisual", "FocusVisual" })
                Require(cell.GetVisualDescendants().OfType<Control>().Single(control => control.Name == part).Opacity == 0,
                    $"The current Downloads cell still draws {part}.");
            Require(RowBackground(row).BorderThickness == new Thickness(3, 1, 1, 1),
                "The focused Downloads row lacks a border.");
        }

        AssertRowFocus(Row(first));
        DataGridColumn currentColumn = grid.CurrentColumn;
        grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Require(grid.CurrentColumn != currentColumn && ReferenceEquals(grid.SelectedItem, first),
            "Right Arrow did not move the current cell within the row.");
        AssertRowFocus(Row(first));
        grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        Require(ReferenceEquals(grid.SelectedItem, second), "Down Arrow did not move the selection to the next row.");
        AssertRowFocus(Row(second));
        window.FindControl<TextBox>("UrlInput")!.Focus(NavigationMethod.Tab);
        Require(RowBackground(Row(second)).BorderThickness == new Thickness(3, 0, 0, 0),
            "Moving focus out of the table did not leave the normal row selection.");
    }

    private static void CheckHeaderAlignment(TabControl tabs, ActionToolBar toolbar, Window window)
    {
        static double Centre(Control control, Window owner) =>
            control.TranslatePoint(new Point(0, control.Bounds.Height / 2), owner)!.Value.Y;

        TabItem firstTab = tabs.Items.OfType<TabItem>().First();
        Border header = window.FindControl<Border>("DownloadsListHeader")!;
        var controls = tabs.Items.OfType<TabItem>().Cast<Control>()
            .Concat(toolbar.DirectHost!.Children.OfType<Button>().Where(button => button.IsEffectivelyVisible))
            .Concat(toolbar.More is { IsEffectivelyVisible: true } more ? [more] : Array.Empty<Control>());
        foreach (Control control in controls)
        {
            Point? right = control.TranslatePoint(new Point(control.Bounds.Width, 0), header);
            Require(right is { } edge && edge.X <= header.Bounds.Width - header.Padding.Right + 1,
                $"The Downloads header control {control.Name} is clipped or outside the header.");
            Require(Math.Abs(Centre(firstTab, window) - Centre(control, window)) <= 1,
                $"The Downloads header control {control.Name} is vertically misaligned ({Centre(control, window)} vs {Centre(firstTab, window)}).");
            Require(Math.Abs(firstTab.Bounds.Height - control.Bounds.Height) <= 1,
                $"The Downloads header control {control.Name} has a different height from the tabs ({control.Bounds.Height} vs {firstTab.Bounds.Height}).");
            if (control is Button { Content: StackPanel label })
                Require(label.Spacing == 8, $"The icon and label in {control.Name} are too close.");
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
