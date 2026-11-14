using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// V3 (gallery filmstrip): the plan arithmetic for lists up to thousands of entries, the thumbnail window that keeps only the visible
/// tiles and the preload, the three settings (default, minimum, maximum), the policy rows and the texts. The strip itself needs a
/// platform (bitmaps, pointer, focus): its visual proof is the gallery-edge / gallery-toolbar / gallery-focus smokes.
/// </summary>
[Collection("Localization")]
public sealed class GalleryFilmstripTests : IDisposable
{
    public GalleryFilmstripTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private const double Tile = 72;
    private const double Pitch = Tile + GalleryFilmstripPlan.TileGap;

    // ---- plan ----

    [Fact]
    public void TheRowIsAMultiplicationNeverAMeasure()
    {
        Assert.Equal(Pitch, GalleryFilmstripPlan.Pitch(Tile));
        Assert.Equal(0, GalleryFilmstripPlan.Extent(0, Tile));
        Assert.Equal(Tile, GalleryFilmstripPlan.Extent(1, Tile));
        Assert.Equal(5000 * Pitch - GalleryFilmstripPlan.TileGap, GalleryFilmstripPlan.Extent(5000, Tile));
        Assert.Equal(0, GalleryFilmstripPlan.MaximumOffset(3, Tile, 1000));
        Assert.Equal(GalleryFilmstripPlan.Extent(100, Tile) - 500, GalleryFilmstripPlan.MaximumOffset(100, Tile, 500));
        Assert.Equal(Tile + 2 * GalleryFilmstripPlan.BandPadding, GalleryFilmstripPlan.BandHeight(Tile));
    }

    [Fact]
    public void VisibleTilesAreThoseThatTouchTheViewport()
    {
        var range = GalleryFilmstripPlan.Visible(0, 300, Tile, 100);
        Assert.Equal(new GalleryFilmstripPlan.TileRange(0, 3), range);
        // Scrolled so that tile 2 is cut at its left edge: it still counts; once only its gap shows, it does not.
        Assert.Equal(2, GalleryFilmstripPlan.Visible(2 * Pitch + 10, 300, Tile, 100).First);
        Assert.Equal(3, GalleryFilmstripPlan.Visible(2 * Pitch + Tile, 300, Tile, 100).First);
        Assert.True(GalleryFilmstripPlan.Visible(0, 0, Tile, 100).IsEmpty);
        Assert.True(GalleryFilmstripPlan.Visible(0, 300, Tile, 0).IsEmpty);
        Assert.True(GalleryFilmstripPlan.Visible(double.NaN, 300, Tile, 10).IsEmpty);
        // Past the end the range is cut to the list.
        Assert.Equal(9, GalleryFilmstripPlan.Visible(0, 5000, Tile, 10).Last);
    }

    [Fact]
    public void AFiveThousandEntryRowDecodesOnlyTheWindow()
    {
        const int count = 5000;
        double offset = GalleryFilmstripPlan.CentreOffset(2500, count, Tile, 1000);
        var visible = GalleryFilmstripPlan.Visible(offset, 1000, Tile, count);
        var decode = GalleryFilmstripPlan.Decode(visible, Config.GalleryFilmstripPreloadDefault, count);
        Assert.True(visible.Contains(2500));
        Assert.True(visible.Count <= 15);
        Assert.Equal(visible.Count + 2 * Config.GalleryFilmstripPreloadDefault, decode.Count);
        Assert.True(decode.Count < 60, "a long list must not hold more than the window");
        Assert.Equal(count - 1, GalleryFilmstripPlan.Decode(new(count - 2, count - 1), 60, count).Last);
        Assert.Equal(0, GalleryFilmstripPlan.Decode(new(0, 1), 60, count).First);
        Assert.True(GalleryFilmstripPlan.Decode(GalleryFilmstripPlan.TileRange.Empty, 12, count).IsEmpty);
    }

    [Fact]
    public void PreloadAndHeightAreClampedToTheirLimits()
    {
        Assert.Equal(Config.GalleryFilmstripPreloadMinimum, GalleryFilmstripPlan.ClampPreload(-5));
        Assert.Equal(Config.GalleryFilmstripPreloadMaximum, GalleryFilmstripPlan.ClampPreload(10_000));
        Assert.Equal(Config.GalleryFilmstripHeightMinimum, GalleryFilmstripPlan.ClampHeight(0));
        Assert.Equal(Config.GalleryFilmstripHeightMaximum, GalleryFilmstripPlan.ClampHeight(10_000));
        Assert.Equal(72, GalleryFilmstripPlan.ClampHeight(72));
        // The preload limit bounds the decoded window of any visible range.
        var decode = GalleryFilmstripPlan.Decode(new(100, 108), 10_000, 5000);
        Assert.Equal(9 + 2 * Config.GalleryFilmstripPreloadMaximum, decode.Count);
    }

    [Fact]
    public void OutwardOrderStartsAtTheCentreAndAlternatesNearerSideFirst()
    {
        Assert.Equal(new[] { 5, 4, 6, 3, 7, 2, 8 }, GalleryFilmstripPlan.OutwardOrder(new(2, 8), 5));
        // A centre outside the range starts at the nearest end; an end-centred range runs one way only.
        Assert.Equal(new[] { 2, 3, 4 }, GalleryFilmstripPlan.OutwardOrder(new(2, 4), -9));
        Assert.Equal(new[] { 4, 3, 2 }, GalleryFilmstripPlan.OutwardOrder(new(2, 4), 40));
        Assert.Empty(GalleryFilmstripPlan.OutwardOrder(GalleryFilmstripPlan.TileRange.Empty, 0));
        var all = GalleryFilmstripPlan.OutwardOrder(new(10, 70), 33).ToList();
        Assert.Equal(61, all.Count);
        Assert.Equal(61, all.Distinct().Count());
    }

    [Fact]
    public void CentreRevealAndPageOffsetsStayInsideTheRow()
    {
        const int count = 200;
        const double viewport = 600;
        double centre = GalleryFilmstripPlan.CentreOffset(100, count, Tile, viewport);
        Assert.Equal(100 * Pitch + Tile / 2 - viewport / 2, centre, 6);
        Assert.Equal(0, GalleryFilmstripPlan.CentreOffset(0, count, Tile, viewport));
        Assert.Equal(GalleryFilmstripPlan.MaximumOffset(count, Tile, viewport), GalleryFilmstripPlan.CentreOffset(count - 1, count, Tile, viewport));
        Assert.Equal(0, GalleryFilmstripPlan.CentreOffset(5, 0, Tile, viewport));
        // Reveal changes nothing for a tile that is fully in view and moves the least otherwise.
        Assert.Equal(centre, GalleryFilmstripPlan.RevealOffset(centre, 100, count, Tile, viewport));
        Assert.Equal(150 * Pitch, GalleryFilmstripPlan.RevealOffset(0, 150, count, Tile, viewport) + viewport - Tile, 6);
        Assert.Equal(20 * Pitch, GalleryFilmstripPlan.RevealOffset(10_000, 20, count, Tile, viewport));
        // A page is a viewport less one tile, so one tile stays as a landmark; it stops at both ends.
        Assert.Equal(viewport - Pitch, GalleryFilmstripPlan.PageOffset(0, 1, count, Tile, viewport), 6);
        Assert.Equal(0, GalleryFilmstripPlan.PageOffset(100, -1, count, Tile, viewport));
        Assert.Equal(GalleryFilmstripPlan.MaximumOffset(count, Tile, viewport), GalleryFilmstripPlan.PageOffset(1e9, 1, count, Tile, viewport));
        Assert.Equal(0, GalleryFilmstripPlan.ClampOffset(double.NaN, count, Tile, viewport));
    }

    [Fact]
    public void TileAtFindsTheTileUnderThePointerAndNothingOverGapsOrPastTheEnds()
    {
        Assert.Equal(0, GalleryFilmstripPlan.TileAt(10, 0, Tile, 5));
        Assert.Equal(1, GalleryFilmstripPlan.TileAt(Pitch + 1, 0, Tile, 5));
        Assert.Equal(-1, GalleryFilmstripPlan.TileAt(Tile + 1, 0, Tile, 5)); // the gap
        Assert.Equal(-1, GalleryFilmstripPlan.TileAt(-1, 0, Tile, 5));
        Assert.Equal(-1, GalleryFilmstripPlan.TileAt(5 * Pitch + 1, 0, Tile, 5));
        Assert.Equal(3, GalleryFilmstripPlan.TileAt(5, 3 * Pitch, Tile, 5)); // scrolled
        Assert.Equal(-1, GalleryFilmstripPlan.TileAt(5, 0, Tile, 0));
    }

    // ---- thumbnail window ----

    private sealed class Lease(int index) { internal int Index { get; } = index; internal bool Released { get; set; } }

    private sealed class Fixture
    {
        internal readonly List<Lease> Leases = new();
        internal readonly Dictionary<int, TaskCompletionSource<Lease?>> Waiting = new();
        internal readonly List<int> Started = new();
        internal readonly HashSet<int> Failing = new();
        internal readonly HashSet<int> IgnoreCancel = new();
        internal bool Immediate = true;
        internal readonly FilmstripThumbnailWindow<Lease> Window;

        internal Fixture() => Window = new(Load, lease => lease.Released = true);

        private Task<Lease?> Load(int index, CancellationToken token)
        {
            Started.Add(index);
            if (Failing.Contains(index)) return Task.FromException<Lease?>(new IOException("no preview"));
            if (Immediate) { Lease lease = new(index); Leases.Add(lease); return Task.FromResult<Lease?>(lease); }
            TaskCompletionSource<Lease?> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiting[index] = source;
            if (!IgnoreCancel.Contains(index)) token.Register(() => source.TrySetCanceled(token));
            return source.Task;
        }

        internal void Finish(int index)
        {
            Lease lease = new(index); Leases.Add(lease);
            Waiting[index].TrySetResult(lease);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task OnlyTheWindowIsHeldAndWhatScrollsOutIsGivenBack()
    {
        Fixture f = new();
        f.Window.SetWindow(new(0, 9), 0);
        await Until(() => f.Window.HeldCount == 10);
        Assert.Equal(10, f.Started.Count);
        f.Window.SetWindow(new(5, 14), 10);
        await Until(() => f.Window.HeldCount == 10 && f.Window.IsHeld(14));
        Assert.All(f.Leases.Where(lease => lease.Index < 5), lease => Assert.True(lease.Released));
        Assert.All(f.Leases.Where(lease => lease.Index >= 5), lease => Assert.False(lease.Released));
        // Tiles 5..9 were kept, not decoded again.
        Assert.Equal(1, f.Started.Count(index => index == 7));
        Assert.Null(f.Window.TryGet(2));
        Assert.NotNull(f.Window.TryGet(7));
    }

    [Fact]
    public async Task ALoadThatScrolledOutIsCancelledAndALateLeaseIsGivenBack()
    {
        Fixture f = new() { Immediate = false };
        f.IgnoreCancel.Add(2); // a decoder that does not notice the cancellation and delivers late
        f.Window.SetWindow(new(0, 3), 0);
        Assert.Equal(4, f.Window.PendingCount);
        f.Window.SetWindow(new(2, 5), 3);
        // 0 and 1 were cancelled; 2 and 3 stay pending, 4 and 5 start.
        await Until(() => f.Window.PendingCount == 4);
        Assert.True(f.Waiting[0].Task.IsCanceled);
        Assert.True(f.Waiting[1].Task.IsCanceled);
        // A result that arrives after its tile left the window is released, never held.
        f.Window.SetWindow(new(4, 5), 4);
        f.Finish(2);
        await Until(() => f.Leases.Any(lease => lease.Index == 2 && lease.Released));
        Assert.False(f.Window.IsHeld(2));
        f.Finish(4);
        await Until(() => f.Window.IsHeld(4));
    }

    [Fact]
    public async Task ANeighbourIsLoadedFirstAndAFailedTileIsNotRetriedUntilItReturns()
    {
        Fixture f = new() { Immediate = false };
        f.Window.SetWindow(new(0, 6), 3);
        Assert.Equal(new[] { 3, 2, 4, 1, 5, 0, 6 }, f.Started);
        f.Window.Dispose();

        Fixture g = new();
        g.Failing.Add(1);
        g.Window.SetWindow(new(0, 3), 0);
        await Until(() => g.Window.HeldCount == 3);
        g.Window.SetWindow(new(0, 3), 0);
        g.Window.SetWindow(new(0, 3), 1);
        Assert.Equal(1, g.Started.Count(index => index == 1));
        // Out of the window and back: it is tried again.
        g.Window.SetWindow(new(2, 6), 4);
        await Until(() => g.Window.IsHeld(6));
        g.Failing.Clear();
        g.Window.SetWindow(new(0, 3), 1);
        await Until(() => g.Window.IsHeld(1));
        Assert.Equal(2, g.Started.Count(index => index == 1));
    }

    [Fact]
    public async Task ResetAndDisposeReleaseEverythingAndSilenceLaterResults()
    {
        Fixture f = new();
        List<int> loaded = new();
        f.Window.Loaded += loaded.Add;
        f.Window.SetWindow(new(0, 4), 0);
        await Until(() => f.Window.HeldCount == 5);
        Assert.Equal(5, loaded.Count);
        f.Window.Reset();
        Assert.Equal(0, f.Window.HeldCount);
        Assert.All(f.Leases, lease => Assert.True(lease.Released));

        Fixture g = new() { Immediate = false };
        g.Window.SetWindow(new(0, 2), 0);
        g.Window.Dispose();
        Assert.Equal(0, g.Window.PendingCount);
        g.Window.SetWindow(new(0, 2), 0);
        Assert.Empty(g.Waiting.Where(pair => pair.Key > 2));
        Assert.Equal(3, g.Started.Count);
    }

    // ---- settings ----

    [Fact]
    public void TheThreeSettingsHaveDefaultMinimumAndMaximumAndTheDocumentedValues()
    {
        Config defaults = new();
        Assert.True(defaults.GalleryFilmstrip);
        Assert.Equal(64, defaults.GalleryFilmstripHeight); // GL5e: 72 before; 72 stays choosable
        Assert.Equal(12, defaults.GalleryFilmstripPreload);
        Assert.Equal((48, 160), (Config.GalleryFilmstripHeightMinimum, Config.GalleryFilmstripHeightMaximum));
        Assert.Equal((4, 60), (Config.GalleryFilmstripPreloadMinimum, Config.GalleryFilmstripPreloadMaximum));
        defaults.Validate();
        foreach (int valid in new[] { 48, 160 })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryFilmstripHeight = valid;
            candidate.Validate();
            Config target = new();
            target.CopySettingsFieldsFrom(candidate);
            Assert.Equal(valid, target.GalleryFilmstripHeight);
        }
        foreach (int invalid in new[] { 47, 161, int.MinValue, int.MaxValue })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryFilmstripHeight = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
        }
        foreach (int valid in new[] { 4, 60 })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryFilmstripPreload = valid;
            candidate.Validate();
        }
        foreach (int invalid in new[] { 3, 61, -1 })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryFilmstripPreload = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
        }
    }

    [Fact]
    public void TheFilmstripChoicesRoundTripThroughTheProfileAndTheCopies()
    {
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        config.GalleryFilmstrip = false;
        config.GalleryFilmstripHeight = 100;
        config.GalleryFilmstripPreload = 20;
        config.SaveConfig(profile.Path);
        Config loaded = Config.LoadConfig(profile.Path);
        Assert.False(loaded.GalleryFilmstrip);
        Assert.Equal((100, 20), (loaded.GalleryFilmstripHeight, loaded.GalleryFilmstripPreload));
        Config clone = loaded.Clone();
        Assert.False(clone.GalleryFilmstrip);
        Assert.Equal((100, 20), (clone.GalleryFilmstripHeight, clone.GalleryFilmstripPreload));
        Config copy = new();
        copy.CopyFrom(loaded);
        Assert.False(copy.GalleryFilmstrip);
        Assert.Equal((100, 20), (copy.GalleryFilmstripHeight, copy.GalleryFilmstripPreload));
        Config settings = new();
        settings.CopySettingsFieldsFrom(loaded);
        Assert.False(settings.GalleryFilmstrip);
        Assert.Equal((100, 20), (settings.GalleryFilmstripHeight, settings.GalleryFilmstripPreload));
    }

    // ---- GL5e: the look of the strip ----

    [Fact]
    public void TheTileGapIsASettingFromFourToSixteenDipWithEightAsTheDefault()
    {
        Assert.Equal((8, 4, 16), (Config.GalleryFilmstripTileGapDefault, Config.GalleryFilmstripTileGapMinimum, Config.GalleryFilmstripTileGapMaximum));
        Assert.Equal(8, GalleryFilmstripPlan.TileGap);
        Assert.Equal(64, Config.GalleryFilmstripHeightDefault);
        Assert.Equal((4, 16, 8), (GalleryFilmstripPlan.ClampGap(0), GalleryFilmstripPlan.ClampGap(99), GalleryFilmstripPlan.ClampGap(8)));
        Config defaults = new();
        Assert.Equal(8, defaults.GalleryFilmstripTileGap);
        defaults.Validate();
        foreach (int valid in new[] { 4, 16 })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryFilmstripTileGap = valid;
            candidate.Validate();
            Config target = new();
            target.CopySettingsFieldsFrom(candidate);
            Assert.Equal(valid, target.GalleryFilmstripTileGap);
        }
        foreach (int invalid in new[] { 3, 17, int.MinValue, int.MaxValue })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryFilmstripTileGap = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
        }
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        config.GalleryFilmstripTileGap = 12;
        config.SaveConfig(profile.Path);
        Config loaded = Config.LoadConfig(profile.Path);
        Assert.Equal(12, loaded.GalleryFilmstripTileGap);
        Assert.Equal(12, loaded.Clone().GalleryFilmstripTileGap);
        Config copy = new(); copy.CopyFrom(loaded);
        Assert.Equal(12, copy.GalleryFilmstripTileGap);
        Assert.Contains("GalleryFilmstripTileGap = source.GalleryFilmstripTileGap", Source("Desktop/Settings/SettingsTransfer.cs"));
    }

    [Fact]
    public void EveryPlacementOfTilesTakesTheChosenGap()
    {
        const double Tile = 64;
        foreach (double gap in new double[] { 4, 8, 16 })
        {
            double pitch = Tile + gap;
            Assert.Equal(pitch, GalleryFilmstripPlan.Pitch(Tile, gap));
            Assert.Equal(10 * pitch - gap, GalleryFilmstripPlan.Extent(10, Tile, gap));
            Assert.Equal(2, GalleryFilmstripPlan.TileAt(2 * pitch + 1, 0, Tile, 10, gap));
            Assert.Equal(-1, GalleryFilmstripPlan.TileAt(2 * pitch - 1, 0, Tile, 10, gap)); // inside the gap before tile 2
            Assert.Equal(3, GalleryFilmstripPlan.Visible(2 * pitch + Tile, 300, Tile, 100, gap).First);
            Assert.Equal(100 * pitch + Tile / 2 - 500, GalleryFilmstripPlan.CentreOffset(100, 5000, Tile, 1000, gap), 6);
            Assert.Equal(1000 - pitch, GalleryFilmstripPlan.PageOffset(0, 1, 5000, Tile, 1000, gap), 6);
        }
    }

    [Fact]
    public void TheStripHasNoRestingFrameArrowsOnlyWhenNeededAndFadedEnds()
    {
        string strip = Source("Desktop/Controls/GalleryFilmstrip.cs");
        // A tile has no border of its own: the frame is an overlay, 2 dip accent when shown, 1 dip when hovered, nothing at rest.
        Assert.DoesNotContain("BorderThickness = new Thickness(2);", strip);
        Assert.Contains("_frame.BorderThickness = new Thickness(_current ? 2 : _hover ? 1 : 0);", strip);
        Assert.Contains("CornerRadius = new CornerRadius(4)", strip);
        // 32 dip arrows over the strip's ends, shown only where there is somewhere to scroll and the pointer or keyboard is in the band.
        Assert.Contains("button.Width = 32; button.Height = 32;", strip);
        Assert.Contains("_back.IsVisible = active && _back.IsEnabled;", strip);
        Assert.Contains("_forward.IsVisible = active && _forward.IsEnabled;", strip);
        Assert.Contains("bool active = _pointerOver || _keyboardFocus;", strip);
        Assert.Equal(24, GalleryFilmstrip.EdgeFadeWidth);
        Assert.Contains("_strip.OpacityMask = mask;", strip);
    }

    // ---- policy, command, texts ----

    [Fact]
    public void BarebonesLeavesTheStripAwayBySimpleAndAdvancedShowItAndTheUsersChoiceWinsEverywhere()
    {
        Assert.False(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.filmstrip", WorkView.Barebones)));
        Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.filmstrip", WorkView.Simple)));
        Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.filmstrip", WorkView.Advanced)));
        Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.filmstrip", WorkView.Barebones, nonDefaultState: true)));
        Assert.Equal("gallery.filmstrip-toggle", WorkViewPolicy.Get("gallery.filmstrip").Alternative);
        Assert.True(WorkViewPolicy.Entries.Any(entry => entry.Id == "gallery.menu.filmstrip-toggle"));
    }

    [Fact]
    public void TheToggleIsASingleKeyInTheGalleryAndTurnsOffWithTheSingleKeySwitch()
    {
        CommandDefinition command = CommandCatalog.All.Single(item => item.Id == "gallery.filmstrip-toggle");
        Assert.Equal(CommandScope.Gallery, command.Scope);
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            var on = EffectiveShortcutBindings.Build(platform: platform);
            // the key comes from the catalog and may move (Shift+L): the test asserts one effective binding that shows what the catalog says, never a literal
            Assert.NotNull(command.Shortcut);
            Assert.Equal(on.Display("gallery.filmstrip-toggle"), on.For("gallery.filmstrip-toggle").Single().Display(platform));
            Assert.Empty(on.Issues);
        }
        Assert.True(EffectiveShortcutBindings.YieldingDefaultCommandIds.Contains("gallery.filmstrip-toggle"));
    }

    [Theory]
    [InlineData("a.png", "Image")]
    [InlineData("a.GIF", "Gif")]
    [InlineData("clip.webm", "Video")]
    [InlineData("clip.MP4", "Video")]
    [InlineData("noextension", "Image")]
    public void TheTileKindFollowsTheFileExtension(string name, string expected) =>
        Assert.Equal(expected, GalleryFilmstrip.KindOf(name).ToString());

    [Fact]
    public void EveryFilmstripTextExistsInEnglishAndGermanAndKeepsItsPlaceholders()
    {
        string[] keys = GalleryFilmstripCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        foreach ((string key, string english, string german) in GalleryFilmstripCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.False(string.IsNullOrWhiteSpace(german), key);
            for (int slot = 0; slot < 4; slot++)
                Assert.Equal(english.Contains("{" + slot + "}"), german.Contains("{" + slot + "}"));
        }
        Assert.Equal("3 of 40, a.png, image", Texts.T("gallery.filmstrip.tile", 3, 40, "a.png", Texts.T("gallery.filmstrip.kind.image")));
        Texts.SetLanguage("de");
        Assert.Equal("3 von 40, a.png, Bild", Texts.T("gallery.filmstrip.tile", 3, 40, "a.png", Texts.T("gallery.filmstrip.kind.image")));
        Assert.Contains("72", Texts.T("settings.galleryFilmstripHeightHint", 72, 48, 160));
    }

    // ---- GL1: the place of the band (bottom of the dock by default, top selectable) ----

    private const double TopRow = 56; // GalleryViewer.TopRowClearance: the row of the way back and the star

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheBottomBandIsInTheDockOrTheFooterAndTakesNothingFromThePicture(bool overlay)
    {
        GalleryFilmstripPlan.Host host = overlay ? GalleryFilmstripPlan.Host.OverlayFooter : GalleryFilmstripPlan.Host.Dock;
        foreach (bool show in new[] { false, true })
        {
            var place = GalleryFilmstripPlan.Place(GalleryFilmstripDock.Bottom, overlay, show, Tile, TopRow, panelRight: 320);
            Assert.Equal(host, place.Host);
            Assert.Equal(0, place.PictureTopInset);   // the picture keeps the whole band's height (Tom: it wins 80 dip)
            Assert.False(place.PeeksAtTopEdge);        // the bottom band comes with the controls: no top edge zone, no peek
            Assert.Equal(GalleryFilmstripPlan.DockMargin, place.Left);
            Assert.Equal(GalleryFilmstripPlan.DockMargin, place.Right); // an open side panel is not the bottom band's business
            // GL1+GL2 (integration): docked the band is the dock's first row and touches the picture like the video bar and the tools
            // (GL2: no gap under the picture); floating in the footer it keeps its 8 dip from the picture. The tools follow without a gap.
            Assert.Equal(overlay ? GalleryFilmstripPlan.DockMargin : 0, place.Top);
            Assert.Equal(0, place.Bottom);
        }
    }

    [Fact]
    public void TheDockedBottomBandKeepsTheFallbackGapBelowTheVideoStrip()
    {
        // The Windows transparency fallback keeps the video bar under the picture; the dock (here: the band, its first row) starts 8 dip below.
        var fallback = GalleryFilmstripPlan.Place(GalleryFilmstripDock.Bottom, overlay: false, show: true, Tile, TopRow, panelRight: 0, transparencyFallback: true);
        Assert.Equal(GalleryFilmstripPlan.Host.Dock, fallback.Host);
        Assert.Equal(GalleryFilmstripPlan.DockMargin, fallback.Top);
        Assert.Equal(0, fallback.PictureTopInset);
        Assert.Equal(0, GalleryFilmstripPlan.Place(GalleryFilmstripDock.Bottom, false, true, Tile, TopRow, 0).Top);
        // The top band is not the dock's business: the fallback changes nothing there.
        Assert.Equal(GalleryFilmstripPlan.Place(GalleryFilmstripDock.Top, false, true, Tile, TopRow, 0),
            GalleryFilmstripPlan.Place(GalleryFilmstripDock.Top, false, true, Tile, TopRow, 0, transparencyFallback: true));
    }

    [Fact]
    public void TheTopBandKeepsTheOldPlacementBelowTheWayBackAndTheStar()
    {
        var docked = GalleryFilmstripPlan.Place(GalleryFilmstripDock.Top, overlay: false, show: true, Tile, TopRow, panelRight: 0);
        Assert.Equal(GalleryFilmstripPlan.Host.Picture, docked.Host);
        Assert.Equal(TopRow, docked.Top);                       // never above the top row: it covers neither the back button nor the star
        Assert.Equal(12, docked.Left);
        Assert.Equal(12, docked.Right);
        Assert.Equal(TopRow + GalleryFilmstripPlan.BandHeight(Tile) + 2, docked.PictureTopInset); // docked: the picture starts below the band
        Assert.False(docked.PeeksAtTopEdge);                    // docked it is permanent: nothing to peek
        Assert.Equal(0, GalleryFilmstripPlan.Place(GalleryFilmstripDock.Top, false, show: false, Tile, TopRow, 0).PictureTopInset);

        var floating = GalleryFilmstripPlan.Place(GalleryFilmstripDock.Top, overlay: true, show: true, Tile, TopRow, panelRight: 332);
        Assert.Equal(GalleryFilmstripPlan.Host.Picture, floating.Host);
        Assert.Equal(0, floating.PictureTopInset);              // floating: it covers the picture and moves nothing
        Assert.True(floating.PeeksAtTopEdge);                   // and the top edge shows it
        Assert.Equal(12 + 332, floating.Right);                 // it ends before an open side panel
    }

    [Theory]
    [InlineData(48)]
    [InlineData(72)]
    [InlineData(160)]
    public void TheBottomBandNeverAsksForMoreRoomThanTheTopBand(int tile)
    {
        var top = GalleryFilmstripPlan.Place(GalleryFilmstripDock.Top, false, true, tile, TopRow, 0);
        var bottom = GalleryFilmstripPlan.Place(GalleryFilmstripDock.Bottom, false, true, tile, TopRow, 0);
        Assert.True(top.PictureTopInset >= TopRow + GalleryFilmstripPlan.BandHeight(tile));
        Assert.Equal(0, bottom.PictureTopInset);
        // The bottom band costs the picture its own height at the foot instead, and only while it is shown: not shown, nothing is reserved.
        Assert.Equal(top.PictureTopInset - TopRow - 2, GalleryFilmstripPlan.BandHeight(tile));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ThePeekFollowsThePlanForEveryDockAndOverlayState(bool top, bool overlay, bool peeks)
    {
        GalleryFilmstripDock dock = top ? GalleryFilmstripDock.Top : GalleryFilmstripDock.Bottom;
        var plan = GalleryChromePlan.Resolve(new(GalleryChromeMode.View, WorkView.Simple, new GalleryLayoutChoice(FilmstripDock: dock), false,
            overlay, false, false, false, false, 1920, 1030));
        Assert.True(plan.Filmstrip);
        Assert.Equal(peeks, plan.FilmstripPeek);
        var place = GalleryFilmstripPlan.Place(plan.FilmstripDock, overlay, true, Tile, TopRow, 0);
        Assert.Equal(plan.FilmstripPeek, place.PeeksAtTopEdge); // the viewer's rule and the plan agree
    }

    [Fact]
    public void TheDefaultIsTheBottomAndTheWorkViewStillDecidesWhetherThereIsAStrip()
    {
        foreach (bool overlay in new[] { false, true })
        {
            var simple = GalleryChromePlan.Resolve(new(GalleryChromeMode.View, WorkView.Simple, GalleryLayoutChoice.Default, false, overlay, false, false, false, false, 1280, 800));
            Assert.True(simple.Filmstrip);
            Assert.Equal(GalleryFilmstripDock.Bottom, simple.FilmstripDock);
            Assert.False(simple.FilmstripPeek);
            var barebones = GalleryChromePlan.Resolve(new(GalleryChromeMode.View, WorkView.Barebones, GalleryLayoutChoice.Default, false, overlay, false, false, false, false, 1280, 800));
            Assert.False(barebones.Filmstrip); // gallery.filmstrip is Hidden in Barebones: availability stays with the work view
            Assert.True(GalleryChromePlan.Resolve(new(GalleryChromeMode.View, WorkView.Barebones, new GalleryLayoutChoice(Filmstrip: true), false, overlay, false, false, false, false, 1280, 800)).Filmstrip);
        }
    }

    [Fact]
    public void TheViewerPutsTheBottomBandFirstInTheDockAndInTheFooterAndOnlyTheTopBandPeeks()
    {
        string filmstrip = Source("Desktop/GalleryViewer.Filmstrip.cs");
        Assert.Contains("MoveOverlayControlAt(_filmstrip, _dockStack, 0)", filmstrip);
        Assert.Contains("MoveOverlayControlAt(_filmstrip, _overlayFooter, 0)", filmstrip);
        Assert.Contains("_viewport.Margin = new Thickness(0, place.PictureTopInset, 0, 0)", filmstrip);   // no hand-made inset any more
        Assert.DoesNotContain("TopRowClearance + GalleryFilmstripPlan.BandHeight", filmstrip);
        Assert.Contains("if (_filmstripPeek && !plan.FilmstripPeek) _filmstripPeek = false;", filmstrip);
        Assert.Contains("picture.Children.IndexOf(_close) + 1", filmstrip); // the top band is the tab stop after the way back
        Assert.Contains("FilmstripPeeksAtTop", filmstrip);
        // The hot zone and the peek start only through the top-band rule.
        Assert.Contains("OverlayControlsEffective && FilmstripPeeksAtTop;", filmstrip);
        Assert.Contains("!_helpOpen && FilmstripPeeksAtTop;", filmstrip);
        // The dock exists once, below the picture; the tools and the info line are its children after the band.
        string viewer = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("_dockStack.Children.Add(_chrome);", viewer);
        Assert.Contains("_dockStack.Children.Add(_infoOverlay);", viewer);
        string overlay = Source("Desktop/GalleryViewer.Overlay.cs");
        Assert.Contains("MoveOverlayControl(_chrome, _dockStack);", overlay);
        Assert.Contains("MoveOverlayControl(_chrome, _overlayFooter);", overlay);
        Assert.Contains("private void MoveOverlayControl(Control control, Panel target) => MoveOverlayControlAt(control, target, -1);", overlay);
        // The transfer keeps the place of a control that re-attaches to the same parent and inserts at the requested place.
        string transfer = Source("Desktop/GalleryControlTransfer.cs");
        Assert.Contains("target.Children.Insert(Math.Min(_index, target.Children.Count), _control)", transfer);
        Assert.Contains("parent.Children.IndexOf(control)", transfer);
    }

    [Fact]
    public void TheBandTellsAScreenReaderWhereItIsInEveryLanguage()
    {
        string englishBottom = LanguageText("en", "gallery.filmstrip.dock.bottom"), englishTop = LanguageText("en", "gallery.filmstrip.dock.top");
        Assert.Contains("below the picture", englishBottom);
        Assert.Contains("above the picture", englishTop);
        foreach (string code in Texts.BundledPackCodes().Prepend("de"))
        {
            string bottom = LanguageText(code, "gallery.filmstrip.dock.bottom"), top = LanguageText(code, "gallery.filmstrip.dock.top");
            Assert.NotEqual("gallery.filmstrip.dock.bottom", bottom);   // a real text, not the key
            Assert.NotEqual("gallery.filmstrip.dock.top", top);
            Assert.NotEqual(englishBottom, bottom);                      // and a translation, not the English source
            Assert.NotEqual(englishTop, top);
            Assert.NotEqual(bottom, top);
            Assert.NotEqual(LanguageText("en", "settings.galleryFilmstripHint"), LanguageText(code, "settings.galleryFilmstripHint"));
        }
        // The settings hint describes the new default and no longer says that the row sits under the back and star buttons.
        Assert.StartsWith("A row of thumbnails of the gallery's media. By default it sits at the bottom", LanguageText("en", "settings.galleryFilmstripHint"));
        Assert.Contains("unten", LanguageText("de", "settings.galleryFilmstripHint"));
    }

    private static string LanguageText(string code, string key)
    {
        Texts.SetLanguage(code);
        try { return Texts.T(key); } finally { Texts.SetLanguage("en"); }
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)));

    private sealed class Profile : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("meowchan-filmstrip-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
