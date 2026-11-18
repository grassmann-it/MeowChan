using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Review 15.11.2026 (Tom): the star on a post's file line ("name.jpg (74 KiB) ☆") did nothing, notably in a thread opened from Downloads.
// One real main window over a seeded thread whose posts have saved pictures (the last one an unsaved file); the stars are clicked with OS input
// (XTest, private Xvfb): in the embedded archive reader, on an archive media tile, in the large reader opened the Downloads way, far down the
// recycled list, on the next page, with the media sidebar on (and its header star). Each click must reach the star (not the card behind it),
// change the profile's favourite of exactly that file, and the star must show it; F on the selected post does the same; the unsaved file's
// star is off, muted, and names the reason. A routed Click (the older smokes) never proved the pointer path: the star had no hit surface.
internal static class ReaderPostStarChecks
{
    private const int Posts = 80;
    internal static readonly ResourceId Resource = new("star-smoke", "synthetic", "77");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task SeedAsync(string profile)
    {
        string directory = Path.Combine(profile, "media", "star");
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        List<CapturedPost> posts = new();
        for (int index = 0; index < Posts; index++)
        {
            byte[] bytes = Jpeg(index);
            string file = $"star-{index:D3}.jpg", media = $"star-{index:D3}", url = "https://offline.invalid/" + file;
            await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes);
            entries[file] = new { MediaIdentifier = media, Variant = "original", SourceUrl = url, FileName = file,
                OriginalFileName = file, Length = bytes.Length, Md5 = Convert.ToBase64String(MD5.HashData(bytes)),
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
            posts.Add(new CapturedPost((1000 + index).ToString(), index, PostFieldPresence.All, Html: $"<p>Post {index}</p>",
                Attachments: [new CapturedAttachment(0, media, "original", url, file)]));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        CapturedPost[] bound = await StrictMediaFixture.BindAsync(directory, Resource, posts);
        // The last post's file was never saved: its star is shown unavailable, muted, with the reason in its tooltip.
        bound = [.. bound, new CapturedPost((1000 + Posts).ToString(), Posts, PostFieldPresence.All, Html: "<p>Not saved</p>",
            Attachments: [new CapturedAttachment(0, "star-missing", "original", "https://offline.invalid/star-missing.jpg", "star-missing.jpg")])];
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new(Resource, "https://offline.invalid/77", "Synthetic star thread",
            ThreadSourceState.Active, DateTimeOffset.UnixEpoch, directory, bound));
    }

    internal static async Task RunAsync(MainWindow window)
    {
        ArchivePage page = window.ArchivePage;
        using MediaNativeInput input = new(window);
        List<string> failures = new();
        // A: the archive's own reader (embedded).
        Navigate(window, "Archive", activate: true);
        await page.LoadThreadAsync(Resource);
        await page.MediaLoadTask;
        await Settle(window);
        await ProbeAsync(window, page, input, "archive-embedded-first", failures, index: 0);
        await ProbeKeyAsync(window, page, input, "archive-embedded-key", failures);
        await ProbeTileAsync(window, page, input, failures);
        // B: the Downloads way: the large reader over the Downloads page (MainWindow.OpenDownloadReaderAsync).
        Navigate(window, "Downloads", activate: true);
        await Settle(window);
        typeof(MainWindow).GetMethod("OpenLargeReader", Private, null, [typeof(Control), typeof(IReadOnlyList<ResourceId>)], null)!
            .Invoke(window, [null, new[] { Resource }]);
        await page.LoadThreadAsync(Resource);
        await page.MediaLoadTask;
        await Settle(window);
        await ProbeAsync(window, page, input, "downloads-large-first", failures, index: 0);
        await ProbeAsync(window, page, input, "downloads-large-second-card", failures, index: 1);
        await ProbeKeyAsync(window, page, input, "downloads-large-key", failures);
        // Far down the recycled list of the first page.
        ScrollViewer scroll = PostList(window).GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        await Settle(window);
        await ProbeAsync(window, page, input, "downloads-large-scrolled", failures, index: -1);
        // The next page (a new list, new cards).
        Button next = Named<Button>(window, "NextPostsButton");
        if (next.IsEffectivelyEnabled) { await input.ClickAsync(next); await Settle(window); }
        else failures.Add("next page unavailable");
        await ProbeAsync(window, page, input, "downloads-large-next-page", failures, index: 0);
        await ProbeUnavailableAsync(window, failures);
        // The media sidebar.
        if (!ReaderMediaSidebarRules.IsOn(page.ReaderMedia, true)) page.ToggleMediaSidebar();
        await Settle(window);
        await ProbeAsync(window, page, input, "downloads-large-sidebar", failures, index: 0);
        if (page.MediaSidebar is { } sidebar && sidebar.IsEffectivelyVisible)
            await ProbeSidebarAsync(window, page, input, sidebar, failures);
        typeof(MainWindow).GetMethod("CloseLargeReader", Private)!.Invoke(window, [true]);
        await Settle(window);
        // C: back in the archive's embedded reader.
        Navigate(window, "Archive", activate: false);
        await page.LoadThreadAsync(Resource);
        await page.MediaLoadTask;
        await Settle(window);
        await ProbeAsync(window, page, input, "archive-embedded-again", failures, index: 2);
        Console.WriteLine("SMOKE reader post star: " + (failures.Count == 0 ? "all stars toggled" : string.Join(" | ", failures)));
        if (failures.Count > 0) throw new InvalidOperationException("Reader post stars failed: " + string.Join(" | ", failures));
    }

    private static async Task ProbeAsync(MainWindow window, ArchivePage page, MediaNativeInput input, string label, List<string> failures, int index)
    {
        ListBox list = PostList(window);
        ArchiveAttachmentView[] views = list.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .Where(view => view.IsEffectivelyVisible && view.DataContext is ArchiveAttachmentRow { Attachment.MediaId: not "star-missing" } && Visible(view, list)).ToArray();
        if (views.Length == 0) { failures.Add(label + ": no visible attachment"); return; }
        ArchiveAttachmentView view = index < 0 ? views[^1] : views[Math.Min(index, views.Length - 1)];
        // Bring the star itself into view (a tall card can leave its file line above or below).
        ArchiveFavoriteButton star = view.GetVisualDescendants().OfType<ArchiveFavoriteButton>().Single();
        star.BringIntoView();
        await Settle(window);
        ArchiveAttachmentRow row = (ArchiveAttachmentRow)view.DataContext!;
        GalleryEntry? entry = row.ResolveEntry == null ? null : await row.ResolveEntry(CancellationToken.None);
        bool before = entry != null && window.SessionHost.IsFavorite(entry);
        string state = $"enabled={star.IsEnabled}/{star.IsEffectivelyEnabled} available={row.FavoriteAvailable?.Invoke()} " +
            $"wired={row.ToggleFavoriteAsync != null} checked={star.IsChecked} key={(entry == null ? "none" : FavoriteAssetKey.From(entry.Key.Thread, entry.Asset).ToString())} " +
            $"file={row.Attachment.OriginalFileName}";
        Console.WriteLine($"SMOKE star probe {label} before: {state} favourite={before}");
        if (!star.IsEffectivelyEnabled) { failures.Add(label + ": star disabled (" + state + ")"); return; }
        // The element a real pointer reaches at the star's centre: the star itself (or its template), never the card behind it.
        Point centre = star.TranslatePoint(new Point(star.Bounds.Width / 2, star.Bounds.Height / 2), window) ?? default;
        IInputElement? hit = window.InputHitTest(centre);
        bool onStar = hit is Visual reached && reached.GetSelfAndVisualAncestors().Contains(star);
        Console.WriteLine($"SMOKE star probe {label} hit: {(hit as Control)?.GetType().Name}#{(hit as Control)?.Name} onStar={onStar} classes={string.Join(".", star.Classes)}");
        if (!onStar) failures.Add(label + ": the pointer reaches " + (hit as Control)?.GetType().Name + ", not the star");
        try { await input.ClickAsync(star); }
        catch (Exception error) { failures.Add(label + ": click failed " + error.Message); return; }
        try { await Program.WaitUntilAsync(() => entry != null && window.SessionHost.IsFavorite(entry) != before); }
        catch (TimeoutException) { }
        await Settle(window);
        bool after = entry != null && window.SessionHost.IsFavorite(entry);
        ArchiveFavoriteButton current = view.GetVisualDescendants().OfType<ArchiveFavoriteButton>().Single();
        Console.WriteLine($"SMOKE star probe {label} after: favourite={after} checked={current.IsChecked} enabled={current.IsEnabled}");
        if (after == before) failures.Add(label + ": click did not change the favourite (" + state + ")");
        else if (current.IsChecked != after) failures.Add(label + ": star does not show the favourite");
        // Restore for the next probe.
        if (after != before && entry != null) await RestoreAsync(window, entry, before);
        page.RefreshArchiveFavoriteControls();
        await Settle(window);
    }

    // F (favorites.toggle) on the selected post: the medium the post shows (FavoriteKeys); OS key input on the focused card.
    private static async Task ProbeKeyAsync(MainWindow window, ArchivePage page, MediaNativeInput input, string label, List<string> failures)
    {
        ListBox list = PostList(window);
        ListBoxItem? card = list.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => item.DataContext is ArchivePostRow { Attachments.Count: > 0 } && Visible(item, list));
        if (card?.DataContext is not ArchivePostRow post) { failures.Add(label + ": no visible post"); return; }
        list.SelectedItem = post;
        card.Focus(NavigationMethod.Tab);
        await Settle(window);
        ArchiveAttachmentRow row = post.Attachments[0];
        GalleryEntry? entry = row.ResolveEntry == null ? null : await row.ResolveEntry(CancellationToken.None);
        bool before = entry != null && window.SessionHost.IsFavorite(entry);
        await input.KeyAsync(0x66); // f
        await page.FavoriteKeyTask;
        try { await Program.WaitUntilAsync(() => entry != null && window.SessionHost.IsFavorite(entry) != before); } catch (TimeoutException) { }
        bool after = entry != null && window.SessionHost.IsFavorite(entry);
        Console.WriteLine($"SMOKE star probe {label}: post={post.Id} favourite {before}->{after} focus={window.FocusManager?.GetFocusedElement()?.GetType().Name}");
        if (after == before) failures.Add(label + ": F did not change the favourite of post " + post.Id);
        else if (entry != null) await RestoreAsync(window, entry, before);
        page.RefreshArchiveFavoriteControls();
        await Settle(window);
    }

    // A post whose file is not saved: the star stays in place but is off (no click, muted, no fill) and says why.
    private static async Task ProbeUnavailableAsync(MainWindow window, List<string> failures)
    {
        ListBox list = PostList(window);
        ArchivePostRow? post = list.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault(row => row.Attachments.Any(item => item.Attachment.MediaId == "star-missing"));
        if (post == null) { failures.Add("unavailable: fixture post not on this page"); return; }
        list.ScrollIntoView(post);
        await Settle(window);
        ArchiveAttachmentView? view = list.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .FirstOrDefault(item => item.DataContext is ArchiveAttachmentRow { Attachment.MediaId: "star-missing" });
        if (view == null) { failures.Add("unavailable: card not realized"); return; }
        ArchiveFavoriteButton star = view.GetVisualDescendants().OfType<ArchiveFavoriteButton>().Single();
        Avalonia.Controls.Presenters.ContentPresenter? surface = star.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault();
        Avalonia.Media.Color muted = SmokeThemeResources.Color(window, "Workspace.Muted");
        string? tip = ToolTip.GetTip(star) as string;
        Console.WriteLine($"SMOKE star probe unavailable: enabled={star.IsEffectivelyEnabled} visible={star.IsEffectivelyVisible} tip={tip} " +
            $"foreground={surface?.Foreground} muted={muted} opacity={surface?.Opacity} showOnDisabled={ToolTip.GetShowOnDisabled(star)}");
        if (star.IsEffectivelyEnabled || !star.IsEffectivelyVisible) failures.Add("unavailable: the star of an unsaved file is not shown as off");
        if (tip != MeowChan.Desktop.Localization.Texts.T("archive.notSaved") || !ToolTip.GetShowOnDisabled(star)) failures.Add("unavailable: no reason in the tooltip");
        if (surface == null || (surface.Foreground as Avalonia.Media.ISolidColorBrush)?.Color != muted || surface.Opacity >= 1) failures.Add("unavailable: the off star looks like an enabled one");
    }

    // The archive's "Saved media" tiles: the quiet corner star shows under the pointer and toggles that file.
    private static async Task ProbeTileAsync(MainWindow window, ArchivePage page, MediaNativeInput input, List<string> failures)
    {
        TabControl tabs = Named<TabControl>(window, "ArchiveContentTabs");
        tabs.SelectedIndex = 1;
        page.MediaTilesPreference = true;
        await Settle(window);
        ListBox tiles = Named<ListBox>(window, "ArchiveMediaTiles");
        ArchiveMediaTileView? tile = tiles.GetVisualDescendants().OfType<ArchiveMediaTileView>()
            .FirstOrDefault(view => view.DataContext is ArchiveAssetRow && Visible(view, tiles));
        if (tile?.DataContext is not ArchiveAssetRow row) { failures.Add("tile: no visible tile"); tabs.SelectedIndex = 0; return; }
        GalleryEntry entry = new(new GalleryAssetKey(Resource, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset, null, null, !row.Asset.IsCurrent, true);
        bool before = window.SessionHost.IsFavorite(entry);
        await input.MoveAsync(tile);
        await Settle(window);
        ArchiveFavoriteButton star = tile.GetVisualDescendants().OfType<ArchiveFavoriteButton>().Single();
        Point centre = star.TranslatePoint(new Point(star.Bounds.Width / 2, star.Bounds.Height / 2), window) ?? default;
        bool onStar = window.InputHitTest(centre) is Visual reached && reached.GetSelfAndVisualAncestors().Contains(star);
        Console.WriteLine($"SMOKE star probe tile before: enabled={star.IsEffectivelyEnabled} opacity={star.Opacity} onStar={onStar} favourite={before}");
        if (!star.IsEffectivelyEnabled) { failures.Add("tile: star disabled"); tabs.SelectedIndex = 0; return; }
        await input.ClickAsync(star);
        try { await Program.WaitUntilAsync(() => window.SessionHost.IsFavorite(entry) != before); } catch (TimeoutException) { }
        bool after = window.SessionHost.IsFavorite(entry);
        await Settle(window);
        Console.WriteLine($"SMOKE star probe tile after: favourite={after} checked={star.IsChecked}");
        if (after == before) failures.Add("tile: click did not change the favourite");
        else if (star.IsChecked != after) failures.Add("tile: star does not show the favourite");
        else await RestoreAsync(window, entry, before);
        page.RefreshArchiveFavoriteControls();
        tabs.SelectedIndex = 0;
        await Settle(window);
    }

    private static async Task ProbeSidebarAsync(MainWindow window, ArchivePage page, MediaNativeInput input, ReaderMediaSidebar sidebar, List<string> failures)
    {
        // The sidebar shows the selected post's medium.
        ListBox list = PostList(window);
        if (list.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(item => item.DataContext is ArchivePostRow { Attachments.Count: > 0 } candidate
            && candidate.Attachments[0].Attachment.MediaId != "star-missing" && Visible(item, list))?.DataContext is ArchivePostRow post) list.SelectedItem = post;
        ArchiveFavoriteButton? star = null;
        try
        {
            await Program.WaitUntilAsync(() => (star = sidebar.GetVisualDescendants().OfType<ArchiveFavoriteButton>()
                .FirstOrDefault(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled)) != null);
        }
        catch (TimeoutException) { }
        await Settle(window);
        if (star == null) { failures.Add("sidebar: no star"); return; }
        Console.WriteLine($"SMOKE star probe sidebar before: enabled={star.IsEnabled}/{star.IsEffectivelyEnabled} checked={star.IsChecked}");
        if (!star.IsEffectivelyEnabled) { failures.Add("sidebar: star disabled"); return; }
        bool? before = star.IsChecked;
        await input.ClickAsync(star);
        try { await Program.WaitUntilAsync(() => star.IsChecked != before); } catch (TimeoutException) { }
        Console.WriteLine($"SMOKE star probe sidebar after: checked={star.IsChecked}");
        if (star.IsChecked == before) failures.Add("sidebar: click did not change the star");
        else { await input.ClickAsync(star); try { await Program.WaitUntilAsync(() => star.IsChecked == before); } catch (TimeoutException) { } }
    }

    // Puts a favourite back once the app's own write has returned (the session admits one write of a key at a time).
    private static async Task RestoreAsync(MainWindow window, GalleryEntry entry, bool favourite)
    {
        for (int attempt = 0; ; attempt++)
        {
            await Task.Delay(150);
            try { await window.SessionHost.SetFavoriteAsync(entry, favourite); return; }
            catch (DesktopSessionBusyException) when (attempt < 40) { }
        }
    }

    private static bool Visible(Control view, Control list)
    {
        Point? top = view.TranslatePoint(default, list);
        return top is { } point && point.Y + view.Bounds.Height > 0 && point.Y < list.Bounds.Height;
    }

    private static ListBox PostList(Window window) => Named<ListBox>(window, "ArchivePosts");

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(control => control.Name == name && control.IsEffectivelyVisible);

    private static void Navigate(MainWindow window, string page, bool activate) =>
        typeof(MainWindow).GetMethod("NavigateWorkspace", Private)!.Invoke(window, [Enum.Parse(typeof(MainWindow.WorkspacePage), page), activate]);

    private static async Task Settle(Window window)
    {
        for (int turn = 0; turn < 4; turn++)
        {
            await SmokeDispatcher.SettleAsync(2);
            window.UpdateLayout();
            await Task.Delay(60);
        }
    }

    private static byte[] Jpeg(int index)
    {
        using SKBitmap bitmap = new(160, 120, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (SKCanvas canvas = new(bitmap)) canvas.Clear(new SKColor((byte)(index * 37), (byte)(index * 71), (byte)(255 - index * 3)));
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}
