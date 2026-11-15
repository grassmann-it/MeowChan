using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// The media panel of the Media page (smoke id media-page, after the page journey; its own profile with the three saved files of SeedAsync).
// The panel is on by default and stands right of the grid on a wide page. It follows the grid's selection: a real click, a programmatic
// selection and its own "next" arrow each make it show the selected tile, with the header naming that medium (post number, file name) and the
// counter "n / total" over the grid's results; a single click only selects (the gallery is not opened), "Open in gallery" opens it at the
// panel's medium. Compile-checked only when written (no GUI smokes at that time); the central run decides.
internal static partial class MediaPageChecks
{
    private static async Task PanelFollowsSelectionAsync(MainWindow owner, string profile)
    {
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        List<(IReadOnlyList<MediaLibraryEntry> Window, int Start)> opened = new();
        MediaPage page = new(host.OpenArchiveReaderAsync)
        {
            ReadUserData = () => UserData(host),
            OpenGallery = (window, start) => { opened.Add((window, start)); return Task.CompletedTask; }
        };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media panel", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.ShownEntries.Count == 3, "The panel fixture must show the three saved files: " + page.ShownEntries.Count);
            Require(page.PageLayout.Panel, "The media panel must be on in the standard layout.");

            // On a wide page the panel is on screen, right of the grid, and shows nothing until a tile is selected.
            await PanelUntilAsync(() => page.IsPanelShown && page.Panel is { IsEffectivelyVisible: true } && page.Panel.Bounds.Width > 0,
                () => "the panel never appeared on a 1400 dip page: shown " + page.IsPanelShown + ", collapsed " + page.IsPanelCollapsedByWidth);
            ReaderMediaSidebar panel = page.Panel!;
            Control surface = C<Control>(page, "MediaGridSurface");
            double gridRight = surface.TranslatePoint(new Point(surface.Bounds.Width, 0), page)!.Value.X, panelLeft = panel.TranslatePoint(default, page)!.Value.X;
            Require(panelLeft >= gridRight - 0.5 && page.PanelHandle.IsEffectivelyVisible,
                "The panel must stand right of the grid with its drag handle: panel at " + panelLeft + ", grid ends at " + gridRight);
            Require(page.PanelRow == null && panel.HeaderLine == Texts.T("media.panel.title") && panel.CounterLine.Length == 0,
                "Without a selection the panel must show nothing: row " + page.PanelRow?.Name + ", header '" + panel.HeaderLine + "', counter '" + panel.CounterLine + "'");

            MediaLibraryTileRow[] rows = (MediaLibraryTileRow[])page.Grid.ItemsSource!;
            // By thread: B's post file (200), A's post file (100), A's file without a post.
            Require(rows.Length == 3 && rows[0].Entry.PostId == "200" && rows[1].Entry.PostId == "100" && rows[2].Entry.PostId == null,
                "The panel fixture's order is wrong: " + string.Join(",", rows.Select(row => row.Name + ":" + row.Entry.PostId)));

            // The panel shows exactly this row: its header names the medium, the counter counts the grid's results, the picture is named after it.
            async Task<Bitmap?> ShowsAsync(int index, string how, bool picture)
            {
                MediaLibraryTileRow row = rows[index];
                await PanelUntilAsync(() => page.PanelRow?.Entry == row.Entry && page.PanelTask.IsCompleted && (!picture || panel.Shown != null),
                    () => how + ": the panel did not follow to " + row.Name + " (shows " + page.PanelRow?.Name + ", load done " + page.PanelTask.IsCompleted
                        + ", picture " + (panel.Shown != null) + ", status '" + panel.StatusText + "')");
                string header = panel.HeaderLine;
                Require(header.Contains(row.Name, StringComparison.Ordinal) && header != Texts.T("media.panel.title"),
                    how + ": the header must name " + row.Name + ": '" + header + "'");
                foreach (MediaLibraryTileRow other in rows.Where(other => other != row))
                    Require(!header.Contains(other.Name, StringComparison.Ordinal), how + ": the header still names " + other.Name + ": '" + header + "'");
                Require(row.Entry.PostId is { } post ? header.Contains(Texts.T("reader.postNumber", post), StringComparison.Ordinal)
                        : rows.All(other => other.Entry.PostId is not { } otherPost || !header.Contains(Texts.T("reader.postNumber", otherPost), StringComparison.Ordinal)),
                    how + ": the header's post line does not match " + row.Name + " (post " + (row.Entry.PostId ?? "none") + "): '" + header + "'");
                Require(panel.CounterLine == (index + 1) + " / " + rows.Length, how + ": the counter must be " + (index + 1) + " / " + rows.Length + ": '" + panel.CounterLine + "'");
                Require(AutomationProperties.GetName(panel.Picture) == row.Name, how + ": the picture must be named after " + row.Name + ": " + AutomationProperties.GetName(panel.Picture));
                Require(panel.Previous.IsEnabled == (index > 0) && panel.Next.IsEnabled == (index < rows.Length - 1),
                    how + ": the arrows must stop at the ends: previous " + panel.Previous.IsEnabled + ", next " + panel.Next.IsEnabled + " at " + index);
                return panel.Shown;
            }

            // 1. A real click on the first tile only selects it (the gallery stays closed); the panel shows it.
            await ClickTileAsync(page, window, 0);
            Require(page.Grid.SelectedIndex == 0 && opened.Count == 0, "A single click must select the tile and not open the gallery: " + page.Grid.SelectedIndex + "/" + opened.Count);
            Bitmap? first = await ShowsAsync(0, "click", picture: true);

            // 2. Another selection (the grid's own selection, as an arrow key makes it): the content changes with it.
            page.Grid.SelectedIndex = 1;
            Bitmap? second = await ShowsAsync(1, "selection", picture: true);
            Require(!ReferenceEquals(first, second), "The panel's picture must change with the selected tile.");

            // 3. The panel's own "next" arrow selects the next tile in the grid, and the panel follows to it (the file without a post: no post line).
            panel.Next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(page.Grid.SelectedIndex == 2, "The panel's next arrow must select the next tile: " + page.Grid.SelectedIndex);
            await ShowsAsync(2, "next arrow", picture: false);

            // 4. Back to the start by selection, then "Open in gallery" opens the gallery at the panel's medium.
            page.Grid.SelectedIndex = 0;
            await ShowsAsync(0, "back", picture: true);
            panel.Open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PanelUntilAsync(() => opened.Count == 1, () => "'Open in gallery' did not open the gallery");
            Require(opened[0].Window[opened[0].Start] == rows[0].Entry, "'Open in gallery' must start at the panel's medium: " + opened[0].Window[opened[0].Start].Name);
            Program.CaptureWindow(window, "smoke-media-panel.png");
            Console.WriteLine("SMOKE media panel [en]: on by default right of the grid with its handle; empty without a selection; follows a click (which only selects), the grid's selection and its own next arrow; header names the selected medium (post number, file name), counter n / 3, picture named after it and changed with the tile; arrows stop at the ends; Open in gallery starts at the panel's medium.");
        }
        finally { page.EndLifetime(); window.Close(); }
    }

    private static async Task PanelUntilAsync(Func<bool> condition, Func<string> describe)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Media panel: " + describe());
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
