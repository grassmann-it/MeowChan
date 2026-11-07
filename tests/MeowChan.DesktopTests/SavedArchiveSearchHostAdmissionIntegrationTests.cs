using System.Reflection;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Real host, store and private-profile AtomicJsonFile IO; observers only stop real boundaries.
[Collection("Delivered plugin hosts")]
public sealed class SavedArchiveSearchHostAdmissionIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly Guid Seed = Guid.Parse("aa000000-0000-0000-0000-000000000001");
    private static readonly Guid Added = Guid.Parse("aa000000-0000-0000-0000-000000000002");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static ArchiveSearchSpec Definition() => ArchiveSearchSpec.FromExactIdentities("  untouched 😀  ",
        [" source "], [new(" source ", " board ")]);

    [Theory]
    [InlineData("Create")]
    [InlineData("Rename")]
    [InlineData("Replace")]
    [InlineData("Delete")]
    [InlineData("Recover")]
    public async Task AllEntriesBorrowAcceptedOwnerAcrossCommandWaitIoAndProjection(string entry)
    {
        await using Fixture f = await Fixture.StartAsync(entry == "Recover");
        using Barrier before = new(), after = new(), projection = new();
        ProfileAdmissionToken? first = null, last = null;
        int beforeCount = 0, afterCount = 0;
        f.Host.BeforeSavedArchiveSearchAdmittedMutationForTests = token =>
        {
            first = token; Interlocked.Increment(ref beforeCount);
            Assert.Same(token, Assert.Single(Field<HashSet<ProfileAdmissionToken>>(f.Lease.Admission, "_active")));
            before.Hold();
        };
        f.Host.AfterSavedArchiveSearchAdmittedMutationForTests = token =>
        { last = token; Interlocked.Increment(ref afterCount); after.Hold(); };
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        Task<SavedArchiveSearchSnapshot>? mutation = null;
        Task exclusive = Task.CompletedTask, capture = Task.CompletedTask, coreObserver = Task.CompletedTask;
        TaskCompletionSource granted = Signal(), resume = Signal(), coreReturned = Signal(), releaseCommands = Signal();
        using CancellationTokenSource cancel = new();
        List<string> sources = [" source "];
        ArchiveSearchSpec input = ArchiveSearchSpec.FromExactIdentities("  untouched 😀  ", sources,
            [new(" source ", " board ")]);
        long version = f.Host.Projection.Version;
        try
        {
            mutation = f.Dispatch(entry, input);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            // The spec already snapshots constructor input; host/store must preserve, not normalize it.
            sources.Clear(); sources.Add("late input");
            exclusive = f.Host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            }, cancel.Token);
            Assert.Throws<InvalidOperationException>(() => { using var denied = f.Lease.Admission.EnterOrdinaryWriter(); });
            Assert.Throws<InvalidOperationException>(() => f.Store.Rename(Seed, "nested", f.Current.Revision));
            // Different target even for Delete: prove kernel refusal, not duplicate-command Busy.
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.DeleteSavedArchiveSearchAsync(Added, f.Current.Revision));
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            capture = Task.Run(() => { lock (Field<object>(f.Host, "_viewCapture")) projection.Hold(); });
            await projection.Entered.Task.WaitAsync(Deadline);
            commands.Release(); held = false;
            await before.Entered.Task.WaitAsync(Deadline);
            Assert.NotNull(first); Assert.Equal(ProfileAdmissionKind.Ordinary, first.Kind);
            Assert.Equal(f.PrimaryBefore, File.ReadAllBytes(f.Path));
            Assert.False(mutation.IsCompleted); Assert.False(granted.Task.IsCompleted);
            before.Release(); await after.Entered.Task.WaitAsync(Deadline);
            Assert.Same(first, last); f.AssertPublished(entry);
            coreObserver = Task.Run(async () =>
            {
                Assert.True(await commands.WaitAsync(Deadline));
                try { coreReturned.TrySetResult(); await releaseCommands.Task.WaitAsync(Deadline); }
                finally { commands.Release(); }
            });
            after.Release(); await coreReturned.Task.WaitAsync(Deadline);
            Assert.False(mutation.IsCompleted); Assert.False(granted.Task.IsCompleted);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            Assert.True(f.Lease.Admission.RunWithToken(first!, () => true, ProfileAdmissionKind.Ordinary));
            projection.Release(); releaseCommands.TrySetResult();
            await coreObserver.WaitAsync(Deadline); await capture.WaitAsync(Deadline);
            Assert.Same(f.Current, await mutation.WaitAsync(Deadline));
            await granted.Task.WaitAsync(Deadline);
            Assert.Equal(1, beforeCount); Assert.Equal(1, afterCount);
            Assert.Equal(0, f.Host.AcceptedCommandCount); Assert.True(f.Host.Projection.Version > version);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(first!, () => true, ProfileAdmissionKind.Ordinary));
            f.AssertPublished(entry);
            if (entry is "Create" or "Replace")
                Assert.NotSame(input, Assert.Single(f.Current.Searches.Where(row => row.Id == (entry == "Create" ? Added : Seed))).Definition);
            resume.TrySetResult(); await exclusive.WaitAsync(Deadline);
        }
        finally
        {
            before.Release(); after.Release(); projection.Release(); releaseCommands.TrySetResult(); resume.TrySetResult(); cancel.Cancel();
            if (held) commands.Release();
            if (mutation != null) await Observe(mutation);
            await Observe(exclusive); await Observe(capture); await Observe(coreObserver); f.ClearHooks();
        }
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Rename")]
    [InlineData("Replace")]
    [InlineData("Delete")]
    [InlineData("Recover")]
    public async Task ShutdownBeforeInnerEntryDoesNotMutate(string entry)
    {
        await using Fixture f = await Fixture.StartAsync(entry == "Recover");
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        int entries = 0, completions = 0;
        SavedArchiveSearchSnapshot original = f.Current;
        string? recoverySource = Field<string?>(f.Store, "_recoverySource"), recoveryIdentity = Field<string?>(f.Store, "_recoveryIdentity");
        f.Host.BeforeSavedArchiveSearchAdmittedMutationForTests = _ => entries++;
        f.Host.AfterSavedArchiveSearchAdmittedMutationForTests = _ => completions++;
        Task mutation = f.Dispatch(entry, Definition()), stop = Task.CompletedTask;
        try
        {
            stop = f.Host.StopAsync(); Assert.False(stop.IsCompleted);
            commands.Release(); held = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => mutation.WaitAsync(Deadline));
            await stop.WaitAsync(Deadline);
            Assert.Equal(0, entries); Assert.Equal(f.PrimaryBefore, File.ReadAllBytes(f.Path));
            Assert.Equal(f.BackupBefore, File.ReadAllBytes(f.Path + ".bak")); f.AssertNoTemps();
            Assert.Equal(0, completions); Assert.Same(original, f.Current);
            Assert.Equal(recoverySource, Field<string?>(f.Store, "_recoverySource"));
            Assert.Equal(recoveryIdentity, Field<string?>(f.Store, "_recoveryIdentity"));
            Assert.Empty(System.IO.Directory.EnumerateFiles(f.Directory.Path, "archive-searches.json.corrupt-*"));
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
        }
        finally { if (held) commands.Release(); await Observe(mutation); await Observe(stop); f.ClearHooks(); }
    }

    [Theory]
    [InlineData("Create", false)] [InlineData("Rename", true)]
    [InlineData("Replace", false)] [InlineData("Delete", true)]
    [InlineData("Recover", false)] [InlineData("Recover", true)]
    public async Task ShutdownInsideMutationWaitsForRealPublicationAndReturn(string entry, bool afterCommit)
    {
        await using Fixture f = await Fixture.StartAsync(entry == "Recover");
        using Barrier before = new(), after = new();
        ProfileAdmissionToken? first = null;
        f.Host.BeforeSavedArchiveSearchAdmittedMutationForTests = token => { first = token; before.Hold(); };
        f.Host.AfterSavedArchiveSearchAdmittedMutationForTests = token => { Assert.Same(first, token); after.Hold(); };
        Task<SavedArchiveSearchSnapshot> mutation = f.Dispatch(entry, Definition());
        Task stop = Task.CompletedTask;
        try
        {
            await before.Entered.Task.WaitAsync(Deadline);
            Assert.NotNull(first); Assert.Equal(ProfileAdmissionKind.Ordinary, first.Kind);
            Assert.Equal(f.PrimaryBefore, File.ReadAllBytes(f.Path));
            Assert.Equal(f.BackupBefore, File.ReadAllBytes(f.Path + ".bak"));
            if (afterCommit) { before.Release(); await after.Entered.Task.WaitAsync(Deadline); }
            stop = f.Host.StopAsync(); Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            using CancellationTokenSource observerCancel = new();
            Task observer = f.Lease.Admission.DrainAsync(observerCancel.Token); observerCancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
            Assert.False(stop.IsCompleted); Assert.False(f.Host.IsProfileReleased);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Dispatch(entry, Definition()));
            before.Release(); await after.Entered.Task.WaitAsync(Deadline);
            f.AssertPublished(entry); Assert.False(mutation.IsCompleted); Assert.False(stop.IsCompleted);
            after.Release(); Assert.Same(f.Current, await mutation.WaitAsync(Deadline)); await stop.WaitAsync(Deadline);
            f.AssertPublished(entry); Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(first!, () => true, ProfileAdmissionKind.Ordinary));
        }
        finally { before.Release(); after.Release(); await Observe(mutation); await Observe(stop); f.ClearHooks(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveryCannotReplaceChangedReviewedPrimaryOrBackup(bool changeBackup)
    {
        await using Fixture f = await Fixture.StartAsync(true);
        int entries = 0; f.Host.BeforeSavedArchiveSearchAdmittedMutationForTests = _ => entries++;
        if (changeBackup)
            File.WriteAllText(f.Path + ".bak", File.ReadAllText(f.Path + ".bak").Replace("seed", "changed", StringComparison.Ordinal));
        else File.WriteAllText(f.Path, "{different-corruption");
        byte[] primary = File.ReadAllBytes(f.Path), backup = File.ReadAllBytes(f.Path + ".bak");
        await Assert.ThrowsAsync<SavedSearchRevisionException>(() => f.Dispatch("Recover", Definition()));
        Assert.Equal(0, entries); Assert.Equal(primary, File.ReadAllBytes(f.Path));
        Assert.Equal(backup, File.ReadAllBytes(f.Path + ".bak")); f.AssertNoTemps();
        Assert.Equal(SavedSearchStoreState.RecoveryAvailable, f.Current.State);
        Assert.NotNull(Field<string?>(f.Store, "_recoverySource"));
        Assert.NotNull(Field<string?>(f.Store, "_recoveryIdentity"));
    }

    [Fact]
    public async Task ActualReplaceFailureCleansOwnTempAndAllowsExplicitRetry()
    {
        await using Fixture f = await Fixture.StartAsync(false);
        // Calibrate this platform's real File.Replace failure, rather than accepting an exception palette.
        string source = System.IO.Path.Combine(f.Directory.Path, "probe-source"), dest = source + "-dest", backupDir = source + "-backup";
        File.WriteAllText(source, "new"); File.WriteAllText(dest, "old"); System.IO.Directory.CreateDirectory(backupDir);
        Exception expected = Assert.IsAssignableFrom<Exception>(Record.Exception(() => File.Replace(source, dest, backupDir)));
        string parked = f.Path + ".parked";
        int after = 0;
        f.Host.BeforeSavedArchiveSearchAdmittedMutationForTests = _ =>
        { File.Move(f.Path + ".bak", parked); System.IO.Directory.CreateDirectory(f.Path + ".bak"); };
        f.Host.AfterSavedArchiveSearchAdmittedMutationForTests = _ => after++;
        Exception actual = Assert.IsAssignableFrom<Exception>(await Record.ExceptionAsync(() => f.Dispatch("Rename", Definition())));
        Assert.Equal(expected.GetType(), actual.GetType()); Assert.Equal(expected.HResult, actual.HResult);
        Assert.Equal(0, after); Assert.Equal(f.PrimaryBefore, File.ReadAllBytes(f.Path));
        Assert.Equal(f.BackupBefore, File.ReadAllBytes(parked)); Assert.Equal("current", Assert.Single(f.Current.Searches).Name);
        f.AssertNoTemps(); f.ClearHooks();
        System.IO.Directory.Delete(f.Path + ".bak"); File.Move(parked, f.Path + ".bak");
        await f.Dispatch("Rename", Definition()).WaitAsync(Deadline); f.AssertPublished("Rename");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IdenticalValueCommitsOnceAndStaleRetryReleasesHostAdmission(bool replace)
    {
        await using Fixture f = await Fixture.StartAsync(false);
        SavedArchiveSearchSnapshot original = f.Current;
        int before = 0, after = 0;
        f.Host.BeforeSavedArchiveSearchAdmittedMutationForTests = _ => before++;
        f.Host.AfterSavedArchiveSearchAdmittedMutationForTests = _ => after++;
        Task<SavedArchiveSearchSnapshot> Save(long revision) => replace
            ? f.Host.ReplaceSavedArchiveSearchAsync(Seed, Assert.Single(original.Searches).Definition!, revision)
            : f.Host.RenameSavedArchiveSearchAsync(Seed, "current", revision);
        SavedArchiveSearchSnapshot saved = await Save(original.Revision).WaitAsync(Deadline);
        Assert.Same(saved, f.Current); Assert.Equal(original.Revision + 1, saved.Revision);
        Assert.Equal(Assert.Single(original.Searches), Assert.Single(saved.Searches));
        byte[] primary = File.ReadAllBytes(f.Path), backup = File.ReadAllBytes(f.Path + ".bak");
        SavedSearchRevisionException conflict = await Assert.ThrowsAsync<SavedSearchRevisionException>(() => Save(original.Revision).WaitAsync(Deadline));
        Assert.Equal(saved.Revision, conflict.Current.Revision);
        Assert.Equal(primary, File.ReadAllBytes(f.Path)); Assert.Equal(backup, File.ReadAllBytes(f.Path + ".bak"));
        Assert.Equal(1, before); Assert.Equal(1, after); f.AssertNoTemps();
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        // The real host lane must first drain its persistent producer epoch. Asking
        // the kernel directly would wait on those valid owners, not detect a leak.
        bool exclusiveGranted = false;
        await f.Host.RunWithProfileProducerExclusiveAsync(exclusive =>
        {
            Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
            exclusiveGranted = true;
            return Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true));
        }).WaitAsync(Deadline);
        Assert.True(exclusiveGranted);
        Assert.Equal(DesktopSessionState.Ready, f.Host.State);
        Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
    }

    private static async Task Observe(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch when (task.IsCompleted) { /* Observe completed failures on unwind; pending timeout is never a drain/PASS oracle. */ }
    }
    private sealed class Barrier : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        private readonly ManualResetEventSlim release = new();
        internal void Hold() { Entered.TrySetResult(); Assert.True(release.Wait(Deadline), "Boundary timed out."); }
        internal void Release() => release.Set();
        public void Dispose() => release.Dispose();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Directory { get; } = new();
        internal DesktopSessionHost Host { get; }
        internal ProfileLease Lease => Field<ProfileLease>(Host, "_profileLease");
        internal SavedArchiveSearchStore Store => Field<SavedArchiveSearchStore>(Host, "_savedArchiveSearchStore");
        internal SavedArchiveSearchSnapshot Current => Field<SavedArchiveSearchSnapshot>(Store, "_current");
        internal string Path => System.IO.Path.Combine(Directory.Path, "archive-searches.json");
        internal byte[] PrimaryBefore = [], BackupBefore = [];
        private long revision;
        private Fixture()
        {
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                DownloadPath = System.IO.Path.Combine(Directory.Path, "absent-media") }.SaveConfig(Directory.Path);
            Host = new(Directory.Path, Array.Empty<IImageBoard>());
        }
        internal static async Task<Fixture> StartAsync(bool recovery)
        {
            Fixture f = new();
            try
            {
                await f.Host.StartAsync().WaitAsync(Deadline);
                await f.Host.LoadSavedArchiveSearchesAsync().WaitAsync(Deadline);
                await f.Host.CreateSavedArchiveSearchAsync(Seed, "seed", new("initial"), 0).WaitAsync(Deadline);
                await f.Host.RenameSavedArchiveSearchAsync(Seed, "current", 1).WaitAsync(Deadline);
                if (recovery) { File.WriteAllText(f.Path, "{broken"); await f.Host.LoadSavedArchiveSearchesAsync().WaitAsync(Deadline); }
                f.revision = f.Current.Revision;
                f.PrimaryBefore = File.ReadAllBytes(f.Path); f.BackupBefore = File.ReadAllBytes(f.Path + ".bak");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task<SavedArchiveSearchSnapshot> Dispatch(string entry, ArchiveSearchSpec definition) => entry switch
        {
            "Create" => Host.CreateSavedArchiveSearchAsync(Added, "created", definition, revision),
            "Rename" => Host.RenameSavedArchiveSearchAsync(Seed, "renamed", revision),
            "Replace" => Host.ReplaceSavedArchiveSearchAsync(Seed, definition, revision),
            "Delete" => Host.DeleteSavedArchiveSearchAsync(Seed, revision),
            "Recover" => Host.RecoverSavedArchiveSearchesAsync(revision),
            _ => throw new ArgumentOutOfRangeException(nameof(entry))
        };
        internal void AssertNoTemps() => Assert.Empty(System.IO.Directory.EnumerateFiles(Directory.Path, ".archive-searches.json.*.tmp"));
        internal void AssertPublished(string entry)
        {
            Assert.Equal(revision + 1, Current.Revision); Assert.Equal(SavedSearchStoreState.Ready, Current.State);
            using JsonDocument disk = JsonDocument.Parse(File.ReadAllBytes(Path));
            Assert.Equal(Current.Revision, disk.RootElement.GetProperty("revision").GetInt64());
            var rows = disk.RootElement.GetProperty("searches").EnumerateArray().ToArray();
            Assert.Equal(Current.Searches.Count, rows.Length);
            foreach (SavedArchiveSearch row in Current.Searches)
            {
                var stored = Assert.Single(rows.Where(x => x.GetProperty("id").GetGuid() == row.Id));
                Assert.Equal(row.Name, stored.GetProperty("name").GetString());
                Assert.Equal(ArchiveSearchSpecJson.Serialize(row.Definition!),
                    ArchiveSearchSpecJson.Serialize(ArchiveSearchSpecJson.Deserialize(stored.GetProperty("definition").GetRawText())));
            }
            if (entry == "Delete") Assert.Empty(Current.Searches);
            else if (entry == "Create" || entry == "Replace")
                Assert.Equal(ArchiveSearchSpecJson.Serialize(Definition()), ArchiveSearchSpecJson.Serialize(Assert.Single(Current.Searches.Where(x => x.Id == (entry == "Create" ? Added : Seed))).Definition!));
            else Assert.Equal(entry == "Recover" ? "seed" : "renamed", Assert.Single(Current.Searches).Name);
            Assert.Equal(entry == "Recover" ? BackupBefore : PrimaryBefore, File.ReadAllBytes(Path + ".bak"));
            if (entry == "Recover")
            {
                Assert.Null(Field<string?>(Store, "_recoverySource")); Assert.Null(Field<string?>(Store, "_recoveryIdentity"));
                string evidence = Assert.Single(System.IO.Directory.EnumerateFiles(Directory.Path, "archive-searches.json.corrupt-*"));
                Assert.Equal(PrimaryBefore, File.ReadAllBytes(evidence));
            }
            AssertNoTemps();
        }
        internal void ClearHooks()
        {
            Host.BeforeSavedArchiveSearchAdmittedMutationForTests = null; Host.AfterSavedArchiveSearchAdmittedMutationForTests = null;
            Store.BeforeAdmittedMutationForTests = null; Store.AfterAdmittedMutationForTests = null;
        }
        public async ValueTask DisposeAsync()
        {
            if (Field<SavedArchiveSearchStore?>(Host, "_savedArchiveSearchStore") != null) ClearHooks();
            await Host.DisposeAsync().AsTask().WaitAsync(Deadline);
            if (Host.IsProfileReleased) Directory.Dispose();
        }
    }
}
