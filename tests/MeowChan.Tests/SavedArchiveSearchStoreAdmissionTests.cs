using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Tests;

// Synthetic physical profiles, real AtomicJsonFile operations; gates are causal, not timing probes.
public sealed class SavedArchiveSearchStoreAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly string[] Operations = ["create", "rename", "replace", "delete", "recover"];
    public static IEnumerable<object[]> Cases => Operations.SelectMany(op => new[] { new object[] { op, false }, new object[] { op, true } });

    [Theory, MemberData(nameof(Cases))]
    public async Task PublicMutatorsOwnOneEntryAndBorrowedMutatorsSurvivePendingExclusive(string op, bool ready)
    {
        using Fixture f = new(op);
        if (ready) f.Lease.Admission.CompleteStartup();
        ProfileAdmissionToken? observed = null;
        f.Store.BeforeAdmittedMutationForTests = token =>
        {
            observed = token;
            Assert.Same(token, Assert.Single(Active(f.Lease)));
            Assert.Equal(ready ? ProfileAdmissionKind.Ordinary : ProfileAdmissionKind.Startup, token.Kind);
        };
        f.AssertPublished(op, f.Public(op));
        Assert.NotNull(observed);
        Assert.Empty(Active(f.Lease));
        Assert.Empty(f.Temps());
        // A fresh fixture permits Delete and Recover too: the accepted owner starts
        // the real consumer only AFTER Exclusive has become pending.
        using Fixture borrowed = new(op);
        borrowed.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = borrowed.Lease.Admission.EnterOrdinaryWriter();
        Task<ProfileAdmissionToken> pending = borrowed.Lease.Admission.EnterExclusiveAsync().AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => borrowed.Public(op));
            borrowed.Store.BeforeAdmittedMutationForTests = token => Assert.Same(owner, token);
            borrowed.AssertPublished(op, borrowed.Borrow(op, owner));
            Assert.False(pending.IsCompleted);
            Assert.Same(owner, Assert.Single(Active(borrowed.Lease)));
        }
        finally { owner.Dispose(); (await pending.WaitAsync(Deadline)).Dispose(); }
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task EveryBorrowedConsumerPinsAtomicCleanupAndPublicationThroughRelease(string op, bool shutdown)
    {
        using Fixture f = new(op);
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterOrdinaryWriter();
        using Gate before = new(); using Gate after = new();
        f.Store.BeforeAdmittedMutationForTests = token => { Assert.Same(owner, token); before.Block(); };
        f.Store.AfterAdmittedMutationForTests = token =>
        {
            Assert.Same(owner, token);
            Assert.Empty(f.Temps());
            f.AssertPublished(op, f.Current);
            if (op == "recover")
            {
                Assert.Null(Field(f.Store, "_recoverySource")); Assert.Null(Field(f.Store, "_recoveryIdentity"));
            }
            after.Block();
        };
        Task<SavedArchiveSearchSnapshot> work = Task.Run(() => f.Borrow(op, owner));
        Task<ProfileAdmissionToken>? exclusive = null; Task? drain = null;
        try
        {
            await before.Reached.Task.WaitAsync(Deadline);
            if (shutdown) { f.Lease.Admission.BeginShutdown(); drain = f.Lease.Admission.DrainAsync(); }
            else exclusive = f.Lease.Admission.EnterExclusiveAsync().AsTask();
            owner.Dispose();
            Assert.Throws<InvalidOperationException>(() => f.Borrow(op, owner));
            before.Release();
            await after.Reached.Task.WaitAsync(Deadline);
            Assert.False(work.IsCompleted);
            if (drain != null) Assert.False(drain.IsCompleted);
            if (exclusive != null) Assert.False(exclusive.IsCompleted);
            after.Release();
            Assert.Same(await work.WaitAsync(Deadline), f.Current);
        }
        finally
        {
            before.Release(); after.Release();
            await ObserveForTeardown(work); owner.Dispose();
            if (exclusive != null) (await exclusive.WaitAsync(Deadline)).Dispose();
            if (drain != null) await drain.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("create"), InlineData("rename"), InlineData("replace"), InlineData("delete"), InlineData("recover")]
    public async Task InvalidAuthorityCannotExploitNoChangeOrRecoveryIdentity(string op)
    {
        using Fixture f = new(op); using Fixture foreign = new("create");
        using ProfileAdmissionToken other = foreign.Lease.Admission.EnterStartupWriter();
        using ProfileAdmissionToken disposed = f.Lease.Admission.EnterStartupWriter(); disposed.Dispose();
        // Deliberate negative allocation: not kernel-issued membership.
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Startup);
        string[] inventory = f.Inventory(); object? current = Field(f.Store, "_current");
        int hooks = 0; f.Store.BeforeAdmittedMutationForTests = _ => hooks++;
        foreach (ProfileAdmissionToken bad in new[] { null!, other, disposed, unissued })
        {
            Assert.Throws<InvalidOperationException>(() => f.Borrow(op, bad));
            Assert.Equal(inventory, f.Inventory()); Assert.Same(current, Field(f.Store, "_current"));
        }
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken exclusive = await f.Lease.Admission.EnterExclusiveAsync();
        Assert.Throws<InvalidOperationException>(() => f.Borrow(op, exclusive));
        Assert.Throws<InvalidOperationException>(() => f.Public(op));
        Assert.Equal(inventory, f.Inventory()); Assert.Equal(0, hooks);
        f.Store.Load(); // Passive read remains legal even under Exclusive.
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void UnchangedDefinitionOrNameStillRequiresIssuedAuthority(bool replace)
    {
        using Fixture f = new("rename");
        using ProfileAdmissionToken disposed = f.Lease.Admission.EnterStartupWriter(); disposed.Dispose();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Startup);
        string[] inventory = f.Inventory(); SavedArchiveSearchSnapshot current = f.Current;
        int hooks = 0; f.Store.BeforeAdmittedMutationForTests = _ => hooks++;
        foreach (ProfileAdmissionToken invalid in new[] { null!, disposed, unissued })
        {
            Assert.Throws<InvalidOperationException>(() => replace
                ? f.Store.ReplaceAdmitted(f.Id, Assert.Single(current.Searches).Definition!, current.Revision, invalid)
                : f.Store.RenameAdmitted(f.Id, "primary", current.Revision, invalid));
        }
        Assert.Equal(0, hooks); Assert.Same(current, f.Current); Assert.Equal(inventory, f.Inventory());
    }

    public static IEnumerable<object[]> PhaseCases => Operations.SelectMany(op => new[] { "failed", "closed", "stopping", "stopped" }.Select(phase => new object[] { op, phase }));
    [Theory, MemberData(nameof(PhaseCases))]
    public void SealedAdmissionRejectsEveryStandaloneMutatorWithoutSideEffects(string op, string phase)
    {
        using Fixture f = new(op);
        if (phase == "failed") f.Lease.Admission.FailStartup();
        else if (phase == "closed") f.Lease.Admission.Close();
        else { f.Lease.Admission.BeginShutdown(); if (phase == "stopped") f.Lease.Admission.CompleteShutdown(); }
        string[] inventory = f.Inventory(); SavedArchiveSearchSnapshot current = f.Current;
        int hooks = 0; f.Store.BeforeAdmittedMutationForTests = _ => hooks++;
        if (phase == "closed") Assert.Throws<ObjectDisposedException>(() => f.Public(op));
        else Assert.Throws<InvalidOperationException>(() => f.Public(op));
        Assert.Equal(0, hooks); Assert.Same(current, f.Current); Assert.Equal(inventory, f.Inventory());
        f.Store.Load(); // Sealing admission never turns this passive load into a writer.
        Assert.Equal(inventory, f.Inventory());
    }

    [Theory]
    [InlineData("create"), InlineData("rename"), InlineData("replace"), InlineData("delete"), InlineData("recover")]
    public void AcceptedTokenDoesNotReplacePhysicalLease(string op)
    {
        using Fixture f = new(op);
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterStartupWriter();
        int hooks = 0; f.Store.BeforeAdmittedMutationForTests = _ => hooks++;
        string[] inventory = f.Inventory(); SavedArchiveSearchSnapshot current = f.Current;
        f.Lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => f.Borrow(op, owner));
        Assert.Throws<ObjectDisposedException>(() => f.Public(op));
        Assert.Equal(0, hooks); Assert.Same(current, f.Current); Assert.Equal(inventory, f.Inventory());
    }

    [Theory]
    [InlineData("create"), InlineData("rename"), InlineData("replace"), InlineData("delete"), InlineData("recover")]
    public void CancellationBeforeAtomicPreservesFilesAndAfterCommitDoesNotUndoSuccess(string op)
    {
        using Fixture f = new(op); using CancellationTokenSource cancel = new();
        string[] inventory = f.Inventory();
        using CancellationTokenSource preCancelled = new(); preCancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => f.Public(op, preCancelled.Token));
        Assert.Equal(inventory, f.Inventory()); Assert.Empty(Active(f.Lease));
        f.Store.BeforeAdmittedMutationForTests = _ => cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => f.Public(op, cancel.Token));
        Assert.Equal(inventory, f.Inventory()); Assert.Empty(f.Temps()); Assert.Empty(Active(f.Lease));
        f.Store.BeforeAdmittedMutationForTests = null;
        using CancellationTokenSource late = new();
        f.Store.AfterAdmittedMutationForTests = _ => late.Cancel();
        SavedArchiveSearchSnapshot saved = f.Public(op, late.Token);
        Assert.True(late.IsCancellationRequested); Assert.Same(saved, f.Current);
        f.AssertPublished(op, saved);
        Assert.Empty(Active(f.Lease)); Assert.Empty(f.Temps());
    }

    [Fact]
    public void RealAtomicReplaceErrorCleansTempAndAllowsExplicitRetryWithSameOwner()
    {
        using Fixture f = new("rename");
        byte[] original = File.ReadAllBytes(f.Path), backup = File.ReadAllBytes(f.Path + ".bak");
        string parked = f.Path + ".bak.parked";
        string foreign = System.IO.Path.Combine(f.Directory, ".archive-searches.json.foreign.tmp");
        File.WriteAllText(foreign, "foreign");
        // Calibrate exactly the same filesystem primitive, never an exception palette.
        Exception expected = ReplaceDirectoryReference(f.Directory);
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterStartupWriter();
        int after = 0;
        f.Store.BeforeAdmittedMutationForTests = _ => { File.Move(f.Path + ".bak", parked); System.IO.Directory.CreateDirectory(f.Path + ".bak"); };
        f.Store.AfterAdmittedMutationForTests = _ => after++;
        try
        {
            Exception? failure = Record.Exception(() => f.Borrow("rename", owner));
            Assert.NotNull(failure);
            Assert.Equal(expected.GetType(), failure!.GetType()); Assert.Equal(expected.HResult, failure.HResult);
            Assert.Equal(original, File.ReadAllBytes(f.Path)); Assert.Equal(backup, File.ReadAllBytes(parked));
            Assert.Equal(new[] { foreign }, f.Temps()); Assert.Equal("foreign", File.ReadAllText(foreign)); Assert.Equal(0, after);
            Assert.Equal("primary", Assert.Single(f.Current.Searches).Name);
        }
        finally
        {
            System.IO.Directory.Delete(f.Path + ".bak"); File.Move(parked, f.Path + ".bak");
            f.Store.BeforeAdmittedMutationForTests = null;
        }
        Assert.Equal("renamed", Assert.Single(f.Borrow("rename", owner).Searches).Name);
        Assert.Equal(1, after); Assert.Equal(original, File.ReadAllBytes(f.Path + ".bak"));
        Assert.Equal(new[] { foreign }, f.Temps()); Assert.Equal("foreign", File.ReadAllText(foreign));
        Assert.True(f.Lease.Admission.RunWithToken(owner, () => true, ProfileAdmissionKind.Startup));
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void RecoveryRequiresBothReviewedIdentities(bool changeBackup)
    {
        using Fixture f = new("recover");
        File.AppendAllText(changeBackup ? f.Path + ".bak" : f.Path, " ");
        string[] inventory = f.Inventory();
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterStartupWriter();
        Assert.Throws<SavedSearchRevisionException>(() => f.Borrow("recover", owner));
        Assert.Equal(inventory, f.Inventory()); Assert.Empty(f.Temps());
    }

    [Theory]
    [InlineData("same-revision"), InlineData("stale-revision"), InlineData("future-primary"), InlineData("future-backup")]
    public void BorrowedWritesRetainDiskContentRevisionAndFutureFences(string kind)
    {
        using Fixture f = new("rename");
        if (kind == "same-revision") File.WriteAllText(f.Path, File.ReadAllText(f.Path).Replace("primary", "external", StringComparison.Ordinal));
        if (kind == "future-primary") File.WriteAllText(f.Path, "{\"schemaVersion\":99,\"foreign\":true}");
        if (kind == "future-backup") File.WriteAllText(f.Path + ".bak", "{\"schemaVersion\":99,\"foreign\":true}");
        string[] inventory = f.Inventory();
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterStartupWriter();
        int hooks = 0; f.Store.BeforeAdmittedMutationForTests = _ => hooks++;
        if (kind == "future-backup") Assert.Throws<ProfileBackupBlockedException>(() => f.Borrow("rename", owner));
        else Assert.Throws<SavedSearchRevisionException>(() => f.Store.RenameAdmitted(f.Id, "must not write",
            kind == "stale-revision" ? f.Current.Revision - 1 : f.Current.Revision, owner));
        Assert.Equal(0, hooks); Assert.Equal(inventory, f.Inventory()); Assert.Empty(f.Temps());
        Assert.True(f.Lease.Admission.RunWithToken(owner, () => true, ProfileAdmissionKind.Startup));
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void IdenticalValueIsStillARevisionedCommitAndStaleRetryCannotWrite(bool replace)
    {
        using Fixture f = new("rename");
        SavedArchiveSearchSnapshot original = f.Current;
        int before = 0, after = 0;
        f.Store.BeforeAdmittedMutationForTests = _ => before++;
        f.Store.AfterAdmittedMutationForTests = _ => after++;
        SavedArchiveSearchSnapshot Save(long revision) => replace
            ? f.Store.Replace(f.Id, Assert.Single(original.Searches).Definition!, revision)
            : f.Store.Rename(f.Id, "primary", revision);
        SavedArchiveSearchSnapshot saved = Save(original.Revision);
        Assert.Equal(original.Revision + 1, saved.Revision);
        Assert.Equal(Assert.Single(original.Searches), Assert.Single(saved.Searches));
        Assert.Equal(1, before); Assert.Equal(1, after);
        Assert.Empty(Active(f.Lease)); Assert.Empty(f.Temps());
        string[] committed = f.Inventory();
        SavedSearchRevisionException conflict = Assert.Throws<SavedSearchRevisionException>(() => Save(original.Revision));
        Assert.Equal(saved.Revision, conflict.Current.Revision);
        Assert.Equal(committed, f.Inventory()); Assert.Equal(1, before); Assert.Equal(1, after);
        Assert.Empty(Active(f.Lease)); Assert.Empty(f.Temps());
    }

    [Theory]
    [InlineData("create"), InlineData("rename"), InlineData("replace"), InlineData("delete"), InlineData("recover")]
    public void StartupConflictReleasesStandaloneOwnerButNotBorrowedOwner(string op)
    {
        using Fixture f = new(op);
        string[] inventory = f.Inventory();
        long stale = f.Current.Revision + 1;
        SavedArchiveSearchSnapshot Invoke(ProfileAdmissionToken? owner) => op switch
        {
            "create" => owner == null ? f.Store.Create(Guid.NewGuid(), "new", new(), stale)
                : f.Store.CreateAdmitted(Guid.NewGuid(), "new", new(), stale, owner),
            "rename" => owner == null ? f.Store.Rename(f.Id, "new", stale) : f.Store.RenameAdmitted(f.Id, "new", stale, owner),
            "replace" => owner == null ? f.Store.Replace(f.Id, new(), stale) : f.Store.ReplaceAdmitted(f.Id, new(), stale, owner),
            "delete" => owner == null ? f.Store.Delete(f.Id, stale) : f.Store.DeleteAdmitted(f.Id, stale, owner),
            _ => owner == null ? f.Store.Recover(stale) : f.Store.RecoverAdmitted(stale, owner)
        };
        int hooks = 0; f.Store.BeforeAdmittedMutationForTests = _ => hooks++;
        Assert.Throws<SavedSearchRevisionException>(() => Invoke(null));
        Assert.Empty(Active(f.Lease));
        using (ProfileAdmissionToken owner = f.Lease.Admission.EnterStartupWriter())
        {
            Assert.Throws<SavedSearchRevisionException>(() => Invoke(owner));
            Assert.Same(owner, Assert.Single(Active(f.Lease)));
            Assert.True(f.Lease.Admission.RunWithToken(owner, () => true, ProfileAdmissionKind.Startup));
        }
        Assert.Empty(Active(f.Lease)); Assert.Equal(inventory, f.Inventory());
        Assert.Equal(0, hooks); Assert.Empty(f.Temps());
        f.Lease.Admission.CompleteStartup();
        Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
    }

    private static Exception ReplaceDirectoryReference(string directory)
    {
        string root = System.IO.Path.Combine(directory, "replace-reference");
        System.IO.Directory.CreateDirectory(root);
        string source = System.IO.Path.Combine(root, "source"), destination = System.IO.Path.Combine(root, "destination"), backup = System.IO.Path.Combine(root, "backup");
        File.WriteAllText(source, "source"); File.WriteAllText(destination, "destination"); System.IO.Directory.CreateDirectory(backup);
        Exception? failure = Record.Exception(() => File.Replace(source, destination, backup));
        Assert.NotNull(failure);
        Assert.Equal("source", File.ReadAllText(source)); Assert.Equal("destination", File.ReadAllText(destination));
        Assert.True(System.IO.Directory.Exists(backup));
        return failure!;
    }
    private static async Task ObserveForTeardown(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch when (task.IsCompleted) { /* Body asserts outcome; observe actual return before fixture disposal. */ }
    }
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static ProfileAdmissionToken[] Active(ProfileLease lease) => ((IEnumerable<ProfileAdmissionToken>)Field(lease.Admission, "_active")!).ToArray();
    private sealed class Gate : IDisposable
    {
        internal TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        internal void Block() { Reached.TrySetResult(true); if (!_release.Wait(Deadline)) throw new TimeoutException("Store gate watchdog"); }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "saved-admission-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "archive-searches.json");
        internal readonly ProfileLease Lease;
        internal readonly SavedArchiveSearchStore Store;
        internal readonly Guid Id = Guid.NewGuid();
        private readonly long _revision;
        private readonly byte[] _primaryBefore, _backupBefore;
        internal SavedArchiveSearchSnapshot Current => (SavedArchiveSearchSnapshot)Field(Store, "_current")!;
        internal Fixture(string operation)
        {
            Lease = ProfileLease.Acquire(Directory); Store = new(Directory, Lease); Store.Load();
            Store.Create(Id, "backup", new ArchiveSearchSpec(), 0);
            Store.Rename(Id, "primary", 1);
            if (operation == "recover") { File.WriteAllText(Path, "damaged"); Store.Load(); }
            _revision = Current.Revision; _primaryBefore = File.ReadAllBytes(Path); _backupBefore = File.ReadAllBytes(Path + ".bak");
        }
        internal SavedArchiveSearchSnapshot Public(string op, CancellationToken token = default) => op switch
        {
            "create" => Store.Create(Guid.NewGuid(), "created", new ArchiveSearchSpec(), Current.Revision, token),
            "rename" => Store.Rename(Id, "renamed", Current.Revision, token),
            "replace" => Store.Replace(Id, new ArchiveSearchSpec("replacement"), Current.Revision, token),
            "delete" => Store.Delete(Id, Current.Revision, token),
            _ => Store.Recover(Current.Revision, token)
        };
        internal SavedArchiveSearchSnapshot Borrow(string op, ProfileAdmissionToken admission, CancellationToken token = default) => op switch
        {
            "create" => Store.CreateAdmitted(Guid.NewGuid(), "created", new ArchiveSearchSpec(), Current.Revision, admission, token),
            "rename" => Store.RenameAdmitted(Id, "renamed", Current.Revision, admission, token),
            "replace" => Store.ReplaceAdmitted(Id, new ArchiveSearchSpec("replacement"), Current.Revision, admission, token),
            "delete" => Store.DeleteAdmitted(Id, Current.Revision, admission, token),
            _ => Store.RecoverAdmitted(Current.Revision, admission, token)
        };
        internal void AssertPublished(string op, SavedArchiveSearchSnapshot saved)
        {
            Assert.Same(saved, Current); Assert.Equal(_revision + 1, saved.Revision);
            Assert.Equal(SavedSearchStoreState.Ready, saved.State);
            using JsonDocument disk = JsonDocument.Parse(File.ReadAllText(Path));
            Assert.Equal(saved.Revision, disk.RootElement.GetProperty("revision").GetInt64());
            JsonElement[] rows = disk.RootElement.GetProperty("searches").EnumerateArray().ToArray();
            Assert.Equal(saved.Searches.Count, rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                Assert.Equal(saved.Searches[i].Id, rows[i].GetProperty("id").GetGuid());
                Assert.Equal(saved.Searches[i].Name, rows[i].GetProperty("name").GetString());
                Assert.Equal(ArchiveSearchSpecJson.Serialize(saved.Searches[i].Definition!),
                    ArchiveSearchSpecJson.Serialize(ArchiveSearchSpecJson.Deserialize(rows[i].GetProperty("definition").GetRawText())));
            }
            if (op == "delete") Assert.Empty(saved.Searches);
            if (op == "rename") Assert.Equal("renamed", Assert.Single(saved.Searches).Name);
            if (op == "replace") Assert.Equal(new ArchiveSearchSpec("replacement"), Assert.Single(saved.Searches).Definition);
            if (op == "create") { Assert.Equal(2, saved.Searches.Count); Assert.Equal("created", saved.Searches[1].Name); }
            Assert.Equal(op == "recover" ? _backupBefore : _primaryBefore, File.ReadAllBytes(Path + ".bak"));
            if (op == "recover")
            {
                Assert.Equal("backup", Assert.Single(saved.Searches).Name);
                Assert.Equal(_primaryBefore, File.ReadAllBytes(Assert.Single(System.IO.Directory.GetFiles(Directory, "archive-searches.json.corrupt-*"))));
                Assert.Null(Field(Store, "_recoverySource")); Assert.Null(Field(Store, "_recoveryIdentity"));
            }
        }
        internal string[] Temps() => System.IO.Directory.GetFiles(Directory, ".archive-searches.json.*.tmp").Order().ToArray();
        // The lease deliberately holds these two lock handles with FileShare.None.
        // Inventory persistent data, not the active interprocess ownership handles.
        internal string[] Inventory() => System.IO.Directory.GetFiles(Directory)
            .Where(p => System.IO.Path.GetFileName(p) is not (".meowchan-profile.lock" or ".ochan-profile.lock"))
            .Order().Select(p => System.IO.Path.GetFileName(p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        public void Dispose() { Lease.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
}
