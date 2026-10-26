using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Boards.FourChan;
using MeowChan.Boards.EightKun;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>PF01 admission, coalesced final writers and the actual shutdown publication/ownership barrier.</summary>
public sealed class DesktopSessionAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static DesktopSessionHost CreateHost(string path, bool reading = false)
    {
        new Config { SaveUrlsOnExit = false, RememberReadingPosition = reading, AutomaticUpdateChecks = false }.SaveConfig(path);
        return new(path, Array.Empty<IImageBoard>());
    }
    private static SemaphoreSlim Commands(DesktopSessionHost host) => (SemaphoreSlim)typeof(DesktopSessionHost)
        .GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    [Fact]
    public async Task FreeCommandGateStillEntersSynchronousPersistenceOnAWorker()
    {
        using TempDirectory directory = new();
        new Config { DownloadsPaused = true, AutomaticUpdateChecks = false, DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        using BlockingSessionStore store = new();
        // Paused watches do not send network requests; only the injected owner store is exercised.
        using HttpClient client = new(new NoNetwork());
        DesktopSessionHost host = new(directory.Path, [new FourChanImageBoard(client)], (_, _) => store);
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            int callerThread = 0;
            Task<Task<DesktopAdmissionResult>> admission = Task.Factory.StartNew(() =>
            {
                callerThread = Environment.CurrentManagedThreadId;
                return host.AddAsync("https://boards.4chan.org/g/thread/123");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // This completion precedes releasing the held synchronous SaveAsync. The original immediately
            // acquired semaphore implementation cannot return the Task to its caller in this ordering.
            Task<DesktopAdmissionResult> command = await admission.WaitAsync(Deadline);
            await store.Entered.Task.WaitAsync(Deadline);
            Assert.NotEqual(callerThread, store.SaveThread);
            Assert.False(command.IsCompleted);
            store.Release.Set();
            Assert.Equal(AdmissionStatus.Added, (await command.WaitAsync(Deadline)).Status);
        }
        finally { store.Release.Set(); await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Paused admission must not perform network IO.");
        }
    }

    [Fact]
    public async Task RejectedSyntheticExampleDoesNotPoisonAdmissionRestoreOrOtherSourceHints()
    {
        using TempDirectory directory = new();
        new Config { DownloadsPaused = true, AutomaticUpdateChecks = false,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        using NoNetwork transport = new();
        using HttpClient client = new(transport);
        FixedBoardProvider provider = new(client);
        await using (DesktopSessionHost host = new(directory.Path, [provider, new EightKunImageBoard(client)]))
        {
            await host.StartAsync().WaitAsync(Deadline);
            Assert.True(provider.SyntheticThreadProbes > 0); // The former throwing path really ran.
            Assert.Null(host.Projection.CaptureError);
            Assert.Equal(0, host.ProjectionStats.Failures);
            Assert.Contains("https://boards.4chan.org/g/", host.Projection.AdmissionExamples);
            Assert.Contains("https://8kun.top/b/res/123456.html", host.Projection.AdmissionExamples);
            int initialProbes = provider.SyntheticThreadProbes;
            DesktopAdmissionResult admission = await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Deadline);
            Assert.Equal(AdmissionStatus.Added, admission.Status);
            DesktopThreadEntry added = Assert.Single(host.GetView().Threads);
            Assert.Equal(admission.WatchId, added.WatchId);
            Assert.Equal("123", added.Snapshot.Id.Thread);
            DiagnosticTarget target = new(DiagnosticTargetKind.Thread, added.WatchId);
            Assert.Contains(target, host.Projection.CheckableTargets);
            Assert.False(host.CanRequestCheck(target)); // Paused watches retain capability, but cannot start checks.
            for (int index = 0; index < 5; index++)
            {
                DesktopSessionProjection capture = await host.CaptureProjectionAsync().WaitAsync(Deadline);
                Assert.False(capture.IsStale);
                Assert.Single(capture.View.Threads);
            }
            Assert.Equal(initialProbes, provider.SyntheticThreadProbes); // Rejected probes are not retried every frame.
        }
        await using (DesktopSessionHost restored = new(directory.Path, [new FixedBoardProvider(client), new EightKunImageBoard(client)]))
        {
            await restored.StartAsync().WaitAsync(Deadline);
            Assert.Null(restored.Projection.CaptureError);
            Assert.Equal("123", Assert.Single(restored.GetView().Threads).Snapshot.Id.Thread);
            Assert.Contains("https://8kun.top/b/res/123456.html", restored.Projection.AdmissionExamples);
        }
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task ActualCoreCaptureArgumentFailureStillPublishesStaleErrorAndCanRecover()
    {
        using TempDirectory directory = new();
        new Config { DownloadsPaused = true, AutomaticUpdateChecks = false,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        using HttpClient client = new(new NoNetwork());
        FixedBoardProvider provider = new(client);
        await using DesktopSessionHost host = new(directory.Path, [provider]);
        await host.StartAsync().WaitAsync(Deadline);
        await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Deadline);
        DesktopThreadEntry complete = Assert.Single(host.Projection.View.Threads);
        long failures = host.ProjectionStats.Failures;
        try
        {
            provider.Board.FailName = true;
            DesktopSessionProjection stale = await host.CaptureProjectionAsync().WaitAsync(Deadline);
            Assert.True(stale.IsStale);
            Assert.Contains("Core capture fixture failure", stale.CaptureError);
            Assert.Contains(stale.View.Warnings, warning => warning.Contains("Core capture fixture failure", StringComparison.Ordinal));
            Assert.Equal(complete.WatchId, Assert.Single(stale.View.Threads).WatchId);
            Assert.True(host.ProjectionStats.Failures > failures);
        }
        finally { provider.Board.FailName = false; }
        DesktopSessionProjection recovered = await host.CaptureProjectionAsync().WaitAsync(Deadline);
        Assert.False(recovered.IsStale);
        Assert.Null(recovered.CaptureError);
        Assert.Equal(complete.WatchId, Assert.Single(recovered.View.Threads).WatchId);
    }

    // Exactly the customization behind the integration failures: GetBoard intentionally returns /g/
    // even when an optional /b/thread/... example was invented. Real /g/ watches remain valid.
    private sealed class FixedBoardProvider : FourChanImageBoard
    {
        private int _syntheticThreadProbes;
        internal FixedBoardProvider(HttpClient client) : base(client) => Board = new(this);
        internal FixedBoard Board { get; }
        internal int SyntheticThreadProbes => Volatile.Read(ref _syntheticThreadProbes);
        public override IBoard GetBoard(Uri boardUri) => Board;
        public override IThread GetThread(Uri threadUri)
        {
            if (threadUri.AbsolutePath.StartsWith("/b/thread/", StringComparison.Ordinal))
                Interlocked.Increment(ref _syntheticThreadProbes);
            return base.GetThread(threadUri);
        }
    }

    private sealed class FixedBoard(IImageBoard provider) : FourChanBoard(provider, new Uri("https://boards.4chan.org/g/"))
    {
        internal volatile bool FailName;
        public override string NiceName => FailName ? throw new ArgumentException("Core capture fixture failure") : base.NiceName;
    }

    private sealed class BlockingSessionStore : ISessionStore, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal int SaveThread;
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLoadResult(new SessionSnapshot(), null, false));
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveThread = Environment.CurrentManagedThreadId; Entered.TrySetResult();
            if (!Release.Wait(Deadline)) throw new TimeoutException("The test did not release its synchronous store.");
            return Task.CompletedTask;
        }
        public void Dispose() => Release.Dispose();
    }

    [Fact]
    public async Task EightInteractiveSlotsDeduplicateActionAndTargetAndKeepPauseOutsideTheBudget()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = CreateHost(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        SemaphoreSlim gate = Commands(host);
        await gate.WaitAsync();
        Task[] accepted = Array.Empty<Task>();
        Task pause = Task.CompletedTask;
        try
        {
            accepted = Enumerable.Range(1, 8).Select(id => host.RemoveThreadAsync(id)).ToArray();
            Assert.Equal(8, host.AcceptedCommandCount);
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => host.RemoveThreadAsync(1));
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => host.RemoveThreadAsync(9));
            Config draft = host.Config.Clone(); draft.Theme = "Light";
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => host.ApplySettingsAsync(draft));
            Assert.Equal("Light", draft.Theme);
            Assert.Equal("Dark", host.Config.Theme);
            pause = host.SetPausedAsync(true);
            Assert.Same(pause, host.SetPausedAsync(true));
            Assert.False(pause.IsCompleted);
            Assert.Equal(8, host.AcceptedCommandCount);
        }
        finally { gate.Release(); await Task.WhenAll(accepted.Append(pause)).WaitAsync(Deadline); }
        Assert.Equal(0, host.AcceptedCommandCount);
        Assert.True(host.Projection.IsPaused);
    }

    [Fact]
    public async Task MixedPendingPreferenceEditsAccumulateEveryIndependentFieldWithOneLatestBatch()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = CreateHost(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        SemaphoreSlim gate = Commands(host);
        await gate.WaitAsync();
        List<Task> writes = new();
        try
        {
            writes.Add(host.SaveViewPreferencesAsync(config => config.WindowWidth = 1100));
            Assert.True(SpinWait.SpinUntil(() => host.PreferenceWriteCounts.Active == 1, Deadline));
            writes.Add(host.SaveViewPreferencesAsync(config => config.Theme = "Light"));
            Task pending = writes[^1];
            writes.Add(host.SaveViewPreferencesAsync(config => config.LastExportDirectory = Path.Combine(directory.Path, "export")));
            for (int value = 200; value < 240; value++)
            {
                int width = value;
                writes.Add(host.SaveViewPreferencesAsync(config => config.ColumnWidths["archive.title"] = width));
            }
            Assert.All(writes.Skip(1), task => Assert.Same(pending, task));
            Assert.Equal((1, 1, 0), host.PreferenceWriteCounts);
            Assert.All(writes, task => Assert.False(task.IsCompleted));
        }
        finally { gate.Release(); }
        await Task.WhenAll(writes).WaitAsync(Deadline);
        await host.FlushPreferencesAsync().WaitAsync(Deadline);
        Config saved = Config.LoadConfig(directory.Path);
        Assert.Equal(1100, saved.WindowWidth); Assert.Equal("Light", saved.Theme);
        Assert.Equal(Path.Combine(directory.Path, "export"), saved.LastExportDirectory);
        Assert.Equal(239, saved.ColumnWidths["archive.title"]);
    }

    [Fact]
    public async Task LatestPreferenceCanRevertAnActiveValueAndRetainAnotherPendingField()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = CreateHost(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        SemaphoreSlim gate = Commands(host);
        await gate.WaitAsync();
        List<Task> writes = new();
        try
        {
            writes.Add(host.SaveViewPreferencesAsync(config => config.Theme = "Light"));
            Assert.True(SpinWait.SpinUntil(() => host.PreferenceWriteCounts.Active == 1, Deadline));
            writes.Add(host.SaveViewPreferencesAsync(config => config.WindowWidth = 1120));
            writes.Add(host.SaveViewPreferencesAsync(config => config.Theme = "Dark"));
        }
        finally { gate.Release(); }
        await Task.WhenAll(writes).WaitAsync(Deadline);
        Config saved = Config.LoadConfig(directory.Path);
        Assert.Equal("Dark", saved.Theme); Assert.Equal(1120, saved.WindowWidth);
    }

    [Fact]
    public async Task FailedPreferenceWriteKeepsTheDraftForExplicitFlushRetry()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = CreateHost(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        string settings = Path.Combine(directory.Path, "settings.json");
        File.Delete(settings); Directory.CreateDirectory(settings);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => host.SaveViewPreferencesAsync(config => config.Theme = "Light"));
            Assert.Equal("Dark", host.Config.Theme);
            Assert.Equal(1, host.PreferenceWriteCounts.Failed);
            await Assert.ThrowsAnyAsync<Exception>(() => host.FlushPreferencesAsync());
        }
        finally { Directory.Delete(settings); }
        await host.FlushPreferencesAsync().WaitAsync(Deadline);
        Assert.Equal("Light", Config.LoadConfig(directory.Path).Theme);
        Assert.Equal(0, host.PreferenceWriteCounts.Failed);
    }

    [Fact]
    public async Task ExplicitSettingsSaveWaitsForAllEarlierAutomaticPreferenceDeltas()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = CreateHost(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        Config draft = host.Config.Clone(); draft.RecheckTimer = 120;
        SemaphoreSlim gate = Commands(host);
        await gate.WaitAsync();
        Task view = host.SaveViewPreferencesAsync(config => config.GalleryContextOpen = true);
        Task settings = host.ApplySettingsAsync(draft);
        draft.RecheckTimer = 180; // Accepted drafts must already be frozen.
        gate.Release();
        await Task.WhenAll(view, settings).WaitAsync(Deadline);
        Config saved = Config.LoadConfig(directory.Path);
        Assert.Equal(120, saved.RecheckTimer); Assert.True(saved.GalleryContextOpen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FrozenSettingsRetainsOriginalPresentationBaselineAndIndependentLivePreferences(bool withBaseline)
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = CreateHost(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        Config draft = host.Config.Clone();
        if (!withBaseline)
        {
            draft.SettingsBaselineCompactDensity = null;
            draft.SettingsBaselineReadingFontSize = null;
            draft.SettingsBaselineWorkspaceMode = null;
            draft.SettingsBaselineProfiles = null;
        }
        draft.WorkspaceMode = "Advanced";
        draft.CompactDensity = true;
        draft.ReadingFontSize = "Large";
        SemaphoreSlim gate = Commands(host);
        await gate.WaitAsync();
        Task view = Task.CompletedTask, settings = Task.CompletedTask;
        try
        {
            view = host.SaveViewPreferencesAsync(config =>
            {
                config.CompactDensity = true;
                config.ReadingFontSize = "Small";
                config.GetLayoutProfile("Advanced").ColumnWidths["ThreadsDataGrid.Title"] = 640;
            });
            settings = host.ApplySettingsAsync(draft);
            // Neither edited values nor the original baseline objects may remain shared with admission.
            draft.WorkspaceMode = "Simple";
            draft.GetLayoutProfile("Advanced").CompactDensity = false;
            draft.GetLayoutProfile("Advanced").ReadingFontSize = "Normal";
            if (draft.SettingsBaselineProfiles is { } baseline)
            {
                baseline["Advanced"].CompactDensity = true;
                baseline["Advanced"].ReadingFontSize = "Large";
                baseline["Barebones"].CompactDensity = true;
                baseline["Barebones"].ReadingFontSize = "Small";
                baseline.Clear();
                baseline["Advanced"] = new() { CompactDensity = true, ReadingFontSize = "Large" };
                baseline["Barebones"] = new() { CompactDensity = true, ReadingFontSize = "Small" };
            }
        }
        finally { gate.Release(); }
        await Task.WhenAll(view, settings).WaitAsync(Deadline);
        Config saved = Config.LoadConfig(directory.Path);
        Assert.Equal("Advanced", saved.WorkspaceMode);
        Assert.True(saved.CompactDensity);
        Assert.Equal("Large", saved.ReadingFontSize);
        Assert.Equal(640, saved.ColumnWidths["ThreadsDataGrid.Title"]);
        // Fields untouched in the draft retain newer automatic preference writes.
        Assert.True(saved.GetLayoutProfile("Barebones").CompactDensity);
        Assert.Equal("Small", saved.GetLayoutProfile("Barebones").ReadingFontSize);
    }

    [Fact]
    public async Task ForcedCaptureBurstUsesAtMostOneActiveAndOnePendingCompletion()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = CreateHost(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        using ManualResetEventSlim release = new();
        Task holder = Task.Run(() => host.HoldCaptureLockForTest(release));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => host.CaptureLockHeldForTest, Deadline));
            long before = host.ProjectionStats.Captures;
            Task<DesktopSessionProjection>[] captures = Enumerable.Range(0, 100).Select(_ => host.CaptureProjectionAsync()).ToArray();
            Assert.InRange(captures.Distinct().Count(), 1, 2);
            Assert.All(captures, task => Assert.False(task.IsCompleted));
            Assert.False(host.CanRequestCheck(new(DiagnosticTargetKind.Thread, long.MaxValue)));
            release.Set();
            await Task.WhenAll(captures).WaitAsync(Deadline);
            Assert.InRange(host.ProjectionStats.Captures - before, 1, 2);
        }
        finally { release.Set(); await holder.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task ShutdownPublishesAcceptanceAndRetainsLeaseUntilHeldCaptureActuallyReturns()
    {
        using TempDirectory directory = new();
        DesktopSessionHost host = CreateHost(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        using ManualResetEventSlim release = new();
        Task holder = Task.Run(() => host.HoldCaptureLockForTest(release));
        Task dispose = Task.CompletedTask;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => host.CaptureLockHeldForTest, Deadline));
            Task capture = host.CaptureProjectionAsync();
            Task engine = host.BeginShutdown();
            Assert.Equal(DesktopSessionState.Stopping, host.Projection.View.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.RemoveThreadAsync(12));
            dispose = host.DisposeAsync().AsTask();
            Assert.False(dispose.IsCompleted);
            Assert.Throws<IOException>(() => ProfileLease.Acquire(directory.Path));
            release.Set();
            await Task.WhenAll(capture, engine, dispose).WaitAsync(Deadline);
            long terminal = host.Projection.Version;
            int notifications = 0; host.ProjectionPublished += () => notifications++;
            await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => host.CaptureProjectionAsync())).WaitAsync(Deadline);
            Assert.Equal(terminal, host.Projection.Version); Assert.Equal(0, notifications);
            Assert.Equal(DesktopSessionState.Stopped, host.Projection.View.State);
            using ProfileLease reacquired = ProfileLease.Acquire(directory.Path);
        }
        finally { release.Set(); await holder.WaitAsync(Deadline); await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    [Fact]
    public async Task StopAcceptanceDoesNotWaitForStartupsForcedCapture()
    {
        using TempDirectory directory = new();
        DesktopSessionHost host = CreateHost(directory.Path);
        using ManualResetEventSlim release = new();
        Task holder = Task.Run(() => host.HoldCaptureLockForTest(release));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => host.CaptureLockHeldForTest, Deadline));
            Task start = host.StartAsync();
            // Force a capture waiter even when startup IO is still in flight.
            Task capture = host.CaptureProjectionAsync();
            Task stop = host.StopAsync();
            Assert.Equal(DesktopSessionState.Stopping, host.Projection.View.State);
            Assert.False(start.IsCompleted); Assert.False(stop.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync("https://boards.4chan.org/g/thread/123"));
            Assert.Throws<IOException>(() => ProfileLease.Acquire(directory.Path));
            release.Set();
            await Task.WhenAll(start, capture, stop).WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Stopped, host.Projection.View.State);
        }
        finally { release.Set(); await holder.WaitAsync(Deadline); await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    [Fact]
    public async Task QuiescenceAllowsFinalViewAndReadingWritesBeforeTheExplicitDrain()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = CreateHost(directory.Path, reading: true);
        await host.StartAsync().WaitAsync(Deadline);
        await host.BeginShutdown().WaitAsync(Deadline);
        LocalReadingPosition final = new("4chan", "g", "1000", "1020", -20, "1022");
        await host.SaveViewPreferencesAsync(config => config.WindowWidth = 1120).WaitAsync(Deadline);
        await host.RememberReadingPositionAsync(final, host.ReadingPositionGeneration).WaitAsync(Deadline);
        await host.FlushPreferencesAsync().WaitAsync(Deadline);
        Assert.Throws<IOException>(() => ProfileLease.Acquire(directory.Path));
        Assert.Equal(1120, Config.LoadConfig(directory.Path).WindowWidth);
        Assert.Contains("1020", File.ReadAllText(Path.Combine(directory.Path, "reading-position.json")));
        await host.StopAsync().WaitAsync(Deadline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveViewPreferencesAsync(config => config.WindowWidth = 1200));
    }
}
