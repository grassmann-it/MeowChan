using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Guards against shipping the media.sort.* keys as English copies in the bundled previews
// (review finding M02). Scoped to this key prefix on purpose: other keys may legitimately
// stay identical for now. A value that is genuinely the same word in a language goes into
// SameWordAsEnglish with a reason.
public sealed class MediaSortTranslationTests
{
    private const string Prefix = "media.sort.";

    // pack code -> keys whose translation legitimately equals the English text. Empty today.
    private static readonly Dictionary<string, HashSet<string>> SameWordAsEnglish = new(StringComparer.Ordinal);

    public static TheoryData<string> PackFiles()
    {
        TheoryData<string> data = new();
        foreach (string file in Directory.EnumerateFiles(PacksDirectory(), "*.json").OrderBy(f => f, StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    [Fact]
    public void CatalogGermanIsNotTheEnglishText()
    {
        foreach ((string key, string english, string german) in MediaLibraryCatalog.Entries.Where(e => e.Key.StartsWith(Prefix, StringComparison.Ordinal)))
            Assert.True(!string.Equals(english, german, StringComparison.Ordinal), key + " has an English German text");
    }

    [Theory]
    [MemberData(nameof(PackFiles))]
    public void MediaSortValuesAreNotLeftAsEnglishCopies(string code)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(PacksDirectory(), code + ".json")));
        JsonElement strings = json.RootElement.GetProperty("strings");
        (string Key, string English, string German)[] entries = MediaLibraryCatalog.Entries
            .Where(e => e.Key.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(entries);

        foreach ((string key, string english, _) in entries)
        {
            Assert.True(strings.TryGetProperty(key, out JsonElement value), code + " lacks " + key);
            string text = value.GetString() ?? "";
            if (SameWordAsEnglish.TryGetValue(code, out HashSet<string>? allowed) && allowed.Contains(key)) continue;
            Assert.False(string.Equals(text, english, StringComparison.Ordinal),
                $"{code}: {key} is still the English text \"{english}\"");
            Assert.False(string.IsNullOrWhiteSpace(text), code + ": " + key + " is empty");
        }
    }

    private static string PacksDirectory([CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "../..", "Desktop/Localization/Packs"));
}
