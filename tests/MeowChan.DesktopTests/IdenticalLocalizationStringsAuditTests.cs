using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class IdenticalLocalizationStringsAuditTests
{
    private const string AllowlistResourcePrefix = "MeowChan.DesktopTests.LocalizationAudit.";
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    private static readonly IReadOnlyDictionary<string, (int Entries, int ExactEnglish, string Digest)> Expected =
        new Dictionary<string, (int, int, string)>(StringComparer.Ordinal)
        {
            ["cs"] = (5804, 110, "cfea3c4852a593bba5295873b4033b536e925a5652d67e6bde4d853bfc10fdb4"),
            ["es"] = (5745, 101, "fa51e90a5b17e336ae086493d719ad0df0437add59c506d9d2223da8fda05ab8"),
            ["fr"] = (5745, 138, "11e04513855529b8a049a9eabafee180220d09dc163af168b65eccf0af88c647"),
            ["it"] = (5751, 148, "6a81ca9d002265a055bf943141f7c281c29e16ce98540698bae35a7fd6624c3b"),
            ["ja"] = (5632, 76, "654d7c632e2671216bc61cb2a2cbab53347e5cc88874475df888d9e64a785b41"),
            ["ko"] = (5632, 72, "060cdcd180f483d124344d5f3ae3810525a0c57dc5729c586047e009192fd700"),
            ["pl"] = (5805, 113, "6e2e4bfde5b24517691cc07f62926966f7240452df06b3da6eafd00a7f6cebe8"),
            ["pt-BR"] = (5751, 160, "30a2c91dd5c809daf5815a90811b61dd655e747d3c655da140efc83ce0cb26df"),
            ["ru"] = (5782, 77, "3c6d61a156e45e97184c9a8c63e50c72982dbb00f89336a9142891656af08b5d"),
            ["zh-Hans"] = (5632, 67, "f57bc31784a7b680307854a2b7e988c969f034d3048e435ae903ab1693474caf"),
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

    [Fact]
    public void EnglishSourceCatalogHasTheAuditedUniqueInventory()
    {
        // Includes the 1.2.0 catalog additions and Tom's approved 95 taglines for 1.1.3.
        Assert.Equal(5747, BuiltInCatalog.English.Count);
        Assert.Equal(BuiltInCatalog.English.Count, BuiltInCatalog.English.Keys.Distinct(StringComparer.Ordinal).Count());
    }

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

        Dictionary<string, string> approved = ReadExactEnglishAllowlist(code);
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

        Assert.True(Expected.TryGetValue(code, out var expected), code);
        Assert.Equal(expected.Entries, pack.RootElement.GetProperty("strings").EnumerateObject().Count());
        Assert.Equal(AllowedMediaFacts(code), exact.Keys
            .Where(key => key.StartsWith("media.fact.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        KeyValuePair<string, string>[] literalMatches = exact
            .Where(entry => BuiltInCatalog.English.ContainsKey(entry.Key) && !entry.Key.StartsWith("media.fact.", StringComparison.Ordinal))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.ExactEnglish, literalMatches.Length);
        string payload = string.Concat(literalMatches.Select(entry => entry.Key + "\t" + entry.Value + "\n"));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        Assert.Equal(expected.Digest, digest);
    }

    internal static Dictionary<string, string> ReadExactEnglishAllowlist(string code)
    {
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
        return approved;
    }
}
