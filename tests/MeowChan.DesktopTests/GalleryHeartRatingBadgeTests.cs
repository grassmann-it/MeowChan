using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Motion;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// FIX-112-FAV-HEART (Tom, 25.11.2026): the favourite is a heart everywhere, the star belongs to the rating. In the gallery the heart beside
// "Back to …" gets the shown medium's rating as five stars at its right; M/U and the rating keys flash both, and a click on star n rates
// exactly the shown medium with n (a click on the set star removes it, as on every star row) without taking the focus from the picture.
[Collection("Media shortcut routing")]
public sealed class GalleryHeartRatingBadgeTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    // ---- the shared icon sources -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheSharedFavouriteMarkIsAHeartAndTheRatingStarStaysAStar() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        foreach (bool marked in new[] { false, true })
        {
            Border heart = FavoriteStar.Create(marked);
            Assert.Equal(marked, FavoriteStar.MarkOf(heart));
            Assert.Null(RatingStar.FilledOf(heart));
            Assert.Equal(marked ? WorkspaceIcons.HeartFilled : WorkspaceIcons.Heart, WorkspaceIcons.KeyOf(heart.Child));

            Border star = RatingStar.Create(marked);
            Assert.Equal(marked, RatingStar.FilledOf(star));
            Assert.Null(FavoriteStar.MarkOf(star));
            Assert.Equal(marked ? WorkspaceIcons.StarFilled : WorkspaceIcons.Star, WorkspaceIcons.KeyOf(star.Child));
        }
        // A filled heart is filled with the foreground it inherits, like the filled star.
        Border filled = FavoriteStar.Create(true, tinted: false);
        var foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White);
        _ = new ContentControl { Foreground = foreground, Content = filled };
        var path = Assert.IsType<Avalonia.Controls.Shapes.Path>(filled.Child);
        Assert.Same(foreground, path.Fill);
    });

    [Fact]
    public void EveryCatalogFavouriteIconAndTheTileBadgeAreHearts()
    {
        foreach (string id in new[] { "favorites.toggle", "favorites.add", "favorites.filter", "favorites.open", "thread.favorite-toggle" })
            Assert.Equal("heart", CommandCatalog.Get(id).Icon);
        Assert.Equal("heart", CommandCatalog.IconForState("favorites.toggle", "unmarked"));
        Assert.Equal("heart-filled", CommandCatalog.IconForState("favorites.toggle", "marked"));
        Assert.Equal("heart-filled", MediaBadgeIcons.Favorite);
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "Assets/icons/feather/masks/heart-filled.png")));
    }

    // ---- the gallery's top row -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheHeartBadgeShowsAHeartAndTheStarsBesideItShowTheRating() => WithGallery(h =>
    {
        h.Viewer.SetRating(2, null);
        Border badge = h.Viewer.RatingIndicator!;
        Assert.Equal("GalleryRatingIndicator", badge.Name);
        Assert.True(badge.IsVisible);
        Assert.False(badge.Focusable);
        Assert.Equal(WorkspaceIcons.Heart, WorkspaceIcons.KeyOf(((StackPanel)h.Badge.Content!).Children[0]));
        Assert.Equal(new bool?[] { true, true, false, false, false }, h.Marks());
        // The stars stand right of the heart, on its row.
        Assert.True(badge.Margin.Left > h.Badge.Margin.Left);
        Assert.Equal(h.Badge.Margin.Top, badge.Margin.Top);
        // Tom's rule: the stars never take the focus.
        Assert.All(h.Stars, star => { Assert.False(star.Focusable); Assert.False(star.IsTabStop); });
        Assert.Equal("GalleryRatingIndicatorRowRating3", Avalonia.Automation.AutomationProperties.GetAutomationId(h.Stars[2]));

        h.Viewer.SetFavorite(true);
        Assert.Equal((true, false), GalleryViewer.FavoriteBadgeState(h.Badge));
        Assert.Equal(WorkspaceIcons.HeartFilled, WorkspaceIcons.KeyOf(((StackPanel)h.Badge.Content!).Children[0]));
        h.Viewer.SetRating(null, null);
        Assert.Equal(new bool?[] { false, false, false, false, false }, h.Marks());
    }, autoHidden: false);

    [Fact]
    public void AMediumWithoutAKnownRatingShowsTheHeartAlone() => WithGallery(h =>
    {
        Assert.False(h.Viewer.RatingIndicator!.IsVisible); // the rating is not known yet
        h.Viewer.SetRating(3, null, available: false);
        Assert.False(h.Viewer.RatingIndicator.IsVisible);
        Assert.True(h.Badge.IsVisible);
    }, autoHidden: false);

    [Fact]
    public void ARatingKeyFlashesTheHeartAndTheStarsAndTheStarsFollowTheSavedRating() => WithGallery(h =>
    {
        h.Viewer.SetRating(null, null);
        h.AssertShown(false);
        h.Invoke("RequestRating", (int?)3);
        Assert.Equal([(h.Key, (int?)3)], h.Ratings);
        h.AssertShown(true);
        h.Viewer.SetRating(3, null);
        Assert.Equal(new bool?[] { true, true, true, false, false }, h.Marks());
        // The rating key also brought the controls back (RevealForRatingFeedback); the idle delay hides them again, and the flash runs out.
        h.Set("_overlayHidden", true);
        h.Age("_lastFavoriteFlash");
        h.Invoke("UpdateCornerChrome");
        h.AssertShown(false);
        // A change from elsewhere (the Post row, the media page) flashes too.
        h.Viewer.SetRating(5, null);
        h.AssertShown(true);
        Assert.Equal(new bool?[] { true, true, true, true, true }, h.Marks());
    });

    [Fact]
    public void MFlashesTheStarsWithTheHeart() => WithGallery(h =>
    {
        h.Viewer.SetRating(1, null);
        h.AssertShown(false);
        h.Invoke("RequestFavorite", true);
        Assert.Single(h.Favorites);
        h.AssertShown(true);
    });

    [Fact]
    public void AClickOnStarNRatesExactlyTheShownMediumAndLeavesTheFocusOnThePicture() => WithGallery(h => h.WithActiveHost(host =>
    {
        h.Viewer.SetRating(1, null);
        Dispatcher.UIThread.RunJobs();
        host.UpdateLayout();
        Assert.True(h.Viewer.RatingIndicator!.IsEffectivelyVisible);
        h.Viewer.FocusGallerySurface();
        Assert.True(h.Viewer.IsFocused);

        h.Click(host, h.Stars[3]);
        Assert.Equal([(h.Key, (int?)4)], h.Ratings);
        Assert.True(h.Viewer.IsFocused, "focus moved to " + host.FocusManager?.GetFocusedElement());

        // The badge follows the saved state; a click on the set star removes the rating, as on every star row.
        h.Viewer.SetRating(4, null);
        h.Click(host, h.Stars[3]);
        Assert.Equal((h.Key, (int?)null), h.Ratings[^1]);
        Assert.True(h.Viewer.IsFocused);

        // A pending write ignores the click (like the Post row's stars).
        h.Viewer.SetRating(4, null, pending: true);
        h.Click(host, h.Stars[0]);
        Assert.Equal(2, h.Ratings.Count);
    }), autoHidden: false);

    // ---- harness ---------------------------------------------------------------------------------------------------------------

    private sealed class Harness
    {
        internal GalleryViewer Viewer { get; }
        internal GalleryAssetKey Key { get; }
        internal ToggleButton Badge => (ToggleButton)Get("_favoriteIndicator")!;
        internal IReadOnlyList<ToggleButton> Stars => Viewer.RatingIndicatorRow!.StarButtons;
        internal readonly List<(GalleryAssetKey Key, int? Value)> Ratings = new();
        internal readonly List<bool> Favorites = new();

        internal Harness(GalleryViewer viewer, bool autoHidden)
        {
            Viewer = viewer;
            GalleryEntry entry = Entry();
            Key = entry.Key;
            Set("_entry", entry);
            Set("_overlayHidden", autoHidden);
            Viewer.RatingSetRequested += (shown, value) => Ratings.Add((shown.Key, value));
            Viewer.FavoriteSetRequested += (_, selected) => Favorites.Add(selected);
            Viewer.SetFavorite(false);
            Invoke("UpdateCornerChrome");
        }

        internal bool?[] Marks() => Stars.Select(star => RatingStar.FilledOf(star.Content)).ToArray();

        internal void AssertShown(bool shown)
        {
            Assert.Equal(shown ? 1 : 0, Badge.Opacity);
            Assert.Equal(shown ? 1 : 0, Viewer.RatingIndicator!.Opacity);
            Assert.Equal(shown, Viewer.RatingIndicator.IsHitTestVisible);
            Assert.False(Viewer.RatingIndicator.Focusable);
        }

        internal void Click(Window host, Control target)
        {
            Point point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), host)!.Value;
            host.MouseMove(point);
            host.MouseDown(point, MouseButton.Left);
            host.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        internal void WithActiveHost(Action<Window> test)
        {
            Window host = new() { Width = 1400, Height = 900, Content = Viewer, Classes = { MotionPolicy.OffClass } };
            try
            {
                host.Show();
                host.PlatformImpl!.Activated?.Invoke();
                Dispatcher.UIThread.RunJobs();
                Assert.True(host.IsActive);
                test(host);
            }
            finally { host.Close(); }
        }

        internal void Set(string field, object? value) => typeof(GalleryViewer).GetField(field, Fields)!.SetValue(Viewer, value);
        internal object? Get(string field) => typeof(GalleryViewer).GetField(field, Fields)!.GetValue(Viewer);
        internal object? Invoke(string method, params object?[] args) => typeof(GalleryViewer).GetMethod(method, Fields)!.Invoke(Viewer, args);
        internal void Age(string field) => Set(field, Stopwatch.GetTimestamp() - 60 * Stopwatch.Frequency);
    }

    private static GalleryEntry Entry(string assetId = "a")
    {
        MediaAssetReference asset = new(assetId, "m", "original", "a.png", "/x/a.png", 1, new string('a', 64), "", "a.png", true);
        MediaAssetSnapshot manifest = new("dir", MediaManifestStatus.Available, 1, "fixture", [asset]);
        return new(new(new("fixture", "g", "42"), "dir", assetId), manifest, asset, null, null, false, false);
    }

    private static void WithGallery(Action<Harness> test, bool autoHidden = true) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruComponentDispatcher.Wait(Exercise());

        async Task<bool> Exercise()
        {
            await using GalleryViewer viewer = new();
            test(new Harness(viewer, autoHidden));
            return true;
        }
    });

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
