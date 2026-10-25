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
using MeowChan.Rules;

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
        Click(GalleryButton(gallery, "GalleryFavorite"));
        await Program.WaitUntilAsync(() => page.GetFavorites?.Invoke().Count is > 0);
        Click(GalleryButton(gallery, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => gallery.GetVisualDescendants().OfType<Border>()
            .Any(border => border.Name == "GalleryContext" && border.IsVisible && border.Bounds.Width > 0));
        await CaptureAsync(window, "v0.9.0-gallery.png");
        // Capture the same saved artwork through the real media-focus action.
        Click(GalleryButton(gallery, "GalleryToggleContext"));
        Click(GalleryButton(gallery, "GalleryToolsToggle"));
        await Program.WaitUntilAsync(() => !GalleryButton(gallery, "GalleryFit").IsEffectivelyVisible
            && !gallery.GetVisualDescendants().OfType<Border>()
                .Any(border => border.Name is "GalleryContext" or "GalleryOverlayInfo" && border.IsVisible));
        await CaptureAsync(window, "smoke-1.0-gallery-focus.png");
        Click(GalleryButton(gallery, "GalleryToolsToggle"));
        Click(GalleryButton(gallery, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => GalleryButton(gallery, "GalleryFit").IsEffectivelyVisible);
        await CheckGalleryModeGeometryAsync(window, gallery);
        Click(GalleryButton(gallery, "GalleryClose"));

        await CaptureReaderAsync(window, page);

        // Search remains active beside the actual saved-media tiles of its selected thread.
        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
        if (!page.FindControl<ListBox>("ArchiveMediaTiles")!.IsVisible)
            Click(page.FindControl<Button>("ArchiveMediaMode")!);
        await WaitForArchiveTilesAsync(window, page);
        await CaptureAsync(window, "v0.9.0-archive.png");

        await CaptureRepeatExportAsync(window, page);
        await CaptureFavoritesAsync(window, page);
        await CaptureSettingsAndRulesAsync(window);
        await ResizeAsync(window, 1440, 920);
        window.FindControl<MenuItem>("AboutMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any(about => about.IsVisible));
        AboutWindow about = window.OwnedWindows.OfType<AboutWindow>().Single();
        await Program.WaitUntilAsync(() => about.FindControl<Image>("CatLogo")!.Source != null && about.Bounds.Width > 0);
        Button donate = about.FindControl<Button>("DonationLink")!;
        if (!donate.IsEffectivelyVisible || donate.Tag?.ToString() != "https://www.paypal.com/donate/?hosted_button_id=5BR4MK748PDSQ")
            throw new InvalidOperationException("The visible About support link does not match the project donation destination.");
        await CaptureAsync(about, "v0.9.0-about.png");
        about.FindControl<TabControl>("AboutTabs")!.SelectedIndex = 1;
        await Program.WaitUntilAsync(() => about.FindControl<TextBlock>("VideoCreditsText")!.IsEffectivelyVisible);
        await CaptureAsync(about, "v0.9.0-about-credits.png");
        about.FindControl<TabControl>("AboutTabs")!.SelectedIndex = 2;
        TextBox licenseSearch = about.FindControl<TextBox>("LicenseSearchBox")!;
        licenseSearch.Text = "Feather";
        await Program.WaitUntilAsync(() => about.FindControl<TextBox>("LicenseFullText")!.Text?.Contains("Cole Bemis") == true);
        await CaptureAsync(about, "v0.9.0-about-licenses.png");
        Click(about.FindControl<Button>("CloseButton")!);
        Console.WriteLine("SHOWCASE final UI scenes captured: downloads, gallery/post context/favorite, reader and replies, archive/media, HTML export, favorites/original export, settings, rules, About/support/credits/offline licenses.");
    }

    private static async Task CheckGalleryModeGeometryAsync(MainWindow window, ContentControl gallery)
    {
        Size previous = window.ClientSize;
        foreach ((string mode, int width, int height) in new[] { ("Barebones", 640, 480), ("Advanced", 760, 500), ("Simple", 760, 500) })
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            await ResizeAsync(window, width, height);
            await ReaderUxChecks.CheckOverlayChromeAsync(window, gallery);
            Program.CaptureWindow(window, "smoke-gallery-minimum-" + mode.ToLowerInvariant() + ".png");
        }
        await ResizeAsync(window, previous.Width, previous.Height);
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
                || !waiting.Progress.Contains("1 waiting (404)", StringComparison.Ordinal)
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
                SelectableTextBlock context = window.FindControl<SelectableTextBlock>("InspectorContext")!;
                return window.FindControl<Border>("WorkspaceInspector")!.IsEffectivelyVisible
                    && progress.IsEffectivelyVisible && progress.Bounds.Width > 0
                    && progress.Text == waiting.Progress
                    && progress.Text!.Contains("1 waiting (404)", StringComparison.Ordinal)
                    && error.IsEffectivelyVisible && error.Text == waiting.IssueDetails
                    && error.Text!.Contains("404", StringComparison.Ordinal)
                    && !error.Text.Contains("Earliest retry:", StringComparison.Ordinal)
                    && context.IsEffectivelyVisible && context.Text == waiting.ContextDetails
                    && context.Text!.Contains("Earliest retry:", StringComparison.Ordinal);
            });
            await CaptureAsync(window, "v0.9.0-downloads.png");
        }
        finally
        {
            grid.SelectedItem = null;
            foreach (ThreadRow row in rows) { window.UrlList.Remove(row); row.Dispose(); }
        }
    }

    private static async Task CaptureReaderAsync(MainWindow window, ArchivePage page)
    {
        await ResizeAsync(window, 1440, 960);
        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
        await Program.WaitUntilAsync(() => reader.IsVisible && !page.IsBusy && page.CurrentThread?.Summary.Id == ShowcaseData.Hero);
        await ReaderUxChecks.CheckOverlayChromeAsync(window, reader);
        await page.MediaLoadTask;
        await page.FocusPostAsync(ShowcaseData.Hero.Thread!);
        Rect? previousImage = null;
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            ArchiveAttachmentView? first = reader.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .FirstOrDefault(view => view.IsEffectivelyVisible && view.DataContext is ArchiveAttachmentRow);
            ScrollViewer viewport = posts.GetVisualDescendants().OfType<ScrollViewer>().Single();
            ArchivePostRow? opening = posts.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault();
            SelectableTextBlock? body = reader.GetVisualDescendants().OfType<SelectableTextBlock>()
                .FirstOrDefault(text => text.IsEffectivelyVisible && text.Text == opening?.Body);
            if (page.FindControl<Grid>("ArchiveReaderPane")!.Parent != reader
                || first?.Thumbnail.Source is not Bitmap bitmap || bitmap.PixelSize.Width <= 240
                || first.Thumbnail.Bounds.Width < 240 || first.Thumbnail.Bounds.Height < 160 || body == null
                || !FitsInViewport(first.Thumbnail, viewport) || !FitsInViewport(body, viewport)
                || !page.FindControl<Button>("ReaderNextPost")!.IsEnabled
                || !page.FindControl<Button>("ReaderNextThread")!.IsEnabled
                || !page.FindControl<CheckBox>("ReaderFollowLive")!.IsEffectivelyVisible)
            { previousImage = null; return false; }
            Point? origin = first.Thumbnail.TranslatePoint(default, reader);
            if (origin == null) return false;
            Rect image = new(origin.Value, first.Thumbnail.Bounds.Size);
            bool stable = previousImage == image;
            previousImage = image;
            return stable;
        });
        await CaptureAsync(window, "v0.9.0-reader.png");
        ListBox postsList = page.FindControl<ListBox>("ArchivePosts")!;
        ArchivePostRow openingPost = postsList.ItemsSource!.Cast<ArchivePostRow>().First();
        Expander replies = reader.GetVisualDescendants().OfType<Expander>()
            .First(expander => ReferenceEquals(expander.DataContext, openingPost));
        replies.IsExpanded = true;
        await Program.WaitUntilAsync(() => openingPost.RepliesLoaded && openingPost.Replies.Count > 0
            && replies.IsEffectivelyVisible);
        // Avalonia's Expander animates its reveal after the reply data is loaded.
        // Capturing when only the first button appears clips later replies.
        await Task.Delay(350);
        replies.BringIntoView();
        ScrollViewer repliesViewport = postsList.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Rect[]? previousReplyBounds = null;
        double previousExpanderHeight = 0;
        int stableReplyPasses = 0;
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            Button[] buttons = replies.GetVisualDescendants().OfType<Button>()
                .Where(button => button.DataContext is ArchiveQuoteRow).ToArray();
            if (buttons.Length != openingPost.Replies.Count ||
                buttons.Any(button => !button.IsEffectivelyVisible || button.Bounds.Width <= 0 || button.Bounds.Height <= 0
                    || !FitsInside(button, replies) || !FitsInViewport(button, repliesViewport)))
            {
                stableReplyPasses = 0;
                previousReplyBounds = null;
                replies.BringIntoView();
                return false;
            }
            Rect[] currentBounds = buttons.Select(button =>
                new Rect(button.TranslatePoint(default, repliesViewport)!.Value, button.Bounds.Size)).ToArray();
            stableReplyPasses = previousReplyBounds != null && currentBounds.SequenceEqual(previousReplyBounds)
                && Math.Abs(replies.Bounds.Height - previousExpanderHeight) < 0.1 ? stableReplyPasses + 1 : 0;
            previousReplyBounds = currentBounds;
            previousExpanderHeight = replies.Bounds.Height;
            return stableReplyPasses >= 2;
        });
        Console.WriteLine($"SHOWCASE reader replies settled: {openingPost.Replies.Count} complete visible buttons in expander and post viewport.");
        await CaptureAsync(window, "v0.9.0-reader-replies.png");
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        await Program.WaitUntilAsync(() => !reader.IsVisible
            && page.FindControl<Grid>("ArchiveReaderPane")!.Parent == page.FindControl<Grid>("ArchiveSplit"));
        await ResizeAsync(window, 1440, 920);
    }

    private static bool FitsInViewport(Control control, ScrollViewer viewport)
    {
        Point? point = control.TranslatePoint(default, viewport);
        return point.HasValue && point.Value.X >= 0 && point.Value.Y >= 0
            && point.Value.X + control.Bounds.Width <= viewport.Viewport.Width + 1
            && point.Value.Y + control.Bounds.Height <= viewport.Viewport.Height + 1;
    }

    private static bool FitsInside(Control child, Control parent)
    {
        Point? point = child.TranslatePoint(default, parent);
        return point.HasValue && point.Value.X >= -1 && point.Value.Y >= -1
            && point.Value.X + child.Bounds.Width <= parent.Bounds.Width + 1
            && point.Value.Y + child.Bounds.Height <= parent.Bounds.Height + 1;
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
                    || !HasOnlyReadyTileText(view)))
            { previous = null; return false; }
            WrapPanel panel = tiles.GetVisualDescendants().OfType<WrapPanel>().Single();
            ScrollViewer scroll = tiles.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Control[] children = panel.Children.ToArray();
            Rect[] bounds = children.Select(child => child.Bounds).ToArray();
            bool wrapped = children.Length == 8 && bounds.Select(bound => Math.Round(bound.Y)).Distinct().Count() > 1;
            bool fits = scroll.Viewport.Width > 0 && scroll.Extent.Width <= scroll.Viewport.Width + 1;
            // Require the decoded tile arrangement to survive a complete further layout pass.
            bool settled = wrapped && fits && previous != null && bounds.SequenceEqual(previous);
            previous = bounds;
            return settled;
        });
        Console.WriteLine("SHOWCASE archive media tiles ready: all eight thumbnails decoded; multiple wrapped rows; no horizontal overflow; stable layout.");
    }

    private static bool HasOnlyReadyTileText(ArchiveMediaTileView view)
    {
        if (view.DataContext is not ArchiveAssetRow row) return false;
        // Current tiles display saved-media metadata alongside their preview.
        // Keep rejecting loading, unavailable-preview and error text rather than
        // treating every nonempty label as an unfinished thumbnail.
        string[] expected = [row.Name, row.TileSummary, MediaRatingText.Label + ": " + row.RatingLabel];
        string[] actual = view.GetVisualDescendants().OfType<TextBlock>()
            .Select(text => text.Text ?? string.Empty).Where(text => text.Length > 0).ToArray();
        return expected.All(text => actual.Contains(text)) && actual.All(text => expected.Contains(text));
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

    private static async Task CaptureFavoritesAsync(MainWindow window, ArchivePage page)
    {
        if (page.GetFavorites?.Invoke().Count is not > 0)
            throw new InvalidOperationException("The showcase must mark a real stored image as a favorite before capturing its export.");
        await ResizeAsync(window, 1440, 920);
        Task overviewTask = page.OpenFavoritesOverviewAsync();
        Window overview = await WaitForOwnedWindowAsync(window, owned => owned.GetVisualDescendants()
            .OfType<Button>().Any(button => button.Name == "FavoritesExportOriginals"));
        try
        {
            await Program.WaitUntilAsync(() => overview.GetVisualDescendants().OfType<ListBox>()
                .Any(list => list.Name == "FavoritesList" && list.ItemCount > 0)
                && overview.GetVisualDescendants().OfType<Button>()
                    .Any(button => button.Name == "FavoritesExportOriginals" && button.IsEnabled));
            await CaptureAsync(overview, "v0.9.0-favorites.png");

            string profile = Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE")
                ?? throw new InvalidOperationException("A showcase profile is required.");
            string destination = Path.Combine(Path.GetDirectoryName(profile)!, "Exports", "favorites-originals");
            Directory.CreateDirectory(destination);
            page.LastExportDirectoryPreference = destination;
            Click(overview.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "FavoritesExportOriginals"));
            Window export = await WaitForOwnedWindowAsync(overview, owned => owned.GetVisualDescendants()
                .OfType<TextBox>().Any(box => box.Name == "OriginalExportDestination"));
            try
            {
                TextBox target = export.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Name == "OriginalExportDestination");
                if (target.Text != destination)
                    throw new InvalidOperationException("Original export did not retain the isolated showcase destination.");
                await CaptureAsync(export, "v0.9.0-favorites-original-export.png");
                Button start = export.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Content?.ToString() == "Export originals");
                Click(start);
                await Program.WaitUntilAsync(() => export.GetVisualDescendants().OfType<TextBlock>()
                    .Any(label => label.Text?.StartsWith("Copied 1 ·", StringComparison.Ordinal) == true));
                await CaptureAsync(export, "v0.9.0-favorites-original-result.png");
            }
            finally { export.Close(); }
        }
        finally { overview.Close(); await overviewTask; }
    }

    private static async Task CaptureSettingsAndRulesAsync(MainWindow window)
    {
        await ResizeAsync(window, 1200, 700);
        window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SettingsPageVisible);
        SettingsEditor settings = window.SettingsEditor;
        settings.FindControl<ScrollViewer>("SettingsScrollViewer")!.Offset = default;
        await Program.WaitUntilAsync(() => settings.FindControl<ComboBox>("ReadingFontSizeInput")!.IsEffectivelyVisible);
        await CaptureAsync(window, "v0.9.0-settings.png");

        window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        RulesPage rules = window.RulesPage;
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Rules && rules.IsEditable);
        rules.EditScope(MediaRuleScope.Global);
        rules.FindControl<ScrollViewer>("RulesScroll")!.Offset = default;
        await Program.WaitUntilAsync(() => rules.FindControl<TextBlock>("RuleEffectiveTypes")!.IsEffectivelyVisible);
        await CaptureAsync(window, "v0.9.0-rules.png");
    }

    private static async Task<Window> WaitForOwnedWindowAsync(Window owner, Func<Window, bool> predicate)
    {
        Window? match = null;
        await Program.WaitUntilAsync(() => (match = owner.OwnedWindows.FirstOrDefault(window =>
            window.IsVisible && predicate(window))) != null);
        return match!;
    }

    private static Button GalleryButton(ContentControl gallery, string name) => gallery.GetVisualDescendants()
        .OfType<Button>().Single(button => button.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task ResizeAsync(Window window, double width, double height)
    {
        window.WindowState = WindowState.Normal; window.Width = width; window.Height = height;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
    }
    private static async Task CaptureAsync(Window window, string name)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
        if (window is MainWindow main)
        {
            if (Config.LoadConfig().WorkspaceMode != "Simple" || Texts.Language != "en"
                || Application.Current!.RequestedThemeVariant != ThemeVariant.Dark)
                throw new InvalidOperationException("Release showcase captures require Simple/Dark/English.");
            await WindowChromeChecks.CheckReachableAsync(main);
            foreach (string hostName in new[] { "ReaderHost", "GalleryHost" })
            {
                ContentControl overlay = main.FindControl<ContentControl>(hostName)!;
                if (overlay.IsEffectivelyVisible) await ReaderUxChecks.CheckOverlayChromeAsync(main, overlay);
            }
            await Program.WaitUntilAsync(() =>
            {
                Image logo = main.FindControl<Image>("WorkspaceLogo")!;
                Point? origin = logo.TranslatePoint(default, main);
                return logo.IsEffectivelyVisible && logo.Source != null && logo.Bounds.Width >= 24
                    && logo.Bounds.Height >= 24 && origin is Point position && position.X >= 0 && position.Y >= 0
                    && position.X + logo.Bounds.Width <= main.Bounds.Width + 1
                    && position.Y + logo.Bounds.Height <= main.FindControl<Grid>("WorkspaceTitleBar")!.Bounds.Height + 1;
            });
        }
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
