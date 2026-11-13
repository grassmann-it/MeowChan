using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Find next / previous in the large reader: F3 and Ctrl+G everywhere, ⌘G on macOS.</summary>
[Collection("Localization")]
public sealed class ReaderFindShortcutTests
{
    // Shortcut display strings are localized; the language is process-global, so run serially with the other language tests, in English.
    public ReaderFindShortcutTests() => Texts.SetLanguage("en");

    [Theory]
    [InlineData(ShortcutPlatform.Windows, "reader.find-next", "F3 / Ctrl+G")]
    [InlineData(ShortcutPlatform.Linux, "reader.find-next", "F3 / Ctrl+G")]
    [InlineData(ShortcutPlatform.Windows, "reader.find-previous", "Shift+F3 / Ctrl+Shift+G")]
    [InlineData(ShortcutPlatform.MacOS, "reader.find-next", "⌘G / F3 (Fn)")]
    [InlineData(ShortcutPlatform.MacOS, "reader.find-previous", "⇧⌘G / ⇧F3 (Fn)")]
    public void DefaultsPerPlatform(ShortcutPlatform platform, string id, string expected)
    {
        var gestures = EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform);
        Assert.Equal(expected, string.Join(" / ", gestures.Select(g => g.Display(platform))));
    }

    [Fact]
    public void FindNextAndPreviousLiveInTheLargeReaderLikeFind()
    {
        foreach (string id in new[] { "reader.find", "reader.find-next", "reader.find-previous" })
            Assert.Equal(new[] { "large-reader" }, EffectiveShortcutBindings.ExecutionContexts(id));
        Assert.Contains("reader.find-next", EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.Contains("reader.find-previous", EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }
}
