using System.Net.Http;
using System.Reflection;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// Real Host/Registry/BaseThread/runtime/catalog, but only synthetic text and profiles.
// Catalog instance barriers observe actual dispatch and SQL commit; no substitute write tasks.
[Collection("Delivered plugin hosts")]
public sealed class ThreadContentHostAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly ResourceId Id = new("host-content-fixture", "g", "2601");
    private static readonly ResourceId Other = new(Id.Source, Id.Board, "2699");
    private const string Url = "https://host-content.invalid/g/thread/2601";
    private const string Title = "Synthetic host content admission";
    private static readonly DateTimeOffset PreviousAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LatestAt = PreviousAt.AddMinutes(1);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    // Read-only reflection, under the kernel's own monitor. Holding _commands lets
    // us identify the actual synchronously accepted Host token before any leaf runs.
    private static ProfileAdmissionToken[] ActiveAdmissions(Fixture f)
    {
        lock (Field<object>(f.Lease.Admission, "_sync"))
            return Field<HashSet<ProfileAdmissionToken>>(f.Lease.Admission, "_active").ToArray();
    }
    private static ProfileAdmissionToken AcceptedSince(Fixture f, ProfileAdmissionToken[] before)
    {
        ProfileAdmissionToken accepted = Assert.Single(ActiveAdmissions(f).Where(token => !before.Contains(token)));
        Assert.Equal(ProfileAdmissionKind.Ordinary, accepted.Kind);
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        return accepted;
    }
    private static void Live(Fixture f, ProfileAdmissionToken token) =>
        Assert.True(f.Lease.Admission.RunWithToken(token, () => true, token.Kind));
    private static void Retired(Fixture f, ProfileAdmissionToken token) =>
        Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(token, () => true, token.Kind));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcceptedSetCaptureKeepsExactHostOwnerThroughPendingExclusiveCommitAndPublication(bool active, bool enabled)
    {
        await using Fixture f = await Fixture.StartAsync(initialCapture: !enabled, active: active);
        bool latestBefore = active && !enabled;
        ArchivedThreadView baseline = await f.ReadAsync();
        AssertArchive(f, baseline, !enabled, baseline.Summary.Revision, latestBefore);
        if (active)
        {
            f.AssertAttached();
            AssertContent(f.Thread.GetSnapshot(new DownloadCounts()).Content, !enabled, latestBefore);
            Assert.Equal(1, f.Thread.Observations);
        }
        else Assert.Empty(f.Host.Projection.View.Threads);
        // In the active enable case LastCapturedAt is already non-null, but _latest
        // contains newer, disabled content. Only forceLatest may persist that content.
        Assert.NotNull(baseline.Summary.LastCapturedAt);

        using Barrier flagDispatch = new(), flagCommit = new(), latestDispatch = new(), latestCommit = new(), projection = new();
        int dispatches = 0, commits = 0;
        int expectedWrites = active && enabled ? 2 : 1;
        ProfileAdmissionToken? accepted = null;
        f.Catalog.BeforeWriteDispatchForTests = token =>
        {
            Assert.Same(accepted, token); Live(f, token);
            int ordinal = Interlocked.Increment(ref dispatches);
            Assert.InRange(ordinal, 1, expectedWrites);
            (ordinal == 1 ? flagDispatch : latestDispatch).Hold();
        };
        f.Catalog.AfterWriteCommitForTests = token =>
        {
            Assert.Same(accepted, token); Live(f, token);
            int ordinal = Interlocked.Increment(ref commits);
            Assert.InRange(ordinal, 1, expectedWrites);
            (ordinal == 1 ? flagCommit : latestCommit).Hold();
        };
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool commandsHeld = true;
        Task mutation = Task.CompletedTask, owner = Task.CompletedTask, capture = Task.CompletedTask, observer = Task.CompletedTask;
        TaskCompletionSource granted = Signal(), resume = Signal(), coreEnded = Signal(), releaseObserver = Signal();
        using CancellationTokenSource cancelExclusive = new();
        long version = f.Host.Projection.Version;
        try
        {
            capture = Task.Run(() => { lock (Field<object>(f.Host, "_viewCapture")) projection.Hold(); });
            await projection.Entered.Task.WaitAsync(Deadline);
            ProfileAdmissionToken[] before = ActiveAdmissions(f);
            mutation = f.Host.SetCaptureEnabledAsync(Id, enabled);
            accepted = AcceptedSince(f, before);
            Assert.False(mutation.IsCompleted);
            owner = f.Host.RunWithProfileProducerExclusiveAsync(async token =>
            {
                Assert.Equal(ProfileAdmissionKind.Exclusive, token.Kind);
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true); // No Apply or other mutation in this delegate.
            }, cancelExclusive.Token);
            // The real, uncontended owner lane reserves synchronously before drain.
            await AssertNewWritersRefusedAsync(f, active, enabled);
            Assert.Equal(0, dispatches); Assert.Equal(0, commits);
            Assert.False(granted.Task.IsCompleted);
            commands.Release(); commandsHeld = false;

            await flagDispatch.Entered.Task.WaitAsync(Deadline);
            AssertArchive(f, await f.ReadAsync(), !enabled, baseline.Summary.Revision, latestBefore);
            Live(f, accepted); Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);
            flagDispatch.Release();
            await flagCommit.Entered.Task.WaitAsync(Deadline);
            // Independent ArchiveReader: catalog reads would wait on the held lease
            // monitor. The actual SQL commit is already visible to this reader.
            AssertArchive(f, await f.ReadAsync(), enabled, baseline.Summary.Revision + 1, latestBefore);
            Assert.Equal(1, dispatches); Assert.Equal(1, commits);
            Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);
            if (active)
                AssertContent(f.Thread.GetSnapshot(new DownloadCounts()).Content, !enabled, latestBefore);

            Barrier finalCommit = flagCommit;
            if (active && enabled)
            {
                flagCommit.Release();
                await latestDispatch.Entered.Task.WaitAsync(Deadline);
                AssertContent(f.Thread.GetSnapshot(new DownloadCounts()).Content, true, latest: false);
                AssertArchive(f, await f.ReadAsync(), true, baseline.Summary.Revision + 1, latest: false);
                Live(f, accepted); Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);
                latestDispatch.Release();
                await latestCommit.Entered.Task.WaitAsync(Deadline);
                AssertArchive(f, await f.ReadAsync(), true, baseline.Summary.Revision + 2, latest: true);
                // forceLatest SQL committed, but its in-memory/result tail has not returned.
                AssertContent(f.Thread.GetSnapshot(new DownloadCounts()).Content, true, latest: false);
                Assert.Equal(2, dispatches); Assert.Equal(2, commits);
                Live(f, accepted); Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);
                finalCommit = latestCommit;
            }
            observer = Task.Run(async () =>
            {
                Assert.True(await commands.WaitAsync(Deadline));
                try { coreEnded.TrySetResult(); await releaseObserver.Task.WaitAsync(Deadline); }
                finally { commands.Release(); }
            });
            finalCommit.Release();
            await coreEnded.Task.WaitAsync(Deadline); // Real core + SQL disposal ended; host publication is still held.
            bool latestAfter = active;
            ArchivedThreadView final = await f.ReadAsync();
            AssertArchive(f, final, enabled, baseline.Summary.Revision + expectedWrites, latestAfter);
            Assert.Equal(baseline.Summary.FirstSeenAt, final.Summary.FirstSeenAt);
            if (active) AssertContent(f.Thread.GetSnapshot(new DownloadCounts()).Content, enabled, latestAfter);
            Assert.Equal(expectedWrites, dispatches); Assert.Equal(expectedWrites, commits);
            Assert.Equal(1, f.Host.AcceptedCommandCount); Live(f, accepted);
            Assert.False(granted.Task.IsCompleted); Assert.False(mutation.IsCompleted);

            projection.Release(); releaseObserver.TrySetResult();
            await observer.WaitAsync(Deadline); await capture.WaitAsync(Deadline);
            await mutation.WaitAsync(Deadline); await granted.Task.WaitAsync(Deadline);
            Assert.Equal(0, f.Host.AcceptedCommandCount); Retired(f, accepted);
            Assert.True(f.Host.Projection.Version > version); Assert.False(f.Host.Projection.IsStale);
            if (active) AssertContent(Assert.Single(f.Host.Projection.View.Threads).Snapshot.Content, enabled, latestAfter);
            await AssertNewWritersRefusedAsync(f, active, enabled); // Also rejected during active Exclusive.
            Assert.Equal(expectedWrites, dispatches); Assert.Equal(expectedWrites, commits);
            AssertArchive(f, await f.ReadAsync(), enabled, baseline.Summary.Revision + expectedWrites, latestAfter);
            f.ClearHooks();
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
            if (active) Assert.Equal(1, f.Thread.Observations); // Resume does not supply another observation to mask forceLatest.
            AssertArchive(f, await f.ReadAsync(), enabled, baseline.Summary.Revision + expectedWrites, latestAfter);
        }
        finally
        {
            flagDispatch.Release(); flagCommit.Release(); latestDispatch.Release(); latestCommit.Release(); projection.Release();
            releaseObserver.TrySetResult(); resume.TrySetResult(); cancelExclusive.Cancel();
            if (commandsHeld) commands.Release();
            await Observe(mutation); await Observe(owner); await Observe(capture); await Observe(observer); f.ClearHooks();
        }
    }

    [Fact]
    public async Task AcceptedAddBindsContentWithExactHostOwnerWhileExclusiveIsPending()
    {
        await using Fixture f = await Fixture.StartAsync();
        Assert.Null(await f.ReadOptionalAsync());
        using Barrier dispatch = new(), commit = new();
        ProfileAdmissionToken? accepted = null;
        int writes = 0, commits = 0;
        f.Catalog.BeforeWriteDispatchForTests = token =>
        { Assert.Same(accepted, token); Live(f, token); Assert.Equal(1, Interlocked.Increment(ref writes)); dispatch.Hold(); };
        f.Catalog.AfterWriteCommitForTests = token =>
        { Assert.Same(accepted, token); Live(f, token); Assert.Equal(1, Interlocked.Increment(ref commits)); commit.Hold(); };
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool commandsHeld = true;
        Task<DesktopAdmissionResult>? add = null;
        Task owner = Task.CompletedTask;
        TaskCompletionSource granted = Signal(), resume = Signal();
        using CancellationTokenSource cancelExclusive = new();
        try
        {
            ProfileAdmissionToken[] before = ActiveAdmissions(f);
            add = f.Host.AddAsync(Url);
            accepted = AcceptedSince(f, before);
            owner = f.Host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            }, cancelExclusive.Token);
            await AssertNewWritersRefusedAsync(f, active: false, enabled: true);
            commands.Release(); commandsHeld = false;
            await dispatch.Entered.Task.WaitAsync(Deadline);
            Assert.Null(await f.ReadOptionalAsync()); Assert.Null(f.Thread.GetSnapshot(new DownloadCounts()).Content);
            Assert.False(granted.Task.IsCompleted); Assert.False(add.IsCompleted);
            dispatch.Release();
            await commit.Entered.Task.WaitAsync(Deadline);
            AssertRegistration(f, await f.ReadAsync(), enabled: true);
            Assert.Null(f.Thread.GetSnapshot(new DownloadCounts()).Content); // Attach follows committed registration + filter reads.
            Live(f, accepted); Assert.False(granted.Task.IsCompleted); Assert.False(add.IsCompleted);
            Assert.False(f.Thread.Observed.Task.IsCompleted); // Cut prevents producer start, not this already accepted Bind.
            commit.Release();
            DesktopAdmissionResult result = await add.WaitAsync(Deadline);
            Assert.Equal(AdmissionStatus.Added, result.Status); Assert.NotNull(result.WatchId);
            await granted.Task.WaitAsync(Deadline);
            f.AssertAttached(); AssertContent(f.Thread.GetSnapshot(new DownloadCounts()).Content, true, capturedAt: null);
            Assert.False(f.Thread.Observed.Task.IsCompleted);
            Assert.Equal(1, writes); Assert.Equal(1, commits);
            Assert.Equal(0, f.Host.AcceptedCommandCount); Retired(f, accepted);
            AssertRegistration(f, await f.ReadAsync(), enabled: true);
            f.ClearHooks();
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            await f.Thread.Observed.Task.WaitAsync(Deadline);
            AssertArchive(f, await f.ReadAsync(), true, revision: 1, latest: true);
            AssertContent(f.Thread.GetSnapshot(new DownloadCounts()).Content, true, latest: true);
            Assert.Equal(DesktopSessionState.Ready, f.Host.State);
        }
        finally
        {
            dispatch.Release(); commit.Release(); resume.TrySetResult(); cancelExclusive.Cancel();
            if (commandsHeld) commands.Release();
            if (add != null) await Observe(add);
            await Observe(owner); f.ClearHooks();
        }
    }

    [Fact]
    public async Task StartupRestoreBindsRealContentWithHostStartupOwnerBeforeReady()
    {
        GatedSessionStore sessions = new();
        await using Fixture f = await Fixture.CreateAsync(restore: sessions);
        ProfileAdmissionToken startupOwner = Field<ProfileAdmissionToken>(f.Host, "_profileStartupAdmission");
        Assert.Equal(ProfileAdmissionKind.Startup, startupOwner.Kind);
        using Barrier dispatch = new(), commit = new();
        int writes = 0, commits = 0;
        Task startup = f.Host.StartAsync();
        try
        {
            await sessions.Entered.Task.WaitAsync(Deadline);
            // Host.InitializeAdmitted has completed before Restore asks for sessions.
            // No instance hook exists before Catalog.Open; this does not assert its DDL token identity.
            Assert.True(Field<ThreadContentRuntime>(f.Host, "_content").IsInitialized);
            Assert.Null(await f.ReadOptionalAsync());
            Assert.Equal(DesktopSessionState.Starting, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Starting, f.Lease.Admission.Phase);
            Live(f, startupOwner);
            f.Catalog.BeforeWriteDispatchForTests = token =>
            { Assert.Same(startupOwner, token); Live(f, token); Assert.Equal(1, Interlocked.Increment(ref writes)); dispatch.Hold(); };
            f.Catalog.AfterWriteCommitForTests = token =>
            { Assert.Same(startupOwner, token); Live(f, token); Assert.Equal(1, Interlocked.Increment(ref commits)); commit.Hold(); };
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SetCaptureEnabledAsync(Id, true).WaitAsync(Deadline));
            Assert.Equal(0, f.Host.AcceptedCommandCount); Assert.Equal(0, writes);
            sessions.Release();
            await dispatch.Entered.Task.WaitAsync(Deadline);
            Assert.Null(await f.ReadOptionalAsync()); Assert.False(startup.IsCompleted);
            dispatch.Release();
            await commit.Entered.Task.WaitAsync(Deadline);
            AssertRegistration(f, await f.ReadAsync(), enabled: false); // Legacy restore does not opt in to text capture.
            Assert.Null(f.Thread.GetSnapshot(new DownloadCounts()).Content);
            Assert.Equal(DesktopSessionState.Starting, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Starting, f.Lease.Admission.Phase);
            Assert.False(startup.IsCompleted); Live(f, startupOwner);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SetCaptureEnabledAsync(Id, true).WaitAsync(Deadline));
            commit.Release(); await startup.WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
            Assert.Null(f.Host.StartupError); Retired(f, startupOwner);
            Assert.Equal(1, writes); Assert.Equal(1, commits); Assert.Equal(0, f.Host.AcceptedCommandCount);
            f.AssertAttached(); AssertContent(f.Thread.GetSnapshot(new DownloadCounts()).Content, false, capturedAt: null);
            Assert.False(f.Thread.Observed.Task.IsCompleted); // This restore fixture stays user-paused.
            AssertRegistration(f, await f.ReadAsync(), enabled: false);
        }
        finally { sessions.Release(); dispatch.Release(); commit.Release(); await Observe(startup); f.ClearHooks(); }
    }

    private static async Task AssertNewWritersRefusedAsync(Fixture f, bool active, bool enabled)
    {
        Assert.Throws<InvalidOperationException>(() => { using var denied = f.Lease.Admission.EnterOrdinaryWriter(); });
        // Other target avoids the duplicate-command Busy lane, so this tests admission refusal.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SetCaptureEnabledAsync(Other, enabled).WaitAsync(Deadline));
        Assert.Throws<InvalidOperationException>(() => { _ = f.Catalog.SetCaptureEnabledAsync(Id, enabled); });
        if (active) Assert.Throws<InvalidOperationException>(() => { _ = f.Thread.SetCaptureEnabledAsync(enabled); });
    }

    private static void AssertContent(ThreadContentStatus? status, bool enabled, bool latest) =>
        AssertContent(status, enabled, latest ? LatestAt : PreviousAt);
    private static void AssertContent(ThreadContentStatus? status, bool enabled, DateTimeOffset? capturedAt)
    {
        Assert.NotNull(status); Assert.Equal(enabled, status.CaptureEnabled); Assert.Equal(capturedAt, status.LastCapturedAt);
        Assert.Null(status.CaptureFailure); Assert.Null(status.SelectionFailure); Assert.Equal(0, status.ExcludedMedia);
    }
    private static void AssertRegistration(Fixture f, ArchivedThreadView view, bool enabled)
    {
        Assert.Equal(Id, view.Summary.Id); Assert.Equal(Url, view.Summary.Url); Assert.Equal(Title, view.Summary.Title);
        Assert.Equal(f.Media, view.Summary.MediaDirectory); Assert.Equal(ThreadSourceState.Active, view.Summary.SourceState);
        Assert.Equal(enabled, view.Summary.CaptureEnabled); Assert.Equal(0, view.Summary.Revision);
        Assert.Null(view.Summary.LastCapturedAt); Assert.Equal(0, view.Summary.PostCount); Assert.Empty(view.GetPosts());
    }
    private static void AssertArchive(Fixture f, ArchivedThreadView view, bool enabled, long revision, bool latest)
    {
        CapturedThreadSnapshot expected = Snapshot(f.Media, latest);
        Assert.Equal(Id, view.Summary.Id); Assert.Equal(Url, view.Summary.Url); Assert.Equal(Title, view.Summary.Title);
        Assert.Equal(f.Media, view.Summary.MediaDirectory); Assert.Equal(ThreadSourceState.Active, view.Summary.SourceState);
        Assert.Equal(enabled, view.Summary.CaptureEnabled); Assert.Equal(revision, view.Summary.Revision);
        Assert.Equal<DateTimeOffset?>(expected.CapturedAt, view.Summary.LastCapturedAt);
        Assert.Equal(expected.Posts.Count, view.Summary.PostCount); Assert.Equal(expected.Posts.Count, view.PostCount);
        CapturedPost[] posts = view.GetPosts().ToArray();
        for (int index = 0; index < expected.Posts.Count; index++)
        {
            CapturedPost wanted = expected.Posts[index], actual = posts[index];
            Assert.Equal(wanted.PostId, actual.PostId); Assert.Equal(wanted.Ordinal, actual.Ordinal);
            Assert.Equal(wanted.Fields, actual.Fields); Assert.Equal(wanted.PostedAt, actual.PostedAt);
            Assert.Equal(wanted.Html, actual.Html); Assert.Equal(wanted.Subject, actual.Subject); Assert.Equal(wanted.Author, actual.Author);
            Assert.Equal(wanted.IsDeleted, actual.IsDeleted); Assert.Empty(actual.Attachments!);
        }
    }
    private static CapturedThreadSnapshot Snapshot(string media, bool latest) => new(Id, Url, Title,
        ThreadSourceState.Active, latest ? LatestAt : PreviousAt, media, latest
            ? [new(Id.Thread!, 0, PostFieldPresence.All, PostedAt: PreviousAt, Html: "<p>New disabled observation</p>", Attachments: []),
               new("2602", 1, PostFieldPresence.All, PostedAt: LatestAt, Html: "<p>Synthetic reply</p>", Attachments: [])]
            : [new(Id.Thread!, 0, PostFieldPresence.All, PostedAt: PreviousAt, Html: "<p>Previously saved content</p>", Attachments: [])]);

    private static async Task Observe(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (Exception) when (task.IsCompleted) { } // Never disguise a hung worker as teardown success.
    }
    private sealed class Barrier : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = Signal();
        internal void Hold() { Entered.TrySetResult(); Assert.True(_release.Wait(Deadline)); }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly SyntheticSource _source = new();
        internal string Profile => Path.Combine(_root.Path, "profile");
        private string Output => Path.Combine(_root.Path, "media");
        internal string Media => StorageLayout.GetThreadDirectory(Output, Id.Source, Id.Board, Id.Thread!);
        internal DesktopSessionHost Host { get; private set; } = null!;
        internal ProfileLease Lease => Field<ProfileLease>(Host, "_profileLease");
        internal ArchiveCatalog Catalog => Field<ThreadContentRuntime>(Host, "_content").Catalog;
        internal SyntheticThread Thread => _source.Thread;
        internal static async Task<Fixture> CreateAsync(bool? initialCapture = null, GatedSessionStore? restore = null)
        {
            Fixture f = new();
            try
            {
                Directory.CreateDirectory(f.Profile);
                new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = restore != null, DownloadsPaused = restore != null,
                    DownloadPath = f.Output }.SaveConfig(f.Profile);
                if (initialCapture.HasValue)
                {
                    using ProfileLease lease = ProfileLease.Acquire(f.Profile);
                    await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(f.Profile, lease).WaitAsync(Deadline);
                    await catalog.RegisterThreadAsync(new(Id, Url, Title, f.Media, initialCapture.Value)).WaitAsync(Deadline);
                    await catalog.CaptureAsync(Snapshot(f.Media, latest: false)).WaitAsync(Deadline);
                }
                f.Host = new(f.Profile, new IImageBoard[] { f._source },
                    sessionStoreFactory: restore == null ? null : (_, _) => restore);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal static async Task<Fixture> StartAsync(bool? initialCapture = null, bool active = false)
        {
            Fixture f = await CreateAsync(initialCapture);
            try
            {
                await f.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State); Assert.Null(f.Host.StartupError);
                if (active)
                {
                    DesktopAdmissionResult result = await f.Host.AddAsync(Url).WaitAsync(Deadline);
                    Assert.Equal(AdmissionStatus.Added, result.Status); Assert.NotNull(result.WatchId);
                    await f.Thread.Observed.Task.WaitAsync(Deadline);
                    await f.Host.CaptureProjectionAsync().WaitAsync(Deadline);
                    f.AssertAttached();
                }
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal void AssertAttached()
        {
            lock (Field<object>(Host, "_projection"))
                Assert.Same(Thread, Assert.Single(Field<Dictionary<long, IThread>>(Host, "_threadHandles").Values));
            Assert.False(Host.Projection.IsStale);
            Assert.Equal(Id, Assert.Single(Host.Projection.View.Threads).Snapshot.Id);
            Assert.NotNull(Thread.GetSnapshot(new DownloadCounts()).Content);
            Assert.Null(Thread.LastCheckFailure);
        }
        internal async Task<ArchivedThreadView?> ReadOptionalAsync()
        {
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(Profile).WaitAsync(Deadline);
            return await reader.ReadThreadAsync(Id).WaitAsync(Deadline);
        }
        internal async Task<ArchivedThreadView> ReadAsync() => Assert.IsType<ArchivedThreadView>(await ReadOptionalAsync());
        internal void ClearHooks()
        {
            if (Host == null || !Field<ThreadContentRuntime>(Host, "_content").IsInitialized) return;
            Catalog.BeforeWriteDispatchForTests = null; Catalog.AfterWriteCommitForTests = null;
        }
        public async ValueTask DisposeAsync()
        {
            ClearHooks();
            if (Host != null) await Host.DisposeAsync().AsTask().WaitAsync(Deadline);
            _source.Dispose(); _root.Dispose(); // Only this fixture's generated workspace, when Root runs the tests.
        }
    }

    private sealed class GatedSessionStore : ISessionStore
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        private readonly TaskCompletionSource _release = Signal();
        internal void Release() => _release.TrySetResult();
        public async Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await _release.Task.WaitAsync(Deadline, cancellationToken);
            return new(new SessionSnapshot { ThreadUrls = [Url] }, null, false);
        }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class SyntheticSource : IImageBoard, IDisposable
    {
        private readonly HttpClient _client = new(new RejectHttp());
        private readonly SyntheticBoard _board;
        internal SyntheticThread Thread { get; }
        internal SyntheticSource() { _board = new(this); Thread = new(_board); }
        public string Name => Id.Source;
        public string NiceName => "Synthetic host content source";
        public Uri BaseUri => new("https://host-content.invalid/");
        public bool CanHandle(Uri uri) => uri.Host == BaseUri.Host;
        public bool IsThreadUri(Uri uri) => CanHandle(uri) && uri.AbsoluteUri == Url;
        public bool IsBoardUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath == "/g/";
        public IBoard GetBoard(Uri uri) => _board;
        public IThread GetThread(Uri uri) => Thread;
        public HttpClient GetHttpClient() => _client;
        public void Dispose() => _client.Dispose();
    }
    private sealed class SyntheticBoard(SyntheticSource source) : BaseBoard
    {
        public override IImageBoard ImageBoard => source;
        public override string BoardCode => Id.Board;
        public override string Name { get; protected set; } = "Synthetic board";
        public override string NiceName => Name;
        public override Uri BoardUri => new(source.BaseUri, "g/");
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<IThread>>(Array.Empty<IThread>());
    }
    private sealed class SyntheticThread : BaseThread
    {
        private int _observations;
        internal SyntheticThread(SyntheticBoard board) { Board = board; Title = ThreadContentHostAdmissionTests.Title; }
        public override IBoard Board { get; }
        public override string ThreadId => Id.Thread!;
        public override string NiceName => Title;
        public override Uri ThreadUri => new(ThreadContentHostAdmissionTests.Url);
        public override bool SupportsContent => true;
        internal TaskCompletionSource Observed { get; } = Signal();
        internal int Observations => Volatile.Read(ref _observations);
        public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            // Exactly one real provider observation, before the race. A resumed
            // monitor cannot silently make the expected second capture happen.
            if (Interlocked.CompareExchange(ref _observations, 1, 0) == 0)
            {
                try
                {
                    await CaptureContentAsync(Snapshot(DownloadDirectory, latest: true), cancellationToken).ConfigureAwait(false);
                    Observed.TrySetResult();
                }
                catch (Exception error) { Observed.TrySetException(error); throw; }
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }
    private sealed class RejectHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic content fixture must not send HTTP requests.");
    }
}
