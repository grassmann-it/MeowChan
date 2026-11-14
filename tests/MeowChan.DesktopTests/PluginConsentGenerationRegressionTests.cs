using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

// Dialog -> changing plugin file / rights state -> stale or restored review -> restart.
// Everything runs through the REAL profile store, PluginMultiApproval, review parser, draft and consent.
// The plugin files are inert synthetic bytes in a private temporary directory: no plugin domain,
// worker, compiler, network, ~/.oChan or personal profile is involved.
//
// Existing PluginChangeReviewTests cover the row-local consent object and BundledChangeApprovalTests cover the
// controller with a recording store. These tests cover the combination against the real store: a consent
// given for one generation of the review must not authorize another generation, even when the visible
// values were restored, and an unchanged review of a second plugin must keep working.
public sealed class PluginConsentGenerationRegressionTests
{
    private static readonly PluginGrantScope GrantScope = new("fixture-source", "board", "rule");

    [Fact]
    public async Task ConsentGeneration_RightsRestoredToEarlierValueDoNotRevive_OldDialogDecision()
    {
        using Fixture f = await Fixture.CreateAsync();
        BundledCandidateRepair alphaRow = f.Row("alpha");
        BundledCandidateRepair betaRow = f.Row("beta");
        PluginGrantSnapshot originalGrant = Assert.Single(Fixture.Review(alphaRow).Permissions.FilterGrants);

        // The dialog is open and the user has acknowledged exactly this review (including the rights).
        Dialog old = Dialog.Open(alphaRow, f.Revision, acknowledged: true);
        PluginApprovalChoice oldChoice = old.Save(authorizePermissions: true);

        // Meanwhile the permission state changes and is changed back to the earlier access value.
        var state = await f.Store.SetGrantAsync("alpha", "dimensions", GrantScope, FilterDataAccess.None, f.Revision);
        BundledCandidateRepair mid = Assert.Single((await f.Store.PreviewBundledRepairsAsync()).Rows, row => row.PluginId == "alpha");
        Assert.Empty(Fixture.Review(mid).Permissions.FilterGrants);
        state = await f.Store.SetGrantAsync("alpha", "dimensions", GrantScope, FilterDataAccess.OriginalImage, state.Revision);
        BundledCandidateRepairPreview preview = await f.Store.PreviewBundledRepairsAsync();
        BundledCandidateRepair fresh = Assert.Single(preview.Rows, row => row.PluginId == "alpha");
        Assert.Equal(state.Revision, preview.ProfileRevision);
        PluginGrantSnapshot restoredGrant = Assert.Single(Fixture.Review(fresh).Permissions.FilterGrants);
        Assert.Equal(originalGrant.Access, restoredGrant.Access); // Same visible access value ...
        Assert.Equal(originalGrant.Revision + 2, restoredGrant.Revision); // ... but a new permission generation.
        Assert.NotEqual(alphaRow.ChangeFingerprint, fresh.ChangeFingerprint);
        Assert.Equal(betaRow, Assert.Single(preview.Rows, row => row.PluginId == "beta")); // Other plugin's review is untouched.

        Dictionary<string, byte[]> before = await Fixture.FilesAsync(f.Store);
        PluginMultiApproval session = new(f.Store);
        await session.SubmitAsync([oldChoice], () => true); // Old revision.
        Assert.Equal(PluginApprovalState.NeedsReview, session.Receipts[0].State);
        // Re-basing the old decision on the latest revision does not make the old row current either.
        await session.SubmitAsync([oldChoice with { Revision = preview.ProfileRevision }], () => true);
        Assert.Equal(PluginApprovalState.NeedsReview, session.Receipts[1].State);
        Assert.Equal("plugins.approval.needsReview", session.IssueKey);
        Assert.True(session.MayOffer("alpha"));
        await Fixture.AssertFilesAsync(f.Store, before);
        Assert.Equal(f.PendingJson(f.InitialState, "alpha"), f.PendingJson(await f.Store.ReadAsync(), "alpha"));

        // A re-opened dialog starts without consent, and edits made before the acknowledgement are not carried along.
        Dialog reopened = Dialog.Open(fresh, preview.ProfileRevision, acknowledged: false);
        Assert.False(reopened.Consent.IsAcknowledged);
        reopened.Consent.SetAcknowledged(true);
        reopened.Draft.Plugin.Set("label", "interim");
        Assert.False(reopened.Consent.IsAcknowledged);
        reopened.Draft.Plugin.Set("label", "chosen · λ");
        reopened.Consent.SetAcknowledged(true);
        PluginApprovalChoice current = reopened.Save(authorizePermissions: true);
        await session.SubmitAsync([current], () => true);
        PluginApprovalReceipt saved = session.Receipts[2];
        Assert.Equal(PluginApprovalState.Saved, saved.State);
        Assert.Equal(preview.ProfileRevision + 1, saved.Revision);
        Assert.Null(session.IssueKey);
        Assert.False(session.MayOffer("alpha"));

        PluginProfileSnapshot after = await f.Store.ReadAsync();
        PluginActivationSnapshot alpha = f.Plugin(after, "alpha");
        Assert.Null(alpha.Active); // Consent for next start is not activation.
        Assert.False(alpha.Enabled);
        Assert.Equal(SdkJson.Serialize(current.ChangeConfiguration!), SdkJson.Serialize(alpha.Pending!.Configuration));
        Assert.Equal("chosen · λ", alpha.Pending.Configuration.PluginSettings.Values.Single(v => v.Key == "label").Text);
        Assert.Equal(restoredGrant, Assert.Single(after.Grants, grant => grant.PluginId == "alpha"));

        // The first, long gone decision still cannot be replayed, neither now nor after restarts.
        for (int restart = 0; restart < 2; restart++)
        {
            after = await new PluginProfileStore(f.Root).ApplyBundledStartupAsync(f.Targets, f.TargetProofs, [], after.Revision);
            alpha = f.Plugin(after, "alpha");
            Assert.Equal(f.TargetExecution("alpha"), alpha.Active!.ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(current.ChangeConfiguration!), SdkJson.Serialize(alpha.Active.Configuration));
            Assert.Equal(restoredGrant, Assert.Single(after.Grants, grant => grant.PluginId == "alpha"));
            Assert.Null(alpha.Pending);
            Assert.False(alpha.Enabled);
        }
        byte[] stored = await File.ReadAllBytesAsync(f.Store.StorePath);
        PluginMultiApproval replay = new(new PluginProfileStore(f.Root));
        await replay.SubmitAsync([oldChoice with { Revision = after.Revision }], () => true);
        Assert.Equal(PluginApprovalState.NeedsReview, Assert.Single(replay.Receipts).State);
        await replay.SubmitAsync([current with { Revision = after.Revision }], () => true);
        Assert.Equal(PluginApprovalState.NeedsReview, replay.Receipts[1].State);
        Assert.Equal(stored, await File.ReadAllBytesAsync(f.Store.StorePath));
    }

    [Fact]
    public async Task ConsentGeneration_PluginFileReplacedAndRestoredDuringDialogNeedsFreshDecisionAndRestartUsesIt()
    {
        using Fixture f = await Fixture.CreateAsync();
        BundledCandidateRepair rowB = f.Row("alpha");
        BundledCandidateRepair betaRow = f.Row("beta");
        long revisionB = f.Revision;
        Dialog old = Dialog.Open(rowB, revisionB, acknowledged: true);
        PluginApprovalChoice oldChoice = old.Save(authorizePermissions: true);

        // The plugin file at the same location becomes another build while the dialog is open.
        var third = await f.DeliverTargetAsync("alpha", "c");
        PluginProfileStore store = new(f.Root);
        var afterC = await store.ApplyBundledStartupAsync(f.TargetsWith("alpha", third.Candidate), f.ProofsWith("alpha", third.Proof), [], revisionB);
        Assert.NotEqual(revisionB, afterC.Revision);
        BundledCandidateRepair rowC = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows, row => row.PluginId == "alpha");
        Assert.Equal(third.Candidate.Artifact!.ExecutionRevision, rowC.NewExecutionRevision);
        Assert.NotEqual(rowB.NewExecutionRevision, rowC.NewExecutionRevision);
        // ChangeFingerprint covers declarations/settings/rights, not executable identity. These builds
        // intentionally share that payload; the public lifecycle binds execution separately in the frozen row.
        Assert.Equal(rowB.ChangeReviewJson, rowC.ChangeReviewJson);
        Assert.Equal(rowB.ChangeFingerprint, rowC.ChangeFingerprint);
        Assert.NotEqual(rowB, rowC);
        Dictionary<string, byte[]> before = await Fixture.FilesAsync(store);
        PluginMultiApproval session = new(store);
        await session.SubmitAsync([oldChoice], () => true);
        Assert.Equal(PluginApprovalState.NeedsReview, Assert.Single(session.Receipts).State);
        await Fixture.AssertFilesAsync(store, before);
        await session.SubmitAsync([oldChoice with { Revision = afterC.Revision }], () => true);
        Assert.Equal(PluginApprovalState.NeedsReview, session.Receipts[1].State);
        Assert.All(session.Receipts, receipt => Assert.Equal(PluginApprovalState.NeedsReview, receipt.State));
        await Fixture.AssertFilesAsync(store, before);

        // The previous file is restored. The visible review may look like before, but the old decision belongs to the old revision.
        var restored = await f.DeliverTargetAsync("alpha", "b");
        Assert.Equal(rowB.NewExecutionRevision, restored.Candidate.Artifact!.ExecutionRevision);
        var afterB = await new PluginProfileStore(f.Root).ApplyBundledStartupAsync(f.Targets, f.TargetProofs, [], afterC.Revision);
        Assert.NotEqual(revisionB, afterB.Revision);
        BundledCandidateRepairPreview preview = await store.PreviewBundledRepairsAsync();
        BundledCandidateRepair rowAgain = Assert.Single(preview.Rows, row => row.PluginId == "alpha");
        Assert.Equal(rowB.NewExecutionRevision, rowAgain.NewExecutionRevision);
        Assert.Equal(afterB.Revision, preview.ProfileRevision);
        before = await Fixture.FilesAsync(store);
        await session.SubmitAsync([oldChoice], () => true); // Frozen at the revision before the file change.
        Assert.Equal(PluginApprovalState.NeedsReview, session.Receipts[^1].State);
        await Fixture.AssertFilesAsync(store, before);
        Assert.True(session.MayOffer("alpha"));

        // Only a decision made against the restored generation is accepted, and the plugin keeps its identity.
        Dialog reopened = Dialog.Open(rowAgain, preview.ProfileRevision, acknowledged: false);
        Assert.False(reopened.Consent.IsAcknowledged);
        reopened.Consent.SetAcknowledged(true);
        PluginApprovalChoice current = reopened.Save(authorizePermissions: true);
        await session.SubmitAsync([current], () => true);
        Assert.Equal(PluginApprovalState.Saved, session.Receipts[^1].State);
        PluginProfileSnapshot saved = await store.ReadAsync();
        Assert.Equal(f.TargetExecution("alpha"), f.Plugin(saved, "alpha").Pending!.ExecutionRevision);
        Assert.Null(f.Plugin(saved, "alpha").Active);
        Assert.Equal(f.PendingJson(f.InitialState, "beta"), f.PendingJson(saved, "beta")); // The decision belongs to alpha only.
        Assert.Equal(betaRow, Assert.Single((await store.PreviewBundledRepairsAsync()).Rows));

        for (int restart = 0; restart < 2; restart++)
        {
            saved = await new PluginProfileStore(f.Root).ApplyBundledStartupAsync(f.Targets, f.TargetProofs, [], saved.Revision);
            Assert.Equal(f.TargetExecution("alpha"), f.Plugin(saved, "alpha").Active!.ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(current.ChangeConfiguration!), SdkJson.Serialize(f.Plugin(saved, "alpha").Active!.Configuration));
            Assert.Null(f.Plugin(saved, "beta").Active);
        }
    }

    [Fact]
    public async Task ConsentGeneration_SameReviewPayloadStillRequiresTheCurrentExecutableRow()
    {
        using Fixture f = await Fixture.CreateAsync();
        BundledCandidateRepair previous = f.Row("alpha");
        PluginApprovalChoice stale = Dialog.Open(previous, f.Revision, acknowledged: true).Save(authorizePermissions: true);
        var replacement = await f.DeliverTargetAsync("alpha", "c");
        var targets = f.TargetsWith("alpha", replacement.Candidate);
        var proofs = f.ProofsWith("alpha", replacement.Proof);
        var state = await f.Store.ApplyBundledStartupAsync(targets, proofs, [], f.Revision);
        var preview = await f.Store.PreviewBundledRepairsAsync();
        BundledCandidateRepair current = Assert.Single(preview.Rows, row => row.PluginId == "alpha");
        Assert.Equal(previous.ChangeReviewJson, current.ChangeReviewJson);
        Assert.Equal(previous.ChangeFingerprint, current.ChangeFingerprint);
        Assert.NotEqual(previous.NewExecutionRevision, current.NewExecutionRevision);
        Assert.NotEqual(previous, current);

        PluginMultiApproval session = new(f.Store);
        var before = await Fixture.FilesAsync(f.Store);
        await session.SubmitAsync([stale with { Revision = state.Revision }], () => true);
        Assert.Equal(PluginApprovalState.NeedsReview, Assert.Single(session.Receipts).State);
        await Fixture.AssertFilesAsync(f.Store, before);
        Assert.Null(f.Plugin(await f.Store.ReadAsync(), "alpha").Active);

        Dialog reopened = Dialog.Open(current, preview.ProfileRevision, acknowledged: false);
        reopened.Consent.SetAcknowledged(true);
        PluginApprovalChoice decision = reopened.Save(authorizePermissions: true);
        await session.SubmitAsync([decision], () => true);
        Assert.Equal(PluginApprovalState.Saved, session.Receipts[1].State);
        state = await f.Store.ReadAsync();
        Assert.Null(f.Plugin(state, "alpha").Active);
        Assert.Equal(replacement.Candidate.Artifact!.ExecutionRevision, f.Plugin(state, "alpha").Pending!.ExecutionRevision);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await new PluginProfileStore(f.Root).ApplyBundledStartupAsync(targets, proofs, [], state.Revision);
            Assert.Equal(current.NewExecutionRevision, f.Plugin(state, "alpha").Active!.ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(decision.ChangeConfiguration!), SdkJson.Serialize(f.Plugin(state, "alpha").Active!.Configuration));
            Assert.False(f.Plugin(state, "alpha").Enabled);
            Assert.Null(f.Plugin(state, "alpha").Pending);
            Assert.Null(f.Plugin(state, "beta").Active);
        }
    }

    [Fact]
    public async Task ConsentGeneration_SavedDecisionStaysWithItsPluginAndUnchangedOtherReviewStillWorks()
    {
        using Fixture f = await Fixture.CreateAsync();
        BundledCandidateRepair alphaRow = f.Row("alpha");
        BundledCandidateRepair betaRow = f.Row("beta");
        Dialog alpha = Dialog.Open(alphaRow, f.Revision, acknowledged: true);
        Dialog beta = Dialog.Open(betaRow, f.Revision, acknowledged: true);
        PluginApprovalChoice alphaChoice = alpha.Save(authorizePermissions: true);
        PluginApprovalChoice betaChoice = beta.Save(authorizePermissions: true);
        PluginMultiApproval session = new(f.Store);
        await session.SubmitAsync([alphaChoice], () => true);
        Assert.Equal(PluginApprovalState.Saved, Assert.Single(session.Receipts).State);
        Assert.False(session.MayOffer("alpha"));
        Assert.True(session.MayOffer("beta"));

        PluginProfileSnapshot afterAlpha = await f.Store.ReadAsync();
        Assert.NotNull(f.Plugin(afterAlpha, "alpha").Pending);
        Assert.Equal(f.PendingJson(f.InitialState, "beta"), f.PendingJson(afterAlpha, "beta"));
        BundledCandidateRepairPreview preview = await f.Store.PreviewBundledRepairsAsync();
        Assert.Equal(betaRow, Assert.Single(preview.Rows)); // Alpha's consent neither consumed nor changed beta's review.

        // The frozen beta decision still carries the revision before alpha's write and is refused.
        Dictionary<string, byte[]> before = await Fixture.FilesAsync(f.Store);
        await session.SubmitAsync([betaChoice], () => true);
        Assert.Equal(PluginApprovalState.NeedsReview, session.Receipts[1].State);
        await Fixture.AssertFilesAsync(f.Store, before);
        // Alpha's consumed decision cannot be used to save again, not even rebased on the current revision.
        await session.SubmitAsync([alphaChoice with { Revision = preview.ProfileRevision }], () => true);
        Assert.Equal(PluginApprovalState.NotExecuted, session.Receipts[2].State);
        await Fixture.AssertFilesAsync(f.Store, before);

        // beta's unchanged review, acknowledged again against the current revision, is still accepted.
        Dialog refreshed = Dialog.Open(Assert.Single(preview.Rows), preview.ProfileRevision, acknowledged: false);
        refreshed.Consent.SetAcknowledged(true);
        await session.SubmitAsync([refreshed.Save(authorizePermissions: true)], () => true);
        Assert.Equal(PluginApprovalState.Saved, session.Receipts[3].State);
        PluginProfileSnapshot saved = await f.Store.ReadAsync();
        Assert.Equal(f.TargetExecution("alpha"), f.Plugin(saved, "alpha").Pending!.ExecutionRevision);
        Assert.Equal(f.TargetExecution("beta"), f.Plugin(saved, "beta").Pending!.ExecutionRevision);
        Assert.Empty((await f.Store.PreviewBundledRepairsAsync()).Rows);
    }

    // Mirrors the row-local state of AddBundledChangeReviewRow: real parser, draft and consent, and the same
    // "Save requires an acknowledged consent" guard before a choice is created.
    private sealed class Dialog
    {
        internal BundledPluginChangeDraft Draft { get; }
        internal BundledPluginChangeConsent Consent { get; }
        private readonly BundledCandidateRepair _row;
        private readonly long _revision;
        private Dialog(BundledCandidateRepair row, long revision)
        {
            _row = row; _revision = revision;
            Draft = new(Fixture.Review(row));
            Consent = new(Draft);
        }
        internal static Dialog Open(BundledCandidateRepair row, long revision, bool acknowledged)
        {
            Dialog dialog = new(row, revision);
            Assert.False(dialog.Consent.IsAcknowledged);
            if (acknowledged) dialog.Consent.SetAcknowledged(true);
            return dialog;
        }
        internal PluginApprovalChoice Save(bool authorizePermissions)
        {
            Assert.True(Consent.IsAcknowledged);
            PluginConfiguration configuration = Draft.Configuration(out var errors);
            Assert.Empty(errors);
            return new(PluginApprovalKind.ChangeReview, _revision, Repair: _row, ChangeConfiguration: configuration,
                AcknowledgeChanges: true, AuthorizePermissions: authorizePermissions);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] Ids = ["alpha", "beta"];
        private readonly Dictionary<string, (PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> _targets = new();
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "meowchan-consent-generation-" + Guid.NewGuid().ToString("N"));
        internal PluginProfileStore Store { get; }
        internal long Revision { get; private set; }
        internal PluginProfileSnapshot InitialState { get; private set; } = new(0, [], []);
        internal IReadOnlyList<PluginCandidateObservation> Targets => Ids.Select(id => _targets[id].Candidate).ToArray();
        internal IReadOnlyList<DeliveredCandidateProvenance> TargetProofs => Ids.Select(id => _targets[id].Proof).ToArray();
        private BundledCandidateRepairPreview _preview = new(0, []);
        private Fixture() => Store = new(Root);

        internal BundledCandidateRepair Row(string id) => Assert.Single(_preview.Rows, row => row.PluginId == id);
        internal PluginActivationSnapshot Plugin(PluginProfileSnapshot state, string id) => Assert.Single(state.Plugins, plugin => plugin.PluginId == id);
        internal string PendingJson(PluginProfileSnapshot state, string id) => Encoding.UTF8.GetString(SdkJson.Serialize(Plugin(state, id).Pending));
        internal string TargetExecution(string id) => _targets[id].Candidate.Artifact!.ExecutionRevision;
        internal IReadOnlyList<PluginCandidateObservation> TargetsWith(string id, PluginCandidateObservation candidate) =>
            Ids.Select(other => other == id ? candidate : _targets[other].Candidate).ToArray();
        internal IReadOnlyList<DeliveredCandidateProvenance> ProofsWith(string id, DeliveredCandidateProvenance proof) =>
            Ids.Select(other => other == id ? proof : _targets[other].Proof).ToArray();

        internal static BundledPluginChangeReview Review(BundledCandidateRepair row)
        {
            Assert.True(PluginsPage.TryReadBundledChangeReview(row, out BundledPluginChangeReview review),
                "The host preview must be a valid, fingerprint-consistent change review.");
            return review;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new();
            try
            {
                List<PluginCandidateObservation> old = [];
                List<DeliveredCandidateProvenance> oldProofs = [];
                foreach (string id in Ids)
                {
                    var previous = await f.DeliveredAsync(id, "old", "old", "Original " + id);
                    old.Add(previous.Candidate); oldProofs.Add(previous.Proof);
                    f._targets[id] = await f.DeliveredAsync(id, "target", "b", "Reviewed " + id);
                }
                var state = await f.Store.ApplyBundledStartupAsync(old, oldProofs, [], 0);
                foreach (string id in Ids)
                {
                    Assert.NotNull(f.Plugin(state, id).Active);
                    state = await f.Store.SetGrantAsync(id, "dimensions", GrantScope, FilterDataAccess.OriginalImage, state.Revision);
                    state = await f.Store.SetEnabledAsync(id, false, state.Revision);
                }
                state = await f.Store.ApplyBundledStartupAsync(f.Targets, f.TargetProofs, [], state.Revision);
                f._preview = await f.Store.PreviewBundledRepairsAsync();
                f.Revision = f._preview.ProfileRevision;
                f.InitialState = state;
                Assert.Equal(state.Revision, f.Revision);
                Assert.Equal(2, f._preview.Rows.Count);
                Assert.All(f._preview.Rows, row =>
                {
                    Assert.Equal("description-changed", row.BlockedReason);
                    Assert.True(row.CanReviewChanges);
                    Assert.False(row.CanConfirm);
                    Assert.False(row.CanReauthorize);
                });
                Assert.All(Ids, id => Assert.Null(f.Plugin(state, id).Active));
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        internal async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> DeliverTargetAsync(string id, string build)
        {
            var delivered = await DeliveredAsync(id, "target", build, "Reviewed " + id);
            if (build == "b") _targets[id] = delivered;
            return delivered;
        }

        // The same folder always names the same candidate path; a different build there is a new plugin generation.
        private async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> DeliveredAsync(
            string id, string folder, string build, string name)
        {
            string directory = Path.Combine(Root, folder, id); Directory.CreateDirectory(directory);
            byte[] bytes = Encoding.UTF8.GetBytes("inert synthetic assembly " + id + build);
            string sha = Convert.ToHexStringLower(SHA256.HashData(bytes)), sdk = new('b', 64), compiler = new('c', 64);
            string compilation = HashParts("prebuilt-v1", sha, sdk, compiler);
            string execution = HashParts("artifact-v1", compilation);
            string cache = Path.Combine(Root, "cache", execution); Directory.CreateDirectory(cache);
            await File.WriteAllBytesAsync(Path.Combine(directory, "fixture.dll"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(cache, "plugin.dll"), bytes);
            await File.WriteAllTextAsync(Path.Combine(directory, id + ".plugin.json"),
                JsonSerializer.Serialize(new PluginBundleManifest(1, "fixture.dll", sha, sdk, compiler, [])));
            var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(directory)).Candidates,
                candidate => candidate.Kind == PluginCandidateKind.PrebuiltManifest);
            PluginArtifact artifact = new(compilation, execution, Path.Combine(cache, "plugin.dll"), sha, sdk, compiler, [], cache);
            SettingsSchema empty = new("1", []);
            SettingsSchema settings = new("1", [new("label", SettingScope.Plugin, SettingKind.Text, "Label",
                Default: new("label", SettingKind.Text, Text: "start"))]);
            PluginDescription description = new(id, name, "1.0.0", SdkContract.Version, "Inert test filter", [],
                [new("dimensions", "Dimensions", "Filter", FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, empty, ["image/png"])],
                settings, []);
            Assert.Empty(SdkValidation.ValidateDescription(description));
            PluginCandidateObservation observation = new(installed.CandidateId, id, artifact, description, CandidatePath: installed.Path);
            return (observation, await PluginInstallationInventory.RecordDeliveredAsync(directory, installed, artifact, description));
        }

        // Every store-owned file including backups and hash sidecars; the lease file is not profile data.
        internal static async Task<Dictionary<string, byte[]>> FilesAsync(PluginProfileStore store)
        {
            Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(Path.GetDirectoryName(store.StorePath)!))
                if (Path.GetFileName(path) != "state.lock") files.Add(Path.GetFileName(path), await File.ReadAllBytesAsync(path));
            return files;
        }

        internal static async Task AssertFilesAsync(PluginProfileStore store, Dictionary<string, byte[]> before)
        {
            var after = await FilesAsync(store);
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal).ToArray(), after.Keys.Order(StringComparer.Ordinal).ToArray());
            foreach (var (name, bytes) in before) Assert.Equal(bytes, after[name]);
        }

        private static string HashParts(params string[] parts)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string part in parts)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(part), length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
