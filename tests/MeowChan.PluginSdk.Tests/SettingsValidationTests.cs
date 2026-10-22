using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class SettingsValidationTests
{
    private static SettingsSchema Schema() => new SettingsSchema("2",
    [
        new SettingDefinition("label", SettingScope.Plugin, SettingKind.Text, "Label", IsRequired: true, MaximumLength: 20),
        new SettingDefinition("count", SettingScope.Plugin, SettingKind.Integer, "Count", Minimum: 1, Maximum: 5),
        new SettingDefinition("enabled", SettingScope.Plugin, SettingKind.Boolean, "Enabled"),
        new SettingDefinition("mode", SettingScope.Plugin, SettingKind.Enum, "Mode", Options: [new SettingOption("safe", "Safe")]),
        new SettingDefinition("website", SettingScope.Plugin, SettingKind.Uri, "Website")
    ]);

    [Fact]
    public void AllFiveSettingKindsRoundTripAndValidateWithoutUiOrCoreTypes()
    {
        SettingsSnapshot settings = new SettingsSnapshot("2",
        [new SettingValue("label", SettingKind.Text, Text: "Example"),
            new SettingValue("count", SettingKind.Integer, Integer: 3),
            new SettingValue("enabled", SettingKind.Boolean, Boolean: true),
            new SettingValue("mode", SettingKind.Enum, Text: "safe"),
            new SettingValue("website", SettingKind.Uri, Text: "https://source.test/help")]);
        SettingsSnapshot copy = SdkJson.Deserialize<SettingsSnapshot>(SdkJson.Serialize(settings));
        Assert.Empty(SdkValidation.ValidateSettings(Schema(), copy, SettingScope.Plugin));
        Assert.True(copy.Values[2].Boolean);
    }

    [Fact]
    public void InvalidTypedPayloadBoundsEnumUriAndRequiredFieldsRemainFieldAddressable()
    {
        SettingsSnapshot settings = new SettingsSnapshot("2",
        [new SettingValue("count", SettingKind.Integer, Text: "wrong payload", Integer: 100),
            new SettingValue("mode", SettingKind.Enum, Text: "unlisted"),
            new SettingValue("website", SettingKind.Uri, Text: "relative/path")]);
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateSettings(Schema(), settings, SettingScope.Plugin);
        Assert.Contains(issues, issue => issue.Code == "wrong_payload" && issue.Path.Contains("values[0]", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Code == "out_of_range");
        Assert.Contains(issues, issue => issue.Code == "unknown_option");
        Assert.Contains(issues, issue => issue.Code == "invalid_uri");
        Assert.Contains(issues, issue => issue.Code == "required_setting" && issue.Path.EndsWith("label", StringComparison.Ordinal));
    }

    [Fact]
    public void SchemaChangesAndUnknownOldValuesRequireMigrationRatherThanSilentReset()
    {
        SettingsSnapshot old = new SettingsSnapshot("1", [new SettingValue("removed", SettingKind.Text, Text: "retain this")]);
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateSettings(Schema(), old, SettingScope.Plugin);
        Assert.Contains(issues, issue => issue.Code == "schema_mismatch");
        Assert.Contains(issues, issue => issue.Code == "unknown_setting");
        Assert.Equal("retain this", Assert.Single(old.Values).Text);
    }

    [Fact]
    public void AdditiveDefaultCanSatisfyANewRequiredSettingWithoutRewritingOldValues()
    {
        SettingsSchema schema = new SettingsSchema("1",
            [new SettingDefinition("enabled", SettingScope.Plugin, SettingKind.Boolean, "Enabled", IsRequired: true,
                Default: new SettingValue("enabled", SettingKind.Boolean, Boolean: true))]);
        SettingsSnapshot empty = new SettingsSnapshot("1", Array.Empty<SettingValue>());
        Assert.Empty(SdkValidation.ValidateSettings(schema, empty, SettingScope.Plugin));
        Assert.Empty(empty.Values);
    }

    [Fact]
    public void SourceConfigurationCannotSmuggleSettingsForAnUndeclaredSource()
    {
        SettingsSnapshot empty = new SettingsSnapshot("1", Array.Empty<SettingValue>());
        PluginConfiguration configuration = new PluginConfiguration("config-1", empty,
            new Dictionary<string, SettingsSnapshot> { ["other"] = empty });
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateConfiguration(TestContracts.Plugin(), configuration);
        Assert.Contains(issues, issue => issue.Code == "unknown_source");
        Assert.Contains(issues, issue => issue.Path.EndsWith("synthetic", StringComparison.Ordinal) && issue.Code == "missing");
    }

    [Fact]
    public void OversizedSchemaIsNeverVisitedBySettingsConfigurationOrFilterValidation()
    {
        SettingsSchema schema = new SettingsSchema("1", new CountOnlyList<SettingDefinition>(257));
        SettingsSnapshot empty = new SettingsSnapshot("1", Array.Empty<SettingValue>());
        Assert.Contains(SdkValidation.ValidateSettings(schema, empty, SettingScope.Plugin), issue => issue.Code == "too_large");
        PluginDescription plugin = TestContracts.Plugin(TestContracts.Source() with { Settings = schema }) with { Settings = schema };
        PluginConfiguration configuration = new PluginConfiguration("config-1", empty,
            new Dictionary<string, SettingsSnapshot> { ["synthetic"] = empty });
        Assert.Contains(SdkValidation.ValidateConfiguration(plugin, configuration), issue => issue.Code == "too_large");
        Assert.Contains(SdkValidation.ValidateImageFilterRequest(TestContracts.FilterRequest(), TestContracts.Filter() with { Settings = schema }),
            issue => issue.Code == "too_large");
    }

    [Fact]
    public void OversizedEnumOptionsAreNeverVisitedForDefaultsOrSuppliedValues()
    {
        SettingValue value = new SettingValue("mode", SettingKind.Enum, Text: "safe");
        SettingsSchema schema = new SettingsSchema("1",
            [new SettingDefinition("mode", SettingScope.Plugin, SettingKind.Enum, "Mode", Default: value,
                Options: new CountOnlyList<SettingOption>(257))]);
        Assert.Contains(SdkValidation.ValidateSettings(schema, new SettingsSnapshot("1", [value]), SettingScope.Plugin),
            issue => issue.Code == "too_large");
    }

    [Fact]
    public void SchemaAndValuesAtTheExactLimitStillValidateAfterJsonRoundTrip()
    {
        SettingDefinition[] fields = Enumerable.Range(0, 256)
            .Select(index => new SettingDefinition("flag" + index, SettingScope.Plugin, SettingKind.Boolean, "Flag", IsRequired: true)).ToArray();
        SettingValue[] values = fields.Select(field => new SettingValue(field.Key, SettingKind.Boolean, Boolean: true)).ToArray();
        SettingsSchema schema = SdkJson.Deserialize<SettingsSchema>(SdkJson.Serialize(new SettingsSchema("1", fields)));
        SettingsSnapshot settings = SdkJson.Deserialize<SettingsSnapshot>(SdkJson.Serialize(new SettingsSnapshot("1", values)));
        Assert.Empty(SdkValidation.ValidateSettings(schema, settings, SettingScope.Plugin));
        Assert.Equal(256, settings.Values.Count);
    }

    private sealed class CountOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("Oversized settings must never be visited.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Oversized settings must never be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
