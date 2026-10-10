using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using oChan.Desktop;

namespace oChan.DesktopSmoke;

internal static class GridChecks
{
    public static async Task RunAsync(MainWindow window)
    {
        using ThreadRow two = CreateRow("2", 2);
        using ThreadRow ten = CreateRow("10", 10);
        using BoardRow first = new BoardRow(SnapshotFixtures.Board(102));
        using BoardRow second = new BoardRow(SnapshotFixtures.Board(103, "po"));
        window.UrlList.Add(ten);
        window.UrlList.Add(two);
        window.BoardsList.Add(first);
        window.BoardsList.Add(second);
        TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        DataGrid threadGrid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        DataGrid boardGrid = window.FindControl<DataGrid>("BoardsDataGrid")!;
        try
        {
            tabs.SelectedIndex = 0;
            await WaitForRowsAsync(threadGrid, two, ten);
            foreach (string property in new[] { nameof(ThreadRow.SourceBoard), nameof(ThreadRow.ThreadNumber), nameof(ThreadRow.Completion), nameof(ThreadRow.Queued), nameof(ThreadRow.Active) })
            {
                DataGridColumn column = threadGrid.Columns.Single(column => column.SortMemberPath == property);
                threadGrid.CollectionView.SortDescriptions.Clear();
                column.Sort(ListSortDirection.Ascending);
                await Program.WaitUntilAsync(() => ReferenceEquals(threadGrid.CollectionView.Cast<ThreadRow>().First(), two));
                column.Sort(ListSortDirection.Descending);
                await Program.WaitUntilAsync(() => ReferenceEquals(threadGrid.CollectionView.Cast<ThreadRow>().First(), ten));
            }

            Console.WriteLine("SMOKE source/board sorting distinguishes boards within one provider; numeric thread ID, progress, queued and active sorting distinguishes 2 from 10.");
            await WaitForRowsAsync(threadGrid, two, ten);
            await CheckContextSelectionAsync(window, threadGrid, two, ten);
            await WaitForRowsAsync(threadGrid, two, ten);
            DataGridColumn activeColumn = threadGrid.Columns.Single(column => column.SortMemberPath == nameof(ThreadRow.Active));
            DataGridCell activeCell = FindRow(threadGrid, ten).GetVisualDescendants().OfType<DataGridCell>()
                .Single(cell => ReferenceEquals(DataGridColumn.GetColumnContainingElement(cell), activeColumn));
            Point? activeEnd = activeCell.TranslatePoint(new Point(activeCell.Bounds.Width, 0), threadGrid);
            if (!activeCell.IsVisible || activeCell.Bounds.Width <= 0 || activeEnd == null
                || activeEnd.Value.X > threadGrid.Bounds.Width + 1)
                throw new InvalidOperationException("The default window hides the active-download column offscreen.");
            Program.CaptureWindow(window, "smoke-grid.png");

            tabs.SelectedIndex = 1;
            await WaitForRowsAsync(boardGrid, first, second);
            await CheckContextSelectionAsync(window, boardGrid, first, second);
            await ClipboardChecks.CheckCopyAsync(window, two, second);
            Console.WriteLine("SMOKE thread and board context menus select the pointed row and clear stale selection on blank space.");
        }
        finally
        {
            threadGrid.ContextMenu?.Close();
            boardGrid.ContextMenu?.Close();
            threadGrid.CollectionView.SortDescriptions.Clear();
            window.UrlList.Remove(two);
            window.UrlList.Remove(ten);
            window.BoardsList.Remove(first);
            window.BoardsList.Remove(second);
            tabs.SelectedIndex = 0;
        }
    }

    private static ThreadRow CreateRow(string id, int value)
    {
        return new ThreadRow(SnapshotFixtures.Thread(value, id, "Grid fixture " + id,
            completed: value, queued: value, active: value, board: value == 2 ? "g" : "wg"));
    }

    private static Task WaitForRowsAsync(DataGrid grid, object first, object second) => Program.WaitUntilAsync(() =>
        VisibleRows(grid, first).Count() == 1 && VisibleRows(grid, second).Count() == 1);

    private static IEnumerable<DataGridRow> VisibleRows(DataGrid grid, object item) => grid.GetVisualDescendants()
        .OfType<DataGridRow>().Where(row => row.IsEffectivelyVisible && row.Bounds.Height > 0 && ReferenceEquals(row.DataContext, item));

    private static DataGridRow FindRow(DataGrid grid, object item) => VisibleRows(grid, item).Single();

    internal static async Task OpenContextForAsync(MainWindow window, DataGrid grid, object item)
    {
        Point? position = null;
        // A tab becomes effectively visible before its retained rows have necessarily
        // finished layout. Wait for the actual input target, not just the visibility flag.
        await Program.WaitUntilAsync(() =>
        {
            DataGridRow? row = VisibleRows(grid, item).SingleOrDefault();
            position = row?.TranslatePoint(new Point(20, row.Bounds.Height / 2), window);
            if (!position.HasValue)
                return false;
            Visual? hit = window.InputHitTest(position.Value) as Visual;
            DataGridRow? hitRow = hit as DataGridRow ?? hit?.FindAncestorOfType<DataGridRow>();
            return ReferenceEquals(hitRow?.DataContext, item) && ReferenceEquals(hitRow?.FindAncestorOfType<DataGrid>(), grid);
        });
        RightPress(window, position!.Value, KeyModifiers.None);
        if (!ReferenceEquals(grid.SelectedItem, item))
            throw new InvalidOperationException("Opening the copy menu selected a different row in " + grid.Name
                + "; selected=" + Describe(grid.SelectedItem) + "; expected=" + Describe(item) + "; point=" + position);
        ContextMenu menu = grid.ContextMenu ?? throw new InvalidOperationException("The copy context menu is missing.");
        menu.Open(grid);
        await Program.WaitUntilAsync(() => menu.IsOpen);
    }

    private static string Describe(object? item) => item switch
    {
        ThreadRow thread => thread.Url,
        BoardRow board => board.Url,
        _ => item?.GetType().Name ?? "null"
    };

    private static async Task CheckContextSelectionAsync(MainWindow window, DataGrid grid, object first, object second)
    {
        DataGridRow target = FindRow(grid, second);
        Point position = target.TranslatePoint(new Point(20, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The context target is not attached to the window.");
        ContextMenu menu = grid.ContextMenu ?? throw new InvalidOperationException("The grid context menu is missing.");
        foreach (KeyModifiers modifiers in new[] { KeyModifiers.None, KeyModifiers.Control })
        {
            grid.SelectedItem = first;
            RightPress(window, position, modifiers);
            if (!ReferenceEquals(grid.SelectedItem, second))
                throw new InvalidOperationException("Right click left the previously selected row active: " + modifiers);
            menu.Open(grid);
            await Program.WaitUntilAsync(() => menu.IsOpen);
            if (!ReferenceEquals(grid.SelectedItem, second))
                throw new InvalidOperationException("Opening the context menu changed the selected row.");
            menu.Close();
        }

        Point? blank = null;
        // The last row can reach the old fixed offset on a scaled, shorter desktop.
        // Require a real empty grid target before exercising stale-selection clearing.
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            for (double y = grid.Bounds.Height - 2; y >= grid.Bounds.Height / 2; y -= 2)
            {
                Point? candidate = grid.TranslatePoint(new Point(20, y), window);
                Visual? hit = candidate.HasValue ? window.InputHitTest(candidate.Value) as Visual : null;
                DataGrid? hitGrid = hit as DataGrid ?? hit?.FindAncestorOfType<DataGrid>();
                if (!ReferenceEquals(hitGrid, grid) || hit is DataGridRow || hit?.FindAncestorOfType<DataGridRow>() != null
                    || hit is ScrollBar || hit?.FindAncestorOfType<ScrollBar>() != null
                    || hit is DataGridColumnHeader || hit?.FindAncestorOfType<DataGridColumnHeader>() != null)
                    continue;
                blank = candidate;
                return true;
            }
            return false;
        });
        RightPress(window, blank!.Value, KeyModifiers.None);
        if (grid.SelectedItem != null)
            throw new InvalidOperationException("Right click on blank space retained a stale action target.");
    }

    private static void RightPress(MainWindow window, Point position, KeyModifiers modifiers)
    {
        using Pointer pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        PointerPointProperties properties = new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        InputElement hit = window.InputHitTest(position) as InputElement
            ?? throw new InvalidOperationException("The pointer target is not hit-testable.");
        hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, position, 0, properties, modifiers));
        pointer.Capture(null);
    }
}
