using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// MB1a (unified media badges): the model that turns the state of a medium into badges (corner, priority, icon), the geometry (unit, collapse to
/// dots, hiding, corner capacity, overlap), the spoken sentence in English and German (the other ten packs are covered by the pack completeness
/// tests and the plural checks below), the three settings, the colour rule and a creation budget for a grid of tiles. The drawing itself needs
/// a render platform: its visual proof is the smokes that show the surfaces migrated in MB1b and the reader sidebar demo (reader-preview).
/// </summary>
[Collection("Localization")]
public sealed class MediaBadgeTests : IDisposable
{
    public MediaBadgeTests() { Texts.Initialize(); Texts.SetLanguage("en"); MediaBadgeLimits.Configure(Defaults.CollapseBelow, Defaults.HideBelow, Defaults.MaxPerCorner); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); MediaBadgeLimits.Configure(Defaults.CollapseBelow, Defaults.HideBelow, Defaults.MaxPerCorner); }

    private static MediaBadgeLimits Defaults => MediaBadgeLimits.Default;
    private static readonly MediaBadgeOptions Everything = MediaBadgeOptions.Everything;

    private static readonly MediaBadgeState Full = new(
        Favorite: true, Rating: 4, Kind: MediaBadgeKind.Video, Duration: TimeSpan.FromSeconds(42), TagCount: 3, Queued: true,
        Missing: true, Booru: true);

    private static string[] Ids(IEnumerable<MediaBadge> badges) => badges.Select(badge => badge.Id.ToString()).ToArray();

    // ---- corner and priority resolution ----

    [Fact]
    public void ADefaultStateHasNoBadge() =>
        Assert.Empty(MediaBadgeModel.Resolve(default, Everything));

    [Fact]
    public void TheBadgesAreSortedByPriorityAndKnowTheirCornerAndIcon()
    {
        MediaBadge[] badges = MediaBadgeModel.Resolve(Full, Everything);
        Assert.Equal(["Missing", "Favorite", "PlayMark", "Video", "Queued", "TagCount", "Rating", "Booru"], Ids(badges));
        Assert.Equal([1, 2, 3, 3, 4, 5, 7, 8], badges.Select(badge => badge.Priority));
        Assert.Equal(
            [MediaBadgeCorner.TopRight, MediaBadgeCorner.TopLeft, MediaBadgeCorner.Center, MediaBadgeCorner.BottomLeft,
             MediaBadgeCorner.TopRight, MediaBadgeCorner.BottomRight, MediaBadgeCorner.TopLeft, MediaBadgeCorner.TopRight],
            badges.Select(badge => badge.Corner));
        Assert.Equal(["x-circle", "star-filled", null, null, "clipboard", "tag", "star", "globe"], badges.Select(badge => badge.Icon));
        Assert.Equal([null, null, null, "0:42", null, "3", "4", null], badges.Select(badge => badge.Label));
    }

    [Fact]
    public void ACornerMeansOneThing()
    {
        // Top left: the verdict. Top right: the state. Bottom left: the kind. Bottom right: the tags. The centre: the play mark.
        foreach (MediaBadge badge in MediaBadgeModel.Resolve(Full with { Tagging = MediaTaggingState.Done }, Everything))
            Assert.Equal(
                badge.Id switch
                {
                    MediaBadgeId.Favorite or MediaBadgeId.Rating => MediaBadgeCorner.TopLeft,
                    MediaBadgeId.Missing or MediaBadgeId.Queued or MediaBadgeId.Tagging or MediaBadgeId.Booru => MediaBadgeCorner.TopRight,
                    MediaBadgeId.Video or MediaBadgeId.Gif => MediaBadgeCorner.BottomLeft,
                    MediaBadgeId.TagCount or MediaBadgeId.Untagged => MediaBadgeCorner.BottomRight,
                    _ => MediaBadgeCorner.Center
                }, badge.Corner);
    }

    [Fact]
    public void AGifIsExclusiveToAVideoAndAnImageHasNoKindBadge()
    {
        MediaBadge gif = Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(Kind: MediaBadgeKind.Gif, Duration: TimeSpan.FromSeconds(3)), Everything));
        Assert.Equal((MediaBadgeId.Gif, MediaBadgeCorner.BottomLeft, 3, (string?)null, "GIF"), (gif.Id, gif.Corner, gif.Priority, gif.Icon, gif.Label));
        Assert.Empty(MediaBadgeModel.Resolve(new MediaBadgeState(Kind: MediaBadgeKind.Image, Duration: TimeSpan.FromSeconds(3)), Everything));
    }

    [Fact]
    public void AVideoGetsAPlayMarkAndAChipWithItsLengthOnlyWhenItIsKnownUnlessThereIsNoMark()
    {
        MediaBadgeState video = new(Kind: MediaBadgeKind.Video);
        Assert.Equal(["PlayMark"], Ids(MediaBadgeModel.Resolve(video, Everything)));
        Assert.Equal(["PlayMark", "Video"], Ids(MediaBadgeModel.Resolve(video with { Duration = TimeSpan.FromMinutes(75) }, Everything)));
        Assert.Equal("1:15:00", MediaBadgeModel.Resolve(video with { Duration = TimeSpan.FromMinutes(75) }, Everything)[1].Label);
        // An own mark that the surface keeps (the sidebar's play button): no second one, same chip.
        MediaBadgeOptions external = MediaBadgeOptions.Sidebar;
        Assert.Empty(MediaBadgeModel.Resolve(video, external));
        Assert.Equal(["Video"], Ids(MediaBadgeModel.Resolve(video with { Duration = TimeSpan.FromSeconds(5) }, external)));
        Assert.Null(MediaBadgeModel.Resolve(video with { Duration = TimeSpan.FromSeconds(5) }, external)[0].Icon);
        // No mark at all: the chip says "video" itself, with the play glyph, also without a length.
        MediaBadgeOptions none = new(PlayMark: MediaPlayMarkMode.None);
        MediaBadge chip = Assert.Single(MediaBadgeModel.Resolve(video, none));
        Assert.Equal(("play", null), (chip.Icon, chip.Label));
        Assert.Equal(("play", "0:05"), (MediaBadgeModel.Resolve(video with { Duration = TimeSpan.FromSeconds(5) }, none)[0].Icon,
            MediaBadgeModel.Resolve(video with { Duration = TimeSpan.FromSeconds(5) }, none)[0].Label));
    }

    [Fact]
    public void TheQueueMarkAndTheTaggingStateShareTheirPriorityAndCorner()
    {
        MediaBadge done = Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(Tagging: MediaTaggingState.Done), Everything));
        MediaBadge skipped = Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(Tagging: MediaTaggingState.Skipped), Everything));
        MediaBadge queued = Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(Queued: true), Everything));
        Assert.Equal(["check", "skip-forward", "clipboard"], new[] { done.Icon, skipped.Icon, queued.Icon });
        Assert.All(new[] { done, skipped, queued }, badge => Assert.Equal((MediaBadgeCorner.TopRight, 4), (badge.Corner, badge.Priority)));
    }

    [Fact]
    public void TheTagCountIsShownFromOneAndCappedAndNoTagsIsAnOptInMark()
    {
        Assert.Empty(MediaBadgeModel.Resolve(new MediaBadgeState(TagCount: null), Everything));
        Assert.Empty(MediaBadgeModel.Resolve(new MediaBadgeState(TagCount: 0), MediaBadgeOptions.Default));
        MediaBadge untagged = Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(TagCount: 0), MediaBadgeOptions.MediaPage));
        Assert.Equal((MediaBadgeId.Untagged, MediaBadgeCorner.BottomRight, 6, "untagged"), (untagged.Id, untagged.Corner, untagged.Priority, untagged.Icon));
        Assert.Equal("1", Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(TagCount: 1), Everything)).Label);
        Assert.Equal("99", Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(TagCount: 99), Everything)).Label);
        Assert.Equal("99+", Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(TagCount: 100), Everything)).Label);
    }

    [Fact]
    public void TheRatingIsShownFromOneAndNeverAboveFiveAndTheBooruMarkIsOptIn()
    {
        Assert.Empty(MediaBadgeModel.Resolve(new MediaBadgeState(Rating: 0), Everything));
        Assert.Empty(MediaBadgeModel.Resolve(new MediaBadgeState(Rating: -2), Everything));
        Assert.Equal("5", Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(Rating: 9), Everything)).Label);
        Assert.Empty(MediaBadgeModel.Resolve(new MediaBadgeState(Booru: true), MediaBadgeOptions.Default));
        Assert.Equal(MediaBadgeId.Booru, Assert.Single(MediaBadgeModel.Resolve(new MediaBadgeState(Booru: true), MediaBadgeOptions.TaggingGrid)).Id);
    }

    [Fact]
    public void TheSurfacePresetsFollowTheRecommendationsOfTheSpec()
    {
        // F6: the booru mark only on the media page and the tagging grid. F7: the untagged mark likewise. The filmstrip is dense; the sidebar keeps its own mark.
        Assert.Equal((true, true), (MediaBadgeOptions.MediaPage.ShowBooru, MediaBadgeOptions.MediaPage.ShowUntagged));
        Assert.Equal((true, true), (MediaBadgeOptions.TaggingGrid.ShowBooru, MediaBadgeOptions.TaggingGrid.ShowUntagged));
        foreach (MediaBadgeOptions off in new[] { MediaBadgeOptions.Archive, MediaBadgeOptions.Filmstrip, MediaBadgeOptions.Sidebar, MediaBadgeOptions.Default })
            Assert.Equal((false, false), (off.ShowBooru, off.ShowUntagged));
        Assert.Equal(MediaBadgeDensity.Dense, MediaBadgeOptions.Filmstrip.Density);
        Assert.Equal(MediaPlayMarkMode.External, MediaBadgeOptions.Sidebar.PlayMark);
    }

    [Fact]
    public void EveryIconIsAnExistingMaskOrTheOneDrawnMark()
    {
        string masks = Path.Combine(RepoRoot(), "Assets", "icons", "feather", "masks");
        foreach (string icon in MediaBadgeIcons.Masks) Assert.True(File.Exists(Path.Combine(masks, icon + ".png")), "Missing mask: " + icon);
        string[] used = Enum.GetValues<MediaTaggingState>().SelectMany(tagging => new[] { MediaBadgeKind.Image, MediaBadgeKind.Video, MediaBadgeKind.Gif }
                .SelectMany(kind => new[] { MediaPlayMarkMode.Drawn, MediaPlayMarkMode.None }.SelectMany(mark => new[] { 0, 2 }.SelectMany(tags =>
                    MediaBadgeModel.Resolve(Full with { Kind = kind, Tagging = tagging, TagCount = tags }, new MediaBadgeOptions(true, true, mark))))))
            .Where(badge => badge.Icon != null).Select(badge => badge.Icon!).Distinct().Order().ToArray();
        Assert.All(used, icon => Assert.True(icon == MediaBadgeIcons.Untagged || MediaBadgeIcons.Masks.Contains(icon), icon));
        Assert.Equal(WorkspaceChipsUntagged, "M1,1 L6,1 L11,6 L6,11 L1,6 Z M3.6,3.6 L3.7,3.7 M1,11 L11,1");
    }

    private static string WorkspaceChipsUntagged => MeowChan.Desktop.Controls.WorkspaceChips.MarkUntagged;

    // ---- the corner capacity and the order inside a corner ----

    [Fact]
    public void ACornerHoldsAtMostTheConfiguredNumberAndTheLeastImportantDropOut()
    {
        MediaBadgeState topRight = new(Missing: true, Queued: true, Booru: true);
        Size picture = new(160, 160);
        string[] Placed(int perCorner) => Ids(MediaBadgeLayout.Plan(topRight, picture, Everything, new MediaBadgeLimits(48, 32, perCorner)).Badges.Select(placed => placed.Badge));
        Assert.Equal(["Missing"], Placed(1));
        Assert.Equal(["Missing", "Queued"], Placed(2));
        Assert.Equal(["Missing", "Queued", "Booru"], Placed(3));
        // A limit outside its range is brought into it: 0 behaves as 1, 9 as 3.
        Assert.Equal(["Missing"], Placed(0));
        Assert.Equal(["Missing", "Queued", "Booru"], Placed(9));
    }

    [Fact]
    public void TheBadgesOfACornerStandNextToTheCornerFirstAndGrowAwayFromIt()
    {
        MediaBadgePlan plan = MediaBadgeLayout.Plan(Full, new Size(160, 160), Everything, Defaults);
        Dictionary<MediaBadgeId, Rect> at = plan.Badges.ToDictionary(placed => placed.Badge.Id, placed => placed.Bounds);
        double margin = MediaBadgeLayout.Margin(plan.Unit);
        Assert.Equal(20, plan.Unit);
        Assert.Equal(margin, at[MediaBadgeId.Favorite].Left);                              // top left: favourite at the corner, rating after it
        Assert.True(at[MediaBadgeId.Rating].Left >= at[MediaBadgeId.Favorite].Right);
        Assert.Equal(160 - margin, at[MediaBadgeId.Missing].Right);                        // top right: missing at the corner, queue after it
        Assert.True(at[MediaBadgeId.Queued].Right <= at[MediaBadgeId.Missing].Left);
        Assert.DoesNotContain(MediaBadgeId.Booru, plan.Badges.Select(placed => placed.Badge.Id)); // the third of the top right drops out (default 2)
        Assert.Equal(margin, at[MediaBadgeId.Video].Left);                                 // bottom left
        Assert.Equal(160 - margin - plan.Unit, at[MediaBadgeId.Video].Top);
        Assert.Equal(160 - margin, at[MediaBadgeId.TagCount].Right);                       // bottom right
        Assert.Equal(margin, at[MediaBadgeId.Missing].Top);
        Rect mark = at[MediaBadgeId.PlayMark];                                              // the centre belongs to the play mark
        Assert.Equal(80, mark.Center.X, 6);
        Assert.Equal(80, mark.Center.Y, 6);
        Assert.Equal(VideoPlayMark.Diameter(new Size(160, 160)), mark.Width, 6);
    }

    [Fact]
    public void TheDensePlayMarkOfAFilmstripTileIsTheSmallestCircle()
    {
        MediaBadgeState video = new(Kind: MediaBadgeKind.Video);
        Rect Mark(MediaBadgeOptions options, double side) =>
            MediaBadgeLayout.Plan(video, new Size(side, side), options, Defaults).Badges.Single(placed => placed.Badge.Id == MediaBadgeId.PlayMark).Bounds;
        Assert.Equal(20.48, Mark(MediaBadgeOptions.Filmstrip, 64).Width, 2);
        Assert.Equal(VideoPlayMark.Diameter(new Size(64, 64)), Mark(MediaBadgeOptions.Default, 64).Width, 6);   // 0.45 x 64 = 28.8
        Assert.True(Mark(MediaBadgeOptions.Filmstrip, 100).Width < Mark(MediaBadgeOptions.Default, 100).Width);   // 32 against the largest circle, 44
        Assert.Equal(VideoPlayMark.Diameter(new Size(64, 64), VideoPlayMark.DenseRatio), Mark(MediaBadgeOptions.Filmstrip, 64).Width, 6);
    }

    // ---- unit ----

    [Theory]
    [InlineData(48, 12)]     // 9.6 raised to the smallest unit
    [InlineData(60, 12)]
    [InlineData(64, 12)]     // 12.8 -> 12
    [InlineData(70, 14)]     // 14
    [InlineData(76, 16)]     // 15.2 -> 16: the 76 dip row
    [InlineData(84, 16)]
    [InlineData(90, 18)]
    [InlineData(94, 18)]     // 18.8 -> 18
    [InlineData(96, 20)]     // 19.2 -> 20
    [InlineData(100, 20)]
    [InlineData(160, 20)]    // 32 capped at 20
    [InlineData(4000, 20)]
    [InlineData(0, 12)]
    [InlineData(-5, 12)]
    [InlineData(double.NaN, 12)]
    public void TheUnitIsTheNearestEvenNumberToTwentyPercentOfTheShorterSideFromTwelveToTwenty(double shortSide, double expected) =>
        Assert.Equal(expected, MediaBadgeLayout.Unit(shortSide));

    [Fact]
    public void TheUnitIsEvenInsideItsRangeAndNeverShrinksWhenThePictureGrows()
    {
        double previous = 0;
        for (double side = 1; side <= 600; side += 0.5)
        {
            double unit = MediaBadgeLayout.Unit(side);
            Assert.InRange(unit, MediaBadgeLayout.UnitMinimum, MediaBadgeLayout.UnitMaximum);
            Assert.Equal(0, unit % 2);
            Assert.True(unit >= previous, $"the unit shrank at {side}");
            previous = unit;
        }
    }

    [Theory]
    [InlineData(12, 9, 3)]
    [InlineData(16, 12, 4)]
    [InlineData(20, 12, 5)]
    public void TheFontMarginAndIconFollowTheUnit(double unit, double font, double margin)
    {
        Assert.Equal(font, MediaBadgeLayout.FontSize(unit));
        Assert.Equal(margin, MediaBadgeLayout.Margin(unit));
        Assert.Equal(unit - 4, MediaBadgeLayout.IconSize(MediaBadgeId.Favorite, unit));
        Assert.Equal(0.6 * unit, MediaBadgeLayout.IconSize(MediaBadgeId.Rating, unit), 6);
        Assert.InRange(MediaBadgeLayout.FontSize(unit), MediaBadgeLayout.FontMinimum, MediaBadgeLayout.FontMaximum);
    }

    // ---- collapse thresholds ----

    [Theory]
    [InlineData(0, "Hidden")]
    [InlineData(31.9, "Hidden")]
    [InlineData(32, "Dots")]
    [InlineData(47.9, "Dots")]
    [InlineData(48, "Full")]
    [InlineData(500, "Full")]
    [InlineData(double.NaN, "Hidden")]
    public void BelowFortyEightThereAreDotsAndBelowThirtyTwoNothingByDefault(double shortSide, string expected) =>
        Assert.Equal(expected, MediaBadgeLayout.ModeFor(shortSide, Defaults).ToString());

    [Fact]
    public void TheThresholdsAreTheSettingsAndHideNeverExceedsCollapse()
    {
        Assert.Equal(MediaBadgeMode.Full, MediaBadgeLayout.ModeFor(60, new MediaBadgeLimits(60, 20, 2)));
        Assert.Equal(MediaBadgeMode.Dots, MediaBadgeLayout.ModeFor(59.9, new MediaBadgeLimits(60, 20, 2)));
        Assert.Equal(MediaBadgeMode.Hidden, MediaBadgeLayout.ModeFor(19.9, new MediaBadgeLimits(60, 20, 2)));
        // Hide above collapse (64 > 24) is brought down to collapse: there is no gap where badges would be hidden but not dots.
        MediaBadgeLimits crossed = new MediaBadgeLimits(24, 64, 2).Normalize();
        Assert.Equal((24, 24), (crossed.CollapseBelow, crossed.HideBelow));
        Assert.Equal(MediaBadgeMode.Full, MediaBadgeLayout.ModeFor(24, crossed));
        Assert.Equal(MediaBadgeMode.Hidden, MediaBadgeLayout.ModeFor(23.9, crossed));
        // Values outside the ranges are brought inside.
        MediaBadgeLimits wild = new MediaBadgeLimits(-5, 9999, 99).Normalize();
        Assert.Equal((Config.MediaBadgeCollapseBelowMinimum, Config.MediaBadgeCollapseBelowMinimum, Config.MediaBadgeMaxPerCornerMaximum),
            (wild.CollapseBelow, wild.HideBelow, wild.MaxPerCorner));
        Assert.Equal(new MediaBadgeLimits(96, 64, 3), new MediaBadgeLimits(96, 64, 3).Normalize());
    }

    [Fact]
    public void ADotPictureShowsOnlyTheMostImportantBadgeOfEachCornerAsADotAndKeepsTheMark()
    {
        MediaBadgePlan plan = MediaBadgeLayout.Plan(Full, new Size(40, 40), Everything, Defaults);
        Assert.Equal(MediaBadgeMode.Dots, plan.Mode);
        PlacedBadge[] dots = plan.Badges.Where(placed => placed.Dot).ToArray();
        Assert.Equal(["Favorite", "Missing", "TagCount", "Video"], Ids(dots.Select(placed => placed.Badge)).Order(StringComparer.Ordinal));
        Assert.All(dots, dot => Assert.Equal((MediaBadgeLayout.DotSize, MediaBadgeLayout.DotSize), (dot.Bounds.Width, dot.Bounds.Height)));
        Assert.Single(dots, dot => dot.Badge.Corner == MediaBadgeCorner.TopRight && dot.Badge.Id == MediaBadgeId.Missing); // safety: always the top right dot
        PlacedBadge mark = Assert.Single(plan.Badges, placed => placed.Badge.Id == MediaBadgeId.PlayMark);
        Assert.False(mark.Dot);
        Assert.Equal(20, mark.Bounds.Width, 6);
        Assert.Equal(3, MediaBadgeLayout.Margin(plan.Unit));
        Assert.All(dots, dot => Assert.True(new Rect(0, 0, 40, 40).Contains(dot.Bounds)));
    }

    [Fact]
    public void BelowTheHideLimitNothingIsShownNotEvenAMissingFileOrTheMark()
    {
        Assert.Same(MediaBadgePlan.Empty, MediaBadgeLayout.Plan(Full, new Size(31, 400), Everything, Defaults));
        Assert.Empty(MediaBadgeLayout.Plan(Full, new Size(400, 31), Everything, Defaults).Badges);
        Assert.Empty(MediaBadgeLayout.Plan(Full, new Size(0, 0), Everything, Defaults).Badges);
        Assert.Empty(MediaBadgeLayout.Plan(Full, new Size(double.NaN, 100), Everything, Defaults).Badges);
        Assert.NotEmpty(MediaBadgeLayout.Plan(Full, new Size(32, 400), Everything, Defaults).Badges);
        // The size that counts is the shorter side of the shown picture: a wide strip of 31 dip is hidden, a 48 dip square is full.
        Assert.Equal(MediaBadgeMode.Full, MediaBadgeLayout.Plan(Full, new Size(48, 48), Everything, Defaults).Mode);
    }

    [Fact]
    public void NoBadgeMeansNoPlanObject()
    {
        Assert.Same(MediaBadgePlan.Empty, MediaBadgeLayout.Plan(default, new Size(160, 160), Everything, Defaults));
    }

    // ---- geometry holds on every size ----

    [Fact]
    public void EveryBadgeLiesInsideItsPictureAndNoTwoOverlapOnAnySize()
    {
        MediaBadgeState[] states =
        [
            Full, Full with { Tagging = MediaTaggingState.Skipped, TagCount = 120, Rating = 5, Duration = TimeSpan.FromHours(10) },
            new(Missing: true, Queued: true, Booru: true, Favorite: true, Rating: 1, Kind: MediaBadgeKind.Gif, TagCount: 0)
        ];
        foreach (MediaBadgeState state in states)
            foreach (int perCorner in new[] { 1, 2, 3 })
                for (double width = 32; width <= 400; width += 8)
                    foreach (double height in new[] { 32d, 48, 64, 90, 160, 400 })
                    {
                        Size picture = new(width, height);
                        MediaBadgePlan plan = MediaBadgeLayout.Plan(state, picture, Everything, new MediaBadgeLimits(48, 32, perCorner));
                        Rect all = new(0, 0, width, height);
                        PlacedBadge[] chips = plan.Badges.Where(placed => placed.Badge.Id != MediaBadgeId.PlayMark).ToArray();
                        foreach (PlacedBadge placed in plan.Badges)
                            Assert.True(all.Contains(placed.Bounds.TopLeft) && all.Contains(placed.Bounds.BottomRight), $"{placed.Badge.Id} outside {picture}: {placed.Bounds}");
                        for (int a = 0; a < chips.Length; a++)
                            for (int b = a + 1; b < chips.Length; b++)
                                Assert.False(chips[a].Bounds.Intersects(chips[b].Bounds), $"{chips[a].Badge.Id} meets {chips[b].Badge.Id} on {picture}");
                    }
    }

    [Fact]
    public void TwoStacksOfOneEdgeThatWouldMeetDropTheirLeastImportantBadges()
    {
        // 48 dip wide: top left favourite + rating, top right missing + queue + booru would need far more than the 42 dip between the margins.
        MediaBadgeState crowded = new(Favorite: true, Rating: 5, Missing: true, Queued: true, Booru: true);
        MediaBadgePlan plan = MediaBadgeLayout.Plan(crowded, new Size(48, 400), Everything, new MediaBadgeLimits(48, 32, 3));
        string[] ids = Ids(plan.Badges.Select(placed => placed.Badge));
        Assert.Contains("Favorite", ids);
        Assert.Contains("Missing", ids);
        Assert.DoesNotContain("Booru", ids);
        PlacedBadge left = plan.Badges.Last(placed => placed.Badge.Corner == MediaBadgeCorner.TopLeft);
        PlacedBadge right = plan.Badges.Last(placed => placed.Badge.Corner == MediaBadgeCorner.TopRight);
        Assert.True(left.Bounds.Right + MediaBadgeLayout.Gap <= right.Bounds.Left + 1e-9 || ids.Length == 2);
        // The same state on a wide picture keeps everything the capacity allows.
        Assert.Equal(5, MediaBadgeLayout.Plan(crowded, new Size(400, 400), Everything, new MediaBadgeLimits(48, 32, 3)).Badges.Length);
    }

    [Fact]
    public void WhenEvenOneChipEachWouldMeetTheLessImportantOneGivesWay()
    {
        // 48 dip: a video length and a tag count together are wider than the room between the margins; the tag count (priority 5) goes,
        // the length (3) stays. A wider picture keeps both. Both facts stay in the spoken sentence.
        MediaBadgeState both = new(Kind: MediaBadgeKind.Video, Duration: TimeSpan.FromSeconds(42), TagCount: 3);
        Assert.Equal(["Video"], Ids(MediaBadgeLayout.Plan(both, new Size(48, 48), MediaBadgeOptions.Sidebar, Defaults).Badges.Select(placed => placed.Badge)));
        Assert.Equal(["Video", "TagCount"], Ids(MediaBadgeLayout.Plan(both, new Size(64, 64), MediaBadgeOptions.Sidebar, Defaults).Badges.Select(placed => placed.Badge)));
        Assert.Contains("3 tags", MediaBadgeText.Describe(both));
    }

    [Fact]
    public void AChipIsAsWideAsItsContentNeedsButNeverNarrowerThanItsHeight()
    {
        double unit = 16, font = MediaBadgeLayout.FontSize(unit);
        double Width(MediaBadge badge) => MediaBadgeLayout.ChipWidth(badge, unit, font);
        MediaBadge iconOnly = new(MediaBadgeId.Favorite, MediaBadgeCorner.TopLeft, 2, "star-filled", null);
        MediaBadge count = new(MediaBadgeId.TagCount, MediaBadgeCorner.BottomRight, 5, "tag", "3");
        MediaBadge longCount = count with { Label = "99+" };
        MediaBadge gif = new(MediaBadgeId.Gif, MediaBadgeCorner.BottomLeft, 3, null, "GIF");
        MediaBadge time = new(MediaBadgeId.Video, MediaBadgeCorner.BottomLeft, 3, null, "1:15:00");
        Assert.Equal(unit, Width(iconOnly));
        Assert.True(Width(count) > unit);
        Assert.True(Width(longCount) > Width(count));
        Assert.True(Width(time) > Width(gif));
        Assert.True(Width(gif) >= unit);
        Assert.True(MediaBadgeLayout.EstimateTextWidth("0:42", font) > MediaBadgeLayout.EstimateTextWidth("1", font));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(0.9, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(42.7, "0:42")]
    [InlineData(60, "1:00")]
    [InlineData(599, "9:59")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(4505, "1:15:05")]
    [InlineData(-3, "0:00")]
    public void ADurationIsShownAsMinutesAndSecondsRoundedDownAndAsHoursFromAnHour(double seconds, string expected) =>
        Assert.Equal(expected, MediaBadgeLayout.FormatDuration(TimeSpan.FromSeconds(seconds)));

    // ---- the spoken sentence ----

    [Fact]
    public void TheDescriptionIsOneSentenceInPriorityOrderInEnglish()
    {
        Assert.Equal("File missing · Favorite · Video 0:42 · In the tagging queue · 3 tags · Rating 4 of 5 · Booru source", MediaBadgeText.Describe(Full));
        Assert.Equal("Favorite", MediaBadgeText.Describe(new MediaBadgeState(Favorite: true)));
        Assert.Equal("Video", MediaBadgeText.Describe(new MediaBadgeState(Kind: MediaBadgeKind.Video)));
        Assert.Equal("Animated image · 1 tag", MediaBadgeText.Describe(new MediaBadgeState(Kind: MediaBadgeKind.Gif, TagCount: 1)));
        Assert.Equal("Tagging done", MediaBadgeText.Describe(new MediaBadgeState(Tagging: MediaTaggingState.Done)));
        Assert.Equal("Tagging skipped · No tags yet", MediaBadgeText.Describe(new MediaBadgeState(Tagging: MediaTaggingState.Skipped, TagCount: 0)));
        Assert.Equal(string.Empty, MediaBadgeText.Describe(default));
        Assert.Equal(string.Empty, MediaBadgeText.Describe(new MediaBadgeState(Kind: MediaBadgeKind.Image, TagCount: null)));
    }

    [Fact]
    public void TheDescriptionIsOneSentenceInPriorityOrderInGerman()
    {
        Texts.SetLanguage("de");
        Assert.Equal("Datei fehlt · Favorit · Video 0:42 · In der Tagging-Warteschlange · 3 Tags · Bewertung 4 von 5 · Booru-Quelle", MediaBadgeText.Describe(Full));
        Assert.Equal("Animiertes Bild · 1 Tag", MediaBadgeText.Describe(new MediaBadgeState(Kind: MediaBadgeKind.Gif, TagCount: 1)));
        Assert.Equal("Tagging übersprungen · Noch keine Tags", MediaBadgeText.Describe(new MediaBadgeState(Tagging: MediaTaggingState.Skipped, TagCount: 0)));
        Assert.Equal("Video", MediaBadgeText.Describe(new MediaBadgeState(Kind: MediaBadgeKind.Video)));
    }

    [Fact]
    public void ASurfaceThatLeavesAMarkOutDoesNotSpeakItAndDotsDoNotChangeTheSentence()
    {
        MediaBadgeState untaggedBooru = new(TagCount: 0, Booru: true);
        Assert.Equal("No tags yet · Booru source", MediaBadgeText.Describe(untaggedBooru));
        Assert.Equal("No tags yet · Booru source", MediaBadgeText.Describe(untaggedBooru, MediaBadgeOptions.MediaPage));
        Assert.Equal(string.Empty, MediaBadgeText.Describe(untaggedBooru, MediaBadgeOptions.Filmstrip));
        // The sentence is the state, not what fits on the picture.
        string sentence = MediaBadgeText.Describe(Full);
        MediaBadgeView view = new();
        view.Apply(Full, new Size(40, 40), Everything);
        Assert.Equal(MediaBadgeMode.Dots, view.Plan.Mode);
        Assert.Equal(sentence, MediaBadgeText.Describe(view.State));
    }

    [Theory]
    [InlineData("es", 1, "1 etiqueta")]
    [InlineData("es", 3, "3 etiquetas")]
    [InlineData("fr", 1, "1 étiquette")]
    [InlineData("fr", 2, "2 étiquettes")]
    [InlineData("ru", 1, "1 тег")]
    [InlineData("ru", 3, "3 тега")]
    [InlineData("ru", 5, "5 тегов")]
    [InlineData("ru", 21, "21 тег")]
    [InlineData("cs", 1, "1 štítek")]
    [InlineData("cs", 3, "3 štítky")]
    [InlineData("cs", 5, "5 štítků")]
    [InlineData("pl", 1, "1 tag")]
    [InlineData("pl", 3, "3 tagi")]
    [InlineData("pl", 5, "5 tagów")]
    [InlineData("pl", 22, "22 tagi")]
    [InlineData("pt-BR", 1, "1 tag")]
    [InlineData("it", 7, "7 tag")]
    [InlineData("zh-Hans", 1, "1 个标签")]
    [InlineData("ja", 5, "タグ 5 件")]
    [InlineData("ko", 5, "태그 5개")]
    public void TheTagCountFollowsTheNumberFormsOfEveryBundledLanguage(string language, int count, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, MediaBadgeText.Describe(new MediaBadgeState(TagCount: count)));
    }

    [Fact]
    public void EveryBadgeTextExistsInAllTwelveLanguagesAndKeepsItsPlaceholders()
    {
        string[] keys = MediaBadgeCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        foreach ((string key, string english, string german) in MediaBadgeCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.False(string.IsNullOrWhiteSpace(german), key);
            for (int slot = 0; slot < 3; slot++)
                Assert.Equal(english.Contains("{" + slot + "}"), german.Contains("{" + slot + "}"));
        }
        foreach (string code in Texts.BundledPackCodes())
        {
            Texts.SetLanguage(code);
            foreach (string key in keys.Where(key => !key.EndsWith(".one", StringComparison.Ordinal)))
                Assert.False(string.IsNullOrWhiteSpace(Texts.Own(key)), code + " has no own text for " + key); // never the English fallback
            Assert.Contains("3", MediaBadgeText.Describe(new MediaBadgeState(Rating: 3)));
        }
        Texts.SetLanguage("en");
        Assert.Contains("48", Texts.T("settings.mediaBadgeCollapseBelowHint", 48, 24, 96));
    }

    // ---- the view ----

    [Fact]
    public void TheViewKeepsItsPlanForTheSameStateAndReplansOnAChange()
    {
        MediaBadgeView view = new();
        Assert.False(view.IsVisible);
        view.Apply(Full, new Size(160, 90), Everything);
        MediaBadgePlan first = view.Plan;
        Assert.True(view.IsVisible);
        Assert.Equal((160d, 90d), (view.Width, view.Height));
        view.Apply(Full, new Size(160, 90), Everything);
        Assert.Same(first, view.Plan);                                  // nothing recomputed, nothing allocated
        view.Apply(Full with { Favorite = false }, new Size(160, 90), Everything);
        Assert.NotSame(first, view.Plan);
        view.Apply(Full with { Favorite = false }, new Size(200, 90), Everything);
        Assert.Equal(200, view.Width);
        view.Clear();
        Assert.False(view.IsVisible);
        Assert.Same(MediaBadgePlan.Empty, view.Plan);
        view.Apply(default, new Size(160, 90), Everything);
        Assert.False(view.IsVisible);                                   // nothing to show: no visible control
    }

    [Fact]
    public void TheViewTakesTheNewLimitsOnItsNextApply()
    {
        MediaBadgeView view = new();
        MediaBadgeState state = new(Missing: true);
        view.Apply(state, new Size(40, 40), Everything);
        Assert.Equal(MediaBadgeMode.Dots, view.Plan.Mode);
        MediaBadgeLimits.Configure(24, 16, 2);
        view.Apply(state, new Size(40, 40), Everything);
        Assert.Equal(MediaBadgeMode.Full, view.Plan.Mode);
        MediaBadgeLimits.Configure(96, 64, 2);
        view.Apply(state, new Size(40, 40), Everything);
        Assert.Equal(MediaBadgeMode.Hidden, view.Plan.Mode);
        Assert.False(view.IsVisible);
    }

    [Fact]
    public void TheViewIsNeitherAHitTargetNorFocusableNorPartOfTheAccessibilityTree()
    {
        MediaBadgeView view = new();
        Assert.False(view.IsHitTestVisible);
        Assert.False(view.Focusable);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(view));
    }

    [Fact]
    public void TheViewHandlesAnUnusablePictureSize()
    {
        MediaBadgeView view = new();
        foreach (Size size in new[] { new Size(double.NaN, 50), new Size(double.PositiveInfinity, 50), new Size(0, 0), new Size(-3, 40) })
        {
            view.Apply(Full, size, Everything);
            Assert.False(view.IsVisible);
            Assert.Equal((0d, 0d), (view.Width, view.Height));
        }
    }

    // ---- budget: a grid of tens of thousands of virtualized tiles ----

    [Fact]
    public void ACreationBudgetHoldsForAThousandViewsAndFiftyThousandPlans()
    {
        // The budget is generous (a build may run beside this test); the real figures are a few milliseconds.
        Stopwatch watch = Stopwatch.StartNew();
        MediaBadgeView[] views = new MediaBadgeView[1000];
        for (int index = 0; index < views.Length; index++)
        {
            views[index] = new MediaBadgeView { Options = MediaBadgeOptions.MediaPage };
            views[index].Apply(index % 3 == 0 ? Full : new MediaBadgeState(Favorite: index % 2 == 0, TagCount: index % 7, Kind: (MediaBadgeKind)(index % 3)), new Size(160, 120));
        }
        long created = watch.ElapsedMilliseconds;
        Assert.InRange(created, 0, 1500);
        Assert.Equal(1000, views.Length);

        // Recycling: a tile that scrolls into view gets a new state; the same state again costs nothing.
        watch.Restart();
        for (int round = 0; round < 5; round++)
            for (int index = 0; index < views.Length; index++)
                views[index].Apply(index % 5 == 0 ? Full : new MediaBadgeState(Rating: index % 6, TagCount: index % 11), new Size(160, 120));
        Assert.InRange(watch.ElapsedMilliseconds, 0, 3000);

        // The pure plan: 50 000 tiles are planned within a moment.
        watch.Restart();
        MediaBadgeLimits limits = Defaults;
        int total = 0;
        for (int index = 0; index < 50_000; index++)
            total += MediaBadgeLayout.Plan(index % 4 == 0 ? Full : new MediaBadgeState(Favorite: true, TagCount: index % 9), new Size(76 + index % 80, 76), Everything, limits).Badges.Length;
        Assert.InRange(watch.ElapsedMilliseconds, 0, 4000);
        Assert.True(total > 50_000);
    }

    // ---- look: no colour of its own ----

    [Fact]
    public void TheComponentUsesTheColoursOfThePlayMarkAndNoColourOfItsOwn()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Desktop", "Controls", "MediaBadges.cs"));
        string code = Regex.Replace(Regex.Replace(source, @"//.*", ""), @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.DoesNotContain("Color.FromArgb", code);
        Assert.DoesNotContain("Color.FromRgb", code);
        Assert.DoesNotContain("Brushes.", code);
        Assert.DoesNotContain("Colors.", code);
        Assert.DoesNotContain("AccentSurface", code);
        Assert.DoesNotContain("WarningSurface", code);
        Assert.DoesNotContain("DynamicResource", code); // theme independent
        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b", code);
        // The colours: the dark disc and the white ring of the play mark, in both themes.
        Avalonia.Media.Color disc = VideoPlayMark.DiscColor, ring = VideoPlayMark.RingColor;
        Assert.True(disc.R < 40 && disc.G < 40 && disc.B < 40 && disc.A >= 180);
        Assert.True(ring.R == 255 && ring.G == 255 && ring.B == 255 && ring.A >= 150);
        Assert.Same(Avalonia.Media.Brushes.White, VideoPlayMark.GlyphBrush);
        Assert.Equal(disc, ((Avalonia.Media.ISolidColorBrush)VideoPlayMark.DiscBrush).Color);
    }

    // ---- settings: default, minimum, maximum ----

    [Fact]
    public void TheThreeLimitsHaveDefaultMinimumAndMaximumAndTheDocumentedValues()
    {
        Config defaults = new();
        Assert.Equal((48, 24, 96), (defaults.MediaBadgeCollapseBelow, Config.MediaBadgeCollapseBelowMinimum, Config.MediaBadgeCollapseBelowMaximum));
        Assert.Equal((32, 16, 64), (defaults.MediaBadgeHideBelow, Config.MediaBadgeHideBelowMinimum, Config.MediaBadgeHideBelowMaximum));
        Assert.Equal((2, 1, 3), (defaults.MediaBadgeMaxPerCorner, Config.MediaBadgeMaxPerCornerMinimum, Config.MediaBadgeMaxPerCornerMaximum));
        defaults.Validate();
        Assert.Equal(new MediaBadgeLimits(48, 32, 2), MediaBadgeLimits.Default);

        void Check(Action<Config, int> set, int[] valid, int[] invalid, string member)
        {
            foreach (int value in valid)
            {
                Config candidate = defaults.Clone();
                set(candidate, value);
                candidate.Validate();
                Config target = new();
                target.CopySettingsFieldsFrom(candidate);
                Assert.Equal(value, (int)typeof(Config).GetProperty(member)!.GetValue(target)!);
            }
            foreach (int value in invalid)
            {
                Config candidate = defaults.Clone();
                set(candidate, value);
                Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
                Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
            }
        }
        Check((c, v) => c.MediaBadgeCollapseBelow = v, [24, 96], [23, 97, int.MinValue, int.MaxValue], nameof(Config.MediaBadgeCollapseBelow));
        Check((c, v) => c.MediaBadgeHideBelow = v, [16, 64], [15, 65, -1], nameof(Config.MediaBadgeHideBelow));
        Check((c, v) => c.MediaBadgeMaxPerCorner = v, [1, 3], [0, 4, -7], nameof(Config.MediaBadgeMaxPerCorner));
    }

    [Fact]
    public void TheLimitsRoundTripThroughTheProfileTheCopiesTheExportAndTheLimitsInForce()
    {
        string directory = Directory.CreateTempSubdirectory("meowchan-badges-").FullName;
        try
        {
            Config config = Config.LoadConfig(directory);
            config.MediaBadgeCollapseBelow = 60; config.MediaBadgeHideBelow = 20; config.MediaBadgeMaxPerCorner = 3;
            config.SaveConfig(directory);
            Config loaded = Config.LoadConfig(directory);
            Assert.Equal((60, 20, 3), (loaded.MediaBadgeCollapseBelow, loaded.MediaBadgeHideBelow, loaded.MediaBadgeMaxPerCorner));
            Config clone = loaded.Clone();
            Assert.Equal((60, 20, 3), (clone.MediaBadgeCollapseBelow, clone.MediaBadgeHideBelow, clone.MediaBadgeMaxPerCorner));
            Config copy = new();
            copy.CopyFrom(loaded);
            Assert.Equal((60, 20, 3), (copy.MediaBadgeCollapseBelow, copy.MediaBadgeHideBelow, copy.MediaBadgeMaxPerCorner));
            Config settings = new();
            settings.CopySettingsFieldsFrom(loaded);
            Assert.Equal((60, 20, 3), (settings.MediaBadgeCollapseBelow, settings.MediaBadgeHideBelow, settings.MediaBadgeMaxPerCorner));

            JsonObject export = SettingsTransfer.Export(loaded, "1.1.0");
            Assert.Equal(60, export[nameof(Config.MediaBadgeCollapseBelow)]!.GetValue<int>());
            Config restored = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;
            restored.Validate();
            Assert.Equal((60, 20, 3), (restored.MediaBadgeCollapseBelow, restored.MediaBadgeHideBelow, restored.MediaBadgeMaxPerCorner));

            MediaBadgeLimits.Configure(loaded);
            Assert.Equal(new MediaBadgeLimits(60, 20, 3), MediaBadgeLimits.Current);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TheSettingsRowsExistInTheEditorAndTheSessionHandsTheLimitsOver()
    {
        string axaml = File.ReadAllText(Path.Combine(RepoRoot(), "Desktop", "Views", "Settings", "SettingsEditor.axaml"));
        foreach (string name in new[] { "MediaBadgeCollapseBelow", "MediaBadgeHideBelow", "MediaBadgeMaxPerCorner" })
        {
            Assert.Contains("x:Name=\"" + name + "Input\"", axaml);
            Assert.Contains("x:Name=\"" + name + "InputError\"", axaml);
        }
        Assert.Contains("Minimum=\"24\" Maximum=\"96\"", axaml);
        Assert.Contains("Minimum=\"16\" Maximum=\"64\"", axaml);
        Assert.Contains("Minimum=\"1\" Maximum=\"3\"", axaml);
        Assert.Contains("MediaBadgeLimits.Configure(Config)", File.ReadAllText(Path.Combine(RepoRoot(), "Desktop", "DesktopSessionHost.Favorites.cs")));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
}
