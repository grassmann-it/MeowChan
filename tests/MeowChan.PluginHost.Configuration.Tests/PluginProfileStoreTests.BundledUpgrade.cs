using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    // Synthetic delivered-store fixtures, not historical-release provenance.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KnownDeliveredInPlaceRebuildPreservesLatestSettingsAndChoiceAcrossRestartsAndAnotherBuild(bool enabled)
    {
        var old = await Delivered("automatic-install", "one", source: true);
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetEnabledAsync("test.plugin", enabled, state.Revision);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 7) }, state.Revision);
        // Latest settings belong to OLD Pending, not a new independent candidate choice.
        var configuration = SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        var owners = SdkJson.Serialize(state.SelectedSourceOwners);
        var previousExecution = old.Candidate.Artifact!.ExecutionRevision;
        foreach (string build in new[] { "two", "three" })
        {
            var next = await Delivered("automatic-install", build, source: true);
            Assert.Equal(old.Candidate.CandidateId, next.Candidate.CandidateId);
            Assert.Equal(old.Proof.SlotId, next.Proof.SlotId);
            for (int restart = 0; restart < 2; restart++)
            {
                state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
                var plugin = Assert.Single(state.Plugins);
                Assert.Equal(enabled, plugin.Enabled);
                Assert.Equal(old.Candidate.CandidateId, plugin.SelectedCandidateId);
                Assert.Equal(next.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
                Assert.Equal(configuration, SdkJson.Serialize(Active(state).Configuration));
                Assert.Equal(previousExecution, plugin.LastGood!.ExecutionRevision);
                Assert.Null(plugin.Pending); Assert.Null(plugin.Pinned); Assert.Null(plugin.Problem);
                Assert.Empty(state.Grants);
                Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
                Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
            }
            previousExecution = next.Candidate.Artifact!.ExecutionRevision;
        }
    }

    [Fact]
    public async Task KnownDeliveredLastGoodRecoversAfterUnprovenStartupBlockedTheRebuild()
    {
        var old = await Delivered("recovery-install", "one");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await Delivered("recovery-install", "two");
        // Real store transitions create the blocked state; never edit an envelope to invent history.
        state = await Store().ApplyStartupAsync([next.Candidate], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
            Assert.Equal(next.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(old.Candidate.Artifact.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Null(state.Plugins[0].Problem);
        }
    }

    [Fact]
    public async Task PersistedDeliveredEvidenceAloneCannotAuthorizeAnOrdinaryStartupRebuild()
    {
        var old = await Delivered("persisted-install", "one");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await Delivered("persisted-install", "two");
        // Observe persists genuine current proof without making it this startup's authority.
        // A rights restriction safely prevents activation while recording that proof.
        state = await Store().SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        state = await Store().SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.None, state.Revision);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyStartupAsync([next.Candidate], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        }
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("delivered-assembly")]
    [InlineData("cache")]
    public async Task AutomaticBundledRebuildReverifiesCurrentBytesAndFailsClosed(string corruption)
    {
        var old = await Delivered("corrupt-install", "one");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await Delivered("corrupt-install", "two");
        string path = corruption switch
        {
            "manifest" => next.Candidate.CandidatePath!,
            "delivered-assembly" => Path.Combine(Path.GetDirectoryName(next.Candidate.CandidatePath!)!, "fixture.dll"),
            _ => next.Candidate.Artifact!.AssemblyPath
        };
        await File.WriteAllTextAsync(path, "tampered after delivery observation");
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Contains("integrity validation failed", state.Plugins[0].Problem);
            Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        }
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("schema")]
    [InlineData("local")]
    [InlineData("ambiguous-plugin")]
    [InlineData("ambiguous-slot")]
    [InlineData("unproven-old")]
    [InlineData("grant")]
    [InlineData("restricted-source")]
    [InlineData("pin")]
    public async Task AutomaticInPlaceRebuildDoesNotBroadenEligibility(string guard)
    {
        var old = await Delivered("guard-install", "one", source: true);
        var state = guard == "unproven-old"
            ? await Store().ApplyStartupAsync([old.Candidate], 0)
            : await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        if (guard == "grant")
            state = await Store().SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        if (guard == "pin") state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        var next = await Delivered("guard-install", "two", source: true,
            settings: guard == "schema" ? NumberSchema("2", 99) : null);
        if (guard == "capability")
        {
            var description = next.Candidate.Description!;
            next.Candidate = next.Candidate with { Description = description with
                { Sources = [description.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture }] } };
            var root = Path.GetDirectoryName(next.Candidate.CandidatePath!)!;
            var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(root)).Candidates,
                c => c.Kind == PluginCandidateKind.PrebuiltManifest);
            next.Proof = await PluginInstallationInventory.RecordDeliveredAsync(root, installed,
                next.Candidate.Artifact!, next.Candidate.Description!);
        }
        var candidates = new List<PluginCandidateObservation> { next.Candidate };
        var proofs = new List<DeliveredCandidateProvenance>();
        if (guard != "local") proofs.Add(next.Proof);
        if (guard == "ambiguous-plugin") candidates.Add(next.Candidate with
            { CandidateId = "independent-local-choice", CandidatePath = Path.Combine(profile, "plugins", "local.cs") });
        if (guard == "ambiguous-slot")
        {
            var duplicate = await Delivered("duplicate-install", "two", source: true, fileName: "FIXTURE.plugin.json");
            candidates.Add(duplicate.Candidate); proofs.Add(duplicate.Proof);
        }
        var grants = SdkJson.Serialize(state.Grants);
        var owners = SdkJson.Serialize(state.SelectedSourceOwners);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync(candidates, proofs,
                guard == "restricted-source" ? ["shared"] : [], state.Revision);
            Assert.NotEqual(next.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Active?.ExecutionRevision);
            Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.True(state.Plugins[0].Enabled);
            if (guard == "pin") Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedBindingFollowsLaterInPlaceRebuildOnlyAfterTargetActuallyActivated(bool activated)
    {
        var old = await Delivered("binding-old", "one");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var target = await Delivered("binding-target", "two");
        state = await Store().ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var preview = await Store().PreviewBundledRepairsAsync();
        state = await Store().ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        Assert.Null(state.Plugins[0].Active);
        if (activated)
        {
            state = await Store().ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
            Assert.Equal(target.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
        }
        var next = await Delivered("binding-target", "three");
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
            Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            if (activated)
            {
                Assert.Equal(next.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
                Assert.Equal(target.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
                Assert.Null(state.Plugins[0].Problem);
            }
            else
            {
                Assert.Null(state.Plugins[0].Active);
                Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
                Assert.Equal(target.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
            }
        }
    }

}
