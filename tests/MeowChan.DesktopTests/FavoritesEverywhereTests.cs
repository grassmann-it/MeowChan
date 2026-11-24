using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

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
    public void MAddsURemovesFTogglesAndShiftMIsMuteOnEveryPlatform(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        FavoriteKeyAction Of(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
            FavoriteKeys.Classify(id => bindings.Matches(new KeyEventArgs { Key = key, KeyModifiers = modifiers }, id));
        Assert.Equal(FavoriteKeyAction.Add, Of(Key.M));
        Assert.Equal(FavoriteKeyAction.None, Of(Key.B)); // revision 21: B is free
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

    [Theory, MemberData(nameof(Platforms))]
    public void TheStarTooltipIsOneLineWithExactlyTheKeysThatDoWhatTheClickDoesInTheReader(ShortcutPlatform platform)
    {
        // Tom's screenshot (16.11.): the Media tab tile's star said "Add to favorites · F" and, on a second line, "Add favorite · M / B · Remove favorite · U".
        // F and M act on that tile (the reader's keys, TryRouteFavoriteKey; B went in revision 21); U does nothing on a medium that is no favourite. One line, effective keys only.
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        string[] add = FavoriteKeys.EffectiveKeys(FavoriteKeys.AddId, "reader", bindings).ToArray();
        string[] toggle = FavoriteKeys.EffectiveKeys(FavoriteKeys.ToggleId, "reader", bindings).ToArray();
        string[] remove = FavoriteKeys.EffectiveKeys(FavoriteKeys.RemoveId, "reader", bindings).ToArray();
        Assert.Equal(new[] { "F" }, toggle);
        Assert.Equal(new[] { "M" }, add);
        Assert.Equal(new[] { "U" }, remove);
        // Every named key really acts in the reader's context, as the action the tooltip implies.
        foreach ((string[] keys, FavoriteKeyAction action) in new[] { (toggle, FavoriteKeyAction.Toggle), (add, FavoriteKeyAction.Add), (remove, FavoriteKeyAction.Remove) })
            foreach (string key in keys)
                Assert.Equal(action, FavoriteKeys.Classify(id => bindings.Matches(new KeyEventArgs { Key = Enum.Parse<Key>(key), KeyModifiers = KeyModifiers.None }, id, "reader")));
    }

    [Fact]
    public void TheStarTooltipNamesTheClickThenItsKeysOnOneLine() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ShortcutService.Publish(new());
        bool marked = false;
        ArchiveFavoriteButton star = new();
        star.Configure(() => true, () => marked, () => { marked = !marked; return Task.CompletedTask; });

        Assert.Equal(Texts.T("viewer.favorite.add") + " · F / M", Assert.IsType<string>(ToolTip.GetTip(star)));
        Click(star);
        Assert.True(star.IsChecked);
        Assert.Equal(Texts.T("viewer.favorite.remove") + " · F / U", Assert.IsType<string>(ToolTip.GetTip(star)));

        Texts.SetLanguage("de");
        ShortcutService.Publish(new ShortcutBindingsSection().Set(FavoriteKeys.ToggleId, [ShortcutGesture.Parse("F9")]));
        Assert.Equal(Texts.T("viewer.favorite.remove") + " · F9 / U", Assert.IsType<string>(ToolTip.GetTip(star)));
    });

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheStarsUseTheRowWiringAndShowAPendingWrite(bool inSidebar) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool marked = false;
        int writes = 0;
        ArchiveAttachmentRow row = new(new CapturedAttachment(0, "sample", OriginalFileName: "sample.png"))
        {
            FavoriteAvailable = () => true,
            IsFavorite = () => marked,
            ToggleFavoriteAsync = async () => { writes++; await release.Task; marked = true; }
        };
        ToggleButton star;
        if (inSidebar)
        {
            ReaderMediaSidebar sidebar = new();
            sidebar.Describe("No. 42", "sample.png", "", true);
            sidebar.ConfigureFavorite(row);
            star = sidebar.FavoriteButton;
        }
        else
        {
            ArchiveFavoriteButton postStar = new();
            postStar.Configure(row);
            star = postStar;
        }
        Assert.True(star.IsEnabled);
        try
        {
            Click(star);
            Assert.Equal(1, writes);
            Assert.False(star.IsChecked);
            if (!inSidebar)
            {
                Assert.False(star.IsEnabled);
                Assert.Contains(Texts.T("gallery.saving"), AutomationProperties.GetName(star));
            }
            Click(star);
            Assert.Equal(1, writes);
        }
        finally
        {
            release.TrySetResult();
            BooruComponentDispatcher.WaitUntil(() => star.IsEnabled && star.IsChecked == true,
                "the favorite star to show the stored value");
        }
        Assert.DoesNotContain(Texts.T("gallery.saving"), AutomationProperties.GetName(star));
    });

    [Theory]
    [InlineData("post")]
    [InlineData("archive tile")]
    [InlineData("reader sidebar")]
    [InlineData("media panel")]
    [InlineData("media tile")]
    [InlineData("gallery")]
    public void EveryFavoriteStarOfTheDesktopIsARealToggleOrADecorativeBadge(string surface) =>
        BooruComponentDispatcher.Run(() => BooruComponentDispatcher.Wait(ExerciseFavoriteSurfaceAsync(surface)));

    private static async Task ExerciseFavoriteSurfaceAsync(string surface)
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false);
        MediaAssetReference asset = media.Asset;
        MediaAssetSnapshot manifest = media.Snapshot;
        MediaLibraryEntry indexed = Entry(asset.OriginalFileName!) with
        {
            Directory = media.Directory, AssetId = asset.AssetId, Sha256 = asset.Sha256, Length = asset.Length
        };
        GalleryEntry entry = new(new(indexed.Thread, media.Directory, asset.AssetId), manifest, asset, null, null, false, true);
        List<object> writes = [];
        Task Write(object target) { writes.Add(target); return Task.CompletedTask; }
        ToggleButton? toggle = null;
        await using GalleryViewer? viewer = surface == "gallery" ? new GalleryViewer() : null;
        Control host;
        object expected;
        switch (surface)
        {
            case "post":
                ArchiveAttachmentRow post = new(new CapturedAttachment(0, "sample", OriginalFileName: indexed.Name))
                { FavoriteAvailable = () => true };
                post.ToggleFavoriteAsync = () => Write(post);
                host = new ArchiveAttachmentView { DataContext = post };
                expected = post;
                break;
            case "archive tile":
                ArchiveAssetRow archive = new(manifest, asset) { FavoriteAvailable = () => true, IsFavorite = () => true };
                archive.ToggleFavoriteAsync = () => Write(archive);
                host = new ArchiveMediaTileView { DataContext = archive };
                expected = archive;
                break;
            case "reader sidebar":
                ArchiveAttachmentRow shown = new(new CapturedAttachment(0, "sample")) { FavoriteAvailable = () => true };
                shown.ToggleFavoriteAsync = () => Write(shown);
                ReaderMediaSidebar sidebar = new();
                sidebar.Describe("No. 42", "sample.png", "", true);
                sidebar.ConfigureFavorite(shown);
                host = sidebar;
                toggle = sidebar.FavoriteButton;
                expected = shown;
                break;
            case "media panel":
                ReaderMediaSidebar panel = new() { DecisionKeyContext = "media" };
                panel.Describe("", "sample.png", "", true);
                panel.ConfigureFavorite(() => true, () => false, () => Write(indexed));
                host = panel;
                toggle = panel.FavoriteButton;
                expected = indexed;
                break;
            case "media tile":
                MediaLibraryTileView tile = new() { FavoriteToggle = item => Write(item) };
                tile.DataContext = Row(indexed, MediaLibraryFlag.Yes);
                host = tile;
                toggle = tile.FavoriteToggleButton;
                expected = indexed;
                break;
            case "gallery":
                viewer!.FavoriteToggleRequested += item => writes.Add(item);
                host = viewer;
                expected = entry;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(surface));
        }

        Window window = new() { Width = 900, Height = 700, Content = host };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            if (viewer != null)
            {
                await viewer.ShowAsync(entry, BooruComponentDispatcher.Cancellation);
                viewer.SetInteractionAvailable(true);
                viewer.SetFavorite(false);
                toggle = viewer.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "GalleryFavoriteIndicator");
            }
            window.UpdateLayout();
            toggle ??= Assert.Single(host.GetVisualDescendants().OfType<ArchiveFavoriteButton>());
            Assert.True(toggle.IsEffectivelyVisible && toggle.IsEffectivelyEnabled, surface);
            Click(toggle);
            Assert.Same(expected, Assert.Single(writes));

            foreach (MediaBadgeView badge in host.GetVisualDescendants().OfType<MediaBadgeView>())
            {
                Assert.False(badge.IsHitTestVisible);
                Assert.False(badge.Focusable);
            }
            // A decorative badge in the picture has no button route.
            MediaBadgeView decoration = new() { Options = MediaBadgeOptions.Archive };
            decoration.Apply(new MediaBadgeState(Favorite: true), new Size(170, 125));
            PlacedBadge placed = Assert.Single(decoration.Plan.Badges, badge => badge.Badge.Id == MediaBadgeId.Favorite);
            window.Content = decoration;
            window.UpdateLayout();
            Point badgeCenter = decoration.TranslatePoint(placed.Bounds.Center, window)!.Value;
            Assert.False(decoration.IsHitTestVisible);
            Assert.NotSame(decoration, window.InputHitTest(badgeCenter));
            window.MouseDown(badgeCenter, MouseButton.Left);
            window.MouseUp(badgeCenter, MouseButton.Left);
            Assert.Single(writes);
        }
        finally { window.Close(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
