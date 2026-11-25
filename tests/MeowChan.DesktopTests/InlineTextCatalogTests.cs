using MeowChan.Desktop;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// TD-09: the former hard-coded English/German pairs now live in InlineTextCatalog.
[Collection("Localization")]
public sealed class InlineTextCatalogTests : IDisposable
{
    private readonly string _previous = Texts.Language;
    public InlineTextCatalogTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    [Fact]
    public void KeysAreUniqueAndEnglishAndGermanAreResolved()
    {
        Assert.Equal(InlineTextCatalog.Entries.Length, InlineTextCatalog.Entries.Select(entry => entry.Key).Distinct().Count());
        foreach (var entry in InlineTextCatalog.Entries)
        {
            Texts.SetLanguage("en");
            Assert.NotEqual(entry.Key, Texts.T(entry.Key));
            Texts.SetLanguage("de");
            Assert.NotEqual(entry.Key, Texts.T(entry.Key));
        }
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void BundledPacksTranslateEveryKeyAndKeepFormatContracts(string language)
    {
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + language + ".json")!;
        using JsonDocument pack = JsonDocument.Parse(stream);
        JsonElement strings = pack.RootElement.GetProperty("strings");
        Dictionary<string, string> approved = IdenticalLocalizationStringsAuditTests.ReadExactEnglishAllowlist(language);
        Texts.SetLanguage(language);
        foreach ((string key, string english, _) in InlineTextCatalog.Entries)
        {
            Assert.True(strings.TryGetProperty(key, out JsonElement value), language + " lacks " + key);
            string translated = value.GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(translated), key);
            Assert.Equal(translated, Texts.T(key));
            Assert.True(translated != english || (approved.TryGetValue(key, out string? text) && text == translated),
                language + " uses English for " + key);
            Assert.Equal(Placeholders(english), Placeholders(translated));
        }
    }

    [Fact]
    public void EnglishAndGermanTextStaysExactForFormattedAndPluralisedEntries()
    {
        Texts.SetLanguage("en");
        Assert.Equal("Saved file: 1.2 KiB (1,234 bytes)", Texts.T("media.provenance.localSize", 1234L, SizeText.Format(1234L)));
        Assert.Equal("Deleted threads: 1, owned files: 2 (3,000 bytes). Selected threads remaining: 4. ",
            Texts.T("archive.cleanup.status.deleted", 1, 2, 3000L, 4));
        Assert.Equal("Source size hint (not exact bytes): 2.4e1 KiB (Unspecified)",
            Texts.T("media.provenance.sizeHint", "2.4e1", "KiB", "Unspecified"));
        Texts.SetLanguage("de");
        Assert.Equal("Gespeicherte Datei: 1,2 KiB (1.234 Bytes)", Texts.T("media.provenance.localSize", 1234L, SizeText.Format(1234L)));
        Assert.Equal("Gelöschte Threads: 1, zugeordnete Dateien: 2 (3.000 Bytes). Verbleibende ausgewählte Threads: 4. ",
            Texts.T("archive.cleanup.status.deleted", 1, 2, 3000L, 4));
        Assert.Equal("Threadfavorit konnte nicht gespeichert werden (a /b/ 1).\nDetails: x",
            Texts.T("archive.threadFavorite.saveError", "a", "b", 1L, "x"));
    }

    [Fact]
    public void ProductCodeNoLongerChoosesBetweenEnglishAndGermanInline()
    {
        string root = FindRoot();
        string[] allowed = { "Desktop/Localization", "Desktop/Commands/CommandCatalog.cs" };
        var inline = new Regex(@"Language\s*==\s*""de""|Language\.StartsWith\(""de""|TwoLetterISOLanguageName\s*==\s*""de""");
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Desktop"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || allowed.Any(relative.StartsWith)) continue;
            Assert.False(inline.IsMatch(File.ReadAllText(file)), "Inline language pair in " + relative);
        }
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
        .Select(match => match.Value).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Desktop", "Localization"))) return directory.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
