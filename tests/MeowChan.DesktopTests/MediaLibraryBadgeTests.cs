using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// MB1b (media page): the tile's badges come from its row through the shared badge view; the Layout menu's caption parts keep hiding the three
// facts they are named for, the sentence in the tile's name keeps every fact, and a rebind of an unchanged state allocates nothing.
[Collection("Localization")]
public sealed class MediaLibraryBadgeTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public MediaLibraryBadgeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static MediaLibraryEntry Entry(string name = "f.png", SavedMediaKind kind = SavedMediaKind.Image, string? booruPost = null) =>
        new(new ResourceId("s", "b", "1"), "T1", 1, Day, Day, "1", 1, null, 0, "att-v1:" + new string('a', 64),
            "/dir/1", "asset-1", new string('a', 64), 1, "m1", "original", name, kind, MediaLibraryPresence.Present, false, false)
        { BooruPostId = booruPost };

    private static PrivateTagDefinition Tag(int id, string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? tagName, out _));
        return new PrivateTagDefinition(new TagId(new Guid(id, 0, 0, new byte[8])), tagName!);
    }

    private static MediaLibraryTileRow Row(MediaLibraryEntry entry, MediaLibraryFlag favorite = MediaLibraryFlag.No, int tags = 0, bool editable = true,
        MediaLibraryTile? tile = null)
    {
        PrivateTagDefinition[] carried = Enumerable.Range(1, tags).Select(index => Tag(index, "t" + index)).ToArray();
        return new MediaLibraryTileRow(tile ?? new MediaLibraryTile(entry, [entry]), 0, e =>
            new MediaLibraryEntryState(favorite, true, null, true, carried.Select(tag => tag.Name.Display).ToArray(), Array.Empty<string>(), editable ? MediaTagSubjects.Of(e) : null)
            { CarriedTags = carried });
    }

    [Fact]
    public void TheStateComesFromTheRowAndTheMediaPageOptionsShowTheBooruAndUntaggedMarks()
    {
        MediaBadgeState gif = MediaLibraryBadges.ForRow(Row(Entry("a.GIF"), MediaLibraryFlag.Yes, tags: 3), queued: true);
        Assert.Equal(new MediaBadgeState(Favorite: true, Kind: MediaBadgeKind.Gif, TagCount: 3, Queued: true), gif);
        Assert.Equal(MediaBadgeKind.Video, MediaLibraryBadges.ForRow(Row(Entry("a.webm", SavedMediaKind.Video)), false).Kind);
        Assert.Equal(0, MediaLibraryBadges.ForRow(Row(Entry()), false).TagCount);                       // editable, no media tag: untagged
        Assert.Null(MediaLibraryBadges.ForRow(Row(Entry(), tags: 0, editable: false), false).TagCount);  // cannot be tagged: no claim
        Assert.True(MediaLibraryBadges.ForRow(Row(Entry(booruPost: "7")), false).Booru);
        MediaLibraryEntry gone = Entry() with { Presence = MediaLibraryPresence.Missing };
        Assert.True(MediaLibraryBadges.ForRow(new MediaLibraryTileRow(new MediaLibraryTile(gone, [gone]), 0), false).Missing);

        MediaBadgeOptions options = new MediaLibraryTileView().Badges.Options;
        Assert.Equal(MediaBadgeOptions.MediaPage, options);
        Assert.True(options.ShowUntagged && options.ShowBooru && options.PlayMark == MediaPlayMarkMode.Drawn);
    }

    [Fact]
    public void AGroupIsAFavouriteWhenOneOfItsOccurrencesIs()
    {
        MediaLibraryEntry first = Entry(), second = first with { Thread = new ResourceId("s", "b", "2") };
        foreach ((MediaLibraryFavoriteState state, bool expected) in new[] { (MediaLibraryFavoriteState.All, true), (MediaLibraryFavoriteState.Some, true),
            (MediaLibraryFavoriteState.None, false), (MediaLibraryFavoriteState.Unknown, false) })
            Assert.Equal(expected, MediaLibraryBadges.IsFavorite(Row(first, tile: new MediaLibraryTile(first, [first, second], state))));
    }

    [Fact]
    public void TheCaptionPartsHideOnlyTheirOwnThreeFactsFromThePictureNotFromTheName()
    {
        MediaLibraryEntry entry = Entry("a.mp4", SavedMediaKind.Video, booruPost: "7");
        MediaLibraryTileView view = new() { InTagQueue = _ => true, DataContext = Row(entry, MediaLibraryFlag.Yes, tags: 2) };
        MediaBadgeState all = view.Badges.State;
        Assert.True(all is { Favorite: true, Queued: true, Booru: true, TagCount: 2, Kind: MediaBadgeKind.Video });
        string name = AutomationProperties.GetName(view)!;

        // Tags, queue and source off (and the name and thread lines too): the picture keeps favourite and kind, the name keeps every fact.
        view.Appearance = MediaTileMetrics.For(MediaTileSize.Medium, MediaTileCaption.None);
        Assert.Equal(all with { TagCount = null, Queued = false, Booru = false }, view.Badges.State);
        Assert.Equal(name, AutomationProperties.GetName(view));
        foreach (string key in new[] { "media.badge.favorite", "media.badge.queued", "media.badge.booru" }) Assert.Contains(Texts.T(key), name);
        Assert.Contains(Texts.Count("media.badge.tags", 2, "2"), name);

        // The name and source lines alone never touch a badge.
        view.Appearance = MediaTileMetrics.For(MediaTileSize.Medium, MediaTileCaption.Name);
        Assert.Equal(all with { TagCount = null, Queued = false, Booru = false }, view.Badges.State);
        view.Appearance = MediaTileMetrics.For(MediaTileSize.Medium, MediaTileCaption.TagCount | MediaTileCaption.QueueMark | MediaTileCaption.Source);
        Assert.Equal(all, view.Badges.State);
        // The untagged mark is the absence of tags: it stays with the tags off.
        view.DataContext = Row(Entry());
        view.Appearance = MediaTileMetrics.For(MediaTileSize.Medium, MediaTileCaption.None);
        Assert.Equal(0, view.Badges.State.TagCount);
    }

    [Fact]
    public void ARecycledTileTakesTheBadgesOffAndTheNameFollowsTheLanguage()
    {
        MediaLibraryTileView view = new() { DataContext = Row(Entry("a.gif"), MediaLibraryFlag.Yes) };
        Assert.Contains(Texts.T("media.badge.favorite"), view.BadgeDescription);
        Texts.SetLanguage("de");
        view.DataContext = Row(Entry("a.gif"), MediaLibraryFlag.Yes);
        Assert.Contains(Texts.T("media.badge.favorite"), view.BadgeDescription);
        Assert.NotEqual("Favorite", Texts.T("media.badge.favorite"));
        view.DataContext = null;
        Assert.Equal(default, view.Badges.State);
        Assert.Equal(string.Empty, view.BadgeDescription);
    }

    [Fact]
    public void TheFavouriteToggleIsQuietAtRestBecauseTheBadgeShowsTheState()
    {
        MediaLibraryTileView view = new() { FavoriteToggle = _ => Task.CompletedTask, DataContext = Row(Entry(), MediaLibraryFlag.Yes) };
        ToggleButton star = view.FavoriteToggleButton;
        Assert.Contains(MediaLibraryTileView.QuietFavoriteClass, star.Classes);
        Assert.True(star.IsChecked == true && view.Badges.State.Favorite);
        string styles = File.ReadAllText(Path.Combine(Root(), "Desktop/WorkspaceStyles.axaml"));
        Assert.Contains("ToggleButton.tile-favorite.tile-favorite-quiet:checked", styles);
        Assert.Contains("ListBoxItem:pointerover ToggleButton.tile-favorite.tile-favorite-quiet", styles);
    }

    [Fact]
    public void TheTopRightExtentKeepsTheSelectionCheckBesideABadgeOfThatCorner()
    {
        MediaBadgePlan none = MediaBadgeLayout.Plan(new MediaBadgeState(Favorite: true), new Size(120, 90), MediaBadgeOptions.MediaPage);
        Assert.Equal(0, MediaLibraryBadges.TopRightExtent(none, 120));
        MediaBadgePlan missing = MediaBadgeLayout.Plan(new MediaBadgeState(Missing: true), new Size(120, 90), MediaBadgeOptions.MediaPage);
        PlacedBadge badge = Assert.Single(missing.Badges);
        Assert.Equal(120 - badge.Bounds.Left + MediaLibraryBadges.CornerGap, MediaLibraryBadges.TopRightExtent(missing, 120), 6);
    }

    [Fact]
    public void RebindingAnUnchangedStateAllocatesNothing()
    {
        MediaLibraryEntry entry = Entry("a.gif");
        MediaLibraryTileView view = new() { InTagQueue = _ => true, DataContext = Row(entry, MediaLibraryFlag.Yes, tags: 2) };
        for (int warm = 0; warm < 8; warm++) view.RefreshQueueBadge();
        string sentence = view.BadgeDescription;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 2000; index++) view.RefreshQueueBadge();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(sentence, view.BadgeDescription);
        Assert.True(allocated < 16 * 1024, "A refresh of an unchanged state must not allocate: " + allocated + " bytes over 2000 refreshes.");
    }

    [Fact]
    public void TheOldPerTileMarksAreGone()
    {
        string tile = File.ReadAllText(Path.Combine(Root(), "Desktop/MediaLibraryTileView.cs"));
        foreach (string gone in new[] { "_kindBadge", "_stateMark", "_queueMark", "_tagCountBadge", "_providerBadge" }) Assert.DoesNotContain(gone, tile);
        Assert.Contains("new() { Name = \"MediaTileBadges\", Options = MediaBadgeOptions.MediaPage }", tile);
    }

    private static string Root()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("MeowChan.csproj");
    }
}
