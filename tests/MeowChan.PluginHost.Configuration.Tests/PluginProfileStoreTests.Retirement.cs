using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

// A replaced single-site identity that its family serves is retired from the active profile (backup kept, once).
public sealed partial class PluginProfileStoreTests
{
    private static readonly IReadOnlySet<string> BothEligible = new HashSet<string>(StringComparer.Ordinal) { "site.a", "site.b" };

    /// <summary>The observed upgraded state: the family serves a and b, both old records are replaced history
    /// (one still enabled, one disabled), each with candidates from two build folders that are both gone.</summary>
    private async Task<(PluginProfileSnapshot State, string OldA, string OldB)> ReplacedHistoryAsync()
    {
        var a = await DeliveredSource("release-1", "a1", "site.a", "a.plugin.json", "a");
        var b = await DeliveredSource("release-1", "b1", "site.b", "b.plugin.json", "b");
        var otherA = await DeliveredSource("worktree", "a1", "site.a", "a.plugin.json", "a");
        var state = await FamilyStore().ApplyBundledStartupAsync([a.Candidate, b.Candidate], [a.Proof, b.Proof], [], 0);
        state = await FamilyStore().ApplyStartupAsync([otherA.Candidate, a.Candidate, b.Candidate], state.Revision);
        state = await FamilyStore().SetEnabledAsync("site.b", false, state.Revision);
        var family = await DeliveredSource("release-2", "f1", "family", "family.plugin.json", "a", "b", "c");
        state = await FamilyStore().ApplyBundledStartupAsync([family.Candidate], [family.Proof], [], state.Revision);
        Assert.Equal(new[] { "site.a", "site.b" }, SupersededBundledPlugins.InProfile(state, Replaced).Order(StringComparer.Ordinal));
        Assert.True(state.Plugins.Single(p => p.PluginId == "site.a").Enabled);
        return (state, a.Candidate.Artifact!.ExecutionRevision, b.Candidate.Artifact!.ExecutionRevision);
    }

    [Fact]
    public async Task ReplacedRecordsLeaveTheProfileOnceWithABackupAndReleaseOnlyTheirOwnBuilds()
    {
        (PluginProfileSnapshot state, string oldA, string oldB) = await ReplacedHistoryAsync();
        PluginActivationSnapshot family = state.Plugins.Single(p => p.PluginId == "family");
        byte[] before = await File.ReadAllBytesAsync(FamilyStore().StorePath);

        SupersededRetirement retirement = await FamilyStore().RetireSupersededAsync(BothEligible, state.Revision);

        Assert.Equal(new[] { "site.a", "site.b" }, retirement.RetiredPluginIds);
        Assert.DoesNotContain(retirement.Profile.Plugins, p => p.PluginId.StartsWith("site.", StringComparison.Ordinal));
        Assert.DoesNotContain(retirement.Profile.Grants, g => g.PluginId.StartsWith("site.", StringComparison.Ordinal));
        Assert.DoesNotContain(retirement.Profile.SelectedSourceOwners.Values, owner => owner.StartsWith("site.", StringComparison.Ordinal));
        // The family keeps its record, its active build and its routing.
        PluginActivationSnapshot after = retirement.Profile.Plugins.Single(p => p.PluginId == "family");
        Assert.Equal(SdkJson.Serialize(family), SdkJson.Serialize(after));
        Assert.Equal(state.Revision + 1, retirement.Profile.Revision);
        // The exact replaced envelope is kept as an immutable backup next to the profile.
        Assert.NotNull(retirement.BackupPath);
        Assert.Equal(Path.GetDirectoryName(FamilyStore().StorePath), Path.GetDirectoryName(retirement.BackupPath));
        Assert.Equal(before, await File.ReadAllBytesAsync(retirement.BackupPath!));
        // Only builds nothing else references are released; the family's build stays.
        Assert.Equal(new[] { oldA, oldB }.Order(StringComparer.Ordinal), retirement.ReleasedExecutionRevisions.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(family.Active!.ExecutionRevision, retirement.ReleasedExecutionRevisions);
        Assert.Contains(family.Active.ExecutionRevision, retirement.Profile.RetainedExecutionRevisions);

        // Idempotent: a second start finds nothing to retire and writes nothing.
        byte[] published = await File.ReadAllBytesAsync(FamilyStore().StorePath);
        SupersededRetirement again = await FamilyStore().RetireSupersededAsync(BothEligible, retirement.Profile.Revision);
        Assert.Empty(again.RetiredPluginIds);
        Assert.Null(again.BackupPath);
        Assert.Equal(retirement.Profile.Revision, again.Profile.Revision);
        Assert.Equal(published, await File.ReadAllBytesAsync(FamilyStore().StorePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(FamilyStore().StorePath)!, "state.pre-retirement-*.json"));

        // Later starts never bring the old identities back or give a source two owners.
        var familyAgain = await DeliveredSource("release-2", "f1", "family", "family.plugin.json", "a", "b", "c");
        PluginProfileSnapshot restarted = await FamilyStore().ApplyBundledStartupAsync([familyAgain.Candidate], [familyAgain.Proof], [], again.Profile.Revision);
        Assert.DoesNotContain(restarted.Plugins, p => p.PluginId.StartsWith("site.", StringComparison.Ordinal));
        Assert.Equal(new[] { "a", "b", "c" }, restarted.Plugins.Single(p => p.PluginId == "family").Active!.Description.Sources.Select(s => s.SourceId));
        Assert.Null(restarted.Plugins.Single(p => p.PluginId == "family").Problem);
    }

    [Fact]
    public async Task OnlyEligibleReplacedRecordsAreRetired()
    {
        (PluginProfileSnapshot state, _, _) = await ReplacedHistoryAsync();
        // The caller has not taken over site.b's state (for example its site switch file is unusable): it stays.
        SupersededRetirement retirement = await FamilyStore().RetireSupersededAsync(new HashSet<string>(StringComparer.Ordinal) { "site.a" }, state.Revision);
        Assert.Equal(new[] { "site.a" }, retirement.RetiredPluginIds);
        Assert.Contains(retirement.Profile.Plugins, p => p.PluginId == "site.b");
        // Nothing eligible: nothing written.
        SupersededRetirement none = await FamilyStore().RetireSupersededAsync(new HashSet<string>(StringComparer.Ordinal), retirement.Profile.Revision);
        Assert.Empty(none.RetiredPluginIds);
        Assert.Equal(retirement.Profile.Revision, none.Profile.Revision);
    }

    [Fact]
    public async Task AnIdentityThatIsStillInstalledOrNotReplacedIsNeverRetired()
    {
        var a = await DeliveredSource("release-1", "a1", "site.a", "a.plugin.json", "a");
        var state = await FamilyStore().ApplyBundledStartupAsync([a.Candidate], [a.Proof], [], 0);
        var family = await DeliveredSource("release-2", "f1", "family", "family.plugin.json", "a", "b");
        // The old bundle is still installed beside the family: explicit conflict rules, no retirement.
        state = await FamilyStore().ApplyBundledStartupAsync([a.Candidate, family.Candidate], [a.Proof, family.Proof], [], state.Revision);
        SupersededRetirement retirement = await FamilyStore().RetireSupersededAsync(BothEligible, state.Revision);
        Assert.Empty(retirement.RetiredPluginIds);
        Assert.NotNull(retirement.Profile.Plugins.Single(p => p.PluginId == "site.a").Active);
        // Without the host table nothing is replaced, so nothing is retired.
        Assert.Empty((await FamilyStore(table: false).RetireSupersededAsync(BothEligible, state.Revision)).RetiredPluginIds);
    }

    [Fact]
    public async Task AStaleRevisionIsRejectedWithoutWriting()
    {
        (PluginProfileSnapshot state, _, _) = await ReplacedHistoryAsync();
        byte[] before = await File.ReadAllBytesAsync(FamilyStore().StorePath);
        await Assert.ThrowsAsync<PluginProfileConflictException>(() => FamilyStore().RetireSupersededAsync(BothEligible, state.Revision - 1));
        Assert.Equal(before, await File.ReadAllBytesAsync(FamilyStore().StorePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FamilyStore().StorePath)!, "state.pre-retirement-*.json"));
    }
}
