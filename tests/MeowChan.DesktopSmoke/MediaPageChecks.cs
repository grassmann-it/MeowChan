using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_MEDIA_PAGE_ONLY. Real host, catalog, bound manifests, verified PNG
// thumbnails and the real navigation button. Managed input only; no Windows/macOS acceptance.
internal static partial class MediaPageChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
    // Files named dup-* share one content on purpose; every other file gets its own bytes (a trailing note after the PNG data),
    // because the page merges equal bytes into one tile.
    // snd-*, mute-* and odd-* are real MP4 bytes with sound, real MP4 bytes without, and bytes no probe can decide.
    // The WebM path uses the checked-in decoder fixture; the odd case stays deliberately undecodable.
    private static byte[] BytesOf(string name) => name.StartsWith("snd-", StringComparison.Ordinal) ? SoundMp4.Value
        : name.StartsWith("mute-", StringComparison.Ordinal) ? SilentMp4.Value
        : name.StartsWith("odd-", StringComparison.Ordinal) ? [.. UndecodableVideoBytes, .. System.Text.Encoding.UTF8.GetBytes(name)]
        : name.EndsWith(".webm", StringComparison.Ordinal) ? VideoBytes : name.StartsWith("dup-", StringComparison.Ordinal) ? Png : [.. Png, .. System.Text.Encoding.UTF8.GetBytes(name)];

    private static readonly Lazy<byte[]> SoundMp4 = new(() => File.ReadAllBytes(FixturePath("tests/Fixtures/release102/playback-h264-audio.mp4")));
    private static readonly Lazy<byte[]> SilentMp4 = new(() => File.ReadAllBytes(FixturePath("tests/Fixtures/release102/playback-h264-silent.mp4")));

    private static string FixturePath(string relative)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (DirectoryInfo? parent = new(start); parent != null; parent = parent.Parent)
                if (File.Exists(Path.Combine(parent.FullName, relative))) return Path.Combine(parent.FullName, relative);
        throw new FileNotFoundException("Required checked-in synthetic fixture is absent: " + relative);
    }

    private static readonly ResourceId A = new("media-fixture", "g", "100");
    private static readonly ResourceId B = new("media-fixture", "g", "200");

    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("media-page-native-");
        try
        {
            Texts.SetLanguage("en");
            new Config().SaveConfig(profile);
            await SeedAsync(profile);
            await PageJourneyAsync(owner, profile);
            string panelProfile = TemporaryWorkspacePaths.CreateDirectory("media-page-panel-");
            new Config().SaveConfig(panelProfile);
            await SeedAsync(panelProfile);
            await PanelFollowsSelectionAsync(owner, panelProfile); // the media panel follows the selection (MediaPageChecks.Panel.cs)
            await RatingsManagerEntryAsync(owner, profile);
            await FilterJourneyAsync(owner, profile);
            await BackgroundPresentRacesAsync(owner, TemporaryWorkspacePaths.CreateDirectory("media-page-present-races-"));
            await FilterSurfaceAsync(owner, TemporaryWorkspacePaths.CreateDirectory("media-page-filter-surface-"));
            await SortToolbarAsync(owner, TemporaryWorkspacePaths.CreateDirectory("media-page-sort-toolbar-"));
            await Cx03MediaFilterClosureAsync(owner);
            await OwnStateJourneyAsync(owner, profile);
            await DuplicateJourneyAsync(owner, TemporaryWorkspacePaths.CreateDirectory("media-page-duplicates-"));
            await SelectionJourneyAsync(owner, TemporaryWorkspacePaths.CreateDirectory("media-page-selection-"));
            await NavigationAsync(owner);
            await HostPathsAsync(owner, TemporaryWorkspacePaths.CreateDirectory("media-page-host-"));
            await ReturnPathAsync(owner, TemporaryWorkspacePaths.CreateDirectory("media-page-return-"));
            await GalleryReturnPathAsync(owner, TemporaryWorkspacePaths.CreateDirectory("media-gallery-return-"));
            string videoProfile = TemporaryWorkspacePaths.CreateDirectory("media-video-native-");
            new Config().SaveConfig(videoProfile);
            await SeedVideosAsync(videoProfile);
            await VideoJourneyAsync(owner, videoProfile);
            string audioProfile = TemporaryWorkspacePaths.CreateDirectory("media-audio-native-");
            new Config().SaveConfig(audioProfile);
            await SeedAudioAsync(audioProfile);
            await AudioJourneyAsync(owner, audioProfile);
            Console.WriteLine("SMOKE media sound [en]: three videos (real MP4 with sound, real MP4 without, bytes no probe can decide) are probed in the background from their headers; 'with sound' shows exactly one, 'without sound' exactly one, the undecidable one is counted as not checked and never shown or called silent; a sound choice sets the type to videos and another type drops it; reset clears it; results are cached and a second page starts from the cache.");
            Console.WriteLine("SMOKE media videos [en]: a video tile shows the local reader poster (cache keyed by verified bytes), also when it is a grouped tile; nothing fetched.");
            Console.WriteLine("SMOKE media page [en]: index build, saved/missing toggle, three orders, verified thumbnail, gallery window, show in thread, cache reuse, own state on tiles (favorite, rating, media vs thread tags, unknown wording, tag edit reload, language switch), tag chips on tiles (media and thread tag, named, fit the cell, Enter and click filter by the tag, focus back on the grid), 'N without media tags' status link (counts bound and loose files, applies the filter, removable chip, focus on the grid, thread tags do not count but the thread-tag filter still matches, combines with type, reset), filters (thread and media tags all/any/exclude, without media tags, favorites, rating, type, name, source, board, thread chip, reset, deleted tag refused), duplicates (same bytes in three threads: one tile, 'in 3 threads' hint, details with three occurrences and their own tags, jump into a thread, filter matches by one occurrence, gallery gets one entry per tile), selection (click, Ctrl+click, Shift+click, Ctrl+A, Esc, check mark on selected tiles, three tiles tagged and untagged with one occurrence of a duplicate deselected, gallery with exactly the selection, export of exactly their originals, selection kept or dropped by filters and order, actions disabled while building), export all (exactly the originals of the tiles the filter shows, independent of the selection; question above a lowered limit with confirm, cancel, Esc and dropped by a filter change; disabled with a reason without tiles and while building), navigation button in Simple and Tools menu in Barebones checked.");
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task PageJourneyAsync(MainWindow owner, string profile)
    {
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        string cache = Path.Combine(profile, "cache", MediaLibraryIndexCache.FileName);
        List<(IReadOnlyList<MediaLibraryEntry> Window, int Start)> opened = new();
        List<MediaLibraryEntry> threads = new();
        MediaPage page = new(host.OpenArchiveReaderAsync, cachePath: cache)
        {
            OpenGallery = (window, start) => { opened.Add((window, start)); return Task.CompletedTask; },
            ShowInThread = entry => { threads.Add(entry); return Task.CompletedTask; }
        };
        Window window = new() { Width = 1100, Height = 800, Content = page, Title = "Synthetic media page", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Snapshot is { Complete: true } && page.Snapshot.Threads.Count == 2, "Index did not cover both threads.");
            Require(page.ShownEntries.Count == 3 && page.ShownEntries.All(entry => entry.Presence == MediaLibraryPresence.Present),
                "Default view must show exactly the three saved files.");
            // By thread: the newer thread B first, then A's post file, then A's file without a post.
            Require(page.ShownEntries.Select(entry => entry.Thread.Thread + ":" + (entry.PostId ?? "-")).SequenceEqual(new[] { "200:200", "100:100", "100:-" }),
                "By-thread order is wrong: " + string.Join(",", page.ShownEntries.Select(entry => entry.Thread.Thread + ":" + entry.PostId)));
            TextBlock status = C<TextBlock>(page, "MediaStatus");
            Require(status.Text == Texts.T("media.count", 3, 2), "Status is wrong: " + status.Text);

            CheckBox missing = C<CheckBox>(page, "MediaShowMissing");
            missing.IsChecked = true;
            Require(page.ShownEntries.Count == 4 && status.Text == Texts.T("media.countMissing", 4, 2, 1), "Missing toggle failed: " + status.Text);
            missing.IsChecked = false;

            ComboBox sort = C<ComboBox>(page, "MediaSort");
            Button shuffle = C<Button>(page, "MediaShuffle");
            sort.SelectedIndex = 2;
            Require(shuffle.IsVisible && page.ShownEntries.Count == 3, "Random order did not offer a shuffle.");
            MediaLibraryEntry[] random = page.ShownEntries.ToArray();
            sort.SelectedIndex = 1;
            Require(!shuffle.IsVisible && page.ShownEntries.Count == 3, "Newest order lost entries.");
            sort.SelectedIndex = 2;
            Require(page.ShownEntries.SequenceEqual(random), "Random order reshuffled without asking.");
            sort.SelectedIndex = 0;

            // A real verified thumbnail appears for a visible saved image.
            await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>()
                .Any(tile => tile.GetVisualDescendants().OfType<Image>().Any(image => image.Source != null)));

            ListBox grid = page.Grid;
            grid.SelectedIndex = 1; // A's post file
            grid.Focus();
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            grid.RaiseEvent(enter);
            await UntilAsync(() => opened.Count == 1);
            (IReadOnlyList<MediaLibraryEntry> gallery, int start) = opened[0];
            Require(enter.Handled && gallery.Count == 3 && gallery[start] == page.ShownEntries[1], "Gallery window does not start at the chosen tile.");

            await page.ShowSelectedInThreadAsync();
            Require(threads.Count == 1 && threads[0].Thread == A && threads[0].PostId == "100", "Show in thread got the wrong post.");
            grid.SelectedIndex = 2; // file without a post: nothing to show in a thread
            await page.ShowSelectedInThreadAsync();
            Require(threads.Count == 1, "A file without a post must not jump into a thread.");
            Require(File.Exists(cache), "The index cache was not written.");
        }
        finally { page.EndLifetime(); window.Close(); }

        // A new page reads the cached index in the background (the UI thread is not blocked and says so), shows it, and
        // does not write the unchanged index back.
        MediaPage again = new(host.OpenArchiveReaderAsync, cachePath: cache);
        Window second = new() { Width = 900, Height = 700, Content = again, ShowInTaskbar = false };
        try
        {
            second.Show(owner);
            DateTime written = File.GetLastWriteTimeUtc(cache);
            Task update = again.ActivateAsync();
            Require(again.LoadingCache && again.Snapshot == null && C<TextBlock>(again, "MediaStatus").Text!.Contains(Texts.T("media.cache.loading")),
                "The cached index was not loading in the background: " + C<TextBlock>(again, "MediaStatus").Text);
            await UntilAsync(() => again.Snapshot != null);
            Require(again.ShownEntries.Count == 3, "Cached index was not shown.");
            await update.WaitAsync(Deadline);
            Require(again.CacheFile is { Writes: 0, SkippedWrites: 1 } && File.GetLastWriteTimeUtc(cache) == written,
                "The unchanged index was written again.");
        }
        finally { again.EndLifetime(); second.Close(); }
    }

    private static async Task FilterJourneyAsync(MainWindow owner, string profile)
    {
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        MediaPage page = new(host.OpenArchiveReaderAsync) { ReadUserData = () => UserData(host) };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media filters", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.ShownEntries.Count == 3, "Filter journey starts without the three saved files.");
            MediaLibraryEntry one = page.ShownEntries.Single(entry => entry.PostId == "100");
            MediaLibraryEntry two = page.ShownEntries.Single(entry => entry.PostId == "200");

            // T1: before any tag the status line counts all three files as untagged (bound and loose alike). The link applies
            // "without media tags" on top of the other filters, hides itself, hands the keyboard to the tiles and is removable as a chip.
            Button untagged = C<Button>(page, "MediaUntaggedCount");
            CheckBox withoutTags = C<CheckBox>(page, "MediaWithoutMediaTags");
            Require(untagged.IsVisible && (untagged.Content as TextBlock)?.Text == Texts.T("media.untagged.count", 3)
                && Equals(ToolTip.GetTip(untagged), Texts.T("media.untagged.hint", 3))
                && C<TextBlock>(page, "MediaStatus").Text == Texts.T("media.count", 3, 2), "The status line must offer the three untagged files.");
            Program.CaptureWindow(window, "smoke-media-untagged-link.png");
            Require(untagged.Focus(), "The untagged count link must take keyboard focus.");
            untagged.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(withoutTags.IsChecked == true && page.ShownEntries.Count == 3 && !untagged.IsVisible && page.FilterChips.Children.Count == 1
                && C<TextBlock>(page, "MediaStatus").Text == Texts.T("media.filter.count", 3, 3), "The untagged link did not apply its filter.");
            await UntilAsync(() => window.FocusManager?.GetFocusedElement() is Visual focused && focused.GetSelfAndVisualAncestors().Contains(page.Grid));
            RemoveChip(page.FilterChips, 0);
            Require(withoutTags.IsChecked == false && untagged.IsVisible && !page.FilterChips.IsVisible, "Removing the untagged chip did not widen back.");

            // Real stores: a thread tag on A, a media tag on B's file, a favorite on A's file, a rating on B's file.
            PrivateTagCommit rpt = await host.DefinePrivateTagAsync("rpt").WaitAsync(Deadline);
            PrivateTagCommit cute = await host.DefinePrivateTagAsync("cute").WaitAsync(Deadline);
            await host.SetThreadTagAsync(rpt.Tag, A, true).WaitAsync(Deadline);
            // A thread tag is no media tag: the files of the tagged thread still wait for their own tags.
            page.RefreshUserData();
            Require((untagged.Content as TextBlock)?.Text == Texts.T("media.untagged.count", 3), "A thread tag must not count as a media tag.");
            await host.SetMediaTagAsync(cute.Tag, MediaLibraryFilterEngine.TagTarget(two)!, true).WaitAsync(Deadline);
            Require(await host.SetFavoriteAsync(new FavoriteEntry(MediaLibraryFilterEngine.AssetKey(one)!, one.Length!.Value, one.Name), true).WaitAsync(Deadline),
                "Favorite was not stored.");
            await host.SetMediaRatingAsync(MediaLibraryFilterEngine.AssetKey(two)!, 4).WaitAsync(Deadline);
            page.RefreshUserData();
            // Only B's file has a media tag; A's two files (one in a tagged thread, one loose) are left, also together with the type
            // filter. Both at once: the thread-tag filter still matches those untagged files.
            Require((untagged.Content as TextBlock)?.Text == Texts.T("media.untagged.count", 2), "Only the media tag must remove a file from the count.");
            C<ComboBox>(page, "MediaKindFilter").SelectedIndex = (int)MediaLibraryKindFilter.Image;
            withoutTags.IsChecked = true;
            string Untagged() => string.Join(",", page.ShownEntries.Select(entry => entry.Thread.Thread + ":" + (entry.PostId ?? "-")));
            Require(Untagged() == "100:100,100:-", "Without media tags and the type filter must combine: " + Untagged());
            page.FilterToTag(rpt.Tag.Value);
            // C3: type, without media tags and the tag are three chips now (the type had none before).
            Require(Untagged() == "100:100,100:-" && page.FilterChips.Children.Count == 3, "The thread tag filter must still match untagged media: " + Untagged());
            page.ResetFilters();
            Require(withoutTags.IsChecked == false && page.ShownEntries.Count == 3 && untagged.IsVisible, "Reset did not clear without media tags.");

            MeowChan.Desktop.Controls.TwoWayToggle mode = C<MeowChan.Desktop.Controls.TwoWayToggle>(page, "MediaTagMode");
            MeowChan.Desktop.Controls.TagFilterPicker addTag = C<MeowChan.Desktop.Controls.TagFilterPicker>(page, "MediaTagAdd");
            WrapPanel chips = page.FilterChips;
            Require(page.AddableTagNames.SequenceEqual(new[] { "cute", "rpt" }) && addTag.IsEnabled, "The tag popover must offer both tags: " + string.Join(",", page.AddableTagNames));
            void Include(string name) => Require(page.AddTagFilterByName(name, include: true) == null, "Including " + name + " failed.");
            void Exclude(string name) => Require(page.AddTagFilterByName(name, include: false) == null, "Excluding " + name + " failed.");
            string Shown() => string.Join(",", page.ShownEntries.Select(entry => entry.Thread.Thread + ":" + (entry.PostId ?? "-")));

            // A thread tag reaches every file of the thread, the loose file included.
            Include("rpt");
            Require(Shown() == "100:100,100:-" && chips.Children.Count == 1 && page.ActiveFilterCount > 0, "Thread tag filter failed: " + Shown());
            Require(C<TextBlock>(page, "MediaStatus").Text == Texts.T("media.filter.count", 2, 3), "Filtered count is wrong.");
            Include("cute");
            Require(page.ShownEntries.Count == 0 && C<TextBlock>(page, "MediaEmpty").IsVisible
                && C<TextBlock>(page, "MediaEmpty").Text == Texts.T("media.filter.noMatch"), "All-of two tags must match nothing: " + Shown());
            mode.SelectedIndex = 1;
            Require(page.ShownEntries.Count == 3 && chips.Children.Count == 3, "At-least-one must match all three: " + Shown());
            page.ResetFilters();
            Require(page.ShownEntries.Count == 3 && !chips.IsVisible && page.ActiveFilterCount == 0 && mode.SelectedIndex == 0, "Reset did not clear the tag filter.");
            Exclude("cute");
            Require(Shown() == "100:100,100:-", "Excluding a media tag must hide only that file: " + Shown());
            // S1/M2: a tag chip carries its state and an explicit toggle; toggling moves the tag to the other list and filters again.
            Ursa.Controls.ClosableTag cuteChip() => page.FilterChips.Children.OfType<Ursa.Controls.ClosableTag>().Single();
            Require(cuteChip().Classes.Contains("chip-exclude") && ArchivePage.FilterTagLabel(cuteChip()) == Texts.T("tags.filter.excludeChip", "cute"), "An excluded tag chip must say so.");
            Require(page.ToggleTagFilterByName("cute") == null && page.ShownEntries.Count == 1 && !cuteChip().Classes.Contains("chip-exclude")
                && ArchivePage.FilterTagLabel(cuteChip()) == Texts.T("tags.filter.includeChip", "cute"), "Toggling an excluded tag must include it: " + Shown());
            Require(page.ToggleTagFilterByName("cute") == null && Shown() == "100:100,100:-" && cuteChip().Classes.Contains("chip-exclude"), "Toggling back must exclude it again: " + Shown());
            page.ResetFilters();

            // Favorites, rating, and both together.
            CheckBox favorites = C<CheckBox>(page, "MediaFavoritesOnly");
            ComboBox rating = C<ComboBox>(page, "MediaMinRating");
            favorites.IsChecked = true;
            Require(Shown() == "100:100", "Favorites filter failed: " + Shown());
            rating.SelectedIndex = 4;
            Require(page.ShownEntries.Count == 0, "Favorite and rating at least 4 must match nothing.");
            favorites.IsChecked = false;
            Require(Shown() == "200:200", "Rating filter failed: " + Shown());
            rating.SelectedIndex = 5;
            Require(page.ShownEntries.Count == 0, "No file is rated 5.");
            page.ResetFilters();

            // Type, name, source and board.
            ComboBox kind = C<ComboBox>(page, "MediaKindFilter"), source = C<ComboBox>(page, "MediaSourceFilter"), board = C<ComboBox>(page, "MediaBoardFilter");
            kind.SelectedIndex = (int)MediaLibraryKindFilter.Gif;
            Require(page.ShownEntries.Count == 0, "PNG files are not GIFs.");
            kind.SelectedIndex = (int)MediaLibraryKindFilter.Image;
            Require(page.ShownEntries.Count == 3, "Image filter lost files.");
            SearchBox name = C<SearchBox>(page, "MediaNameFilter");
            name.Text = "  TWO ";
            await UntilAsync(() => Shown() == "200:200"); // TextChanged arrives after a dispatcher turn.
            // Over a large index typing waits for the last keystroke: one rebuild for a burst, results unchanged.
            page.ResetFilters();
            page.SearchImmediateEntries = 0;
            int presented = page.PresentCount;
            name.Text = "t"; name.Text = "tw"; name.Text = "two";
            Require(page.PresentCount == presented && name.IsQueryPending, "A burst of keystrokes must not rebuild the grid before the delay.");
            await UntilAsync(() => Shown() == "200:200" && !name.IsQueryPending);
            Require(page.PresentCount == presented + 1, "The burst must rebuild the grid once: " + (page.PresentCount - presented));
            Require(name.ResultText == Texts.Count("search.count", 1), "The field must show the hit count: " + name.ResultText);
            name.ClearAndFocus();
            Require(name.Text == string.Empty && page.ShownEntries.Count == 3 && name.ResultText == null, "Clearing must apply at once.");
            page.SearchImmediateEntries = 2000;
            name.Text = "  TWO ";
            await UntilAsync(() => Shown() == "200:200");
            page.ResetFilters();
            // A large library presents off the UI thread: the grid keeps its rows until the new ones are ready, only the newest of
            // several requests is applied, and the selection is carried over.
            page.BackgroundPresentEntries = 0;
            try
            {
                MediaLibraryTile picked = page.Tiles[1];
                page.Grid.Selection.Select(1);
                int presentedInBackground = page.PresentCount;
                object? rowsBefore = page.Grid.ItemsSource;
                kind.SelectedIndex = (int)MediaLibraryKindFilter.Gif;
                kind.SelectedIndex = (int)MediaLibraryKindFilter.Image;
                Require(page.PresentCount == presentedInBackground && ReferenceEquals(page.Grid.ItemsSource, rowsBefore),
                    "A background presentation must not change the grid before it is ready.");
                await page.PresentTask.WaitAsync(TimeSpan.FromSeconds(30));
                Require(page.PresentCount == presentedInBackground + 1 && page.ShownEntries.Count == 3,
                    "Two requests in a row must apply once, with the newest filter: " + (page.PresentCount - presentedInBackground) + "/" + page.ShownEntries.Count);
                Require(page.SelectedTiles.Count == 1 && MediaLibraryTileKey.Of(page.SelectedTiles[0]) == MediaLibraryTileKey.Of(picked),
                    "The selection must survive a background presentation.");
                kind.SelectedIndex = (int)MediaLibraryKindFilter.Gif;
                await page.PresentTask.WaitAsync(TimeSpan.FromSeconds(30));
                Require(page.ShownEntries.Count == 0 && page.SelectedTiles.Count == 0,
                    "A filter that matches nothing must empty the grid and drop the selection.");
                kind.SelectedIndex = 0;
                await page.PresentTask.WaitAsync(TimeSpan.FromSeconds(30));
                Require(page.ShownEntries.Count == 3, "Clearing the type must show everything again.");
            }
            finally { page.BackgroundPresentEntries = 2000; }
            Require(name.Text == string.Empty && kind.SelectedIndex == 0, "Reset did not clear name and type.");
            Require(source.Items.Count == 2 && source.Items[1]!.ToString() == "media-fixture", "Source choice must list the archived source.");
            Require(board.Items.Count == 2 && board.Items[1]!.ToString() == Texts.T("media.filter.boardOfSource", "g", "media-fixture"),
                "Board choice must name its source while no source is chosen.");
            board.SelectedIndex = 1;
            Require(page.ShownEntries.Count == 3 && page.CurrentFilter().Source == "media-fixture" && page.CurrentFilter().Board == "g",
                "A board alone must filter by its source and board.");
            source.SelectedIndex = 1;
            Require(board.SelectedIndex == 1 && board.Items[1]!.ToString() == "/g/", "Choosing the board's own source must keep the board.");
            page.ResetFilters();

            // The thread as a meta tag: the tile's thread label filters to that thread; its chip removes it.
            Button threadLink = await FindThreadLinkAsync(page, B);
            threadLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Shown() == "200:200" && chips.Children.Count == 1, "Thread meta filter failed: " + Shown());
            RemoveChip(chips, 0);
            Require(page.ShownEntries.Count == 3 && !chips.IsVisible, "Removing the thread chip did not widen back.");

            // A deleted tag in the filter is refused: explanation, empty grid, no silent widening.
            Include("cute");
            Require(Shown() == "200:200", "Media tag filter failed: " + Shown());
            PrivateTagLoadSnapshot before = host.GetPublishedPrivateTagSnapshot();
            await host.DeletePrivateTagAsync(cute.Tag, before.Snapshot!.Revision).WaitAsync(Deadline);
            page.RefreshUserData();
            Require(page.FilterError == Texts.T("media.filter.error.unknownTag") && page.ShownEntries.Count == 0
                && !C<TextBlock>(page, "MediaEmpty").IsVisible, "A deleted tag must refuse the filter with an explanation.");
            // A tag chip is negatable (M2): its content is the switch and the label, read through the one chip model.
            string deletedLabel = chips.Children[0] is Ursa.Controls.ClosableTag deletedChip ? MeowChan.Desktop.Controls.WorkspaceChips.TermChipLabel(deletedChip) : "";
            Require(deletedLabel == Texts.T("tags.filter.includeChip", Texts.T("tags.filter.deleted")),
                "The chip of a deleted tag must say so: " + deletedLabel);
            RemoveChip(chips, 0);
            Require(page.FilterError == null && page.ShownEntries.Count == 3, "Removing the deleted tag did not clear the refusal.");
        }
        finally { page.EndLifetime(); window.Close(); }
    }

    // The same bytes in three threads are one tile; tags stay with each occurrence; a filter matches by one occurrence.
    // OR-02: continues on the stores the filter journey filled (thread tag on A, media tag on B's file, favorite on A's file,
    // rating on B's file). The tiles must show exactly that, keep the facts apart and follow tag changes and the language.
    private static async Task OwnStateJourneyAsync(MainWindow owner, string profile)
    {
        TagId extra;
        {
            await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
            await host.StartAsync().WaitAsync(Deadline);
            // The filter journey deleted its media tag; give the second file a fresh one.
            MediaLibraryEntry fixtureTwo = (await MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync)).Entries.Single(entry => entry.PostId == "200");
            PrivateTagCommit keep = await host.DefinePrivateTagAsync("keep").WaitAsync(Deadline);
            await host.SetMediaTagAsync(keep.Tag, MediaLibraryFilterEngine.TagTarget(fixtureTwo)!, true).WaitAsync(Deadline);
            List<MediaLibraryEntry> edited = new();
            MediaPage page = new(host.OpenArchiveReaderAsync) { ReadUserData = () => UserData(host), EditTags = entry => { edited.Add(entry); return Task.CompletedTask; } };
            Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media state", ShowInTaskbar = false };
            try
            {
                window.Show(owner);
                page.IsAvailable = true;
                await page.ActivateAsync().WaitAsync(Deadline);
                MediaLibraryEntry one = page.ShownEntries.Single(entry => entry.PostId == "100");
                MediaLibraryEntry two = page.ShownEntries.Single(entry => entry.PostId == "200");
                MediaLibraryEntry loose = page.ShownEntries.Single(entry => entry.Unassigned);
                MediaLibraryTileRow Row(MediaLibraryEntry entry) => ((MediaLibraryTileRow[])page.Grid.ItemsSource!).Single(row => row.Entry == entry);

                MediaLibraryEntryState s1 = Row(one).State!, s2 = Row(two).State!, s3 = Row(loose).State!;
                Require(s1.Favorite == MediaLibraryFlag.Yes && s1.UserRating == null && s1.RatingKnown, "Favorite without a rating must stay a favorite without a rating.");
                Require(s1.MediaTags.Count == 0 && s1.ThreadTags.SequenceEqual(new[] { "rpt" }), "A thread tag must show as a thread tag only.");
                Require(s2.Favorite == MediaLibraryFlag.No && s2.UserRating == 4 && s2.MediaTags.SequenceEqual(new[] { "keep" }) && s2.ThreadTags.Count == 0,
                    "Rating and media tag of the second file are wrong.");
                // AP-01b: a loose file is taggable as its own local occurrence; it starts without media tags (none from its thread).
                Require(s3.TagsKnown && s3.CanEditMediaTags && s3.TagSubject is MediaTagSubject.Local && s3.MediaTags.Count == 0
                    && s3.ThreadTags.SequenceEqual(new[] { "rpt" }), "A loose file must be taggable as a local file and start untagged.");
                Require(Row(one).DetailWithState.Contains(Texts.T("command.gallery.favorite.compact")) && Row(two).DetailWithState.Contains("4/5"), "The tile line must show favorite and rating.");

                // T2: own tags are chips beside the thread link; a chip filters by its tag (a thread tag reaches the whole thread),
                // a second chip narrows further, and the chip in the filter row removes it again.
                Button chip = null!;
                await UntilAsync(() => (chip = page.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "MediaTileTag"
                    && button.IsEffectivelyVisible && button.DataContext is MediaLibraryTileRow row && row.Entry == two)!) != null);
                Require(MediaLibraryTileView.ChipLabel(chip).Text == "keep" && Equals(ToolTip.GetTip(chip), Texts.T("media.tile.tagHint", "keep"))
                    && chip.Classes.Contains("chip-own") && chip.Classes.Contains("chip-compact") && !MediaLibraryTileView.ChipThreadMark(chip).IsVisible
                    && Avalonia.Automation.AutomationProperties.GetName(chip) == Texts.T("media.tile.tagHint", "keep"), "The media tag chip is not named.");
                // The chip row shares the tile's cell; nothing is clipped or pushed under the next tile.
                MediaLibraryTileView chipTile = chip.FindAncestorOfType<MediaLibraryTileView>()!;
                Point? chipEnd = chip.TranslatePoint(new Point(chip.Bounds.Width, chip.Bounds.Height), chipTile);
                Require(chipEnd is { } end && end.Y <= chipTile.Bounds.Height && end.X <= chipTile.Bounds.Width
                    && (chipTile.Content as Control)!.DesiredSize.Height <= MediaLibraryTileView.TileHeight, "The tag chip must fit inside its tile: " + chipEnd);
                Program.CaptureWindow(window, "smoke-media-tag-chips.png");
                Require(chip.Focus(), "A tag chip must take keyboard focus.");
                chip.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Require(page.ShownEntries.SequenceEqual(new[] { two }) && page.FilterChips.Children.Count == 1, "Enter on the tag chip did not filter by it.");
                await UntilAsync(() => window.FocusManager?.GetFocusedElement() is Visual focused && focused.GetSelfAndVisualAncestors().Contains(page.Grid));
                RemoveChip(page.FilterChips, 0);
                // S7: Shift+Enter (like Shift+click) on a tile chip hides the media that carry the tag; its filter chip says "Without tag".
                await UntilAsync(() => (chip = page.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "MediaTileTag"
                    && button.IsEffectivelyVisible && button.DataContext is MediaLibraryTileRow row && row.Entry == two)!) != null);
                Require(chip.Focus(), "A tag chip must take keyboard focus for the exclusion.");
                chip.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift });
                Require(!page.ShownEntries.Contains(two) && page.FilterChips.Children.OfType<Ursa.Controls.ClosableTag>().Single().Classes.Contains("chip-exclude"),
                    "Shift+Enter on the tag chip did not exclude its tag.");
                await UntilAsync(() => window.FocusManager?.GetFocusedElement() is Visual focused && focused.GetSelfAndVisualAncestors().Contains(page.Grid));
                RemoveChip(page.FilterChips, 0);
                Button threadTag = null!;
                await UntilAsync(() => (threadTag = page.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "MediaTileTag"
                    && button.IsEffectivelyVisible && button.DataContext is MediaLibraryTileRow row && row.Entry == one)!) != null);
                Require(MediaLibraryTileView.ChipLabel(threadTag).Text == "rpt" && MediaLibraryTileView.ChipThreadMark(threadTag).IsEffectivelyVisible
                    && threadTag.Classes.Contains("chip-thread") && Equals(ToolTip.GetTip(threadTag), Texts.T("media.tile.threadTagHint", "rpt")),
                    "The thread tag must show as a chip as well, with its thread mark.");
                threadTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(page.ShownEntries.Count == 2 && page.ShownEntries.All(entry => entry.Thread == A), "The thread tag chip must reach the whole thread.");
                page.ResetFilters();
                Require(page.ShownEntries.Count == 3, "Reset did not clear the chip filter.");

                // The tile carries the same text as a tooltip and as its automation name.
                MediaLibraryTileView tile = null!;
                await UntilAsync(() => (tile = page.GetVisualDescendants().OfType<MediaLibraryTileView>().FirstOrDefault(view => view.DataContext is MediaLibraryTileRow row && row.Entry == two)!) != null);
                Require(ToolTip.GetTip(tile) is string tip && tip.Contains(Texts.T("media.state.rating", Texts.T("media.state.ratingValue", 4)))
                    && tip.Contains(Texts.T("media.state.mediaTags", "keep")), "Tile tooltip lacks the own state.");

                // Language switch re-words the same facts.
                Texts.SetLanguage("de");
                await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>().Any(view => view.DataContext is MediaLibraryTileRow row && row.Entry == two
                    && ToolTip.GetTip(view) is string de && de.Contains("Deine Bewertung: 4 von 5")));
                Texts.SetLanguage("en");
                await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>().Any(view => view.DataContext is MediaLibraryTileRow row && row.Entry == two
                    && ToolTip.GetTip(view) is string en && en.Contains("Your rating: 4 of 5")));

                // The tag command is offered for bound and loose files alike, each for exactly its own file.
                page.Grid.SelectedItem = Row(loose);
                await page.EditSelectedTagsAsync();
                Require(edited.SequenceEqual(new[] { loose }), "A loose file must open the tag editor for itself.");
                page.Grid.SelectedItem = Row(two);
                await page.EditSelectedTagsAsync();
                Require(edited.SequenceEqual(new[] { loose, two }), "The tag editor got the wrong file.");

                // A tag change through the host command shows at once after a refresh of the own data.
                extra = (await host.DefinePrivateTagAsync("extra").WaitAsync(Deadline)).Tag;
                await host.SetMediaTagAsync(extra, MediaLibraryFilterEngine.TagTarget(two)!, true).WaitAsync(Deadline);
                page.RefreshUserData();
                Require(Row(two).State!.MediaTags.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(new[] { "extra", "keep" }), "The tag change did not show.");
                Require(page.Grid.SelectedItem is MediaLibraryTileRow { Entry: var selected } && selected == two, "Refreshing own data lost the selection.");
            }
            finally { page.EndLifetime(); window.Close(); Texts.SetLanguage("en"); }
        }

        // Reload: a fresh host and page read the same tag, favorite and rating from disk.
        await using DesktopSessionHost reloaded = new(profile, Array.Empty<IImageBoard>());
        await reloaded.StartAsync().WaitAsync(Deadline);
        MediaPage again = new(reloaded.OpenArchiveReaderAsync) { ReadUserData = () => UserData(reloaded) };
        Window second = new() { Width = 1000, Height = 800, Content = again, ShowInTaskbar = false };
        try
        {
            second.Show(owner);
            again.IsAvailable = true;
            await again.ActivateAsync().WaitAsync(Deadline);
            MediaLibraryEntry two = again.ShownEntries.Single(entry => entry.PostId == "200");
            MediaLibraryEntryState state = ((MediaLibraryTileRow[])again.Grid.ItemsSource!).Single(row => row.Entry == two).State!;
            Require(state.MediaTags.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(new[] { "extra", "keep" }) && state.UserRating == 4
                && state.Favorite == MediaLibraryFlag.No, "Reload shows a different state.");
        }
        finally { again.EndLifetime(); second.Close(); }
    }

    private static async Task DuplicateJourneyAsync(MainWindow owner, string profile)
    {
        new Config().SaveConfig(profile);
        await SeedDuplicatesAsync(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        List<(IReadOnlyList<MediaLibraryEntry> Window, int Start)> opened = new();
        List<MediaLibraryEntry> threads = new();
        MediaPage page = new(host.OpenArchiveReaderAsync)
        {
            ReadUserData = () => UserData(host),
            OpenGallery = (window, start) => { opened.Add((window, start)); return Task.CompletedTask; },
            ShowInThread = entry => { threads.Add(entry); return Task.CompletedTask; }
        };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media duplicates", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Snapshot is { Complete: true } && page.Snapshot.Entries.Count == 4, "Duplicate fixture must index four occurrences.");
            Require(page.Tiles.Count == 2 && page.ShownEntries.Count == 2, "Three equal files and one other must give two tiles, got " + page.Tiles.Count);
            MediaLibraryTile group = page.Tiles.Single(tile => tile.IsGroup);
            Require(group.Occurrences.Count == 3 && group.ThreadCount == 3 && group.Occurrences.Select(entry => entry.Thread.Thread).Order().SequenceEqual(new[] { "300", "400", "500" }),
                "The group must know its three occurrences in three threads.");
            Require(C<TextBlock>(page, "MediaStatus").Text == Texts.T("media.count", 2, 3), "Status counts tiles: " + C<TextBlock>(page, "MediaStatus").Text);

            // Tags per occurrence: a media tag on the file in 400, a thread tag on 500, nothing in 300.
            MediaLibraryEntry in300 = group.Occurrences.Single(entry => entry.Thread.Thread == "300");
            MediaLibraryEntry in400 = group.Occurrences.Single(entry => entry.Thread.Thread == "400");
            MediaLibraryEntry in500 = group.Occurrences.Single(entry => entry.Thread.Thread == "500");
            PrivateTagCommit rpt = await host.DefinePrivateTagAsync("rpt").WaitAsync(Deadline);
            PrivateTagCommit cute = await host.DefinePrivateTagAsync("cute").WaitAsync(Deadline);
            await host.SetMediaTagAsync(cute.Tag, MediaLibraryFilterEngine.TagTarget(in400)!, true).WaitAsync(Deadline);
            await host.SetThreadTagAsync(rpt.Tag, in500.Thread, true).WaitAsync(Deadline);
            page.RefreshUserData();
            Require(page.Tiles.Count == 2, "Tagging must not split or merge tiles.");

            // The tile says where else the bytes are; the single file says nothing.
            Button hint = await FindDuplicatesHintAsync(page);
            Require(((hint.Content as TextBlock)?.Text) == Texts.T("media.tile.inThreads", 3), "Hint must read 'in 3 threads'.");
            Require(page.GetVisualDescendants().OfType<Button>().Count(button => button.Name == "MediaTileDuplicates" && button.IsVisible) == 1,
                "Only the group tile may carry the hint.");

            hint.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Window details = page.OccurrencesWindow ?? throw new InvalidOperationException("The hint did not open the occurrences.");
            await UntilAsync(() => details.GetVisualDescendants().OfType<StackPanel>().Count(row => row.Name == "MediaOccurrence") == 3);
            Require(C<TextBlock>(details, "MediaOccurrencesSummary").Text == Texts.T("media.occurrences.summary", 3, 3), "Details summary is wrong.");
            Require(C<TextBlock>(details, "MediaOccurrencesFavorite").Text == Texts.T("media.group.favorite.none"), "Details must state the favorite state of the bytes.");
            string TagsOfThread(string thread) => details.GetVisualDescendants().OfType<StackPanel>()
                .Single(row => row.Name == "MediaOccurrence" && row.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "MediaOccurrenceWhere" && text.Text!.Contains("Dup " + thread)))
                .GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "MediaOccurrenceTags").Text!;
            Require(TagsOfThread("300") == Texts.T("media.occurrence.noTags"), "Occurrence 300 must have no tags: " + TagsOfThread("300"));
            Require(TagsOfThread("400") == Texts.T("media.occurrence.tags", "cute"), "Occurrence 400 must carry only its own media tag: " + TagsOfThread("400"));
            Require(TagsOfThread("500") == Texts.T("media.occurrence.tags", "rpt"), "Occurrence 500 must carry only its thread tag: " + TagsOfThread("500"));

            // Show in thread per occurrence reuses the archive jump and leaves the list.
            Button show = details.GetVisualDescendants().OfType<StackPanel>()
                .Single(row => row.Name == "MediaOccurrence" && row.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "MediaOccurrenceWhere" && text.Text!.Contains("Dup 400")))
                .GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MediaOccurrenceShow");
            show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => threads.Count == 1 && page.OccurrencesWindow == null);
            Require(threads[0].Thread == in400.Thread && threads[0].PostId == in400.PostId, "Show in thread jumped to the wrong occurrence.");

            // A filter matches a group by one occurrence; the tile still lists all of them.
            Require(page.AddTagFilterByName("cute", include: true) == null, "Including cute failed.");
            Require(page.Tiles.Count == 1 && page.Tiles[0].Occurrences.Count == 3 && page.Tiles[0].ThreadCount == 3 && page.Tiles[0].Entry == in400,
                "A media tag on one occurrence must keep the whole group, shown by the matching occurrence.");
            Require(C<TextBlock>(page, "MediaStatus").Text == Texts.T("media.filter.count", 1, 2), "Filtered count is wrong: " + C<TextBlock>(page, "MediaStatus").Text);
            Require(((await FindDuplicatesHintAsync(page)).Content as TextBlock)?.Text == Texts.T("media.tile.inThreads", 3), "The hint counts all occurrences, not only matching ones.");
            page.ResetFilters();

            // Favorite state of the group tile counts all three saved occurrences: unknown/none/some.
            MediaLibraryTile shared = page.Tiles.Single(tile => tile.IsGroup);
            Require(shared.Favorite == MediaLibraryFavoriteState.None, "No favorite yet must read none: " + shared.Favorite);
            Require(await host.SetFavoriteAsync(new FavoriteEntry(MediaLibraryFilterEngine.AssetKey(in400)!, Png.Length, in400.Name), true).WaitAsync(Deadline), "Favorite was not stored.");
            page.RefreshUserData();
            Require(page.Tiles.Single(tile => tile.IsGroup).Favorite == MediaLibraryFavoriteState.Some, "One of three favorites must read some.");
            await UntilAsync(() => page.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "MediaTileFavorite" && text.IsVisible
                && text.Text == Texts.T("media.group.favorite.some")));
            // The panel lays out cells with the tile's own height, so the favorite row is neither clipped nor under the next row.
            MediaTilePanel panel = page.Grid.GetVisualDescendants().OfType<MediaTilePanel>().Single();
            Require(panel.ItemHeight == MediaLibraryTileView.TileHeight, "Medien panel cell height must match the tile: " + panel.ItemHeight);
            TextBlock favoriteRow = page.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "MediaTileFavorite" && text.IsVisible);
            MediaLibraryTileView favoriteTile = favoriteRow.FindAncestorOfType<MediaLibraryTileView>()!;
            Point? rowBottom = favoriteRow.TranslatePoint(new Point(0, favoriteRow.Bounds.Height), favoriteTile);
            Require(rowBottom is { } bottom && bottom.Y <= favoriteTile.Bounds.Height && favoriteTile.Bounds.Height <= panel.ItemHeight,
                "Favorite row must fit inside its tile and the tile inside its cell: " + rowBottom + " / " + favoriteTile.Bounds.Height);
            Require(await host.SetFavoriteAsync(new FavoriteEntry(MediaLibraryFilterEngine.AssetKey(in400)!, Png.Length, in400.Name), false).WaitAsync(Deadline), "Favorite was not removed.");
            page.RefreshUserData();

            // The gallery gets one entry per tile.
            ListBox grid = page.Grid;
            grid.SelectedIndex = 0;
            await page.OpenSelectedAsync();
            Require(opened.Count == 1 && opened[0].Window.Count == 2, "Gallery window must hold one entry per tile.");
            // Interaction audit: double click and Enter on a tile open the same gallery window (MediaPageChecks.TileActivation.cs).
            await TileActivationParityAsync(page, window, opened);
        }
        finally { page.EndLifetime(); window.Close(); }
    }

    // Multi-select: real pointer and key input on the grid, real tag store, the real exporter into a throwaway folder.
    private static async Task SelectionJourneyAsync(MainWindow owner, string profile)
    {
        new Config().SaveConfig(profile);
        await SeedSelectionAsync(profile);
        string exportDirectory = TemporaryWorkspacePaths.CreateDirectory("media-page-selection-export-");
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        List<(IReadOnlyList<MediaLibraryEntry> Window, int Start)> opened = new();
        List<(FavoriteExportAsset[] Assets, int Unavailable, FavoriteExportResult Result)> exported = new();
        List<string> scopes = new();
        string exportTarget = exportDirectory; // The "export all" steps below copy into folders of their own.
        List<string> extraDirectories = new();
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holding = false;
        MediaPage page = new(host.OpenArchiveReaderAsync, async (directory, token) =>
        {
            if (holding) await hold.Task.WaitAsync(token); // lets the test look at the page while the index is being rebuilt
            return await MediaAssetReader.OpenAsync(directory, token);
        })
        {
            ReadUserData = () => UserData(host),
            OpenGallery = (window, start) => { opened.Add((window, start)); return Task.CompletedTask; },
            TagServices = new(host.GetPublishedPrivateTagSnapshot,
                (name, subjects, revision, token) => host.DefineAndSetMediaSubjectTagsAsync(name, subjects, revision, token),
                (tag, subjects, assigned, revision, token) => host.SetMediaSubjectTagsAsync(tag, subjects, assigned, revision, token)),
            ExportOriginals = async (assets, unavailable, scope) =>
            {
                scopes.Add(scope);
                exported.Add((assets.ToArray(), unavailable, await FavoriteOriginalExporter.ExportAsync(assets, exportTarget)));
            }
        };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media selection", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Snapshot is { Complete: true } && page.Snapshot.Entries.Count == 5, "Selection fixture must index five occurrences.");
            // By thread, newest capture first: sel-z (900), the pair dup (shown by 800), sel-b (610), sel-a (600).
            string[] order = ["sel-z.png", "dup-800.png", "sel-b.png", "sel-a.png"];
            Require(page.Tiles.Select(tile => tile.Entry.Name).SequenceEqual(order), "Tile order is wrong: " + string.Join(",", page.Tiles.Select(tile => tile.Entry.Name)));
            Require(page.Tiles[1].IsGroup && page.Tiles[1].Occurrences.Count == 2, "The pair of equal files must be one tile with two occurrences.");

            ListBox grid = page.Grid;
            Button tag = C<Button>(page, "MediaSelectionTag"), gallery = C<Button>(page, "MediaSelectionGallery"),
                export = C<Button>(page, "MediaSelectionExport"), clear = C<Button>(page, "MediaSelectionClear");
            TextBlock bar = C<TextBlock>(page, "MediaSelectionStatus");
            string[] Selected() => page.SelectedTiles.Select(tile => tile.Entry.Name!).ToArray();
            // The tile's check mark; false while the tile has no container yet.
            bool Marked(int index) => page.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(item => item.DataContext is MediaLibraryTileRow row && row.Index == index)
                ?.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "MediaTileSelected")?.IsVisible == true;

            // Nothing selected: the action row is not shown at all (no hint row, no greyed-out actions); its actions are still disabled and say why.
            Control selectionRow = C<Control>(page, "MediaSelectionRow");
            Require(!selectionRow.IsVisible && !bar.IsEffectivelyVisible && !tag.IsEffectivelyVisible && bar.Text == string.Empty && !tag.IsEnabled && !gallery.IsEnabled && !export.IsEnabled && !clear.IsEnabled,
                "Without a selection the actions must be disabled.");
            Require(ToolTip.GetTip(tag) as string == Texts.T("media.selection.reason.none"), "A disabled action must give its reason.");

            // Plain click selects one, Shift+click a range, Escape clears, Ctrl+A takes every shown tile.
            await ClickTileAsync(page, window, 0);
            Require(Selected().SequenceEqual(["sel-z.png"]) && bar.Text == Texts.T("media.selection.count", 1), "A plain click must select one tile: " + string.Join(",", Selected()));
            // The row appears with the first selected tile, counter and actions on one line; the how-to is the tag action's tooltip.
            Require(selectionRow.IsVisible && bar.IsEffectivelyVisible && tag.IsEffectivelyVisible && clear.IsEffectivelyVisible, "The action row must appear with a selection.");
            Require(new Control[] { bar, tag, gallery, export, clear }.Select(control => (int)Math.Round(control.TranslatePoint(new Point(0, control.Bounds.Height / 2), page)!.Value.Y)).Distinct().Count() == 1,
                "The counter and the actions must share one line.");
            Require((ToolTip.GetTip(tag) as string)!.Contains(Texts.T("media.selection.howTo")) && (ToolTip.GetTip(tag) as string)!.Contains(Texts.T("media.selection.tag.hint")),
                "The tag action's tooltip must carry the how-to sentence.");
            await ClickTileAsync(page, window, 2, KeyModifiers.Shift);
            Require(Selected().SequenceEqual(order[..3]) && bar.Text == Texts.T("media.selection.count", 3), "Shift+click must select the range: " + string.Join(",", Selected()));
            await UntilAsync(() => Marked(0) && Marked(1) && Marked(2) && !Marked(3));
            grid.Focus();
            grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Require(Selected().Length == 0 && !selectionRow.IsVisible && !gallery.IsEnabled, "Escape must clear the selection and hide the action row.");
            await UntilAsync(() => !Marked(0) && !Marked(1));
            grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control });
            Require(Selected().SequenceEqual(order) && bar.Text == Texts.T("media.selection.count", 4), "Ctrl+A must select every shown tile.");
            ClickRaise(clear);
            Require(Selected().Length == 0, "Clear selection did not clear.");

            // Ctrl+click adds and removes single tiles: the pair, sel-b and sel-a end up selected, sel-z does not.
            await ClickTileAsync(page, window, 1);
            await ClickTileAsync(page, window, 2, KeyModifiers.Control);
            await ClickTileAsync(page, window, 3, KeyModifiers.Control);
            await ClickTileAsync(page, window, 2, KeyModifiers.Control);
            Require(Selected().SequenceEqual(["dup-800.png", "sel-a.png"]), "Ctrl+click must toggle a tile off: " + string.Join(",", Selected()));
            await ClickTileAsync(page, window, 2, KeyModifiers.Control);
            string[] chosen = ["dup-800.png", "sel-b.png", "sel-a.png"];
            Require(Selected().SequenceEqual(chosen) && bar.Text == Texts.T("media.selection.count", 3) && tag.IsEnabled && gallery.IsEnabled && export.IsEnabled,
                "Three selected tiles must enable all actions: " + string.Join(",", Selected()));

            // Auswahl als Galerie: exactly the three shown files in page order, nothing else.
            ClickRaise(gallery);
            await UntilAsync(() => opened.Count == 1);
            Require(opened[0].Window.Select(entry => entry.Name).SequenceEqual(chosen) && opened[0].Start == 0
                && opened[0].Window[0] == page.Tiles[1].Entry, "The gallery must hold exactly the selected tiles, one file each: " + string.Join(",", opened[0].Window.Select(entry => entry.Name)));

            // Gemeinsam taggen: the pair lists both occurrences, all are ticked, one is deselected.
            MediaLibraryEntry pairIn700 = page.Tiles[1].Occurrences.Single(entry => entry.Thread.Thread == "700");
            MediaLibraryEntry pairIn800 = page.Tiles[1].Occurrences.Single(entry => entry.Thread.Thread == "800");
            ClickRaise(tag);
            // TP3: the selection bar opens the quick tag popover over the selection (tri-state, one write); "Advanced…" lists the occurrences.
            await UntilAsync(() => page.SelectionTagPopover is { IsOpen: true, Surface: not null });
            Require(page.SelectionTagPopover!.Surface!.Session.IsSelection && page.SelectionTagWindow == null, "The selection tag popover is not over the selection.");
            page.SelectionTagPopover.Surface.AdvancedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => page.SelectionTagWindow != null);
            MediaSelectionTagDialog dialog = (MediaSelectionTagDialog)page.SelectionTagWindow!;
            await UntilAsync(() => dialog.OccurrenceBoxes.Count == 4);
            Require(dialog.OccurrenceBoxes.All(box => box.IsChecked == true) && dialog.Session.Chosen.Count == 4, "Every occurrence must be ticked by default.");
            Require(dialog.OccurrenceBoxes.Select(box => box.Name).Distinct(StringComparer.Ordinal).Count() == 4,
                "The occurrence check boxes share an automation id: " + string.Join(",", dialog.OccurrenceBoxes.Select(box => box.Name)));
            CheckBox box700 = dialog.OccurrenceBoxes.Single(box => ((box.Content as TextBlock)?.Text ?? string.Empty).Contains("dup-700.png"));
            box700.IsChecked = false;
            await UntilAsync(() => dialog.Session.Chosen.Count == 3 && dialog.Session.Chosen.All(candidate => candidate.Entry != pairIn700));
            dialog.Input.Text = "picked";
            ClickRaise(dialog.AddButton);
            await UntilAsync(() => host.GetPublishedPrivateTagSnapshot().Snapshot?.MediaAssignments.Count == 3);
            PrivateTagStoreSnapshot stored = host.GetPublishedPrivateTagSnapshot().Snapshot!;
            PrivateMediaTagTarget? TargetOf(MediaLibraryEntry entry) => MediaLibraryFilterEngine.TagTarget(entry);
            string[] NamesOn(MediaLibraryEntry entry) => host.GetPublishedPrivateTagSnapshot().Snapshot!.TagsOf(TargetOf(entry)!).Select(item => item.Name.Display).ToArray();
            MediaLibraryEntry Single(string name) => page.Snapshot!.Entries.Single(entry => entry.Name == name);
            Require(NamesOn(pairIn800).SequenceEqual(["picked"]) && NamesOn(Single("sel-b.png")).SequenceEqual(["picked"]) && NamesOn(Single("sel-a.png")).SequenceEqual(["picked"]),
                "The tag must be on the three chosen occurrences.");
            Require(NamesOn(pairIn700).Length == 0 && NamesOn(Single("sel-z.png")).Length == 0, "The deselected occurrence and the unselected tile must stay untagged.");
            Require(stored.ThreadAssignments.Count == 0, "Media tags must not tag threads.");
            TagToggleRow? PickedRow() => dialog.TagList.Rows.SingleOrDefault(row => row.Tag?.Name.Display == "picked");
            await UntilAsync(() => PickedRow() is { State: TagToggleState.All, Carriers: 3, Total: 3 } && dialog.Status.Text == string.Empty);
            Require(dialog.TagList.Rows.Count(row => !row.IsHeader && row.State != TagToggleState.None) == 1
                && dialog.Surface.Notice.Text == Texts.T("tags.dialog.addedMany", "picked", 3) && dialog.UndoButton.IsVisible,
                "The selection list must show the new tag on all three ticked occurrences, with an undo.");
            // Ticking the fourth occurrence again makes the row partial ("some"), unticking makes it whole again; no write.
            box700.IsChecked = true;
            await UntilAsync(() => PickedRow() is { State: TagToggleState.Some, Carriers: 3, Total: 4 });
            Require(PickedRow()!.IsChecked == null && PickedRow()!.CountText == Texts.T("tags.dialog.carriers", 3, 4), "A partial tag must show the indeterminate state.");
            Program.CaptureWindow(dialog, "smoke-media-selection-tags-partial.png");
            box700.IsChecked = false;
            await UntilAsync(() => PickedRow() is { State: TagToggleState.All, Total: 3 });
            Require(host.GetPublishedPrivateTagSnapshot().Snapshot!.MediaAssignments.Count == 3, "Ticking must not write.");

            // The deselected occurrence gets the tag on its own; removing it again from the chosen ones leaves that one alone.
            PrivateTagCommit picked = await host.DefinePrivateTagAsync("picked").WaitAsync(Deadline);
            await host.SetMediaTagAsync(picked.Tag, TargetOf(pairIn700)!, true).WaitAsync(Deadline);
            dialog.Session.Reload(); // The modal dialog has no other writer; the test is one.
            await UntilAsync(() => dialog.Session.Snapshot!.MediaAssignments.Count == 4);
            await UntilAsync(() => PickedRow() is { State: TagToggleState.All, Carriers: 3, Total: 3 });
            dialog.TagList.ToggleForTests(PickedRow()!);
            await UntilAsync(() => host.GetPublishedPrivateTagSnapshot().Snapshot?.MediaAssignments.Count == 1 && !dialog.Session.Busy);
            // One undo gives the tag back to exactly the three it was taken from, whatever is ticked now.
            box700.IsChecked = true;
            await UntilAsync(() => dialog.Session.Chosen.Count == 4);
            ClickRaise(dialog.UndoButton);
            await UntilAsync(() => host.GetPublishedPrivateTagSnapshot().Snapshot?.MediaAssignments.Count == 4 && !dialog.Session.Busy);
            Require(!dialog.UndoButton.IsVisible && dialog.Surface.Notice.Text == Texts.T("tags.dialog.undone") && PickedRow() is { State: TagToggleState.All, Total: 4 },
                "Undo must restore the removed tag once.");
            box700.IsChecked = false;
            await UntilAsync(() => dialog.Session.Chosen.Count == 3 && PickedRow() is { State: TagToggleState.All, Total: 3 });
            dialog.TagList.ToggleForTests(PickedRow()!);
            await UntilAsync(() => host.GetPublishedPrivateTagSnapshot().Snapshot?.MediaAssignments.Count == 1);
            Require(NamesOn(pairIn700).SequenceEqual(["picked"]) && NamesOn(pairIn800).Length == 0 && NamesOn(Single("sel-b.png")).Length == 0 && NamesOn(Single("sel-a.png")).Length == 0,
                "Removing must reach only the chosen occurrences that had the tag.");

            // A tag that is deleted while the dialog is open: a new name still works; the leftover button of a deleted tag is refused.
            PrivateTagLoadSnapshot before = host.GetPublishedPrivateTagSnapshot();
            await host.DeletePrivateTagAsync(picked.Tag, before.Snapshot!.Revision).WaitAsync(Deadline);
            dialog.Input.Text = "later";
            ClickRaise(dialog.AddButton);
            await UntilAsync(() => host.GetPublishedPrivateTagSnapshot().Snapshot?.MediaAssignments.Count == 3 && dialog.Status.Text == string.Empty);
            Require(NamesOn(pairIn800).SequenceEqual(["later"]), "A new name after a deleted tag must still tag the chosen occurrences.");
            PrivateTagLoadSnapshot afterAdd = host.GetPublishedPrivateTagSnapshot();
            await host.DeletePrivateTagAsync(afterAdd.Snapshot!.Tags.Single().Id, afterAdd.Snapshot.Revision).WaitAsync(Deadline);
            dialog.Session.Reload();
            await dialog.Session.AddExistingAsync(afterAdd.Snapshot.Tags.Single().Id);
            await UntilAsync(() => dialog.Status.Text == Texts.T("tags.manage.error.gone") || dialog.Status.Text == Texts.T("tags.error.stale"));
            Require(host.GetPublishedPrivateTagSnapshot().Snapshot!.MediaAssignments.Count == 0, "A refused batch must not leave a partial write.");
            dialog.Close();
            await UntilAsync(() => page.SelectionTagWindow == null);
            Require(Selected().SequenceEqual(chosen), "Closing the tag dialog must keep the selection.");

            // Originale exportieren: the three originals, once each, from the shown occurrence of the pair only.
            ClickRaise(export);
            await UntilAsync(() => exported.Count == 1);
            (FavoriteExportAsset[] assets, int unavailable, FavoriteExportResult result) = exported[0];
            Require(assets.Length == 3 && unavailable == 0 && result.Copied == 3, "Export must offer and copy exactly three originals.");
            Require(assets.Select(asset => asset.OriginalFileName).SequenceEqual(chosen), "Export order or names are wrong: " + string.Join(",", assets.Select(asset => asset.OriginalFileName)));
            Require(assets[0].Key.Thread.Thread == "800", "The pair must be exported from the occurrence the tile shows.");
            string[] files = Directory.GetFiles(exportDirectory, "*", SearchOption.AllDirectories);
            Require(files.Length == 3, "Exactly three files must be copied, found " + files.Length);
            Require(files.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Order()
                .SequenceEqual(chosen.Select(name => Convert.ToHexString(SHA256.HashData(BytesOf(name)))).Order()), "The copied bytes are not the selected originals.");
            Require(files.All(path => !path.Contains(Path.DirectorySeparatorChar + "700" + Path.DirectorySeparatorChar)) && files.All(path => !Path.GetFileName(path).StartsWith("sel-z", StringComparison.Ordinal)),
                "A duplicate or an unselected file was exported.");

            // Filters, order and shuffling keep selected tiles that are still shown and drop the others for good.
            TextBox name = C<TextBox>(page, "MediaNameFilter");
            name.Text = "sel-";
            await UntilAsync(() => page.Tiles.Count == 3);
            Require(Selected().SequenceEqual(["sel-b.png", "sel-a.png"]) && bar.Text == Texts.T("media.selection.count", 2), "A filter must drop the hidden pair from the selection: " + string.Join(",", Selected()));
            C<ComboBox>(page, "MediaSort").SelectedIndex = 1;
            Require(Selected().Length == 2, "Another order must keep the shown selected tiles.");
            C<ComboBox>(page, "MediaSort").SelectedIndex = 2;
            ClickRaise(C<Button>(page, "MediaShuffle"));
            Require(Selected().Order().SequenceEqual(["sel-a.png", "sel-b.png"]), "Shuffling must keep the shown selected tiles.");
            C<ComboBox>(page, "MediaSort").SelectedIndex = 0;
            page.ResetFilters();
            await UntilAsync(() => page.Tiles.Count == 4);
            Require(Selected().SequenceEqual(["sel-b.png", "sel-a.png"]), "A tile that dropped out must not come back selected: " + string.Join(",", Selected()));
            await UntilAsync(() => !Marked(0) && !Marked(1) && Marked(2) && Marked(3));

            // While the index is rebuilt the selection stays, the actions wait and say so.
            holding = true;
            ClickRaise(C<Button>(page, "MediaRefresh"));
            await UntilAsync(() => !tag.IsEnabled && !gallery.IsEnabled && !export.IsEnabled);
            Require(ToolTip.GetTip(export) as string == Texts.T("media.selection.reason.building") && bar.Text!.Contains(Texts.T("media.selection.reason.building"))
                && Selected().Length == 2, "Actions must be disabled with a reason while the index is rebuilt.");
            holding = false;
            hold.SetResult();
            await page.BuildTask.WaitAsync(Deadline);
            Require(tag.IsEnabled && gallery.IsEnabled && export.IsEnabled && Selected().Order().SequenceEqual(["sel-a.png", "sel-b.png"]),
                "After the rebuild the selection must be intact and the actions available again.");

            // Alle Treffer exportieren: exactly the tiles the active filter shows, whatever is selected; hidden tiles stay out.
            Button exportAll = C<Button>(page, "MediaExportAll");
            string NewTarget() { string made = TemporaryWorkspacePaths.CreateDirectory("media-page-export-all-"); extraDirectories.Add(made); return made; }
            Require(exportAll.IsEnabled && exportAll.Content as string == Texts.T("media.exportAll") && ToolTip.GetTip(exportAll) as string == Texts.T("media.exportAll.hint"),
                "Export all must be available while tiles are shown.");
            name.Text = "sel-";
            await UntilAsync(() => page.Tiles.Count == 3);
            string allTarget = exportTarget = NewTarget();
            int done = exported.Count;
            ClickRaise(exportAll);
            await UntilAsync(() => exported.Count == done + 1);
            (FavoriteExportAsset[] allAssets, int allUnavailable, FavoriteExportResult allResult) = exported[^1];
            string[] shownByFilter = ["sel-z.png", "sel-b.png", "sel-a.png"];
            Require(allAssets.Select(asset => asset.OriginalFileName).SequenceEqual(shownByFilter) && allUnavailable == 0 && allResult.Copied == 3 && scopes[^1] == Texts.T("media.exportAll.scope"),
                "Export all must offer exactly the three shown originals, not only the selection: " + string.Join(",", allAssets.Select(asset => asset.OriginalFileName)));
            string[] allFiles = Directory.GetFiles(allTarget, "*", SearchOption.AllDirectories);
            Require(allFiles.Length == 3 && allFiles.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Order()
                .SequenceEqual(shownByFilter.Select(file => Convert.ToHexString(SHA256.HashData(BytesOf(file)))).Order()), "The copied files are not the shown originals.");
            Require(Selected().Length == 2 && !page.ExportAllAsking, "Export all must leave the selection alone and not ask below the limits.");

            // Above the limit the page asks first, with count and size, and exports only after "Trotzdem exportieren".
            page.ExportAllConfirmAbove = (2, long.MaxValue);
            done = exported.Count;
            ClickRaise(exportAll);
            await UntilAsync(() => page.ExportAllAsking);
            TextBlock ask = C<TextBlock>(page, "MediaExportAllConfirmText");
            // The three seeded originals are a few hundred bytes: the one size text (SizeText) names them in bytes, never "0 MiB".
            Require(ask.Text!.Contains('3') && ask.Text.Contains(" B)") && exported.Count == done, "The question must name the count and the size and start nothing: " + ask.Text);
            ClickRaise(C<Button>(page, "MediaExportAllConfirmNo"));
            Require(!page.ExportAllAsking && exported.Count == done, "Cancel must close the question without exporting.");
            ClickRaise(exportAll);
            await UntilAsync(() => page.ExportAllAsking);
            grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Require(!page.ExportAllAsking && exported.Count == done && Selected().Length == 2, "Escape must close the question and keep the selection.");
            ClickRaise(exportAll);
            await UntilAsync(() => page.ExportAllAsking);
            exportTarget = NewTarget();
            ClickRaise(C<Button>(page, "MediaExportAllConfirmYes"));
            await UntilAsync(() => exported.Count == done + 1);
            Require(!page.ExportAllAsking && exported[^1].Assets.Length == 3 && exported[^1].Result.Copied == 3, "Confirming must export the three shown originals.");
            // A filter change while the question is open drops it: the frozen tiles are not the shown ones any more.
            ClickRaise(exportAll);
            await UntilAsync(() => page.ExportAllAsking);
            name.Text = "sel-a";
            await UntilAsync(() => page.Tiles.Count == 1);
            Require(!page.ExportAllAsking && exported.Count == done + 1, "A filter change must drop the pending question.");
            page.ExportAllConfirmAbove = (500, 2L * 1024 * 1024 * 1024);
            exportTarget = NewTarget();
            ClickRaise(exportAll);
            await UntilAsync(() => exported.Count == done + 2);
            Require(exported[^1].Assets.Select(asset => asset.OriginalFileName).SequenceEqual(["sel-a.png"]) && !page.ExportAllAsking, "One shown tile must export directly.");

            // Disabled with a reason when nothing is shown and while the index is built.
            name.Text = "no-such-file";
            await UntilAsync(() => page.Tiles.Count == 0);
            Require(!exportAll.IsEnabled && ToolTip.GetTip(exportAll) as string == Texts.T("media.exportAll.reason.none"), "Export all must say why it is disabled without tiles.");
            page.ResetFilters();
            await UntilAsync(() => page.Tiles.Count == 4 && exportAll.IsEnabled);
            holding = true;
            hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ClickRaise(C<Button>(page, "MediaRefresh"));
            await UntilAsync(() => !exportAll.IsEnabled);
            Require(ToolTip.GetTip(exportAll) as string == Texts.T("media.selection.reason.building"), "Export all must say that the index is being built.");
            holding = false;
            hold.SetResult();
            await page.BuildTask.WaitAsync(Deadline);
            Require(exportAll.IsEnabled, "Export all must be available again after the rebuild.");
        }
        finally
        {
            hold.TrySetResult();
            page.EndLifetime();
            window.Close();
            foreach (string folder in extraDirectories.Append(exportDirectory))
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void ClickRaise(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // A real pointer click on a tile's preview area, with optional Ctrl/Shift. Waits until the tile is hit-testable, i.e. laid out and drawn.
    private static async Task ClickTileAsync(MediaPage page, Window window, int index, KeyModifiers modifiers = KeyModifiers.None)
    {
        ListBoxItem? item = null;
        InputElement? hit = null;
        Point point = default;
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (true)
        {
            item = page.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(candidate => candidate.DataContext is MediaLibraryTileRow row && row.Index == index);
            if (item is { IsEffectivelyVisible: true } && item.Bounds.Width > 0
                && item.TranslatePoint(new Point(item.Bounds.Width / 2, 55), window) is { } at
                && window.InputHitTest(at) is InputElement found && found.GetSelfAndVisualAncestors().Contains(item)
                // A container still moving to its row reports a stale position; the point must land on the preview, not on a link below it.
                && item.IsArrangeValid && !found.GetSelfAndVisualAncestors().TakeWhile(visual => visual != item).OfType<Button>().Any())
            { point = at; hit = found; break; }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Tile " + index + " never became hit-testable.");
            await Task.Delay(20);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        hit!.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), modifiers));
        hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, point, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), modifiers, MouseButton.Left));
        pointer.Capture(null);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    // Five threads, one bound file each: two single files, a pair of equal bytes in two threads, and a fourth tile that is never selected.
    private static async Task SeedSelectionAsync(string profile)
    {
        (string Id, string File, int Day)[] threads = [("600", "sel-a.png", 3), ("610", "sel-b.png", 4), ("700", "dup-700.png", 5), ("800", "dup-800.png", 6), ("900", "sel-z.png", 7)];
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        foreach ((string id, string file, int day) in threads)
        {
            string directory = Path.Combine(profile, "media", id);
            ResourceId resource = new("media-fixture", "g", id);
            CapturedAttachment bound = await WriteAsync(directory, resource, (file, "s" + id, id));
            await catalog.CaptureAsync(new CapturedThreadSnapshot(resource, "https://offline.invalid/" + id, "Sel " + id,
                ThreadSourceState.Archived, new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero), directory,
                [new CapturedPost(id, 0, PostFieldPresence.All, Html: "op", Attachments: [bound])]));
        }
    }

    private static async Task<Button> FindDuplicatesHintAsync(MediaPage page)
    {
        Button? hint = null;
        await UntilAsync(() => (hint = page.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "MediaTileDuplicates" && button.IsVisible)) != null);
        return hint!;
    }

    // Same composition as the main window: passive views, unavailable stores stay null.
    private static MediaLibraryUserData UserData(DesktopSessionHost host)
    {
        PrivateTagLoadSnapshot tags = host.GetPublishedPrivateTagSnapshot();
        IReadOnlyList<FavoriteEntry>? favorites;
        try { favorites = host.GetFavorites(); } catch (InvalidDataException) { favorites = null; }
        IReadOnlyList<Ratings.MediaRatingEntry>? ratings;
        try { ratings = host.GetMediaRatings(); } catch (InvalidDataException) { ratings = null; }
        return new MediaLibraryUserData(tags.State == PrivateTagLoadState.Ready ? tags.Snapshot : null, favorites, ratings);
    }

    private static async Task<Button> FindThreadLinkAsync(MediaPage page, ResourceId thread)
    {
        Button? link = null;
        await UntilAsync(() => (link = page.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "MediaTileThread"
            && button.DataContext is MediaLibraryTileRow row && row.Entry.Thread == thread)) != null);
        return link!;
    }

    private static void RemoveChip(WrapPanel chips, int index)
    {
        System.Windows.Input.ICommand command = ((Ursa.Controls.ClosableTag)chips.Children[index]).Command
            ?? throw new InvalidOperationException("Filter chip has no remove command.");
        command.Execute(null);
    }

    private static async Task NavigationAsync(MainWindow owner)
    {
        string mode = owner.SessionHost.Config.WorkspaceMode;
        try
        {
            // Barebones keeps reading pages in the Tools menu; Simple shows Archive and Media directly.
            await PresentationChecks.SwitchModeAsync(owner, "Simple");
            Button media = owner.FindControl<Button>("MediaNavigationButton")!;
            Button archive = owner.FindControl<Button>("ArchiveNavigationButton")!;
            await UntilAsync(() => media.IsVisible && media.IsEffectivelyEnabled);
            Require(archive.IsVisible, "Media is shown while Archive is not.");
            media.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ContentControl host = owner.FindControl<ContentControl>("MediaPageHost")!;
            await UntilAsync(() => host.IsVisible && host.Content is MediaPage);
            MediaPage page = (MediaPage)host.Content!;
            await page.BuildTask.WaitAsync(Deadline);
            Require(media.Classes.Contains("selected") && !archive.Classes.Contains("selected"), "Media navigation is not marked as selected.");
            Require(C<TextBlock>(page, "MediaEmpty").IsVisible, "An empty archive must say that there are no saved media.");
            // The empty state is the sentence and one primary action (like the empty Downloads list): no filters, counters, selection row or other actions.
            Button goDownloads = C<Button>(page, "MediaGoDownloads");
            Require(goDownloads.IsEffectivelyVisible && AutomationProperties.GetName(goDownloads) == Texts.T("nav.goDownloads"),
                "The empty media page did not offer a labelled Downloads button.");
            Require(!C<Avalonia.Controls.Primitives.ToggleButton>(page, "MediaFilters").IsEffectivelyVisible && !C<ComboBox>(page, "MediaSort").IsEffectivelyVisible
                && !C<TextBlock>(page, "MediaStatus").IsEffectivelyVisible && !C<Control>(page, "MediaSelectionRow").IsEffectivelyVisible
                && !C<Button>(page, "MediaExportAll").IsEffectivelyVisible && !C<Button>(page, "MediaRefresh").IsEffectivelyVisible,
                "The empty media page must not show filters, counters or actions.");
            Program.CaptureWindow(owner, "smoke-empty-media.png");
            owner.FindControl<Button>("ArchiveNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ContentControl archiveHost = owner.FindControl<ContentControl>("ArchivePageHost")!;
            await UntilAsync(() => !host.IsVisible && archiveHost.IsVisible);
            Button archiveGo = C<Button>(owner.ArchivePage, "ArchiveGoDownloads");
            await UntilAsync(() => archiveGo.IsEffectivelyVisible);
            Require(AutomationProperties.GetName(archiveGo) == Texts.T("nav.goDownloads")
                && !C<Button>(owner.ArchivePage, "ArchiveResetFilters").IsVisible,
                "The empty archive did not offer a labelled Go to Downloads button instead of Reset filters.");
            await UntilAsync(() => !owner.ArchivePage.IsBusy);
            owner.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Program.CaptureWindow(owner, "smoke-empty-archive.png");
            archiveGo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !archiveHost.IsVisible && owner.FindControl<Control>("WorkspaceBody")!.IsVisible);
            Button importEmpty = C<Button>(owner, "ThreadsEmptyImportButton");
            await UntilAsync(() => importEmpty.IsEffectivelyVisible);
            Require(AutomationProperties.GetName(importEmpty) == CommandCatalog.Label("watch.import-clipboard"),
                "The empty Downloads list did not offer the clipboard import under its command name.");
            Program.CaptureWindow(owner, "smoke-empty-downloads.png");
            media.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => host.IsVisible);
            await page.BuildTask.WaitAsync(Deadline);
            goDownloads.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !host.IsVisible && owner.FindControl<Control>("WorkspaceBody")!.IsVisible);
            archive.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !host.IsVisible && owner.FindControl<ContentControl>("ArchivePageHost")!.IsVisible);
            // Barebones moves Media into the Tools menu, next to Archive.
            await PresentationChecks.SwitchModeAsync(owner, "Barebones");
            Button tools = owner.FindControl<Button>("ToolsNavigationButton")!;
            await UntilAsync(() => !media.IsVisible && tools.IsVisible);
            MenuItem item = ((MenuFlyout)tools.Flyout!).Items.OfType<MenuItem>().Single(entry => entry.Tag as string == "nav.media");
            Require(item.IsVisible, "Media is missing from the Tools menu in Barebones.");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await UntilAsync(() => host.IsVisible);
            owner.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !host.IsVisible);
        }
        finally { await PresentationChecks.SwitchModeAsync(owner, mode); }
    }

    internal static async Task SeedAsync(string profile)
    {
        string a = Path.Combine(profile, "media", "a"), b = Path.Combine(profile, "media", "b");
        CapturedAttachment boundA = await WriteAsync(a, A, ("one.png", "m1", "100"), ("loose.png", "m9", null));
        CapturedAttachment boundB = await WriteAsync(b, B, ("two.png", "x1", "200"));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(A, "https://offline.invalid/100", "Thread A",
            ThreadSourceState.Archived, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), a,
            [new CapturedPost("100", 0, PostFieldPresence.All, Html: "op", Attachments: [boundA]),
             new CapturedPost("101", 1, PostFieldPresence.All, Html: "reply",
                Attachments: [new CapturedAttachment(0, "m2", "original", "https://offline.invalid/gone.png", "gone.png")])]));
        await catalog.CaptureAsync(new CapturedThreadSnapshot(B, "https://offline.invalid/200", "Thread B",
            ThreadSourceState.Archived, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), b,
            [new CapturedPost("200", 0, PostFieldPresence.All, Html: "op", Attachments: [boundB])]));
    }

    // Three threads hold the very same bytes under different names; thread 300 also holds one other file.
    private static async Task SeedDuplicatesAsync(string profile)
    {
        string[] threads = ["300", "400", "500"];
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        for (int index = 0; index < threads.Length; index++)
        {
            string id = threads[index], directory = Path.Combine(profile, "media", id);
            ResourceId resource = new("media-fixture", "g", id);
            CapturedAttachment bound = id == "300"
                ? await WriteAsync(directory, resource, ("dup-300.png", "d300", "300"), ("solo.png", "s300", null))
                : await WriteAsync(directory, resource, ("dup-" + id + ".png", "d" + id, id));
            await catalog.CaptureAsync(new CapturedThreadSnapshot(resource, "https://offline.invalid/" + id, "Dup " + id,
                ThreadSourceState.Archived, new DateTimeOffset(2026, 11, 3 + index, 0, 0, 0, TimeSpan.Zero), directory,
                [new CapturedPost(id, 0, PostFieldPresence.All, Html: "op", Attachments: [bound])]));
        }
    }

    private static readonly byte[] VideoBytes = File.ReadAllBytes(FixturePath("tests/Fixtures/gallery-playback-vp8.webm"));
    private static readonly byte[] UndecodableVideoBytes = [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8];

    // Two threads hold the same video bytes (one byte group). Only the poster the reader would have generated exists.
    private static async Task SeedVideosAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        int day = 1;
        foreach (ResourceId id in new[] { new ResourceId("media-fixture", "g", "600"), new ResourceId("media-fixture", "g", "700") })
        {
            string directory = Path.Combine(profile, "media", id.Thread);
            CapturedAttachment bound = await WriteAsync(directory, id, ("clip-" + id.Thread + ".webm", "v" + id.Thread, id.Thread));
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/" + id.Thread, "Video " + id.Thread,
                ThreadSourceState.Archived, new DateTimeOffset(2026, 11, day++, 0, 0, 0, TimeSpan.Zero), directory,
                [new CapturedPost(id.Thread, 0, PostFieldPresence.All, Html: "op", Attachments: [bound])]));
        }
    }

    // One thread each: a video with sound, one without, one that cannot be decided.
    private static async Task SeedAudioAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        int day = 1;
        foreach ((string thread, string name) in new[] { ("900", "snd-900.mp4"), ("910", "mute-910.mp4"), ("920", "odd-920.mp4") })
        {
            ResourceId id = new("media-fixture", "g", thread);
            string directory = Path.Combine(profile, "media", thread);
            CapturedAttachment bound = await WriteAsync(directory, id, (name, "a" + thread, thread));
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/" + thread, "Audio " + thread,
                ThreadSourceState.Archived, new DateTimeOffset(2026, 11, day++, 0, 0, 0, TimeSpan.Zero), directory,
                [new CapturedPost(thread, 0, PostFieldPresence.All, Html: "op", Attachments: [bound])]));
        }
    }

    private static async Task AudioJourneyAsync(MainWindow owner, string profile)
    {
        string cache = Path.Combine(profile, "cache", MediaLibraryIndexCache.FileName);
        string audioCache = Path.Combine(profile, "cache", MediaAudioCache.FileName);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        MediaPage page = new(host.OpenArchiveReaderAsync, cachePath: cache) { ReadUserData = () => UserData(host) };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media sound", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Tiles.Count == 3 && page.Tiles.All(tile => tile.Entry.Kind == SavedMediaKind.Video), "Audio fixture must give three video tiles.");
            ComboBox sound = C<ComboBox>(page, "MediaSoundFilter"), kind = C<ComboBox>(page, "MediaKindFilter");
            TextBlock note = C<TextBlock>(page, "MediaAudioNote");
            string Shown() => string.Join(",", page.ShownEntries.Select(entry => entry.Name));

            // The check runs by itself in the background; the undecidable video stays unknown (recorded as probed, but not as silent).
            await UntilAsync(() => page.AudioResults.Count == 3 && !note.IsVisible);
            MediaLibraryEntry Video(string name) => page.Snapshot!.Entries.Single(entry => entry.Name == name);
            Require(page.AudioResults.Get(Video("snd-900.mp4")) == MediaAudio.HasSound && page.AudioResults.Get(Video("mute-910.mp4")) == MediaAudio.Silent
                && page.AudioResults.Get(Video("odd-920.mp4")) == MediaAudio.Unknown && page.AudioResults.WasProbed(MediaAudioMap.KeyOf(Video("odd-920.mp4"))),
                "The background check classified the three videos wrongly.");
            await page.AudioScanTask.WaitAsync(Deadline); // the cache is saved when the scan ends
            Require(File.Exists(audioCache) && MediaAudioCache.Load(audioCache)?.Count == 3, "The audio cache was not written.");
            Require(page.ShownEntries.Count == 3 && !note.IsVisible, "Without a sound filter nothing changes and no hint shows.");

            // Mit Ton: one video, the undecidable one is counted and not shown; the type follows to Videos.
            sound.SelectedIndex = 1;
            Require(Shown() == "snd-900.mp4" && kind.SelectedIndex == (int)MediaLibraryKindFilter.Video && page.ActiveFilterCount > 0, "With sound must show only the sound video: " + Shown());
            Require(note.IsVisible && note.Text == Texts.T("media.audio.unknown", 1) && page.UncheckedVideos == 1, "The undecidable video must be counted: " + note.Text);
            Require(C<TextBlock>(page, "MediaStatus").Text == Texts.T("media.filter.count", 1, 3), "Filtered count is wrong: " + C<TextBlock>(page, "MediaStatus").Text);
            sound.SelectedIndex = 2;
            Require(Shown() == "mute-910.mp4" && note.Text == Texts.T("media.audio.unknown", 1), "Without sound must show only the silent video, never the undecidable one: " + Shown());
            Require(page.CurrentFilter().Sound == MediaLibrarySoundFilter.WithoutSound, "The filter does not describe the choice.");

            // Another type drops the sound choice instead of contradicting it; "egal" widens back.
            kind.SelectedIndex = (int)MediaLibraryKindFilter.Image;
            Require(sound.SelectedIndex == 0 && page.ShownEntries.Count == 0 && !note.IsVisible, "Choosing another type must drop the sound choice.");
            kind.SelectedIndex = 0;
            sound.SelectedIndex = 1;
            sound.SelectedIndex = 0;
            Require(page.ShownEntries.Count == 3 && !note.IsVisible, "Sound 'egal' must show all three again.");
            sound.SelectedIndex = 2;
            page.ResetFilters();
            Require(sound.SelectedIndex == 0 && kind.SelectedIndex == 0 && page.ShownEntries.Count == 3 && page.ActiveFilterCount == 0, "Reset must clear the sound choice.");
        }
        finally { page.EndLifetime(); window.Close(); }

        // A second page starts from the cache: nothing is probed again, the filter works at once.
        MediaPage again = new(host.OpenArchiveReaderAsync, cachePath: cache);
        Window second = new() { Width = 1000, Height = 700, Content = again, ShowInTaskbar = false };
        try
        {
            second.Show(owner);
            again.IsAvailable = true;
            // The stored results do not wait for the first previews: with a long grace they are still there at once.
            TimeSpan grace = MediaPage.AudioScanGrace;
            MediaPage.AudioScanGrace = TimeSpan.FromSeconds(12);
            System.Diagnostics.Stopwatch storedIn = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await again.ActivateAsync().WaitAsync(Deadline);
                await UntilAsync(() => again.AudioResults.Count == 3);
                Require(storedIn.Elapsed < TimeSpan.FromSeconds(6), "The stored sound results must not wait for the first previews: " + storedIn.Elapsed);
            }
            finally { MediaPage.AudioScanGrace = grace; }
            await again.AudioScanTask.WaitAsync(Deadline);
            C<ComboBox>(again, "MediaSoundFilter").SelectedIndex = 1;
            Require(again.ShownEntries.Count == 1 && again.ShownEntries[0].Name == "snd-900.mp4", "The cached results must filter again.");
        }
        finally { again.EndLifetime(); second.Close(); }
    }

    private static async Task VideoJourneyAsync(MainWindow owner, string profile)
    {
        string posters = Path.Combine(profile, "cache", "video-posters-v1");
        Directory.CreateDirectory(posters);
        await File.WriteAllBytesAsync(Path.Combine(posters, Convert.ToHexString(SHA256.HashData(VideoBytes)).ToLowerInvariant() + ".png"), Png);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        MediaPage page = new(host.OpenArchiveReaderAsync) { ReadUserData = () => UserData(host), PosterCacheDirectory = posters };
        Window window = new() { Width = 1100, Height = 800, Content = page, Title = "Synthetic media videos", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Tiles.Count == 1 && page.Tiles[0].Occurrences.Count == 2 && page.Tiles[0].Entry.Kind == SavedMediaKind.Video,
                "Two identical videos must make one video tile: " + page.Tiles.Count);
            await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>().Any(view => view.HasThumbnail));
        }
        finally { page.EndLifetime(); window.Close(); }
    }

    /// <summary>Writes real PNG files and a manifest that binds them like a capture does; returns the first bound attachment.</summary>
    private static async Task<CapturedAttachment> WriteAsync(string directory, ResourceId resource, params (string Name, string MediaId, string? Post)[] files)
    {
        Directory.CreateDirectory(directory);
        static string Md5Of(string name) => Convert.ToBase64String(MD5.HashData(BytesOf(name)));
        static string ShaOf(string name) => Convert.ToHexString(SHA256.HashData(BytesOf(name))).ToLowerInvariant();
        Dictionary<string, object> entries = new(), assets = new();
        foreach (var file in files)
        {
            byte[] bytes = BytesOf(file.Name);
            await File.WriteAllBytesAsync(Path.Combine(directory, file.Name), bytes);
            entries[file.Name] = new { MediaIdentifier = file.MediaId, Variant = "original", SourceUrl = "https://offline.invalid/" + file.Name,
                FileName = file.Name, OriginalFileName = file.Name, Length = bytes.Length, Md5 = Md5Of(file.Name), Sha256 = ShaOf(file.Name) };
        }
        string path = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        MediaAssetSnapshot plain = await MediaAssetReader.OpenAsync(directory);
        Dictionary<string, MediaAttachmentBinding> bindings = new();
        CapturedAttachment? first = null;
        foreach (var file in files)
        {
            MediaAssetReference asset = plain.GetPage().Single(item => item.RequestedFileName == file.Name);
            assets[asset.AssetId] = new { RequestedFileName = file.Name, Entry = entries[file.Name] };
            if (file.Post == null) continue;
            string md5 = Md5Of(file.Name), sha = ShaOf(file.Name);
            int length = BytesOf(file.Name).Length;
            CapturedAttachment attachment = new CapturedAttachment(0, file.MediaId, "original", "https://offline.invalid/" + file.Name, file.Name,
                ExpectedSize: length, ExpectedMd5: md5) { ExpectedSha256 = sha };
            MediaProviderMetadata provider = new(file.MediaId, "original", attachment.Url!, file.Name, length, md5, sha);
            string key = MediaRevisionKeys.CreateAttachmentKey(resource, file.Post, 0, provider);
            bindings[key] = new(new(resource, file.Post, 0, key), MediaRevisionKeys.CreateAcquisitionKey(resource, provider),
                asset.AssetId, provider, new(asset.Length, asset.Md5, asset.Sha256), new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch));
            first ??= attachment;
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings }));
        return first!;
    }

    // The filter fields live in a surface that is hidden until opened; before its first open its scroll viewer has no
    // template yet, so they are found through the logical tree.
    private static T C<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Missing control " + name);

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Media page UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
