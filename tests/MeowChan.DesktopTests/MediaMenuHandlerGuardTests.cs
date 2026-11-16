using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>
/// The guard of the reader audit (16.11.2026, Tom: "right-click on media in the reader → Open in gallery does nothing"): on every surface of the archive's
/// media menu (a post's thumbnail, the Media tab's tiles and its table) the real menu is built for a real page with a saved thread, and every ENABLED entry
/// is run. An entry whose handler finds its precondition false (MediaMenuHandlers.NoOp, the one seam every handler is registered through) or an available
/// action without a handler fails the test, so an enabled no-op cannot come back unnoticed. The fakes stand for the shell; nothing touches a window.
/// </summary>
[Collection("Booru exact navigation")]
public sealed class MediaMenuHandlerGuardTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly ResourceId Thread = new("fixture", "g", "42");

    // ---- the generic guard -------------------------------------------------------------------------------------------------------------------------

    // By name: the surface enum is internal, a public theory cannot take it.
    public static IEnumerable<object[]> ArchiveSurfaces() =>
        new[] { nameof(MediaMenuSurface.ArchiveInline), nameof(MediaMenuSurface.ArchiveTiles), nameof(MediaMenuSurface.ArchiveTable) }.Select(surface => new object[] { surface });

    [Theory, MemberData(nameof(ArchiveSurfaces))]
    public void EveryEnabledEntryOfTheArchiveMediaMenuRunsItsHandler(string surfaceName) => Run(fixture =>
    {
        MediaMenuSurface surface = Enum.Parse<MediaMenuSurface>(surfaceName);
        (ContextMenu menu, List<(MediaMenuSurface, MediaMenuAction)> noOps) = fixture.Menu(surface);
        MenuItem[] enabled = Common(menu).Where(item => item.IsEnabled).ToArray();
        // Everything the fixture wires is on offer: a saved file of one post, a shell that opens galleries, folders, the queue, the popover.
        Assert.Equal(new[] { "gallery.open", "favorites.toggle", "media.rating.edit", "media.queue.add", "media.export-original", "media.show-in-folder",
            "media.copy-path", "media.details", "gallery.show-in-thread" }, enabled.Select(item => (string)item.Tag!).ToArray());
        foreach (MenuItem item in enabled.Where(item => (string)item.Tag! != "gallery.show-in-thread")) // the jump needs the reader's list (smokes)
        {
            Assert.True(item.Command!.CanExecute(null), item.Tag + " looks enabled but refuses");
            item.Command.Execute(null);
        }
        Assert.Empty(noOps);
        Assert.Single(fixture.MediaGalleries);
        Assert.Equal(new[] { fixture.Second.Asset.AssetId }, fixture.Favorites.Select(entry => entry.Asset.AssetId));
        Assert.Single(fixture.Popovers);
        Assert.Single(fixture.Queued);
        Assert.Equal(new[] { fixture.Second.Asset.Path }, fixture.CopiedPaths);
        Assert.Equal(new[] { Path.GetDirectoryName(fixture.Second.Asset.Path) }, fixture.Folders);
    });

    [Fact]
    public void AComposedActionWithoutAHandlerIsReportedInsteadOfShownAsAQuietDisabledEntry()
    {
        List<(MediaMenuSurface, MediaMenuAction)> seen = new();
        MediaMenuHandlers.NoOp = (surface, action) => seen.Add((surface, action));
        try
        {
            Func<Task> handler = MediaMenuHandlers.When(MediaMenuSurface.ArchiveInline, MediaMenuAction.Open, () => false, () => throw new InvalidOperationException());
            Assert.True(handler().IsCompletedSuccessfully);
            Assert.Equal(new[] { (MediaMenuSurface.ArchiveInline, MediaMenuAction.Open) }, seen);
            int ran = 0;
            MediaMenuHandlers.When(MediaMenuSurface.MediaPage, MediaMenuAction.Favorite, () => true, () => { ran++; return Task.CompletedTask; })();
            Assert.Equal(1, ran);
            Assert.Single(seen);
        }
        finally { MediaMenuHandlers.NoOp = null; }
    }

    // ---- Tom's bug: "Open in gallery" on a post's medium ------------------------------------------------------------------------------------------

    [Fact]
    public void OpenInGalleryOnAPostMediumTheMediaTabHidesOpensTheThreadGalleryAtExactlyThatMedium() => Run(fixture =>
    {
        // "Favorites only" on the Media tab hides the unmarked file; the post still shows it. The entry used to be refused ("not in the gallery
        // sequence") although a click on the same thumbnail opened it.
        fixture.Set("_archiveFavoritesOnly", true);
        (ContextMenu menu, var noOps) = fixture.Menu(MediaMenuSurface.ArchiveInline);
        MenuItem open = Common(menu).Single(item => (string?)item.Tag == "gallery.open");
        Assert.True(open.IsEnabled, "Open in gallery is refused for a saved file the post shows: " + ToolTip.GetTip(open));
        open.Command!.Execute(null);
        Assert.Empty(noOps);
        Assert.Empty(fixture.MediaGalleries);
        (ResourceId thread, GalleryEntryKey? key) = Assert.Single(fixture.ThreadGalleries);
        Assert.Equal(Thread, thread);
        Assert.Equal(new GalleryEntryKey(new GalleryAssetKey(Thread, fixture.Second.Snapshot.Directory, fixture.Second.Asset.AssetId), "p2", 0), key);
    });

    [Fact]
    public void OpenInGalleryFromAVisiblePostMediumKeepsTheMediaTabSequenceAndThePostAssociation() => Run(fixture =>
    {
        (ContextMenu menu, var noOps) = fixture.Menu(MediaMenuSurface.ArchiveInline);
        Common(menu).Single(item => (string?)item.Tag == "gallery.open").Command!.Execute(null);
        (IReadOnlyList<GalleryEntry> entries, GalleryEntryKey key) = Assert.Single(fixture.MediaGalleries);
        Assert.Equal("p2", key.PostId);
        Assert.Equal(2, entries.Count);
        Assert.Empty(fixture.ThreadGalleries);
        Assert.Empty(noOps);
    });

    [Fact]
    public void OpenInGalleryIsDisabledWithItsReasonWhenNeitherGalleryCanOpenIt() => Run(fixture =>
    {
        fixture.Set("_archiveFavoritesOnly", true);
        fixture.Page.OpenGallery = null;
        (ContextMenu menu, var noOps) = fixture.Menu(MediaMenuSurface.ArchiveInline);
        MenuItem open = Common(menu).Single(item => (string?)item.Tag == "gallery.open");
        Assert.False(open.IsEnabled);
        Assert.False(open.Command!.CanExecute(null));
        Assert.Empty(noOps);
        // A tile of the Media tab never falls back to the thread: its sequence is the tab's.
        fixture.Page.OpenGallery = (_, _, _) => Task.CompletedTask;
        fixture.Set("_archiveFavoritesOnly", false);
        fixture.Page.OpenMediaGallery = null;
        (ContextMenu tiles, _) = fixture.Menu(MediaMenuSurface.ArchiveTiles);
        Assert.False(Common(tiles).Single(item => (string?)item.Tag == "gallery.open").IsEnabled);
    });

    // ---- the keys of a focused post card ------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithThePostCardFocusedTheTagKeyActsOnThePostsMediumAndTheCopyLinkGestureStaysThePosts() => Run(fixture =>
    {
        ListBox posts = fixture.Page.FindControl<ListBox>("ArchivePosts")!;
        ArchivePostRow post = new("p2", string.Empty, "b", new[] { fixture.Attachment });
        posts.ItemsSource = new[] { post };
        posts.SelectedItem = post;
        MethodInfo route = typeof(ArchivePage).GetMethod("TryRouteArchiveMediaShortcut", Instance)!;
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyEventArgs tag = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.T, KeyModifiers = primary | KeyModifiers.Shift, Source = posts };
        Assert.True((bool)route.Invoke(fixture.Page, new object[] { posts, tag })!, "The tag key on a focused post card did nothing.");
        Assert.True(tag.Handled);
        Assert.Single(fixture.Popovers);
        // Primary+Shift+C is the post's "Copy post link" there, not the medium's file path.
        KeyEventArgs copy = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.C, KeyModifiers = primary | KeyModifiers.Shift, Source = posts };
        Assert.False((bool)route.Invoke(fixture.Page, new object[] { posts, copy })!);
        Assert.False(copy.Handled);
        Assert.Empty(fixture.CopiedPaths);
    });

    [Fact]
    public void AltMInTheReaderShowsTheReadersThreadInMedia() => Run(fixture =>
    {
        List<ResourceId> shown = new();
        fixture.Page.ShowThreadInMediaRequested = (id, _) => shown.Add(id);
        ListBox posts = fixture.Page.FindControl<ListBox>("ArchivePosts")!;
        KeyEventArgs key = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.M, KeyModifiers = KeyModifiers.Alt, Source = posts };
        Assert.True(fixture.Page.TryRouteReaderThreadKey(key));
        Assert.Equal(new[] { Thread }, shown);
    });

    // ---- the fixture ---------------------------------------------------------------------------------------------------------------------------------

    private static IEnumerable<MenuItem> Common(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Where(item => item.Tag is string tag && MediaMenuComposition.Order.Any(action => tag == Id(action)));

    private static string Id(MediaMenuAction action) => action switch
    {
        MediaMenuAction.Open => "gallery.open",
        MediaMenuAction.Favorite => "favorites.toggle",
        MediaMenuAction.RateAndTag => "media.rating.edit",
        MediaMenuAction.Queue => "media.queue.add",
        MediaMenuAction.Export => "media.export-original",
        MediaMenuAction.ShowInFolder => "media.show-in-folder",
        MediaMenuAction.CopyPath => "media.copy-path",
        MediaMenuAction.Details => "media.details",
        MediaMenuAction.ShowInThread => "gallery.show-in-thread",
        _ => action.ToString()
    };

    private static void Run(Action<Fixture> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        // The design tokens the page's XAML looks up, as the product's application has them (no theme, no window).
        TokenApplication.Bind();
        try { test(new Fixture()); }
        finally { MediaMenuHandlers.NoOp = null; }
    });

    private sealed record BoundAsset(MediaAssetSnapshot Snapshot, MediaAssetReference Asset, CapturedAttachment Attachment);

    private sealed class Fixture
    {
        internal ArchivePage Page { get; } = new();
        internal BoundAsset First { get; } = Bound("p1", 0, "first.png");
        internal BoundAsset Second { get; } = Bound("p2", 0, "second.png");
        internal ArchiveAttachmentRow Attachment { get; }
        internal ArchiveAssetRow[] Assets { get; }
        internal List<(IReadOnlyList<GalleryEntry> Entries, GalleryEntryKey Key)> MediaGalleries { get; } = new();
        internal List<(ResourceId Thread, GalleryEntryKey? Key)> ThreadGalleries { get; } = new();
        internal List<GalleryEntry> Favorites { get; } = new();
        internal List<GalleryEntry> Popovers { get; } = new();
        internal List<GalleryEntry> Queued { get; } = new();
        internal List<string> CopiedPaths { get; } = new();
        internal List<string> Folders { get; } = new();

        internal Fixture()
        {
            MediaAssetSnapshot snapshot = new(First.Snapshot.Directory, MediaManifestStatus.Available, 3, "fixture", new[] { First.Asset, Second.Asset },
                attachmentBindings: First.Snapshot.AttachmentBindings.Concat(Second.Snapshot.AttachmentBindings).ToDictionary(pair => pair.Key, pair => pair.Value));
            Assets = snapshot.GetPage().Select(asset => new ArchiveAssetRow(snapshot, asset)).ToArray();
            ArchivedThreadView view = new(new ArchiveThreadSummary(Thread, "https://offline.invalid/g/thread/42", "fixture", snapshot.Directory,
                    ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, null, 2, 1),
                new[] { new CapturedPost("p1", 1, PostFieldPresence.All, Html: "a", Attachments: new[] { First.Attachment }),
                        new CapturedPost("p2", 2, PostFieldPresence.All, Html: "b", Attachments: new[] { Second.Attachment }) },
                decimalPostOrder: false);
            Attachment = new ArchiveAttachmentRow(Second.Attachment);
            Set("_available", true);
            Set("_thread", view);
            Set("_selectedThread", Thread);
            Set("_assets", (IReadOnlyList<ArchiveAssetRow>)Assets);
            Set("_openFolder", (Action<string>)(directory => Folders.Add(directory)));
            Page.OpenMediaGallery = (entries, key, _) => { MediaGalleries.Add((entries, key)); return Task.CompletedTask; };
            Page.OpenGallery = (id, key, _) => { ThreadGalleries.Add((id, key)); return Task.CompletedTask; };
            Page.IsArchiveFavorite = _ => false;
            Page.ToggleArchiveFavoriteAsync = entry => { Favorites.Add(entry); return Task.CompletedTask; };
            Page.TagPopoverAsync = (entry, _, _, _, _) => { Popovers.Add(entry); return Task.CompletedTask; };
            Page.AddToTagQueue = entries => { Queued.AddRange(entries); return Task.FromResult(string.Empty); };
            Page.CopyFilePathAsync = path => { CopiedPaths.Add(path); return Task.CompletedTask; };
        }

        internal void Set(string name, object? value) => typeof(ArchivePage).GetField(name, Instance)!.SetValue(Page, value);

        /// <summary>The real archive media menu of the second post's medium on <paramref name="surface"/>, with the guard's seam listening while it is built and run.</summary>
        internal (ContextMenu Menu, List<(MediaMenuSurface, MediaMenuAction)> NoOps) Menu(MediaMenuSurface surface)
        {
            List<(MediaMenuSurface, MediaMenuAction)> noOps = new();
            MediaMenuHandlers.NoOp = (where, action) => noOps.Add((where, action));
            ArchiveAssetRow asset = Assets.Single(row => row.Asset.AssetId == Second.Asset.AssetId);
            bool inline = surface == MediaMenuSurface.ArchiveInline;
            MethodInfo build = typeof(ArchivePage).GetMethod("BuildArchiveMediaMenu", Instance)!;
            var built = ((ContextMenu Menu, MediaMenuSession Session))build.Invoke(Page,
                new object?[] { asset, inline ? Attachment : null, new Border(), inline ? "p2" : null, surface, null, null })!;
            return (built.Menu, noOps);
        }
    }

    private static BoundAsset Bound(string post, int position, string file)
    {
        string directory = Path.Combine(Path.GetTempPath(), "media-menu-guard-fixture");
        var provider = new MediaProviderMetadata(file, "original", "https://offline.invalid/" + file, file, 3, null, null);
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file))).ToLowerInvariant();
        var actual = new MediaActualIntegrity(3, Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(file))), sha);
        var asset = new MediaAssetReference(sha, file, "original", file, Path.Combine(directory, file), 3, actual.Sha256, provider.Url, file, true)
        { Md5 = actual.Md5 };
        string revision = MediaRevisionKeys.CreateAttachmentKey(Thread, post, position, provider);
        var binding = new MediaAttachmentBinding(new(Thread, post, position, revision),
            MediaRevisionKeys.CreateAcquisitionKey(Thread, provider), sha, provider, actual,
            new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch, "fixture", "owner", Thread.Source, 1));
        var snapshot = new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 3, "fixture", [asset],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [revision] = binding });
        var attachment = new CapturedAttachment(position, file, "original", provider.Url, file, ExpectedSize: 3) { AttachmentRevisionKey = revision };
        return new(snapshot, asset, attachment);
    }
}
