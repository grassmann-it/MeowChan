using System.Security.Cryptography;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task DeliveredRebuildKeepsTheSourceAcrossInstallations(bool moved, bool previouslyMissing, bool enabled)
    {
        var old = await Delivered("MeowChan-1.1.0", "one", source: true);
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetEnabledAsync("test.plugin", enabled, state.Revision);
        state = await store.SelectCandidateAsync("test.plugin", old.Candidate.CandidateId, state.Revision);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 7) }, state.Revision);
        state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        if (previouslyMissing)
            state = await store.ApplyBundledStartupAsync([], [], [], state.Revision);

        var next = await Delivered(moved ? "MeowChan-1.1.1" : "MeowChan-1.1.0", "two", source: true,
            sdkFingerprint: previouslyMissing ? new('d', 64) : null);
        Assert.Equal(moved, old.Candidate.CandidateId != next.Candidate.CandidateId);
        Assert.NotEqual(old.Candidate.Artifact!.AssemblySha256, next.Candidate.Artifact!.AssemblySha256);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
            var plugin = Assert.Single(state.Plugins);
            Assert.Equal(next.Candidate.CandidateId, plugin.SelectedCandidateId);
            Assert.Equal(next.Candidate.Artifact.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal("shared", Assert.Single(Active(state).Description.Sources).SourceId);
            Assert.Equal("test.plugin", state.SelectedSourceOwners["shared"]);
            Assert.Equal(7, Number(Active(state)));
            Assert.Equal(old.Candidate.Artifact.ExecutionRevision, plugin.LastGood!.ExecutionRevision);
            Assert.Equal(enabled, plugin.Enabled);
            Assert.Null(plugin.Problem);
            Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingSettingsForAChangedInstalledBuildKeepsItLockedAcrossRestarts(bool sourceFile)
    {
        var old = await Delivered("user-install", "one");
        var store = Store();
        if (sourceFile)
        {
            string local = Path.Combine(profile, "plugins");
            Directory.CreateDirectory(local);
            await File.WriteAllTextAsync(Path.Combine(local, "fixture.cs"), "// Local source candidate");
            var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(local)).Candidates);
            Assert.Equal(PluginCandidateKind.Source, installed.Kind);
            old.Candidate = old.Candidate with { CandidateId = installed.CandidateId, CandidatePath = installed.Path };
        }
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [], [], 0);
        PluginGrantScope scope = new("source", "board", "rule");
        state = await store.SetGrantAsync("test.plugin", "dimensions", scope, FilterDataAccess.OriginalImage, state.Revision);
        var priorGrant = Assert.Single(state.Grants);
        var next = await Delivered("user-install", "two");
        if (sourceFile) next.Candidate = next.Candidate with { CandidateId = old.Candidate.CandidateId, CandidatePath = old.Candidate.CandidatePath };
        Assert.Equal(old.Candidate.CandidateId, next.Candidate.CandidateId);
        Assert.NotEqual(old.Candidate.Artifact!.AssemblySha256, next.Candidate.Artifact!.AssemblySha256);
        var proposed = Active(state).Configuration with { PluginSettings = Values("1", 17) };
        state = await store.ApplyBundledStartupAsync([next.Candidate], [], [], state.Revision);
        Assert.Null(Assert.Single(state.Plugins).Active);
        Assert.Contains("Select the new build", state.Plugins[0].Problem);
        state = await store.SaveConfigurationAsync("test.plugin", next.Candidate.CandidateId, proposed, state.Revision);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([next.Candidate], [], [], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Null(state.Plugins[0].Pending);
            Assert.Equal(old.Candidate.Artifact.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Contains("Select the new build", state.Plugins[0].Problem);
            Assert.Equal(priorGrant, Assert.Single(state.Grants));
        }
        state = await Store().SelectCandidateAsync("test.plugin", next.Candidate.CandidateId, state.Revision);
        Assert.Equal(17, Number(state.Plugins[0].Pending!));
        Assert.Equal(priorGrant, Assert.Single(state.Grants));
        state = await Store().ApplyBundledStartupAsync([next.Candidate], [], [], state.Revision);
        Assert.Equal(next.Candidate.Artifact.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(17, Number(Active(state)));
        Assert.Equal(FilterDataAccess.None, PluginGrantAuthorization.Resolve(state, "test.plugin", "dimensions", "source", "board", "rule").Access);
        Assert.Equal(priorGrant.Revision + 1, Assert.Single(state.Grants).Revision);
    }

    [Fact]
    public async Task ExplicitInstalledSelectionSurvivesSettingsCorrectionWithoutApprovingALaterBuild()
    {
        var old = await Delivered("local-revision", "one");
        var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        var required = new SettingsSchema("2", [new("flag", SettingScope.Plugin, SettingKind.Boolean, "Flag", IsRequired: true)]);
        var next = await Delivered("local-revision", "two", settings: required);
        state = await store.ApplyStartupAsync([next.Candidate], state.Revision);
        state = await store.SelectCandidateAsync("test.plugin", next.Candidate.CandidateId, state.Revision);
        Assert.Null(state.Plugins[0].Pending);
        var proposed = new PluginConfiguration("draft", new("2", [new("flag", SettingKind.Boolean, Boolean: true)]),
            new Dictionary<string, SettingsSnapshot>());
        state = await Store().SaveConfigurationAsync("test.plugin", next.Candidate.CandidateId, proposed, state.Revision);
        Assert.Equal(next.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);

        var later = await Delivered("local-revision", "three", settings: required);
        state = await Store().ApplyStartupAsync([later.Candidate], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Contains("Select the new build", state.Plugins[0].Problem);
        state = await Store().SaveConfigurationAsync("test.plugin", later.Candidate.CandidateId, proposed, state.Revision);
        state = await Store().ApplyStartupAsync([later.Candidate], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(next.Candidate.Artifact.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
    }

    [Fact]
    public async Task LegacyPendingForAChangedInstalledBuildBecomesADraftUntilSelected()
    {
        var first = await Delivered("legacy-installed", "one");
        var store = Store();
        var state = await store.ApplyStartupAsync([first.Candidate], 0);
        var next = await Delivered("legacy-installed", "two");
        state = await store.ApplyStartupAsync([next.Candidate], state.Revision);

        // Both old commands wrote this same snapshot, so the envelope cannot establish explicit intent.
        var plugin = Assert.Single(state.Plugins);
        var values = plugin.LastGood!.Configuration with { PluginSettings = Values("1", 29) };
        byte[] payload = SdkJson.Serialize(state with { Plugins = [plugin with
        {
            Pending = new(next.Candidate.CandidateId, next.Candidate.Artifact!, next.Candidate.Description!, values),
            Problem = null
        }] });
        await File.WriteAllBytesAsync(store.StorePath,
            SdkJson.Serialize(new { Version = 1, Sha256 = Convert.ToHexString(SHA256.HashData(payload)), Payload = payload }));

        state = await Store().ApplyStartupAsync([next.Candidate], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Null(state.Plugins[0].Pending);
        state = await Store().SelectCandidateAsync("test.plugin", next.Candidate.CandidateId, state.Revision);
        state = await Store().ApplyStartupAsync([next.Candidate], state.Revision);
        Assert.Equal(29, Number(Active(state)));
    }

    [Fact]
    public async Task UnconfiguredInstalledBuildCannotBeReplacedBySavingSettingsForARebuild()
    {
        var first = await Delivered("not-configured", "one", requiredSetting: true);
        var store = Store();
        var state = await store.ApplyStartupAsync([first.Candidate], 0);
        Assert.Null(state.Plugins[0].Active);
        Assert.Null(state.Plugins[0].LastGood);
        var next = await Delivered("not-configured", "two", requiredSetting: true);
        state = await store.ApplyStartupAsync([next.Candidate], state.Revision);
        Assert.Contains("Select the new build", state.Plugins[0].Problem);

        state = await Store().SaveConfigurationAsync("test.plugin", next.Candidate.CandidateId,
            new("draft", Values("1", 11), new Dictionary<string, SettingsSnapshot>()), state.Revision);
        state = await Store().ApplyStartupAsync([next.Candidate], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Null(state.Plugins[0].Pending);
        state = await Store().SelectCandidateAsync("test.plugin", next.Candidate.CandidateId, state.Revision);
        state = await Store().ApplyStartupAsync([next.Candidate], state.Revision);
        Assert.Equal(11, Number(Active(state)));
    }

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
        // A host without serialized live permission authority records that proof but can never
        // auto-update (since #130 an existing grant no longer blocks an in-place update by itself).
        PluginProfileStore ungated = new(profile, static _ => true, () => Array.Empty<string>());
        state = await ungated.ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyStartupAsync([next.Candidate], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        }
    }

    [Theory]
    [InlineData("manifest", false)]
    [InlineData("delivered-assembly", false)]
    [InlineData("cache", false)]
    [InlineData("manifest", true)]
    [InlineData("delivered-assembly", true)]
    [InlineData("cache", true)]
    public async Task AutomaticBundledRebuildReverifiesCurrentBytesAndFailsClosed(string corruption, bool moved)
    {
        var old = await Delivered("corrupt-install", "one");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await Delivered(moved ? "corrupt-replacement" : "corrupt-install", "two");
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

    // Decision #130 (16.11.2026): an in-place update of a delivered bundle (same shipped manifest path, current proof,
    // established release) carries the plugin's existing grants and size/MD5 exceptions over and accepts a
    // declaration that only narrows. Every other case below still needs the explicit repair review.
    [Theory]
    [InlineData("grant", true)]
    [InlineData("restricted-source", true)]
    [InlineData("grant-and-restricted-source", true)]
    [InlineData("capability", false)]
    [InlineData("host", false)]
    [InlineData("host-purpose", false)]
    [InlineData("route", false)]
    [InlineData("source", false)]
    [InlineData("filter", false)]
    [InlineData("mime", false)]
    [InlineData("schema", false)]
    [InlineData("relocation", false)]
    [InlineData("user-plugin", false)]
    [InlineData("local", false)]
    [InlineData("ambiguous-plugin", false)]
    [InlineData("ambiguous-slot", false)]
    [InlineData("unproven-old", false)]
    // #130 (17.11.): a pinned release follows its identical same-path rebuild of the same version; the pin moves along.
    [InlineData("pin", true)]
    public async Task AutomaticInPlaceRebuildDoesNotBroadenEligibility(string guard, bool carriesOver)
    {
        var old = await Delivered("guard-install", "one", source: true);
        var state = guard == "unproven-old"
            ? await Store().ApplyStartupAsync([old.Candidate], 0)
            : await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        // Widening and relocation cases hold rights too: existing rights never excuse a broadened or moved build.
        if (guard is not "restricted-source" and not "unproven-old")
            state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared"), FilterDataAccess.OriginalImage, state.Revision);
        if (guard == "pin") state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        var next = await Delivered(guard == "relocation" ? "guard-worktree" : "guard-install", "two", source: true,
            settings: guard == "schema" ? NumberSchema("2", 99) : null);
        Func<PluginDescription, PluginDescription>? widen = guard switch
        {
            "capability" => d => d with { Sources = [d.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture }] },
            "host" => d => d with { Sources = [d.Sources[0] with { HttpHosts = [.. d.Sources[0].HttpHosts, new("media.fixture.test", HttpHostPurpose.OriginalMedia)] }] },
            "host-purpose" => d => d with { Sources = [d.Sources[0] with { HttpHosts = [new("fixture.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)] }] },
            "route" => d => d with { Sources = [d.Sources[0] with { Routes = [.. d.Sources[0].Routes,
                new(SourceResourceKind.Thread, "fixture.test", "/{board}/res/{thread}.html")] }] },
            "source" => d => d with { Sources = [.. d.Sources, d.Sources[0] with { SourceId = "second", Name = "Second source",
                BaseUri = new("https://second.fixture.test"), Routes = [new(SourceResourceKind.Thread, "second.fixture.test", "/{board}/thread/{thread}", IsCanonical: true)],
                HttpHosts = [new("second.fixture.test", HttpHostPurpose.Metadata)] }] },
            "filter" => d => d with { Filters = [.. d.Filters, d.Filters[0] with { Key = "second-filter", Name = "Second filter" }] },
            "mime" => d => d with { Filters = [d.Filters[0] with { SupportedMimeTypes = [.. d.Filters[0].SupportedMimeTypes, "image/jpeg"] }] },
            _ => null
        };
        if (widen is not null) next = await Redeclared(next, widen);
        var candidates = new List<PluginCandidateObservation> { next.Candidate };
        var proofs = new List<DeliveredCandidateProvenance>();
        if (guard is not "local" and not "user-plugin") proofs.Add(next.Proof);
        if (guard == "user-plugin")
        {
            // The same new build installed by the user into the profile's plugins folder: never delivered, never this identity.
            string userPath = Path.Combine(profile, "plugins", "fixture.plugin.json");
            candidates[0] = next.Candidate with { CandidateId = "user-installed-copy", CandidatePath = userPath };
        }
        if (guard == "ambiguous-plugin") candidates.Add(next.Candidate with
            { CandidateId = "independent-local-choice", CandidatePath = Path.Combine(profile, "plugins", "local.cs") });
        if (guard == "ambiguous-slot")
        {
            var duplicate = await Delivered("duplicate-install", "two", source: true, fileName: "FIXTURE.plugin.json");
            candidates.Add(duplicate.Candidate); proofs.Add(duplicate.Proof);
        }
        var grants = SdkJson.Serialize(state.Grants);
        var owners = SdkJson.Serialize(state.SelectedSourceOwners);
        string[] restricted = guard is "restricted-source" or "grant-and-restricted-source" or "relocation" or "host" ? ["shared"] : [];
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync(candidates, proofs, restricted, state.Revision);
            Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.True(state.Plugins[0].Enabled);
            if (carriesOver)
            {
                Assert.Equal(next.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
                Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
                Assert.Equal(guard == "pin" && restart == 0 ? PluginActivationProblems.PinnedReleaseUpdatedInPlace : null, state.Plugins[0].Problem);
                Assert.Null(state.Plugins[0].Pending);
                if (guard == "pin") Assert.Equal(next.Candidate.Artifact.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
                else Assert.Null(state.Plugins[0].Pinned);
                Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
                continue;
            }
            Assert.NotEqual(next.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Active?.ExecutionRevision);
            if (guard == "pin") Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pinned!.ExecutionRevision);
        }
        if (guard is "capability" or "host" or "host-purpose" or "route" or "filter" or "mime" or "schema")
            Assert.Equal("description-changed", Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows).BlockedReason);
        if (guard == "relocation")
            Assert.Equal("inherited-grants", Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows).BlockedReason);
    }

    /// <summary>The same delivery with a changed declaration, re-proven from the same shipped folder.</summary>
    private static async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> Redeclared(
        (PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof) delivery, Func<PluginDescription, PluginDescription> change)
    {
        var candidate = delivery.Candidate with { Description = change(delivery.Candidate.Description!) };
        Assert.Empty(SdkValidation.ValidateDescription(candidate.Description!));
        var root = Path.GetDirectoryName(candidate.CandidatePath!)!;
        var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(root)).Candidates,
            c => c.Kind == PluginCandidateKind.PrebuiltManifest);
        var proof = await PluginInstallationInventory.RecordDeliveredAsync(root, installed, candidate.Artifact!, candidate.Description!);
        return (candidate, proof);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedBindingFollowsLaterInPlaceRebuildOnlyAfterTargetActuallyActivated(bool activated)
    {
        var old = await Delivered("binding-old", "one");
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var target = await Delivered("binding-target", "two");
        state = await ReviewStore().ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
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
