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
            ["cs"] = (5243, 93, "662ed45eb88706fb5abd17b68af4b4cec4f7c4ff3295fa37bf30743221326e8d"),
            ["es"] = (5195, 84, "d1a88ed1eefd1a51a7b39660b90feda12c9163dfee17db8a247198c3776506c4"),
            ["fr"] = (5195, 120, "7b5f781094edcf29e359eeb3a7c733241c4161be7e0a5ad3df54f954ca68d8f1"),
            ["it"] = (5201, 126, "083ce18fcdd9881677e3a3ec50907240b58a1a29e8c0e7fccd06625d24de5ddd"),
            ["ja"] = (5094, 58, "7ab427fa2065e50f09d5476f116ff2a051f8cacd9ebf1f1ec844fc34c1d28e3f"),
            ["ko"] = (5094, 53, "b51b63ed6b216c6ce9dd984e23b33d0d07ebf1b9bf849869987a3fcda3b68eb4"),
            ["pl"] = (5244, 93, "643c4cc1d370218f32b7f49d97c185a290c7519de266cd7a97e72e4ecb4f2519"),
            ["pt-BR"] = (5201, 141, "691b2c3c7e41140805f5bdcf2cae5e9df986b3ed415bb1bb1abffa063d238cdd"),
            ["ru"] = (5221, 60, "6c6ec2607287bb12ae697b7c09f5b1cd150a05077e80d3578496280fe196b6d0"),
            ["zh-Hans"] = (5094, 48, "c9ec9a93a852abae49941e4bfc105328d0f04cafd9de3fa51bf17718cc74d0ff"),
        };

    public IdenticalLocalizationStringsAuditTests() => Texts.Initialize();
    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Fact]
    public void EnglishSourceCatalogHasTheAuditedUniqueInventory()
    {
        Assert.Equal(5199, BuiltInCatalog.English.Count);
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
