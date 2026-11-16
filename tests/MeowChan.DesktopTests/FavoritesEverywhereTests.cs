using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

/// <summary>
/// Favourites everywhere (Tom's review of 15.11.2026, defaults revision 20): M adds, U removes, F toggles on every surface that shows or selects a medium, mute
/// moved to Shift+M, and every favourite star toggles through the shared favourite call. The keys' table and its yield are in GalleryShortcutSchemeTests and
/// GalleryFavoriteShortcutTests; the routing of a real window is the smokes' (this class proves the decisions and the wiring that needs no window).
/// </summary>
[Collection("Localization")]
public sealed class FavoritesEverywhereTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public FavoritesEverywhereTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    // ---- the keys ----

    [Theory, MemberData(nameof(Platforms))]
    public void MAddsUBRemovesFTogglesAndShiftMIsMuteOnEveryPlatform(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        FavoriteKeyAction Of(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
            FavoriteKeys.Classify(id => bindings.Matches(new KeyEventArgs { Key = key, KeyModifiers = modifiers }, id));
        Assert.Equal(FavoriteKeyAction.Add, Of(Key.M));
        Assert.Equal(FavoriteKeyAction.Add, Of(Key.B));
        Assert.Equal(FavoriteKeyAction.Remove, Of(Key.U));
        Assert.Equal(FavoriteKeyAction.Toggle, Of(Key.F));
        Assert.Equal(FavoriteKeyAction.None, Of(Key.M, KeyModifiers.Shift));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.M, KeyModifiers = KeyModifiers.Shift }, "video.mute"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.M }, "video.mute"));
        // No ⌘ / Ctrl in any of them: macOS keeps ⌘M (minimise), ⌘H, ⌘Q, ⌘W, ⌘, and ⌘Space to itself.
        foreach (string id in GalleryShortcutScheme.FavoriteKeyIds.Append("video.mute"))
            Assert.All(bindings.For(id), gesture => Assert.DoesNotContain(gesture.Modifiers, modifier => modifier is "Primary" or "Meta" or "Control" or "Alt"));
        // The reader and the Media page route mute after their own commands; M no longer reaches the video keys there.
        Assert.Equal(ReaderVideoAction.None, ReaderVideoKeys.Classify(id => bindings.Matches(new KeyEventArgs { Key = Key.M }, id)));
        Assert.Equal(ReaderVideoAction.Mute, ReaderVideoKeys.Classify(id => bindings.Matches(new KeyEventArgs { Key = Key.M, KeyModifiers = KeyModifiers.Shift }, id)));
        Assert.Equal(new MediaPanelKeyRoute(MediaPanelKeyKind.Video, ReaderVideoAction.Mute), MediaPanelKeys.Route(Key.M, KeyModifiers.Shift,
            id => bindings.Matches(new KeyEventArgs { Key = Key.M, KeyModifiers = KeyModifiers.Shift }, id), panelShown: true, videoOffered: true, nativeInput: false, fromPanel: true));
        Assert.Equal(MediaPanelKeyRoute.None, MediaPanelKeys.Route(Key.M, KeyModifiers.None,
            id => bindings.Matches(new KeyEventArgs { Key = Key.M }, id), panelShown: true, videoOffered: true, nativeInput: false, fromPanel: true));
    }

    [Fact]
    public void ASelectionIsWrittenAsOneDecisionAndAnUnknownStateIsNeverWritten()
    {
        bool?[] mixed = { true, false, null, false };
        Assert.Equal(new[] { 1, 3 }, FavoriteKeys.Targets(FavoriteKeyAction.Add, mixed));      // M: only the ones that are no favourites
        Assert.Equal(new[] { 0 }, FavoriteKeys.Targets(FavoriteKeyAction.Remove, mixed));      // U: only the favourites
        Assert.Equal(new[] { 1, 3 }, FavoriteKeys.Targets(FavoriteKeyAction.Toggle, mixed));   // F on a mixed selection adds the missing ones
        Assert.Equal(new[] { 0, 1 }, FavoriteKeys.Targets(FavoriteKeyAction.Toggle, new bool?[] { true, true, null })); // all favourites: F removes all
        Assert.Equal(new[] { 0 }, FavoriteKeys.Targets(FavoriteKeyAction.Toggle, new bool?[] { false }));
        Assert.Empty(FavoriteKeys.Targets(FavoriteKeyAction.Add, new bool?[] { true }));        // already a favourite: nothing to write
        Assert.Empty(FavoriteKeys.Targets(FavoriteKeyAction.Remove, new bool?[] { false }));
        Assert.Empty(FavoriteKeys.Targets(FavoriteKeyAction.Toggle, new bool?[] { null, null }));
        Assert.Empty(FavoriteKeys.Targets(FavoriteKeyAction.Toggle, Array.Empty<bool?>()));
        Assert.Empty(FavoriteKeys.Targets(FavoriteKeyAction.None, mixed));
    }

    [Fact]
    public void TheMediaPagesTileStateForTheKeysSkipsGroupsMissingFilesAndUnknownStates()
    {
        Assert.True(MediaPage.FavoriteKeyState(Row(Entry(), MediaLibraryFlag.Yes)));
        Assert.False(MediaPage.FavoriteKeyState(Row(Entry(), MediaLibraryFlag.No)));
        Assert.Null(MediaPage.FavoriteKeyState(Row(Entry(), MediaLibraryFlag.Unknown)));
        MediaLibraryEntry gone = Entry() with { Presence = MediaLibraryPresence.Missing };
        Assert.Null(MediaPage.FavoriteKeyState(Row(gone, MediaLibraryFlag.No)));
        MediaLibraryEntry second = Entry("g.png") with { AssetId = "asset-2" };
        Assert.Null(MediaPage.FavoriteKeyState(Row(Entry(), MediaLibraryFlag.No, new MediaLibraryTile(Entry(), [Entry(), second]))));
    }

    [Fact]
    public void TheStarTooltipNamesTheToggleAndTheAddAndRemoveKeysFromTheBindings()
    {
        string tip = FavoriteKeys.Tooltip(false);
        string[] lines = tip.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith(Texts.T("viewer.favorite.add"), lines[0]);
        Assert.Contains(CommandCatalog.ShortcutDisplay("favorites.toggle")!, lines[0]);
        Assert.Contains(CommandBindings.TooltipHeadline("favorites.add"), lines[1]);
        Assert.Contains(CommandBindings.TooltipHeadline("favorites.remove"), lines[1]);
        Assert.StartsWith(Texts.T("viewer.favorite.remove"), FavoriteKeys.Tooltip(true));
        Texts.SetLanguage("de");
        Assert.StartsWith("Aus Favoriten entfernen", FavoriteKeys.Tooltip(true));
    }

    // ---- every star toggles ----

    [Fact]
    public void TheStarOfAReaderAttachmentAsksItsRowSoAFavoriteWiredAfterItWasShownStillToggles()
    {
        // The review's case (screenshot 86, the sidebar viewer's header): the sidebar showed the medium before the page had wired the row's favourite.
        // The star copied the callbacks it found (none) and stayed disabled; now it asks the row each time (the controls need a window: smokes).
        ArchiveAttachmentRow row = new(new CapturedAttachment(0, "m1", OriginalFileName: "a.jpg"));
        var (available, marked, toggle, error) = ArchiveFavoriteButton.Callbacks(row);
        Assert.Equal((false, false, (string?)null), ArchiveFavoriteButton.ReadState(available, marked, error));
        bool favorite = false;
        int writes = 0;
        row.FavoriteAvailable = () => true;
        row.IsFavorite = () => favorite;
        row.ToggleFavoriteAsync = () => { writes++; favorite = !favorite; return Task.CompletedTask; };
        Assert.Equal((true, false, (string?)null), ArchiveFavoriteButton.ReadState(available, marked, error));
        Assert.True(toggle().IsCompletedSuccessfully);
        Assert.Equal((1, true), (writes, ArchiveFavoriteButton.ReadState(available, marked, error).Marked));
        // A rescan rewires the row: the star follows without being configured again.
        row.ToggleFavoriteAsync = () => { writes += 10; favorite = !favorite; return Task.CompletedTask; };
        row.FavoriteError = () => "disk full";
        toggle();
        Assert.Equal(11, writes);
        Assert.Equal((true, false, "disk full"), ArchiveFavoriteButton.ReadState(available, marked, error));
        // A row that is no longer wired (the page ended) makes the star unavailable, never a stale write.
        row.FavoriteAvailable = null;
        Assert.False(ArchiveFavoriteButton.ReadState(available, marked, error).Available);
    }

    [Fact]
    public void TheStarsUseTheRowWiringAndShowAPendingWrite()
    {
        string button = Source("Desktop/ArchiveFavoriteButton.cs");
        // Pending: disabled and named "… · saving" until the write returns.
        Assert.Contains("Refresh(); // pending", button);
        Assert.Contains("Texts.T(\"gallery.saving\")", button);
        Assert.Contains("if (row != null) _favorite.Configure(row);", Source("Desktop/Views/Archive/ReaderMediaSidebar.cs"));
        Assert.Contains("Texts.T(\"gallery.saving\")", Source("Desktop/MediaLibraryTileView.cs"));
    }

    [Fact]
    public void EveryFavoriteStarOfTheDesktopIsARealToggleOrADecorativeBadge()
    {
        // Source contract: the interactive stars all run the shared favourite call; the badges on pictures and small tiles stay decorative.
        string button = Source("Desktop/ArchiveFavoriteButton.cs");
        Assert.Contains("FavoriteKeys.Tooltip(marked)", button);
        Assert.Contains("internal void Configure(ArchiveAttachmentRow? row)", button);
        Assert.Contains("_favorite.Configure(row);", Source("Desktop/ArchiveAttachmentView.cs"));          // the reader post's file line
        Assert.Contains("_favorite.Configure(row);", Source("Desktop/ArchiveMediaTileView.cs"));           // the archive's media tiles
        Assert.Contains("_favorite.Configure(row);", Source("Desktop/Views/Archive/ReaderMediaSidebar.cs")); // the sidebar viewer's header
        Assert.Contains("panel.ConfigureFavorite(PanelFavoriteAvailable", Source("Desktop/Views/Media/MediaPage.Panel.cs")); // the Media page's panel
        string tile = Source("Desktop/MediaLibraryTileView.cs");                                             // the Media page's tile corner
        Assert.Contains("await toggle(row.Entry)", tile);
        Assert.Contains("FavoriteKeys.Tooltip(marked)", tile);
        Assert.Contains("FavoriteToggle = ToggleTileFavoriteAsync", Source("Desktop/Views/Media/MediaPage.cs"));
        Assert.Contains("FavoriteKeys.Tooltip(_favoriteSelected)", Source("Desktop/GalleryViewer.Favorites.cs")); // the gallery badge
        Assert.Contains("ToggleArchiveMediaFavoriteAsync(entry)", Source("Desktop/Views/Archive/ArchivePage.MediaCommands.cs"));
    }

    // ---- routing per surface (the decisions; the window routing is the smokes') ----

    [Fact]
    public void EachSurfaceRoutesTheFavoriteKeysToItsTarget()
    {
        string archive = Source("Desktop/Views/Archive/ArchivePage.FavoriteKeys.cs");
        // The reader: the shared resolver (live player, else the sidebar's medium, else the focused tile, else the first).
        Assert.Contains("ReaderMediaActivation.Choose(snapshot.Media, snapshot.SidebarIndex, snapshot.SidebarLive, snapshot.SidebarPlaying, snapshot.FocusedIndex)", archive);
        // The archive's media tiles and list: every selected file.
        Assert.Contains("SelectedMediaRows()", archive);
        // The sidebar: the medium it shows (or the strip's thumbnail).
        Assert.Contains("SidebarMediaContext(ancestors)", archive);
        Assert.Contains("SidebarStripContext(ancestors)", archive);
        Assert.Contains("FavoriteKeys.Writes(action, marked)", archive);
        Assert.Contains("TryRouteFavoriteKey(posts, e)", Source("Desktop/Views/Archive/ArchivePage.Reader.cs"));
        Assert.Contains("TryRouteFavoriteKey(list, e)", Source("Desktop/Views/Archive/ArchivePage.Gallery.cs"));
        Assert.Contains("TryRouteFavoriteKey(sidebar, e)", Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs"));
        // The Media page: the grid writes the selection, the panel its medium.
        string media = Source("Desktop/Views/Media/MediaPage.FavoriteKeys.cs");
        Assert.Contains("_grid.Selection.SelectedItems.OfType<MediaLibraryTileRow>()", media);
        Assert.Contains("new[] { row }", media);
        Assert.Contains("if (TryRouteGridFavoriteKey(e)) return;", Source("Desktop/Views/Media/MediaPage.cs"));
        Assert.Contains("if (fromPanel && TryRoutePanelFavoriteKey(e)) return true;", Source("Desktop/Views/Media/MediaPage.Panel.cs"));
        // The gallery keeps M / U / F (F asks before it removes).
        string gallery = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("CommandBindings.Matches(e, \"favorites.add\")", gallery);
        Assert.Contains("CommandBindings.Matches(e, \"favorites.remove\")", gallery);
    }

    private static MediaLibraryEntry Entry(string name = "f.png") =>
        new(new ResourceId("s", "b", "1"), "T1", 1, Day, Day, "1", 1, null, 0, "att-v1:" + new string('a', 64),
            "/dir/1", "asset-1", new string('a', 64), 1, "m1", "original", name, SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    private static MediaLibraryTileRow Row(MediaLibraryEntry entry, MediaLibraryFlag favorite, MediaLibraryTile? tile = null) =>
        new(tile ?? new MediaLibraryTile(entry, [entry]), 0, _ =>
            new MediaLibraryEntryState(favorite, true, null, true, Array.Empty<string>(), Array.Empty<string>(), null));

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
