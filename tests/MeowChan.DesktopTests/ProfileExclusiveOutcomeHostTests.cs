using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Real empty-provider Host, Registry and private synthetic profile. These tests
// classify explicit synthetic outcomes, not SQL receipts or transfer recovery.
public sealed class ProfileExclusiveOutcomeHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly LocalReadingPosition Anchor = new("fixture", "b", "123", "124", -12, "125");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [Theory]
    [InlineData("default")] [InlineData("unknown")] [InlineData("stale-after-claim")]
    [InlineData("throw")] [InlineData("cancel")]
    public async Task UncertainReturnSealsHostAndKernelBeforeReleaseAndSettlesParkedObservers(string mode)
    {
        await using Fixture f = await Fixture.StartAsync();
        TaskCompletionSource entered = Signal(), finish = Signal();
        using Gate sealedGate = new();
        ProfileAdmissionToken? accepted = null;
        ProfileProducerEpoch old = Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch");
        int resumes = 0, writes = 0, cleanup = 0;
        IOException failure = new("synthetic lost operation return");
        double originalWidth = f.Host.Config.WindowWidth;
        f.Registry.BeforeProfileProducerResume = () => { resumes++; return Task.CompletedTask; };
        f.Host.BeforeFinalWriteIoForTests = _ => Interlocked.Increment(ref writes);
        f.Registry.AfterProfileExclusiveRecoverySealedForTests = token =>
        {
            Assert.Same(accepted, token); Assert.Equal(1, cleanup);
            Assert.Equal(ProfileAdmissionPhase.RecoveryFailed, f.Lease.Admission.Phase);
            Assert.Equal(DesktopSessionState.RecoveryFailed, f.Host.State);
            Assert.Equal(DesktopSessionState.RecoveryFailed, f.Host.GetView().State);
            Assert.Equal(f.Host.StartupError, f.Host.GetView().StartupError);
            Assert.False(string.IsNullOrEmpty(f.Host.StartupError));
            foreach (string monitor in new[] { "_lifecycle", "_finalWrites", "_admission" })
                Assert.False(Monitor.IsEntered(Field<object>(f.Host, monitor)));
            Assert.False(Monitor.IsEntered(Field<object>(f.Registry, "_state")));
            Assert.False(Monitor.IsEntered(Field<object>(f.Lease.Admission, "_sync")));
            Assert.True(f.Lease.Admission.RunWithToken(token, () => true, ProfileAdmissionKind.Exclusive));
            sealedGate.Hold();
        };
        Task<ProfileExclusiveOutcome<string>> owner = f.Track(f.Host.RunWithProfileProducerExclusiveAsync<string>(async token =>
        {
            accepted = token; entered.TrySetResult();
            try
            {
                await finish.Task.WaitAsync(Deadline);
                if (mode == "throw") throw failure;
                if (mode == "cancel") throw new OperationCanceledException(new CancellationToken(true));
                return mode == "default" ? default : ProfileExclusiveOutcome<string>.RecoveryRequired(mode);
            }
            finally { cleanup++; }
        }));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Task view = f.Track(f.Host.SaveViewPreferencesAsync(c => c.WindowWidth = 1870));
            Task reading = f.Track(f.Host.RememberReadingPositionAsync(Anchor, f.Host.ReadingPositionGeneration));
            Task flush = f.Track(f.Host.FlushPreferencesAsync());
            f.RejectedDraftsOnStop = true;
            Assert.False(view.IsCompleted); Assert.False(reading.IsCompleted);
            finish.TrySetResult(); await sealedGate.Entered.Task.WaitAsync(Deadline);
            Assert.False(owner.IsCompleted); Assert.Equal(0, resumes);
            Assert.True(view.IsFaulted); Assert.True(reading.IsFaulted);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => flush.WaitAsync(Deadline));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => view);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => reading);
            Assert.Equal(2, f.Host.FinalWriteAdmissionCounts.Parked); // retained, not fictitiously persisted
            Assert.False(f.Host.FinalWriteAdmissionCounts.HasEpoch);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(old.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.EnterOrdinaryWriter());
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.ApplySettingsAsync(f.Host.Config.Clone()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.CompleteFirstRunSetupAsync(new(f.Host.Config.DownloadPath, 1048576, 5)));
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = f.Host.RunWithProfileProducerExclusiveAsync(
                    _ => Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true)));
            });
            SessionStore passive = new(f.Directory.Path, f.Lease);
            await passive.LoadAsync().WaitAsync(Deadline); // passive diagnostics remain legal
            await Assert.ThrowsAsync<InvalidOperationException>(() => passive.SaveAsync(new()));
            Assert.Equal(originalWidth, Config.LoadConfig(f.Directory.Path).WindowWidth);
            Assert.False(File.Exists(Path.Combine(f.Directory.Path, "reading-position.json")));
            Assert.Equal(0, writes);
            Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(f.Directory.Path); });

            // Stop must not create a terminal writer after the recovery fence and
            // cannot finish while this actual exclusive owner's finally is held.
            Task stop = f.Track(f.Host.StopAsync());
            Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Stopping, f.Lease.Admission.Phase);
            Assert.Null(Field<ProfileProducerEpoch.Use?>(f.Host, "_terminalFinalWriteUse"));
            Assert.False(stop.IsCompleted); Assert.False(f.Host.IsProfileReleased);
            Assert.False(f.Lease.Admission.DrainAsync().IsCompleted);
            sealedGate.Release();
            if (mode == "throw") Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => owner.WaitAsync(Deadline)));
            else if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.WaitAsync(Deadline));
            else
            {
                var result = await owner.WaitAsync(Deadline);
                Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, result.Disposition);
                Assert.Equal(mode == "default" ? null : mode, result.Result);
            }
            await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(Deadline));
            Assert.Equal(DesktopSessionState.Stopped, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(accepted!, () => true, ProfileAdmissionKind.Exclusive));
            Assert.Equal(0, resumes); Assert.Equal(0, writes);
            Assert.False(File.Exists(Path.Combine(f.Directory.Path, "startup-preview.json")));
        }
        finally { finish.TrySetResult(); sealedGate.Release(); await Observe(owner); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConfirmedSafeResultSurvivesLateCancellationAndReplaysOnlyUnderFreshEpoch(bool cancelAfterEffect)
    {
        await using Fixture f = await Fixture.StartAsync();
        using CancellationTokenSource cancel = new();
        TaskCompletionSource entered = Signal(), finish = Signal();
        ProfileProducerEpoch old = Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch");
        ProfileAdmissionToken? accepted = null;
        object receipt = new(); int resumes = 0;
        f.Registry.BeforeProfileProducerResume = () => { resumes++; return Task.CompletedTask; };
        Task<ProfileExclusiveOutcome<object>> owner = f.Track(f.Host.RunWithProfileProducerExclusiveAsync(async token =>
        {
            accepted = token; entered.TrySetResult(); await finish.Task.WaitAsync(Deadline);
            if (cancelAfterEffect) cancel.Cancel();
            return ProfileExclusiveOutcome<object>.SafeToResume(receipt);
        }, cancel.Token));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Task view = f.Track(f.Host.SaveViewPreferencesAsync(c => c.WindowWidth = 1880));
            Assert.False(view.IsCompleted);
            finish.TrySetResult();
            var result = await owner.WaitAsync(Deadline); await view.WaitAsync(Deadline);
            Assert.Same(receipt, result.Result); Assert.Equal(ProfileExclusiveDisposition.SafeToResume, result.Disposition);
            Assert.Equal(1, resumes); Assert.Equal(DesktopSessionState.Ready, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
            Assert.NotSame(old, Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch"));
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(accepted!, () => true, ProfileAdmissionKind.Exclusive));
            Assert.Equal(1880, Config.LoadConfig(f.Directory.Path).WindowWidth);
            Assert.Equal(0, f.Host.FinalWriteAdmissionCounts.Parked);
            await f.Track(f.Host.StopAsync()).WaitAsync(Deadline);
        }
        finally { finish.TrySetResult(); await Observe(owner); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StopDuringDelegateWinsOverSafeAndRecoveryOutcomes(bool safe)
    {
        await using Fixture f = await Fixture.StartAsync();
        TaskCompletionSource entered = Signal(), finish = Signal();
        int resumes = 0, seals = 0;
        f.Registry.BeforeProfileProducerResume = () => { resumes++; return Task.CompletedTask; };
        f.Registry.AfterProfileExclusiveRecoverySealedForTests = token =>
        {
            seals++;
            Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Stopping, f.Lease.Admission.Phase);
            Assert.True(f.Lease.Admission.RunWithToken(token, () => true, ProfileAdmissionKind.Exclusive));
        };
        Task<ProfileExclusiveOutcome<int>> owner = f.Track(f.Host.RunWithProfileProducerExclusiveAsync(async _ =>
        {
            entered.TrySetResult(); await finish.Task.WaitAsync(Deadline);
            return safe ? ProfileExclusiveOutcome<int>.SafeToResume(7) : ProfileExclusiveOutcome<int>.RecoveryRequired(7);
        }));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Task stop = f.Track(f.Host.StopAsync());
            Assert.False(stop.IsCompleted); Assert.False(owner.IsCompleted);
            Assert.Null(Field<ProfileProducerEpoch.Use?>(f.Host, "_terminalFinalWriteUse"));
            finish.TrySetResult(); Assert.Equal(7, (await owner.WaitAsync(Deadline)).Result);
            await stop.WaitAsync(Deadline);
            Assert.Equal(0, resumes); Assert.Equal(safe ? 0 : 1, seals);
            Assert.Equal(DesktopSessionState.Stopped, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
        }
        finally { finish.TrySetResult(); await Observe(owner); }
    }

    [Fact]
    public async Task StopAfterGrantBeforeDelegatePreventsEffectsAndDoesNotResume()
    {
        await using Fixture f = await Fixture.StartAsync();
        using Gate granted = new(); int calls = 0, resumes = 0;
        ProfileAdmissionToken? exact = null;
        f.Registry.BeforeProfileExclusiveOperationForTests = token => { exact = token; granted.Hold(); };
        f.Registry.BeforeProfileProducerResume = () => { resumes++; return Task.CompletedTask; };
        Task owner = f.Track(Task.Run(() => f.Host.RunWithProfileProducerExclusiveAsync(_ =>
        {
            calls++; return Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true));
        })));
        try
        {
            await granted.Entered.Task.WaitAsync(Deadline);
            Task stop = f.Track(f.Host.StopAsync());
            Assert.False(stop.IsCompleted);
            granted.Release();
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.WaitAsync(Deadline));
            await stop.WaitAsync(Deadline);
            Assert.Equal(0, calls); Assert.Equal(0, resumes);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(exact!, () => true, ProfileAdmissionKind.Exclusive));
            Assert.Equal(DesktopSessionState.Stopped, f.Host.State);
        }
        finally { granted.Release(); await Observe(owner); }
    }

    [Fact]
    public async Task StopAtResumeBarrierCannotInstallOrOpenAFreshFamily()
    {
        await using Fixture f = await Fixture.StartAsync();
        TaskCompletionSource resume = Signal(), releaseResume = Signal();
        f.Registry.BeforeProfileProducerResume = async () =>
        { resume.TrySetResult(); await releaseResume.Task.WaitAsync(Deadline); };
        Task owner = f.Track(f.Host.RunWithProfileProducerExclusiveAsync(
            _ => Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true))));
        try
        {
            await resume.Task.WaitAsync(Deadline);
            Assert.False(f.Host.FinalWriteAdmissionCounts.HasEpoch);
            Task stop = f.Track(f.Host.StopAsync());
            Assert.False(stop.IsCompleted); Assert.False(owner.IsCompleted);
            releaseResume.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.WaitAsync(Deadline));
            await stop.WaitAsync(Deadline);
            Assert.False(f.Host.FinalWriteAdmissionCounts.HasEpoch);
            Assert.Equal(DesktopSessionState.Stopped, f.Host.State);
            Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
        }
        finally { releaseResume.TrySetResult(); await Observe(owner); }
    }

    [Fact]
    public async Task ExplicitSafeOutcomeStillPreservesB2NotificationAndPreparedOwnershipUntilRollbackEnds()
    {
        FailOnceActivationStore work = new();
        await using Fixture f = await Fixture.StartAsync(work);
        TaskCompletionSource entered = Signal(), finish = Signal(), rollback = Signal(), releaseRollback = Signal();
        f.Registry.BeforeProfileProducerFailureDrain = async () =>
        { rollback.TrySetResult(); await releaseRollback.Task.WaitAsync(Deadline); };
        Task owner = f.Track(f.Host.RunWithProfileProducerExclusiveAsync(async _ =>
        {
            entered.TrySetResult(); await finish.Task.WaitAsync(Deadline); work.FailNextRead = true;
            return ProfileExclusiveOutcome<bool>.SafeToResume(true);
        }));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Task parked = f.Track(f.Host.SaveViewPreferencesAsync(c => c.WindowWidth = 1890));
            Task flush = f.Track(f.Host.FlushPreferencesAsync());
            finish.TrySetResult(); await rollback.Task.WaitAsync(Deadline);
            Assert.True(parked.IsFaulted); Assert.False(owner.IsCompleted);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => flush.WaitAsync(Deadline));
            Assert.True(f.Host.FinalWriteAdmissionCounts.HasEpoch);
            ProfileProducerEpoch prepared = Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch");
            Assert.True(f.Lease.Admission.RunWithToken(prepared.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
            Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(f.Directory.Path); });
            Assert.NotEqual(1890, Config.LoadConfig(f.Directory.Path).WindowWidth);
            releaseRollback.TrySetResult();
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => owner.WaitAsync(Deadline));
            Assert.Contains("Synthetic resume activation read", failure.ToString());
            Assert.False(f.Host.FinalWriteAdmissionCounts.HasEpoch);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(prepared.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            f.Registry.BeforeProfileProducerFailureDrain = null;
            // This is a new explicitly safe non-transfer attempt, not automatic
            // Ready-only retry after an uncertain transfer result.
            await f.Track(f.Host.RunWithProfileProducerExclusiveAsync(
                _ => Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true)))).WaitAsync(Deadline);
            await f.Track(f.Host.FlushPreferencesAsync()).WaitAsync(Deadline);
            Assert.Equal(1890, Config.LoadConfig(f.Directory.Path).WindowWidth);
            Assert.True(parked.IsFaulted);
            await f.Track(f.Host.StopAsync()).WaitAsync(Deadline);
        }
        finally { finish.TrySetResult(); releaseRollback.TrySetResult(); await Observe(owner); }
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch when (task.IsCompleted) { /* Teardown only: await the actual task, not a deadline surrogate. */ }
    }
    private sealed class Gate : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        private readonly ManualResetEventSlim release = new();
        internal void Hold() { Entered.TrySetResult(); Assert.True(release.Wait(Deadline), "Boundary was not released."); }
        internal void Release() => release.Set();
        public void Dispose() => release.Dispose();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Directory { get; } = new();
        internal DesktopSessionHost Host { get; }
        internal Registry Registry => Field<Registry>(Host, "_registry");
        internal ProfileLease Lease => Field<ProfileLease>(Host, "_profileLease");
        internal bool RejectedDraftsOnStop;
        private readonly List<Task> started = new();
        private Fixture(IWorkStateStore? work)
        {
            new Config
            {
                AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                RememberReadingPosition = true, FirstRunSetupPending = true,
                DownloadPath = Path.Combine(Directory.Path, "absent-media")
            }.SaveConfig(Directory.Path);
            Host = new(Directory.Path, Array.Empty<IImageBoard>(), workStoreFactory: work == null ? null : (_, _) => work);
        }
        internal static async Task<Fixture> StartAsync(IWorkStateStore? work = null)
        {
            Fixture f = new(work);
            try
            {
                await f.Track(f.Host.StartAsync()).WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task Track(Task task) { started.Add(task); return task; }
        internal Task<T> Track<T>(Task<T> task) { started.Add(task); return task; }
        public async ValueTask DisposeAsync()
        {
            Registry.BeforeProfileExclusiveOperationForTests = null;
            Registry.AfterProfileExclusiveRecoverySealedForTests = null;
            Registry.BeforeProfileProducerResume = null;
            Registry.BeforeProfileProducerFailureDrain = null;
            Host.BeforeFinalWriteIoForTests = null;
            Task stop = Host.StopAsync();
            await Task.WhenAll(started.Select(Observe).Append(Observe(stop)));
            try { await Host.DisposeAsync(); }
            catch (AggregateException) when (RejectedDraftsOnStop && Host.IsProfileReleased) { }
            Assert.True(Host.IsProfileReleased); Directory.Dispose();
        }
    }
    private sealed class FailOnceActivationStore : IWorkStateStore
    {
        private readonly MemoryWorkStateStore inner = new();
        private int failNextRead;
        internal bool FailNextRead { set => Volatile.Write(ref failNextRead, value ? 1 : 0); }
        public IReadOnlyList<WorkResource> LoadResources()
        {
            if (Interlocked.Exchange(ref failNextRead, 0) != 0) throw new IOException("Synthetic resume activation read");
            return inner.LoadResources();
        }
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => inner.LoadMedia(url, generation);
        public WorkResource CommitIntent(WorkResource resource) => inner.CommitIntent(resource);
        public WorkResource? CommitState(WorkResource resource) => inner.CommitState(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset observedAt,
            string? finalSnapshotJson = null) => inner.CommitEnd(url, generation, source, observedAt, finalSnapshotJson);
        public bool CommitMedia(MediaWork media) => inner.CommitMedia(media);
    }
}
