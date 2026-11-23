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
    public void BundledPackHasNoDuplicatePropertiesAndItsExactEnglishTextIsAllowlisted(string code)
    {
        string packResource = Texts.BundledPackResourcePrefix + code + ".json";
        using Stream? packStream = typeof(Texts).Assembly.GetManifestResourceStream(packResource);
        Assert.NotNull(packStream);
        using JsonDocument pack = JsonDocument.Parse(packStream, StrictJson);

        using Stream? allowlistStream = typeof(IdenticalLocalizationStringsAuditTests).Assembly
            .GetManifestResourceStream(AllowlistResourcePrefix + code + ".json");
        Assert.True(allowlistStream is not null, $"Missing exact-English allowlist for {code}: LocalizationAudit/{code}.json");
        using JsonDocument allowlist = JsonDocument.Parse(allowlistStream, StrictJson);

        Dictionary<string, string> approved = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in allowlist.RootElement.EnumerateObject())
        {
            JsonElement reason = entry.Value.GetProperty("reason");
            Assert.True(reason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reason.GetString()),
                $"Exact-English allowlist entry {code}/{entry.Name} needs a reason.");
            JsonElement text = entry.Value.GetProperty("text");
            Assert.True(text.ValueKind == JsonValueKind.String,
                $"Exact-English allowlist entry {code}/{entry.Name} needs the approved text.");
            approved.Add(entry.Name, text.GetString()!);
        }

        Dictionary<string, string> exact = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in pack.RootElement.GetProperty("strings").EnumerateObject())
        {
            // Pack-specific plural variants use the English base key when there is no exact source key.
            if (!BuiltInCatalog.English.TryGetValue(entry.Name, out string? english)
                && !(PluralRules.BaseKey(entry.Name) is { } baseKey && BuiltInCatalog.English.TryGetValue(baseKey, out english)))
                continue;

            if (string.Equals(entry.Value.GetString(), english, StringComparison.Ordinal))
                exact.Add(entry.Name, english);
        }

        string[] unexpected = exact.Keys.Except(approved.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] stale = approved.Keys.Except(exact.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] changed = exact.Where(entry => approved.TryGetValue(entry.Key, out string? text) && entry.Value != text)
            .Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unexpected.Length == 0 && stale.Length == 0 && changed.Length == 0,
            $"Exact-English audit failed for {code} (LocalizationAudit/{code}.json).\n"
            + "Unapproved keys (translate or approve text with a reason): " + string.Join(", ", unexpected) + "\n"
            + "Stale allowlist keys (remove): " + string.Join(", ", stale) + "\n"
            + "Changed approved text (translate or review the approval and its reason): " + string.Join(", ", changed));
    }
}
