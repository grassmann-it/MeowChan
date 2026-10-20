using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

/// <summary>Real retained desktop pages and shared archive/export services, with offline ShowcaseData.</summary>
internal static class ShowcaseCaptures
{
    internal static async Task RunAsync(MainWindow window)
    {
        Texts.SetLanguage("en");
        await PresentationChecks.SwitchModeAsync(window, "Simple");
        if (Application.Current!.RequestedThemeVariant != ThemeVariant.Dark)
            Click(window.FindControl<Button>("ThemeToggleButton")!);
        await Program.WaitUntilAsync(() => Application.Current.RequestedThemeVariant == ThemeVariant.Dark);
        await ResizeAsync(window, 1440, 920);
        await CaptureDownloadsAsync(window);

        window.FindControl<MenuItem>("ArchiveMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount > 0);
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = ShowcaseData.Search;
        await page.ApplySearchAsync();
        if (!page.FindControl<Button>("NextThreadsButton")!.IsEnabled)
            throw new InvalidOperationException("The showcase archive must have a second page of matching threads.");
        await page.LoadThreadAsync(ShowcaseData.Hero);
        await page.MediaLoadTask;
        await window.OpenGalleryAsync(ShowcaseData.Hero, origin: page.FindControl<Button>("ArchiveResultsGallery"));
        ContentControl gallery = window.FindControl<ContentControl>("GalleryHost")!;
        await Program.WaitUntilAsync(() => gallery.IsVisible && gallery.GetVisualDescendants().OfType<Image>()
            .Any(image => image.Name == "GalleryImage" && image.Source is Bitmap));
        Click(GalleryButton(gallery, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => gallery.GetVisualDescendants().OfType<Border>()
            .Any(border => border.Name == "GalleryContext" && border.IsVisible && border.Bounds.Width > 0));
        await CaptureAsync(window, "v0.9.0-gallery.png");
        Click(GalleryButton(gallery, "GalleryClose"));

        // Search remains active beside the actual saved-media tiles of its selected thread.
        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
        if (!page.FindControl<ListBox>("ArchiveMediaTiles")!.IsVisible)
            Click(page.FindControl<Button>("ArchiveMediaMode")!);
        await WaitForArchiveTilesAsync(window, page);
        await CaptureAsync(window, "v0.9.0-archive.png");

        await CaptureRepeatExportAsync(window, page);
        await ResizeAsync(window, 1440, 920);
        window.FindControl<MenuItem>("AboutMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any(about => about.IsVisible));
        AboutWindow about = window.OwnedWindows.OfType<AboutWindow>().Single();
        await Program.WaitUntilAsync(() => about.FindControl<Image>("CatLogo")!.Source != null && about.Bounds.Width > 0);
        await CaptureAsync(about, "v0.9.0-about.png");
        Click(about.FindControl<Button>("CloseButton")!);
        Console.WriteLine("SHOWCASE five actual UI scenes captured: passive download projections, gallery with persisted post context, multipage archive search and media tiles, successful repeated HTML export, About.");
    }

    private static async Task CaptureDownloadsAsync(MainWindow window)
    {
        Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 0;
        if (window.FindControl<Border>("WorkspaceInspector")!.IsVisible)
            Click(window.FindControl<Button>("DetailsToggleButton")!);
        (string Title, int Saved, int Total, ThreadActivity Activity, int Queued, int Active)[] scenes =
        [
            ("Landscape sketchbook — mountains & reflections", 8, 8, ThreadActivity.Finished, 0, 0),
            ("Coastal light and quiet harbours", 38, 64, ThreadActivity.Downloading, 24, 2),
            ("Forest colours through the seasons", 24, 24, ThreadActivity.Finished, 0, 0),
            ("Architecture — lines, light & shadow", 17, 18, ThreadActivity.Pending, 0, 0),
            ("Night skies and distant horizons", 56, 56, ThreadActivity.Finished, 0, 0),
            ("Weekend drawing studies", 0, 12, ThreadActivity.Pending, 12, 0)
        ];
        List<ThreadRow> rows = new();
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        try
        {
            for (int i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i];
                DesktopThreadEntry entry = SnapshotFixtures.Thread(980000001 + i, (900000001 + i).ToString(),
                    scene.Title, scene.Saved, scene.Total, scene.Queued, scene.Active, "wg");
                entry = entry with { Snapshot = entry.Snapshot with { Activity = scene.Activity,
                    SourceState = scene.Activity == ThreadActivity.Finished ? ThreadSourceState.Archived : ThreadSourceState.Active } };
                if (i == 3)
                {
                    DateTimeOffset attempted = new(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
                    MediaWork[] media = Enumerable.Range(0, scene.Total).Select(number => new MediaWork
                    {
                        ResourceUrl = entry.Snapshot.Url, Generation = 1,
                        MediaIdentifier = "architecture-" + (number + 1).ToString("00"),
                        DestinationPath = Path.Combine(entry.Snapshot.DownloadDirectory, "architecture-" + (number + 1).ToString("00") + ".png"),
                        DownloadUrl = "https://showcase.invalid/art/architecture-" + (number + 1).ToString("00") + ".png",
                        OriginalFileName = "architecture-" + (number + 1).ToString("00") + ".png",
                        Status = number < scene.Saved ? MediaWorkStatus.Succeeded : MediaWorkStatus.Waiting,
                        Attempts = 1, LastAttemptAt = attempted,
                        ConsecutiveNotFound = number < scene.Saved ? 0 : 1,
                        HttpStatus = number < scene.Saved ? null : 404,
                        DueAt = number < scene.Saved ? null : attempted.AddMinutes(15)
                    }).ToArray();
                    // Active source + one temporary media 404: real formatter semantics, not a stopped thread.
                    ThreadWorkSnapshot work = new(entry.Snapshot.Url, WorkIntent.Watching,
                        ThreadSourceStateValue.Active, null, false, media);
                    entry = entry with { Snapshot = entry.Snapshot with { Work = work } };
                }
                ThreadRow row = new(entry); rows.Add(row); window.UrlList.Add(row);
            }
            grid.SelectedItem = null;
            // Simple's normal list view is retained; no controls or labels are synthesized.
            await Program.WaitUntilAsync(() => grid.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.IsEffectivelyVisible && text.Text == scenes[0].Title));
            ThreadRow waiting = rows[3];
            ThreadWorkSnapshot retryWork = waiting.Snapshot.Work
                ?? throw new InvalidOperationException("Missing showcase media work projection.");
            MediaWork firstNotFound = retryWork.Media.Single(media => media.Status == MediaWorkStatus.Waiting);
            if (firstNotFound.HttpStatus != 404 || firstNotFound.Attempts != 1 || firstNotFound.ConsecutiveNotFound != 1
                || firstNotFound.LastAttemptAt == null
                || firstNotFound.DueAt != firstNotFound.LastAttemptAt.Value.AddMinutes(15))
                throw new InvalidOperationException("The first showcase media 404 must retain its real fifteen-minute retry interval.");
            if (retryWork.SavedMedia != 17 || retryWork.MissingMedia != 1
                || retryWork.NextDueAt == null || retryWork.Deadline != null
                || !waiting.Progress.Contains("1 paused (404)", StringComparison.Ordinal)
                || !waiting.ErrorDetails.Contains("Earliest retry:", StringComparison.Ordinal))
                throw new InvalidOperationException("The showcase media-404 work projection lost saved counts, retry schedule or active-source semantics.");
            // Simple's grid shows compact state/counts; full media retry progress is available
            // through its real Details inspector (and the grid tooltip), not a grid TextBlock.
            grid.SelectedItem = waiting;
            grid.ScrollIntoView(waiting, null);
            if (!window.FindControl<Border>("WorkspaceInspector")!.IsVisible)
                Click(window.FindControl<Button>("DetailsToggleButton")!);
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                SelectableTextBlock progress = window.FindControl<SelectableTextBlock>("InspectorProgress")!;
                SelectableTextBlock error = window.FindControl<SelectableTextBlock>("InspectorError")!;
                return window.FindControl<Border>("WorkspaceInspector")!.IsEffectivelyVisible
                    && progress.IsEffectivelyVisible && progress.Bounds.Width > 0
                    && progress.Text == waiting.Progress
                    && progress.Text!.Contains("1 paused (404)", StringComparison.Ordinal)
                    && error.IsEffectivelyVisible && error.Text == waiting.ErrorDetails
                    && error.Text!.Contains("Earliest retry:", StringComparison.Ordinal);
            });
            await CaptureAsync(window, "v0.9.0-downloads.png");
        }
        finally
        {
            grid.SelectedItem = null;
            foreach (ThreadRow row in rows) { window.UrlList.Remove(row); row.Dispose(); }
        }
    }

    private static async Task WaitForArchiveTilesAsync(MainWindow window, ArchivePage page)
    {
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        Rect[]? previous = null;
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            ArchiveMediaTileView[] views = tiles.GetVisualDescendants().OfType<ArchiveMediaTileView>().ToArray();
            if (!tiles.IsEffectivelyVisible || views.Length != 8
                || views.Any(view => !view.IsEffectivelyVisible || view.Bounds.Width <= 0
                    || !view.GetVisualDescendants().OfType<Image>().Any(image => image.Source is Bitmap)
                    || view.GetVisualDescendants().OfType<TextBlock>().Any(text => !string.IsNullOrEmpty(text.Text))))
            { previous = null; return false; }
            WrapPanel panel = tiles.GetVisualDescendants().OfType<WrapPanel>().Single();
            ScrollViewer scroll = tiles.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Control[] children = panel.Children.ToArray();
            Rect[] bounds = children.Select(child => child.Bounds).ToArray();
            bool wrapped = children.Length == 8 && bounds.Select(bound => Math.Round(bound.Y)).Distinct().Count() > 1;
            bool fits = scroll.Viewport.Width > 0 && scroll.Extent.Width <= scroll.Viewport.Width + 1
                && scroll.Extent.Height <= scroll.Viewport.Height + 1;
            // Require the decoded tile arrangement to survive a complete further layout pass.
            bool settled = wrapped && fits && previous != null && bounds.SequenceEqual(previous);
            previous = bounds;
            return settled;
        });
        Console.WriteLine("SHOWCASE archive tiles ready: all eight thumbnails decoded; multiple wrapped rows; all fit viewport; stable layout.");
    }

    private static async Task CaptureRepeatExportAsync(MainWindow window, ArchivePage page)
    {
        string profile = Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE")
            ?? throw new InvalidOperationException("A showcase profile is required.");
        string destination = Path.Combine(Path.GetDirectoryName(profile)!, "Exports", "landscape-sketchbook");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await page.OpenExportForThreadAsync(ShowcaseData.Hero, page.FindControl<Button>("ExportArchiveButton"));
        page.FindControl<TextBox>("ArchiveExportDestination")!.Text = destination;
        Click(page.FindControl<Button>("ArchiveExportStart")!);
        await Program.WaitUntilAsync(() => !page.IsExportRunning);
        if (page.LastExport == null || !File.Exists(page.LastExport.IndexPath))
            throw new InvalidOperationException("Showcase export did not publish its portable HTML result: "
                + page.FindControl<TextBlock>("ArchiveExportStatus")!.Text);
        Click(page.FindControl<Button>("ArchiveExportClose")!);
        await page.OpenExportForThreadAsync(ShowcaseData.Hero, page.FindControl<Button>("ExportArchiveButton"));
        if (page.FindControl<ComboBox>("ArchiveExportHistory")!.SelectedItem == null)
            throw new InvalidOperationException("Showcase export history was not persisted.");
        page.FindControl<RadioButton>("ArchiveExportUpdate")!.IsChecked = true;
        page.FindControl<CheckBox>("ArchiveExportBackup")!.IsChecked = true;
        Click(page.FindControl<Button>("ArchiveExportStart")!);
        await Program.WaitUntilAsync(() => !page.IsExportRunning);
        if (page.LastExport?.Metadata?.Generation != 2 || !page.FindControl<Button>("ArchiveExportOpen")!.IsEnabled)
            throw new InvalidOperationException("Repeated showcase export did not publish generation two.");
        await ResizeAsync(window, 1000, 820);
        await CaptureAsync(window, "v0.9.0-export.png");
        Click(page.FindControl<Button>("ArchiveExportClose")!);
    }

    private static Button GalleryButton(ContentControl gallery, string name) => gallery.GetVisualDescendants()
        .OfType<Button>().Single(button => button.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task ResizeAsync(Window window, double width, double height)
    {
        window.WindowState = WindowState.Normal; window.Width = width; window.Height = height;
        await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - width) < 2 && Math.Abs(window.Bounds.Height - height) < 2);
    }
    private static async Task CaptureAsync(Window window, string name)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        Program.CaptureWindow(window, name);
        Console.WriteLine($"SHOWCASE {name}; OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}; client={window.Bounds.Size}; scale={window.RenderScaling}; style=Simple/Dark/en; content=original synthetic demo, Skia procedural artwork.");
    }
}

/// <summary>Production window/session with provider admission disabled for the offline showcase.</summary>
internal sealed class ShowcaseSmokeApp : App
{
    protected override MainWindow CreateMainWindow() => new(new DesktopSessionHost(
        Config.GetConfigDirectory(), providers: Array.Empty<IImageBoard>()));
}
