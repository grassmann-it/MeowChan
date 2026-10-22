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
}
