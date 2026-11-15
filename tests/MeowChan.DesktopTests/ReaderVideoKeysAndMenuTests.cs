using Avalonia.Input;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// A click on a video must not cost the reader its keys or the medium its menu: Up, Down and the page keys keep moving the reader from any video control, the
// timeline and the picture keep Left and Right for seeking; the media sidebar opens the very menu of the post's medium (also in the large reader, whose frame
// is not under the page), and its video's timeline shows the gallery's hover preview from the gallery's cache.
public sealed class ReaderVideoKeysAndMenuTests
{
    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative));

    // ---- the keys ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Key.Up, false, true)]
    [InlineData(Key.Down, true, true)]
    [InlineData(Key.PageUp, true, false)]
    [InlineData(Key.PageDown, false, false)]
    [InlineData(Key.Home, true, true)]
    [InlineData(Key.End, false, true)]
    public void PostKeysGoToTheReaderFromEveryVideoControl(Key key, bool inTimeline, bool playerLive)
    {
        Assert.True(ReaderVideoKeys.IsPostNavigationKey(key));
        Assert.Equal(VideoControlKeyOwner.Reader, ReaderVideoKeys.OwnerOf(key, KeyModifiers.None, inTimeline, playerLive));
    }

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    public void LeftAndRightSeekOnTheTimelineAndTheVideoAndGoToTheReaderWithoutAPlayer(Key key)
    {
        Assert.False(ReaderVideoKeys.IsPostNavigationKey(key));
        Assert.Equal(VideoControlKeyOwner.Control, ReaderVideoKeys.OwnerOf(key, KeyModifiers.None, inTimeline: true, playerLive: true));   // the slider's own seek
        Assert.Equal(VideoControlKeyOwner.Seek, ReaderVideoKeys.OwnerOf(key, KeyModifiers.None, inTimeline: false, playerLive: true));    // the picture seeks by the step
        Assert.Equal(VideoControlKeyOwner.Reader, ReaderVideoKeys.OwnerOf(key, KeyModifiers.None, inTimeline: false, playerLive: false)); // nothing to seek: the reader's thread keys
        Assert.Equal(VideoControlKeyOwner.Control, ReaderVideoKeys.OwnerOf(key, KeyModifiers.Alt, inTimeline: false, playerLive: true));   // Alt+arrow is a command (history), not a seek
    }

    [Theory]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    [InlineData(Key.M)]
    [InlineData(Key.K)]
    [InlineData(Key.L)]
    [InlineData(Key.OemComma)]
    [InlineData(Key.OemPeriod)]
    public void TheVideoKeysAndTheControlsOwnKeysStayWhereTheyAre(Key key)
    {
        Assert.Equal(VideoControlKeyOwner.Control, ReaderVideoKeys.OwnerOf(key, KeyModifiers.None, inTimeline: false, playerLive: true));
        Assert.Equal(VideoControlKeyOwner.Control, ReaderVideoKeys.OwnerOf(key, KeyModifiers.None, inTimeline: true, playerLive: true));
    }

    [Fact]
    public void TheSlotRoutesBeforeTheControlAndTheSidebarAndTheWindowCatchWhatIsLeft()
    {
        string slot = Source("Desktop/ReaderInlineVideoSlot.cs");
        Assert.Contains("RoutingStrategies.Tunnel", slot);
        Assert.Contains("ReaderVideoKeys.OwnerOf(", slot);
        Assert.Contains("case VideoControlKeyOwner.Seek", slot);
        string archive = Source("Desktop/Views/Archive/ArchivePage.InlineVideo.cs");
        Assert.Contains("OnSidebarNavigationKey", Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs"));
        Assert.Contains("window.AddHandler(KeyDownEvent, OnViewerFocusLostKeyDown", archive);
        Assert.Contains("ReferenceEquals(e.Source, sender)", archive); // only when the window itself is the source (the focus is nowhere)
        Assert.Contains("PointerPlayFocus = FocusPostsAfterPointerPlay", archive);
        Assert.Contains("PointerPlayFocus = FocusGridAfterPointerPlay", Source("Desktop/Views/Media/MediaPage.Panel.cs"));
        Assert.Contains("_panelVideoController.NavigationKeys = TryNavigateGridFromPanel", Source("Desktop/Views/Media/MediaPage.Panel.cs"));
    }

    // ---- the media menu ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSidebarOpensTheSameMediaMenuAsThePostsMediumOnClickMenuKeyAndShiftF10()
    {
        string sidebar = Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");
        // The large reader's frame is hosted by the shell, not under the page: the sidebar carries the handlers itself, with the page's own builder.
        Assert.Contains("sidebar.AddHandler(ContextRequestedEvent, OnArchiveMediaContextRequested, RoutingStrategies.Tunnel)", sidebar);
        Assert.Contains("sidebar.AddHandler(PointerPressedEvent, OnSidebarPointerPressed, RoutingStrategies.Tunnel)", sidebar);
        Assert.Contains("sidebar.AddHandler(KeyDownEvent, OnArchiveMediaContextKey, RoutingStrategies.Tunnel)", sidebar);
        string commands = Source("Desktop/Views/Archive/ArchivePage.MediaCommands.cs");
        Assert.Contains("BuildArchiveMediaMenu(", commands);
        Assert.Contains("SidebarMediaContext(ancestors, keyboard)", commands);
        Assert.Contains("playback = _sidebarVideo", commands);
        // One builder: the sidebar adds no menu of its own.
        Assert.DoesNotContain("new ContextMenu", sidebar);
    }

    [Fact]
    public void TheMediaPanelKeepsTheMediaPagesOwnTileMenuForItsMedium()
    {
        string panel = Source("Desktop/Views/Media/MediaPage.Panel.cs");
        Assert.Contains("OpenSharedMediaMenu(_panelRow, panel.Frame, _panelVideo)", panel);
        Assert.Contains("panel.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel)", panel);
    }

    // ---- the timeline's hover preview ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheControllerCarriesTheGalleryScrubCacheToEveryPlayerAndTheShellSetsItForPagesWithoutAGallery()
    {
        ReaderInlineVideoController controller = new();
        Assert.Null(controller.ScrubCache); // no cache: no preview, playback unchanged
        string slot = Source("Desktop/ReaderInlineVideoSlot.cs");
        Assert.Contains("Player.ConfigureScrubPreview(scrubCache)", slot);
        Assert.Contains("scrubCache: controller.ScrubCache", slot);
        string shell = Source("Desktop/Shell/MainWindow.GalleryScrub.cs");
        Assert.Contains("_gallery == null && _archivePage == null && _mediaPage == null", shell);
        Assert.Contains("_archivePage.ScrubPreviewCache = host.Cache", shell);
        Assert.Contains("_mediaPage?.ConfigureScrubPreviewCache(host.Cache)", shell);
        // The budget stays the gallery's one setting (nothing new to limit): the same host configures the same cache.
        Assert.Contains("host.Configure(GalleryScrubPreviewHost.OptionsFor(_config))", shell);
    }
}
