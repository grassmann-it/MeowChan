using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

public sealed class NativeMediaMergeFreshnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleAdmissionChecksFenceBeforeCorruptTargetGraphAndPublishesNothing(bool instanceOnly)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed(catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);

        // Mutate exactly at the existing pre-admission seam. The instance case restores the
        // numeric generation so that it cannot accidentally pass on the generation check alone.
        Dictionary<string, string>? beforeAdmission = null;
        NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore(), beforeAdmission: () =>
        {
            CorruptTargetPostFields(files);
            if (instanceOnly) ReplaceInstanceKeepingGeneration(files, plan.Generation);
            beforeAdmission = Files(files.Profile);
        });

        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, result.Status);
        Assert.False(Directory.Exists(plan.OperationsParent));
        Assert.NotNull(beforeAdmission);
        Assert.Equal(beforeAdmission, Files(files.Profile));
        Assert.Empty((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.Null(result.Diagnostic); // Old graph read throws, reconciles as Inactive instead.
        AssertReleasedStoreLeases(plan.Roots.Select(root => root.Directory));
        // A subsequent catalog operation must not be blocked by a freshness early return.
        await catalog.SetCaptureEnabledAsync(Id, false).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleFinalAdmissionChecksFenceBeforeCorruptTargetGraphAndLeavesPreparedJournal(bool instanceOnly)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed(catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);

        NativeMediaMergeResult result = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore(), checkpoint: phase =>
        {
            if (phase == NativeMediaImportPhase.CandidateVerified)
            {
                CorruptTargetPostFields(files);
                if (instanceOnly) ReplaceInstanceKeepingGeneration(files, plan.Generation);
            }
        });

        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, result.Status);
        NativeMediaMergeOperation operation = Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
        Assert.Equal(NativeMediaImportOperationState.Prepared, operation.State);
        Assert.NotNull(operation.FullDirectory);
        Assert.True(Directory.Exists(operation.FullDirectory));
        Assert.Null(await catalog.ReadNativeMediaMergeReceiptAsync(plan.OperationId, plan.RequestSha256));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Single(await reader.GetMediaDirectoriesAsync(Id));
        Assert.Null(result.Diagnostic);
        AssertReleasedStoreLeases(plan.Roots.Select(root => root.Directory)
            .Concat(new[] { operation.FullDirectory! })
            .Concat(operation.SafeDirectory == null ? Array.Empty<string>() : new[] { operation.SafeDirectory! }));
        await catalog.SetCaptureEnabledAsync(Id, false).WaitAsync(TimeSpan.FromSeconds(5));
        using SqliteConnection check = ArchiveSql.OpenConnection(catalog.DatabasePath, SqliteOpenMode.ReadOnly);
        using SqliteCommand count = check.CreateCommand();
        count.CommandText = "SELECT count(*) FROM archive_posts WHERE source=$source AND board=$board AND thread=$thread";
        ArchiveSql.AddId(count, Id);
        Assert.Equal(2L, (long)count.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptWinsBeforeFreshnessAndDisposedStage(bool receiptDuringAdmission)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed(catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        NativeMediaMergeResult? committed = null;
        Dictionary<string, string>? beforeReplay = null;
        if (!receiptDuringAdmission)
        {
            committed = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore());
            Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Status);
            CorruptTargetPostFields(files, expectedPosts: 3);
            await stage.DisposeAsync();
            beforeReplay = Files(files.Profile);
        }
        bool admissionReached = false;
        NativeMediaMergeResult replay = await catalog.CommitNativeMediaMergeAsync(plan, new MediaStore(), beforeAdmission: () =>
        {
            admissionReached = true;
            // Existing seam is outside WriteAsync/the profile monitor. Commit the same issued
            // operation here so the outer admission must find its receipt before freshness.
            committed = catalog.CommitNativeMediaMergeAsync(plan, new MediaStore()).GetAwaiter().GetResult();
            CorruptTargetPostFields(files, expectedPosts: 3);
            stage.DisposeAsync().GetAwaiter().GetResult();
            beforeReplay = Files(files.Profile);
        });
        Assert.Equal(receiptDuringAdmission, admissionReached);
        NativeMediaMergeResult original = Assert.IsType<NativeMediaMergeResult>(committed);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, original.Status);
        Assert.Equal(original with { IsReplay = true }, replay);
        Assert.NotNull(beforeReplay);
        Assert.Equal(beforeReplay, Files(files.Profile));
        Assert.Single((await catalog.ReadNativeMediaMergeOperationsPageAsync()).Items);
    }

    [Fact]
    public async Task BoundedTargetCheckUsesLongRevisionAndNeverParsesPosts()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await Seed(catalog, files);
        await using NativeTransferImportStage stage = await Stage(files, Incoming());
        NativeMediaMergePlan plan = Assert.IsType<NativeMediaMergePlan>(
            (await catalog.PrepareNativeMediaMergeAsync(stage)).Plan);
        CorruptTargetPostFields(files);
        using SqliteConnection connection = ArchiveSql.OpenConnection(catalog.DatabasePath, SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction();
        const long revision = (long)int.MaxValue + 42;
        using (SqliteCommand update = ArchiveSql.Command(connection, transaction,
            "UPDATE archive_threads SET revision=$revision WHERE source=$source AND board=$board AND thread=$thread"))
        {
            ArchiveSql.AddId(update, Id);
            update.Parameters.AddWithValue("$revision", revision);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        string root = plan.Target.Summary.MediaDirectory;
        Assert.True(ArchiveSql.IsMediaMergeTargetFresh(connection, transaction, Id, revision, root));
        Assert.False(ArchiveSql.IsMediaMergeTargetFresh(connection, transaction, Id, revision + 1, root));
        Assert.False(ArchiveSql.IsMediaMergeTargetFresh(connection, transaction, Id, revision, root + "-different"));
        Assert.False(ArchiveSql.IsMediaMergeTargetFresh(connection, transaction,
            new(Id.Source, Id.Board, "missing"), revision, root));
        // Positive control: the fixture really rejects a graph read, rather than relying on
        // a timing assertion to infer that no graph was loaded.
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadThread(connection, transaction, Id));
    }

    private static void AssertReleasedStoreLeases(IEnumerable<string> directories)
    {
        foreach (string root in directories.Distinct(StringComparer.Ordinal))
        {
            using FileStream acquired = new(Path.Combine(root, ".meowchan-store.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private static void ReplaceInstanceKeepingGeneration(Workspace files, long generation)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(
            Path.Combine(files.Profile, "archive.db"), SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE archive_import_fence SET instance=$instance,generation=$generation WHERE singleton=1";
        command.Parameters.AddWithValue("$instance", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static async Task Seed(ArchiveCatalog catalog, Workspace files)
    {
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
            (await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        Assert.Equal(NativeMediaImportCommitStatus.Committed,
            (await catalog.CommitNativeMediaImportAsync(plan, new MediaStore())).Status);
    }

    private static void CorruptTargetPostFields(Workspace files, int expectedPosts = 2)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(
            Path.Combine(files.Profile, "archive.db"), SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE archive_posts SET fields=2147483647
            WHERE source=$source AND board=$board AND thread=$thread
            """;
        command.Parameters.AddWithValue("$source", Id.Source);
        command.Parameters.AddWithValue("$board", Id.Board);
        command.Parameters.AddWithValue("$thread", Id.Thread);
        Assert.Equal(expectedPosts, command.ExecuteNonQuery());
    }

    private static NativeTransferCatalog Incoming()
    {
        NativeTransferCatalog catalog = Content();
        TransferThread thread = catalog.Threads[0];
        TransferAttachment attachment = Attachment(postId: "17");
        TransferPost added = new(new("17", 2, PostFieldPresence.All, At, "", "", "new-post", false,
            [attachment.Metadata]), [attachment]);
        return catalog with { Threads = [thread with { Posts = thread.Posts.Add(added) }] };
    }
}
