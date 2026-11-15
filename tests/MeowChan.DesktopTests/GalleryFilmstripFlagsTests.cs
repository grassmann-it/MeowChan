using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Avalonia;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>
/// Filmstrip flags (decision 123): the favourite star, the own rating and the "no tags yet" mark on the filmstrip tiles. Pure rules (which flag
/// shows when), the badge plan they produce at the filmstrip's smallest tile, the three settings (default, round trip, every copy, export), the
/// live update (a changed fact is drawn on the next refresh, a notification that changed no source draws nothing) and the contracts of the
/// settings page and the window. The strip's drawing needs a platform and is covered by the gallery smokes.
/// </summary>
[Collection("Localization")]
public sealed class GalleryFilmstripFlagsTests : IDisposable
{
    public GalleryFilmstripFlagsTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Named(XDocument markup, string name) =>
        Assert.Single(markup.Descendants(), element => (string?)element.Attribute(X + "Name") == name);

    private static GalleryEntry EntryOf(string file, char sha = 'a')
    {
        string directory = Path.Combine(Path.GetTempPath(), "filmstrip-flags-fixture");
        string hash = new(sha, 64);
        MediaAssetReference asset = new(hash, file, "original", file, Path.Combine(directory, file), 3, hash, "https://offline.invalid/" + file, file, true);
        MediaAssetSnapshot snapshot = new(directory, MediaManifestStatus.Available, 3, "fixture", [asset]);
        return new GalleryEntry(new GalleryAssetKey(new ResourceId("test", "gallery", "1"), directory, asset.AssetId), snapshot, asset, null, null, false, false);
    }

    // ---- which flag shows when ----

    [Fact]
    public void TheShippedDefaultsAreFavouriteAndRatingOnAndUntaggedWithTheTaggingView()
    {
        FilmstripFlagSettings defaults = FilmstripFlagSettings.Default;
        Assert.True(defaults.Favorite);
        Assert.True(defaults.Rating);
        Assert.Equal(FilmstripUntaggedMode.WithTaggingView, defaults.Untagged);
        Assert.Equal(FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Rating, defaults.Shown(taggingViewOpen: false));
        Assert.Equal(FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Rating | FilmstripFlagKinds.Untagged, defaults.Shown(taggingViewOpen: true));
        Assert.Equal(defaults, FilmstripFlagSettings.FromConfig(new Config()));
    }

    [Theory]
    [InlineData((int)FilmstripUntaggedMode.Off, false, false)]
    [InlineData((int)FilmstripUntaggedMode.Off, true, false)]
    [InlineData((int)FilmstripUntaggedMode.WithTaggingView, false, false)]
    [InlineData((int)FilmstripUntaggedMode.WithTaggingView, true, true)]
    [InlineData((int)FilmstripUntaggedMode.Always, false, true)]
    [InlineData((int)FilmstripUntaggedMode.Always, true, true)]
    public void TheUntaggedMarkFollowsItsThreeStates(int mode, bool taggingViewOpen, bool shown) =>
        Assert.Equal(shown, new FilmstripFlagSettings(Untagged: (FilmstripUntaggedMode)mode).ShowsUntagged(taggingViewOpen));

    [Fact]
    public void EachSwitchOnlyTouchesItsOwnFlag()
    {
        FilmstripFlagFacts facts = new(Favorite: true, Rating: 4, Untagged: true);
        MediaBadgeState kind = new(Kind: MediaBadgeKind.Video, Duration: TimeSpan.FromSeconds(2));
        FilmstripFlagKinds all = FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Rating | FilmstripFlagKinds.Untagged;

        MediaBadgeState everything = FilmstripFlagModel.Compose(kind, facts, all);
        Assert.Equal(new MediaBadgeState(Favorite: true, Rating: 4, Kind: MediaBadgeKind.Video, Duration: TimeSpan.FromSeconds(2), TagCount: 0), everything);

        Assert.Equal(kind with { Favorite = true }, FilmstripFlagModel.Compose(kind, facts, FilmstripFlagKinds.Favorite));
        Assert.Equal(kind with { Rating = 4 }, FilmstripFlagModel.Compose(kind, facts, FilmstripFlagKinds.Rating));
        Assert.Equal(kind with { TagCount = 0 }, FilmstripFlagModel.Compose(kind, facts, FilmstripFlagKinds.Untagged));
        Assert.Equal(kind, FilmstripFlagModel.Compose(kind, facts, FilmstripFlagKinds.None)); // the kind badge (the length) is never a flag
    }

    [Fact]
    public void AMediumWithoutAFactShowsNoFlag()
    {
        FilmstripFlagKinds all = FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Rating | FilmstripFlagKinds.Untagged;
        MediaBadgeState kind = default;
        Assert.Equal(kind, FilmstripFlagModel.Compose(kind, default, all));
        Assert.Equal(kind, FilmstripFlagModel.Compose(kind, new FilmstripFlagFacts(Rating: 0), all));       // 0 is "not rated"
        Assert.Equal(kind, FilmstripFlagModel.Compose(kind, new FilmstripFlagFacts(Untagged: false), all)); // tagged: nothing (the count is no flag)
        Assert.Equal(kind, FilmstripFlagModel.Compose(kind, new FilmstripFlagFacts(Untagged: null), all));  // unknown (tags still loading): never a mark
        Assert.Empty(MediaBadgeModel.Resolve(FilmstripFlagModel.Compose(kind, new FilmstripFlagFacts(Rating: -3), all), MediaBadgeOptions.FilmstripWith(true)));
    }

    [Fact]
    public void TheSourceIsAskedOnlyForTheFlagsThatAreShownAndNotAtAllWhenNoneIs()
    {
        List<FilmstripFlagKinds> asked = [];
        FilmstripFlagModel model = new() { Source = (_, kinds) => { asked.Add(kinds); return new FilmstripFlagFacts(true, 5, true); } };
        GalleryEntry entry = EntryOf("a.png");

        // The defaults with the tagging view closed: favourite and rating only, so the (dearest) tag read never happens.
        MediaBadgeState state = model.StateFor(entry, default);
        Assert.Equal([FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Rating], asked);
        Assert.Equal(new MediaBadgeState(Favorite: true, Rating: 5), state);
        Assert.False(model.Options.ShowUntagged);

        asked.Clear();
        Assert.True(model.SetTaggingViewOpen(true));
        Assert.True(model.Options.ShowUntagged);
        Assert.Equal(0, model.StateFor(entry, default).TagCount);
        Assert.Equal([FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Rating | FilmstripFlagKinds.Untagged], asked);

        asked.Clear();
        Assert.True(model.Configure(new FilmstripFlagSettings(false, false, FilmstripUntaggedMode.Off)));
        Assert.Equal(default, model.StateFor(entry, default));
        Assert.Empty(asked); // everything off: no per-tile work at all
        Assert.False(model.Options.ShowUntagged);
    }

    [Fact]
    public void ChangingASettingOrTheTaggingViewReportsOnlyWhatChangesTheTiles()
    {
        FilmstripFlagModel model = new();
        Assert.False(model.SetTaggingViewOpen(false));
        Assert.True(model.SetTaggingViewOpen(true));    // the untagged mark joins
        Assert.False(model.SetTaggingViewOpen(true));
        Assert.False(model.Configure(FilmstripFlagSettings.Default)); // the same settings redraw nothing
        Assert.False(model.Configure(new FilmstripFlagSettings(Untagged: FilmstripUntaggedMode.Always))); // already shown (the view is open)
        Assert.False(model.SetTaggingViewOpen(false)); // "always" does not care about the view
        Assert.True(model.Configure(new FilmstripFlagSettings(Rating: false, Untagged: FilmstripUntaggedMode.Always)));
        Assert.Equal(FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Untagged, model.Shown);
    }

    // ---- what a tile looks like ----

    private static readonly Size SmallestTile = new(Config.GalleryFilmstripHeightMinimum, Config.GalleryFilmstripHeightMinimum);

    [Fact]
    public void TheFlagsUseTheMediaPageBadgesAndTheHalfStarAndFitTheSmallestTileBesideTheLength()
    {
        MediaBadgeState state = FilmstripFlagModel.Compose(GalleryFilmstrip.BadgeStateOf("clip.mp4", TimeSpan.FromSeconds(2)),
            new FilmstripFlagFacts(true, 4, true), FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Rating | FilmstripFlagKinds.Untagged);
        MediaBadgePlan plan = MediaBadgeLayout.Plan(state, SmallestTile, MediaBadgeOptions.FilmstripWith(true), MediaBadgeLimits.Default);

        Assert.Equal(MediaBadgeMode.Full, plan.Mode);
        Dictionary<MediaBadgeId, PlacedBadge> at = plan.Badges.ToDictionary(placed => placed.Badge.Id);
        Assert.Equal([MediaBadgeId.Favorite, MediaBadgeId.PlayMark, MediaBadgeId.Video, MediaBadgeId.Untagged, MediaBadgeId.Rating],
            at.Keys.Order().ToArray()); // nothing dropped at 48 dip: all four marks and the dense play mark
        Assert.Equal("star-filled", at[MediaBadgeId.Favorite].Badge.Icon);
        Assert.Equal("star-half", at[MediaBadgeId.Rating].Badge.Icon);
        Assert.Equal("4", at[MediaBadgeId.Rating].Badge.Label);
        Assert.Equal(MediaBadgeIcons.Untagged, at[MediaBadgeId.Untagged].Badge.Icon); // the mark of the media page's untagged tiles
        Assert.Equal("0:02", at[MediaBadgeId.Video].Badge.Label);
        Assert.Equal(MediaBadgeCorner.TopLeft, at[MediaBadgeId.Favorite].Badge.Corner);
        Assert.Equal(MediaBadgeCorner.BottomRight, at[MediaBadgeId.Untagged].Badge.Corner);
        // Inside the tile and not on top of each other: the badges lie over the picture, the tile's size never changes.
        Rect tile = new(default, SmallestTile);
        foreach (PlacedBadge placed in plan.Badges.Where(placed => placed.Badge.Id != MediaBadgeId.PlayMark))
            Assert.True(tile.Contains(placed.Bounds.TopLeft) && tile.Contains(placed.Bounds.BottomRight), placed.Badge.Id.ToString());
        Assert.True(at[MediaBadgeId.Rating].Bounds.Left >= at[MediaBadgeId.Favorite].Bounds.Right);
        Assert.False(at[MediaBadgeId.Video].Bounds.Intersects(at[MediaBadgeId.Untagged].Bounds));
    }

    [Fact]
    public void TheUntaggedMarkIsDrawnOnlyWhereTheOptionAsksForIt()
    {
        MediaBadgeState untagged = new(TagCount: 0);
        Assert.Empty(MediaBadgeLayout.Plan(untagged, SmallestTile, MediaBadgeOptions.Filmstrip, MediaBadgeLimits.Default).Badges);
        Assert.Single(MediaBadgeLayout.Plan(untagged, SmallestTile, MediaBadgeOptions.FilmstripWith(true), MediaBadgeLimits.Default).Badges);
        Assert.Equal(MediaBadgeDensity.Dense, MediaBadgeOptions.FilmstripWith(true).Density);
    }

    [Fact]
    public void TheTileNameAndTheTipSayTheFlagsInWords()
    {
        MediaBadgeState state = new(Favorite: true, Rating: 4, TagCount: 0);
        MediaBadgeOptions options = MediaBadgeOptions.FilmstripWith(true);
        Assert.Equal("3 of 40, a.png, Favorite · No tags yet · Rating 4 of 5", GalleryFilmstrip.TileName(3, 40, "a.png", state, options));
        // Without the untagged option the mark is not part of what the tile says either.
        Assert.Equal("3 of 40, a.png, Favorite · Rating 4 of 5", GalleryFilmstrip.TileName(3, 40, "a.png", state, MediaBadgeOptions.Filmstrip));
        Texts.SetLanguage("de");
        Assert.Equal("3 von 40, a.png, Favorit · Noch keine Tags · Bewertung 4 von 5", GalleryFilmstrip.TileName(3, 40, "a.png", state, options));
        Texts.SetLanguage("en");
        // A tile without a flag keeps the old name.
        Assert.Equal("3 of 40, a.png, image", GalleryFilmstrip.TileName(3, 40, "a.png", default, options));
    }

    // ---- the live update ----

    [Fact]
    public void ASessionNotificationThatChangedNoSourceRedrawsNothing()
    {
        object tags = new(), favorites = new(), ratings = new();
        FilmstripFlagSources first = new(tags, favorites, ratings);
        Assert.True(first.Same(new FilmstripFlagSources(tags, favorites, ratings)));
        Assert.False(first.Same(new FilmstripFlagSources(new object(), favorites, ratings)));  // a tag was set
        Assert.False(first.Same(new FilmstripFlagSources(tags, new object(), ratings)));       // a favourite was set
        Assert.False(first.Same(new FilmstripFlagSources(tags, favorites, new object())));     // a rating was set
        Assert.True(default(FilmstripFlagSources).Same(default));
    }

    [Fact]
    public void AChangedFavouriteRatingOrTagIsReadOnTheNextRead()
    {
        // The strip itself needs a platform (cursors, bitmaps); what it draws is this model's state, read again for every tile that is bound.
        GalleryEntry first = EntryOf("a.png", 'a'), second = EntryOf("b.mp4", 'b');
        Dictionary<GalleryEntry, FilmstripFlagFacts> store = new() { [first] = default, [second] = new FilmstripFlagFacts(Favorite: true) };
        FilmstripFlagModel model = new() { Source = (entry, _) => store[entry] };
        model.Configure(new FilmstripFlagSettings(Untagged: FilmstripUntaggedMode.Always));
        MediaBadgeState image = GalleryFilmstrip.BadgeStateOf("a.png"), video = GalleryFilmstrip.BadgeStateOf("b.mp4");

        Assert.Equal(image, model.StateFor(first, image));
        Assert.Equal(video with { Favorite = true }, model.StateFor(second, video));

        store[first] = new FilmstripFlagFacts(Rating: 3, Untagged: true); // rated, and its tags removed elsewhere
        store[second] = default;                                        // unfavourited elsewhere
        Assert.Equal(image with { Rating = 3, TagCount = 0 }, model.StateFor(first, image));
        Assert.Equal(video, model.StateFor(second, video));

        // The tagging view closes: with "always" the mark stays; with the default it goes, and comes back with the view.
        model.SetTaggingViewOpen(false);
        Assert.Equal(0, model.StateFor(first, image).TagCount);
        model.Configure(FilmstripFlagSettings.Default);
        Assert.Null(model.StateFor(first, image).TagCount);
        model.SetTaggingViewOpen(true);
        Assert.Equal(0, model.StateFor(first, image).TagCount);
        Assert.Equal(FilmstripFlagKinds.Favorite | FilmstripFlagKinds.Rating | FilmstripFlagKinds.Untagged, model.Shown);
    }

    [Fact]
    public void TheStripRedrawsItsTilesWhenTheFlagsOrTheTaggingViewChangeAndDoesNotPoll()
    {
        string strip = Source("Desktop/Controls/GalleryFilmstrip.cs");
        Assert.Contains("if (_flags.Configure(settings)) UpdateTileNames();", strip);
        Assert.Contains("if (_flags.SetTaggingViewOpen(open)) UpdateTileNames();", strip);
        Assert.Contains("if (!_retired && _flags.Shown != FilmstripFlagKinds.None) UpdateTileNames();", strip);
        Assert.Contains("_flags.StateFor(entry, BadgeStateOf(name,", strip);   // the facts are read while a tile is bound, from the entry already loaded
        Assert.DoesNotContain("DispatcherTimer", strip);
    }

    [Fact]
    public void TheWindowFollowsTheSessionLikeTheMediaPageAndLetsGoOfItWhenItCloses()
    {
        string window = Source("Desktop/Shell/MainWindow.GalleryFilmstrip.cs");
        Assert.Contains("_session.StateChanged += OnGalleryFilmstripSessionStateChanged;", window);
        Assert.Contains("Closed += (_, _) => _session.StateChanged -= OnGalleryFilmstripSessionStateChanged;", window);
        Assert.Contains("InitializeGalleryFilmstripFlags();", Source("Desktop/Shell/MainWindow.Navigation.cs"));
        // Only a changed source redraws a notification; an action of the gallery itself always does (rating, favourite, tags).
        Assert.Contains("RefreshGalleryFilmstripFlags(onlyIfSourcesChanged: true)", window);
        Assert.Contains("RefreshGalleryFilmstripFlags();", Source("Desktop/Shell/MainWindow.Ratings.cs"));
        Assert.Contains("RefreshGalleryFilmstripFlags();", Source("Desktop/Shell/MainWindow.GalleryTags.cs"));
        // No polling anywhere in the flags, and the facts come from memory: no file, no timer, no wait.
        foreach (string path in new[] { "Desktop/Controls/GalleryFilmstripFlags.cs", "Desktop/Shell/MainWindow.GalleryFilmstrip.cs" })
            foreach (string forbidden in new[] { "DispatcherTimer", "Task.Delay", "File.", "Thread.Sleep", ".Wait(", ".Result" })
                Assert.DoesNotContain(forbidden, Source(path));
        // The settings reach the strip with the other gallery preferences, and the tag panel tells it when the tagging view opens and closes.
        Assert.Contains("_gallery.ConfigureFilmstripFlags(FilmstripFlagSettings.FromConfig(_config));", window);
        string panel = Source("Desktop/GalleryViewer.TagPanel.cs");
        Assert.Contains("_filmstrip.SetTaggingViewOpen(true);", panel);
        Assert.Contains("_filmstrip.SetTaggingViewOpen(false);", panel);
        // The untagged rule is the media page's own (index per tag snapshot, own media tags only).
        Assert.Contains("_filmstripTagIndex.LacksMediaTags(MediaTagSubjects.Of(entry))", window);
    }

    // ---- the settings ----

    [Fact]
    public void TheSettingsDefaultToFavouriteAndRatingOnAndUntaggedWithTheTaggingViewAndReadUnknownValuesAsTheDefault()
    {
        Config config = new();
        Assert.True(config.GalleryFilmstripFlagFavorite);
        Assert.True(config.GalleryFilmstripFlagRating);
        Assert.Equal("Tagging", config.GalleryFilmstripFlagUntagged);
        foreach ((string? stored, string expected) in new (string?, string)[]
            { ("Always", "Always"), (" always ", "Always"), ("OFF", "Off"), ("Tagging", "Tagging"), ("nonsense", "Tagging"), ("", "Tagging"), (null, "Tagging") })
        {
            Assert.Equal(expected, new Config { GalleryFilmstripFlagUntagged = stored! }.GalleryFilmstripFlagUntagged);
            Assert.Equal(expected, Config.NormalizeGalleryFilmstripUntagged(stored));
            Assert.Equal(FilmstripFlagSettings.ModeOf(stored), FilmstripFlagSettings.ModeOf(expected));
            Assert.Equal(expected, FilmstripFlagSettings.TextOf(FilmstripFlagSettings.ModeOf(stored)));
        }
        // A damaged file keeps the shipped defaults instead of switching a flag off.
        Config loaded = JsonSerializer.Deserialize("{\"GalleryFilmstripFlagFavorite\":\"x\",\"GalleryFilmstripFlagRating\":7,\"GalleryFilmstripFlagUntagged\":12}",
            ConfigJsonContext.Default.Config)!;
        Assert.True(loaded.GalleryFilmstripFlagFavorite);
        Assert.True(loaded.GalleryFilmstripFlagRating);
        Assert.Equal("Tagging", loaded.GalleryFilmstripFlagUntagged);
    }

    [Theory]
    [InlineData(false, true, "Always")]
    [InlineData(true, false, "Off")]
    [InlineData(false, false, "Tagging")]
    public void TheChoicesRoundTripThroughTheProfileAndEveryCopy(bool favorite, bool rating, string untagged)
    {
        using TempDirectory directory = new();
        Config original = new()
        {
            GalleryFilmstripFlagFavorite = favorite, GalleryFilmstripFlagRating = rating, GalleryFilmstripFlagUntagged = untagged,
            DownloadPath = Path.Combine(directory.Path, "media")
        };
        Config clone = original.Clone();
        Config settingsCopy = new();
        settingsCopy.CopySettingsFieldsFrom(clone);
        Config fullCopy = new();
        fullCopy.CopyFrom(settingsCopy);
        fullCopy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config candidate in new[] { original, clone, settingsCopy, fullCopy, loaded })
        {
            Assert.Equal(favorite, candidate.GalleryFilmstripFlagFavorite);
            Assert.Equal(rating, candidate.GalleryFilmstripFlagRating);
            Assert.Equal(untagged, candidate.GalleryFilmstripFlagUntagged);
            candidate.Validate();
            Assert.Equal(new FilmstripFlagSettings(favorite, rating, FilmstripFlagSettings.ModeOf(untagged)), FilmstripFlagSettings.FromConfig(candidate));
        }
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(favorite, saved.RootElement.GetProperty(nameof(Config.GalleryFilmstripFlagFavorite)).GetBoolean());
        Assert.Equal(rating, saved.RootElement.GetProperty(nameof(Config.GalleryFilmstripFlagRating)).GetBoolean());
        Assert.Equal(untagged, saved.RootElement.GetProperty(nameof(Config.GalleryFilmstripFlagUntagged)).GetString());
    }

    [Fact]
    public void TheSettingsTransferCarriesTheThreeChoicesBothWays()
    {
        Config source = new() { GalleryFilmstripFlagFavorite = false, GalleryFilmstripFlagRating = false, GalleryFilmstripFlagUntagged = "Always" };
        Config snapshot = SettingsTransfer.CreatePortableSnapshot(source);
        Assert.False(snapshot.GalleryFilmstripFlagFavorite);
        Assert.False(snapshot.GalleryFilmstripFlagRating);
        Assert.Equal("Always", snapshot.GalleryFilmstripFlagUntagged);
        JsonObject exported = SettingsTransfer.Export(source);
        Assert.False((bool?)exported[nameof(Config.GalleryFilmstripFlagFavorite)]);
        Assert.False((bool?)exported[nameof(Config.GalleryFilmstripFlagRating)]);
        Assert.Equal("Always", (string?)exported[nameof(Config.GalleryFilmstripFlagUntagged)]);
        JsonObject defaults = SettingsTransfer.Export(new Config());
        Assert.True((bool?)defaults[nameof(Config.GalleryFilmstripFlagFavorite)]);
        Assert.Equal("Tagging", (string?)defaults[nameof(Config.GalleryFilmstripFlagUntagged)]);
        // Import goes through the same settings reader: the exported document reads back as the same choices.
        Config imported = JsonSerializer.Deserialize(exported.ToJsonString(), ConfigJsonContext.Default.Config)!;
        Assert.False(imported.GalleryFilmstripFlagFavorite);
        Assert.False(imported.GalleryFilmstripFlagRating);
        Assert.Equal("Always", imported.GalleryFilmstripFlagUntagged);
    }

    [Fact]
    public void TheSettingsPageHasOneUniformRowPerFlagWithResetSearchAndHelp()
    {
        XDocument markup = XDocument.Parse(Source("Desktop/Views/Settings/SettingsEditor.axaml"));
        XElement group = Named(markup, "SettingsReadingMediaGroup");
        XElement favorite = Named(markup, "GalleryFilmstripFlagFavoriteInput"), rating = Named(markup, "GalleryFilmstripFlagRatingInput"),
            untagged = Named(markup, "GalleryFilmstripFlagUntaggedInput");
        Assert.Equal("ToggleSwitch", favorite.Name.LocalName);
        Assert.Equal("{Binding GalleryFilmstripFlagFavorite, Mode=TwoWay}", (string?)favorite.Attribute("IsChecked"));
        Assert.Equal("ToggleSwitch", rating.Name.LocalName);
        Assert.Equal("{Binding GalleryFilmstripFlagRating, Mode=TwoWay}", (string?)rating.Attribute("IsChecked"));
        Assert.Equal("ComboBox", untagged.Name.LocalName);
        Assert.Equal("{Binding SelectedGalleryFilmstripFlagUntagged, Mode=TwoWay}", (string?)untagged.Attribute("SelectedItem"));
        Assert.Equal("{Binding GalleryFilmstripFlagUntaggedChoices}", (string?)untagged.Attribute("ItemsSource"));
        foreach (XElement input in new[] { favorite, rating, untagged })
        {
            Assert.Contains(input.Ancestors(), ancestor => ReferenceEquals(ancestor, group));
            XElement row = Assert.Single(input.Ancestors().Where(ancestor => ancestor.Name.LocalName == "Grid" && ((string?)ancestor.Attribute("Classes"))?.Contains("settings-row") == true).Take(1));
            Assert.Equal("*,280", (string?)row.Attribute("ColumnDefinitions"));
            Assert.Equal("settings-divider", (string?)row.Parent!.Attribute("Classes"));
            Assert.Contains("Hint", input.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText").Value);
        }
        string rowMenu = Source("Desktop/Views/Settings/SettingsEditor.RowMenu.cs");
        Assert.Contains("Flag(GalleryFilmstripFlagFavoriteInput, () => GalleryFilmstripFlagFavorite, value => GalleryFilmstripFlagFavorite = value, config => config.GalleryFilmstripFlagFavorite),", rowMenu);
        Assert.Contains("Flag(GalleryFilmstripFlagRatingInput, () => GalleryFilmstripFlagRating, value => GalleryFilmstripFlagRating = value, config => config.GalleryFilmstripFlagRating),", rowMenu);
        Assert.Contains("Choice(GalleryFilmstripFlagUntaggedInput, () => SelectedGalleryFilmstripFlagUntagged?.Value,", rowMenu);
        string aliases = Source("Desktop/Views/Settings/SettingsSearchAliases.cs");
        foreach (string key in new[] { "GalleryFilmstripFlagFavorite", "GalleryFilmstripFlagRating", "GalleryFilmstripFlagUntagged" })
            Assert.Contains("[\"" + key + "\"] =", aliases);
        string viewer = Source("Desktop/Views/Settings/SettingsEditor.Viewer.cs");
        Assert.Contains("GalleryFilmstripFlagFavorite = config.GalleryFilmstripFlagFavorite;", viewer);   // start, discard, reset, after a save
        Assert.Contains("GalleryFilmstripFlagRating = config.GalleryFilmstripFlagRating;", viewer);
        Assert.Contains("RefreshGalleryFilmstripFlagUntaggedChoices(config.GalleryFilmstripFlagUntagged);", viewer);
        Assert.Contains("candidate.GalleryFilmstripFlagFavorite = GalleryFilmstripFlagFavorite;", viewer);
        Assert.Contains("candidate.GalleryFilmstripFlagRating = GalleryFilmstripFlagRating;", viewer);
        Assert.Contains("candidate.GalleryFilmstripFlagUntagged = SelectedGalleryFilmstripFlagUntagged?.Value ?? _config.GalleryFilmstripFlagUntagged;", viewer);
        Assert.Contains("nameof(SelectedGalleryFilmstripFlagUntagged)", Source("Desktop/Views/Settings/SettingsEditor.axaml.cs")); // a language refresh keeps the choice
    }

    [Fact]
    public void EveryTextOfTheThreeSettingsExistsInEnglishAndGermanAndTheChoicesDiffer()
    {
        string[] keys =
        [
            "settings.galleryFilmstripFlagFavorite", "settings.galleryFilmstripFlagFavoriteHint", "settings.galleryFilmstripFlagRating",
            "settings.galleryFilmstripFlagRatingHint", "settings.galleryFilmstripFlagUntagged", "settings.galleryFilmstripFlagUntaggedHint",
            "settings.galleryFilmstripFlagUntagged.tagging", "settings.galleryFilmstripFlagUntagged.always", "settings.galleryFilmstripFlagUntagged.off"
        ];
        Assert.StartsWith("Filmstrip shows:", Texts.T("settings.galleryFilmstripFlagFavorite"));
        Assert.StartsWith("Filmstrip shows:", Texts.T("settings.galleryFilmstripFlagRating"));
        Assert.StartsWith("Filmstrip shows:", Texts.T("settings.galleryFilmstripFlagUntagged"));
        string[] english = keys.Select(key => Texts.T(key)).ToArray();
        Texts.SetLanguage("de");
        string[] german = keys.Select(key => Texts.T(key)).ToArray();
        Assert.StartsWith("Filmstreifen zeigt:", german[0]);
        for (int index = 0; index < keys.Length; index++)
        {
            Assert.NotEqual(keys[index], english[index]);
            Assert.NotEqual(english[index], german[index]);
        }
        Assert.Equal(3, english[6..].Distinct().Count());
    }
}
