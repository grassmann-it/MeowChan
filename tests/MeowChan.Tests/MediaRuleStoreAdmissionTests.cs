using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.Tests;

// Real synthetic profiles; observations are inside the real synchronous store pin.
// Existing MediaRuleStoreTests, templates, and active workers remain untouched.
public sealed class MediaRuleStoreAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static ResourceId Target => new("synthetic", "g", "42");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneSaveOwnsExactlyOneStartupOrOrdinaryUntilRulesAndResolverArePublished(bool ready)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet original = store.Current;
        if (ready) lease.Admission.CompleteStartup();
        ProfileAdmissionToken? observed = null;
        int before = 0, after = 0;
        store.BeforeAdmittedSaveMutationForTests = token =>
        {
            observed = token;
            Assert.Equal(ready ? ProfileAdmissionKind.Ordinary : ProfileAdmissionKind.Startup, token.Kind);
            Assert.Same(token, OnlyActiveOwner(lease));
            AssertVisible(store, original, blocked: false);
            Assert.False(File.Exists(StorePath(directory)));
            before++;
        };
        store.AfterAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(observed, token);
            Assert.Same(token, OnlyActiveOwner(lease));
            AssertPublished(directory, store, store.Current, blocked: true);
            Assert.Empty(CatalogTemporaryFiles(directory));
            after++;
        };
        MediaRuleSet saved = await store.SaveAsync([BlockAll()]).WaitAsync(Deadline);
        AssertPublished(directory, store, saved, blocked: true);
        Assert.Equal(1, before);
        Assert.Equal(1, after);
        if (!ready) lease.Admission.CompleteStartup();
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync().AsTask().WaitAsync(Deadline);
        Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(observed!, () => true,
            ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary));
        MediaRuleStore restarted = new(directory.Path, lease);
        Assert.Equal(saved.Revision, (await restarted.LoadAsync()).RuleSet.Revision);
        AssertVisible(restarted, restarted.Current, blocked: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidBorrowedAuthorityCannotSaveEvenTheSameEmptyOrNonemptyRules(bool empty)
    {
        using TempDirectory directory = new(), otherDirectory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ProfileLease otherLease = ProfileLease.Acquire(otherDirectory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet original = await store.SaveAsync(empty ? [] : [BlockAll()]);
        using ProfileAdmissionToken foreign = otherLease.Admission.EnterStartupWriter();
        // Allocation is intentionally not issuance. Negative membership fixture only.
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Startup);
        ProfileAdmissionToken released = lease.Admission.EnterStartupWriter();
        released.Dispose();
        int hooks = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => hooks++;
        store.AfterAdmittedSaveMutationForTests = _ => hooks++;
        string[] bytes = Inventory(directory);
        foreach (ProfileAdmissionToken invalid in new[] { null!, foreign, released, unissued })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAdmittedAsync(original.Rules, null, invalid));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAdmittedAsync(original.Rules, original.Revision, invalid));
        }
        lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAdmittedAsync(original.Rules, null, exclusive));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(original.Rules));
            MediaRuleStore readOnly = new(directory.Path, lease);
            Assert.Equal(original.Revision, (await readOnly.LoadAsync()).RuleSet.Revision);
            AssertVisible(readOnly, readOnly.Current, blocked: !empty);
        }
        AssertVisible(store, original, blocked: !empty);
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
    }

    [Fact]
    public async Task BorrowedTokenDoesNotReplaceTheLivePhysicalLeaseCheck()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int hooks = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => hooks++;
        store.AfterAdmittedSaveMutationForTests = _ => hooks++;
        MediaRuleSet original = store.Current;
        string[] bytes = Inventory(directory);
        lease.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAdmittedAsync([BlockAll()], null, accepted));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync([BlockAll()]));
        AssertVisible(store, original, blocked: false);
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("stopping")]
    [InlineData("stopped")]
    [InlineData("closed")]
    public async Task ClosedAdmissionRefusesStandaloneWhilePassiveLoadPreservesFiles(string phase)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet original = await store.SaveAsync([BlockAll()]);
        if (phase == "failed") lease.Admission.FailStartup();
        else if (phase == "closed") lease.Admission.Close();
        else
        {
            lease.Admission.BeginShutdown();
            if (phase == "stopped") lease.Admission.CompleteShutdown();
        }
        string[] bytes = Inventory(directory);
        if (phase == "closed")
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync(original.Rules));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(original.Rules));
        Assert.Equal(original.Revision, (await store.LoadAsync()).RuleSet.Revision);
        AssertVisible(store, store.Current, blocked: true);
        Assert.Equal(bytes, Inventory(directory));
    }

    [Fact]
    public async Task PendingExclusiveRefusesNewEntriesButThePreviouslyAcceptedBorrowPublishes()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            string[] bytes = Inventory(directory);
            Assert.False(pending.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([]));
            MediaRuleStore readOnly = new(directory.Path, lease);
            Assert.Empty((await readOnly.LoadAsync()).RuleSet.Rules);
            Assert.Equal(bytes, Inventory(directory));
            store.BeforeAdmittedSaveMutationForTests = token => Assert.Same(accepted, token);
            store.AfterAdmittedSaveMutationForTests = token => Assert.Same(accepted, token);
            MediaRuleSet saved = await store.SaveAdmittedAsync([BlockAll()], null, accepted);
            AssertPublished(directory, store, saved, blocked: true);
            Assert.False(pending.IsCompleted); // Borrow did not dispose the command's owner.
            accepted.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAdmittedAsync(saved.Rules, null, accepted));
        }
        finally
        {
            accepted.Dispose();
            cancel.Cancel();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedOwnerStillPinsActualIoCleanupAndRulesResolverPublicationUntilReturn(bool shutdown)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet original = await store.SaveAsync([]);
        byte[] primary = File.ReadAllBytes(StorePath(directory));
        EffectiveMediaRule previous = store.Resolve(Target);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancelExclusive = new();
        using StepBarrier before = new(), after = new();
        store.BeforeAdmittedSaveMutationForTests = token => { Assert.Same(accepted, token); before.Block(); };
        store.AfterAdmittedSaveMutationForTests = token => { Assert.Same(accepted, token); after.Block(); };
        Task<MediaRuleSet> save = store.SaveAdmittedAsync([BlockAll()], original.Revision, accepted);
        Task<ProfileAdmissionToken>? pending = null;
        Task? drain = null;
        try
        {
            await before.Reached.WaitAsync(Deadline);
            AssertVisible(store, original, blocked: false);
            Assert.Equal(primary, File.ReadAllBytes(StorePath(directory)));
            if (shutdown)
            {
                lease.Admission.BeginShutdown();
                drain = lease.Admission.DrainAsync();
                using CancellationTokenSource observer = new();
                Task cancelled = lease.Admission.DrainAsync(observer.Token);
                observer.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
                Assert.False(drain.IsCompleted);
            }
            else pending = lease.Admission.EnterExclusiveAsync(cancelExclusive.Token).AsTask();
            accepted.Dispose();
            Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(accepted,
                () => true, ProfileAdmissionKind.Ordinary));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAdmittedAsync([], null, accepted));
            Assert.False(save.IsCompleted);
            if (pending != null) Assert.False(pending.IsCompleted);
            before.Release();
            await after.Reached.WaitAsync(Deadline);
            AssertPublished(directory, store, store.Current, blocked: true);
            Assert.Equal(primary, File.ReadAllBytes(StorePath(directory) + ".bak"));
            Assert.Empty(CatalogTemporaryFiles(directory));
            Assert.Equal(original.Revision, previous.Revision);
            Assert.Equal(MediaTypeSelectionMode.All, previous.Types.Mode); // Old resolution is immutable.
            Assert.False(save.IsCompleted);
            if (pending != null) Assert.False(pending.IsCompleted);
            if (drain != null) Assert.False(drain.IsCompleted);
            after.Release();
            MediaRuleSet saved = await save.WaitAsync(Deadline);
            AssertPublished(directory, store, saved, blocked: true);
            if (pending != null) (await pending.WaitAsync(Deadline)).Dispose();
            if (drain != null)
            {
                await drain.WaitAsync(Deadline);
                lease.Admission.CompleteShutdown();
            }
        }
        finally
        {
            before.Release(); after.Release();
            await ObserveForTeardownAsync(save);
            accepted.Dispose();
            cancelExclusive.Cancel();
            if (pending != null)
            {
                try { (await pending.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("before")]
    [InlineData("after")]
    public async Task CancellationBeforeCommitDoesNotActivateAndLateCancellationCannotUndoPublishedResolver(string cancelAt)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet original = await store.SaveAsync([]);
        lease.Admission.CompleteStartup();
        string foreign = Path.Combine(directory.Path, ".media_rules.json.foreign.tmp");
        File.WriteAllText(foreign, "foreign bytes");
        string[] bytes = Inventory(directory);
        using CancellationTokenSource cancel = new();
        int before = 0, after = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => { before++; if (cancelAt == "before") cancel.Cancel(); };
        store.AfterAdmittedSaveMutationForTests = _ => { after++; if (cancelAt == "after") cancel.Cancel(); };
        if (cancelAt == "entry") cancel.Cancel();
        Task<MediaRuleSet> save = store.SaveAsync([BlockAll()], original.Revision, cancel.Token);
        if (cancelAt == "after")
        {
            MediaRuleSet saved = await save.WaitAsync(Deadline);
            Assert.True(cancel.IsCancellationRequested);
            Assert.False(save.IsCanceled);
            AssertPublished(directory, store, saved, blocked: true);
            Assert.Equal(1, before);
            Assert.Equal(1, after);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(Deadline));
            Assert.True(save.IsCanceled);
            AssertVisible(store, original, blocked: false);
            Assert.Equal(bytes, Inventory(directory));
            Assert.Equal(cancelAt == "entry" ? 0 : 1, before);
            Assert.Equal(0, after);
        }
        Assert.Equal("foreign bytes", File.ReadAllText(foreign));
        Assert.Equal(new[] { foreign }, CatalogTemporaryFiles(directory));
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task ActualReplaceFailureCleansOwnTempKeepsOldResolverAndAllowsRetryWithSameBorrowedOwner()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        await store.SaveAsync([BlockAll()]);
        MediaRuleSet original = await store.SaveAsync([]);
        string path = StorePath(directory), parked = path + ".bak.fixture-parked";
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        string foreign = Path.Combine(directory.Path, ".media_rules.json.foreign.tmp");
        File.WriteAllText(foreign, "foreign bytes");
        Exception reference = ReplaceBackupDirectoryReference(directory);
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int before = 0, after = 0;
        store.BeforeAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(accepted, token);
            Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
            File.Move(path + ".bak", parked);
            Directory.CreateDirectory(path + ".bak");
            before++;
        };
        store.AfterAdmittedSaveMutationForTests = _ => after++;
        try
        {
            // Successful catalog/backup reads precede Before. After it, a valid
            // uncancelled Save must write+flush its temp before File.Replace fails.
            Exception? failure = await Record.ExceptionAsync(() => store.SaveAdmittedAsync([BlockAll()], null, accepted));
            Assert.IsType(reference.GetType(), failure);
            Assert.Equal(reference.HResult, failure!.HResult);
            Assert.Equal(1, before);
            Assert.Equal(0, after);
            AssertVisible(store, original, blocked: false);
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(backup, File.ReadAllBytes(parked));
            Assert.True(Directory.Exists(path + ".bak"));
            Assert.Equal(new[] { foreign }, CatalogTemporaryFiles(directory));
            Assert.Equal("foreign bytes", File.ReadAllText(foreign));
            Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
        }
        finally
        {
            if (Directory.Exists(path + ".bak")) Directory.Delete(path + ".bak");
            if (File.Exists(parked)) File.Move(parked, path + ".bak");
            store.BeforeAdmittedSaveMutationForTests = null;
        }
        MediaRuleSet retried = await store.SaveAdmittedAsync([BlockAll()], null, accepted);
        AssertPublished(directory, store, retried, blocked: true);
        Assert.Equal(1, after);
        Assert.Equal(primary, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(new[] { foreign }, CatalogTemporaryFiles(directory));
        Assert.Equal("foreign bytes", File.ReadAllText(foreign));
        accepted.Dispose();
        lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalExpectedRevisionStillDistinguishesNullFromStaleOrEmptyAndAcceptsMatchingRevision(bool borrowed)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet first = await store.SaveAsync([BlockAll()]);
        MediaRuleSet original = await store.SaveAsync([]);
        using ProfileAdmissionToken? accepted = borrowed ? lease.Admission.EnterStartupWriter() : null;
        Task<MediaRuleSet> Save(IEnumerable<MediaRule> rules, string? revision) => borrowed
            ? store.SaveAdmittedAsync(rules, revision, accepted!) : store.SaveAsync(rules, revision);
        string[] bytes = Inventory(directory);
        foreach (string stale in new[] { first.Revision, string.Empty })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Save([BlockAll()], stale));
        Assert.Equal(bytes, Inventory(directory));
        AssertVisible(store, original, blocked: false);
        MediaRuleSet withoutExpected = await Save([BlockAll()], null);
        AssertPublished(directory, store, withoutExpected, blocked: true);
        MediaRuleSet withExpected = await Save([], withoutExpected.Revision);
        AssertPublished(directory, store, withExpected, blocked: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalDiskRevisionFenceRemainsMandatoryWhenExpectedRevisionIsOmitted(bool provideExpected)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet original = await store.SaveAsync([BlockAll()]);
        MediaRuleStore other = new(directory.Path, lease);
        await other.LoadAsync();
        await other.SaveAsync([]);
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int hooks = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => hooks++;
        store.AfterAdmittedSaveMutationForTests = _ => hooks++;
        string[] bytes = Inventory(directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAdmittedAsync([], provideExpected ? original.Revision : null, accepted));
        AssertVisible(store, original, blocked: true);
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
    }

    [Theory]
    [InlineData("future-primary")]
    [InlineData("future-backup")]
    [InlineData("invalid-json")]
    public async Task FutureAndUnrecoverableFilesCannotPublishNewRulesOrResolver(string invalid)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        await store.SaveAsync([BlockAll()]);
        MediaRuleSet original = await store.SaveAsync([]);
        string path = StorePath(directory);
        if (invalid.StartsWith("future", StringComparison.Ordinal))
            File.WriteAllText(invalid == "future-primary" ? path : path + ".bak", "{\"SchemaVersion\":99,\"Foreign\":true}");
        else
        {
            File.Delete(path + ".bak");
            File.WriteAllText(path, "invalid JSON");
        }
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int hooks = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => hooks++;
        store.AfterAdmittedSaveMutationForTests = _ => hooks++;
        string[] bytes = Inventory(directory);
        if (invalid.StartsWith("future", StringComparison.Ordinal))
            await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveAdmittedAsync([BlockAll()], null, accepted));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAdmittedAsync([BlockAll()], null, accepted));
        AssertVisible(store, original, blocked: false);
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRecoverySaveKeepsWarningsPassiveAndPreservesCorruptEvidenceAndBackup(bool corruptBackup)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet first = await store.SaveAsync([BlockAll()]);
        MediaRuleSet second = await store.SaveAsync([]);
        string path = StorePath(directory);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        const string corrupt = "broken JSON to preserve";
        File.WriteAllText(corruptBackup ? path + ".bak" : path, corrupt);
        string[] bytes = Inventory(directory);
        MediaRuleLoadResult loaded = await store.LoadAsync();
        Assert.Equal(!corruptBackup, loaded.Recovered);
        if (corruptBackup) Assert.Null(loaded.Warning);
        else Assert.False(string.IsNullOrWhiteSpace(loaded.Warning));
        Assert.Equal(corruptBackup ? second.Revision : first.Revision, loaded.RuleSet.Revision);
        AssertVisible(store, loaded.RuleSet, blocked: !corruptBackup);
        Assert.Equal(bytes, Inventory(directory));
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int after = 0;
        store.BeforeAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(accepted, token);
            Assert.Equal(bytes, Inventory(directory)); // No automatic preserve/repair before Before.
        };
        store.AfterAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(accepted, token);
            AssertPublished(directory, store, store.Current, blocked: true);
            Assert.Empty(CatalogTemporaryFiles(directory));
            after++;
        };
        MediaRuleSet saved = await store.SaveAdmittedAsync([BlockAll()], loaded.RuleSet.Revision, accepted);
        AssertPublished(directory, store, saved, blocked: true);
        Assert.Equal(1, after);
        string preserved = Assert.Single(Directory.GetFiles(directory.Path,
            corruptBackup ? "media_rules.json.bak.corrupt-*" : "media_rules.json.corrupt-*"));
        Assert.Equal(corrupt, File.ReadAllText(preserved));
        Assert.Equal(corruptBackup ? primary : backup, File.ReadAllBytes(path + ".bak"));
        MediaRuleStore restarted = new(directory.Path, lease);
        MediaRuleLoadResult reloaded = await restarted.LoadAsync();
        Assert.False(reloaded.Recovered);
        Assert.Null(reloaded.Warning);
        Assert.Equal(saved.Revision, reloaded.RuleSet.Revision);
        AssertVisible(restarted, reloaded.RuleSet, blocked: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputIsFrozenOnCallingThreadBeforeTaskReturnAndResolverSeesOnlyDurableSnapshot(bool borrowed)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet original = store.Current;
        EffectiveMediaRule oldResolution = store.Resolve(Target);
        using ProfileAdmissionToken? accepted = borrowed ? lease.Admission.EnterStartupWriter() : null;
        using StepBarrier before = new();
        store.BeforeAdmittedSaveMutationForTests = _ => before.Block();
        List<string> extensions = ["jpg"];
        Dictionary<string, string> parameters = new() { ["enabled"] = "false", ["count"] = "0", ["empty"] = "" };
        MediaRule frozenRule = new(MediaRuleScope.Global, types: new(MediaTypeSelectionMode.Selected, extensions),
            sizeLimit: new(MediaSizeLimitMode.Limited, 64))
        { ImageFilter = new(MediaImageFilterMode.Plugin, "synthetic.plugin", "filter", "7", parameters) };
        List<MediaRule> values = [frozenRule];
        CallingThreadEnumerable input = new(values);
        Task<MediaRuleSet> save = borrowed
            ? store.SaveAdmittedAsync(input, original.Revision, accepted!) : store.SaveAsync(input, original.Revision);
        try
        {
            Assert.Equal(1, input.Enumerations);
            Assert.Equal(input.CallingThread, input.EnumerationThread);
            values.Clear(); values.Add(BlockAll());
            extensions.Add("png"); parameters["enabled"] = "true";
            await before.Reached.WaitAsync(Deadline);
            AssertVisible(store, original, blocked: false);
            Assert.False(File.Exists(StorePath(directory)));
            before.Release();
            MediaRuleSet saved = await save.WaitAsync(Deadline);
            Assert.Same(saved, store.Current);
            MediaRuleStore restarted = new(directory.Path, lease);
            Assert.Equal(saved.Revision, (await restarted.LoadAsync()).RuleSet.Revision);
            MediaRule persisted = Assert.Single(restarted.Current.Rules);
            Assert.Equal(new[] { ".jpg" }, persisted.Types.Extensions);
            Assert.Equal(64L, persisted.SizeLimit.Bytes);
            Assert.Equal("7", persisted.ImageFilter.SchemaVersion);
            Assert.Equal("false", persisted.ImageFilter.Parameters["enabled"]);
            Assert.Equal("0", persisted.ImageFilter.Parameters["count"]);
            Assert.Equal("", persisted.ImageFilter.Parameters["empty"]);
            foreach (MediaRuleStore current in new[] { store, restarted })
            {
                EffectiveMediaRule resolved = current.Resolve(Target);
                Assert.Equal(saved.Revision, resolved.Revision);
                Assert.Equal(MediaRuleScope.Global, resolved.TypesOrigin.Scope);
                Assert.Equal(saved.Revision, resolved.ImageFilterOrigin!.Revision);
                Assert.Equal(MediaSelectionOutcome.Selected, current.Evaluate(Target, "jpg", 64).Outcome);
                Assert.Equal(MediaSelectionReason.SizeExceeded, current.Evaluate(Target, "jpg", 65).Reason);
                Assert.Equal(MediaSelectionReason.TypeExcluded, current.Evaluate(Target, "png", 1).Reason);
                Assert.Equal(MediaSelectionReason.AllowedUnknownSize, current.Evaluate(Target, "jpg", null).Reason);
            }
            Assert.Equal(original.Revision, oldResolution.Revision);
            Assert.Equal(MediaTypeSelectionMode.All, oldResolution.Types.Mode);
        }
        finally { before.Release(); await ObserveForTeardownAsync(save); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ObserverFailureIsVisibleWithoutRollbackOfDurableRulesResolverOrBorrowedOwnership(bool afterCommit, bool disposedFailure)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet original = await store.SaveAsync([]);
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        Exception marker = disposedFailure ? new ObjectDisposedException("synthetic-media-rule-observer")
            : new InvalidOperationException("synthetic-media-rule-observer");
        int before = 0, after = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => { before++; if (!afterCommit) throw marker; };
        store.AfterAdmittedSaveMutationForTests = _ => { after++; throw marker; };
        string[] bytes = Inventory(directory);
        Exception? failure = await Record.ExceptionAsync(() => store.SaveAdmittedAsync([BlockAll()], null, accepted));
        Assert.Same(marker, failure);
        Assert.Equal(1, before);
        Assert.Equal(afterCommit ? 1 : 0, after);
        if (afterCommit) AssertPublished(directory, store, store.Current, blocked: true);
        else
        {
            AssertVisible(store, original, blocked: false);
            Assert.Equal(bytes, Inventory(directory));
        }
        Assert.Empty(CatalogTemporaryFiles(directory));
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
        store.BeforeAdmittedSaveMutationForTests = null;
        store.AfterAdmittedSaveMutationForTests = null;
        MediaRuleSet retry = await store.SaveAdmittedAsync([], store.Current.Revision, accepted);
        AssertPublished(directory, store, retry, blocked: false);
        accepted.Dispose();
        lease.Admission.CompleteStartup();
    }

    private static MediaRule BlockAll() => new(MediaRuleScope.Global, types: new(MediaTypeSelectionMode.Selected));
    private static string StorePath(TempDirectory directory) => Path.Combine(directory.Path, "media_rules.json");
    private static string[] CatalogTemporaryFiles(TempDirectory directory) =>
        Directory.GetFiles(directory.Path, ".media_rules.json.*.tmp").Order(StringComparer.Ordinal).ToArray();
    private static string[] Inventory(TempDirectory directory) => Directory.GetFileSystemEntries(directory.Path)
        .Where(path => Path.GetFileName(path) is not (".meowchan-profile.lock" or ".ochan-profile.lock"))
        .Order(StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .ToArray();

    private static void AssertVisible(MediaRuleStore store, MediaRuleSet expected, bool blocked)
    {
        Assert.Same(expected, store.Current);
        foreach (EffectiveMediaRule rule in new[] { store.Resolve(Target), store.Resolve(MediaRuleScope.ForThread(Target.Source, Target.Board, Target.Thread!)) })
        {
            Assert.Equal(expected.Revision, rule.Revision);
            Assert.Equal(expected.Revision, rule.TypesOrigin.Revision);
            Assert.Equal(expected.Revision, rule.SizeLimitOrigin.Revision);
            Assert.Equal(blocked ? MediaTypeSelectionMode.Selected : MediaTypeSelectionMode.All, rule.Types.Mode);
            if (blocked) Assert.Empty(rule.Types.Extensions);
        }
        MediaSelectionResult selected = store.Evaluate(Target, "jpg", 1);
        Assert.Equal(expected.Revision, selected.EffectiveRule.Revision);
        Assert.Equal(blocked ? MediaSelectionOutcome.Excluded : MediaSelectionOutcome.Selected, selected.Outcome);
        Assert.Equal(blocked ? MediaSelectionReason.TypeExcluded : MediaSelectionReason.Allowed, selected.Reason);
    }

    private static void AssertPublished(TempDirectory directory, MediaRuleStore store, MediaRuleSet expected, bool blocked)
    {
        AssertVisible(store, expected, blocked);
        using JsonDocument disk = JsonDocument.Parse(File.ReadAllBytes(StorePath(directory)));
        Assert.Equal(1, disk.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(expected.Revision, disk.RootElement.GetProperty("Revision").GetString());
        Assert.Equal(expected.Rules.Count, disk.RootElement.GetProperty("Rules").GetArrayLength());
        if (blocked)
        {
            JsonElement saved = Assert.Single(disk.RootElement.GetProperty("Rules").EnumerateArray());
            Assert.Equal((int)MediaTypeSelectionMode.Selected, saved.GetProperty("Types").GetProperty("Mode").GetInt32());
            Assert.Equal(0, saved.GetProperty("Types").GetProperty("Extensions").GetArrayLength());
        }
    }

    private static ProfileAdmissionToken OnlyActiveOwner(ProfileLease lease) => Assert.Single(
        Assert.IsType<HashSet<ProfileAdmissionToken>>(typeof(ProfileAdmission)
            .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease.Admission)));

    private static Exception ReplaceBackupDirectoryReference(TempDirectory directory)
    {
        string root = Path.Combine(directory.Path, "replace-reference");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.json"), destination = Path.Combine(root, "destination.json");
        string backup = Path.Combine(root, "backup.json");
        File.WriteAllText(source, "source"); File.WriteAllText(destination, "destination");
        Directory.CreateDirectory(backup);
        Exception? failure = Record.Exception(() => File.Replace(source, destination, backup));
        Assert.NotNull(failure);
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("destination", File.ReadAllText(destination));
        Assert.True(Directory.Exists(backup));
        return failure!;
    }

    private static async Task ObserveForTeardownAsync(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch when (task.IsCompleted) { /* Only failure-unwind: the main path asserts the exact task outcome. */ }
    }

    private sealed class CallingThreadEnumerable(List<MediaRule> values) : IEnumerable<MediaRule>
    {
        internal int CallingThread { get; } = Environment.CurrentManagedThreadId;
        internal int EnumerationThread { get; private set; }
        internal int Enumerations { get; private set; }
        public IEnumerator<MediaRule> GetEnumerator()
        {
            EnumerationThread = Environment.CurrentManagedThreadId;
            Enumerations++;
            return values.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class StepBarrier : IDisposable
    {
        private readonly TaskCompletionSource<bool> _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        internal Task Reached => _reached.Task;
        internal void Block()
        {
            _reached.TrySetResult(true);
            if (!_release.Wait(Deadline)) throw new TimeoutException("Synthetic media-rule store barrier was not released.");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
