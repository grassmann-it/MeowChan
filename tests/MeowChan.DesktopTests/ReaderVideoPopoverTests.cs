using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// The reader popover's inline video settings: volume (a slider), autoplay, "Repeat video" and "Keep images expanded". Volume, autoplay and repeat are the
// settings the page has (one setting, two places); "Keep images expanded" is new (Config, copy, export, reset, search, packs). A change reaches a playing
// inline video without reloading it.
[Collection("Localization")]
public sealed class ReaderVideoPopoverTests : IDisposable
{
    public ReaderVideoPopoverTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static QuickSettingsSpec Reader => QuickSettingsSurfaces.Get(QuickSettingsSurfaces.Reader);
    private static QuickSettingField Field(string id) => Reader.Fields.Single(field => field.Id == id);

    private sealed class FakePlayer : IReaderInlineVideoPlayer
    {
        internal readonly List<(int? Volume, bool? Repeat)> Live = new();
        internal readonly List<bool?> LiveMuted = new();
        public bool IsPlaying { get; private set; }
        public Task StartAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PlayAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync() { IsPlaying = false; return Task.CompletedTask; }
        public void ApplyLive(int? volume, bool? repeat, bool? muted = null) { Live.Add((volume, repeat)); LiveMuted.Add(muted); }
        public ValueTask DisposeAsync() { IsPlaying = false; return ValueTask.CompletedTask; }
    }

    private sealed class FakeOwner : IReaderInlineVideoOwner
    {
        internal bool Viewer;
        public bool IsViewer => Viewer;
        public void PlayerReleased() { }
        public void CurrentChanged(bool current) { }
        public void PlaybackChanged(bool playing) { }
    }

    private static ReaderInlineVideoOptions On() => ReaderInlineVideoOptions.Default with { Enabled = true };

    // ---- the popover rows ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheVideoGroupsAreOwnSectionsAndKeepImagesExpandedFollowsTheClickChoice()
    {
        string[] ids = ReaderQuickSettings.Fields.Select(field => field.Id).ToArray();
        int click = Array.IndexOf(ids, ReaderQuickSettings.MediaClickFieldId);
        Assert.Equal(SettingsFieldId.ReaderKeepImagesExpanded, ids[click + 1]);
        string? section = null;
        Dictionary<string, List<string>> groups = new();
        foreach (QuickSettingsEntry entry in ReaderQuickSettings.Layout)
        {
            if (entry is QuickSettingsSection header) section = header.TitleKey;
            else if (entry is QuickSettingsFieldEntry row) { if (!groups.TryGetValue(section!, out List<string>? list)) groups[section!] = list = new(); list.Add(row.Field.Id); }
        }
        Assert.Equal(new[] { SettingsFieldId.ReaderInlineVideoAutoplayMuted, SettingsFieldId.GalleryVideoRepeat, SettingsFieldId.VideoStartMuted, SettingsFieldId.VideoVolume },
            groups["quicksettings.reader.section.postVideo"]);
        Assert.Equal(new[] { SettingsFieldId.ReaderSidebarVideoAutoplay, SettingsFieldId.ReaderSidebarVideoLoop, SettingsFieldId.ReaderSidebarVideoStartMuted },
            groups["quicksettings.reader.section.sidebarVideo"]);
    }

    [Fact]
    public void TheSidebarVideoDefaultsAreAutoplayOnLoopOnAndSoundOn()
    {
        Config config = new();
        Assert.True(config.ReaderSidebarVideoAutoplay);
        Assert.True(config.ReaderSidebarVideoLoop);
        Assert.False(config.ReaderSidebarVideoStartMuted);
        ReaderInlineVideoOptions options = ReaderInlineVideoOptions.From(config);
        Assert.True(options.SidebarAutoplay);
        Assert.True(options.SidebarLoop);
        Assert.False(options.SidebarStartMuted);
        // An older settings file without the keys, or with a broken value, gets the defaults ON; a saved off stays off.
        Config old = JsonSerializer.Deserialize<Config>("{}")!;
        Assert.True(old.ReaderSidebarVideoAutoplay && old.ReaderSidebarVideoLoop);
        Config broken = JsonSerializer.Deserialize<Config>("{\"ReaderSidebarVideoAutoplay\":\"x\",\"ReaderSidebarVideoLoop\":null}")!;
        Assert.True(broken.ReaderSidebarVideoAutoplay && broken.ReaderSidebarVideoLoop);
        Config saved = JsonSerializer.Deserialize<Config>("{\"ReaderSidebarVideoAutoplay\":false,\"ReaderSidebarVideoLoop\":false,\"ReaderSidebarVideoStartMuted\":true}")!;
        Assert.False(saved.ReaderSidebarVideoAutoplay);
        Assert.False(saved.ReaderSidebarVideoLoop);
        Assert.True(saved.ReaderSidebarVideoStartMuted);
        Assert.True(ReaderInlineVideoRules.ShouldAutoplayViewer(options with { Enabled = true }, shown: true));
        Assert.False(ReaderInlineVideoRules.ShouldAutoplayViewer(options with { Enabled = true, SidebarAutoplay = false }, shown: true));
        Assert.False(ReaderInlineVideoRules.ShouldAutoplayViewer(options with { Enabled = true }, shown: false));
        Assert.True(ReaderInlineVideoRules.StartMutedViewer(options with { SidebarStartMuted = true }, null));
        Assert.False(ReaderInlineVideoRules.StartMutedViewer(options with { SidebarStartMuted = true }, false)); // the session's speaker choice wins
        Assert.True(ReaderInlineVideoRules.StartMutedViewer(options, true));
    }

    [Fact]
    public void SidebarVideoSettingsSurviveCloneCopiesExportAndTheJsonRoundTrip()
    {
        Config source = new() { ReaderSidebarVideoAutoplay = false, ReaderSidebarVideoLoop = false, ReaderSidebarVideoStartMuted = true };
        Config clone = source.Clone();
        Config copy = new(); copy.CopyFrom(source);
        Config settings = new(); settings.CopySettingsFieldsFrom(source);
        Config loaded = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(source))!;
        foreach (Config config in new[] { clone, copy, settings, loaded })
        {
            Assert.False(config.ReaderSidebarVideoAutoplay);
            Assert.False(config.ReaderSidebarVideoLoop);
            Assert.True(config.ReaderSidebarVideoStartMuted);
        }
        Assert.False(SettingsTransfer.CreatePortableSnapshot(source).ReaderSidebarVideoLoop);
        Assert.False(SettingsTransfer.Export(source, "1.1.0")[nameof(Config.ReaderSidebarVideoLoop)]!.GetValue<bool>());
    }

    [Fact]
    public async Task SidebarLoopAndMuteReachOnlyTheViewerAndInlineRepeatOnlyThePostVideos()
    {
        ReaderInlineVideoController controller = new();
        await controller.ConfigureAsync(On());
        FakeOwner viewer = new() { Viewer = true };
        FakeOwner post = new();
        FakePlayer viewerPlayer = new(), postPlayer = new();
        Assert.True(await controller.PlayAsync(viewer, () => viewerPlayer));
        Assert.True(await controller.PlayAsync(post, () => postPlayer));

        await controller.ConfigureAsync(On() with { SidebarLoop = false });
        Assert.Equal(((int?)null, (bool?)false), Assert.Single(viewerPlayer.Live));
        Assert.Empty(postPlayer.Live);
        await controller.ConfigureAsync(On() with { SidebarLoop = false, Repeat = true });
        Assert.Equal(((int?)null, (bool?)true), postPlayer.Live[0]);
        Assert.Single(viewerPlayer.Live);
        await controller.ConfigureAsync(On() with { SidebarLoop = false, Repeat = true, SidebarStartMuted = true });
        Assert.Equal(true, viewerPlayer.LiveMuted[^1]);
        Assert.Single(postPlayer.Live);
        Assert.True(postPlayer.IsPlaying);
    }

    [Fact]
    public void TheNewTextsExistInAllTwelveLanguages()
    {
        string[] keys =
        {
            "settings.reader.sidebarVideoAutoplay", "settings.reader.sidebarVideoAutoplay.hint", "settings.reader.sidebarVideoLoop", "settings.reader.sidebarVideoLoop.hint",
            "settings.reader.sidebarVideoMuted", "settings.reader.sidebarVideoMuted.hint",
            "quicksettings.reader.section.postVideo", "quicksettings.reader.section.sidebarVideo"
        };
        foreach (string key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.English[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.German[key]), key);
        }
        string packs = Path.Combine(RepositoryRoot(), "Desktop/Localization/Packs");
        foreach (string file in Directory.GetFiles(packs, "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement strings = doc.RootElement.GetProperty("strings");
            foreach (string key in keys) Assert.True(strings.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"{Path.GetFileName(file)} {key}");
        }
    }

    [Fact]
    public void TheDefaultsAreTheBehaviourBeforeTheRows()
    {
        Config config = new();
        Assert.False(config.ReaderKeepImagesExpanded);       // an expanded image went back to its thumbnail
        Assert.False(config.ReaderInlineVideoAutoplayMuted); // no autoplay
        Assert.False(config.GalleryVideoRepeat);             // no repeat
        Assert.Equal(70, config.VideoVolume);                // the gallery's remembered volume
        ReaderInlineVideoOptions options = ReaderInlineVideoOptions.From(config);
        Assert.False(options.KeepImagesExpanded);
        Assert.False(options.Repeat);
        Assert.Equal(70, options.Volume);
    }

    [Fact]
    public void VolumeIsASliderOverTheSettingsOwnRangeAndTheOtherRowsAreToggles()
    {
        QuickIntegerField volume = Assert.IsType<QuickIntegerField>(Field(SettingsFieldId.VideoVolume));
        Assert.True(volume.AsSlider);
        Assert.Equal((0, 100), (volume.Minimum, volume.Maximum));
        Assert.True(volume.TryCreateChange("100", out _));
        Assert.True(volume.TryCreateChange("0", out _));
        Assert.False(volume.TryCreateChange("101", out _));
        Assert.False(volume.TryCreateChange("-1", out _));
        Assert.False(volume.TryCreateChange("loud", out _));
        Config config = new();
        Assert.True(volume.TryCreateChange("35", out Action<Config>? change));
        change(config);
        Assert.Equal(35, config.VideoVolume);
        Assert.Equal("35", volume.Read(config));
        config.Validate();
        // Only a volume uses a slider; the page's number fields keep their number field.
        Assert.All(Reader.Fields.OfType<QuickIntegerField>().Where(field => field.Id != SettingsFieldId.VideoVolume), field => Assert.False(field.AsSlider));
        foreach (string id in new[] { SettingsFieldId.ReaderKeepImagesExpanded, SettingsFieldId.ReaderInlineVideoAutoplayMuted, SettingsFieldId.GalleryVideoRepeat })
        {
            QuickSettingField toggle = Field(id);
            Assert.Equal(QuickSettingKind.Toggle, toggle.Kind);
            Config target = new();
            Assert.True(toggle.TryCreateChange("True", out Action<Config>? on));
            on(target);
            Assert.Equal("True", toggle.Read(target));
            Assert.True(toggle.TryCreateChange("False", out Action<Config>? off));
            off(target);
            Assert.Equal("False", toggle.Read(target));
        }
        Config repeat = new();
        Field(SettingsFieldId.GalleryVideoRepeat).TryCreateChange("True", out Action<Config>? repeatOn);
        repeatOn(repeat);
        Assert.True(repeat.GalleryVideoRepeat); // the very setting the gallery's transport and the page use
    }

    [Fact]
    public void TheRowsUseTheLabelsOfTheSettingsAndTheNewSettingIsInAllTwelveLanguages()
    {
        Assert.Equal("viewer.playback.repeat", Field(SettingsFieldId.GalleryVideoRepeat).LabelKey);
        Assert.Equal("r08d.settings.videoVolume", Field(SettingsFieldId.VideoVolume).LabelKey);
        Assert.Equal("settings.reader.inlineVideoAutoplay", Field(SettingsFieldId.ReaderInlineVideoAutoplayMuted).LabelKey);
        string[] keys = { "settings.reader.keepExpanded", "settings.reader.keepExpanded.hint" };
        foreach (string key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.English[key]));
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.German[key]));
        }
        string packs = Path.Combine(RepositoryRoot(), "Desktop/Localization/Packs");
        string[] files = Directory.GetFiles(packs, "*.json");
        Assert.Equal(10, files.Length); // English and German are built in: twelve languages together
        foreach (string file in files)
        {
            using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(file));
            foreach (string key in keys)
                Assert.True(pack.RootElement.GetProperty("strings").TryGetProperty(key, out JsonElement text) && !string.IsNullOrWhiteSpace(text.GetString()), Path.GetFileName(file) + " lacks " + key);
        }
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    // ---- the new setting in the Config -----------------------------------------------------------------------------------------------

    [Fact]
    public void KeepImagesExpandedSurvivesCloneCopiesExportAndTheJsonRoundTrip()
    {
        Config source = new() { ReaderKeepImagesExpanded = true };
        Config clone = source.Clone(), copy = new(), settings = new();
        copy.CopyFrom(source);
        settings.CopySettingsFieldsFrom(source);
        foreach (Config config in new[] { clone, copy, settings }) Assert.True(config.ReaderKeepImagesExpanded);
        Assert.True(SettingsTransfer.CreatePortableSnapshot(source).ReaderKeepImagesExpanded);
        Assert.True(SettingsTransfer.Export(source, "1.1.0")[nameof(Config.ReaderKeepImagesExpanded)]!.GetValue<bool>());
        Config loaded = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(source))!;
        Assert.True(loaded.ReaderKeepImagesExpanded);
        Assert.False(new Config().ReaderKeepImagesExpanded);
        Assert.False(JsonSerializer.Deserialize<Config>("{}")!.ReaderKeepImagesExpanded); // an older file: the behaviour as before
    }

    [Fact]
    public void TheOptionsFollowTheConfigWithoutChangingTheGalleryOwnedValues()
    {
        Config config = new() { VideoVolume = 20, GalleryVideoRepeat = true, ReaderKeepImagesExpanded = true, ReaderInlineVideoAutoplayMuted = true };
        ReaderInlineVideoOptions options = ReaderMediaPreferences.From(config).Video;
        Assert.Equal(20, options.Volume);
        Assert.True(options.Repeat);
        Assert.True(options.KeepImagesExpanded);
        Assert.True(options.AutoplayMuted);
        // Each is part of the media preferences' value, so a change reaches the page's live apply.
        Assert.NotEqual(ReaderMediaPreferences.From(new Config()), ReaderMediaPreferences.From(config));
        Assert.NotEqual(ReaderMediaPreferences.From(new Config()), ReaderMediaPreferences.From(new Config { GalleryVideoRepeat = true }));
        Assert.NotEqual(ReaderMediaPreferences.From(new Config()), ReaderMediaPreferences.From(new Config { ReaderKeepImagesExpanded = true }));
    }

    // ---- live application ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task VolumeAndRepeatReachAPlayingVideoWithoutReloadingIt()
    {
        ReaderInlineVideoController controller = new();
        await controller.ConfigureAsync(On() with { Volume = 70 });
        FakeOwner owner = new();
        FakePlayer player = new();
        Assert.True(await controller.PlayAsync(owner, () => player));
        Assert.True(player.IsPlaying);

        await controller.ConfigureAsync(On() with { Volume = 30 });
        Assert.Equal((30, (bool?)null), Assert.Single(player.Live));
        await controller.ConfigureAsync(On() with { Volume = 30, Repeat = true });
        Assert.Equal(((int?)null, (bool?)true), player.Live[1]);
        await controller.ConfigureAsync(On() with { Volume = 10, Repeat = false });
        Assert.Equal((10, (bool?)false), player.Live[2]);
        Assert.Equal(3, player.Live.Count);
        Assert.True(player.IsPlaying);              // never paused or restarted
        Assert.Equal(1, controller.Created);        // never re-created
        Assert.Equal(0, controller.Released);
    }

    [Fact]
    public async Task UnchangedOptionsAndOtherChangesLeaveThePlayersAlone()
    {
        ReaderInlineVideoController controller = new();
        await controller.ConfigureAsync(On() with { Volume = 50, Repeat = true });
        FakeOwner owner = new();
        FakePlayer player = new();
        await controller.PlayAsync(owner, () => player);
        await controller.ConfigureAsync(On() with { Volume = 50, Repeat = true });
        await controller.ConfigureAsync(On() with { Volume = 50, Repeat = true, AutoplayMuted = true, MaxFileMiB = 64 });
        await controller.ConfigureAsync(On() with { Volume = 50, Repeat = true, KeepImagesExpanded = true });
        Assert.Empty(player.Live); // a key's own "L" toggle of one player is not undone by an unrelated setting
    }

    [Fact]
    public async Task KeepImagesExpandedChangingTellsTheViewsAndSwitchingItOffForgetsTheMemory()
    {
        ReaderInlineVideoController controller = new();
        int changes = 0;
        controller.OptionsChanged += () => changes++;
        await controller.ConfigureAsync(On() with { InPost = true });
        int afterFirst = changes;
        await controller.ConfigureAsync(On() with { InPost = true, KeepImagesExpanded = true });
        Assert.Equal(afterFirst + 1, changes);
        controller.ExpandedImages.Set("a", true);
        Assert.True(controller.ExpandedImages.Contains("a"));
        await controller.ConfigureAsync(On() with { InPost = true, KeepImagesExpanded = true, Volume = 11 });
        Assert.True(controller.ExpandedImages.Contains("a")); // unrelated change: kept
        await controller.ConfigureAsync(On() with { InPost = true, KeepImagesExpanded = false });
        Assert.Equal(afterFirst + 2, changes);
        Assert.False(controller.ExpandedImages.Contains("a"));
        Assert.Equal(0, controller.ExpandedImages.Count);
    }

    [Fact]
    public void TheExpandedMemoryHoldsTheOpenThreadOnly()
    {
        ReaderExpandedImages memory = new();
        memory.BeginThread("4chan/g/1");
        memory.Set("p1", true);
        memory.Set("p2", true);
        memory.Set("p2", false);
        Assert.True(memory.Contains("p1"));
        Assert.False(memory.Contains("p2"));
        memory.BeginThread("4chan/g/1"); // the same thread built again (a page, a setting): kept
        Assert.True(memory.Contains("p1"));
        memory.BeginThread("4chan/g/2");
        Assert.Equal(0, memory.Count);
        Assert.False(memory.Contains("p1"));
        memory.Set("q", true);
        memory.Clear();
        Assert.False(memory.Contains("q"));
    }
}
