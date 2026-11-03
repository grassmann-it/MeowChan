using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class ProfileProducerDesktopAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ActivationReadFailureAfterCompleteStartupSealsDirectStoresAndStillDisposes()
    {
        using TempDirectory directory = new();
        SaveConfig(directory.Path);
        ProfileLease? lease = null;
        FailingActivationStore? work = null;
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(),
            workStoreFactory: (_, owner) => { lease = owner; return work = new(owner); });
        await host.StartAsync().WaitAsync(Deadline);

        Assert.NotNull(lease);
        Assert.NotNull(work);
        Assert.True(work.FailedInReady);
        Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
        Assert.Equal(ProfileAdmissionPhase.Stopping, lease.Admission.Phase);
        Assert.Contains("Synthetic activation read", host.StartupError);
        Assert.Empty(RegistryOf(host).Threads);
        Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterOrdinaryWriter());
        Assert.Throws<InvalidOperationException>(() => new WorkStateStore(directory.Path, lease));
        SessionStore session = new(directory.Path, lease);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(new()));
        Assert.False(File.Exists(Path.Combine(directory.Path, "saved_urls.json")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "work-state.sqlite")));
        Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(directory.Path); });

        await host.StopAsync().WaitAsync(Deadline);
        Assert.Equal(ProfileAdmissionPhase.Stopped, lease.Admission.Phase);
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.True(host.IsProfileReleased);
        using ProfileLease reacquired = ProfileLease.Acquire(directory.Path);
        Assert.Equal(ProfileAdmissionPhase.Starting, reacquired.Admission.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerRetryRecoversLostFreshEpochRaceUnlessStopWins(bool stopInsteadOfRetry)
    {
        using TempDirectory directory = new();
        SaveConfig(directory.Path);
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State);
        ProfileLease lease = LeaseOf(host);
        Registry registry = RegistryOf(host);
        TaskCompletionSource releaseWindow = NewSignal();
        TaskCompletionSource atWindow = NewSignal();
        ProfileAdmissionToken? competitor = null;
        Task owner = Task.CompletedTask;
        try
        {
            registry.BeforeProfileProducerResume = async () =>
            {
                atWindow.TrySetResult();
                await releaseWindow.Task.WaitAsync(Deadline);
            };
            owner = host.RunWithProfileProducerExclusiveAsync(admission =>
            {
                Assert.True(lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
                return Task.CompletedTask;
            });
            await atWindow.Task.WaitAsync(Deadline);
            competitor = await lease.Admission.EnterExclusiveAsync().AsTask().WaitAsync(Deadline);
            releaseWindow.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.WaitAsync(Deadline));
            Assert.True(host.IsPaused); // Internal suspend never rewrites the user preference.
            Assert.True(Config.LoadConfig(directory.Path).DownloadsPaused);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterOrdinaryWriter());
            Assert.False(registry.Threads.Any());

            registry.BeforeProfileProducerResume = null;
            if (stopInsteadOfRetry)
            {
                Task stop = host.StopAsync();
                Assert.False(stop.IsCompleted); // The competing exact token still owns drain.
                Assert.False(lease.Admission.DrainAsync().IsCompleted);
                competitor.Dispose();
                competitor = null;
                await stop.WaitAsync(Deadline);
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunWithProfileProducerExclusiveAsync(_ => Task.CompletedTask));
                Assert.Equal(ProfileAdmissionPhase.Stopped, lease.Admission.Phase);
            }
            else
            {
                competitor.Dispose();
                competitor = null;
                int calls = 0;
                await host.RunWithProfileProducerExclusiveAsync(admission =>
                {
                    Assert.True(lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
                    calls++;
                    return Task.CompletedTask;
                }).WaitAsync(Deadline);
                Assert.Equal(1, calls);
                Assert.Equal(DesktopSessionState.Ready, host.State);
                Assert.True(host.IsPaused);
                await host.SetPausedAsync(false).WaitAsync(Deadline);
                Assert.False(host.IsPaused);
                await host.SetPausedAsync(true).WaitAsync(Deadline);
                using ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter();
                Assert.Equal(ProfileAdmissionKind.Ordinary, ordinary.Kind);
            }
        }
        finally
        {
            registry.BeforeProfileProducerResume = null;
            releaseWindow.TrySetResult();
            competitor?.Dispose();
            try { await owner.WaitAsync(Deadline); }
            catch (InvalidOperationException) { } // Expected lost fresh-epoch acquisition, already asserted above.
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void SaveConfig(string path) => new Config
    {
        DownloadsPaused = true, SaveUrlsOnExit = true, AutomaticUpdateChecks = false,
        DownloadPath = Path.Combine(path, "media")
    }.SaveConfig(path);
    private static Registry RegistryOf(DesktopSessionHost host) => Private<Registry>(host, "_registry");
    private static ProfileLease LeaseOf(DesktopSessionHost host) => Private<ProfileLease>(host, "_profileLease");
    private static T Private<T>(DesktopSessionHost host, string field) =>
        (T)typeof(DesktopSessionHost).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    // A real activation cache rebuild, not a thrown StartAsync delegate. Restore
    // succeeds in Starting; the first Ready read fails before Host Ready publication.
    private sealed class FailingActivationStore(ProfileLease lease) : IWorkStateStore
    {
        private readonly MemoryWorkStateStore _inner = new();
        internal bool FailedInReady { get; private set; }
        public IReadOnlyList<WorkResource> LoadResources()
        {
            if (lease.Admission.Phase == ProfileAdmissionPhase.Ready)
            {
                FailedInReady = true;
                throw new IOException("Synthetic activation read");
            }
            return _inner.LoadResources();
        }
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => _inner.LoadMedia(url, generation);
        public WorkResource CommitIntent(WorkResource resource) => _inner.CommitIntent(resource);
        public WorkResource? CommitState(WorkResource resource) => _inner.CommitState(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset observedAt,
            string? finalSnapshotJson = null) => _inner.CommitEnd(url, generation, source, observedAt, finalSnapshotJson);
        public bool CommitMedia(MediaWork media) => _inner.CommitMedia(media);
    }
}
