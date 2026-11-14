using Avalonia;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// MB1b (tagging grid): what a grid tile tells the shared media badges. Pure: the state of an entry, the plan the badge view draws for it, the
// sentence in the tile's name and the geometry of the shown picture. The tile and page are GUI controls (checked by source in
// TaggingPageSelectionTests and by the tagging-page smoke).
[Collection("Localization")]
public sealed class TaggingTileBadgesTests : IDisposable
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    public TaggingTileBadgesTests() { Texts.Initialize(); Texts.SetLanguage("en"); MediaBadgeLimits.Configure(MediaBadgeLimits.Default.CollapseBelow, MediaBadgeLimits.Default.HideBelow, MediaBadgeLimits.Default.MaxPerCorner); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); MediaBadgeLimits.Configure(MediaBadgeLimits.Default.CollapseBelow, MediaBadgeLimits.Default.HideBelow, MediaBadgeLimits.Default.MaxPerCorner); }

    private static TaggingQueueRow Row(int n, TagQueueState state = TagQueueState.Open, SavedMediaKind kind = SavedMediaKind.Image, string? booru = null, bool next = false) =>
        new(new TagQueueItem(Thread, "/media/123", "asset-" + n, (n % 16).ToString("x") + new string('a', 63), 100 + n, "media-" + n, "original",
            "p" + n, null, null, booru, false, null, "file-" + n + ".png", kind, state, T0.AddSeconds(n)), n - 1, next);

    private static string[] Ids(MediaBadgePlan plan) => plan.Badges.Select(placed => placed.Badge.Id.ToString()).ToArray();

    private static readonly Size Picture = new(160, 124);

    private static string Read(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    [Fact]
    public void TheStateFollowsTheEntryTheTagCountAndTheBooruSource()
    {
        Assert.Equal(new MediaBadgeState(TagCount: 0), TaggingTileBadges.StateOf(Row(1), 0));
        Assert.Equal(new MediaBadgeState(), TaggingTileBadges.StateOf(Row(1), null));
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Video, TagCount: 3, Tagging: MediaTaggingState.Done),
            TaggingTileBadges.StateOf(Row(2, TagQueueState.Done, SavedMediaKind.Video), 3));
        Assert.Equal(MediaTaggingState.Skipped, TaggingTileBadges.StateOf(Row(3, TagQueueState.Skipped), null).Tagging);
        Assert.True(TaggingTileBadges.StateOf(Row(4, booru: "991"), null).Booru);
        Assert.False(TaggingTileBadges.StateOf(Row(4), null).Booru);
        // Never "in the queue" (the grid is the queue) and no favourite, rating or missing mark: the queue knows none of them.
        MediaBadgeState all = TaggingTileBadges.StateOf(Row(5, TagQueueState.Done, SavedMediaKind.Video, "992", next: true), 7);
        Assert.False(all.Queued);
        Assert.False(all.Favorite);
        Assert.False(all.Missing);
        Assert.Null(all.Rating);
    }

    [Fact]
    public void TheGridShowsTheStateTheUntaggedMarkAndTheBooruGlobeByDefault()
    {
        TaggingTileBadges badges = new();
        Assert.Equal(MediaBadgeOptions.TaggingGrid, badges.View.Options);
        Assert.True(MediaBadgeOptions.TaggingGrid is { ShowUntagged: true, ShowBooru: true });
        badges.Show(Row(1, TagQueueState.Skipped, booru: "991"), 0, Picture);
        Assert.Equal(["Tagging", "Booru", "Untagged"], Ids(badges.View.Plan));
        Assert.Equal([MediaBadgeCorner.TopRight, MediaBadgeCorner.TopRight, MediaBadgeCorner.BottomRight], badges.View.Plan.Badges.Select(placed => placed.Badge.Corner));
        Assert.Equal(["skip-forward", "globe", "untagged"], badges.View.Plan.Badges.Select(placed => placed.Badge.Icon));
        Assert.True(badges.View.IsVisible);
        // A finished video with tags: the play mark, the check and the count; nothing is left of the number or the "next" mark.
        badges.Show(Row(2, TagQueueState.Done, SavedMediaKind.Video, next: true), 3, Picture);
        Assert.Equal(["PlayMark", "Tagging", "TagCount"], Ids(badges.View.Plan));
        Assert.Equal("check", badges.View.Plan.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.Tagging).Badge.Icon);
        // An open image whose tags are not read yet carries nothing (not even the untagged mark: unknown is not none).
        badges.Show(Row(3), null, Picture);
        Assert.Empty(badges.View.Plan.Badges);
        Assert.False(badges.View.IsVisible);
    }

    [Fact]
    public void ARecycledTileTakesItsBadgesOffAndTinyPicturesCarryNone()
    {
        TaggingTileBadges badges = new();
        badges.Show(Row(1, TagQueueState.Done), 2, Picture);
        Assert.NotEmpty(badges.View.Plan.Badges);
        badges.Show(null, null, Picture);
        Assert.Empty(badges.View.Plan.Badges);
        Assert.Equal(string.Empty, badges.Description);
        Assert.Equal(default, badges.State);
        // Below the hide limit (32 dip) the view is hidden: name and tooltip alone carry the facts.
        badges.Show(Row(1, TagQueueState.Done), 2, new Size(160, 20));
        Assert.Empty(badges.View.Plan.Badges);
        Assert.NotEqual(string.Empty, badges.Description);
        // Place follows the picture without a new row, and does nothing for a tile with none.
        badges.Place(Picture);
        Assert.Equal(["Tagging", "TagCount"], Ids(badges.View.Plan));
        TaggingTileBadges empty = new();
        empty.Place(Picture);
        Assert.Empty(empty.View.Plan.Badges);
    }

    [Fact]
    public void TheSentenceNamesTheBadgesInEnglishAndGermanAndIsKeptUntilTheStateOrTheLanguageChanges()
    {
        TaggingTileBadges badges = new();
        badges.Show(Row(1, TagQueueState.Done, SavedMediaKind.Video, "991"), 3, Picture);
        Assert.Equal("Video · Tagging done · 3 tags · Booru source", badges.Description);
        Assert.Same(badges.Description, badges.Description);
        string english = badges.Description;
        badges.Show(Row(1, TagQueueState.Done, SavedMediaKind.Video, "991"), 3, new Size(100, 80));
        Assert.Same(english, badges.Description);
        Texts.SetLanguage("de");
        Assert.Equal("Video · Taggen erledigt · 3 Tags · Booru-Quelle", badges.Description);
        Texts.SetLanguage("en");
        badges.Show(Row(1, TagQueueState.Open), 0, Picture);
        Assert.Equal("No tags yet", badges.Description);
        // The sentence goes behind the row's own words in the tile's name; an entry without any badge keeps the plain name.
        Assert.Equal("Entry 1: file-1.png, Open, the walk continues here", TaggingQueueView.TileName(Row(1, next: true), null));
        Assert.Equal("Entry 1: file-1.png, Open, No tags yet", TaggingQueueView.TileName(Row(1), 0));
        Assert.Equal("Tagging skipped · 1 tag", TaggingTileBadges.DescribeOf(Row(2, TagQueueState.Skipped), 1));
    }

    [Fact]
    public void TheSelectionCheckKeepsClearOfTheBadgesOfTheTopRightCorner()
    {
        TaggingTileBadges badges = new();
        badges.Show(Row(1), null, Picture);
        Assert.Equal(0, TaggingTileBadges.TopRightReach(badges.View.Plan, Picture.Width));
        badges.Show(Row(1, TagQueueState.Done, booru: "991"), null, Picture);
        double reach = TaggingTileBadges.TopRightReach(badges.View.Plan, Picture.Width);
        PlacedBadge left = badges.View.Plan.Badges.Where(placed => placed.Badge.Corner == MediaBadgeCorner.TopRight).MinBy(placed => placed.Bounds.Left);
        Assert.Equal(Picture.Width - left.Bounds.Left, reach);
        // Two chips of 20 dip at most plus the margin: more than one chip, less than the tile.
        Assert.InRange(reach, 2 * 12, Picture.Width / 2);
    }

    [Fact]
    public void TheGridAnchorsItsBadgesToTheSlotSoPortraitAndLandscapeTilesCarryThemAtTheSameHeight()
    {
        Assert.Equal(MediaBadgeAnchor.Slot, MediaBadgeOptions.TaggingGrid.Anchor);
        Size slot = new(160, 124);
        Size portrait = TaggingThumbnail.FittedSize(new Size(400, 600), slot), landscape = TaggingThumbnail.FittedSize(new Size(640, 360), slot), wide = TaggingThumbnail.FittedSize(new Size(800, 200), slot);
        PlacedBadge[] Chips(Size picture)
        {
            TaggingTileBadges badges = new();
            badges.Show(Row(1, TagQueueState.Done, SavedMediaKind.Video, "991"), 3, picture, slot);
            Assert.Equal((slot.Width, slot.Height), (badges.View.Width, badges.View.Height));   // the view lies on the whole slot
            PlacedBadge mark = badges.View.Plan.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.PlayMark);
            Assert.Equal(slot.Width / 2, mark.Bounds.Center.X, 6);   // the mark is the picture's: centred on it, and the picture is centred in the slot
            Assert.Equal(slot.Height / 2, mark.Bounds.Center.Y, 6);
            return badges.View.Plan.Badges.Where(placed => placed.Badge.Id != MediaBadgeId.PlayMark).ToArray();
        }
        PlacedBadge[] expected = Chips(landscape);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, Chips(portrait));
        Assert.Equal(expected, Chips(wide));
        Assert.All(expected, placed => Assert.True(new Rect(default, slot).Contains(placed.Bounds.BottomRight)));
        // The tile follows a changed slot with Place and the check's reach is measured against the slot.
        TaggingTileBadges tile = new();
        tile.Show(Row(1, TagQueueState.Done, booru: "991"), null, landscape, slot);
        tile.Place(portrait, new Size(200, 124));
        Assert.Equal(200, tile.View.Width);
        double reach = TaggingTileBadges.TopRightReach(tile.View.Plan, 200);
        PlacedBadge leftmost = tile.View.Plan.Badges.Where(placed => placed.Badge.Corner == MediaBadgeCorner.TopRight).MinBy(placed => placed.Bounds.Left);
        Assert.Equal(200 - leftmost.Bounds.Left, reach);
        string source = Read("Desktop/Views/Tagging/TaggingQueueTileView.cs");
        Assert.Contains("_badges.Show(row, tags, _preview.PictureSize, _preview.Bounds.Size)", source);
        Assert.Contains("TopRightReach(_badges.View.Plan, _preview.Bounds.Width)", source);
    }

    [Fact]
    public void TheShownPictureIsTheLetterboxedFitOfTheSlotAndTheSlotItselfWhileNoneIsShown()
    {
        Size slot = new(160, 124);
        Assert.Equal(slot, TaggingThumbnail.FittedSize(null, slot));
        Assert.Equal(slot, TaggingThumbnail.FittedSize(new Size(0, 10), slot));
        Assert.Equal(new Size(160, 80), TaggingThumbnail.FittedSize(new Size(320, 160), slot));
        Assert.Equal(new Size(62, 124), TaggingThumbnail.FittedSize(new Size(31, 62), slot));
        // A small picture is scaled up uniformly like the image control does.
        Assert.Equal(new Size(124, 124), TaggingThumbnail.FittedSize(new Size(20, 20), slot));
        Assert.Equal(default, TaggingThumbnail.FittedSize(null, default));
    }
}
