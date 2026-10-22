using System.Net;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.OfficialSources.FourChan;

namespace MeowChan.SourceAdapter.Tests;

public sealed class SourceAdapterTests
{
    [Theory]
    [InlineData("https://boards.4chan.org/g/thread/123/title#p124", "https://boards.4chan.org/g/thread/123")]
    [InlineData("http://boards.4channel.org/g/thread/123/?test=1", "https://boards.4chan.org/g/thread/123")]
    [InlineData("https://boards.4chan.org/g/", "https://boards.4chan.org/g/")]
    public void RoutingAndConstructionNeverAcquireWorker(string input, string expected)
    {
        int calls = 0;
        using PluginSourceImageBoard source = new(new FourChanPlugin().Describe(), "4chan", _ => { calls++; throw new Exception("No IPC in routing"); });
        Uri uri = new(input);
        Assert.True(source.CanHandle(uri));
        string actual = source.IsThreadUri(uri) ? source.GetThread(uri).Url : source.GetBoard(uri).BoardUri.AbsoluteUri;
        Assert.Equal(expected, actual);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("https://boards.4chan.org.evil/g/thread/123")]
    [InlineData("https://user@boards.4chan.org/g/thread/123")]
    [InlineData("https://boards.4chan.org:8443/g/thread/123")]
    [InlineData("https://boards.4chan.org/g/thread/12%2f3")]
    public void InvalidRoutesDoNotOwnTheResource(string input)
    {
        PluginSourceRouter router = new(new FourChanPlugin().Describe().Sources[0]);
        Assert.False(router.TryResolve(new Uri(input), out _, out _, out _, out _));
    }

    [Fact]
    public async Task RemovedWatchCannotApplyLateWorkerResult()
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime content = new(profile.Path, lease);
        await content.InitializeAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pendingToken = default;
        TestWorker worker = new(async (request, operation, token) =>
        {
            pendingToken = token;
            entered.TrySetResult();
            await release.Task; // Adversarial worker ignores cancellation.
            return Snapshot(request.Thread, "late");
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, profile.Path + "/media", TimeSpan.FromHours(1), 3600,
            contentRuntime: content, workStore: new WorkStateStore(profile.Path, lease));
        AdmissionResult admitted = await registry.AddAsync("https://boards.4chan.org/g/thread/123");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task removal = registry.RemoveThreadAsync(admitted.Thread!);
        await WaitUntilAsync(() => pendingToken.IsCancellationRequested);
        release.SetResult();
        await removal.WaitAsync(TimeSpan.FromSeconds(10));
        await using ArchiveReader reader = await content.OpenReaderAsync();
        ArchivedThreadView? view = await reader.ReadThreadAsync(new ResourceId("4chan", "g", "123"));
        Assert.Equal(0, view!.PostCount);
        Assert.True(worker.LastOperation!.Generation > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndDeferredNeverBecomeSourceEnd(bool deferred)
    {
        using TemporaryProfile profile = new();
        TestWorker worker = new((request, _, _) => Task.FromResult<ThreadReadResult>(deferred
            ? new ThreadDeferred(request.Thread, DateTimeOffset.UtcNow.AddMinutes(1), "cooldown", 429)
            : new ThreadFailure(request.Thread, new FailureDetail("bad_json", "Invalid source JSON"))));
        using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, profile.Path, TimeSpan.FromHours(1), 3600);
        AdmissionResult admitted = await registry.AddAsync("https://boards.4chan.org/g/thread/123");
        await WaitUntilAsync(() => admitted.Thread!.LastCheckFailure != null);
        Assert.Equal(ThreadSourceState.Active, admitted.Thread!.SourceState);
        Assert.Single(registry.Threads);
    }

    internal static ThreadReadResult Snapshot(SourceThreadId id, string text) => new PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(id,
        new Uri($"https://boards.4chan.org/{id.BoardId}/thread/{id.ThreadId}"), "captured", SourceThreadState.Active,
        new[] { new SourcePost(id.ThreadId, 0, SourceFieldPresence.Text, Text: text, TextFormat: SourceTextFormat.PlainText) }));

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    internal sealed class TestWorker(Func<ThreadReadRequest, PluginOperationContext, CancellationToken, Task<ThreadReadResult>> read, PluginDescription? description = null) : IPluginWorkerClient
    {
        public PluginDescription Description { get; } = description ?? new FourChanPlugin().Describe();
        public PluginWorkerIdentity Identity { get; } = new("test", "build", "config");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, Array.Empty<string>());
        public PluginOperationContext? LastOperation { get; private set; }
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        { LastOperation = operation; return read(request, operation, cancellationToken); }
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FieldValidationError>>(Array.Empty<FieldValidationError>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class TemporaryProfile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-adapter-" + Guid.NewGuid().ToString("N"));
        public TemporaryProfile() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
