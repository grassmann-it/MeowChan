using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// Auto-hide leaves the picture clean (Tom, 15.11.): the way back and the favourite badge are fully hidden (opacity 0, not hit-testable, not
// focusable), come back with motion, a favourite change, the pointer or the keyboard focus, and no command depends on them being shown.
[Collection("Media shortcut routing")]
public sealed class GalleryAutoHideCleanTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false, false, true)]  // controls shown: the pair is shown
    [InlineData(true, false, false, false)]  // auto-hidden and nothing holds it: gone
    [InlineData(true, true, false, true)]    // the pointer over it / the keyboard focus inside it
    [InlineData(true, false, true, true)]    // recent motion or a change
    public void TheCornerRuleHidesCompletelyOrShowsCompletely(bool autoHidden, bool held, bool revealed, bool shown)
    {
        GalleryCornerChrome state = GalleryCornerChrome.Resolve(autoHidden, held, revealed);
        Assert.Equal(shown ? 1 : 0, state.Opacity);
        Assert.Equal(shown, state.HitTestVisible);
        Assert.Equal(shown, state.Focusable);
    }

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private sealed class Harness
    {
        internal GalleryViewer Viewer { get; }
        internal Button Back => (Button)Get("_close")!;
        internal ToggleButton Badge => (ToggleButton)Get("_favoriteIndicator")!;
        internal ToggleButton NativeBadge => (ToggleButton)Get("_nativeFavoriteIndicator")!;
        internal readonly List<(GalleryAssetKey Key, bool Selected)> Sets = new();

        internal Harness(GalleryViewer viewer, bool autoHidden)
        {
            Viewer = viewer;
            Set("_entry", Entry());
            Set("_overlayHidden", autoHidden);
            Viewer.FavoriteSetRequested += (entry, selected) => Sets.Add((entry.Key, selected));
            Viewer.SetFavorite(false);
            Invoke("UpdateCornerChrome");
        }

        internal void WithActiveHost(Action<Window> test)
        {
            Window host = new() { Width = 800, Height = 600, Content = Viewer, Classes = { MotionPolicy.OffClass } };
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
        internal object? Invoke(string method, params object[] args) => typeof(GalleryViewer).GetMethod(method, Fields)!.Invoke(Viewer, args);
        internal void Age(string field) => Set(field, Stopwatch.GetTimestamp() - 60 * Stopwatch.Frequency);

        internal void AssertCornerHidden(bool back, bool badge)
        {
            Assert.Equal(back ? 1 : 0, Back.Opacity);
            Assert.Equal(back, Back.IsHitTestVisible);
            Assert.Equal(back, Back.Focusable);
            foreach (ToggleButton b in new[] { Badge, NativeBadge })
            {
                Assert.Equal(badge ? 1 : 0, b.Opacity);
                Assert.Equal(badge, b.IsHitTestVisible);
                Assert.Equal(badge, b.Focusable);
            }
        }

        internal KeyEventArgs Key(Key key) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = Viewer };
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

    [Fact]
    public void AutoHiddenControlsLeaveNothingOnThePicture() => WithGallery(h =>
    {
        h.AssertCornerHidden(back: false, badge: false);
    });

    [Fact]
    public void ShownControlsKeepTheWayBackAndTheBadge() => WithGallery(h =>
    {
        h.AssertCornerHidden(back: true, badge: true);
    }, autoHidden: false);

    [Fact]
    public void MouseMotionBringsThePairBackAndTheIdleDelayHidesItAgain() => WithGallery(h =>
    {
        h.Set("_lastCornerMotion", Stopwatch.GetTimestamp());
        h.Invoke("UpdateCornerChrome");
        h.AssertCornerHidden(back: true, badge: true);
        Assert.True((bool)typeof(GalleryViewer).GetProperty("CornerRevealTimed", Fields)!.GetValue(h.Viewer)!); // the timer runs to hide it again
        h.Age("_lastCornerMotion");
        h.Invoke("UpdateCornerChrome");
        h.AssertCornerHidden(back: false, badge: false);
    });

    [Fact]
    public void ADisabledDelayFollowsTheSettingNotAConstant() => WithGallery(h =>
    {
        h.Set("_controlsIdleHideMs", Config.ControlsIdleHideMsMinimum);
        h.Set("_lastCornerMotion", Stopwatch.GetTimestamp() - Stopwatch.Frequency); // 1 s ago: past the 500 ms minimum, inside the 2 s default
        h.Invoke("UpdateCornerChrome");
        h.AssertCornerHidden(back: false, badge: false);
        h.Set("_controlsIdleHideMs", Config.ControlsIdleHideMsDefault);
        h.Invoke("UpdateCornerChrome");
        h.AssertCornerHidden(back: true, badge: true);
    });

    [Fact]
    public void AFavouriteChangeShowsTheBadgeAloneThenHidesItAgain() => WithGallery(h =>
    {
        h.Viewer.SetFavorite(false); // the first known state of a medium never flashes
        h.AssertCornerHidden(back: false, badge: false);
        h.Viewer.SetFavorite(true);
        h.AssertCornerHidden(back: false, badge: true);
        Assert.Equal((true, false), GalleryViewer.FavoriteBadgeState(h.Badge));
        h.Age("_lastFavoriteFlash");
        h.Invoke("UpdateCornerChrome");
        h.AssertCornerHidden(back: false, badge: false);
    });

    [Fact]
    public void FMAndUWorkWithTheChromeHiddenAndFlashTheBadge()
    {
        foreach (Key key in new[] { Key.F, Key.M, Key.U })
        {
            WithGallery(h =>
            {
                h.Set("_favoriteSelected", key == Key.U);
                var e = h.Key(key);
                if (key == Key.F) h.Viewer.RouteFavoriteToggleKey(e);
                else h.Invoke("RequestFavorite", key == Key.M);
                Assert.Single(h.Sets);
                Assert.Equal(key != Key.U, h.Sets[0].Selected);
                h.AssertCornerHidden(back: false, badge: true);
            });
        }
    }

    [Fact]
    public void FPressedTwiceWithNoFocusActsTwiceWhenTheKeyUpArrivesAnywhere() => WithGallery(h =>
    {
        h.Viewer.RouteFavoriteToggleKey(h.Key(Key.F));
        Assert.True(h.Viewer.FavoriteGestureLatched);
        // The held key repeats: swallowed.
        var repeat = h.Key(Key.F);
        h.Viewer.RouteFavoriteToggleKey(repeat);
        Assert.True(repeat.Handled);
        Assert.Single(h.Sets);
        // The key-up now reaches the viewer through the window-level tunnel handler, whatever had the focus.
        h.Invoke("OnFavoriteKeyUp", null!, new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.F });
        Assert.False(h.Viewer.FavoriteGestureLatched);
        h.Viewer.RouteFavoriteToggleKey(h.Key(Key.F));
        Assert.Equal(2, h.Sets.Count);
    });

    [Fact]
    public void ALostActivationOrANewMediumClearsTheHeldKeyLatch() => WithGallery(h =>
    {
        h.WithActiveHost(host =>
        {
            h.Viewer.RouteFavoriteToggleKey(h.Key(Key.F));
            Assert.True(h.Viewer.FavoriteGestureLatched);

            host.PlatformImpl!.Deactivated?.Invoke();
            Assert.False(host.IsActive);
            Assert.False(h.Viewer.FavoriteGestureLatched);

            host.PlatformImpl.Activated?.Invoke();
            h.Viewer.RouteFavoriteToggleKey(h.Key(Key.F));
            Assert.True(h.Viewer.FavoriteGestureLatched);
            BooruComponentDispatcher.Wait(ShowNextMedium());
            Assert.False(h.Viewer.FavoriteGestureLatched);
        });

        async Task<bool> ShowNextMedium()
        {
            await h.Viewer.ShowAsync(Entry("next"), CancellationToken.None);
            return true;
        }
    });

    [Fact]
    public void TheSidePanelDoesNotBlockUnfocusedGalleryKeysButHelpAndTheQuestionDo() => WithGallery(h =>
    {
        h.Set("_contextOpen", true);
        Assert.True(h.Viewer.IsOverlayOpen);
        Assert.False(h.Viewer.BlocksUnfocusedHostKeys);
        h.Set("_contextOpen", false);
        h.Set("_helpOpen", true);
        Assert.True(h.Viewer.BlocksUnfocusedHostKeys);
        h.Set("_helpOpen", false);
        h.Set("_favoriteConfirmKey", new GalleryAssetKey(new("fixture", "g", "42"), "dir", "a"));
        Assert.True(h.Viewer.BlocksUnfocusedHostKeys);
    });

    [Theory]
    [InlineData(true, true, false, false, false, false, false, true)]   // active, usable gallery: any mouse motion reveals
    [InlineData(false, true, false, false, false, false, false, false)] // another window has the focus
    [InlineData(true, false, false, false, false, false, false, false)] // covered/unavailable
    [InlineData(true, true, true, false, false, false, false, false)]   // dialog or popup over it
    [InlineData(true, true, false, true, false, false, false, false)]   // a drag or pointer capture
    [InlineData(true, true, false, false, true, false, false, false)]   // selected text is being read
    [InlineData(true, true, false, false, false, true, false, false)]   // keyboard focus in a panel
    [InlineData(true, true, false, false, false, false, true, false)]   // shortcut help open
    public void MouseMotionRevealsTheControlsUnlessSomethingElseOwnsTheGallery(bool active, bool available, bool dialog,
        bool captured, bool selection, bool keyboard, bool help, bool expected) =>
        Assert.Equal(expected, GalleryEdgeHover.MotionMayReveal(active, available, dialog, captured, selection, keyboard, help));

    [Theory]
    [InlineData(true, true, "Reveal")]      // may reveal, controls hidden: they come back
    [InlineData(true, false, "RenewClock")] // may reveal, controls shown: the idle clock restarts
    [InlineData(false, true, "None")]       // not the active gallery: nothing shows
    [InlineData(false, false, "None")]      // ... and shown controls get no new clock (Opus review 15.11., finding 6)
    public void MotionRenewsTheClockOrRevealsOnlyWhenItMayReveal(bool mayReveal, bool hidden, string expected) =>
        Assert.Equal(expected, GalleryEdgeHover.MotionEffect(mayReveal, hidden).ToString());

    [Fact]
    public void TheGuardsRunBeforeAnythingTouchesTheIdleClock()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Desktop", "GalleryViewer.Overlay.cs"));
        int start = source.IndexOf("private void RevealControlsOnMotion()", StringComparison.Ordinal);
        string body = source.Substring(start, source.IndexOf("private void RevealHoveredPanels()", start, StringComparison.Ordinal) - start);
        int guards = body.IndexOf("GalleryEdgeHover.MotionMayReveal(", StringComparison.Ordinal);
        Assert.True(guards > 0);
        Assert.True(body.IndexOf("_lastOverlayActivity", StringComparison.Ordinal) > guards);
        Assert.True(body.IndexOf("RevealOverlayControls()", StringComparison.Ordinal) > guards);
    }

    [Fact]
    public void ActualMouseMotionLeavesKeyboardPanelFocusGuardBeforeReveal()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Desktop", "GalleryViewer.Overlay.cs"));
        int start = source.IndexOf("private void OnOverlayPointerMoved", StringComparison.Ordinal);
        int end = source.IndexOf("private void OnIndicatorExpiry", start, StringComparison.Ordinal);
        string body = source.Substring(start, end - start);
        int motion = body.IndexOf("e.Pointer.Type == PointerType.Mouse", StringComparison.Ordinal);
        Assert.True(motion >= 0);
        int modalityReset = body.IndexOf("_keyboardInteraction = false;", motion, StringComparison.Ordinal);
        int reveal = body.IndexOf("RevealControlsOnMotion();", motion, StringComparison.Ordinal);
        Assert.True(modalityReset >= 0);
        Assert.True(reveal > modalityReset);
    }

    [Fact]
    public void AButtonOnlyCommandBehindTheHiddenFooterIsFoundSoTheControlsComeFirst()
    {
        var footer = new StackPanel { IsVisible = false };
        var context = new Grid();
        var button = new Button();
        CommandBindings.Apply(button, "gallery.layout-menu");
        footer.Children.Add(button);
        context.Children.Add(footer);
        // Any bound key of that command: its gesture, or none when unbound (then nothing can match and nothing is revealed).
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F24 };
        Assert.False(CommandBindings.HasActionBehind(context, footer, e));
        footer.IsVisible = true;
        Assert.False(CommandBindings.HasActionBehind(context, footer, e)); // shown: the normal visible-action route runs
    }

    [Fact]
    public void TheEdgeHintsTakeNoClicksWhileTheyAreNotDrawn() => WithGallery(h => h.WithActiveHost(_ =>
    {
        Button controls = (Button)h.Get("_controlsEdgeIndicator")!;
        Button post = (Button)h.Get("_postEdgeIndicator")!;
        h.Viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayPostContext: true));
        h.Set("_overlayHidden", true);
        h.Set("_lastIndicatorMotion", null);
        h.Invoke("UpdateEdgeIndicators");
        Dispatcher.UIThread.RunJobs();
        Assert.True(controls.IsVisible && post.IsVisible);
        Assert.False(controls.IsHitTestVisible);
        Assert.False(post.IsHitTestVisible);
        Assert.True(controls.Focusable && post.Focusable);

        // Exercise the artwork's movement window without racing its wall-clock expiry.
        h.Set("_lastIndicatorMotion", Stopwatch.GetTimestamp());
        h.Invoke("UpdateEdgeIndicatorArtwork");
        Assert.True(controls.IsHitTestVisible);
        Assert.True(post.IsHitTestVisible);

        h.Invoke("OnIndicatorExpiry", h.Viewer, EventArgs.Empty);
        Assert.False(controls.IsHitTestVisible);
        Assert.False(post.IsHitTestVisible);
        Assert.True(controls.Focus(NavigationMethod.Tab));
        Assert.True(controls.IsKeyboardFocusWithin);
        Assert.True(controls.IsHitTestVisible);
        Assert.False(post.IsHitTestVisible);
    }));
}
