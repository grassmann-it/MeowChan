using System.Text;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task BundledChangeReview_ExactSettingsConsentSurvivesTwoRestarts(bool samePath, bool changedSchema)
    {
        var old = await SdkDelivery("change-old", "A", "1.1.0");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 73) }, state.Revision);
        state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        var originalConfiguration = Active(state).Configuration;
        var originalOwners = SdkJson.Serialize(state.SelectedSourceOwners);
        // The rename comes with a widening (one more file type for the filter): since #130 a display-only
        // change in place is accepted silently, so the review flow needs a change that adds something.
        var target = await SdkDelivery(samePath ? "change-old" : "change-new", "B", "1.1.0", d => d with
        {
            Name = "Reviewed plugin name",
            Filters = [d.Filters[0] with { SupportedMimeTypes = [.. d.Filters[0].SupportedMimeTypes, "image/jpeg"] }],
            Settings = changedSchema ? new("2", [new("flag", SettingScope.Plugin, SettingKind.Boolean,
                "New flag", IsRequired: true)]) : d.Settings
        });
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        var preview = await store.PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        Assert.Equal("description-changed", row.BlockedReason);
        Assert.True(row.CanReviewChanges);
        Assert.False(row.CanConfirm);
        Assert.False(row.CanReauthorize);
        var review = SdkJson.Deserialize<BundledPluginChangeReview>(Encoding.UTF8.GetBytes(row.ChangeReviewJson!));
        Assert.Equal(SdkJson.Serialize(originalConfiguration), SdkJson.Serialize(review.PreviousConfiguration));
        Assert.Equal("Reviewed plugin name", review.NewDescription.Name);
        var corrected = changedSchema ? originalConfiguration with { PluginSettings = new("2", [new("flag", SettingKind.Boolean, Boolean: false)]) }
            : originalConfiguration;
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledChangeAsync(row, corrected, false, false, state.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row, state.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SelectCandidateAsync("test.plugin", target.Candidate.CandidateId, state.Revision));
        if (changedSchema)
            await Assert.ThrowsAsync<PluginConfigurationValidationException>(() => store.ConfirmBundledChangeAsync(row, originalConfiguration, true, false, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        state = await store.ConfirmBundledChangeAsync(row, corrected, true, false, state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.False(state.Plugins[0].Enabled);
        Assert.Equal(old.Proof.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        Assert.Equal(SdkJson.Serialize(corrected), SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.PreReselectionBackupPath));
        Assert.Empty((await store.PreviewBundledRepairsAsync()).Rows);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
            Assert.Equal(target.Proof.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(corrected), SdkJson.Serialize(Active(state).Configuration));
            Assert.Equal(SdkJson.Serialize(originalConfiguration), SdkJson.Serialize(state.Plugins[0].LastGood!.Configuration));
            Assert.Equal(originalOwners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.False(state.Plugins[0].Enabled);
            Assert.Empty(state.Grants);
            Assert.Null(state.Plugins[0].Pending);
            Assert.Null(state.Plugins[0].Problem);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundledChangeReview_ChangedDescriptionWithSameExecutableCannotReuseConsent(bool changedProof)
    {
        var old = await SdkDelivery("change-metadata", "A", "1.1.0");
        var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        // Widened (one more filter file type) so this in-place change still needs review since #130.
        static PluginDescription Widened(PluginDescription d, string name) => d with { Name = name,
            Filters = [d.Filters[0] with { SupportedMimeTypes = [.. d.Filters[0].SupportedMimeTypes, "image/jpeg"] }] };
        var target = await SdkDelivery("change-metadata", "B", "1.1.0", d => Widened(d, "Reviewed name"));
        state = await store.ApplyBundledStartupAsync([target.Candidate], [target.Proof], [], state.Revision);
        var row = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        state = await store.ConfirmBundledChangeAsync(row, state.Plugins[0].LastGood!.Configuration, true, false, state.Revision);
        var changed = await SdkDelivery("change-metadata", "B", "1.1.0", d => Widened(d, "UNREVIEWED metadata"));
        Assert.Equal(target.Proof.ExecutionRevision, changed.Proof.ExecutionRevision);
        if (changedProof)
        {
            // A conflicting proof for a retained execution is rejected even earlier than binding admission.
            byte[] saved = await File.ReadAllBytesAsync(store.StorePath);
            await Assert.ThrowsAsync<InvalidDataException>(() => Store().ApplyBundledStartupAsync(
                [changed.Candidate], [changed.Proof], [], state.Revision));
            Assert.Equal(saved, await File.ReadAllBytesAsync(store.StorePath));
        }
        else
        {
            state = await Store().ApplyStartupAsync([changed.Candidate], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Contains("changed", state.Plugins[0].Problem);
        }
    }
}
