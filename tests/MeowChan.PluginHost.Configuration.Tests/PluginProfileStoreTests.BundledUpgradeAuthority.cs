using MeowChan.PluginHost.Configuration;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    [Fact]
    public async Task AutomaticBundleUpdateWaitsForLivePermissionAuthorityBeforeTakingProfileLease()
    {
        using SemaphoreSlim permissionGate = new(1, 1);
        string[] restrictions = [];
        PluginProfileStore store = new(profile, static _ => true, () => restrictions, permissionGate);
        var old = await Delivered("serialized-update", "one", source: true);
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await Delivered("serialized-update", "two", source: true);
        await permissionGate.WaitAsync();
        Task<PluginProfileSnapshot> updating;
        try
        {
            updating = store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
            Assert.False(updating.IsCompleted);
            // A reader remains possible: the startup must wait on authority BEFORE its writer lease.
            Assert.Equal(state.Revision, (await Store().ReadAsync().WaitAsync(TimeSpan.FromSeconds(10))).Revision);
            restrictions = ["shared"];
        }
        finally { permissionGate.Release(); }
        state = await updating.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(state.Plugins[0].Active);
        Assert.True(state.Plugins[0].Enabled);
        Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
    }

    [Fact]
    public async Task UnserializedLivePermissionAuthorityCannotAuthorizeAutomaticBundleUpdate()
    {
        PluginProfileStore store = new(profile, static _ => true, () => Array.Empty<string>());
        var old = await Delivered("unserialized-update", "one", source: true);
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await Delivered("unserialized-update", "two", source: true);
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.True(state.Plugins[0].Enabled);
        Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
    }

    [Fact]
    public async Task CancelledAutomaticBundleUpdateDoesNotPublishOrReleaseAnotherPermissionLease()
    {
        using SemaphoreSlim permissionGate = new(1, 1);
        PluginProfileStore store = new(profile, static _ => true, () => Array.Empty<string>(), permissionGate);
        var old = await Delivered("cancelled-update", "one", source: true);
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await Delivered("cancelled-update", "two", source: true);
        byte[] original = await File.ReadAllBytesAsync(store.StorePath);
        using CancellationTokenSource cancellation = new();
        await permissionGate.WaitAsync();
        try
        {
            Task<PluginProfileSnapshot> update = store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            Assert.Equal(0, permissionGate.CurrentCount);
            Assert.Equal(original, await File.ReadAllBytesAsync(store.StorePath));
        }
        finally { permissionGate.Release(); }
    }
    [Fact]
    public async Task EstablishedInPlaceSourceEscapesAlreadyConfirmedRebuildLoopWithoutAnotherReview()
    {
        // Create the old review state through real store operations, not a forged envelope.
        // The three-argument host has no serialized live authority and cannot auto-update.
        PluginProfileStore reviewing = new(profile, static _ => true, () => Array.Empty<string>());
        var old = await Delivered("confirmed-in-place-loop", "one", source: true);
        var state = await reviewing.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await reviewing.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 11) }, state.Revision);
        state = await reviewing.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        var accepted = await Delivered("confirmed-in-place-loop", "two", source: true);
        state = await reviewing.ApplyBundledStartupAsync([accepted.Candidate], [accepted.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        var preview = await reviewing.PreviewBundledRepairsAsync();
        state = await reviewing.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        byte[] configuration = MeowChan.PluginSdk.SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        var rebuiltAgain = await Delivered("confirmed-in-place-loop", "three", source: true);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([rebuiltAgain.Candidate], [rebuiltAgain.Proof], [], state.Revision);
            Assert.Equal(rebuiltAgain.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(configuration, MeowChan.PluginSdk.SdkJson.Serialize(Active(state).Configuration));
            Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Null(state.Plugins[0].Pending); Assert.Null(state.Plugins[0].Problem);
        }
    }

    [Fact]
    public async Task OneCorruptInPlaceBundleDoesNotSuppressUnrelatedPluginStartup()
    {
        var old = await Delivered("isolated-bad-update", "one");
        var healthy = await Delivered("isolated-healthy-update", "stable", fileName: "healthy.plugin.json");
        healthy.Candidate = healthy.Candidate with { PluginId = "healthy.plugin",
            Description = healthy.Candidate.Description! with { PluginId = "healthy.plugin" } };
        string root = Path.GetDirectoryName(healthy.Candidate.CandidatePath!)!;
        var installed = Assert.Single((await new MeowChan.PluginHost.Installation.PluginInstallationInventory().ScanAsync(root)).Candidates,
            candidate => candidate.Kind == MeowChan.PluginHost.Installation.PluginCandidateKind.PrebuiltManifest);
        healthy.Proof = await MeowChan.PluginHost.Installation.PluginInstallationInventory.RecordDeliveredAsync(root,
            installed, healthy.Candidate.Artifact!, healthy.Candidate.Description!);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate, healthy.Candidate], [old.Proof, healthy.Proof], [], 0);
        var changed = await Delivered("isolated-bad-update", "two");
        await File.WriteAllTextAsync(changed.Candidate.Artifact!.AssemblyPath, "corrupt cached update");
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([changed.Candidate, healthy.Candidate],
                [changed.Proof, healthy.Proof], [], state.Revision);
            var bad = Assert.Single(state.Plugins, plugin => plugin.PluginId == "test.plugin");
            var good = Assert.Single(state.Plugins, plugin => plugin.PluginId == "healthy.plugin");
            Assert.Null(bad.Active); Assert.Contains("integrity validation failed", bad.Problem);
            Assert.True(good.Enabled); Assert.Null(good.Problem);
            Assert.Equal(healthy.Candidate.Artifact!.ExecutionRevision, good.Active!.ExecutionRevision);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitLegacyNewSelectionThenRebuildActivatesOnFirstStartWithoutInventedOldRuntimeHistory(bool enabled)
    {
        var legacy = await Delivered("legacy-before-selected-rebuild", "legacy", source: true);
        var state = await Store().ApplyStartupAsync([legacy.Candidate], 0); // Genuine unproven legacy-store path.
        state = await Store().SaveConfigurationAsync("test.plugin", legacy.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 42) }, state.Revision);
        state = await Store().SetEnabledAsync("test.plugin", enabled, state.Revision);
        var selected = await Delivered("selected-new-rebuild", "selected", source: true);
        state = await Store().ApplyBundledStartupAsync([selected.Candidate], [selected.Proof], [], state.Revision);
        var preview = await Store().PreviewBundledReselectionsAsync();
        var row = Assert.Single(preview.Rows); Assert.True(row.CanConfirm, row.BlockedReason);
        state = await Store().ConfirmBundledReselectionAsync(row, preview.ProfileRevision);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
        byte[] configuration = MeowChan.PluginSdk.SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        byte[] original = await File.ReadAllBytesAsync(Store().PreV2BackupPath);
        byte[] selectedBackup = await File.ReadAllBytesAsync(Store().PreReselectionBackupPath);
        var rebuilt = await Delivered("selected-new-rebuild", "rebuilt-before-first-start", source: true);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([rebuilt.Candidate], [rebuilt.Proof], [], state.Revision);
            Assert.Equal(rebuilt.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(configuration, MeowChan.PluginSdk.SdkJson.Serialize(Active(state).Configuration));
            Assert.Equal(enabled, state.Plugins[0].Enabled);
            Assert.Equal(selected.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            // C is the first runtime-ready build. Neither legacy A nor selected-but-unrun B is invented as LastGood.
            Assert.Equal(rebuilt.Candidate.Artifact.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Null(state.Plugins[0].Pending); Assert.Null(state.Plugins[0].Problem);
            Assert.Empty(state.Grants); Assert.Empty(state.SelectedSourceOwners);
            Assert.Equal(original, await File.ReadAllBytesAsync(Store().PreV2BackupPath));
            Assert.Equal(selectedBackup, await File.ReadAllBytesAsync(Store().PreReselectionBackupPath));
        }
    }

}
