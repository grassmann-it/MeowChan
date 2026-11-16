using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Tom (16.11.2026): "Sometimes the image in the media sidebar briefly flashes up and then shows Loading." The cause: the loading timer of a load was stopped only
// after the neighbours it preloads, so a picture that was ready in time turned into "Loading…" 100 ms later (and stayed so; the header already had its pixel
// size). Around it: the same medium shown again reset the picture, a larger decode put the placeholder in between. The load state decides all of it without a
// window; the view tests drive the real sidebar on a dispatcher of their own.
[Collection("Localization")]
public sealed class ReaderMediaSidebarLoadStateTests : IDisposable
{
    private static readonly (double Width, double Height) Frame = (400, 900);
    // A 992×1585 PNG fitted into that frame: width-limited.
    private static readonly PixelSize Decoded = new(400, 639);
    private static readonly PixelSize Source = new(992, 1585);

    public ReaderMediaSidebarLoadStateTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ReaderMediaSidebarLoadState Showing(string key, PixelSize decoded, PixelSize? source)
    {
        ReaderMediaSidebarLoadState state = new();
        state.Begin(key, Frame);
        state.Showed(key, decoded, source);
        return state;
    }

    // ---- the state machine ------------------------------------------------------------------------------------------------------

    [Fact]
    public void APictureThatIsShownIsNeverTurnedIntoLoadingByItsOwnTimer()
    {
        ReaderMediaSidebarLoadState state = new();
        ReaderMediaLoadTicket ticket = state.Begin("a", Frame);
        Assert.True(state.Awaiting);
        state.Showed("a", Decoded, Source);
        // The load goes on (it warms the neighbours); the delay passes now: nothing changes.
        Assert.True(state.IsCurrent(ticket.Request));
        Assert.False(state.Awaiting);
        Assert.Equal(ReaderMediaLoadingLook.Keep, state.LoadingLook(ticket.Request));
    }

    [Fact]
    public void TheSameMediumShownAgainNeedsNoLoadAndNoPlaceholder()
    {
        ReaderMediaSidebarLoadState state = Showing("a", Decoded, Source);
        ReaderMediaLoadTicket again = state.Begin("a", Frame);
        Assert.True(again.SameMedium);
        Assert.True(again.UpToDate);
        Assert.False(state.Awaiting);
        Assert.Equal(ReaderMediaLoadingLook.Keep, state.LoadingLook(again.Request));
        Assert.True(state.HasPicture);
        Assert.Equal("a", state.ShownKey);
    }

    [Fact]
    public void TheNewerLoadWinsAndAnOlderOneIsStale()
    {
        ReaderMediaSidebarLoadState state = new();
        ReaderMediaLoadTicket first = state.Begin("a", Frame);
        ReaderMediaLoadTicket second = state.Begin("b", Frame);
        Assert.NotEqual(first.Request, second.Request);
        Assert.False(state.IsCurrent(first.Request));
        Assert.True(state.IsCurrent(second.Request));
        // The older load's timer does nothing; the newer one's shows the placeholder (nothing is shown yet).
        Assert.Equal(ReaderMediaLoadingLook.Keep, state.LoadingLook(first.Request));
        Assert.Equal(ReaderMediaLoadingLook.Placeholder, state.LoadingLook(second.Request));
        // A cancel (another post without media, the sidebar hidden) makes every load stale but keeps what is shown.
        state.Showed("b", Decoded, Source);
        state.Cancel();
        Assert.False(state.IsCurrent(second.Request));
        Assert.Equal("b", state.ShownKey);
        Assert.True(state.HasPicture);
    }

    [Fact]
    public void ALargerFrameDecodesTheSameMediumAgainWithoutAPlaceholderInBetween()
    {
        ReaderMediaSidebarLoadState state = Showing("a", Decoded, Source);
        ReaderMediaLoadTicket larger = state.Begin("a", (800, 1300));
        Assert.True(larger.SameMedium);
        Assert.False(larger.UpToDate);
        Assert.True(state.Awaiting);
        // The delay passes while the larger decode runs: the picture stays.
        Assert.Equal(ReaderMediaLoadingLook.Keep, state.LoadingLook(larger.Request));
        Assert.True(state.HasPicture);
        state.Showed("a", new PixelSize(800, 1278), Source);
        Assert.Equal(new PixelSize(800, 1278), state.ShownPixels);
        Assert.Equal(ReaderMediaLoadingLook.Keep, state.LoadingLook(larger.Request));
    }

    [Theory]
    [InlineData(400.4, 900.4)] // sub-dip layout jitter
    [InlineData(401, 901)]
    [InlineData(430, 960)] // the alignment's inset corrections: a few pixels
    [InlineData(300, 600)] // smaller: the shown picture is sharp enough
    public void LayoutJitterDecodesNothingAgain(double width, double height)
    {
        ReaderMediaSidebarLoadState state = Showing("a", Decoded, Source);
        ReaderMediaLoadTicket ticket = state.Begin("a", ReaderMediaSidebarRules.PhysicalTarget(new Size(width, height), 1));
        Assert.True(ticket.UpToDate);
        Assert.False(state.Awaiting);
    }

    [Fact]
    public void APictureAtItsSourceSizeNeverAsksForALargerDecode()
    {
        ReaderMediaSidebarLoadState state = Showing("a", Source, Source);
        Assert.True(state.Begin("a", (3000, 4000)).UpToDate);
    }

    [Fact]
    public void AnotherMediumThatIsNotReadyDimsThePreviousPictureInsteadOfFlashingEmpty()
    {
        ReaderMediaSidebarLoadState state = Showing("a", Decoded, Source);
        ReaderMediaLoadTicket next = state.Begin("b", Frame);
        Assert.False(next.SameMedium);
        Assert.Equal(ReaderMediaLoadingLook.DimPrevious, state.LoadingLook(next.Request));
        // The view dimmed it: only a trace now, so going back to "a" decodes (and shows at full opacity) again.
        state.Dimmed();
        Assert.True(state.HasPicture);
        Assert.Null(state.ShownKey);
        ReaderMediaLoadTicket back = state.Begin("a", Frame);
        Assert.False(back.SameMedium);
        Assert.False(back.UpToDate);
    }

    [Fact]
    public void WithNothingShownTheLoadingPlaceholderStandsIn()
    {
        ReaderMediaSidebarLoadState state = Showing("a", Decoded, Source);
        state.Cleared();
        Assert.False(state.HasPicture);
        ReaderMediaLoadTicket ticket = state.Begin("a", Frame);
        Assert.False(ticket.SameMedium);
        Assert.Equal(ReaderMediaLoadingLook.Placeholder, state.LoadingLook(ticket.Request));
        // A final text (not saved, cannot be shown) answers the load: the timer has nothing more to do.
        state.Cleared();
        Assert.Equal(ReaderMediaLoadingLook.Keep, state.LoadingLook(ticket.Request));
    }

    // ---- the view -----------------------------------------------------------------------------------------------------------------

    private static void OnOwnDispatcher(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        test();
    });

    private static WriteableBitmap Picture(int width, int height) => new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    [Fact]
    public void TheSidebarSwapsALargerDecodeOfTheSameMediumInPlace() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        List<Bitmap> released = new();
        using WriteableBitmap small = Picture(40, 64), large = Picture(80, 128);
        sidebar.ShowPicture(small, released.Add, playable: false, double.PositiveInfinity, double.PositiveInfinity, "a", new PixelSize(99, 158));
        Assert.Equal("a", sidebar.Load.ShownKey);
        Assert.Equal(new PixelSize(40, 64), sidebar.Load.ShownPixels);
        Assert.Equal(new PixelSize(99, 158), sidebar.Load.ShownSource);
        ReaderMediaLoadTicket ticket = sidebar.Load.Begin("a", (80, 128));
        Assert.True(ticket.SameMedium);
        Assert.False(ticket.UpToDate);
        // The delay passes: the look is Keep and the view changes nothing.
        sidebar.ShowLoading(sidebar.Load.LoadingLook(ticket.Request), "Loading…");
        Assert.Same(small, sidebar.Shown);
        Assert.Equal(string.Empty, sidebar.StatusText);
        Assert.False(sidebar.IsDimmed);
        sidebar.ShowPicture(large, released.Add, playable: false, double.PositiveInfinity, double.PositiveInfinity, "a", new PixelSize(99, 158));
        Assert.Same(large, sidebar.Shown);
        Assert.Same(large, sidebar.Picture.Source);
        Assert.Equal(new[] { (Bitmap)small }, released);
        Assert.Equal(new PixelSize(80, 128), sidebar.Load.ShownPixels);
        Assert.Equal(string.Empty, sidebar.StatusText);
    });

    [Fact]
    public void TheSidebarDimsThePreviousMediumWhileTheNextOneLoadsAndShowsItAtFullOpacityThen() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        using WriteableBitmap first = Picture(40, 64), second = Picture(40, 64);
        sidebar.ShowPicture(first, _ => { }, playable: false, double.PositiveInfinity, double.PositiveInfinity, "a", new PixelSize(40, 64));
        ReaderMediaLoadTicket ticket = sidebar.Load.Begin("b", (40, 64));
        sidebar.ShowLoading(sidebar.Load.LoadingLook(ticket.Request), "Loading…");
        // The previous picture stays (dimmed) under the text; no empty card in between.
        Assert.Same(first, sidebar.Shown);
        Assert.True(sidebar.IsDimmed);
        Assert.Equal("Loading…", sidebar.StatusText);
        sidebar.ShowPicture(second, _ => { }, playable: false, double.PositiveInfinity, double.PositiveInfinity, "b", new PixelSize(40, 64));
        Assert.False(sidebar.IsDimmed);
        Assert.Equal(string.Empty, sidebar.StatusText);
        Assert.Equal("b", sidebar.Load.ShownKey);
    });

    [Fact]
    public void TheSidebarShowsTheLoadingPlaceholderOnlyWhileNothingIsShown() => OnOwnDispatcher(() =>
    {
        ReaderMediaSidebar sidebar = new();
        ReaderMediaLoadTicket ticket = sidebar.Load.Begin("a", (40, 64));
        sidebar.ShowLoading(sidebar.Load.LoadingLook(ticket.Request), "Loading…");
        Assert.Null(sidebar.Shown);
        Assert.Equal("Loading…", sidebar.StatusText);
        // The empty state and a given-back picture leave nothing that a refresh could take for the shown medium.
        using WriteableBitmap picture = Picture(40, 64);
        sidebar.ShowPicture(picture, _ => { }, playable: false, double.PositiveInfinity, double.PositiveInfinity, "a", null);
        sidebar.ShowEmpty();
        Assert.False(sidebar.Load.HasPicture);
        Assert.False(sidebar.Load.Begin("a", (40, 64)).SameMedium);
    });

    // ---- the hosts ----------------------------------------------------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative)).Replace("\r\n", "\n");

    [Theory]
    [InlineData("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs", "sidebar")]
    [InlineData("Desktop/Views/Media/MediaPage.Panel.cs", "panel")]
    public void BothHostsAskTheLoadStateAndStopTheTimerBeforeThePictureShows(string file, string view)
    {
        string source = Read(file);
        Assert.Contains("shown.ShowLoading(shown.Load.LoadingLook(", source);
        Assert.Contains($"ReaderMediaLoadTicket ticket = {view}.Load.Begin(mediumKey, target);", source);
        Assert.Contains("if (ticket.UpToDate)", source);
        Assert.Contains("if (!ticket.SameMedium)", source);
        Assert.Contains($"placeholder.Stop();\n                {view}.ShowPicture(bitmap, GalleryImageCache.Shared.Release", source);
        // The load tokens are the load state's only.
        Assert.DoesNotContain("_sidebarRequest", source);
        Assert.DoesNotContain("_panelRequest", source);
    }

    [Fact]
    public void TheMoveBetweenTheHostsKeepsThePictureAndAHiddenSidebarGivesItBack()
    {
        string page = Read("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");
        Assert.Contains("if (_mediaSidebarShown) SetMediaSidebarShown(false, keepPicture: true);", page);
        Assert.Contains("if (!_mediaSidebarShown) ReleaseKeptMediaSidebarPicture();", page);
        Assert.Contains("if (keepPicture) _mediaSidebar?.ClearStrip();\n        else ReleaseKeptMediaSidebarPicture();", page);
    }
}
