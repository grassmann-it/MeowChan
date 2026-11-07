using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests;

// Real synthetic package/stage/SQL/candidate checkpoints; no mocked outcome classifier.
public sealed class NativeMediaImportOutcomeTests
{
    [Theory]
    [InlineData((int)NativeMediaImportPhase.PreparingSaved, false)]
    [InlineData((int)NativeMediaImportPhase.CandidateDirectoryCreated, false)]
    [InlineData((int)NativeMediaImportPhase.CandidateReady, false)]
    [InlineData((int)NativeMediaImportPhase.PreparedSaved, false)]
    [InlineData((int)NativeMediaImportPhase.BeforeSqlCommit, false)]
    [InlineData((int)NativeMediaImportPhase.PreparingSaved, true)]
    [InlineData((int)NativeMediaImportPhase.CandidateReady, true)]
    [InlineData((int)NativeMediaImportPhase.PreparedSaved, true)]
    [InlineData((int)NativeMediaImportPhase.BeforeSqlCommit, true)]
    public async Task UnfinishedDurableClaimRequiresReconciliation(int phaseValue, bool cancel)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        using CancellationTokenSource cancellation = new();
        bool reached = false;
        var result = await CommitWithExclusiveAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired, cancellation.Token, phase =>
        {
            if ((int)phase != phaseValue) return;
            reached = true;
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw new IOException("synthetic checkpoint failure");
        });
        Assert.True(reached);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, result.Status);
        Assert.Equal(cancel ? nameof(OperationCanceledException) : nameof(IOException), result.Diagnostic);
        Assert.Equal(0, result.NewThreads);
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        var operation = Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(phaseValue >= (int)NativeMediaImportPhase.PreparedSaved
            ? NativeMediaImportOperationState.Prepared : NativeMediaImportOperationState.Preparing, operation.State);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Null(await reader.ReadThreadAsync(Id));
        var before = Files(files.Profile);
        int callbacks = 0;
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired,
            (await CommitWithExclusiveAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired, checkpoint: _ => callbacks++)).Status);
        Assert.Equal(0, callbacks);
        Assert.Equal(before, Files(files.Profile));
    }

    [Fact]
    public async Task PostCommitFailureAndCancellationReturnHistoricalReceiptFirst()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        var plan = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        using CancellationTokenSource cancellation = new();
        var result = await CommitWithExclusiveAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume, cancellation.Token, phase =>
        {
            if (phase != NativeMediaImportPhase.SqlCommitted) return;
            cancellation.Cancel();
            throw new IOException("synthetic lost commit response");
        });
        var receipt = Assert.IsType<NativeMediaImportCommitResult>(
            await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        Assert.Equal(receipt with { Diagnostic = nameof(IOException) }, result);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, result.Status);
        Assert.True(result.IsReplay);
        Assert.Equal(1, result.NewThreads); Assert.Equal(2, result.NewPosts); Assert.Equal(1, result.NewAttachments);
        Assert.Equal(receipt, await CommitWithExclusiveAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoClaimOnUnchangedTargetIsProvenInactive(bool cancel)
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        var plan = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        using CancellationTokenSource cancellation = new();
        if (cancel) cancellation.Cancel();
        var result = await CommitWithExclusiveAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.SafeToResume, cancellation.Token,
            beforeAdmission: () => { if (!cancel) throw new IOException("synthetic pre-claim failure"); });
        Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Status);
        Assert.NotNull(result.Diagnostic);
        Assert.Empty((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.False(Directory.Exists(plan.OperationsParent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentOperationOnUnreadableOrReplacedDatabaseIsUnknown(bool replace)
    {
        using Workspace files = new(); using Workspace replacement = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        using ProfileLease otherLease = ProfileLease.Acquire(replacement.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using ArchiveCatalog other = await ArchiveCatalog.OpenAsync(replacement.Profile, otherLease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        var plan = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        string parked = catalog.DatabasePath + ".parked";
        bool moved = false;
        try
        {
            var result = await CommitWithExclusiveAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired, beforeAdmission: () =>
            {
                File.Move(catalog.DatabasePath, parked); moved = true;
                if (replace) File.Copy(other.DatabasePath, catalog.DatabasePath);
                else Directory.CreateDirectory(catalog.DatabasePath);
                throw new IOException("synthetic unavailable/replaced target");
            });
            Assert.Equal(NativeMediaImportCommitStatus.Unknown, result.Status);
            Assert.NotNull(result.Diagnostic);
            Assert.False(Directory.Exists(plan.OperationsParent));
            if (replace) Assert.Empty((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        }
        finally
        {
            if (moved)
            {
                if (replace) File.Delete(catalog.DatabasePath); else Directory.Delete(catalog.DatabasePath);
                File.Move(parked, catalog.DatabasePath);
            }
        }
    }

    [Fact]
    public async Task OtherUnfinishedClaimPreventsFalseInactiveAfterPreAdmissionFailure()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        var first = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        var second = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired,
            (await CommitWithExclusiveAsync(admission, catalog, first, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired, checkpoint: phase =>
            { if (phase == NativeMediaImportPhase.PreparingSaved) throw new IOException("synthetic claim interruption"); })).Status);
        var result = await CommitWithExclusiveAsync(admission, catalog, second, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired,
            beforeAdmission: () => throw new IOException("synthetic observer failure"));
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, result.Status);
        Assert.Equal(first.OperationId, Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items).OperationId);
        Assert.False(Directory.Exists(second.OperationsParent));
    }

    [Fact]
    public async Task CommittedOperationWithoutReceiptIsUnknownNotSuccessOrInactive()
    {
        using Workspace files = new();
        using ProfileLease lease = ProfileLease.Acquire(files.Profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
        await using NativeTransferImportStage stage = await Stage(files, Content());
        var plan = Assert.IsType<NativeMediaImportPlan>((await catalog.PrepareNativeMediaImportAsync(stage)).Plan);
        using ProfileAdmissionToken admission = await EnterExclusiveAsync(lease);
        var result = await CommitWithExclusiveAsync(admission, catalog, plan, new MediaStore(), ProfileExclusiveDisposition.RecoveryRequired, checkpoint: phase =>
        {
            if (phase != NativeMediaImportPhase.SqlCommitted) return;
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            { DataSource = catalog.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM archive_media_import_receipts WHERE operation_id=$op";
            command.Parameters.AddWithValue("$op", plan.OperationId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
            throw new IOException("synthetic receipt loss");
        });
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, result.Status);
        Assert.Equal(NativeMediaImportOperationState.Committed,
            Assert.Single((await catalog.ReadNativeMediaImportOperationsPageAsync()).Items).State);
        Assert.Null(await catalog.ReadNativeMediaImportReceiptAsync(plan.OperationId, plan.RequestSha256));
    }

    // Direct synthetic storage owner, not a public Registry startup shortcut. All
    // catalog/stage initialization and initial planning have completed before this call.
    private static ValueTask<ProfileAdmissionToken> EnterExclusiveAsync(ProfileLease lease)
    {
        if (lease.Admission.Phase == ProfileAdmissionPhase.Starting) lease.Admission.CompleteStartup();
        return lease.Admission.EnterExclusiveAsync();
    }

    // Explicit caller-owned token and expectation: never reserve, retire or infer safety
    // from the domain status. RecoveryRequired probes here do not authorize lane resume.
    private static async Task<NativeMediaImportCommitResult> CommitWithExclusiveAsync(
        ProfileAdmissionToken admission, ArchiveCatalog catalog, NativeMediaImportPlan plan, MediaStore store,
        ProfileExclusiveDisposition expectedDisposition, CancellationToken cancellationToken = default,
        Action<NativeMediaImportPhase>? checkpoint = null, Action? beforeAdmission = null)
    {
        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome =
            await catalog.CommitNativeMediaImportAdmittedAsync(admission, plan, store,
                cancellationToken, checkpoint, beforeAdmission);
        catalog.EnsureTransferAdmission(admission); // Still live after full reconciliation, including failures/replay.
        Assert.Equal(expectedDisposition, outcome.Disposition);
        return outcome.Result;
    }
}
