using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ArchiveCatalogTests
{
    private static readonly ResourceId ThreadId = new ResourceId("4chan", "g", "123");
    private static readonly DateTimeOffset CapturedAt = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("4chan")]
    [InlineData("8kun")]
    public async Task RetainedPostsKeepNumericSourceOrderWhenLaterResponsePositionsShift(string source)
    {
        await using Harness fixture = await Harness.OpenAsync();
        ResourceId id = ThreadId with { Source = source };
        CapturedThreadSnapshot first = fixture.Snapshot(Post("9", 0, "nine"), Post("10", 1, "ten"),
            Post("11", 2, "eleven"), Post("12", 3, "twelve")) with { Id = id };
        CapturedThreadSnapshot next = fixture.Snapshot(Post("10", 0, "updated ten"),
            Post("12", 1, "updated twelve"), Post("13", 2, "thirteen")) with { Id = id };
        string[] expected = ["9", "10", "11", "12", "13"];

        await fixture.Catalog.CaptureAsync(first);
        await fixture.Catalog.CaptureAsync(next);
        ArchivedThreadView stored = (await fixture.Reader.ReadThreadAsync(id))!;
        CapturedThreadSnapshot pending = ArchiveCaptureMerge.Merge(first, next);

        Assert.Equal(expected, stored.GetPosts().Select(post => post.PostId));
        Assert.Equal(expected, pending.Posts.Select(post => post.PostId));
        Assert.Equal(new[] { "10", "11" }, stored.GetPosts(1, 2).Select(post => post.PostId));
        Assert.Equal("updated ten", stored.GetPosts().Single(post => post.PostId == "10").Html);
    }

    [Fact]
    public async Task DecimalPostOrderDoesNotOverflowAndCustomSourcesKeepTheirProviderOrder()
    {
        await using Harness fixture = await Harness.OpenAsync();
        const string lower = "99999999999999999999999999999999";
        const string higher = "100000000000000000000000000000000";
        CapturedThreadSnapshot source = fixture.Snapshot(Post(higher, 0, "later"), Post(lower, 1, "earlier"));
        await fixture.Catalog.CaptureAsync(source);
        Assert.Equal(new[] { lower, higher }, (await fixture.Reader.ReadThreadAsync(ThreadId))!.GetPosts().Select(post => post.PostId));

        ResourceId custom = ThreadId with { Source = "custom" };
        await fixture.Catalog.CaptureAsync(source with { Id = custom });
        Assert.Equal(new[] { higher, lower }, (await fixture.Reader.ReadThreadAsync(custom))!.GetPosts().Select(post => post.PostId));
    }

    [Fact]
    public async Task LaterSnapshotsKeepMissingPostsAndFieldsButReplaceExplicitlyPresentValues()
    {
        await using Harness fixture = await Harness.OpenAsync();
        CapturedPost first = Post("1", 0, "first") with { Subject = "subject", Author = "author", PostedAt = CapturedAt };
        CapturedPost second = Post("2", 1, "old second") with { Subject = "retained subject", Author = "retained author" };
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(first, second));
        ArchivedThreadView before = (await fixture.Reader.ReadThreadAsync(ThreadId))!;

        await fixture.Catalog.CaptureAsync(fixture.Snapshot(
            new CapturedPost("2", 1, PostFieldPresence.Html | PostFieldPresence.Subject, Html: "new second", Subject: null),
            Post("3", 2, "third")) with { CapturedAt = CapturedAt.AddMinutes(1), SourceState = ThreadSourceState.Archived });
        ArchivedThreadView after = (await fixture.Reader.ReadThreadAsync(ThreadId))!;

        Assert.Equal(3, after.PostCount);
        Assert.Equal("first", after.GetPosts().Single(post => post.PostId == "1").Html);
        CapturedPost updated = after.GetPosts().Single(post => post.PostId == "2");
        Assert.Equal("new second", updated.Html);
        Assert.Null(updated.Subject);
        Assert.Equal("retained author", updated.Author);
        Assert.Equal(ThreadSourceState.Archived, after.Summary.SourceState);
        Assert.Equal(CapturedAt.AddMinutes(1), after.Summary.LastCapturedAt);
        Assert.Equal(2, before.PostCount);
        Assert.Equal("old second", before.GetPosts().Single(post => post.PostId == "2").Html);
    }

    [Fact]
    public async Task AttachmentDeletionMarksEarlierMetadataAndKeepsOrderedExtraAttachments()
    {
        await using Harness fixture = await Harness.OpenAsync();
        CapturedAttachment[] attachments =
        [
            new CapturedAttachment(0, "image", Url: "https://cdn.test/image.png", PlannedPath: Path.Combine(fixture.MediaDirectory, "image.png")),
            new CapturedAttachment(1, "video", Url: "https://cdn.test/video.webm", ExpectedSize: 42)
        ];
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "post") with { Attachments = attachments }));

        await fixture.Catalog.CaptureAsync(fixture.Snapshot(new CapturedPost("1", 0,
            PostFieldPresence.Attachments | PostFieldPresence.IsDeleted, IsDeleted: true,
            Attachments: new[] { new CapturedAttachment(0, null, IsDeleted: true) })));
        CapturedPost stored = Assert.Single((await fixture.Reader.ReadThreadAsync(ThreadId))!.GetPosts());

        Assert.True(stored.IsDeleted);
        Assert.Equal("post", stored.Html);
        Assert.Equal(2, stored.Attachments!.Count);
        Assert.Equal("image", stored.Attachments[0].MediaId);
        Assert.True(stored.Attachments[0].IsDeleted);
        Assert.Equal(attachments[0].PlannedPath, stored.Attachments[0].PlannedPath);
        Assert.Equal("video", stored.Attachments[1].MediaId);
        Assert.False(stored.Attachments[1].IsDeleted);
        Assert.False(Directory.Exists(fixture.MediaDirectory));
    }

    [Fact]
    public async Task MediaOnlyRegistrationAndExplicitCapturePreferenceSurviveNewWriter()
    {
        using TempDirectory directory = new TempDirectory();
        string media = Path.Combine(directory.Path, "media");
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            ArchiveThreadRegistration registration = new ArchiveThreadRegistration(ThreadId,
                "https://boards.4chan.org/g/thread/123", "title", media, false);
            await catalog.RegisterThreadAsync(registration);
            ArchiveThreadSummary existing = await catalog.RegisterThreadAsync(registration with { CaptureEnabled = true });
            Assert.False(existing.CaptureEnabled);
            Assert.Equal(0, existing.PostCount);
            Assert.Null(existing.LastCapturedAt);
        }
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            Assert.False(await catalog.GetCaptureEnabledAsync(ThreadId));
            await catalog.SetCaptureEnabledAsync(ThreadId, true);
            Assert.True(await catalog.GetCaptureEnabledAsync(ThreadId));
            await catalog.SetCaptureEnabledAsync(ThreadId, false);
            // A successful observation already admitted by the runtime still commits after disable.
            await catalog.CaptureAsync(new CapturedThreadSnapshot(ThreadId, "https://boards.4chan.org/g/thread/123",
                "title", ThreadSourceState.NotFound, CapturedAt, media, new[] { Post("1", 0, "offline") }));
        }
        // No writer lease, live watches or provider registration exists now.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchivedThreadView archived = (await reader.ReadThreadAsync(ThreadId))!;
        Assert.False(archived.Summary.CaptureEnabled);
        Assert.Equal(ThreadSourceState.NotFound, archived.Summary.SourceState);
        Assert.Equal("offline", Assert.Single(archived.GetPosts()).Html);
    }

    [Fact]
    public async Task ReassignedMediaRootKeepsEarlierDirectoriesWithoutCreatingThem()
    {
        await using Harness fixture = await Harness.OpenAsync();
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "post")));
        string second = Path.Combine(fixture.Directory.Path, "other-media-root");
        await fixture.Catalog.RegisterThreadAsync(new ArchiveThreadRegistration(ThreadId,
            "https://boards.4chan.org/g/thread/123", "changed title", second, true));

        IReadOnlyList<string> roots = await fixture.Reader.GetMediaDirectoriesAsync(ThreadId);
        ArchivedThreadView view = (await fixture.Reader.ReadThreadAsync(ThreadId))!;

        Assert.Equal(2, roots.Count);
        Assert.Contains(fixture.MediaDirectory, roots);
        Assert.Contains(second, roots);
        Assert.Equal(roots, view.MediaDirectories);
        Assert.Equal(second, view.Summary.MediaDirectory);
        Assert.False(Directory.Exists(second));
        Assert.False(Directory.Exists(fixture.MediaDirectory));
    }

    [Fact]
    public async Task IdenticalBoardThreadAndPostNumbersRemainSeparateAcrossProviders()
    {
        await using Harness fixture = await Harness.OpenAsync();
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "fourchan")));
        ResourceId other = ThreadId with { Source = "8kun" };
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "eightkun"))
            with { Id = other, Url = "https://8kun.top/g/res/123.html" });
        await fixture.Catalog.SetCaptureEnabledAsync(other, false);

        Assert.Equal(2, (await fixture.Reader.ListThreadsAsync()).TotalCount);
        Assert.Equal("fourchan", Assert.Single((await fixture.Reader.ReadThreadAsync(ThreadId))!.GetPosts()).Html);
        Assert.Equal("eightkun", Assert.Single((await fixture.Reader.ReadThreadAsync(other))!.GetPosts()).Html);
        Assert.True(await fixture.Catalog.GetCaptureEnabledAsync(ThreadId));
        Assert.False(await fixture.Catalog.GetCaptureEnabledAsync(other));
    }

    [Fact]
    public async Task RebindingRetainsCapturedMetadataAndSourceObservationDoesNotPretendToCaptureContent()
    {
        await using Harness fixture = await Harness.OpenAsync();
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "retained"))
            with { Title = "observed title", SourceState = ThreadSourceState.Archived });
        string nextDirectory = Path.Combine(fixture.Directory.Path, "new-default");

        ArchiveThreadSummary rebound = await fixture.Catalog.RegisterThreadAsync(new ArchiveThreadRegistration(ThreadId,
            "https://boards.4chan.org/g/thread/123", "Thread 123", nextDirectory, true));

        Assert.Equal("observed title", rebound.Title);
        Assert.Equal(ThreadSourceState.Archived, rebound.SourceState);
        Assert.Equal(CapturedAt, rebound.LastCapturedAt);
        Assert.Equal(nextDirectory, rebound.MediaDirectory);
        await fixture.Catalog.UpdateSourceStateAsync(ThreadId, ThreadSourceState.NotFound);
        ArchivedThreadView missing = (await fixture.Reader.ReadThreadAsync(ThreadId))!;
        Assert.Equal(ThreadSourceState.NotFound, missing.Summary.SourceState);
        Assert.Equal(CapturedAt, missing.Summary.LastCapturedAt);
        Assert.Equal("observed title", missing.Summary.Title);
        Assert.Equal("retained", Assert.Single(missing.GetPosts()).Html);
        Assert.True(missing.Summary.Revision > rebound.Revision);

        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "new content"))
            with { Title = "new source title", CapturedAt = CapturedAt.AddMinutes(1) });
        ArchiveThreadSummary current = (await fixture.Reader.ReadThreadAsync(ThreadId))!.Summary;
        Assert.Equal("new source title", current.Title);
        Assert.Equal(ThreadSourceState.Active, current.SourceState);
        Assert.Equal(CapturedAt.AddMinutes(1), current.LastCapturedAt);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, " \t ")]
    [InlineData(true, "")]
    [InlineData(true, " \t ")]
    public async Task TitlePersistenceReopenWithEmptyRegistrationKeepsKnownTitleAndCapturePreference(bool captured, string placeholder)
    {
        using TempDirectory directory = new TempDirectory();
        string media = Path.Combine(directory.Path, "media-not-created");
        const string title = "Known source subject – café 😀";
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(ThreadId,
                "https://boards.4chan.org/g/thread/123", title, media, captured));
            if (captured)
                await catalog.CaptureAsync(new CapturedThreadSnapshot(ThreadId,
                    "https://boards.4chan.org/g/thread/123", title, ThreadSourceState.Active, CapturedAt,
                    media, new[] { Post("123", 0, "Retained OP") }));
        }
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            ArchiveThreadSummary rebound = await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(ThreadId,
                "https://boards.4chan.org/g/thread/123", placeholder, media, !captured));
            Assert.Equal(title, rebound.Title);
            Assert.Equal(captured, rebound.CaptureEnabled);
            Assert.Equal(captured, rebound.LastCapturedAt.HasValue);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchivedThreadView stored = (await reader.ReadThreadAsync(ThreadId))!;
        Assert.Equal(title, stored.Summary.Title);
        Assert.Equal(captured, stored.Summary.CaptureEnabled);
        Assert.Equal(captured ? 1 : 0, stored.PostCount);
        Assert.False(Directory.Exists(media));
    }

    [Theory]
    [InlineData(false, "Unknown")]
    [InlineData(false, "Thread 123")]
    [InlineData(true, "Unknown")]
    [InlineData(true, "Thread 123")]
    public async Task TitlePersistenceLiteralDefaultShapedSubjectsSurviveNewWriter(bool captured, string title)
    {
        using TempDirectory directory = new TempDirectory();
        string media = Path.Combine(directory.Path, "media-not-created");
        ArchiveThreadRegistration registration = new ArchiveThreadRegistration(ThreadId,
            "https://boards.4chan.org/g/thread/123", title, media, captured);
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            await catalog.RegisterThreadAsync(registration);
            if (captured)
                await catalog.CaptureAsync(new CapturedThreadSnapshot(ThreadId, registration.Url, title,
                    ThreadSourceState.Active, CapturedAt, media, new[] { Post("123", 0, "Not the subject") }));
        }
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            Assert.Equal(title, (await catalog.RegisterThreadAsync(registration)).Title);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Equal(title, (await reader.ReadThreadAsync(ThreadId))!.Summary.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public async Task TitlePersistenceMissingSubjectObservationUpdatesPostsWithoutErasingTrueTitle(string missingTitle)
    {
        await using Harness fixture = await Harness.OpenAsync();
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("123", 0, "Old OP"))
            with { Title = "True subject" });
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("123", 0, "New OP"))
            with { Title = missingTitle, CapturedAt = CapturedAt.AddMinutes(1) });

        ArchivedThreadView stored = (await fixture.Reader.ReadThreadAsync(ThreadId))!;
        Assert.Equal("True subject", stored.Summary.Title);
        Assert.Equal("New OP", Assert.Single(stored.GetPosts()).Html);
        Assert.Equal(CapturedAt.AddMinutes(1), stored.Summary.LastCapturedAt);
    }

    [Fact]
    public async Task FailedTransactionPreservesEarlierPostsAndCaptureTimeThenRetryCommits()
    {
        await using Harness fixture = await Harness.OpenAsync();
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "one"), Post("2", 1, "old")));
        Execute(fixture.Catalog.DatabasePath, "CREATE TRIGGER fail_capture BEFORE INSERT ON archive_posts WHEN NEW.post='3' BEGIN SELECT RAISE(ABORT, 'synthetic'); END;");
        CapturedThreadSnapshot next = fixture.Snapshot(Post("2", 1, "new"), Post("3", 2, "three"))
            with { CapturedAt = CapturedAt.AddMinutes(1) };

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Catalog.CaptureAsync(next));
        ArchivedThreadView failed = (await fixture.Reader.ReadThreadAsync(ThreadId))!;
        Assert.Equal(2, failed.PostCount);
        Assert.Equal("old", failed.GetPosts().Single(post => post.PostId == "2").Html);
        Assert.Equal(CapturedAt, failed.Summary.LastCapturedAt);

        Execute(fixture.Catalog.DatabasePath, "DROP TRIGGER fail_capture;");
        await fixture.Catalog.CaptureAsync(next);
        ArchivedThreadView recovered = (await fixture.Reader.ReadThreadAsync(ThreadId))!;
        Assert.Equal(3, recovered.PostCount);
        Assert.Equal("new", recovered.GetPosts().Single(post => post.PostId == "2").Html);
    }

    [Fact]
    public async Task IndependentReaderSeesCommittedStateDuringAnotherTransaction()
    {
        await using Harness fixture = await Harness.OpenAsync();
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "committed")));
        using SqliteConnection writer = OpenSqlite(fixture.Catalog.DatabasePath);
        using SqliteTransaction transaction = writer.BeginTransaction();
        using SqliteCommand command = writer.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE archive_posts SET html='next' WHERE post='1'";
        command.ExecuteNonQuery();

        ArchivedThreadView before = (await fixture.Reader.ReadThreadAsync(ThreadId).WaitAsync(TestWait.Timeout))!;
        Assert.Equal("committed", Assert.Single(before.GetPosts()).Html);
        transaction.Commit();
        ArchivedThreadView after = (await fixture.Reader.ReadThreadAsync(ThreadId))!;
        Assert.Equal("next", Assert.Single(after.GetPosts()).Html);
        Assert.Equal("committed", Assert.Single(before.GetPosts()).Html);
    }

    [Fact]
    public async Task CaptureCopiesCallerListsBeforeWaitingForTheWriterLease()
    {
        await using Harness fixture = await Harness.OpenAsync();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holder = Task.Run(() => fixture.Lease.RunExclusive(() =>
        {
            entered.SetResult();
            if (!release.Wait(TestWait.Timeout)) throw new TimeoutException("Test gate not released.");
            return true;
        }));
        Task capture = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TestWait.Timeout);
            List<CapturedAttachment> attachments = new List<CapturedAttachment> { new CapturedAttachment(0, "media") };
            List<CapturedPost> posts = new List<CapturedPost> { Post("1", 0, "copied") with { Attachments = attachments } };
            CapturedThreadSnapshot snapshot = fixture.Snapshot() with { Posts = posts };
            capture = fixture.Catalog.CaptureAsync(snapshot);
            attachments.Clear();
            posts.Clear();
        }
        finally { release.Set(); await holder.WaitAsync(TestWait.Timeout); }
        await capture.WaitAsync(TestWait.Timeout);

        CapturedPost stored = Assert.Single((await fixture.Reader.ReadThreadAsync(ThreadId))!.GetPosts());
        Assert.Equal("copied", stored.Html);
        Assert.Equal("media", Assert.Single(stored.Attachments!).MediaId);
    }

    [Fact]
    public void PendingMergeRetainsEarlierObservationsWithoutAliasingCallerCollections()
    {
        List<CapturedPost> previousPosts = new List<CapturedPost> { Post("1", 0, "one"), Post("2", 1, "old") with { Author = "retained" } };
        string media = Path.GetFullPath("pending-media");
        CapturedThreadSnapshot previous = new CapturedThreadSnapshot(ThreadId, "https://boards.4chan.org/g/thread/123",
            "title", ThreadSourceState.Active, CapturedAt, media, previousPosts);
        List<CapturedPost> nextPosts = new List<CapturedPost>
        {
            new CapturedPost("2", 1, PostFieldPresence.Html, Html: "new"), Post("3", 2, "three")
        };

        CapturedThreadSnapshot pending = ArchiveCaptureMerge.Merge(previous, previous with { Posts = nextPosts });
        previousPosts.Clear();
        nextPosts.Clear();

        Assert.Equal(3, pending.Posts.Count);
        Assert.Equal("new", pending.Posts.Single(post => post.PostId == "2").Html);
        Assert.Equal("retained", pending.Posts.Single(post => post.PostId == "2").Author);
        Assert.Throws<NotSupportedException>(() => ((IList<CapturedPost>)pending.Posts).Clear());
    }

    [Fact]
    public async Task PaginationAndMissingThreadsStayBoundedAndReadOnly()
    {
        await using Harness fixture = await Harness.OpenAsync();
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "one"), Post("2", 1, "two"), Post("3", 2, "three")));
        ArchiveThreadPage page = await fixture.Reader.ListThreadsAsync(0, 1);
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Throws<NotSupportedException>(() => ((IList<ArchiveThreadSummary>)page.Items).Clear());
        Assert.Empty((await fixture.Reader.ListThreadsAsync(int.MaxValue)).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Reader.ListThreadsAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Reader.ListThreadsAsync(0, 501));
        ArchivedThreadView view = (await fixture.Reader.ReadThreadAsync(ThreadId))!;
        Assert.Equal("2", Assert.Single(view.GetPosts(1, 1)).PostId);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.GetPosts(0, 0));
        Assert.Null(await fixture.Reader.ReadThreadAsync(ThreadId with { Thread = "missing" }));
    }

    [Fact]
    public async Task ReaderDoesNotCreateMissingProfilesOrModifyExistingCatalogFiles()
    {
        using TempDirectory missing = new TempDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() => ArchiveReader.OpenAsync(Path.Combine(missing.Path, "absent")));
        Assert.Empty(Directory.GetFileSystemEntries(missing.Path));
        await using Harness fixture = await Harness.OpenAsync();
        await fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "post")));
        byte[] before = await File.ReadAllBytesAsync(fixture.Catalog.DatabasePath);
        string[] files = Directory.GetFiles(fixture.Directory.Path).Order().ToArray();

        await using ArchiveReader fresh = await ArchiveReader.OpenAsync(fixture.Directory.Path);
        await fresh.ListThreadsAsync();
        await fresh.ReadThreadAsync(ThreadId);

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Catalog.DatabasePath));
        Assert.Equal(files, Directory.GetFiles(fixture.Directory.Path).Order().ToArray());
    }

    [Fact]
    public async Task FutureSchemaFailsWithoutOverwritingIt()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease)) { }
        string path = Path.Combine(directory.Path, "archive.db");
        Execute(path, "PRAGMA user_version=999;");
        byte[] before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCatalog.OpenAsync(directory.Path, lease));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveReader.OpenAsync(directory.Path));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("archive_attachments")]
    [InlineData("media_decisions")]
    public async Task IncompleteCurrentSchemaFailsAtOpenInsteadOfBeingSilentlyRepaired(string missingTable)
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease)) { }
        string path = Path.Combine(directory.Path, "archive.db");
        Execute(path, "DROP TABLE " + missingTable);
        byte[] before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<SqliteException>(() => ArchiveReader.OpenAsync(directory.Path));
        await Assert.ThrowsAsync<SqliteException>(() => ArchiveCatalog.OpenAsync(directory.Path, lease));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrUnrelatedDatabaseIsNotReinitialized(bool unrelated)
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, "archive.db");
        if (unrelated) Execute(path, "CREATE TABLE unrelated(value TEXT);");
        else await File.WriteAllTextAsync(path, "not a sqlite database");
        byte[] before = await File.ReadAllBytesAsync(path);

        Exception? failure = await Record.ExceptionAsync(() => ArchiveCatalog.OpenAsync(directory.Path, lease));

        Assert.NotNull(failure);
        Assert.True(failure is SqliteException or InvalidDataException);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ValidationAndCancellationDoNotPublishPartialContent()
    {
        await using Harness fixture = await Harness.OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "a"), Post("1", 1, "b"))));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog.CaptureAsync(fixture.Snapshot(Post("1", 0, "a")), cancellation.Token));
        Assert.Empty((await fixture.Reader.ListThreadsAsync()).Items);
    }

    private static CapturedPost Post(string id, int ordinal, string html)
        => new CapturedPost(id, ordinal, PostFieldPresence.All, Html: html);

    private static SqliteConnection OpenSqlite(string path)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql)
    {
        using SqliteConnection connection = OpenSqlite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public TempDirectory Directory { get; } = new TempDirectory();
        public ProfileLease Lease { get; private set; } = null!;
        public ArchiveCatalog Catalog { get; private set; } = null!;
        public ArchiveReader Reader { get; private set; } = null!;
        public string MediaDirectory => Path.Combine(Directory.Path, "media-not-created");
        public static async Task<Harness> OpenAsync()
        {
            Harness fixture = new Harness();
            fixture.Lease = ProfileLease.Acquire(fixture.Directory.Path);
            fixture.Catalog = await ArchiveCatalog.OpenAsync(fixture.Directory.Path, fixture.Lease);
            fixture.Reader = await ArchiveReader.OpenAsync(fixture.Directory.Path);
            return fixture;
        }
        public CapturedThreadSnapshot Snapshot(params CapturedPost[] posts)
            => new CapturedThreadSnapshot(ThreadId, "https://boards.4chan.org/g/thread/123", "title",
                ThreadSourceState.Active, CapturedAt, MediaDirectory, posts);
        public async ValueTask DisposeAsync()
        {
            await Reader.DisposeAsync();
            await Catalog.DisposeAsync();
            Lease.Dispose();
            Directory.Dispose();
        }
    }
}
