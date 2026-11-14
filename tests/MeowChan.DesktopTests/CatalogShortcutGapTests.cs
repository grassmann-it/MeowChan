using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Commands that had no catalog shortcut (or only a hard-wired key) now own one, on every platform, without conflicts.</summary>
[Collection("Localization")]
public sealed class CatalogShortcutGapTests
{
    // Shortcut display strings are localized; the language is process-global, so run serially with the other language tests, in English.
    public CatalogShortcutGapTests() => Texts.SetLanguage("en");

    public static IEnumerable<object[]> Cases()
    {
        foreach (var platform in new[] { ShortcutPlatform.Linux, ShortcutPlatform.Windows, ShortcutPlatform.MacOS })
        {
            bool mac = platform == ShortcutPlatform.MacOS;
            yield return new object[] { platform, "media.select-all", mac ? "⌘A" : "Ctrl+A" };
            yield return new object[] { platform, "media.focus-mode", mac ? "⇧F" : "Shift+F" };
            yield return new object[] { platform, "thread.favorite-toggle", mac ? "⌘D" : "Ctrl+D" };
            yield return new object[] { platform, "thread.tags", mac ? "⌘T" : "Ctrl+T" };
            yield return new object[] { platform, "media.tags.edit", mac ? "⇧⌘T" : "Ctrl+Shift+T" };
            yield return new object[] { platform, "archive.filters-toggle", mac ? "⇧⌘F" : "Ctrl+Shift+F" };
            yield return new object[] { platform, "board.filters", mac ? "⇧⌘F" : "Ctrl+Shift+F" };
            yield return new object[] { platform, "menu.context-open", mac ? "⇧F10 (Fn) / Menu" : "Shift+F10 / Menu" };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCommandOwnsItsDefaultAndTheDefaultTableStaysConflictFree(ShortcutPlatform platform, string id, string display)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Equal(display, bindings.Display(id));
        Assert.Empty(bindings.Issues);
        foreach (var gesture in bindings.For(id)) Assert.Null(EffectiveShortcutBindings.InvalidReason(gesture, platform));
    }

    [Fact]
    public void TheMediaPageSelectAllIsACatalogCommandThatMatchesTheOldHardWiredKey()
    {
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Control }, "media.select-all"));
        Assert.Equal(new[] { "media" }, CommandCatalog.Get("media.select-all").ExecutionContexts);
        var mac = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        Assert.True(mac.Matches(new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Meta }, "media.select-all"));
        Assert.False(mac.Matches(new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Control }, "media.select-all"));
    }

    [Fact]
    public void TagsKeepThreadsAndMediaApartAndTheMenuKeyIsAGestureNextToShiftF10()
    {
        // Two gestures, two targets: Ctrl+T tags the thread in Archive and the large reader; Ctrl+Shift+T tags the tile or selection in
        // Media and the shown medium in the gallery (the reader never reads Ctrl+T as a media shortcut).
        Assert.Equal(new[] { "archive", "reader" }, CommandCatalog.Get("thread.tags").ExecutionContexts);
        Assert.Equal(new[] { "media", "gallery" }, CommandCatalog.Get("media.tags.edit").ExecutionContexts);
        // The catalog scope now says what the contexts and the help already claimed: the gallery tags the shown medium with it.
        Assert.Equal(CommandScope.Media | CommandScope.Reader | CommandScope.Gallery, CommandCatalog.Get("media.tags.edit").Scope);
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        var ctrlT = new KeyEventArgs { Key = Key.T, KeyModifiers = KeyModifiers.Control };
        var ctrlShiftT = new KeyEventArgs { Key = Key.T, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift };
        Assert.True(bindings.Matches(ctrlT, "thread.tags"));
        Assert.False(bindings.Matches(ctrlT, "media.tags.edit"));
        Assert.True(bindings.Matches(ctrlShiftT, "media.tags.edit"));
        Assert.False(bindings.Matches(ctrlShiftT, "thread.tags"));
        var mac = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        Assert.True(mac.Matches(new KeyEventArgs { Key = Key.T, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift }, "media.tags.edit"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Apps }, "menu.context-open"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.F10, KeyModifiers = KeyModifiers.Shift }, "menu.context-open"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.Apps, KeyModifiers = KeyModifiers.Shift }, "menu.context-open"));
    }

    [Fact]
    public void ABoardFiltersBindingSavedBeforeTheMoveKeepsItsKeyAndTheNewKeyGoesToTheArchiveToggle()
    {
        var section = new ShortcutBindingsSection().Set("board.filters", [ShortcutGesture.Parse("Primary+Shift+R")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+Shift+R", bindings.Display("board.filters"));
        Assert.Equal("Ctrl+Shift+F", bindings.Display("archive.filters-toggle"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Fact]
    public void ASavedBindingOnTheTagsOrFavoriteKeyWinsOverTheNewDefault()
    {
        var section = new ShortcutBindingsSection()
            .Set("navigation.archive", [ShortcutGesture.Parse("Primary+T")])
            .Set("navigation.statistics", [ShortcutGesture.Parse("Primary+Shift+T")])
            .Set("input.focus-search", [ShortcutGesture.Parse("Primary+D")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Empty(bindings.For("thread.tags"));
        Assert.Empty(bindings.For("media.tags.edit"));
        Assert.Empty(bindings.For("thread.favorite-toggle"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        // Only the saved key blocks its own default: a profile that never used Ctrl+Shift+T gets the media tags there.
        var other = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("navigation.archive", [ShortcutGesture.Parse("Primary+T")]), ShortcutPlatform.Linux);
        Assert.Empty(other.For("thread.tags"));
        Assert.Equal("Ctrl+Shift+T", other.Display("media.tags.edit"));
    }
}
