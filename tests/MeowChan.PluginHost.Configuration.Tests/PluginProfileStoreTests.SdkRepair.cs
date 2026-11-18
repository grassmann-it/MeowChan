using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    // Synthetic delivered fixtures only. When the automatic SDK transition refuses, the
    // repair dialog must reach the ordinary review reasons rather than description-changed.
    [Theory]
    [InlineData(false, "revision")]
    [InlineData(true, "revision")]
    [InlineData(false, "scope")]
    [InlineData(true, "scope")]
    public async Task SdkOnlyRebuildWithChangedConsentOffersReauthorizationAndThenActivates(bool gated, string change)
    {
        using SemaphoreSlim gate = new(1, 1);
        PluginProfileStore Open() => gated ? new(profile, static _ => true, () => ["shared"], gate) : Store();
        var old = await SdkDelivery("sdk-way-out-old", "A");
        var store = Open();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"), FilterDataAccess.OriginalImage, state.Revision);
        var target = await SdkDelivery("sdk-way-out-target", "B");
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], ["shared"], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ReauthorizeBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], ["shared"], state.Revision);
        Assert.Equal(target.Proof.ExecutionRevision, Active(state).ExecutionRevision);
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        byte[] configuration = SdkJson.Serialize(Active(state).Configuration);
        if (change == "revision") state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"), FilterDataAccess.OriginalImage, state.Revision);
        else state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "other", "rule"), FilterDataAccess.CandidateMetadata, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants), owners = SdkJson.Serialize(state.SelectedSourceOwners);
        string selected = state.Plugins[0].SelectedCandidateId!;
        var next = await SdkDelivery("sdk-way-out-target", "C", "1.1.0");
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active);

        preview = await store.PreviewBundledRepairsAsync();
        BundledCandidateRepair row = Assert.Single(preview.Rows);
        Assert.Equal("inherited-grants", row.BlockedReason);
        Assert.False(row.CanConfirm);
        Assert.True(row.CanReauthorize);
        Assert.Equal(next.Candidate.CandidateId, row.NewCandidateId);
        Assert.Equal(next.Proof.ExecutionRevision, row.NewExecutionRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        state = await store.ReauthorizeBundledRepairAsync(row, preview.ProfileRevision);
        Assert.Null(state.Plugins[0].Active);
        for (int i = 0; i < 2; i++)
        {
            state = await Open().ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
            PluginActivationSnapshot plugin = Assert.Single(state.Plugins);
            Assert.Equal(next.Proof.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal("1.1.0", Active(state).Description.SdkVersion);
            Assert.Equal(target.Proof.ExecutionRevision, plugin.LastGood!.ExecutionRevision);
            Assert.Equal(selected, plugin.SelectedCandidateId);
            Assert.False(plugin.Enabled);
            Assert.Equal(configuration, SdkJson.Serialize(Active(state).Configuration));
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Null(plugin.Pending); Assert.Null(plugin.Pinned); Assert.Null(plugin.Problem);
            Assert.Empty((await store.PreviewBundledRepairsAsync()).Rows);
        }
    }

    [Fact]
    public async Task SdkOnlyRebuildOfUnactivatedConfirmedRepairCanBeConfirmedAndThenActivates()
    {
        var old = await SdkDelivery("sdk-confirm-old", "A");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        byte[] configuration = SdkJson.Serialize(Active(state).Configuration);
        var target = await SdkDelivery("sdk-confirm-target", "B");
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        byte[] owners = SdkJson.Serialize(state.SelectedSourceOwners);
        // The confirmed target was never activated, so the automatic transition has no authority.
        var next = await SdkDelivery("sdk-confirm-target", "C", "1.1.0");
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);

        preview = await store.PreviewBundledRepairsAsync();
        BundledCandidateRepair row = Assert.Single(preview.Rows);
        Assert.Null(row.BlockedReason);
        Assert.True(row.CanConfirm);
        Assert.False(row.CanReauthorize);
        Assert.Equal(next.Proof.ExecutionRevision, row.NewExecutionRevision);
        state = await store.ConfirmBundledRepairAsync(row, preview.ProfileRevision);
        state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        PluginActivationSnapshot plugin = Assert.Single(state.Plugins);
        Assert.Equal(next.Proof.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(old.Proof.ExecutionRevision, plugin.LastGood!.ExecutionRevision);
        Assert.Equal(old.Candidate.CandidateId, plugin.SelectedCandidateId);
        Assert.False(plugin.Enabled);
        Assert.Equal(configuration, SdkJson.Serialize(Active(state).Configuration));
        Assert.Empty(state.Grants);
        Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
        Assert.Null(plugin.Problem);
        Assert.Empty((await store.PreviewBundledRepairsAsync()).Rows);
    }

    // Only the SDK field is exempt: SDK plus anything else, reverse and unknown remain blocked.
    [Theory]
    [InlineData("name")]
    [InlineData("metadata")]
    [InlineData("capability")]
    [InlineData("host")]
    [InlineData("filter-access")]
    [InlineData("schema")]
    [InlineData("reverse")]
    [InlineData("unknown")]
    public async Task SdkRepairPreviewKeepsEveryOtherDescriptionChangeBlocked(string mutation)
    {
        string oldSdk = mutation == "reverse" ? "1.1.0" : "1.0.0";
        string newSdk = mutation == "reverse" ? "1.0.0" : mutation == "unknown" ? "1.2.0" : "1.1.0";
        var old = await SdkDelivery("sdk-preview-material", "old", oldSdk);
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await AddReviewedFilterGrantsAsync(store, state);
        var next = await SdkDelivery("sdk-preview-material", "new", newSdk, d => mutation switch
        {
            "name" => d with { Name = "Other" },
            "metadata" => d with { Author = "Other" },
            "capability" => d with { Sources = [d.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture }] },
            "host" => d with { Sources = [d.Sources[0] with { HttpHosts = [new("other.test", HttpHostPurpose.Metadata)] }] },
            "filter-access" => d with { Filters = [d.Filters[0] with { RequiredAccess = FilterDataAccess.CandidateMetadata }] },
            "schema" => d with { Settings = NumberSchema("2", 99) },
            _ => d
        });
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        if (mutation == "unknown")
        {
            await Assert.ThrowsAsync<PluginConfigurationValidationException>(() =>
                store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
            Assert.Empty((await store.PreviewBundledRepairsAsync()).Rows);
            return;
        }
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        var preview = await store.PreviewBundledRepairsAsync();
        BundledCandidateRepair row = Assert.Single(preview.Rows);
        Assert.Equal("description-changed", row.BlockedReason);
        Assert.False(row.CanConfirm);
        Assert.False(row.CanReauthorize);
        before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReauthorizeBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

    // Regression: a failed 1.1.0 activation leaves LastGood 1.0.0, which must not let the SDK
    // bridge be used a second time for a rebuild (or a retry) to inherit rights unreviewed.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SdkBridgeIsUsedUpByAcceptedBuildEvenAfterItsActivationFails(bool filterRight, bool sourceRight, bool retrySame)
    {
        string[] restricted = sourceRight ? ["shared"] : [];
        var old = await SdkDelivery("sdk-once", "A");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], restricted, 0);
        if (filterRight) state = await AddReviewedFilterGrantsAsync(store, state);
        byte[] configuration = SdkJson.Serialize(Active(state).Configuration);
        var adopted = await SdkDelivery("sdk-once", "B", "1.1.0");
        state = await store.ApplyBundledStartupAsync([adopted.Candidate], [adopted.Proof], restricted, state.Revision);
        Assert.Equal(adopted.Proof.ExecutionRevision, Active(state).ExecutionRevision);
        state = await store.ReportActivationFailureAsync("test.plugin", adopted.Proof.ExecutionRevision, "Synthetic load failure.", state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Null(state.Plugins[0].AutomaticSdkTransition);
        Assert.Equal(old.Proof.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        Assert.Equal("1.0.0", state.Plugins[0].LastGood!.Description.SdkVersion);

        var rebuild = retrySame ? adopted : await SdkDelivery("sdk-once", "C", "1.1.0");
        byte[] grants = SdkJson.Serialize(state.Grants), owners = SdkJson.Serialize(state.SelectedSourceOwners);
        for (int i = 0; i < 2; i++)
        {
            state = await Store().ApplyBundledStartupAsync([rebuild.Candidate], [rebuild.Proof], restricted, state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Null(state.Plugins[0].AutomaticSdkTransition);
            Assert.Equal(old.Proof.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
        }
        var preview = await store.PreviewBundledRepairsAsync();
        BundledCandidateRepair row = Assert.Single(preview.Rows);
        Assert.Equal("inherited-grants", row.BlockedReason);
        Assert.False(row.CanConfirm);
        Assert.True(row.CanReauthorize);
        Assert.Equal(rebuild.Proof.ExecutionRevision, row.NewExecutionRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        state = await store.ReauthorizeBundledRepairAsync(row, preview.ProfileRevision);
        state = await Store().ApplyBundledStartupAsync([rebuild.Candidate], [rebuild.Proof], restricted, state.Revision);
        Assert.Equal(rebuild.Proof.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(configuration, SdkJson.Serialize(Active(state).Configuration));
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        Assert.Null(state.Plugins[0].Problem);
    }

    [Fact]
    public async Task SdkUpgradeActivatesThePendingSameBuildSettingsItValidated()
    {
        var old = await SdkDelivery("sdk-pending-settings", "old");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 91) }, state.Revision);
        PluginReleaseSnapshot pending = state.Plugins[0].Pending!;
        Assert.Equal(old.Proof.ExecutionRevision, pending.ExecutionRevision);
        Assert.NotEqual(91, Number(Active(state)));
        byte[] configuration = SdkJson.Serialize(pending.Configuration);
        var next = await SdkDelivery("sdk-pending-settings", "new", "1.1.0");
        state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        Assert.Equal(next.Proof.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(91, Number(Active(state)));
        Assert.Equal(configuration, SdkJson.Serialize(Active(state).Configuration));
        Assert.Null(state.Plugins[0].Pending); Assert.Null(state.Plugins[0].Problem);
    }
}
