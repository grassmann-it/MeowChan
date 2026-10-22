using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class DownloadSearchChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        ThreadRow[] rows = Enumerable.Range(2000, 300).Select(value => new ThreadRow(SnapshotFixtures.Thread(value,
            value.ToString(), value % 2 == 0 ? "Alpha local fixture" : "Beta local fixture", board: "local"))).ToArray();
        using BoardRow board = new(SnapshotFixtures.Board(2900, "local"));
        TextBox input = window.FindControl<TextBox>("DownloadSearchInput")!;
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        TabControl tabs = window.FindControl<TabControl>("DownloadsTabs")!;
        try
        {
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
            Require(input.Text == "" && !window.FindControl<Grid>("DownloadSearchBar")!.IsVisible, "Clearing collapsed search did not restore the compact view.");
            tabs.SelectedIndex = 0;
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
                {
                    await PresentationChecks.SwitchModeAsync(window, mode);
                    window.Width = mode == "Barebones" ? 640 : 760;
                    window.Height = mode == "Barebones" ? 480 : 500;
                    await Task.Delay(80);
                    window.UpdateLayout();
                    Control actions = window.FindControl<StackPanel>("WorkspaceListActions")!;
                    Point? actionStart = actions.TranslatePoint(default, window);
                    Point? tabEnd = tabs.TranslatePoint(new Point(tabs.Bounds.Width, 0), window);
                    Require(actionStart != null && tabEnd != null && tabEnd.Value.X <= actionStart.Value.X + 1,
                        "Download actions overlap the independently measured tabs at " + mode + "/" + language);
                    Require(window.FindControl<Button>("DetailsToggleButton")!.IsEffectivelyVisible, "Compact header hid the direct details action.");
                    Program.CaptureWindow(window, "smoke-download-header-" + mode.ToLowerInvariant() + "-" + language + ".png");
                }
            }
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
