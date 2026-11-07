using System.Collections.Concurrent;
using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>P08c: the desktop host owns and drains its profile lease admission through every lifecycle path.</summary>
public sealed class DesktopProfileAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StartupProfileAdmissionStaysStartingUntilRestoreCompletesOrFails()
    {
        await using HostFixture fixture = new(ControlledStore.Blocked());
        ProfileAdmission admission = fixture.Admission;
        Assert.Equal(ProfileAdmissionPhase.Starting, admission.Phase);
        Assert.Same(admission, PrivateLease(fixture.Host).Admission);

        Task startup = fixture.Host.StartAsync();
        await fixture.Store.Entered.Task.WaitAsync(Deadline);

        Assert.False(startup.IsCompleted);
        // A forced capture would intentionally wait behind Restore's registry gate.
        // The UI reads only the existing passive projection while restore is held.
        Assert.Equal(DesktopSessionState.Starting, fixture.Host.GetView().State);
        Assert.Equal(ProfileAdmissionPhase.Starting, admission.Phase);
        Assert.Throws<InvalidOperationException>(() => admission.EnterOrdinaryWriter());

        fixture.Store.CompleteRestore();
        await startup.WaitAsync(Deadline);

        Assert.Equal(DesktopSessionState.Ready, fixture.Host.State);
        Assert.Equal(ProfileAdmissionPhase.Ready, admission.Phase);
        Assert.Same(startup, fixture.Host.StartAsync());
        using (ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter())
        {
            Assert.Equal(ProfileAdmissionKind.Ordinary, ordinary.Kind);
        }

        await using HostFixture failed = new(new ControlledStore { FailLoad = true });
        Task failedStartup = failed.Host.StartAsync();
        await failedStartup.WaitAsync(Deadline);

        Assert.Equal(DesktopSessionState.RecoveryFailed, failed.Host.State);
        Assert.Equal(ProfileAdmissionPhase.RecoveryFailed, failed.Admission.Phase);
        Assert.Contains(ControlledStore.FailureMessage, failed.Host.StartupError);
        Assert.Equal(failed.Host.StartupError, failed.Host.GetView().StartupError);
        Assert.Throws<InvalidOperationException>(() => failed.Admission.EnterOrdinaryWriter());
    }

    [Fact]
    public async Task StopDuringBlockedRestoreSealsAdmissionWithoutReadyAndRetainsLeaseUntilDisposal()
    {
        await using HostFixture fixture = new(ControlledStore.Blocked());
        ConcurrentBag<DesktopSessionState> observedStates = new();
        void ObserveState() => observedStates.Add(fixture.Host.State);
        fixture.Host.StateChanged += ObserveState;
        Task startup = fixture.Host.StartAsync();
        Task stop = Task.CompletedTask;
        try
        {
            await fixture.Store.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(ProfileAdmissionPhase.Starting, fixture.Admission.Phase);

            stop = fixture.Host.StopAsync();
            SpinUntil(() => fixture.Admission.Phase == ProfileAdmissionPhase.Stopping
                && fixture.Host.State == DesktopSessionState.Stopping, "Stop did not seal profile admission.");

            Assert.False(startup.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.Equal(DesktopSessionState.Stopping, fixture.Host.GetView().State);
            Assert.DoesNotContain(DesktopSessionState.Ready, observedStates);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Host.AddAsync("https://fixture.invalid/g/thread/1").WaitAsync(Deadline));
            fixture.AssertProfileHeld();

            fixture.Store.CompleteRestore();
            await Task.WhenAll(startup, stop).WaitAsync(Deadline);

            Assert.Equal(DesktopSessionState.Stopped, fixture.Host.State);
            Assert.Equal(DesktopSessionState.Stopped, fixture.Host.GetView().State);
            Assert.Equal(ProfileAdmissionPhase.Stopped, fixture.Admission.Phase);
            Assert.DoesNotContain(DesktopSessionState.Ready, observedStates);
            Assert.Same(stop, fixture.Host.StopAsync());
            fixture.AssertProfileHeld();

            await fixture.Host.DisposeAsync().AsTask().WaitAsync(Deadline);
            using ProfileLease reacquired = ProfileLease.Acquire(fixture.Directory.Path);
            Assert.Equal(fixture.Directory.Path, reacquired.ProfileDirectory);
        }
        finally
        {
            fixture.Host.StateChanged -= ObserveState;
            fixture.Store.CompleteRestore();
        }
    }

    [Fact]
    public async Task AcceptedInteractiveCommandHoldsOrdinaryAdmissionThroughShutdownDrain()
    {
        await using HostFixture fixture = await HostFixture.StartedAsync();
        await fixture.HoldCommandsAsync();
        Task command = fixture.Host.RemoveThreadAsync(long.MaxValue);
        SpinUntil(() => fixture.Host.AcceptedCommandCount == 1, "The interactive command was not accepted.");
        Assert.False(command.IsCompleted);

        Task stop = fixture.Host.StopAsync();
        SpinUntil(() => fixture.Admission.Phase == ProfileAdmissionPhase.Stopping
            && fixture.Host.State == DesktopSessionState.Stopping, "Stop did not seal the accepted command's admission.");

        Task admissionDrain = fixture.Admission.DrainAsync();
        Assert.False(admissionDrain.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Host.RemoveThreadAsync(long.MaxValue - 1).WaitAsync(Deadline));
        fixture.AssertProfileHeld();

        Task dispose = fixture.Host.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        fixture.ReleaseCommands();

        await admissionDrain.WaitAsync(Deadline);
        Exception? commandError = await Record.ExceptionAsync(() => command.WaitAsync(Deadline));
        Assert.NotNull(commandError);
        await dispose.WaitAsync(Deadline);

        Assert.Equal(ProfileAdmissionPhase.Stopped, fixture.Admission.Phase);
        Assert.True(fixture.Host.IsProfileReleased);
        using ProfileLease reacquired = ProfileLease.Acquire(fixture.Directory.Path);
        Assert.Equal(ProfileAdmissionPhase.Starting, reacquired.Admission.Phase);
    }

    [Fact]
    public async Task FavoriteSequenceHoldsOrdinaryAdmissionAndLeaseUntilFinalWriteAndClose()
    {
        await using HostFixture fixture = await HostFixture.StartedAsync();
        DesktopSessionHost.FavoriteWriteSequence sequence = fixture.AdmitSequence();
        Assert.Equal(1, fixture.Host.AcceptedCommandCount);
        await fixture.HoldCommandsAsync();

        Task<bool> mark = sequence.SetAsync(true);
        Assert.False(mark.IsCompleted);
        Task stop = fixture.Host.StopAsync();
        SpinUntil(() => fixture.Admission.Phase == ProfileAdmissionPhase.Stopping
            && fixture.Host.State == DesktopSessionState.Stopping, "Stop did not seal favorite admission.");

        Task admissionDrain = fixture.Admission.DrainAsync();
        Assert.False(admissionDrain.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => fixture.Host.AdmitFavoriteSequence(fixture.OtherEntry(1)));
        fixture.AssertProfileHeld();

        fixture.ReleaseCommands();
        Assert.True(await mark.WaitAsync(Deadline));
        Assert.True(fixture.Host.IsFavorite(fixture.Entry));
        Assert.False(admissionDrain.IsCompleted);
        Assert.False(stop.IsCompleted);
        fixture.AssertProfileHeld();

        Assert.True(await sequence.SetAsync(false).WaitAsync(Deadline));
        Assert.False(fixture.Host.IsFavorite(fixture.Entry));
        Assert.False(admissionDrain.IsCompleted);
        Assert.False(stop.IsCompleted);
        fixture.AssertProfileHeld();

        sequence.Dispose();
        await Task.WhenAll(admissionDrain, stop).WaitAsync(Deadline);
        Assert.Equal(0, fixture.Host.AcceptedCommandCount);
        Assert.Equal(DesktopSessionState.Stopped, fixture.Host.State);
        Assert.Equal(ProfileAdmissionPhase.Stopped, fixture.Admission.Phase);
        fixture.AssertProfileHeld();

        await fixture.Host.DisposeAsync().AsTask().WaitAsync(Deadline);
        using ProfileLease reacquired = ProfileLease.Acquire(fixture.Directory.Path);
        Assert.Equal(ProfileAdmissionPhase.Starting, reacquired.Admission.Phase);
    }

    [Fact]
    public async Task StopBeforeStartSealsAdmissionAndDoubleDisposeAllowsIndependentReacquisition()
    {
        await using HostFixture fixture = new();
        ProfileAdmission admission = fixture.Admission;
        Assert.Equal(DesktopSessionState.Starting, fixture.Host.State);
        Assert.Equal(ProfileAdmissionPhase.Starting, admission.Phase);
        Assert.Same(admission, PrivateLease(fixture.Host).Admission);

        Task stop = fixture.Host.StopAsync();
        await stop.WaitAsync(Deadline);

        Assert.Equal(DesktopSessionState.Stopped, fixture.Host.State);
        Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        fixture.AssertProfileHeld();

        Task firstDispose = fixture.Host.DisposeAsync().AsTask();
        Task secondDispose = fixture.Host.DisposeAsync().AsTask();
        Assert.Same(firstDispose, secondDispose);
        await firstDispose.WaitAsync(Deadline);

        Assert.True(fixture.Host.IsProfileReleased);
        Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        using ProfileLease reacquired = ProfileLease.Acquire(fixture.Directory.Path);
        Assert.NotSame(admission, reacquired.Admission);
        Assert.Equal(ProfileAdmissionPhase.Starting, reacquired.Admission.Phase);
    }

        private static void SpinUntil(Func<bool> condition, string message) =>
        Assert.True(SpinWait.SpinUntil(condition, (int)Deadline.TotalMilliseconds), message);

    [Fact]
    public async Task DuplicateControlSharesItsTokenAndPendingExclusiveRejectsNewCommandsWithoutLeaking()
    {
        await using HostFixture fixture = await HostFixture.StartedAsync();
        await fixture.HoldCommandsAsync();
        Task control = fixture.Host.SetPausedAsync(true);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        Task owner = fixture.Host.RunWithProfileProducerExclusiveAsync(async admission =>
        {
            Assert.Equal(ProfileAdmissionKind.Exclusive, admission.Kind);
            Assert.True(fixture.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
            entered.TrySetResult();
            await release.Task.WaitAsync(Deadline);
            // Cancellation after grant cannot revoke this exact accepted token.
            Assert.True(fixture.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
            return ProfileExclusiveOutcome<bool>.SafeToResume(true);
        }, cancellation.Token);
        try
        {
            Assert.False(control.IsCompleted);
            Assert.False(owner.IsCompleted); // The accepted control keeps the producer-owner reservation pending.
            Assert.Same(control, fixture.Host.SetPausedAsync(true));
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => fixture.Host.SetPausedAsync(false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.RemoveThreadAsync(1));
            Assert.Throws<InvalidOperationException>(() => fixture.Host.AdmitFavoriteSequence(fixture.Entry));
            Assert.Equal(0, fixture.Host.AcceptedCommandCount); // Controls have their own lane.

            fixture.ReleaseCommands();
            await control.WaitAsync(Deadline);
            await entered.Task.WaitAsync(Deadline);
            Assert.False(owner.IsCompleted);
            cancellation.Cancel();
            release.TrySetResult();
            await owner.WaitAsync(Deadline);
            Assert.Equal(ProfileAdmissionPhase.Ready, fixture.Admission.Phase);
        }
        finally
        {
            fixture.ReleaseCommands();
            release.TrySetResult();
            await control.WaitAsync(Deadline);
            await owner.WaitAsync(Deadline);
        }
        await fixture.Host.SetPausedAsync(true).WaitAsync(Deadline);
    }

    [Fact]
    public async Task ActiveExclusiveRefusesOrdinaryHostBookkeepingAndLeavesEveryLaneReusable()
    {
        await using HostFixture fixture = await HostFixture.StartedAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task owner = fixture.Host.RunWithProfileProducerExclusiveAsync(async admission =>
        {
            Assert.Equal(ProfileAdmissionKind.Exclusive, admission.Kind);
            Assert.True(fixture.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
            entered.TrySetResult();
            await release.Task.WaitAsync(Deadline);
            return ProfileExclusiveOutcome<bool>.SafeToResume(true);
        });
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.RemoveThreadAsync(1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.SetPausedAsync(true));
            Assert.Throws<InvalidOperationException>(() => fixture.Host.AdmitFavoriteSequence(fixture.Entry));
            Assert.Equal(0, fixture.Host.AcceptedCommandCount);
        }
        finally
        {
            release.TrySetResult();
            await owner.WaitAsync(Deadline);
        }
        await fixture.Host.SetPausedAsync(true).WaitAsync(Deadline);
        DesktopSessionHost.FavoriteWriteSequence sequence = fixture.AdmitSequence();
        Assert.Equal(1, fixture.Host.AcceptedCommandCount);
        Assert.True(await sequence.SetAsync(true).WaitAsync(Deadline));
        sequence.Dispose();
        Assert.Equal(0, fixture.Host.AcceptedCommandCount);
    }

    private static ProfileLease PrivateLease(DesktopSessionHost host) =>
        (ProfileLease)typeof(DesktopSessionHost).GetField("_profileLease", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly SemaphoreSlim _commands;
        private readonly List<DesktopSessionHost.FavoriteWriteSequence> _sequences = new();
        private bool _commandsHeld;

        internal TempDirectory Directory { get; } = new();
        internal ControlledStore Store { get; }
        internal DesktopSessionHost Host { get; }
        internal ProfileLease Lease { get; }
        internal ProfileAdmission Admission => Lease.Admission;
        internal GalleryEntry Entry => OtherEntry(0);

        internal HostFixture(ControlledStore? store = null)
        {
            Store = store ?? new ControlledStore();
            try
            {
                new Config
                {
                    DownloadsPaused = true,
                    // Restore tests must actually enter ISessionStore.LoadAsync.
                    SaveUrlsOnExit = true,
                    AutomaticUpdateChecks = false,
                    DownloadPath = Path.Combine(Directory.Path, "media")
                }.SaveConfig(Directory.Path);
                Host = new DesktopSessionHost(Directory.Path, Array.Empty<IImageBoard>(), (_, _) => Store);
                Lease = PrivateLease(Host);
                _commands = (SemaphoreSlim)typeof(DesktopSessionHost)
                    .GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
            }
            catch
            {
                Directory.Dispose();
                throw;
            }
        }

        internal static async Task<HostFixture> StartedAsync()
        {
            HostFixture fixture = new(new ControlledStore());
            try
            {
                await fixture.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, fixture.Host.State);
                Assert.Equal(ProfileAdmissionPhase.Ready, fixture.Admission.Phase);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal GalleryEntry OtherEntry(int index)
        {
            string directory = Path.Combine(Directory.Path, "absent-originals");
            MediaAssetReference asset = new("asset-" + index, "image-" + index, "original", "image.png",
                Path.Combine(directory, "image-" + index + ".png"), 7, new string('a', 64),
                "https://fixture.invalid/image.png", "image.png", true);
            MediaAssetSnapshot manifest = new(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
            return new(new(new("fixture", "g", "42"), directory, asset.AssetId), manifest, asset, null, null, false, true);
        }

        internal DesktopSessionHost.FavoriteWriteSequence AdmitSequence()
        {
            DesktopSessionHost.FavoriteWriteSequence sequence = Host.AdmitFavoriteSequence(Entry);
            _sequences.Add(sequence);
            return sequence;
        }

        internal async Task HoldCommandsAsync()
        {
            Assert.False(_commandsHeld);
            Assert.True(await _commands.WaitAsync(Deadline));
            _commandsHeld = true;
        }

        internal void ReleaseCommands()
        {
            if (!_commandsHeld) return;
            _commandsHeld = false;
            _commands.Release();
        }

        internal void AssertProfileHeld() => Assert.Throws<IOException>(() =>
        {
            using ProfileLease unexpected = ProfileLease.Acquire(Directory.Path);
        });

        public async ValueTask DisposeAsync()
        {
            Exception? disposalFailure = null;
            Store.CompleteRestore();
            ReleaseCommands();
            foreach (DesktopSessionHost.FavoriteWriteSequence sequence in _sequences) sequence.Dispose();
            try { await Host.DisposeAsync().AsTask().WaitAsync(Deadline); }
            catch (Exception error) { disposalFailure = error; }
            finally
            {
                if (!Host.IsProfileReleased) Lease.Dispose();
                Directory.Dispose();
            }
            if (disposalFailure is not null) throw disposalFailure;
        }
    }

    private sealed class ControlledStore : ISessionStore
    {
        internal const string FailureMessage = "Synthetic profile restore failure";

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<SessionLoadResult>? Restoration { get; private set; }
        internal bool FailLoad { get; init; }
        internal int Saves { get; private set; }

        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (FailLoad) return Task.FromException<SessionLoadResult>(new IOException(FailureMessage));
            return Restoration?.Task ?? Task.FromResult(EmptyResult());
        }

        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }

        internal static ControlledStore Blocked()
        {
            ControlledStore store = new();
            store.BlockRestore();
            return store;
        }

        internal void BlockRestore() => Restoration ??= new TaskCompletionSource<SessionLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CompleteRestore() => Restoration?.TrySetResult(EmptyResult());

        private static SessionLoadResult EmptyResult() => new(new SessionSnapshot(), null, false);
    }
}
