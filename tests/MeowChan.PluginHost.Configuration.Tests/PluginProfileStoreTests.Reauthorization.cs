using System.Security.Cryptography;
using System.Text;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    // Only synthetic temporary profiles and delivered fixtures; no installed user profile is read.
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task ExactBundledPermissionConsentSurvivesFreshStoreRestartWithoutChangingRights(
        bool samePath, bool sourceRights, bool filterRights)
    {
        var old = await Delivered("reauthorize-old", "A", source: sourceRights);
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SelectCandidateAsync("test.plugin", old.Candidate.CandidateId, state.Revision);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            state.Plugins[0].Pending!.Configuration with { PluginSettings = Values("1", 23) }, state.Revision);
        state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        if (filterRights) state = await AddReviewedFilterGrantsAsync(store, state);
        string[] restrictions = sourceRights ? ["shared"] : [];
        byte[] grants = SdkJson.Serialize(state.Grants);
        byte[] owners = SdkJson.Serialize(state.SelectedSourceOwners);
        byte[] configuration = SdkJson.Serialize(Active(state).Configuration);
        var next = await Delivered(samePath ? "reauthorize-old" : "reauthorize-new", "B", source: sourceRights);
        Assert.Equal(samePath, old.Candidate.CandidateId == next.Candidate.CandidateId);
        Assert.NotEqual(old.Candidate.Artifact!.ExecutionRevision, next.Candidate.Artifact!.ExecutionRevision);
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], restrictions, state.Revision);
        Assert.Null(state.Plugins[0].Active);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        var preview = await store.PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        Assert.True(row.BuildChanged);
        Assert.Equal(next.Candidate.Artifact.ExecutionRevision, row.NewExecutionRevision);
        AssertExactPermissionReview(row, state.Grants, restrictions);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));

        // Exercise the public interface as well as the concrete implementation.
        IPluginProfileStore consent = store;
        state = await consent.ReauthorizeBundledRepairAsync(row, preview.ProfileRevision);
        Assert.True(state.Revision > preview.ProfileRevision);
        Assert.Null(state.Plugins[0].Active); // Consent is not live activation.
        Assert.Equal(next.Candidate.Artifact.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
        Assert.Equal(configuration, SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
        Assert.Equal(old.Candidate.Artifact.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
        Assert.False(state.Plugins[0].Enabled);
        Assert.Null(state.Plugins[0].Pinned);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.PreReselectionBackupPath));
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(before)),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(store.StorePath))));

        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([next.Candidate], [next.Proof], restrictions, state.Revision);
            Assert.Equal(next.Candidate.Artifact.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(configuration, SdkJson.Serialize(Active(state).Configuration));
            Assert.Null(state.Plugins[0].Pending);
            Assert.Null(state.Plugins[0].Problem);
            Assert.False(state.Plugins[0].Enabled);
            Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.PreReselectionBackupPath));
        }
        // A consumed/pending repair row may disappear; do not require CanConfirm after consent.
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ReauthorizationForBDoesNotAuthorizeCBeforeOrAfterBActivation(bool samePath, bool activateB)
    {
        var fixture = await ReviewableRepairAsync(samePath, source: true);
        var state = await fixture.Store.ReauthorizeBundledRepairAsync(fixture.Row, fixture.State.Revision);
        byte[] originalBackup = await File.ReadAllBytesAsync(fixture.Store.PreReselectionBackupPath);
        byte[] grants = SdkJson.Serialize(state.Grants);
        if (activateB)
        {
            state = await Store().ApplyBundledStartupAsync([fixture.Target], [fixture.Proof], ["shared"], state.Revision);
            Assert.Equal(fixture.Target.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
        }
        var third = await Delivered(samePath ? "consent-old" : "consent-target", "C", source: true);
        state = await Store().ApplyBundledStartupAsync([third.Candidate], [third.Proof], ["shared"], state.Revision);
        Assert.NotEqual(third.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Active?.ExecutionRevision);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        var preview = await Store().PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        AssertExactPermissionReview(row, state.Grants, ["shared"]);
        Assert.Equal(third.Candidate.Artifact.ExecutionRevision, row.NewExecutionRevision);
        byte[] before = await File.ReadAllBytesAsync(fixture.Store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.StorePath));
        // C needs a separate explicit review, and must not replace the first backup.
        state = await Store().ReauthorizeBundledRepairAsync(row, preview.ProfileRevision);
        state = await Store().ApplyBundledStartupAsync([third.Candidate], [third.Proof], ["shared"], state.Revision);
        Assert.Equal(third.Candidate.Artifact.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        Assert.Equal(originalBackup, await File.ReadAllBytesAsync(fixture.Store.PreReselectionBackupPath));
    }

    [Theory]
    [InlineData("row")]
    [InlineData("fingerprint")]
    [InlineData("review-json")]
    [InlineData("review-json-and-fingerprint")]
    [InlineData("missing-review")]
    [InlineData("missing-fingerprint")]
    [InlineData("revision")]
    [InlineData("cancel")]
    public async Task InvalidOrCancelledReauthorizationDoesNotPublishAnyBytes(string rejection)
    {
        var fixture = await ReviewableRepairAsync(source: true);
        var row = fixture.Row;
        long revision = fixture.State.Revision;
        if (rejection == "row") row = row with { NewExecutionRevision = new string('d', 64) };
        if (rejection == "fingerprint") row = row with { PermissionFingerprint = new string('0', 64) };
        if (rejection is "review-json" or "review-json-and-fingerprint")
        {
            // Valid JSON with fewer rights is still not the exact review the host produced.
            string forgedJson = Encoding.UTF8.GetString(SdkJson.Serialize(new BundledRepairPermissions([], [])));
            row = row with { PermissionReviewJson = forgedJson };
            if (rejection == "review-json-and-fingerprint")
                row = row with { PermissionFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(forgedJson))) };
        }
        if (rejection == "missing-review") row = row with { PermissionReviewJson = null };
        if (rejection == "missing-fingerprint") row = row with { PermissionFingerprint = null };
        if (rejection == "revision")
            await fixture.Store.SetEnabledAsync("test.plugin", false, revision);
        byte[] before = await File.ReadAllBytesAsync(fixture.Store.StorePath);
        using CancellationTokenSource cancellation = new();
        if (rejection == "cancel") cancellation.Cancel();
        if (rejection == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.ReauthorizeBundledRepairAsync(row, revision, cancellation.Token));
        else if (rejection == "revision")
            await Assert.ThrowsAsync<PluginProfileConflictException>(() => fixture.Store.ReauthorizeBundledRepairAsync(row, revision));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReauthorizeBundledRepairAsync(row, revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.StorePath));
        Assert.False(File.Exists(fixture.Store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task ReauthorizationRejectsStalePermissionRowEvenWithLatestProfileRevision()
    {
        var fixture = await ReviewableRepairAsync();
        var grant = Assert.Single(fixture.State.Grants);
        var state = await fixture.Store.SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope,
            grant.Access, fixture.State.Revision); // Same rights, new grant revision needs a new review.
        var current = Assert.Single((await fixture.Store.PreviewBundledRepairsAsync()).Rows);
        Assert.True(current.CanReauthorize);
        Assert.NotEqual(fixture.Row.PermissionFingerprint, current.PermissionFingerprint);
        byte[] before = await File.ReadAllBytesAsync(fixture.Store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReauthorizeBundledRepairAsync(fixture.Row, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.StorePath));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("delivered-assembly")]
    [InlineData("cache")]
    public async Task ReauthorizationReverifiesReviewedTargetBytesBeforeWriting(string corruption)
    {
        var fixture = await ReviewableRepairAsync();
        string target = corruption switch
        {
            "manifest" => fixture.Target.CandidatePath!,
            "delivered-assembly" => Path.Combine(Path.GetDirectoryName(fixture.Target.CandidatePath!)!, "fixture.dll"),
            _ => fixture.Target.Artifact!.AssemblyPath
        };
        byte[] before = await File.ReadAllBytesAsync(fixture.Store.StorePath);
        await File.AppendAllTextAsync(target, " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ReauthorizeBundledRepairAsync(fixture.Row, fixture.State.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.StorePath));
        Assert.False(File.Exists(fixture.Store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task ReauthorizationMustPreservePreReselectionBackupBeforePublishing()
    {
        var fixture = await ReviewableRepairAsync();
        byte[] conflictingBackup = Encoding.UTF8.GetBytes("unrelated backup must not be overwritten");
        await File.WriteAllBytesAsync(fixture.Store.PreReselectionBackupPath, conflictingBackup);
        byte[] before = await File.ReadAllBytesAsync(fixture.Store.StorePath);
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.ReauthorizeBundledRepairAsync(fixture.Row, fixture.State.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.StorePath));
        Assert.Equal(conflictingBackup, await File.ReadAllBytesAsync(fixture.Store.PreReselectionBackupPath));
    }

    [Theory]
    [InlineData("grant-revision")]
    [InlineData("grant-access")]
    [InlineData("grant-revoked")]
    [InlineData("grant-scope")]
    [InlineData("new-grant")]
    [InlineData("new-source-permission")]
    [InlineData("removed-source-permission")]
    public async Task RightsChangedAfterConsentCannotBeInheritedAtRestart(string change)
    {
        // Keep another reviewed right present when one is removed: this still exercises
        // inheritance, rather than asking to run a build with no special rights at all.
        bool initiallyRestricted = change != "new-source-permission";
        var fixture = await ReviewableRepairAsync(source: true, restrictSource: initiallyRestricted);
        var state = await fixture.Store.ReauthorizeBundledRepairAsync(fixture.Row, fixture.State.Revision);
        var reviewedGrant = Assert.Single(state.Grants);
        string[] restrictions = initiallyRestricted ? ["shared"] : [];
        if (change == "new-source-permission") restrictions = ["shared"];
        else if (change == "removed-source-permission") restrictions = [];
        else
        {
            var scope = change switch
            {
                "grant-scope" => reviewedGrant.Scope with { RuleId = "different-rule" },
                "new-grant" => new PluginGrantScope("another-source", "another-board", "another-rule"),
                _ => reviewedGrant.Scope
            };
            FilterDataAccess access = change switch
            {
                "grant-access" => FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage,
                "grant-revoked" => FilterDataAccess.None,
                _ => reviewedGrant.Access
            };
            state = await Store().SetGrantAsync(reviewedGrant.PluginId, reviewedGrant.FilterKey, scope, access, state.Revision);
            if (change == "grant-revision") Assert.True(Assert.Single(state.Grants).Revision > reviewedGrant.Revision);
        }
        byte[] grants = SdkJson.Serialize(state.Grants);
        state = await Store().ApplyBundledStartupAsync([fixture.Target], [fixture.Proof], restrictions, state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants)); // Denial neither revokes nor broadens the changed rights.
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("description")]
    [InlineData("owner")]
    public async Task AdditionalRepairBlockersAreNotEligibleForPermissionConsent(string blocker)
    {
        var old = await Delivered("blocked-consent-old", "A", source: true);
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"),
            FilterDataAccess.OriginalImage, state.Revision);
        if (blocker == "pin") state = await store.SelectRollbackAsync("test.plugin", state.Revision);
        var target = await Delivered("blocked-consent-new", "B", source: true);
        if (blocker == "description")
        {
            target.Candidate = target.Candidate with { Description = target.Candidate.Description! with { Name = "Changed contract" } };
            string root = Path.GetDirectoryName(target.Candidate.CandidatePath!)!;
            var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(root)).Candidates,
                candidate => candidate.Kind == PluginCandidateKind.PrebuiltManifest);
            target.Proof = await PluginInstallationInventory.RecordDeliveredAsync(root, installed,
                target.Candidate.Artifact!, target.Candidate.Description!);
        }
        var candidates = new List<PluginCandidateObservation> { target.Candidate };
        if (blocker == "owner") candidates.Add(target.Candidate with
        {
            CandidateId = "independent-owner", PluginId = "other.plugin",
            Description = target.Candidate.Description! with { PluginId = "other.plugin" }
        });
        state = await store.ApplyBundledStartupAsync(candidates, [target.Proof], ["shared"], state.Revision);
        var row = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows, item => item.PluginId == "test.plugin");
        Assert.False(row.CanConfirm);
        Assert.False(row.CanReauthorize);
        Assert.Equal(blocker switch { "pin" => "pinned", "description" => "description-changed", _ => "source-owner-conflict" }, row.BlockedReason);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReauthorizeBundledRepairAsync(row, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Fact]
    public async Task OrdinaryRepairWithoutInheritedRightsDoesNotOfferReauthorization()
    {
        var old = await Delivered("no-rights-old", "A");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var next = await Delivered("no-rights-new", "B");
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], [], state.Revision);
        var row = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        Assert.True(row.CanConfirm);
        Assert.False(row.CanReauthorize);
        Assert.Null(row.PermissionReviewJson);
        Assert.Null(row.PermissionFingerprint);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReauthorizeBundledRepairAsync(row, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LiveRestrictionProviderRequiresSharedPermissionGateForReauthorization(bool liveRestrictionsPresent)
    {
        var fixture = await ReviewableRepairAsync(source: true);
        PluginProfileStore unserialized = new(profile, static _ => true,
            () => liveRestrictionsPresent ? new[] { "shared" } : Array.Empty<string>());
        var preview = await unserialized.PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        Assert.True(row.CanReauthorize);
        byte[] before = await File.ReadAllBytesAsync(fixture.Store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unserialized.ReauthorizeBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.StorePath));
    }

    [Fact]
    public async Task ReauthorizationWaitsForPermissionWriterAndRejectsItsNowStaleReview()
    {
        var fixture = await ReviewableRepairAsync(source: true, restrictSource: false);
        using SemaphoreSlim permissionGate = new(1, 1);
        string[] restrictions = [];
        PluginProfileStore serialized = new(profile, static _ => true, () => restrictions, permissionGate);
        byte[] before = await File.ReadAllBytesAsync(serialized.StorePath);
        await permissionGate.WaitAsync();
        Task<PluginProfileSnapshot> consent;
        try
        {
            consent = serialized.ReauthorizeBundledRepairAsync(fixture.Row, fixture.State.Revision);
            Assert.False(consent.IsCompleted);
            // Waiting for source authority must not already hold the profile writer lease.
            Assert.Equal(fixture.State.Revision, (await Store().ReadAsync().WaitAsync(TimeSpan.FromSeconds(10))).Revision);
            restrictions = ["shared"]; // A source-permission writer commits while holding the SAME gate.
        }
        finally { permissionGate.Release(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => consent.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, permissionGate.CurrentCount);
        Assert.Equal(before, await File.ReadAllBytesAsync(serialized.StorePath));

        var preview = await serialized.PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        AssertExactPermissionReview(row, fixture.State.Grants, ["shared"]);
        var state = await serialized.ReauthorizeBundledRepairAsync(row, preview.ProfileRevision).WaitAsync(TimeSpan.FromSeconds(10));
        state = await new PluginProfileStore(profile, static _ => true, () => restrictions, permissionGate)
            .ApplyBundledStartupAsync([fixture.Target], [fixture.Proof], restrictions, state.Revision);
        Assert.Equal(fixture.Target.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(SdkJson.Serialize(fixture.State.Grants), SdkJson.Serialize(state.Grants));
    }

    [Fact]
    public async Task CancelledReauthorizationWaitingForPermissionGateDoesNotReleaseWritersLease()
    {
        var fixture = await ReviewableRepairAsync(source: true);
        using SemaphoreSlim permissionGate = new(1, 1);
        PluginProfileStore serialized = new(profile, static _ => true, () => new[] { "shared" }, permissionGate);
        byte[] before = await File.ReadAllBytesAsync(serialized.StorePath);
        using CancellationTokenSource cancellation = new();
        await permissionGate.WaitAsync();
        try
        {
            var consent = serialized.ReauthorizeBundledRepairAsync(fixture.Row, fixture.State.Revision, cancellation.Token);
            Assert.False(consent.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consent.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, permissionGate.CurrentCount);
            Assert.Equal(before, await File.ReadAllBytesAsync(serialized.StorePath));
        }
        finally { permissionGate.Release(); }
    }

    [Fact]
    public async Task PersistedReauthorizationCannotBeConsumedWithUngatedLivePermissionAuthority()
    {
        var fixture = await ReviewableRepairAsync(source: true);
        var state = await fixture.Store.ReauthorizeBundledRepairAsync(fixture.Row, fixture.State.Revision);
        // A consent recorded by a serialized writer is not a license to consume it
        // later through the public legacy constructor without that authority gate.
        PluginProfileStore ungated = new(profile, static _ => true, () => new[] { "shared" });
        state = await ungated.ApplyBundledStartupAsync([fixture.Target], [fixture.Proof], ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(SdkJson.Serialize(fixture.State.Grants), SdkJson.Serialize(state.Grants));
    }

    [Fact]
    public async Task NewLivePermissionBeforeRestartInvalidatesPersistedReauthorization()
    {
        var fixture = await ReviewableRepairAsync(source: true, restrictSource: false);
        var state = await fixture.Store.ReauthorizeBundledRepairAsync(fixture.Row, fixture.State.Revision);
        using SemaphoreSlim permissionGate = new(1, 1);
        string[] restrictions = [];
        PluginProfileStore restarting = new(profile, static _ => true, () => restrictions, permissionGate);
        await permissionGate.WaitAsync();
        Task<PluginProfileSnapshot> startup;
        try
        {
            startup = restarting.ApplyBundledStartupAsync([fixture.Target], [fixture.Proof], [], state.Revision);
            Assert.False(startup.IsCompleted);
            restrictions = ["shared"];
        }
        finally { permissionGate.Release(); }
        state = await startup.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(SdkJson.Serialize(fixture.State.Grants), SdkJson.Serialize(state.Grants));
    }

    private static void AssertExactPermissionReview(BundledCandidateRepair row,
        IReadOnlyList<PluginGrantSnapshot> grants, IReadOnlyList<string> sources)
    {
        Assert.Equal("inherited-grants", row.BlockedReason);
        Assert.False(row.CanConfirm);
        Assert.True(row.CanReauthorize);
        Assert.False(string.IsNullOrWhiteSpace(row.PermissionReviewJson));
        Assert.False(string.IsNullOrWhiteSpace(row.PermissionFingerprint));
        var review = SdkJson.Deserialize<BundledRepairPermissions>(Encoding.UTF8.GetBytes(row.PermissionReviewJson!));
        Assert.Equal(grants.Count, review.FilterGrants.Count);
        foreach (var grant in grants) Assert.Contains(grant, review.FilterGrants);
        Assert.Equal(sources.Order(StringComparer.Ordinal).ToArray(), review.SourceIds.ToArray());
    }

    private static async Task<PluginProfileSnapshot> AddReviewedFilterGrantsAsync(PluginProfileStore store, PluginProfileSnapshot state)
    {
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "board-a", "rule-a"),
            FilterDataAccess.OriginalImage, state.Revision);
        return await store.SetGrantAsync("test.plugin", "dimensions", new("another-source", "board-b", "rule-b"),
            FilterDataAccess.CandidateMetadata, state.Revision);
    }

    private async Task<(PluginProfileStore Store, PluginProfileSnapshot State, PluginCandidateObservation Target,
        DeliveredCandidateProvenance Proof, BundledCandidateRepair Row)> ReviewableRepairAsync(
        bool samePath = false, bool source = false, bool restrictSource = true)
    {
        var old = await Delivered("consent-old", "A", source: source);
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"),
            FilterDataAccess.OriginalImage, state.Revision);
        var next = await Delivered(samePath ? "consent-old" : "consent-target", "B", source: source);
        string[] restrictions = source && restrictSource ? ["shared"] : [];
        state = await store.ApplyBundledStartupAsync([next.Candidate], [next.Proof], restrictions, state.Revision);
        Assert.Null(state.Plugins[0].Active);
        var row = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        AssertExactPermissionReview(row, state.Grants, restrictions);
        return (store, state, next.Candidate, next.Proof, row);
    }
}
