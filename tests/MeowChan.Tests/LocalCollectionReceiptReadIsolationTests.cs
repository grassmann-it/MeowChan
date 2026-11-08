using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Tests.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// Distinct-path/lifetime isolation only. Same-path replacement, codec corruption and
// held SQL snapshots belong to LocalCollectionReaderTests/ReceiptAdversarialTests.
public sealed class LocalCollectionReceiptReadIsolationTests
{
    private const string Instance = "10203040-5060-7080-90a0-b0c0d0e0f001";
    private const string Foreign = "60708090-a0b0-c0d0-e0f0-010203040506";
    private const string Operation = "50607080-90a0-b0c0-d0e0-f00102030405";
    private const string Collection = "20304050-6070-8090-a0b0-c0d0e0f00102";
    private static readonly string Digest = new('a', 64);
    private static LocalImportReceiptKey Key => new(Instance, Guid.Parse(Operation), Digest);

    [Theory]
    [InlineData(9, 9)] [InlineData(9, 10)] [InlineData(10, 9)] [InlineData(10, 10)]
    public async Task AtPathSeesLaterCommitWhileOtherReaderRemainsAliveAndAbsent(int mainVersion, int targetVersion)
    {
        using var files = new Workspace();
        files.Seed(files.Main, mainVersion);
        files.Seed(files.Target, targetVersion);
        await using var main = await ArchiveReader.OpenAsync(files.MainDirectory);
        await using var targetReader = await ArchiveReader.OpenAsync(Path.GetDirectoryName(files.Target)!);
        Assert.Null(await targetReader.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        // Main DB and target have compatible v9/v10 schemas and matching instances, but independent contents.
        Assert.Null(await main.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        string[] beforeAbsent = files.Fingerprint();
        Assert.Null(await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Target, Key, CancellationToken.None));
        Assert.Equal(beforeAbsent, files.Fingerprint());

        // Explicit synthetic fixture writer commits AFTER the first lookup/reader-open;
        // no SQL read transaction is held across this rollback-journal commit.
        files.InsertReceipt(files.Target);
        string[] afterCommit = files.Fingerprint();
        Assert.NotEqual(string.Join("\n", beforeAbsent), string.Join("\n", afterCommit));
        AssertHistorical(await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Target, Key, CancellationToken.None));
        Assert.Null(await main.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        AssertHistorical(await targetReader.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        AssertHistorical(await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Target, Key, CancellationToken.None));
        Assert.Equal(afterCommit, files.Fingerprint());
    }

    [Theory]
    [InlineData(9)] [InlineData(10)]
    public async Task LiveLegacyReaderDoesNotImposeItsSchemaOnIndependentCompatibleReceipt(int targetVersion)
    {
        using var files = new Workspace();
        files.Seed(files.Main, 8);
        files.Seed(files.Target, targetVersion);
        files.InsertReceipt(files.Target);
        await using var legacy = await ArchiveReader.OpenAsync(files.MainDirectory);
        Assert.Empty((await legacy.ListThreadsAsync()).Items);
        string[] before = files.Fingerprint();
        // Target B succeeds even though the still-live reader A cannot perform a bound lookup.
        AssertHistorical(await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Target, Key, CancellationToken.None));
        await AssertReplaced(() => legacy.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        AssertHistorical(await ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Target, Key, CancellationToken.None));
        Assert.Equal(before, files.Fingerprint());
    }

    [Theory]
    [InlineData(9, "legacy8")] [InlineData(10, "legacy8")]
    [InlineData(9, "unknown11")] [InlineData(10, "unknown11")]
    [InlineData(9, "foreignInstance")] [InlineData(10, "foreignInstance")]
    public async Task LiveMatchingReaderCannotAuthorizeAnotherIncompatibleAtPathTarget(int mainVersion, string kind)
    {
        using var files = new Workspace();
        files.Seed(files.Main, mainVersion);
        files.InsertReceipt(files.Main);
        files.Seed(files.Target, kind == "legacy8" ? 8 : mainVersion);
        if (kind == "unknown11") files.Execute(files.Target, "PRAGMA user_version=11");
        if (kind == "foreignInstance") files.Execute(files.Target, $"UPDATE archive_import_fence SET instance='{Foreign}'");
        await using var main = await ArchiveReader.OpenAsync(files.MainDirectory);
        AssertHistorical(await main.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        string[] before = files.Fingerprint();
        // Empty B must not inherit A's valid instance/schema/historical receipt or return null.
        await AssertReplaced(() => ArchiveReader.ReadLocalImportReceiptAtPathAsync(files.Target, Key, CancellationToken.None));
        AssertHistorical(await main.ReadLocalImportReceiptAsync(Key, CancellationToken.None));
        Assert.Equal(before, files.Fingerprint());
    }

    private static void AssertHistorical(LocalCollectionImportReceipt? value)
    {
        var receipt = Assert.IsType<LocalCollectionImportReceipt>(value);
        Assert.Equal(Instance, receipt.CatalogInstance);
        Assert.Equal(Guid.Parse(Operation), receipt.OperationId);
        Assert.Equal(Digest, receipt.RequestSha256);
        Assert.Equal(Collection, receipt.CollectionId.ToString());
        var item = Assert.Single(receipt.Revisions);
        Assert.Equal(0, item.Ordinal);
        Assert.Equal(3L, item.Length);
        Assert.Equal(Digest, item.Sha256);
    }
    private static async Task AssertReplaced(Func<Task<LocalCollectionImportReceipt?>> lookup)
    {
        var error = await Assert.ThrowsAsync<LocalImportReadException>(lookup);
        Assert.Equal(LocalImportError.TargetReplaced, error.Error);
        Assert.Null(error.InnerException);
        Assert.Equal("Local import read failed: TargetReplaced", error.Message);
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ochan-receipt-isolation-" + Guid.NewGuid().ToString("N"));
        internal string MainDirectory => Path.Combine(root, "main");
        internal string Main => Path.Combine(MainDirectory, "archive.db");
        internal string Target => Path.Combine(root, "target", "archive.db");
        internal Workspace()
        {
            Directory.CreateDirectory(MainDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        }
        internal void Seed(string path, int version)
        {
            using var c = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
            using var fixture = typeof(LocalCollectionReceiptReadIsolationTests).Assembly.GetManifestResourceStream(
                "MeowChan.Tests.Fixtures.archive-schema-1.sql")!;
            using var text = new StreamReader(fixture);
            using (var schema = ArchiveSql.Command(c, null, text.ReadToEnd())) schema.ExecuteNonQuery();
            using var tx = c.BeginTransaction();
            ArchiveSql.MigrateSearchSchema(c, tx, CancellationToken.None);
            ArchiveSql.CreateSourceContractSchema(c, tx);
            ArchiveSql.CreateIntegritySchema(c, tx);
            ArchiveSql.CreateNativeImportSchema(c, tx);
            // Frozen legacy v9 tables, or the current schema10 tables, never a current
            // schema restamped as an older version.
            if (version == 10) ArchiveSql.CreateMediaTransferSchema10(c, tx);
            else if (version == 9) Schema9MediaOperationsFixture.CreateSchema9(c, tx);
            else
            {
                ArchiveSql.CreateMediaImportSchema(c, tx);
                if (version >= 8) ArchiveSql.CreateMediaMergeSchema(c, tx);
            }
            if (version >= 9) ArchiveSql.CreateLocalCollectionSchema(c, tx);
            using var stamp = ArchiveSql.Command(c, tx,
                $"UPDATE archive_import_fence SET instance='{Instance}'; PRAGMA user_version={version}");
            stamp.ExecuteNonQuery();
            ArchiveSql.ValidateSchema(c, tx);
            tx.Commit();
        }
        internal void InsertReceipt(string path)
        {
            // Only fixture writes use this connection. No operation/content/payload required.
            string json = $$"""
                {"version":1,"catalogInstance":"{{Instance}}","operationId":"{{Operation}}","requestSha256":"{{Digest}}","collectionId":"{{Collection}}","collectionRevision":1,"registeredAtUtc":"2026-11-05T00:00:00.0000000+00:00","revisions":[{"itemId":"30405060-7080-90a0-b0c0-d0e0f0010203","revisionId":"40506070-8090-a0b0-c0d0-e0f001020304","ordinal":0,"role":1,"length":3,"sha256":"{{Digest}}"}]}
                """;
            using var c = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);
            using var tx = c.BeginTransaction();
            using var insert = ArchiveSql.Command(c, tx,
                "INSERT INTO local_import_receipts VALUES($op,$hash,$instance,$collection,$json)");
            insert.Parameters.AddWithValue("$op", Operation);
            insert.Parameters.AddWithValue("$hash", Digest);
            insert.Parameters.AddWithValue("$instance", Instance);
            insert.Parameters.AddWithValue("$collection", Collection);
            insert.Parameters.AddWithValue("$json", json);
            insert.ExecuteNonQuery();
            tx.Commit();
        }
        internal void Execute(string path, string sql)
        {
            using var c = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);
            using var command = ArchiveSql.Command(c, null, sql);
            command.ExecuteNonQuery();
        }
        internal string[] Fingerprint()
        {
            // File bytes cover schema/stamps/fence/receipt; inventory covers persistent sidecars,
            // mkdir/lockfiles and unexpected artifacts. Access timestamps deliberately excluded.
            Assert.Equal(new[] { Main, Target }.OrderBy(p => p, StringComparer.Ordinal),
                Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal));
            return Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .Select(p => "dir:" + Path.GetRelativePath(root, p))
                .Concat(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(p =>
                    "file:" + Path.GetRelativePath(root, p) + ":" + File.GetLastWriteTimeUtc(p).Ticks + ":"
                    + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))))
                .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
