using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
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
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
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
                + "source tags apart from an unavailable own-tag section, open and copy of the canonical post link, gallery provider context, a favorite shared by gallery and "
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
            Border badge = view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "MediaTileProvider");
            Require(badge.IsVisible == row.IsProviderMedium, phase + ": the Danbooru badge must show on provider tiles only.");
            if (row.IsProviderMedium)
                Require(badge.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("booru.media.badge")), phase + ": badge text.");
        }
        await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>()
            .Any(view => view.GetVisualDescendants().OfType<Image>().Any(image => image.Source != null)));
        Require(shell.FindControl<TextBlock>("ShellActionError")?.Text is null or "", phase + ": the shell shows an error.");
        string status = C<TextBlock>(page, "MediaStatus").Text ?? string.Empty;
        Require(status.Contains(Texts.T("booru.media.statusCount", 3)), phase + ": the status must count the three downloaded posts: " + status);
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
        C<Button>(page, "MediaFilterReset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        await UntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "GalleryScope").Text == Texts.T("booru.media.gallery.scope", 2));
        GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
        await UntilAsync(() => !galleryHost.IsVisible);
        await shell.GalleryCleanupTask.WaitAsync(Deadline);
        C<Button>(page, "MediaFilterReset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

    private static async Task DetailsAsync(MainWindow shell, MediaPage page, MediaLibraryTileRow row, DesktopSessionHost host, string phase,
        bool removedWatchers, bool screenshots)
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
        BooruMediaDetailsDialog dialog = shell.BooruDetailsWindow!;
        string post = row.Entry.BooruPostId!;
        Require(dialog.Title == Texts.T("booru.media.detail.title", post) && shell.OwnedWindows.Contains(dialog), phase + ": details dialog title or owner.");

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
        // Source tags: read-only, grouped by the provider's categories, never a text field or tag chip.
        string[] groups = Lines("BooruSourceTagGroup");
        Require(groups.Any(line => line.StartsWith(BooruMessages.TagCategory(BooruPostClaimTagCategory.Artist) + ": artist_name"))
            && groups.Any(line => line.Contains("blue_sky") && line.Contains("1girl"))
            && groups.Any(line => line.StartsWith(BooruMessages.TagCategory(BooruPostClaimTagCategory.Meta) + ": highres")),
            phase + ": source tag groups: " + string.Join(" | ", groups));
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
        Button copy = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BooruCopyPost");
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => dialog.Status.Text is { Length: > 0 });
        Require(dialog.Status.Text == Texts.T("booru.media.copied") || dialog.Status.Text == Texts.T("booru.media.copyFailed"), phase + ": copy status: " + dialog.Status.Text);
        // The smoke host's clipboard is a write-only fixture, so only the honest outcome is checked; the copied address is the
        // same canonical URL the open action received (see Core tests for its derivation).

        if (screenshots)
        {
            foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                await SetThemeAsync(theme, shell, dialog);
                Program.CaptureWindow(dialog, "smoke-booru-media-details-" + name + ".png");
            }
        }
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "BooruDetailsClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => shell.BooruDetailsWindow == null);
        shell.OpenProviderPageForTests = null;
    }

    private static async Task TagActionAsync(MediaPage page, MediaLibraryTileRow row, DesktopSessionHost host, string phase)
    {
        await SelectAsync(page, row);
        MenuItem tags = await MenuItemAsync(page, "media.tags.edit");
        Require(!tags.IsEnabled, phase + ": own tags for downloaded provider media are not available yet and must be disabled, never silently tagged.");
        page.MediaMenu!.Close();
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
        Require(context.Any(line => line == Texts.T("booru.media.gallery.tags", post == "125" ? 6 : 1)), phase + ": the gallery must count the provider tags.");
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
