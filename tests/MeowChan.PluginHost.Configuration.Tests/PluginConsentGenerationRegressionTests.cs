using System.Text;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

// Consent-generation regressions for the bundled change review at the real store boundary. These extend the
// existing ChangeReviewBoundaries tests (which cover forged rows, stale revisions, queued permission changes and
// replacement by a third build) with the remaining generation combinations: rights changed and restored, a
// confirmed decision that is replayed or invalidated afterwards, plugin identity of the decision, and plugin files
// that are tampered with and then restored. It is a partial of PluginProfileStoreTests only to reuse its synthetic
// delivered-plugin fixtures; every file lives in this test's private temporary profile.
public sealed partial class PluginProfileStoreTests
{
    private static BundledPluginChangeReview ConsentGenerationReview(BundledCandidateRepair row) =>
        SdkJson.Deserialize<BundledPluginChangeReview>(Encoding.UTF8.GetBytes(row.ChangeReviewJson!));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsentGeneration_RightsRestoredToEarlierValueDoNotReviveTheOldRowDecision(bool samePath)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        var grant = Assert.Single(f.State.Grants);
        byte[] original = await File.ReadAllBytesAsync(f.Store.StorePath);

        // A review is open; the filter right is withdrawn and later granted again with the same access value.
        var withdrawn = await Store().SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, FilterDataAccess.None, f.State.Revision);
        var midRow = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
        Assert.Empty(ConsentGenerationReview(midRow).Permissions.FilterGrants);
        Assert.Equal(new[] { "shared" }, ConsentGenerationReview(midRow).Permissions.SourceIds.ToArray());
        var restored = await Store().SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, grant.Access, withdrawn.Revision);
        var preview = await Store().PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        Assert.Equal(restored.Revision, preview.ProfileRevision);
        var reviewedGrant = Assert.Single(ConsentGenerationReview(row).Permissions.FilterGrants);
        Assert.Equal(grant.Access, reviewedGrant.Access);
        Assert.Equal(grant.Revision + 2, reviewedGrant.Revision); // Same visible value, new permission generation.
        Assert.NotEqual(f.Row.ChangeFingerprint, row.ChangeFingerprint);

        var before = await ChangeBoundaryFilesAsync(f.Store);
        // The old decision fails whether it is presented with its own old revision or re-based on the latest one.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, f.Configuration, true, true, restored.Revision));
        await Assert.ThrowsAsync<PluginProfileConflictException>(() => Store().ConfirmBundledChangeAsync(
            row, f.Configuration, true, true, f.State.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            row, f.Configuration, true, false, restored.Revision)); // Change consent never implies rights consent.
        await AssertChangeBoundaryFilesAsync(f.Store, before);

        var state = await Store().ConfirmBundledChangeAsync(row, f.Configuration, true, true, preview.ProfileRevision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal(SdkJson.Serialize(f.Configuration), SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.PreReselectionBackupPath));
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([f.Target], [f.Proof], ["shared"], state.Revision);
            Assert.Equal(f.Target.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(f.Configuration), SdkJson.Serialize(Active(state).Configuration));
            Assert.Equal(SdkJson.Serialize(restored.Grants), SdkJson.Serialize(state.Grants));
            Assert.False(state.Plugins[0].Enabled);
            Assert.Null(state.Plugins[0].Pending);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsentGeneration_ConfirmedDecisionCannotBeReplayedWithAnotherConfigurationBeforeOrAfterRestart(bool samePath)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        PluginConfiguration other = f.Configuration with { PluginSettings = Values("1", 91) };
        Assert.Empty(SdkValidation.ValidateConfiguration(f.Target.Description!, other)); // Valid, so only consent can refuse it.
        var state = await Store().ConfirmBundledChangeAsync(f.Row, f.Configuration, true, true, f.State.Revision);
        byte[] confirmed = SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows); // The consumed review is no longer offered.

        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, other, true, true, state.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([f.Target], [f.Proof], ["shared"], state.Revision);
            Assert.Equal(f.Target.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(confirmed, SdkJson.Serialize(Active(state).Configuration));
            before = await ChangeBoundaryFilesAsync(f.Store);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
                f.Row, other, true, true, state.Revision));
            await AssertChangeBoundaryFilesAsync(f.Store, before);
            Assert.Equal(confirmed, SdkJson.Serialize((await Store().ReadAsync()).Plugins[0].Active!.Configuration));
        }
    }

    [Theory]
    [InlineData(false, "plugin")]
    [InlineData(true, "plugin")]
    [InlineData(false, "old-candidate")]
    [InlineData(true, "old-candidate")]
    public async Task ConsentGeneration_DecisionBelongsToItsPluginAndOldCandidateIdentity(bool samePath, string forged)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        var row = forged == "plugin"
            ? f.Row with { PluginId = "other.plugin" }
            : f.Row with { OldCandidateId = f.Row.OldCandidateId + "-other" };
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            row, f.Configuration, true, true, f.State.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);

        // The refused foreign attempt neither consumed nor altered the genuine review; it still works unchanged.
        Assert.Equal(f.Row, Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows));
        var state = await Store().ConfirmBundledChangeAsync(f.Row, f.Configuration, true, true, f.State.Revision);
        Assert.Equal(SdkJson.Serialize(f.Configuration), SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
        Assert.Null(state.Plugins[0].Active);
    }

    [Theory]
    [InlineData(false, "manifest")]
    [InlineData(true, "manifest")]
    [InlineData(false, "delivered-assembly")]
    [InlineData(true, "delivered-assembly")]
    [InlineData(false, "cache")]
    [InlineData(true, "cache")]
    public async Task ConsentGeneration_PluginFileTamperedThenRestoredKeepsTheUnchangedReviewUsable(bool samePath, string file)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        string path = file switch
        {
            "manifest" => f.Target.CandidatePath!,
            "delivered-assembly" => Path.Combine(Path.GetDirectoryName(f.Target.CandidatePath!)!, "fixture.dll"),
            _ => f.Target.Artifact!.AssemblyPath
        };
        byte[] bytes = await File.ReadAllBytesAsync(path);
        await File.AppendAllTextAsync(path, " ");
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, f.Configuration, true, true, f.State.Revision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);

        // The previous bytes are back: the reviewed generation is exactly the one the user saw.
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal(f.Row, Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows));
        var state = await Store().ConfirmBundledChangeAsync(f.Row, f.Configuration, true, true, f.State.Revision);
        Assert.Null(state.Plugins[0].Active);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([f.Target], [f.Proof], ["shared"], state.Revision);
            Assert.Equal(f.Target.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(f.Configuration), SdkJson.Serialize(Active(state).Configuration));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsentGeneration_RightsChangedAndRestoredAfterConfirmationBlockRestartUntilFreshReview(bool samePath)
    {
        var f = await ChangeBoundaryFixtureAsync(samePath);
        var grant = Assert.Single(f.State.Grants);
        byte[] original = await File.ReadAllBytesAsync(f.Store.StorePath);
        var state = await Store().ConfirmBundledChangeAsync(f.Row, f.Configuration, true, true, f.State.Revision);
        Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows); // Fully consumed while the permissions are unchanged.

        // After the consent, the rights are withdrawn and granted again with the same visible access value.
        state = await Store().SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, FilterDataAccess.None, state.Revision);
        state = await Store().SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, grant.Access, state.Revision);
        Assert.Equal(grant.Revision + 2, Assert.Single(state.Grants).Revision);
        state = await Store().ApplyBundledStartupAsync([f.Target], [f.Proof], ["shared"], state.Revision);
        Assert.Null(state.Plugins[0].Active); // The earlier consent does not authorize the new permission generation.
        Assert.Contains("grants", state.Plugins[0].Problem);

        var preview = await Store().PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        Assert.True(row.CanReviewChanges);
        Assert.NotEqual(f.Row.ChangeFingerprint, row.ChangeFingerprint);
        Assert.Equal(grant.Revision + 2, Assert.Single(ConsentGenerationReview(row).Permissions.FilterGrants).Revision);
        var before = await ChangeBoundaryFilesAsync(f.Store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            f.Row, f.Configuration, true, true, preview.ProfileRevision)); // Old decision, latest revision.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().ConfirmBundledChangeAsync(
            row, f.Configuration, true, false, preview.ProfileRevision));
        await AssertChangeBoundaryFilesAsync(f.Store, before);

        state = await Store().ConfirmBundledChangeAsync(row, f.Configuration, true, true, preview.ProfileRevision);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.PreReselectionBackupPath));
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([f.Target], [f.Proof], ["shared"], state.Revision);
            Assert.Equal(f.Target.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(f.Configuration), SdkJson.Serialize(Active(state).Configuration));
            Assert.Equal(grant.Revision + 2, Assert.Single(state.Grants).Revision);
            Assert.False(state.Plugins[0].Enabled);
            Assert.Null(state.Plugins[0].Problem);
        }
    }
}
