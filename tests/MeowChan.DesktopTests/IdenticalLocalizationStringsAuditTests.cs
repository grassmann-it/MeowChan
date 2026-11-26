using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class IdenticalLocalizationStringsAuditTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, (int Entries, int ExactEnglish, string Digest)> Expected =
        new Dictionary<string, (int, int, string)>(StringComparer.Ordinal)
        {
            // Bundle D review blocker 3 calls for two Booru search actions in the command catalog, with labels in every pack.
            // 73-2 review #4 makes the sidebar preview pixel limit a setting (label and hint in every pack).
            // Tom's B1/B2 selection adds the shared English quips and the per-language "EN" cells from the approved matrix.
            // Tom's B3/B4 selection keeps English where the pop culture references work best in the original.
            // Round 4 keeps the marked English lines, release puns and language-specific English fallbacks.
            // Tagline curation keeps the approved shared English texts in the Top 100, seasonal and release selections.
            ["cs"] = (5745, 134, "ffa77d9c7f3289662bd6025f4a3f665234975201b70d9e70814bed8fe7071557"),
            ["es"] = (5689, 123, "07fbb0e45f80756812edd5fc6fdcb30747fe1d28dbcbb8ad80402638145934e4"),
            ["fr"] = (5689, 160, "b8501b96bbcd45b685a61033cfea567313cf49cf31f2ece76ed893cf212a4199"),
            ["it"] = (5695, 174, "39059c62f0177b1f68271b85c51b40e4f1884d38f959f901d87c779768255e55"),
            ["ja"] = (5579, 101, "49f4b6439fa244f1367c5d8456fe123b232b21d1d462cd3cf8f680b56097b798"),
            ["ko"] = (5579, 97, "4fca56c66ab0f3cea7c6b318fa5461be90c53d0d59ef5fbe478b99750fa4ee52"),
            ["pl"] = (5746, 138, "55964252f97b6167a450c6968d2f8093e27b34bff90f6728b69401a4eb2fd272"),
            ["pt-BR"] = (5695, 184, "59d6702c52135de891262df6d37bb9328ffa6c18a72382ea2fdb42cdf7825d24"),
            ["ru"] = (5723, 103, "ce08668ea1d7afa72d174f378378fc8a91c4c6868ae4ad7b6cec4016fb84d058"),
            ["zh-Hans"] = (5579, 92, "9d6c1d9be94421cb3ceb889237ed8cefeacf681da4bced030d34f5788fbbd181"),
        };

    // The provider number, unit and precision retain the same punctuation in these packs.
    // "Dimensions" is French and "File" is conventional Italian UI terminology.
    // Spanish and Brazilian Portuguese use "bytes" with the same punctuation.
    private static readonly string[] SharedHintFormat = ["cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru"];

    internal static string[] AllowedMediaFacts(string code)
    {
        List<string> keys = [];
        if (SharedHintFormat.Contains(code, StringComparer.Ordinal)) keys.Add("media.fact.sizeHint");
        if (code == "fr") keys.Add("media.fact.dimensions");
        if (code == "it") keys.Add("media.fact.file");
        if (code is "es" or "pt-BR") keys.Add("media.fact.byteSize");
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    public IdenticalLocalizationStringsAuditTests() => Texts.Initialize();
    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Fact]
    public void EnglishSourceCatalogHasTheAuditedUniqueInventory()
    {
        // Includes the three footer display hints and the labels and hints for the two Booru limits.
        // Board-watcher wizard specification §5.8 requires all new texts in English and the ten bundled packs.
        // UX review §3 adds the shared label for reading the full tag list.
        // Bundle D review blocker 3 adds the Watch and Download/Stop command labels.
        // 73-2 review #4 adds the label and hint of the sidebar preview pixel limit.
        // Tom, 26.11.2026 (icons): the rating stars get a theme colour of their own; its name is new in English and the ten packs.
        // Tom's B1/B2 selection adds 93 taglines and removes four that were still in the catalog.
        // Tom's B3/B4 selection adds 125 pop culture taglines.
        // Tom’s 1.1.3 round 4 selection adds 94 taglines.
        // Fable 18 and 25 add the Stamp command label and remove three unused labels.
        // The Taglines Top 100 consensus reduces the General selection to 100 quips.
        // Fable §1 Z2 adds the third-party archive label and tooltip.
        Assert.Equal(5691, BuiltInCatalog.English.Count);
        Assert.Equal(2, BuiltInCatalog.English.Count(entry => entry.Value.Length == 0));
        Assert.Equal(BuiltInCatalog.English.Count, BuiltInCatalog.English.Keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(BundledLanguagePackCompletenessTests.BundledCodes), MemberType = typeof(BundledLanguagePackCompletenessTests))]
    public void BundledPackHasNoDuplicatePropertiesAndItsExactEnglishSetIsAudited(string code)
    {
        Assert.True(Expected.TryGetValue(code, out (int Entries, int ExactEnglish, string Digest) expected), code);
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        AssertNoDuplicateProperties(document.RootElement, "$" + code);

        JsonElement strings = document.RootElement.GetProperty("strings");
        List<(string Key, string Value)> exact = strings.EnumerateObject()
            .Where(property => BuiltInCatalog.English.TryGetValue(property.Name, out string? english)
                && string.Equals(property.Value.GetString(), english, StringComparison.Ordinal))
            .Select(property => (property.Name, property.Value.GetString()!))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected.Entries, strings.EnumerateObject().Count());
        Assert.Equal(AllowedMediaFacts(code), exact.Where(item => item.Key.StartsWith("media.fact.", StringComparison.Ordinal)).Select(item => item.Key));
        exact.RemoveAll(item => item.Key.StartsWith("media.fact.", StringComparison.Ordinal));
        Assert.Equal(expected.ExactEnglish, exact.Count);
        string payload = string.Concat(exact.Select(item => item.Key + "\t" + item.Value + "\n"));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        Assert.Equal(expected.Digest, digest);
    }

    private static void AssertNoDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Assert.True(names.Add(property.Name), "Duplicate JSON property at " + path + ": " + property.Name);
                AssertNoDuplicateProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                AssertNoDuplicateProperties(item, path + "[" + index++ + "]");
        }
    }
}
