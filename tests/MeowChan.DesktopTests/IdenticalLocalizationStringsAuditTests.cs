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
            ["cs"] = (5828, 196, "7352a1604765761cf781ae18131e8ef748700a3bc63a185a10a5a6186f67f7b8"),
            ["es"] = (5772, 182, "c7ae005f0c39a84ab62658ad778a0d11fe9e4a460bd06f27d17f3ca8b602f102"),
            ["fr"] = (5772, 218, "1ff6244f61cab4e655366a786a52d72ac71a3648ff7a9e1520ed643336aac08e"),
            ["it"] = (5778, 233, "2268f8f6022aad5dfbbd2260f4bbcf4dbc989a2d9f2d44bd365cd83223cebf1d"),
            ["ja"] = (5662, 163, "7e01aef1c2e429efbc61d7d5fe69bfefb68aa2cbc457615e6ec2e4471fca30d1"),
            ["ko"] = (5662, 159, "9599e2d906befb8b6bb12126ecc242b5ac44a42e7071754ba1e61aaafc830690"),
            ["pl"] = (5829, 198, "77fa1ab994571caf6d5adacf64cc7638989bbf36bd2fd4085be9875454ecdc31"),
            ["pt-BR"] = (5778, 244, "c8c2bec6259323dd5bb5947caf095bead0bb777c6e7bb4049b2219f00ab7ae60"),
            ["ru"] = (5806, 164, "51946e41f99f9771b169f329b58abf24ee0cae02fe2b1983e1e5d83206dc69b6"),
            ["zh-Hans"] = (5662, 155, "bbab78fcc85538e3bb109e49f4967b5566744d90d408ed88c270bf41de49ba93"),
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
        Assert.Equal(5774, BuiltInCatalog.English.Count);
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
