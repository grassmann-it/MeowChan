using System.Reflection;
using System.Text;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class TaggerContractTests
{
    private static readonly SettingsSchema TaggerSchema = new("1", Array.Empty<SettingDefinition>());
    private static readonly SettingsSnapshot NoSettings = new("1", Array.Empty<SettingValue>());
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static TaggerDescription Tagger(TaggerMediaKind media = TaggerMediaKind.Image | TaggerMediaKind.Video)
        => new("reference", "Reference tagger", "Deterministic test double, no model.", TaggerContract.Version, media,
            ["image/png", "video/mp4"], [new TaggerModelRequirement("reference-model", "r1", "Apache-2.0", 1024, Sha)], TaggerSchema);

    private static PluginDescription Plugin(params TaggerDescription[] taggers)
        => new PluginDescription("test.tagger", "Tagger plugin", "1.0", SdkContract.Version, "Tagger only.",
            Array.Empty<SourceDescription>(), Array.Empty<FilterDescription>(), TestContracts.EmptySchema,
            Array.Empty<InformationEntry>()) { Taggers = taggers };

    private static TaggerBatchRequest Request(params TaggerMediaInput[] items)
        => new("reference", "batch-1", items.Length == 0 ? [Image("item-0")] : items, 0.05, 8, NoSettings, "configuration-1");

    private static TaggerMediaInput Image(string id, long length = 100)
        => new(id, TaggerMediaKind.Image, "image/png", length, Sha, "token-" + id);

    private static TaggerMediaInput Video(string id, long duration = 10_000)
        => new(id, TaggerMediaKind.Video, "video/mp4", 100, Sha, "token-" + id, DurationMilliseconds: duration);

    private static TaggerBatchResult Result(TaggerBatchRequest request, params TaggerSuggestion[] suggestions)
        => new(request.TaggerKey, request.BatchId, new TaggerModelProvenance("reference-model", "r1", Sha.ToUpperInvariant(), "managed"),
            request.Items.Select(item => new TaggerItemResult(item.ItemId, TaggerItemOutcome.Tagged, suggestions)).ToArray());

    private static string[] Codes(IReadOnlyList<ValidationIssue> issues) => issues.Select(issue => issue.Code).ToArray();

    [Fact]
    public void TaggerOnlyPluginIsACapabilityAndValidates()
    {
        Assert.Empty(SdkValidation.ValidateDescription(Plugin(Tagger())));
        Assert.Contains("missing_capability", Codes(SdkValidation.ValidateDescription(Plugin())));
        PluginDescription blocked = Plugin(Tagger()) with { DeclarationStatus = PluginDeclarationStatus.Blocked };
        Assert.Contains("blocked_capability", Codes(SdkValidation.ValidateDescription(blocked)));
    }

    [Fact]
    public void DescriptionRulesCoverVersionsKindsModelsSettingsAndNetwork()
    {
        Assert.Contains("incompatible_tagger_contract", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with { ContractVersion = "2.0.0" }))));
        Assert.Contains("incompatible_sdk", Codes(SdkValidation.ValidateDescription(Plugin(Tagger()) with { SdkVersion = "1.0.0" })));
        Assert.Contains("duplicate_tagger", Codes(SdkValidation.ValidateDescription(Plugin(Tagger(), Tagger()))));
        Assert.Contains("unknown_media", Codes(SdkValidation.ValidateDescription(Plugin(Tagger(TaggerMediaKind.None)))));
        Assert.Contains("missing_media_types", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with { SupportedMimeTypes = [] }))));
        Assert.Contains("invalid_size", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with
            { Models = [new TaggerModelRequirement("m", "r", "MIT", 0)] }))));
        Assert.Contains("duplicate_model", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with
            { Models = [new TaggerModelRequirement("m", "r", "MIT", 1), new TaggerModelRequirement("m", "r", "MIT", 2)] }))));
        SettingsSchema wrongScope = new("1", [new SettingDefinition("threshold", SettingScope.Plugin, SettingKind.Integer, "Threshold")]);
        Assert.Contains("wrong_scope", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with { Settings = wrongScope }))));
        SettingsSchema taggerScope = new("1", [new SettingDefinition("threshold", SettingScope.Tagger, SettingKind.Integer, "Threshold",
            Default: new SettingValue("threshold", SettingKind.Integer, Integer: 35), Minimum: 5, Maximum: 95)]);
        Assert.Empty(SdkValidation.ValidateDescription(Plugin(Tagger() with { Settings = taggerScope })));
        Assert.Contains("unknown_purpose", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with
            { NetworkHosts = [new HttpHostDeclaration("models.example", HttpHostPurpose.OriginalMedia)] }))));
        Assert.Contains("invalid_host", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with
            { NetworkHosts = [new HttpHostDeclaration("*.example", HttpHostPurpose.Metadata)] }))));
        Assert.Empty(SdkValidation.ValidateDescription(Plugin(Tagger() with
            { NetworkHosts = [new HttpHostDeclaration("models.example", HttpHostPurpose.Metadata)] })));
        Assert.Contains("invalid_bounds", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with { MaximumBatchItems = 65 }))));
        Assert.Contains("invalid_bounds", Codes(SdkValidation.ValidateDescription(Plugin(Tagger() with { MaximumInputBytes = TaggerLimits.MaximumInputBytes + 1 }))));
        Assert.Contains("too_large", Codes(SdkValidation.ValidateDescription(Plugin(Enumerable.Range(0, 17)
            .Select(index => Tagger() with { Key = "t" + index }).ToArray()))));
    }

    [Fact]
    public void DescriptionsWithoutTaggersKeepTheReleasedWireShapeAndTaggersRoundTrip()
    {
        const string legacy = """
            {"pluginId":"test.plugin","name":"Test","pluginVersion":"1.0","sdkVersion":"1.1.0",
             "description":"Metadata only","sources":[],"filters":[],
             "settings":{"version":"1","fields":[]},"information":[],"declarationStatus":"Blocked"}
            """;
        PluginDescription description = SdkJson.Deserialize<PluginDescription>(Encoding.UTF8.GetBytes(legacy));
        Assert.Null(description.Taggers);
        Assert.Empty(description.TaggersOrEmpty());
        Assert.DoesNotContain("taggers", Encoding.UTF8.GetString(SdkJson.Serialize(description)));

        PluginDescription withTagger = Plugin(Tagger() with { NetworkHosts = [new HttpHostDeclaration("models.example", HttpHostPurpose.Metadata)] });
        string json = Encoding.UTF8.GetString(SdkJson.Serialize(withTagger));
        Assert.Contains("\"taggers\":[", json);
        Assert.Contains("\"supportedMedia\":\"Image, Video\"", json);
        PluginDescription back = SdkJson.Deserialize<PluginDescription>(Encoding.UTF8.GetBytes(json));
        TaggerDescription tagger = Assert.Single(back.TaggersOrEmpty());
        Assert.Equal("models.example", Assert.Single(tagger.NetworkHosts).Host);
        Assert.Equal(TaggerLimits.MaximumBatchItems, tagger.MaximumBatchItems);
        Assert.Empty(SdkValidation.ValidateDescription(back));

        TaggerBatchRequest request = Request(Video("v"));
        TaggerBatchResult result = Result(request, new TaggerSuggestion("running", TaggerTagCategory.General, 0.5, new TaggerTimeRange(0, 500)));
        TaggerBatchResult resultBack = SdkJson.Deserialize<TaggerBatchResult>(SdkJson.Serialize(result));
        Assert.Equal(result.Items[0].Suggestions[0], resultBack.Items[0].Suggestions[0]);
        Assert.Equal(request.Items[0], SdkJson.Deserialize<TaggerBatchRequest>(SdkJson.Serialize(request)).Items[0]);
    }

    [Fact]
    public void TaggerIsAnIndependentInterfaceAndTaggersIsInitOnly()
    {
        Assert.Equal(new[] { typeof(IPlugin) }, typeof(ITaggerPlugin).GetInterfaces());
        MethodInfo method = Assert.Single(typeof(ITaggerPlugin).GetMethods());
        Assert.Equal(new[] { typeof(TaggerBatchRequest), typeof(PluginCallContext), typeof(IProgress<TaggerBatchProgress>), typeof(CancellationToken) },
            method.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.True(method.GetParameters()[2].IsOptional && method.GetParameters()[3].IsOptional);
        PropertyInfo taggers = typeof(PluginDescription).GetProperty(nameof(PluginDescription.Taggers))!;
        Assert.Contains(taggers.SetMethod!.ReturnParameter.GetRequiredCustomModifiers(), type => type.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        Assert.Equal("1.0.0", TaggerContract.Version);
        Assert.True(TaggerContract.IsSupported("1.0.0"));
        Assert.False(TaggerContract.IsSupported("1.0"));
        Assert.Equal("1.1.0", SdkContract.Version);
    }

    [Fact]
    public void RequestRulesBoundBatchesKindsTypesSizesAndFloor()
    {
        TaggerDescription tagger = Tagger();
        Assert.Empty(SdkValidation.ValidateTaggerRequest(Request(), tagger));
        Assert.Contains("wrong_tagger", Codes(SdkValidation.ValidateTaggerRequest(Request() with { TaggerKey = "other" }, tagger)));
        Assert.Contains("empty_batch", Codes(SdkValidation.ValidateTaggerRequest(Request() with { Items = [] }, tagger)));
        Assert.Contains("too_large", Codes(SdkValidation.ValidateTaggerRequest(Request(Enumerable.Range(0, 3).Select(i => Image("i" + i)).ToArray()),
            tagger with { MaximumBatchItems = 2 })));
        Assert.Contains("duplicate_item", Codes(SdkValidation.ValidateTaggerRequest(Request(Image("a"), Image("a")), tagger)));
        Assert.Contains("unsupported_media", Codes(SdkValidation.ValidateTaggerRequest(Request(Image("a") with { Kind = TaggerMediaKind.Audio }), tagger)));
        Assert.Contains("unknown_media", Codes(SdkValidation.ValidateTaggerRequest(Request(Image("a") with { Kind = TaggerMediaKind.Image | TaggerMediaKind.Video }), tagger)));
        Assert.Contains("unsupported_media", Codes(SdkValidation.ValidateTaggerRequest(Request(Image("a") with { MimeType = "image/webp" }), tagger)));
        Assert.Contains("image_size_limit", Codes(SdkValidation.ValidateTaggerRequest(Request(Image("a", SdkLimits.MaximumImageBytes + 1)), tagger)));
        Assert.Contains("input_size_limit", Codes(SdkValidation.ValidateTaggerRequest(Request(Image("a", 101)), tagger with { MaximumInputBytes = 100 })));
        Assert.Contains("invalid_hash", Codes(SdkValidation.ValidateTaggerRequest(Request(Image("a") with { ContentSha256 = "abc" }), tagger)));
        Assert.Contains("out_of_range", Codes(SdkValidation.ValidateTaggerRequest(Request() with { MinimumConfidence = double.NaN }, tagger)));
        Assert.Contains("out_of_range", Codes(SdkValidation.ValidateTaggerRequest(Request() with { MaximumSuggestionsPerItem = 513 }, tagger)));
    }

    [Fact]
    public void ResultMustAnswerExactlyTheBatchWithBoundedValidSuggestions()
    {
        TaggerDescription tagger = Tagger();
        TaggerBatchRequest request = Request(Image("a"), Video("b"));
        TaggerSuggestion tag = new("1girl", TaggerTagCategory.General, 0.9);
        Assert.Empty(SdkValidation.ValidateTaggerResult(request, Result(request, tag), tagger));

        TaggerBatchResult valid = Result(request, tag);
        Assert.Contains("wrong_batch", Codes(SdkValidation.ValidateTaggerResult(request, valid with { BatchId = "other" }, tagger)));
        Assert.Contains("undeclared_model", Codes(SdkValidation.ValidateTaggerResult(request, valid with
            { Provenance = new TaggerModelProvenance("reference-model", "r2") }, tagger)));
        Assert.Contains("model_mismatch", Codes(SdkValidation.ValidateTaggerResult(request, valid with
            { Provenance = new TaggerModelProvenance("reference-model", "r1", new string('f', 64)) }, tagger)));
        Assert.Contains("missing_item", Codes(SdkValidation.ValidateTaggerResult(request, valid with { Items = [valid.Items[0]] }, tagger)));
        Assert.Contains("duplicate_item", Codes(SdkValidation.ValidateTaggerResult(request, valid with { Items = [valid.Items[0], valid.Items[0]] }, tagger)));
        Assert.Contains("unknown_item", Codes(SdkValidation.ValidateTaggerResult(request, valid with
            { Items = [valid.Items[0], valid.Items[1] with { ItemId = "foreign" }] }, tagger)));
        Assert.Contains("unexpected_suggestions", Codes(SdkValidation.ValidateTaggerResult(request, valid with
            { Items = [valid.Items[0] with { Outcome = TaggerItemOutcome.Error }, valid.Items[1]] }, tagger)));
        Assert.Empty(SdkValidation.ValidateTaggerResult(request, valid with
            { Items = [new TaggerItemResult("a", TaggerItemOutcome.ModelUnavailable, [], "model_missing"), valid.Items[1]] }, tagger));

        Assert.Contains("out_of_range", Codes(SdkValidation.ValidateTaggerResult(request, Result(request, tag with { Confidence = 0.01 }), tagger)));
        Assert.Contains("out_of_range", Codes(SdkValidation.ValidateTaggerResult(request, Result(request, tag with { Confidence = double.PositiveInfinity }), tagger)));
        Assert.Contains("duplicate_suggestion", Codes(SdkValidation.ValidateTaggerResult(request, Result(request, tag, tag), tagger)));
        Assert.Contains("too_many_suggestions", Codes(SdkValidation.ValidateTaggerResult(request with { MaximumSuggestionsPerItem = 1 },
            Result(request, tag, tag with { Name = "solo" }), tagger)));
        foreach (string name in new[] { "", " padded", "line\nbreak", new string('x', 257) })
            Assert.Contains("invalid_tag", Codes(SdkValidation.ValidateTaggerResult(request, Result(request, tag with { Name = name }), tagger)));
        Assert.Contains("unknown_value", Codes(SdkValidation.ValidateTaggerResult(request, Result(request, tag with { Category = (TaggerTagCategory)99 }), tagger)));
    }

    [Fact]
    public void TimeRangesBelongToVideoAndStayInsideTheItem()
    {
        TaggerDescription tagger = Tagger();
        TaggerSuggestion ranged = new("running", TaggerTagCategory.General, 0.5, new TaggerTimeRange(1000, 2000));
        TaggerBatchRequest video = Request(Video("v", duration: 5000));
        Assert.Empty(SdkValidation.ValidateTaggerResult(video, Result(video, ranged), tagger));
        Assert.Contains("invalid_range", Codes(SdkValidation.ValidateTaggerResult(video, Result(video, ranged with { Range = new TaggerTimeRange(1000, 6000) }), tagger)));
        Assert.Contains("invalid_range", Codes(SdkValidation.ValidateTaggerResult(video, Result(video, ranged with { Range = new TaggerTimeRange(2000, 2000) }), tagger)));
        TaggerBatchRequest image = Request(Image("i"));
        Assert.Contains("unexpected_range", Codes(SdkValidation.ValidateTaggerResult(image, Result(image, ranged), tagger)));
        // The same name in two different ranges is two suggestions.
        Assert.Empty(SdkValidation.ValidateTaggerResult(video, Result(video, ranged, ranged with { Range = new TaggerTimeRange(3000, 4000) }), tagger));
    }

    [Fact]
    public void IssuesNeverEchoTagNamesOrTokens()
    {
        TaggerBatchRequest request = Request(Image("secret-item"));
        TaggerBatchResult result = Result(request, new TaggerSuggestion("private name\u0001", TaggerTagCategory.General, 0.9));
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateTaggerResult(request, result, Tagger());
        Assert.NotEmpty(issues);
        Assert.All(issues, issue => Assert.DoesNotContain("private name", issue.Message + issue.Path));
        Assert.All(issues, issue => Assert.DoesNotContain("token-secret", issue.Message + issue.Path));
    }
}
