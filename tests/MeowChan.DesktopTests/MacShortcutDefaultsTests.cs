using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>macOS gets its own defaults (no Fn as the only way, ⌘ main modifier) and Apple's display; Windows and Linux are unchanged.</summary>
public sealed class MacShortcutDefaultsTests
{
    [Theory]
    [InlineData("Primary+Shift+P", "⇧⌘P")]
    [InlineData("Control+Primary+F", "⌃⌘F")]
    [InlineData("Primary+Back", "⌘⌫")]
    [InlineData("Primary+Shift+Back", "⇧⌘⌫")]
    [InlineData("Primary+Alt+Shift+R", "⌥⇧⌘R")]
    [InlineData("Primary+Alt+Left", "⌥⌘←")]
    [InlineData("Primary+,", "⌘,")]
    [InlineData("Primary+3", "⌘3")]
    [InlineData("Alt+Enter", "⌥↩")]
    [InlineData("Alt+PageDown", "⌥⇟")]
    [InlineData("Shift+F5", "⇧F5 (Fn)")]
    [InlineData("F11", "F11 (Fn)")]
    [InlineData("Space", "␣")]
    [InlineData("T", "T")]
    public void MacShowsAppleGlyphsInAppleOrderWithoutPlus(string gesture, string mac) =>
        Assert.Equal(mac, ShortcutGesture.Parse(gesture).Display(ShortcutPlatform.MacOS));

    [Theory]
    [InlineData("Primary+Shift+P", "Ctrl+Shift+P")]
    [InlineData("Primary+,", "Ctrl+,")]
    [InlineData("Primary+3", "Ctrl+3")]
    [InlineData("Alt+Enter", "Alt+Enter")]
    [InlineData("Shift+F5", "Shift+F5")]
    [InlineData("F11", "F11")]
    public void WindowsAndLinuxKeepTheirTextNames(string gesture, string expected)
    {
        Assert.Equal(expected, ShortcutGesture.Parse(gesture).Display(ShortcutPlatform.Windows));
        Assert.Equal(expected, ShortcutGesture.Parse(gesture).Display(ShortcutPlatform.Linux));
    }

    [Fact]
    public void TheHelpGestureShowsItsQuestionMarkWithoutAnExtraShiftGlyph()
    {
        var help = EffectiveShortcutBindings.Defaults(CommandCatalog.Get("viewer.shortcut-help"), ShortcutPlatform.MacOS);
        Assert.Equal(new[] { "⌘?", "?", "F1 (Fn)" }, help.Select(g => g.Display(ShortcutPlatform.MacOS)));
        Assert.Equal("F1 / Shift+?", string.Join(" / ", EffectiveShortcutBindings.Defaults(CommandCatalog.Get("viewer.shortcut-help"), ShortcutPlatform.Linux)
            .Select(g => g.Display(ShortcutPlatform.Linux))));
    }

    [Theory]
    [InlineData("window.fullscreen", "⌃⌘F / F11 (Fn)", "F11")]
    [InlineData("watch.remove", "⌘⌫ / ⌦", "Delete")]
    [InlineData("archive.delete-permanently", "⇧⌘⌫ / ⇧⌦", "Shift+Delete")]
    [InlineData("thread.details", "⌘I / ⌥↩", "Alt+Enter")]
    [InlineData("watch.check", "⌥⌘R / F5 (Fn)", "F5")]
    [InlineData("watch.retry-media", "⌥⇧⌘R / ⇧F5 (Fn)", "Shift+F5")]
    [InlineData("archive.previous-page", "⌥⌘← / ⌥⇞", "Alt+PageUp")]
    [InlineData("archive.next-page", "⌥⌘→ / ⌥⇟", "Alt+PageDown")]
    [InlineData("media.return", "⌘←", "Alt+Left")]
    public void MacStandardsDifferFromWindowsAndLinuxWhichKeepTheirOwn(string id, string mac, string other)
    {
        Assert.Equal(mac, EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS).Display(id));
        Assert.Equal(other, EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux).Display(id));
        Assert.Equal(other, EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Windows).Display(id));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void EveryPlatformsDefaultTableIsValidAndFreeOfConflicts(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        foreach (var command in CommandCatalog.All)
            foreach (var gesture in EffectiveShortcutBindings.Defaults(command, platform))
                Assert.True(EffectiveShortcutBindings.InvalidReason(gesture, platform) == null, command.Id + " " + gesture.Display(platform));
    }

    [Fact]
    public void NoMacCommandDependsOnFnAsItsOnlyWay()
    {
        // F-keys, forward Delete, PageUp/PageDown and Home/End need Fn on an Apple keyboard; they may only be extras.
        static bool NeedsFn(ShortcutGesture g) => g.Key is "Delete" or "PageUp" or "PageDown" or "Home" or "End" or "Apps"
            || g.Key.Length >= 2 && g.Key[0] == 'F' && char.IsAsciiDigit(g.Key[1]);
        var onlyFn = CommandCatalog.All.Where(c => c.IsImplemented)
            .Where(c => EffectiveShortcutBindings.Defaults(c, ShortcutPlatform.MacOS) is { Count: > 0 } list && list.All(NeedsFn))
            .Select(c => c.Id).ToArray();
        // The context menu has no ⌘ standard on macOS (Control-click); Shift+F10 and the Menu key are extras there.
        Assert.Equal(new[] { "menu.context-open" }, onlyFn);
    }

    [Fact]
    public void MacRoutesTheMacGesturesToTheirCommandsAndNotTheWindowsOnes()
    {
        var mac = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        Assert.True(mac.Matches(new KeyEventArgs { Key = Key.Back, KeyModifiers = KeyModifiers.Meta }, "watch.remove"));
        // A bare Backspace (deleting a character after leaving a text field) never removes watches by default.
        Assert.False(mac.Matches(new KeyEventArgs { Key = Key.Back }, "watch.remove"));
        Assert.DoesNotContain(mac.For("watch.remove"), gesture => gesture.Key == "Back" && gesture.Modifiers.Count == 0);
        Assert.True(mac.Matches(new KeyEventArgs { Key = Key.Back, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift }, "archive.delete-permanently"));
        Assert.True(mac.Matches(new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Control }, "window.fullscreen"));
        Assert.True(mac.Matches(new KeyEventArgs { Key = Key.Left, KeyModifiers = KeyModifiers.Meta }, "media.return"));
        Assert.False(mac.Matches(new KeyEventArgs { Key = Key.Left, KeyModifiers = KeyModifiers.Alt }, "media.return"));
        Assert.True(mac.Matches(new KeyEventArgs { Key = Key.R, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Alt }, "watch.check"));
        Assert.True(mac.Matches(new KeyEventArgs { Key = Key.R, KeyModifiers = KeyModifiers.Meta }, "rules.edit-scope"));
        var linux = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.False(linux.Matches(new KeyEventArgs { Key = Key.Back }, "watch.remove"));
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.Left, KeyModifiers = KeyModifiers.Alt }, "media.return"));
    }

    [Fact]
    public void ASavedBareBackspaceRemovalOnMacIsAUserBindingAndKeepsWorking()
    {
        var section = new ShortcutBindingsSection().Set("watch.remove", [ShortcutGesture.Parse("Back")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.MacOS);
        Assert.Equal("⌫", bindings.Display("watch.remove"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Back }, "watch.remove"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.Back, KeyModifiers = KeyModifiers.Meta }, "watch.remove"));
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Fact]
    public void ASavedWindowsStyleBindingKeepsWinningOnMac()
    {
        var section = new ShortcutBindingsSection()
            .Set("watch.remove", [ShortcutGesture.Parse("Delete")])
            .Set("navigation.archive", [ShortcutGesture.Parse("Primary+Back")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.MacOS);
        Assert.Equal("⌦", bindings.Display("watch.remove"));
        Assert.Equal("⌘⌫", bindings.Display("navigation.archive"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Equal(before, section.Raw.GetRawText());
    }
}
