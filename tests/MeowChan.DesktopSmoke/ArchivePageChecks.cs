using Avalonia;
using Avalonia.Controls;
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
        await Program.WaitUntilAsync(() => page.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .Any(view => view.Thumbnail.Source is Bitmap));
        ArchiveAttachmentView inline = page.GetVisualDescendants().OfType<ArchiveAttachmentView>().Single();
        Require(inline.Thumbnail.Bounds.Width <= 240 && inline.Thumbnail.Bounds.Height <= 180,
            "An inline image exceeded its bounded thumbnail dimensions.");
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
        Click(GalleryControl<Button>(inlineViewer, "GalleryToggleContext"));
        // The hidden context ScrollViewer applies its content template on the next layout pass.
        await Program.WaitUntilAsync(() => inlineViewer.GetVisualDescendants().OfType<Button>()
            .Any(button => button.Name == "GalleryReadPost"));
        Click(GalleryControl<Button>(inlineViewer, "GalleryReadPost"));
        await Program.WaitUntilAsync(() => !gallery.IsVisible && !page.IsBusy && page.CurrentThread?.Summary.Id == id);
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
        Button focusOrigin = page.FindControl<Button>("ArchiveReadPosts")!;
        Task supersededSearch = window.OpenSearchGalleryAsync(new ArchiveThreadQuery(), origin: focusOrigin);
        await window.OpenGalleryAsync(id, origin: focusOrigin);
        GalleryViewer replacementViewer = (GalleryViewer)gallery.Content!;
        await supersededSearch;
        Require(ReferenceEquals(gallery.Content, replacementViewer), "A superseded search replaced the current gallery.");
        Require(GalleryControl<TextBlock>(replacementViewer, "GalleryScope").Text!.StartsWith("Thread"),
            "A superseded search changed the current thread scope.");
        Click(GalleryControl<Button>(replacementViewer, "GalleryClose"));
        Require(ReferenceEquals(window.FocusManager!.GetFocusedElement(), focusOrigin), "Gallery close did not restore origin focus.");

        // A captured self-quote exercises the real quote/reader path without changing source data.
        CapturedPost quotePost = page.CurrentThread!.GetPosts().Single();
        ArchivePostId quoteId = new(id, quotePost.PostId);
        await page.NavigateQuoteAsync(new ArchiveQuoteRow(new ArchiveQuote(quoteId, quoteId, ">>" + quotePost.PostId, null)));
        await page.MediaLoadTask;
        Require(page.FindControl<Button>("ArchiveHistoryBack")!.IsVisible, "Quote navigation did not create a reader history entry.");
        Button backlinks = page.GetVisualDescendants().OfType<Button>().First(button => button.DataContext is ArchivePostRow
            && button.Content?.ToString() == ((ArchivePostRow)button.DataContext).BacklinksLabel);
        Click(backlinks);
        await Program.WaitUntilAsync(() => page.FindControl<TextBlock>("ArchiveReferencesTitle")!.Text
            == Texts.T("archive.noBacklinks", quotePost.PostId));
        string? referenceTitle = page.FindControl<TextBlock>("ArchiveReferencesTitle")!.Text;
        string? referencePage = page.FindControl<TextBlock>("ArchiveReferencesPage")!.Text;
        ArchiveAssetRow quoteAsset = media.ItemsSource.Cast<ArchiveAssetRow>().Single();
        GalleryEntry quotedEntry = new(new GalleryAssetKey(id, quoteAsset.Manifest.Directory, quoteAsset.Asset.AssetId),
            quoteAsset.Manifest, quoteAsset.Asset,
            quotePost with { Html = "<a href='#p" + quotePost.PostId + "'>&gt;&gt;" + quotePost.PostId + "</a>" },
            null, false, false) { ThreadUrl = page.CurrentThread!.Summary.Url };
        await window.OpenMediaGalleryAsync(new[] { quotedEntry }, quotedEntry.EntryKey, focusOrigin);
        GalleryViewer quoteViewer = (GalleryViewer)gallery.Content!;
        Click(GalleryControl<Button>(quoteViewer, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => quoteViewer.GetVisualDescendants().OfType<Button>()
            .Any(button => button.Name == "GalleryQuote"));
        Click(GalleryControl<Button>(quoteViewer, "GalleryQuote"));
        await Program.WaitUntilAsync(() => !gallery.IsVisible && page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex == 0);
        Click(page.FindControl<Button>("ArchiveGalleryReturn")!);
        Click(GalleryControl<Button>(quoteViewer, "GalleryClose"));
        Require(page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex == 2,
            "Closing after a quote excursion lost the original references tab.");
        Require(page.FindControl<TextBlock>("ArchiveReferencesTitle")!.Text == referenceTitle
            && page.FindControl<TextBlock>("ArchiveReferencesPage")!.Text == referencePage,
            "Closing after a quote excursion lost the original reference panel state.");
        Require(!page.FindControl<Button>("PreviousReferences")!.IsEnabled && !page.FindControl<Button>("NextReferences")!.IsEnabled,
            "Restored empty reference panel has incoherent paging actions.");
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
                    return Math.Abs(window.Bounds.Width - width) <= 1 && Math.Abs(window.Bounds.Height - height) <= 1
                        && Fits(window, page.FindControl<Button>("CapturePostsButton")!)
                        && Fits(window, page.FindControl<Button>("ArchiveBackButton")!)
                        && page.FindControl<ListBox>("ArchivePosts")!.Bounds.Height >= 100
                        && Fits(window, window.FindControl<Button>("SettingsNavigationButton")!)
                        && Fits(window, window.FindControl<ComboBox>("WorkspaceModeComboBox")!);
                });
                await Program.WaitUntilAsync(() => page.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                    .Any(view => view.Thumbnail.Source is Bitmap));
                Program.CaptureWindow(window, $"smoke-archive-{mode.ToLowerInvariant()}-{language}-minimum.png");
            }
        }
        Texts.SetLanguage("en");
        Click(window.FindControl<Button>("ThemeToggleButton")!);
        await Program.WaitUntilAsync(() => Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            && window.FindControl<Button>("ThemeToggleButton")!.IsEnabled);
        Program.CaptureWindow(window, "smoke-archive-dark-compact.png");
        await ArchiveExportChecks.RunAsync(window, page.CurrentThread!);
        await CheckSlowMediaAsync(window, id);
        await ArchiveNavigationChecks.RunAsync(window);
        await GalleryViewerChecks.RunAsync(window);
        Console.WriteLine("SMOKE archive: real post capture, restart, watch removal, plain offline reader, verified image preview, independent media loading and compact EN/DE pages passed.");
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
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
