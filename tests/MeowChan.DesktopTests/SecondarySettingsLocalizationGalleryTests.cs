using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Catalog and CLR-only refresh coverage; no Avalonia window, profile or media access.
[Collection("Localization")]
public sealed class SecondarySettingsLocalizationGalleryTests : IDisposable
{
    public SecondarySettingsLocalizationGalleryTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly (string Suffix, Func<string> Read)[] Labels =
    {
        ("title", () => GalleryPlaybackCatalog.Title),
        ("mode", () => GalleryPlaybackCatalog.Mode),
        ("manual", () => GalleryPlaybackCatalog.Manual),
        ("repeat", () => GalleryPlaybackCatalog.Repeat),
        ("slideshow", () => GalleryPlaybackCatalog.Slideshow),
        ("imageSeconds", () => GalleryPlaybackCatalog.ImageSeconds),
        ("imageSecondsHelp", () => GalleryPlaybackCatalog.ImageSecondsHelp),
        ("modeHelp", () => GalleryPlaybackCatalog.ModeHelp),
        ("mediaFocus", () => GalleryPlaybackCatalog.MediaFocus),
        ("overlayControls", () => GalleryPlaybackCatalog.OverlayControls),
        ("overlayPostContext", () => GalleryPlaybackCatalog.OverlayPostContext),
        ("overlayHelp", () => GalleryPlaybackCatalog.OverlayHelp),
        ("profileHelp", () => GalleryPlaybackCatalog.ProfileHelp)
    };

    [Theory]
    [InlineData("en", "Gallery & playback", "Manual", "Repeat current video")]
    [InlineData("de", "Galerie & Wiedergabe", "Manuell", "Aktuelles Video wiederholen")]
    [InlineData("es", "Galería y reproducción", "Manual", "Repetir el vídeo actual")]
    [InlineData("fr", "Galerie et lecture", "Manuel", "Répéter la vidéo actuelle")]
    [InlineData("ru", "Галерея и воспроизведение", "Вручную", "Повторять текущее видео")]
    public void SettingsLabelsReadTheActiveBuiltInOrBundledPack(string language, string title, string manual, string repeat)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        Assert.Equal(title, GalleryPlaybackCatalog.Title);
        Assert.Equal(manual, GalleryPlaybackCatalog.Manual);
        Assert.Equal(repeat, GalleryPlaybackCatalog.Repeat);
        Assert.Equal(GalleryPlaybackCatalog.OverlayControls, GalleryPlaybackCatalog.AutoHideControls);
        Assert.Contains("300", GalleryPlaybackCatalog.ImageSecondsHelp);

        if (language is "en" or "de")
        {
            var builtIn = language == "en" ? BuiltInCatalog.English : BuiltInCatalog.German;
            foreach (var (suffix, read) in Labels)
                Assert.Equal(builtIn["settings.galleryPlayback." + suffix], read());
            return;
        }

        // Compare the actual Settings-facing getters against the embedded pack, not
        // just Texts.T(key) against itself. A surviving EN/DE helper bypass must fail.
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
            "MeowChan.Desktop.Localization.Packs." + language + ".json")!;
        using JsonDocument pack = JsonDocument.Parse(stream);
        JsonElement strings = pack.RootElement.GetProperty("strings");
        foreach (var (suffix, read) in Labels)
        {
            string key = "settings.galleryPlayback." + suffix;
            string expected = strings.GetProperty(key).GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(expected));
            Assert.Equal(expected, read());
            // Spanish Manual is intentionally identical to English.
            if (!(language == "es" && suffix == "manual"))
                Assert.NotEqual(BuiltInCatalog.English[key], read());
        }
        Assert.Contains("(Untested)", Texts.Languages.Single(info => info.Code == language).NativeName);
    }

    [Fact]
    public void ChoiceRefreshPreservesDraftAndPendingValidationAcrossPreviewLanguagesAndSameCodeReloads()
    {
        // Bypass InitializeComponent as in SettingsLanguageRefreshStateTests. This
        // exercises production setters/helper, not actual UI focus or Avalonia bindings.
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        Set(editor, "_config", new Config());
        Set(editor, "_selectedTheme", new SettingsChoice("Dark", "old"));
        Set(editor, "_selectedWorkspaceMode", new SettingsChoice("Advanced", "old"));
        Set(editor, "_selectedReadingFontSize", new SettingsChoice("Large", "old"));
        Set(editor, "_selectedGalleryPlaybackMode", new SettingsChoice("Slideshow", "old"));
        Set(editor, "_selectedLanguage", new LanguageInfo("en", "unsaved language choice"));
        Set(editor, "_gallerySlideshowSeconds", 17m);
        Set(editor, "_draftRevision", 37L);
        Set(editor, "_destinationStatus", DownloadDestinationStatus.BelowReserve);
        using var wait = new CancellationTokenSource();
        Set(editor, "_destinationWait", wait);
        editor.PropertyChanged += (_, e) =>
        {
            // Simulate a synchronous two-way selection clear on ItemsSource refresh.
            if (e.PropertyName == nameof(SettingsEditor.GalleryPlaybackModeChoices))
                editor.SelectedGalleryPlaybackMode = null;
        };

        foreach (string language in new[] { "es", "fr", "ru", "de", "en" })
        {
            Texts.SetLanguage(language);
            for (int reload = 0; reload < 2; reload++)
            {
                if (reload == 1) Texts.Initialize(); // same-code catalog notification
                RefreshChoiceLabels(editor);
                Assert.Equal(language, Texts.Language);
                Assert.Equal(37L, Get<long>(editor, "_draftRevision"));
                Assert.False(wait.IsCancellationRequested);
                Assert.Same(wait, Get<CancellationTokenSource>(editor, "_destinationWait"));
                Assert.Equal(DownloadDestinationStatus.BelowReserve,
                    Get<DownloadDestinationStatus?>(editor, "_destinationStatus"));
                Assert.False(Get<bool>(editor, "_refreshingLanguageLabels"));
                Assert.Equal("en", editor.SelectedLanguage!.Code); // UI language != unsaved language draft
                Assert.Equal("Slideshow", editor.SelectedGalleryPlaybackMode!.Value);
                Assert.Equal(17m, editor.GallerySlideshowSeconds);
                Assert.Equal(new[] { "Manual", "Repeat", "Slideshow" },
                    editor.GalleryPlaybackModeChoices.Select(choice => choice.Value));
                Assert.Equal(new[] { GalleryPlaybackCatalog.Manual, GalleryPlaybackCatalog.Repeat, GalleryPlaybackCatalog.Slideshow },
                    editor.GalleryPlaybackModeChoices.Select(choice => choice.Label));
                Assert.Equal(GalleryPlaybackCatalog.Title, editor.GalleryPlaybackTitle);
                Assert.Equal(GalleryPlaybackCatalog.Mode, editor.GalleryPlaybackModeLabel);
                Assert.Equal(GalleryPlaybackCatalog.ImageSeconds, editor.GallerySlideshowSecondsLabel);
                Assert.Equal(GalleryPlaybackCatalog.ModeHelp, editor.GalleryPlaybackModeHelp);
                Assert.Equal(GalleryPlaybackCatalog.MediaFocus, editor.GalleryMediaFocusLabel);
                Assert.Equal(GalleryPlaybackCatalog.OverlayControls, editor.GalleryOverlayControlsLabel);
                Assert.Equal(editor.GalleryOverlayControlsLabel, editor.GalleryAutoHideControlsLabel);
                Assert.Equal(GalleryPlaybackCatalog.OverlayPostContext, editor.GalleryOverlayPostContextLabel);
                Assert.Equal(GalleryPlaybackCatalog.OverlayHelp, editor.GalleryOverlayHelp);
                Assert.Equal(GalleryPlaybackCatalog.ProfileHelp, editor.GalleryPlaybackProfileHelp);
            }
        }

        // The label guard must not suppress real input after the presentation refresh.
        editor.SelectedGalleryPlaybackMode = editor.GalleryPlaybackModeChoices.Single(choice => choice.Value == "Repeat");
        Assert.Equal(38L, Get<long>(editor, "_draftRevision"));
        Assert.True(wait.IsCancellationRequested);
        Assert.Null(Get<DownloadDestinationStatus?>(editor, "_destinationStatus"));
    }

    private static void RefreshChoiceLabels(SettingsEditor editor) =>
        typeof(SettingsEditor).GetMethod("RefreshChoiceLabels", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, null);

    private static void Set(SettingsEditor editor, string name, object? value) =>
        typeof(SettingsEditor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);

    private static T Get<T>(SettingsEditor editor, string name) =>
        (T)typeof(SettingsEditor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
}
