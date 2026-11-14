using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// No native window, player or running dispatcher timer. Root owns isolated execution.
[Collection("Localization")]
public sealed class GalleryPlaybackLanguageRefreshTests : IDisposable
{
    private readonly string _previous = Texts.Language;
    public GalleryPlaybackLanguageRefreshTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    [Fact]
    public void ViewerEntriesAreCompleteUniqueAndUseTheGlobalCatalog()
    {
        // G1 added viewer.playback.status.tagHold (the slideshow waits while the tag panel is worked in); G2 added menu and menuHelp.
        Assert.Equal(40, GalleryPlaybackCatalog.Entries.Length);
        Assert.Equal(40, GalleryPlaybackCatalog.Entries.Select(entry => entry.Key).Distinct().Count());
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.Initialize(); // refresh the normal global catalog snapshot
            Texts.SetLanguage(language);
            foreach (var entry in GalleryPlaybackCatalog.Entries)
            {
                Assert.StartsWith("viewer.playback.", entry.Key);
                Assert.False(string.IsNullOrWhiteSpace(entry.English));
                Assert.False(string.IsNullOrWhiteSpace(entry.German));
                string expected;
                if (language is "en" or "de")
                    expected = language == "de" ? entry.German : entry.English;
                else
                {
                    using var pack = JsonDocument.Parse(ReadSource("Desktop/Localization/Packs/" + language + ".json"));
                    expected = pack.RootElement.GetProperty("strings").GetProperty(entry.Key).GetString()!;
                }
                Assert.Equal(expected, Texts.T(entry.Key));
                Assert.Equal(expected, GalleryPlaybackCatalog.Get(entry.Key));
            }
        }
        // Existing Settings API must not be replaced with the viewer's shorter wording.
        Assert.Equal(Texts.T("settings.galleryPlayback.slideshow"), GalleryPlaybackCatalog.Slideshow);
        Assert.Equal(GalleryPlaybackCatalog.OverlayControls, GalleryPlaybackCatalog.AutoHideControls);
    }

    [Fact]
    public void SameCodeUserPackReloadUsesNormalTextsAndRefreshesChoices()
    {
        using var directory = new TempDirectory();
        const string key = "viewer.playback.manual";
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var selector = new ComboBox();
        var preferences = new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow);
        Set(viewer, "_playbackMode", selector);
        Set(viewer, "_playbackPreferences", preferences);
        string[] Labels() => new[] { "manual", "slideshow" }
            .Select(suffix => GalleryPlaybackCatalog.Get("viewer.playback." + suffix)).ToArray();
        void Reload(Dictionary<string, string> strings)
        {
            File.WriteAllText(Path.Combine(directory.Path, "fr.json"), JsonSerializer.Serialize(new
                { schemaVersion = 1, code = "fr", nativeName = "Français", strings }));
            Texts.Initialize(directory.Path);
            Texts.SetLanguage("fr");
            Assert.Empty(Texts.Warnings);
            viewer.RefreshPlaybackModeChoices(Labels());
            Assert.Equal(1, selector.SelectedIndex);
            Assert.Same(preferences, Get<GalleryPlaybackPreferences>(viewer, "_playbackPreferences"));
            Assert.False(Get<bool>(viewer, "_updatingPlaybackControls"));
        }
        Reload(new() { [key] = "Manuel v1" });
        var first = selector.ItemsSource;
        Assert.Equal("Manuel v1", GalleryPlaybackCatalog.Get(key));
        Reload(new() { [key] = "Manuel v2" });
        Assert.Equal("fr", Texts.Language);
        Assert.Equal("Manuel v2", GalleryPlaybackCatalog.Get(key));
        Assert.NotSame(first, selector.ItemsSource);
        Assert.Equal("Manuel v2", Assert.IsType<string[]>(selector.ItemsSource)[0]);
        // A valid sparse pack replaces the bundle without translating playback.manual.
        // English fallback belongs solely to Texts, not GalleryPlaybackCatalog.
        Reload(new() { ["action.cancel"] = "Annuler" });
        Assert.Equal("fr", Texts.Language);
        Assert.Equal("Annuler", Texts.T("action.cancel"));
        Assert.Equal("Manual", Texts.T(key));
        Assert.Equal(Texts.T(key), GalleryPlaybackCatalog.Get(key));
        Assert.Equal("Manual", Assert.IsType<string[]>(selector.ItemsSource)[0]);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void AllTwelveStatusBranchesResolveAtReadTime(string language)
    {
        Texts.SetLanguage(language);
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var render = typeof(GalleryViewer).GetMethod("SlideshowStatusText", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (SlideshowStatus status in Enum.GetValues<SlideshowStatus>())
        {
            string actual = (string)render.Invoke(viewer, new object[] { status })!;
            if (status == SlideshowStatus.None) { Assert.Equal("", actual); continue; }
            string suffix = char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..];
            var entry = Assert.Single(GalleryPlaybackCatalog.Entries, item => item.Key == "viewer.playback.status." + suffix);
            Assert.Equal(language == "de" ? entry.German : entry.English, actual);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ChoiceRefreshPreservesSemanticIndexAndSuppressesSelectionFeedback(int mode)
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var selector = new ComboBox();
        var preferences = new GalleryPlaybackPreferences((GalleryPlaybackMode)mode, ImageSeconds: 17,
            MediaFocus: true, OverlayControls: true, AutoHideControls: true, OverlayPostContext: true);
        Set(viewer, "_playbackMode", selector);
        Set(viewer, "_playbackPreferences", preferences);
        int changes = 0, unguarded = 0, preferenceWrites = 0;
        selector.SelectionChanged += (_, _) =>
        {
            changes++;
            if (!Get<bool>(viewer, "_updatingPlaybackControls")) unguarded++;
        };
        viewer.PlaybackPreferencesChanged += _ => preferenceWrites++;
        foreach (string language in new[] { "en", "de", "fr", "es", "ru" })
        {
            Texts.Initialize();
            Texts.SetLanguage(language);
            string[] Labels() => new[] { "manual", "slideshow" }
                .Select(suffix => GalleryPlaybackCatalog.Get("viewer.playback." + suffix)).ToArray();
            viewer.RefreshPlaybackModeChoices(Labels());
            Assert.Equal(mode, selector.SelectedIndex);
            var first = selector.ItemsSource;
            viewer.RefreshPlaybackModeChoices(Labels());
            Assert.Same(first, selector.ItemsSource); // ordinary interaction ticks do not churn choices
            Texts.Initialize(); // same code, refreshed global catalog snapshot
            viewer.RefreshPlaybackModeChoices(Labels());
            Assert.Same(first, selector.ItemsSource); // unchanged catalog content does not churn choices
            Assert.Equal(Labels(), Assert.IsType<string[]>(selector.ItemsSource));
            Assert.Equal(mode, selector.SelectedIndex);
            Assert.Same(preferences, Get<GalleryPlaybackPreferences>(viewer, "_playbackPreferences"));
            Assert.False(Get<bool>(viewer, "_updatingPlaybackControls"));
        }
        Assert.True(changes > 0);
        Assert.Equal(0, unguarded);
        Assert.Equal(0, preferenceWrites);
        Set(viewer, "_updatingPlaybackControls", true);
        viewer.RefreshPlaybackModeChoices(new[] { "Manual", "Slideshow" });
        Assert.True(Get<bool>(viewer, "_updatingPlaybackControls")); // retain outer refresh guard
    }

    [Fact]
    public void RelabelingDoesNotResetCountdownGenerationTimerOrRawDiagnostic()
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var clock = new Clock();
        int advances = 0;
        var slideshow = new GallerySlideshowController(() => advances++, clock);
        slideshow.SetMode(GalleryPlaybackMode.Slideshow);
        slideshow.BeginMedia(41, video: false, hasNext: true);
        slideshow.ImageReady(41);
        slideshow.Start();
        var timer = new DispatcherTimer(); // remains stopped; no dispatcher loop runs
        var raw = new TextBlock { Text = "provider: errno=42 / сырой / ü" };
        Set(viewer, "_playbackMode", new ComboBox());
        Set(viewer, "_playbackPreferences", new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow));
        Set(viewer, "_slideshow", slideshow);
        Set(viewer, "_interactionTimer", timer);
        Set(viewer, "_slideshowGeneration", 41L);
        Set(viewer, "_request", 9);
        Set(viewer, "_status", raw);
        var before = typeof(GallerySlideshowController).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToDictionary(field => field, field => field.GetValue(slideshow));
        clock.Advance(TimeSpan.FromSeconds(4));
        foreach (string language in new[] { "de", "en", "de" })
        {
            Texts.SetLanguage(language);
            viewer.RefreshPlaybackModeChoices(new[] { "manual", "slideshow" }
                .Select(suffix => GalleryPlaybackCatalog.Get("viewer.playback." + suffix)).ToArray());
        }
        foreach (var (field, value) in before) Assert.Equal(value, field.GetValue(slideshow));
        Assert.Same(timer, Get<DispatcherTimer>(viewer, "_interactionTimer"));
        Assert.False(timer.IsEnabled);
        Assert.Equal(41L, Get<long>(viewer, "_slideshowGeneration"));
        Assert.Equal(9, Get<int>(viewer, "_request"));
        Assert.Equal("provider: errno=42 / сырой / ü", raw.Text);
        Assert.True(slideshow.IsRunning);
        Assert.Equal(0, advances);
        clock.Advance(TimeSpan.FromSeconds(1));
        slideshow.Tick();
        Assert.Equal(1, advances); // original five-second deadline, not restarted at relabel
    }

    [Fact]
    public void BundledPacksMatchEveryPlaybackEntryAndPreserveFormattingContracts()
    {
        var expected = GalleryPlaybackCatalog.Entries.Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray();
        foreach (string language in new[] { "es", "fr", "ru" })
        {
            string resource = "MeowChan.Desktop.Localization.Packs." + language + ".json";
            Assert.Contains(resource, typeof(Texts).Assembly.GetManifestResourceNames());
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)!;
            Assert.NotNull(stream);
            using var pack = JsonDocument.Parse(stream);
            Assert.Equal(language, pack.RootElement.GetProperty("code").GetString());
            var strings = pack.RootElement.GetProperty("strings");
            Assert.Equal(expected, strings.EnumerateObject()
                .Where(item => item.Name.StartsWith("viewer.playback.", StringComparison.Ordinal))
                .Select(item => item.Name).Order(StringComparer.Ordinal).ToArray());
            foreach (var entry in GalleryPlaybackCatalog.Entries)
            {
                var value = strings.GetProperty(entry.Key);
                Assert.Equal(JsonValueKind.String, value.ValueKind);
                string translated = value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(translated));
                Assert.Equal(Placeholders(entry.English), Placeholders(translated));
            }
        }
    }

    [Fact]
    public void RefreshUsesCatalogContentWithoutTransportPersistenceOrDiagnosticTranslation()
    {
        string source = ReadSource("Desktop/GalleryViewer.Playback.cs");
        string refresh = source[source.IndexOf("    private void RefreshPlaybackControls()", StringComparison.Ordinal)..
            source.IndexOf("    private static void SetToggleLabel(", StringComparison.Ordinal)];
        Assert.Contains("RefreshPlaybackModeChoices(new[]", refresh);
        Assert.Contains("SequenceEqual(labels, StringComparer.Ordinal)", refresh);
        Assert.DoesNotContain("_playbackLabelsLanguage", source);
        Assert.DoesNotContain("GalleryViewerText.Get", source);
        foreach (string forbidden in new[] { "ConfigurePlayback(", "ChangePlaybackPreferences(", ".SetMode(",
            ".SetImageDuration(", ".Start(", ".Pause(", ".Tick(", "_status.Text", "_video.", "_gif.",
            "Texts.Changed +=", "PlaybackPreferencesChanged?.Invoke" })
            Assert.DoesNotContain(forbidden, refresh);
        // The existing owner still dispatches/subscribes; no second lifecycle owner added.
        string owner = ReadSource("Desktop/GalleryViewer.cs");
        Assert.Contains("Texts.Changed += OnLanguageChanged", owner);
        Assert.Contains("Texts.Changed -= OnLanguageChanged", owner);
        Assert.Contains("RefreshPlaybackControls();", owner);
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+[^}]*\}")
        .Select(match => match.Value).Order().ToArray();
    private static void Set(object target, string name, object value) =>
        typeof(GalleryViewer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string name) =>
        (T)typeof(GalleryViewer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static string ReadSource(string path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(path);
    }
    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
