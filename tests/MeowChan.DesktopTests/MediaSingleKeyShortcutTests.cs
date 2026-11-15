using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Slideshow D (revision 13; it was S), details I, rating R, export Ctrl+E and folder Ctrl+O in the gallery and the media page.</summary>
[Collection("Localization")]
public sealed class MediaSingleKeyShortcutTests
{
    // Shortcut display strings are localized; the language is process-global, so run serially with the other language tests, in English.
    public MediaSingleKeyShortcutTests() => Texts.SetLanguage("en");

    [Theory]
    [InlineData("gallery.slideshow-toggle", "D")]
    [InlineData("media.details", "I")]
    [InlineData("media.rating.edit", "R")]
    [InlineData("media.export-original", "Ctrl+E")]
    [InlineData("media.show-in-folder", "Ctrl+O")]
    public void LinuxAndWindowsDefaults(string id, string display)
    {
        foreach (var platform in new[] { ShortcutPlatform.Linux, ShortcutPlatform.Windows })
            Assert.Equal(display, EffectiveShortcutBindings.Build(null, platform).Display(id));
    }

    [Theory]
    [InlineData("media.export-original", "⌘E")]
    [InlineData("media.show-in-folder", "⌘O")]
    [InlineData("media.details", "I / ⌘I")]
    public void MacUsesCommandWhereTheNoteSaysSo(string id, string display) =>
        Assert.Equal(display, EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS).Display(id));

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void TheNewDefaultsRaiseNoConflictWithThreadAndDownloadsFolderOrExport(ShortcutPlatform platform)
    {
        var built = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(built.Issues);
        // Ctrl+O reads as the thread folder in the archive and downloads, and as the medium's folder in media and gallery.
        Assert.Equal(built.Display("location.open-folder"), built.Display("media.show-in-folder"));
        Assert.DoesNotContain("media", EffectiveShortcutBindings.ExecutionContexts("location.open-folder"));
        Assert.Contains("media", EffectiveShortcutBindings.ExecutionContexts("media.show-in-folder"));
        Assert.Contains("gallery", EffectiveShortcutBindings.ExecutionContexts("media.show-in-folder"));
    }

    [Fact]
    public void TheSingleKeysFollowTheSingleKeySwitchAndTheChordsDoNot()
    {
        var section = new ShortcutBindingsSection().WithSingleKeyShortcuts(false);
        var built = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        foreach (string id in new[] { "gallery.slideshow-toggle", "media.details", "media.rating.edit" })
            Assert.Empty(built.For(id));
        Assert.Equal("Ctrl+E", built.Display("media.export-original"));
        Assert.Equal("Ctrl+O", built.Display("media.show-in-folder"));
    }

    [Fact]
    public void ADefaultNeverSilencesASavedBindingOfAnotherCommand()
    {
        foreach (string id in new[] { "gallery.slideshow-toggle", "media.details", "media.rating.edit", "media.export-original", "media.show-in-folder" })
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }
}
