using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class PowerUserCommandTests
{
    [Theory]
    [InlineData("navigation.downloads", "1")]
    [InlineData("navigation.archive", "2")]
    [InlineData("navigation.media", "3")]
    [InlineData("navigation.booru", "8")]
    [InlineData("navigation.rules", "5")]
    [InlineData("navigation.sources", "6")]
    [InlineData("navigation.tagging", "4")]
    [InlineData("navigation.plugins", null)]
    [InlineData("navigation.status", "7")]
    [InlineData("session.pause", "Shift+P")]
    public void PlatformGesturesUseTheDeclaredShortcut(string id, string? suffix)
    {
        if (suffix is null)
        {
            // The compatibility command remains bindable, but has no default on either platform.
            Assert.Null(CommandCatalog.Get(id).Shortcut);
            Assert.Null(CommandCatalog.ShortcutDisplay(id, false));
            Assert.Null(CommandCatalog.ShortcutDisplay(id, true));
            Assert.Null(CommandBindings.Gesture(id));
            Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.D6, KeyModifiers = KeyModifiers.Control }, id));
            Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.D6, KeyModifiers = KeyModifiers.Meta }, id));
            return;
        }
        Assert.Equal("Ctrl+" + suffix, CommandCatalog.ShortcutDisplay(id, false));
        // macOS shows Apple glyphs in Apple's order without "+": ⌘3, ⇧⌘P.
        Assert.Equal(suffix == "Shift+P" ? "⇧⌘P" : "⌘" + suffix, CommandCatalog.ShortcutDisplay(id, true));
        KeyGesture gesture = CommandBindings.Gesture(id)!;
        Assert.Equal(suffix switch { "1" => Key.D1, "2" => Key.D2, "3" => Key.D3, "4" => Key.D4, "5" => Key.D5, "6" => Key.D6, "7" => Key.D7, "8" => Key.D8, _ => Key.P }, gesture.Key);
        Assert.True(CommandBindings.Matches(new KeyEventArgs { Key = gesture.Key, KeyModifiers = gesture.KeyModifiers }, id));
        Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = gesture.Key, KeyModifiers = KeyModifiers.None }, id));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void ASavedPluginShortcutKeepsItsSlotAndYieldsTheNewBooruDefault(ShortcutPlatform platform)
    {
        var gesture = ShortcutGesture.Parse(MainPageOrder.DefaultShortcut("navigation.booru")); // slot 6 before F1, now the footer digit
        var defaults = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(defaults.For("navigation.plugins"));
        Assert.Contains(defaults.For("navigation.booru"), candidate =>
            EffectiveShortcutBindings.Equivalent(gesture, candidate, platform));

        var section = new ShortcutBindingsSection().Set("navigation.plugins", [gesture]);
        string before = section.Raw.GetRawText();
        var saved = EffectiveShortcutBindings.Build(section, platform);
        Assert.True(EffectiveShortcutBindings.Equivalent(gesture, Assert.Single(saved.For("navigation.plugins")), platform));
        Assert.Contains(saved.Issues, issue => issue.CommandId == "navigation.booru"
            && issue.OtherCommandId == "navigation.plugins" && issue.Reason?.Code == "defaultYields");
        Assert.Equal(before, section.Raw.GetRawText());
        Assert.Equal(MainWindow.WorkspacePage.Sources, MainWindow.PageFor("navigation.plugins"));
        Assert.DoesNotContain("navigation.plugins", MainPageOrder.CommandIds);
        Assert.Null(MainPageOrder.Digit("navigation.plugins"));
        Assert.Equal(7, MainPageOrder.Digit("navigation.status"));
    }

    [Fact]
    public void ContextGesturesKeepDistinctActionsAndScopes()
    {
        Assert.Equal("Shift+P", CommandCatalog.Get("gallery.context-toggle").Shortcut);
        // Shift+P is the context toggle since the gallery key scheme (revision 13, it was P; V-06); the jump into the thread has its own key since MEDIA103-NAV (user decision 11.11.2026).
        Assert.Equal("T", CommandCatalog.Get("gallery.show-in-thread").Shortcut);
        Assert.True(CommandBindings.Matches(new KeyEventArgs { Key = Key.T }, "gallery.show-in-thread"));
        Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.P }, "gallery.show-in-thread"));
        Assert.Equal(CommandScope.Reader, CommandCatalog.Get("media.return").Scope);
        Assert.Equal("Alt+Left", CommandCatalog.ShortcutDisplay("media.return", false));
        Assert.True(CommandBindings.Matches(new KeyEventArgs { Key = Key.Left, KeyModifiers = KeyModifiers.Alt }, "media.return"));
        Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.Left }, "media.return"));
        // One Back gesture for three commands that never apply together (their execution contexts differ).
        Assert.Equal(CommandCatalog.Get("media.return").Shortcut, CommandCatalog.Get("reader.history-back").Shortcut);
        Assert.Equal(CommandCatalog.Get("media.return").Shortcut, CommandCatalog.Get("gallery.return").Shortcut);
        Assert.Equal(CommandScope.Downloads, CommandCatalog.Get("input.focus-download-search").Scope);
        Assert.Equal(CommandScope.Archive, CommandCatalog.Get("archive.previous-page").Scope);
        Assert.Equal(CommandScope.Archive, CommandCatalog.Get("archive.next-page").Scope);
        Assert.Equal("⌥⇞", CommandCatalog.ShortcutDisplay("archive.previous-page", true));
        Assert.Equal("Alt+PageDown", CommandCatalog.ShortcutDisplay("archive.next-page", false));
        Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.PageDown }, "archive.next-page"));
        Assert.Null(CommandCatalog.Get("export.open-all-results-html-dialog").Shortcut);
        Assert.NotNull(CommandCatalog.ScopeHint("export.open-html-dialog", CommandScope.Reader));
        Assert.Null(CommandCatalog.ScopeHint("export.open-html-dialog", CommandScope.Archive));
    }
}
