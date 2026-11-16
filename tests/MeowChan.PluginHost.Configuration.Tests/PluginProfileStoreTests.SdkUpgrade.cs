using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    // These are synthetic delivered manifests and profiles, never the installed profile.
    private async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> SdkDelivery(
        string installation, string build, string sdk = "1.0.0", Func<PluginDescription, PluginDescription>? change = null,
        bool requiredSetting = false, string fileName = "fixture.plugin.json")
    {
        var delivered = await Delivered(installation, build, fileName: fileName, source: true, requiredSetting: requiredSetting);
        PluginDescription description = delivered.Candidate.Description! with { SdkVersion = sdk };
        if (change is not null) description = change(description);
        delivered.Candidate = delivered.Candidate with { PluginId = description.PluginId, Description = description };
        string root = Path.GetDirectoryName(delivered.Candidate.CandidatePath!)!;
        var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(root)).Candidates,
            c => c.Kind == PluginCandidateKind.PrebuiltManifest);
        delivered.Proof = await PluginInstallationInventory.RecordDeliveredAsync(root, installed, delivered.Candidate.Artifact!, description);
        return delivered;
    }

    private static async Task<JsonElement> StoredSdkBinding(PluginProfileStore store)
    {
        using JsonDocument envelope = JsonDocument.Parse(await File.ReadAllBytesAsync(store.StorePath));
        byte[] payload = envelope.RootElement.GetProperty("payload").GetBytesFromBase64();
        using JsonDocument document = JsonDocument.Parse(payload);
        return Assert.Single(document.RootElement.GetProperty("bindings").EnumerateArray()).Clone();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    public async Task SdkOnlyInPlaceUpgradeRetainsExactChoiceSettingsAndRights(bool enabled, bool sourceRight, bool filterRight)
    {
        PluginDescription SourceSettings(PluginDescription d) => d with { Sources = [d.Sources[0] with
        {
            Settings = new("1", [new("limit", SettingScope.Source, SettingKind.Integer, "Limit",
                Default: new("limit", SettingKind.Integer, Integer: 1))])
        }] };
        var old = await SdkDelivery("sdk-incident", "old", change: SourceSettings);
        var store = Store(); string[] restricted = sourceRight ? ["shared"] : [];
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], restricted, 0);
        state = await store.SetEnabledAsync("test.plugin", enabled, state.Revision);
        state = await store.SelectCandidateAsync("test.plugin", old.Candidate.CandidateId, state.Revision);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 73), SourceSettings =
                new Dictionary<string, SettingsSnapshot> { ["shared"] = new("1", [new("limit", SettingKind.Integer, Integer: 42)]) } }, state.Revision);
        if (filterRight) state = await AddReviewedFilterGrantsAsync(store, state);
        byte[] configuration = SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        byte[] grants = SdkJson.Serialize(state.Grants), owners = SdkJson.Serialize(state.SelectedSourceOwners);
        var next = await SdkDelivery("sdk-incident", "new", "1.1.0", SourceSettings);
        Assert.NotEqual(old.Proof.DescriptionSha256, next.Proof.DescriptionSha256);
        Assert.NotEqual(old.Proof.ExecutionRevision, next.Proof.ExecutionRevision);
        for (int i = 0; i < 2; i++)
        {
            state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], restricted, state.Revision);
            var plugin = Assert.Single(state.Plugins);
            Assert.Equal(next.Proof.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(old.Proof.ExecutionRevision, plugin.LastGood!.ExecutionRevision);
            Assert.Equal(old.Candidate.CandidateId, plugin.SelectedCandidateId);
            Assert.Equal(enabled, plugin.Enabled);
            Assert.Equal(configuration, SdkJson.Serialize(Active(state).Configuration));
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Null(plugin.Pending); Assert.Null(plugin.Pinned); Assert.Null(plugin.Problem);
        }
    }

    [Fact]
    public async Task AutomaticSdkTransitionOffersDurableOptionalExplicitReviewForExactActiveBuild()
    {
        var old = await SdkDelivery("sdk-review", "old");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await SdkDelivery("sdk-review", "new", "1.1.0");

        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        PluginActivationSnapshot automatic = Assert.Single(state.Plugins);
        Assert.Equal(new PluginSdkTransitionSnapshot("1.0.0", "1.1.0", next.Proof.ExecutionRevision),
            automatic.AutomaticSdkTransition);
        Assert.False(automatic.AutomaticSdkTransition!.ExplicitlyApproved);

        state = await store.ExplicitlyApproveSdkTransitionAsync("test.plugin", next.Proof.ExecutionRevision, state.Revision);
        PluginActivationSnapshot reviewed = Assert.Single(state.Plugins);
        Assert.True(reviewed.AutomaticSdkTransition!.ExplicitlyApproved);
        Assert.Equal(next.Proof.ExecutionRevision, reviewed.Active!.ExecutionRevision);

        // A later offline startup retains the acknowledgement for the same exact build;
        // it does not reopen the automatic-review action or alter the active release.
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        PluginActivationSnapshot persisted = Assert.Single(state.Plugins);
        Assert.True(persisted.AutomaticSdkTransition!.ExplicitlyApproved);
        Assert.Equal(next.Proof.ExecutionRevision, persisted.Active!.ExecutionRevision);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("metadata")]
    [InlineData("version")]
    [InlineData("capability")]
    [InlineData("route")]
    [InlineData("host")]
    [InlineData("filter-access")]
    [InlineData("filter-name")]
    [InlineData("schema")]
    [InlineData("source-schema")]
    [InlineData("reverse")]
    [InlineData("unknown")]
    public async Task SdkUpgradeRejectsAllOtherDescriptionChanges(string mutation)
    {
        string oldSdk = mutation == "reverse" ? "1.1.0" : "1.0.0";
        string newSdk = mutation == "reverse" ? "1.0.0" : mutation == "unknown" ? "1.2.0" : "1.1.0";
        var old = await SdkDelivery("sdk-material", "old", oldSdk);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await AddReviewedFilterGrantsAsync(Store(), state);
        var next = await SdkDelivery("sdk-material", "new", newSdk, d => mutation switch
        {
            "name" => d with { Name = "Other" },
            "metadata" => d with { Author = "Other" },
            "version" => d with { PluginVersion = "2.0" },
            "capability" => d with { Sources = [d.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture }] },
            "route" => d with { Sources = [d.Sources[0] with { Routes = [d.Sources[0].Routes[0] with { PathTemplate = "/{board}/t/{thread}" }] }] },
            "host" => d with { Sources = [d.Sources[0] with { HttpHosts = [new("other.test", HttpHostPurpose.Metadata)] }] },
            "filter-access" => d with { Filters = [d.Filters[0] with { RequiredAccess = FilterDataAccess.CandidateMetadata }] },
            "filter-name" => d with { Filters = [d.Filters[0] with { Name = "Other" }] },
            "schema" => d with { Settings = NumberSchema("2", 99) },
            "source-schema" => d with { Sources = [d.Sources[0] with { Settings = new("2", []) }] },
            _ => d
        });
        byte[] grants = SdkJson.Serialize(state.Grants);
        if (mutation == "unknown")
        {
            await Assert.ThrowsAsync<PluginConfigurationValidationException>(() =>
                Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision));
            return;
        }
        state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(old.Proof.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("assembly")]
    [InlineData("cache")]
    [InlineData("unproven")]
    [InlineData("moved")]
    [InlineData("second")]
    [InlineData("slot")]
    [InlineData("pin")]
    [InlineData("unserialized")]
    public async Task SdkUpgradeFailsClosedForIntegrityAndAuthorityGuards(string guard)
    {
        var old = await SdkDelivery("sdk-guard", "old");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await AddReviewedFilterGrantsAsync(Store(), state);
        if (guard == "pin") state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        var next = await SdkDelivery(guard == "moved" ? "sdk-moved" : "sdk-guard", "new", "1.1.0");
        if (guard is "manifest" or "assembly" or "cache")
        {
            string path = guard == "manifest" ? next.Candidate.CandidatePath! : guard == "assembly"
                ? Path.Combine(Path.GetDirectoryName(next.Candidate.CandidatePath!)!, "fixture.dll") : next.Candidate.Artifact!.AssemblyPath;
            await File.WriteAllTextAsync(path, "tampered");
        }
        var candidates = new List<PluginCandidateObservation> { next.Candidate };
        var proofs = new List<DeliveredCandidateProvenance>();
        if (guard != "unproven") proofs.Add(next.Proof);
        if (guard == "second") candidates.Add(next.Candidate with { CandidateId = "other", CandidatePath = Path.Combine(profile, "other.cs") });
        if (guard == "slot")
        {
            var other = await Delivered("sdk-duplicate", "other", fileName: "FIXTURE.plugin.json", source: true);
            candidates.Add(other.Candidate); proofs.Add(other.Proof);
        }
        PluginProfileStore store = guard == "unserialized" ? new(profile, static _ => true, () => ["shared"]) : Store();
        byte[] grants = SdkJson.Serialize(state.Grants);
        state = await store.ApplyBundledStartupAsync(candidates, proofs, ["shared"], state.Revision);
        Assert.NotEqual(next.Proof.ExecutionRevision, state.Plugins[0].Active?.ExecutionRevision);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
    }

    [Fact]
    public async Task SdkUpgradeCannotBorrowPendingOnlyAuthority()
    {
        var old = await SdkDelivery("sdk-pending", "old", requiredSetting: true);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
        state = await Store().SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            new("pending", Values("1", 7), new Dictionary<string, SettingsSnapshot> { ["shared"] = new("1", []) }), state.Revision);
        var next = await SdkDelivery("sdk-pending", "new", "1.1.0", requiredSetting: true);
        state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
        Assert.Equal(old.Proof.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkUpgradeOfConfirmedBindingRequiresActivatedTargetAndClearsExplicitConsent(bool activateTarget)
    {
        var old = await SdkDelivery("sdk-binding-old", "A");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await AddReviewedFilterGrantsAsync(store, state);
        var target = await SdkDelivery("sdk-binding-target", "B");
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], ["shared"], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ReauthorizeBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        JsonElement binding = await StoredSdkBinding(store);
        string fingerprint = binding.GetProperty("reauthorizedPermissionFingerprint").GetString()!;
        Assert.False(string.IsNullOrEmpty(fingerprint));
        string original = binding.GetProperty("originalRelease").GetRawText();
        string selected = binding.GetProperty("selectedCandidateId").GetString()!;
        if (activateTarget)
        {
            state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], ["shared"], state.Revision);
            Assert.Equal(target.Proof.ExecutionRevision, Active(state).ExecutionRevision);
        }
        var next = await SdkDelivery("sdk-binding-target", "C", "1.1.0");
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
        binding = await StoredSdkBinding(store);
        Assert.Equal(original, binding.GetProperty("originalRelease").GetRawText());
        Assert.Equal(selected, binding.GetProperty("selectedCandidateId").GetString());
        if (!activateTarget)
        {
            Assert.Null(state.Plugins[0].Active);
            Assert.Equal(target.Proof.ExecutionRevision, binding.GetProperty("targetExecutionRevision").GetString());
            Assert.Equal(fingerprint, binding.GetProperty("reauthorizedPermissionFingerprint").GetString());
            return;
        }
        Assert.Equal(next.Proof.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(next.Proof.ExecutionRevision, binding.GetProperty("targetExecutionRevision").GetString());
        Assert.True(!binding.TryGetProperty("reauthorizedPermissionFingerprint", out var stored) || stored.ValueKind == JsonValueKind.Null);
        byte[] grants = SdkJson.Serialize(state.Grants);
        // Decision #130: an ordinary in-place rebuild of the established 1.1.0 build (same folder, SDK version
        // strictly equal, identical declaration) carries the rights over. This is not a second use of the SDK bridge.
        var rebuild = await SdkDelivery("sdk-binding-target", "D", "1.1.0");
        state = await store.ApplyBundledStartupAsync([rebuild.Candidate], [rebuild.Proof], ["shared"], state.Revision);
        Assert.Equal(rebuild.Proof.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Null(state.Plugins[0].AutomaticSdkTransition);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        binding = await StoredSdkBinding(store);
        Assert.Equal(rebuild.Proof.ExecutionRevision, binding.GetProperty("targetExecutionRevision").GetString());
        Assert.True(!binding.TryGetProperty("reauthorizedPermissionFingerprint", out stored) || stored.ValueKind == JsonValueKind.Null);
        Assert.Empty((await store.PreviewBundledRepairsAsync()).Rows);
        // Any further SDK version change is never automatic: the bridge is used up and the in-place rule needs an equal SDK.
        var reverse = await SdkDelivery("sdk-binding-target", "E", "1.0.0");
        state = await store.ApplyBundledStartupAsync([reverse.Candidate], [reverse.Proof], ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        var blocked = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        Assert.False(blocked.CanConfirm);
        Assert.False(blocked.CanReauthorize);
    }
    [Fact]
    public async Task TwentyThreeLegacyCandidatesRecoverAfterUnprovenStartupWithoutChangingRights()
    {
        var olds = new List<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)>();
        var news = new List<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)>();
        for (int i = 0; i < 23; i++)
        {
            string id = "incident.plugin." + i, source = "incident-source-" + i;
            PluginDescription Customize(PluginDescription d) => d with { PluginId = id,
                Sources = [d.Sources[0] with { SourceId = source }] };
            string root = "sdk-incident-" + i, file = "incident-" + i + ".plugin.json";
            olds.Add(await SdkDelivery(root, "old-" + i, change: Customize, fileName: file));
        }
        var store = Store();
        string[] restricted = Enumerable.Range(0, 23).Select(i => "incident-source-" + i).ToArray();
        var state = await store.ApplyBundledStartupAsync(olds.Select(x => x.Candidate).ToArray(), olds.Select(x => x.Proof).ToArray(), restricted, 0);
        for (int i = 0; i < 23; i++)
        {
            string id = "incident.plugin." + i, source = "incident-source-" + i;
            state = await store.SetEnabledAsync(id, i % 2 == 0, state.Revision);
            state = await store.SetGrantAsync(id, "dimensions", new(source, "board", "rule"), FilterDataAccess.OriginalImage, state.Revision);
            PluginDescription Customize(PluginDescription d) => d with { PluginId = id,
                Sources = [d.Sources[0] with { SourceId = source }] };
            news.Add(await SdkDelivery("sdk-incident-" + i, "new-" + i, "1.1.0", Customize, fileName: "incident-" + i + ".plugin.json"));
        }
        var configurations = state.Plugins.ToDictionary(p => p.PluginId, p => SdkJson.Serialize(p.Active!.Configuration));
        byte[] grants = SdkJson.Serialize(state.Grants), owners = SdkJson.Serialize(state.SelectedSourceOwners);
        state = await store.ApplyStartupAsync(news.Select(x => x.Candidate).ToArray(), state.Revision);
        Assert.All(state.Plugins, p => Assert.Null(p.Active));
        state = await store.ApplyBundledStartupAsync(news.Select(x => x.Candidate).ToArray(), news.Select(x => x.Proof).ToArray(), restricted, state.Revision);
        for (int i = 0; i < 23; i++)
        {
            var plugin = Assert.Single(state.Plugins, p => p.PluginId == "incident.plugin." + i);
            Assert.Equal(news[i].Proof.ExecutionRevision, plugin.Active!.ExecutionRevision);
            Assert.Equal(olds[i].Proof.ExecutionRevision, plugin.LastGood!.ExecutionRevision);
            Assert.Equal(i % 2 == 0, plugin.Enabled);
            Assert.Equal(configurations[plugin.PluginId], SdkJson.Serialize(plugin.Active.Configuration));
        }
        Assert.Equal(grants, SdkJson.Serialize(state.Grants)); Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("scope")]
    [InlineData("source")]
    public async Task SdkUpgradeCannotUseStaleBindingWhenReviewedRightsChange(string change)
    {
        var old = await SdkDelivery("sdk-rights-old", "A");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"), FilterDataAccess.OriginalImage, state.Revision);
        var target = await SdkDelivery("sdk-rights-target", "B");
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], ["shared"], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ReauthorizeBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], ["shared"], state.Revision);
        if (change == "revision") state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"), FilterDataAccess.OriginalImage, state.Revision);
        if (change == "scope") state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "other", "rule"), FilterDataAccess.CandidateMetadata, state.Revision);
        var next = await SdkDelivery("sdk-rights-target", "C", "1.1.0");
        byte[] grants = SdkJson.Serialize(state.Grants);
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], change == "source" ? [] : ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(target.Proof.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
    }

    [Fact]
    public async Task SdkUpgradeCannotOverrideCompetingSourceOwner()
    {
        var old = await SdkDelivery("sdk-owner", "A");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared"), FilterDataAccess.OriginalImage, state.Revision);
        var next = await SdkDelivery("sdk-owner", "B", "1.1.0");
        var competitor = next.Candidate with { CandidateId = "other-owner", PluginId = "other.plugin",
            Description = next.Candidate.Description! with { PluginId = "other.plugin" } };
        state = await Store().ApplyBundledStartupAsync([next.Candidate, competitor], [next.Proof], ["shared"], state.Revision);
        Assert.Null(Assert.Single(state.Plugins, p => p.PluginId == "test.plugin").Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkUpgradeRechecksLivePermissionAuthorityAfterIntegrityVerification(bool remove)
    {
        using SemaphoreSlim gate = new(1, 1);
        int calls = 0;
        PluginProfileStore store = new(profile, static _ => true,
            () => (++calls == 1) == remove ? ["shared"] : [], gate);
        var old = await SdkDelivery("sdk-live", "A");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await SdkDelivery("sdk-live", "B", "1.1.0");
        // The startup snapshot already contains the restriction; the raw live
        // authority must still be checked for both additions and removals.
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Contains("authority changed", state.Plugins[0].Problem);
    }

    [Fact]
    public async Task SdkUpgradeRejectsStaleRevisionAndMismatchedProvenanceWithoutWriting()
    {
        var old = await SdkDelivery("sdk-revision", "old");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        var next = await SdkDelivery("sdk-revision", "new", "1.1.0");
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<PluginProfileConflictException>(() =>
            store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision - 1));
        var forged = next.Proof with { DescriptionSha256 = old.Proof.DescriptionSha256 };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ApplyBundledStartupAsync([next.Candidate], [forged], ["shared"], state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

}
