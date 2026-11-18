using System.Text.RegularExpressions;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Review item 14, second half (15.11.2026): the shortcut display names keys in the interface language ("Entf", "Pfeil hoch", "Num 0"), in all
/// twelve languages (English, German and the ten bundled packs), while a Mac keeps its symbols and the stored gesture never changes. English reads
/// exactly as before.
/// </summary>
[Collection("Localization")]
public sealed class KeyNamesTests : IDisposable
{
    private readonly string _language = Texts.Language;

    public KeyNamesTests() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    public static IEnumerable<object[]> Languages() =>
        new[] { "en", "de" }.Concat(new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" }).Select(code => new object[] { code });

    private static string Show(string gesture, ShortcutPlatform platform = ShortcutPlatform.Windows) => ShortcutGesture.Parse(gesture).Display(platform);

    [Fact]
    public void AllTwelveLanguagesAreCoveredByTheTheory()
    {
        Assert.Equal(12, Languages().Count());
        foreach (string code in Texts.BundledPackCodes()) Assert.Contains(code, Languages().Select(row => (string)row[0]));
    }

    [Theory, MemberData(nameof(Languages))]
    public void EveryKeyNameExistsInTheLanguageAndTheNamedKeysStayDistinct(string code)
    {
        Texts.SetLanguage(code);
        Assert.Equal(code, Texts.Language);
        string[] simple = KeyNames.Keys.Where(key => key != "key.numpad").Where(key => !key.StartsWith("key.num", StringComparison.Ordinal)).ToArray();
        foreach (string key in KeyNames.Keys)
        {
            string text = Texts.T(key, "5");
            Assert.False(string.IsNullOrWhiteSpace(text), code + " " + key);
            Assert.NotEqual(key, text); // found, not echoed
        }
        // Two keys never read the same in one language (Page Up and Page Down, Left and Right, ...).
        string[] names = simple.Select(key => Texts.T(key)).Concat(new[] { "key.backspace", "key.ctrl", "key.shift" }.Select(key => Texts.T(key))).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        string[] numpad = new[] { "key.numpad", "key.numAdd", "key.numSubtract", "key.numMultiply", "key.numDivide", "key.numDecimal" }
            .Select(key => Texts.T(key, "0")).ToArray();
        Assert.Equal(numpad.Length, numpad.Distinct(StringComparer.Ordinal).Count());
        // The number goes where the language puts it, and only the digit key has a placeholder.
        Assert.Contains("0", numpad[0]);
        Assert.DoesNotContain("{", numpad[0]);
        Assert.All(KeyNames.Keys.Where(key => key != "key.numpad"), key => Assert.DoesNotContain("{", Texts.T(key)));
    }

    [Theory, MemberData(nameof(Languages))]
    public void EveryPackCarriesAllNamesAsRealTextWithThePlaceholderOnlyWhereTheCatalogHasOne(string code)
    {
        if (code is "en" or "de")
        {
            foreach (string key in KeyNames.Keys)
            {
                Assert.True(BuiltInCatalog.English.ContainsKey(key), key);
                Assert.True(BuiltInCatalog.German.ContainsKey(key), key);
            }
            return;
        }
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
        using var pack = System.Text.Json.JsonDocument.Parse(stream);
        var strings = pack.RootElement.GetProperty("strings");
        foreach (string key in KeyNames.Keys)
        {
            Assert.True(strings.TryGetProperty(key, out var value), code + " lacks " + key);
            string text = value.GetString() ?? "";
            Assert.False(string.IsNullOrWhiteSpace(text), code + " " + key);
            Assert.Equal(BuiltInCatalog.English[key].Contains("{0}", StringComparison.Ordinal), text.Contains("{0}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EnglishUsesTheBuiltInKeyNames()
    {
        Assert.Equal("Alt+PageDown", Show("Alt+PageDown"));
        Assert.Equal("Alt+PageUp", Show("Alt+PageUp"));
        Assert.Equal("Alt+Left", Show("Alt+Left"));
        Assert.Equal("Delete", Show("Delete"));
        Assert.Equal("Shift+Delete", Show("Shift+Delete"));
        Assert.Equal("Num 0", Show("NumPad0"));
        Assert.Equal("Num +", Show("Add"));
        Assert.Equal("Num −", Show("Subtract"));
        Assert.Equal("Space", Show("Space"));
        Assert.Equal("Shift+Space", Show("Shift+Space"));
        Assert.Equal("Enter", Show("Enter"));
        Assert.Equal("Escape", Show("Escape"));
        Assert.Equal("Menu", Show("Apps"));
        Assert.Equal("Backspace", Show("Back"));
        Assert.Equal("Ctrl+Shift+Left", Show("Primary+Shift+Left"));
        Assert.Equal("F5", Show("F5"));
        Assert.Equal("Ctrl+,", Show("Primary+,"));
    }

    [Fact]
    public void GermanNamesTheKeysTheWayAGermanKeyboardAndManualDo()
    {
        Texts.SetLanguage("de");
        Assert.Equal("Entf", Show("Delete"));
        Assert.Equal("Umschalt+Entf", Show("Shift+Delete"));
        Assert.Equal("Alt+Pfeil links", Show("Alt+Left"));
        Assert.Equal("Pfeil hoch", Show("Up"));
        Assert.Equal("Strg+Umschalt+Pfeil rechts", Show("Primary+Shift+Right"));
        Assert.Equal("Num 0", Show("NumPad0"));
        Assert.Equal("Num 1", Show("NumPad1"));
        Assert.Equal("Num +", Show("Add"));
        Assert.Equal("Num −", Show("Subtract"));
        Assert.Equal("Enter", Show("Enter"));
        Assert.Equal("Umschalt+Enter", Show("Shift+Enter"));
        Assert.Equal("Leertaste", Show("Space"));
        Assert.Equal("Alt+Bild ab", Show("Alt+PageDown"));
        Assert.Equal("Alt+Bild auf", Show("Alt+PageUp"));
        Assert.Equal("Alt+Pos1", Show("Alt+Home"));
        Assert.Equal("Alt+Ende", Show("Alt+End"));
        Assert.Equal("Rücktaste", Show("Back"));
        Assert.Equal("Menü", Show("Apps"));
        Assert.Equal("Escape", Show("Escape"));
        Assert.Equal("Strg+Umschalt+D", Show("Primary+Shift+D"));
        Assert.Equal("Strg+3", Show("Primary+3"));
        Assert.Equal("F5", Show("F5"));
        Assert.Equal("Strg+,", Show("Primary+,"));
    }

    [Theory]
    [InlineData("es", "Delete", "Supr")]
    [InlineData("es", "Space", "Espacio")]
    [InlineData("fr", "Delete", "Suppr")]
    [InlineData("fr", "Escape", "Échap")]
    [InlineData("fr", "NumPad7", "Pavé num. 7")]
    [InlineData("it", "Delete", "Canc")]
    [InlineData("it", "Left", "Freccia sinistra")]
    [InlineData("pt-BR", "Space", "Espaço")]
    [InlineData("ru", "Space", "Пробел")]
    [InlineData("ru", "Up", "Стрелка вверх")]
    [InlineData("cs", "Space", "Mezerník")]
    [InlineData("cs", "Down", "Šipka dolů")]
    [InlineData("pl", "Space", "Spacja")]
    [InlineData("pl", "Right", "Strzałka w prawo")]
    [InlineData("ja", "Space", "スペース")]
    [InlineData("ja", "NumPad2", "テンキー 2")]
    [InlineData("ko", "Left", "왼쪽 화살표")]
    [InlineData("zh-Hans", "Delete", "删除键")]
    [InlineData("zh-Hans", "Enter", "回车键")]
    public void TheBundledLanguagesUseTheirOwnNamesWhereTheyHaveOne(string language, string key, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, Show(key));
        Assert.Equal("Ctrl+" + expected, Show("Primary+" + key));
    }

    [Theory, MemberData(nameof(Languages))]
    public void NoLanguageOtherThanEnglishShowsARawKeyNameThatHasAName(string code)
    {
        Texts.SetLanguage(code);
        var raw = new Regex("^(Left|Right|Up|Down|PageUp|PageDown|NumPad[0-9]|Add|Subtract|Multiply|Divide|Decimal|Space|Escape|Apps|Prior|Next)$");
        foreach (var command in CommandCatalog.All)
            foreach (var gesture in EffectiveShortcutBindings.Defaults(command, ShortcutPlatform.Windows))
                foreach (string part in gesture.DisplayParts(ShortcutPlatform.Windows))
                    if (code == "en") Assert.NotEmpty(part);
                    else Assert.False(raw.IsMatch(part), code + " " + command.Id + ": " + part);
    }

    [Theory, MemberData(nameof(Languages))]
    public void AMacKeepsItsSymbolsAndTheStoredGestureNeverChangesWhateverTheLanguage(string code)
    {
        Texts.SetLanguage(code);
        Assert.Equal("⌦", Show("Delete", ShortcutPlatform.MacOS));
        Assert.Equal("⇧⌦", Show("Shift+Delete", ShortcutPlatform.MacOS));
        Assert.Equal("⌥←", Show("Alt+Left", ShortcutPlatform.MacOS));
        Assert.Equal("⌥⇞", Show("Alt+PageUp", ShortcutPlatform.MacOS));
        Assert.Equal("⌘↩", Show("Primary+Enter", ShortcutPlatform.MacOS));
        Assert.Equal("␣", Show("Space", ShortcutPlatform.MacOS));
        Assert.Equal("⇧⌘D", Show("Primary+Shift+D", ShortcutPlatform.MacOS));
        Assert.Equal("⌫", Show("Back", ShortcutPlatform.MacOS));
        // The stored gesture is language-free: parsing the same text gives the same key and modifiers.
        ShortcutGesture gesture = ShortcutGesture.Parse("Primary+Shift+Left");
        Assert.Equal("Left", gesture.Key);
        Assert.Equal(new[] { "Primary", "Shift" }, gesture.Modifiers);
        Assert.Equal(gesture.Resolve(ShortcutPlatform.Linux), ShortcutGesture.Parse("Primary+Shift+Left").Resolve(ShortcutPlatform.Linux));
    }

    [Fact]
    public void TheLanguageSwitchRefreshesTheDisplayWithoutTouchingTheBinding()
    {
        var bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        string english = bindings.Display("watch.remove")!;
        Texts.SetLanguage("de");
        string german = bindings.Display("watch.remove")!;
        Assert.Contains("Delete", english);
        Assert.Contains("Entf", german);
        Assert.Equal(bindings.For("watch.remove").Count, EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux).For("watch.remove").Count);
    }
}
