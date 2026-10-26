using MeowChan.Downloader;
using MeowChan.PluginHost.Filters;
using MeowChan.PluginHost.Runtime;
using MeowChan.Tests;

namespace MeowChan.PluginFilters.Tests;

public sealed class FilterStageLedgerTests
{
    [Fact]
    public void InventoryAdoptsForeignMalformedAndPendingFilesWithoutDeletingThem()
    {
        using TempDirectory root = new();
        File.WriteAllBytes(Path.Combine(root.Path, "foreign.bytes"), new byte[11]);
        File.WriteAllText(Path.Combine(root.Path, "stage-invalid.json"), "{bad");
        File.WriteAllBytes(Path.Combine(root.Path, "stage-invalid.json.next"), new byte[17]);
        FilterTemporaryBudget budget = new(100);
        FilterStageLedger ledger = new(root.Path, budget, 100);
        ledger.Reconcile(); ledger.Check();
        Assert.Equal(32, ledger.Bytes); Assert.Equal(3, ledger.Files);
        Assert.Equal(3, Directory.GetFiles(root.Path).Length);
        Assert.Equal(32, budget.Total);
    }

    [Fact]
    public void JournalReplacementCountsBothFilesAndReleasesOnlyAfterRename()
    {
        using TempDirectory root = new();
        FilterTemporaryBudget budget = new(100);
        FilterStageLedger ledger = new(root.Path, budget, 100); ledger.Reconcile();
        string journal = Path.Combine(root.Path, "stage.json"), next = journal + ".next";
        ledger.Reserve(journal, 40); File.WriteAllBytes(journal, new byte[40]);
        ledger.Reserve(next, 60); File.WriteAllBytes(next, new byte[60]);
        ledger.Reconcile();
        Assert.Equal(100, ledger.Bytes); Assert.Equal(2, ledger.Files);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Reserve(Path.Combine(root.Path, "another"), 1));
        File.Move(next, journal, true); ledger.Renamed(next, journal);
        Assert.Equal(60, ledger.Bytes); Assert.Equal(1, ledger.Files);
        ledger.Reserve(next, 40); // The previous .next slot is available again.
        Assert.Equal(100, ledger.Bytes);
    }

    [Fact]
    public void ShortWriteAndReconciliationDoNotReleaseAnOutstandingReservation()
    {
        using TempDirectory root = new();
        FilterTemporaryBudget budget = new(100);
        FilterStageLedger ledger = new(root.Path, budget, 100); ledger.Reconcile();
        string path = Path.Combine(root.Path, "stage.bytes");
        ledger.Reserve(path, 100); File.WriteAllBytes(path, new byte[7]);
        ledger.Reconcile();
        Assert.Equal(100, ledger.Bytes);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Reserve(Path.Combine(root.Path, "second"), 1));
        File.Delete(path); ledger.Deleted(path); ledger.Deleted(path);
        Assert.Equal(0, ledger.Bytes); Assert.Equal(0, ledger.Files); Assert.Equal(0, budget.Total);
    }

    [Fact]
    public void FailedDeletionNeverFreesBytesOrTheFileSlot()
    {
        using TempDirectory root = new();
        FilterTemporaryBudget budget = new(100);
        FilterStageLedger ledger = new(root.Path, budget, 100); ledger.Reconcile();
        string path = Path.Combine(root.Path, "stage.bytes");
        ledger.Reserve(path, 100);
        // A directory occupying the owned path makes File.Delete fail on every OS,
        // without depending on permissions (the test runner may be root).
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "keep"), "x");
        Assert.ThrowsAny<Exception>(() => File.Delete(path));
        Assert.Equal(100, ledger.Bytes);
        ledger.Reconcile(); Assert.False(ledger.IsCurrent);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Check());
        Directory.Delete(path, true); ledger.Deleted(path); ledger.Reconcile();
        Assert.Equal(0, ledger.Bytes); ledger.Check();
    }

    [Fact]
    public void TargetCopyOnAnotherDirectoryRetainsItsPromiseUntilConfirmedRemoval()
    {
        using TempDirectory root = new(); using TempDirectory target = new();
        FilterTemporaryBudget budget = new(150);
        FilterStageLedger ledger = new(root.Path, budget, 150); ledger.Reconcile();
        string stage = Path.Combine(root.Path, "stage.bytes"), part = Path.Combine(target.Path, ".part");
        ledger.Reserve(stage, 80); File.WriteAllBytes(stage, new byte[80]);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Reserve(part, 80));
        ledger.Reserve(part, 70); File.WriteAllBytes(part, new byte[3]); ledger.Reconcile();
        Assert.Equal(150, ledger.Bytes); Assert.Equal(1, ledger.Files);
        File.Delete(stage); ledger.Deleted(stage); ledger.Reconcile();
        Assert.Equal(70, ledger.Bytes);
        File.Delete(part); ledger.Deleted(part); Assert.Equal(0, budget.Total);
    }

    [Fact]
    public void Initial1025FilesDenyAdmissionAnd1024FilesDenyANewZeroByteSlot()
    {
        using TempDirectory root = new();
        for (int i = 0; i < 1025; i++) File.WriteAllBytes(Path.Combine(root.Path, i.ToString()), []);
        FilterTemporaryBudget budget = new(100);
        FilterStageLedger ledger = new(root.Path, budget, 100); ledger.Reconcile();
        Assert.False(ledger.IsCurrent); Assert.True(ledger.Bytes > 100);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Check());
        File.Delete(Path.Combine(root.Path, "1024")); ledger.Reconcile(); ledger.Check();
        Assert.Equal(1024, ledger.Files);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Reserve(Path.Combine(root.Path, "new"), 0));
        File.Delete(Path.Combine(root.Path, "1023")); ledger.Reconcile();
        ledger.Reserve(Path.Combine(root.Path, "new"), 0);
        Assert.Equal(1024, ledger.Files);
    }

    [Fact]
    public void PendingJournalSlotIsReservedBeforeItsFileExists()
    {
        using TempDirectory root = new();
        for (int i = 0; i < 1022; i++) File.WriteAllBytes(Path.Combine(root.Path, i.ToString()), []);
        FilterStageLedger ledger = new(root.Path, new(100), 100); ledger.Reconcile();
        ledger.Reserve(Path.Combine(root.Path, "stage.bytes"), 1);
        ledger.Reserve(Path.Combine(root.Path, "stage.json"), 1);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Reserve(Path.Combine(root.Path, "stage.json.next"), 1));
    }

    [Fact]
    public async Task ConcurrentStagesCannotBothPromiseTheSameRemainingCapacity()
    {
        using TempDirectory first = new(); using TempDirectory second = new();
        FilterTemporaryBudget budget = new(100);
        FilterStageLedger a = new(first.Path, budget, 100), b = new(second.Path, budget, 100);
        a.Reconcile(); b.Reconcile();
        TaskCompletionSource go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Reserve(FilterStageLedger ledger, string path)
        {
            await go.Task;
            try { ledger.Reserve(path, 60); return true; }
            catch (MediaFilterOmissionException) { return false; }
        }
        Task<bool> one = Reserve(a, Path.Combine(first.Path, "stage")), two = Reserve(b, Path.Combine(second.Path, "stage"));
        go.SetResult();
        Assert.Single((await Task.WhenAll(one, two)).Where(success => success));
        Assert.Equal(60, budget.Total);
    }

    [Fact]
    public void WorkerGrowthAndStagePromisesShareOneAtomicBudget()
    {
        using TempDirectory root = new();
        FilterTemporaryBudget budget = new(100); object worker = new();
        FilterStageLedger ledger = new(root.Path, budget, 100); ledger.Reconcile();
        string stage = Path.Combine(root.Path, "stage.bytes"); ledger.Reserve(stage, 60);
        Assert.True(budget.ObserveWorker(worker, 30));
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Grow(stage, 11));
        ledger.Grow(stage, 10); Assert.Equal(100, budget.Total);
        Assert.False(budget.ObserveWorker(worker, 31)); // Monitor must stop this worker.
        Assert.Equal(101, budget.Total); // Observation is retained, never silently refused.
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Check());
        Assert.True(budget.ObserveWorker(worker, 0)); ledger.Check();
        Assert.Equal(70, budget.Total);
    }

    [Fact]
    public void UnexpectedGrowthIsAdoptedWithoutDoubleCountingKnownFiles()
    {
        using TempDirectory root = new();
        FilterTemporaryBudget budget = new(100);
        FilterStageLedger ledger = new(root.Path, budget, 100); ledger.Reconcile();
        string stage = Path.Combine(root.Path, "stage.bytes"); ledger.Reserve(stage, 20);
        File.WriteAllBytes(stage, new byte[30]); File.WriteAllBytes(Path.Combine(root.Path, "foreign"), new byte[71]);
        ledger.Reconcile(); Assert.Equal(101, ledger.Bytes);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Check());
        File.Delete(Path.Combine(root.Path, "foreign")); ledger.Reconcile();
        Assert.Equal(30, ledger.Bytes); ledger.Check();
    }

    [Fact]
    public void LinksAreCountedAsEntriesAndNeverFollowedDuringInventory()
    {
        if (OperatingSystem.IsWindows()) return; // Creating links needs a platform privilege there.
        using TempDirectory root = new(); using TempDirectory target = new();
        File.WriteAllBytes(Path.Combine(target.Path, "large"), new byte[101]);
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "link"), target.Path);
        FilterStageLedger ledger = new(root.Path, new(100), 100); ledger.Reconcile(); ledger.Check();
        Assert.Equal(1, ledger.Files); Assert.Equal(0, ledger.Bytes);
        Assert.Equal(0, WorkerConnection.ScanWorkspaceBytes(root.Path, 100));
        Assert.True(WorkerConnection.ScanWorkspaceBytes(Path.Combine(root.Path, "link"), 100) > 100);
    }

    [Fact]
    public void DetachedWorkerObservationRemainsChargedUntilPhysicalCleanupIsConfirmed()
    {
        FilterTemporaryBudget budget = new(100); object oldWorker = new(), nextWorker = new();
        Assert.True(budget.ObserveWorker(oldWorker, 80));
        Assert.False(budget.ObserveWorker(nextWorker, 30));
        Assert.Equal(110, budget.WorkerBytes);
        Assert.True(budget.ObserveWorker(oldWorker, 0)); Assert.Equal(30, budget.WorkerBytes);
    }
    [Fact]
    public async Task InventoryAdoptionRetriesConcurrentReservationAndRetainsNewForeignBytes()
    {
        using TempDirectory root = new();
        using ManualResetEventSlim scanned = new(), resume = new();
        int pause = 0;
        FilterTemporaryBudget budget = new(100);
        FilterStageLedger ledger = new(root.Path, budget, 100, () => {
            if (Interlocked.Exchange(ref pause, 0) == 0) return;
            scanned.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)), "Inventory race was never released.");
        });
        ledger.Reconcile(); long scans = ledger.ScanCount;
        Volatile.Write(ref pause, 1);
        Task inventory = Task.Run(ledger.Reconcile);
        try
        {
            Assert.True(scanned.Wait(TimeSpan.FromSeconds(5)));
            // Reserving while a scan is held proves filesystem work owns neither
            // the shared budget lock nor the coordinator's state lock.
            await Task.Run(() => ledger.Reserve(Path.Combine(root.Path, "in-flight"), 50)).WaitAsync(TimeSpan.FromSeconds(2));
            File.WriteAllBytes(Path.Combine(root.Path, "new-foreign"), new byte[20]);
        }
        finally { resume.Set(); }
        await inventory.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(scans + 2, ledger.ScanCount); Assert.Equal(70, ledger.Bytes);
        Assert.Equal(2, ledger.Files); ledger.Check();
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Reserve(Path.Combine(root.Path, "other"), 31));
    }

    [Fact]
    public void MissingOrUnreadableInventoryDeniesGrowthUntilSuccessfulReconciliation()
    {
        using TempDirectory root = new();
        FilterStageLedger ledger = new(root.Path, new(100), 100); ledger.Reconcile();
        string path = Path.Combine(root.Path, "in-flight"); ledger.Reserve(path, 20);
        Directory.Delete(root.Path); ledger.Reconcile();
        Assert.False(ledger.IsCurrent); Assert.True(ledger.Bytes > 100);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Grow(path, 1));
        Directory.CreateDirectory(root.Path); ledger.Reconcile();
        Assert.Equal(20, ledger.Bytes); ledger.Grow(path, 1); Assert.Equal(21, ledger.Bytes);
    }

    [Fact]
    public async Task WorkerStartupInventoryChargesPreExistingUnmanagedLeftovers()
    {
        using TempDirectory root = new();
        string orphan = Path.Combine(root.Path, "worker-orphan"); Directory.CreateDirectory(orphan);
        File.WriteAllBytes(Path.Combine(orphan, "leftover"), new byte[80]);
        await using PluginWorkerManager manager = new(new() { MaximumTemporaryBytes = 100 });
        await manager.InventoryTemporaryRootAsync(root.Path, CancellationToken.None);
        Assert.Equal(80, manager.GetTemporaryBytes());
        using TempDirectory stages = new();
        FilterStageLedger ledger = new(stages.Path, manager.TemporaryBudget, 100); ledger.Reconcile();
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Reserve(Path.Combine(stages.Path, "new-stage"), 21));
        ledger.Reserve(Path.Combine(stages.Path, "new-stage"), 20);
        Assert.Equal(100, manager.TemporaryBudget.Total);
        Assert.True(File.Exists(Path.Combine(orphan, "leftover")));
    }

    [Fact]
    public async Task OversizedWorkerStartupInventoryRejectsWithoutStartingAProcess()
    {
        using TempDirectory root = new();
        File.WriteAllBytes(Path.Combine(root.Path, "foreign"), new byte[101]);
        await using PluginWorkerManager manager = new(new() { MaximumTemporaryBytes = 100 });
        var failure = await Assert.ThrowsAsync<MeowChan.PluginHost.Contracts.PluginWorkerException>(
            () => manager.InventoryTemporaryRootAsync(root.Path, CancellationToken.None));
        Assert.Equal("temporary-limit", failure.Code); Assert.True(manager.GetTemporaryBytes() > 100);
        Assert.Equal(101, new FileInfo(Path.Combine(root.Path, "foreign")).Length);
    }

    [Fact]
    public void AcceptedWorkerQuotaDoesNotShrinkOnEmptyScanCancellationOrIncompleteCleanup()
    {
        FilterTemporaryBudget budget = new(100); object worker = new();
        Assert.True(budget.AcceptWorker(worker, 80));
        Assert.True(budget.ObserveWorker(worker, 0));
        Assert.Equal(80, budget.WorkerBytes); Assert.Equal(0, budget.ObservedWorkerBytes);
        budget.CompleteWorker(worker, 0, stoppedAndWorkspaceRemoved: false);
        Assert.Equal(80, budget.Total); Assert.False(budget.AcceptWorker(new object(), 21));
        budget.CompleteWorker(worker, 30, stoppedAndWorkspaceRemoved: false);
        Assert.Equal(80, budget.Total); Assert.Equal(30, budget.ObservedWorkerBytes);
        budget.CompleteWorker(worker, 0, stoppedAndWorkspaceRemoved: true);
        budget.CompleteWorker(worker, 0, stoppedAndWorkspaceRemoved: true);
        Assert.Equal(0, budget.Total); Assert.True(budget.AcceptWorker(new object(), 100));
    }

    [Fact]
    public void StageReservationsCannotConsumeAnEmptyWorkersAcceptedWorkspaceQuota()
    {
        using TempDirectory root = new();
        FilterTemporaryBudget budget = new(100); object worker = new();
        Assert.True(budget.AcceptWorker(worker, 70));
        FilterStageLedger ledger = new(root.Path, budget, 100); ledger.Reconcile();
        string path = Path.Combine(root.Path, "stage"); ledger.Reserve(path, 30);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Grow(path, 1));
        Assert.True(budget.ObserveWorker(worker, 70)); Assert.Equal(100, budget.Total);
        Assert.False(budget.ObserveWorker(worker, 71)); Assert.Equal(101, budget.Total);
        Assert.Throws<MediaFilterOmissionException>(() => ledger.Check());
    }

    [Fact]
    public async Task ConcurrentAdmissionsCannotBothTakeTheLastFileSlot()
    {
        using TempDirectory root = new();
        for (int i = 0; i < 1023; i++) File.WriteAllBytes(Path.Combine(root.Path, i.ToString()), []);
        FilterStageLedger ledger = new(root.Path, new(100), 100); ledger.Reconcile();
        TaskCompletionSource go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Create(string name)
        {
            await go.Task;
            string path = Path.Combine(root.Path, name);
            try { ledger.Reserve(path, 0); File.WriteAllBytes(path, []); return true; }
            catch (MediaFilterOmissionException) { return false; }
        }
        Task<bool> first = Create("first"), second = Create("second"); go.SetResult();
        Assert.Single((await Task.WhenAll(first, second)).Where(success => success));
        Assert.Equal(1024, Directory.GetFiles(root.Path).Length); Assert.Equal(1024, ledger.Files);
    }

}
