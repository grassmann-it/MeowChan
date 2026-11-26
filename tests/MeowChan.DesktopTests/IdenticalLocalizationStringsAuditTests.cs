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
            // Tagline curation §§ RAUS, ÄNDERN and NEU update the approved set of shared English quips.
            ["cs"] = (5868, 221, "6f59b64d0b2a230a9f32e04b7002179c3fd8c90dd65cce48ea1442b39a055c68"),
            ["es"] = (5812, 208, "06132a32f0853116b3d048c44200f0fc7a7d8b5e02cac3b9d0389c04b791edb7"),
            ["fr"] = (5812, 245, "c3e66e4af4a2d7226a31241ab73827f05fe3b314398ee9504c6de65980572b4b"),
            ["it"] = (5818, 259, "440206eee6ee81a0191da64fd6905b4af243e00731aa647d3e1216041ec4ce2e"),
            ["ja"] = (5702, 186, "90f86ea166c573dc66ee885195970fa41c032c3c6d586657bd3adff73083af50"),
            ["ko"] = (5702, 182, "6f395d855bf0d15dfdad65acdd11f3b93c4a3e6779b8b6744e83bb0ea3007efa"),
            ["pl"] = (5869, 224, "9edcebf4fc65999be90447660e427fcdecb8f1ea31c7586687177e9b2595a0dc"),
            ["pt-BR"] = (5818, 270, "fd461a52bf0d55c5d95f93cc56faa85666a0e1c6f8556fff606a00f09bce4be8"),
            ["ru"] = (5846, 189, "56dbe77bda79933f995c75a998ab4c3f414950a55ae9dd10b931ed3484fca18f"),
            ["zh-Hans"] = (5702, 177, "100ec53a8dd72bbd9127490a7c7fd4550df82f45fb1833a23b90c4cf485fdc80"),
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
        // Tagline curation § Zählung removes 80 quips and adds 26.
        Assert.Equal(5814, BuiltInCatalog.English.Count);
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
