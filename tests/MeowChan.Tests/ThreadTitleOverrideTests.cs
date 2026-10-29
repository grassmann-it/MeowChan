using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ThreadTitleOverrideTests
{
    private static readonly ResourceId Id = new("stub", "g", "123");

    [Theory]
    [InlineData("External provider title")]
    [InlineData("Unknown")]
    [InlineData("Thread 123")]
    [InlineData("stub /g/ 123")]
    [InlineData("Opening fallback")]
    public async Task IndependentVirtualTitleRemainsProviderMetadataAtBind(string title)
    {
        using TempDirectory profile = new();
        using StubImageBoard source = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        IndependentTitleThread thread = new(source.Board) { Title = title };
        thread.SetOpeningTitle("Opening fallback");
        await runtime.BindAsync(thread, profile.Path, restored: true);
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        Assert.Equal(title, (await reader.ReadThreadAsync(Id))!.Summary.Title);
        Assert.Equal(title, thread.Title);
        Assert.Equal(title, thread.SourceTitle);
        Assert.Equal(title, thread.GetSnapshot(new()).Title);
    }

    [Fact]
    public async Task OfflineBindRestoresThroughExistingVirtualSetterWithoutOverwritingCurrentProviderTitle()
    {
        using TempDirectory profile = new();
        using StubImageBoard source = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        await runtime.Catalog.RegisterThreadAsync(new(Id, "https://stub.test/g/thread/123", "Saved provider title", profile.Path, false));
        IndependentTitleThread restored = new(source.Board);
        await runtime.BindAsync(restored, profile.Path, restored: true);
        Assert.Equal("Saved provider title", restored.Title);
        Assert.Equal("Saved provider title", restored.GetSnapshot(new()).Title);
        Assert.False(restored.GetSnapshot(new()).Content!.CaptureEnabled);
        IndependentTitleThread current = new(source.Board) { Title = "Fresh provider title" };
        await runtime.BindAsync(current, profile.Path, restored: true);
        Assert.Equal("Fresh provider title", current.Title);
        Assert.Equal("Fresh provider title", current.GetSnapshot(new()).Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task MissingObservationDoesNotClearAnIndependentOverride(string missing)
    {
        using TempDirectory profile = new();
        using StubImageBoard source = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        IndependentTitleThread thread = new(source.Board) { Title = "Known external title" };
        await runtime.BindAsync(thread, profile.Path, restored: true);
        await thread.ObserveAsync(new(Id, thread.Url, missing, ThreadSourceState.Active, DateTimeOffset.UtcNow, profile.Path,
            [new("123", 0, PostFieldPresence.Html, Html: "OP") ]));
        Assert.Equal("Known external title", thread.Title);
        Assert.Equal("Known external title", thread.GetSnapshot(new()).Title);
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        Assert.Equal("Known external title", (await reader.ReadThreadAsync(Id))!.Summary.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedBaseGetterNeverPersistsItsIdentityOrOpeningDisplay(bool opening)
    {
        using TempDirectory profile = new();
        using StubImageBoard source = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        DelegatingTitleThread thread = new(source.Board);
        if (opening) thread.SetOpeningTitle("Opening fallback");
        string display = opening ? "Opening fallback" : "stub /g/ 123";
        Assert.Equal(display, thread.Title);
        Assert.Equal("", thread.SourceTitle);
        await runtime.BindAsync(thread, profile.Path, restored: true);
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        Assert.Equal("", (await reader.ReadThreadAsync(Id))!.Summary.Title);
        Assert.Equal(display, thread.Title);
        Assert.Equal(display, thread.GetSnapshot(new()).Title);
        thread.Title = "Unknown";
        Assert.Equal("Unknown", thread.SourceTitle);
    }

    [Fact]
    public async Task OverrideFormattingIsNotFedBackIntoBaseBackingState()
    {
        using TempDirectory profile = new();
        using StubImageBoard source = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        FormattingTitleThread thread = new(source.Board) { Title = "Subject" };
        await runtime.BindAsync(thread, profile.Path, restored: true);
        for (int read = 0; read < 3; read++)
        {
            Assert.Equal("Provider: Subject", thread.SourceTitle);
            Assert.Equal("Provider: Subject", thread.Title);
            Assert.Equal("Provider: Subject", thread.GetSnapshot(new()).Title);
        }
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        Assert.Equal("Provider: Subject", (await reader.ReadThreadAsync(Id))!.Summary.Title);
    }

    [Fact]
    public async Task PassiveSnapshotDoesNotInvokeProviderVirtualGetter()
    {
        using TempDirectory profile = new();
        using StubImageBoard source = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        DelegatingTitleThread thread = new(source.Board) { Title = "Provider title" };
        await runtime.BindAsync(thread, profile.Path, restored: true);
        thread.BeforeRead = () => throw new InvalidOperationException("Snapshot must not invoke external code");
        Assert.Equal("Provider title", thread.GetSnapshot(new()).Title);
    }

    [Fact]
    public void FailedVirtualReadCannotLeaveTheBaseGetterInRawMode()
    {
        using StubImageBoard source = new();
        DelegatingTitleThread thread = new(source.Board) { BeforeRead = () => throw new InvalidOperationException("provider getter") };
        thread.SetOpeningTitle("Opening fallback");
        Assert.Throws<InvalidOperationException>(() => thread.SourceTitle);
        thread.BeforeRead = null;
        Assert.Equal("Opening fallback", thread.Title);
        Assert.Equal("", thread.SourceTitle);
    }

    [Fact]
    public async Task RawReadOnWorkerDoesNotChangeConcurrentDisplayGetter()
    {
        using StubImageBoard source = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        DelegatingTitleThread thread = new(source.Board) { BeforeRead = () => {
            if (Interlocked.Increment(ref reads) != 1) return;
            entered.TrySetResult();
            if (!release.Wait(TestWait.Timeout)) throw new TimeoutException("test getter not released");
        } };
        thread.SetOpeningTitle("Opening fallback");
        Task<string> raw = Task.Run(() => thread.SourceTitle);
        try
        {
            await entered.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal("Opening fallback", thread.Title);
        }
        finally { release.Set(); }
        Assert.Equal("", await raw.WaitAsync(TestWait.Timeout));
        Assert.Equal("Opening fallback", thread.Title);
    }

    private class ProviderThread(IBoard board) : BaseThread
    {
        public override IBoard Board => board;
        public override string ThreadId => "123";
        public override string NiceName => Title;
        public override Uri ThreadUri => new("https://stub.test/g/thread/123");
        public override bool SupportsContent => true;
        internal Task ObserveAsync(CapturedThreadSnapshot snapshot) => CaptureContentAsync(snapshot, default);
    }

    // An existing third-party extension: no base getter/setter and no new title API.
    private sealed class IndependentTitleThread(IBoard board) : ProviderThread(board)
    {
        public override string Title { get; set; } = string.Empty;
    }

    private sealed class FormattingTitleThread(IBoard board) : ProviderThread(board)
    {
        public override string Title
        {
            get => "Provider: " + base.Title;
            set => base.Title = value;
        }
    }

    private sealed class DelegatingTitleThread(IBoard board) : ProviderThread(board)
    {
        internal Action? BeforeRead;
        public override string Title
        {
            get { BeforeRead?.Invoke(); return base.Title; }
            set => base.Title = value;
        }
    }
}
