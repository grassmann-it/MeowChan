using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Deliberately separate from the content merge/restart/FTS suite. No providers or real media.
public sealed class NativeTextImportFenceTests
{
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Url = "https://boards.4chan.org/b/thread/7001";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureInvalidatesPreparedPlanIncludingUnrelatedThread(bool unrelated)
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await catalog.CaptureAsync(Capture(profile.Path));
        NativeTextImportPlan plan = await Prepare(catalog);
        await catalog.CaptureAsync(Capture(profile.Path) with
        {
            Id = unrelated ? new("4chan", "b", "9001") : Id,
            Url = unrelated ? "https://boards.4chan.org/b/thread/9001" : Url,
            Title = "intervening capture", CapturedAt = At.AddMinutes(1)
        });
        await AssertStaleWithoutMutation(profile.Path, catalog, plan);
    }

    [Fact]
    public async Task RevisionlessRegistrationInvalidatesPreparedPlan()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        ArchiveThreadRegistration registration = new(Id, Url, "before", Path.Combine(profile.Path, "inert"), false);
        ArchiveThreadSummary first = await catalog.RegisterThreadAsync(registration);
        NativeTextImportPlan plan = await Prepare(catalog);
        ArchiveThreadSummary changed = await catalog.RegisterThreadAsync(registration with { MediaDirectory = Path.Combine(profile.Path, "inert-reregistered") });
        Assert.Equal(first.Revision, changed.Revision); // Tripwire: revision-only freshness is insufficient.
        Assert.NotEqual(first.MediaDirectory, changed.MediaDirectory);
        await AssertStaleWithoutMutation(profile.Path, catalog, plan);
    }

    [Fact]
    public async Task DeleteAndIdenticalRecreateDoesNotResurrectOldPlan()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        CapturedThreadSnapshot seed = Capture(profile.Path);
        await catalog.CaptureAsync(seed);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView before = (await reader.ReadThreadAsync(Id))!;
        NativeTextImportPlan plan = await Prepare(catalog);
        Assert.Single(await catalog.DeleteThreadsAsync([Id]));
        await catalog.CaptureAsync(seed);
        ArchivedThreadView recreated = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(before.Summary.Revision, recreated.Summary.Revision);
        Assert.Equal(before.Summary.Title, recreated.Summary.Title);
        Assert.Equal(before.Posts.Select(p => p.Html), recreated.Posts.Select(p => p.Html));
        await AssertStaleWithoutMutation(profile.Path, catalog, plan);
    }

    [Fact]
    public async Task EqualContentsInAnotherCatalogNeverAuthorizeForeignPlan()
    {
        using TempDirectory left = new(); using TempDirectory right = new();
        using ProfileLease leftLease = ProfileLease.Acquire(left.Path);
        using ProfileLease rightLease = ProfileLease.Acquire(right.Path);
        await using ArchiveCatalog a = await ArchiveCatalog.OpenAsync(left.Path, leftLease);
        await using ArchiveCatalog b = await ArchiveCatalog.OpenAsync(right.Path, rightLease);
        // Both empty, with equal generations, but distinct durable identities and owners.
        NativeTextImportPlan plan = await Prepare(a);
        string sourceBefore = Snapshot(a.DatabasePath);
        await AssertStaleWithoutMutation(right.Path, b, plan);
        Assert.Equal(sourceBefore, Snapshot(a.DatabasePath));
        NativeTextImportCommitResult accepted = await a.CommitNativeTextImportAsync(plan);
        Assert.Equal(NativeTextImportCommitStatus.Committed, accepted.Status);
        Assert.Equal(1, accepted.NewThreads); // Foreign rejection must not consume the owner's plan.
    }

    [Fact]
    public async Task AlreadyCanceledCommitLeavesEveryCatalogTableAndDirectoryUnchanged()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await catalog.CaptureAsync(Capture(profile.Path));
        NativeTextImportPlan plan = await Prepare(catalog);
        string before = Snapshot(catalog.DatabasePath);
        string[] directories = Directory.GetDirectories(profile.Path, "*", SearchOption.AllDirectories);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.CommitNativeTextImportAsync(plan, cancellation.Token));
        Assert.Equal(before, Snapshot(catalog.DatabasePath));
        Assert.Equal(directories, Directory.GetDirectories(profile.Path, "*", SearchOption.AllDirectories));
        await AssertNoIncomingPost(profile.Path);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("attachment")]
    [InlineData("payload")]
    [InlineData("retained")]
    [InlineData("nullAttachments")]
    public async Task InvalidOrMediaPackageRejectsWholeSelectionWithoutMutation(string mode)
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await catalog.CaptureAsync(Capture(profile.Path));
        TransferThread clean = Incoming();
        TransferThread second = Incoming() with { Id = new("4chan", "b", "7002"), PageUrl = "https://boards.4chan.org/b/thread/7002" };
        CapturedAttachment metadata = new(0, "7002001", Url: "https://i.4cdn.org/b/7002001.jpg", OriginalFileName: "synthetic.jpg");
        metadata = metadata with { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(second.Id, "7002", 0,
            new("7002001", "original", metadata.Url!, metadata.OriginalFileName, null, null, null)) };
        TransferAttachment media = new(metadata, TransferMediaRole.Original, TransferContentStatus.Omitted, null, null);
        byte[] payload = [1, 3, 7, 9];
        string payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (mode == "payload") media = media with
        {
            Status = TransferContentStatus.Contained, PayloadSha256 = payloadHash,
            History = new("strict", At, false, false)
        };
        if (mode is "attachment" or "payload") second = second with { Posts = [new(new("7002", 0, PostFieldPresence.Attachments, Attachments: [metadata]), [media])] };
        if (mode == "retained") second = second with { RetainedBindings = [new("7002", media)] };
        if (mode == "nullAttachments") second = second with { Posts = [new(new("7002", 0, PostFieldPresence.Attachments), []) { AttachmentsWereNull = true }] };
        TransferPayloadSource[] sources = mode == "payload"
            ? [new(new(payload.Length, payloadHash), _ => Task.FromResult<Stream>(new MemoryStream(payload, false)))] : [];
        using MemoryStream package = await Package(new(At, [clean, second]), sources);
        if (mode == "malformed") package.SetLength(12); // Corrupt a real writer product, not a parser double.
        package.Position = 0;
        string before = Snapshot(catalog.DatabasePath);
        NativeTextImportPreparation rejected = await catalog.PrepareNativeTextImportAsync(package);
        Assert.Null(rejected.Plan);
        Assert.Equal(mode == "malformed" ? NativeTextImportError.InvalidPackage : NativeTextImportError.MediaNotSupported, rejected.Error);
        Assert.Equal(before, Snapshot(catalog.DatabasePath));
        await AssertNoIncomingPost(profile.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        Assert.Null(await reader.ReadThreadAsync(second.Id));
        Assert.Empty(Directory.GetDirectories(profile.Path));
    }

    [Fact]
    public async Task SchemaFiveUpgradeIsAdditiveAndCreatesUsableFence()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await CreateSchemaFive(profile.Path, lease);
        string db = Path.Combine(profile.Path, "archive.db");
        string before = Snapshot(db);
        await using ArchiveCatalog upgraded = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        Assert.Equal(9L, Scalar(db, "PRAGMA user_version"));
        Assert.Equal(before, Snapshot(db, excludeImport: true));
        Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM archive_import_fence"));
        NativeTextImportPlan plan = await Prepare(upgraded);
        await upgraded.RegisterThreadAsync(new(Id, Url, "migration intervening registration", Path.Combine(profile.Path, "inert"), false));
        await AssertStaleWithoutMutation(profile.Path, upgraded, plan);
    }

    [Fact]
    public async Task FailedSchemaFiveMigrationRollsBackVersionTablesAndSeed()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await CreateSchemaFive(profile.Path, lease);
        string db = Path.Combine(profile.Path, "archive.db");
        // A colliding malformed table fails seed insertion AFTER candidate DDL was attempted.
        Execute(db, "CREATE TABLE archive_import_fence(sentinel TEXT); INSERT INTO archive_import_fence VALUES('keep');");
        string before = Snapshot(db);
        await Assert.ThrowsAsync<SqliteException>(async () => { await using ArchiveCatalog ignored = await ArchiveCatalog.OpenAsync(profile.Path, lease); });
        Assert.Equal(5L, Scalar(db, "PRAGMA user_version"));
        Assert.Equal(before, Snapshot(db));
        Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name IN ('archive_import_candidates','archive_import_receipts')"));
        Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'import_fence_%'"));
        await AssertNoIncomingPost(profile.Path);
    }

    [Fact]
    public async Task SchemaSixWithMissingFenceTriggerFailsClosedInsteadOfRepairingSilently()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using (ArchiveCatalog seed = await ArchiveCatalog.OpenAsync(profile.Path, lease))
            await seed.CaptureAsync(Capture(profile.Path));
        string db = Path.Combine(profile.Path, "archive.db");
        Execute(db, "DROP TRIGGER import_fence_archive_threads_UPDATE");
        string before = Snapshot(db);
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await using ArchiveCatalog ignored = await ArchiveCatalog.OpenAsync(profile.Path, lease); });
        Assert.Equal(before, Snapshot(db));
        Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name='import_fence_archive_threads_UPDATE'"));
    }

    [Fact]
    public async Task ExactReplayPrecedesStaleFenceAndReconcilesAfterDeleteRecreateAndReopen()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        NativeTextImportPlan plan;
        NativeTextImportCommitResult committed;
        string beforeReopen;
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease))
        {
            plan = await Prepare(catalog);
            Assert.Null(await catalog.ReadNativeTextImportReceiptAsync(plan.OperationId, plan.RequestSha256));
            committed = await catalog.CommitNativeTextImportAsync(plan);
            Assert.Equal(NativeTextImportCommitStatus.Committed, committed.Status);
            Assert.False(committed.IsReplay);
            Assert.Equal(1, committed.NewThreads); Assert.Equal(1, committed.NewPosts);
            Assert.Equal(0, committed.UpdatedPosts); Assert.Equal(0, committed.ConflictingThreads);
            Assert.Single(await catalog.DeleteThreadsAsync([Id]));
            // Recreate the exact imported observation, including the originally imported post.
            // This must be historical replay, not another merge or candidate insertion.
            await catalog.CaptureAsync(Capture(profile.Path) with
            {
                Title = Incoming().Title, DecimalPostOrder = false,
                Posts = Incoming().Posts.Select(p => p.Metadata).ToArray()
            });
            string beforeReplay = Snapshot(catalog.DatabasePath);
            NativeTextImportCommitResult replay = await catalog.CommitNativeTextImportAsync(plan);
            Assert.Equal(committed with { IsReplay = true }, replay);
            Assert.Equal(beforeReplay, Snapshot(catalog.DatabasePath));
            Assert.Empty(await catalog.ReadNativeTextImportCandidatesAsync(Id));
            beforeReopen = Snapshot(catalog.DatabasePath);
        }
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        NativeTextImportCommitResult? receipt = await reopened.ReadNativeTextImportReceiptAsync(plan.OperationId, plan.RequestSha256);
        Assert.Equal(committed with { IsReplay = true }, receipt);
        Assert.Null(await reopened.ReadNativeTextImportReceiptAsync(Guid.NewGuid(), plan.RequestSha256));
        string wrongHash = (plan.RequestSha256[0] == '0' ? "1" : "0") + plan.RequestSha256[1..];
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ReadNativeTextImportReceiptAsync(plan.OperationId, wrongHash));
        Assert.Equal(beforeReopen, Snapshot(reopened.DatabasePath));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        Assert.Single((await reader.ReadThreadAsync(Id))!.Posts);
    }

    private static CapturedThreadSnapshot Capture(string profile) => new(Id, Url, "seed", ThreadSourceState.NotFound,
        At, Path.Combine(profile, "inert"), [new("7001", 0, PostFieldPresence.Html, Html: "<p>seed</p>")]);

    private static TransferThread Incoming() => new(Id, Url, "incoming", ThreadSourceState.NotFound, At, null, null,
        false, [new(new("7003", 1, PostFieldPresence.Html, Html: "<p>incoming fence sentinel</p>"), [])]);

    private static async Task<MemoryStream> Package(NativeTransferCatalog content, TransferPayloadSource[]? payloads = null)
    {
        MemoryStream stream = new();
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stream, content, payloads ?? [], Guid.NewGuid());
        string diagnostic = $"Writer Error={result.Error}";
        if (!result.IsComplete && stream.Length > 0)
        {
            stream.Position = 0;
            TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(stream);
            diagnostic += $"; EnvelopeError={inspection.EnvelopeError}; CatalogError={inspection.CatalogError}";
        }
        Assert.True(result.IsComplete, diagnostic);
        stream.Position = 0;
        return stream;
    }

    private static async Task<NativeTextImportPlan> Prepare(ArchiveCatalog catalog)
    {
        using MemoryStream stream = await Package(new(At, [Incoming()]));
        NativeTextImportPreparation prepared = await catalog.PrepareNativeTextImportAsync(stream);
        Assert.Equal(NativeTextImportError.None, prepared.Error);
        return Assert.IsType<NativeTextImportPlan>(prepared.Plan);
    }

    private static async Task AssertStaleWithoutMutation(string profile, ArchiveCatalog catalog, NativeTextImportPlan plan)
    {
        string before = Snapshot(catalog.DatabasePath);
        NativeTextImportCommitResult rejected = await catalog.CommitNativeTextImportAsync(plan);
        Assert.Equal(NativeTextImportCommitStatus.StalePlan, rejected.Status);
        Assert.Equal(0, rejected.NewThreads); Assert.Equal(0, rejected.NewPosts);
        Assert.Equal(0, rejected.UpdatedPosts); Assert.Equal(0, rejected.ConflictingThreads);
        Assert.Equal(before, Snapshot(catalog.DatabasePath));
        await AssertNoIncomingPost(profile);
    }

    private static async Task AssertNoIncomingPost(string profile)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchivedThreadView? view = await reader.ReadThreadAsync(Id);
        if (view != null) Assert.DoesNotContain(view.Posts, p => p.PostId == "7003");
    }

    private static async Task CreateSchemaFive(string profile, ProfileLease lease)
    {
        await using (ArchiveCatalog seed = await ArchiveCatalog.OpenAsync(profile, lease))
            await seed.CaptureAsync(Capture(profile));
        string db = Path.Combine(profile, "archive.db");
        using SqliteConnection connection = Open(db);
        List<string> objects = new();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'import_fence_%'";
            using SqliteDataReader rows = command.ExecuteReader();
            while (rows.Read()) objects.Add(rows.GetString(0));
        }
        foreach (string name in objects) Execute(db, "DROP TRIGGER " + Quote(name));
        // Schema-shape fixture: retain content/search/decisions, remove native text and merge additions.
        Execute(db, "DROP TABLE IF EXISTS archive_import_receipts; DROP TABLE archive_import_candidates; DROP TABLE archive_import_fence; DROP TABLE IF EXISTS archive_media_merge_receipts; DROP TABLE IF EXISTS archive_media_merge_operations; PRAGMA user_version=5;");
    }

    private static SqliteConnection Open(string db) => ArchiveSql.OpenConnection(db, SqliteOpenMode.ReadWrite);
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static void Execute(string db, string sql)
    {
        using SqliteConnection connection = Open(db); using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Scalar(string db, string sql)
    {
        using SqliteConnection connection = Open(db); using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // Logical database snapshot includes fence, candidates, receipts, quotes and search shadow rows.
    // Sorted encoded rows avoid incidental SQLite row order and include BLOB bytes without truncation.
    private static string Snapshot(string db, bool excludeImport = false)
    {
        using SqliteConnection connection = Open(db);
        List<string> names = new();
        using (SqliteCommand tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using SqliteDataReader rows = tables.ExecuteReader();
            while (rows.Read())
                if (!excludeImport || (!rows.GetString(0).StartsWith("archive_import_", StringComparison.Ordinal) && !rows.GetString(0).StartsWith("archive_media_merge_", StringComparison.Ordinal))) names.Add(rows.GetString(0));
        }
        List<string> snapshot = new();
        foreach (string name in names)
        {
            using SqliteCommand command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + Quote(name);
            using SqliteDataReader rows = command.ExecuteReader(); List<string> values = new();
            while (rows.Read())
                values.Add(JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount).Select(i => rows.IsDBNull(i) ? null :
                    rows.GetValue(i) is byte[] bytes ? "blob:" + Convert.ToBase64String(bytes) :
                    Convert.ToString(rows.GetValue(i), CultureInfo.InvariantCulture)).ToArray()));
            values.Sort(StringComparer.Ordinal); snapshot.Add(name + "\n" + string.Join("\n", values));
        }
        return string.Join("\n", snapshot);
    }
}
