using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Package D of the dead-affordance audit (15.11.2026), the media views: the sidebar's cursor, hidden cross and "No." link, the media tile's "+N" link, badge
// zones and tag chip menu, the file name of a post's attachment and the media panel's right click. The views are built and driven without a window; the
// wiring that needs a page is checked against its source, as the other package tests do.
[Collection("Localization")]
public sealed class MediaViewsDeadAffordanceTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public MediaViewsDeadAffordanceTests() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // The sidebar loads icon masks, so its tests need the asset loader (and the hand cursor needs a cursor factory). They run on a dispatcher of
    // their own (BooruComponentDispatcher: asset loader and Skia in a locator scope, the UI thread and the shortcut observers restored afterwards)
    // with a cursor factory in that scope, like the dead-affordance guard. No process-wide AppBuilder setup: a bare Application left in the root
    // locator hid the design tokens from the guard's pages, and controls bound on an xunit worker thread made the next test's thread "a
    // different thread" (combined runs only).
    private static void OnOwnDispatcher(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        test();
    });
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative));

    // ---- the reader sidebar -----------------------------------------------------------------------------------------------------

    // MD-03 (17.11.2026): the contract is single click selects, double click opens the gallery (8896e40a7). The hand shows only where a single click
    // acts: the host says so (an explicit "open the gallery on click" setting, or a video it plays here) and there is a medium to act on.
    [Fact]
    public void TheSidebarsHandCursorShowsOnlyWhileASingleClickActs() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        // Nothing selected: the placeholder says so, a click opens nothing, so no hand.
        Assert.False(sidebar.SingleClickActs);
        Assert.False(sidebar.FrameClickable);
        Assert.Null(sidebar.Frame.Cursor);
        // A medium in the default click mode: a single click does nothing (a double click opens the gallery), so the normal arrow.
        sidebar.Describe("No. 7", "cat.png", "1 KiB", canOpen: true);
        sidebar.ShowPlaceholder("Loading");
        Assert.False(sidebar.FrameClickable);
        Assert.Null(sidebar.Frame.Cursor);
        // The host says a single click acts (gallery on click, or a video played here): the hand.
        sidebar.SingleClickActs = true;
        Assert.True(sidebar.FrameClickable);
        Assert.Same(PointerCursors.Hand, sidebar.Frame.Cursor);
        // A post without media: the reason is shown, but nothing opens.
        sidebar.Describe("No. 8", string.Empty, string.Empty, canOpen: false);
        sidebar.ShowNoMedia("No media");
        Assert.False(sidebar.FrameClickable);
        Assert.Null(sidebar.Frame.Cursor);
        // Back to empty.
        sidebar.ShowEmpty();
        Assert.False(sidebar.FrameClickable);
        Assert.Null(sidebar.Frame.Cursor);
        // The gallery button changing its state follows too.
        sidebar.ShowPlaceholder("Loading");
        Assert.False(sidebar.FrameClickable);
        sidebar.Open.IsEnabled = true;
        Assert.True(sidebar.FrameClickable);
        Assert.Same(PointerCursors.Hand, sidebar.Frame.Cursor);
        // And the host taking it back (the click mode changed, the video went): the arrow again.
        sidebar.SingleClickActs = false;
        Assert.False(sidebar.FrameClickable);
        Assert.Null(sidebar.Frame.Cursor);
    });

    [Fact]
    public void BothHostsSayWhetherASingleClickOnThePictureActs()
    {
        string panel = Source("Desktop/Views/Media/MediaPage.Panel.cs"), reader = Source("Desktop/Views/Archive/ArchivePage.InlineVideo.cs");
        Assert.Contains("panel.SingleClickActs = slot.IsOffered;", panel);
        Assert.Contains("UseGalleryCursor = true", panel);
        Assert.Contains("sidebar.SingleClickActs = slot.IsOffered;", reader);
        Assert.DoesNotContain("ReaderMediaSidebarRules.OpensGalleryOnPictureClick", reader);
        // The double click keeps opening the gallery in every click mode.
        Assert.Contains("panel.GalleryRequested += (_, _) => StartOpenPanelMedia();", panel);
    }

    [Fact]
    public void OnlyTheMediaPreviewOptsIntoAHandForDoubleClickGalleryActivation() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        sidebar.Describe("No. 7", "cat.png", "1 KiB", canOpen: true);
        sidebar.ShowPlaceholder("Loading");
        Assert.False(sidebar.SingleClickActs);
        Assert.False(sidebar.UseGalleryCursor);
        Assert.Null(sidebar.Frame.Cursor); // Reader keeps its existing cursor contract.
        sidebar.UseGalleryCursor = true;
        Assert.True(sidebar.FrameClickable);
        Assert.Same(PointerCursors.Hand, sidebar.Frame.Cursor);
        Assert.False(sidebar.SingleClickActs); // The hand is not permission for single-click gallery activation.
        sidebar.UseGalleryCursor = false;
        Assert.Null(sidebar.Frame.Cursor);
    });

    [Fact]
    public void TheSidebarsCrossIsNoClickTargetWhileItIsInvisible() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        Assert.Equal(0, sidebar.Close.Opacity);
        Assert.False(sidebar.Close.IsHitTestVisible);
        // The pointer over the sidebar shows it, and then it is clickable; leaving hides it and takes the click away again.
        sidebar.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent, sidebar, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), sidebar,
            default, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
        Assert.Equal(1, sidebar.Close.Opacity);
        Assert.True(sidebar.Close.IsHitTestVisible);
        sidebar.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, sidebar, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), sidebar,
            default, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
        Assert.Equal(0, sidebar.Close.Opacity);
        Assert.False(sidebar.Close.IsHitTestVisible);
    });

    [Fact]
    public void TheHeadersPostNumberIsALinkWhenTheHostGaveItATipAndTheLineStaysWhole() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        int clicks = 0;
        sidebar.PostLinkClicked += (_, _) => clicks++;
        sidebar.Describe("No. 75", "cat.png", "880 KiB", canOpen: true, postLinkTip: "Copy >>75");
        Assert.True(sidebar.PostLink.IsVisible);
        Assert.Equal("No. 75", sidebar.PostLink.Content);
        Assert.Equal("Copy >>75", ToolTip.GetTip(sidebar.PostLink));
        Assert.Equal("No. 75 · cat.png · 880 KiB", sidebar.HeaderLine); // smokes and the screen reader still read one line
        Assert.Contains("link", sidebar.PostLink.Classes);
        RoutedEventArgs click = new(Button.ClickEvent);
        sidebar.PostLink.RaiseEvent(click);
        Assert.Equal(1, clicks);
        Assert.True(click.Handled); // the picture and the header are not activated by it

        // A post without a medium: the number alone is still the link.
        sidebar.Describe("No. 75", string.Empty, string.Empty, canOpen: false, postLinkTip: "Copy >>75");
        Assert.True(sidebar.PostLink.IsVisible);
        Assert.Equal("No. 75", sidebar.HeaderLine);

        // The media page's panel gives no tip: its line is the plain text, as before.
        sidebar.Describe("Post 12", "dog.png", "1 KiB", canOpen: true);
        Assert.False(sidebar.PostLink.IsVisible);
        Assert.Equal("Post 12 · dog.png · 1 KiB", sidebar.HeaderLine);
        sidebar.ShowEmpty();
        Assert.False(sidebar.PostLink.IsVisible);
        Assert.Equal(Texts.T(sidebar.TitleKey), sidebar.HeaderLine);
    });

    // The sidebar's medium has one favourite star: the header toggle. The picture's badges (Tom, 16.11.2026, the sweep's F2) no longer repeat it.
    private static WriteableBitmap Picture() => new(new PixelSize(8, 8), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    [Fact]
    public void TheSidebarsPictureDrawsNoFavouriteBadgeWhileTheHeaderStarShowsTheFavourite() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        bool favorite = true;
        sidebar.ConfigureFavorite(() => true, () => favorite, () => Task.CompletedTask);
        sidebar.ShowPicture(Picture(), picture => picture.Dispose(), playable: false, double.PositiveInfinity, double.PositiveInfinity);
        // The header star is the favourite's one mark ...
        Assert.True(sidebar.FavoriteButton.IsVisible);
        Assert.True(sidebar.FavoriteButton.IsChecked);
        // ... and the picture carries only its kind, no favourite, in the state and in the placed badges.
        Assert.False(sidebar.Badges.State.Favorite);
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Image), sidebar.Badges.State);
        Assert.DoesNotContain(sidebar.Badges.Plan.Badges, placed => placed.Badge.Id == MediaBadgeId.Favorite);
        // A video keeps its kind and is still no favourite badge; a refresh after the favourite changed does not bring one.
        sidebar.ShowPicture(Picture(), picture => picture.Dispose(), playable: true, double.PositiveInfinity, double.PositiveInfinity);
        favorite = false; sidebar.RefreshFavorite(); favorite = true; sidebar.RefreshFavorite();
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Video), sidebar.Badges.State);
        Assert.True(sidebar.FavoriteButton.IsChecked);
        Assert.DoesNotContain(sidebar.Badges.Plan.Badges, placed => placed.Badge.Id == MediaBadgeId.Favorite);
        // The accessible sentence of the badges does not name a favourite either (the header star carries that name).
        Assert.DoesNotContain("Favorite", MediaBadgeSentences.Get(sidebar.Badges.State, sidebar.Badges.Options));
    });

    [Fact]
    public void TheSidebarsHeaderStarStillTogglesTheFavouriteAndTheBadgesStayWithoutOne() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        bool favorite = false;
        int writes = 0;
        sidebar.ConfigureFavorite(() => true, () => favorite, () => { writes++; favorite = !favorite; return Task.CompletedTask; });
        sidebar.ShowPicture(Picture(), picture => picture.Dispose(), playable: false, double.PositiveInfinity, double.PositiveInfinity);
        Assert.True(sidebar.FavoriteButton.IsEnabled);
        Assert.False(sidebar.FavoriteButton.IsChecked);
        sidebar.FavoriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((1, true), (writes, favorite));
        Assert.True(sidebar.FavoriteButton.IsChecked);
        Assert.False(sidebar.Badges.State.Favorite);
        sidebar.FavoriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((2, false), (writes, favorite));
        Assert.False(sidebar.FavoriteButton.IsChecked);
        Assert.False(sidebar.Badges.State.Favorite);
        // A medium without a star (nothing configured) shows no header star and still no badge.
        sidebar.ConfigureFavorite(null, null, null);
        Assert.False(sidebar.FavoriteButton.IsVisible);
        Assert.False(sidebar.Badges.State.Favorite);
    });

    [Fact]
    public void OnlyTheSidebarDropsTheFavouriteBadgeTheOtherSurfacesKeepTheirs()
    {
        // The state each other surface builds still resolves a favourite badge under its options (the gallery filmstrip, the Media page and the
        // tagging tiles, the archive's saved-media tiles and the Media tab); the sidebar change is its own view's state, not the shared options.
        MediaBadgeState favourite = new(Favorite: true);
        foreach (MediaBadgeOptions options in new[] { MediaBadgeOptions.Filmstrip, MediaBadgeOptions.MediaPage, MediaBadgeOptions.TaggingGrid, MediaBadgeOptions.ArchiveTile, MediaBadgeOptions.Archive })
            Assert.Contains(MediaBadgeModel.Resolve(favourite, options), badge => badge.Id == MediaBadgeId.Favorite);
        // The sidebar's own state is never built with the flag.
        string sidebar = Source("Desktop/Views/Archive/ReaderMediaSidebar.cs");
        Assert.DoesNotContain("Favorite: favorite", sidebar);
        Assert.Contains("_badges.Apply(new MediaBadgeState(Kind: _playable ? MediaBadgeKind.Video : MediaBadgeKind.Image), _image.Bounds.Size);", sidebar);
        // The header toggle is still wired into the sidebar and the Media page's panel.
        Assert.Contains("if (row != null) _favorite.Configure(row);", sidebar);
        Assert.Contains("panel.ConfigureFavorite(PanelFavoriteAvailable", Source("Desktop/Views/Media/MediaPage.Panel.cs"));
    }

    [Fact]
    public void TheArchivePageWiresTheNumberLinkToTheSameCopyAsThePostsOwnNumber()
    {
        string page = Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");
        Assert.Contains("sidebar.PostLinkClicked += (_, _) => { if (!_ended && ArchivePosts.SelectedItem is ArchivePostRow post) _ = CopyPostReferenceAsync(post); };", page);
        Assert.Contains("sidebar.Describe(post.NumberLabel, string.Empty, string.Empty, false, post.CopyNumberLabel);", page);
        Assert.Contains("sidebar.Describe(postLine, file.Name, size, row.OpenPreview != null, post.CopyNumberLabel);", page);
    }

    // ---- the media tile ----------------------------------------------------------------------------------------------------------

    private static MediaLibraryEntry Entry(string thread = "1") =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day, "1", 1, null, 0, "att-v1:" + new string('a', 64),
            "/dir/" + thread, "asset-" + thread, new string('a', 64), 1, "m" + thread, "original", "f.png", SavedMediaKind.Image,
            MediaLibraryPresence.Present, false, false);

    private static PrivateTagDefinition Tag(int id, string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? tagName, out _));
        return new PrivateTagDefinition(new TagId(new Guid(id, 0, 0, new byte[8])), tagName!);
    }

    private static MediaLibraryTileRow Row(MediaLibraryEntry entry, params PrivateTagDefinition[] tags) => new(new MediaLibraryTile(entry, [entry]), 0, e =>
        new MediaLibraryEntryState(MediaLibraryFlag.No, true, null, true, tags.Select(tag => tag.Name.Display).ToArray(), Array.Empty<string>(), MediaTagSubjects.Of(e))
        { CarriedTags = tags });

    private static Button[] Chips(Control view) => view.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "MediaTileTag").ToArray();

    [Fact]
    public void ThePlusNAfterTheTagChipsIsALinkThatOpensTheTagEditorOfThatTile()
    {
        MediaLibraryEntry entry = Entry();
        MediaLibraryTileRow row = Row(entry, Tag(1, "cat"), Tag(2, "dog"), Tag(3, "owl"));
        List<(MediaLibraryTileRow Row, Control Anchor)> edited = new();
        MediaLibraryTileView view = new() { EditTags = (r, anchor) => edited.Add((r, anchor)), DataContext = row };
        Button link = view.MoreTagsLink;
        Assert.True(link.IsVisible);
        Assert.Contains("link", link.Classes);
        Assert.Same(link, view.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "MediaTileMoreTagsLink"));
        Assert.Equal("+1", ((TextBlock)link.Content!).Text);
        Assert.Equal(Texts.T("media.tile.moreTags", "owl"), Avalonia.Automation.AutomationProperties.GetName(link));
        RoutedEventArgs click = new(Button.ClickEvent);
        link.RaiseEvent(click);
        Assert.True(click.Handled); // the tile is not activated by it
        Assert.Equal([(row, (Control)view)], edited);

        // No tag beyond the chips: no link, and a recycled tile loses it.
        view.DataContext = Row(entry, Tag(1, "cat"));
        Assert.False(link.IsVisible);
    }

    [Fact]
    public void TheLinkKeepsItsWidthInTheTagRowWhileTheChipsShareTheRest()
    {
        string tile = Source("Desktop/MediaLibraryTileView.cs");
        Assert.Contains("private static bool IsChip(Control child) => child is Button button && !button.Classes.Contains(\"link\");", tile);
        Assert.Contains("visible.Where(child => !IsChip(child))", tile);
        Assert.Contains("visible.Where(IsChip).Cast<Button>()", tile);
    }

    [Fact]
    public void OnlyTheUntaggedMarkAndTheTagCountEditTagsAndOnlyTheQueueBadgeOpensTagging()
    {
        MediaLibraryEntry entry = Entry();
        MediaLibraryTileRow row = Row(entry);
        List<string> log = new();
        bool tagging = true;
        MediaLibraryTileView view = new() { DataContext = row };
        // Nothing wired (a harness page): no badge acts.
        Assert.All(Enum.GetValues<MediaBadgeId>(), id => Assert.False(view.IsBadgeAction(id)));
        view.EditTags = (r, anchor) => log.Add("tags:" + ReferenceEquals(r, row) + ":" + ReferenceEquals(anchor, view));
        view.OpenTagging = () => log.Add("tagging");
        view.CanOpenTagging = () => tagging;
        Assert.Equal(new HashSet<MediaBadgeId> { MediaBadgeId.TagCount, MediaBadgeId.Untagged, MediaBadgeId.Queued }, Enum.GetValues<MediaBadgeId>().Where(view.IsBadgeAction).ToHashSet());
        Assert.All(new[] { MediaBadgeId.Favorite, MediaBadgeId.Rating, MediaBadgeId.Video, MediaBadgeId.Gif, MediaBadgeId.Missing, MediaBadgeId.Booru,
            MediaBadgeId.PlayMark, MediaBadgeId.Tagging }, id => Assert.False(view.IsBadgeAction(id)));
        view.OnBadgeActivated(MediaBadgeId.Untagged);
        view.OnBadgeActivated(MediaBadgeId.TagCount);
        view.OnBadgeActivated(MediaBadgeId.Queued);
        Assert.Equal(["tags:True:True", "tags:True:True", "tagging"], log);
        // The page without the Tagging navigation: the queue badge is only drawn.
        tagging = false;
        Assert.False(view.IsBadgeAction(MediaBadgeId.Queued));
        // Without a row nothing edits.
        view.DataContext = null;
        Assert.False(view.IsBadgeAction(MediaBadgeId.TagCount));
    }

    [Fact]
    public void ThePageWiresThePlusNAndTheBadgesToItsOwnTagPopoverAndToTheTaggingNavigation()
    {
        string page = Source("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("EditTags = EditTagsOfTile, OpenTagging = () => OpenTaggingPage?.Invoke(), CanOpenTagging = () => OpenTaggingPage != null", page);
        string affordances = Source("Desktop/Views/Media/MediaPage.TileAffordances.cs");
        // The same "Tag…" as the menu entry and the shortcut: the popover on the tile, or the reason as text for a medium that cannot be tagged.
        Assert.Contains("await OpenRateAndTagAsync(row.Entry, row, anchor, Current, initialFocus: QuickTagInitialFocus.TagInput);", affordances);
    }

    [Fact]
    public void ARightClickOrTheMenuKeyOnATagChipLeavesTheTileMenuAndOpensTheChipsFilterMenu()
    {
        string page = Source("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("|| IsNativeContextSource(e.Source) || IsTileTagChip(e.Source)", page);
        Assert.Contains("if (!IsContextKey(e) || IsTileTagChip(e.Source)) return;", page);
        Assert.Contains("WorkspaceChips.HasMenu", Source("Desktop/Views/Media/MediaPage.TileAffordances.cs"));

        PrivateTagDefinition cat = Tag(1, "cat");
        List<Guid> included = new(), excluded = new();
        MediaLibraryTileView view = new() { TagFilter = included.Add, TagExclude = excluded.Add, DataContext = Row(Entry(), cat, Tag(2, "dog"), Tag(3, "owl")) };
        Button[] shown = Chips(view).Where(chip => chip.IsVisible).ToArray();
        Assert.Equal(2, shown.Length);
        Assert.All(shown, chip => Assert.True(WorkspaceChips.HasMenu(chip)));
        ContextMenuModel menu = WorkspaceChips.MenuModelOf(shown[0])!;
        Assert.Equal([TagPickerMenu.Include, TagPickerMenu.Exclude, TagPickerMenu.CopyName], menu.Entries.Select(entry => entry.CommandId));
        menu.Entries.Single(entry => entry.CommandId == TagPickerMenu.Include).Invoke!();
        menu.Entries.Single(entry => entry.CommandId == TagPickerMenu.Exclude).Invoke!();
        Assert.Equal([cat.Id.Value], included);
        Assert.Equal([cat.Id.Value], excluded);

        // A chip that is hidden (no tag) has nothing to offer.
        view.DataContext = Row(Entry(), cat);
        Button hidden = Chips(view).First(chip => !chip.IsVisible);
        Assert.Null(WorkspaceChips.MenuModelOf(hidden));
    }

    // ---- the badge zones ----------------------------------------------------------------------------------------------------------

    private static (LetterboxPanel Panel, MediaBadgeView Badges) Arranged(MediaBadgeState state)
    {
        MediaBadgeView badges = new() { Options = MediaBadgeOptions.MediaPage };
        LetterboxPanel panel = new() { Width = 174, Height = 120, Aspect = 1.5, MarksOnSlot = true, Children = { new Image(), badges } };
        badges.Apply(state, new Size(1, 1));
        panel.Measure(new Size(174, 120)); panel.Arrange(new Rect(0, 0, 174, 120));
        return (panel, badges);
    }

    [Fact]
    public void AViewNobodyAsksToActIsNoHitTargetAtAll()
    {
        (_, MediaBadgeView badges) = Arranged(new MediaBadgeState(TagCount: 3, Queued: true));
        Assert.False(badges.IsHitTestVisible);
        Assert.Null(badges.Cursor);
        Assert.Empty(badges.Zones());
        Assert.Null(badges.ZoneAt(new Point(160, 110)));
    }

    [Fact]
    public void OnlyTheNamedBadgesHaveAZoneAndAZoneIsNeverSmallerThanAPointerTarget()
    {
        (_, MediaBadgeView badges) = Arranged(new MediaBadgeState(Favorite: true, TagCount: 3, Queued: true, Kind: MediaBadgeKind.Video));
        badges.Activatable = id => id is MediaBadgeId.TagCount or MediaBadgeId.Queued;
        Assert.True(badges.IsHitTestVisible);
        PlacedBadge count = badges.Plan.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.TagCount);
        PlacedBadge queue = badges.Plan.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.Queued);
        PlacedBadge star = badges.Plan.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.Favorite);
        Assert.Equal(MediaBadgeId.TagCount, badges.ZoneAt(count.Bounds.Center));
        Assert.Equal(MediaBadgeId.Queued, badges.ZoneAt(queue.Bounds.Center));
        Assert.Null(badges.ZoneAt(star.Bounds.Center));            // drawn only (the favourite is another package's)
        Assert.Null(badges.ZoneAt(new Point(87, 60)));             // the picture's centre: the tile's own click
        Assert.Equal(2, badges.Zones().Length);
        Assert.All(badges.Zones(), zone => Assert.True(zone.Width >= MediaBadgeView.MinimumZone - 0.01 && zone.Height >= MediaBadgeView.MinimumZone - 0.01
            || zone.Right >= 174 - 0.01 || zone.Bottom >= 120 - 0.01 || zone.Left <= 0.01 || zone.Top <= 0.01)); // only an edge may cut it
        // The hit test of the framework agrees: the point counts for the view only inside a zone.
        ICustomHitTest hit = badges;
        Assert.True(hit.HitTest(count.Bounds.Center));
        Assert.False(hit.HitTest(new Point(87, 60)));
        Assert.False(hit.HitTest(star.Bounds.Center));
        // Taking the callback away takes the target away.
        badges.Activatable = null;
        Assert.False(badges.IsHitTestVisible);
        Assert.False(hit.HitTest(count.Bounds.Center));
    }

    [Fact]
    public void AClickOnAZoneIsTheZonesAloneAndAClickElsewhereOrWithTheOtherButtonIsNot()
    {
        (_, MediaBadgeView badges) = Arranged(new MediaBadgeState(TagCount: 0, Queued: true));
        badges.Activatable = id => id is MediaBadgeId.Untagged or MediaBadgeId.Queued;
        List<MediaBadgeId> activated = new();
        badges.Activated += activated.Add;
        Point untagged = badges.Plan.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.Untagged).Bounds.Center;
        Point queued = badges.Plan.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.Queued).Bounds.Center;

        // The press and the release go through the same methods the pointer events call (a pointer event needs a window's root to place its position).
        Assert.True(badges.Press(untagged, leftButton: true)); // handled: the list's selection and the tile's activation never see the press
        Assert.True(badges.Release(untagged, MouseButton.Left));
        Assert.Equal([MediaBadgeId.Untagged], activated);

        // Pressed on one zone, released on the other: no click (still the zone's gesture).
        Assert.True(badges.Press(untagged, true));
        Assert.True(badges.Release(queued, MouseButton.Left));
        Assert.Equal([MediaBadgeId.Untagged], activated);

        // Outside every zone the press stays unhandled (the picture's own click) and the release is nobody's.
        Assert.False(badges.Press(new Point(87, 60), true));
        Assert.False(badges.Release(new Point(87, 60), MouseButton.Left));
        Assert.Equal([MediaBadgeId.Untagged], activated);

        // The right button is the menu's, not the badge's.
        Assert.False(badges.Press(queued, leftButton: false));
        Assert.False(badges.Release(queued, MouseButton.Right));
        Assert.Equal([MediaBadgeId.Untagged], activated);
        Assert.True(badges.Press(queued, true));
        Assert.True(badges.Release(queued, MouseButton.Left));
        Assert.Equal([MediaBadgeId.Untagged, MediaBadgeId.Queued], activated);
    }

    [Fact]
    public void AMediaTileOffersItsBadgeZonesAndOtherSurfacesKeepTheirRenderOnlyViews()
    {
        Assert.NotNull(new MediaLibraryTileView().Badges.Activatable);
        Assert.Null(new MediaBadgeView().Activatable);
        Assert.False(new MediaBadgeView().IsHitTestVisible);
    }

    // ---- the file name of a post's attachment ------------------------------------------------------------------------------------

    [Fact]
    public void TheFileNameOfAPostsAttachmentDoesWhatAClickOnTheThumbnailDoes()
    {
        string view = Source("Desktop/ArchiveAttachmentView.cs");
        Assert.Contains("ConfigureFileNameClick();", view);
        int start = view.IndexOf("private void ConfigureFileNameClick()", StringComparison.Ordinal);
        string body = view[start..view.IndexOf("private readonly AttachmentColumn _column;", start, StringComparison.Ordinal)];
        Assert.Contains("_label.Cursor = PointerCursors.Hand;", body);
        Assert.Contains("_label.Tapped += (_, e) =>", body);
        Assert.Contains("ActivateMedium();", body);
        // A double click opens the gallery like the thumbnail's, after putting an expanded picture back.
        Assert.Contains("_label.DoubleTapped += (_, e) =>", body);
        Assert.Contains("if (_expanded) SetExpanded(false);", body);
        Assert.Contains("open();", body);
    }

    // ---- the media page's panel -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePanelsRightClickFlagLivesUntilTheReleaseHasPassedAndTheWholePanelIsCovered()
    {
        string panel = Source("Desktop/Views/Media/MediaPage.Panel.cs");
        int press = panel.IndexOf("private void OnPanelPointerPressed", StringComparison.Ordinal);
        int released = panel.IndexOf("private void OnPanelPointerReleased", StringComparison.Ordinal);
        int requested = panel.IndexOf("private void OnPanelContextRequested", StringComparison.Ordinal);
        string pressBody = panel[press..released];
        // The press no longer drops the flag on a Background tick (that came before Avalonia's release-time ContextRequested); the release does.
        Assert.DoesNotContain("Dispatcher.UIThread.Post", pressBody);
        Assert.Contains("_panelPointerContext.BeginPress(); // each press starts its own gesture", pressBody);
        Assert.Contains("_panelPointerContext.MenuOpenedByPress();", pressBody);
        string releaseBody = panel[released..requested];
        Assert.Contains("_panelPointerContext.Released(e.InitialPressMouseButton)", releaseBody);
        Assert.Contains("panel.AddHandler(PointerReleasedEvent, OnPanelPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);", panel);
        // A key ends a gesture whose release never came.
        Assert.Contains("_panelPointerContext.Reset(); // a key ends a pointer gesture whose release never came", panel);
        // The whole panel, header line and its buttons included: the handlers sit on the panel itself and the press path asks for no frame.
        Assert.Contains("panel.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);", panel);
        Assert.Contains("panel.AddHandler(ContextRequestedEvent, OnPanelContextRequested, RoutingStrategies.Bubble);", panel);
        Assert.DoesNotContain("FrameArea", pressBody);
        Assert.DoesNotContain("Frame.IsPointerOver", pressBody);
    }
}
