using System.Text;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Tests.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// R0 contract tests authored against wave18-local-collection-l2-cut-sol.md §3.
// No product shim, profile lease, host, media files or import producer.
public sealed class LocalCollectionReceiptAdversarialTests
{
    private const string Instance = "10203040-5060-7080-90a0-b0c0d0e0f001";
    private const string Collection = "20304050-6070-8090-a0b0-c0d0e0f00102";
    private const string Item = "30405060-7080-90a0-b0c0-d0e0f0010203";
    private const string Revision = "40506070-8090-a0b0-c0d0-e0f001020304";
    private const string Operation = "50607080-90a0-b0c0-d0e0-f00102030405";
    private const string Other = "60708090-a0b0-c0d0-e0f0-010203040506";
    private static readonly string Digest = new('a', 64);
    private static readonly string OtherDigest = new('b', 64);
    private static LocalImportReceiptKey Key => new(Instance, Guid.Parse(Operation), Digest);
    private static LocalImportRecoveryKey RecoveryKey => new(Instance, Guid.Parse(Operation));

    [Fact]
    public async Task LegacyEightReplacementWithSameReadableFenceNeverProvesAbsence()
    {
        using var db = new Database();
        db.InsertReceipt();
        await using var reader = await ArchiveReader.OpenAsync(db.Directory);
        // No connection survives OpenAsync. Replace the DB at the same trusted path,
        // retaining even the expected instance: a UUID match is NOT schema compatibility.
        File.Delete(db.Path);
        db.Seed(8);
        using (var read = db.Open(SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(8, ArchiveSql.GetPragma(read, "user_version"));
            Assert.Equal(Instance, ArchiveSql.ReadImportFence(read, null).Instance);
            using var tx = read.BeginTransaction(deferred: true);
            AssertCode(() => ArchiveSql.RequireLocalTarget(read, tx, Instance), LocalImportError.TargetReplaced);
            AssertCode(() => ArchiveSql.ReadLocalImportReceiptForRecovery(read, tx, RecoveryKey,
                new LocalImportCodec(), CancellationToken.None), LocalImportError.TargetReplaced);
        }
        byte[] before = File.ReadAllBytes(db.Path);
        await AssertCodeAsync(() => reader.ReadLocalImportReceiptAsync(Key, CancellationToken.None), LocalImportError.TargetReplaced);
        await AssertCodeAsync(() => Lookup(db), LocalImportError.TargetReplaced);
        Assert.Equal(before, File.ReadAllBytes(db.Path));
    }

    [Theory]
    [InlineData("PRAGMA application_id=123")]
    [InlineData("PRAGMA user_version=11")] // Malformed legacy schema restamped as current.
    [InlineData("PRAGMA user_version=12")]
    [InlineData("UPDATE archive_import_fence SET instance='60708090-a0b0-c0d0-e0f0-010203040506'")]
    public async Task IncompatibleOrForeignTargetIsNeverReportedAsReceiptAbsence(string mutation)
    {
        using var db = new Database();
        // Deliberately no Receiptrow: wrong target must win over an empty table.
        db.Execute(mutation);
        byte[] before = File.ReadAllBytes(db.Path);
        await AssertCodeAsync(() => Lookup(db), LocalImportError.TargetReplaced);
        using (var read = db.Open(SqliteOpenMode.ReadOnly))
        using (var tx = read.BeginTransaction(deferred: true))
        {
            AssertCode(() => ArchiveSql.RequireLocalTarget(read, tx, Instance), LocalImportError.TargetReplaced);
            AssertCode(() => ArchiveSql.ReadLocalImportReceiptForRecovery(read, tx, RecoveryKey,
                new LocalImportCodec(), CancellationToken.None), LocalImportError.TargetReplaced);
        }
        Assert.Equal(before, File.ReadAllBytes(db.Path));
    }

    [Fact]
    public async Task DifferentExpectedDigestIsConflictNotAnEmptyLookupAndRecoveryKeepsStoredDigest()
    {
        using var db = new Database();
        db.InsertReceipt();
        var wrong = Key with { RequestSha256 = OtherDigest };
        await AssertCodeAsync(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(db.Path, wrong, CancellationToken.None),
            LocalImportError.OperationConflict);
        using var read = db.Open(SqliteOpenMode.ReadOnly);
        using var tx = read.BeginTransaction(deferred: true);
        AssertCode(() => ArchiveSql.ReadLocalImportReceipt(read, tx, wrong, new LocalImportCodec(), CancellationToken.None),
            LocalImportError.OperationConflict);
        var receipt = ArchiveSql.ReadLocalImportReceiptForRecovery(read, tx, RecoveryKey, new LocalImportCodec(), CancellationToken.None);
        Assert.NotNull(receipt);
        Assert.Equal(Digest, receipt.RequestSha256);
    }

    [Theory]
    [InlineData("collection_id")]
    [InlineData("catalog_instance")]
    [InlineData("request_sha256")]
    public async Task CanonicalButContradictoryRowHeadersCannotBeIgnored(string field)
    {
        using var db = new Database();
        db.InsertReceipt();
        // All mutated headers remain individually canonical and satisfy the actual DDL.
        db.Execute($"UPDATE local_import_receipts SET {field}=$value", field == "request_sha256" ? OtherDigest : Other);
        await RejectBothLookupModes(db, LocalImportError.IntegrityFailure);
    }

    [Fact]
    public async Task JsonOperationMismatchIsNotAbsenceOfTheRequestedOperation()
    {
        using var db = new Database();
        db.InsertReceipt(ReceiptJson().Replace(Operation, Other, StringComparison.Ordinal));
        await RejectBothLookupModes(db, LocalImportError.IntegrityFailure);
    }

    [Fact]
    public async Task OversizedStoredReceiptIsBudgetFailureWithoutTruncationOrRepair()
    {
        using var db = new Database();
        // The same valid JSON plus whitespace is intrinsically parseable but one byte over cap.
        string json = ReceiptJson();
        string oversized = json + new string(' ', LocalImportBounds.MaximumReceiptBytes + 1 - Encoding.UTF8.GetByteCount(json));
        db.InsertReceipt(oversized, ignoreChecks: true);
        using (var read = db.Open(SqliteOpenMode.ReadOnly))
            Assert.Equal((long)LocalImportBounds.MaximumReceiptBytes + 1,
                Scalar(read, "SELECT length(CAST(receipt_json AS BLOB)) FROM local_import_receipts"));
        await RejectBothLookupModes(db, LocalImportError.BudgetExceeded);
    }

    [Fact]
    public async Task InvalidRawUtf8InStoredReceiptTextFailsClosed()
    {
        using var db = new Database();
        // Keep the complete valid receipt shape, corrupt only one raw byte in its
        // timestamp string. This exercises stored TEXT bytes, not .NET surrogate input.
        // Timestamp syntax is also strict: this case asserts fail-closed behavior,
        // not that this receipt format admits a valid replacement-character string.
        string json = ReceiptJson();
        byte[] raw = Encoding.UTF8.GetBytes(json);
        int timestamp = json.IndexOf("2026-11-05", StringComparison.Ordinal);
        raw[timestamp] = 0xff;
        db.InsertRawReceipt(raw);
        using (var read = db.Open(SqliteOpenMode.ReadOnly))
            Assert.Contains("FF", Text(read, "SELECT hex(CAST(receipt_json AS BLOB)) FROM local_import_receipts"));
        await RejectBothLookupModes(db, LocalImportError.InvalidSelection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlongOrInvalidRawUtf8HeaderIsCorruptDataNotAStringCoercion(bool invalidUtf8)
    {
        using var db = new Database();
        db.InsertReceipt();
        using (var write = db.Open(SqliteOpenMode.ReadWrite))
        {
            Execute(write, "PRAGMA ignore_check_constraints=ON");
            if (invalidUtf8)
            {
                byte[] raw = Encoding.ASCII.GetBytes(Collection);
                raw[0] = 0xff;
                using var command = write.CreateCommand();
                command.CommandText = "UPDATE local_import_receipts SET collection_id=CAST($raw AS TEXT)";
                command.Parameters.AddWithValue("$raw", raw);
                command.ExecuteNonQuery();
            }
            else
            {
                using var command = write.CreateCommand();
                command.CommandText = "UPDATE local_import_receipts SET collection_id=$text";
                command.Parameters.AddWithValue("$text", Collection + new string('x', 4096));
                command.ExecuteNonQuery();
            }
            Execute(write, "PRAGMA ignore_check_constraints=OFF");
        }
        // Malformed redundant headers are structural Receiptrow contradictions, not target absence.
        // UUID headers have a fixed 36-byte form; overlength is also IntegrityFailure.
        // Only the separate variable-size receipt JSON budget uses BudgetExceeded.
        await RejectBothLookupModes(db, LocalImportError.IntegrityFailure);
    }

    [Fact]
    public async Task HistoricalReceiptSurvivesMissingOperationsAndContentAndMaximumCurrentGeneration()
    {
        using var db = new Database();
        db.InsertReceipt();
        db.Execute("UPDATE archive_import_fence SET generation=9223372036854775807");
        using (var read = db.Open(SqliteOpenMode.ReadOnly))
            foreach (string table in new[] { "local_import_operations", "local_collections", "local_items", "local_media_revisions" })
                Assert.Equal(0L, Scalar(read, $"SELECT count(*) FROM {table}"));
        byte[] before = File.ReadAllBytes(db.Path);
        var receipt = await Lookup(db);
        AssertHistorical(receipt);
        using (var read = db.Open(SqliteOpenMode.ReadOnly))
        using (var tx = read.BeginTransaction(deferred: true))
        {
            Assert.Equal(long.MaxValue, ArchiveSql.RequireLocalTarget(read, tx, Instance).Generation);
            AssertHistorical(ArchiveSql.ReadLocalImportReceiptForRecovery(read, tx, RecoveryKey,
                new LocalImportCodec(), CancellationToken.None));
        }
        Assert.Equal(before, File.ReadAllBytes(db.Path));
    }

    private static void AssertHistorical(LocalCollectionImportReceipt? receipt)
    {
        Assert.NotNull(receipt);
        Assert.Equal(Digest, receipt.RequestSha256);
        Assert.Equal(Guid.Parse(Operation), receipt.OperationId);
        Assert.Equal(Guid.Parse(Collection), receipt.CollectionId.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture), receipt.RegisteredAtUtc);
        var revision = Assert.Single(receipt.Revisions);
        Assert.Equal(Guid.Parse(Item), revision.Reference.ItemId.Value);
        Assert.Equal(Guid.Parse(Revision), revision.Reference.RevisionId.Value);
        Assert.Equal(12345678901L, revision.Length);
    }

    private static async Task RejectBothLookupModes(Database db, LocalImportError expected)
    {
        byte[] before = File.ReadAllBytes(db.Path);
        await AssertCodeAsync(() => Lookup(db), expected);
        using (var read = db.Open(SqliteOpenMode.ReadOnly))
        using (var tx = read.BeginTransaction(deferred: true))
        {
            AssertCode(() => ArchiveSql.ReadLocalImportReceipt(read, tx, Key, new LocalImportCodec(), CancellationToken.None), expected);
            AssertCode(() => ArchiveSql.ReadLocalImportReceiptForRecovery(read, tx, RecoveryKey, new LocalImportCodec(), CancellationToken.None), expected);
        }
        Assert.Equal(before, File.ReadAllBytes(db.Path));
    }
    private static Task<LocalCollectionImportReceipt?> Lookup(Database db)
        => ArchiveReader.ReadLocalImportReceiptAtPathAsync(db.Path, Key, CancellationToken.None);
    private static void AssertCode(Action action, LocalImportError expected)
        => AssertDiagnostic(Assert.Throws<LocalImportReadException>(action), expected);
    private static async Task AssertCodeAsync(Func<Task<LocalCollectionImportReceipt?>> action, LocalImportError expected)
        => AssertDiagnostic(await Assert.ThrowsAsync<LocalImportReadException>(async () => { await action(); }), expected);
    private static void AssertDiagnostic(LocalImportReadException error, LocalImportError expected)
    {
        Assert.Equal(expected, error.Error);
        Assert.Equal("Local import read failed: " + expected, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("MARKER", error.ToString());
    }

    private static string ReceiptJson() => $$"""
        {"version":1,"catalogInstance":"{{Instance}}","operationId":"{{Operation}}","requestSha256":"{{Digest}}","collectionId":"{{Collection}}","collectionRevision":1,"registeredAtUtc":"2026-11-05T00:00:00.0000000+00:00","revisions":[{"itemId":"{{Item}}","revisionId":"{{Revision}}","ordinal":0,"role":1,"length":12345678901,"sha256":"{{Digest}}"}]}
        """;
    private static void Execute(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    { using var command = ArchiveSql.Command(c, tx, sql); command.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection c, string sql)
    { using var command = c.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static string Text(SqliteConnection c, string sql)
    { using var command = c.CreateCommand(); command.CommandText = sql; return (string)command.ExecuteScalar()!; }

    private sealed class Database : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ochan-receipt-adversarial-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "archive.db");
        internal SqliteConnection Open(SqliteOpenMode mode) => ArchiveSql.OpenConnection(Path, mode);
        internal Database()
        {
            System.IO.Directory.CreateDirectory(Directory);
            try { Seed(9); }
            catch { System.IO.Directory.Delete(Directory, true); throw; }
        }
        internal void Seed(int version)
        {
            using var c = Open(SqliteOpenMode.ReadWriteCreate);
            using var fixture = typeof(LocalCollectionReceiptAdversarialTests).Assembly
                .GetManifestResourceStream("MeowChan.Tests.Fixtures.archive-schema-1.sql")!;
            using var text = new StreamReader(fixture);
            LocalCollectionReceiptAdversarialTests.Execute(c, text.ReadToEnd());
            using var tx = c.BeginTransaction();
            ArchiveSql.MigrateSearchSchema(c, tx, CancellationToken.None);
            ArchiveSql.CreateSourceContractSchema(c, tx);
            ArchiveSql.CreateIntegritySchema(c, tx);
            ArchiveSql.CreateNativeImportSchema(c, tx);
            // Frozen legacy media tables, never a current schema with an old stamp.
            Schema9MediaOperationsFixture.CreateSchema9(c, tx);
            if (version == 9) ArchiveSql.CreateLocalCollectionSchema(c, tx);
            LocalCollectionReceiptAdversarialTests.Execute(c, $"UPDATE archive_import_fence SET instance='{Instance}'; PRAGMA user_version={version}", tx);
            ArchiveSql.ValidateSchema(c, tx);
            tx.Commit();
        }
        internal void Execute(string sql, string? value = null)
        {
            using var c = Open(SqliteOpenMode.ReadWrite);
            using var command = c.CreateCommand(); command.CommandText = sql;
            if (value != null) command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
        internal void InsertReceipt(string? json = null, bool ignoreChecks = false)
        {
            using var c = Open(SqliteOpenMode.ReadWrite);
            if (ignoreChecks) LocalCollectionReceiptAdversarialTests.Execute(c, "PRAGMA ignore_check_constraints=ON");
            Insert(c, json ?? ReceiptJson(), false);
        }
        internal void InsertRawReceipt(byte[] bytes)
        { using var c = Open(SqliteOpenMode.ReadWrite); Insert(c, bytes, true); }
        private static void Insert(SqliteConnection c, object json, bool raw)
        {
            using var command = c.CreateCommand();
            command.CommandText = "INSERT INTO local_import_receipts(operation_id,request_sha256,catalog_instance,collection_id,receipt_json) "
                + "VALUES($op,$digest,$instance,$collection," + (raw ? "CAST($json AS TEXT)" : "$json") + ")";
            command.Parameters.AddWithValue("$op", Operation); command.Parameters.AddWithValue("$digest", Digest);
            command.Parameters.AddWithValue("$instance", Instance); command.Parameters.AddWithValue("$collection", Collection);
            command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
