using System.Text;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The header's I/O captions are words (network Down/Up, disk Write/Read), one key each, in all twelve languages: no arrows,
/// no single letters, no ALLCAPS, no punctuation. The retired single-letter keys are gone from the catalog and every pack.
/// </summary>
[Collection("Localization")]
public sealed class HeaderIoWordLocalizationTests : IDisposable
{
    private static readonly string[] Keys = ["stats.header.down", "stats.header.up", "stats.header.write", "stats.header.read"];
    private static readonly string[] RetiredKeys = ["stats.header.readMark", "stats.header.writeMark"];
    private const int MaximumLatinLetters = 9;
    private const int MaximumCjkCharacters = 5;

    // Down, Up, Write, Read per language, exactly as specified.
    private static readonly Dictionary<string, string[]> Expected = new(StringComparer.Ordinal)
    {
        ["en"] = ["Down", "Up", "Write", "Read"],
        ["de"] = ["Download", "Upload", "Schreiben", "Lesen"],
        ["es"] = ["Bajada", "Subida", "Escritura", "Lectura"],
        ["fr"] = ["Réception", "Envoi", "Écriture", "Lecture"],
        ["ru"] = ["Загрузка", "Отдача", "Запись", "Чтение"],
        ["zh-Hans"] = ["下载", "上传", "写入", "读取"],
        ["ja"] = ["受信", "送信", "書き込み", "読み取り"],
        ["ko"] = ["수신", "송신", "쓰기", "읽기"],
        ["pt-BR"] = ["Download", "Upload", "Gravação", "Leitura"],
        ["cs"] = ["Stahování", "Odesílání", "Zápis", "Čtení"],
        ["it"] = ["Download", "Upload", "Scrittura", "Lettura"],
        ["pl"] = ["Odbiór", "Wysyłka", "Zapis", "Odczyt"],
    };

    private static readonly string[] CjkLanguages = ["zh-Hans", "ja", "ko"];

    public HeaderIoWordLocalizationTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static TheoryData<string> Languages()
    {
        TheoryData<string> data = new();
        foreach (string language in Expected.Keys) data.Add(language);
        return data;
    }

    [Fact]
    public void TheSpecificationCoversExactlyTheTwelveLanguages()
    {
        Assert.Equal(
            new[] { "cs", "de", "en", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans" },
            Expected.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(Expected.Values, words => Assert.Equal(Keys.Length, words.Length));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheFourWordsAreLocalizedWithTheSpecifiedText(string language)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        for (int i = 0; i < Keys.Length; i++)
        {
            string text = Texts.T(Keys[i]);
            Assert.NotEqual(Keys[i], text);
            Assert.Equal(Expected[language][i], text);
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheWordsAreCompactPlainLettersAndDistinct(string language)
    {
        Texts.SetLanguage(language);
        bool cjk = CjkLanguages.Contains(language, StringComparer.Ordinal);
        string[] words = Keys.Select(key => Texts.T(key)).ToArray();
        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
        foreach (string word in words)
        {
            Rune[] runes = word.EnumerateRunes().ToArray();
            Assert.True(runes.Length >= 2, $"{language}: '{word}' is a single letter.");
            Assert.True(runes.Length <= (cjk ? MaximumCjkCharacters : MaximumLatinLetters),
                $"{language}: '{word}' has {runes.Length} characters.");
            // Letters only: no arrows, digits, spaces, punctuation or symbols.
            Assert.All(runes, rune => Assert.True(Rune.IsLetter(rune), $"{language}: '{word}' holds a non-letter {rune}."));
            Assert.Equal(word, word.Trim());
            if (!cjk)
            {
                // Not ALLCAPS: at least one lowercase letter (every word starts uppercase, so this also excludes shouting).
                Assert.Contains(runes, rune => Rune.IsLower(rune));
                Assert.True(Rune.IsUpper(runes[0]), $"{language}: '{word}' does not start uppercase.");
            }
        }
    }

    [Fact]
    public void TheCatalogCarriesTheFourNewKeysAndNotTheRetiredOnes()
    {
        string[] catalogKeys = IoStatsPageTexts.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(catalogKeys.Length, catalogKeys.Distinct(StringComparer.Ordinal).Count());
        foreach (string key in Keys) Assert.Contains(key, catalogKeys);
        foreach (string key in RetiredKeys) Assert.DoesNotContain(key, catalogKeys);
        for (int i = 0; i < Keys.Length; i++)
        {
            (string Key, string English, string German) entry = IoStatsPageTexts.Entries.Single(e => e.Key == Keys[i]);
            Assert.Equal(Expected["en"][i], entry.English);
            Assert.Equal(Expected["de"][i], entry.German);
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheRetiredKeysAreGoneFromTheLoadedCatalogOfEveryLanguage(string language)
    {
        Texts.SetLanguage(language);
        // A missing key is answered with the key itself, so a surviving value would show as something else.
        foreach (string key in RetiredKeys) Assert.Equal(key, Texts.T(key));
    }

    [Fact]
    public void EveryBundledPackHoldsTheFourKeysItselfAndNoRetiredKey()
    {
        // Read from the embedded files, so the English fallback cannot hide a missing translation entry.
        string[] bundled = Texts.BundledPackCodes().ToArray();
        string[] expectedPacks = Expected.Keys.Where(code => code is not ("en" or "de")).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedPacks, bundled.Where(code => expectedPacks.Contains(code, StringComparer.Ordinal)).ToArray());
        foreach (string code in expectedPacks)
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement strings = document.RootElement.GetProperty("strings");
            for (int i = 0; i < Keys.Length; i++)
            {
                Assert.True(strings.TryGetProperty(Keys[i], out JsonElement value), $"{code} lacks {Keys[i]}.");
                Assert.Equal(Expected[code][i], value.GetString());
            }
            foreach (string key in RetiredKeys) Assert.False(strings.TryGetProperty(key, out _), $"{code} still has {key}.");
        }
    }

    [Fact]
    public void TheValueAndTitleTextsOfTheTrayAndTheTooltipStayUnchanged()
    {
        // Only the caption keys changed: the titles and value formats the tray, tooltip, name and statistics use are kept.
        foreach (string key in new[] { "stats.header.networkTitle", "stats.header.diskTitle", "stats.header.netValues", "stats.header.diskValues" })
            Assert.Contains(IoStatsPageTexts.Entries, entry => entry.Key == key);
        Assert.Equal("↓ {0} ↑ {1}", IoStatsPageTexts.Entries.Single(e => e.Key == "stats.header.netValues").English);
        Assert.Equal("R {0} W {1}", IoStatsPageTexts.Entries.Single(e => e.Key == "stats.header.diskValues").English);
    }
}
