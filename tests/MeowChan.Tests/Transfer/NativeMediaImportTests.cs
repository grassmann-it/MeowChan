using System.Collections.Immutable;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// Real writer -> owned stage -> import coordinator -> fresh normal reader/resolver. The
// fixture has no provider, session, network or personal profile and never seeds imported rows.
public sealed class NativeMediaImportTests
{
    internal static readonly ResourceId Id = new("4chan", "b", "7001");
    internal const string Url = "https://boards.4chan.org/b/thread/7001";
    internal static readonly DateTimeOffset At = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    internal static readonly byte[] Bytes = Enumerable.Range(0, 150_123).Select(i => (byte)(i * 31 % 251)).ToArray();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompleteRealProducerSurvivesOwnerRestartWithVerifiedOriginalAndPassiveProvenance(bool legacy, bool mismatch)
    {
        using Workspace files = new();
        string fingerprint, request; Guid operation;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content(legacy, mismatch));
            fingerprint = stage.Package.Envelope.Fingerprint;
            var prepared = await catalog.PrepareNativeMediaImportAsync(stage);
            Assert.Equal(NativeMediaImportError.None, prepared.Error);
            NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
            operation = plan.OperationId; request = plan.RequestSha256;
            var committed = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore());
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
            Assert.False(committed.IsReplay);
        }
        Assert.Empty(Directory.GetFiles(files.StageRoot)); // Original package handle also gone.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.False(view.Summary.CaptureEnabled); Assert.Null(view.Summary.LastCapturedAt);
        Assert.Equal(ThreadSourceState.NotFound, view.Summary.SourceState);
        Assert.NotEqual(At, view.Summary.FirstSeenAt);
        Assert.False(view.DecimalPostOrder);
        Assert.Equal(new[] { "15", "16" }, view.Posts.Select(p => p.PostId));
        Assert.Single((await reader.SearchThreadsAsync(new("synthetic-media-import-needle"))).Items);
        Assert.Single((await reader.GetBacklinksAsync(new(Id, "15"))).Items);
        string root = Assert.Single(view.MediaDirectories);
        Assert.Equal(view.Summary.MediaDirectory, root);
        Assert.Contains(Path.DirectorySeparatorChar + "import-media-operations" + Path.DirectorySeparatorChar, root);
        CapturedPost post = view.Posts.Single(p => p.PostId == "15");
        CapturedAttachment attachment = Assert.Single(post.Attachments!);
        Assert.Null(attachment.PlannedPath);
        Assert.NotNull(attachment.AttachmentRevisionKey); // Local keyed binding, even for legacy input.
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(root);
        Assert.Equal(5, snapshot.ManifestVersion);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(
            new[] { snapshot }, Id, post.PostId, attachment));
        Assert.False(resolved.IsLegacy);
        MediaAttachmentBinding binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
        Assert.Equal(MediaAcceptanceKind.PassiveImport, binding.Acceptance.Kind);
        Assert.Null(binding.Acceptance.ProfileId); Assert.Null(binding.Acceptance.PolicyRevision);
        Assert.Null(binding.Acceptance.PluginOwnerId); Assert.Null(binding.Acceptance.SourceId);
        Assert.Equal(mismatch, binding.Acceptance.SizeMismatch); Assert.Equal(mismatch, binding.Acceptance.Md5Mismatch);
        Assert.Equal(fingerprint, binding.PassiveImport!.PackageFingerprint);
        Assert.Equal(operation.ToString("D"), binding.PassiveImport.OperationId);
        Assert.Equal(legacy ? null : Attachment().Metadata.AttachmentRevisionKey, binding.PassiveImport.IncomingRevisionKey);
        Assert.Null(binding.PassiveImport.History);
        await using (Stream verified = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId))
        {
            using MemoryStream actual = new(); await verified.CopyToAsync(actual);
            Assert.Equal(Bytes, actual.ToArray());
        }
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            var receipt = await reopened.ReadNativeMediaImportReceiptAsync(operation, request);
            Assert.NotNull(receipt); Assert.True(receipt.IsReplay);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, receipt.Status);
        }
    }

    [Fact]
    public async Task ReplayUsesHistoricalReceiptBeforeFilesAndDoesNotResurrectDeletedResource()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        NativeMediaImportPlan plan;
        await using (NativeTransferImportStage stage = await Stage(files, Content()))
        {
            plan = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
            Assert.Equal(NativeMediaImportCommitStatus.Committed, (await catalog.CommitNativeMediaImportAsync(plan, new MediaStore())).Status);
        }
        var deleted = Assert.Single(await catalog.DeleteThreadsAsync([Id]));
        // Deletion of generated synthetic payload is deliberately after a proven commit: its
        // absence must not demote history, trigger re-copy, or require the disposed input stage.
        foreach (string file in Directory.GetFiles(deleted.Summary.MediaDirectory, "*.original.bin")) File.Delete(file);
        Dictionary<string, string> before = Files(files.Profile);
        var replay = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Status); Assert.True(replay.IsReplay);
        Assert.Equal(before, Files(files.Profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Null(await reader.ReadThreadAsync(Id));
        string wrong = (plan.RequestSha256[0] == '0' ? "1" : "0") + plan.RequestSha256[1..];
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, wrong));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterveningRegistrationAndDeleteRecreateNeverPublishAnOldPlan(bool deleteAgain)
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
        await catalog.RegisterThreadAsync(new(Id, Url, "intervening", Path.Combine(files.Profile, "inert-existing"), false));
        if (deleteAgain) await catalog.DeleteThreadsAsync([Id]);
        Dictionary<string, string> before = Files(files.Profile);
        var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, result.Status);
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        var thread = await reader.ReadThreadAsync(Id);
        if (deleteAgain) Assert.Null(thread);
        else { Assert.Equal("intervening", thread!.Summary.Title); Assert.Empty(thread.Posts); }
        Assert.Equal(before, Files(files.Profile)); // Stale before Preparing should not even create an operation root.
    }

    [Fact]
    public async Task APlanFromAnotherLiveCatalogCannotAuthorizeThisOwner()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog owner = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using ArchiveCatalog other = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = (await owner.PrepareNativeMediaImportAsync(stage)).Plan!;
        Dictionary<string, string> before = Files(files.Profile);
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, (await other.CommitNativeMediaImportAsync(plan, new MediaStore())).Status);
        Assert.Equal(before, Files(files.Profile));
    }

    [Fact]
    public async Task ASecondPlanForAResourceWithAnUnresolvedOperationCannotCreateAnotherRoot()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        var first = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
        var second = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
        var interrupted = await catalog.CommitNativeMediaImportAsync(first, new MediaStore(), checkpoint: phase =>
        { if (phase == NativeMediaImportPhase.PreparingSaved) throw new IOException("synthetic interruption before mkdir"); });
        Assert.Equal(NativeMediaImportCommitStatus.Inactive, interrupted.Status);
        Assert.False(Directory.Exists(first.OperationsParent));
        Dictionary<string, string> before = Files(files.Profile);
        var rejected = await catalog.CommitNativeMediaImportAsync(second, new MediaStore());
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, rejected.Status);
        Assert.False(Directory.Exists(second.OperationsParent));
        Assert.Equal(before, Files(files.Profile));
        NativeMediaImportOperation operation = Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(first.OperationId, operation.OperationId);
    }

    [Theory]
    [InlineData("nullAttachments")]
    [InlineData("retained")]
    [InlineData("multiThread")]
    [InlineData("preview")]
    public async Task UnsupportedWholePackageIsRejectedBeforeAnyJournalOrFilesystemMutation(string kind)
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        NativeTransferCatalog content = Content();
        TransferThread thread = content.Threads[0];
        if (kind == "nullAttachments") thread = thread with { Posts = [thread.Posts[0],
            new(new("16", 1, PostFieldPresence.Attachments), []) { AttachmentsWereNull = true }] };
        if (kind == "retained")
        {
            TransferAttachment retained = Attachment(postId: "19");
            thread = thread with { RetainedBindings = [new("19", retained)] };
        }
        if (kind == "preview")
        {
            TransferAttachment preview = Attachment() with { Role = TransferMediaRole.Preview,
                Metadata = Attachment().Metadata with { AttachmentRevisionKey = null, Variant = "preview" } };
            thread = thread with { Posts = [new(new("15", 0, PostFieldPresence.Attachments, Attachments: [preview.Metadata]), [preview])] };
        }
        content = content with { Threads = kind == "multiThread" ? [thread, thread with
        { Id = new("4chan", "b", "7002"), PageUrl = "https://boards.4chan.org/b/thread/7002",
          Posts = [new(new("7002", 0, PostFieldPresence.Html, Html: "second"), [])] }] : [thread] };
        await using NativeTransferImportStage stage = await Stage(files, content);
        Dictionary<string, string> before = Files(files.Profile);
        var rejected = await catalog.PrepareNativeMediaImportAsync(stage);
        Assert.Equal(NativeMediaImportError.UnsupportedPackage, rejected.Error); Assert.Null(rejected.Plan);
        Assert.Empty((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(before, Files(files.Profile));
    }

    [Fact]
    public async Task SchemaSixContentAndFenceMigrateAdditivelyWithoutLosingTheTextImportContract()
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using (ArchiveCatalog seed = await ArchiveCatalog.OpenAsync(files.Profile, lease))
            await seed.RegisterThreadAsync(new(new("4chan", "b", "6999"), "https://boards.4chan.org/b/thread/6999",
                "migration sentinel", Path.Combine(files.Profile, "inert"), false));
        string database = Path.Combine(files.Profile, "archive.db");
        using (SqliteConnection connection = ArchiveSql.OpenConnection(database, SqliteOpenMode.ReadWrite))
        using (SqliteCommand command = connection.CreateCommand())
        {
            // A schema-shape fixture, not a claim that a released predecessor generated it.
            command.CommandText = "DROP TABLE archive_media_import_operations; DROP TABLE archive_media_import_receipts; PRAGMA user_version=6"; command.ExecuteNonQuery();
        }
        string[] before;
        using (SqliteConnection connection = ArchiveSql.OpenConnection(database, SqliteOpenMode.ReadOnly))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT instance,generation FROM archive_import_fence";
            using var row = command.ExecuteReader(); Assert.True(row.Read()); before = [row.GetString(0), row.GetInt64(1).ToString()];
        }
        await using ArchiveCatalog migrated = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        using (SqliteConnection connection = ArchiveSql.OpenConnection(database, SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(8, ArchiveSql.GetPragma(connection, "user_version"));
            var fence = ArchiveSql.ReadImportFence(connection, null);
            Assert.Equal(before, new[] { fence.Instance, fence.Generation.ToString() });
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Equal("migration sentinel", (await reader.ReadThreadAsync(new("4chan", "b", "6999")))!.Summary.Title);
        Assert.Empty((await migrated.ReadNativeMediaImportOperationsPageAsync()).Items);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        var plan = (await migrated.PrepareNativeMediaImportAsync(stage)).Plan!;
        Assert.Equal(NativeMediaImportCommitStatus.Committed, (await migrated.CommitNativeMediaImportAsync(plan, new MediaStore())).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteOrCleanupDropsCommittedMetadataButKeepsOnlyContentFreeHistoricalReceipt(bool cleanup)
    {
        using Workspace files = new();
        NativeMediaImportPlan plan;
        Dictionary<string, string> payloadBefore;
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            await using NativeTransferImportStage stage = await Stage(files, Content());
            plan = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
            var commit = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore());
            Assert.Equal(NativeMediaImportCommitStatus.Committed, commit.Status);
            Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
            payloadBefore = Files(plan.OperationsParent);
            if (cleanup)
            {
                ArchiveDeletedThread selected = Assert.Single((await catalog.PrepareDeletionAsync([Id])).Threads);
                Assert.True(await catalog.DeleteCleanupThreadAsync(selected));
            }
            else Assert.Single(await catalog.DeleteThreadsAsync([Id]));
            Assert.Empty((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
            Assert.Equal(payloadBefore, Files(plan.OperationsParent)); // SQL deletion is not FS deletion.
        }
        using (ProfileLease lease = ProfileLease.Acquire(files.Profile))
        {
            await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            var receipt = Assert.IsType<NativeMediaImportCommitResult>(await reopened.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
            Assert.Equal(NativeMediaImportCommitStatus.Committed, receipt.Status);
            Assert.True(receipt.IsReplay);
            Assert.Equal((1, 2, 1), (receipt.NewThreads, receipt.NewPosts, receipt.NewAttachments));
            var inspection = await reopened.InspectNativeMediaImportOperationAsync(plan.OperationId, plan.RequestSha256, new MediaStore());
            Assert.Null(inspection.Operation); Assert.Empty(inspection.Children);
            Assert.Equal(receipt, inspection.Receipt);
            // Recreated content must survive replay unchanged. The disposed Stage and deleted
            // operations metadata are not needed, even through the original disposed owner.
            await reopened.RegisterThreadAsync(new(Id, Url, "deliberate replacement", Path.Combine(files.Profile, "replacement"), false));
            Dictionary<string, string> before = Files(files.Profile);
            var replay = await plan.Owner.CommitNativeMediaImportAsync(plan, new MediaStore());
            Assert.Equal(receipt, replay);
            Assert.Equal(before, Files(files.Profile));
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
            var replacement = (await reader.ReadThreadAsync(Id))!;
            Assert.Equal("deliberate replacement", replacement.Summary.Title); Assert.Empty(replacement.Posts);
        }
        using SqliteConnection connection = ArchiveSql.OpenConnection(Path.Combine(files.Profile, "archive.db"), SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(archive_media_import_receipts)";
        using SqliteDataReader columns = command.ExecuteReader();
        List<string> names = [];
        while (columns.Read()) names.Add(columns.GetString(1));
        Assert.Equal(new[] { "operation_id", "request_sha256", "post_count", "attachment_count" }, names);
    }

    [Theory]
    [InlineData(false, "PreparingSaved")]
    [InlineData(true, "PreparingSaved")]
    [InlineData(false, "PreparedSaved")]
    [InlineData(true, "PreparedSaved")]
    public async Task DeleteAndCleanupNeverEraseUnresolvedRecoveryAuthority(bool cleanup, string interruption)
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
        var interrupted = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: phase =>
        { if (phase.ToString() == interruption) throw new IOException("synthetic unresolved operation"); });
        Assert.Equal(NativeMediaImportCommitStatus.Inactive, interrupted.Status);
        NativeMediaImportOperation before = Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        await catalog.RegisterThreadAsync(new(Id, Url, "intervening owner", Path.Combine(files.Profile, "inert"), false));
        if (cleanup)
        {
            ArchiveDeletedThread selected = Assert.Single((await catalog.PrepareDeletionAsync([Id])).Threads);
            Assert.True(await catalog.DeleteCleanupThreadAsync(selected));
        }
        else await catalog.DeleteThreadsAsync([Id]);
        Assert.Equal(before, Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items));
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        NativeMediaImportPlan replacement = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired,
            (await catalog.CommitNativeMediaImportAsync(replacement, new MediaStore())).Status);
        Assert.False(Directory.Exists(replacement.OperationsParent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationAtVerifiedCandidateInvalidatesFinalSqlAdmissionWithoutPublishingPartialContent(bool deleteAgain)
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
        int verified = 0;
        var result = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore(), checkpoint: phase =>
        {
            if (phase != NativeMediaImportPhase.CandidateVerified) return;
            verified++;
            // This hook is outside the SQL writer/ProfileLease monitor, while the candidate
            // lease is held. Registration must be able to win before the final transaction.
            catalog.RegisterThreadAsync(new(Id, Url, "won final fence", Path.Combine(files.Profile, "inert"), false)).GetAwaiter().GetResult();
            if (deleteAgain) catalog.DeleteThreadsAsync([Id]).GetAwaiter().GetResult();
        });
        Assert.Equal(1, verified);
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, result.Status);
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        NativeMediaImportOperation operation = Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(NativeMediaImportOperationState.Prepared, operation.State);
        var inspection = await catalog.InspectNativeMediaImportOperationAsync(plan.OperationId, plan.RequestSha256, new MediaStore());
        Assert.Null(inspection.Receipt); Assert.Single(inspection.Children);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        var thread = await reader.ReadThreadAsync(Id);
        if (deleteAgain) Assert.Null(thread);
        else
        {
            Assert.Equal("won final fence", thread!.Summary.Title); Assert.Empty(thread.Posts);
            Assert.DoesNotContain(operation.CandidateDirectory!, thread.MediaDirectories);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalReceiptWinsAdmissionRaceAfterAnotherCallCommitsThenDeletesMetadata(bool cleanup)
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = (await catalog.PrepareNativeMediaImportAsync(stage)).Plan!;
        TimeSpan deadline = TimeSpan.FromSeconds(15);
        using ManualResetEventSlim release = new();
        TaskCompletionSource negativeRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int admissions = 0, materializationCallbacks = 0;
        Task<NativeMediaImportCommitResult> pending = Task.Run(() => catalog.CommitNativeMediaImportAsync(plan, new MediaStore(),
            checkpoint: _ => Interlocked.Increment(ref materializationCallbacks), beforeAdmission: () =>
            {
                Interlocked.Increment(ref admissions);
                negativeRead.TrySetResult();
                if (!release.Wait(deadline)) throw new TimeoutException("test did not release the post-negative-receipt admission barrier");
            }));
        NativeMediaImportCommitResult? historical = null;
        Dictionary<string, string>? before = null;
        try
        {
            await negativeRead.Task.WaitAsync(deadline);
            Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
            Assert.Empty((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
            Assert.False(Directory.Exists(plan.OperationsParent));
            var winningCall = await catalog.CommitNativeMediaImportAsync(plan, new MediaStore()).WaitAsync(deadline);
            Assert.Equal(NativeMediaImportCommitStatus.Committed, winningCall.Status); Assert.False(winningCall.IsReplay);
            historical = await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256);
            Assert.NotNull(historical);
            if (cleanup)
            {
                ArchiveDeletedThread selected = Assert.Single((await catalog.PrepareDeletionAsync([Id])).Threads);
                Assert.True(await catalog.DeleteCleanupThreadAsync(selected));
            }
            else Assert.Single(await catalog.DeleteThreadsAsync([Id]));
            Assert.Empty((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
            before = Files(files.Profile);
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(deadline);
        }
        var replay = await pending;
        Assert.Equal(historical, replay);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, replay.Status); Assert.True(replay.IsReplay);
        Assert.Equal((1, 2, 1), (replay.NewThreads, replay.NewPosts, replay.NewAttachments));
        Assert.Equal(1, admissions); Assert.Equal(0, materializationCallbacks);
        Assert.Equal(before, Files(files.Profile));
        Assert.Empty((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Single(Directory.GetDirectories(plan.OperationsParent));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Null(await reader.ReadThreadAsync(Id));
    }

    public static IEnumerable<object[]> MalformedMediaSchemaColumns()
    {
        foreach (string column in MediaOperationColumnNames) yield return ["operations", column];
        foreach (string column in MediaReceiptColumnNames) yield return ["receipts", column];
    }

    private static readonly string[] MediaOperationColumnNames = ["operation_id", "request_sha256", "package_fingerprint", "catalog_instance",
        "catalog_generation", "source", "board", "thread", "operations_parent", "imported_at", "state", "revision_keys_json",
        "candidate_directory", "manifest_generation", "bindings_sha256", "post_count", "attachment_count"];
    private static readonly string[] MediaReceiptColumnNames = ["operation_id", "request_sha256", "post_count", "attachment_count"];

    [Theory]
    [MemberData(nameof(MalformedMediaSchemaColumns))]
    public async Task EveryMissingOperationOrReceiptColumnRollsBackTheWholeSchemaSixMigration(string table, string missingColumn)
    {
        using Workspace files = new(); using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using (ArchiveCatalog seed = await ArchiveCatalog.OpenAsync(files.Profile, lease))
            await seed.RegisterThreadAsync(new(Id, Url, "rollback sentinel", Path.Combine(files.Profile, "sentinel"), false));
        string database = Path.Combine(files.Profile, "archive.db");
        (string Instance, long Generation) fence;
        string tableName = "archive_media_import_" + table; // Only fixed MemberData above, never input SQL.
        string[] columns = table == "operations" ? MediaOperationColumnNames : MediaReceiptColumnNames;
        using (SqliteConnection connection = ArchiveSql.OpenConnection(database, SqliteOpenMode.ReadWrite))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE archive_media_import_operations; DROP TABLE archive_media_import_receipts; PRAGMA user_version=6; "
                + "CREATE TABLE " + tableName + "(" + string.Join(",", columns.Where(c => c != missingColumn).Select(c => c + " TEXT")) + "); "
                + "INSERT INTO " + tableName + " DEFAULT VALUES";
            command.ExecuteNonQuery();
            fence = ArchiveSql.ReadImportFence(connection, null);
        }
        await Assert.ThrowsAsync<SqliteException>(async () =>
        { await using var invalid = await ArchiveCatalog.OpenAsync(files.Profile, lease); });
        using (SqliteConnection connection = ArchiveSql.OpenConnection(database, SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(6, ArchiveSql.GetPragma(connection, "user_version"));
            Assert.Equal(fence, ArchiveSql.ReadImportFence(connection, null));
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'archive_media_import_%' ORDER BY name";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                Assert.True(reader.Read()); Assert.Equal(tableName, reader.GetString(0)); Assert.False(reader.Read());
            }
            command.CommandText = "SELECT count(*) FROM " + tableName;
            Assert.Equal(1L, (long)command.ExecuteScalar()!); // Existing malformed table was not repaired/replaced.
            command.CommandText = "SELECT title FROM archive_threads";
            Assert.Equal("rollback sentinel", command.ExecuteScalar());
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'import_fence_%'";
            Assert.Equal(21L, (long)command.ExecuteScalar()!);
        }
    }

    internal static NativeTransferCatalog Content(bool legacy = false, bool mismatch = false)
    {
        TransferAttachment attachment = Attachment(legacy, mismatch);
        return new(At, [new(Id, Url, "synthetic-media-import-needle", ThreadSourceState.NotFound, At,
            At, 9, false,
            [new(new("15", 0, PostFieldPresence.All, At, "", "", "synthetic-media-import-needle", false, [attachment.Metadata]), [attachment]),
             new(new("16", 1, PostFieldPresence.Html, Html: "<a href=\"#p15\">&gt;&gt;15</a>"), [])])]);
    }
    internal static TransferAttachment Attachment(bool legacy = false, bool mismatch = false, string postId = "15")
    {
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.jpg", "synthetic.bin",
            mismatch ? Bytes.Length + 13 : Bytes.Length, Md5(mismatch ? [5, 6] : Bytes), Hash(Bytes)) { ExpectedSha1 = Sha1(Bytes) };
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha1 = provider.ExpectedSha1, ExpectedSha256 = provider.ExpectedSha256,
          AttachmentRevisionKey = legacy ? null : MediaRevisionKeys.CreateAttachmentKey(Id, postId, 0, provider) };
        return new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained, Hash(Bytes), null);
    }
    internal static async Task<NativeTransferImportStage> Stage(Workspace files, NativeTransferCatalog content)
    {
        using MemoryStream package = new();
        var result = await NativeTransferPackageWriter.WriteAsync(package, content,
            [new(new(Bytes.Length, Hash(Bytes)), _ => Task.FromResult<Stream>(new MemoryStream(Bytes, false)))], Guid.NewGuid());
        Assert.True(result.IsComplete, result.Error.ToString()); package.Position = 0;
        var staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        return Assert.IsType<NativeTransferImportStage>(staged.Stage);
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    internal static string Sha1(byte[] bytes) => Convert.ToBase64String(SHA1.HashData(bytes));
    internal static Dictionary<string, string> Files(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(p => !Path.GetFileName(p).EndsWith(".lock", StringComparison.Ordinal))
        .OrderBy(p => p, StringComparer.Ordinal).ToDictionary(p => Path.GetRelativePath(directory, p), p => Hash(File.ReadAllBytes(p)), StringComparer.Ordinal);
    internal sealed class Workspace : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("meowchan-native-media-tests-").FullName;
        internal string Profile { get; }
        internal string StageRoot { get; }
        internal Workspace()
        { Profile = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName; StageRoot = Directory.CreateDirectory(Path.Combine(_root, "stage")).FullName; }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
