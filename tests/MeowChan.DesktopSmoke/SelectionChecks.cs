using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class SelectionChecks
{
    private static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    internal static async Task RunAsync(MainWindow window)
    {
        Require(window.SessionHost.IsPaused, "Selection fixture must never start network work.");
        window.Width = 1280; window.Height = 900;
        for (int i = 1000; i < 1005; i++) await window.SessionHost.AddAsync("https://boards.4chan.org/g/thread/" + i);
        await Program.WaitUntilAsync(() => window.UrlList.Count == 5);
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        ThreadRow[] rows = window.UrlList.OrderBy(row => row.ThreadId).ToArray();
        await WaitRow(grid, rows[4]);
        await PressAsync(window, Row(grid, rows[0]));
        await PressAsync(window, Row(grid, rows[2]), CommandModifier);
        Require(grid.SelectedItems.Count == 2, "Command-click did not extend the native watch selection.");
        await PressAsync(window, Row(grid, rows[2]), right: true);
        Require(grid.SelectedItems.Count == 2, "Right-click collapsed the existing multiple selection.");
        Require(grid.ContextMenu!.IsOpen, "Right-click on a selected watch did not open its context menu.");
        MenuItem read = grid.ContextMenu.Items.OfType<MenuItem>()
            .Single(item => item.Tag as string == "thread.read");
        Require(!read.IsEnabled, "Read posts must be disabled for multiple selected watches.");
        grid.ContextMenu.Close();
        await CheckReadHelpAsync(window, grid, expected: false);
        PressKey(grid, Key.Enter);
        await Task.Delay(80);
        Require(!window.FindControl<ContentControl>("ReaderHost")!.IsVisible, "Enter opened an ambiguous multiple-watch reader.");
        grid.ContextMenu!.Close();
        await Task.Delay(80);
        await CheckKeyboardContextAsync(grid, grid.ContextMenu);
        await PressAsync(window, Row(grid, rows[4]), right: true);
        Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItem, rows[4]), "Right-click unselected watch did not replace selection.");
        Require(grid.ContextMenu.IsOpen, "Right-click on an unselected watch did not open its context menu.");
        grid.ContextMenu.Close();
        await CheckReadHelpAsync(window, grid, expected: true);
        await Task.Delay(80);
        Point blank = grid.TranslatePoint(new Point(20, grid.Bounds.Height - 24), window)!.Value;
        PressAt(window, blank, KeyModifiers.None, right: true);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(grid.SelectedItems.Count == 0, "Blank-space right-click retained stale watch targets.");
        Require(!grid.ContextMenu.IsOpen, "Blank-space right-click reopened an empty watch context menu.");
        await PressAsync(window, Row(grid, rows[0]));
        await PressAsync(window, Row(grid, rows[3]), KeyModifiers.Shift);
        Require(grid.SelectedItems.Count == 4, "Shift-click did not select a contiguous native row range.");
        await PressAsync(window, Row(grid, rows[0]));
        PressKey(grid, Key.Down, KeyModifiers.Shift);
        Require(grid.SelectedItems.Count == 2, "Native Shift+Down did not extend a watch range.");
        PressKey(grid, Key.Down);
        Require(grid.SelectedItems.Count == 1, "Native Down did not move to one row.");
        ThreadRow selectedBeforeFocusMove = grid.SelectedItems.Cast<ThreadRow>().Single();
        ThreadRow[] currentOrder = grid.CollectionView.Cast<ThreadRow>().ToArray();
        ThreadRow focusedAfterMove = currentOrder[Array.IndexOf(currentOrder, selectedBeforeFocusMove) + 1];
        await PressAsync(window, Row(grid, focusedAfterMove), CommandModifier);
        await PressAsync(window, Row(grid, focusedAfterMove), CommandModifier);
        await Task.Delay(50);
        PressKey(grid, Key.Enter);
        await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("ReaderHost")!.IsVisible
            && !window.ArchivePage.IsBusy && window.ArchivePage.CurrentThread?.Summary.Id == selectedBeforeFocusMove.Snapshot.Id);
        Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItems[0], selectedBeforeFocusMove),
            "Command-toggle/Enter did not retain and open the one selected watch.");
        Click(window.ArchivePage.FindControl<Button>("ArchiveCloseReader")!);
        await Program.WaitUntilAsync(() => !window.FindControl<ContentControl>("ReaderHost")!.IsVisible);
        PressKey(grid, Key.A, CommandModifier);
        Require(grid.SelectedItems.Count == 5, "List Select All did not select all watches.");
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
        PressKey(input, Key.Delete); PressKey(input, Key.E, CommandModifier); PressKey(input, Key.A, CommandModifier);
        Require(window.UrlList.Count == 5 && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
            "Text input shortcut escaped to a list command.");
        PressKey(grid, Key.E, CommandModifier);
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

        // Enter has no unambiguous reader target while multiple watches are selected.
        PressKey(grid, Key.Enter);
        await Task.Delay(80);
        Require(!window.FindControl<ContentControl>("ReaderHost")!.IsVisible
            && grid.SelectedItems.Cast<ThreadRow>().Select(row => row.WatchId).Order().SequenceEqual(selectedIds),
            "Enter opened an ambiguous multiple-watch reader or changed the selection.");
        PressKey(grid, Key.Delete);
        await Program.WaitUntilAsync(() => window.UrlList.Count == 0);
        await using (ArchiveReader reader = await window.SessionHost.OpenArchiveReaderAsync(CancellationToken.None))
            Require((await reader.SelectThreadIdsAsync(new ArchiveThreadQuery(), CancellationToken.None)).Count == 52,
                "Removing watches deleted archived posts.");
        Console.WriteLine("SMOKE selection watches: native Ctrl/Shift range+arrow, selected/unselected right-click, EN/DE/modes, narrow logo, input shielding, frozen batch-export return, single-watch Enter, ambiguous multi-watch Enter blocked and non-destructive batch Delete passed.");

        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<DataGrid>("ArchiveThreadTable")!.ItemsSource.Cast<object>().Count() == 50);
        Click(page.FindControl<Button>("ArchiveLayoutTable")!);
        DataGrid table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
        ArchiveThreadRow[] archived = table.ItemsSource.Cast<ArchiveThreadRow>().ToArray();
        await ArchiveCommandChecks.RunAsync(page, table);
        archived = table.ItemsSource.Cast<ArchiveThreadRow>().ToArray();
        await WaitRow(table, archived[3]);
        await Task.Delay(80);
        await PressAsync(window, Row(table, archived[0]));
        await Task.Delay(80);
        Require(table.IsEffectivelyVisible && !page.FindControl<Grid>("ArchiveReaderPane")!.IsEffectivelyVisible,
            "Selecting a narrow archive table row hid the list before an explicit reader action.");
        await PressAsync(window, Row(table, archived[3]), KeyModifiers.Shift);
        Require(table.IsEffectivelyVisible && !page.FindControl<Grid>("ArchiveReaderPane")!.IsEffectivelyVisible,
            "Selecting a narrow archive table range hid the list before an explicit reader action.");
        Program.CaptureWindow(window, "smoke-selection-archive-range.png");
        Require(table.SelectedItems.Count == 4, "Archive native Shift range did not work.");
        await PressAsync(window, Row(table, archived[3]), right: true);
        Require(table.SelectedItems.Count == 4, "Archive selected right-click collapsed the set.");
        Require(table.ContextMenu!.IsOpen, "Archive table right-click did not open its context menu.");
        table.ContextMenu!.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await CheckKeyboardContextAsync(table, table.ContextMenu);
        ResourceId[] archiveIds = table.SelectedItems.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id).ToArray();
        Texts.SetLanguage("de"); await Task.Delay(80);
        Require(table.SelectedItems.Count == 4, "Archive language refresh collapsed the set.");
        Click(page.FindControl<Button>("ArchiveLayoutList")!);
        ListBox cards = page.FindControl<ListBox>("ArchiveThreads")!;
        Require(cards.SelectedItems!.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id).ToHashSet().SetEquals(archiveIds), "Table-to-card selection lost resource IDs.");
        ResourceId firstCardId = archived[0].Summary.Id;
        ResourceId thirdCardId = archived[2].Summary.Id;
        string CardSelection() => string.Join(",", cards.SelectedItems!.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id.Thread));
        await PressAsync(window, await CardForAsync(window, cards, firstCardId));
        await PressAsync(window, await CardForAsync(window, cards, thirdCardId), CommandModifier);
        if (cards.SelectedItems!.Count != 2)
        {
            Program.CaptureWindow(window, "smoke-selection-archive-command-click-failed.png");
            throw new InvalidOperationException("Archive cards Command-click did not preserve native multiple selection: count=" +
                cards.SelectedItems.Count + "; selected=" + CardSelection() + "; row contexts=" +
                firstCardId.Thread + "," + thirdCardId.Thread);
        }
        ListBoxItem thirdCard = await CardForAsync(window, cards, thirdCardId);
        await PressAsync(window, thirdCard, right: true);
        ContextMenu cardMenu = thirdCard.GetVisualDescendants().OfType<Control>()
            .Select(control => control.ContextMenu).First(menu => menu != null)!;
        Require(cards.SelectedItems.Count == 2 && cardMenu.IsOpen,
            "Archive card right-click changed the selected set or failed to open its attached menu.");
        cardMenu.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await PressAsync(window, await CardForAsync(window, cards, firstCardId));
        await PressAsync(window, await CardForAsync(window, cards, thirdCardId), KeyModifiers.Shift);
        Require(cards.SelectedItems!.Count == 3 && table.SelectedItems.Count == 3, "Archive card Shift range or table synchronization failed.");
        Click(page.FindControl<Button>("ArchiveLayoutTable")!);
        PressKey(table, Key.A, CommandModifier);
        Require(table.SelectedItems.Count == 50, "Archive Select All escaped the current page.");
        Button nextPage = page.FindControl<Button>("NextThreadsButton")!;
        await Program.WaitUntilAsync(() => !page.IsBusy && nextPage.IsEffectivelyEnabled);
        Click(nextPage);
        await Program.WaitUntilAsync(() => !page.IsBusy && table.ItemsSource.Cast<object>().Count() == 2);
        Require(table.SelectedItems.Count == 0 && cards.SelectedItems!.Count == 0, "Archive retained invisible IDs across paging.");
        PressKey(table, Key.Delete);
        Require(window.UrlList.Count == 0, "Archive Delete invented a watch target.");
        Texts.SetLanguage("en");
        Program.CaptureWindow(window, "smoke-selection-archive.png");
        await ArchiveDeletionChecks.RunAsync(window, table);
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
    internal static Task WaitRow(DataGrid grid, object item) => Program.WaitUntilAsync(() => grid.GetVisualDescendants().OfType<DataGridRow>().Any(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, item)));
    internal static DataGridRow Row(DataGrid grid, object item) => grid.GetVisualDescendants().OfType<DataGridRow>().First(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, item));
    private static bool CardPointerReady(MainWindow window, ListBoxItem row)
    {
        if (row.Bounds.Width <= 0 || row.Bounds.Height <= 0) return false;
        Point? point = row.TranslatePoint(CardSelectionPoint(row), window);
        return point.HasValue && window.InputHitTest(point.Value) is Visual hit
            && hit.GetSelfAndVisualAncestors().Contains(row)
            && !hit.GetSelfAndVisualAncestors().OfType<Button>().Any();
    }
    internal static async Task<ListBoxItem> CardForAsync(MainWindow window, ListBox cards, ResourceId id)
    {
        await Program.WaitUntilAsync(() => cards.IsEffectivelyVisible && cards.Bounds.Height > 0);
        ArchiveThreadRow item = cards.ItemsSource!.Cast<ArchiveThreadRow>().Single(row => row.Summary.Id == id);
        ListBoxItem? container = null;
        await Program.WaitUntilAsync(() =>
        {
            // A small viewport may virtualize one of the range endpoints.
            // Bring this exact item into view before injecting its native click.
            cards.ScrollIntoView(item);
            container = cards.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(row =>
                row.IsEffectivelyVisible && (row.DataContext as ArchiveThreadRow)?.Summary.Id == id
                && CardPointerReady(window, row));
            return container != null;
        });
        return container!;
    }
    private static Point CardSelectionPoint(ListBoxItem row) => new(
        Math.Min(Math.Max(112, row.Bounds.Width / 2), Math.Max(8, row.Bounds.Width - 8)), row.Bounds.Height / 2);
    private static async Task CheckKeyboardContextAsync(Control list, ContextMenu menu)
    {
        // Avalonia translates ContextMenu/Shift+F10 into a positionless request.
        // Inject that native routed request, rather than bypassing it with menu.Open().
        list.Focus();
        list.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await Program.WaitUntilAsync(() => menu.IsOpen);
        menu.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    internal static async Task PressAsync(MainWindow window, Control row, KeyModifiers modifiers = KeyModifiers.None, bool right = false)
    {
        Control target = row is DataGridRow ? row.GetVisualDescendants().OfType<DataGridCell>().First(cell => cell.Bounds.Width > 10) : row;
        Point point = default;
        try
        {
            await Program.WaitUntilAsync(() =>
            {
                if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0) return false;
                Point? translated = target.TranslatePoint(target is ListBoxItem card ? CardSelectionPoint(card)
                    : new Point(Math.Min(20, target.Bounds.Width / 2), target.Bounds.Height / 2), window);
                if (!translated.HasValue) return false;
                point = translated.Value;
                return window.InputHitTest(point) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(row);
            });
        }
        catch
        {
            Console.WriteLine($"SMOKE selection pointer blocked: row={row.GetType().Name}, point={point}, hit={window.InputHitTest(point)?.GetType().Name}; open context owners="
                + string.Join(",", window.GetVisualDescendants().OfType<Control>().Where(control => control.ContextMenu?.IsOpen == true).Select(control => control.Name ?? control.GetType().Name))
                + "; open submenus=" + string.Join(",", window.GetVisualDescendants().OfType<MenuItem>().Where(item => item.IsSubMenuOpen).Select(item => item.Name)));
            Program.CaptureWindow(window, "smoke-selection-pointer-blocked.png");
            throw;
        }
        PressAt(window, point, modifiers, right);
        // Complete queued native context-menu processing before the fixture closes
        // a menu or injects the next pointer action.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
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
    private static async Task CheckReadHelpAsync(MainWindow window, DataGrid grid, bool expected)
    {
        PressKey(grid, Key.F1);
        Border help = window.FindControl<Border>("ShellHelpOverlay")!;
        await Program.WaitUntilAsync(() => help.IsEffectivelyVisible);
        string text = window.FindControl<TextBlock>("ShellHelpText")!.Text ?? string.Empty;
        Require(text.Contains(CommandCatalog.Label("thread.read"), StringComparison.Ordinal) == expected,
            "Shortcut help advertised Read posts for an unavailable selection or hid it for one thread.");
        Click(window.FindControl<Button>("ShellHelpClose")!);
        await Program.WaitUntilAsync(() => !help.IsVisible);
    }

    private static void Click(Control control) => SmokeActionClick.Execute(control);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
