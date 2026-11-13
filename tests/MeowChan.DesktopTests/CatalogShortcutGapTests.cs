using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>Commands that had no catalog shortcut (or only a hard-wired key) now own one, on every platform, without conflicts.</summary>
public sealed class CatalogShortcutGapTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var platform in new[] { ShortcutPlatform.Linux, ShortcutPlatform.Windows, ShortcutPlatform.MacOS })
        {
            string primary = platform == ShortcutPlatform.MacOS ? "⌘" : "Ctrl";
            yield return new object[] { platform, "media.select-all", primary + "+A" };
            yield return new object[] { platform, "thread.favorite-toggle", primary + "+D" };
            yield return new object[] { platform, "thread.tags", primary + "+T" };
            yield return new object[] { platform, "media.tags.edit", primary + "+T" };
            yield return new object[] { platform, "archive.filters-toggle", primary + "+Shift+F" };
            yield return new object[] { platform, "board.filters", primary + "+Shift+F" };
            yield return new object[] { platform, "menu.context-open", "Shift+F10 / Menu" };
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
        // One gesture, different targets: the thread in Archive and the large reader, the tile or selection in Media, the shown medium in the gallery.
        Assert.Equal(new[] { "archive", "reader" }, CommandCatalog.Get("thread.tags").ExecutionContexts);
        Assert.Equal(new[] { "media", "gallery" }, CommandCatalog.Get("media.tags.edit").ExecutionContexts);
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
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
            .Set("input.focus-search", [ShortcutGesture.Parse("Primary+D")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Empty(bindings.For("thread.tags"));
        Assert.Empty(bindings.For("media.tags.edit"));
        Assert.Empty(bindings.For("thread.favorite-toggle"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }
}
