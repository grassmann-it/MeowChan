using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ArchiveUrlTextExportLifecycleTests
{
    private static ArchiveUrlTextExportPlan Plan()
    {
        ResourceId id = new("4chan", "b", "123");
        return ArchiveUrlTextExportPlan.TryCreate(id, new ArchiveThreadSummary(id,
            "https://boards.4chan.org/b/thread/123", "PRIVATE", "/private/media",
            ThreadSourceState.NotFound, false, DateTimeOffset.UnixEpoch, null, 0, 1), out _)!;
    }
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<UrlTextExportFileWriteResult> Saved(ArchiveUrlTextExportPlan p, string path, CancellationToken token)
        => Task.FromResult(new UrlTextExportFileWriteResult(UrlTextExportFileWriteStatus.Saved, UrlTextExportFileWriteError.None));

    [Fact]
    public async Task NoConfirmationOrTerminationMeansNoPickerAndNoWrite()
    {
        int picks = 0, writes = 0;
        using var operation = new UrlTextExportOperation(Plan(), () => { picks++; return Task.FromResult<string?>("target"); },
            (p, path, token) => { writes++; return Saved(p, path, token); }, default);
        Assert.Equal(0, picks);
        await operation.TerminateAsync();
        await operation.ConfirmAsync();
        Assert.Equal(0, picks); Assert.Equal(0, writes);
    }

    [Fact]
    public async Task CapturedPlanIsUsedOnceDespiteRepeatedConfirmation()
    {
        var plan = Plan(); var picker = Gate<string?>(); int writes = 0, picks = 0;
        using var operation = new UrlTextExportOperation(plan, () => { picks++; return picker.Task; },
            (p, path, token) => { Assert.Same(plan, p); Assert.Equal("chosen", path); writes++; return Saved(p, path, token); }, default);
        Task first = operation.ConfirmAsync(); Task second = operation.ConfirmAsync();
        Assert.Same(first, second); Assert.Equal(1, picks); Assert.Equal(0, writes);
        picker.SetResult("chosen"); await first;
        await operation.ConfirmAsync();
        Assert.Equal(1, writes); Assert.Equal("urltext.export.saved", operation.StatusKey);
    }

    [Fact]
    public async Task LatePickerAfterTerminationCannotWrite()
    {
        var picker = Gate<string?>(); int writes = 0;
        using var operation = new UrlTextExportOperation(Plan(), () => picker.Task,
            (p, path, token) => { writes++; return Saved(p, path, token); }, default);
        Task run = operation.ConfirmAsync(); Task drain = operation.TerminateAsync();
        Assert.False(drain.IsCompleted);
        picker.SetResult("late-target"); await drain; await run;
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task ParentCancellationRejectsUncooperativePicker()
    {
        using var lifetime = new CancellationTokenSource(); var picker = Gate<string?>(); int writes = 0;
        using var operation = new UrlTextExportOperation(Plan(), () => picker.Task,
            (p, path, token) => { writes++; return Saved(p, path, token); }, lifetime.Token);
        Task run = operation.ConfirmAsync(); lifetime.Cancel(); picker.SetResult("late"); await run;
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task StartedWriteIsDrainedAndItsPartialResultRetainedWithoutStaleNotification()
    {
        var writer = Gate<UrlTextExportFileWriteResult>(); CancellationToken observed = default; int notifications = 0;
        using var operation = new UrlTextExportOperation(Plan(), () => Task.FromResult<string?>("target"),
            (p, path, token) => { observed = token; return writer.Task; }, default);
        operation.Changed += () => notifications++;
        Task run = operation.ConfirmAsync(); Task drain = operation.TerminateAsync(); int before = notifications;
        Assert.True(observed.IsCancellationRequested); Assert.False(drain.IsCompleted);
        var partial = new UrlTextExportFileWriteResult(UrlTextExportFileWriteStatus.PartialRetained, UrlTextExportFileWriteError.Canceled);
        writer.SetResult(partial); await drain; await run;
        Assert.Same(partial, operation.Result); Assert.Equal(before, notifications);
    }

    [Fact]
    public async Task PickerCancelDoesNotWriteOrAutomaticallyRetry()
    {
        int writes = 0;
        using var operation = new UrlTextExportOperation(Plan(), () => Task.FromResult<string?>(null),
            (p, path, token) => { writes++; return Saved(p, path, token); }, default);
        await operation.ConfirmAsync(); await operation.ConfirmAsync();
        Assert.Equal(0, writes); Assert.Equal("urltext.export.canceled", operation.StatusKey);
    }

    [Theory]
    [InlineData((int)UrlTextExportFileWriteStatus.PartialRetained, (int)UrlTextExportFileWriteError.CreationUncertain, "creationUncertain")]
    [InlineData((int)UrlTextExportFileWriteStatus.PartialRetained, (int)UrlTextExportFileWriteError.IoFailure, "partialRetained")]
    [InlineData((int)UrlTextExportFileWriteStatus.Failed, (int)UrlTextExportFileWriteError.DestinationExists, "error.exists")]
    public async Task WriterResultsAreNotReportedAsSaved(int status, int error, string key)
    {
        using var operation = new UrlTextExportOperation(Plan(), () => Task.FromResult<string?>("target"),
            (p, path, token) => Task.FromResult(new UrlTextExportFileWriteResult((UrlTextExportFileWriteStatus)status, (UrlTextExportFileWriteError)error)), default);
        await operation.ConfirmAsync(); Assert.Equal("urltext.export." + key, operation.StatusKey);
    }

    [Fact]
    public async Task PickerExceptionHasFixedDiagnosticOnly()
    {
        using var operation = new UrlTextExportOperation(Plan(), () => throw new InvalidOperationException("PRIVATE"), Saved, default);
        await operation.ConfirmAsync(); Assert.Equal("urltext.export.error.target", operation.StatusKey);
    }
    [Fact]
    public async Task CancellationAtWritingTransitionPreventsWriterEntry()
    {
        using var lifetime = new CancellationTokenSource(); int writes = 0;
        using var operation = new UrlTextExportOperation(Plan(), () => Task.FromResult<string?>("target"),
            (p, path, token) => { writes++; return Saved(p, path, token); }, lifetime.Token);
        operation.Changed += () => { if (operation.StatusKey == "urltext.export.writing") lifetime.Cancel(); };
        await operation.ConfirmAsync(); Assert.Equal(0, writes);
    }

    [Fact]
    public async Task UnexpectedWriterFaultIsObservedConservatively()
    {
        using var operation = new UrlTextExportOperation(Plan(), () => Task.FromResult<string?>("target"),
            (p, path, token) => throw new InvalidOperationException("PRIVATE"), default);
        await operation.ConfirmAsync();
        Assert.Equal(UrlTextExportFileWriteStatus.PartialRetained, operation.Result!.Status);
        Assert.Equal("urltext.export.partialRetained", operation.StatusKey);
    }

}
