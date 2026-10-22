using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class WorkStatusPresentationTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RowUsesDurableMissingCountEvenIfLegacyActivitySaysFinished()
    {
        ThreadWorkSnapshot work = Work(WorkIntent.Completed, MediaWorkStatus.Expired);
        ThreadSnapshot snapshot = new(new ResourceId("fixture", "b", "1"), work.Url, "Fixture", "/fixture",
            MeowChan.Interfaces.ThreadActivity.Finished, MeowChan.Interfaces.ThreadSourceState.NotFound,
            null, 2, 2, new Dictionary<string, FailureInfo>(), null, new MeowChan.Downloader.DownloadCounts(0, 0)) { Work = work };
        using ThreadRow row = new(new DesktopThreadEntry(1, snapshot, "Fixture", "b"));
        Assert.Equal(50, row.Completion);
        Assert.Contains(Texts.T("work.saved", 1, 2), row.Progress);
        Assert.True(row.CanRetryMedia);
        Assert.Contains("404", row.StatusDetails);
    }

    [Fact]
    public void CompletedMissingMediaNeverAppearsFullySavedAndHasNoInventedDueDate()
    {
        ThreadWorkSnapshot work = Work(WorkIntent.Completed, MediaWorkStatus.Expired);
        WorkStatusPresentation result = WorkStatusFormatter.Format(work);
        Assert.Equal(50, result.Completion);
        Assert.Equal(Texts.T("work.completed"), result.Status);
        Assert.Contains(Texts.T("work.missingCount", 1, "404"), result.Progress);
        Assert.DoesNotContain(Texts.T("work.nextDue", ""), result.Details);
        Assert.True(result.CanRetry);
    }

    [Fact]
    public void WatchingAtFullMediaCompletionStillWatchesAndPauseDoesNotChangeDeadline()
    {
        ThreadWorkSnapshot work = Work(WorkIntent.Watching, MediaWorkStatus.Succeeded) with { Source = ThreadSourceStateValue.Active, EndObservedAt = null };
        Assert.Equal(Texts.T("work.watching"), WorkStatusFormatter.Format(work).Status);
        Assert.Equal(100, WorkStatusFormatter.Format(work).Completion);
        Assert.False(WorkStatusFormatter.Format(work).CanRetry);
        Assert.Contains(Texts.T("work.paused"), WorkStatusFormatter.Format(work, true).Status);
    }

    [Fact]
    public void RunoutInterventionManualStopAndRepairRemainDistinctAndManualJobsCoalesce()
    {
        ThreadWorkSnapshot work = Work(WorkIntent.Watching, MediaWorkStatus.Waiting);
        WorkStatusPresentation runout = WorkStatusFormatter.Format(work);
        Assert.Equal(Texts.T("work.runout"), runout.Status);
        Assert.Contains(Texts.T("work.nextDue", End.AddMinutes(15).ToLocalTime().ToString("g", Texts.Culture)), runout.Details);
        Assert.Contains(Texts.T("work.deadline", End.AddHours(12).ToLocalTime().ToString("g", Texts.Culture)), runout.Details);
        Assert.Equal(Texts.T("work.intervention"), WorkStatusFormatter.Format(work with { CapturePending = true }).Status);
        Assert.Equal(Texts.T("work.stopped"), WorkStatusFormatter.Format(work with { Intent = WorkIntent.Suppressed }).Status);
        Assert.False(WorkStatusFormatter.Format(work with { Intent = WorkIntent.Suppressed }).CanRetry);
        Assert.Equal(Texts.T("work.repairing"), WorkStatusFormatter.Format(work with { Intent = WorkIntent.Repairing }).Status);
        Assert.False(WorkStatusFormatter.Format(work with { Media = work.Media.Select(item => item with { ManualAttempt = true }).ToArray() }).CanRetry);
    }

    [Fact]
    public void WaitingStaysWaitingBeforeDuringAndAfterGlobalPause()
    {
        ThreadWorkSnapshot work = Work(WorkIntent.Watching, MediaWorkStatus.Waiting);
        WorkStatusPresentation before = WorkStatusFormatter.Format(work);
        WorkStatusPresentation paused = WorkStatusFormatter.Format(work, true);
        WorkStatusPresentation resumed = WorkStatusFormatter.Format(work);
        Assert.Contains(Texts.T("work.waitingCount", 1, "404"), before.ExceptionHint);
        Assert.Equal(before.ExceptionHint, paused.ExceptionHint);
        Assert.Equal(before, resumed);
        Assert.Equal(before.Details, paused.Details);
        Assert.Contains(Texts.T("work.paused"), paused.Status);
        Assert.DoesNotContain(Texts.T("work.paused"), resumed.Status);
    }

    [Fact]
    public void OptionalColumnsUseTypedSnapshotsAndSuccessHasNoExceptionHint()
    {
        ThreadWorkSnapshot work = Work(WorkIntent.Watching, MediaWorkStatus.Waiting);
        ThreadSnapshot snapshot = new(new ResourceId("fixture", "b", "1"), work.Url, "Fixture", "/never/read",
            MeowChan.Interfaces.ThreadActivity.Finished, MeowChan.Interfaces.ThreadSourceState.Active,
            null, 10, 10, new Dictionary<string, FailureInfo>(), null, new MeowChan.Downloader.DownloadCounts(0, 0))
            { Work = work, Content = new MeowChan.Archive.ThreadContentStatus(true, null, null, null, 8) };
        using ThreadRow row = new(new DesktopThreadEntry(1, snapshot, "Fixture", "b"));
        Assert.Equal(2, row.SelectedMedia);
        Assert.Equal(1, row.CompletedMedia);
        Assert.Equal(1, row.MissingMedia);
        Assert.Equal(0, row.MediaErrorCount);
        Assert.Equal(8, row.ExcludedMedia);
        Assert.Equal(End.AddMinutes(15), row.EarliestRetry);
        Assert.True(row.HasExceptionHint);
        row.UpdateSnapshot(new DesktopThreadEntry(1, snapshot with { Work = Work(WorkIntent.Watching, MediaWorkStatus.Succeeded) }, "Fixture", "b"));
        Assert.False(row.HasExceptionHint);
        Assert.Equal(0, row.MissingMedia);
        Assert.Null(row.EarliestRetry);
        Assert.Equal("—", row.EarliestRetryText);
        Assert.Equal(100, row.Completion);
        Assert.NotEqual(Texts.T("work.completed"), row.Status);
    }

    private static ThreadWorkSnapshot Work(WorkIntent intent, MediaWorkStatus missingStatus) => new(
        "https://fixture.test/b/thread/1", intent, ThreadSourceStateValue.NotFound, End, false,
        new[] { Media("saved", MediaWorkStatus.Succeeded), Media("missing", missingStatus) });

    private static MediaWork Media(string id, MediaWorkStatus status) => new()
    {
        ResourceUrl = "https://fixture.test/b/thread/1", Generation = 1, MediaIdentifier = id,
        DestinationPath = "/fixture/" + id, DownloadUrl = "https://fixture.test/" + id,
        OriginalFileName = id + ".jpg", Status = status, HttpStatus = 404, Attempts = 2,
        DueAt = status == MediaWorkStatus.Waiting ? End.AddMinutes(15) : null
    };
}
