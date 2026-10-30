using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class DesktopSessionHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PauseSurvivesRestartAndSettingsDraftWithoutRewritingWatchPersistence(bool saveUrls)
    {
        using TempDirectory directory = new TempDirectory();
        Config settings = new Config { SaveUrlsOnExit = saveUrls };
        settings.SaveConfig(directory.Path);
        string sessionPath = Path.Combine(directory.Path, "session.json");
        File.WriteAllText(sessionPath, "retained watch intent");
        // Use an injected empty store: this test isolates preferences from watch restoration.
        ControlledStore store = new ControlledStore();
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            Config draft = host.Config.Clone();
            await host.SetPausedAsync(true).WaitAsync(Timeout);
            await host.ApplySettingsAsync(draft).WaitAsync(Timeout);
            Assert.True(host.IsPaused);
            Assert.True(Config.LoadConfig(directory.Path).DownloadsPaused);
            Assert.Equal("retained watch intent", File.ReadAllText(sessionPath));
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
        DesktopSessionHost restarted = new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>(), (_, _) => new ControlledStore());
        try
        {
            await restarted.StartAsync().WaitAsync(Timeout);
            Assert.True(restarted.IsPaused);
            await restarted.SetPausedAsync(false).WaitAsync(Timeout);
            Assert.False(Config.LoadConfig(directory.Path).DownloadsPaused);
        }
        finally { await restarted.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task OldSettingsDraftPreservesDirectViewPreferences()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), (_, _) => new ControlledStore());
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            Config draft = host.Config.Clone();
            draft.RecheckTimer = 120;
            await host.SaveViewPreferencesAsync(config =>
            {
                config.ArchiveThreadLayout = "Table";
                config.ArchiveSplitWidth = 430;
                config.GalleryContextOpen = true;
                config.CompactDensity = true;
                config.WindowWidth = 1120;
                config.WindowHeight = 740;
                config.WindowPositionX = 35;
                config.WindowPositionY = 60;
                config.WindowMaximized = true;
                config.ColumnWidths["archive.title"] = 340;
                config.LastExportDirectory = Path.Combine(directory.Path, "exports");
            }).WaitAsync(Timeout);

            await host.ApplySettingsAsync(draft).WaitAsync(Timeout);

            Config saved = Config.LoadConfig(directory.Path);
            Assert.Equal(120, saved.RecheckTimer);
            Assert.Equal("Table", saved.ArchiveThreadLayout);
            Assert.Equal(430, saved.ArchiveSplitWidth);
            Assert.True(saved.GalleryContextOpen);
            Assert.True(saved.CompactDensity);
            Assert.Equal(1120, saved.WindowWidth);
            Assert.Equal(740, saved.WindowHeight);
            Assert.Equal(35, saved.WindowPositionX);
            Assert.Equal(60, saved.WindowPositionY);
            Assert.True(saved.WindowMaximized);
            Assert.Equal(340, saved.ColumnWidths["archive.title"]);
            Assert.Equal(Path.Combine(directory.Path, "exports"), saved.LastExportDirectory);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Theory]
    [InlineData(null, 29)]
    [InlineData(47, 47)]
    [InlineData(0, 0)]
    public async Task SettingsDraftMergesNewerGalleryVolumeUnlessVolumeWasEdited(int? editedVolume, int expectedVolume)
    {
        using TempDirectory directory = new();
        new Config { VideoVolume = 70 }.SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), (_, _) => new ControlledStore());
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            Config draft = host.Config.Clone();
            draft.RecheckTimer = 120;
            if (editedVolume.HasValue) draft.VideoVolume = editedVolume.Value;
            await host.SaveViewPreferencesAsync(current => current.VideoVolume = 29).WaitAsync(Timeout);

            await host.ApplySettingsAsync(draft).WaitAsync(Timeout);

            Assert.Equal(expectedVolume, host.Config.VideoVolume);
            Config saved = Config.LoadConfig(directory.Path);
            Assert.Equal(expectedVolume, saved.VideoVolume);
            Assert.Equal(120, saved.RecheckTimer);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ManualCheckUsesExactWatchLifetimeAndRespectsPause()
    {
        using TempDirectory directory = new TempDirectory();
        new Config { DownloadsPaused = true, DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        using HttpClient client = new HttpClient(new FixtureTransport());
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, new[] { new FourChanImageBoard(client) });
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            DesktopAdmissionResult first = await host.AddAsync("https://boards.4chan.org/g/thread/123");
            DiagnosticTarget old = new DiagnosticTarget(DiagnosticTargetKind.Thread, first.WatchId!.Value);
            Assert.False(host.CanRequestCheck(old));
            Assert.Equal(CheckRequestStatus.Paused, await host.RequestCheckAsync(old));
            await host.RemoveThreadAsync(old.WatchId);
            DesktopAdmissionResult replacement = await host.AddAsync("https://boards.4chan.org/g/thread/123");
            Assert.NotEqual(first.WatchId, replacement.WatchId);
            await host.SetPausedAsync(false);
            Assert.Equal(CheckRequestStatus.Stale, await host.RequestCheckAsync(old));
            DiagnosticTarget current = new DiagnosticTarget(DiagnosticTargetKind.Thread, replacement.WatchId!.Value);
            Assert.True(host.CanRequestCheck(current));
            Assert.Equal(CheckRequestStatus.Stale, await host.RequestCheckAsync(current with { Kind = DiagnosticTargetKind.Board }));
            Assert.Contains(await host.RequestCheckAsync(current), new[] { CheckRequestStatus.Scheduled, CheckRequestStatus.AlreadyScheduled });
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task PausedStartupRestoresRealWatchesWithoutRequestsAndResumeKeepsTheirIdentity()
    {
        using TempDirectory directory = new TempDirectory();
        new Config { DownloadsPaused = true, DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        FixtureTransport initialTransport = new FixtureTransport();
        using HttpClient initialClient = new HttpClient(initialTransport);
        DesktopSessionHost first = new DesktopSessionHost(directory.Path, new[] { new FourChanImageBoard(initialClient) });
        try
        {
            await first.StartAsync().WaitAsync(Timeout);
            await first.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
            await first.AddAsync("https://boards.4chan.org/g/").WaitAsync(Timeout);
        }
        finally { await first.DisposeAsync().AsTask().WaitAsync(Timeout); }
        Assert.Equal(0, initialTransport.Requests);
        FixtureTransport restoredTransport = new FixtureTransport();
        using HttpClient restoredClient = new HttpClient(restoredTransport);
        DesktopSessionHost restored = new DesktopSessionHost(directory.Path, new[] { new FourChanImageBoard(restoredClient) });
        try
        {
            await restored.StartAsync().WaitAsync(Timeout);
            Assert.True(restored.IsPaused);
            DesktopSessionView before = restored.GetView();
            long threadId = Assert.Single(before.Threads).WatchId;
            long boardId = Assert.Single(before.Boards).WatchId;
            Assert.Equal(0, restoredTransport.Requests);
            await restored.SetPausedAsync(false).WaitAsync(Timeout);
            await restoredTransport.FirstRequest.Task.WaitAsync(Timeout);
            DesktopSessionView after = restored.GetView();
            Assert.Contains(after.Threads, entry => entry.WatchId == threadId);
            Assert.Contains(after.Boards, entry => entry.WatchId == boardId);
        }
        finally { await restored.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task PauseTransitionRemainsBusyUntilSourceCancellationAndSerializesStop()
    {
        using TempDirectory directory = new();
        using DelayedPauseTransport transport = new();
        using HttpClient client = new(transport);
        DesktopSessionHost host = new(directory.Path, new[] { new FourChanImageBoard(client) });
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
            await transport.Started.Task.WaitAsync(Timeout);
            ConcurrentQueue<bool> completedTransitionConfig = new();
            host.StateChanged += () =>
            {
                if (host.IsPaused && !host.IsPauseTransitioning)
                    completedTransitionConfig.Enqueue(host.Config.DownloadsPaused);
            };
            Task pause = host.SetPausedAsync(true);
            await transport.Cancelled.Task.WaitAsync(Timeout);
            // Cancellation confirms the runtime transition; the UI observes its asynchronous projection.
            await host.CaptureProjectionAsync().WaitAsync(Timeout);
            Assert.True(host.IsPaused);
            Assert.True(host.IsPauseTransitioning);
            Assert.False(pause.IsCompleted);
            Task stop = host.StopAsync();
            Assert.False(stop.IsCompleted);
            transport.Release.TrySetResult();
            await Task.WhenAll(pause, stop).WaitAsync(Timeout);
            Assert.False(host.IsPauseTransitioning);
            Assert.NotEmpty(completedTransitionConfig);
            Assert.All(completedTransitionConfig, value => Assert.True(value));
            Assert.Equal(1, transport.Requests);
        }
        finally { transport.Release.TrySetResult(); await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    private sealed class DelayedPauseTransport : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            Started.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                await Release.Task;
                throw;
            }
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task FailedPauseWriteLeavesRuntimeRunning()
    {
        using TempDirectory directory = new TempDirectory();
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>());
        try
        {
            await host.StartAsync().WaitAsync(Timeout);
            File.Delete(Path.Combine(directory.Path, "settings.json"));
            Directory.CreateDirectory(Path.Combine(directory.Path, "settings.json"));
            await Assert.ThrowsAnyAsync<Exception>(() => host.SetPausedAsync(true).WaitAsync(Timeout));
            Assert.False(host.IsPaused);
            Assert.False(host.Config.DownloadsPaused);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ProfileRemainsLockedThroughStopUntilNativeCleanupCanFinish()
    {
        using TempDirectory directory = new TempDirectory();
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>());
        await using BoundedHostDisposal cleanup = new BoundedHostDisposal(host);
        Assert.Throws<IOException>(() => new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>()));

        await host.StartAsync().WaitAsync(Timeout);
        await host.StopAsync().WaitAsync(Timeout);

        Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.Throws<IOException>(() => ProfileLease.Acquire(directory.Path));
        await host.DisposeAsync().AsTask().WaitAsync(Timeout);
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Assert.Equal(directory.Path, lease.ProfileDirectory);
    }

    [Fact]
    public async Task RestoreFailureIsVisibleAndDoesNotEnableMutations()
    {
        using TempDirectory directory = new TempDirectory();
        ControlledStore store = new ControlledStore
        {
            Load = () => Task.FromException<SessionLoadResult>(new IOException("Synthetic profile read failure"))
        };
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        await using BoundedHostDisposal cleanup = new BoundedHostDisposal(host);

        Task start = host.StartAsync();
        await start.WaitAsync(Timeout);

        Assert.Same(start, host.StartAsync());
        Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
        Assert.Contains("Synthetic profile read failure", host.StartupError);
        Assert.Equal(host.StartupError, host.GetView().StartupError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ApplySettingsAsync(host.Config.Clone()).WaitAsync(Timeout));
        Assert.Equal(0, store.Saves);
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
    }

    [Fact]
    public async Task StopDuringRestoreWaitsForItWithoutBrieflyReenablingCommands()
    {
        using TempDirectory directory = new TempDirectory();
        TaskCompletionSource<SessionLoadResult> restore = new TaskCompletionSource<SessionLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledStore store = new ControlledStore { Load = () => restore.Task };
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        await using BoundedHostDisposal cleanup = new BoundedHostDisposal(host);
        ConcurrentQueue<DesktopSessionState> states = new ConcurrentQueue<DesktopSessionState>();
        host.StateChanged += () => states.Enqueue(host.State);

        try
        {
            Task start = host.StartAsync();
            Task stop = host.StopAsync();
            Assert.False(stop.IsCompleted);
            Assert.Equal(DesktopSessionState.Stopping, host.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout));
            restore.TrySetResult(new SessionLoadResult(new SessionSnapshot(), null, false));
            await Task.WhenAll(start, stop).WaitAsync(Timeout);

            Assert.Equal(DesktopSessionState.Stopped, host.State);
            Assert.DoesNotContain(DesktopSessionState.Ready, states);
            Assert.Same(stop, host.StopAsync());
        }
        finally
        {
            // Failed pre-release assertions must not leave await-disposal waiting on our own fixture forever.
            restore.TrySetResult(new SessionLoadResult(new SessionSnapshot(), null, false));
        }
    }

    [Fact]
    public async Task StaleThreadCommandCannotRemoveAReadmittedWatch()
    {
        using TempDirectory directory = new TempDirectory();
        using HttpClient client = new HttpClient(new FixtureTransport());
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, new IImageBoard[] { new FourChanImageBoard(client) });
        await using BoundedHostDisposal cleanup = new BoundedHostDisposal(host);
        await host.StartAsync().WaitAsync(Timeout);

        DesktopAdmissionResult first = await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
        DesktopAdmissionResult duplicate = await host.AddAsync("https://boards.4channel.org/g/thread/123/title#p124").WaitAsync(Timeout);
        long firstId = Assert.IsType<long>(first.WatchId);
        Assert.Equal(AdmissionStatus.AlreadyPresent, duplicate.Status);
        Assert.Equal(firstId, duplicate.WatchId);
        await host.RemoveThreadAsync(firstId).WaitAsync(Timeout);

        DesktopAdmissionResult readmitted = await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
        long nextId = Assert.IsType<long>(readmitted.WatchId);
        Assert.True(nextId > firstId);
        await host.RemoveThreadAsync(firstId).WaitAsync(Timeout);

        Assert.Equal(nextId, Assert.Single(host.GetView().Threads).WatchId);
    }

    [Fact]
    public async Task RemovingBoardRetainsThreadsAndStaleBoardCommandCannotRemoveItsReplacement()
    {
        using TempDirectory directory = new TempDirectory();
        using HttpClient client = new HttpClient(new FixtureTransport());
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, new IImageBoard[] { new FourChanImageBoard(client) });
        await using BoundedHostDisposal cleanup = new BoundedHostDisposal(host);
        await host.StartAsync().WaitAsync(Timeout);
        DesktopAdmissionResult thread = await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
        DesktopAdmissionResult board = await host.AddAsync("https://boards.4chan.org/g/").WaitAsync(Timeout);
        long boardId = Assert.IsType<long>(board.WatchId);

        await host.RemoveBoardAsync(boardId).WaitAsync(Timeout);
        Assert.Equal(thread.WatchId, Assert.Single(host.GetView().Threads).WatchId);
        Assert.Empty(host.GetView().Boards);
        DesktopAdmissionResult replacement = await host.AddAsync("https://boards.4chan.org/g/catalog").WaitAsync(Timeout);
        await host.RemoveBoardAsync(boardId).WaitAsync(Timeout);

        Assert.NotEqual(boardId, replacement.WatchId);
        Assert.Equal(replacement.WatchId, Assert.Single(host.GetView().Boards).WatchId);
    }

    [Fact]
    public async Task FailedSettingsWritePreservesActiveValuesAndSuccessfulChangesKeepExistingTargets()
    {
        using TempDirectory directory = new TempDirectory();
        using HttpClient client = new HttpClient(new FixtureTransport());
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, new IImageBoard[] { new FourChanImageBoard(client) });
        await using BoundedHostDisposal cleanup = new BoundedHostDisposal(host);
        await host.StartAsync().WaitAsync(Timeout);
        await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
        DesktopThreadEntry existing = Assert.Single(host.GetView().Threads);
        Config original = host.Config.Clone();
        Config candidate = host.Config.Clone();
        candidate.DownloadPath = Path.Combine(directory.Path, "new-downloads");
        candidate.MaxParallelDownloads = 9;
        candidate.BandwidthLimiter = 3 * 1024 * 1024;
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        File.Delete(settingsPath);
        Directory.CreateDirectory(settingsPath);

        await Assert.ThrowsAnyAsync<Exception>(() => host.ApplySettingsAsync(candidate).WaitAsync(Timeout));

        Assert.Equal(original.DownloadPath, host.Config.DownloadPath);
        Assert.Equal(original.MaxParallelDownloads, host.Config.MaxParallelDownloads);
        Directory.Delete(settingsPath);
        await host.ApplySettingsAsync(candidate).WaitAsync(Timeout);
        Assert.Equal(candidate.DownloadPath, host.Config.DownloadPath);
        Assert.Equal(9, Config.LoadConfig(directory.Path).MaxParallelDownloads);
        Assert.Equal(existing.Snapshot.DownloadDirectory, Assert.Single(host.GetView().Threads).Snapshot.DownloadDirectory);
        await host.AddAsync("https://boards.4chan.org/g/thread/456").WaitAsync(Timeout);
        Assert.Contains(host.GetView().Threads, entry => entry.Snapshot.Id.Thread == "456"
            && entry.Snapshot.DownloadDirectory.StartsWith(candidate.DownloadPath + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObserverFailureDoesNotPreventReadyOrOtherObservers()
    {
        using TempDirectory directory = new TempDirectory();
        DesktopSessionHost host = new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>());
        await using BoundedHostDisposal cleanup = new BoundedHostDisposal(host);
        int observed = 0;
        host.StateChanged += () => throw new InvalidOperationException("Synthetic observer failure");
        host.StateChanged += () => Interlocked.Increment(ref observed);

        await host.StartAsync().WaitAsync(Timeout);

        Assert.Equal(DesktopSessionState.Ready, host.State);
        Assert.True(Volatile.Read(ref observed) > 0);
    }

    [Fact]
    public async Task VerifiedDownloadSurvivesLivePreferencesAndProfileRestoreWithoutAnotherMediaRequest()
    {
        using TempDirectory directory = new TempDirectory();
        string originalRoot = Path.Combine(directory.Path, "original-downloads");
        string futureRoot = Path.Combine(directory.Path, "future-downloads");
        Config initial = Config.LoadConfig(directory.Path);
        initial.DownloadPath = originalRoot;
        initial.SaveConfig();
        byte[] payload = Encoding.UTF8.GetBytes("Synthetic MeowChan host integration media.");
        FixtureTransport transport = new FixtureTransport { MediaPayload = payload };
        using HttpClient client = new HttpClient(transport);
        string savedFile = StorageLayout.GetMediaPath(originalRoot, "4chan", "g", "123", "777", ".png");
        string assignedDirectory;

        {
            DesktopSessionHost first = new DesktopSessionHost(directory.Path, new IImageBoard[] { new FourChanImageBoard(client) });
            await using BoundedHostDisposal cleanup = new BoundedHostDisposal(first);
            await first.StartAsync().WaitAsync(Timeout);
            DesktopAdmissionResult admitted = await first.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
            DesktopThreadEntry downloaded = await WaitForSavedThreadAsync(first);
            assignedDirectory = downloaded.Snapshot.DownloadDirectory;
            Assert.Equal(payload, await File.ReadAllBytesAsync(savedFile));
            Assert.Equal(1, transport.MediaRequests);

            Config candidate = first.Config.Clone();
            candidate.DownloadPath = futureRoot;
            candidate.Theme = "Light";
            candidate.Language = "de";
            candidate.WorkspaceMode = "Advanced";
            candidate.CompactDensity = true;
            candidate.MaxParallelDownloads = 2;
            candidate.RecheckTimer = 120;
            await first.ApplySettingsAsync(candidate).WaitAsync(Timeout);

            DesktopThreadEntry afterPreferences = Assert.Single(first.GetView().Threads);
            Assert.Equal(admitted.WatchId, afterPreferences.WatchId);
            Assert.Equal(assignedDirectory, afterPreferences.Snapshot.DownloadDirectory);
            Assert.Equal(ThreadSourceState.Active, afterPreferences.Snapshot.SourceState);
            Assert.Equal(1, afterPreferences.Snapshot.CompletedMedia);
            await first.StopAsync().WaitAsync(Timeout);
        }

        DesktopSessionHost restored = new DesktopSessionHost(directory.Path, new IImageBoard[] { new FourChanImageBoard(client) });
        await using BoundedHostDisposal restoredCleanup = new BoundedHostDisposal(restored);
        await restored.StartAsync().WaitAsync(Timeout);
        DesktopThreadEntry restoredThread = await WaitForSavedThreadAsync(restored);

        Assert.Equal(assignedDirectory, restoredThread.Snapshot.DownloadDirectory);
        Assert.Equal(ThreadSourceState.Active, restoredThread.Snapshot.SourceState);
        Assert.Equal(payload, await File.ReadAllBytesAsync(savedFile));
        Assert.Equal(1, transport.MediaRequests);
        Assert.Equal(futureRoot, restored.Config.DownloadPath);
        Assert.Equal("Light", restored.Config.Theme);
        Assert.Equal("de", restored.Config.Language);
        Assert.Equal("Advanced", restored.Config.WorkspaceMode);
        Assert.True(restored.Config.CompactDensity);
        Assert.Equal(2, restored.Config.MaxParallelDownloads);
        Assert.Equal(120, restored.Config.RecheckTimer);
    }

    private static async Task<DesktopThreadEntry> WaitForSavedThreadAsync(DesktopSessionHost host)
    {
        TaskCompletionSource<DesktopThreadEntry> saved = new TaskCompletionSource<DesktopThreadEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ReadSnapshot()
        {
            DesktopThreadEntry? thread = host.GetView().Threads.FirstOrDefault(entry => entry.Snapshot.Id.Thread == "123"
                && entry.Snapshot.KnownMedia == 1 && entry.Snapshot.CompletedMedia == 1
                && entry.Snapshot.Downloads.Active == 0 && entry.Snapshot.Downloads.Queued == 0);
            if (thread != null) saved.TrySetResult(thread);
        }
        host.StateChanged += ReadSnapshot;
        try
        {
            ReadSnapshot();
            return await saved.Task.WaitAsync(Timeout);
        }
        finally { host.StateChanged -= ReadSnapshot; }
    }

    private sealed class BoundedHostDisposal(DesktopSessionHost host) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await host.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    private sealed class ControlledStore : ISessionStore
    {
        public Func<Task<SessionLoadResult>> Load { get; init; } = () => Task.FromResult(new SessionLoadResult(new SessionSnapshot(), null, false));
        public int Saves { get; private set; }
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Load();
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureTransport : HttpMessageHandler
    {
        private int _mediaRequests;
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public TaskCompletionSource FirstRequest { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? MediaPayload { get; init; }
        public int MediaRequests => Volatile.Read(ref _mediaRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            FirstRequest.TrySetResult();
            string path = request.RequestUri!.AbsolutePath;
            if (MediaPayload is byte[] payload)
            {
                if (path == "/g/777.png")
                {
                    Interlocked.Increment(ref _mediaRequests);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
                }
                if (path == "/g/thread/123.json")
                {
                    string withMedia = JsonSerializer.Serialize(new
                    {
                        posts = new[] { new { no = 123, sub = "Live fixture", tim = 777, filename = "fixture", ext = ".png",
                            fsize = payload.Length, md5 = Convert.ToBase64String(MD5.HashData(payload)) } }
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(withMedia) });
                }
            }
            string json = path switch
            {
                "/boards.json" => "{\"boards\":[{\"board\":\"g\",\"title\":\"Technology\"}]}",
                "/g/catalog.json" => "[]",
                "/g/thread/123.json" => "{\"posts\":[{\"no\":123,\"sub\":\"First local fixture\"}]}",
                "/g/thread/456.json" => "{\"posts\":[{\"no\":456,\"sub\":\"Second local fixture\"}]}",
                _ => throw new InvalidOperationException("Unexpected fixture request: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
