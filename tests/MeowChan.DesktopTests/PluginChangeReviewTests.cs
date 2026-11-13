using System.Security.Cryptography;
using System.Text;
using MeowChan;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginChangeReviewTests
{
    [Fact]
    public void DraftKeepsRemovedValuesUntilExplicitAcknowledgementAndValidatesCorrectedValues()
    {
        PluginDescription previous = Description(new("limit", SettingScope.Plugin, SettingKind.Text, "Limit"),
            new("removed", SettingScope.Plugin, SettingKind.Text, "Removed"));
        PluginDescription next = Description(new SettingDefinition("limit", SettingScope.Plugin, SettingKind.Integer, "Limit", IsRequired: true, Minimum: 1));
        PluginConfiguration old = new("old", new("1", [
            new("limit", SettingKind.Text, Text: "not an integer"),
            new("removed", SettingKind.Text, Text: "keep until reviewed")]), new Dictionary<string, SettingsSnapshot>());
        BundledPluginChangeDraft draft = new(new(previous, next, old, new([], [])));

        Assert.True(draft.NeedsRemovedValueAcknowledgement);
        Assert.Contains("removed", draft.Plugin.UnmappedValues.Keys);
        _ = draft.Configuration(out IReadOnlyList<PluginConfigurationFieldError> invalidErrors);
        Assert.NotEmpty(invalidErrors);
        Assert.Contains(invalidErrors, error => error.Error.Code == "removed_setting");

        draft.AcknowledgeRemovedValues();
        Assert.False(draft.NeedsRemovedValueAcknowledgement);
        draft.Plugin.Set("limit", "5");
        _ = draft.Configuration(out IReadOnlyList<PluginConfigurationFieldError> errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void ReviewPayloadFingerprintAndDescriptionIdentityAreFailClosed()
    {
        PluginDescription previous = Description();
        PluginDescription next = Description(new SettingDefinition("new", SettingScope.Plugin, SettingKind.Text, "New"));
        BundledPluginChangeReview review = new(previous, next, new("old", new("1", []), new Dictionary<string, SettingsSnapshot>()), new([], []));
        string json = Encoding.UTF8.GetString(SdkJson.Serialize(review));
        BundledCandidateRepair row = new("fixture", "old", "new", "slot", "/old", "/new", "old-execution", "new-execution", true, "description-changed")
        {
            ChangeReviewJson = json,
            ChangeFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
        };

        Assert.True(PluginsPage.TryReadBundledChangeReview(row, out _));
        Assert.False(PluginsPage.TryReadBundledChangeReview(row with { ChangeReviewJson = json + " " } , out _));
        Assert.False(PluginsPage.TryReadBundledChangeReview(row with { ChangeFingerprint = "00" }, out _));
        Assert.False(PluginsPage.TryReadBundledChangeReview(row with { PluginId = "other" }, out _));
    }

    [Theory]
    [InlineData("previousDescription", "sources")]
    [InlineData("previousDescription", "settings")]
    [InlineData("previousConfiguration", "pluginSettings")]
    [InlineData("previousConfiguration", "sourceSettings")]
    [InlineData("permissions", "filterGrants")]
    [InlineData("permissions", "sourceIds")]
    public void NestedNullReviewDataIsRejectedWithoutCrashing(string section, string field)
    {
        BundledPluginChangeReview review = new(Description(), Description(),
            new("old", new("1", []), new Dictionary<string, SettingsSnapshot>()), new([], []));
        var node = System.Text.Json.Nodes.JsonNode.Parse(SdkJson.Serialize(review))!;
        node[section]![field] = null;
        string json = node.ToJsonString();
        BundledCandidateRepair row = new("fixture", "old", "new", "slot", "/old", "/new", "old-execution", "new-execution", true, "description-changed")
        { ChangeReviewJson = json, ChangeFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) };
        Assert.False(PluginsPage.TryReadBundledChangeReview(row, out _));
    }

    [Fact]
    public void ReadableComparisonDisclosesNetworkHostsAndFilterDataAccess()
    {
        SourceDescription source = new("source", "Example source", new Uri("https://media.review.test/"), SourceCapabilities.ThreadRead,
            [], [new("media.review.test", HttpHostPurpose.Metadata)], new("1", []));
        FilterDescription filter = new("filter", "Example filter", "Reads images", FilterDataAccess.OriginalImage, new("1", []), ["image/png"]);
        PluginDescription description = Description() with { Sources = [source], Filters = [filter], DeclarationStatus = PluginDeclarationStatus.Available };
        string text = PluginsPage.CapabilitySummary(description);
        Assert.Contains("media.review.test", text);
        Assert.Contains("Example filter", text);
        Assert.Contains(nameof(FilterDataAccess.OriginalImage), text);
        Assert.Contains("image/png", text);
    }

    [Fact]
    public void CatalogHasEnglishAndGermanConsentTextWithMatchingPlaceholders()
    {
        Assert.NotEmpty(PluginChangeReviewCatalog.Entries);
        foreach ((string key, string english, string german) in PluginChangeReviewCatalog.Entries)
            Assert.Equal(Placeholders(english), Placeholders(german));
    }

    private static PluginDescription Description(params SettingDefinition[] fields) => new PluginDescription(
        "fixture", "Fixture plugin", "1.0.0", SdkContract.Version, "Synthetic description",
        Array.Empty<SourceDescription>(), Array.Empty<FilterDescription>(), new SettingsSchema("1", fields),
        Array.Empty<InformationEntry>()) with { DeclarationStatus = PluginDeclarationStatus.Blocked };

    private static IReadOnlySet<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, "\\{[0-9]+\\}").Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
}
