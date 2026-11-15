using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using MeowChan.Tests.Shared;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_BOORU_MEDIA_ONLY. B7 in the real window: downloaded Danbooru originals of a synthetic
// catalog (no provider, no network) appear on the media page with a source badge and a source filter, show their post
// details, provider tags (apart from private tags) and every finding, group by equal bytes, open in the gallery with the
// provider context, share favorites with the gallery, and stay exactly the same after the watchers are removed and the
// app is restarted without any provider. Managed input only; screenshots in Light and Dark.
internal static class BooruMediaChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly ResourceId Chan = new("booru-media-fixture", "g", "900");
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
    private static readonly Dictionary<string, byte[]> Files = new()
    {
        ["123"] = Picture(SKColors.SteelBlue, SKColors.Khaki), ["124"] = Picture(SKColors.IndianRed, SKColors.Gold),
        ["900"] = Picture(SKColors.SeaGreen, SKColors.Lavender)
    };

    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        string profile = TemporaryWorkspacePaths.CreateDirectory("booru-media-native-");
        try
        {
            Texts.SetLanguage("en");
            await SeedAsync(profile);
            await FirstSessionAsync(owner, profile);
            // The user removes both watchers; the app starts again with no provider, plugin or network.
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            {
                BooruCatalogStore store = new(profile, lease);
                Require(store.MarkWatcherRemoved("w1") && store.MarkWatcherRemoved("w2"), "The fixture watchers could not be removed.");
            }
            await RestartedSessionAsync(owner, profile);
            Console.WriteLine("SMOKE booru media [en]: three downloaded Danbooru originals (two posts with the same bytes) on the media page with source badge and "
                + "Danbooru source filter, one tile for equal bytes listing both claims and their findings, details with provider rating, versions, findings and read-only "
                + "source tags apart from an unavailable own-tag section, open and copy of the canonical post link, copy of source tags (all, per category, marked, "
                + "explicitly as a two-tag query, refused over two tags, failing clipboard), unsent draft on the hosted Booru page, no search action when unavailable or hidden, "
                + "no stale handoff after availability, presentation or composition changes, gallery provider context, a favorite shared by gallery and "
                + "media page, nothing written to private tags; identical after watcher removal and a restart without a provider. Light and Dark screenshots.");
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current!.RequestedThemeVariant = theme;
        }
    }

    private static byte[] Picture(SKColor from, SKColor to)
    {
        using SKBitmap bitmap = new(360, 240);
        using SKCanvas canvas = new(bitmap);
        using SKPaint paint = new() { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(360, 240), [from, to], SKShaderTileMode.Clamp) };
        canvas.DrawRect(0, 0, 360, 240, paint);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static BooruPostClaim Claim(string id, BooruPostClaimRating rating, int score, params BooruPostClaimTag[] tags) => new("danbooru", id,
        BooruPostClaimPresence.All, $"https://danbooru.donmai.us/posts/{id}", CreatedAtUtc: Observed.AddDays(-3), Rating: rating, Score: score,
        FavoriteCount: score / 3, Tags: tags,
        Media:
        [
            new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, $"https://cdn.donmai.us/original/{id}.png", ".png", Files["123"].Length, 360, 240),
            new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, $"https://cdn.donmai.us/sample/{id}.jpg", ".jpg", null, 180, 120),
            new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview, $"https://cdn.donmai.us/preview/{id}.jpg", ".jpg", null, 90, 60)
        ],
        SourceClaim: "https://source.example/art/" + id);

    private static async Task SeedAsync(string profile)
    {
        new Config { DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Simple" }.SaveConfig(profile);
        // A chan thread with one loose file: the source filter must tell it apart from the provider media.
        string chanDirectory = Path.Combine(profile, "media", "chan");
        await WriteOriginalAsync(chanDirectory, "chan.png", "m-chan", Files["900"]);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            await catalog.CaptureAsync(new CapturedThreadSnapshot(Chan, "https://offline.invalid/900", "Chan thread", ThreadSourceState.Archived,
                DateTimeOffset.UnixEpoch, chanDirectory, [new CapturedPost("900", 0, PostFieldPresence.All, Html: "No attachments.")]));
        }
        // The Danbooru catalog exactly as finished original downloads leave it: files and manifests in post folders, a claim and a link per post.
        using ProfileLease booru = ProfileLease.Acquire(profile);
        BooruCatalogStore store = new(profile, booru);
        BooruPostClaimTag[] tags123 =
        [
            new("artist_name", BooruPostClaimTagCategory.Artist), new("some_series", BooruPostClaimTagCategory.Copyright),
            new("main_character", BooruPostClaimTagCategory.Character), new("blue_sky", BooruPostClaimTagCategory.General),
            new("1girl", BooruPostClaimTagCategory.General), new("highres", BooruPostClaimTagCategory.Meta)
        ];
        foreach ((string id, string bytesOf, BooruPostClaimRating rating, int score, BooruPostClaimTag[] tags) in new (string, string, BooruPostClaimRating, int, BooruPostClaimTag[])[]
        {
            ("123", "123", BooruPostClaimRating.General, 5, [new("blue_sky", BooruPostClaimTagCategory.General)]),
            ("124", "124", BooruPostClaimRating.Questionable, 7, [new("moody_light", BooruPostClaimTagCategory.General)]),
            // The same bytes as post 123, claimed again by another post; the page shows the more recently seen post (125) for the tile.
            ("125", "123", BooruPostClaimRating.General, 42, tags123)
        })
        {
            string directory = Path.Combine(profile, "danbooru", id);
            await WriteOriginalAsync(directory, id + ".png", BooruMediaIdentity.MediaIdentifier(id), Files[bytesOf]);
            string assetId = (await MeowChan.Storage.MediaAssetReader.OpenAsync(directory)).GetPage().Single().AssetId;
            store.UpsertPost(Claim(id, rating, score, tags), Observed);
            store.LinkLocalAsset("danbooru", id, assetId, Path.Combine(directory, id + ".png"));
        }
        store.AddFinding("danbooru", "125", BooruFindingKind.Search, null, "1girl", Observed);
        store.AddFinding("danbooru", "125", BooruFindingKind.Watcher, "w1", "1girl rating:g", Observed.AddMinutes(1));
        store.AddFinding("danbooru", "125", BooruFindingKind.Watcher, "w2", "blue_sky", Observed.AddMinutes(2));
        store.AddFinding("danbooru", "123", BooruFindingKind.Watcher, "w1", "1girl rating:g", Observed);
        store.AddFinding("danbooru", "124", BooruFindingKind.OneShot, null, null, Observed.AddMinutes(4));
        store.UpsertWatcher(new("w1", "danbooru", "1girl rating:g", "Active", Observed));
        store.UpsertWatcher(new("w2", "danbooru", "blue_sky", "Active", Observed));
    }

    private static async Task WriteOriginalAsync(string directory, string name, string mediaId, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new Dictionary<string, object>
            {
                [name] = new
                {
                    MediaIdentifier = mediaId, Variant = "original", SourceUrl = "https://offline.invalid/" + name, FileName = name,
                    OriginalFileName = name, Length = bytes.Length, Md5 = Convert.ToBase64String(MD5.HashData(bytes)),
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                }
            }
        }));
    }

    private static async Task<(DesktopSessionHost Host, MainWindow Shell, MediaPage Page)> OpenAsync(MainWindow owner, string profile)
    {
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        MainWindow shell = new(host) { Width = 1300, Height = 900 };
        shell.Show(owner);
        await UntilAsync(() => host.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
        await ShowMediaAsync(shell);
        MediaPage page = shell.MediaPageForTests ?? throw new InvalidOperationException("The shell has no media page.");
        await page.BuildTask.WaitAsync(Deadline);
        return (host, shell, page);
    }

    private static async Task ShowMediaAsync(MainWindow shell)
    {
        Button navigation = shell.FindControl<Button>("MediaNavigationButton")!;
        await UntilAsync(() => navigation.IsVisible && navigation.IsEffectivelyEnabled);
        navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ContentControl host = shell.FindControl<ContentControl>("MediaPageHost")!;
        await UntilAsync(() => host.IsVisible && host.Content is MediaPage);
        await shell.MediaPageForTests!.BuildTask.WaitAsync(Deadline);
    }

    private static async Task CloseAsync(DesktopSessionHost host, MainWindow shell)
    {
        foreach (Window child in shell.OwnedWindows.Where(child => child.IsVisible).ToArray()) child.Close();
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(Deadline);
        await UntilAsync(() => !shell.IsVisible);
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Require(host.IsProfileReleased, "The fixture host did not release its private profile.");
    }

    private static T C<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Missing control " + name);

    private static MediaLibraryTileRow[] Rows(MediaPage page) => (MediaLibraryTileRow[])page.Grid.ItemsSource!;

    private static async Task SetThemeAsync(ThemeVariant theme, params Window[] windows)
    {
        Application.Current!.RequestedThemeVariant = theme;
        foreach (Window window in windows) window.RequestedThemeVariant = theme;
        await Program.WaitUntilAsync(() => windows.All(window => window.ActualThemeVariant == theme));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    // The media page as the user sees it: all tiles, provider badge on exactly the provider tiles, one tile for equal bytes.
    private static async Task CheckPageAsync(MediaPage page, MainWindow shell, string phase)
    {
        Require(page.Snapshot is { Complete: true } && page.Snapshot.Entries.Count == 4, phase + ": the index must list three provider files and the chan file.");
        Require(page.ShownEntries.Count == 3, phase + ": equal bytes must be one tile: " + page.ShownEntries.Count + " tiles.");
        MediaLibraryTileRow[] rows = Rows(page);
        MediaLibraryTileRow group = rows.Single(row => row.Tile.IsGroup);
        Require(group.Tile.Occurrences.Select(entry => entry.BooruPostId).Order(StringComparer.Ordinal).SequenceEqual(new[] { "123", "125" }),
            phase + ": the grouped tile must hold both Danbooru posts of the same bytes.");
        Require(group.ThreadLabel == Texts.T("booru.media.tile.post", group.Entry.BooruPostId!) && group.Duplicates == Texts.T("booru.media.tile.inPosts", 2), phase + ": grouped tile label: " + group.ThreadLabel);
        MediaLibraryTileRow chan = rows.Single(row => row.Entry.BooruPostId == null);
        Require(chan.Entry.Thread == Chan && !chan.IsProviderMedium, phase + ": the chan file must stay a chan file.");
        await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>().Count(view => view.DataContext is MediaLibraryTileRow) == 3);
        foreach (MediaLibraryTileView view in page.GetVisualDescendants().OfType<MediaLibraryTileView>())
        {
            MediaLibraryTileRow row = (MediaLibraryTileRow)view.DataContext!;
            Require(view.Badges.State.Booru == row.IsProviderMedium, phase + ": the booru source badge must be in the state of provider tiles only.");
            Require(!row.IsProviderMedium || view.BadgeDescription.Contains(Texts.T("media.badge.booru"), StringComparison.Ordinal), phase + ": the badge sentence must name the booru source.");
        }
        await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>()
            .Any(view => view.GetVisualDescendants().OfType<Image>().Any(image => image.Source != null)));
        Require(shell.FindControl<TextBlock>("ShellActionError")?.Text is null or "", phase + ": the shell shows an error.");
        string status = C<TextBlock>(page, "MediaStatus").Text ?? string.Empty;
        Require(status.Contains(Texts.Count("booru.media.statusCount", 3)), phase + ": the status must count the three downloaded posts: " + status);
    }

    private static async Task SourceFilterAsync(MediaPage page, string phase)
    {
        ComboBox source = C<ComboBox>(page, "MediaSourceFilter");
        string label = Texts.T("booru.media.source");
        int index = Enumerable.Range(0, source.Items.Count).Single(i => source.Items[i]!.ToString() == label);
        source.SelectedIndex = index;
        Require(page.CurrentFilter().Source == "danbooru" && page.ShownEntries.Count == 2 && page.ShownEntries.All(entry => entry.BooruPostId != null),
            phase + ": the Danbooru source filter must show exactly the two provider tiles: " + page.ShownEntries.Count);
        Require(source.Items.Cast<object>().Any(item => item.ToString() == "booru-media-fixture"), phase + ": chan sources keep their identifier.");
        page.ResetFilters();
        Require(page.ShownEntries.Count == 3, phase + ": reset did not widen the page.");
        await Task.CompletedTask;
    }

    // A gallery over the Danbooru source filter holds provider posts only: it is named by its source.
    private static async Task ProviderOnlyGalleryAsync(MainWindow shell, MediaPage page, string phase)
    {
        ComboBox source = C<ComboBox>(page, "MediaSourceFilter");
        source.SelectedIndex = Enumerable.Range(0, source.Items.Count).Single(i => source.Items[i]!.ToString() == Texts.T("booru.media.source"));
        MediaLibraryTileRow first = Rows(page)[0];
        await SelectAsync(page, first);
        page.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
        await UntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
        GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
        await UntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "GalleryScope").Text == Texts.Count("booru.media.gallery.scope", 2));
        GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
        await UntilAsync(() => !galleryHost.IsVisible);
        await shell.GalleryCleanupTask.WaitAsync(Deadline);
        page.ResetFilters();
        Require(page.ShownEntries.Count == 3, phase + ": reset after the provider-only gallery.");
    }

    private static async Task SelectAsync(MediaPage page, MediaLibraryTileRow row)
    {
        // The page may have presented its rows again (a theme or language change); the tile is found by its entry.
        MediaLibraryTileRow[] rows = Rows(page);
        page.Grid.SelectedIndex = Array.FindIndex(rows, candidate => candidate.Entry == row.Entry);
        page.Grid.Focus();
        await UntilAsync(() => page.Grid.SelectedItem is MediaLibraryTileRow selected && selected.Entry == row.Entry);
    }

    private static async Task<MenuItem> MenuItemAsync(MediaPage page, string id)
    {
        page.Grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await UntilAsync(() => page.MediaMenu?.IsOpen == true);
        MenuItem[] items = page.MediaMenu!.Items.OfType<MenuItem>().ToArray();
        return items.SingleOrDefault(item => item.Tag as string == id)
            ?? throw new InvalidOperationException("Menu item " + id + " missing; the menu offers: " + string.Join(", ", items.Select(item => item.Tag)));
    }

    private static async Task OccurrencesAsync(MediaPage page, MediaLibraryTileRow group, string phase)
    {
        await SelectAsync(page, group);
        MenuItem item = await MenuItemAsync(page, "media.occurrences");
        item.Command!.Execute(null);
        page.MediaMenu!.Close();
        await UntilAsync(() => page.OccurrencesWindow?.IsVisible == true);
        Window window = page.OccurrencesWindow!;
        TextBlock[] findings = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "MediaOccurrenceFindings").ToArray();
        TextBlock[] files = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "MediaOccurrenceFile").ToArray();
        Require(findings.Length == 2 && files.Length == 2, phase + ": both Danbooru claims must be listed as occurrences.");
        string watcher = BooruMessages.Finding(BooruFindingKind.Watcher), search = BooruMessages.Finding(BooruFindingKind.Search);
        string rich = findings[Array.FindIndex(files, text => text.Text!.Contains("125.png"))].Text!, poor = findings[Array.FindIndex(files, text => text.Text!.Contains("123.png"))].Text!;
        Require(rich.Contains(search) && rich.Contains(watcher + " ×2"), phase + ": post 125 must list its search and two watcher findings: " + rich);
        Require(poor.Contains(watcher) && !poor.Contains(search) && !poor.Contains("×"), phase + ": post 123 must list its single watcher finding: " + poor);
        Require(window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "MediaOccurrenceWhere")
            .All(text => text.Text!.Contains(Texts.T("booru.media.source"))), phase + ": occurrences must name Danbooru as their source.");
        window.Close();
        await UntilAsync(() => page.OccurrencesWindow == null);
    }

    private static async Task<BooruMediaDetailsDialog> OpenDetailsAsync(MainWindow shell, MediaPage page, MediaLibraryTileRow row, string phase)
    {
        MenuItem details = null!;
        // The page drops an open menu whenever it presents its rows again (by design); a user simply opens it again.
        for (int attempt = 1; ; attempt++)
        {
            await page.BuildTask.WaitAsync(Deadline);
            await SelectAsync(page, row);
            details = await MenuItemAsync(page, "media.details");
            Require(details.IsEnabled && details.Command!.CanExecute(null), phase + ": details must be offered for a downloaded provider file.");
            details.Command.Execute(null);
            page.MediaMenu!.Close();
            try { await UntilAsync(() => shell.BooruDetailsWindow?.IsVisible == true); break; }
            catch (TimeoutException) when (attempt < 3) { }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(phase + ": the details dialog did not open. Shell notice: '" + shell.FindControl<TextBlock>("ShellActionError")?.Text
                    + "', owned windows: " + string.Join(",", shell.OwnedWindows.Select(window => window.GetType().Name)));
            }
        }
        return shell.BooruDetailsWindow!;
    }

    private static async Task DetailsAsync(MainWindow shell, MediaPage page, MediaLibraryTileRow row, DesktopSessionHost host, string phase,
        bool removedWatchers, bool screenshots)
    {
        Require(host.BooruServices is null && host.Config.ShowBooruTools, phase + ": local details start without a provider composition.");
        BooruMediaDetailsDialog dialog = await OpenDetailsAsync(shell, page, row, phase);
        string post = row.Entry.BooruPostId!;
        Require(dialog.Title == Texts.T("booru.media.detail.title", post) && shell.OwnedWindows.Contains(dialog), phase + ": details dialog title or owner.");
        // The dialog starts on its first action, not nowhere (1.1.0 audit M6); it has no Close button (modals plan MD2: × and Escape close it).
        await UntilAsync(() => dialog.FocusManager?.GetFocusedElement() is Button { Name: "BooruOpenPost" });

        string[] Lines(string name) => dialog.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == name).Select(text => text.Text ?? string.Empty).ToArray();
        Border Chip(string name) => dialog.GetVisualDescendants().OfType<Border>().Single(border => border.Name == name);
        Require(Chip("BooruBadgeSource").GetVisualDescendants().OfType<TextBlock>().Single().Text == Texts.T("booru.media.badge"), phase + ": source badge.");
        Require(Chip("BooruBadgeRating").GetVisualDescendants().OfType<TextBlock>().Single().Text!
            == Texts.T("booru.rating.label") + ": " + BooruMessages.Rating(BooruPostClaimRating.General), phase + ": rating badge.");
        Require(Chip("BooruBadgeOriginal").GetVisualDescendants().OfType<TextBlock>().Single().Text!.EndsWith(Texts.T("booru.media.variant.Stored")), phase + ": original badge.");
        // Original saved here; sample and preview are only claims of the provider and never replace it.
        string[] versions = Lines("BooruVersionsLine");
        Require(versions.Length == 3 && versions[0].Contains(Texts.T("booru.media.variant.Stored")) && versions.Skip(1).All(line => line.Contains(Texts.T("booru.media.variant.ProviderClaimOnly"))),
            phase + ": version lines: " + string.Join(" | ", versions));
        // Provider data stays apart from the user's own state.
        string[] provider = Lines("BooruProviderLine");
        Require(provider.Any(line => line.Contains("42")) && provider.Any(line => line.Contains("https://source.example/art/125")),
            phase + ": provider score and source claim: " + string.Join(" | ", provider));
        // Source tags: read-only, grouped by the provider's categories as selectable lists, never a text field or tag chip.
        string[] categories = Lines("BooruSourceTagCategory");
        foreach (BooruPostClaimTagCategory category in new[] { BooruPostClaimTagCategory.Artist, BooruPostClaimTagCategory.Copyright,
            BooruPostClaimTagCategory.Character, BooruPostClaimTagCategory.General, BooruPostClaimTagCategory.Meta })
            Require(categories.Contains(BooruMessages.TagCategory(category)), phase + ": source tag category " + category + ": " + string.Join(" | ", categories));
        string[] general = TagList(dialog, BooruPostClaimTagCategory.General).Items.OfType<ListBoxItem>().Select(item => (string)item.Tag!).Order().ToArray();
        Require(general.SequenceEqual(new[] { "1girl", "blue_sky" })
            && TagList(dialog, BooruPostClaimTagCategory.Artist).Items.OfType<ListBoxItem>().Select(item => (string)item.Tag!).SequenceEqual(new[] { "artist_name" })
            && TagList(dialog, BooruPostClaimTagCategory.Meta).Items.OfType<ListBoxItem>().Select(item => (string)item.Tag!).SequenceEqual(new[] { "highres" }),
            phase + ": source tag lists: " + string.Join(" | ", general));
        Require(!dialog.GetVisualDescendants().OfType<TextBox>().Any(), phase + ": the details must not offer a field that could write private tags.");
        Require(Lines("BooruOwnTagsNote").Single() == Texts.T("booru.media.ownTags.unavailable"), phase + ": own-tag explanation.");
        Require(host.GetPrivateTagSnapshot().Snapshot!.Tags.Count == 0, phase + ": provider tags must never become private tags.");
        // Every finding of the post is listed: a search and two watchers.
        string[] findings = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Where(text => text.Name == "BooruFindingsLine").Select(text => text.Text!).ToArray();
        Require(findings.Length == 3, phase + ": post 125 has three findings: " + string.Join(" | ", findings));
        string removed = Texts.T("booru.media.finding.watcherRemoved");
        Require(findings.Count(line => line.Contains(removed)) == (removedWatchers ? 2 : 0), phase + ": watcher standing of the findings: " + string.Join(" | ", findings));

        // Open and copy use only the canonical post address; no browser is started.
        List<Uri> opened = new();
        shell.OpenProviderPageForTests = url => { opened.Add(url); return Task.CompletedTask; };
        Button open = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BooruOpenPost");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => opened.Count == 1);
        Require(opened[0].AbsoluteUri == $"https://danbooru.donmai.us/posts/{post}", phase + ": the opened address is not the canonical post page: " + opened[0]);
        string copiedPostUrl = "https://danbooru.donmai.us/posts/" + post;
        Button copy = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BooruCopyPost");
        bool copiedPost = await ClipboardChecks.CheckIsolatedCopyAsync(dialog, () =>
        {
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        }, new[] { copiedPostUrl }, Array.Empty<string>(), exactText: copiedPostUrl);
        if (copiedPost)
            await UntilAsync(() => dialog.Status.Text == Texts.T("booru.media.copied"));
        // Without the isolated fixture the helper uses the existing technical-copy skip convention and does not touch native
        // clipboard state; a copyFailed notice is never accepted as a successful smoke result.

        List<string> copied = await SourceTagActionsAsync(shell, dialog, host, post, phase, searchAvailable: false);

        if (screenshots)
        {
            dialog.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "BooruSourceTags").BringIntoView();
            foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                await SetThemeAsync(theme, shell, dialog);
                Program.CaptureWindow(dialog, "smoke-booru-media-details-" + name + ".png");
            }
        }
        // The old fixture installed services only now. A dialog opened without them must stay local,
        // even when services appear later; it must not grow a handoff or silently copy a search.
        BooruUiHarness late = new();
        try
        {
            host.BooruServices = late.Services;
            TagList(dialog, BooruPostClaimTagCategory.Character).SelectedItems!.Clear();
            Mark(dialog, BooruPostClaimTagCategory.Character, "main_character");
            RequireSearchAbsent(dialog, phase + " / late composition");
            Require(copied.Count == 7 && dialog.SelectedTagNames().Count == 2 && shell.BooruPageForTests is null,
                phase + ": late services must not copy, drop tags or open a Booru page for the old dialog.");
            RequireNoDraftActions(late, phase + " / late composition");
        }
        finally
        {
            dialog.Close();
            host.BooruServices = null;
            shell.OpenProviderPageForTests = null;
            shell.CopyTextForTests = null;
        }
        await UntilAsync(() => shell.BooruDetailsWindow == null);
        await UseAsSearchInDanbooruPageAsync(shell, page, row, host, phase, screenshots);
        await SearchToolsOffAsync(shell, page, row, host, phase);
        foreach (SearchRevocation revocation in Enum.GetValues<SearchRevocation>())
            await SearchRevokedAsync(shell, page, row, host, phase, revocation);
    }

    private static void RequireSearchAbsent(BooruMediaDetailsDialog dialog, string phase) =>
        Require(!dialog.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "BooruUseAsSearch")
            && !dialog.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "BooruUseAsSearch"),
            phase + ": unavailable/off details must not contain a search action, not even a disabled copy fallback.");

    private static void RequireNoDraftActions(BooruUiHarness harness, string phase)
    {
        Require(harness.Search.Requests == 0 && harness.Previews.Requests == 0
            && harness.Queue.Loads.Count == 0 && harness.Queue.StateReads == 0 && harness.Watchers.Checks == 0,
            phase + ": a draft must not request provider data, previews, queue state or originals, or check watchers.");
        // First activation may read the local watcher list; no watcher command may run.
        Require(harness.Watchers.Calls.All(call => call == "list") && harness.Watchers.Snapshot.Count == 0,
            phase + ": a draft must not create, pause, resume, remove or restart a watcher.");
    }

    // Services are captured when details open. The real shell page receives an unsent draft; no
    // standalone workbench window, provider request, queue action or watcher mutation is involved.
    private static async Task UseAsSearchInDanbooruPageAsync(MainWindow shell, MediaPage media, MediaLibraryTileRow row,
        DesktopSessionHost host, string phase, bool screenshots)
    {
        BooruUiHarness harness = new();
        host.BooruServices = harness.Services;
        BooruMediaDetailsDialog? dialog = null;
        try
        {
            dialog = await OpenDetailsAsync(shell, media, row, phase + " / available");
            List<string> copied = await SourceTagActionsAsync(shell, dialog, host, row.Entry.BooruPostId!, phase + " / available", searchAvailable: true);
            string query = string.Join(' ', dialog.SelectedTagNames());
            Require(dialog.SelectedTagNames().Count == 2 && shell.BooruPageForTests is null,
                phase + ": two marked tags and no Booru page before the first handoff.");
            C<Button>(dialog, "BooruUseAsSearch").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ContentControl pageHost = shell.FindControl<ContentControl>("BooruPageHost")!;
            await UntilAsync(() => shell.BooruDetailsWindow == null && shell.SelectedPage == MainWindow.WorkspacePage.Booru
                && pageHost.IsVisible && shell.BooruPageForTests is { IsEffectivelyVisible: true });
            BooruPage page = shell.BooruPageForTests!;
            Require(ReferenceEquals(pageHost.Content, page) && ReferenceEquals(page.Services, harness.Services)
                && ReferenceEquals(page.View.Model, page.Model) && ReferenceEquals(TopLevel.GetTopLevel(page.View), shell),
                phase + ": the exact originating services must own the actual shell-hosted Booru view.");
            TextBox input = C<TextBox>(page.View, BooruWorkbenchView.InputName);
            await UntilAsync(() => input.IsEffectivelyVisible && input.Text == query);
            Require(page.Model.Search.View.Input == query && page.Model.Search.View.Mode == BooruInputMode.Tags
                && page.Model.Search.View.Parsed.Kind == BooruInputKind.Tags,
                phase + ": the Booru page input and model must hold exactly the marked tags: '" + page.Model.Search.View.Input + "'");
            Require(page.Model.Search.View.Status == BooruSearchStatus.Idle && page.Model.Search.View.ExecutedQuery is null
                && page.Model.Search.View.ExecutedPostId is null && page.Model.Search.View.Rows.IsEmpty && copied.Count == 7,
                phase + ": the handoff is only a draft, not an executed search or implicit copy.");
            await page.ActivateAsync().WaitAsync(Deadline);
            RequireNoDraftActions(harness, phase + " / page prefill");
            Require(host.GetPrivateTagSnapshot().Snapshot!.Tags.Count == 0, phase + ": prefilling a provider query must not create private tags.");
            if (screenshots)
            {
                await SetThemeAsync(ThemeVariant.Light, shell);
                Program.CaptureWindow(shell, "smoke-booru-media-use-as-search.png");
            }
        }
        finally
        {
            dialog?.Close();
            shell.CopyTextForTests = null;
            host.BooruServices = null;
        }
        await ShowMediaAsync(shell);
    }

    private static async Task SetBooruToolsViaShellAsync(MainWindow shell, DesktopSessionHost host, bool visible, string phase)
    {
        Require(host.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEffectivelyEnabled,
            phase + ": the shell must be restored and interactive before changing Booru presentation.");
        // F1: the footer check box is gone; the switch is "Show Booru actions" in Settings > Appearance > Footer. Change it there like a person
        // does, then return to the page the shell was on (Booru itself goes back to Downloads, as the page is unavailable while the tools are off).
        MainWindow.WorkspacePage before = shell.SelectedPage == MainWindow.WorkspacePage.Booru || shell.SelectedPage == MainWindow.WorkspacePage.Settings
            ? MainWindow.WorkspacePage.Downloads : shell.SelectedPage;
        Require(shell.FindControl<CheckBox>("ShowBooruToolsToggle") == null, phase + ": the footer still has the Show Booru check box.");
        await SmokeNavigation.GoToAsync(shell, MainWindow.WorkspacePage.Settings);
        SettingsEditor settings = shell.SettingsEditor;
        await UntilAsync(() => !settings.IsBusy);
        settings.RefreshConfirmedFooter(); // the draft shows the confirmed value even if the setting was changed behind the editor (owner-level revocations)
        Require(settings.ShowBooruActions == !visible, phase + ": the Settings page does not show the opposite of the requested Booru presentation.");
        settings.ShowBooruActions = visible;
        settings.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !settings.IsBusy && host.Config.ShowBooruTools == visible && settings.ShowBooruActions == visible);
        await SmokeNavigation.GoToAsync(shell, before);
    }

    private static async Task<BooruPage> OpenBooruFromShellAsync(MainWindow shell, string phase)
    {
        // F1: the pages left the ⋯ menu; Booru is hidden in the footer by default, so the page is reached through the More menu.
        Require(SmokeNavigation.Route(shell, MainWindow.WorkspacePage.Booru) is "footer" or "more" && SmokeNavigation.IsEnabled(shell, MainWindow.WorkspacePage.Booru),
            phase + ": the footer and More menu did not expose the Booru entry.");
        await SmokeNavigation.GoToAsync(shell, MainWindow.WorkspacePage.Booru);
        ContentControl pageHost = shell.FindControl<ContentControl>("BooruPageHost")!;
        await UntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Booru
            && pageHost.IsVisible && shell.BooruPageForTests is not null);
        BooruPage page = shell.BooruPageForTests!;
        Require(ReferenceEquals(pageHost.Content, page) && TopLevel.GetTopLevel(page.View) == shell,
            phase + ": the footer route did not open its own hosted Booru page.");
        return page;
    }

    private static void RequireBooruPresentation(MainWindow shell, bool visible, MainWindow.WorkspacePage selected,
        bool pageHostVisible, bool pageRetained, string phase)
    {
        ContentControl pageHost = shell.FindControl<ContentControl>("BooruPageHost")!;
        // The Danbooru tools entry of the ⋯ menu (the pages have no entry there since F1), the Booru entry of More and the footer button.
        MenuItem appEntry = shell.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.OfType<MenuItem>()
            .Single(item => item.Header as string == MeowChan.Desktop.Commands.CommandCatalog.Label("booru.open"));
        MenuItem overflowEntry = shell.FindControl<MenuItem>("BooruMoreMenuItem")!;
        Button navigationEntry = shell.FindControl<Button>("BooruNavigationButton")!;
        bool entryVisible = appEntry.IsVisible || overflowEntry.IsVisible || navigationEntry.IsVisible;
        Require(shell.SelectedPage == selected && pageHost.IsVisible == pageHostVisible
            && (shell.BooruPageForTests is not null) == pageRetained && entryVisible == visible,
            phase + ": Booru presentation is inconsistent (selected=" + shell.SelectedPage
            + ", host=" + pageHost.IsVisible + ", page=" + (shell.BooruPageForTests != null)
            + ", appEntry=" + appEntry.IsVisible + ", overflowEntry=" + overflowEntry.IsVisible
            + ", navigationEntry=" + navigationEntry.IsVisible + ").");
    }

    // Hiding the tools is independent of source availability. The presentation is changed through
    // Settings > Appearance > Footer "Show Booru actions" (F1; the footer check box is gone), not by writing the preference behind the shell's back. The
    // modal details window is closed before the setting is restored because its owner is not a
    // user-reachable surface while ShowDialog is active.
    private static readonly MethodInfo ReconcileBooruChanged = typeof(MainWindow).GetMethod("OnBooruChanged",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The Booru shell composition callback is missing.");

    private static void ReconcileBooruPresentation(MainWindow shell) => ReconcileBooruChanged.Invoke(shell, null);

    private static async Task SearchToolsOffAsync(MainWindow shell, MediaPage media, MediaLibraryTileRow row,
        DesktopSessionHost host, string phase)
    {
        BooruUiHarness harness = new();
        host.BooruServices = harness.Services;
        ReconcileBooruPresentation(shell);
        await UntilAsync(() => shell.BooruPageForTests is null);
        BooruMediaDetailsDialog? dialog = null;
        bool visible = host.Config.ShowBooruTools;
        Exception? failureBeforeCleanup = null;
        try
        {
            Require(visible, phase + ": the tools-off fixture must start with the presentation enabled.");
            BooruPage page = await OpenBooruFromShellAsync(shell, phase + " / menu open");
            await SetBooruToolsViaShellAsync(shell, host, false, phase + " / shell toggle off");
            RequireBooruPresentation(shell, visible: false, selected: MainWindow.WorkspacePage.Downloads,
                pageHostVisible: false, pageRetained: true, phase + " / shell toggle off");
            await ShowMediaAsync(shell);
            RequireBooruPresentation(shell, visible: false, selected: MainWindow.WorkspacePage.Media,
                pageHostVisible: false, pageRetained: true, phase + " / tools off media");
            Require(ReferenceEquals(shell.FindControl<ContentControl>("BooruPageHost")!.Content, page),
                phase + ": hiding the Booru presentation discarded the hosted page.");
            dialog = await OpenDetailsAsync(shell, media, row, phase + " / tools off");
            List<string> copied = await SourceTagActionsAsync(shell, dialog, host, row.Entry.BooruPostId!, phase + " / tools off", searchAvailable: false);
            Require(!host.Config.ShowBooruTools && dialog.IsVisible && shell.SelectedPage == MainWindow.WorkspacePage.Media,
                phase + ": the modal tools-off details view changed the shell presentation.");
            RequireNoDraftActions(harness, phase + " / tools off");
            dialog.Close();
            dialog = null;
            await UntilAsync(() => shell.BooruDetailsWindow == null);
            await SetBooruToolsViaShellAsync(shell, host, true, phase + " / shell toggle on");
            RequireBooruPresentation(shell, visible: true, selected: MainWindow.WorkspacePage.Media,
                pageHostVisible: false, pageRetained: true, phase + " / shell toggle on");
            Require(copied.Count == 7, phase + ": the tools-off details actions copied an unexpected number of tag payloads.");
            await OpenBooruFromShellAsync(shell, phase + " / menu reuse");
            Require(ReferenceEquals(shell.FindControl<ContentControl>("BooruPageHost")!.Content, page),
                phase + ": re-enabling the presentation replaced the retained page.");
            await ShowMediaAsync(shell);
            RequireBooruPresentation(shell, visible: true, selected: MainWindow.WorkspacePage.Media,
                pageHostVisible: false, pageRetained: true, phase + " / restored presentation");
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                dialog?.Close();
                shell.CopyTextForTests = null;
                if (dialog != null) await UntilAsync(() => shell.BooruDetailsWindow == null);
                if (host.BooruServices != null && host.Config.ShowBooruTools != visible)
                    await SetBooruToolsViaShellAsync(shell, host, visible, phase + " / restore shell presentation");
                host.BooruServices = null;
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in BooruMediaChecks (" + phase + "): " + exception));
        }
        await UntilAsync(() => shell.BooruDetailsWindow == null);
    }

    private enum SearchRevocation { Unavailable, ToolsOff, Replaced }

    // Exercise the retained button's click-time gate before a selection update could hide it.
    // Even another composition of the same provider is a different owner: its draft must survive.
    private static async Task SearchRevokedAsync(MainWindow shell, MediaPage media, MediaLibraryTileRow row,
        DesktopSessionHost host, string phase, SearchRevocation revocation)
    {
        phase += " / revoked " + revocation;
        BooruUiHarness opened = new(), replacement = new();
        host.BooruServices = opened.Services;
        BooruMediaDetailsDialog? dialog = null;
        bool visible = host.Config.ShowBooruTools;
        try
        {
            const string originalDraft = "origin_draft", replacementDraft = "replacement_draft";
            Require(shell.UseAsBooruSearch(originalDraft), phase + ": the originating composition must have a reachable page.");
            BooruPage originalPage = shell.BooruPageForTests!;
            await originalPage.ActivateAsync().WaitAsync(Deadline);
            await ShowMediaAsync(shell);
            dialog = await OpenDetailsAsync(shell, media, row, phase);
            Mark(dialog, BooruPostClaimTagCategory.Artist, "artist_name");
            Mark(dialog, BooruPostClaimTagCategory.Character, "main_character");
            Button search = C<Button>(dialog, "BooruUseAsSearch");
            Require(search.IsVisible && search.IsEnabled, phase + ": the originating composition must initially offer the search action.");
            List<string> copied = new();
            shell.CopyTextForTests = text => { copied.Add(text); return Task.CompletedTask; };
            BooruPage page = originalPage;
            string draft = originalDraft;
            switch (revocation)
            {
                case SearchRevocation.Unavailable:
                    host.BooruServices = null;
                    Require(host.BooruServices is null, phase + ": the provider services must really be unavailable.");
                    break;
                case SearchRevocation.ToolsOff:
                    // The details window is modal, so the owning shell toggle is not user-reachable here. Keep this
                    // owner-level revocation for the stale click-time gate; SearchToolsOffAsync covers the real shell path.
                    await host.SaveViewPreferencesAsync(config => config.ShowBooruTools = false).WaitAsync(Deadline);
                    Require(!host.Config.ShowBooruTools, phase + ": the tools must really be off.");
                    break;
                case SearchRevocation.Replaced:
                    host.BooruServices = replacement.Services;
                    Require(shell.UseAsBooruSearch(replacementDraft), phase + ": the replacement composition must have a reachable page.");
                    page = shell.BooruPageForTests!;
                    draft = replacementDraft;
                    await page.ActivateAsync().WaitAsync(Deadline);
                    Require(!ReferenceEquals(page, originalPage) && ReferenceEquals(page.Services, replacement.Services)
                        && page.Model.Search.View.Input == draft,
                        phase + ": a distinct page must belong to the replacement services and start with its own draft.");
                    break;
            }
            BooruPage? currentPage = shell.BooruPageForTests;
            MainWindow.WorkspacePage selectedPage = shell.SelectedPage;
            search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(ReferenceEquals(shell.BooruDetailsWindow, dialog) && dialog.IsVisible && !search.IsVisible && !search.IsEnabled,
                phase + ": the stale search action must disappear and leave details open.");
            Require(ReferenceEquals(shell.BooruPageForTests, currentPage) && shell.SelectedPage == selectedPage
                && page.Model.Search.View.Input == draft && C<TextBox>(page.View, BooruWorkbenchView.InputName).Text == draft && copied.Count == 0
                && dialog.SelectedTagNames().Count == 2,
                phase + ": a stale dialog must not overwrite a page draft, silently copy, or drop selected tags.");
            C<Button>(dialog, "BooruCopySelectedTags").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => copied.Count == 1);
            Require(copied[0] == string.Join(' ', dialog.SelectedTagNames())
                && dialog.TagStatus.Text == Texts.Count("booru.media.tags.copied", 2) && page.Model.Search.View.Input == draft,
                phase + ": explicit Copy must remain available after revocation, without touching a page draft.");
            RequireNoDraftActions(opened, phase + " / old composition");
            RequireNoDraftActions(replacement, phase + " / replacement composition");
            Require(host.GetPrivateTagSnapshot().Snapshot!.Tags.Count == 0, phase + ": stale handoff and explicit Copy must not create private tags.");
        }
        finally
        {
            dialog?.Close();
            shell.CopyTextForTests = null;
            host.BooruServices = null;
            if (host.Config.ShowBooruTools != visible)
                await host.SaveViewPreferencesAsync(config => config.ShowBooruTools = visible).WaitAsync(Deadline);
        }
        await UntilAsync(() => shell.BooruDetailsWindow == null);
        await ShowMediaAsync(shell);
    }

    private static ListBox TagList(Window dialog, BooruPostClaimTagCategory category) =>
        dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "BooruSourceTagList:" + category);

    private static void Mark(Window dialog, BooruPostClaimTagCategory category, string tag) =>
        TagList(dialog, category).SelectedItems!.Add(TagList(dialog, category).Items.OfType<ListBoxItem>().Single(item => (string)item.Tag! == tag));

    // Explicit Copy works independently of search availability. When a handoff exists, the
    // two-tag search limit is still enforced; the final valid draft is submitted by the caller.
    private static async Task<List<string>> SourceTagActionsAsync(MainWindow shell, BooruMediaDetailsDialog dialog, DesktopSessionHost host,
        string post, string phase, bool searchAvailable)
    {
        List<string> copied = new();
        shell.CopyTextForTests = text => { copied.Add(text); return Task.CompletedTask; };
        BooruMediaDetails details = BooruMediaProjection.Find(host.ReadBooruCatalog()!, post)!;
        Button Named(string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        async Task ClickAsync(string name, int expectedCopies)
        {
            Named(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => copied.Count >= expectedCopies);
        }
        TextBlock hint = dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "BooruSourceTagSelection");

        // Reachable from the keyboard and named for assistive technology; nothing is marked yet.
        string[] names = searchAvailable
            ? ["BooruCopyAllTags", "BooruCopySelectedTags", "BooruUseAsSearch"]
            : ["BooruCopyAllTags", "BooruCopySelectedTags"];
        if (!searchAvailable) RequireSearchAbsent(dialog, phase);
        foreach (BooruPostClaimTagCategory category in details.Tags.Select(tag => tag.Category).Distinct())
        {
            Button copyCategory = Named("BooruCopyCategory:" + category);
            Require(copyCategory.Focusable && copyCategory.IsTabStop && copyCategory.IsEnabled
                && (AutomationProperties.GetName(copyCategory) ?? string.Empty).Contains(BooruMessages.TagCategory(category)), phase + ": the category copy button for " + category + ".");
            Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(TagList(dialog, category))) 
                && KeyboardNavigation.GetTabNavigation(TagList(dialog, category)) == KeyboardNavigationMode.Once
                && TagList(dialog, category).Items.OfType<ListBoxItem>().All(item => item.Focusable),
                phase + ": the tag list for " + category + " must be one tab stop with focusable items: tab navigation "
                + KeyboardNavigation.GetTabNavigation(TagList(dialog, category)));
        }
        foreach (string name in names) Require(Named(name).Focusable && Named(name).IsTabStop && !string.IsNullOrWhiteSpace(Named(name).Content as string), phase + ": " + name + " must be a named tab stop.");
        Require(Named("BooruCopyAllTags").IsEnabled && !Named("BooruCopySelectedTags").IsEnabled
            && (!searchAvailable || !Named("BooruUseAsSearch").IsEnabled)
            && hint.Text == Texts.T("booru.media.tags.selection.none"), phase + ": initial tag action state.");

        // All source tags, in the provider's spelling and one space apart; one category alone.
        await ClickAsync("BooruCopyAllTags", 1);
        Require(copied[^1] == string.Join(' ', details.Tags.Select(tag => tag.Name)) && copied[^1].Split(' ').Length == 6,
            phase + ": copy all source tags: '" + copied[^1] + "'");
        Require(dialog.TagStatus.Text == Texts.Count("booru.media.tags.copied", 6), phase + ": copy status: " + dialog.TagStatus.Text);
        await ClickAsync("BooruCopyCategory:" + BooruPostClaimTagCategory.General, 2);
        Require(copied[^1] == string.Join(' ', details.Tags.Where(tag => tag.Category == BooruPostClaimTagCategory.General).Select(tag => tag.Name)) && copied[^1].Split(' ').Length == 2,
            phase + ": copy one category: '" + copied[^1] + "'");

        // Marked tags: one tag, then two from different categories (a valid search), via buttons and the copy shortcut.
        Mark(dialog, BooruPostClaimTagCategory.Meta, "highres");
        Require(Named("BooruCopySelectedTags").IsEnabled && hint.Text == Texts.Count("booru.media.tags.selection.count", 1), phase + ": one marked tag: " + hint.Text);
        await ClickAsync("BooruCopySelectedTags", 3);
        Require(copied[^1] == "highres", phase + ": copy one tag: '" + copied[^1] + "'");
        TagList(dialog, BooruPostClaimTagCategory.Meta).SelectedItems!.Clear();
        Mark(dialog, BooruPostClaimTagCategory.Artist, "artist_name");
        Mark(dialog, BooruPostClaimTagCategory.Character, "main_character");
        string query = string.Join(' ', dialog.SelectedTagNames());
        Require(query.Split(' ').OrderBy(x => x).SequenceEqual(new[] { "artist_name", "main_character" }) && hint.Text == Texts.Count("booru.media.tags.selection.count", 2),
            phase + ": two marked tags: '" + query + "' / " + hint.Text);
        if (searchAvailable) Require(Named("BooruUseAsSearch").IsEnabled, phase + ": two marked tags must enable the real search handoff.");
        else RequireSearchAbsent(dialog, phase + " / two marked tags");
        await ClickAsync("BooruCopySelectedTags", 4);
        Require(copied[^1] == query && dialog.TagStatus.Text == Texts.Count("booru.media.tags.copied", 2),
            phase + ": explicit copy of the two-tag query: '" + copied[^1] + "' / " + dialog.TagStatus.Text);
        TagList(dialog, BooruPostClaimTagCategory.Artist).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.C, KeyModifiers = KeyModifiers.Control });
        await UntilAsync(() => copied.Count >= 5);
        Require(copied[^1] == query, phase + ": the copy shortcut in a tag list must copy the marked tags: '" + copied[^1] + "'");

        // A third tag cannot become a Danbooru search: stated, nothing dropped, nothing copied as a search; plain copy still works.
        Mark(dialog, BooruPostClaimTagCategory.General, "blue_sky");
        Require(hint.Text == Texts.Count("booru.media.tags.selection.tooMany", 3, 3, 2), phase + ": three marked tags: " + hint.Text);
        if (searchAvailable)
        {
            Named("BooruUseAsSearch").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => dialog.TagStatus.Text == Texts.T("booru.media.tags.search.tooMany", 2, 3));
            Require(ReferenceEquals(shell.BooruDetailsWindow, dialog) && shell.SelectedPage == MainWindow.WorkspacePage.Media,
                phase + ": refusing three tags must leave details open without navigating to Booru.");
        }
        else RequireSearchAbsent(dialog, phase + " / three marked tags");
        Require(copied.Count == 5 && dialog.SelectedTagNames().Count == 3, phase + ": a refused search must not copy or drop tags.");
        await ClickAsync("BooruCopySelectedTags", 6);
        Require(copied[^1].Split(' ').Length == 3, phase + ": plain copy of three tags: '" + copied[^1] + "'");

        // A failing clipboard is reported, never silent.
        shell.CopyTextForTests = _ => throw new InvalidOperationException("clipboard unavailable");
        Named("BooruCopyAllTags").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => dialog.TagStatus.Text == Texts.T("booru.media.tags.copyFailed"));
        shell.CopyTextForTests = text => { copied.Add(text); return Task.CompletedTask; };

        // Back to two marked tags, which is also what the screenshots show.
        TagList(dialog, BooruPostClaimTagCategory.General).SelectedItems!.Clear();
        Require(dialog.SelectedTagNames().Count == 2, phase + ": unmarking the third tag.");
        // C1: source tags are dashed chips with a category mark; a marked one turns solid with a 2-px accent outline (not tint only).
        ChipOutline Outline(BooruPostClaimTagCategory category, string tag) =>
            TagList(dialog, category).Items.OfType<ListBoxItem>().Single(item => (string)item.Tag! == tag).GetVisualDescendants().OfType<ChipOutline>().Single();
        await UntilAsync(() => { dialog.UpdateLayout(); return !Outline(BooruPostClaimTagCategory.Artist, "artist_name").IsDashed; });
        ChipOutline marked = Outline(BooruPostClaimTagCategory.Artist, "artist_name"), plain = Outline(BooruPostClaimTagCategory.General, "blue_sky");
        Require(marked.StrokeThickness == 2 && plain.IsDashed && plain.StrokeThickness == 1 && plain.Classes.Contains("chip-source-general")
            && marked.Classes.Contains("chip-source-artist") && marked.Bounds.Height >= 20
            && AutomationProperties.GetName(TagList(dialog, BooruPostClaimTagCategory.General).Items.OfType<ListBoxItem>().Single(item => (string)item.Tag! == "blue_sky")) == "blue_sky",
            phase + ": source tag chips must be dashed, marked ones solid with a 2-px outline, and keep the tag name.");
        await ClickAsync("BooruCopySelectedTags", 7);
        Require(dialog.TagStatus.Text == Texts.Count("booru.media.tags.copied", 2), phase + ": status after recovery: " + dialog.TagStatus.Text);
        Require(host.GetPrivateTagSnapshot().Snapshot!.Tags.Count == 0, phase + ": copying provider tags must never create private tags.");
        return copied;
    }

    private static async Task TagActionAsync(MediaPage page, MediaLibraryTileRow row, DesktopSessionHost host, string phase)
    {
        await SelectAsync(page, row);
        MenuItem tags = await MenuItemAsync(page, "media.tags.edit");
        // TP3 (plan 1.4): the entry stays enabled and the popover states the reason as text; nothing is tagged.
        Require(tags.IsEnabled && tags.Command!.CanExecute(null), phase + ": the tag entry of downloaded provider media must stay enabled and explain itself.");
        tags.Command!.Execute(null);
        page.MediaMenu!.Close();
        await UntilAsync(() => QuickTagPopover.Current is { IsOpen: true, Surface: null });
        Require(QuickTagPopover.Current!.BlockedReason == Texts.T("booru.media.tags.unavailable"),
            phase + ": the popover must say why provider media cannot be tagged: " + QuickTagPopover.Current.BlockedReason);
        QuickTagPopover.Current.Close();
        Require(host.GetPrivateTagSnapshot().Snapshot!.Tags.Count == 0, phase + ": a private tag appeared.");
        await Task.CompletedTask;
    }

    private static async Task GalleryAsync(MainWindow shell, MediaPage page, MediaLibraryTileRow group, DesktopSessionHost host, string phase,
        bool favorite, bool screenshots)
    {
        await SelectAsync(page, group);
        page.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
        await UntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
        GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
        GalleryViewerChecksAccess.Click(viewer, "GalleryToggleContext");
        string post = group.Entry.BooruPostId!;
        await UntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == Texts.T("booru.media.tile.post", post)));
        string[] context = viewer.GetGalleryVisualDescendants().OfType<SelectableTextBlock>().Select(text => text.Text ?? string.Empty).ToArray();
        Require(context.Contains(Texts.T("booru.rating.label") + ": " + BooruMessages.Rating(BooruPostClaimRating.General)),
            phase + ": the gallery must show the provider rating: " + string.Join(" | ", context));
        Require(context.Any(line => line == Texts.Count("booru.media.gallery.tags", post == "125" ? 6 : 1)), phase + ": the gallery must count the provider tags.");
        Require(!context.Contains(Texts.T("gallery.noPost")), phase + ": a provider file is not 'a file without a post'.");
        // A window of provider posts and a chan file is a result list, never "Thread · danbooru /posts/ · <first post>".
        string scope = viewer.GetGalleryVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "GalleryScope").Text ?? string.Empty;
        Require(scope == Texts.T("gallery.results") + " · 3 " + Texts.T("gallery.threads"), phase + ": gallery scope: " + scope);

        ToggleButton favoriteButton = viewer.GetGalleryVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "GalleryFavorite");
        if (favorite)
        {
            GalleryViewerChecksAccess.Click(viewer, "GalleryFavorite");
            await UntilAsync(() => host.GetFavorites().Count == 1);
        }
        else
        {
            Require(host.GetFavorites().Count == 1, phase + ": the favorite did not survive the restart.");
            await UntilAsync(() => favoriteButton.IsChecked == true);
        }

        if (screenshots)
        {
            GalleryViewerChecksAccess.Click(viewer, "GalleryReturnImage");
            foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                await SetThemeAsync(theme, shell);
                GalleryViewerChecksAccess.Click(viewer, "GalleryToggleContext");
                await UntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == Texts.T("booru.media.tile.post", post)));
                Program.CaptureWindow(shell, "smoke-booru-media-gallery-" + name + ".png");
                GalleryViewerChecksAccess.Click(viewer, "GalleryReturnImage");
            }
        }
        GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
        await UntilAsync(() => !galleryHost.IsVisible);
        await shell.GalleryCleanupTask.WaitAsync(Deadline);
    }

    private static async Task FavoritesOnPageAsync(MainWindow shell, MediaPage page, string phase)
    {
        // Away and back: the page reads the profile's favorites again, like a user returning to it.
        shell.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !shell.FindControl<ContentControl>("MediaPageHost")!.IsVisible);
        await ShowMediaAsync(shell);
        CheckBox only = C<CheckBox>(page, "MediaFavoritesOnly");
        only.IsChecked = true;
        await UntilAsync(() => page.ShownEntries.Count == 1);
        Require(page.ShownEntries[0].BooruPostId != null && Rows(page).Single().Tile.Occurrences.Count == 2,
            phase + ": the gallery's favorite must be the media page's favorite, shown on the tile of the equal bytes.");
        only.IsChecked = false;
        Require(page.ShownEntries.Count == 3, phase + ": clearing the favorites filter did not widen the page.");
    }

    private static async Task FirstSessionAsync(MainWindow owner, string profile)
    {
        (DesktopSessionHost host, MainWindow shell, MediaPage page) = await OpenAsync(owner, profile);
        try
        {
            await CheckPageAsync(page, shell, "first session");
            await SourceFilterAsync(page, "first session");
            MediaLibraryTileRow group = Rows(page).Single(row => row.Tile.IsGroup);
            await OccurrencesAsync(page, group, "first session");
            await TagActionAsync(page, group, host, "first session");
            // Light and Dark shots of the page itself and of the gallery and details that belong to it.
            foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                await SetThemeAsync(theme, shell);
                await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>()
                    .Any(view => view.GetVisualDescendants().OfType<Image>().Any(image => image.Source != null)));
                Program.CaptureWindow(shell, "smoke-booru-media-page-" + name + ".png");
            }
            Require(group.Entry.BooruPostId == "125", "The tile of the equal bytes must be shown by the newer post, 125: " + group.Entry.BooruPostId);
            await DetailsAsync(shell, page, group, host, "first session", removedWatchers: false, screenshots: true);
            await GalleryAsync(shell, page, Rows(page).Single(row => row.Tile.IsGroup), host, "first session", favorite: true, screenshots: true);
            await FavoritesOnPageAsync(shell, page, "first session");
            await SetThemeAsync(ThemeVariant.Light, shell);
        }
        finally { await CloseAsync(host, shell); }
    }

    private static async Task RestartedSessionAsync(MainWindow owner, string profile)
    {
        (DesktopSessionHost host, MainWindow shell, MediaPage page) = await OpenAsync(owner, profile);
        try
        {
            await CheckPageAsync(page, shell, "restart");
            await SourceFilterAsync(page, "restart");
            MediaLibraryTileRow group = Rows(page).Single(row => row.Tile.IsGroup);
            await OccurrencesAsync(page, group, "restart");
            await DetailsAsync(shell, page, group, host, "restart", removedWatchers: true, screenshots: false);
            await GalleryAsync(shell, page, group, host, "restart", favorite: false, screenshots: false);
            await ProviderOnlyGalleryAsync(shell, page, "restart");
            await FavoritesOnPageAsync(shell, page, "restart");
        }
        finally { await CloseAsync(host, shell); }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Booru media UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
