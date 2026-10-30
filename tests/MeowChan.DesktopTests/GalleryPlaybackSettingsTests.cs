using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class GalleryPlaybackSettingsTests : IDisposable
{
    public GalleryPlaybackSettingsTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("300", 300)]
    public void SlideshowDurationAcceptsOnlyTheConfiguredWholeSecondRange(string raw, int expected)
    {
        Assert.True(SettingsEditor.TryParseGalleryImageSeconds(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("0")]
    [InlineData("301")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999999999999999999999999")]
    public void RejectedRawDurationNeverFallsBackToAPreviouslyValidNumericValue(string? raw)
    {
        Assert.False(SettingsEditor.TryParseGalleryImageSeconds(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out int value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void DurationParserHonorsTheControlsNumberFormatWithoutAcceptingFractionalSeconds()
    {
        Assert.False(SettingsEditor.TryParseGalleryImageSeconds("1,5", NumberStyles.Number, CultureInfo.GetCultureInfo("de"), out _));
        Assert.True(SettingsEditor.TryParseGalleryImageSeconds("5,0", NumberStyles.Number, CultureInfo.GetCultureInfo("de"), out int value));
        Assert.Equal(5, value);
    }

    [Theory]
    [InlineData("en", "one choice", "bottom edge", "independent choices", "platform and renderer")]
    [InlineData("de", "eine gemeinsame Option", "unteren Rand", "unabhängige Optionen", "Plattform und Renderer")]
    public void OverlayHelpExplainsPairedEdgeControlsIndependentContextAndPlatformSupport(string language, string placement, string autoHide, string context, string platform)
    {
        Texts.SetLanguage(language);
        Assert.Contains(placement, GalleryPlaybackCatalog.OverlayHelp);
        Assert.Contains(autoHide, GalleryPlaybackCatalog.OverlayHelp);
        Assert.Contains(context, GalleryPlaybackCatalog.OverlayHelp);
        Assert.Contains(platform, GalleryPlaybackCatalog.OverlayHelp);
    }

    [Fact]
    public void EitherSettingsAliasUpdatesBothValuesBeforeNotifyingBindings()
    {
        // Test just the CLR binding properties without bootstrapping an Avalonia window.
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        var notifications = new List<string>();
        editor.PropertyChanged += (_, e) =>
        {
            Assert.Equal(editor.GalleryOverlayControls, editor.GalleryAutoHideControls);
            notifications.Add(e.PropertyName!);
        };
        editor.GalleryMediaFocus = true;
        editor.GalleryOverlayPostContext = true;
        editor.VideoVolume = 29;
        editor.VideoStartMuted = true;
        foreach (bool useLegacyAlias in new[] { false, true })
        foreach (bool enabled in new[] { true, false })
        {
            notifications.Clear();
            if (useLegacyAlias) editor.GalleryAutoHideControls = enabled;
            else editor.GalleryOverlayControls = enabled;
            Assert.Equal(enabled, editor.GalleryOverlayControls);
            Assert.Equal(enabled, editor.GalleryAutoHideControls);
            Assert.Contains(nameof(editor.GalleryOverlayControls), notifications);
            Assert.Contains(nameof(editor.GalleryAutoHideControls), notifications);
            Assert.True(editor.GalleryMediaFocus);
            Assert.True(editor.GalleryOverlayPostContext);
            Assert.Equal(29, editor.VideoVolume);
            Assert.True(editor.VideoStartMuted);
        }
    }

    [Fact]
    public void SettingsMarkupExposesOnePairedSwitchAndKeepsLegacyBindingHidden()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var controls = markup.Descendants().Where(element => element.Name.LocalName == "CheckBox")
            .ToDictionary(element => (string?)element.Attribute(x + "Name") ?? Guid.NewGuid().ToString());
        XElement visible = controls["GalleryOverlayControlsInput"];
        Assert.Null(visible.Attribute("IsVisible"));
        Assert.Equal("{Binding GalleryOverlayControls, Mode=TwoWay}", (string?)visible.Attribute("IsChecked"));
        Assert.NotNull(visible.Attribute("AutomationProperties.HelpText"));
        XElement legacy = controls["GalleryAutoHideControlsInput"];
        Assert.Equal("False", (string?)legacy.Attribute("IsVisible"));
        Assert.Equal("False", (string?)legacy.Attribute("IsTabStop"));
        Assert.Equal("{Binding GalleryAutoHideControls, Mode=TwoWay}", (string?)legacy.Attribute("IsChecked"));
        Assert.Null(controls["GalleryOverlayPostContextInput"].Attribute("IsVisible"));
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));

    [Theory]
    [InlineData("en", "Gallery & playback", "Manual", "Repeat current video")]
    [InlineData("de", "Galerie & Wiedergabe", "Manuell", "Aktuelles Video wiederholen")]
    public void EveryAddedLabelHasCompleteEnglishAndGermanText(string language, string title, string manual, string repeat)
    {
        Texts.SetLanguage(language);
        Assert.Equal(title, GalleryPlaybackCatalog.Title);
        Assert.Equal(manual, GalleryPlaybackCatalog.Manual);
        Assert.Equal(repeat, GalleryPlaybackCatalog.Repeat);
        Assert.Equal(GalleryPlaybackCatalog.OverlayControls, GalleryPlaybackCatalog.AutoHideControls);
        Assert.Contains(language == "de" ? "automatisch ausblenden" : "Auto-hide", GalleryPlaybackCatalog.OverlayControls);
        Assert.Contains(language == "de" ? "unteren Rand" : "bottom edge", GalleryPlaybackCatalog.OverlayControls);
        string[] labels = [GalleryPlaybackCatalog.Title, GalleryPlaybackCatalog.Mode, GalleryPlaybackCatalog.Manual,
            GalleryPlaybackCatalog.Repeat, GalleryPlaybackCatalog.Slideshow, GalleryPlaybackCatalog.ImageSeconds,
            GalleryPlaybackCatalog.ImageSecondsHelp, GalleryPlaybackCatalog.ModeHelp, GalleryPlaybackCatalog.MediaFocus,
            GalleryPlaybackCatalog.OverlayControls, GalleryPlaybackCatalog.AutoHideControls, GalleryPlaybackCatalog.OverlayPostContext,
            GalleryPlaybackCatalog.OverlayHelp, GalleryPlaybackCatalog.ProfileHelp];
        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Contains("300", GalleryPlaybackCatalog.ImageSecondsHelp);
        Assert.Contains(language == "de" ? "keine Diashow" : "never starts a slideshow", GalleryPlaybackCatalog.ModeHelp);
        Assert.Contains(language == "de" ? "Stummstart" : "start-muted", GalleryPlaybackCatalog.ProfileHelp);
    }
}
