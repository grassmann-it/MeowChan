using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class SelectionChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Require(window.SessionHost.IsPaused, "Selection fixture must never start network work.");
        window.Width = 1280; window.Height = 900;
        for (int i = 1000; i < 1005; i++) await window.SessionHost.AddAsync("https://boards.4chan.org/g/thread/" + i);
        await Program.WaitUntilAsync(() => window.UrlList.Count == 5);
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        ThreadRow[] rows = window.UrlList.OrderBy(row => row.ThreadId).ToArray();
        await WaitRow(grid, rows[4]);
        Press(window, Row(grid, rows[0]));
        Press(window, Row(grid, rows[2]), KeyModifiers.Control);
        Require(grid.SelectedItems.Count == 2, "Ctrl-click did not extend the native watch selection.");
        Press(window, Row(grid, rows[2]), right: true);
        Require(grid.SelectedItems.Count == 2, "Right-click collapsed the existing multiple selection.");
        grid.ContextMenu!.Close();
        await Task.Delay(80);
        Press(window, Row(grid, rows[4]), right: true);
        Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItem, rows[4]), "Right-click unselected watch did not replace selection.");
        grid.ContextMenu.Close();
        await Task.Delay(80);
        Point blank = grid.TranslatePoint(new Point(20, grid.Bounds.Height - 24), window)!.Value;
        PressAt(window, blank, KeyModifiers.None, right: true);
        Require(grid.SelectedItems.Count == 0, "Blank-space right-click retained stale watch targets.");
        Press(window, Row(grid, rows[0]));
        Press(window, Row(grid, rows[3]), KeyModifiers.Shift);
        Require(grid.SelectedItems.Count == 4, "Shift-click did not select a contiguous native row range.");
        Press(window, Row(grid, rows[0]));
        PressKey(grid, Key.Down, KeyModifiers.Shift);
        Require(grid.SelectedItems.Count == 2, "Native Shift+Down did not extend a watch range.");
        PressKey(grid, Key.Down);
        Require(grid.SelectedItems.Count == 1, "Native Down did not move to one row.");
        ThreadRow selectedBeforeFocusMove = grid.SelectedItems.Cast<ThreadRow>().Single();
        ThreadRow[] currentOrder = grid.CollectionView.Cast<ThreadRow>().ToArray();
        ThreadRow focusedAfterMove = currentOrder[Array.IndexOf(currentOrder, selectedBeforeFocusMove) + 1];
        Press(window, Row(grid, focusedAfterMove), KeyModifiers.Control);
        Press(window, Row(grid, focusedAfterMove), KeyModifiers.Control);
        await Task.Delay(50);
        PressKey(grid, Key.Enter);
        await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("ReaderHost")!.IsVisible
            && !window.ArchivePage.IsBusy && window.ArchivePage.CurrentThread?.Summary.Id == focusedAfterMove.Snapshot.Id);
        Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], selectedBeforeFocusMove),
            "Ctrl-toggle/Enter changed the selection instead of opening only the focused unselected lead.");
        Click(window.ArchivePage.FindControl<Button>("ArchiveCloseReader")!);
        await Program.WaitUntilAsync(() => !window.FindControl<ContentControl>("ReaderHost")!.IsVisible);
        PressKey(grid, Key.A, KeyModifiers.Control);
        Require(grid.SelectedItems.Count == 5, "List Ctrl+A did not select all watches.");
        long[] selectedIds = grid.SelectedItems.Cast<ThreadRow>().Select(row => row.WatchId).Order().ToArray();
        Texts.SetLanguage("de");
        await Task.Delay(50);
        await PresentationChecks.SwitchModeAsync(window, "Barebones");
        Require(grid.SelectedItems.Cast<ThreadRow>().Select(row => row.WatchId).Order().SequenceEqual(selectedIds), "Language or mode lost selected watch identities.");
        window.Width = 640; window.Height = 480;
        await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - 640) < 1);
        await CheckHeader(window, "smoke-selection-header-640.png");
        await PresentationChecks.SwitchModeAsync(window, "Simple");
        window.Width = 760; window.Height = 600;
        await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - 760) < 1);
        await CheckHeader(window, "smoke-selection-header-760.png");
        window.Width = 1280; window.Height = 900;
        Texts.SetLanguage("en");
        await Task.Delay(80);

        TextBox input = window.FindControl<TextBox>("UrlInput")!;
        input.Text = "keep native input";
        PressKey(input, Key.Delete); PressKey(input, Key.E, KeyModifiers.Control); PressKey(input, Key.A, KeyModifiers.Control);
        Require(window.UrlList.Count == 5 && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
            "Text input shortcut escaped to a list command.");
        PressKey(grid, Key.E, KeyModifiers.Control);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => page.FindControl<Border>("ArchiveExportDialog")?.IsVisible == true && page.FindControl<Button>("ArchiveExportClose")!.IsEnabled);
        Require(window.SelectedPage == MainWindow.WorkspacePage.Archive && !window.FindControl<ContentControl>("ReaderHost")!.IsVisible,
            "Batch export did not display Archive without loading a reader.");
        Require(page.FindControl<TextBlock>("ArchiveExportSummary")!.Text!.Split('\n').Length == 6,
            "Batch export did not freeze all five resource IDs.");
        Click(page.FindControl<Button>("ArchiveExportClose")!);
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads);
        Require(grid.SelectedItems.Cast<ThreadRow>().Select(row => row.WatchId).Order().SequenceEqual(selectedIds), "Export close lost the original watch set.");
        await Program.WaitUntilAsync(() => ReferenceEquals(window.FocusManager!.GetFocusedElement(), grid));

        // Enter opens only the lead row; the original multiple selection survives return.
        PressKey(grid, Key.Enter);
        await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("ReaderHost")!.IsVisible && !page.IsBusy);
        Require(page.CurrentThread != null && grid.SelectedItems.Count == 5, "Enter collapsed the selection or failed to open one reader.");
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        await Program.WaitUntilAsync(() => !window.FindControl<ContentControl>("ReaderHost")!.IsVisible);
        PressKey(grid, Key.Delete);
        await Program.WaitUntilAsync(() => window.UrlList.Count == 0);
        await using (ArchiveReader reader = await window.SessionHost.OpenArchiveReaderAsync(CancellationToken.None))
            Require((await reader.SelectThreadIdsAsync(new ArchiveThreadQuery(), CancellationToken.None)).Count == 52,
                "Removing watches deleted archived posts.");
        Console.WriteLine("SMOKE selection watches: native Ctrl/Shift range+arrow, selected/unselected right-click, EN/DE/modes, narrow logo, input shielding, frozen batch-export return, Enter reader and non-destructive batch Delete passed.");

        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<DataGrid>("ArchiveThreadTable")!.ItemsSource.Cast<object>().Count() == 50);
        DataGrid table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
        ArchiveThreadRow[] archived = table.ItemsSource.Cast<ArchiveThreadRow>().ToArray();
        await WaitRow(table, archived[3]);
        await Task.Delay(80);
        Press(window, Row(table, archived[0]));
        await Task.Delay(80);
        Press(window, Row(table, archived[3]), KeyModifiers.Shift);
        Program.CaptureWindow(window, "smoke-selection-archive-range.png");
        Require(table.SelectedItems.Count == 4, "Archive native Shift range did not work.");
        Press(window, Row(table, archived[3]), right: true);
        Require(table.SelectedItems.Count == 4, "Archive selected right-click collapsed the set.");
        table.ContextMenu!.Close();
        ResourceId[] archiveIds = table.SelectedItems.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id).ToArray();
        Texts.SetLanguage("de"); await Task.Delay(80);
        Require(table.SelectedItems.Count == 4, "Archive language refresh collapsed the set.");
        Click(page.FindControl<Button>("ArchiveThreadMode")!);
        ListBox cards = page.FindControl<ListBox>("ArchiveThreads")!;
        Require(cards.SelectedItems!.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id).ToHashSet().SetEquals(archiveIds), "Table-to-card selection lost resource IDs.");
        await Program.WaitUntilAsync(() => cards.GetVisualDescendants().OfType<ListBoxItem>().Count(item => item.IsEffectivelyVisible) >= 3);
        ListBoxItem[] cardRows = cards.GetVisualDescendants().OfType<ListBoxItem>().Where(item => item.IsEffectivelyVisible).Take(3).ToArray();
        Press(window, cardRows[0]); Press(window, cardRows[2], KeyModifiers.Control);
        Require(cards.SelectedItems!.Count == 2, "Archive cards Ctrl-click did not preserve native multiple selection.");
        Press(window, cardRows[0]); Press(window, cardRows[2], KeyModifiers.Shift);
        Require(cards.SelectedItems!.Count == 3 && table.SelectedItems.Count == 3, "Archive card Shift range or table synchronization failed.");
        Click(page.FindControl<Button>("ArchiveThreadMode")!);
        PressKey(table, Key.A, KeyModifiers.Control);
        Require(table.SelectedItems.Count == 50, "Archive Select All escaped the current page.");
        Click(page.FindControl<Button>("NextThreadsButton")!);
        await Program.WaitUntilAsync(() => !page.IsBusy && table.ItemsSource.Cast<object>().Count() == 2);
        Require(table.SelectedItems.Count == 0 && cards.SelectedItems!.Count == 0, "Archive retained invisible IDs across paging.");
        PressKey(table, Key.Delete);
        Require(window.UrlList.Count == 0, "Archive Delete invented a watch target.");
        Texts.SetLanguage("en");
        Program.CaptureWindow(window, "smoke-selection-archive.png");
        Console.WriteLine("SMOKE selection archive: native range/right-click, stable EN/DE IDs, table/cards preservation, page-boundary pruning and absent-watch Delete guard passed.");
    }

    private static async Task CheckHeader(MainWindow window, string image)
    {
        await Task.Delay(80); window.UpdateLayout();
        Image logo = window.FindControl<Image>("WorkspaceLogo")!;
        Require(logo.Source != null && logo.IsEffectivelyVisible && logo.Bounds.Width == 30 && logo.Bounds.Height == 30,
            "The compact header lost its cat asset.");
        foreach (Control item in new Control[] { logo, window.FindControl<TextBlock>("WorkspaceVersion")!, window.FindControl<ComboBox>("WorkspaceModeComboBox")!, window.FindControl<MenuItem>("WorkspaceMenuItem")! })
        {
            Point? position = item.TranslatePoint(default, window);
            Require(position.HasValue && position.Value.X >= 0 && position.Value.X + item.Bounds.Width <= window.Bounds.Width + 1,
                "A narrow header/footer item was clipped: " + item.Name);
        }
        Program.CaptureWindow(window, image);
    }
    private static Task WaitRow(DataGrid grid, object item) => Program.WaitUntilAsync(() => grid.GetVisualDescendants().OfType<DataGridRow>().Any(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, item)));
    private static DataGridRow Row(DataGrid grid, object item) => grid.GetVisualDescendants().OfType<DataGridRow>().First(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, item));
    private static void Press(MainWindow window, Control row, KeyModifiers modifiers = KeyModifiers.None, bool right = false)
    {
        Control target = row is DataGridRow ? row.GetVisualDescendants().OfType<DataGridCell>().First(cell => cell.Bounds.Width > 10) : row;
        Point point = target.TranslatePoint(new Point(Math.Min(20, target.Bounds.Width / 2), target.Bounds.Height / 2), window)!.Value;
        PressAt(window, point, modifiers, right);
    }
    private static void PressAt(MainWindow window, Point point, KeyModifiers modifiers, bool right)
    {
        InputElement hit = window.InputHitTest(point) as InputElement ?? throw new InvalidOperationException("No native pointer target.");
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, point, 0,
            new PointerPointProperties(right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
                right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed), modifiers));
        hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, point, 1,
            new PointerPointProperties(RawInputModifiers.None, right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased), modifiers,
            right ? MouseButton.Right : MouseButton.Left));
        pointer.Capture(null);
    }
    private static void PressKey(Control control, Key key, KeyModifiers modifiers = KeyModifiers.None)
    { control.Focus(); control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers }); }
    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
