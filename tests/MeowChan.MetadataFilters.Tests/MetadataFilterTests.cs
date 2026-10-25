using System.Globalization;
using System.Text;
using MeowChan.ExamplePlugins;
using MeowChan.PluginSdk;

namespace MeowChan.MetadataFilters.Tests;

public sealed class MetadataFilterTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static SettingsSnapshot Settings(params SettingValue[] values) => new("1", values);
    private static SettingValue Integer(string key, long value) => new(key, SettingKind.Integer, Integer: value);
    private static SettingValue Text(string key, string value) => new(key, SettingKind.Text, Text: value);
    private static SettingValue Enum(string key, string value) => new(key, SettingKind.Enum, Text: value);
    private static SettingValue Boolean(string key, bool value) => new(key, SettingKind.Boolean, Boolean: value);
    private static ImageFilterRequest Request(string key, SettingsSnapshot? settings = null, long size = 100,
        string? name = "猫 Portrait.PNG", string hash = Hash) => new(key,
            new(new(new("fixture", "b", "1"), "media"), "post", 0, new("https://fixture.test/not-the-name.jpg"), name, "image/png"),
            "input", "media", "private-token", size, hash, "rule", settings ?? Settings(), "build", "configuration", "grant");
    private static PluginCallContext Context() => new(new("configuration", Settings(), new Dictionary<string, SettingsSnapshot>()),
        new ForbiddenServices(), new ForbiddenServices(), new ForbiddenServices(), new ForbiddenServices());

    [Fact]
    public void DescriptionsAreInertValidSdkOnlyMetadataCapabilities()
    {
        foreach (IImageFilterPlugin filter in new IImageFilterPlugin[] { new FileRulesFilter(), new HashBlocklistFilter() })
        {
            PluginDescription description = filter.Describe();
            Assert.Empty(SdkValidation.ValidateDescription(description));
            Assert.Equal(SdkContract.Version, description.SdkVersion);
            Assert.Empty(description.Sources);
            Assert.Empty(description.Settings.Fields);
            Assert.Equal(FilterDataAccess.CandidateMetadata, Assert.Single(description.Filters).RequiredAccess);
            Assert.All(filter.GetType().Assembly.GetReferencedAssemblies(), assembly =>
                Assert.True(assembly.Name == "MeowChan.PluginSdk" || assembly.Name?.StartsWith("System.", StringComparison.Ordinal) == true,
                    "Only SDK/framework references are allowed: " + assembly.Name));
        }
    }

    [Theory]
    [InlineData(99, ImageFilterOutcome.NoMatch, "below_minimum_bytes")]
    [InlineData(100, ImageFilterOutcome.Match, "file_rules_match")]
    [InlineData(200, ImageFilterOutcome.Match, "file_rules_match")]
    [InlineData(201, ImageFilterOutcome.NoMatch, "above_maximum_bytes")]
    public async Task FileSizeUsesInclusiveMeasuredStageLimits(long actual, ImageFilterOutcome outcome, string reason)
    {
        ImageFilterResult result = await new FileRulesFilter().EvaluateAsync(Request(FileRulesFilter.FilterKey,
            Settings(Integer("minimumBytes", 100), Integer("maximumBytes", 200)), size: actual), Context());
        Assert.Equal(outcome, result.Outcome); Assert.Equal(reason, result.ReasonCode);
    }

    [Theory]
    [InlineData("contains", "portrait")]
    [InlineData("startsWith", "猫 p")]
    [InlineData("endsWith", ".png")]
    [InlineData("exact", "猫 portrait.png")]
    public async Task FilenameModesUseOriginalNameAndOrdinalIgnoreCaseByDefault(string mode, string text)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            ImageFilterResult result = await new FileRulesFilter().EvaluateAsync(Request(FileRulesFilter.FilterKey,
                Settings(Enum("nameMode", mode), Text("nameText", text))), Context());
            Assert.Equal(ImageFilterOutcome.Match, result.Outcome);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public async Task FilenameRuleNeverFallsBackToUrlAndCanBeCaseSensitive()
    {
        FileRulesFilter filter = new();
        SettingsSnapshot lower = Settings(Enum("nameMode", "endsWith"), Text("nameText", ".png"), Boolean("ignoreCase", false));
        Assert.Equal(ImageFilterOutcome.NoMatch, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, lower), Context())).Outcome);
        ImageFilterResult missing = await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey,
            Settings(Enum("nameMode", "contains"), Text("nameText", "not-the-name")), name: null), Context());
        Assert.Equal(ImageFilterOutcome.Unavailable, missing.Outcome);
        Assert.Equal("original_name_unavailable", missing.ReasonCode);
        Assert.Equal(ImageFilterOutcome.Match, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, name: null), Context())).Outcome);
    }

    [Fact]
    public async Task AllEnabledFileConditionsMustMatchAndLiteralPatternsAreNotRegex()
    {
        FileRulesFilter filter = new();
        SettingsSnapshot settings = Settings(Integer("minimumBytes", 100), Integer("maximumBytes", 200),
            Enum("nameMode", "contains"), Text("nameText", "Portrait"));
        Assert.Equal(ImageFilterOutcome.NoMatch, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, settings, size: 201), Context())).Outcome);
        Assert.Equal(ImageFilterOutcome.NoMatch, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, settings, name: "other.png"), Context())).Outcome);
        Assert.Equal(ImageFilterOutcome.Match, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, settings), Context())).Outcome);
        settings = Settings(Enum("nameMode", "contains"), Text("nameText", ".*"));
        Assert.Equal(ImageFilterOutcome.NoMatch, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, settings), Context())).Outcome);
        Assert.Equal(ImageFilterOutcome.Match, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, settings, name: "literal.*.png"), Context())).Outcome);
    }

    [Fact]
    public async Task FileConditionsMayBeExplicitlyUnrestricted()
    {
        ImageFilterResult result = await new FileRulesFilter().EvaluateAsync(Request(FileRulesFilter.FilterKey), Context());
        Assert.Equal(ImageFilterOutcome.Match, result.Outcome);
        Assert.Equal("no_file_conditions", result.ReasonCode);
        Assert.Contains("No file conditions", result.Message);
    }

    [Fact]
    public async Task InvalidFileRulesAreFieldErrorsAndNeverAFilterPass()
    {
        FileRulesFilter filter = new();
        foreach ((SettingsSnapshot settings, string field) in new[] {
            (Settings(Integer("minimumBytes", 200), Integer("maximumBytes", 100)), "maximumBytes"),
            (Settings(Enum("nameMode", "contains")), "nameText"),
            (Settings(Enum("nameMode", "exact"), Text("nameText", "")), "nameText") })
        {
            IReadOnlyList<FieldValidationError> errors = await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, settings), Context());
            Assert.Contains(errors, error => error.FieldKey == field);
            Assert.Equal(ImageFilterOutcome.Error, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, settings), Context())).Outcome);
        }
        foreach (SettingsSnapshot settings in new[] {
            Settings(Text("nameText", new string('x', 257))), Settings(Integer("minimumBytes", -1)),
            Settings(Integer("maximumBytes", long.MaxValue)), Settings(Enum("nameMode", "regex")),
            Settings(Integer("minimumBytes", 1), Integer("minimumBytes", 2)), Settings(Text("minimumBytes", "1")) })
            Assert.NotEmpty(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, settings), Context()));
    }

    [Fact]
    public async Task MaximumLengthNameAndExactSdkByteBoundaryAreAccepted()
    {
        SettingsSnapshot settings = Settings(Enum("nameMode", "exact"), Text("nameText", new string('猫', 256)),
            Integer("minimumBytes", SdkLimits.MaximumImageBytes), Integer("maximumBytes", SdkLimits.MaximumImageBytes));
        FileRulesFilter filter = new();
        Assert.Empty(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, settings), Context()));
        Assert.Equal(ImageFilterOutcome.Match, (await filter.EvaluateAsync(Request(FileRulesFilter.FilterKey, settings,
            SdkLimits.MaximumImageBytes, new string('猫', 256)), Context())).Outcome);
    }

    [Theory]
    [InlineData("", ImageFilterOutcome.Match, "empty_blocklist")]
    [InlineData(" ,\r\n \t", ImageFilterOutcome.Match, "empty_blocklist")]
    [InlineData(Hash, ImageFilterOutcome.NoMatch, "sha256_blocked")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", ImageFilterOutcome.Match, "sha256_allowed")]
    public async Task BlocklistUsesTheMeasuredSha256WithoutByteReads(string list, ImageFilterOutcome outcome, string reason)
    {
        ImageFilterResult result = await new HashBlocklistFilter().EvaluateAsync(Request(HashBlocklistFilter.FilterKey,
            Settings(Text("blockedSha256", list))), Context());
        Assert.Equal(outcome, result.Outcome); Assert.Equal(reason, result.ReasonCode);
        Assert.DoesNotContain(Hash, result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlocklistCombinesCaseDuplicatesCrLfWhitespaceAndCommas()
    {
        string list = "  " + Hash.ToUpperInvariant() + ",\r\n\t" + Hash + "\n" + new string('f', 64);
        HashBlocklistFilter filter = new();
        SettingsSnapshot settings = Settings(Text("blockedSha256", list));
        Assert.Empty(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, settings), Context()));
        Assert.Equal(ImageFilterOutcome.NoMatch, (await filter.EvaluateAsync(Request(HashBlocklistFilter.FilterKey, settings, hash: Hash.ToUpperInvariant()), Context())).Outcome);
        // Duplicates do not use the logical entry budget; the independent text limit still applies.
        settings = Settings(Text("blockedSha256", string.Join(',', Enumerable.Repeat(Hash, 300))));
        Assert.Empty(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, settings), Context()));
    }

    [Fact]
    public async Task BlocklistHasIndependentDistinctEntryAndUtf8TextBudgets()
    {
        HashBlocklistFilter filter = new();
        string valid = string.Join('\n', Enumerable.Range(0, 256).Select(index => index.ToString("x64", CultureInfo.InvariantCulture)));
        Assert.Empty(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, Settings(Text("blockedSha256", valid))), Context()));
        string tooMany = valid + "\n" + 256.ToString("x64", CultureInfo.InvariantCulture);
        Assert.Contains(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, Settings(Text("blockedSha256", tooMany))), Context()),
            error => error.FieldKey == "blockedSha256" && error.Code == "hash_list_count_limit");
        string exactBytes = Hash + new string(' ', HashBlocklistFilter.MaximumListBytes - Hash.Length);
        Assert.Equal(HashBlocklistFilter.MaximumListBytes, Encoding.UTF8.GetByteCount(exactBytes));
        Assert.Empty(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, Settings(Text("blockedSha256", exactBytes))), Context()));
        Assert.NotEmpty(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, Settings(Text("blockedSha256", exactBytes + " "))), Context()));
        string unicodeWhitespace = new('\u2003', HashBlocklistFilter.MaximumListBytes / 3 + 1);
        Assert.True(unicodeWhitespace.Length < HashBlocklistFilter.MaximumListBytes);
        Assert.Contains(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, Settings(Text("blockedSha256", unicodeWhitespace))), Context()),
            error => error.Code == "hash_list_text_limit");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffg")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("C:\\list.txt")]
    [InlineData("https://fixture.test/list.txt")]
    public async Task InvalidHashTokensProduceFieldErrorsAndTechnicalFailure(string token)
    {
        HashBlocklistFilter filter = new();
        SettingsSnapshot settings = Settings(Text("blockedSha256", Hash + "," + token));
        Assert.Contains(await filter.ValidateSettingsAsync(new(SettingScope.FilterRule, settings), Context()),
            error => error.FieldKey == "blockedSha256" && error.Code == "invalid_sha256_entry");
        ImageFilterResult result = await filter.EvaluateAsync(Request(HashBlocklistFilter.FilterKey, settings), Context());
        Assert.Equal(ImageFilterOutcome.Error, result.Outcome);
        Assert.DoesNotContain(Hash, result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffg")]
    public async Task InvalidStageHashIsATechnicalErrorEvenForAnEmptyList(string hash)
    {
        ImageFilterResult result = await new HashBlocklistFilter().EvaluateAsync(Request(HashBlocklistFilter.FilterKey, hash: hash), Context());
        Assert.Equal(ImageFilterOutcome.Error, result.Outcome); Assert.Equal("invalid_original_sha256", result.ReasonCode);
    }

    [Fact]
    public async Task BothFiltersRespectCancellationByteBudgetAndUnknownFilterKeys()
    {
        foreach ((IImageFilterPlugin filter, string key) in new (IImageFilterPlugin, string)[] {
            (new FileRulesFilter(), FileRulesFilter.FilterKey), (new HashBlocklistFilter(), HashBlocklistFilter.FilterKey) })
        {
            using CancellationTokenSource cancellation = new(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => filter.EvaluateAsync(Request(key), Context(), cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ((IPluginSettingsValidator)filter)
                .ValidateSettingsAsync(new(SettingScope.FilterRule, Settings()), Context(), cancellation.Token));
            Assert.Equal(ImageFilterOutcome.Unavailable, (await filter.EvaluateAsync(Request("unknown"), Context())).Outcome);
            Assert.Equal("image_byte_limit", (await filter.EvaluateAsync(Request(key, size: SdkLimits.MaximumImageBytes + 1), Context())).ReasonCode);
            Assert.Equal("invalid_original_size", (await filter.EvaluateAsync(Request(key, size: 0), Context())).ReasonCode);
            Assert.Equal("invalid_original_size", (await filter.EvaluateAsync(Request(key, size: -1), Context())).ReasonCode);
        }
    }

    [Fact]
    public async Task SdkJsonSettingsRoundTripPreservesMultilineHashListAndRuleMeaning()
    {
        string list = Hash.ToUpperInvariant() + ",\r\n " + new string('f', 64);
        SettingsSnapshot original = Settings(Text("blockedSha256", list));
        SettingsSnapshot restored = SdkJson.Deserialize<SettingsSnapshot>(SdkJson.Serialize(original));
        Assert.Equal(list, Assert.Single(restored.Values).Text);
        Assert.Equal(ImageFilterOutcome.NoMatch, (await new HashBlocklistFilter().EvaluateAsync(
            Request(HashBlocklistFilter.FilterKey, restored), Context())).Outcome);
    }

    private sealed class ForbiddenServices : IHostHttp, IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A metadata-only filter must not request HTTP.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A metadata-only filter must not read the original.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("A metadata-only filter must not access resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("A metadata-only filter must not log file identities.");
    }
}
