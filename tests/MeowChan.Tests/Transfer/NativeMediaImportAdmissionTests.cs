using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;
using static MeowChan.Tests.Transfer.NativeMediaImportTests;

namespace MeowChan.Tests.Transfer;

// Synthetic sources only. Root runs these together with the separately migrated callers.
public sealed class NativeMediaImportAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task EntryAndPassiveConsumersRejectInvalidAuthorityBeforeCallbacksOrFilesystem()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await using Fixture other = await Fixture.CreateAsync();
        using (ProfileAdmissionToken startup = f.Lease.Admission.EnterStartupWriter())
            await Reject(startup);
        f.Lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken ordinary = f.Lease.Admission.EnterOrdinaryWriter())
            await Reject(ordinary);
        using ProfileAdmissionToken foreign = await other.ExclusiveAsync();
        using ProfileAdmissionToken retired = await f.Lease.Admission.EnterExclusiveAsync();
        retired.Dispose();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Exclusive);
        await Reject(foreign); await Reject(retired); await Reject(unissued);
        await Assert.ThrowsAsync<ArgumentNullException>(() => f.Catalog.CommitNativeMediaImportAdmittedAsync(null!, f.Plan, f.Store));
        Assert.Empty((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));

        async Task Reject(ProfileAdmissionToken token)
        {
            int callbacks = 0;
            var before = Files(f.Files.Profile);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Catalog.CommitNativeMediaImportAdmittedAsync(
                token, f.Plan, f.Store, checkpoint: _ => callbacks++, beforeAdmission: () => callbacks++));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.MaterializePassiveImportCandidateAdmittedAsync(
                token, f.Plan, childDirectoryCreated: _ => callbacks++));
            // Null candidate must not be inspected before authority is rejected.
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.WithVerifiedPassiveImportCandidateAdmittedAsync(
                token, f.Plan, (PassiveMediaImportCandidate)null!, () => { callbacks++; return Task.FromResult(true); }));
            Assert.Equal(0, callbacks);
            Assert.Equal(before, Files(f.Files.Profile));
        }
    }

    [Fact]
    public async Task SuccessUsesOneBorrowedExclusiveForEveryTransactionAndReplaysWithoutStageOrFiles()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        List<ProfileAdmissionToken> dispatch = [], commits = [];
        f.Catalog.BeforeWriteDispatchForTests = dispatch.Add;
        f.Catalog.AfterWriteCommitForTests = commits.Add;
        var first = await f.Commit(owner);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, first.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, first.Result.Status);
        Assert.False(first.Result.IsReplay);
        Assert.Equal(3, dispatch.Count); Assert.Equal(3, commits.Count);
        Assert.All(dispatch.Concat(commits), value => Assert.Same(owner, value));
        f.Catalog.EnsureTransferAdmission(owner); // borrowed, not released
        NativeMediaImportOperation operation = Assert.Single((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(NativeMediaImportOperationState.Committed, operation.State);
        // Verification callback's candidate handle really ended before the total result.
        using (var handle = new FileStream(Path.Combine(operation.CandidateDirectory!, ".meowchan-store.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        await f.Stage.DisposeAsync();
        var before = Files(f.Files.Profile);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        var replay = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store, canceled.Token,
            _ => throw new InvalidOperationException("Replay accessed filesystem phases"),
            () => throw new InvalidOperationException("Replay reached admission observer"));
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, replay.Disposition);
        Assert.Equal(first.Result with { IsReplay = true }, replay.Result);
        Assert.Equal(3, dispatch.Count); Assert.Equal(before, Files(f.Files.Profile));
        owner.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Commit(owner));
        Assert.NotNull(await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
    }

    [Fact]
    public async Task ValidReceiptInReplacedInstancePreservesHistoryButCannotAuthorizeResume()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        var committed = await f.Commit(owner);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, committed.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, committed.Disposition);
        Assert.False(committed.Result.IsReplay);
        NativeMediaImportOperation operation = Assert.Single((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(NativeMediaImportOperationState.Committed, operation.State);
        NativeMediaImportCommitResult receipt = Assert.IsType<NativeMediaImportCommitResult>(
            await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        Assert.Equal(committed.Result with { IsReplay = true }, receipt);
        long committedGeneration;
        using (SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly))
        {
            var fence = ArchiveSql.ReadImportFence(connection, null);
            Assert.Equal(f.Plan.Instance, fence.Instance);
            committedGeneration = fence.Generation;
        }
        string replacement = Guid.NewGuid().ToString("D");
        Assert.NotEqual(f.Plan.Instance, replacement);
        // Deliberate identity-only recovery-evidence replacement: preserve receipt,
        // consistent Committed operation, generation and every imported byte.
        Execute(f, "UPDATE archive_import_fence SET instance='" + replacement + "' WHERE singleton=1");
        await f.Stage.DisposeAsync();
        var before = Files(f.Files.Profile);
        int dispatches = 0, callbacks = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => dispatches++;
        var replay = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store,
            new CancellationToken(true), checkpoint: _ => callbacks++, beforeAdmission: () => callbacks++);
        Assert.Equal(receipt, replay.Result); // Exact historical counts, replay and null diagnostic.
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, replay.Disposition);
        Assert.Equal(0, dispatches);
        Assert.Equal(0, callbacks);
        Assert.Equal(operation, Assert.Single((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items));
        Assert.Equal(receipt, await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        Assert.Equal(before, Files(f.Files.Profile));
        using (SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadOnly))
        {
            var fence = ArchiveSql.ReadImportFence(connection, null);
            Assert.Equal(replacement, fence.Instance);
            Assert.Equal(committedGeneration, fence.Generation);
        }
        f.Catalog.EnsureTransferAdmission(owner); // Still the caller's legitimate borrowed owner.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NoClaimFailureOrCancellationIsIndependentlySafe(bool cancel)
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        using CancellationTokenSource cancellation = new();
        if (cancel) cancellation.Cancel();
        var result = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store, cancellation.Token,
            beforeAdmission: () => { if (!cancel) throw new IOException("before claim"); });
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, result.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Inactive, result.Result.Status);
        Assert.Empty((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
        f.Catalog.EnsureTransferAdmission(owner);
    }

    [Theory]
    [InlineData((int)NativeMediaImportPhase.PreparingSaved, false)]
    [InlineData((int)NativeMediaImportPhase.OperationsParentCreated, true)]
    [InlineData((int)NativeMediaImportPhase.CandidateDirectoryCreated, false)]
    [InlineData((int)NativeMediaImportPhase.CandidateReady, true)]
    [InlineData((int)NativeMediaImportPhase.PreparedSaved, false)]
    [InlineData((int)NativeMediaImportPhase.CandidateVerified, true)]
    [InlineData((int)NativeMediaImportPhase.BeforeSqlCommit, false)]
    [InlineData((int)NativeMediaImportPhase.BeforeSqlCommit, true)]
    public async Task InterruptedDurablePhasesNeverResumeOrPublish(int phaseValue, bool cancel)
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        using CancellationTokenSource cancellation = new();
        bool reached = false;
        var outcome = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store, cancellation.Token, phase =>
        {
            if ((int)phase != phaseValue) return;
            reached = true;
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw new IOException("phase interrupted");
        });
        Assert.True(reached);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.ReconciliationRequired, outcome.Result.Status);
        NativeMediaImportOperation operation = Assert.Single((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(phaseValue >= (int)NativeMediaImportPhase.PreparedSaved
            ? NativeMediaImportOperationState.Prepared : NativeMediaImportOperationState.Preparing, operation.State);
        Assert.Null(await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(f.Files.Profile);
        Assert.Null(await reader.ReadThreadAsync(Id));
        var beforeRetry = Files(f.Files.Profile);
        var retry = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store,
            checkpoint: _ => throw new InvalidOperationException("A retry rematerialized"));
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, retry.Disposition);
        Assert.Equal(beforeRetry, Files(f.Files.Profile));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task LostReturnAfterActualCommitUsesReceiptNotTaskSuccess(int lostCommit)
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        int commits = 0;
        f.Catalog.AfterWriteCommitForTests = token =>
        {
            Assert.Same(owner, token);
            if (++commits == lostCommit) throw new IOException("after real Commit");
        };
        var outcome = await f.Commit(owner);
        Assert.Equal(lostCommit, commits);
        Assert.Equal(lostCommit == 3 ? ProfileExclusiveDisposition.SafeToResume : ProfileExclusiveDisposition.RecoveryRequired,
            outcome.Disposition);
        Assert.Equal(lostCommit == 3 ? NativeMediaImportCommitStatus.Committed : NativeMediaImportCommitStatus.ReconciliationRequired,
            outcome.Result.Status);
        Assert.Equal(nameof(IOException), outcome.Result.Diagnostic);
        if (lostCommit == 3) Assert.True(outcome.Result.IsReplay);
        else if (lostCommit == 1) Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    [Fact]
    public async Task NormalFinalStaleWithPreparedJournalIsRecoveryButPreClaimStaleIsSafe()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store, checkpoint: phase =>
        {
            if (phase == NativeMediaImportPhase.PreparedSaved) BumpGeneration(f);
        });
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, outcome.Result.Status);
        Assert.Equal(NativeMediaImportOperationState.Prepared,
            Assert.Single((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items).State);
        Assert.Null(await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
        await using Fixture clean = await Fixture.CreateAsync();
        using ProfileAdmissionToken cleanOwner = await clean.ExclusiveAsync();
        BumpGeneration(clean);
        var stale = await clean.Commit(cleanOwner);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, stale.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, stale.Result.Status);
        Assert.False(Directory.Exists(clean.Plan.OperationsParent));
    }

    [Fact]
    public async Task DisappearedClaimAfterCommitReturnLossCannotMasqueradeAsNoClaim()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        f.Catalog.AfterWriteCommitForTests = _ =>
        {
            Execute(f, "DELETE FROM archive_media_import_operations");
            throw new IOException("claim committed but metadata vanished before return");
        };
        var outcome = await f.Commit(owner);
        Assert.Empty((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, outcome.Result.Status);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoricalReceiptWithSeparateUnresolvedOperationPreservesSuccessButCannotResume(bool merge)
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        var success = await f.Commit(owner);
        AddSeparatePreparing(f, merge);
        var before = Files(f.Files.Profile);
        var replay = await f.Commit(owner);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, replay.Disposition);
        Assert.Equal(success.Result with { IsReplay = true }, replay.Result);
        Assert.Equal(before, Files(f.Files.Profile));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrUnreadableReceiptAfterCommitIsUnknown(bool unreadable)
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store, checkpoint: phase =>
        {
            if (phase != NativeMediaImportPhase.SqlCommitted) return;
            Execute(f, unreadable ? "DROP TABLE archive_media_import_receipts" : "DELETE FROM archive_media_import_receipts");
            throw new IOException("receipt unavailable after commit");
        });
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, outcome.Result.Status);
    }

    [Fact]
    public async Task ReconciliationAfterCatalogDisposalAndCanceledReturnStillFindsHistoricalCommit()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        using CancellationTokenSource cancellation = new();
        var outcome = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store, cancellation.Token, phase =>
        {
            if (phase != NativeMediaImportPhase.SqlCommitted) return;
            f.Catalog.DisposeAsync().GetAwaiter().GetResult();
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.True(outcome.Result.IsReplay);
        Assert.NotNull(await f.Catalog.ReadNativeMediaImportReceiptAsync(f.Plan.OperationId, f.Plan.RequestSha256));
    }

    [Theory]
    [InlineData((int)NativeMediaImportPhase.PreparingSaved)]
    [InlineData((int)NativeMediaImportPhase.CandidateDirectoryCreated)]
    [InlineData((int)NativeMediaImportPhase.PreparedSaved)]
    [InlineData((int)NativeMediaImportPhase.SqlCommitted)]
    public async Task BorrowedOwnerCountsWholeOperationUntilItsAwaitedTailFinishes(int phaseValue)
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        Task<ProfileExclusiveOutcome<NativeMediaImportCommitResult>> pending = Task.Run(() => f.Catalog.CommitNativeMediaImportAdmittedAsync(
            owner, f.Plan, f.Store, checkpoint: phase =>
            {
                if ((int)phase != phaseValue) return;
                reached.TrySetResult();
                if (!release.Wait(Deadline)) throw new TimeoutException("test gate watchdog");
            }));
        Task? drain = null;
        try
        {
            await reached.Task.WaitAsync(Deadline);
            Assert.False(pending.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.EnterStoreWriter());
            f.Lease.Admission.BeginShutdown(); drain = f.Lease.Admission.DrainAsync();
            Assert.False(drain.IsCompleted);
        }
        finally { release.Set(); await pending.WaitAsync(Deadline); }
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, pending.Result.Disposition);
        Assert.NotNull(drain); Assert.False(drain.IsCompleted);
        owner.Dispose(); await drain.WaitAsync(Deadline);
    }

    [Fact]
    public async Task CancelAtDispatchBeforeInsertIsNoClaimNotAnAmbiguousCommit()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        using CancellationTokenSource cancellation = new();
        int dispatches = 0;
        f.Catalog.BeforeWriteDispatchForTests = token =>
        { Assert.Same(owner, token); dispatches++; cancellation.Cancel(); };
        var outcome = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store, cancellation.Token);
        Assert.Equal(1, dispatches);
        Assert.Equal(NativeMediaImportCommitStatus.Inactive, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Empty((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    [Fact]
    public async Task LostClaimMetadataAfterRealFilesystemWorkIsNotRollback()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store, checkpoint: phase =>
        {
            if (phase != NativeMediaImportPhase.CandidateReady) return;
            Execute(f, "DELETE FROM archive_media_import_operations");
            throw new IOException("lost claim after ready files");
        });
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Empty((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.Single(Directory.GetDirectories(f.Plan.OperationsParent));
    }

    [Fact]
    public async Task ReplacedInstanceBeforeClaimCannotAttestInactivity()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        var outcome = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, f.Plan, f.Store,
            beforeAdmission: () =>
            {
                Execute(f, "UPDATE archive_import_fence SET instance='" + Guid.NewGuid().ToString("D") + "'");
                throw new IOException("replacement before claim");
            });
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    [Fact]
    public async Task ConstructedPlanCannotUseAnIssuedPlansIdentityAsMutationAuthority()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        NativeMediaImportPlan p = f.Plan;
        NativeMediaImportPlan forged = new(f.Catalog, p.Stage, p.Package, p.Instance, p.Generation,
            p.OperationId, p.RequestSha256, p.OperationsParent, p.Attachments, p.PrivateConsent);
        var outcome = await f.Catalog.CommitNativeMediaImportAdmittedAsync(owner, forged, f.Store,
            checkpoint: _ => throw new InvalidOperationException("Unissued plan ran"));
        Assert.Equal(NativeMediaImportCommitStatus.StalePlan, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.False(Directory.Exists(p.OperationsParent));
        Assert.Empty((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
    }

    [Fact]
    public async Task PreviouslyObservedClaimCannotBecomeSafeWhenItsRowVanishes()
    {
        await using Fixture f = await Fixture.CreateAsync();
        using ProfileAdmissionToken owner = await f.ExclusiveAsync();
        using (SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadWrite))
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Assert.True(ArchiveSql.ClaimMediaImport(connection, transaction, f.Plan));
            transaction.Commit();
        }
        // The coordinator has read Preparing in its no-op admission transaction;
        // erase it only AFTER that transaction commits, before reconciliation starts.
        f.Catalog.AfterWriteCommitForTests = _ => Execute(f, "DELETE FROM archive_media_import_operations");
        var outcome = await f.Commit(owner);
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, outcome.Result.Status);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, outcome.Disposition);
        Assert.Empty((await f.Catalog.ReadNativeMediaImportOperationsPageAsync()).Items);
        Assert.False(Directory.Exists(f.Plan.OperationsParent));
    }

    private static void BumpGeneration(Fixture f)
        => Execute(f, "UPDATE archive_import_fence SET generation=generation+1");

    private static void Execute(Fixture f, string sql)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static void AddSeparatePreparing(Fixture f, bool merge)
    {
        using SqliteConnection connection = ArchiveSql.OpenConnection(f.Catalog.DatabasePath, SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = merge ? """
            INSERT INTO archive_media_merge_operations(operation_id,request_sha256,package_fingerprint,
                catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,
                roots_json,full_keys_json,safe_keys_json,catalog_bytes,new_posts,updated_posts,new_attachments,new_bindings,conflicts)
            SELECT $op,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                source,board,'other-resource',operations_parent||'-other',imported_at,'Preparing',
                '[]','[]','[]',X'00',0,0,0,0,0 FROM archive_media_import_operations
            """ : """
            INSERT INTO archive_media_import_operations(operation_id,request_sha256,package_fingerprint,
                catalog_instance,catalog_generation,source,board,thread,operations_parent,imported_at,state,
                revision_keys_json,post_count,attachment_count)
            SELECT $op,request_sha256,package_fingerprint,catalog_instance,catalog_generation,
                source,board,'other-resource',operations_parent||'-other',imported_at,'Preparing',
                revision_keys_json,post_count,attachment_count FROM archive_media_import_operations
            """;
        command.Parameters.AddWithValue("$op", Guid.NewGuid().ToString("D"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal Workspace Files { get; } = new();
        internal ProfileLease Lease { get; private set; } = null!;
        internal ArchiveCatalog Catalog { get; private set; } = null!;
        internal NativeTransferImportStage Stage { get; private set; } = null!;
        internal NativeMediaImportPlan Plan { get; private set; } = null!;
        internal MediaStore Store { get; } = new();
        internal static async Task<Fixture> CreateAsync()
        {
            Fixture f = new();
            try
            {
                f.Lease = ProfileLease.Acquire(f.Files.Profile);
                f.Catalog = await ArchiveCatalog.OpenAsync(f.Files.Profile, f.Lease);
                f.Stage = await NativeMediaImportTests.Stage(f.Files, Content());
                f.Plan = Assert.IsType<NativeMediaImportPlan>((await f.Catalog.PrepareNativeMediaImportAsync(f.Stage)).Plan);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal ValueTask<ProfileAdmissionToken> ExclusiveAsync()
        {
            if (Lease.Admission.Phase == ProfileAdmissionPhase.Starting) Lease.Admission.CompleteStartup();
            return Lease.Admission.EnterExclusiveAsync();
        }
        internal Task<ProfileExclusiveOutcome<NativeMediaImportCommitResult>> Commit(ProfileAdmissionToken owner)
            => Catalog.CommitNativeMediaImportAdmittedAsync(owner, Plan, Store);
        public async ValueTask DisposeAsync()
        {
            if (Stage != null) await Stage.DisposeAsync();
            if (Catalog != null) await Catalog.DisposeAsync();
            Lease?.Dispose(); Files.Dispose();
        }
    }
}
