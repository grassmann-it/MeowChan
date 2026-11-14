using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

internal static class ArchivePageChecks
{
    internal static async Task RunAsync(MainWindow window, long watchId, ResourceId id)
    {
        // Removing monitoring must leave independently readable posts and saved media intact.
        await window.SessionHost.RemoveThreadAsync(watchId);
        await Program.WaitUntilAsync(() => window.UrlList.Count == 0);
        OpenArchive(window);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == 1);
        await ArchiveSearchStateChecks.RunAsync();
        await CheckArchiveColumnsAsync(page);
        await CheckEmptySearchAsync(window, page);
        await ReaderPassiveFocusChecks.RunAsync(window, page, id);
        ListBox threads = page.FindControl<ListBox>("ArchiveThreads")!;
        threads.SelectedIndex = 0;
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == id);
        Require(page.CurrentThread!.PostCount == 1, "Removing a restored watch lost its captured posts.");
        Require(page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!.Cast<ArchivePostRow>().Single().Body
            == "Offline & readable\n>>124", "The reader did not render persisted source text as plain text.");
        await page.MediaLoadTask;
        DataGrid media = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        Require(media.ItemsSource.Cast<ArchiveAssetRow>().Count() == 1, "The independent archive did not discover the saved manifest.");
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        await Program.WaitUntilAsync(() => posts.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .Any(view => view.Thumbnail.Source is Bitmap));
        ArchiveAttachmentView inline = posts.GetVisualDescendants().OfType<ArchiveAttachmentView>().Single();
        Require(inline.Thumbnail.Bounds.Width <= 1040 && inline.Thumbnail.Bounds.Height <= 720,
            "An inline reader image exceeded its bounded preview dimensions.");
        await ArchiveMediaCommandChecks.RunAsync(page, inline);
        await FlushAsync();
        Program.CaptureWindow(window, "smoke-archive-restored-posts.png");
        Click(inline.PreviewButton);
        ContentControl gallery = window.FindControl<ContentControl>("GalleryHost")!;
        await Program.WaitUntilAsync(() => gallery.IsVisible && gallery.GetVisualDescendants().OfType<Image>()
            .Any(image => image.Name == "GalleryImage" && image.Source is Bitmap));
        Require(page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex == 0,
            "Clicking an inline image changed away from the post reader.");
        GalleryViewer inlineViewer = (GalleryViewer)gallery.Content!;
        Require(!string.IsNullOrWhiteSpace(GalleryControl<TextBlock>(inlineViewer, "GalleryScope").Text),
            "Thread gallery did not label its scope.");
        Require(inlineViewer.CaptureState().PostId != null, "Inline gallery lost the selected image's captured post association.");
        // PF-05: while the gallery covers the archive page, the page pauses background work, and a closed
        // context panel builds no controls until it is opened.
        Require(page.IsObscuredByGallery, "The archive page was not told that the gallery covers it.");
        Require(ArchiveAttachmentView.GetLoadingSuspended(page), "Thumbnail loading was not suspended while the gallery covers the page.");
        Require(inlineViewer.ContextBuildCount == 0, "A closed gallery context was built eagerly.");
        Click(GalleryControl<Button>(inlineViewer, "GalleryToggleContext"));
        // The hidden context ScrollViewer applies its content template on the next layout pass.
        await Program.WaitUntilAsync(() => inlineViewer.GetVisualDescendants().OfType<Button>()
            .Any(button => button.Name == "GalleryReadPost"));
        Require(inlineViewer.ContextBuildCount == 1, "Opening the gallery context did not build its content exactly once.");
        await CheckGalleryLanguageFocusAsync(window, inlineViewer);
        Click(GalleryControl<Button>(inlineViewer, "GalleryReadPost"));
        await Program.WaitUntilAsync(() => !gallery.IsVisible && !page.IsBusy && page.CurrentThread?.Summary.Id == id);
        Require(!page.IsObscuredByGallery && !ArchiveAttachmentView.GetLoadingSuspended(page),
            "The archive page stayed paused after the gallery stopped covering it.");
        Require(page.FindControl<Button>("ArchiveGalleryReturn")!.IsVisible, "Reader excursion has no gallery return action.");
        Click(page.FindControl<Button>("ArchiveGalleryReturn")!);
        Require(gallery.IsVisible && ReferenceEquals(gallery.Content, inlineViewer), "Reader return replaced the frozen gallery.");
        Require(inlineViewer.CaptureState().ContextOpen, "Reader return lost the open post context.");
        Click(GalleryControl<Button>(inlineViewer, "GalleryClose"));
        Require(!gallery.IsVisible && !page.FindControl<Button>("ArchiveGalleryReturn")!.IsVisible,
            "Closing the gallery retained an overlay or return action.");
        Require(page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex == 0 && page.CurrentThread?.Summary.Id == id,
            "Closing after a reader excursion lost the original reader and tab.");

        // Visible entry action restores keyboard focus to the same origin control.
        Button focusOrigin = page.FindControl<Button>("ArchiveCloseReader")!;
        Task supersededSearch = window.OpenSearchGalleryAsync(new ArchiveThreadQuery(), origin: focusOrigin);
        await window.OpenGalleryAsync(id, origin: focusOrigin);
        GalleryViewer replacementViewer = (GalleryViewer)gallery.Content!;
        await supersededSearch;
        Require(ReferenceEquals(gallery.Content, replacementViewer), "A superseded search replaced the current gallery.");
        Require(GalleryControl<TextBlock>(replacementViewer, "GalleryScope").Text!.StartsWith("Thread"),
            "A superseded search changed the current thread scope.");
        Click(GalleryControl<Button>(replacementViewer, "GalleryClose"));
        await Program.WaitUntilAsync(() => window.FocusManager!.GetFocusedElement() is Visual focused
            && focused.GetVisualAncestors().Contains(page.FindControl<ListBox>("ArchivePosts")!));

        // A captured self-quote exercises the real quote/reader path without changing source data.
        CapturedPost quotePost = page.CurrentThread!.GetPosts().Single();
        ArchiveAssetRow quoteAsset = media.ItemsSource.Cast<ArchiveAssetRow>().Single();
        GalleryEntry quotedEntry = new(new GalleryAssetKey(id, quoteAsset.Manifest.Directory, quoteAsset.Asset.AssetId),
            quoteAsset.Manifest, quoteAsset.Asset,
            quotePost with { Html = "<a href='#p" + quotePost.PostId + "'>&gt;&gt;" + quotePost.PostId + "</a>" },
            null, false, false) { ThreadUrl = page.CurrentThread!.Summary.Url };
        // This independent workload changes language and therefore recreates post rows. Run it
        // before establishing the history/inline-expansion state the quote excursion must retain.
        await GalleryWorkloadChecks.RunAsync(window, quotedEntry);
        ArchivePostId quoteId = new(id, quotePost.PostId);
        await page.NavigateQuoteAsync(new ArchiveQuoteRow(new ArchiveQuote(quoteId, quoteId, ">>" + quotePost.PostId, null)));
        await page.MediaLoadTask;
        Require(page.FindControl<Button>("ArchiveHistoryBack")!.IsVisible, "Quote navigation did not create a reader history entry.");
        ArchivePostRow replyTarget = page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!
            .Cast<ArchivePostRow>().Single();
        await Program.WaitUntilAsync(() => replyTarget.BacklinkCount == 0);
        await page.SetRepliesExpandedAsync(replyTarget, true); // A confirmed zero offers no counter; the state itself is what this check keeps.
        await Program.WaitUntilAsync(() => replyTarget.RepliesLoaded && !replyTarget.RepliesLoading);
        Require(replyTarget.Replies.Count == 0 && replyTarget.RepliesExpanded,
            "The inline reply expander did not show its empty local result.");
        Require(page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!.Cast<ArchivePostRow>().Single().RepliesExpanded,
            "The quote excursion setup lost its expanded inline replies before opening the gallery.");
        await window.OpenMediaGalleryLaidOutAsync(new[] { quotedEntry }, quotedEntry.EntryKey, focusOrigin);
        GalleryViewer quoteViewer = (GalleryViewer)gallery.Content!;
        // The previous gallery may have persisted an open context. A blind toggle would close
        // it, while retained hidden quote controls still appear in GetVisualDescendants().
        if (!quoteViewer.CaptureState().ContextOpen)
            Click(GalleryControl<Button>(quoteViewer, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => window.IsActive && quoteViewer.CaptureState().ContextOpen
            && quoteViewer.GetVisualDescendants().OfType<Button>()
                .Any(button => button.Name == "GalleryQuote" && button.IsEffectivelyVisible && button.IsEffectivelyEnabled));
        Button quoteAction = GalleryControl<Button>(quoteViewer, "GalleryQuote");
        Require(quoteAction.Focus() && quoteAction.IsKeyboardFocusWithin,
            "The gallery quote action did not receive focus in the active shell.");
        Click(quoteAction);
        await Program.WaitUntilAsync(() => !gallery.IsVisible && page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex == 0);
        Click(page.FindControl<Button>("ArchiveGalleryReturn")!);
        Click(GalleryControl<Button>(quoteViewer, "GalleryClose"));
        Require(page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex == 0,
            "Closing after a quote excursion lost the post reader.");
        ArchivePostRow restoredReplyTarget = page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!
            .Cast<ArchivePostRow>().Single();
        Require(restoredReplyTarget.Id == quotePost.PostId && restoredReplyTarget.RepliesExpanded,
            "Closing after a quote excursion lost the inline reply expansion or post.");
        Click(page.FindControl<Button>("ArchiveHistoryBack")!);
        Require(!page.FindControl<Button>("ArchiveHistoryBack")!.IsVisible,
            "Closing after a quote excursion leaked its extra reader history entry.");
        await page.MediaLoadTask;

        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
        ArchiveAssetRow previewAsset = media.ItemsSource.Cast<ArchiveAssetRow>().Single();
        ListBox mediaTiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        if (mediaTiles.IsVisible) mediaTiles.SelectedItem = previewAsset;
        else media.SelectedItem = previewAsset;
        await FlushAsync();
        Require(page.FindControl<Button>("PreviewArchiveMediaButton")!.IsEnabled,
            "Selecting the visible saved-media item did not enable its preview action.");
        // Supersede an off-thread saved-media association before it can publish its overlay.
        Click(page.FindControl<Button>("PreviewArchiveMediaButton")!);
        await window.OpenGalleryAsync(id, origin: focusOrigin);
        GalleryViewer associationWinner = (GalleryViewer)gallery.Content!;
        await FlushAsync();
        Require(ReferenceEquals(gallery.Content, associationWinner),
            "A superseded saved-media association replaced the newer gallery.");
        Click(GalleryControl<Button>(associationWinner, "GalleryClose"));
        Click(page.FindControl<Button>("PreviewArchiveMediaButton")!);
        await Program.WaitUntilAsync(() => gallery.IsVisible);
        try
        {
            await Program.WaitUntilAsync(() => gallery.GetVisualDescendants().OfType<Image>()
                .Any(image => image.Name == "GalleryImage" && image.Source is Bitmap));
        }
        catch
        {
            Console.WriteLine("SMOKE saved-media gallery state: " + string.Join(" | ", gallery.GetVisualDescendants()
                .OfType<TextBlock>().Select(text => text.Name + "=" + text.Text)));
            Program.CaptureWindow(window, "smoke-archive-preview-failure.png");
            throw;
        }
        Program.CaptureWindow(window, "smoke-archive-verified-preview.png");
        Click(gallery.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose"));
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        await page.LoadThreadAsync(id);
        Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        OpenArchive(window);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == id);
        Require(!page.FindControl<Border>("ArchivePreview")!.IsVisible,
            "Reactivating the archive retained a stale preview or blocked its list load.");

        foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                window.WindowState = WindowState.Normal;
                double width = mode == "Barebones" ? 640 : 760;
                double height = mode == "Barebones" ? 480 : 500;
                window.Width = width;
                window.Height = height;
                page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
                await Program.WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return PresentationChecks.MatchesRequestedSize(window, width, height)
                        // WV3/R2: the Thread menu is a reader tool; Barebones and narrow windows keep it under the toolbar's More.
                        && (Fits(window, page.FindControl<Button>("ReaderThreadOpen")!) || ReaderWorkViewChecks.OnBarOrUnderMore(page, "ReaderThreadOpen"))
                        && Fits(window, page.FindControl<Button>("ArchiveBackButton")!)
                        && page.FindControl<ListBox>("ArchivePosts")!.Bounds.Height >= 100
                        && Fits(window, window.FindControl<Button>("SettingsNavigationButton")!)
                        && Fits(window, window.FindControl<ComboBox>("WorkspaceModeComboBox")!);
                });
                await Program.WaitUntilAsync(() => window.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                    .Any(view => view.Thumbnail.Source is Bitmap));
                Program.CaptureWindow(window, $"smoke-archive-{mode.ToLowerInvariant()}-{language}-minimum.png");
                // ARCH-DENSE-01: the reader pane ends with its content border; no footer row is left below it,
                // and without an error the status row reserves nothing (messages are an overlay).
                Grid readerPane = page.FindControl<Grid>("ArchiveReaderPane")!;
                Grid root = page.FindControl<Grid>("ArchiveRoot")!;
                Border readerContent = page.FindControl<Border>("ArchiveReaderContent")!;
                double blankBelowReader = page.Bounds.Height - (readerContent.TranslatePoint(default, page)!.Value.Y + readerContent.Bounds.Height);
                bool errorShown = page.FindControl<MeowChan.Desktop.Controls.WorkspaceBanner>("ArchiveErrorBanner")!.IsVisible;
                Console.WriteLine($"SMOKE archive stacked footer {mode}/{language}: listVisible={page.FindControl<Grid>("ArchiveListPane")!.IsVisible}, readerRows={readerPane.RowDefinitions.Count}, statusStrip={root.RowDefinitions[2].ActualHeight}, blankBelowReader={blankBelowReader}, errorShown={errorShown}");
                Require(readerPane.RowDefinitions.Count == 2 && page.FindControl<TextBlock>("ArchivePartialHint") == null
                    && (errorShown || blankBelowReader <= root.Margin.Bottom + 1),
                    "The stacked archive reader still reserves a footer or status row below its content.");
                if (mode == "Barebones" && language == "en") await CheckNarrowStatusOverlayAsync(window, page, id);
            }
        }
        Texts.SetLanguage("en");
        Click(window.FindControl<Button>("ThemeToggleButton")!);
        await Program.WaitUntilAsync(() => Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            && window.FindControl<Button>("ThemeToggleButton")!.IsEnabled);
        Program.CaptureWindow(window, "smoke-archive-dark-compact.png");
        await ArchiveExportChecks.RunAsync(window, page.CurrentThread!);
        await CheckSlowMediaAsync(window, id);
        await ReaderPreviewChecks.RunAsync(window);
        await ArchiveNavigationChecks.RunAsync(window);
        await GalleryViewerChecks.RunAsync(window);
        Console.WriteLine("SMOKE archive: real post capture, restart, watch removal, plain offline reader, verified image preview, independent media loading and compact EN/DE pages passed.");
    }

    private static async Task CheckEmptySearchAsync(MainWindow window, ArchivePage page)
    {
        TextBox search = page.FindControl<TextBox>("ArchiveSearchText")!;
        ComboBox sort = page.FindControl<ComboBox>("ArchiveSort")!;
        search.Text = "no local captured thread can match this search";
        sort.SelectedIndex = 2;
        await page.ApplySearchAsync();
        Button reset = page.FindControl<Button>("ArchiveResetFilters")!;
        Require(page.FindControl<StackPanel>("ArchiveEmptyState")!.IsVisible && reset.IsVisible
            && page.FindControl<TextBlock>("ArchiveEmpty")!.Text == Texts.T("archive.noResults"),
            "A filtered empty archive was presented as an empty collection.");
        Program.CaptureWindow(window, "smoke-archive-no-search-results.png");
        Click(reset);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == 1);
        Require(search.Text == string.Empty && sort.SelectedIndex == 2
            && !page.FindControl<StackPanel>("ArchiveEmptyState")!.IsVisible,
            "Explicit search reset failed to restore local results or changed sorting.");
        sort.SelectedIndex = 0;
        await page.ApplySearchAsync();
        page.FindControl<ComboBox>("ArchiveWorkFilter")!.SelectedIndex = 4;
        await page.ApplySearchAsync();
        Require(page.FindControl<StackPanel>("ArchiveEmptyState")!.IsVisible && reset.IsVisible,
            "A work filter with no matches did not expose its explicit reset.");
        Click(reset);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == 1);
        Require(page.FindControl<ComboBox>("ArchiveWorkFilter")!.SelectedIndex == 0,
            "Reset retained a restrictive work filter.");
    }

    // ARCH-DENSE-01 (user decision 11.11.2026): in the narrow, compact layout the status overlay
    // floats inside the stacked reader or list above its docked pager, and once empty or expired
    // it is gone, so the list beneath takes the pointer again.
    private static async Task CheckNarrowStatusOverlayAsync(MainWindow window, ArchivePage page, ResourceId id)
    {
        MethodInfo operationMessage = typeof(ArchivePage).GetMethod("OperationMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo expireMessage = typeof(ArchivePage).GetMethod("ExpireStatusMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Border overlay = page.FindControl<Border>("ArchiveStatusOverlay")!;
        TextBlock message = page.FindControl<TextBlock>("ArchiveMessage")!;
        Rect Bounds(Control control) => new(control.TranslatePoint(default, window)
            ?? throw new InvalidOperationException(control.Name + " is detached."), control.Bounds.Size);
        bool Inside(Rect outer, Rect inner) => inner.Left >= outer.Left - 1 && inner.Top >= outer.Top - 1
            && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;
        bool Hits(Point point, Visual target) => window.InputHitTest(point) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(target);
        async Task RequireHitAsync(Func<bool> condition, Point point, string failure)
        {
            try { await Program.WaitUntilAsync(() => { window.UpdateLayout(); return condition(); }); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Visual? hit = window.InputHitTest(point) as Visual;
                throw new InvalidOperationException(failure + $" overlayVisible={overlay.IsVisible}, point={point}, hit="
                    + (hit == null ? "none" : string.Join(" < ", hit.GetSelfAndVisualAncestors().Take(8).Select(v => v.GetType().Name + (v is Control { Name: { } n } ? "#" + n : "")))) + ".", error);
            }
        }
        void Show(string text)
        {
            operationMessage.Invoke(page, new object?[] { "action", text, false });
            window.UpdateLayout(); window.UpdateLayout();
        }
        string text = Texts.T("archive.loading");
        Border readerContent = page.FindControl<Border>("ArchiveReaderContent")!;
        Require(readerContent.IsEffectivelyVisible && !page.FindControl<GridSplitter>("ArchiveSplitter")!.IsVisible
            && !page.FindControl<Grid>("ArchiveListPane")!.IsVisible, "The narrow status overlay check requires the stacked reader.");
        Show(text);
        Rect box = Bounds(overlay), reader = Bounds(readerContent);
        Grid postPager = page.FindControl<Grid>("ArchivePostNavigation")!;
        Console.WriteLine($"SMOKE archive narrow status overlay reader: overlay={box}, reader={reader}, postPager={(postPager.IsEffectivelyVisible ? Bounds(postPager).ToString() : "hidden")}, page={Bounds(page)}");
        Require(overlay.IsEffectivelyVisible && message.IsEffectivelyVisible && Inside(reader, box)
            && (!postPager.IsEffectivelyVisible || box.Bottom <= Bounds(postPager).Top + 1),
            $"The narrow archive status overlay is not shown inside the stacked reader above its post pager: overlay={box}, reader={reader}.");
        Program.CaptureWindow(window, "smoke-archive-status-overlay-narrow-reader.png");
        expireMessage.Invoke(page, Array.Empty<object?>());
        window.UpdateLayout();
        Require(!overlay.IsVisible, "The expired narrow status overlay is still shown.");
        await RequireHitAsync(() => Hits(box.Center, readerContent), box.Center, "The expired narrow status overlay still takes the pointer from the reader.");

        Click(page.FindControl<Button>("ArchiveBackButton")!);
        await Program.WaitUntilAsync(() => { window.UpdateLayout(); return page.FindControl<Grid>("ArchiveListPane")!.IsEffectivelyVisible; });
        Control list = page.FindControl<ListBox>("ArchiveThreads")!.IsEffectivelyVisible
            ? page.FindControl<ListBox>("ArchiveThreads")! : page.FindControl<DataGrid>("ArchiveThreadTable")!;
        Border results = page.FindControl<Border>("ArchiveResultsSurface")!, bar = page.FindControl<Border>("ArchiveResultsBar")!;
        Rect listBox = Bounds(list);
        Point lastRow = new(listBox.Left + 40, listBox.Bottom - 12);
        await RequireHitAsync(() => !overlay.IsVisible && Hits(lastRow, list), lastRow, "The empty narrow status overlay is shown or takes the pointer from the list.");
        Show(text);
        box = Bounds(overlay);
        Rect resultBox = Bounds(results), barBox = Bounds(bar);
        Console.WriteLine($"SMOKE archive narrow status overlay list: overlay={box}, results={resultBox}, pager={barBox}, list={listBox}, lastRowProbe={lastRow}");
        Require(overlay.IsEffectivelyVisible && Inside(resultBox, box) && box.Bottom <= barBox.Top + 1 && !box.Intersects(barBox),
            $"The narrow archive status overlay is not shown inside the stacked list above its docked pager: overlay={box}, results={resultBox}, pager={barBox}.");
        Program.CaptureWindow(window, "smoke-archive-status-overlay-narrow-list.png");
        Show(string.Empty);
        Require(!overlay.IsVisible, "The emptied narrow status overlay is still shown.");
        await RequireHitAsync(() => Hits(box.Center, list) && Hits(lastRow, list), box.Center,
            "The emptied narrow status overlay still intercepts pointer input on the list.");
        Program.CaptureWindow(window, "smoke-archive-status-overlay-narrow-list-empty.png");
        await page.LoadThreadAsync(id);
        await Program.WaitUntilAsync(() => { window.UpdateLayout(); return !page.IsBusy && readerContent.IsEffectivelyVisible; });
    }
    private static async Task CheckSlowMediaAsync(MainWindow window, ResourceId id)
    {
        ContentControl host = window.FindControl<ContentControl>("ArchivePageHost")!;
        object? retained = host.Content;
        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArchivePage fixture = new ArchivePage(window.SessionHost.OpenArchiveReaderAsync,
            window.SessionHost.SetCaptureEnabledAsync, _ => { }, async (directory, cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                return await MediaAssetReader.OpenAsync(directory, cancellationToken);
            });
        host.Content = fixture;
        try
        {
            fixture.IsAvailable = true;
            await fixture.LoadThreadAsync(id).WaitAsync(TimeSpan.FromSeconds(10));
            Require(fixture.CurrentThread?.PostCount == 1 && !fixture.IsBusy && fixture.IsMediaLoading
                && !fixture.MediaLoadTask.IsCompleted,
                "A delayed media directory blocked local posts or left local navigation busy.");
            await FlushAsync();
            Program.CaptureWindow(window, "smoke-archive-slow-media.png");
            Require(!fixture.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .Any(view => view.Thumbnail.Source is Bitmap), "The delayed media fixture displayed stale media.");
            gate.SetResult();
            await fixture.MediaLoadTask.WaitAsync(TimeSpan.FromSeconds(10));
            Require(fixture.FindControl<DataGrid>("ArchiveMediaGrid")!.ItemsSource.Cast<ArchiveAssetRow>().Count() == 1,
                "The delayed media snapshot did not populate after the local reader.");
            await Program.WaitUntilAsync(() => fixture.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .Any(view => view.Thumbnail.Source is Bitmap));
        }
        finally
        {
            ArchiveAttachmentView[] detached = fixture.GetVisualDescendants().OfType<ArchiveAttachmentView>().ToArray();
            fixture.EndLifetime();
            gate.TrySetResult();
            host.Content = retained;
            Require(detached.All(view => view.Thumbnail.Source == null), "Detached posts retained image buffers.");
        }
    }

    private static void OpenArchive(MainWindow window) => window.FindControl<MenuItem>("ArchiveMenuItem")!
        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static async Task CheckGalleryLanguageFocusAsync(MainWindow window, GalleryViewer viewer)
    {
        string originalLanguage = Texts.Language;
        try
        {
            foreach (string language in new[] { originalLanguage == "de" ? "en" : "de", originalLanguage })
            {
                Button read = GalleryControl<Button>(viewer, "GalleryReadPost");
                Require(read.Focus() && read.IsKeyboardFocusWithin,
                    "Cannot establish context-action focus in the real shell gallery.");
                long builds = viewer.ContextBuildCount;
                Texts.SetLanguage(language);
                await window.ArchivePage.ReaderLocalizationTask;
                await FlushAsync();
                Require(viewer.ContextBuildCount == builds + 1,
                    $"Shell gallery localization expected one context rebuild, observed {viewer.ContextBuildCount - builds}.");
                var focused = window.FocusManager?.GetFocusedElement();
                Require(GalleryControl<Button>(viewer, "GalleryReadPost").IsKeyboardFocusWithin,
                    "Shell gallery localization moved context focus to "
                    + (focused is Control control ? control.GetType().Name + "#" + control.Name : focused?.GetType().Name ?? "none"));
                Require(window.ArchivePage.IsObscuredByGallery,
                    "Language refresh changed real gallery coverage of its reader origin.");
            }
        }
        finally
        {
            Texts.SetLanguage(originalLanguage);
            await window.ArchivePage.ReaderLocalizationTask;
        }
    }

    private static async Task FlushAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static bool Fits(Window window, Control control)
    {
        Point? point = control.TranslatePoint(new Point(0, 0), window);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && point.HasValue
            && point.Value.X >= -1 && point.Value.Y >= -1 && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static T GalleryControl<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task CheckArchiveColumnsAsync(ArchivePage page)
    {
        string oldMode = page.ArchiveColumnModePreference;
        string oldLayout = page.ArchiveLayoutPreference;
        var oldVisibility = page.ArchiveColumnVisibilityPreference.ToDictionary(pair => pair.Key, pair => pair.Value);
        var oldWidths = page.ArchiveColumnWidthsPreference.ToDictionary(pair => pair.Key, pair => pair.Value);
        var oldOrder = page.ArchiveColumnOrderPreference.ToDictionary(pair => pair.Key, pair => pair.Value);
        var saveVisibility = page.SaveArchiveColumnVisibilityAsync;
        var saveWidths = page.SaveArchiveColumnWidthsAsync;
        var saveOrder = page.SaveArchiveColumnOrderAsync;
        try
        {
            page.SaveArchiveColumnVisibilityAsync = _ => Task.CompletedTask;
            page.SaveArchiveColumnWidthsAsync = _ => Task.CompletedTask;
            page.SaveArchiveColumnOrderAsync = _ => Task.CompletedTask;
            DataGrid threads = page.FindControl<DataGrid>("ArchiveThreadTable")!;
            DataGrid media = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
            page.ArchiveColumnVisibilityPreference = new Dictionary<string, bool>();
            page.ArchiveColumnModePreference = "Barebones";
            Require(threads.Columns[0].IsVisible && threads.Columns[1].IsVisible && threads.Columns[2].IsVisible
                && threads.Columns[3].IsVisible && !threads.Columns[4].IsVisible && !threads.Columns[5].IsVisible
                && !threads.Columns[6].IsVisible && !threads.Columns[7].IsVisible && media.Columns[0].IsVisible && !media.Columns[2].IsVisible,
                "Barebones archive table did not use compact, identifying default columns.");
            page.ArchiveColumnVisibilityPreference = new Dictionary<string, bool>
            {
                ["ArchiveThreadTable.identity"] = false,
                ["ArchiveThreadTable.title"] = false,
                ["ArchiveMediaGrid.name"] = false,
                ["ArchiveThreadTable.work"] = true
            };
            Require(threads.Columns[0].IsVisible && media.Columns[0].IsVisible && threads.Columns[5].IsVisible,
                "Archive column preferences hid every identifier or ignored a custom selection.");
            page.ArchiveColumnModePreference = "Advanced";
            Require(threads.Columns[5].IsVisible && threads.Columns[6].IsVisible && threads.Columns[7].IsVisible && media.Columns[2].IsVisible,
                "Advanced archive defaults did not restore richer columns.");
            // Tags is chooser-only in Simple: hidden by default, shown once chosen.
            page.ArchiveColumnVisibilityPreference = new Dictionary<string, bool>();
            page.ArchiveColumnModePreference = "Simple";
            Require(!threads.Columns[7].IsVisible, "The tag column must be hidden by default in Simple.");
            page.ArchiveColumnVisibilityPreference = new Dictionary<string, bool> { ["ArchiveThreadTable.tags"] = true };
            Require(threads.Columns[7].IsVisible, "The tag column could not be chosen in Simple.");
            page.ArchiveColumnVisibilityPreference = new Dictionary<string, bool>();
            page.ArchiveColumnModePreference = "Advanced";
            page.ArchiveColumnOrderPreference = new Dictionary<string, int>
            {
                ["ArchiveThreadTable.title"] = 0,
                ["ArchiveThreadTable.identity"] = 1
            };
            Require(threads.Columns[1].DisplayIndex == 0 && threads.Columns[0].DisplayIndex == 1,
                "Archive column order did not apply semantic preferences.");
            Button editor = page.FindControl<Button>("ArchiveThreadColumnsButton")!;
            page.ArchiveLayoutPreference = "Table";
            Require(editor.IsVisible, "The thread table has no visible Columns editor.");
            Require(editor.IsEnabled && editor.Command?.CanExecute(editor.CommandParameter) == true,
                "The thread Columns editor command is unavailable.");
            // This toolbar action uses ICommand; a routed Click bypasses Button.OnClick dispatch.
            SmokeActionClick.Execute(editor);
            ContextMenu menu = editor.ContextMenu
                ?? throw new InvalidOperationException("The thread Columns command did not create its menu.");
            Require(menu.IsOpen, "The thread Columns command did not open its menu.");
            Require(menu.Items.OfType<MenuItem>().Count() == 9 &&
                menu.Items.OfType<MenuItem>().First().ToggleType == MenuItemToggleType.CheckBox,
                "The thread Columns editor lacks keyboard-friendly checked items and reset.");
            MenuItem reset = menu.Items.OfType<MenuItem>().Last();
            reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => !page.ArchiveColumnVisibilityPreference.Keys.Any(
                key => key.StartsWith("ArchiveThreadTable.", StringComparison.Ordinal)));
            Require(threads.Columns[0].IsVisible && threads.Columns[1].IsVisible,
                "Reset did not restore identifying archive columns.");
        }
        finally
        {
            page.ArchiveColumnModePreference = oldMode;
            page.ArchiveColumnVisibilityPreference = oldVisibility;
            page.ArchiveColumnWidthsPreference = oldWidths;
            page.ArchiveColumnOrderPreference = oldOrder;
            page.ArchiveLayoutPreference = oldLayout;
            page.SaveArchiveColumnVisibilityAsync = saveVisibility;
            page.SaveArchiveColumnWidthsAsync = saveWidths;
            page.SaveArchiveColumnOrderAsync = saveOrder;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
