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
        foreach (var entry in InlineTextCatalog.Entries)
        {
            Texts.SetLanguage("en");
            string english = Texts.T(entry.Key);
            Texts.SetLanguage(language);
            string translated = Texts.T(entry.Key);
            Assert.NotEqual(english, translated);
            // Placeholders (including numeric formats) must survive translation.
            Assert.Equal(Placeholders(english), Placeholders(translated));
        }
    }

    [Fact]
    public void EnglishAndGermanTextStaysExactForFormattedAndPluralisedEntries()
    {
        Texts.SetLanguage("en");
        Assert.Equal("Saved file: 1,234 bytes", Texts.T("media.provenance.localSize", 1234L));
        Assert.Equal("Deleted 1 threads and 2 owned files (3,000 bytes). 4 selected threads remain. ",
            Texts.T("archive.cleanup.status.deleted", 1, 2, 3000L, 4));
        Assert.Equal("Source size hint (not exact bytes): 2.4e1 KiB (Unspecified)",
            Texts.T("media.provenance.sizeHint", "2.4e1", "KiB", "Unspecified"));
        Texts.SetLanguage("de");
        Assert.Equal("Gespeicherte Datei: 1.234 bytes", Texts.T("media.provenance.localSize", 1234L));
        Assert.Equal("1 Threads und 2 zugeordnete Dateien (3.000 Bytes) gelöscht. 4 ausgewählte Threads verbleiben. ",
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
