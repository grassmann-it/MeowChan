using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.Tests;

// Real synthetic profiles and synchronous inner-store barriers; no global hook,
// fake persistence, sleeps, or edits to the existing RuleTemplateStoreTests.
public sealed class RuleTemplateStoreAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string CatalogName = "rule_templates.json";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneTemplateSavesUseExactlyOneStartupOrOrdinaryOwner(bool ready)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        if (ready) lease.Admission.CompleteStartup();
        ProfileAdmissionToken? observed = null;
        int before = 0, after = 0;
        store.BeforeAdmittedSaveMutationForTests = token =>
        {
            observed = token;
            Assert.Equal(ready ? ProfileAdmissionKind.Ordinary : ProfileAdmissionKind.Startup, token.Kind);
            Assert.Same(token, OnlyActiveOwner(lease));
            Assert.False(File.Exists(CatalogPath(directory)));
            before++;
        };
        store.AfterAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(observed, token);
            Assert.Same(token, OnlyActiveOwner(lease));
            AssertCommitted(directory, store.Current);
            Assert.Empty(CatalogTemporaryFiles(directory));
            after++;
        };
        RuleTemplateSet saved = await store.SaveAsync([Template("standalone")], store.Current.Revision).WaitAsync(Deadline);
        Assert.Same(saved, store.Current);
        Assert.Equal(1, before);
        Assert.Equal(1, after);
        if (!ready) lease.Admission.CompleteStartup(); // No standalone owner leaked.
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync().AsTask().WaitAsync(Deadline);
        Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(observed!, () => true,
            ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary));
        RuleTemplateLoadResult loaded = await new RuleTemplateStore(directory.Path, lease).LoadAsync();
        Assert.Equal(saved.Revision, loaded.Catalog.Revision);
        Assert.Equal("standalone", Assert.Single(loaded.Catalog.Templates).Name);
        Assert.False(File.Exists(Path.Combine(directory.Path, "media_rules.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidBorrowedTemplateAuthorityCannotWriteEvenUnchangedCatalog(bool empty)
    {
        using TempDirectory directory = new();
        using TempDirectory foreignDirectory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ProfileLease foreignLease = ProfileLease.Acquire(foreignDirectory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        RuleTemplateSet saved = await store.SaveAsync(empty ? [] : [Template("unchanged")], store.Current.Revision);
        int before = 0, after = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => before++;
        store.AfterAdmittedSaveMutationForTests = _ => after++;
        using ProfileAdmissionToken foreign = foreignLease.Admission.EnterStartupWriter();
        // Allocation is deliberately NOT issuance: negative membership test only.
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Startup);
        ProfileAdmissionToken released = lease.Admission.EnterStartupWriter();
        released.Dispose();
        string[] bytes = Inventory(directory);
        foreach (ProfileAdmissionToken invalid in new[] { null!, foreign, unissued, released })
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAdmittedAsync(saved.Templates, saved.Revision, invalid));
        lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAdmittedAsync(saved.Templates, saved.Revision, exclusive));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(saved.Templates, saved.Revision));
            RuleTemplateLoadResult loaded = await new RuleTemplateStore(directory.Path, lease).LoadAsync();
            Assert.Equal(saved.Revision, loaded.Catalog.Revision); // Passive load remains legal.
        }
        Assert.Same(saved, store.Current);
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, before);
        Assert.Equal(0, after);
    }

    [Fact]
    public async Task AcceptedTokenCannotReplaceTheDisposedPhysicalLease()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int hooks = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => hooks++;
        store.AfterAdmittedSaveMutationForTests = _ => hooks++;
        string[] bytes = Inventory(directory);
        lease.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            store.SaveAdmittedAsync([], store.Current.Revision, accepted));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync([], store.Current.Revision));
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("stopping")]
    [InlineData("stopped")]
    [InlineData("closed")]
    public async Task StandaloneClosedAdmissionRefusesEvenEmptySaveWithoutDiskChanges(string phase)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        if (phase == "failed") lease.Admission.FailStartup();
        else if (phase == "closed") lease.Admission.Close();
        else
        {
            lease.Admission.BeginShutdown();
            if (phase == "stopped") lease.Admission.CompleteShutdown();
        }
        string[] bytes = Inventory(directory);
        if (phase == "closed")
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync([], store.Current.Revision));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([], store.Current.Revision));
        Assert.Empty((await store.LoadAsync()).Catalog.Templates);
        Assert.Equal(bytes, Inventory(directory));
    }

    [Fact]
    public async Task PendingExclusiveRefusesStandaloneButTheExactAcceptedBorrowStillSaves()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            string[] bytes = Inventory(directory);
            Assert.False(pending.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([], store.Current.Revision));
            Assert.Empty((await new RuleTemplateStore(directory.Path, lease).LoadAsync()).Catalog.Templates);
            Assert.Equal(bytes, Inventory(directory));
            store.BeforeAdmittedSaveMutationForTests = token => Assert.Same(accepted, token);
            store.AfterAdmittedSaveMutationForTests = token => Assert.Same(accepted, token);
            RuleTemplateSet saved = await store.SaveAdmittedAsync([Template("accepted")], store.Current.Revision, accepted);
            AssertCommitted(directory, saved);
            Assert.False(pending.IsCompleted); // The store borrows, never disposes the owner.
            accepted.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAdmittedAsync(saved.Templates, saved.Revision, accepted));
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
    public async Task RealMutationAndPublishedCurrentStayPinnedAfterOwnerReleaseRequest(bool shutdown)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancelExclusive = new();
        using StepBarrier before = new();
        using StepBarrier after = new();
        store.BeforeAdmittedSaveMutationForTests = token => { Assert.Same(accepted, token); before.Block(); };
        store.AfterAdmittedSaveMutationForTests = token => { Assert.Same(accepted, token); after.Block(); };
        Task<RuleTemplateSet> save = store.SaveAdmittedAsync([Template("pinned")], store.Current.Revision, accepted);
        Task<ProfileAdmissionToken>? pending = null;
        Task? drain = null;
        try
        {
            await before.Reached.WaitAsync(Deadline);
            Assert.False(File.Exists(CatalogPath(directory)));
            if (shutdown)
            {
                lease.Admission.BeginShutdown();
                drain = lease.Admission.DrainAsync();
                using CancellationTokenSource observer = new();
                Task cancelledObserver = lease.Admission.DrainAsync(observer.Token);
                observer.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledObserver);
                Assert.False(drain.IsCompleted);
            }
            else pending = lease.Admission.EnterExclusiveAsync(cancelExclusive.Token).AsTask();
            accepted.Dispose(); // Release requested, not the end of the real synchronous use.
            Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(accepted,
                () => true, ProfileAdmissionKind.Ordinary));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([], store.Current.Revision));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SaveAdmittedAsync([], store.Current.Revision, accepted));
            Assert.False(save.IsCompleted);
            if (pending != null) Assert.False(pending.IsCompleted);
            before.Release();
            await after.Reached.WaitAsync(Deadline);
            AssertCommitted(directory, store.Current);
            Assert.Empty(CatalogTemporaryFiles(directory));
            Assert.False(save.IsCompleted); // After Atomicfinally + Current, but still inside the pin.
            if (pending != null) Assert.False(pending.IsCompleted);
            if (drain != null) Assert.False(drain.IsCompleted);
            after.Release();
            RuleTemplateSet saved = await save.WaitAsync(Deadline);
            Assert.Same(saved, store.Current);
            if (pending != null) (await pending.WaitAsync(Deadline)).Dispose();
            if (drain != null)
            {
                await drain.WaitAsync(Deadline);
                lease.Admission.CompleteShutdown();
            }
        }
        finally
        {
            before.Release();
            after.Release();
            // Gates are released and the actual operation is observed before fixture disposal.
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
    public async Task TemplateCancellationDoesNotLeakOwnershipOrUndoPublishedCurrent(string cancelAt)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        RuleTemplateSet original = await store.SaveAsync([Template("original")], store.Current.Revision);
        lease.Admission.CompleteStartup();
        string foreignTemp = Path.Combine(directory.Path, ".rule_templates.json.foreign.tmp");
        File.WriteAllText(foreignTemp, "not our temporary file");
        string[] bytes = Inventory(directory);
        using CancellationTokenSource cancel = new();
        int before = 0, after = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => { before++; if (cancelAt == "before") cancel.Cancel(); };
        store.AfterAdmittedSaveMutationForTests = _ => { after++; if (cancelAt == "after") cancel.Cancel(); };
        if (cancelAt == "entry") cancel.Cancel();
        Task<RuleTemplateSet> save = store.SaveAsync([Template("replacement")], original.Revision, cancel.Token);
        if (cancelAt == "after")
        {
            RuleTemplateSet saved = await save.WaitAsync(Deadline);
            Assert.True(cancel.IsCancellationRequested);
            Assert.False(save.IsCanceled);
            Assert.Same(saved, store.Current);
            AssertCommitted(directory, saved);
            Assert.Equal(1, before);
            Assert.Equal(1, after);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(Deadline));
            Assert.True(save.IsCanceled);
            Assert.Same(original, store.Current);
            Assert.Equal(bytes, Inventory(directory));
            Assert.Equal(cancelAt == "entry" ? 0 : 1, before);
            Assert.Equal(0, after);
        }
        Assert.Equal("not our temporary file", File.ReadAllText(foreignTemp));
        Assert.Equal(new[] { foreignTemp }, CatalogTemporaryFiles(directory));
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync().AsTask().WaitAsync(Deadline);
    }

    [Fact]
    public async Task ActualTemplateReplaceFailureKeepsCurrentAndBorrowedOwnerForRetry()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        await store.SaveAsync([Template("backup")], store.Current.Revision);
        RuleTemplateSet original = await store.SaveAsync([Template("primary")], store.Current.Revision);
        string path = CatalogPath(directory);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        string parked = path + ".bak.fixture-parked";
        string foreignTemp = Path.Combine(directory.Path, ".rule_templates.json.foreign.tmp");
        File.WriteAllText(foreignTemp, "foreign bytes");
        // The reference uses the same filesystem and exact primitive, rather than
        // guessing a Windows/Linux error mapping. It is executed only by Root's test run.
        Exception referenceFailure = ReplaceBackupDirectoryReference(directory);
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
            // All catalog reads have finished at Before. The primary is still a
            // valid file, so Atomic writes+flushes its own temp before File.Replace
            // encounters the directory at the backup destination (not a read error).
            Exception? failure = await Record.ExceptionAsync(() =>
                store.SaveAdmittedAsync([Template("not committed")], original.Revision, accepted));
            Assert.IsType(referenceFailure.GetType(), failure);
            Assert.Equal(referenceFailure.HResult, failure!.HResult);
            Assert.Equal(1, before);
            Assert.Equal(0, after);
            Assert.Same(original, store.Current);
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(backup, File.ReadAllBytes(parked));
            Assert.True(Directory.Exists(path + ".bak"));
            Assert.Equal(new[] { foreignTemp }, CatalogTemporaryFiles(directory));
            Assert.Equal("foreign bytes", File.ReadAllText(foreignTemp));
            Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
        }
        finally
        {
            if (Directory.Exists(path + ".bak")) Directory.Delete(path + ".bak");
            if (File.Exists(parked)) File.Move(parked, path + ".bak");
            store.BeforeAdmittedSaveMutationForTests = null;
        }
        RuleTemplateSet retry = await store.SaveAdmittedAsync([Template("retry")], original.Revision, accepted);
        Assert.Equal(1, after);
        Assert.Same(retry, store.Current);
        AssertCommitted(directory, retry);
        Assert.Equal(primary, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(new[] { foreignTemp }, CatalogTemporaryFiles(directory));
        Assert.Equal("foreign bytes", File.ReadAllText(foreignTemp));
        accepted.Dispose();
        lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedSavesKeepMemoryAndExternalDiskRevisionFences(bool externalDiskChange)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        string stale = store.Current.Revision;
        RuleTemplateSet saved = await store.SaveAsync([Template("current")], stale);
        if (externalDiskChange)
        {
            RuleTemplateStore other = new(directory.Path, lease);
            await other.LoadAsync();
            await other.SaveAsync([], other.Current.Revision);
        }
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int hooks = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => hooks++;
        store.AfterAdmittedSaveMutationForTests = _ => hooks++;
        string[] bytes = Inventory(directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAdmittedAsync([], externalDiskChange ? saved.Revision : stale, accepted));
        Assert.Same(saved, store.Current);
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
    }

    [Theory]
    [InlineData("future-primary")]
    [InlineData("future-backup")]
    [InlineData("utf8")]
    [InlineData("json")]
    public async Task BorrowedSavesPreserveFutureSchemasAndUnrecoverableInput(string invalid)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        await store.SaveAsync([Template("backup")], store.Current.Revision);
        RuleTemplateSet saved = await store.SaveAsync([Template("primary")], store.Current.Revision);
        string path = CatalogPath(directory);
        if (invalid.StartsWith("future", StringComparison.Ordinal))
            File.WriteAllText(invalid == "future-primary" ? path : path + ".bak", "{\"schemaVersion\":99,\"foreign\":true}");
        else
        {
            File.Delete(path + ".bak");
            File.WriteAllBytes(path, invalid == "utf8" ? new byte[] { 0xFF, 0xFE, 0xFF } : Encoding.UTF8.GetBytes("broken-json"));
        }
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int hooks = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => hooks++;
        store.AfterAdmittedSaveMutationForTests = _ => hooks++;
        string[] bytes = Inventory(directory);
        if (invalid.StartsWith("future", StringComparison.Ordinal))
            await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveAdmittedAsync([], saved.Revision, accepted));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAdmittedAsync([], saved.Revision, accepted));
        Assert.Same(saved, store.Current);
        Assert.Equal(bytes, Inventory(directory));
        Assert.Equal(0, hooks);
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitBorrowedRecoverySavePreservesCorruptEvidenceAndValidBackup(bool corruptBackup)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        RuleTemplateSet first = await store.SaveAsync([Template("backup")], store.Current.Revision);
        RuleTemplateSet second = await store.SaveAsync([Template("primary")], store.Current.Revision);
        string path = CatalogPath(directory);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        byte[] corrupt = [0xFF, 0xFE, 0xFF];
        File.WriteAllBytes(corruptBackup ? path + ".bak" : path, corrupt);
        string[] bytes = Inventory(directory);
        RuleTemplateLoadResult loaded = await store.LoadAsync();
        Assert.Equal(!corruptBackup, loaded.Recovered);
        if (corruptBackup) Assert.Null(loaded.Warning);
        else Assert.False(string.IsNullOrWhiteSpace(loaded.Warning));
        Assert.Equal(corruptBackup ? second.Revision : first.Revision, loaded.Catalog.Revision);
        Assert.Equal(bytes, Inventory(directory)); // Load is passive even when recovering.
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        int after = 0;
        store.BeforeAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(accepted, token);
            Assert.Equal(bytes, Inventory(directory)); // No Preserve before the barrier.
        };
        store.AfterAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(accepted, token);
            AssertCommitted(directory, store.Current);
            Assert.Empty(CatalogTemporaryFiles(directory));
            after++;
        };
        RuleTemplateSet saved = await store.SaveAdmittedAsync([Template("repaired explicitly")], loaded.Catalog.Revision, accepted);
        Assert.Same(saved, store.Current);
        Assert.Equal(1, after);
        string preserved = Assert.Single(Directory.GetFiles(directory.Path,
            corruptBackup ? "rule_templates.json.bak.corrupt-*" : "rule_templates.json.corrupt-*"));
        Assert.Equal(corrupt, File.ReadAllBytes(preserved));
        Assert.Equal(corruptBackup ? primary : backup, File.ReadAllBytes(path + ".bak"));
        RuleTemplateLoadResult reloaded = await new RuleTemplateStore(directory.Path, lease).LoadAsync();
        Assert.False(reloaded.Recovered);
        Assert.Null(reloaded.Warning);
        Assert.Equal(saved.Revision, reloaded.Catalog.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateSaveFreezesInputAndDoesNotChangeActiveRules(bool borrowed)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore active = new(directory.Path, lease);
        await active.LoadAsync();
        await active.SaveAsync([], active.Current.Revision);
        MediaRuleSet activeRules = active.Current;
        byte[] activeBytes = File.ReadAllBytes(Path.Combine(directory.Path, "media_rules.json"));
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        using ProfileAdmissionToken? accepted = borrowed ? lease.Admission.EnterStartupWriter() : null;
        using StepBarrier before = new();
        store.BeforeAdmittedSaveMutationForTests = _ => before.Block();
        List<string> extensions = ["jpg"];
        Dictionary<string, string> parameters = new() { ["enabled"] = "false", ["count"] = "0", ["empty"] = "" };
        RuleTemplate template = new(Guid.NewGuid(), " frozen ", new(MediaTypeSelectionMode.Selected, extensions),
            imageFilter: new(MediaImageFilterMode.Plugin, "synthetic.plugin", "filter", "7", parameters));
        List<RuleTemplate> input = [template];
        Task<RuleTemplateSet> save = borrowed
            ? store.SaveAdmittedAsync(input, store.Current.Revision, accepted!)
            : store.SaveAsync(input, store.Current.Revision);
        try
        {
            await before.Reached.WaitAsync(Deadline);
            input.Clear();
            input.Add(Template("later caller change"));
            extensions.Add("png");
            parameters["enabled"] = "true";
            before.Release();
            RuleTemplateSet saved = await save.WaitAsync(Deadline);
            RuleTemplate persisted = Assert.Single((await new RuleTemplateStore(directory.Path, lease).LoadAsync()).Catalog.Templates);
            Assert.Equal(template.Id, persisted.Id);
            Assert.Equal("frozen", persisted.Name);
            Assert.Equal(new[] { ".jpg" }, persisted.Types!.Extensions);
            Assert.Null(persisted.SizeLimit);
            Assert.Equal("7", persisted.ImageFilter!.SchemaVersion);
            Assert.Equal("false", persisted.ImageFilter.Parameters["enabled"]);
            Assert.Equal("0", persisted.ImageFilter.Parameters["count"]);
            Assert.Equal("", persisted.ImageFilter.Parameters["empty"]);
            Assert.Same(saved, store.Current);
            Assert.Same(activeRules, active.Current);
            Assert.Equal(activeBytes, File.ReadAllBytes(Path.Combine(directory.Path, "media_rules.json")));
            Assert.False(File.Exists(Path.Combine(directory.Path, "media_rules.json.bak")));
        }
        finally { before.Release(); await ObserveForTeardownAsync(save); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ObserverFailuresRemainVisibleAndDoNotInventRollbackOrConsumeBorrowedOwner(bool afterCommit, bool disposedFailure)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        await store.LoadAsync();
        RuleTemplateSet original = await store.SaveAsync([Template("original")], store.Current.Revision);
        using ProfileAdmissionToken accepted = lease.Admission.EnterStartupWriter();
        Exception marker = disposedFailure ? new ObjectDisposedException("synthetic-store-observer") : new InvalidOperationException("synthetic-store-observer");
        int before = 0, after = 0;
        store.BeforeAdmittedSaveMutationForTests = _ => { before++; if (!afterCommit) throw marker; };
        store.AfterAdmittedSaveMutationForTests = _ => { after++; throw marker; };
        string[] bytes = Inventory(directory);
        Exception? failure = await Record.ExceptionAsync(() =>
            store.SaveAdmittedAsync([Template("committed only after Before")], original.Revision, accepted));
        Assert.Same(marker, failure);
        Assert.Equal(1, before);
        Assert.Equal(afterCommit ? 1 : 0, after);
        if (afterCommit)
        {
            Assert.NotSame(original, store.Current);
            AssertCommitted(directory, store.Current);
            Assert.Equal("committed only after Before", Assert.Single(store.Current.Templates).Name);
        }
        else
        {
            Assert.Same(original, store.Current);
            Assert.Equal(bytes, Inventory(directory));
        }
        Assert.Empty(CatalogTemporaryFiles(directory));
        Assert.True(lease.Admission.RunWithToken(accepted, () => true, ProfileAdmissionKind.Startup));
        store.BeforeAdmittedSaveMutationForTests = null;
        store.AfterAdmittedSaveMutationForTests = null;
        RuleTemplateSet retry = await store.SaveAdmittedAsync([Template("explicit retry")], store.Current.Revision, accepted);
        Assert.Same(retry, store.Current);
        AssertCommitted(directory, retry);
        accepted.Dispose();
        lease.Admission.CompleteStartup();
    }

    private static RuleTemplate Template(string name) => new(Guid.NewGuid(), name, MediaTypeSelection.All);
    private static string CatalogPath(TempDirectory directory) => Path.Combine(directory.Path, CatalogName);
    private static string[] CatalogTemporaryFiles(TempDirectory directory) =>
        Directory.GetFiles(directory.Path, ".rule_templates.json.*.tmp").Order(StringComparer.Ordinal).ToArray();
    private static string[] Inventory(TempDirectory directory) => Directory.GetFileSystemEntries(directory.Path)
        .Where(path => Path.GetFileName(path) is not (".meowchan-profile.lock" or ".ochan-profile.lock"))
        .Order(StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + (Directory.Exists(path) ? "/" : ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .ToArray();

    private static Exception ReplaceBackupDirectoryReference(TempDirectory directory)
    {
        string root = Path.Combine(directory.Path, "replace-reference");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.json"), destination = Path.Combine(root, "destination.json");
        string backupDirectory = Path.Combine(root, "backup.json");
        File.WriteAllText(source, "source");
        File.WriteAllText(destination, "destination");
        Directory.CreateDirectory(backupDirectory);
        Exception? failure = Record.Exception(() => File.Replace(source, destination, backupDirectory));
        Assert.NotNull(failure); // A successful/unsupported fixture cannot silently skip the failure arm.
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("destination", File.ReadAllText(destination));
        Assert.True(Directory.Exists(backupDirectory));
        return failure!;
    }

    private static async Task ObserveForTeardownAsync(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch when (task.IsCompleted)
        {
            // The test body asserts the operation's outcome. On assertion failure,
            // still observe its terminal task before disposing the synthetic profile.
        }
    }

    private static void AssertCommitted(TempDirectory directory, RuleTemplateSet expected)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(CatalogPath(directory)));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(expected.Revision, document.RootElement.GetProperty("revision").GetString());
        JsonElement[] templates = document.RootElement.GetProperty("templates").EnumerateArray().ToArray();
        Assert.Equal(expected.Templates.Count, templates.Length);
        for (int i = 0; i < templates.Length; i++)
        {
            Assert.Equal(expected.Templates[i].Id, templates[i].GetProperty("id").GetGuid());
            Assert.Equal(expected.Templates[i].Name, templates[i].GetProperty("name").GetString());
        }
    }

    private static ProfileAdmissionToken OnlyActiveOwner(ProfileLease lease)
    {
        // Observation only, no issuance or state mutation. The two hooks own the
        // sole operation in this isolated domain; no other admission can change it.
        FieldInfo field = typeof(ProfileAdmission).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.Single(Assert.IsType<HashSet<ProfileAdmissionToken>>(field.GetValue(lease.Admission)));
    }

    private sealed class StepBarrier : IDisposable
    {
        private readonly TaskCompletionSource<bool> _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        internal Task Reached => _reached.Task;
        internal void Block()
        {
            _reached.TrySetResult(true);
            if (!_release.Wait(Deadline)) throw new TimeoutException("Synthetic template-store barrier was not released.");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
