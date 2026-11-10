using System.Collections.Immutable;
using System.Text;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tests.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// R0 only: synthetic SQLite metadata, no import producer, payload, Host or network.
// This file does not share fixture ownership with the disjoint adversarial test lease.
public sealed class LocalCollectionReaderTests
{
    private const string Instance = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private static readonly Guid Operation = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly LocalCollectionId Collection = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly string Hash = new('a', 64);
    private static readonly LocalImportCodec Codec = new();
    private static LocalImportReceiptKey Key => new(Instance, Operation, Hash);
    private static LocalImportRecoveryKey RecoveryKey => new(Instance, Operation);

    private static LocalCollectionImportReceipt Receipt() => new(1, Instance, Operation, Hash, Collection, 1,
        new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero),
        ImmutableArray.Create(new LocalRegisteredRevision(new(Collection,
            new(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"))), 0, LocalMediaRole.Original, 0, Hash)));

    [Theory]
    [InlineData(9)] [InlineData(11)]
    public async Task AbsenceRequiresCompatibleSameInstanceSchemaAndDoesNotWrite(int version)
    {
        using Workspace files = new();
        if (version == 9) files.SeedLegacy(9);
        else await files.CreateCurrentAsync();
        byte[] before = File.ReadAllBytes(files.Database);
        Assert.Null(await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None));
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly))
        using (var tx = connection.BeginTransaction(deferred: true))
        {
            Assert.Null(ArchiveSql.ReadLocalImportReceiptForRecovery(connection, tx, RecoveryKey, Codec, CancellationToken.None));
            tx.Commit();
        }
        Assert.Equal(before, File.ReadAllBytes(files.Database));
    }

    [Fact]
    public async Task StaticLookupSurvivesOwnerAndReaderDisposalButInstanceHonorsReaderLifetime()
    {
        using Workspace files = new();
        await files.CreateCurrentAsync(); // The creating Catalog AND ProfileLease are already disposed.
        var expected = Receipt();
        files.InsertReceipt(expected);
        var reader = await ArchiveReader.OpenAsync(files.Directory);
        AssertReceipt(expected, await reader.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        await reader.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        byte[] before = File.ReadAllBytes(files.Database);
        AssertReceipt(expected, await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(files.Database));
    }

    [Theory]
    [InlineData(9)] [InlineData(11)]
    public async Task HistoricalReceiptSurvivesContentDeletionAndNewGenerationWithoutInventoryOrPayload(int version)
    {
        using Workspace files = new();
        if (version == 9) files.SeedLegacy(9);
        else await files.CreateCurrentAsync();
        var expected = Receipt();
        files.InsertReceipt(expected);
        files.Execute("""
            INSERT INTO local_collections VALUES('11111111-1111-1111-1111-111111111111','synthetic',1);
            INSERT INTO local_items VALUES('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222',0,NULL);
            INSERT INTO local_media_revisions VALUES('11111111-1111-1111-1111-111111111111',
                '22222222-2222-2222-2222-222222222222','33333333-3333-3333-3333-333333333333',1,
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0,'synthetic-no-file',NULL);
            DELETE FROM local_media_revisions;
            DELETE FROM local_items;
            DELETE FROM local_collections;
            UPDATE archive_import_fence SET generation=9223372036854775807;
            """);
        AssertReceipt(expected, await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None));
        using var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        using var tx = connection.BeginTransaction(deferred: true);
        Assert.Equal(long.MaxValue, ArchiveSql.RequireLocalTarget(connection, tx, Instance).Generation);
        AssertReceipt(expected, ArchiveSql.ReadLocalImportReceiptForRecovery(connection, tx, RecoveryKey, Codec, CancellationToken.None));
        using var count = ArchiveSql.Command(connection, tx,
            "SELECT (SELECT count(*) FROM local_collections)+(SELECT count(*) FROM local_import_operations)");
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
        tx.Commit();
        Assert.False(System.IO.Directory.Exists(Path.Combine(files.Directory, "local-import-operations")));
    }

    [Fact]
    public async Task ExpectedDigestConflictIsNotAbsenceAndRecoveryUsesValidatedStoredDigest()
    {
        using Workspace files = new();
        await files.CreateCurrentAsync();
        var stored = Receipt() with { RequestSha256 = new string('b', 64) };
        files.InsertReceipt(stored);
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.OperationConflict);
        using var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        using var tx = connection.BeginTransaction(deferred: true);
        AssertReceipt(stored, ArchiveSql.ReadLocalImportReceiptForRecovery(connection, tx, RecoveryKey, Codec, CancellationToken.None));
        Assert.Null(ArchiveSql.ReadLocalImportReceiptForRecovery(connection, tx,
            new(Instance, Guid.Parse("55555555-5555-5555-5555-555555555555")), Codec, CancellationToken.None));
        tx.Commit();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task BoundLegacyIncludingMatchingV8FenceIsTargetReplacedNeverNull(int version)
    {
        using Workspace files = new();
        files.SeedLegacy(version);
        byte[] before = File.ReadAllBytes(files.Database);
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.TargetReplaced);
        using (var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly))
        using (var tx = connection.BeginTransaction(deferred: true))
        {
            RejectRead(() => ArchiveSql.ReadLocalImportReceiptForRecovery(connection, tx, RecoveryKey, Codec, CancellationToken.None),
                LocalImportError.TargetReplaced);
            tx.Commit();
        }
        Assert.Equal(before, File.ReadAllBytes(files.Database));
    }

    [Fact]
    public async Task SamePathReplacementV8AfterSuccessfulCurrentReadIsNotNegativeCommitProof()
    {
        using Workspace files = new();
        using Workspace replacement = new();
        await files.CreateCurrentAsync();
        files.InsertReceipt(Receipt());
        Assert.NotNull(await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None));
        replacement.SeedLegacy(8); // Same instance is deliberately insufficient; the version changed.
        File.Copy(replacement.Database, files.Database, overwrite: true);
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.TargetReplaced);
        // L3, not R0, must map an unclear earlier commit to Unknown. No commit result here.
    }

    [Theory]
    [InlineData("PRAGMA user_version=13")]
    [InlineData("ALTER TABLE archive_posts DROP COLUMN attachments_were_null; PRAGMA user_version=11")]
    [InlineData("PRAGMA application_id=123")]
    [InlineData("DROP TABLE local_import_receipts")]
    [InlineData("DROP TRIGGER import_fence_local_items_INSERT")]
    [InlineData("UPDATE archive_import_fence SET instance='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'")]
    [InlineData("UPDATE archive_import_fence SET instance=upper(instance)")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE archive_import_fence SET generation=1.5")]
    public async Task IncompatibleOrNoncanonicalTargetCannotHideBehindAbsentReceipt(string mutation)
    {
        using Workspace files = new();
        await files.CreateCurrentAsync();
        files.Execute(mutation);
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.TargetReplaced);
    }

    [Fact]
    public async Task RedundantCollectionHeaderMustAgreeWithJsonForBothLookups()
    {
        using Workspace files = new();
        await files.CreateCurrentAsync();
        files.InsertReceipt(Receipt());
        files.Execute("UPDATE local_import_receipts SET collection_id='55555555-5555-5555-5555-555555555555'");
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.IntegrityFailure);
        using var connection = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        using var tx = connection.BeginTransaction(deferred: true);
        RejectRead(() => ArchiveSql.ReadLocalImportReceiptForRecovery(connection, tx, RecoveryKey, Codec, CancellationToken.None),
            LocalImportError.IntegrityFailure);
        tx.Commit();
    }

    [Theory]
    [InlineData("receipt_json")]
    [InlineData("request_sha256")]
    [InlineData("operation_id")]
    public async Task BlobStorageIsRejectedRatherThanCoercedToValidText(string column)
    {
        using Workspace files = new();
        await files.CreateCurrentAsync();
        files.InsertReceipt(Receipt());
        // These column names are test-owned literals, not product SQL input.
        files.Execute($"PRAGMA ignore_check_constraints=ON; UPDATE local_import_receipts SET {column}=CAST({column} AS BLOB)");
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.IntegrityFailure);
    }

    [Fact]
    public async Task RawByteBudgetIsCheckedBeforeJsonParsing()
    {
        using Workspace files = new();
        await files.CreateCurrentAsync();
        files.InsertReceipt(Receipt());
        files.ReplaceJson(new string(' ', LocalImportBounds.MaximumReceiptBytes + 1), ignoreChecks: true);
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.BudgetExceeded);
    }

    [Fact]
    public async Task LoadedCodecFailureRetainsCodeWithoutPrivateDiagnosticText()
    {
        using Workspace files = new();
        await files.CreateCurrentAsync();
        files.InsertReceipt(Receipt());
        files.ReplaceJson("{\"untrusted-private-source\":\"not-a-receipt\"}");
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.InvalidSelection);
        files.Execute("UPDATE local_import_receipts SET receipt_json=CAST(X'80' AS TEXT)");
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.InvalidSelection);
    }

    [Fact]
    public async Task HeaderFenceAndReceiptShareCallerSnapshotWhileWriterIsUncommitted()
    {
        using Workspace files = new();
        await files.CreateCurrentAsync();
        var oldReceipt = Receipt();
        var newReceipt = oldReceipt with { RequestSha256 = new string('b', 64) };
        files.InsertReceipt(oldReceipt);
        using (var read = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly))
        using (var readTx = read.BeginTransaction(deferred: true))
        {
            var fence = ArchiveSql.RequireLocalTarget(read, readTx, Instance); // Pins the read snapshot.
            using var writer = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite);
            using var writeTx = writer.BeginTransaction(deferred: true);
            using (var change = ArchiveSql.Command(writer, writeTx, """
                UPDATE archive_import_fence SET generation=generation+1;
                UPDATE local_import_receipts SET request_sha256=$hash,receipt_json=$json
                """))
            {
                change.Parameters.AddWithValue("$hash", newReceipt.RequestSha256);
                change.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(Codec.EncodeReceipt(newReceipt)));
                change.ExecuteNonQuery();
            }
            Assert.Equal(fence, ArchiveSql.RequireLocalTarget(read, readTx, Instance));
            AssertReceipt(oldReceipt, ArchiveSql.ReadLocalImportReceipt(read, readTx, Key, Codec, CancellationToken.None));
            AssertReceipt(oldReceipt, ArchiveSql.ReadLocalImportReceiptForRecovery(read, readTx, RecoveryKey, Codec, CancellationToken.None));
            // Rollback-journal ordering: release the reader BEFORE asking writer COMMIT.
            readTx.Commit();
            writeTx.Commit();
        }
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.OperationConflict);
        AssertReceipt(newReceipt, await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database,
            Key with { RequestSha256 = newReceipt.RequestSha256 }, CancellationToken.None));
    }

    [Fact]
    public async Task MissingOrUnreadableDatabaseIsNotAbsenceAndDoesNotCreateDatabase()
    {
        using Workspace files = new();
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None));
        Assert.Null(missing.FileName);
        Assert.Null(missing.InnerException);
        Assert.False(File.Exists(files.Database));
        File.WriteAllBytes(files.Database, Encoding.UTF8.GetBytes("synthetic-not-a-sqlite-database"));
        await RejectReadAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, CancellationToken.None),
            LocalImportError.IoFailure);
    }

    [Fact]
    public async Task MalformedCallerKeysFailBeforeIoAndCancellationIsRealCancellation()
    {
        using Workspace files = new(); // No database, so an IO-first implementation would fail differently.
        Assert.Throws<LocalImportCodecException>(() =>
        { _ = ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, default, CancellationToken.None); });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Database, Key, cancelled.Token));
        Assert.False(File.Exists(files.Database));
    }

    private static void AssertReceipt(LocalCollectionImportReceipt expected, LocalCollectionImportReceipt? actual)
    {
        LocalCollectionImportReceipt found = Assert.IsType<LocalCollectionImportReceipt>(actual);
        Assert.Equal(Codec.EncodeReceipt(expected), Codec.EncodeReceipt(found));
    }
    private static void RejectRead(Func<LocalCollectionImportReceipt?> read, LocalImportError error)
    {
        var exception = Assert.Throws<LocalImportReadException>(() => { _ = read(); });
        AssertCodeOnly(exception, error);
    }
    private static async Task RejectReadAsync(Func<Task<LocalCollectionImportReceipt?>> read, LocalImportError error)
    {
        var exception = await Assert.ThrowsAsync<LocalImportReadException>(async () => { _ = await read(); });
        AssertCodeOnly(exception, error);
    }
    private static void AssertCodeOnly(LocalImportReadException exception, LocalImportError error)
    {
        Assert.Equal(error, exception.Error);
        Assert.Null(exception.InnerException);
        Assert.Equal("Local import read failed: " + error, exception.Message);
    }

    private sealed class Workspace : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "ochan-local-receipt-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Directory, "archive.db");
        internal Workspace() => System.IO.Directory.CreateDirectory(Directory);

        internal async Task CreateCurrentAsync()
        {
            using ProfileLease lease = ProfileLease.Acquire(Directory);
            await using (var catalog = await ArchiveCatalog.OpenAsync(Directory, lease)) { }
            Execute("UPDATE archive_import_fence SET instance='" + Instance + "'");
        }
        internal void SeedLegacy(int version)
        {
            using var connection = ArchiveSql.OpenConnection(Database, SqliteOpenMode.ReadWriteCreate);
            using var fixture = typeof(LocalCollectionReaderTests).Assembly.GetManifestResourceStream("MeowChan.Tests.Fixtures.archive-schema-1.sql")!;
            using StreamReader text = new(fixture);
            using (var seed = ArchiveSql.Command(connection, null, text.ReadToEnd())) seed.ExecuteNonQuery();
            using var tx = connection.BeginTransaction();
            if (version >= 2) ArchiveSql.MigrateSearchSchema(connection, tx, CancellationToken.None);
            if (version >= 3) ArchiveSql.CreateSourceContractSchema(connection, tx);
            // Same compatible synthetic v4/v5 superset as L1, no historical provenance claim.
            if (version >= 4) ArchiveSql.CreateIntegritySchema(connection, tx);
            if (version >= 6)
            {
                ArchiveSql.CreateNativeImportSchema(connection, tx);
                using var instance = ArchiveSql.Command(connection, tx, "UPDATE archive_import_fence SET instance=$instance");
                instance.Parameters.AddWithValue("$instance", Instance);
                instance.ExecuteNonQuery();
            }
            if (version == 9)
                Schema9MediaOperationsFixture.CreateSchema9(connection, tx);
            else
            {
                if (version >= 7) ArchiveSql.CreateMediaImportSchema(connection, tx);
                if (version >= 8) ArchiveSql.CreateMediaMergeSchema(connection, tx);
            }
            if (version >= 9) ArchiveSql.CreateLocalCollectionSchema(connection, tx);
            using var stamp = ArchiveSql.Command(connection, tx, $"PRAGMA user_version={version}");
            stamp.ExecuteNonQuery();
            tx.Commit();
        }
        internal void InsertReceipt(LocalCollectionImportReceipt receipt)
        {
            using var connection = ArchiveSql.OpenConnection(Database, SqliteOpenMode.ReadWrite);
            using var tx = connection.BeginTransaction();
            using var command = ArchiveSql.Command(connection, tx, """
                INSERT INTO local_import_receipts(operation_id,request_sha256,catalog_instance,collection_id,receipt_json)
                VALUES($operation,$hash,$instance,$collection,$json)
                """);
            command.Parameters.AddWithValue("$operation", receipt.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$hash", receipt.RequestSha256);
            command.Parameters.AddWithValue("$instance", receipt.CatalogInstance);
            command.Parameters.AddWithValue("$collection", receipt.CollectionId.ToString());
            command.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(Codec.EncodeReceipt(receipt)));
            command.ExecuteNonQuery();
            tx.Commit();
        }
        internal void ReplaceJson(string json, bool ignoreChecks = false)
        {
            using var connection = ArchiveSql.OpenConnection(Database, SqliteOpenMode.ReadWrite);
            using var tx = connection.BeginTransaction();
            using var command = ArchiveSql.Command(connection, tx,
                (ignoreChecks ? "PRAGMA ignore_check_constraints=ON; " : "") + "UPDATE local_import_receipts SET receipt_json=$json");
            command.Parameters.AddWithValue("$json", json);
            command.ExecuteNonQuery();
            tx.Commit();
        }
        internal void Execute(string sql)
        {
            using var connection = ArchiveSql.OpenConnection(Database, SqliteOpenMode.ReadWrite);
            using var tx = connection.BeginTransaction();
            using var command = ArchiveSql.Command(connection, tx, sql);
            command.ExecuteNonQuery();
            tx.Commit();
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
