using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Synthetic local SQLite + real ZIP preparation. No host lane simulation, sleeps or network.
// Root executes after the separately owned old-caller migration, outside the W26 freeze.
public sealed class NativeTextImportAdmissionTests
{
    private static readonly ResourceId Id = new("4chan", "g", "701");
    private const string Url = "https://boards.4chan.org/g/thread/701";
    private static readonly DateTimeOffset At = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidAuthorityCannotDispatchEvenForHistoricalReceipt(bool replay)
    {
        await using Fixture f = await Fixture.OpenAsync();
        await using Fixture other = await Fixture.OpenAsync();
        if (!replay)
        {
            using ProfileAdmissionToken startup = f.Lease.Admission.EnterStartupWriter();
            string before = Snapshot(f.Catalog.DatabasePath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Commit(startup));
            Assert.Equal(before, Snapshot(f.Catalog.DatabasePath));
        }
        f.Lease.Admission.CompleteStartup();
        if (replay)
        {
            using ProfileAdmissionToken accepted = await f.Lease.Admission.EnterExclusiveAsync();
            Assert.Equal(NativeTextImportCommitStatus.Committed, (await f.Commit(accepted)).Result.Status);
        }
        other.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken foreign = await other.Lease.Admission.EnterExclusiveAsync();
        using ProfileAdmissionToken disposed = await f.Lease.Admission.EnterExclusiveAsync();
        disposed.Dispose();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Exclusive);
        using ProfileAdmissionToken wrongRole = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Startup);
        string unchanged = Snapshot(f.Catalog.DatabasePath);
        int dispatched = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatched++;
        using (ProfileAdmissionToken ordinary = f.Lease.Admission.EnterOrdinaryWriter())
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Commit(ordinary));
        foreach (ProfileAdmissionToken bad in new[] { foreign, disposed, unissued, wrongRole })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Commit(bad));
        await Assert.ThrowsAsync<ArgumentNullException>(() => f.Commit(null!));
        Assert.Equal(0, dispatched);
        Assert.Equal(unchanged, Snapshot(f.Catalog.DatabasePath));
        Assert.Empty(Directory.GetDirectories(f.Directory));
        Assert.Equal(replay, await f.Catalog.ReadNativeTextImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256) != null);
    }

    [Fact]
    public async Task OneBorrowedExclusiveSurvivesActualCommitAndReconciliationUntilCallerReleases()
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        using ManualResetEventSlim release = new();
        TaskCompletionSource<bool> committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = token => { Assert.Same(owner, token); dispatches++; };
        f.Catalog.AfterWriteCommitForTests = token =>
        {
            Assert.Same(owner, token);
            committed.TrySetResult(true);
            if (!release.Wait(Deadline)) throw new TimeoutException("Synthetic SQL gate watchdog.");
        };
        Task<ProfileExclusiveOutcome<NativeTextImportCommitResult>> work = f.Commit(owner);
        try
        {
            await committed.Task.WaitAsync(Deadline);
            Assert.Equal(1L, Scalar(f.Catalog.DatabasePath, "SELECT count(*) FROM archive_import_receipts"));
            Assert.False(work.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.EnterOrdinaryWriter());
            f.Lease.Admission.BeginShutdown();
            Task drain = f.Lease.Admission.DrainAsync();
            Assert.False(drain.IsCompleted);
            release.Set();
            var result = await work.WaitAsync(Deadline);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, result.Disposition);
            Assert.Equal(NativeTextImportCommitStatus.Committed, result.Result.Status);
            Assert.False(result.Result.IsReplay);
            Assert.Equal(1, dispatches);
            Assert.False(drain.IsCompleted); // Consumer did not dispose the borrowed owner.
            owner.Dispose();
            await drain.WaitAsync(Deadline);
            f.Lease.Admission.CompleteShutdown();
        }
        finally
        {
            release.Set();
            try { await work.WaitAsync(Deadline); } catch when (work.IsCompleted) { }
        }
    }

    [Fact]
    public async Task ReceiptTriggerAbortProvesFullRollbackThenSameOwnerCanRetry()
    {
        await using Fixture f = await Fixture.OpenAsync(seed: true);
        Execute(f.Catalog.DatabasePath, "CREATE TRIGGER fail_text_receipt BEFORE INSERT ON archive_import_receipts BEGIN SELECT RAISE(ABORT,'synthetic abort'); END;");
        string before = Snapshot(f.Catalog.DatabasePath);
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var failed = await f.Commit(owner);
        Assert.Equal(NativeTextImportCommitStatus.Inactive, failed.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, failed.Disposition);
        Assert.Equal(nameof(SqliteException), failed.Result.Diagnostic);
        Assert.Equal(before, Snapshot(f.Catalog.DatabasePath)); // All tables, fence, quotes, candidate bytes.
        Assert.Null(await f.Catalog.ReadNativeTextImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        Assert.True(f.Lease.RunPrivateStoreCommit(owner, () => true));
        Execute(f.Catalog.DatabasePath, "DROP TRIGGER fail_text_receipt;");
        var retried = await f.Commit(owner);
        Assert.Equal(NativeTextImportCommitStatus.Committed, retried.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, retried.Disposition);
        Assert.Equal(1, retried.Result.NewPosts);
        Assert.Equal(1, retried.Result.UpdatedPosts);
        Assert.Equal(1, retried.Result.ConflictingThreads);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Directory);
        CapturedPost original = (await reader.ReadThreadAsync(Id))!.GetPosts().Single(p => p.PostId == "1");
        Assert.Null(original.Author); // Explicit null remains; incoming author cannot replace it.
        Assert.Equal("filled", original.Html);
        Assert.Single(await f.Catalog.ReadNativeTextImportCandidatesAsync(Id));
        Assert.Single((await reader.GetBacklinksAsync(new(Id, "1"))).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelBeforeSqlUsesUncancelledIndependentNegativeProof(bool atDispatch)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        using CancellationTokenSource cancel = new();
        string before = Snapshot(f.Catalog.DatabasePath);
        if (atDispatch) f.Catalog.BeforeWriteDispatchForTests = _ => cancel.Cancel();
        else cancel.Cancel();
        var result = await f.Commit(owner, cancel.Token);
        Assert.Equal(NativeTextImportCommitStatus.Inactive, result.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, result.Disposition);
        Assert.Equal(before, Snapshot(f.Catalog.DatabasePath));
        Assert.True(f.Lease.RunPrivateStoreCommit(owner, () => true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostReturnOrCancellationAfterActualCommitReconcilesReceipt(bool cancelAfterCommit)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        using CancellationTokenSource cancel = new();
        f.Catalog.AfterWriteCommitForTests = _ =>
        {
            if (cancelAfterCommit) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
            throw new IOException("synthetic lost committed return");
        };
        var result = await f.Commit(owner, cancel.Token);
        Assert.Equal(NativeTextImportCommitStatus.Committed, result.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, result.Disposition);
        Assert.True(result.Result.IsReplay);
        Assert.Equal(1, result.Result.NewThreads); Assert.Equal(2, result.Result.NewPosts);
        f.Catalog.AfterWriteCommitForTests = null;
        string after = Snapshot(f.Catalog.DatabasePath);
        var replay = await f.Commit(owner, cancel.Token); // Cancellation cannot erase a durable receipt.
        Assert.Equal(NativeTextImportCommitStatus.Committed, replay.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
        Assert.Equal(after, Snapshot(f.Catalog.DatabasePath));
    }

    [Theory]
    [InlineData("generation", false, "StalePlan", "SafeToResume")]
    [InlineData("generation", true, "Unknown", "RecoveryRequired")]
    [InlineData("instance", false, "Unknown", "RecoveryRequired")]
    [InlineData("instance", true, "Unknown", "RecoveryRequired")]
    [InlineData("schema", true, "Unknown", "RecoveryRequired")]
    public async Task EvidenceDistinguishesExplicitStaleFromUncertainFailure(string mutation, bool fault,
        string status, string disposition)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        f.Catalog.BeforeWriteDispatchForTests = _ =>
        {
            Execute(f.Catalog.DatabasePath, mutation switch
            {
                "generation" => "UPDATE archive_import_fence SET generation=generation+1;",
                "instance" => "UPDATE archive_import_fence SET instance='11111111-1111-1111-1111-111111111111';",
                _ => "PRAGMA user_version=999;"
            });
            if (fault) throw new IOException("synthetic dispatch failure");
        };
        var outcome = await f.Commit(owner);
        Assert.Equal(status, outcome.Result.Status.ToString()); Assert.Equal(disposition, outcome.Disposition.ToString());
        Assert.Equal(0L, Scalar(f.Catalog.DatabasePath, "SELECT count(*) FROM archive_posts"));
        Assert.Equal(0L, Scalar(f.Catalog.DatabasePath, "SELECT count(*) FROM archive_import_receipts"));
    }

    [Fact]
    public async Task ForgedPlanNeverDispatchesOrReplaysButRequiresCleanIndependentEvidence()
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        var forged = new NativeTextImportPlan(f.Catalog, f.Plan.Instance, f.Plan.Generation, At,
            f.Plan.Deltas, f.Plan.RequestSha256);
        int dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        string before = Snapshot(f.Catalog.DatabasePath);
        var result = await f.Catalog.CommitNativeTextImportAdmittedAsync(owner, forged);
        Assert.Equal(NativeTextImportCommitStatus.StalePlan, result.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, result.Disposition);
        Assert.Equal(0, dispatches); Assert.Equal(before, Snapshot(f.Catalog.DatabasePath));
        Assert.Equal(NativeTextImportCommitStatus.Committed, (await f.Commit(owner)).Result.Status);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task SeparateUnresolvedOrCommittedWithoutReceiptBlocksResume(bool merge, bool committedWithoutReceipt, bool replay)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        if (replay) Assert.Equal(NativeTextImportCommitStatus.Committed, (await f.Commit(owner)).Result.Status);
        SeedBlocker(f, merge, committedWithoutReceipt);
        string before = Snapshot(f.Catalog.DatabasePath);
        var outcome = await f.Commit(owner);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(replay ? NativeTextImportCommitStatus.Committed : NativeTextImportCommitStatus.Unknown, outcome.Result.Status);
        Assert.Equal(before, Snapshot(f.Catalog.DatabasePath));
        Assert.Empty(Directory.GetDirectories(f.Directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostReturnReceiptWithSeparateUnresolvedPreservesSuccessButRequiresRecovery(bool merge)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        f.Catalog.AfterWriteCommitForTests = _ =>
        {
            SeedBlocker(f, merge, committed: false);
            throw new IOException("synthetic lost return plus independent unfinished operation");
        };
        var outcome = await f.Commit(owner);
        Assert.Equal(NativeTextImportCommitStatus.Committed, outcome.Result.Status);
        Assert.True(outcome.Result.IsReplay);
        Assert.Equal(2, outcome.Result.NewPosts);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(1L, Scalar(f.Catalog.DatabasePath, "SELECT count(*) FROM archive_import_receipts"));
    }

    [Theory]
    [InlineData("missing")] [InlineData("wrong-request")] [InlineData("instance")] [InlineData("schema")]
    public async Task PostCommitMissingOrInconsistentEvidenceCannotResume(string damage)
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = await f.Lease.Admission.EnterExclusiveAsync();
        f.Catalog.AfterWriteCommitForTests = _ =>
        {
            Execute(f.Catalog.DatabasePath, damage switch
            {
                "missing" => "DELETE FROM archive_import_receipts;",
                "wrong-request" => "UPDATE archive_import_receipts SET request_sha256='" + new string('a', 64) + "';",
                "instance" => "UPDATE archive_import_fence SET instance='22222222-2222-2222-2222-222222222222';",
                _ => "PRAGMA user_version=999;"
            });
            throw new IOException("synthetic evidence damage after commit");
        };
        var outcome = await f.Commit(owner);
        Assert.Equal(NativeTextImportCommitStatus.Unknown, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(2L, Scalar(f.Catalog.DatabasePath, "SELECT count(*) FROM archive_posts"));
    }

    [Fact]
    public async Task HistoricalReceiptSurvivesDeletionAndOriginalCatalogLeaseDisposal()
    {
        using TempDirectory directory = new();
        ArchiveCatalog catalog;
        NativeTextImportPlan plan;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            await using (catalog)
            {
                plan = await Prepare(catalog);
                lease.Admission.CompleteStartup();
                using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
                    Assert.Equal(NativeTextImportCommitStatus.Committed,
                        (await catalog.CommitNativeTextImportAdmittedAsync(owner, plan)).Result.Status);
                await catalog.DeleteThreadsAsync([Id]);
                using ProfileAdmissionToken replayOwner = await lease.Admission.EnterExclusiveAsync();
                var replay = await catalog.CommitNativeTextImportAdmittedAsync(replayOwner, plan);
                Assert.True(replay.Result.IsReplay);
                Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
                Assert.Equal(0L, Scalar(catalog.DatabasePath, "SELECT count(*) FROM archive_posts"));
            }
        }
        NativeTextImportCommitResult receipt = (await catalog.ReadNativeTextImportReceiptAsync(plan.OperationId, plan.RequestSha256))!;
        Assert.Equal(NativeTextImportCommitStatus.Committed, receipt.Status);
        Assert.True(receipt.IsReplay); Assert.Equal(2, receipt.NewPosts);
    }

    private static async Task<NativeTextImportPlan> Prepare(ArchiveCatalog catalog)
    {
        TransferThread thread = new(Id, Url, "synthetic", ThreadSourceState.NotFound, At, null, null, true,
            [new(new("1", 1, PostFieldPresence.Html | PostFieldPresence.Author, Author: "incoming", Html: "filled"), []),
             new(new("2", 2, PostFieldPresence.Html, Html: "<a href=\"#p1\">&gt;&gt;1</a>"), [])]);
        using MemoryStream package = new();
        var written = await NativeTransferPackageWriter.WriteAsync(package, new(At, [thread]), [], Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString()); package.Position = 0;
        var prepared = await catalog.PrepareNativeTextImportAsync(package);
        Assert.Equal(NativeTextImportError.None, prepared.Error);
        return Assert.IsType<NativeTextImportPlan>(prepared.Plan);
    }

    private static void SeedBlocker(Fixture f, bool merge, bool committed)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        // An unrelated resource must block profile resume too; no files/candidates fabricated.
        command.CommandText = merge ? """
            INSERT INTO archive_media_merge_operations(operation_id,request_sha256,package_fingerprint,catalog_instance,
                catalog_generation,source,board,thread,operations_parent,imported_at,state,roots_json,full_keys_json,
                safe_keys_json,catalog_bytes,full_directory,full_manifest_generation,full_bindings_sha256,
                new_posts,updated_posts,new_attachments,new_bindings,conflicts)
            VALUES($id,$hash,$hash,$instance,0,'8kun','b','999',$parent,$at,$state,'[]','[]','[]',X'7B7D',
                $directory,$generation,$generation,0,0,0,0,0);
            """ : """
            INSERT INTO archive_media_import_operations(operation_id,request_sha256,package_fingerprint,catalog_instance,
                catalog_generation,source,board,thread,operations_parent,imported_at,state,revision_keys_json,
                candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count)
            VALUES($id,$hash,$hash,$instance,0,'8kun','b','999',$parent,$at,$state,'[]',$directory,$generation,$generation,0,1);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$hash", new string('b', 64));
        command.Parameters.AddWithValue("$instance", f.Plan.Instance);
        command.Parameters.AddWithValue("$parent", Path.Combine(f.Directory, "inert-unresolved"));
        command.Parameters.AddWithValue("$at", At.ToString("O"));
        command.Parameters.AddWithValue("$state", committed ? "Committed" : "Preparing");
        command.Parameters.AddWithValue("$directory", committed ? (object)Path.Combine(f.Directory, "inert-child") : DBNull.Value);
        command.Parameters.AddWithValue("$generation", committed ? (object)new string('c', 64) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void Execute(string path, string sql)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Scalar(string path, string sql)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static string Snapshot(string path)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        List<string> names = new();
        using (SqliteCommand tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using SqliteDataReader rows = tables.ExecuteReader(); while (rows.Read()) names.Add(rows.GetString(0));
        }
        List<string> snapshot = new();
        foreach (string name in names)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
            using SqliteDataReader rows = command.ExecuteReader(); List<string> values = new();
            while (rows.Read())
                values.Add(JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount).Select(i => rows.IsDBNull(i) ? null :
                    rows.GetValue(i) is byte[] bytes ? "blob:" + Convert.ToBase64String(bytes) :
                    Convert.ToString(rows.GetValue(i), CultureInfo.InvariantCulture)).ToArray()));
            values.Sort(StringComparer.Ordinal); snapshot.Add(name + "\n" + string.Join("\n", values));
        }
        return string.Join("\n", snapshot);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _directory;
        internal string Directory => _directory.Path;
        internal ProfileLease Lease { get; }
        internal ArchiveCatalog Catalog { get; }
        internal NativeTextImportPlan Plan { get; }
        private Fixture(TempDirectory directory, ProfileLease lease, ArchiveCatalog catalog, NativeTextImportPlan plan)
        { _directory = directory; Lease = lease; Catalog = catalog; Plan = plan; }
        internal static async Task<Fixture> OpenAsync(bool seed = false)
        {
            TempDirectory directory = new(); ProfileLease lease = ProfileLease.Acquire(directory.Path);
            ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            try
            {
                if (seed) await catalog.CaptureAsync(new(Id, Url, "synthetic", ThreadSourceState.NotFound, At,
                    Path.Combine(directory.Path, "inert"), [new("1", 1, PostFieldPresence.Author, Author: null)])
                    { DecimalPostOrder = true });
                return new(directory, lease, catalog, await Prepare(catalog));
            }
            catch { await catalog.DisposeAsync(); lease.Dispose(); directory.Dispose(); throw; }
        }
        internal Task<ProfileExclusiveOutcome<NativeTextImportCommitResult>> Commit(ProfileAdmissionToken owner, CancellationToken ct = default)
            => Catalog.CommitNativeTextImportAdmittedAsync(owner, Plan, ct);
        public async ValueTask DisposeAsync()
        {
            Catalog.BeforeWriteDispatchForTests = null; Catalog.AfterWriteCommitForTests = null;
            await Catalog.DisposeAsync(); Lease.Dispose(); _directory.Dispose();
        }
    }
}
