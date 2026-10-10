using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using oChan.Boards.FourChan;
using oChan.Desktop;
using oChan.Interfaces;
using oChan.Persistence;
using oChan.Storage;

namespace oChan.DesktopTests;

public sealed class DesktopSessionHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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
        byte[] payload = Encoding.UTF8.GetBytes("Synthetic oChan host integration media.");
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
        public byte[]? MediaPayload { get; init; }
        public int MediaRequests => Volatile.Read(ref _mediaRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                "/g/thread/123.json" => "{\"posts\":[{\"no\":123,\"sub\":\"First local fixture\",\"archived\":1}]}",
                "/g/thread/456.json" => "{\"posts\":[{\"no\":456,\"sub\":\"Second local fixture\",\"archived\":1}]}",
                _ => throw new InvalidOperationException("Unexpected fixture request: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
