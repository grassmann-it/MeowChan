using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// Synthetic profiles and media only. Barriers observe actual Host -> FS -> Catalog
// work and actual disposal, not substitute deletion tasks or elapsed-time guesses.
[Collection("Delivered plugin hosts")]
public sealed class ArchiveMutationHostAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly ResourceId Id = new("4chan", "g", "2600");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Live(Fixture f, ProfileAdmissionToken token) =>
        Assert.True(f.Lease.Admission.RunWithToken(token, () => true, ProfileAdmissionKind.Ordinary));
    private static void Retired(Fixture f, ProfileAdmissionToken token) =>
        Assert.Throws<InvalidOperationException>(() =>
            f.Lease.Admission.RunWithToken(token, () => true, ProfileAdmissionKind.Ordinary));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedMutationKeepsSameTokenAcrossPendingExclusiveFilesCommitDisposalAndPublication(bool cleanup)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await Attempt.PrepareAsync(f, cleanup);
        using Barrier files = new(), commit = new(), projection = new();
        ProfileAdmissionToken? first = null, sql = null, disposed = null;
        int dispatches = 0, disposalEnds = 0;
        void Files(ProfileAdmissionToken token)
        {
            if (first != null) { Assert.Same(first, token); return; }
            first = token;
            Assert.False(File.Exists(f.Asset)); // The first physical file really went away.
            if (cleanup)
            {
                Assert.True(File.Exists(f.Manifest)); // SQL and manifest loss are still ahead.
                f.AssertResourcesHeld();
            }
            files.Hold();
        }
        f.Host.AfterArchiveDeletionFilesForTests = Files;
        f.Host.AfterArchiveCleanupFileDeletedForTests = Files;
        f.Catalog.BeforeWriteDispatchForTests = token =>
        {
            Assert.Same(first, token); Live(f, token); Interlocked.Increment(ref dispatches);
        };
        f.Catalog.AfterWriteCommitForTests = token =>
        {
            sql = token; Assert.Same(first, token); Live(f, token);
            if (cleanup) f.AssertResourcesHeld();
            commit.Hold(); // Actual SQL commit happened, with its consumer pin still held.
        };
        f.Host.AfterArchiveCleanupDisposalForTests = async token =>
        {
            disposed = token; Assert.Same(first, token); Live(f, token);
            await f.AssertResourcesReleasedAsync();
            Interlocked.Increment(ref disposalEnds);
        };
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool commandsHeld = true;
        Task mutation = Task.CompletedTask, owner = Task.CompletedTask, capture = Task.CompletedTask, observer = Task.CompletedTask;
        TaskCompletionSource granted = Signal(), resume = Signal(), coreEnded = Signal(), releaseObserver = Signal();
        using CancellationTokenSource exclusiveCancellation = new();
        long version = f.Host.Projection.Version;
        try
        {
            capture = Task.Run(() => { lock (Field<object>(f.Host, "_viewCapture")) projection.Hold(); });
            await projection.Entered.Task.WaitAsync(Deadline);
            mutation = attempt.Start(f);
            Assert.Equal(1, f.Host.AcceptedCommandCount); Assert.False(mutation.IsCompleted);
            owner = f.Host.RunWithProfileProducerExclusiveAsync(async token =>
            {
                Assert.Equal(ProfileAdmissionKind.Exclusive, token.Kind);
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            }, exclusiveCancellation.Token);
            // The real lane reserves before its first drain await. No polling for pending.
            Assert.Throws<InvalidOperationException>(() => { using var denied = f.Lease.Admission.EnterOrdinaryWriter(); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.PrepareArchiveDeletionAsync([Id]));
            Assert.Throws<InvalidOperationException>(() => { _ = f.Catalog.DeleteThreadsAsync([Id]); });
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            commands.Release(); commandsHeld = false;
            await files.Entered.Task.WaitAsync(Deadline);
            Assert.NotNull(first); Live(f, first!);
            Assert.Equal(ProfileAdmissionKind.Ordinary, first!.Kind);
            Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);
            // Passive preview remains a reader even while a real Exclusive is pending.
            Assert.Single((await f.Catalog.PrepareDeletionAsync([Id]).WaitAsync(Deadline)).Threads);
            files.Release();
            await commit.Entered.Task.WaitAsync(Deadline);
            Assert.Same(first, sql); Assert.Equal(1, dispatches);
            await f.AssertCatalogAsync(present: false); // Independent reader sees the actual committed loss.
            Assert.False(File.Exists(f.Manifest));
            Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);
            observer = Task.Run(async () =>
            {
                Assert.True(await commands.WaitAsync(Deadline));
                try { coreEnded.TrySetResult(); await releaseObserver.Task.WaitAsync(Deadline); }
                finally { commands.Release(); }
            });
            commit.Release();
            await coreEnded.Task.WaitAsync(Deadline); // Core + await-using scopes ended, publication still blocked.
            if (cleanup) { Assert.Same(first, disposed); Assert.Equal(1, disposalEnds); }
            else Assert.Equal(0, disposalEnds);
            Assert.Equal(1, f.Host.AcceptedCommandCount); Live(f, first!);
            Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);
            projection.Release(); releaseObserver.TrySetResult();
            await observer.WaitAsync(Deadline); await capture.WaitAsync(Deadline);
            await mutation.WaitAsync(Deadline); attempt.AssertSuccess();
            await granted.Task.WaitAsync(Deadline);
            Assert.Equal(0, f.Host.AcceptedCommandCount); Retired(f, first!);
            Assert.True(f.Host.Projection.Version > version);
            // Even active Exclusive cannot turn this preview into a new writer.
            Assert.Empty((await f.Catalog.PrepareDeletionAsync([Id]).WaitAsync(Deadline)).Threads);
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, f.Host.State);
        }
        finally
        {
            files.Release(); commit.Release(); projection.Release(); releaseObserver.TrySetResult(); resume.TrySetResult();
            exclusiveCancellation.Cancel(); if (commandsHeld) commands.Release();
            await Observe(mutation); await Observe(owner); await Observe(capture); await Observe(observer); f.ClearHooks();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupCancellationKeepsActualFileAndCommitAccountingAndDisposesBeforeTokenRelease(bool afterCommit)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await Attempt.PrepareAsync(f, cleanup: true);
        using CancellationTokenSource cancellation = new(), exclusiveCancellation = new();
        TaskCompletionSource disposed = Signal(), releaseTail = Signal(), granted = Signal(), resume = Signal();
        ProfileAdmissionToken? first = null;
        int files = 0, writes = 0;
        f.Host.AfterArchiveCleanupFileDeletedForTests = token =>
        {
            if (first == null) first = token;
            else Assert.Same(first, token);
            Interlocked.Increment(ref files);
            if (!afterCommit) cancellation.Cancel();
        };
        f.Catalog.BeforeWriteDispatchForTests = token =>
        { Assert.Same(first, token); Interlocked.Increment(ref writes); };
        f.Catalog.AfterWriteCommitForTests = token =>
        { Assert.Same(first, token); cancellation.Cancel(); };
        f.Host.AfterArchiveCleanupDisposalForTests = async token =>
        {
            Assert.Same(first, token); Live(f, token); await f.AssertResourcesReleasedAsync();
            disposed.TrySetResult(); await releaseTail.Task.WaitAsync(Deadline);
        };
        Task mutation = attempt.Start(f, cancellation.Token), owner = Task.CompletedTask;
        try
        {
            await disposed.Task.WaitAsync(Deadline);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(afterCommit ? 2 : 1, files); Assert.Equal(afterCommit ? 1 : 0, writes);
            Assert.False(File.Exists(f.Asset)); Assert.Equal(!afterCommit, File.Exists(f.Manifest));
            await f.AssertCatalogAsync(present: !afterCommit);
            owner = f.Host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            }, exclusiveCancellation.Token);
            Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);
            Assert.Equal(1, f.Host.AcceptedCommandCount); Live(f, first!);
            releaseTail.TrySetResult(); await mutation.WaitAsync(Deadline);
            ArchiveCleanupExecutionResult result = Assert.IsType<ArchiveCleanupExecutionResult>(attempt.Result);
            Assert.True(result.RequiresNewPreview); Assert.Empty(result.Errors);
            if (afterCommit)
            {
                // Cancellation after committed SQL is not a rollback, nor an invented
                // canceled result: the single selected row completed normally.
                attempt.AssertSuccess();
            }
            else
            {
                Assert.Empty(result.DeletedThreads); Assert.Equal(new[] { Id }, result.RemainingThreads);
                Assert.True(result.Canceled); Assert.Equal(1, result.DeletedFileCount); Assert.Equal(3L, result.DeletedBytes);
            }
            await granted.Task.WaitAsync(Deadline); Retired(f, first!);
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
        }
        finally
        {
            releaseTail.TrySetResult(); resume.TrySetResult(); exclusiveCancellation.Cancel();
            await Observe(mutation); await Observe(owner); f.ClearHooks();
        }
    }

    [Fact]
    public async Task ShutdownCannotReleaseProfileWhileCleanupPostDisposalTailIsStillAwaited()
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await Attempt.PrepareAsync(f, cleanup: true);
        TaskCompletionSource disposed = Signal(), release = Signal();
        ProfileAdmissionToken? borrowed = null;
        f.Host.AfterArchiveCleanupDisposalForTests = async token =>
        {
            borrowed = token; await f.AssertResourcesReleasedAsync();
            disposed.TrySetResult(); await release.Task.WaitAsync(Deadline);
        };
        Task mutation = attempt.Start(f), shutdown = Task.CompletedTask;
        try
        {
            await disposed.Task.WaitAsync(Deadline);
            shutdown = f.Host.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted); Assert.False(f.Host.IsProfileReleased);
            Assert.Equal(1, f.Host.AcceptedCommandCount); Live(f, borrowed!);
            Assert.Throws<IOException>(() => { using var denied = ProfileLease.Acquire(f.Profile); });
            release.TrySetResult(); await mutation.WaitAsync(Deadline); attempt.AssertSuccess();
            await shutdown.WaitAsync(Deadline);
            Assert.True(f.Host.IsProfileReleased); Assert.Equal(0, f.Host.AcceptedCommandCount);
            using ProfileLease next = ProfileLease.Acquire(f.Profile);
        }
        finally { release.TrySetResult(); await Observe(mutation); await Observe(shutdown); f.ClearHooks(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveExclusiveRefusesNewHostMutationWithoutConsumingConfirmedPlanThenReadyRetryWorks(bool cleanup)
    {
        await using Fixture f = await Fixture.StartAsync();
        Attempt attempt = await Attempt.PrepareAsync(f, cleanup);
        TaskCompletionSource granted = Signal(), resume = Signal();
        int writes = 0, fileSteps = 0;
        f.Catalog.BeforeWriteDispatchForTests = _ => Interlocked.Increment(ref writes);
        f.Host.AfterArchiveDeletionFilesForTests = _ => Interlocked.Increment(ref fileSteps);
        f.Host.AfterArchiveCleanupFileDeletedForTests = _ => Interlocked.Increment(ref fileSteps);
        Task owner = f.Host.RunWithProfileProducerExclusiveAsync(async _ =>
        {
            granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
            return ProfileExclusiveOutcome<bool>.SafeToResume(true);
        });
        try
        {
            await granted.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() => attempt.Start(f).WaitAsync(Deadline));
            Assert.Equal(0, f.Host.AcceptedCommandCount); Assert.Equal(0, writes); Assert.Equal(0, fileSteps);
            Assert.True(File.Exists(f.Asset)); Assert.True(File.Exists(f.Manifest));
            Assert.Single((await f.Catalog.PrepareDeletionAsync([Id]).WaitAsync(Deadline)).Threads);
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            await attempt.Start(f).WaitAsync(Deadline); attempt.AssertSuccess();
            Assert.Equal(1, writes); Assert.True(fileSteps > 0);
        }
        finally { resume.TrySetResult(); await Observe(owner); f.ClearHooks(); }
    }

    [Theory]
    [InlineData(false, "foreign")]
    [InlineData(true, "foreign")]
    [InlineData(false, "retired")]
    [InlineData(true, "retired")]
    [InlineData(false, "exclusive")]
    [InlineData(true, "exclusive")]
    public async Task ActualHostCatalogRejectsForeignRetiredAndWrongRoleAuthorityWithoutMutation(bool cleanup, string authority)
    {
        await using Fixture f = await Fixture.StartAsync();
        ArchiveDeletedThread expected = Assert.Single((await f.Catalog.PrepareDeletionAsync([Id])).Threads);
        byte[] asset = File.ReadAllBytes(f.Asset), manifest = File.ReadAllBytes(f.Manifest);
        using ProfileAdmission foreignKernel = new(); foreignKernel.CompleteStartup();
        using ProfileAdmissionToken foreign = foreignKernel.EnterOrdinaryWriter();
        using ProfileAdmissionToken retired = f.Lease.Admission.EnterOrdinaryWriter(); retired.Dispose();
        async Task Reject(ProfileAdmissionToken token)
        {
            // These invalid authorities are not injectable through the host API. Test
            // the real host-bound catalog leaf, not a new fake host admitted entry.
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                if (cleanup) await f.Catalog.DeleteCleanupThreadAdmittedAsync(token, expected).WaitAsync(Deadline);
                else await f.Catalog.DeleteThreadsAdmittedAsync(token, [Id]).WaitAsync(Deadline);
            });
            await f.AssertCatalogAsync(present: true);
            Assert.Equal(asset, File.ReadAllBytes(f.Asset)); Assert.Equal(manifest, File.ReadAllBytes(f.Manifest));
            Assert.Equal(0, f.Host.AcceptedCommandCount);
        }
        if (authority == "exclusive")
        {
            await f.Host.RunWithProfileProducerExclusiveAsync(async token =>
            {
                await Reject(token);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true); // Independently verified no effect.
            }).WaitAsync(Deadline);
        }
        else
        {
            await Reject(authority == "foreign" ? foreign : retired);
            Assert.True(foreignKernel.RunWithToken(foreign, () => true, ProfileAdmissionKind.Ordinary));
        }
    }

    private static async Task Observe(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (Exception) when (task.IsCompleted) { } // Original assertions observe failures; never hide a hung worker.
    }

    private sealed class Barrier : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal void Hold() { Entered.TrySetResult(); Assert.True(_release.Wait(Deadline)); }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class Attempt(ArchivePermanentDeletionPlan? deletion, ArchiveCleanupExecutionPlan? cleanup)
    {
        internal object? Result { get; private set; }
        internal static async Task<Attempt> PrepareAsync(Fixture f, bool cleanup)
        {
            if (!cleanup) return new(await f.Host.PrepareArchiveDeletionAsync([Id]).WaitAsync(Deadline), null);
            ArchiveCleanupPreview preview = await f.Host.PrepareArchiveCleanupAsync([]).WaitAsync(Deadline);
            Assert.True(preview.Complete); Assert.False(preview.IsStale);
            Assert.Equal(ArchiveCleanupEligibility.Candidate, Assert.Single(preview.Rows).Eligibility);
            ArchiveCleanupExecutionPlan plan = await f.Host.PrepareArchiveCleanupExecutionAsync(preview, [Id]).WaitAsync(Deadline);
            Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
            return new(null, plan);
        }
        internal async Task Start(Fixture f, CancellationToken token = default)
        {
            if (cleanup != null) Result = await f.Host.ExecuteArchiveCleanupAsync(cleanup, token);
            else Result = await f.Host.DeleteArchivePermanentlyAsync(deletion!, token);
        }
        internal void AssertSuccess()
        {
            if (cleanup != null)
            {
                var result = Assert.IsType<ArchiveCleanupExecutionResult>(Result);
                Assert.Equal(new[] { Id }, result.DeletedThreads); Assert.Empty(result.RemainingThreads);
                Assert.Empty(result.Errors); Assert.False(result.Canceled); Assert.True(result.RequiresNewPreview);
                Assert.Equal(2, result.DeletedFileCount); Assert.Equal(cleanup.OwnedBytes, result.DeletedBytes);
            }
            else
            {
                var result = Assert.IsType<ArchivePermanentDeletionResult>(Result);
                Assert.Equal(new[] { Id }, result.DeletedThreads); Assert.Empty(result.Errors);
                Assert.Null(result.RatingCleanupThreads); Assert.NotNull(result.Files);
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _root = new();
        internal string Profile => Path.Combine(_root.Path, "profile");
        private string Output => Path.Combine(_root.Path, "media");
        internal string Media => StorageLayout.GetThreadDirectory(Output, Id.Source, Id.Board, Id.Thread!);
        internal string Asset => Path.Combine(Media, "asset.bin");
        internal string Manifest => Path.Combine(Media, ".meowchan-manifest.json");
        internal DesktopSessionHost Host { get; private set; } = null!;
        internal ProfileLease Lease => Field<ProfileLease>(Host, "_profileLease");
        internal Registry Registry => Field<Registry>(Host, "_registry");
        internal ArchiveCatalog Catalog { get; private set; } = null!;
        internal static async Task<Fixture> StartAsync()
        {
            Fixture f = new();
            try
            {
                Directory.CreateDirectory(f.Profile); Directory.CreateDirectory(f.Media);
                new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                    DownloadPath = f.Output }.SaveConfig(f.Profile);
                byte[] bytes = [26, 17, 29];
                await File.WriteAllBytesAsync(f.Asset, bytes);
                await File.WriteAllTextAsync(f.Manifest, JsonSerializer.Serialize(new
                {
                    Version = 1, Entries = new Dictionary<string, object>
                    {
                        ["asset.bin"] = new { MediaIdentifier = "asset", SourceUrl = "https://offline.invalid/asset.bin",
                            FileName = "asset.bin", Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                            Md5 = Convert.ToBase64String(MD5.HashData(bytes)) }
                    }
                }));
                using (ProfileLease lease = ProfileLease.Acquire(f.Profile))
                await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(f.Profile, lease))
                {
                    string url = "https://boards.4chan.org/g/thread/2600";
                    await catalog.RegisterThreadAsync(new(Id, url, "Admission fixture", f.Media, true));
                    await catalog.CaptureAsync(new(Id, url, "Admission fixture", ThreadSourceState.Archived,
                        DateTimeOffset.UtcNow, f.Media,
                        [new CapturedPost(Id.Thread!, 0, PostFieldPresence.All, Html: "<p>Synthetic archive</p>", Attachments: [])]));
                }
                f.Host = new(f.Profile, Array.Empty<IImageBoard>());
                await f.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State);
                f.Catalog = Field<ThreadContentRuntime>(f.Host, "_content").Catalog;
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal void AssertResourcesHeld()
        {
            Assert.Equal(1, Field<IDictionary>(Registry, "_archiveCleanupReservations").Count);
            foreach (string name in new[] { ".meowchan-store.lock", ".ochan-store.lock" })
                Assert.Throws<IOException>(() =>
                { using FileStream denied = new(Path.Combine(Media, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        }
        internal async Task AssertResourcesReleasedAsync()
        {
            Assert.Empty(Field<IDictionary>(Registry, "_archiveCleanupReservations"));
            foreach (string name in new[] { ".meowchan-store.lock", ".ochan-store.lock" })
            { using FileStream available = new(Path.Combine(Media, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            // Reacquiring the SAME target proves the actual Registry AND queue reservation
            // disposals ended. This target fence is intentionally not profile admission.
            await using IAsyncDisposable? again = await Registry.TryReserveArchiveCleanupAsync(
                [new(Id, "https://boards.4chan.org/g/thread/2600", [Media])]).WaitAsync(Deadline);
            Assert.NotNull(again);
        }
        internal async Task AssertCatalogAsync(bool present)
        {
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(Profile);
            Assert.Equal(present, await reader.ReadThreadAsync(Id).WaitAsync(Deadline) != null);
        }
        internal void ClearHooks()
        {
            Host.AfterArchiveDeletionFilesForTests = null;
            Host.AfterArchiveCleanupFileDeletedForTests = null; Host.AfterArchiveCleanupDisposalForTests = null;
            if (Catalog != null)
            { Catalog.BeforeWriteDispatchForTests = null; Catalog.AfterWriteCommitForTests = null; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Host != null) { ClearHooks(); await Host.DisposeAsync().AsTask().WaitAsync(Deadline); }
            _root.Dispose();
        }
    }
}
