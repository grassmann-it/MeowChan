using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Persistence;

namespace MeowChan.Tests;

// Real synthetic profile files and instance-local barriers, never mocked persistence.
public sealed class ReaderGroupStoreAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId A = new("source-A", "board", "42");
    private static readonly ResourceId B = new("source-B", "Board", "42");
    private const string FileName = "reader-groups.json";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StandaloneSaveAndRestoreUseExactlyOneOwnerUntilActualTaskEnd(bool restore, bool ready)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        if (ready) lease.Admission.CompleteStartup();
        string[] bytes = Inventory(directory);
        using StepBarrier before = new();
        using StepBarrier after = new();
        ProfileAdmissionToken? observed = null;
        int beforeCount = 0, afterCount = 0;
        SetHooks(store, restore, token =>
        {
            observed = token;
            Assert.Equal(ready ? ProfileAdmissionKind.Ordinary : ProfileAdmissionKind.Startup, token.Kind);
            Assert.Same(token, OnlyActiveOwner(lease));
            Assert.Equal(bytes, Inventory(directory));
            beforeCount++;
            before.Block();
        }, token =>
        {
            Assert.Same(observed, token);
            Assert.Same(token, OnlyActiveOwner(lease));
            Assert.Empty(TemporaryFiles(directory));
            afterCount++;
            after.Block();
        });
        using CancellationTokenSource cancelExclusive = new();
        Task<ProfileAdmissionToken>? pending = null;
        Task<ReaderGroupsSnapshot> mutation = MutateAsync(store, initial, restore);
        try
        {
            await before.Reached.WaitAsync(Deadline);
            Assert.False(mutation.IsCompleted);
            if (ready)
            {
                pending = lease.Admission.EnterExclusiveAsync(cancelExclusive.Token).AsTask();
                Assert.False(pending.IsCompleted);
            }
            else Assert.Throws<InvalidOperationException>(() => lease.Admission.CompleteStartup());
            before.Release();
            await after.Reached.WaitAsync(Deadline);
            Assert.False(mutation.IsCompleted);
            if (pending != null) Assert.False(pending.IsCompleted);
            ReaderGroupsSnapshot expected = ExpectedMutation(initial, restore);
            AssertCommitted(directory, expected);
            if (restore) AssertRecoveryEvidence(directory);
            after.Release();
            ReaderGroupsSnapshot saved = await mutation.WaitAsync(Deadline);
            AssertSnapshot(expected, saved);
            Assert.Equal(1, beforeCount);
            Assert.Equal(1, afterCount);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(observed!, () => true,
                ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary));
            if (!ready) lease.Admission.CompleteStartup();
            if (pending != null) (await pending.WaitAsync(Deadline)).Dispose();
            using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync().AsTask().WaitAsync(Deadline);
            string[] committedBytes = Inventory(directory);
            AssertSnapshot(saved, (await store.LoadAsync()).Snapshot!); // Passive even under Exclusive.
            Assert.Equal(committedBytes, Inventory(directory));
        }
        finally
        {
            before.Release(); after.Release();
            await ObserveForTeardownAsync(mutation);
            cancelExclusive.Cancel();
            if (pending != null) await DisposePendingAsync(pending);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidBorrowedAuthorityCannotSaveEvenNoopOrRestoreWithExactRecoveryToken(bool restore)
    {
        using TempDirectory directory = new();
        using TempDirectory foreignDirectory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ProfileLease foreignLease = ProfileLease.Acquire(foreignDirectory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        if (!restore)
        {
            await store.SaveAsync(Draft(), 0);
            initial = await store.LoadAsync();
        }
        int hooks = 0;
        SetHooks(store, restore, _ => hooks++, _ => hooks++);
        using ProfileAdmissionToken foreign = foreignLease.Admission.EnterStartupWriter();
        // Allocation is not issuance. Only a negative membership fixture.
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Startup);
        ProfileAdmissionToken released = lease.Admission.EnterStartupWriter(); released.Dispose();
        string[] bytes = Inventory(directory);
        foreach (ProfileAdmissionToken invalid in new[] { null!, foreign, unissued, released })
            await Assert.ThrowsAsync<InvalidOperationException>(() => restore
                ? store.RestoreBackupAdmittedAsync(initial.RecoveryToken!, invalid)
                : store.SaveAdmittedAsync(initial.Snapshot!, initial.Snapshot!.Revision, invalid));
        lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => restore
                ? store.RestoreBackupAdmittedAsync(initial.RecoveryToken!, exclusive)
                : store.SaveAdmittedAsync(initial.Snapshot!, initial.Snapshot!.Revision, exclusive));
            await Assert.ThrowsAsync<InvalidOperationException>(() => restore
                ? store.RestoreBackupAsync(initial.RecoveryToken!)
                : store.SaveAsync(initial.Snapshot!, initial.Snapshot!.Revision));
            Assert.Equal(initial.State, (await store.LoadAsync()).State);
        }
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoopRequiresAuthorityButDoesNotInspectOrRotateFutureBackup(bool empty)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsSnapshot saved = empty ? ReaderGroupsSnapshot.Empty : await store.SaveAsync(Draft(), 0);
        if (!empty) File.WriteAllText(MainPath(directory) + ".bak", "{\"schemaVersion\":99}");
        string[] bytes = Inventory(directory);
        int hooks = 0;
        SetHooks(store, false, _ => hooks++, _ => hooks++);
        using (ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter())
        {
            ReaderGroupsSnapshot noop = await store.SaveAdmittedAsync(saved, saved.Revision, accepted);
            AssertSnapshot(saved, noop);
            Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
        }
        AssertSnapshot(saved, await store.SaveAsync(saved, saved.Revision));
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
        lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData(false, "failed")]
    [InlineData(true, "failed")]
    [InlineData(false, "stopping")]
    [InlineData(true, "stopping")]
    [InlineData(false, "stopped")]
    [InlineData(true, "stopped")]
    [InlineData(false, "closed")]
    [InlineData(true, "closed")]
    public async Task ClosedStandaloneAdmissionDoesNotGainAuthorityFromRecoveryOrUnchangedDraft(bool restore, string phase)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        if (phase == "failed") lease.Admission.FailStartup();
        else if (phase == "closed") lease.Admission.Close();
        else
        {
            lease.Admission.BeginShutdown();
            if (phase == "stopped") lease.Admission.CompleteShutdown();
        }
        string[] bytes = Inventory(directory);
        Func<Task> write = () => restore ? store.RestoreBackupAsync(initial.RecoveryToken!)
            : store.SaveAsync(initial.Snapshot!, initial.Snapshot!.Revision);
        if (phase == "closed") await Assert.ThrowsAsync<ObjectDisposedException>(write);
        else await Assert.ThrowsAsync<InvalidOperationException>(write);
        Assert.Equal(initial.State, (await store.LoadAsync()).State);
        Assert.Equal(bytes, Inventory(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedAuthorityDoesNotReplaceTheDisposedPhysicalLease(bool restore)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        string[] bytes = Inventory(directory);
        int hooks = 0; SetHooks(store, restore, _ => hooks++, _ => hooks++);
        lease.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => MutateAdmittedAsync(store, initial, restore, accepted));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => MutateAsync(store, initial, restore));
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingExclusiveRefusesNewStandaloneButNotExactAcceptedBorrow(bool restore)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            string[] bytes = Inventory(directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MutateAsync(store, initial, restore));
            Assert.Equal(initial.State, (await store.LoadAsync()).State);
            Assert.Equal(bytes, Inventory(directory));
            int before = 0, after = 0;
            SetHooks(store, restore, token => { Assert.Same(accepted, token); before++; },
                token => { Assert.Same(accepted, token); after++; });
            ReaderGroupsSnapshot saved = await MutateAdmittedAsync(store, initial, restore, accepted);
            AssertCommitted(directory, saved);
            Assert.Equal(1, before); Assert.Equal(1, after);
            Assert.False(pending.IsCompleted); // Borrow never disposes the accepted owner.
            Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Ordinary));
            accepted.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MutateAdmittedAsync(store, initial, restore, accepted));
        }
        finally { accepted.Dispose(); cancel.Cancel(); await DisposePendingAsync(pending); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActualMutationCleanupAndReadbackRemainPinnedAfterOwnerRelease(bool restore, bool shutdown)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancelExclusive = new();
        using StepBarrier before = new(); using StepBarrier after = new();
        SetHooks(store, restore, token => { Assert.Same(accepted, token); before.Block(); },
            token => { Assert.Same(accepted, token); after.Block(); });
        Task<ReaderGroupsSnapshot> mutation = MutateAdmittedAsync(store, initial, restore, accepted);
        Task<ProfileAdmissionToken>? pending = null;
        Task? drain = null;
        try
        {
            await before.Reached.WaitAsync(Deadline);
            if (shutdown)
            {
                lease.Admission.BeginShutdown();
                drain = lease.Admission.DrainAsync();
                using CancellationTokenSource observer = new();
                Task cancelledDrain = lease.Admission.DrainAsync(observer.Token);
                observer.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledDrain);
            }
            else pending = lease.Admission.EnterExclusiveAsync(cancelExclusive.Token).AsTask();
            accepted.Dispose(); // Release request cannot remove the worker's actual synchronous pin.
            Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Ordinary));
            await Assert.ThrowsAsync<InvalidOperationException>(() => MutateAsync(store, initial, restore));
            await Assert.ThrowsAsync<InvalidOperationException>(() => MutateAdmittedAsync(store, initial, restore, accepted));
            before.Release();
            await after.Reached.WaitAsync(Deadline);
            AssertCommitted(directory, ExpectedMutation(initial, restore));
            if (restore) AssertRecoveryEvidence(directory);
            Assert.Empty(TemporaryFiles(directory));
            Assert.False(mutation.IsCompleted);
            if (pending != null) Assert.False(pending.IsCompleted);
            if (drain != null) Assert.False(drain.IsCompleted);
            after.Release();
            ReaderGroupsSnapshot saved = await mutation.WaitAsync(Deadline);
            AssertCommitted(directory, saved);
            if (pending != null) (await pending.WaitAsync(Deadline)).Dispose();
            if (drain != null) { await drain.WaitAsync(Deadline); lease.Admission.CompleteShutdown(); }
        }
        finally
        {
            before.Release(); after.Release();
            await ObserveForTeardownAsync(mutation);
            accepted.Dispose(); cancelExclusive.Cancel();
            if (pending != null) await DisposePendingAsync(pending);
        }
    }

    [Theory]
    [InlineData(false, true, "entry")]
    [InlineData(false, true, "before")]
    [InlineData(false, true, "after")]
    [InlineData(true, true, "entry")]
    [InlineData(true, true, "before")]
    [InlineData(true, true, "after")]
    [InlineData(false, false, "entry")]
    [InlineData(false, false, "before")]
    [InlineData(false, false, "after")]
    [InlineData(true, false, "entry")]
    [InlineData(true, false, "before")]
    [InlineData(true, false, "after")]
    public async Task CancellationDoesNotLeakOwnershipOrUndoAnActualCommit(bool restore, bool ready, string cancelAt)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        if (ready) lease.Admission.CompleteStartup();
        string foreignTemp = Path.Combine(directory.Path, ".reader-groups.json.foreign.tmp");
        File.WriteAllText(foreignTemp, "foreign bytes");
        string[] bytes = Inventory(directory);
        using CancellationTokenSource cancel = new();
        int before = 0, after = 0;
        SetHooks(store, restore, _ => { before++; if (cancelAt == "before") cancel.Cancel(); },
            _ => { after++; if (cancelAt == "after") cancel.Cancel(); });
        if (cancelAt == "entry") cancel.Cancel();
        Task<ReaderGroupsSnapshot> mutation = MutateAsync(store, initial, restore, cancel.Token);
        if (cancelAt == "after")
        {
            ReaderGroupsSnapshot saved = await mutation.WaitAsync(Deadline);
            Assert.True(cancel.IsCancellationRequested);
            Assert.False(mutation.IsCanceled);
            AssertCommitted(directory, saved);
            if (restore) AssertRecoveryEvidence(directory);
            Assert.Equal(1, before); Assert.Equal(1, after);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.WaitAsync(Deadline));
            Assert.True(mutation.IsCanceled);
            Assert.Equal(bytes, Inventory(directory));
            Assert.Equal(cancelAt == "entry" ? 0 : 1, before); Assert.Equal(0, after);
        }
        Assert.Equal("foreign bytes", File.ReadAllText(foreignTemp));
        Assert.Equal(new[] { foreignTemp }, TemporaryFiles(directory));
        if (!ready) lease.Admission.CompleteStartup(); // Canceled Startup work must release its owner too.
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync().AsTask().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAtomicFailureCleansOnlyOwnTempKeepsEvidenceAndAllowsSameOwnerRetry(bool restore)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        if (!restore)
        {
            ReaderGroupsSnapshot first = await store.SaveAsync(Draft(), 0);
            await store.SaveAsync(Rename(first, "primary"), first.Revision);
            initial = await store.LoadAsync();
        }
        string path = MainPath(directory);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        string parked = path + (restore ? ".fixture-parked" : ".bak.fixture-parked");
        string obstructed = restore ? path : path + ".bak";
        string foreignTemp = Path.Combine(directory.Path, ".reader-groups.json.foreign.tmp");
        File.WriteAllText(foreignTemp, "foreign bytes");
        Exception referenceFailure = AtomicDirectoryFailureReference(directory, restore);
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int before = 0, after = 0;
        SetHooks(store, restore, token =>
        {
            Assert.Same(accepted, token);
            // All primary/backup/token reads have completed; the real Atomic primitive fails next.
            File.Move(obstructed, parked);
            Directory.CreateDirectory(obstructed);
            before++;
        }, token => { Assert.Same(accepted, token); after++; });
        try
        {
            Exception? failure = await Record.ExceptionAsync(() => MutateAdmittedAsync(store, initial, restore, accepted));
            Assert.IsType(referenceFailure.GetType(), failure);
            Assert.Equal(referenceFailure.HResult, failure!.HResult);
            Assert.Equal(1, before); Assert.Equal(0, after);
            Assert.True(Directory.Exists(obstructed));
            Assert.Equal(restore ? primary : backup, File.ReadAllBytes(parked));
            Assert.Equal(restore ? backup : primary, File.ReadAllBytes(restore ? path + ".bak" : path));
            Assert.Equal(new[] { foreignTemp }, TemporaryFiles(directory));
            Assert.Empty(Directory.GetFiles(directory.Path, FileName + ".corrupt-*"));
            Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
        }
        finally
        {
            if (Directory.Exists(obstructed)) Directory.Delete(obstructed);
            if (File.Exists(parked)) File.Move(parked, obstructed);
            SetHooks(store, restore, null, token => { Assert.Same(accepted, token); after++; });
        }
        ReaderGroupsSnapshot retry = await MutateAdmittedAsync(store, initial, restore, accepted);
        AssertCommitted(directory, retry);
        Assert.Equal(1, after);
        Assert.Equal(restore ? backup : primary, File.ReadAllBytes(path + ".bak"));
        if (restore) AssertRecoveryEvidence(directory);
        Assert.Equal(new[] { foreignTemp }, TemporaryFiles(directory));
        Assert.Equal("foreign bytes", File.ReadAllText(foreignTemp));
        accepted.Dispose(); lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("revision")]
    [InlineData("groups")]
    public async Task ActualIoFailureReadbackRequiresBothExactRevisionAndExactGroups(string diskAfter)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsSnapshot original = await store.SaveAsync(Draft(), 0);
        ReaderGroupsSnapshot request = Rename(original, "requested");
        ReaderGroupsSnapshot expected = request with { Revision = original.Revision + 1 };
        ReaderGroupsSnapshot externallyPublished = diskAfter == "revision" ? expected with { Revision = expected.Revision + 1 }
            : diskAfter == "groups" ? Rename(expected, "different groups") : expected;
        Exception referenceFailure = AtomicDirectoryFailureReference(directory, restore: false);
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int after = 0;
        SetHooks(store, false, token =>
        {
            Assert.Same(accepted, token);
            // External-file fixture supplies the postcondition; this is NOT evidence
            // that File.Replace itself threw after committing. No fake Atomic/readback.
            File.WriteAllText(MainPath(directory), JsonSerializer.Serialize(externallyPublished,
                ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot));
            Directory.CreateDirectory(MainPath(directory) + ".bak");
        }, token =>
        {
            Assert.Same(accepted, token);
            Assert.Same(token, OnlyActiveOwner(lease));
            AssertCommitted(directory, expected);
            Assert.Empty(TemporaryFiles(directory));
            after++;
        });
        if (diskAfter == "exact")
        {
            ReaderGroupsSnapshot saved = await store.SaveAdmittedAsync(request, original.Revision, accepted);
            AssertSnapshot(expected, saved);
            Assert.Equal(1, after); // Only after actual Atomic failure + cleanup + successful readback.
        }
        else
        {
            Exception? failure = await Record.ExceptionAsync(() => store.SaveAdmittedAsync(request, original.Revision, accepted));
            Assert.IsType(referenceFailure.GetType(), failure);
            Assert.Equal(referenceFailure.HResult, failure!.HResult);
            Assert.Equal(0, after);
        }
        AssertCommitted(directory, externallyPublished);
        Assert.Empty(TemporaryFiles(directory));
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("future-primary")]
    [InlineData("future-backup")]
    [InlineData("invalid")]
    [InlineData("recovery")]
    public async Task BorrowedSaveKeepsExistingRevisionReadOnlyAndFutureBackupFences(string failureKind)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsSnapshot original = await store.SaveAsync(Draft(), 0);
        if (failureKind == "future-primary") File.WriteAllText(MainPath(directory), "{\"schemaVersion\":99}");
        if (failureKind == "future-backup") File.WriteAllText(MainPath(directory) + ".bak", "{\"schemaVersion\":99}");
        if (failureKind == "invalid") File.WriteAllText(MainPath(directory), "broken-json");
        if (failureKind == "recovery")
        {
            await store.SaveAsync(Rename(original, "next"), original.Revision);
            File.WriteAllText(MainPath(directory), "broken-json");
        }
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int hooks = 0; SetHooks(store, false, _ => hooks++, _ => hooks++);
        string[] bytes = Inventory(directory);
        Func<Task> write = () => store.SaveAdmittedAsync(Rename(original, "refused"),
            failureKind == "revision" ? 0 : original.Revision, accepted);
        if (failureKind == "revision") await Assert.ThrowsAsync<ReaderGroupsConflictException>(write);
        else await Assert.ThrowsAsync<InvalidOperationException>(write);
        Assert.Equal(0, hooks);
        Assert.Equal(bytes, Inventory(directory));
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("backup")]
    [InlineData("wrong-token")]
    [InlineData("future-primary")]
    [InlineData("future-backup")]
    public async Task RecoveryConfirmationIsRevalidatedAgainstBothFilesAndNeverIssuesAuthority(string drift)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, true);
        string recoveryToken = initial.RecoveryToken!;
        if (drift == "primary") File.WriteAllText(MainPath(directory), "different-corrupt-evidence");
        if (drift == "backup") File.WriteAllText(MainPath(directory) + ".bak",
            JsonSerializer.Serialize(Rename(initial.Snapshot!, "changed backup"), ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot));
        if (drift == "wrong-token") recoveryToken = "stale";
        if (drift == "future-primary") File.WriteAllText(MainPath(directory), "{\"schemaVersion\":99}");
        if (drift == "future-backup") File.WriteAllText(MainPath(directory) + ".bak", "{\"schemaVersion\":99}");
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int hooks = 0; SetHooks(store, true, _ => hooks++, _ => hooks++);
        string[] bytes = Inventory(directory);
        await Assert.ThrowsAsync<ReaderGroupsConflictException>(() => store.RestoreBackupAdmittedAsync(recoveryToken, accepted));
        Assert.Equal(0, hooks); Assert.Equal(bytes, Inventory(directory));
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFreezesCompleteOrderedInputBeforeDispatchAndNormalizesOnlyNames(bool borrowed)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        using ProfileAdmissionToken? accepted = borrowed ? lease.Admission.EnterStartupWriter() : null;
        using StepBarrier before = new();
        SetHooks(store, false, _ => before.Block(), null);
        List<ResourceId> members = [B, A, A with { Board = "Board" }];
        Guid id = Guid.NewGuid();
        List<ReaderGroup> groups = [new(id, "  夜 🌙  ", members)];
        ReaderGroupsSnapshot input = new(1, 0, groups);
        Task<ReaderGroupsSnapshot> mutation = borrowed ? store.SaveAdmittedAsync(input, 0, accepted!) : store.SaveAsync(input, 0);
        try
        {
            // This mutation happens immediately after return, before waiting for Before:
            // capture must precede Task dispatch, not merely the actual file write.
            groups.Clear(); members.Clear(); members.Add(A);
            await before.Reached.WaitAsync(Deadline);
            before.Release();
            ReaderGroupsSnapshot saved = await mutation.WaitAsync(Deadline);
            ReaderGroup group = Assert.Single(saved.Groups);
            Assert.Equal(id, group.Id); Assert.Equal("夜 🌙", group.Name);
            Assert.Equal(new[] { B, A, A with { Board = "Board" } }, group.Members);
            AssertCommitted(directory, saved);
        }
        finally { before.Release(); await ObserveForTeardownAsync(mutation); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BeforeAndAfterIoObserverFailuresRemainVisibleWithoutInventedRollback(bool restore, bool afterCommit)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsLoadResult initial = await PrepareAsync(directory, store, restore);
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        IOException marker = new("synthetic-reader-group-observer");
        int before = 0, after = 0;
        SetHooks(store, restore, _ => { before++; if (!afterCommit) throw marker; }, _ => { after++; throw marker; });
        string[] bytes = Inventory(directory);
        Exception? failure = await Record.ExceptionAsync(() => MutateAdmittedAsync(store, initial, restore, accepted));
        Assert.Same(marker, failure); // Not consumed by Publish's IO commit-readback catch.
        Assert.Equal(1, before); Assert.Equal(afterCommit ? 1 : 0, after);
        if (afterCommit)
        {
            AssertCommitted(directory, ExpectedMutation(initial, restore));
            if (restore) AssertRecoveryEvidence(directory);
        }
        else Assert.Equal(bytes, Inventory(directory));
        Assert.Empty(TemporaryFiles(directory));
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
    }

    private static ReaderGroupsSnapshot Draft() => new(1, 0, [new(Guid.Parse("387610b9-62e8-48b9-a8c7-b0bdc2ec5073"), "Evening", new[] { B, A })]);
    private static ReaderGroupsSnapshot Rename(ReaderGroupsSnapshot value, string name) => value with { Groups = [value.Groups[0] with { Name = name }] };
    private static async Task<ReaderGroupsLoadResult> PrepareAsync(TempDirectory directory, ReaderGroupStore store, bool restore)
    {
        if (restore)
        {
            ReaderGroupsSnapshot backup = await store.SaveAsync(Draft(), 0);
            await store.SaveAsync(Rename(backup, "primary"), backup.Revision);
            File.WriteAllText(MainPath(directory), "broken original");
        }
        ReaderGroupsLoadResult loaded = await store.LoadAsync();
        Assert.Equal(restore ? ReaderGroupsLoadState.RecoveryAvailable : ReaderGroupsLoadState.Ready, loaded.State);
        return loaded;
    }
    private static ReaderGroupsSnapshot ExpectedMutation(ReaderGroupsLoadResult initial, bool restore) => restore
        ? initial.Snapshot! with { Revision = initial.Snapshot!.Revision + 1 }
        : Draft() with { Revision = initial.Snapshot!.Revision + 1 };
    private static Task<ReaderGroupsSnapshot> MutateAsync(ReaderGroupStore store, ReaderGroupsLoadResult initial, bool restore, CancellationToken token = default) => restore
        ? store.RestoreBackupAsync(initial.RecoveryToken!, token)
        : store.SaveAsync(Draft(), initial.Snapshot!.Revision, token);
    private static Task<ReaderGroupsSnapshot> MutateAdmittedAsync(ReaderGroupStore store, ReaderGroupsLoadResult initial, bool restore,
        ProfileAdmissionToken admission, CancellationToken token = default) => restore
        ? store.RestoreBackupAdmittedAsync(initial.RecoveryToken!, admission, token)
        : store.SaveAdmittedAsync(Draft(), initial.Snapshot!.Revision, admission, token);
    private static void SetHooks(ReaderGroupStore store, bool restore, Action<ProfileAdmissionToken>? before, Action<ProfileAdmissionToken>? after)
    {
        if (restore) { store.BeforeAdmittedRestoreMutationForTests = before; store.AfterAdmittedRestoreMutationForTests = after; }
        else { store.BeforeAdmittedSaveMutationForTests = before; store.AfterAdmittedSaveMutationForTests = after; }
    }
    private static string MainPath(TempDirectory directory) => Path.Combine(directory.Path, FileName);
    private static string[] TemporaryFiles(TempDirectory directory) => Directory.GetFiles(directory.Path, ".reader-groups.json.*.tmp").Order(StringComparer.Ordinal).ToArray();
    private static string[] Inventory(TempDirectory directory) => Directory.GetFileSystemEntries(directory.Path)
        .Where(path => Path.GetFileName(path) is not (".meowchan-profile.lock" or ".ochan-profile.lock"))
        .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
    private static void AssertSnapshot(ReaderGroupsSnapshot expected, ReaderGroupsSnapshot actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.True(ReaderGroupsValidation.SameGroups(expected.Groups, actual.Groups));
    }
    private static void AssertCommitted(TempDirectory directory, ReaderGroupsSnapshot expected)
    {
        ReaderGroupsSnapshot? disk = JsonSerializer.Deserialize(File.ReadAllBytes(MainPath(directory)), ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot);
        Assert.NotNull(disk); AssertSnapshot(expected, disk!);
    }
    private static void AssertRecoveryEvidence(TempDirectory directory)
    {
        string preserved = Assert.Single(Directory.GetFiles(directory.Path, FileName + ".corrupt-*"));
        Assert.Equal("broken original", File.ReadAllText(preserved));
        ReaderGroupsSnapshot? backup = JsonSerializer.Deserialize(File.ReadAllBytes(MainPath(directory) + ".bak"), ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot);
        AssertSnapshot(Draft() with { Revision = 1 }, backup!);
    }
    private static ProfileAdmissionToken OnlyActiveOwner(ProfileLease lease)
    {
        FieldInfo field = typeof(ProfileAdmission).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.Single(Assert.IsType<HashSet<ProfileAdmissionToken>>(field.GetValue(lease.Admission)));
    }
    private static Exception AtomicDirectoryFailureReference(TempDirectory directory, bool restore)
    {
        string root = Path.Combine(directory.Path, "atomic-reference"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.json"), destination = Path.Combine(root, "destination.json"), backup = Path.Combine(root, "backup.json");
        File.WriteAllText(source, "source");
        if (restore) Directory.CreateDirectory(destination);
        else { File.WriteAllText(destination, "destination"); Directory.CreateDirectory(backup); }
        Exception? failure = Record.Exception(() =>
        {
            if (restore) File.Move(source, destination); // Atomic File.Exists(directory) == false branch.
            else File.Replace(source, destination, backup);
        });
        Assert.NotNull(failure);
        Assert.Equal("source", File.ReadAllText(source));
        if (restore) Assert.True(Directory.Exists(destination));
        else { Assert.Equal("destination", File.ReadAllText(destination)); Assert.True(Directory.Exists(backup)); }
        return failure!;
    }
    private static async Task ObserveForTeardownAsync(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch when (task.IsCompleted) { /* Observe the actual operation before disposing the synthetic profile. */ }
    }
    private static async Task DisposePendingAsync(Task<ProfileAdmissionToken> pending)
    {
        try { (await pending.WaitAsync(Deadline)).Dispose(); }
        catch (OperationCanceledException) { }
    }
    private sealed class StepBarrier : IDisposable
    {
        private readonly TaskCompletionSource<bool> _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        internal Task Reached => _reached.Task;
        internal void Block()
        {
            _reached.TrySetResult(true);
            if (!_release.Wait(Deadline)) throw new TimeoutException("Synthetic reader-group barrier was not released.");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
