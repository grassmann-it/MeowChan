using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// B8-R15: with unwritable storage every write action explains itself through the store's state, not with an empty tooltip.
[Collection("Localization")]
public sealed class SavedSearchUnavailableReasonTests
{
    private static SavedArchiveSearchSnapshot Snapshot(SavedSearchStoreState state, string? error = null) => new(3, [], state, error);

    [Theory]
    [InlineData("save-current")]
    [InlineData("replace")]
    [InlineData("rename")]
    [InlineData("delete")]
    public void UnwritableStorageGivesTheStoreReasonForEveryWriteAction(string suffix)
    {
        foreach (SavedSearchStoreState state in new[] { SavedSearchStoreState.Failed, SavedSearchStoreState.Unsupported })
        {
            string? reason = SavedArchiveSearchesDialog.UnavailableReason(Snapshot(state, "file is damaged"), suffix, hasSelection: false, canSaveCurrent: false, hasAppliedSearch: false);
            Assert.Equal(Texts.T("savedSearch.readonly", "file is damaged"), reason);
        }
    }

    [Fact]
    public void PendingRecoveryAlsoPointsAtTheBackup()
    {
        string? reason = SavedArchiveSearchesDialog.UnavailableReason(Snapshot(SavedSearchStoreState.RecoveryAvailable, "damaged"), "delete", true, true, true);
        Assert.Equal(Texts.T("savedSearch.readonly", "damaged") + " " + Texts.T("savedSearch.prompt.Recover"), reason);
    }

    [Fact]
    public void ApplyNeedsNoWriteAccessAndStillAsksForASelection()
    {
        SavedArchiveSearchSnapshot broken = Snapshot(SavedSearchStoreState.Failed, "x");
        Assert.Equal(Texts.T("savedSearch.choose"), SavedArchiveSearchesDialog.UnavailableReason(broken, "apply", false, false, false));
        Assert.Null(SavedArchiveSearchesDialog.UnavailableReason(broken, "apply", true, false, false));
    }

    [Fact]
    public void WritableStorageKeepsTheSelectionAndCaptureReasons()
    {
        SavedArchiveSearchSnapshot ready = Snapshot(SavedSearchStoreState.Ready);
        Assert.Equal(Texts.T("savedSearch.choose"), SavedArchiveSearchesDialog.UnavailableReason(ready, "rename", false, true, true));
        Assert.Equal(Texts.T("savedSearch.noApplied"), SavedArchiveSearchesDialog.UnavailableReason(ready, "save-current", false, false, false));
        Assert.Equal(Texts.T("savedSearch.captureBlocked"), SavedArchiveSearchesDialog.UnavailableReason(ready, "replace", true, false, true));
        Assert.Null(SavedArchiveSearchesDialog.UnavailableReason(ready, "delete", true, false, false));
        Assert.Null(SavedArchiveSearchesDialog.UnavailableReason(null, "delete", false, false, false));
    }

    [Fact]
    public void MissingErrorTextFallsBackToTheStateName()
    {
        string? reason = SavedArchiveSearchesDialog.UnavailableReason(Snapshot(SavedSearchStoreState.Failed), "delete", true, true, true);
        Assert.Equal(Texts.T("savedSearch.readonly", "Failed"), reason);
    }
}
