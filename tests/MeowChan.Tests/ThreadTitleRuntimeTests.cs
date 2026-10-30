using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class ThreadTitleRuntimeTests
{
    private static readonly ResourceId Id = new("stub", "g", "123");

    [Theory]
    [InlineData("Unknown", ThreadSourceState.Active)]
    [InlineData("Thread 123", ThreadSourceState.Archived)]
    [InlineData("A real title", ThreadSourceState.NotFound)]
    public async Task MediaOnlyTitleSurvivesBlankObservationsAndOfflineRestart(string title, ThreadSourceState state)
    {
        using TempDirectory profile = new();
        using StubImageBoard source = OfflineSource();
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ThreadContentRuntime runtime = new(profile.Path, lease);
            await runtime.InitializeAsync();
            TitleThread thread = new(source.Board);
            await runtime.BindAsync(thread, profile.Path, restored: true);
            await thread.ObserveAsync(Observation(profile.Path, title));
            await thread.ObserveAsync(Observation(profile.Path, " \t "));
            await thread.RecordStateAsync(state);
            Assert.Equal(title, thread.Title);
            Assert.Equal(title, thread.GetSnapshot(new()).Title);
            await using ArchiveReader reader = await runtime.OpenReaderAsync();
            ArchivedThreadView stored = (await reader.ReadThreadAsync(Id))!;
            Assert.Equal(title, stored.Summary.Title);
            Assert.False(stored.Summary.CaptureEnabled);
            Assert.Null(stored.Summary.LastCapturedAt);
            Assert.Empty(stored.GetPosts());
            Assert.Equal(state, stored.Summary.SourceState);
        }
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ThreadContentRuntime runtime = new(profile.Path, lease);
            await runtime.InitializeAsync();
            TitleThread restored = new(source.Board);
            await runtime.BindAsync(restored, profile.Path, restored: true);
            Assert.Equal(title, restored.Title);
            Assert.Equal(title, restored.SourceTitle);
            Assert.False(restored.GetSnapshot(new()).Content!.CaptureEnabled);
            await restored.ObserveAsync(null); // retry path does not fetch a title
            Assert.Equal(title, restored.GetSnapshot(new()).Title);
            await restored.ObserveAsync(Observation(profile.Path, "Changed real title"));
            Assert.Equal("Changed real title", restored.Title);
            await using ArchiveReader reader = await runtime.OpenReaderAsync();
            Assert.Equal("Changed real title", (await reader.ReadThreadAsync(Id))!.Summary.Title);
            Assert.Equal(Id, Assert.Single((await reader.SearchThreadsAsync(new("Changed real title"))).Items).Id);
            Assert.Empty((await reader.SearchThreadsAsync(new(title))).Items);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningDisplayNeverBecomesRawTitleOrEnablesCapture(bool capture)
    {
        using TempDirectory profile = new();
        using StubImageBoard source = OfflineSource();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        TitleThread thread = new(source.Board);
        await runtime.BindAsync(thread, profile.Path, restored: !capture);
        Assert.Equal("stub /g/ 123", thread.Title);
        await thread.ObserveAsync(Observation(profile.Path, "", new CapturedPost("124", 0, PostFieldPresence.Html, Html: "reply")));
        Assert.Equal("stub /g/ 123", thread.Title);
        await thread.ObserveAsync(Observation(profile.Path, "", new CapturedPost("123", 1, PostFieldPresence.Html,
            Html: "<b>Hello &amp; 👩🏽‍💻</b><br>world<script>bad()</script>")));
        Assert.Equal("Hello & 👩🏽‍💻 world", thread.Title);
        Assert.Equal(thread.Title, thread.GetSnapshot(new()).Title);
        Assert.Equal("", thread.SourceTitle);
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ArchivedThreadView stored = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal("", stored.Summary.Title);
        Assert.Equal(capture, stored.Summary.CaptureEnabled);
        Assert.Equal(capture, stored.Summary.LastCapturedAt != null);
        TitleThread rebound = new(source.Board);
        await runtime.BindAsync(rebound, profile.Path, restored: true);
        Assert.Equal(capture ? "Hello & 👩🏽‍💻 world" : "stub /g/ 123", rebound.Title);
        Assert.Equal("", rebound.SourceTitle);
        await rebound.ObserveAsync(Observation(profile.Path, "Unknown"));
        Assert.Equal("Unknown", rebound.Title);
    }

    [Fact]
    public async Task PartialResponsesKeepOpeningExcerptButExplicitEmptyOpeningClearsIt()
    {
        using StubImageBoard source = OfflineSource();
        TitleThread thread = new(source.Board);
        await thread.ObserveAsync(Observation(Path.GetTempPath(), "", new CapturedPost("123", 0, PostFieldPresence.Html, Html: "OP")));
        await thread.ObserveAsync(Observation(Path.GetTempPath(), "", new CapturedPost("124", 0, PostFieldPresence.Html, Html: "reply")));
        Assert.Equal("OP", thread.Title);
        await thread.ObserveAsync(Observation(Path.GetTempPath(), "", new CapturedPost("123", 0, PostFieldPresence.Html, Html: " ")));
        Assert.Equal("stub /g/ 123", thread.Title);
        thread.Title = "Unknown";
        await thread.ObserveAsync(Observation(Path.GetTempPath(), "", new CapturedPost("123", 0, PostFieldPresence.Html)));
        Assert.Equal("Unknown", thread.Title);
    }

    [Fact]
    public async Task CapturedTitleSurvivesEmptyCaptureAndStartupRegistration()
    {
        using TempDirectory profile = new();
        using StubImageBoard source = OfflineSource();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        TitleThread thread = new(source.Board);
        await runtime.BindAsync(thread, profile.Path, restored: false);
        await thread.ObserveAsync(Observation(profile.Path, "Saved title"));
        await thread.ObserveAsync(Observation(profile.Path, ""));
        TitleThread rebound = new(source.Board);
        await runtime.BindAsync(rebound, profile.Path, restored: true);
        Assert.Equal("Saved title", rebound.Title);
        Assert.Equal("Saved title", rebound.SourceTitle);
    }

    [Fact]
    public async Task BindingUsesFullIdentityAndCancelledOrForeignObservationsCannotChangeTitle()
    {
        using TempDirectory profile = new();
        using StubImageBoard source = OfflineSource();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        await runtime.Catalog.CaptureAsync(Observation(profile.Path, "Another source") with { Id = Id with { Source = "other" } });
        await runtime.Catalog.CaptureAsync(Observation(profile.Path, "Another board") with { Id = Id with { Board = "b" } });
        TitleThread thread = new(source.Board);
        await runtime.BindAsync(thread, profile.Path, restored: true);
        Assert.Equal("stub /g/ 123", thread.Title);
        await Assert.ThrowsAsync<ArgumentException>(() => thread.ObserveAsync(Observation(profile.Path, "Wrong") with { Id = Id with { Source = "other" } }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => thread.ObserveAsync(Observation(profile.Path, "Late"), new CancellationToken(true)));
        Assert.Equal("stub /g/ 123", thread.Title);
        TitleThread cancelledBind = new(source.Board);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.BindAsync(cancelledBind, profile.Path, true, new CancellationToken(true)));
        Assert.Null(cancelledBind.GetSnapshot(new()).Content);
        Assert.Equal("stub /g/ 123", cancelledBind.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalReplayDoesNotOverwriteNewerBoundMetadata(bool capture)
    {
        using TempDirectory profile = new();
        using StubImageBoard source = OfflineSource();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        await runtime.Catalog.RegisterThreadAsync(new(Id, "https://stub.test/g/thread/123",
            "Newer known title", profile.Path, capture));
        TitleThread thread = new(source.Board);
        thread.RestoreDurableState(new WorkResource {
            Url = thread.Url, OutputRoot = profile.Path, Generation = 8, Intent = WorkIntent.Watching,
            IsThread = true, Source = ThreadSourceStateValue.Archived,
            FinalSnapshotJson = System.Text.Json.JsonSerializer.Serialize(Observation(profile.Path, "Old title")
                with { SourceState = ThreadSourceState.Archived })
        });
        await runtime.BindAsync(thread, profile.Path, restored: true);
        await thread.ObserveAsync(null);
        Assert.Equal("Newer known title", thread.Title);
        Assert.Equal(8, thread.WatchGeneration);
        Assert.Equal(ThreadSourceState.Archived, thread.SourceState);
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        Assert.Equal("Newer known title", (await reader.ReadThreadAsync(Id))!.Summary.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataWriteFailureIsVisibleAndRetriesWithoutCapturingPosts(bool returnToSaved)
    {
        using TempDirectory profile = new();
        using StubImageBoard source = OfflineSource();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        TitleThread thread = new(source.Board);
        await runtime.BindAsync(thread, profile.Path, restored: true);
        await thread.ObserveAsync(Observation(profile.Path, "Saved title"));
        using SqliteConnection connection = new($"Data Source={runtime.Catalog.DatabasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_title BEFORE UPDATE OF title ON archive_threads BEGIN SELECT RAISE(ABORT,'title failure'); END";
        command.ExecuteNonQuery();
        await thread.ObserveAsync(Observation(profile.Path, "Retried title"));
        Assert.NotNull(thread.GetSnapshot(new()).Content!.CaptureFailure);
        Assert.Equal("Retried title", thread.Title);
        command.CommandText = "DROP TRIGGER fail_title";
        command.ExecuteNonQuery();
        await thread.ObserveAsync(returnToSaved ? Observation(profile.Path, "Saved title") : null);
        Assert.Null(thread.GetSnapshot(new()).Content!.CaptureFailure);
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ArchivedThreadView stored = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(returnToSaved ? "Saved title" : "Retried title", stored.Summary.Title);
        Assert.False(stored.Summary.CaptureEnabled);
        Assert.Null(stored.Summary.LastCapturedAt);
        Assert.Empty(stored.GetPosts());
    }

    private static StubImageBoard OfflineSource() => new(new DelegateHandler((_, _) =>
        throw new InvalidOperationException("Title resolution must not access the network.")));

    private static CapturedThreadSnapshot Observation(string directory, string title, params CapturedPost[] posts) =>
        new(Id, "https://stub.test/g/thread/123", title, ThreadSourceState.Active, DateTimeOffset.UtcNow, directory, posts);

    private sealed class TitleThread(IBoard board) : BaseThread
    {
        public override IBoard Board => board;
        public override string ThreadId => "123";
        public override string NiceName => Title;
        public override Uri ThreadUri => new("https://stub.test/g/thread/123");
        public override bool SupportsContent => true;
        internal Task ObserveAsync(CapturedThreadSnapshot? observation, CancellationToken token = default) => CaptureContentAsync(observation, token);
        internal Task RecordStateAsync(ThreadSourceState state) => CaptureSourceStateAsync(state, default);
    }
}
