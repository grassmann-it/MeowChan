using System.Security.Cryptography;
using System.Text;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitNewBundledSelectionRetainsReviewedSettingsAndEnabledAcrossTwoRestarts(bool enabled)
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two"); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        var config = Active(state).Configuration with { PluginSettings = Values("1", 42) };
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId, config, state.Revision);
        state = await store.ApplyStartupAsync([old.Candidate], state.Revision);
        config = Active(state).Configuration; // SaveConfiguration assigns its own authoritative revision.
        Assert.Equal(42, Number(Active(state)));
        state = await store.SetEnabledAsync("test.plugin", enabled, state.Revision);
        byte[] original = File.ReadAllBytes(store.StorePath);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.False(Assert.Single((await store.PreviewBundledRepairsAsync()).Rows).CanConfirm);
        byte[] before = File.ReadAllBytes(store.StorePath);
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows);
        Assert.True(row.CanConfirm, row.BlockedReason); Assert.Equal(before, File.ReadAllBytes(store.StorePath));
        Assert.Equal(Encoding.UTF8.GetString(SdkJson.Serialize(config)), row.ConfigurationJson);
        state = await store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision);
        Assert.Equal(before, File.ReadAllBytes(store.PreReselectionBackupPath));
        // The pre-v2 file may precede later v1 mutations, but is immutable throughout confirmation.
        byte[] v1 = File.ReadAllBytes(store.PreV2BackupPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(before)) + "\n", File.ReadAllText(store.PreReselectionBackupPath + ".sha256"));
        Assert.Equal(target.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
        Assert.Equal(enabled, state.Plugins[0].Enabled);
        Assert.Equal(SdkJson.Serialize(config), SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
        Assert.Empty(state.Grants); Assert.Empty(state.SelectedSourceOwners);
        for (int restart = 0; restart < 2; restart++)
        {
            store = Store(); state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
            Assert.Equal(target.Candidate.CandidateId, Active(state).CandidateId);
            Assert.Equal(42, Number(Active(state))); Assert.Equal(enabled, state.Plugins[0].Enabled);
            Assert.Equal(v1, File.ReadAllBytes(store.PreV2BackupPath));
            Assert.Equal(before, File.ReadAllBytes(store.PreReselectionBackupPath));
            Assert.Empty((await store.PreviewBundledReselectionsAsync()).Rows);
        }
    }

    [Fact]
    public async Task NewSelectionDoesNotImportExistingPermissions()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two"); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("4chan"), FilterDataAccess.OriginalImage, state.Revision);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        byte[] before = File.ReadAllBytes(store.StorePath);
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows);
        Assert.Equal("inherited-grants", row.BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        Assert.Equal(before, File.ReadAllBytes(store.StorePath)); Assert.False(File.Exists(store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task StaleAndForgedNewSelectionsNeverWriteOrBackUp()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two"); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows);
        byte[] before = File.ReadAllBytes(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledReselectionAsync(row with { ConfigurationJson = "{}" }, state.Revision));
        Assert.Equal(before, File.ReadAllBytes(store.StorePath));
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision); before = File.ReadAllBytes(store.StorePath);
        await Assert.ThrowsAsync<PluginProfileConflictException>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        Assert.Equal(before, File.ReadAllBytes(store.StorePath)); Assert.False(File.Exists(store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task AmbiguousPresentCandidatesAreNotSilentlyMatchedByNameOrPath()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two"); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        var local = Candidate("local") with { CandidateId = "arbitrary-local.cs" };
        state = await store.ApplyBundledStartupAsync([target.Candidate, local], [target.Proof], [], state.Revision);
        Assert.Equal("ambiguous", Assert.Single((await store.PreviewBundledReselectionsAsync()).Rows).BlockedReason);
    }

    [Fact]
    public async Task SchemaAndSourceIdentityChangesRemainExplicitlyBlocked()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two", source: true); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        Assert.Equal("source-identity-changed", Assert.Single((await store.PreviewBundledReselectionsAsync()).Rows).BlockedReason);
        var incompatible = await Delivered("new-required", "three", settings: new("2", [new("new-value",
            SettingScope.Plugin, SettingKind.Integer, "New required value", IsRequired: true)]));
        state = await store.ApplyBundledStartupAsync([incompatible.Candidate], [incompatible.Proof], [], state.Revision);
        Assert.Equal("configuration-incompatible", Assert.Single((await store.PreviewBundledReselectionsAsync()).Rows).BlockedReason);
    }

    [Fact]
    public async Task LiveSourceRestrictionsBlockEvenUnpinnedGrantFreeSelections()
    {
        var old = await Delivered("legacy", "one", source: true); var target = await Delivered("new", "two", source: true);
        bool restricted = false;
        using SemaphoreSlim gate = new(1, 1);
        var store = new PluginProfileStore(profile, _ => true, () => restricted ? ["shared"] : [], gate);
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows); Assert.True(row.CanConfirm);
        restricted = true; byte[] before = File.ReadAllBytes(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        Assert.Equal(before, File.ReadAllBytes(store.StorePath));
    }

    [Fact]
    public async Task ChangedTargetBytesAndConflictingBackupAbortBeforeStatePublication()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two"); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows);
        byte[] before = File.ReadAllBytes(store.StorePath), assembly = File.ReadAllBytes(target.Candidate.Artifact!.AssemblyPath);
        File.WriteAllText(target.Candidate.Artifact.AssemblyPath, "changed executable");
        await Assert.ThrowsAnyAsync<Exception>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        Assert.Equal(before, File.ReadAllBytes(store.StorePath)); Assert.False(File.Exists(store.PreReselectionBackupPath));
        File.WriteAllBytes(target.Candidate.Artifact.AssemblyPath, assembly);
        File.WriteAllText(store.PreReselectionBackupPath, "conflicting evidence");
        File.WriteAllText(store.PreReselectionBackupPath + ".sha256", "wrong digest");
        await Assert.ThrowsAsync<IOException>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        Assert.Equal(before, File.ReadAllBytes(store.StorePath)); Assert.Equal("conflicting evidence", File.ReadAllText(store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task AlreadyV2WithoutHistoricalBackupRetainsActualV2WithoutInventingV1()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two"); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        File.Delete(store.PreV2BackupPath); File.Delete(store.PreV2BackupSha256Path); // private fixture simulates a pre-backup v2 profile
        byte[] before = File.ReadAllBytes(store.StorePath);
        var preview = await store.PreviewBundledReselectionsAsync();
        await store.ConfirmBundledReselectionAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        Assert.Equal(before, File.ReadAllBytes(store.PreReselectionBackupPath));
        Assert.False(File.Exists(store.PreV2BackupPath)); Assert.False(File.Exists(store.PreV2BackupSha256Path));
    }
    [Fact]
    public async Task MissingPermissionSerializationFailsClosed()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two");
        var store = new PluginProfileStore(profile, _ => true, () => []);
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows);
        Assert.True(row.CanConfirm);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        Assert.False(File.Exists(store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task PermissionWriterCannotCrossFinalValidationBackupAndPublication()
    {
        var old = await Delivered("legacy", "one", source: true); var target = await Delivered("new", "two", source: true);
        using SemaphoreSlim authority = new(1, 1);
        using ManualResetEventSlim release = new();
        TaskCompletionSource sampled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool pause = false, restricted = false; int calls = 0;
        var store = new PluginProfileStore(profile, _ => true, () =>
        {
            if (pause && Interlocked.Increment(ref calls) == 2)
            {
                sampled.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release the sampled authority.");
            }
            return restricted ? ["shared"] : [];
        }, authority);
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows); Assert.True(row.CanConfirm);
        pause = true;
        Task<PluginProfileSnapshot> confirmation = Task.Run(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        try
        {
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, authority.CurrentCount);
            TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string?> writer = Task.Run(async () =>
            {
                attempted.TrySetResult(); await authority.WaitAsync();
                try
                {
                    // Inspect without the store lock: it must already be the published
                    // NEW choice when a competing permission authority can enter.
                    byte[] bytes = File.ReadAllBytes(store.StorePath);
                    using var envelope = System.Text.Json.JsonDocument.Parse(bytes);
                    byte[] payload = envelope.RootElement.GetProperty("payload").GetBytesFromBase64();
                    using var document = System.Text.Json.JsonDocument.Parse(payload);
                    string? selected = document.RootElement.GetProperty("profile").GetProperty("plugins")[0]
                        .GetProperty("selectedCandidateId").GetString();
                    restricted = true;
                    return selected;
                }
                finally { authority.Release(); }
            });
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(writer.IsCompleted);
            release.Set();
            Assert.Equal(target.Candidate.CandidateId, (await confirmation.WaitAsync(TimeSpan.FromSeconds(10))).Plugins[0].SelectedCandidateId);
            Assert.Equal(target.Candidate.CandidateId, await writer.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(restricted);
        }
        finally { release.Set(); await confirmation.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task PinnedMissingCandidateCannotBeUnpinnedByNewSelection()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two"); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        state = await store.SelectRollbackAsync("test.plugin", state.Revision);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows);
        Assert.Equal("pinned", row.BlockedReason);
        byte[] before = File.ReadAllBytes(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        Assert.Equal(before, File.ReadAllBytes(store.StorePath)); Assert.NotNull(state.Plugins[0].Pinned);
    }

    [Fact]
    public async Task ValidButDifferentBackupCannotMasqueradeAsActualPreSelectionState()
    {
        var old = await Delivered("legacy", "one"); var target = await Delivered("new", "two"); var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0);
        byte[] unrelatedToSelection = File.ReadAllBytes(store.StorePath);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        byte[] before = File.ReadAllBytes(store.StorePath);
        File.WriteAllBytes(store.PreReselectionBackupPath, unrelatedToSelection);
        File.WriteAllText(store.PreReselectionBackupPath + ".sha256", Convert.ToHexString(SHA256.HashData(unrelatedToSelection)) + "\n");
        var preview = await store.PreviewBundledReselectionsAsync(); var row = Assert.Single(preview.Rows);
        await Assert.ThrowsAsync<IOException>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
        Assert.Equal(before, File.ReadAllBytes(store.StorePath));
        Assert.Equal(unrelatedToSelection, File.ReadAllBytes(store.PreReselectionBackupPath));
    }

}
