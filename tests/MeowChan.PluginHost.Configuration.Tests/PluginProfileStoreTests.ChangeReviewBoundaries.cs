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
    // These are deliberately separate from the declaration-review happy path. Delivered writes
    // synthetic manifests/assemblies under this test's temporary profile; nothing loads a provider.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task BundledChangeBoundary_MaterialCapabilitiesNeedDeclarationAndSeparateRightsConsent(
        bool samePath, bool filterRights, bool sourceRights)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath, filterRights, sourceRights);
        Assert.Equal(samePath, f.Old.CandidateId == f.Target.CandidateId);
        Assert.False(f.Row.CanConfirm);
        Assert.False(f.Row.CanReauthorize);
        var review = SdkJson.Deserialize<BundledPluginChangeReview>(Encoding.UTF8.GetBytes(f.Row.ChangeReviewJson!));
        Assert.Equal(SourceCapabilities.ThreadRead, Assert.Single(review.PreviousDescription.Sources).Capabilities);
        Assert.Equal(SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture,
            Assert.Single(review.NewDescription.Sources).Capabilities);
        Assert.Equal(SdkJson.Serialize(f.Configuration), SdkJson.Serialize(review.PreviousConfiguration));
        Assert.Equal(SdkJson.Serialize(f.State.Grants), SdkJson.Serialize(review.Permissions.FilterGrants));
        Assert.Equal(sourceRights ? new[] { "shared" } : [], review.Permissions.SourceIds.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(f.Row.ChangeReviewJson!))),
            f.Row.ChangeFingerprint);
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ConfirmBundledRepairAsync(f.Row, f.State.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ReauthorizeBundledRepairAsync(f.Row, f.State.Revision));
        // Even explicit rights approval cannot stand in for declaration acknowledgement.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ConfirmBundledChangeAsync(
            f.Row, f.Configuration, acknowledgeChanges: false, authorizePermissions: true, f.State.Revision));
        if (filterRights || sourceRights)
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ConfirmBundledChangeAsync(
                f.Row, f.Configuration, acknowledgeChanges: true, authorizePermissions: false, f.State.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);

        var state = f.State;
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([f.Target], [f.Proof], sourceRights ? ["shared"] : [], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Null(state.Plugins[0].Pending);
            Assert.Equal(f.Old.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Equal(f.Old.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.False(state.Plugins[0].Enabled);
            Assert.Null(state.Plugins[0].Pinned);
            Assert.Equal(SdkJson.Serialize(f.State.Grants), SdkJson.Serialize(state.Grants));
            Assert.Equal(SdkJson.Serialize(f.State.SelectedSourceOwners), SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.True(Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows).CanReviewChanges);
            Assert.False(File.Exists(f.Store.PreReselectionBackupPath));
        }
    }

    [Theory]
    [InlineData(false, "candidate")]
    [InlineData(true, "candidate")]
    [InlineData(false, "execution")]
    [InlineData(true, "execution")]
    [InlineData(false, "old-execution")]
    [InlineData(true, "old-execution")]
    [InlineData(false, "path")]
    [InlineData(true, "path")]
    [InlineData(false, "slot")]
    [InlineData(true, "slot")]
    [InlineData(false, "build-changed")]
    [InlineData(true, "build-changed")]
    [InlineData(false, "reason")]
    [InlineData(true, "reason")]
    [InlineData(false, "json")]
    [InlineData(true, "json")]
    [InlineData(false, "json-and-fingerprint")]
    [InlineData(true, "json-and-fingerprint")]
    [InlineData(false, "missing-json")]
    [InlineData(true, "missing-json")]
    [InlineData(false, "fingerprint")]
    [InlineData(true, "fingerprint")]
    [InlineData(false, "missing-fingerprint")]
    [InlineData(true, "missing-fingerprint")]
    [InlineData(false, "permission-json")]
    [InlineData(true, "permission-json")]
    public async Task BundledChangeBoundary_OnlyExactHostPreviewCanBeConfirmed(bool samePath, string forgery)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        var review = SdkJson.Deserialize<BundledPluginChangeReview>(Encoding.UTF8.GetBytes(f.Row.ChangeReviewJson!));
        // Valid, self-consistently hashed JSON still cannot omit rights from the actual review.
        string forgedJson = Encoding.UTF8.GetString(SdkJson.Serialize(review with { Permissions = new([], []) }));
        Assert.NotEqual(f.Row.ChangeReviewJson, forgedJson);
        var row = forgery switch
        {
            "candidate" => f.Row with { NewCandidateId = f.Old.CandidateId + "-forged" },
            "execution" => f.Row with { NewExecutionRevision = new string('d', 64) },
            "old-execution" => f.Row with { OldExecutionRevision = new string('d', 64) },
            "path" => f.Row with { NewPath = f.Old.CandidatePath + ".forged" },
            "slot" => f.Row with { SlotId = f.Row.SlotId + "-forged" },
            "build-changed" => f.Row with { BuildChanged = false },
            "reason" => f.Row with { BlockedReason = null },
            "json" => f.Row with { ChangeReviewJson = forgedJson },
            "json-and-fingerprint" => f.Row with { ChangeReviewJson = forgedJson,
                ChangeFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(forgedJson))) },
            "missing-json" => f.Row with { ChangeReviewJson = null },
            "fingerprint" => f.Row with { ChangeFingerprint = new string('0', 64) },
            "missing-fingerprint" => f.Row with { ChangeFingerprint = null },
            _ => f.Row with { PermissionReviewJson = "{}" }
        };
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            row, f.Configuration, true, true, f.State.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundledChangeBoundary_StaleRevisionAndStaleRightsRowAreIndependentlyRejected(bool samePath)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        var grant = Assert.Single(f.State.Grants);
        var state = await f.Store.SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, grant.Access, f.State.Revision);
        var row = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
        Assert.True(row.CanReviewChanges);
        Assert.NotEqual(f.Row.ChangeFingerprint, row.ChangeFingerprint); // Same access, different grant revision.
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<PluginProfileConflictException>(() => Store().ConfirmBundledChangeAsync(
            row, f.Configuration, true, true, f.State.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, f.Configuration, true, true, state.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);
    }

    [Theory]
    [InlineData(false, "manifest")]
    [InlineData(true, "manifest")]
    [InlineData(false, "delivered-assembly")]
    [InlineData(true, "delivered-assembly")]
    [InlineData(false, "cache")]
    [InlineData(true, "cache")]
    public async Task BundledChangeBoundary_ReviewedTargetIsReverifiedBeforeAnyPublication(bool samePath, string corruption)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        string file = corruption switch
        {
            "manifest" => f.Target.CandidatePath!,
            "delivered-assembly" => Path.Combine(Path.GetDirectoryName(f.Target.CandidatePath!)!, "fixture.dll"),
            _ => f.Target.Artifact!.AssemblyPath
        };
        await File.AppendAllTextAsync(file, " ");
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, f.Configuration, true, true, f.State.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);
    }

    [Theory]
    [InlineData("missing-required")]
    [InlineData("wrong-version")]
    [InlineData("out-of-range")]
    [InlineData("wrong-kind")]
    [InlineData("duplicate-key")]
    [InlineData("missing-source")]
    [InlineData("unknown-source")]
    [InlineData("source-schema")]
    public async Task BundledChangeBoundary_InvalidCorrectedSettingsCannotPublish(string invalid)
    {
        var schema = new SettingsSchema("2", [new("count", SettingScope.Plugin, SettingKind.Integer,
            "Count", IsRequired: true, Minimum: 10, Maximum: 20)]);
        var f = await ChangeBoundaryFixtureAsync(settings: schema);
        var configuration = f.Configuration with { PluginSettings = Values("2", 15) };
        Assert.Empty(SdkValidation.ValidateConfiguration(f.Target.Description!, configuration));
        configuration = invalid switch
        {
            "missing-required" => configuration with { PluginSettings = new("2", []) },
            "wrong-version" => configuration with { PluginSettings = Values("1", 15) },
            "out-of-range" => configuration with { PluginSettings = Values("2", 21) },
            "wrong-kind" => configuration with { PluginSettings = new("2", [new("count", SettingKind.Text, Text: "15")]) },
            "duplicate-key" => configuration with { PluginSettings = new("2", [new("count", SettingKind.Integer, Integer: 15),
                new("count", SettingKind.Integer, Integer: 16)]) },
            "missing-source" => configuration with { SourceSettings = new Dictionary<string, SettingsSnapshot>() },
            "unknown-source" => configuration with { SourceSettings = new Dictionary<string, SettingsSnapshot>(configuration.SourceSettings)
                { ["unreviewed"] = new("1", []) } },
            _ => configuration with { SourceSettings = new Dictionary<string, SettingsSnapshot> { ["shared"] = new("wrong", []) } }
        };
        Assert.NotEmpty(SdkValidation.ValidateConfiguration(f.Target.Description!, configuration));
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<PluginConfigurationValidationException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, configuration, true, true, f.State.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);
        var state = await Store().ReadAsync();
        Assert.Null(state.Plugins[0].Active);
        Assert.Null(state.Plugins[0].Pending);
        Assert.Equal(SdkJson.Serialize(f.Configuration), SdkJson.Serialize(state.Plugins[0].LastGood!.Configuration));
    }

    [Theory]
    [InlineData(false, "unproven-history")]
    [InlineData(true, "unproven-history")]
    [InlineData(false, "unproven-target")]
    [InlineData(true, "unproven-target")]
    [InlineData(false, "pin")]
    [InlineData(true, "pin")]
    [InlineData(false, "owner")]
    [InlineData(true, "owner")]
    [InlineData(false, "source-identity")]
    [InlineData(true, "source-identity")]
    [InlineData(false, "duplicate-slot")]
    [InlineData(true, "duplicate-slot")]
    [InlineData(false, "local-choice")]
    [InlineData(true, "local-choice")]
    public async Task BundledChangeBoundary_ConsentCannotBypassProvenanceOwnershipOrUniqueness(bool samePath, string blocker)
    {
        var old = await Delivered("boundary-old", "A", source: true);
        var store = Store();
        var state = blocker == "unproven-history"
            ? await store.ApplyStartupAsync([old.Candidate], 0)
            : await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        if (blocker == "pin") state = await store.SelectRollbackAsync("test.plugin", state.Revision);
        byte[] owners = SdkJson.Serialize(state.SelectedSourceOwners);
        var target = await ChangeBoundaryDeliveryAsync(samePath ? "boundary-old" : "boundary-new", "B",
            sourceId: blocker == "source-identity" ? "different-source" : "shared");
        var candidates = new List<PluginCandidateObservation> { target.Candidate };
        var proofs = new List<DeliveredCandidateProvenance>();
        if (blocker != "unproven-target") proofs.Add(target.Proof);
        if (blocker == "owner") candidates.Add(target.Candidate with { CandidateId = "independent-owner", PluginId = "other.plugin",
            Description = target.Candidate.Description! with { PluginId = "other.plugin" } });
        if (blocker == "local-choice") candidates.Add(target.Candidate with { CandidateId = "unproven-local-choice",
            CandidatePath = Path.Combine(profile, "plugins", "local.cs") });
        if (blocker == "duplicate-slot")
        {
            var duplicate = await ChangeBoundaryDeliveryAsync("boundary-duplicate", "B", fileName: "FIXTURE.plugin.json");
            candidates.Add(duplicate.Candidate);
            proofs.Add(duplicate.Proof);
        }
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync(candidates, proofs, [], state.Revision);
            var plugin = state.Plugins.Single(p => p.PluginId == "test.plugin");
            Assert.NotEqual(target.Candidate.Artifact!.ExecutionRevision, plugin.Active?.ExecutionRevision);
            Assert.Equal(old.Candidate.CandidateId, plugin.SelectedCandidateId);
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            var row = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows, r => r.PluginId == "test.plugin");
            Assert.False(row.CanReviewChanges);
            Assert.False(row.CanConfirm);
            Assert.False(row.CanReauthorize);
            Assert.Null(row.ChangeReviewJson);
            Assert.Equal(blocker switch
            {
                "unproven-history" => "unproven-history",
                "unproven-target" => "no-proven-target",
                "pin" => "pinned",
                "duplicate-slot" or "local-choice" => "ambiguous",
                _ => "description-changed"
            }, row.BlockedReason);
            var before = await ChangeBoundaryFilesAsync(store);
            var configuration = plugin.LastGood!.Configuration;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
                row, configuration, true, true, state.Revision));
            // A caller cannot turn a blocked row into a review merely by copying in a payload.
            var forged = row with { BlockedReason = "description-changed", NewCandidateId = target.Candidate.CandidateId,
                ChangeReviewJson = "{}", ChangeFingerprint = new string('0', 64) };
            Assert.True(forged.CanReviewChanges);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
                forged, configuration, true, true, state.Revision));
            await AssertChangeBoundaryFilesAsync(store, before);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundledChangeBoundary_CancellationAndConflictingBackupKeepOriginalBytes(bool samePath)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        var before = await ChangeBoundaryFilesAsync(f.Store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, f.Configuration, true, true, f.State.Revision, cancellation.Token));
        await AssertChangeBoundaryFilesAsync(f.Store, before);
        byte[] foreignBackup = Encoding.UTF8.GetBytes("unrelated immutable backup must not be replaced");
        await File.WriteAllBytesAsync(f.Store.PreReselectionBackupPath, foreignBackup);
        before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<IOException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, f.Configuration, true, true, f.State.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);
        Assert.Equal(foreignBackup, await File.ReadAllBytesAsync(f.Store.PreReselectionBackupPath));
    }

    [Fact]
    public async Task BundledChangeBoundary_CancelWhileWaitingForPermissionWriterDoesNotReleaseItsGate()
    {
        var f = await ChangeBoundaryFixtureAsync();
        using SemaphoreSlim gate = new(1, 1);
        var store = new PluginProfileStore(profile, _ => true, () => ["shared"], gate);
        var before = await ChangeBoundaryFilesAsync(store);
        using var cancellation = new CancellationTokenSource();
        await gate.WaitAsync();
        try
        {
            var confirm = store.ConfirmBundledChangeAsync(f.Row, f.Configuration, true, true, f.State.Revision, cancellation.Token);
            Assert.False(confirm.IsCompleted);
            Assert.Equal(f.State.Revision, (await Store().ReadAsync().WaitAsync(TimeSpan.FromSeconds(10))).Revision);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirm.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, gate.CurrentCount);
            await AssertChangeBoundaryFilesAsync(store, before);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task BundledChangeBoundary_LivePermissionsWithoutSharedGateCannotConfirm()
    {
        var f = await ChangeBoundaryFixtureAsync();
        var store = new PluginProfileStore(profile, _ => true, () => ["shared"]);
        var preview = await store.PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        Assert.True(row.CanReviewChanges);
        var before = await ChangeBoundaryFilesAsync(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledChangeAsync(
            row, f.Configuration, true, true, preview.ProfileRevision));
        await AssertChangeBoundaryFilesAsync(store, before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BundledChangeBoundary_BConsentCannotAuthorizeReplacementCAndFirstBackupStaysImmutable(
        bool samePath, bool activateB)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        byte[] original = await File.ReadAllBytesAsync(f.Store.StorePath);
        var state = await Store().ConfirmBundledChangeAsync(f.Row, f.Configuration, true, true, f.State.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.PreReselectionBackupPath));
        if (activateB)
        {
            state = await Store().ApplyBundledStartupAsync([f.Target], [f.Proof], ["shared"], state.Revision);
            Assert.Equal(f.Target.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
        }
        var third = await ChangeBoundaryDeliveryAsync(samePath ? "boundary-old" : "boundary-new", "C",
            capabilities: SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture | SourceCapabilities.Previews);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([third.Candidate], [third.Proof], ["shared"], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Equal(SdkJson.Serialize(f.State.Grants), SdkJson.Serialize(state.Grants));
            Assert.Equal(SdkJson.Serialize(f.State.SelectedSourceOwners), SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Equal(f.Old.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.False(state.Plugins[0].Enabled);
            Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.PreReselectionBackupPath));
        }
        var row = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
        Assert.True(row.CanReviewChanges);
        Assert.Equal(third.Candidate.Artifact!.ExecutionRevision, row.NewExecutionRevision);
        Assert.NotEqual(f.Row.ChangeFingerprint, row.ChangeFingerprint);
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, f.Configuration, true, true, state.Revision)); // Latest revision cannot rescue B's old row.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            row, f.Configuration, true, false, state.Revision)); // C needs its own rights approval too.
        await AssertChangeBoundaryFilesAsync(f.Store, before);
        state = await Store().ConfirmBundledChangeAsync(row, f.Configuration, true, true, state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(third.Candidate.Artifact.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([third.Candidate], [third.Proof], ["shared"], state.Revision);
            Assert.Equal(third.Candidate.Artifact.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(f.Configuration), SdkJson.Serialize(Active(state).Configuration));
            Assert.Null(state.Plugins[0].Pending);
            Assert.Null(state.Plugins[0].Pinned);
            Assert.False(state.Plugins[0].Enabled);
            Assert.Equal(f.Old.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(SdkJson.Serialize(f.State.Grants), SdkJson.Serialize(state.Grants));
            Assert.Equal(SdkJson.Serialize(f.State.SelectedSourceOwners), SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.PreReselectionBackupPath));
        }
    }

    private sealed record ChangeBoundaryFixture(PluginProfileStore Store, PluginProfileSnapshot State,
        PluginCandidateObservation Old, PluginCandidateObservation Target, DeliveredCandidateProvenance Proof,
        BundledCandidateRepair Row, PluginConfiguration Configuration);

    private async Task<ChangeBoundaryFixture> ChangeBoundaryFixtureAsync(bool samePath = false,
        bool filterRights = true, bool sourceRights = true, SettingsSchema? settings = null)
    {
        var old = await Delivered("boundary-old", "A", source: true);
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 17) }, state.Revision);
        state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        var configuration = Active(state).Configuration;
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        if (filterRights) state = await store.SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"),
            FilterDataAccess.OriginalImage, state.Revision);
        var target = await ChangeBoundaryDeliveryAsync(samePath ? "boundary-old" : "boundary-new", "B", settings: settings);
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], sourceRights ? ["shared"] : [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        var row = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        Assert.Equal("description-changed", row.BlockedReason);
        Assert.True(row.CanReviewChanges);
        Assert.Equal(target.Candidate.Artifact!.ExecutionRevision, row.NewExecutionRevision);
        return new(store, state, old.Candidate, target.Candidate, target.Proof, row, configuration);
    }

    private async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> ChangeBoundaryDeliveryAsync(
        string installation, string build, string fileName = "fixture.plugin.json", string sourceId = "shared",
        SettingsSchema? settings = null, SourceCapabilities capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture)
    {
        var target = await Delivered(installation, build, fileName, source: true, settings: settings);
        var description = target.Candidate.Description!;
        target.Candidate = target.Candidate with { Description = description with
            { Sources = [description.Sources[0] with { SourceId = sourceId, Capabilities = capabilities }] } };
        Assert.Empty(SdkValidation.ValidateDescription(target.Candidate.Description!));
        string root = Path.GetDirectoryName(target.Candidate.CandidatePath!)!;
        var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(root)).Candidates,
            c => c.Kind == PluginCandidateKind.PrebuiltManifest);
        target.Proof = await PluginInstallationInventory.RecordDeliveredAsync(root, installed,
            target.Candidate.Artifact!, target.Candidate.Description!);
        return target;
    }

    // Snapshot every store-owned file, including backup/hash sidecars and temporary files. The
    // established stable lease is excluded: opening it is not publication of profile/consent data.
    private static async Task<Dictionary<string, byte[]>> ChangeBoundaryFilesAsync(PluginProfileStore store)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(Path.GetDirectoryName(store.StorePath)!))
            if (Path.GetFileName(path) != "state.lock") files.Add(Path.GetFileName(path), await File.ReadAllBytesAsync(path));
        return files;
    }

    private static async Task AssertChangeBoundaryFilesAsync(PluginProfileStore store, Dictionary<string, byte[]> before)
    {
        var after = await ChangeBoundaryFilesAsync(store);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal).ToArray(), after.Keys.Order(StringComparer.Ordinal).ToArray());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }
}
