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
            ["cs"] = (5511, 93, "662ed45eb88706fb5abd17b68af4b4cec4f7c4ff3295fa37bf30743221326e8d"),
            ["es"] = (5460, 84, "d1a88ed1eefd1a51a7b39660b90feda12c9163dfee17db8a247198c3776506c4"),
            ["fr"] = (5460, 120, "7b5f781094edcf29e359eeb3a7c733241c4161be7e0a5ad3df54f954ca68d8f1"),
            ["it"] = (5466, 129, "eb687b3d54ee0fda9a4292201f275dfe0013253dcee6af3104892abe9f6a4d7b"),
            ["ja"] = (5355, 58, "7ab427fa2065e50f09d5476f116ff2a051f8cacd9ebf1f1ec844fc34c1d28e3f"),
            ["ko"] = (5355, 53, "b51b63ed6b216c6ce9dd984e23b33d0d07ebf1b9bf849869987a3fcda3b68eb4"),
            ["pl"] = (5512, 95, "29ab906199d618762d3af868c893a4adf786c3d460be5a096f2e71ebccbdcfdf"),
            ["pt-BR"] = (5466, 143, "a21be79decfd7566b0ee5cc93b958d6ed69282dd547064012f8b1a488bd54557"),
            ["ru"] = (5489, 60, "6c6ec2607287bb12ae697b7c09f5b1cd150a05077e80d3578496280fe196b6d0"),
            ["zh-Hans"] = (5355, 48, "c9ec9a93a852abae49941e4bfc105328d0f04cafd9de3fa51bf17718cc74d0ff"),
        };

    // The provider number, unit and precision retain the same punctuation in these packs.
    // "Dimensions" is French and "File" is conventional Italian UI terminology.
    // Spanish and Brazilian Portuguese use "bytes" with the same punctuation.
    private static readonly string[] SharedHintFormat = ["cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru"];

    private static string[] AllowedMediaFacts(string code)
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
        Assert.Equal(5462, BuiltInCatalog.English.Count);
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
