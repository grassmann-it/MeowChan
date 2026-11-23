using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class IdenticalLocalizationStringsAuditTests
{
    private const string AllowlistResourcePrefix = "MeowChan.DesktopTests.LocalizationAudit.";
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    [Fact]
    public void EnglishCatalogHasOnlyTheIntentionalEmptySentinels()
    {
        // BuiltInCatalog constructs its dictionaries with ToDictionary, which rejects duplicate source keys.
        Assert.Equal(
            new[] { "savedSearch.confirm.None", "savedSearch.prompt.None" },
            BuiltInCatalog.English.Where(entry => entry.Value.Length == 0)
                .Select(entry => entry.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BundledLanguagePackCompletenessTests.BundledCodes), MemberType = typeof(BundledLanguagePackCompletenessTests))]
    public void BundledPackHasNoDuplicatePropertiesAndItsExactEnglishKeysAreAllowlisted(string code)
    {
        string packResource = Texts.BundledPackResourcePrefix + code + ".json";
        using Stream? packStream = typeof(Texts).Assembly.GetManifestResourceStream(packResource);
        Assert.NotNull(packStream);
        using JsonDocument pack = JsonDocument.Parse(packStream, StrictJson);

        using Stream? allowlistStream = typeof(IdenticalLocalizationStringsAuditTests).Assembly
            .GetManifestResourceStream(AllowlistResourcePrefix + code + ".json");
        Assert.True(allowlistStream is not null, $"Missing exact-English allowlist for {code}: LocalizationAudit/{code}.json");
        using JsonDocument allowlist = JsonDocument.Parse(allowlistStream, StrictJson);

        HashSet<string> approved = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in allowlist.RootElement.EnumerateObject())
        {
            Assert.True(entry.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.Value.GetString()),
                $"Exact-English allowlist entry {code}/{entry.Name} needs a reason.");
            approved.Add(entry.Name);
        }

        HashSet<string> exact = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in pack.RootElement.GetProperty("strings").EnumerateObject())
        {
            // Pack-specific plural variants use the English base key when there is no exact source key.
            if (!BuiltInCatalog.English.TryGetValue(entry.Name, out string? english)
                && !(PluralRules.BaseKey(entry.Name) is { } baseKey && BuiltInCatalog.English.TryGetValue(baseKey, out english)))
                continue;

            if (string.Equals(entry.Value.GetString(), english, StringComparison.Ordinal))
                exact.Add(entry.Name);
        }

        string[] unexpected = exact.Except(approved, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] stale = approved.Except(exact, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unexpected.Length == 0 && stale.Length == 0,
            $"Exact-English audit failed for {code} (LocalizationAudit/{code}.json).\n"
            + "Unapproved keys (translate or add a reason): " + string.Join(", ", unexpected) + "\n"
            + "Stale allowlist keys (remove): " + string.Join(", ", stale));
    }
}
