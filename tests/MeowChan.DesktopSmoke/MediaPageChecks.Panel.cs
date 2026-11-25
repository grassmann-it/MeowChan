using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// The media panel of the Media page (smoke id media-page, after the page journey; its own profile with the three saved files of SeedAsync).
// The panel is on by default and stands right of the grid on a wide page. It follows the grid's selection: a real click, a programmatic
// selection each make it show the selected tile, with the header naming that medium (post number, file name) and the position
// at the start of the file line. Filtering updates the position of a retained selection. A single click selects the tile,
// and "Open in gallery" opens the gallery at the panel's medium.
internal static partial class MediaPageChecks
{
    internal static async Task RunPanelSelectionAsync(MainWindow owner)
    {
        string language = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            string profile = TemporaryWorkspacePaths.CreateDirectory("media-page-panel-");
            new Config().SaveConfig(profile);
            await SeedAsync(profile);
            await PanelFollowsSelectionAsync(owner, profile);
        }
        finally { Texts.SetLanguage(language); }
    }

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
            Require(page.PanelRow == null && panel.HeaderLine == Texts.T("media.panel.title"),
                "Without a selection the panel must show nothing: row " + page.PanelRow?.Name + ", header '" + panel.HeaderLine + "'");

            MediaLibraryTileRow[] rows = (MediaLibraryTileRow[])page.Grid.ItemsSource!;
            // By thread: B's post file (200), A's post file (100), A's file without a post.
            Require(rows.Length == 3 && rows[0].Entry.PostId == "200" && rows[1].Entry.PostId == "100" && rows[2].Entry.PostId == null,
                "The panel fixture's order is wrong: " + string.Join(",", rows.Select(row => row.Name + ":" + row.Entry.PostId)));
            T FindPanel<T>(string id) where T : Control => panel.GetVisualDescendants().OfType<T>()
                .Single(control => AutomationProperties.GetAutomationId(control) == id);
            Control headerRow = FindPanel<Control>("ReaderMediaSidebarHeaderRow");
            TextBlock title = FindPanel<TextBlock>("ReaderMediaSidebarHeading");
            TextBlock position = FindPanel<TextBlock>("ReaderMediaSidebarPosition");
            TextBlock file = FindPanel<TextBlock>("ReaderMediaSidebarMeta");
            Rect InHeader(Control control) => new(control.TranslatePoint(default, headerRow)!.Value, control.Bounds.Size);

            // W112-72, Fable variant A §2.1: “Position vorn in der Dateizeile, keine Pill” (e.g. “20 / 36”).
            async Task CheckPositionAsync(int number, int count, string how)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                string expected = $"{number} / {count}";
                Require(position.IsEffectivelyVisible && position.Bounds.Width > 0 && position.Text == expected,
                    $"{how}: expected visible position '{expected}', got '{position.Text}' (visible: {position.IsEffectivelyVisible}, width: {position.Bounds.Width}).");
                Require(!panel.PositionPill.IsEffectivelyVisible, how + ": the old position pill is visible.");
                Rect counter = InHeader(position), filename = InHeader(file);
                Require(file.IsEffectivelyVisible && filename.Width > 0 && Math.Abs(counter.Top - filename.Top) <= 1
                    && counter.Right <= filename.Left + 1,
                    $"{how}: the position must precede the filename on the same line (position: {counter}, filename: {filename}).");
            }

            // The panel shows exactly this row: its header names the medium, the picture is named after it.
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
                await CheckPositionAsync(index + 1, rows.Length, how);
                // Navigation stays in the grid, with the position in the header.
                string[] overlay = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(panel).OfType<Control>().Select(control => control.Name ?? string.Empty)
                    .Where(name => name is "ReaderMediaSidebarPrevious" or "ReaderMediaSidebarNext" or "ReaderMediaSidebarCounter" or "ReaderMediaSidebarCounterText").ToArray();
                Require(overlay.Length == 0, how + ": the panel must have no overlay arrows or counter: " + string.Join(",", overlay));
                Require(AutomationProperties.GetName(panel.Picture) == row.Name, how + ": the picture must be named after " + row.Name + ": " + AutomationProperties.GetName(panel.Picture));
                return panel.Shown;
            }

            // 1. A real click on the first tile only selects it (the gallery stays closed); the panel shows it.
            await ClickTileAsync(page, window, 0);
            Require(page.Grid.SelectedIndex == 0 && opened.Count == 0, "A single click must select the tile and not open the gallery: " + page.Grid.SelectedIndex + "/" + opened.Count);
            Bitmap? first = await ShowsAsync(0, "click", picture: true);
            // UX-LEDGER-27 / 1.9e supersedes alignment to the neighbouring grid card:
            // the panel/divider fill their host, with title/actions inside the panel's top line.
            Control panelHost = (Control)panel.Parent!;
            await PanelUntilAsync(() => Math.Abs(panel.Bounds.Height - panelHost.Bounds.Height) <= 1,
                () => "the media sidebar does not fill its host: " + panel.Bounds.Height + "/" + panelHost.Bounds.Height);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            double TopOf(Control control) => control.TranslatePoint(default, page)!.Value.Y;
            double panelTop = TopOf(panel), hostTop = TopOf(panelHost);
            Require(Math.Abs(panelTop - hostTop) <= 1 && Math.Abs(panel.Bounds.Bottom - panelHost.Bounds.Height) <= 1,
                "The media sidebar divider must reach both host edges.");
            // W112-72 §2.1: 4 DIP of padding inside the 8 DIP inset, with 32 DIP for actions and 16 DIP for a second line.
            Rect titleBounds = InHeader(title), fileBounds = InHeader(file);
            bool twoRows = fileBounds.Top >= titleBounds.Bottom - 1;
            double headerInset = TopOf(headerRow) - panelTop;
            Require(!page.IsPanelHeaderLifted && Math.Abs(headerInset - 12) <= 1
                    && Math.Abs(headerRow.Bounds.Height - (twoRows ? 48 : 32)) <= 1,
                $"The title/actions line must be inset inside the full-height sidebar (inset: {headerInset}, height: {headerRow.Bounds.Height}, two rows: {twoRows}).");
            Rect counterBounds = InHeader(position), closeBounds = InHeader(FindPanel<Button>("ReaderMediaSidebarClose"));
            Rect favoriteBounds = InHeader(FindPanel<Button>("ReaderMediaSidebarFavorite"));
            Require(Math.Abs(titleBounds.Left) <= 1 && Math.Abs(closeBounds.Right - headerRow.Bounds.Width) <= 1
                && (twoRows ? Math.Abs(counterBounds.Left) <= 1
                    : titleBounds.Right <= counterBounds.Left + 1 && fileBounds.Right <= favoriteBounds.Left + 1),
                $"The post number, file line and actions are misaligned (title: {titleBounds}, position: {counterBounds}, file: {fileBounds}, favorite: {favoriteBounds}, close: {closeBounds}).");
            Require(Math.Abs(TopOf(panel.Card) - panelTop - 24 - headerRow.Bounds.Height) <= 1,
                "The card must begin below the Media header.");
            Control last = panel.Details.IsVisible ? panel.Details : panel.Card;
            Require(TopOf(last) + last.Bounds.Height <= hostTop + panelHost.Bounds.Height - 7,
                "The media card and details must consume the host height below the title line.");
            // A fit change refreshes the current medium without announcing it again.
            TextBlock live = panel.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "ReaderMediaSidebarLive");
            await PanelUntilAsync(() => live.Text?.Contains(rows[0].Name, StringComparison.Ordinal) == true,
                () => "the initial medium was not announced");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                live.Text = null;
                page.ConfigurePanel(ReaderInlineVideoOptions.From(host.Config), new GalleryFitPreferences(Limit: true));
            });
            await Task.Delay(2 * ReaderMediaSidebarRules.AnnounceDelayMs);
            await Dispatcher.UIThread.InvokeAsync(() => Require(live.Text is null,
                "The panel announced the same medium after a fit change."), DispatcherPriority.Background);

            // 2. Another selection (the grid's own selection, as an arrow key makes it): the content changes with it.
            await Dispatcher.UIThread.InvokeAsync(() => page.Grid.SelectedIndex = 1);
            Bitmap? second = await ShowsAsync(1, "selection", picture: true);
            await PanelUntilAsync(() => live.Text?.Contains(rows[1].Name, StringComparison.Ordinal) == true,
                () => "the new medium was not announced: " + live.Text);
            Console.WriteLine("SMOKE media panel announcements: the fit refresh stayed silent and the next medium was announced.");
            Require(!ReferenceEquals(first, second), "The panel's picture must change with the selected tile.");

            // 3. The grid's own selection moves on (as an arrow key in the list does), and the panel follows (the file without a post: no post line).
            page.Grid.SelectedIndex = 2;
            await ShowsAsync(2, "grid selection", picture: false);

            // Filename filtering keeps the selected file and renumbers it in the visible results.
            page.NameFilter.Text = rows[2].Name;
            await PanelUntilAsync(() => page.ShownEntries.Count == 1 && page.ShownEntries[0] == rows[2].Entry
                    && page.Grid.SelectedItem is MediaLibraryTileRow selected && selected.Entry == rows[2].Entry
                    && page.PanelRow?.Entry == rows[2].Entry,
                () => "filtering did not retain the selected file as the sole result: " + page.PanelRow?.Name);
            await CheckPositionAsync(1, 1, "filtered");
            page.NameFilter.Text = string.Empty;
            await PanelUntilAsync(() => page.ShownEntries.Count == rows.Length && page.Grid.SelectedIndex == 2
                    && page.PanelRow?.Entry == rows[2].Entry,
                () => "clearing the filter did not restore the results with the same file selected: " + page.PanelRow?.Name);
            await CheckPositionAsync(3, 3, "filter cleared");
            Console.WriteLine("SMOKE media panel position: the visible file line followed clicks, grid selection and filename filtering.");

            // 4. Back to the start by selection, then "Open in gallery" opens the gallery at the panel's medium.
            page.Grid.SelectedIndex = 0;
            await ShowsAsync(0, "back", picture: true);
            page.SetBooruMode(true, save: false);
            TextBlock tagsTitle = C<TextBlock>(page.TagSidebar, "MediaTagSidebarTitle");
            double CentreY(Control control) => TopOf(control) + control.Bounds.Height / 2;
            await PanelUntilAsync(() => tagsTitle.IsEffectivelyVisible && tagsTitle.Bounds.Height > 0
                    && Math.Abs(CentreY(tagsTitle) - CentreY(title)) <= 2,
                () => $"Tags and Media header centres: {CentreY(tagsTitle)} and {CentreY(title)}, tags visible: {tagsTitle.IsEffectivelyVisible}.");
            page.SetBooruMode(false, save: false);

            panel.Open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PanelUntilAsync(() => opened.Count == 1, () => "'Open in gallery' did not open the gallery");
            Require(opened[0].Window[opened[0].Start] == rows[0].Entry, "'Open in gallery' must start at the panel's medium: " + opened[0].Window[opened[0].Start].Name);
            Program.CaptureWindow(window, "smoke-media-panel.png");
            VerifyPanelKeys(window, page, panel, rows[0]);
            Texts.SetLanguage("de");
            string germanTitle = Texts.T("reader.postNumber", rows[0].Entry.PostId!);
            await PanelUntilAsync(() => page.PanelRow?.Entry == rows[0].Entry && title.IsEffectivelyVisible && title.Text == germanTitle,
                () => "The header did not follow the language change: '" + title.Text + "'.");
            await CheckPositionAsync(1, 3, "German header");
            Console.WriteLine("SMOKE media panel [en]: on by default right of the grid with its handle; sidebar/divider fill the host independently of the grid, title/actions at the top inside the sidebar; empty without a selection; follows a click (which only selects) and the grid's selection; header names the selected medium (post number, file name), picture named after it and changed with the tile; no overlay arrows or counter; Open in gallery starts at the panel's medium.");
        }
        finally { page.EndLifetime(); window.Close(); }
    }

    private static void VerifyPanelKeys(Window window, MediaPage page, ReaderMediaSidebar panel, MediaLibraryTileRow row)
    {
        List<(string Command, MediaLibraryEntry Entry)> actions = new();
        page.ExecuteMediaMenuActionAsync = (id, entry, current) =>
        {
            Require(current(), "A focused panel command must have a current media page.");
            actions.Add((id, entry));
            return Task.CompletedTask;
        };
        panel.RefreshFavorite();

        void Press(Control target, string shortcut, string command)
        {
            Require(target.Focus() && ReferenceEquals(window.FocusManager?.GetFocusedElement(), target),
                "The panel control must take focus for " + shortcut);
            KeyGesture gesture = ShortcutGesture.Parse(shortcut).Resolve(EffectiveShortcutBindings.CurrentPlatform);
            KeyEventArgs key = new() { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers };
            target.RaiseEvent(key);
            Require(key.Handled && actions.Count == 1 && actions[0] == (command, row.Entry),
                "The focused panel must run " + command + " once on its displayed medium.");
            actions.Clear();
        }

        Control frame = panel.GetVisualDescendants().OfType<Control>().Single(control =>
            control.IsEffectivelyVisible && AutomationProperties.GetAutomationId(control) == "ReaderMediaSidebarFrame");
        Press(frame, "I", "media.details");
        Press(frame, "Primary+E", "media.export-original");
        Press(frame, "Primary+O", "media.show-in-folder");
        Press(frame, "Primary+Shift+C", "media.copy-path");
        Press(frame, "Shift+0", "media.rating.clear");
        for (int rating = 1; rating <= 5; rating++)
            Press(frame, "Shift+" + rating, "media.rating.set-" + rating);
        Console.WriteLine("SMOKE media panel shortcuts: the focused frame routed all four menu commands and direct ratings 0 through 5.");
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
