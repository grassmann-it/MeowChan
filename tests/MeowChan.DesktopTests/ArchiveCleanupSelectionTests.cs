using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ArchiveCleanupSelectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void VerifiedCandidatesStartUnselectedAndRequireAnExplicitChoice(int count)
    {
        var row = Row("eligible", count);
        ArchiveCleanupSelection selection = new(); selection.Load(Preview(row));
        Assert.Empty(selection.SelectedRows);
        Assert.Throws<InvalidOperationException>(() => selection.FreezeSelected());
        Assert.True(selection.SetSelected(row.Id, true));
        Assert.Equal(new[] { row.Id }, selection.FreezeSelected());
        Assert.Equal(count, Assert.Single(selection.SelectedRows).Media.SavedAssetCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(3)]
    public void UnknownOrOutOfRangeCountsCannotBeSelectedEvenWithACandidateLabel(int? count)
    {
        var row = Row("invalid", count);
        ArchiveCleanupSelection selection = new(); selection.Load(Preview(row));
        Assert.False(selection.SetSelected(row.Id, true));
        Assert.Empty(selection.SelectedRows);
    }

    [Fact]
    public void ProtectionActivityAndUncertaintyAlwaysWinOverASmallCount()
    {
        foreach (ArchiveCleanupEligibility state in new[] { ArchiveCleanupEligibility.Protected,
            ArchiveCleanupEligibility.Active, ArchiveCleanupEligibility.Kept, ArchiveCleanupEligibility.Unknown })
        {
            var row = Row("protected", 0) with { Eligibility = state };
            ArchiveCleanupSelection selection = new(); selection.Load(Preview(row));
            Assert.False(selection.SetSelected(row.Id, true));
        }
        foreach (ArchiveCleanupReason reason in Enum.GetValues<ArchiveCleanupReason>())
        {
            var row = Row("reason", 1) with { Reasons = new[] { reason } };
            ArchiveCleanupSelection selection = new(); selection.Load(Preview(row));
            Assert.False(selection.SetSelected(row.Id, true));
        }
        var error = Row("io", 0);
        error = error with { Media = error.Media with { Error = "Unconfirmed storage" } };
        ArchiveCleanupSelection blocked = new(); blocked.Load(Preview(error));
        Assert.False(blocked.SetSelected(error.Id, true));
    }

    [Fact]
    public void PartialStaleFuturePolicyAndDuplicateIdentityPreviewsAreLocked()
    {
        var row = Row("same", 0); var ready = Preview(row);
        foreach (var preview in new[] { ready with { Complete = false }, ready with { IsStale = true },
            ready with { PolicyVersion = 2 }, ready with { Rows = new[] { row, row } } })
        {
            ArchiveCleanupSelection selection = new(); selection.Load(preview);
            Assert.False(selection.CanChoose);
            Assert.False(selection.SetSelected(row.Id, true));
            Assert.Throws<InvalidOperationException>(() => selection.FreezeSelected());
        }
    }

    [Fact]
    public void InvalidationAndEveryRefreshDiscardOldChoicesEvenWhenIdsRemainTheSame()
    {
        var row = Row("unchanged-id", 2); var preview = Preview(row);
        ArchiveCleanupSelection selection = new(); selection.Load(preview); selection.SetSelected(row.Id, true);
        selection.Invalidate();
        Assert.Empty(selection.SelectedRows); Assert.False(selection.CanChoose);
        Assert.Throws<InvalidOperationException>(() => selection.FreezeSelected());
        selection.Load(preview with { ProfileBinding = "another-profile", EvidenceToken = "another-generation" });
        Assert.True(selection.CanChoose); Assert.Empty(selection.SelectedRows);
        Assert.False(selection.SetSelected(new("unseen", "board", "42"), true));
        selection.SetSelected(row.Id, true);
        selection.Load(preview);
        Assert.Empty(selection.SelectedRows);
    }

    [Fact]
    public void FrozenChoicesAreExactCopiedIdsInPreviewOrderAndPreserveVersionLoss()
    {
        var first = Row("one", 1); var second = Row("two", 2);
        ArchiveCleanupSelection selection = new(); selection.Load(Preview(first, second));
        selection.SetSelected(second.Id, true); selection.SetSelected(first.Id, true);
        ResourceId[] frozen = selection.FreezeSelected();
        Assert.Equal(new[] { first.Id, second.Id }, frozen);
        Assert.Equal(3, selection.SelectedRows.Sum(row => row.Media.SavedAssetCount));
        Assert.Equal(30, selection.SelectedRows.Sum(row => row.Media.VerifiedBytes));
        frozen[0] = new("foreign", "board", "thread");
        Assert.Equal(first.Id, selection.FreezeSelected()[0]);
        selection.SetSelected(first.Id, false);
        Assert.Equal(new[] { second.Id }, selection.FreezeSelected());
        selection.Clear(); Assert.Empty(selection.SelectedRows);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void LabelsExplainStorageUncertaintyAndNeverPresentUnknownAsZero(string language)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            var unknown = Row("unknown", null) with { Eligibility = ArchiveCleanupEligibility.Unknown };
            Assert.Contains(language == "de" ? "unbekannt" : "unknown", ArchiveCleanupText.Media(unknown));
            Assert.Contains(language == "de" ? "Speicherort" : "storage location", ArchiveCleanupText.Reason(ArchiveCleanupReason.MediaUnknown));
            foreach (var reason in Enum.GetValues<ArchiveCleanupReason>()) Assert.False(string.IsNullOrWhiteSpace(ArchiveCleanupText.Reason(reason)));
            Assert.Equal(language == "de" ? "Archiv aufräumen…" : "Clean up archive…", CommandCatalog.Label("archive.cleanup"));
            Assert.Null(CommandCatalog.Get("archive.cleanup").Shortcut);
        }
        finally { Texts.SetLanguage(previous); }
    }

    private static ArchiveCleanupPreviewRow Row(string thread, int? count)
    {
        ResourceId id = new("source", "board", thread);
        var summary = new ArchiveThreadSummary(id, "https://source.test/board/" + thread, thread,
            "/not-opened-by-this-test/" + thread, ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, null, 4, 1);
        return new(summary, new(count, (count ?? 0) * 10, Array.Empty<ArchiveCleanupDirectoryEvidence>(), "media", null),
            ArchiveCleanupEligibility.Candidate, Array.Empty<ArchiveCleanupReason>(), "row");
    }
    private static ArchiveCleanupPreview Preview(params ArchiveCleanupPreviewRow[] rows) => new("profile", 1, rows,
        Array.Empty<ResourceId>(), "evidence", "protection", "work", true, false, Array.Empty<string>());
}
