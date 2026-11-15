using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Tom's decision of 15.11.2026 (defaults revision 15): Ctrl/Cmd+1 to 9 keep counting the visible footer buttons, and every main page also gets
/// a fixed Ctrl/Cmd+Shift+letter (<see cref="PageKeyScheme"/>). The invariants hold on every platform: unique letters, free in every context
/// of every other default, never Ctrl+Alt, never a letter a Mac reserves, yielding to a saved binding, and the digit stays first.
/// </summary>
[Collection("Localization")]
public sealed class PageKeySchemeTests : IDisposable
{
    private readonly string _language = Texts.Language;

    public PageKeySchemeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static readonly string[] Pages = MainPageOrder.CommandIds.ToArray();

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    [Fact]
    public void EveryMainPageHasExactlyOneDistinctUpperCaseLetterAndSettingsAndTheStatusSegmentsHaveNone()
    {
        Assert.Equal(Pages.Order(StringComparer.Ordinal), PageKeyScheme.Letters.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Pages.Length, PageKeyScheme.Letters.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PageKeyScheme.Letters.Values, letter => Assert.True(letter.Length == 1 && char.IsAsciiLetterUpper(letter[0]), letter));
        Assert.Empty(PageKeyScheme.Letters.Values.Intersect(PageKeyScheme.ReservedLetters, StringComparer.Ordinal));
        Assert.Null(PageKeyScheme.Letter("navigation.settings"));
        Assert.All(MainPageOrder.StatusSegmentCommandIds, id => Assert.Null(PageKeyScheme.Letter(id)));
        Assert.Null(PageKeyScheme.Gesture("navigation.about"));
        // The documented table.
        Assert.Equal("D", PageKeyScheme.Letter("navigation.downloads"));
        Assert.Equal("A", PageKeyScheme.Letter("navigation.archive"));
        Assert.Equal("E", PageKeyScheme.Letter("navigation.media"));
        Assert.Equal("K", PageKeyScheme.Letter("navigation.tagging"));
        Assert.Equal("B", PageKeyScheme.Letter("navigation.booru"));
        Assert.Equal("U", PageKeyScheme.Letter("navigation.rules"));
        Assert.Equal("X", PageKeyScheme.Letter("navigation.sources"));
        Assert.Equal("S", PageKeyScheme.Letter("navigation.status"));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void EveryPageDefaultIsTheFooterDigitFirstAndThenItsFixedLetter(ShortcutPlatform platform)
    {
        foreach (string id in Pages)
        {
            var defaults = EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform);
            Assert.Equal(2, defaults.Count);
            Assert.Equal("D" + MainPageOrder.Digit(id), defaults[0].Key);
            Assert.Equal(new[] { "Primary" }, defaults[0].Modifiers);
            Assert.Equal(PageKeyScheme.Letter(id), defaults[1].Key);
            Assert.Equal(new[] { "Primary", "Shift" }, defaults[1].Modifiers);
        }
        // Settings keeps its one fixed key on every platform.
        Assert.Equal(new[] { "OemComma" }, EffectiveShortcutBindings.Defaults(CommandCatalog.Get("navigation.settings"), platform).Select(g => g.Key));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void NoPageLetterEqualsAnyOtherDefaultInAnyContextAndTheTableStaysIssueFree(ShortcutPlatform platform)
    {
        Assert.Empty(EffectiveShortcutBindings.Build(null, platform).Issues);
        foreach (string id in Pages)
        {
            ShortcutGesture letter = PageKeyScheme.Gesture(id)!;
            foreach (CommandDefinition other in CommandCatalog.All.Where(command => command.Id != id))
                Assert.DoesNotContain(EffectiveShortcutBindings.Defaults(other, platform), gesture => EffectiveShortcutBindings.Equivalent(gesture, letter, platform));
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void NoPageLetterIsInvalidAltGrAMenuKeyAReservedSystemKeyOrATypedCharacter(ShortcutPlatform platform)
    {
        foreach (string id in Pages)
        {
            ShortcutGesture letter = PageKeyScheme.Gesture(id)!;
            Assert.Null(EffectiveShortcutBindings.InvalidReason(letter, platform));
            KeyGesture resolved = letter.Resolve(platform);
            Assert.False(resolved.KeyModifiers.HasFlag(KeyModifiers.Alt)); // no Ctrl+Alt (AltGr), no bare Alt+letter (access keys), no Alt+Shift (layout switch)
            Assert.False(resolved.KeyModifiers.HasFlag(KeyModifiers.Control) && resolved.KeyModifiers.HasFlag(KeyModifiers.Meta));
            Assert.Equal(platform == ShortcutPlatform.MacOS ? KeyModifiers.Meta | KeyModifiers.Shift : KeyModifiers.Control | KeyModifiers.Shift, resolved.KeyModifiers);
            Assert.False(EffectiveShortcutBindings.IsSingleCharacterKey(letter)); // the single-key switch never turns a page letter off
            Assert.DoesNotContain(PageKeyScheme.ReservedLetters, reserved => reserved == letter.Key);
        }
        // A Mac reserves ⌘⇧Q (log out) and ⌘⇧W, ⌘H and ⌘M (and their neighbours): none is a page letter.
        Assert.Empty(new[] { "Q", "W", "H", "M" }.Intersect(PageKeyScheme.Letters.Values));
    }

    [Fact]
    public void TheLettersAreShownInTheNotationOfEachPlatformAndWithTheDigitBesideThem()
    {
        var windows = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Windows);
        var mac = EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS);
        foreach (string id in Pages)
        {
            string letter = PageKeyScheme.Letter(id)!;
            Assert.EndsWith(" / Ctrl+Shift+" + letter, windows.Display(id));
            Assert.EndsWith(" / ⇧⌘" + letter, mac.Display(id));
            Assert.Matches(@"^Ctrl\+[1-9] / ", windows.Display(id));
            Assert.Matches(@"^⌘[1-9] / ", mac.Display(id));
        }
        Assert.Equal("Ctrl+,", windows.Display("navigation.settings"));
        Assert.Equal("⌘,", mac.Display("navigation.settings"));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Windows, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(ShortcutPlatform.Linux, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(ShortcutPlatform.MacOS, KeyModifiers.Meta | KeyModifiers.Shift)]
    public void ThePressedLetterMatchesItsPageAndNoOtherPage(ShortcutPlatform platform, KeyModifiers modifiers)
    {
        var built = EffectiveShortcutBindings.Build(null, platform);
        foreach (string id in Pages)
        {
            Key key = Enum.Parse<Key>(PageKeyScheme.Letter(id)!);
            KeyEventArgs press = Press(key, modifiers);
            Assert.True(built.Matches(press, id));
            Assert.Equal(new[] { id }, CommandCatalog.All.Where(command => built.Matches(press, command.Id)).Select(command => command.Id));
            Assert.False(built.Matches(Press(key, modifiers & ~KeyModifiers.Shift), id)); // without Shift it is a different command or none
            Assert.False(built.Matches(Press(key, KeyModifiers.Alt | KeyModifiers.Shift), id));
        }
    }

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void TheLetterIsAYieldingDefaultAndGivesWayToASavedBindingOfAnotherCommandWhileTheDigitStays(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 15);
        foreach (string id in Pages)
        {
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            ShortcutGesture letter = PageKeyScheme.Gesture(id)!;
            // The person saved the letter for a command of the whole application: the page keeps its digit only, nothing blocks, nothing is rewritten.
            var section = new ShortcutBindingsSection().Set("session.pause", [letter]);
            string before = section.Raw.GetRawText();
            var built = EffectiveShortcutBindings.Build(section, platform);
            Assert.Equal(new[] { "D" + MainPageOrder.Digit(id) }, built.For(id).Select(g => g.Key));
            Assert.Single(built.For("session.pause"));
            Assert.DoesNotContain(built.Issues, issue => issue.BlocksSave);
            Assert.Contains(built.Issues, issue => issue.CommandId == id && issue.OtherCommandId == "session.pause" && !issue.BlocksSave);
            Assert.Equal(before, section.Raw.GetRawText());
            // A page whose own gestures the person saved keeps exactly those, with no letter added.
            var own = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(id, [ShortcutGesture.Parse("Primary+9")]), platform);
            Assert.Equal(new[] { "D9" }, own.For(id).Select(g => g.Key));
            Assert.Empty(own.Issues);
        }
    }

    [Fact]
    public void TwoExplicitBindingsOnAPageLetterStillBlockSoNothingYieldsBetweenThem()
    {
        ShortcutGesture letter = PageKeyScheme.Gesture("navigation.media")!;
        var section = new ShortcutBindingsSection().Set("session.pause", [letter]).Set("import.open", [letter]);
        var built = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Contains(built.Issues, issue => issue.BlocksSave && issue.CommandId is "session.pause" or "import.open");
    }

    [Fact]
    public void TheLetterDoesNotMoveWhenTheFooterIsReorderedButTheDigitDoes()
    {
        IReadOnlyList<string> saved = MainPageOrder.Numbering;
        try
        {
            string[] reversed = MainPageOrder.DefaultNumbering.Reverse().ToArray();
            Assert.True(MainPageOrder.SetNumbering(reversed));
            foreach (string id in Pages)
            {
                var defaults = EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), ShortcutPlatform.Linux);
                Assert.Equal("D" + (Array.IndexOf(reversed, id) + 1), defaults[0].Key);
                Assert.Equal(PageKeyScheme.Letter(id), defaults[1].Key);
            }
        }
        finally { MainPageOrder.SetNumbering(saved); }
    }

    [Fact]
    public void ThePagesBeyondTheNinthDigitWouldKeepOnlyTheirLetterAndTheEditorTextNamesBoth()
    {
        ShortcutPlatform platform = EffectiveShortcutBindings.CurrentPlatform;
        string digit = ShortcutGesture.Parse("Primary+3").Display(platform), letter = PageKeyScheme.Gesture("navigation.media")!.Display(platform);
        Assert.Equal(digit + " / " + letter, FooterLayoutEditor.ShortcutText("navigation.media", 3));
        Assert.Equal(letter, FooterLayoutEditor.ShortcutText("navigation.media", 0));
        Assert.Equal(letter, FooterLayoutEditor.ShortcutText("navigation.media", 10));
        Assert.Equal(string.Empty, FooterLayoutEditor.ShortcutText("navigation.settings", 0));
    }

    [Fact]
    public void TheShortcutsWindowTableListsBothKeysOfEveryPageAndFindsAPageByItsLetter()
    {
        var model = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Windows);
        foreach (string id in Pages)
        {
            ShortcutTableRow row = model.Rows.Single(candidate => candidate.CommandId == id);
            Assert.Equal(2, row.GestureTexts.Count);
            Assert.Equal("Ctrl+Shift+" + PageKeyScheme.Letter(id), row.GestureTexts[1]);
            Assert.Equal(ShortcutRowStatus.Default, row.Status);
            Assert.Empty(row.Conflicts);
            Assert.Contains(model.Query(new ShortcutTableQuery(Text: row.GestureTexts[1])).SelectMany(group => group.Rows), found => found.CommandId == id);
        }
    }
}
