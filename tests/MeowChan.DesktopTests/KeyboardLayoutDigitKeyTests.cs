using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// Layout check of the keys 1.1.1 introduced (bare 1-5 and 0 rate, 6-9 label, Z is 1:1). Avalonia reports the logical Key; the typed character
// (KeySymbol) differs per layout: AZERTY types "&" on the 1 key and "1" only with Shift, Czech types "+", Spanish and US type "1". The defaults
// match on Key, so every layout rates on the same physical key; the character is never what decides. This pins that contract. What a platform's
// Avalonia backend reports as Key for a non-US number row (X11 keysyms, macOS key codes) cannot be simulated here and stays a manual check per
// layout before the release (US, UK, DE, CH-DE, FR, ES, Dvorak).
public sealed class KeyboardLayoutDigitKeyTests
{
    private static KeyEventArgs Press(Key key, KeyModifiers modifiers, string? symbol) => new() { Key = key, KeyModifiers = modifiers, KeySymbol = symbol };

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    [Theory]
    [MemberData(nameof(Platforms))]
    public void RatingAndLabelDigitsMatchTheKeyWhateverCharacterTheLayoutTypes(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        // The first-row characters of US/UK/DE ("1"), AZERTY ("&"), Czech ("+"), and none reported (X11 can omit it).
        foreach (string? symbol in new string?[] { "1", "&", "+", null })
        {
            Assert.True(bindings.Matches(Press(Key.D1, KeyModifiers.None, symbol), "media.rating.set-1"), "bare D1, symbol " + (symbol ?? "none"));
            Assert.True(bindings.Matches(Press(Key.D5, KeyModifiers.None, symbol), "media.rating.set-5"));
            Assert.True(bindings.Matches(Press(Key.D0, KeyModifiers.None, symbol), "media.rating.clear"));
            Assert.True(bindings.Matches(Press(Key.D6, KeyModifiers.None, symbol), "media.label.red"));
            Assert.True(bindings.Matches(Press(Key.D9, KeyModifiers.None, symbol), "media.label.blue"));
        }
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TheShiftDigitSecondKeyIsTheTypedDigitOnAzertyAndStillRates(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        // AZERTY types the digit only with Shift: Shift+D1 with the symbol "1". It must rate, and must not be read as the label of another key.
        Assert.True(bindings.Matches(Press(Key.D1, KeyModifiers.Shift, "1"), "media.rating.set-1"));
        Assert.True(bindings.Matches(Press(Key.D0, KeyModifiers.Shift, "0"), "media.rating.clear"));
        Assert.False(bindings.Matches(Press(Key.D6, KeyModifiers.Shift, "6"), "media.label.red"));
    }

    [Theory]
    [InlineData("!", KeyModifiers.Shift)]
    [InlineData("1", KeyModifiers.Shift)]
    [InlineData(null, KeyModifiers.Shift)]
    [InlineData("&", KeyModifiers.None)]
    public void TheFirstRatingDigitIsAlsoACharacterInTheTagField(string? symbol, KeyModifiers modifiers)
    {
        KeyEventArgs press = Press(Key.D1, modifiers, symbol);
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            Assert.True(EffectiveShortcutBindings.Build(platform: platform).Matches(press, "media.rating.set-1"));
            Assert.True(QuickTagKeys.FieldOwnsCharacter(press, platform));
        }
    }

    [Fact]
    public void MacOptionDigitsStayWithTheFieldAndStillToggleEveryNumberedSuggestionFromTheStrip()
    {
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        for (int number = 1; number <= 9; number++)
        {
            KeyEventArgs press = Press(Key.D0 + number, KeyModifiers.Alt, symbol: null);
            Assert.Equal(QuickTagKeyAction.None, QuickTagKeys.Route(press, QuickTagFocus.Field, false, true, false, bindings).Action);
            Assert.Equal(new QuickTagKeyRoute(QuickTagKeyAction.ToggleSuggestion, number - 1),
                QuickTagKeys.Route(press, QuickTagFocus.Suggestions, false, true, false, bindings));
        }
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void OneToOneIsZOnQwertyAndQwertzAndKeepsTheNumberPadKey(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        // QWERTZ moves the physical position of Z and Y, but both layouts report the logical letter as Key.Z.
        Assert.True(bindings.Matches(Press(Key.Z, KeyModifiers.None, "z"), "gallery.actual-size"));
        Assert.True(bindings.Matches(Press(Key.NumPad1, KeyModifiers.None, "1"), "gallery.actual-size"));
        // The bare 1 and 0 belong to the rating, not to the zoom keys, in every layout.
        Assert.False(bindings.Matches(Press(Key.D1, KeyModifiers.None, "&"), "gallery.actual-size"));
        Assert.False(bindings.Matches(Press(Key.D0, KeyModifiers.None, "à"), "gallery.fit"));
        Assert.True(bindings.Matches(Press(Key.NumPad0, KeyModifiers.None, "0"), "gallery.fit"));
    }

    [Fact]
    public void NoNewDefaultNeedsAltGrOrTheNumberPad()
    {
        // 1.1.1 defaults: none of them is a Ctrl+Alt chord (AltGr text on DE/FR/PL), and each has a key reachable without the number pad.
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            var bindings = EffectiveShortcutBindings.Build(platform: platform);
            foreach (string id in new[] { "media.rating.set-1", "media.rating.set-2", "media.rating.set-3", "media.rating.set-4", "media.rating.set-5", "media.rating.clear",
                         "media.label.red", "media.label.yellow", "media.label.green", "media.label.blue", "gallery.actual-size", "gallery.fit" })
            {
                var gestures = bindings.For(id);
                Assert.NotEmpty(gestures);
                Assert.All(gestures, g => Assert.NotEqual(KeyModifiers.Control | KeyModifiers.Alt, g.Resolve(platform).KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)));
                Assert.Contains(gestures, g => !g.Resolve(platform).Key.ToString().StartsWith("NumPad", StringComparison.Ordinal));
            }
        }
    }
}
