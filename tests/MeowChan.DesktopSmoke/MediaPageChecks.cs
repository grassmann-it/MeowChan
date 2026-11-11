using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_MEDIA_PAGE_ONLY. Real host, catalog, bound manifests, verified PNG
// thumbnails and the real navigation button. Managed input only; no Windows/macOS acceptance.
internal static class MediaPageChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
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
            await NavigationAsync(owner);
            Console.WriteLine("SMOKE media page [en]: index build, saved/missing toggle, three orders, verified thumbnail, gallery window, show in thread, cache reuse, navigation button in Simple and Tools menu in Barebones checked.");
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

        // A new page shows the cached index immediately, before its own update finishes.
        MediaPage again = new(host.OpenArchiveReaderAsync, cachePath: cache);
        Window second = new() { Width = 900, Height = 700, Content = again, ShowInTaskbar = false };
        try
        {
            second.Show(owner);
            Task update = again.ActivateAsync();
            Require(again.Snapshot != null && again.ShownEntries.Count == 3, "Cached index was not shown right away.");
            await update.WaitAsync(Deadline);
        }
        finally { again.EndLifetime(); second.Close(); }
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

    private static async Task SeedAsync(string profile)
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

    /// <summary>Writes real PNG files and a manifest that binds them like a capture does; returns the first bound attachment.</summary>
    private static async Task<CapturedAttachment> WriteAsync(string directory, ResourceId resource, params (string Name, string MediaId, string? Post)[] files)
    {
        Directory.CreateDirectory(directory);
        string md5 = Convert.ToBase64String(MD5.HashData(Png)), sha = Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant();
        Dictionary<string, object> entries = new(), assets = new();
        foreach (var file in files)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, file.Name), Png);
            entries[file.Name] = new { MediaIdentifier = file.MediaId, Variant = "original", SourceUrl = "https://offline.invalid/" + file.Name,
                FileName = file.Name, OriginalFileName = file.Name, Length = Png.Length, Md5 = md5, Sha256 = sha };
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
            CapturedAttachment attachment = new CapturedAttachment(0, file.MediaId, "original", "https://offline.invalid/" + file.Name, file.Name,
                ExpectedSize: Png.Length, ExpectedMd5: md5) { ExpectedSha256 = sha };
            MediaProviderMetadata provider = new(file.MediaId, "original", attachment.Url!, file.Name, Png.Length, md5, sha);
            string key = MediaRevisionKeys.CreateAttachmentKey(resource, file.Post, 0, provider);
            bindings[key] = new(new(resource, file.Post, 0, key), MediaRevisionKeys.CreateAcquisitionKey(resource, provider),
                asset.AssetId, provider, new(asset.Length, asset.Md5, asset.Sha256), new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch));
            first ??= attachment;
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings }));
        return first!;
    }

    private static T C<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
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
