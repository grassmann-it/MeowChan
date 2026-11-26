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
            ["cs"] = (5704, 110, "bcbe32d7d9424ace915616821cdccc2de755120f5c18d0a9727c0c8ae553c27a"),
            ["es"] = (5648, 96, "be2197658a227ee0a945c05ce6951f6099702338c859e3391f65b0555a30ef03"),
            ["fr"] = (5648, 132, "b4b95867895ecef52f201de787a22149d5de6d83c952b80a05002ef0baee5746"),
            ["it"] = (5654, 147, "ff034527957c2b6571680ea842dbad3e59e02f9985599ddeded53466d99c6aeb"),
            ["ja"] = (5538, 77, "8610eb6cb46414e0d6f200989b1f440b220e5cc859f00610b1d075a4d37af4fc"),
            ["ko"] = (5538, 73, "5118fb762b6f27486ab6e54eebd7631076afa6b8858020dad221c73048d500a7"),
            ["pl"] = (5705, 112, "34eaba2fb6b882ae23e67afa0a1eaf1819d7e536c7ce9ca281a3bda3a4bc72c7"),
            ["pt-BR"] = (5654, 158, "8d4249beb4a0a53c5998a5cd2b2b218c1c9e8ee67f2ec931c65752480e8e709e"),
            ["ru"] = (5682, 78, "7c131bec440908d6bb408db86eb0399f63cac357159d54a33fe4d6afb566b673"),
            ["zh-Hans"] = (5538, 69, "c7355ce0a1f5c4768e17619ff533c77dbef4070a66458a00a7220361ed17eaa6"),
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
        Assert.Equal(5650, BuiltInCatalog.English.Count);
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
