using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>
/// MB1b (archive): the archive's saved-media tiles, the thread list's rows and the reader's attachments feed the shared media badges the right
/// state (favourite, kind, missing), the badge sentence (<see cref="MediaBadgeText.Describe(in MediaBadgeState, in MediaBadgeOptions)"/>) ends
/// up in their names, and a recycled row whose state did not change allocates nothing. Drawing and geometry are covered by the frame-panel
/// tests (<see cref="VideoPlayMarkTests"/>) and the media-slot, reader-preview and archive smokes.
/// </summary>
[Collection("Localization")]
public sealed class ArchiveMediaBadgesTests : IDisposable
{
    public ArchiveMediaBadgesTests() { Texts.Initialize(); Texts.SetLanguage("en"); ResetLimits(); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); ResetLimits(); }

    private static void ResetLimits()
    {
        MediaBadgeLimits defaults = MediaBadgeLimits.Default;
        MediaBadgeLimits.Configure(defaults.CollapseBelow, defaults.HideBelow, defaults.MaxPerCorner);
    }

    private static readonly MediaBadgeOptions Archive = MediaBadgeOptions.Archive;

    // The tiles and attachments build icon buttons and need the compiled assets and a render platform: the same owned-dispatcher harness the
    // Booru component tests use (both collections are non-parallel, so nothing else runs while it holds the dispatcher).
    private static void OnPlatform(Action test) => BooruComponentDispatcher.Run(test);

    private static ArchiveAssetRow AssetRow(string file, bool favorite = false, bool available = true, MediaManifestStatus status = MediaManifestStatus.Available)
    {
        string directory = Path.Combine(Path.GetTempPath(), "archive-badge-fixture");
        MediaAssetReference asset = new("asset-" + file, "media-" + file, "original", file, Path.Combine(directory, file), 3, new string('a', 64),
            "https://offline.invalid/" + file, OriginalFileName: file, IsCurrent: true);
        MediaAssetSnapshot snapshot = new(directory, status, 3, "fixture", [asset]);
        return new ArchiveAssetRow(snapshot, asset) { FavoriteAvailable = () => available, IsFavorite = () => favorite, ToggleFavoriteAsync = () => Task.CompletedTask };
    }

    private static ArchiveAttachmentRow AttachmentRow(string? file, bool favorite = false, bool available = true, bool compact = false) =>
        new(new CapturedAttachment(0, "m-" + file, OriginalFileName: file, ExpectedSize: 183_000))
        { Compact = compact, FavoriteAvailable = () => available, IsFavorite = () => favorite, ToggleFavoriteAsync = () => Task.CompletedTask };

    // ---- the state a row feeds ----

    [Theory]
    [InlineData("clip.webm", "Video"), InlineData("CLIP.MP4", "Video"), InlineData("a.mov", "Video"),
     InlineData("a.mkv", "Video"), InlineData("a.avi", "Video")]
    [InlineData("loop.gif", "Gif"), InlineData("LOOP.GIF", "Gif")]
    [InlineData("still.png", "Image"), InlineData("still.jpg", "Image"), InlineData("notes.pdf", "Image")]
    [InlineData("no-extension", "Image"), InlineData("", "Image"), InlineData(null, "Image")]
    [InlineData("clip.webm.png", "Image"), InlineData("archive.gif.zip", "Image")]
    public void TheKindFollowsTheSavedNameAlone(string? name, string expected) =>
        Assert.Equal(expected, ArchiveMediaBadges.KindOf(name).ToString());

    [Fact]
    public void TheKindAgreesWithTheGalleryClassificationForEveryKnownExtension()
    {
        // The video list is a copy of SavedMediaTypes.Classify (read without allocating); this keeps the two together.
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".webm", ".mp4", ".mov", ".mkv", ".avi", ".mp3", ".wav", ".ogg", ".flac", ".pdf", ".zip", ".bin", ".txt", "" })
        {
            string name = "file" + extension;
            Assert.Equal(SavedMediaTypes.Classify(name) == SavedMediaKind.Video, ArchiveMediaBadges.KindOf(name) == MediaBadgeKind.Video);
            Assert.Equal(extension == ".gif", ArchiveMediaBadges.KindOf(name) == MediaBadgeKind.Gif);
        }
    }

    [Fact]
    public void AnAssetRowFeedsFavouriteKindAndMissing()
    {
        Assert.Equal(new MediaBadgeState(Favorite: true, Kind: MediaBadgeKind.Video), ArchiveMediaBadges.ForAsset(AssetRow("clip.webm", favorite: true)));
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Gif), ArchiveMediaBadges.ForAsset(AssetRow("loop.gif")));
        Assert.Equal(default, ArchiveMediaBadges.ForAsset(AssetRow("still.png")));
        // A favourite is only shown while favourites are available (a closed page or an unsaved file has none).
        Assert.False(ArchiveMediaBadges.ForAsset(AssetRow("still.png", favorite: true, available: false)).Favorite);
        // A file that is gone, as the load found out or as the manifest says; and a length, once a source knows it.
        Assert.True(ArchiveMediaBadges.ForAsset(AssetRow("still.png"), missing: true).Missing);
        Assert.True(ArchiveMediaBadges.ForAsset(AssetRow("still.png", status: MediaManifestStatus.Missing)).Missing);
        Assert.False(ArchiveMediaBadges.ForAsset(AssetRow("still.png", status: MediaManifestStatus.Corrupt)).Missing);
        Assert.Equal(TimeSpan.FromSeconds(42), ArchiveMediaBadges.ForAsset(AssetRow("clip.webm"), duration: TimeSpan.FromSeconds(42)).Duration);
        Assert.Null(ArchiveMediaBadges.ForAsset(AssetRow("clip.webm")).Duration);
        // Rows with no delegates at all (a page that has none wired).
        Assert.False(ArchiveMediaBadges.ForAsset(new ArchiveAssetRow(AssetRow("a.png").Manifest, AssetRow("a.png").Asset)).Favorite);
    }

    [Fact]
    public void AnAttachmentRowFeedsKindButNeverTheFileFavouriteBecauseItsFileLineStarIsTheOne()
    {
        // Reader post (Tom, 16.11.2026): the round star over the thumbnail duplicated the star on the file line, so a favourite adds no badge.
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Video), ArchiveMediaBadges.ForAttachment(AttachmentRow("clip.webm", favorite: true)));
        Assert.False(ArchiveMediaBadges.ForAttachment(AttachmentRow("a.png", favorite: true, available: true)).Favorite);
        // The saved-media tiles keep theirs.
        Assert.True(ArchiveMediaBadges.ForAsset(AssetRow("a.png", favorite: true)).Favorite);
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Gif), ArchiveMediaBadges.ForAttachment(AttachmentRow("loop.gif")));
        Assert.Equal(default, ArchiveMediaBadges.ForAttachment(AttachmentRow(null)));
        // The thread list never wires the file favourite, and even a wired one is not drawn on a compact row (its star is hidden there too).
        Assert.False(ArchiveMediaBadges.ForAttachment(AttachmentRow("clip.webm", favorite: true, compact: true)).Favorite);
        Assert.Equal(MediaBadgeKind.Video, ArchiveMediaBadges.ForAttachment(AttachmentRow("clip.webm", compact: true)).Kind);
        Assert.True(ArchiveMediaBadges.ForAttachment(AttachmentRow("a.png"), missing: true).Missing);
    }

    [Fact]
    public void OnlyAFileThatIsGoneCountsAsMissing()
    {
        Assert.True(ArchiveMediaBadges.IsMissingFile(new FileNotFoundException()));
        Assert.True(ArchiveMediaBadges.IsMissingFile(new DirectoryNotFoundException()));
        Assert.True(ArchiveMediaBadges.IsMissingFile(new AggregateException(new InvalidOperationException("wrapped", new FileNotFoundException()))));
        Assert.False(ArchiveMediaBadges.IsMissingFile(new InvalidDataException("not an image")));
        Assert.False(ArchiveMediaBadges.IsMissingFile(new TimeoutException()));
        Assert.False(ArchiveMediaBadges.IsMissingFile(new OperationCanceledException()));
        Assert.False(ArchiveMediaBadges.IsMissingFile(null));
    }

    // ---- the sentence ends up in the names ----

    [Fact]
    public void TheTileFeedsItsStateAndTheSentenceIsPartOfItsNameHelpAndTooltip() => OnPlatform(() =>
    {
        bool favorite = true;
        ArchiveAssetRow row = AssetRow("clip.webm");
        row.IsFavorite = () => favorite;
        ArchiveMediaTileView tile = new() { DataContext = row };
        MediaBadgeState expected = new(Favorite: true, Kind: MediaBadgeKind.Video);
        Assert.Equal(expected, tile.Badges.State);
        Assert.Equal(MediaBadgeOptions.ArchiveTile, tile.Badges.Options);
        string sentence = MediaBadgeText.Describe(expected, Archive);
        Assert.Equal("Favorite · Video", sentence);
        Assert.Equal(sentence, tile.BadgeDescription);
        string name = AutomationProperties.GetName(tile)!;
        Assert.Contains(sentence, name);
        Assert.Contains("clip.webm", name);
        Assert.Equal(sentence, AutomationProperties.GetHelpText(tile));
        Assert.Contains(sentence, (string)ToolTip.GetTip(tile)!);
        // The former type chip is gone; the badge view is raw to automation and no hit target (the tile stays the target).
        Assert.False(tile.Badges.IsHitTestVisible);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(tile.Badges));

        // The page refreshes the favourite: state, sentence and name follow.
        favorite = false;
        tile.RefreshFavorite();
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Video), tile.Badges.State);
        Assert.Equal("Video", tile.BadgeDescription);
        Assert.DoesNotContain("Favorite", AutomationProperties.GetName(tile)!);
        Assert.Contains("Video", AutomationProperties.GetName(tile)!);

        // A recycled tile takes the next row's state; a still image with no badge has no sentence and no help text.
        tile.DataContext = AssetRow("still.png");
        Assert.Equal(default, tile.Badges.State);
        Assert.Equal(string.Empty, tile.BadgeDescription);
        Assert.Equal(string.Empty, AutomationProperties.GetHelpText(tile));
        tile.DataContext = AssetRow("loop.gif", favorite: true);
        Assert.Equal(new MediaBadgeState(Favorite: true, Kind: MediaBadgeKind.Gif), tile.Badges.State);
        Assert.Contains("Favorite · Animated image", AutomationProperties.GetName(tile)!);

        // A tile with no medium (the recycled container is unbound) clears its badges.
        tile.DataContext = null;
        Assert.Equal(default, tile.Badges.State);
        Assert.Equal(string.Empty, tile.BadgeDescription);
    });

    [Fact]
    public void TheTileNamesAnOtherFileItselfBecauseNoBadgeDoes() => OnPlatform(() =>
    {
        ArchiveMediaTileView tile = new() { DataContext = AssetRow("notes.pdf") };
        Assert.Equal(default, tile.Badges.State);
        Assert.Contains(Texts.T("media.tile.kind.other"), AutomationProperties.GetName(tile)!);
        tile.DataContext = AssetRow("clip.webm");
        // GIF and video are said by the badges' sentence, not twice.
        Assert.DoesNotContain(Texts.T("media.tile.kind.other"), AutomationProperties.GetName(tile)!);
    });

    [Fact]
    public void TheTileSentenceFollowsTheLanguage() => OnPlatform(() =>
    {
        ArchiveMediaTileView tile = new() { DataContext = AssetRow("clip.webm", favorite: true) };
        Assert.Contains("Favorite · Video", AutomationProperties.GetName(tile)!);
        Texts.SetLanguage("de");
        tile.DataContext = AssetRow("clip.webm", favorite: true);
        Assert.Contains("Favorit · Video", AutomationProperties.GetName(tile)!);
        tile.RefreshFavorite();
        Assert.Contains("Favorit · Video", AutomationProperties.GetName(tile)!);
    });

    [Fact]
    public void TheAttachmentFeedsItsStateAndTheSentenceIsPartOfThePictureButtonsNameAndHelp() => OnPlatform(() =>
    {
        bool favorite = true;
        ArchiveAttachmentRow row = AttachmentRow("clip.webm");
        row.IsFavorite = () => favorite;
        ArchiveAttachmentView view = new() { DataContext = row };
        // A favourite reader attachment draws no star over the picture and does not name one on the picture button either (the file line's star does).
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Video), view.Badges.State);
        Assert.False(view.Badges.Plan.Badges.Any(placed => placed.Badge.Id == MediaBadgeId.Favorite));
        Assert.Equal(MediaBadgeOptions.Archive, view.Badges.Options);
        string name = AutomationProperties.GetName(view.PreviewButton)!;
        Assert.StartsWith(row.Label, name, StringComparison.Ordinal);
        Assert.EndsWith("Video", name, StringComparison.Ordinal);
        Assert.DoesNotContain("Favorite", name);
        Assert.Equal("Video", AutomationProperties.GetHelpText(view.PreviewButton));
        Assert.Equal(row.Label, (string)ToolTip.GetTip(view.PreviewButton)!);
        Assert.False(view.Badges.IsHitTestVisible);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(view.Badges));

        favorite = false;
        view.RefreshFavorite();
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Video), view.Badges.State);
        Assert.EndsWith("Video", AutomationProperties.GetName(view.PreviewButton)!, StringComparison.Ordinal);
        Assert.DoesNotContain("Favorite", AutomationProperties.GetName(view.PreviewButton)!);

        // A plain picture: the name is exactly the label as before, and there is no help text.
        view.DataContext = AttachmentRow("still.png");
        Assert.Equal(default, view.Badges.State);
        Assert.Equal("still.png", AutomationProperties.GetName(view.PreviewButton));
        Assert.Null(AutomationProperties.GetHelpText(view.PreviewButton));

        view.DataContext = null;
        Assert.Equal(default, view.Badges.State);
    });

    [Fact]
    public void AThreadListRowSaysVideoOrGifButNeverFavourite() => OnPlatform(() =>
    {
        ArchiveAttachmentView view = new() { DataContext = AttachmentRow("clip.webm", favorite: true, compact: true) };
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Video), view.Badges.State);
        Assert.DoesNotContain("Favorite", AutomationProperties.GetName(view.PreviewButton)!);
        view.DataContext = AttachmentRow("loop.gif", compact: true);
        Assert.Equal(new MediaBadgeState(Kind: MediaBadgeKind.Gif), view.Badges.State);
        Assert.EndsWith("Animated image", AutomationProperties.GetName(view.PreviewButton)!, StringComparison.Ordinal);
    });

    [Fact]
    public void BothSurfacesUseTheArchiveOptionsAndTheBadgeViewIsInTheirFramePanel() => OnPlatform(() =>
    {
        ArchiveAttachmentView view = new();
        Assert.Equal(MediaBadgeOptions.Archive, view.Badges.Options);
        Assert.Same(view.Badges, view.GetLogicalDescendants().OfType<MediaFramePanel>().Single().Children.OfType<MediaBadgeView>().Single());
        ArchiveMediaTileView tile = new();
        Assert.Equal(MediaBadgeOptions.ArchiveTile, tile.Badges.Options);   // the grid tile alone is anchored to its slot; rows and reader posts stay on the picture
        Assert.Same(tile.Badges, tile.GetLogicalDescendants().OfType<LetterboxPanel>().Single().Children.OfType<MediaBadgeView>().Single());
        Assert.True(tile.GetLogicalDescendants().OfType<LetterboxPanel>().Single().MarksOnSlot);
        // No former chip, no separate play mark view.
        Assert.Empty(view.GetLogicalDescendants().OfType<VideoPlayMarkView>());
        Assert.DoesNotContain(tile.GetLogicalDescendants().OfType<Control>(), control => control.Name == "ArchiveMediaKind");
    });

    // ---- the slot anchor of the grid tile ----

    private static (Rect Badges, Rect Check, MediaBadgePlan Plan, Rect Play) Lay(double aspect, MediaBadgeOptions options, bool slotMarks)
    {
        MediaBadgeView badges = new() { Options = options };
        Border check = new() { Width = 16, Height = 16, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        LetterboxPanel panel = new() { Width = 170, Height = 125, Aspect = aspect, MarksOnSlot = slotMarks, Children = { new Image(), badges, check } };
        badges.Apply(new MediaBadgeState(Favorite: true, Kind: MediaBadgeKind.Video, Missing: true), new Size(1, 1));
        panel.Measure(new Size(170, 125)); panel.Arrange(new Rect(0, 0, 170, 125));
        return (badges.Bounds, check.Bounds, badges.Plan, badges.Plan.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.PlayMark).Bounds);
    }

    [Fact]
    public void TheGridTileAnchorsItsBadgesToTheSlotAndTheOtherArchiveSurfacesKeepThePicture()
    {
        Assert.Equal(MediaBadgeAnchor.Slot, MediaBadgeOptions.ArchiveTile.Anchor);
        Assert.Equal(MediaBadgeOptions.Archive with { Anchor = MediaBadgeAnchor.Slot }, MediaBadgeOptions.ArchiveTile);
        Assert.Equal(MediaBadgeAnchor.Picture, MediaBadgeOptions.Archive.Anchor);   // thread-list rows and reader posts
        PlacedBadge[] Chips(MediaBadgePlan plan) => plan.Badges.Where(placed => placed.Badge.Id != MediaBadgeId.PlayMark).ToArray();
        var portrait = Lay(2d / 3, MediaBadgeOptions.ArchiveTile, true);
        var landscape = Lay(16d / 9, MediaBadgeOptions.ArchiveTile, true);
        var wide = Lay(4, MediaBadgeOptions.ArchiveTile, true);
        foreach (var tile in new[] { portrait, landscape, wide })
        {
            Assert.Equal(new Rect(0, 0, 170, 125), tile.Badges);   // the view lies on the whole slot
            Assert.Equal(new Rect(154, 0, 16, 16), tile.Check);    // the check shares the slot's top right corner
            Assert.Equal(85, tile.Play.Center.X, 1);               // the play mark is centred on the picture
            Assert.Equal(62.5, tile.Play.Center.Y, 1);
        }
        Assert.Equal(Chips(portrait.Plan), Chips(landscape.Plan));
        Assert.Equal(Chips(portrait.Plan), Chips(wide.Plan));
        // The thread list and reader keep the old picture hugging: the view is as large as the letterboxed picture.
        var hugging = Lay(2d / 3, MediaBadgeOptions.Archive, false);
        Assert.True(hugging.Badges.Width < 100 && hugging.Badges.Height == 125);
        Assert.NotEqual(Chips(hugging.Plan), Chips(landscape.Plan));
    }

    [Fact]
    public void TheTileHandsItsSlotToTheBadgesAndMeasuresTheCheckOnIt() => OnPlatform(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ArchiveMediaTileView tile = new() { DataContext = AssetRow("still.png", status: MediaManifestStatus.Missing) };
        ArchiveAttachmentView attachment = new() { DataContext = AttachmentRow("still.png") };
        StackPanel content = new() { Children = { tile, attachment } };
        Window window = new() { Width = 650, Height = 700, Content = content };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(tile.Badges.Bounds.Width > 0);
            Assert.Equal(MediaBadgeAnchor.Slot, tile.Badges.Options.Anchor);
            Assert.Equal(MediaBadgeAnchor.Picture, attachment.Badges.Options.Anchor);
            Assert.Equal(tile.Badges.Bounds.Size, tile.Badges.GetVisualParent()!.Bounds.Size);
            PlacedBadge missing = Assert.Single(tile.Badges.Plan.Badges, badge => badge.Badge.Id == MediaBadgeId.Missing);
            Assert.Equal(MediaBadgeCorner.TopRight, missing.Badge.Corner);
        }
        finally { window.Close(); }
    });

    // ---- the selection check keeps clear of a badge in its corner ----

    [Fact]
    public void TheSelectionCheckMovesBesideAMissingFileBadgeAndSitsInTheCornerOtherwise()
    {
        Size picture = new(170, 125);
        MediaBadgePlan none = MediaBadgeLayout.Plan(new MediaBadgeState(Favorite: true, Kind: MediaBadgeKind.Video), picture, Archive);
        Assert.Equal(0, ArchiveMediaTileView.TopRightExtent(none, picture.Width));
        MediaBadgePlan missing = MediaBadgeLayout.Plan(new MediaBadgeState(Favorite: true, Missing: true), picture, Archive);
        PlacedBadge flag = missing.Badges.Single(placed => placed.Badge.Id == MediaBadgeId.Missing);
        Assert.Equal(MediaBadgeCorner.TopRight, flag.Badge.Corner);
        double extent = ArchiveMediaTileView.TopRightExtent(missing, picture.Width);
        Assert.Equal(picture.Width - flag.Bounds.Left + ArchiveMediaTileView.CornerGap, extent, 6);
        // The check (about 20 dip wide, 2 from the edge plus the extent) ends left of the flag.
        Assert.True(picture.Width - ArchiveMediaTileView.CornerInset - extent <= flag.Bounds.Left);
        Assert.Equal(0, ArchiveMediaTileView.TopRightExtent(MediaBadgePlan.Empty, 100));
    }

    // ---- the quiet toggle: the badge shows the favourite at rest, the toggle only while pointed at ----

    [Fact]
    public void TheArchiveTilesFavouriteToggleAppearsOnHoverOrFocusBecauseTheBadgeShowsTheState() => OnPlatform(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        TransientPopupPolicy.Install();
        ArchiveMediaTileView tile = new() { DataContext = AssetRow("still.png", favorite: true) };
        ListBoxItem container = new() { Content = tile };
        Window window = new() { Width = 500, Height = 400, Content = container };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            ToggleButton star = Assert.Single(tile.GetVisualDescendants().OfType<ArchiveFavoriteButton>());
            Assert.True(tile.Badges.State.Favorite);
            Assert.True(star.IsChecked);
            Assert.Equal(0, star.Opacity);

            Point at = star.TranslatePoint(new Point(star.Bounds.Width / 2, star.Bounds.Height / 2), window)!.Value;
            window.MouseMove(at);
            BooruComponentDispatcher.WaitUntil(() => star.Opacity == 1, "the hovered tile to reveal its star");

            window.MouseMove(new Point(-1, -1));
            BooruComponentDispatcher.WaitUntil(() => star.Opacity == 0, "the tile to hide its star after the pointer leaves");
            Assert.True(star.Focus(NavigationMethod.Tab));
            Assert.Equal(1, star.Opacity);
        }
        finally { window.Close(); }
    });

    // ---- budget: recycled rows ----

    [Fact]
    public void ARecycledRowWithAnUnchangedStateAllocatesNothing()
    {
        ArchiveAssetRow row = AssetRow("clip.webm", favorite: true);
        MediaBadgeView view = new() { Options = MediaBadgeOptions.Archive };
        Size picture = new(170, 96);
        MediaBadgeState state = ArchiveMediaBadges.ForAsset(row);
        view.Apply(state, picture);
        string sentence = MediaBadgeSentences.Get(state, Archive);
        Assert.Equal("Favorite · Video", sentence);
        // Warm up the paths once (JIT, the delegates), then measure.
        for (int index = 0; index < 100; index++) { view.Apply(ArchiveMediaBadges.ForAsset(row), picture); _ = MediaBadgeSentences.Get(ArchiveMediaBadges.ForAsset(row), Archive); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 50_000; index++)
        {
            MediaBadgeState same = ArchiveMediaBadges.ForAsset(row);
            view.Apply(same, picture);
            if (!ReferenceEquals(sentence, MediaBadgeSentences.Get(same, Archive))) throw new Xunit.Sdk.XunitException("The sentence was built again for an unchanged state.");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 4096, $"50 000 unchanged applications allocated {allocated} bytes.");

        // Clearing an already clear view costs nothing either.
        view.Clear();
        long beforeClear = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 50_000; index++) view.Clear();
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - beforeClear <= 4096);
    }

    [Fact]
    public void TenThousandBindsOfARecycledTileStayWithinABudget() => OnPlatform(() =>
    {
        // A virtualized list of 50 000 saved files binds a few dozen tiles again and again (never 50 000 at once); every bind feeds the badges and builds the name.
        ArchiveAssetRow[] rows = new ArchiveAssetRow[64];
        string[] names = ["a.png", "b.gif", "c.webm", "d.mp4", "e.jpg", "f.pdf"];
        for (int index = 0; index < rows.Length; index++) rows[index] = AssetRow(names[index % names.Length], favorite: index % 3 == 0);
        ArchiveMediaTileView tile = new();
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < 10_000; index++) tile.DataContext = rows[index % rows.Length];
        // Generous: a build may run beside this test; the real figure is well under a second.
        Assert.InRange(watch.ElapsedMilliseconds, 0, 20_000);
        Assert.Equal(ArchiveMediaBadges.ForAsset(rows[(10_000 - 1) % rows.Length]), tile.Badges.State);
    });

}
