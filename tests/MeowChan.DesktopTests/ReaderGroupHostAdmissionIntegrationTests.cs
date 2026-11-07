using System.Reflection;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// W22: actual Host -> lazy ReaderGroupStore -> AtomicJsonFile; no substituted save tasks.
[Collection("Delivered plugin hosts")]
public sealed class ReaderGroupHostAdmissionIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly Guid GroupId = Guid.Parse("22b967ba-12c7-4e24-8e2e-3453185b789a");
    private static readonly ResourceId First = new("source-a", "board", "42");
    private static readonly ResourceId Second = new("source-b", "board", "42");
    private static readonly ResourceId TooLate = new("source-a", "board", "99");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static string Primary(Fixture f) => Path.Combine(f.Directory.Path, "reader-groups.json");
    private static ReaderGroupsSnapshot Draft(string name) => new(1, 0, [new(GroupId, name, [First, Second])]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupHostRefusesSaveAndRestoreUntilActualReadyThenPermitsRealRetry(bool restore)
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
            DownloadPath = Path.Combine(directory.Path, "absent-media") }.SaveConfig(directory.Path);
        string primary = Path.Combine(directory.Path, "reader-groups.json");
        // Seed through the actual standalone store and its issued Startup owner, before the host owns this profile.
        using (ProfileLease seedLease = ProfileLease.Acquire(directory.Path))
        {
            ReaderGroupStore seed = new(directory.Path, seedLease);
            ReaderGroupsSnapshot previous = await seed.SaveAsync(Draft(restore ? "restorable" : "previous"), 0).WaitAsync(Deadline);
            Assert.Equal(1, previous.Revision);
            if (restore)
            {
                await seed.SaveAsync(Draft("latest before damage"), previous.Revision).WaitAsync(Deadline);
                File.WriteAllText(primary, "{damaged startup primary W22");
            }
        }
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        ProfileLease lease = Field<ProfileLease>(host, "_profileLease");
        Assert.Equal(DesktopSessionState.Starting, host.State);
        Assert.Equal(ProfileAdmissionPhase.Starting, lease.Admission.Phase);
        Assert.Null(Field<ReaderGroupStore?>(host, "_readerGroupStore"));
        // Passive Load on the real host lease may inspect recovery; its content token grants no host admission.
        ReaderGroupsLoadResult initial = await new ReaderGroupStore(directory.Path, lease).LoadAsync().WaitAsync(Deadline);
        Assert.Equal(restore ? ReaderGroupsLoadState.RecoveryAvailable : ReaderGroupsLoadState.Ready, initial.State);
        Assert.Equal(!restore, initial.CanWrite);
        if (restore) Assert.NotNull(initial.RecoveryToken);
        Dictionary<string, byte[]> files = Inventory(directory.Path);
        ReaderGroupsSnapshot expected = new(1, 2, [new(GroupId, restore ? "restorable" : "captured", [First, Second])]);
        int before = 0, after = 0;
        ProfileAdmissionToken? admitted = null;
        void Before(ProfileAdmissionToken token)
        {
            Assert.Equal(ProfileAdmissionKind.Ordinary, token.Kind);
            admitted = token; Interlocked.Increment(ref before);
        }
        void After(ProfileAdmissionToken token)
        {
            Assert.Same(admitted, token); AssertSnapshot(expected, ReadDisk(primary));
            Assert.Empty(System.IO.Directory.EnumerateFiles(directory.Path, ".reader-groups.json.*.tmp"));
            Interlocked.Increment(ref after);
        }
        host.BeforeReaderGroupAdmittedSaveMutationForTests = Before;
        host.AfterReaderGroupAdmittedSaveMutationForTests = After;
        host.BeforeReaderGroupAdmittedRestoreMutationForTests = Before;
        host.AfterReaderGroupAdmittedRestoreMutationForTests = After;
        Task<ReaderGroupsSnapshot> Mutate() => restore ? host.RestoreReaderGroupsBackupAsync(initial.RecoveryToken!)
            : host.SaveReaderGroupsAsync(Draft("captured"), 1);
        try
        {
            // No scheduler/timing assumption: this real host has not been asked to start.
            await Assert.ThrowsAsync<InvalidOperationException>(() => Mutate().WaitAsync(Deadline));
            Assert.Equal(DesktopSessionState.Starting, host.State);
            Assert.Equal(ProfileAdmissionPhase.Starting, lease.Admission.Phase);
            Assert.Equal(0, host.AcceptedCommandCount); Assert.Equal(0, before); Assert.Equal(0, after);
            Assert.Null(admitted); Assert.Null(Field<ReaderGroupStore?>(host, "_readerGroupStore"));
            AssertInventory(files, Inventory(directory.Path));
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.Equal(ProfileAdmissionPhase.Ready, lease.Admission.Phase);
            ReaderGroupsLoadResult loaded = await host.LoadReaderGroupsAsync().WaitAsync(Deadline);
            Assert.Equal(initial.State, loaded.State); AssertSnapshot(initial.Snapshot!, loaded.Snapshot!);
            Assert.Equal(initial.RecoveryToken, loaded.RecoveryToken);
            AssertInventory(files, Inventory(directory.Path));
            AssertSnapshot(expected, await Mutate().WaitAsync(Deadline));
            Assert.Equal(1, before); Assert.Equal(1, after); Assert.Equal(0, host.AcceptedCommandCount);
            Assert.NotNull(admitted);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(admitted!, () => true, ProfileAdmissionKind.Ordinary));
            AssertSnapshot(expected, ReadDisk(primary));
            Assert.Equal(files[restore ? "reader-groups.json.bak" : "reader-groups.json"], File.ReadAllBytes(primary + ".bak"));
            string[] preserved = System.IO.Directory.EnumerateFiles(directory.Path, "reader-groups.json.corrupt-*").ToArray();
            if (restore) Assert.Equal(files["reader-groups.json"], File.ReadAllBytes(Assert.Single(preserved)));
            else Assert.Empty(preserved);
            Assert.Empty(System.IO.Directory.EnumerateFiles(directory.Path, ".reader-groups.json.*.tmp"));
            ReaderGroupsLoadResult readback = await host.LoadReaderGroupsAsync().WaitAsync(Deadline);
            Assert.Equal(ReaderGroupsLoadState.Ready, readback.State); AssertSnapshot(expected, readback.Snapshot!);
        }
        finally
        {
            host.BeforeReaderGroupAdmittedSaveMutationForTests = null; host.AfterReaderGroupAdmittedSaveMutationForTests = null;
            host.BeforeReaderGroupAdmittedRestoreMutationForTests = null; host.AfterReaderGroupAdmittedRestoreMutationForTests = null;
            if (Field<ReaderGroupStore?>(host, "_readerGroupStore") is { } store)
            {
                store.BeforeAdmittedSaveMutationForTests = null; store.AfterAdmittedSaveMutationForTests = null;
                store.BeforeAdmittedRestoreMutationForTests = null; store.AfterAdmittedRestoreMutationForTests = null;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedSaveAndRestoreBorrowCommandAcrossExclusiveThroughPhysicalWorkAndProjection(bool restore)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await PrepareAsync(f, restore);
        using Barrier before = new(), after = new(), projection = new();
        ProfileAdmissionToken? entered = null, completed = null;
        f.SetHooks(restore, token => { entered = token; before.Hold(); }, token => { completed = token; after.Hold(); });
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool commandsHeld = true;
        Task<ReaderGroupsSnapshot>? mutation = null;
        Task owner = Task.CompletedTask, capture = Task.CompletedTask, coreObserver = Task.CompletedTask;
        TaskCompletionSource granted = Signal(), resume = Signal(), coreReturned = Signal(), releaseCommands = Signal();
        using CancellationTokenSource cancelExclusive = new();
        long version = f.Host.Projection.Version;
        try
        {
            mutation = attempt.Start(f.Host);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            attempt.ChangeCallerInput(); // Both mutable group list and nested member list change AFTER acceptance.
            owner = f.Host.RunWithProfileProducerExclusiveAsync(async token =>
            {
                Assert.Equal(ProfileAdmissionKind.Exclusive, token.Kind);
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            }, cancelExclusive.Token);
            Assert.Throws<InvalidOperationException>(() => { using var denied = f.Lease.Admission.EnterOrdinaryWriter(); });
            // A different command, not the duplicate Save/Restore Busy lane.
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.LoadReaderGroupsAsync());
            if (restore)
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.RestoreBackupAsync(attempt.RecoveryToken!));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SaveAsync(attempt.Expected, attempt.Expected.Revision - 1));
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            AssertInventory(attempt.BeforeFiles, Inventory(f));
            capture = Task.Run(() => { lock (Field<object>(f.Host, "_viewCapture")) projection.Hold(); });
            await projection.Entered.Task.WaitAsync(Deadline);
            commands.Release(); commandsHeld = false;
            await before.Entered.Task.WaitAsync(Deadline);
            Assert.NotNull(entered); Assert.Equal(ProfileAdmissionKind.Ordinary, entered.Kind);
            AssertInventory(attempt.BeforeFiles, Inventory(f));
            Assert.False(mutation.IsCompleted); Assert.False(granted.Task.IsCompleted);
            before.Release();
            await after.Entered.Task.WaitAsync(Deadline);
            Assert.Same(entered, completed);
            AssertCommitted(f, attempt);
            Assert.False(mutation.IsCompleted); Assert.False(granted.Task.IsCompleted);
            // Acquiring the real serializer proves the real Store/command core returned, not just Atomic commit.
            coreObserver = Task.Run(async () =>
            {
                Assert.True(await commands.WaitAsync(Deadline));
                try { coreReturned.TrySetResult(); await releaseCommands.Task.WaitAsync(Deadline); }
                finally { commands.Release(); }
            });
            after.Release();
            await coreReturned.Task.WaitAsync(Deadline);
            AssertCommitted(f, attempt);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            Assert.False(mutation.IsCompleted); Assert.False(granted.Task.IsCompleted);
            Assert.True(f.Lease.Admission.RunWithToken(entered!, () => true, ProfileAdmissionKind.Ordinary));
            projection.Release(); releaseCommands.TrySetResult();
            await coreObserver.WaitAsync(Deadline); await capture.WaitAsync(Deadline);
            AssertSnapshot(attempt.Expected, await mutation.WaitAsync(Deadline));
            await granted.Task.WaitAsync(Deadline);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(entered!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.True(f.Host.Projection.Version > version);
            AssertCommitted(f, attempt);
            // Passive store Load does not acquire a new writer, even during the real Exclusive grant.
            ReaderGroupsLoadResult passive = await f.Store.LoadAsync().WaitAsync(Deadline);
            Assert.Equal(ReaderGroupsLoadState.Ready, passive.State);
            AssertSnapshot(attempt.Expected, passive.Snapshot!);
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            AssertSnapshot(attempt.Expected, (await f.Host.LoadReaderGroupsAsync().WaitAsync(Deadline)).Snapshot!);
        }
        finally
        {
            before.Release(); after.Release(); projection.Release(); releaseCommands.TrySetResult(); resume.TrySetResult();
            cancelExclusive.Cancel(); if (commandsHeld) commands.Release();
            if (mutation != null) await Observe(mutation);
            await Observe(owner); await Observe(capture); await Observe(coreObserver); f.ClearHooks();
        }
    }

    [Theory]
    [InlineData(false, "entry")]
    [InlineData(false, "before")]
    [InlineData(false, "after")]
    [InlineData(true, "entry")]
    [InlineData(true, "before")]
    [InlineData(true, "after")]
    public async Task CallerCancellationPreservesActualCommitAndAllowsReadyRetry(bool restore, string cancelAt)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await PrepareAsync(f, restore);
        using CancellationTokenSource cancel = new();
        using Barrier before = new(), after = new();
        ProfileAdmissionToken? entered = null, completed = null;
        f.SetHooks(restore, token => { entered = token; before.Hold(); },
            token => { completed = token; after.Hold(); });
        if (cancelAt == "entry") cancel.Cancel();
        Task<ReaderGroupsSnapshot> mutation = attempt.Start(f.Host, cancel.Token);
        try
        {
            if (cancelAt != "entry")
            {
                await before.Entered.Task.WaitAsync(Deadline);
                Assert.NotNull(entered); Assert.Equal(ProfileAdmissionKind.Ordinary, entered.Kind);
                AssertInventory(attempt.BeforeFiles, Inventory(f));
                if (cancelAt == "after")
                {
                    before.Release(); await after.Entered.Task.WaitAsync(Deadline);
                    Assert.Same(entered, completed); AssertCommitted(f, attempt);
                }
                cancel.Cancel();
                // Caller cancellation cannot release authority while actual synchronous work is held.
                Assert.False(mutation.IsCompleted); Assert.Equal(1, f.Host.AcceptedCommandCount);
                Assert.True(f.Lease.Admission.RunWithToken(entered!, () => true, ProfileAdmissionKind.Ordinary));
            }
            before.Release(); after.Release();
            if (cancelAt == "after")
            {
                AssertSnapshot(attempt.Expected, await mutation.WaitAsync(Deadline));
                Assert.False(mutation.IsCanceled); AssertCommitted(f, attempt);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.WaitAsync(Deadline));
                Assert.True(mutation.IsCanceled);
                Assert.Null(completed); Assert.False(after.Entered.Task.IsCompleted);
                if (cancelAt == "entry") { Assert.Null(entered); Assert.False(before.Entered.Task.IsCompleted); }
                AssertInventory(attempt.BeforeFiles, Inventory(f));
            }
            Assert.True(cancel.IsCancellationRequested);
            Assert.Equal(DesktopSessionState.Ready, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            if (entered != null)
                Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(entered, () => true, ProfileAdmissionKind.Ordinary));
            f.ClearHooks();
            if (cancelAt != "after")
            {
                // Same real recovery token remains fresh when the canceled attempt wrote nothing.
                AssertSnapshot(attempt.Expected, await attempt.Start(f.Host).WaitAsync(Deadline));
                AssertCommitted(f, attempt);
            }
            AssertSnapshot(attempt.Expected, (await f.Host.LoadReaderGroupsAsync().WaitAsync(Deadline)).Snapshot!);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
        }
        finally { before.Release(); after.Release(); await Observe(mutation); f.ClearHooks(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownBeforeCommandEntryCancelsSaveAndRestoreWithoutInventingPersistence(bool restore)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await PrepareAsync(f, restore);
        int before = 0, after = 0;
        f.SetHooks(restore, _ => Interlocked.Increment(ref before), _ => Interlocked.Increment(ref after));
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        Task<ReaderGroupsSnapshot> mutation = attempt.Start(f.Host);
        Task stop = Task.CompletedTask;
        TaskCompletionSource lifetimeCanceled = Signal();
        using var registration = Field<CancellationTokenSource>(f.Host, "_startupCancellation").Token.Register(() => lifetimeCanceled.TrySetResult());
        try
        {
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            stop = f.Host.StopAsync();
            Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            // The real linked shutdown token, not a timer, establishes cancellation before opening the serializer.
            await lifetimeCanceled.Task.WaitAsync(Deadline);
            Assert.False(stop.IsCompleted); Assert.False(f.Host.IsProfileReleased);
            commands.Release(); held = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.WaitAsync(Deadline));
            await stop.WaitAsync(Deadline);
            Assert.Equal(0, before); Assert.Equal(0, after);
            AssertInventory(attempt.BeforeFiles, Inventory(f));
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
        }
        finally { if (held) commands.Release(); await Observe(mutation); await Observe(stop); f.ClearHooks(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShutdownDrainsActualSaveAndRestoreAndDistinguishesBeforeAtomicFromAfterCommit(bool restore, bool stopAfterCommit)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await PrepareAsync(f, restore);
        using Barrier before = new(), after = new();
        ProfileAdmissionToken? first = null, last = null;
        f.SetHooks(restore, token => { first = token; before.Hold(); }, token => { last = token; after.Hold(); });
        Task<ReaderGroupsSnapshot> mutation = attempt.Start(f.Host);
        Task stop = Task.CompletedTask;
        TaskCompletionSource lifetimeCanceled = Signal();
        using var registration = Field<CancellationTokenSource>(f.Host, "_startupCancellation").Token.Register(() => lifetimeCanceled.TrySetResult());
        try
        {
            await before.Entered.Task.WaitAsync(Deadline);
            Assert.NotNull(first); Assert.Equal(ProfileAdmissionKind.Ordinary, first.Kind);
            AssertInventory(attempt.BeforeFiles, Inventory(f));
            if (stopAfterCommit)
            {
                before.Release(); await after.Entered.Task.WaitAsync(Deadline);
                Assert.Same(first, last); AssertCommitted(f, attempt);
            }
            stop = f.Host.StopAsync();
            Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            await lifetimeCanceled.Task.WaitAsync(Deadline);
            using CancellationTokenSource cancelObserver = new();
            Task observer = f.Lease.Admission.DrainAsync(cancelObserver.Token);
            cancelObserver.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
            Assert.False(mutation.IsCompleted); Assert.False(stop.IsCompleted); Assert.False(f.Host.IsProfileReleased);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => attempt.Start(f.Host));
            if (stopAfterCommit)
            {
                AssertCommitted(f, attempt);
                after.Release();
                // Existing post-commit behavior: shutdown is not a fictitious rollback or failed save.
                AssertSnapshot(attempt.Expected, await mutation.WaitAsync(Deadline));
            }
            else
            {
                before.Release();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation.WaitAsync(Deadline));
                Assert.False(after.Entered.Task.IsCompleted); Assert.Null(last);
                AssertInventory(attempt.BeforeFiles, Inventory(f));
            }
            await stop.WaitAsync(Deadline);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
            if (stopAfterCommit) AssertCommitted(f, attempt);
            else AssertInventory(attempt.BeforeFiles, Inventory(f));
        }
        finally { before.Release(); after.Release(); await Observe(mutation); await Observe(stop); f.ClearHooks(); }
    }

    [Fact]
    public async Task NoopStillBorrowsAcceptedAuthorityAndPassiveReadsDoNotBecomeWriters()
    {
        await using Fixture f = await Fixture.StartAsync();
        ReaderGroupsSnapshot saved = await f.Host.SaveReaderGroupsAsync(Draft("unchanged"), 0).WaitAsync(Deadline);
        Dictionary<string, byte[]> files = Inventory(f);
        int mutations = 0;
        f.SetHooks(false, _ => Interlocked.Increment(ref mutations), _ => Interlocked.Increment(ref mutations));
        f.SetHooks(true, _ => Interlocked.Increment(ref mutations), _ => Interlocked.Increment(ref mutations));
        AssertSnapshot(saved, (await f.Host.LoadReaderGroupsAsync().WaitAsync(Deadline)).Snapshot!);
        var candidates = await f.Host.FindReaderGroupCandidatesAsync("", 50).WaitAsync(Deadline);
        Assert.Empty(candidates.Items); Assert.False(candidates.HasMore);
        var resolved = await f.Host.ResolveReaderGroupMembersAsync([First, Second]).WaitAsync(Deadline);
        Assert.Equal(new[] { First, Second }, resolved.Select(item => item.Id).ToArray());
        Assert.All(resolved, item => { Assert.False(item.HasLocalPosts); Assert.False(item.Watched); });
        Assert.Equal(0, mutations); AssertInventory(files, Inventory(f));
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        Task<ReaderGroupsSnapshot> noop = f.Host.SaveReaderGroupsAsync(saved, saved.Revision);
        Task owner = Task.CompletedTask;
        TaskCompletionSource granted = Signal(), resume = Signal();
        using CancellationTokenSource cancel = new();
        try
        {
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            owner = f.Host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            }, cancel.Token);
            Assert.Throws<InvalidOperationException>(() => { using var denied = f.Lease.Admission.EnterOrdinaryWriter(); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SaveAsync(saved, saved.Revision));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ReaderGroupStore(f.Directory.Path, f.Lease).SaveAsync(saved, saved.Revision));
            // Existing Host read commands remain admitted commands; they are not an authority bypass.
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.FindReaderGroupCandidatesAsync("", 50));
            AssertSnapshot(saved, (await f.Store.LoadAsync().WaitAsync(Deadline)).Snapshot!);
            Assert.False(noop.IsCompleted); Assert.False(granted.Task.IsCompleted);
            commands.Release(); held = false;
            AssertSnapshot(saved, await noop.WaitAsync(Deadline));
            await granted.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SaveReaderGroupsAsync(saved, saved.Revision));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SaveAsync(saved, saved.Revision));
            AssertSnapshot(saved, (await f.Store.LoadAsync().WaitAsync(Deadline)).Snapshot!);
            Assert.Equal(0, f.Host.AcceptedCommandCount); Assert.Equal(0, mutations);
            AssertInventory(files, Inventory(f));
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            AssertSnapshot(saved, await f.Host.SaveReaderGroupsAsync(saved, saved.Revision).WaitAsync(Deadline));
            Assert.Equal(0, mutations); AssertInventory(files, Inventory(f));
        }
        finally
        {
            if (held) commands.Release(); resume.TrySetResult(); cancel.Cancel();
            await Observe(noop); await Observe(owner); f.ClearHooks();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryContentTokenRemainsARealFreshnessFenceAndFailedAttemptReleasesCommand(bool changeBackup)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await PrepareAsync(f, restore: true);
        int before = 0, after = 0;
        f.SetHooks(true, _ => Interlocked.Increment(ref before), _ => Interlocked.Increment(ref after));
        // Either real file can invalidate the previous Load token; neither creates authority.
        if (changeBackup)
        {
            ReaderGroupsSnapshot backup = ReadDisk(Primary(f) + ".bak");
            ReaderGroupsSnapshot changedBackup = backup with
            { Groups = [backup.Groups[0] with { Name = "changed restorable backup" }] };
            File.WriteAllText(Primary(f) + ".bak", JsonSerializer.Serialize(changedBackup,
                ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot));
        }
        else File.WriteAllText(Primary(f), "{different damaged primary W22");
        Dictionary<string, byte[]> changed = Inventory(f);
        await Assert.ThrowsAsync<ReaderGroupsConflictException>(() => f.Host.RestoreReaderGroupsBackupAsync(attempt.RecoveryToken!).WaitAsync(Deadline));
        Assert.Equal(0, f.Host.AcceptedCommandCount); Assert.Equal(0, before); Assert.Equal(0, after);
        AssertInventory(changed, Inventory(f));
        ReaderGroupsLoadResult current = await f.Host.LoadReaderGroupsAsync().WaitAsync(Deadline);
        Assert.Equal(ReaderGroupsLoadState.RecoveryAvailable, current.State); Assert.False(current.CanWrite);
        Assert.NotNull(current.RecoveryToken); Assert.NotEqual(attempt.RecoveryToken, current.RecoveryToken);
        AssertInventory(changed, Inventory(f));
        Assert.NotNull(current.Snapshot);
        Attempt fresh = attempt with { RecoveryToken = current.RecoveryToken, BeforeFiles = changed,
            Expected = current.Snapshot! with { Revision = current.Snapshot!.Revision + 1 } };
        AssertSnapshot(fresh.Expected, await fresh.Start(f.Host).WaitAsync(Deadline));
        Assert.Equal(1, before); Assert.Equal(1, after); Assert.Equal(0, f.Host.AcceptedCommandCount);
        AssertCommitted(f, fresh);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("revision")]
    [InlineData("groups")]
    public async Task HostObservesActualAtomicFailureAndRequiresExactDurableReadback(string diskAfter)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await PrepareAsync(f, restore: false);
        ReaderGroupsSnapshot external = diskAfter == "revision" ? attempt.Expected with { Revision = attempt.Expected.Revision + 1 }
            : diskAfter == "groups" ? new(1, attempt.Expected.Revision, [new(GroupId, "different durable groups", [First, Second])])
            : attempt.Expected;
        // Establish the actual platform exception for Replace-to-directory, not an injected exception/save task.
        string reference = Path.Combine(f.Directory.Path, "replace-reference");
        System.IO.Directory.CreateDirectory(reference);
        string source = Path.Combine(reference, "source"), target = Path.Combine(reference, "target"), backup = Path.Combine(reference, "backup");
        File.WriteAllText(source, "source"); File.WriteAllText(target, "target"); System.IO.Directory.CreateDirectory(backup);
        Exception referenceFailure = Record.Exception(() => File.Replace(source, target, backup))!;
        Assert.NotNull(referenceFailure);
        Assert.Equal("source", File.ReadAllText(source)); Assert.Equal("target", File.ReadAllText(target));
        using Barrier after = new();
        ProfileAdmissionToken? entered = null;
        int completions = 0;
        f.SetHooks(false, token =>
        {
            entered = token;
            // Explicit external-file postcondition fixture. NOT a claim that native Replace threw after committing.
            File.WriteAllText(Primary(f), JsonSerializer.Serialize(external, ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot));
            System.IO.Directory.CreateDirectory(Primary(f) + ".bak");
        }, token =>
        {
            Assert.Same(entered, token); Interlocked.Increment(ref completions);
            AssertSnapshot(attempt.Expected, ReadDisk(f)); AssertTempCleanup(f, attempt);
            after.Hold();
        });
        Task<ReaderGroupsSnapshot> mutation = attempt.Start(f.Host);
        try
        {
            if (diskAfter == "exact")
            {
                await after.Entered.Task.WaitAsync(Deadline);
                Assert.Equal(1, f.Host.AcceptedCommandCount); Assert.False(mutation.IsCompleted);
                Assert.True(f.Lease.Admission.RunWithToken(entered!, () => true, ProfileAdmissionKind.Ordinary));
                Assert.True(System.IO.Directory.Exists(Primary(f) + ".bak"));
                after.Release(); AssertSnapshot(attempt.Expected, await mutation.WaitAsync(Deadline));
                Assert.Equal(1, completions);
            }
            else
            {
                Exception? failure = await Record.ExceptionAsync(() => mutation.WaitAsync(Deadline));
                Assert.IsType(referenceFailure.GetType(), failure);
                Assert.Equal(referenceFailure.HResult, failure!.HResult);
                Assert.Equal(0, completions); Assert.False(after.Entered.Task.IsCompleted);
            }
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.NotNull(entered);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(entered!, () => true, ProfileAdmissionKind.Ordinary));
            AssertSnapshot(external, ReadDisk(f)); AssertTempCleanup(f, attempt);
            Assert.True(System.IO.Directory.Exists(Primary(f) + ".bak"));
            Assert.Empty(System.IO.Directory.EnumerateFiles(f.Directory.Path, "reader-groups.json.corrupt-*"));
            // Actual host Load reports disk, not a requested-value/authority cache masking the failed save.
            AssertSnapshot(external, (await f.Host.LoadReaderGroupsAsync().WaitAsync(Deadline)).Snapshot!);
        }
        finally { after.Release(); await Observe(mutation); f.ClearHooks(); }
    }

    private static async Task<Attempt> PrepareAsync(Fixture f, bool restore)
    {
        ReaderGroupsSnapshot saved = await f.Host.SaveReaderGroupsAsync(Draft(restore ? "restorable" : "previous"), 0).WaitAsync(Deadline);
        string? recoveryToken = null;
        List<ResourceId> members = [First, Second];
        List<ReaderGroup> groups = [new(GroupId, " captured ", members)];
        ReaderGroupsSnapshot draft = new(1, saved.Revision, groups);
        ReaderGroupsSnapshot expected = new(1, saved.Revision + 1, [new(GroupId, "captured", [First, Second])]);
        if (restore)
        {
            await f.Host.SaveReaderGroupsAsync(Draft("latest before damage"), saved.Revision).WaitAsync(Deadline);
            File.WriteAllText(Primary(f), "{damaged primary W22");
            ReaderGroupsLoadResult recovery = await f.Host.LoadReaderGroupsAsync().WaitAsync(Deadline);
            Assert.Equal(ReaderGroupsLoadState.RecoveryAvailable, recovery.State); Assert.False(recovery.CanWrite);
            Assert.NotNull(recovery.RecoveryToken); recoveryToken = recovery.RecoveryToken;
            AssertSnapshot(saved, recovery.Snapshot!);
            expected = new(1, saved.Revision + 1, [new(GroupId, "restorable", [First, Second])]);
        }
        // Cleanup may remove its own random Atomictemp, never sweep a foreign file.
        File.WriteAllText(Path.Combine(f.Directory.Path, ".reader-groups.json.foreign.tmp"), "foreign W22 sentinel");
        return new(restore, draft, saved.Revision, recoveryToken, expected, groups, members, Inventory(f));
    }

    private sealed record Attempt(bool Restore, ReaderGroupsSnapshot Input, long ExpectedRevision, string? RecoveryToken,
        ReaderGroupsSnapshot Expected, List<ReaderGroup> CallerGroups, List<ResourceId> CallerMembers,
        Dictionary<string, byte[]> BeforeFiles)
    {
        internal Task<ReaderGroupsSnapshot> Start(DesktopSessionHost host, CancellationToken token = default) => Restore
            ? host.RestoreReaderGroupsBackupAsync(RecoveryToken!, token) : host.SaveReaderGroupsAsync(Input, ExpectedRevision, token);
        internal void ChangeCallerInput()
        {
            if (Restore) return; // Recovery token is an immutable content fence, not an admission token.
            CallerMembers.Clear(); CallerMembers.Add(TooLate);
            CallerGroups.Clear(); CallerGroups.Add(new(GroupId, "too late", [TooLate]));
        }
    }

    private static Dictionary<string, byte[]> Inventory(Fixture f) => Inventory(f.Directory.Path);
    private static Dictionary<string, byte[]> Inventory(string directory) => System.IO.Directory
        .EnumerateFiles(directory, "*reader-groups.json*")
        .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);
    private static void AssertInventory(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }
    private static void AssertSnapshot(ReaderGroupsSnapshot expected, ReaderGroupsSnapshot actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion); Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Groups.Count, actual.Groups.Count);
        for (int index = 0; index < expected.Groups.Count; index++)
        {
            Assert.Equal(expected.Groups[index].Id, actual.Groups[index].Id);
            Assert.Equal(expected.Groups[index].Name, actual.Groups[index].Name);
            Assert.Equal(expected.Groups[index].Members.ToArray(), actual.Groups[index].Members.ToArray());
        }
    }
    private static ReaderGroupsSnapshot ReadDisk(Fixture f) => ReadDisk(Primary(f));
    private static ReaderGroupsSnapshot ReadDisk(string primary)
    {
        ReaderGroupsSnapshot? disk = JsonSerializer.Deserialize(File.ReadAllBytes(primary), ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot);
        Assert.NotNull(disk); return disk;
    }
    private static void AssertTempCleanup(Fixture f, Attempt attempt)
    {
        string[] temps = System.IO.Directory.EnumerateFiles(f.Directory.Path, ".reader-groups.json.*.tmp").ToArray();
        Assert.Equal(".reader-groups.json.foreign.tmp", Path.GetFileName(Assert.Single(temps)));
        Assert.Equal(attempt.BeforeFiles[".reader-groups.json.foreign.tmp"], File.ReadAllBytes(temps[0]));
    }
    private static void AssertCommitted(Fixture f, Attempt attempt)
    {
        AssertSnapshot(attempt.Expected, ReadDisk(f)); AssertTempCleanup(f, attempt);
        if (attempt.Restore)
        {
            Assert.Equal(attempt.BeforeFiles["reader-groups.json.bak"], File.ReadAllBytes(Primary(f) + ".bak"));
            string preserved = Assert.Single(System.IO.Directory.EnumerateFiles(f.Directory.Path, "reader-groups.json.corrupt-*"));
            Assert.Equal(attempt.BeforeFiles["reader-groups.json"], File.ReadAllBytes(preserved));
        }
        else
        {
            Assert.Equal(attempt.BeforeFiles["reader-groups.json"], File.ReadAllBytes(Primary(f) + ".bak"));
            Assert.Empty(System.IO.Directory.EnumerateFiles(f.Directory.Path, "reader-groups.json.corrupt-*"));
        }
    }
    private static async Task Observe(Task task)
    { try { await task.WaitAsync(Deadline); } catch when (task.IsCompleted) { /* Observe only ended work; a timeout must not authorize profile disposal. */ } }
    private sealed class Barrier : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        private readonly ManualResetEventSlim _release = new();
        internal void Hold() { Entered.TrySetResult(); Assert.True(_release.Wait(Deadline), "Inner barrier was not released."); }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Directory { get; } = new();
        internal DesktopSessionHost Host { get; }
        internal ProfileLease Lease => Field<ProfileLease>(Host, "_profileLease");
        internal ReaderGroupStore Store => Field<ReaderGroupStore>(Host, "_readerGroupStore");
        private Fixture()
        {
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                DownloadPath = Path.Combine(Directory.Path, "absent-media") }.SaveConfig(Directory.Path);
            Host = new(Directory.Path, Array.Empty<IImageBoard>());
        }
        internal static async Task<Fixture> StartAsync()
        {
            Fixture f = new();
            try
            {
                await f.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State);
                ReaderGroupsLoadResult empty = await f.Host.LoadReaderGroupsAsync().WaitAsync(Deadline);
                Assert.Equal(ReaderGroupsLoadState.Ready, empty.State); Assert.Empty(empty.Snapshot!.Groups);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal void SetHooks(bool restore, Action<ProfileAdmissionToken> before, Action<ProfileAdmissionToken> after)
        {
            if (restore)
            {
                Host.BeforeReaderGroupAdmittedRestoreMutationForTests = before;
                Host.AfterReaderGroupAdmittedRestoreMutationForTests = after;
            }
            else
            {
                Host.BeforeReaderGroupAdmittedSaveMutationForTests = before;
                Host.AfterReaderGroupAdmittedSaveMutationForTests = after;
            }
        }
        internal void ClearHooks()
        {
            Host.BeforeReaderGroupAdmittedSaveMutationForTests = null;
            Host.AfterReaderGroupAdmittedSaveMutationForTests = null;
            Host.BeforeReaderGroupAdmittedRestoreMutationForTests = null;
            Host.AfterReaderGroupAdmittedRestoreMutationForTests = null;
            // Host copies the observers to this same lazy instance, not to a substitute persistence owner.
            if (Field<ReaderGroupStore?>(Host, "_readerGroupStore") is { } store)
            {
                store.BeforeAdmittedSaveMutationForTests = null; store.AfterAdmittedSaveMutationForTests = null;
                store.BeforeAdmittedRestoreMutationForTests = null; store.AfterAdmittedRestoreMutationForTests = null;
            }
        }
        public async ValueTask DisposeAsync()
        {
            ClearHooks(); await Host.DisposeAsync().AsTask().WaitAsync(Deadline);
            if (Host.IsProfileReleased) Directory.Dispose();
        }
    }
}
