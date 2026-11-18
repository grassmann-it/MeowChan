using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>One Back gesture and one page gesture for commands that never apply at the same time.</summary>
[Collection("Localization")]
public sealed class UnifiedBackAndPageShortcutTests
{
    // Shortcut display strings are localized; the language is process-global, so run serially with the other language tests, in English.
    public UnifiedBackAndPageShortcutTests() => Texts.SetLanguage("en");

    private static readonly string[] Back = { "media.return", "reader.history-back", "gallery.return" };

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Alt+Left")]
    [InlineData(ShortcutPlatform.Windows, "Alt+Left")]
    [InlineData(ShortcutPlatform.MacOS, "⌘←")]
    public void ThreeBackCommandsShareOneGesture(ShortcutPlatform platform, string display)
    {
        var built = EffectiveShortcutBindings.Build(null, platform);
        foreach (string id in Back) Assert.Equal(display, built.Display(id));
        Assert.Empty(built.Issues);
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Alt+PageUp", "Alt+PageDown")]
    [InlineData(ShortcutPlatform.Windows, "Alt+PageUp", "Alt+PageDown")]
    [InlineData(ShortcutPlatform.MacOS, "⌥⌘← / ⌥⇞", "⌥⌘→ / ⌥⇟")]
    public void ResultsPagesAndPostPagesShareOnePageGesture(ShortcutPlatform platform, string previous, string next)
    {
        var built = EffectiveShortcutBindings.Build(null, platform);
        Assert.Equal(previous, built.Display("archive.previous-page"));
        Assert.Equal(previous, built.Display("pagination.previous"));
        Assert.Equal(next, built.Display("archive.next-page"));
        Assert.Equal(next, built.Display("pagination.next"));
        Assert.Empty(built.Issues);
    }

    [Fact]
    public void TheSharedGesturesStayConflictFreeBecauseTheirExecutionContextsDiffer()
    {
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("media.return", "reader.history-back"));
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("media.return", "gallery.return"));
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("reader.history-back", "gallery.return"));
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("archive.previous-page", "pagination.previous"));
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("archive.next-page", "pagination.next"));
    }

    [Fact]
    public void AReboundBackCommandKeepsOnlyItsOwnGesture()
    {
        var section = new ShortcutBindingsSection().Set("gallery.return", [ShortcutGesture.Parse("Primary+Shift+K")]);
        var built = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+Shift+K", built.Display("gallery.return"));
        Assert.Equal("Alt+Left", built.Display("media.return"));
        Assert.Equal("Alt+Left", built.Display("reader.history-back"));
    }
}
