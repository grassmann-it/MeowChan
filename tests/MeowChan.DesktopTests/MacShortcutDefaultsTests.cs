using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>macOS gets its own defaults (no Fn as the only way, ⌘ main modifier) and Apple's display; Windows and Linux are unchanged.</summary>
[Collection("Localization")]
public sealed class MacShortcutDefaultsTests
{
    // Shortcut display strings are localized; the language is process-global, so run serially with the other language tests, in English.
    public MacShortcutDefaultsTests() => Texts.SetLanguage("en");

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
    [InlineData("input.focus-download-search", "⌘F", "Ctrl+F")]
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

    // ---- macOS system keys the editor refuses (sweep S3, 16.11.2026) ----

    /// <summary>What macOS takes before any application: hide, quit, log out, lock, the Dock, the screenshots, Spaces / Mission Control and the input source.</summary>
    public static readonly string[] MacSystemGestures =
    {
        "Primary+H", "Primary+Alt+H", "Primary+Q", "Primary+Shift+Q", "Control+Primary+Q", "Primary+Alt+D",
        "Primary+Shift+3", "Primary+Shift+4", "Primary+Shift+5",
        "Control+Left", "Control+Right", "Control+Up", "Control+Down", "Control+Space",
    };

    /// <summary>Keys that do reach a Mac application, among them every Mac default that looks close to a system key.</summary>
    public static readonly string[] MacApplicationGestures =
    {
        "Primary+M", "Primary+W", "Primary+,", "Control+Primary+F", "Primary+Shift+M", "Primary+Shift+H", "Primary+Alt+R", "Primary+Left",
        "Primary+Shift+Left", "Control+Shift+Left", "Alt+Left", "Primary+Shift+6", "Primary+3", "Shift+3", "Control+H", "Control+Q",
    };

    public static IEnumerable<object[]> MacSystem() => MacSystemGestures.Select(gesture => new object[] { gesture });
    public static IEnumerable<object[]> MacApplication() => MacApplicationGestures.Select(gesture => new object[] { gesture });

    [Theory, MemberData(nameof(MacSystem))]
    public void TheEditorRefusesAMacSystemKeyWithTheMacReasonOnMacOnly(string text)
    {
        ShortcutGesture gesture = ShortcutGesture.Parse(text);
        ShortcutIssueReason reason = EffectiveShortcutBindings.InvalidIssueReason(gesture, ShortcutPlatform.MacOS)!;
        Assert.Equal("reservedMacSystem", reason.Code);
        Assert.Equal("Reserved macOS system shortcut / Reserviertes macOS-Systemkürzel", EffectiveShortcutBindings.InvalidReason(gesture, ShortcutPlatform.MacOS));
        // A saved binding on it is the same blocking issue as any other invalid gesture: reported, never active, the saved profile untouched.
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("navigation.archive", [gesture]);
        string before = section.Raw.GetRawText();
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.MacOS);
        ShortcutIssue issue = Assert.Single(bindings.Issues, i => i.CommandId == "navigation.archive" && i.BlocksSave);
        Assert.Equal("reservedMacSystem", issue.Reason!.Code);
        Assert.Empty(bindings.For("navigation.archive"));
        Assert.Equal(before, section.Raw.GetRawText());
        Assert.Equal(Texts.T("shortcuts.issue.reservedMacSystem"), ShortcutIssueCatalog.Render(issue));
        // Windows and Linux never use the Mac reason (there Primary is Ctrl; Ctrl+arrows and Ctrl+Space are ordinary gestures).
        foreach (ShortcutPlatform other in new[] { ShortcutPlatform.Windows, ShortcutPlatform.Linux })
            Assert.NotEqual("reservedMacSystem", EffectiveShortcutBindings.InvalidIssueReason(gesture, other)?.Code);
    }

    [Theory, MemberData(nameof(MacApplication))]
    public void AKeyThatReachesAMacApplicationStaysAllowed(string text) =>
        Assert.Null(EffectiveShortcutBindings.InvalidIssueReason(ShortcutGesture.Parse(text), ShortcutPlatform.MacOS));

    [Fact]
    public void NoMacDefaultIsAMacSystemKeyAndTheMacReasonHasItsOwnCatalogKey()
    {
        foreach (CommandDefinition command in CommandCatalog.All)
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(command, ShortcutPlatform.MacOS))
            {
                var resolved = gesture.Resolve(ShortcutPlatform.MacOS);
                Assert.False(EffectiveShortcutBindings.IsMacSystemKey(resolved.Key, resolved.KeyModifiers), command.Id + " " + gesture.Display(ShortcutPlatform.MacOS));
            }
        Assert.Contains(ShortcutIssueCatalog.Entries, entry => entry.Key == "shortcuts.issue.reservedMacSystem" && entry.English.Contains("macOS") && entry.German.Contains("macOS"));
    }
}
