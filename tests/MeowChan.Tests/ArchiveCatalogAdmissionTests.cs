using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.Tests;

// Real synthetic SQLite profiles only. Gates establish causality; deadlines are
// watchdogs, not scheduling assumptions. No host/runtime/queue/transfer integration.
public sealed class ArchiveCatalogAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly string[] Operations =
    ["register", "title", "capture-enabled", "source", "capture", "capture-preserving",
        "decision", "omission", "delete", "cleanup", "write"];
    public static IEnumerable<object[]> WriterCases => Operations.Select(op => new object[] { op });
    public static IEnumerable<object[]> PhaseCases => Operations.SelectMany(op =>
        new[] { new object[] { op, false }, new object[] { op, true } });

    [Theory, MemberData(nameof(PhaseCases))]
    public async Task CompatibleWritersOwnExactlyOneEntryBeforeDispatchAndReleaseAfterCommit(string op, bool ready)
    {
        await using Fixture f = await Fixture.OpenAsync();
        if (ready) f.Lease.Admission.CompleteStartup();
        ProfileAdmissionToken? observed = null;
        int dispatched = 0, committed = 0;
        f.Catalog.BeforeWriteDispatchForTests = token =>
        {
            observed = token;
            Assert.Same(token, Assert.Single(Active(f.Lease)));
            Assert.Equal(ready ? ProfileAdmissionKind.Ordinary : ProfileAdmissionKind.Startup, token.Kind);
            dispatched++;
        };
        f.Catalog.AfterWriteCommitForTests = token =>
        {
            Assert.Same(observed, token);
            Assert.Same(token, Assert.Single(Active(f.Lease)));
            committed++;
        };
        Task work = f.Invoke(op);
        // Dispatch observation happens in the calling stack, not inside Task.Run.
        Assert.Equal(1, dispatched);
        await work.WaitAsync(Deadline);
        Assert.Equal(1, committed);
        Assert.Empty(Active(f.Lease));
        await f.AssertEffect(op);
    }

    [Theory, MemberData(nameof(WriterCases))]
    public async Task PendingAndActiveExclusiveRejectDirectWritersButAcceptedOwnerCanDrain(string op)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource abandon = new();
        Task<ProfileAdmissionToken> pending = f.Lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Invoke(op); });
            Assert.Throws<InvalidOperationException>(() => { _ = ArchiveCatalog.OpenAsync(f.Directory, f.Lease); });
            int dispatched = 0;
            f.Catalog.BeforeWriteDispatchForTests = token =>
            {
                Assert.Same(owner, token);
                Assert.Same(owner, Assert.Single(Active(f.Lease)));
                dispatched++;
            };
            await using (ArchiveCatalog reopened = await ArchiveCatalog.OpenAdmittedAsync(f.Directory, f.Lease, owner))
                Assert.Equal(f.Catalog.DatabasePath, reopened.DatabasePath);
            await f.Invoke(op, owner).WaitAsync(Deadline);
            Assert.Equal(1, dispatched);
            await f.AssertEffect(op);
            Assert.False(pending.IsCompleted);
            Assert.Same(owner, Assert.Single(Active(f.Lease)));
            owner.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            byte[] before = f.Digest();
            Assert.Throws<InvalidOperationException>(() => { _ = f.Invoke(op); });
            Assert.Throws<InvalidOperationException>(() => { _ = ArchiveCatalog.OpenAsync(f.Directory, f.Lease); });
            Assert.Equal(before, f.Digest());
        }
        finally
        {
            abandon.Cancel(); owner.Dispose();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory, MemberData(nameof(WriterCases))]
    public async Task BorrowedWritersRejectForeignDisposedUnissuedNullAndExclusiveTokensWithoutSqlEffects(string op)
    {
        await using Fixture f = await Fixture.OpenAsync();
        await using Fixture other = await Fixture.OpenAsync();
        using ProfileAdmissionToken foreign = other.Lease.Admission.EnterStartupWriter();
        using ProfileAdmissionToken disposed = f.Lease.Admission.EnterStartupWriter(); disposed.Dispose();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Startup);
        byte[] before = f.Digest();
        int committed = 0;
        f.Catalog.AfterWriteCommitForTests = _ => committed++;
        foreach (ProfileAdmissionToken invalid in new[] { foreign, disposed, unissued })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.InvokeAdmitted(op, invalid));
            Assert.Equal(before, f.Digest());
        }
        await Assert.ThrowsAsync<ArgumentNullException>(() => f.InvokeAdmitted(op, null!));
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken exclusive = await f.Lease.Admission.EnterExclusiveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.InvokeAdmitted(op, exclusive));
        Assert.Equal(0, committed);
        Assert.Equal(before, f.Digest());
    }

    [Theory, MemberData(nameof(PhaseCases))]
    public async Task CancelAfterAcceptanceBeforeDispatchReleasesOwnedWriterWithoutSql(string op, bool ready)
    {
        await using Fixture f = await Fixture.OpenAsync();
        if (ready) f.Lease.Admission.CompleteStartup();
        byte[] before = f.Digest();
        using CancellationTokenSource cancel = new();
        ProfileAdmissionToken? observed = null;
        int committed = 0;
        f.Catalog.BeforeWriteDispatchForTests = token =>
        {
            observed = token;
            Assert.Same(token, Assert.Single(Active(f.Lease)));
            cancel.Cancel(); // Precisely after EnterStoreWriter, before Task.Run.
        };
        f.Catalog.AfterWriteCommitForTests = _ => committed++;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Invoke(op, cancellationToken: cancel.Token));
        Assert.NotNull(observed);
        Assert.Empty(Active(f.Lease));
        Assert.Equal(0, committed);
        Assert.Equal(before, f.Digest());
        Assert.Throws<InvalidOperationException>(() => f.Lease.RunStoreWrite(observed!, () => true));
        f.Catalog.BeforeWriteDispatchForTests = null;
        await f.Invoke(op).WaitAsync(Deadline); // No phantom owner or poisoned catalog.
        await f.AssertEffect(op);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task OpenSupportsStandaloneStartingAndReadyAndBorrowedStartupWithoutTakingOwnership(bool ready)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        if (ready) lease.Admission.CompleteStartup();
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            Assert.Empty(Active(lease));
            using SqliteConnection connection = ArchiveSql.OpenConnection(catalog.DatabasePath, SqliteOpenMode.ReadOnly);
            Assert.Equal(13, ArchiveSql.GetPragma(connection, "user_version"));
        }
        using ProfileAdmissionToken owner = lease.Admission.EnterStoreWriter();
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAdmittedAsync(temp.Path, lease, owner);
        Assert.Same(owner, Assert.Single(Active(lease)));
        Assert.True(lease.RunStoreWrite(owner, () => true));
    }

    [Fact]
    public async Task InvalidOpenTokensCannotCreateDatabaseAndExclusiveDoesNotAuthorizeOrdinaryOpen()
    {
        using TempDirectory temp = new(); using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        using ProfileLease foreignLease = ProfileLease.Acquire(other.Path);
        using ProfileAdmissionToken foreign = foreignLease.Admission.EnterStartupWriter();
        using ProfileAdmissionToken disposed = lease.Admission.EnterStartupWriter(); disposed.Dispose();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Startup);
        foreach (ProfileAdmissionToken bad in new[] { foreign, disposed, unissued })
            await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveCatalog.OpenAdmittedAsync(temp.Path, lease, bad));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ArchiveCatalog.OpenAdmittedAsync(temp.Path, lease, null!));
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveCatalog.OpenAdmittedAsync(temp.Path, lease, exclusive));
        Assert.False(File.Exists(Path.Combine(temp.Path, "archive.db")));
    }

    [Fact]
    public async Task TransferBackendRequiresIssuedSameOwnerLiveExclusiveNotStartupOrOrdinary()
    {
        await using Fixture f = await Fixture.OpenAsync();
        await using Fixture other = await Fixture.OpenAsync();
        int bodies = 0;
        int Body(SqliteConnection c, SqliteTransaction t) { bodies++; return WriteTitle(c, t, "transfer"); }
        byte[] before = f.Digest();
        using (ProfileAdmissionToken startup = f.Lease.Admission.EnterStartupWriter())
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.WriteTransferAdmittedAsync(startup, Body));
        f.Lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken ordinary = f.Lease.Admission.EnterOrdinaryWriter())
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.WriteTransferAdmittedAsync(ordinary, Body));
        other.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken foreign = await other.Lease.Admission.EnterExclusiveAsync();
        using ProfileAdmissionToken disposed = await f.Lease.Admission.EnterExclusiveAsync(); disposed.Dispose();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Exclusive);
        foreach (ProfileAdmissionToken bad in new[] { foreign, disposed, unissued })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.WriteTransferAdmittedAsync(bad, Body));
        await Assert.ThrowsAsync<ArgumentNullException>(() => f.Catalog.WriteTransferAdmittedAsync<int>(null!, Body));
        Assert.Equal(0, bodies); Assert.Equal(before, f.Digest());
        using ProfileAdmissionToken exclusive = await f.Lease.Admission.EnterExclusiveAsync();
        Assert.Equal(1, await f.Catalog.WriteTransferAdmittedAsync(exclusive, Body));
        Assert.Equal(1, bodies); Assert.Equal("transfer", f.ReadTitle());
        Assert.Same(exclusive, Assert.Single(Active(f.Lease)));
        Assert.True(f.Lease.RunPrivateStoreCommit(exclusive, () => true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.WriteAdmittedAsync(exclusive, Body));
    }

    [Theory]
    [InlineData(false, false), InlineData(false, true), InlineData(true, true)]
    public async Task ActualSqlTransactionAndCommittedIoStayPinnedAfterOwnerDispose(bool transfer, bool shutdown)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = transfer ? await f.Lease.Admission.EnterExclusiveAsync()
            : f.Lease.Admission.EnterOrdinaryWriter();
        using Gate sql = new(); using Gate committed = new();
        f.Catalog.AfterWriteCommitForTests = token => { Assert.Same(owner, token); committed.Block(); };
        int Body(SqliteConnection connection, SqliteTransaction transaction)
        {
            Assert.Same(connection, transaction.Connection);
            int rows = WriteTitle(connection, transaction, "pinned");
            sql.Block(); // Real UPDATE executed, transaction still uncommitted.
            return rows;
        }
        Task<int> work = transfer ? f.Catalog.WriteTransferAdmittedAsync(owner, Body)
            : f.Catalog.WriteAdmittedAsync(owner, Body);
        using CancellationTokenSource abandon = new();
        Task<ProfileAdmissionToken>? exclusive = null; Task? drain = null;
        try
        {
            await sql.Reached.Task.WaitAsync(Deadline);
            Assert.Equal("before", f.ReadTitle()); // Separate connection cannot see uncommitted SQL.
            if (shutdown) { f.Lease.Admission.BeginShutdown(); drain = f.Lease.Admission.DrainAsync(); }
            else exclusive = f.Lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
            owner.Dispose();
            Assert.False((drain ?? exclusive!).IsCompleted);
            Assert.Same(owner, Assert.Single(Active(f.Lease)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => transfer
                ? f.Catalog.WriteTransferAdmittedAsync(owner, Body) : f.Catalog.WriteAdmittedAsync(owner, Body));
            sql.Release();
            await committed.Reached.Task.WaitAsync(Deadline);
            Assert.Equal("pinned", f.ReadTitle()); // Actual Commit has completed on disk.
            Assert.False(work.IsCompleted);
            Assert.False((drain ?? exclusive!).IsCompleted);
            Assert.Same(owner, Assert.Single(Active(f.Lease)));
            committed.Release();
            Assert.Equal(1, await work.WaitAsync(Deadline));
            if (drain != null) { await drain.WaitAsync(Deadline); f.Lease.Admission.CompleteShutdown(); }
            else (await exclusive!.WaitAsync(Deadline)).Dispose();
            Assert.Empty(Active(f.Lease));
        }
        finally
        {
            sql.Release(); committed.Release(); abandon.Cancel(); owner.Dispose();
            await Observe(work);
            if (exclusive != null)
            {
                try { (await exclusive.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
            if (drain != null) await drain.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task SqlFaultAndCancellationRollbackWithoutReleasingBorrowedOwner(bool transfer)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = transfer ? await f.Lease.Admission.EnterExclusiveAsync()
            : f.Lease.Admission.EnterOrdinaryWriter();
        Task<int> Write(Func<SqliteConnection, SqliteTransaction, int> body, CancellationToken ct = default) => transfer
            ? f.Catalog.WriteTransferAdmittedAsync(owner, body, ct) : f.Catalog.WriteAdmittedAsync(owner, body, ct);
        await Assert.ThrowsAsync<IOException>(() => Write((c, t) =>
        {
            WriteTitle(c, t, "must roll back");
            throw new IOException("synthetic transaction fault");
        }));
        Assert.Equal("before", f.ReadTitle());
        using CancellationTokenSource cancel = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write((c, t) =>
        {
            int rows = WriteTitle(c, t, "cancelled"); cancel.Cancel(); return rows;
        }, cancel.Token));
        Assert.Equal("before", f.ReadTitle());
        Assert.Same(owner, Assert.Single(Active(f.Lease)));
        Assert.Equal(1, await Write((c, t) => WriteTitle(c, t, "retry")));
        Assert.Equal("retry", f.ReadTitle());
    }

    [Fact]
    public async Task CompatibleSqlFailureAndValidationFaultReleaseOwnerButBorrowedPredispatchCancelDoesNot()
    {
        await using Fixture f = await Fixture.OpenAsync();
        await Assert.ThrowsAsync<IOException>(() => f.Catalog.WriteAsync<int>((c, t) =>
        {
            WriteTitle(c, t, "fault"); throw new IOException("synthetic owned write fault");
        }));
        Assert.Empty(Active(f.Lease)); Assert.Equal("before", f.ReadTitle());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Catalog.UpdateSourceStateAsync(Fixture.Id, (ThreadSourceState)999));
        Assert.Empty(Active(f.Lease));
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterStartupWriter();
        using CancellationTokenSource cancel = new();
        f.Catalog.BeforeWriteDispatchForTests = _ => cancel.Cancel();
        int bodies = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Catalog.WriteAdmittedAsync(owner, (c, t) => ++bodies, cancel.Token));
        Assert.Equal(0, bodies);
        Assert.Same(owner, Assert.Single(Active(f.Lease)));
        Assert.True(f.Lease.RunStoreWrite(owner, () => true));
        f.Catalog.BeforeWriteDispatchForTests = null;
        Assert.Equal(1, await f.Catalog.WriteAdmittedAsync(owner, (c, t) => WriteTitle(c, t, "after cancel")));
        Assert.Equal("after cancel", f.ReadTitle());
    }

    [Fact]
    public async Task InvalidOrdinaryTokenCannotBypassBlankTitleOrEmptyDeletionNoops()
    {
        await using Fixture f = await Fixture.OpenAsync();
        using ProfileAdmissionToken disposed = f.Lease.Admission.EnterStartupWriter(); disposed.Dispose();
        byte[] before = f.Digest();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.UpdateObservedTitleAdmittedAsync(disposed, Fixture.Id, " "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.DeleteThreadsAdmittedAsync(disposed, Array.Empty<ResourceId>()));
        Assert.Equal(before, f.Digest());
    }

    private static int WriteTitle(SqliteConnection connection, SqliteTransaction transaction, string title)
    {
        using SqliteCommand command = ArchiveSql.Command(connection, transaction,
            "UPDATE archive_threads SET title=$title WHERE source='4chan' AND board='g' AND thread='123'");
        command.Parameters.AddWithValue("$title", title);
        return command.ExecuteNonQuery();
    }

    private static ProfileAdmissionToken[] Active(ProfileLease lease)
    {
        object sync = Field(lease.Admission, "_sync")!;
        lock (sync) return ((IEnumerable<ProfileAdmissionToken>)Field(lease.Admission, "_active")!).ToArray();
    }
    private static object? Field(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static async Task Observe(Task work)
    {
        try { await work.WaitAsync(Deadline); }
        catch when (work.IsCompleted) { /* Assertions own outcome; teardown observes settlement. */ }
    }
    private sealed class Gate : IDisposable
    {
        internal TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        internal void Block()
        {
            Reached.TrySetResult(true);
            if (!_release.Wait(Deadline)) throw new TimeoutException("Catalog SQL gate watchdog.");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly ResourceId Id = new("4chan", "g", "123");
        private readonly TempDirectory _directory;
        internal string Directory => _directory.Path;
        internal ProfileLease Lease { get; }
        internal ArchiveCatalog Catalog { get; }
        internal MediaRuleStore Rules { get; }
        internal CandidateDecision Decision { get; }
        internal ArchiveDeletedThread Expected { get; }
        private string Media => Path.Combine(Directory, "media");
        private ArchiveThreadRegistration Registration => new(Id, "https://boards.4chan.org/g/thread/123", "before", Media, false);
        private CapturedThreadSnapshot Snapshot => new(Id, Registration.Url, "captured", ThreadSourceState.Active,
            DateTimeOffset.Parse("2026-11-07T12:00:00Z"), Media,
            [new CapturedPost("1", 0, PostFieldPresence.Html | PostFieldPresence.Attachments, Html: "post",
                Attachments: [new CapturedAttachment(0, "image", Url: "https://cdn.test/image.png")])]);
        private CandidateMetadata Candidate(string mediaId) => new(new CandidateKey(Id, mediaId),
            new Uri("https://cdn.test/" + mediaId + ".png"), Path.Combine(Media, mediaId + ".png"), extension: "png");
        private Fixture(TempDirectory directory, ProfileLease lease, ArchiveCatalog catalog,
            MediaRuleStore rules, CandidateDecision decision, ArchiveDeletedThread expected)
        { _directory = directory; Lease = lease; Catalog = catalog; Rules = rules; Decision = decision; Expected = expected; }
        internal static async Task<Fixture> OpenAsync()
        {
            TempDirectory directory = new();
            ProfileLease lease = ProfileLease.Acquire(directory.Path);
            ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            try
            {
                MediaRuleStore rules = new(directory.Path, lease);
                await rules.LoadAsync();
                string media = Path.Combine(directory.Path, "media");
                await catalog.RegisterThreadAsync(new(Id, "https://boards.4chan.org/g/thread/123", "before", media, false));
                CandidateDecision decision = await catalog.GetOrCreateDecisionAsync(new(new CandidateKey(Id, "seed"),
                    new Uri("https://cdn.test/seed.png"), Path.Combine(media, "seed.png"), extension: "png"), rules);
                ArchiveDeletedThread expected = Assert.Single((await catalog.PrepareDeletionAsync([Id])).Threads);
                return new(directory, lease, catalog, rules, decision, expected);
            }
            catch { await catalog.DisposeAsync(); lease.Dispose(); directory.Dispose(); throw; }
        }
        internal Task Invoke(string op, ProfileAdmissionToken? admission = null, CancellationToken cancellationToken = default)
            => admission == null ? InvokePublic(op, cancellationToken) : InvokeAdmitted(op, admission, cancellationToken);
        // Separate entry permits deliberately passing null to the admitted boundary.
        internal Task InvokeAdmitted(string op, ProfileAdmissionToken admission, CancellationToken ct = default) => op switch
        {
            "register" => Catalog.RegisterThreadAdmittedAsync(admission, Registration with { MediaDirectory = Media + "-new" }, ct),
            "title" => Catalog.UpdateObservedTitleAdmittedAsync(admission, Id, "observed", ct),
            "capture-enabled" => Catalog.SetCaptureEnabledAdmittedAsync(admission, Id, true, ct),
            "source" => Catalog.UpdateSourceStateAdmittedAsync(admission, Id, ThreadSourceState.NotFound, ct),
            "capture" => Catalog.CaptureAdmittedAsync(admission, Snapshot, ct),
            "capture-preserving" => Catalog.CapturePreservingRevisionsAdmittedAsync(admission, Snapshot, ct),
            "decision" => Catalog.GetOrCreateDecisionAdmittedAsync(admission, Candidate("new"), Rules, false, ct),
            "omission" => Catalog.RecordFilterOmissionAdmittedAsync(admission, Decision, true, "filter", ct),
            "delete" => Catalog.DeleteThreadsAdmittedAsync(admission, [Id], ct),
            "cleanup" => Catalog.DeleteCleanupThreadAdmittedAsync(admission, Expected, ct),
            "write" => Catalog.WriteAdmittedAsync(admission, (c, t) => WriteTitle(c, t, "written"), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
        private Task InvokePublic(string op, CancellationToken ct) => op switch
        {
            "register" => Catalog.RegisterThreadAsync(Registration with { MediaDirectory = Media + "-new" }, ct),
            "title" => Catalog.UpdateObservedTitleAsync(Id, "observed", ct),
            "capture-enabled" => Catalog.SetCaptureEnabledAsync(Id, true, ct),
            "source" => Catalog.UpdateSourceStateAsync(Id, ThreadSourceState.NotFound, ct),
            "capture" => Catalog.CaptureAsync(Snapshot, ct),
            "capture-preserving" => Catalog.CapturePreservingRevisionsAsync(Snapshot, ct),
            "decision" => Catalog.GetOrCreateDecisionAsync(Candidate("new"), Rules, false, ct),
            "omission" => Catalog.RecordFilterOmissionAsync(Decision, true, "filter", ct),
            "delete" => Catalog.DeleteThreadsAsync([Id], ct),
            "cleanup" => Catalog.DeleteCleanupThreadAsync(Expected, ct),
            "write" => Catalog.WriteAsync((c, t) => WriteTitle(c, t, "written"), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
        internal byte[] Digest() => SHA256.HashData(File.ReadAllBytes(Catalog.DatabasePath));
        internal string ReadTitle()
        {
            using SqliteConnection connection = ArchiveSql.OpenConnection(Catalog.DatabasePath, SqliteOpenMode.ReadOnly);
            using SqliteCommand command = ArchiveSql.Command(connection, null, "SELECT title FROM archive_threads");
            return (string)command.ExecuteScalar()!;
        }
        internal async Task AssertEffect(string op)
        {
            ArchiveDeletionPlan plan = await Catalog.PrepareDeletionAsync([Id]);
            if (op is "delete" or "cleanup") { Assert.Empty(plan.Threads); Assert.Empty(await Catalog.GetDecisionsAsync(Id)); return; }
            ArchiveThreadSummary summary = Assert.Single(plan.Threads).Summary;
            switch (op)
            {
                case "register": Assert.Equal(Media + "-new", summary.MediaDirectory); break;
                case "title": Assert.Equal("observed", summary.Title); break;
                case "capture-enabled": Assert.True(summary.CaptureEnabled); break;
                case "source": Assert.Equal(ThreadSourceState.NotFound, summary.SourceState); break;
                case "capture": case "capture-preserving":
                    await using (ArchiveReader reader = await ArchiveReader.OpenAsync(Directory))
                    {
                        CapturedAttachment attachment = Assert.Single(Assert.Single((await reader.ReadThreadAsync(Id))!.GetPosts()).Attachments!);
                        if (op == "capture") Assert.NotNull(attachment.AttachmentRevisionKey);
                        else Assert.Null(attachment.AttachmentRevisionKey);
                    }
                    Assert.Equal(1, summary.PostCount); break;
                case "decision": Assert.Equal(2, (await Catalog.GetDecisionsAsync(Id)).Count); break;
                case "omission": Assert.Equal(MediaSelectionReason.FilterNoMatch, Assert.Single(await Catalog.GetDecisionsAsync(Id)).Selection.Reason); break;
                case "write": Assert.Equal("written", summary.Title); break;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Catalog.BeforeWriteDispatchForTests = null; Catalog.AfterWriteCommitForTests = null;
            await Catalog.DisposeAsync(); Lease.Dispose(); _directory.Dispose();
        }
    }
}
