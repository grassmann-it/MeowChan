using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class PowerUserCommandTests
{
    [Theory]
    [InlineData("navigation.downloads", "1")]
    [InlineData("navigation.archive", "2")]
    [InlineData("navigation.rules", "3")]
    [InlineData("navigation.sources", "4")]
    [InlineData("navigation.diagnostics", "5")]
    [InlineData("session.pause", "Shift+P")]
    public void PlatformGesturesUseTheDeclaredShortcut(string id, string suffix)
    {
        Assert.Equal("Ctrl+" + suffix, CommandCatalog.ShortcutDisplay(id, false));
        Assert.Equal("⌘+" + suffix, CommandCatalog.ShortcutDisplay(id, true));
        KeyGesture gesture = CommandBindings.Gesture(id)!;
        Assert.Equal(suffix switch { "1" => Key.D1, "2" => Key.D2, "3" => Key.D3, "4" => Key.D4, "5" => Key.D5, _ => Key.P }, gesture.Key);
        Assert.True(CommandBindings.Matches(new KeyEventArgs { Key = gesture.Key, KeyModifiers = gesture.KeyModifiers }, id));
        Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = gesture.Key, KeyModifiers = KeyModifiers.None }, id));
    }

    [Fact]
    public void ContextGesturesKeepDistinctActionsAndScopes()
    {
        Assert.Equal("P", CommandCatalog.Get("gallery.context-toggle").Shortcut);
        // P stays the context toggle (V-06); the jump into the thread has its own key since MEDIA103-NAV (user decision 11.11.2026).
        Assert.Equal("T", CommandCatalog.Get("gallery.show-in-thread").Shortcut);
        Assert.True(CommandBindings.Matches(new KeyEventArgs { Key = Key.T }, "gallery.show-in-thread"));
        Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.P }, "gallery.show-in-thread"));
        Assert.Equal(CommandScope.Reader, CommandCatalog.Get("media.return").Scope);
        Assert.Equal("Alt+Left", CommandCatalog.ShortcutDisplay("media.return", false));
        Assert.True(CommandBindings.Matches(new KeyEventArgs { Key = Key.Left, KeyModifiers = KeyModifiers.Alt }, "media.return"));
        Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.Left }, "media.return"));
        Assert.NotEqual(CommandCatalog.Get("media.return").Shortcut, CommandCatalog.Get("reader.history-back").Shortcut);
        Assert.Equal(CommandScope.Downloads, CommandCatalog.Get("input.focus-download-search").Scope);
        Assert.Equal(CommandScope.Archive, CommandCatalog.Get("archive.previous-page").Scope);
        Assert.Equal(CommandScope.Archive, CommandCatalog.Get("archive.next-page").Scope);
        Assert.Equal("Alt+PageUp", CommandCatalog.ShortcutDisplay("archive.previous-page", true));
        Assert.Equal("Alt+PageDown", CommandCatalog.ShortcutDisplay("archive.next-page", false));
        Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.PageDown }, "archive.next-page"));
        Assert.Null(CommandCatalog.Get("export.open-all-results-html-dialog").Shortcut);
        Assert.NotNull(CommandCatalog.ScopeHint("export.open-html-dialog", CommandScope.Reader));
        Assert.Null(CommandCatalog.ScopeHint("export.open-html-dialog", CommandScope.Archive));
    }
}
