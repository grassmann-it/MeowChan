using System.Reflection;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

// No transfer/storage status inference: synthetic outcomes exercise the real
// kernel and Registry lane. The transfer adapter must supply its own evidence.
public sealed class ProfileExclusiveOutcomeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [Fact]
    public void DefaultIsUncertainAndDispositionDoesNotInferSafetyFromResult()
    {
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, default(ProfileExclusiveOutcome<string>).Disposition);
        Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, ProfileExclusiveOutcome<string>.RecoveryRequired("StalePlan after own claim").Disposition);
        var receipt = new object();
        var confirmed = ProfileExclusiveOutcome<object>.SafeToResume(receipt);
        Assert.Same(receipt, confirmed.Result);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, confirmed.Disposition);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoverySealIsIrreversibleAndRetainsAcceptedOwner(bool ready)
    {
        using ProfileAdmission admission = new();
        if (ready) admission.CompleteStartup();
        using ProfileAdmissionToken owner = admission.EnterStoreWriter();
        Task<ProfileAdmissionToken>? pending = ready ? admission.EnterExclusiveAsync().AsTask() : null;
        admission.FailRecovery(); admission.FailRecovery();
        Assert.Equal(ProfileAdmissionPhase.RecoveryFailed, admission.Phase);
        if (pending != null)
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Deadline));
            Assert.Equal("Profile recovery is required.", error.Message);
        }
        Assert.Throws<InvalidOperationException>(() => admission.EnterStoreWriter());
        Assert.Throws<InvalidOperationException>(() => admission.EnterStartupWriter());
        Assert.Throws<InvalidOperationException>(() => admission.EnterOrdinaryWriter());
        Assert.Throws<InvalidOperationException>(() => admission.EnterExclusiveAsync());
        Assert.Throws<InvalidOperationException>(admission.CompleteStartup);
        Assert.True(admission.RunWithToken(owner, () => true, owner.Kind));
        admission.BeginShutdown();
        Task drain = admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        owner.Dispose(); await drain.WaitAsync(Deadline); admission.CompleteShutdown();
    }

    [Theory]
    [InlineData("stopping")] [InlineData("stopped")] [InlineData("closed")]
    public void LateRecoveryCannotRewindStopOrClose(string stage)
    {
        using ProfileAdmission admission = new();
        admission.BeginShutdown();
        if (stage == "stopped") admission.CompleteShutdown();
        if (stage == "closed") admission.Close();
        ProfileAdmissionPhase before = admission.Phase;
        admission.FailRecovery(); admission.FailRecovery();
        Assert.Equal(before, admission.Phase);
        Assert.True(admission.DrainAsync().IsCompletedSuccessfully);
        if (stage == "closed") Assert.Throws<ObjectDisposedException>(() => admission.EnterStoreWriter());
        else Assert.Throws<InvalidOperationException>(() => admission.EnterStoreWriter());
    }

    [Fact]
    public async Task RecoveryDoesNotRetireReleaseRequestedWriterBeforeItsPinnedConsumerReturns()
    {
        using ProfileAdmission admission = new(); admission.CompleteStartup();
        using ProfileAdmissionToken owner = admission.EnterOrdinaryWriter();
        using Gate io = new();
        Task work = Task.Factory.StartNew(() => admission.RunWithToken(owner, () =>
        {
            io.Hold(); return true;
        }, ProfileAdmissionKind.Ordinary), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await io.Entered.Task.WaitAsync(Deadline);
            admission.FailRecovery(); owner.Dispose();
            Assert.Throws<InvalidOperationException>(() => admission.RunWithToken(owner, () => true, ProfileAdmissionKind.Ordinary));
            admission.BeginShutdown(); Task drain = admission.DrainAsync();
            Assert.False(drain.IsCompleted); Assert.False(work.IsCompleted);
            Assert.Throws<InvalidOperationException>(admission.CompleteShutdown);
            io.Release(); await work.WaitAsync(Deadline); await drain.WaitAsync(Deadline);
            admission.CompleteShutdown();
        }
        finally { io.Release(); await Observe(work); }
    }

    [Theory]
    [InlineData("default")] [InlineData("unknown")] [InlineData("stale-after-claim")]
    [InlineData("sync-throw")] [InlineData("async-throw")] [InlineData("cancel")]
    public async Task UncertainDelegateSealsBeforeReleaseAndNeverResumes(string mode)
    {
        await using Fixture f = await Fixture.StartAsync();
        using Gate sealedGate = new();
        int resumes = 0, cleanups = 0;
        ProfileAdmissionToken? accepted = null;
        IOException failure = new("synthetic operation failure");
        f.Registry.BeforeProfileProducerResume = () => { resumes++; return Task.CompletedTask; };
        f.Registry.AfterProfileExclusiveRecoverySealedForTests = token =>
        {
            Assert.Same(accepted, token);
            Assert.Equal(1, cleanups);
            Assert.Equal(ProfileAdmissionPhase.RecoveryFailed, f.Lease.Admission.Phase);
            Assert.False(Monitor.IsEntered(Field<object>(f.Registry, "_state")));
            Assert.False(Monitor.IsEntered(Field<object>(f.Lease.Admission, "_sync")));
            Assert.True(f.Lease.Admission.RunWithToken(token, () => true, ProfileAdmissionKind.Exclusive));
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.EnterOrdinaryWriter());
            sealedGate.Hold();
        };
        Task<ProfileExclusiveOutcome<string>> Operation(ProfileAdmissionToken token)
        {
            accepted = token;
            if (mode == "sync-throw")
            {
                try { throw failure; }
                finally { cleanups++; }
            }
            return AsyncOperation();
        }
        async Task<ProfileExclusiveOutcome<string>> AsyncOperation()
        {
            try
            {
                await Task.Yield();
                if (mode == "async-throw") throw failure;
                if (mode == "cancel") throw new OperationCanceledException(new CancellationToken(true));
                return mode == "default" ? default : ProfileExclusiveOutcome<string>.RecoveryRequired(mode);
            }
            finally { cleanups++; }
        }
        Task<ProfileExclusiveOutcome<string>> request = f.Track(Task.Run(() => f.Registry.RunWithProfileExclusiveAsync(Operation)));
        try
        {
            await sealedGate.Entered.Task.WaitAsync(Deadline);
            Assert.False(request.IsCompleted); Assert.Equal(0, resumes);
            sealedGate.Release();
            if (mode is "sync-throw" or "async-throw")
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => request.WaitAsync(Deadline)));
            else if (mode == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
            else
            {
                var result = await request.WaitAsync(Deadline);
                Assert.Equal(ProfileExclusiveDisposition.RecoveryRequired, result.Disposition);
                Assert.Equal(mode == "default" ? null : mode, result.Result);
            }
            Assert.Equal(0, resumes);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(accepted!, () => true, ProfileAdmissionKind.Exclusive));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Registry.RunWithProfileExclusiveAsync(
                _ => Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true))).WaitAsync(Deadline));
            Assert.Empty(f.Registry.Threads); Assert.Empty(f.Registry.Boards);
        }
        finally { sealedGate.Release(); await Observe(request); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OnlyExplicitSafeReturnResumesAndLateCancellationDoesNotUndoIt(bool lateCancel)
    {
        await using Fixture f = await Fixture.StartAsync();
        using CancellationTokenSource cancel = new();
        ProfileAdmissionToken? accepted = null; int resumes = 0;
        f.Registry.BeforeProfileProducerResume = () => { resumes++; return Task.CompletedTask; };
        object receipt = new();
        var result = await f.Track(f.Registry.RunWithProfileExclusiveAsync(token =>
        {
            accepted = token;
            if (lateCancel) cancel.Cancel();
            return Task.FromResult(ProfileExclusiveOutcome<object>.SafeToResume(receipt));
        }, cancel.Token)).WaitAsync(Deadline);
        Assert.Same(receipt, result.Result); Assert.Equal(ProfileExclusiveDisposition.SafeToResume, result.Disposition);
        Assert.Equal(1, resumes); Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
        Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(accepted!, () => true, ProfileAdmissionKind.Exclusive));
        using ProfileAdmissionToken ordinary = f.Lease.Admission.EnterOrdinaryWriter();
        Assert.True(f.Lease.Admission.RunWithToken(ordinary, () => true, ProfileAdmissionKind.Ordinary));
    }

    [Fact]
    public async Task CancellationAfterGrantBeforeDelegateReleasesGrantAndSafelyResumes()
    {
        await using Fixture f = await Fixture.StartAsync();
        using CancellationTokenSource cancel = new();
        ProfileAdmissionToken? granted = null; int calls = 0, resumes = 0;
        f.Registry.BeforeProfileExclusiveOperationForTests = token => { granted = token; cancel.Cancel(); };
        f.Registry.BeforeProfileProducerResume = () => { resumes++; return Task.CompletedTask; };
        Task request = f.Track(f.Registry.RunWithProfileExclusiveAsync(_ =>
        {
            calls++; return Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true));
        }, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
        Assert.NotNull(granted); Assert.Equal(0, calls); Assert.Equal(1, resumes);
        Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
        Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(granted!, () => true, ProfileAdmissionKind.Exclusive));
        using ProfileAdmissionToken ordinary = f.Lease.Admission.EnterOrdinaryWriter();
    }

    [Fact]
    public async Task CancelledPendingReservationStillWaitsForRealCutDrain()
    {
        // The participant is a deterministic synchronous cut + asynchronous drain,
        // not a fake exclusive grant. The Registry/kernel remain the real owners.
        using Gate cut = new();
        TaskCompletionSource drain = Signal();
        CutParticipant participant = new(cut, drain.Task);
        await using Fixture f = await Fixture.StartAsync(participant);
        using CancellationTokenSource cancel = new();
        using ProfileAdmissionToken blocker = f.Lease.Admission.EnterOrdinaryWriter();
        int calls = 0, resumes = 0;
        f.Registry.BeforeProfileProducerResume = () => { resumes++; return Task.CompletedTask; };
        Task request = f.Track(Task.Run(() => f.Registry.RunWithProfileExclusiveAsync(_ =>
        {
            calls++; return Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true));
        }, cancel.Token)));
        try
        {
            await cut.Entered.Task.WaitAsync(Deadline);
            cancel.Cancel(); cut.Release();
            Assert.False(request.IsCompleted); Assert.Equal(0, calls);
            Assert.False(participant.Released);
            Assert.True(f.Lease.Admission.RunWithToken(blocker, () => true, ProfileAdmissionKind.Ordinary));
            drain.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
            Assert.True(participant.Released); Assert.Equal(0, calls); Assert.Equal(1, resumes);
            Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
        }
        finally { cut.Release(); drain.TrySetResult(); await Observe(request); }
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch when (task.IsCompleted) { /* Teardown only; never treat a timeout observer as lifetime completion. */ }
    }
    private sealed class Gate : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        private readonly ManualResetEventSlim release = new();
        internal void Hold() { Entered.TrySetResult(); Assert.True(release.Wait(Deadline), "Test boundary was not released."); }
        internal void Release() => release.Set();
        public void Dispose() => release.Dispose();
    }
    private sealed class CutParticipant(Gate cut, Task drain) : IProfileFinalWriteParticipant
    {
        internal bool Released;
        public void CloseTransitionCommandIngress() { }
        public Task CutAndDrainAsync() { cut.Hold(); return drain; }
        public void ReleaseDrainedEpoch() { Assert.True(drain.IsCompletedSuccessfully); Released = true; }
        public void InstallReadyEpoch() { }
        public void OpenAfterProducerActivation() { }
        public void FailTransition(Exception error) { }
        public void RequireRecovery(Exception error) => Assert.Fail("No delegate ran; recovery classification is unexpected.");
        public void AbortBeforeCut() { }
        public void ReleaseStoppedEpoch() { }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        private readonly List<Task> started = new();
        internal ProfileLease Lease { get; }
        internal Registry Registry { get; }
        private Fixture()
        {
            Lease = ProfileLease.Acquire(directory.Path);
            DownloadQueue queue = new(1, 1024 * 1024); queue.SetPaused(true);
            Registry = new(Array.Empty<IImageBoard>(), queue, directory.Path, TimeSpan.FromMinutes(1), 60,
                persistSession: false, workStore: new MemoryWorkStateStore());
            Registry.PrepareProfileProducers(Lease);
        }
        internal static async Task<Fixture> StartAsync(IProfileFinalWriteParticipant? participant = null)
        {
            Fixture f = new();
            try
            {
                if (participant != null) f.Registry.RegisterProfileFinalWriteParticipant(participant);
                await f.Track(f.RestoreAsync()).WaitAsync(Deadline);
                f.Lease.Admission.CompleteStartup(); f.Registry.InstallReadyProfileEpoch();
                await f.Track(f.Registry.ActivatePreparedProfileProducersAsync()).WaitAsync(Deadline);
                await f.Track(f.Registry.DrainBackgroundTasksAsync()).WaitAsync(Deadline);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private async Task RestoreAsync()
        {
            using ProfileAdmissionToken startup = Lease.Admission.EnterStartupWriter();
            await Registry.RestoreAdmittedAsync(startup);
        }
        internal Task Track(Task task) { started.Add(task); return task; }
        internal Task<T> Track<T>(Task<T> task) { started.Add(task); return task; }
        public async ValueTask DisposeAsync()
        {
            Registry.BeforeProfileExclusiveOperationForTests = null;
            Registry.AfterProfileExclusiveRecoverySealedForTests = null;
            Registry.BeforeProfileProducerResume = null;
            Lease.Admission.BeginShutdown();
            Task stop = Registry.StopAsync();
            await Task.WhenAll(started.Select(Observe).Append(stop));
            await Registry.DisposeAsync();
            await Lease.Admission.DrainAsync();
            Lease.Admission.CompleteShutdown(); Lease.Dispose(); directory.Dispose();
        }
    }
}
