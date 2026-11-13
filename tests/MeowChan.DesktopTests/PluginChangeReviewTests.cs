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

    [Fact]
    public void SourceAndPluginEditsProduceIndependentExactLocalSnapshots()
    {
        var review = ScopedSettingsReview();
        Assert.Empty(SdkValidation.ValidateDescription(review.PreviousDescription));
        Assert.Empty(SdkValidation.ValidateDescription(review.NewDescription));
        byte[] original = SdkJson.Serialize(review);
        BundledPluginChangeDraft draft = new(review);
        draft.AcknowledgeRemovedValues();
        const string exactText = "  corrected · λ\nsecond line  ";
        draft.Plugin.Set("label", exactText);
        draft.Sources["source"].Set("limit", "17");
        var first = draft.Configuration(out var firstErrors);
        Assert.Empty(firstErrors);
        Assert.Empty(SdkValidation.ValidateConfiguration(review.NewDescription, first));
        byte[] firstBytes = SdkJson.Serialize(first);
        Assert.Equal(exactText, Assert.Single(first.PluginSettings.Values).Text);
        Assert.Equal(17L, Assert.Single(first.SourceSettings["source"].Values).Integer);

        draft.Sources["source"].Set("limit", "23");
        var second = draft.Configuration(out var secondErrors);
        Assert.Empty(secondErrors);
        Assert.Equal(exactText, Assert.Single(second.PluginSettings.Values).Text);
        Assert.Equal(23L, Assert.Single(second.SourceSettings["source"].Values).Integer);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal(firstBytes, SdkJson.Serialize(first));
        Assert.Equal(original, SdkJson.Serialize(review)); // Local repair never rewrites the displayed material basis.
        // Serialization used for copyable technical data must retain the exact text and typed source value.
        Assert.Equal(firstBytes, SdkJson.Serialize(SdkJson.Deserialize<PluginConfiguration>(firstBytes)));
    }

    [Fact]
    public void RecreatedReviewDraftDoesNotInheritRemovedSourceSettingConsentOrUnsavedEdits()
    {
        var review = ScopedSettingsReview();
        BundledPluginChangeDraft edited = new(review);
        Assert.True(edited.NeedsRemovedValueAcknowledgement);
        edited.AcknowledgeRemovedValues();
        edited.Plugin.Set("label", "unsaved correction");
        edited.Sources["source"].Set("limit", "29");
        _ = edited.Configuration(out var editedErrors);
        Assert.Empty(editedErrors);
        Assert.False(edited.NeedsRemovedValueAcknowledgement);

        // RenderRows recreates this local buffer on refresh; no acknowledgement belongs to the payload itself.
        BundledPluginChangeDraft refreshed = new(SdkJson.Deserialize<BundledPluginChangeReview>(SdkJson.Serialize(review)));
        Assert.True(refreshed.NeedsRemovedValueAcknowledgement);
        Assert.False(refreshed.RemovedValuesAcknowledged);
        Assert.Equal("original label", refreshed.Plugin.Text("label"));
        Assert.Equal("7", refreshed.Sources["source"].Text("limit"));
        Assert.Contains("removed", refreshed.Sources["source"].UnmappedValues.Keys);
        _ = refreshed.Configuration(out var errors);
        Assert.Contains(errors, error => error.Scope == "source" && error.Error.Code == "removed_setting");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangeConsentIsInvalidatedByValidPluginOrSourceEditEvenWhenValueIsRestored(bool sourceScope)
    {
        BundledPluginChangeDraft draft = new(ScopedSettingsReview());
        BundledPluginChangeConsent consent = new(draft);
        consent.AcknowledgeRemovedValues();
        Assert.False(consent.IsAcknowledged); // Removing values does not itself confirm configuration.
        consent.SetAcknowledged(true);
        int invalidations = 0;
        consent.Invalidated += () => invalidations++;
        PluginSettingsDraft editor = sourceScope ? draft.Sources["source"] : draft.Plugin;
        string key = sourceScope ? "limit" : "label";
        string original = editor.Text(key);
        editor.Set(key, sourceScope ? "19" : "reviewed later · λ");
        _ = draft.Configuration(out var errors);
        Assert.Empty(errors);
        Assert.False(consent.IsAcknowledged);
        Assert.Equal(1, invalidations);

        editor.Set(key, original);
        Assert.False(consent.IsAcknowledged); // Undoing the edit is not explicit consent either.
        consent.SetAcknowledged(true);
        Assert.True(consent.IsAcknowledged);
        editor.Set(key, original); // Identical editor notifications do not create a new edit.
        Assert.True(consent.IsAcknowledged);
        Assert.Equal(1, invalidations);
        consent.SetAcknowledged(false);
        Assert.False(consent.IsAcknowledged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidConfigurationCorrectionNeedsNewExplicitChangeConsent(bool sourceScope)
    {
        var review = ScopedSettingsReview();
        if (!sourceScope)
        {
            // Make the plugin editor numeric too, so both scope paths test invalid raw input.
            var numeric = new SettingsSchema("2", [new("label", SettingScope.Plugin, SettingKind.Integer, "Label", IsRequired: true, Minimum: 1)]);
            review = review with { NewDescription = review.NewDescription with { Settings = numeric } };
        }
        BundledPluginChangeDraft draft = new(review);
        BundledPluginChangeConsent consent = new(draft);
        consent.AcknowledgeRemovedValues();
        PluginSettingsDraft editor = sourceScope ? draft.Sources["source"] : draft.Plugin;
        string key = sourceScope ? "limit" : "label";
        editor.Set(key, "not an integer");
        consent.SetAcknowledged(true);
        _ = draft.Configuration(out var invalidErrors); // Same rejection as the dialog's Save path.
        Assert.NotEmpty(invalidErrors);
        Assert.True(consent.IsAcknowledged); // Validation alone cannot provide a new consent.

        editor.Set(key, "31");
        var corrected = draft.Configuration(out var errors);
        Assert.Empty(errors);
        Assert.False(consent.IsAcknowledged);
        consent.SetAcknowledged(true);
        Assert.True(consent.IsAcknowledged);
        Assert.Equal(31L, Assert.Single(sourceScope ? corrected.SourceSettings["source"].Values : corrected.PluginSettings.Values).Integer);
    }

    [Theory]
    [InlineData("plugin")]
    [InlineData("source")]
    [InlineData("removed-source")]
    public void RemovingHistoricalValuesInvalidatesChangeConsentAndRequiresSeparateReconfirmation(string scope)
    {
        var review = ScopedSettingsReview();
        // Keep exactly the selected removed-values case, including a source without an editor.
        var sources = new Dictionary<string, SettingsSnapshot>
        {
            ["source"] = new("1", scope == "source" ? review.PreviousConfiguration.SourceSettings["source"].Values :
                [new("limit", SettingKind.Integer, Integer: 7)])
        };
        if (scope == "removed-source") sources["historical-source"] = new("1", [new("old", SettingKind.Text, Text: "historical")]);
        var pluginValues = review.PreviousConfiguration.PluginSettings.Values.ToList();
        if (scope == "plugin") pluginValues.Add(new("removed", SettingKind.Text, Text: "historical"));
        review = review with { PreviousConfiguration = new("old", new("1", pluginValues), sources) };
        BundledPluginChangeDraft draft = new(review);
        BundledPluginChangeConsent consent = new(draft);
        consent.SetAcknowledged(true);
        _ = draft.Configuration(out var beforeErrors);
        Assert.NotEmpty(beforeErrors);
        Assert.True(draft.NeedsRemovedValueAcknowledgement);

        consent.AcknowledgeRemovedValues();
        Assert.False(consent.IsAcknowledged);
        Assert.False(draft.NeedsRemovedValueAcknowledgement);
        var configuration = draft.Configuration(out var errors);
        Assert.Empty(errors);
        Assert.DoesNotContain(configuration.PluginSettings.Values, value => value.Key == "removed");
        Assert.DoesNotContain(configuration.SourceSettings["source"].Values, value => value.Key == "removed");
        Assert.False(configuration.SourceSettings.ContainsKey("historical-source"));
        consent.SetAcknowledged(true);
        Assert.True(consent.IsAcknowledged);
    }

    [Fact]
    public void RecreatedRowConsentDoesNotInheritAcknowledgementOrRespondToDetachedDraftEdits()
    {
        var review = ScopedSettingsReview();
        BundledPluginChangeDraft oldDraft = new(review);
        BundledPluginChangeConsent oldConsent = new(oldDraft);
        oldConsent.AcknowledgeRemovedValues();
        oldConsent.SetAcknowledged(true);
        // RenderRows (refresh) and a new modal (close/reopen) both create fresh row-local state.
        for (int render = 0; render < 2; render++)
        {
            BundledPluginChangeDraft freshDraft = new(review);
            BundledPluginChangeConsent freshConsent = new(freshDraft);
            Assert.False(freshConsent.IsAcknowledged);
            freshConsent.AcknowledgeRemovedValues();
            Assert.False(freshConsent.IsAcknowledged);
            oldDraft.Plugin.Set("label", "detached " + render);
            oldConsent.SetAcknowledged(true);
            Assert.False(freshConsent.IsAcknowledged);
            freshConsent.SetAcknowledged(true);
            freshDraft.Sources["source"].Set("limit", "37");
            Assert.False(freshConsent.IsAcknowledged);
            Assert.True(oldConsent.IsAcknowledged);
        }
    }

    private static BundledPluginChangeReview ScopedSettingsReview()
    {
        SourceDescription source = new("source", "Offline review source", new Uri("https://review.test/"),
            SourceCapabilities.ThreadRead, [new(SourceResourceKind.Thread, "review.test", "/{board}/thread/{thread}", IsCanonical: true)],
            [new("review.test", HttpHostPurpose.Metadata)],
            new("1", [new("limit", SettingScope.Source, SettingKind.Integer, "Limit", IsRequired: true, Minimum: 1),
                new("removed", SettingScope.Source, SettingKind.Text, "Removed")]));
        var previous = Description(new SettingDefinition("label", SettingScope.Plugin, SettingKind.Text, "Label")) with
            { Sources = [source], DeclarationStatus = PluginDeclarationStatus.Available };
        var next = previous with { Sources = [source with { Settings = new("2", [source.Settings.Fields[0]]) }] };
        PluginConfiguration configuration = new("old", new("1", [new("label", SettingKind.Text, Text: "original label")]),
            new Dictionary<string, SettingsSnapshot> { ["source"] = new("1", [new("limit", SettingKind.Integer, Integer: 7),
                new("removed", SettingKind.Text, Text: "must be reviewed")]) });
        return new(previous, next, configuration, new([], []));
    }

    private static PluginDescription Description(params SettingDefinition[] fields) => new PluginDescription(
        "fixture", "Fixture plugin", "1.0.0", SdkContract.Version, "Synthetic description",
        Array.Empty<SourceDescription>(), Array.Empty<FilterDescription>(), new SettingsSchema("1", fields),
        Array.Empty<InformationEntry>()) with { DeclarationStatus = PluginDeclarationStatus.Blocked };

    private static IReadOnlySet<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, "\\{[0-9]+\\}").Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
}
