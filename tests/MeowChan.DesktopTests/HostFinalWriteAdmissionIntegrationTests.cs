using System.Reflection;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Synthetic directories/providers only. These tests exercise the real config and
// reading writers; barriers are at admission, actual writer entry and rollback.
public sealed class HostFinalWriteAdmissionIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly LocalReadingPosition Anchor = new("fixture", "b", "123", "124", -12, "125");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static ProfileLease Lease(DesktopSessionHost host) => Field<ProfileLease>(host, "_profileLease");
    private static Registry Registry(DesktopSessionHost host) => Field<Registry>(host, "_registry");
    private static void Seed(string path, bool preview = false, bool paused = true) => new Config
    {
        AutomaticUpdateChecks = false, SaveUrlsOnExit = preview, DownloadsPaused = paused,
        RememberReadingPosition = true, FirstRunSetupPending = true,
        DownloadPath = Path.Combine(path, "media")
    }.SaveConfig(path);
    private static DesktopSessionHost Host(string path) => new(path, Array.Empty<IImageBoard>());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryFailedFlushCommandsRejectBeforeBusyBookkeeping(bool firstRun)
    {
        using TempDirectory directory = new();
        RestoreFailureStore store = new();
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        int binds = 0, writes = 0;
        host.BeforeCommandFinalWriteBindForTests = () => Interlocked.Increment(ref binds);
        host.BeforeFinalWriteIoForTests = _ => Interlocked.Increment(ref writes);
        try
        {
            // Start completion is the real failure/publication barrier, not a delay
            // or a sampled state while startup rollback may still be running.
            Task start = host.StartAsync();
            await start.WaitAsync(Deadline);
            Assert.Same(start, host.StartAsync());
            Assert.Equal(1, store.Loads);
            Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
            Assert.Equal(ProfileAdmissionPhase.RecoveryFailed, Lease(host).Admission.Phase);
            Assert.Contains(RestoreFailureStore.FailureMessage, host.StartupError);
            Assert.Equal(host.StartupError, host.GetView().StartupError);
            Assert.True(Field<bool>(host, "_finalWriteFlushCommandsClosed"));
            long generation = host.ReadingPositionGeneration;
            Config draft = host.Config.Clone();
            draft.RememberReadingPosition = false; // Its onAccepted invalidation must not run.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => firstRun
                    ? host.CompleteFirstRunSetupAsync(new(host.Config.DownloadPath, 1048576, 5)).WaitAsync(Deadline)
                    : host.ApplySettingsAsync(draft).WaitAsync(Deadline));
                Assert.True(Field<bool>(host, "_finalWriteFlushCommandsClosed"));
                Assert.Equal(0, host.AcceptedCommandCount);
                Assert.Null(Field<TaskCompletionSource?>(host, "_commandsDrained"));
                Assert.All(Field<Array>(host, "_acceptedFinalWriteFlushes").Cast<object?>(), slot => Assert.Null(slot));
            }
            Assert.Equal(generation, host.ReadingPositionGeneration);
            Assert.Equal(0, binds);
            Assert.Equal(0, writes);
            Assert.Equal(0, store.Saves);
            Assert.Equal((0, 0, false), host.FinalWriteAdmissionCounts);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.False(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
            Assert.False(File.Exists(Path.Combine(directory.Path, "startup-preview.json")));
        }
        finally
        {
            host.BeforeCommandFinalWriteBindForTests = null;
            host.BeforeFinalWriteIoForTests = null;
            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task RealWriteUseBlocksExclusiveAndParkedBothUnitsReplayOnlyUnderFreshEpoch()
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        var oldEpoch = Field<ProfileProducerEpoch>(host, "_finalWriteEpoch");
        TaskCompletionSource io = Signal(), exclusive = Signal(), resume = Signal();
        using ManualResetEventSlim releaseIo = new();
        int calls = 0;
        host.BeforeFinalWriteIoForTests = unit =>
        {
            if (unit != "view" || Interlocked.Increment(ref calls) != 1) return;
            Assert.True(Lease(host).Admission.RunWithToken(oldEpoch.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            io.TrySetResult(); Assert.True(releaseIo.Wait(Deadline));
        };
        Task owner = Task.CompletedTask;
        try
        {
            Task oldSave = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1234);
            await io.Task.WaitAsync(Deadline);
            owner = host.RunWithProfileProducerExclusiveAsync(async token =>
            {
                Assert.True(Lease(host).Admission.RunWithToken(token, () => true, ProfileAdmissionKind.Exclusive));
                exclusive.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            });
            Assert.Throws<InvalidOperationException>(() => { using var refused = oldEpoch.Enter(); }); // Counted, but already closed.
            Task parkedView = host.SaveViewPreferencesAsync(c => c.VideoVolume = 39);
            Assert.Same(parkedView, host.SaveViewPreferencesAsync(c => c.VideoVolume = 39));
            Task parkedReading = host.RememberReadingPositionAsync(Anchor, host.ReadingPositionGeneration);
            Assert.False(exclusive.Task.IsCompleted);
            Assert.False(oldSave.IsCompleted);
            Assert.False(parkedView.IsCompleted);
            Assert.Equal(2, host.FinalWriteAdmissionCounts.Parked);
            Assert.Throws<InvalidOperationException>(() => { using var refused = Lease(host).Admission.EnterOrdinaryWriter(); });
            releaseIo.Set();
            await exclusive.Task.WaitAsync(Deadline);
            await oldSave.WaitAsync(Deadline);
            Assert.Equal(1234, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.False(parkedView.IsCompleted);
            Assert.False(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
            resume.TrySetResult();
            await owner.WaitAsync(Deadline);
            await Task.WhenAll(parkedView, parkedReading).WaitAsync(Deadline);
            Assert.NotSame(oldEpoch, Field<ProfileProducerEpoch>(host, "_finalWriteEpoch"));
            Assert.Equal(1234, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.Equal(39, Config.LoadConfig(directory.Path).VideoVolume);
            Assert.Equal(Anchor, await host.ReadReadingPositionAsync().WaitAsync(Deadline));
            Assert.Equal((0, 0, 0), host.PreferenceWriteCounts);
            Assert.Equal(0, host.FinalWriteAdmissionCounts.Parked);
        }
        finally { releaseIo.Set(); resume.TrySetResult(); host.BeforeFinalWriteIoForTests = null; await owner.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothFlushCommandsFinishPreCutWatermarkWithoutWaitingForParkedSuccessor(bool firstRun, bool lateAccepted)
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        SemaphoreSlim commands = Field<SemaphoreSlim>(host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline));
        bool held = true;
        TaskCompletionSource registered = Signal(), exclusive = Signal(), resume = Signal();
        using ManualResetEventSlim accept = new();
        void HoldAcceptance() { registered.TrySetResult(); Assert.True(accept.Wait(Deadline)); }
        if (lateAccepted) host.BeforeCommandFinalWriteBindForTests = HoldAcceptance;
        else host.AfterCommandFinalWriteBindForTests = HoldAcceptance;
        Task command = Task.CompletedTask, owner = Task.CompletedTask;
        try
        {
            Task prior = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1250);
            Task priorReading = host.RememberReadingPositionAsync(Anchor, host.ReadingPositionGeneration);
            command = Task.Run(async () =>
            {
                if (firstRun) await host.CompleteFirstRunSetupAsync(new(host.Config.DownloadPath, 1048576, 5));
                else { Config draft = host.Config.Clone(); draft.Theme = "Light"; await host.ApplySettingsAsync(draft); }
            });
            await registered.Task.WaitAsync(Deadline); // Kernel accepted; marker exists, optionally still unbound.
            owner = host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                exclusive.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            });
            Task successor = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1450);
            LocalReadingPosition nextAnchor = Anchor with { Top = -30 };
            Task successorReading = host.RememberReadingPositionAsync(nextAnchor, host.ReadingPositionGeneration);
            Assert.False(successor.IsCompleted); Assert.False(successorReading.IsCompleted);
            Assert.InRange(host.FinalWriteAdmissionCounts.Frozen, 0, 6);
            accept.Set(); commands.Release(); held = false;
            await command.WaitAsync(Deadline);
            await Task.WhenAll(prior, priorReading).WaitAsync(Deadline);
            await exclusive.Task.WaitAsync(Deadline); // Would cycle if either core used the dynamic UI flush.
            Assert.False(successor.IsCompleted);
            Config beforeResume = Config.LoadConfig(directory.Path);
            Assert.Equal(1250, beforeResume.WindowWidth);
            if (firstRun) { Assert.False(beforeResume.FirstRunSetupPending); Assert.Equal(1, beforeResume.FirstRunSetupVersion); }
            else Assert.Equal("Light", beforeResume.Theme);
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            await Task.WhenAll(successor, successorReading).WaitAsync(Deadline);
            Assert.Equal(1450, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.Equal(nextAnchor, await host.ReadReadingPositionAsync().WaitAsync(Deadline));
        }
        finally
        {
            host.BeforeCommandFinalWriteBindForTests = null; host.AfterCommandFinalWriteBindForTests = null;
            accept.Set(); if (held) commands.Release(); resume.TrySetResult();
            await Task.WhenAll(command, owner).WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task ParkedObserversSettleBeforeShellCanCallStopButLiveExclusiveKeepsOwnership()
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        DesktopSessionHost host = Host(directory.Path);
        TaskCompletionSource exclusive = Signal(), release = Signal();
        Task owner = Task.CompletedTask;
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            double initial = host.Config.WindowWidth;
            owner = host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                exclusive.TrySetResult(); await release.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true); // No profile effect; Stop still wins over resume.
            });
            await exclusive.Task.WaitAsync(Deadline);
            Task view = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1600);
            Task reading = host.RememberReadingPositionAsync(Anchor, host.ReadingPositionGeneration);
            Task uiFlush = host.FlushPreferencesAsync();
            Assert.False(view.IsCompleted); Assert.False(reading.IsCompleted);
            Task engine = host.BeginShutdown(); // Real shell order: NO StopAsync to unstick these saves.
            Assert.True(view.IsFaulted); Assert.True(reading.IsFaulted);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => uiFlush.WaitAsync(Deadline));
            Assert.False(engine.IsCompleted);
            Assert.False(Lease(host).Admission.DrainAsync().IsCompleted);
            Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(directory.Path); });
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.FlushPreferencesAsync());
            Assert.Equal(initial, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.False(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
            release.TrySetResult(); await owner.WaitAsync(Deadline); await engine.WaitAsync(Deadline);
            await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync().WaitAsync(Deadline));
            Assert.Equal(ProfileAdmissionPhase.Stopped, Lease(host).Admission.Phase);
            await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask().WaitAsync(Deadline));
            Assert.True(host.IsProfileReleased);
            using ProfileLease next = ProfileLease.Acquire(directory.Path);
        }
        finally
        {
            release.TrySetResult(); await owner.WaitAsync(Deadline);
            try { await host.DisposeAsync().AsTask().WaitAsync(Deadline); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task ShutdownAfterCloseDoesNotReuseStillCountedEpochOrFaultActualOldWrite()
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        DesktopSessionHost host = Host(directory.Path);
        TaskCompletionSource entered = Signal(); using ManualResetEventSlim release = new();
        Task owner = Task.CompletedTask; int callbacks = 0;
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            var old = Field<ProfileProducerEpoch>(host, "_finalWriteEpoch");
            host.BeforeFinalWriteIoForTests = unit =>
            {
                if (unit != "view") return;
                entered.TrySetResult(); Assert.True(release.Wait(Deadline));
            };
            Task write = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1650);
            await entered.Task.WaitAsync(Deadline);
            owner = host.RunWithProfileProducerExclusiveAsync(_ =>
            {
                Interlocked.Increment(ref callbacks);
                return Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true));
            });
            Assert.Throws<InvalidOperationException>(() => { using var refused = old.Enter(); });
            Assert.True(Lease(host).Admission.RunWithToken(old.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            Task parked = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1660);
            Task engine = host.BeginShutdown();
            Assert.True(parked.IsFaulted); Assert.False(write.IsCompleted); Assert.False(engine.IsCompleted);
            Assert.True(host.FinalWriteAdmissionCounts.HasEpoch); // Closed is not yet released.
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveViewPreferencesAsync(c => c.WindowWidth = 1670));
            Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(directory.Path); });
            release.Set();
            await write.WaitAsync(Deadline); // A real accepted write is NOT synthetically faulted by settlement.
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.WaitAsync(Deadline));
            await engine.WaitAsync(Deadline);
            Assert.Equal(0, callbacks);
            Assert.Equal(1650, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.False(File.Exists(Path.Combine(directory.Path, "startup-preview.json")));
            await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync().WaitAsync(Deadline));
            Assert.Equal(ProfileAdmissionPhase.Stopped, Lease(host).Admission.Phase);
        }
        finally
        {
            release.Set(); host.BeforeFinalWriteIoForTests = null;
            try { await owner.WaitAsync(Deadline); } catch (InvalidOperationException) { }
            try { await host.DisposeAsync().AsTask().WaitAsync(Deadline); } catch (AggregateException) { }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentShutdownAndStopSettleBeforeReturnWithoutOuterMonitor(bool firstStop, bool secondStop)
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        DesktopSessionHost host = Host(directory.Path);
        TaskCompletionSource exclusive = Signal(), releaseOwner = Signal(), firstPaused = Signal();
        using ManualResetEventSlim releaseFirst = new();
        Task owner = Task.CompletedTask, firstInvocation = Task.CompletedTask;
        int settlers = 0, monitorChecks = 0;
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            owner = host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                exclusive.TrySetResult(); await releaseOwner.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true); // The held owner ends, without reopening Stop.
            });
            await exclusive.Task.WaitAsync(Deadline);
            Task parked = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1700);
            host.BeforeTerminalFinalWriteSettlementForTests = () =>
            {
                if (Interlocked.Increment(ref settlers) != 1) return;
                firstPaused.TrySetResult(); Assert.True(releaseFirst.Wait(Deadline));
            };
            host.AtTerminalFinalWriteSettlementForTests = () =>
            {
                foreach (string name in new[] { "_lifecycle", "_finalWrites", "_admission" })
                    Assert.False(Monitor.IsEntered(Field<object>(host, name)));
                Interlocked.Increment(ref monitorChecks);
            };
            firstInvocation = Task.Run(() =>
            {
                try { returned.TrySetResult(firstStop ? host.StopAsync() : host.BeginShutdown()); }
                catch (Exception error) { returned.TrySetException(error); throw; }
            });
            await firstPaused.Task.WaitAsync(Deadline);
            Assert.False(parked.IsCompleted);
            Task secondResult = secondStop ? host.StopAsync() : host.BeginShutdown();
            // This is synchronous method-return visibility, NOT waiting for owner/engine/Stop.
            Assert.True(parked.IsFaulted); Assert.False(returned.Task.IsCompleted);
            Assert.True(Volatile.Read(ref monitorChecks) > 0);
            Assert.False(secondResult.IsCompleted);
            releaseFirst.Set(); await firstInvocation.WaitAsync(Deadline);
            Task firstResult = await returned.Task.WaitAsync(Deadline);
            if (firstStop == secondStop) Assert.Same(firstResult, secondResult);
            Assert.Equal(1, Field<int>(host, "_engineStopLaunchClaimed"));
            releaseOwner.TrySetResult(); await owner.WaitAsync(Deadline);
            await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync().WaitAsync(Deadline));
            Assert.Equal(1, Field<int>(host, "_stopLaunchClaimed"));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => parked);
        }
        finally
        {
            releaseFirst.Set(); releaseOwner.TrySetResult();
            host.BeforeTerminalFinalWriteSettlementForTests = null; host.AtTerminalFinalWriteSettlementForTests = null;
            await Task.WhenAll(firstInvocation, owner).WaitAsync(Deadline);
            try { await host.DisposeAsync().AsTask().WaitAsync(Deadline); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task ActivationFailureNotifiesBeforeBlockedRollbackAndDoesNotReleasePreparedOwnership()
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        FailOnceActivationStore work = new();
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), workStoreFactory: (_, _) => work);
        await host.StartAsync().WaitAsync(Deadline);
        Registry registry = Registry(host);
        TaskCompletionSource exclusive = Signal(), resume = Signal(), rollback = Signal(), releaseRollback = Signal();
        Task owner = Task.CompletedTask;
        registry.BeforeProfileProducerFailureDrain = async () =>
        { rollback.TrySetResult(); await releaseRollback.Task.WaitAsync(Deadline); };
        try
        {
            owner = host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                exclusive.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                // Only arm the later activation fault: this synthetic operation itself has no profile effects.
                work.FailNextRead = true;
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            });
            await exclusive.Task.WaitAsync(Deadline);
            Task parked = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1800);
            Task uiFlush = host.FlushPreferencesAsync();
            resume.TrySetResult(); await rollback.Task.WaitAsync(Deadline);
            Assert.True(parked.IsFaulted);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => uiFlush.WaitAsync(Deadline));
            Assert.False(owner.IsCompleted); Assert.Equal(ProfileAdmissionPhase.Ready, Lease(host).Admission.Phase);
            Assert.True(host.FinalWriteAdmissionCounts.HasEpoch);
            var prepared = Field<ProfileProducerEpoch>(host, "_finalWriteEpoch");
            Assert.True(Lease(host).Admission.RunWithToken(prepared.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(directory.Path); });
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => host.ApplySettingsAsync(host.Config.Clone()));
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => host.CompleteFirstRunSetupAsync(new(host.Config.DownloadPath, 1048576, 5)));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.FlushPreferencesAsync());
            Assert.NotEqual(1800, Config.LoadConfig(directory.Path).WindowWidth);
            releaseRollback.TrySetResult();
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => owner.WaitAsync(Deadline));
            Assert.Contains("Synthetic activation read", failure.ToString());
            Assert.False(host.FinalWriteAdmissionCounts.HasEpoch);
            registry.BeforeProfileProducerFailureDrain = null;
            await host.RunWithProfileProducerExclusiveAsync(
                _ => Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true))).WaitAsync(Deadline);
            await host.FlushPreferencesAsync().WaitAsync(Deadline);
            Assert.Equal(1800, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.True(parked.IsFaulted); // A successful later retry never changes the old observer.
        }
        finally
        {
            resume.TrySetResult(); releaseRollback.TrySetResult(); registry.BeforeProfileProducerFailureDrain = null;
            try { await owner.WaitAsync(Deadline); } catch (IOException) { } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task PartialResumeFailureFaultsBothUnitsBeforeRealFreshMonitorRollbackEnds()
    {
        // Restore the seeded watches: preview enables SaveUrlsOnExit, not only the preview cache.
        using TempDirectory directory = new(); Seed(directory.Path, preview: true, paused: false);
        string media = Path.Combine(directory.Path, "media");
        using (ProfileLease seedLease = ProfileLease.Acquire(directory.Path))
        {
            WorkStateStore work = new(directory.Path, seedLease);
            foreach (string url in ResumeFailureProvider.Urls)
                work.CommitIntent(new WorkResource
                {
                    Url = url, OutputRoot = media, Generation = 1, Intent = WorkIntent.Watching,
                    IsThread = true, Source = ThreadSourceStateValue.Active
                });
            await new SessionStore(directory.Path, seedLease).SaveAsync(new SessionSnapshot
            {
                ThreadUrls = ResumeFailureProvider.Urls,
                DownloadDirectories = ResumeFailureProvider.Urls.ToDictionary(url => url, _ => media)
            }).WaitAsync(Deadline);
        }
        ResumeFailureScenario scenario = new();
        using ResumeFailureProvider provider = new(scenario);
        await using DesktopSessionHost host = new(directory.Path, [provider]);
        TaskCompletionSource exclusive = Signal(), resume = Signal(); Task owner = Task.CompletedTask;
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.Equal(2, scenario.Starts);
            owner = host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                exclusive.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            });
            await exclusive.Task.WaitAsync(Deadline);
            Task view = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1810);
            Task reading = host.RememberReadingPositionAsync(Anchor, host.ReadingPositionGeneration);
            Task flush = host.FlushPreferencesAsync();
            resume.TrySetResult();
            // This is the first newly started provider's REAL cancellation/finally
            // path, not BeforeProfileProducerFailureDrain or an artificial token.
            await scenario.FreshCancellation.Task.WaitAsync(Deadline);
            Assert.Equal(4, scenario.Starts);
            Assert.False(scenario.FreshStopped.Task.IsCompleted);
            Assert.False(owner.IsCompleted);
            Assert.True(view.IsFaulted); Assert.True(reading.IsFaulted);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => flush.WaitAsync(Deadline));
            Assert.Equal(ProfileAdmissionPhase.Ready, Lease(host).Admission.Phase);
            Assert.True(host.FinalWriteAdmissionCounts.HasEpoch);
            var fresh = Field<ProfileProducerEpoch>(host, "_finalWriteEpoch");
            Assert.True(Lease(host).Admission.RunWithToken(fresh.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(directory.Path); });
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => host.ApplySettingsAsync(host.Config.Clone()));
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => host.CompleteFirstRunSetupAsync(new(host.Config.DownloadPath, 1048576, 5)));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.FlushPreferencesAsync());
            Assert.NotEqual(1810, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.False(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
            scenario.ReleaseFreshDrain.TrySetResult();
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => owner.WaitAsync(Deadline));
            Assert.Contains("Synthetic fresh second start", error.ToString());
            await scenario.FreshStopped.Task.WaitAsync(Deadline);
            Assert.False(host.FinalWriteAdmissionCounts.HasEpoch);
            Assert.Throws<InvalidOperationException>(() => Lease(host).Admission.RunWithToken(fresh.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            // Only an explicit call through the same lane may replay retained data.
            await host.RunWithProfileProducerExclusiveAsync(
                _ => Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true))).WaitAsync(Deadline);
            await host.FlushPreferencesAsync().WaitAsync(Deadline);
            Assert.Equal(1810, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.Equal(Anchor, await host.ReadReadingPositionAsync().WaitAsync(Deadline));
            Assert.True(view.IsFaulted); Assert.True(reading.IsFaulted);
            Assert.Equal(0, provider.Transport.Requests);
        }
        finally
        {
            resume.TrySetResult(); scenario.ReleaseFreshDrain.TrySetResult();
            try { await owner.WaitAsync(Deadline); } catch (IOException) { } catch (AggregateException) { }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StartupInitialSaveFailureDrainsRealWorkWithoutWaitingOnTransferredTerminalUse(bool directStop, bool terminalBeforeFailure)
    {
        using TempDirectory directory = new(); // Deliberately NO seeded settings.json.
        DesktopSessionHost host = Host(directory.Path);
        host.Config.AutomaticUpdateChecks = false;
        host.Config.SaveUrlsOnExit = false; // The terminal reservation exists even without a preview checkpoint.
        host.Config.RememberReadingPosition = true;
        host.Config.DownloadPath = Path.Combine(directory.Path, "media");
        TaskCompletionSource initialIo = Signal(), readingIo = Signal(), sealedWrites = Signal();
        var cleanupClaim = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseInitial = new(), releaseReading = new();
        Task startup = Task.CompletedTask, engine = Task.CompletedTask, stop = Task.CompletedTask;
        host.BeforeFinalWriteIoForTests = unit =>
        {
            if (unit == "view")
            {
                initialIo.TrySetResult(); Assert.True(releaseInitial.Wait(Deadline));
                throw new IOException("Synthetic initial save failure");
            }
            if (unit == "reading") { readingIo.TrySetResult(); Assert.True(releaseReading.Wait(Deadline)); }
        };
        host.AfterFinalWriteSealForTests = () => sealedWrites.TrySetResult();
        host.AfterStartupFinalWriteCleanupClaimForTests = terminalOwned =>
        {
            Assert.False(Monitor.IsEntered(Field<object>(host, "_lifecycle")));
            Assert.False(Monitor.IsEntered(Field<object>(host, "_finalWrites")));
            cleanupClaim.TrySetResult(terminalOwned);
        };
        try
        {
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.Equal(ProfileOrigin.ConfirmedNew, host.Config.Origin);
            startup = host.StartAsync();
            await initialIo.Task.WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            var epoch = Field<ProfileProducerEpoch>(host, "_finalWriteEpoch");
            Task failedSave = host.FlushPreferencesAsync(); // Observe the actual initial save, before adding reading.
            Task reading = host.RememberReadingPositionAsync(Anchor, host.ReadingPositionGeneration);
            if (terminalBeforeFailure)
            {
                engine = host.BeginShutdown();
                if (directStop)
                {
                    stop = host.StopAsync();
                    await sealedWrites.Task.WaitAsync(Deadline);
                }
            }
            releaseInitial.Set();
            IOException saveError = await Assert.ThrowsAsync<IOException>(() => failedSave.WaitAsync(Deadline));
            Assert.Contains("Synthetic initial save failure", saveError.Message);
            await readingIo.Task.WaitAsync(Deadline);
            Assert.Equal(terminalBeforeFailure, await cleanupClaim.Task.WaitAsync(Deadline));
            if (!terminalBeforeFailure)
            {
                // Cleanup closed the exact generation first. Shutdown must NOT
                // invent a terminal reservation from that still-counted epoch.
                Assert.Throws<InvalidOperationException>(() => { using var refused = epoch.Enter(); });
                engine = host.BeginShutdown();
                Assert.Null(Field<ProfileProducerEpoch.Use?>(host, "_terminalFinalWriteUse"));
            }
            else Assert.NotNull(Field<ProfileProducerEpoch.Use?>(host, "_terminalFinalWriteUse"));
            // The initial save has really faulted, but the distinct real reading
            // writer is still inside its entry barrier. Handoff must not skip it.
            Assert.False(startup.IsCompleted); Assert.False(engine.IsCompleted); Assert.False(reading.IsCompleted);
            if (directStop) Assert.False(stop.IsCompleted);
            Assert.Same(epoch, Field<ProfileProducerEpoch>(host, "_finalWriteEpoch"));
            Assert.True(Lease(host).Admission.RunWithToken(epoch.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.False(Lease(host).Admission.DrainAsync().IsCompleted);
            Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(directory.Path); });
            Assert.False(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
            releaseReading.Set();
            await reading.WaitAsync(Deadline);
            await startup.WaitAsync(Deadline);
            await engine.WaitAsync(Deadline); // Shell order must work BEFORE calling StopAsync.
            Assert.Contains("Synthetic initial save failure", host.StartupError);
            Assert.True(failedSave.IsFaulted);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.Equal(ProfileOrigin.ConfirmedNew, host.Config.Origin);
            Assert.Equal(Anchor, System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllBytes(Path.Combine(directory.Path, "reading-position.json")), ReadingPositionJsonContext.Default.LocalReadingPosition));
            if (terminalBeforeFailure && !directStop)
            {
                Assert.True(host.FinalWriteAdmissionCounts.HasEpoch);
                Assert.True(Lease(host).Admission.RunWithToken(epoch.Admission!, () => true, ProfileAdmissionKind.Ordinary));
                Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(directory.Path); });
            }
            if (!directStop) stop = host.StopAsync();
            AggregateException stopError = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(Deadline));
            Assert.Contains("Synthetic initial save failure", stopError.ToString());
            Assert.Equal(DesktopSessionState.Stopped, host.State);
            Assert.Equal(ProfileAdmissionPhase.Stopped, Lease(host).Admission.Phase);
            Assert.False(host.FinalWriteAdmissionCounts.HasEpoch);
            Assert.Throws<InvalidOperationException>(() => Lease(host).Admission.RunWithToken(epoch.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask().WaitAsync(Deadline));
            Assert.True(host.IsProfileReleased);
            using ProfileLease next = ProfileLease.Acquire(directory.Path);
        }
        finally
        {
            releaseInitial.Set(); releaseReading.Set();
            host.BeforeFinalWriteIoForTests = null;
            host.AfterFinalWriteSealForTests = null;
            host.AfterStartupFinalWriteCleanupClaimForTests = null;
            try { await host.DisposeAsync().AsTask().WaitAsync(Deadline); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task StartupInitialSaveRejectedAfterStopSealStillCompletesWithVisibleError()
    {
        using TempDirectory directory = new(); // No settings file: reach the genuine post-Ready initial save.
        DesktopSessionHost host = Host(directory.Path);
        host.Config.AutomaticUpdateChecks = false;
        host.Config.SaveUrlsOnExit = false;
        host.Config.DownloadPath = Path.Combine(directory.Path, "media");
        TaskCompletionSource decided = Signal(), releaseSave = Signal(), sealedWrites = Signal();
        var cleanupClaim = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int writerEntries = 0;
        host.BeforeStartupInitialSaveForTests = async () =>
        { decided.TrySetResult(); await releaseSave.Task.WaitAsync(Deadline); };
        host.BeforeFinalWriteIoForTests = _ => Interlocked.Increment(ref writerEntries);
        host.AfterFinalWriteSealForTests = () => sealedWrites.TrySetResult();
        host.AfterStartupFinalWriteCleanupClaimForTests = terminalOwned => cleanupClaim.TrySetResult(terminalOwned);
        try
        {
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.Equal(ProfileOrigin.ConfirmedNew, host.Config.Origin);
            Task startup = host.StartAsync();
            await decided.Task.WaitAsync(Deadline); // Ready/Origin condition was already true; Save not invoked yet.
            Assert.Equal(DesktopSessionState.Ready, host.State);
            var epoch = Field<ProfileProducerEpoch>(host, "_finalWriteEpoch");
            Task stop = host.StopAsync();
            Task engine = host.BeginShutdown();
            await sealedWrites.Task.WaitAsync(Deadline); // The REAL StopCore seal, not a private method invocation.
            Assert.False(startup.IsCompleted); Assert.False(engine.IsCompleted); Assert.False(stop.IsCompleted);
            Assert.NotNull(Field<ProfileProducerEpoch.Use?>(host, "_terminalFinalWriteUse"));
            Assert.True(Lease(host).Admission.RunWithToken(epoch.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.Throws<IOException>(() => { using ProfileLease other = ProfileLease.Acquire(directory.Path); });
            releaseSave.TrySetResult();
            Assert.True(await cleanupClaim.Task.WaitAsync(Deadline));
            await startup.WaitAsync(Deadline);
            await engine.WaitAsync(Deadline);
            await stop.WaitAsync(Deadline);
            Assert.Contains("Stopping without authority", host.StartupError);
            Assert.Equal(0, Volatile.Read(ref writerEntries)); // Rejected, never persisted or claimed as saved.
            Assert.False(host.Config.FirstRunSetupPending);
            Assert.Equal(ProfileOrigin.ConfirmedNew, host.Config.Origin);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.False(File.Exists(Path.Combine(directory.Path, "startup-preview.json")));
            Assert.Equal(DesktopSessionState.Stopped, host.State);
            Assert.Equal(ProfileAdmissionPhase.Stopped, Lease(host).Admission.Phase);
            Assert.False(host.FinalWriteAdmissionCounts.HasEpoch);
            Assert.Throws<InvalidOperationException>(() => Lease(host).Admission.RunWithToken(epoch.Admission!, () => true, ProfileAdmissionKind.Ordinary));
            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Assert.True(host.IsProfileReleased);
            using ProfileLease next = ProfileLease.Acquire(directory.Path);
        }
        finally
        {
            releaseSave.TrySetResult();
            host.BeforeStartupInitialSaveForTests = null;
            host.BeforeFinalWriteIoForTests = null;
            host.AfterFinalWriteSealForTests = null;
            host.AfterStartupFinalWriteCleanupClaimForTests = null;
            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task StartupFlushObservesUnwrittenCapturesInsteadOfReportingEmptySuccess()
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        Task view = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1820);
        Task reading = host.RememberReadingPositionAsync(Anchor, host.ReadingPositionGeneration);
        Task flush = host.FlushPreferencesAsync();
        Assert.False(flush.IsCompleted); Assert.False(host.FinalWriteAdmissionCounts.HasEpoch);
        await host.StartAsync().WaitAsync(Deadline);
        await Task.WhenAll(view, reading, flush).WaitAsync(Deadline);
        Assert.Equal(1820, Config.LoadConfig(directory.Path).WindowWidth);
        Assert.Equal(Anchor, await host.ReadReadingPositionAsync().WaitAsync(Deadline));
    }

    [Fact]
    public async Task DispatchFailureAfterFreezeKeepsSlotUntilRealSegmentEnd()
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        SemaphoreSlim commands = Field<SemaphoreSlim>(host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline));
        try
        {
            Task save = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1900);
            host.AfterCommandFinalWriteBindForTests = () => throw new IOException("Synthetic dispatch failure after freeze");
            Assert.Throws<IOException>(() => { _ = host.ApplySettingsAsync(host.Config.Clone()); });
            Assert.Equal(0, host.AcceptedCommandCount);
            Assert.False(save.IsCompleted);
            host.AfterCommandFinalWriteBindForTests = null;
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => host.ApplySettingsAsync(host.Config.Clone()));
        }
        finally { host.AfterCommandFinalWriteBindForTests = null; commands.Release(); }
        await host.FlushPreferencesAsync().WaitAsync(Deadline);
        Assert.Equal(1900, Config.LoadConfig(directory.Path).WindowWidth);
    }

    [Fact]
    public async Task FailedActivePredecessorCannotEraseNewerRevertOrIndependentField()
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        double original = host.Config.WindowWidth;
        TaskCompletionSource entered = Signal(); using ManualResetEventSlim release = new(); int calls = 0;
        host.BeforeFinalWriteIoForTests = unit =>
        {
            if (unit != "view" || Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult(); Assert.True(release.Wait(Deadline)); throw new IOException("Synthetic real-writer failure");
        };
        try
        {
            Task failed = host.SaveViewPreferencesAsync(c => c.WindowWidth = 2000);
            await entered.Task.WaitAsync(Deadline);
            Task later = host.SaveViewPreferencesAsync(c => { c.WindowWidth = original; c.VideoVolume = 41; });
            release.Set();
            await Assert.ThrowsAsync<IOException>(() => failed.WaitAsync(Deadline));
            await later.WaitAsync(Deadline);
            Assert.Equal(original, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.Equal(41, Config.LoadConfig(directory.Path).VideoVolume);
            Assert.Equal((0, 0, 0), host.PreferenceWriteCounts);
        }
        finally { release.Set(); host.BeforeFinalWriteIoForTests = null; }
    }

    [Fact]
    public async Task CutFailureRemainsVisibleToUiFlushAfterItsOldSegmentHasEnded()
    {
        using TempDirectory directory = new(); Seed(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        TaskCompletionSource entered = Signal(), exclusive = Signal(), resume = Signal();
        using ManualResetEventSlim release = new(); int calls = 0; Task owner = Task.CompletedTask;
        host.BeforeFinalWriteIoForTests = unit =>
        {
            if (unit != "view" || Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult(); Assert.True(release.Wait(Deadline)); throw new IOException("Synthetic closed-segment failure");
        };
        try
        {
            Task failed = host.SaveViewPreferencesAsync(c => c.WindowWidth = 2010);
            await entered.Task.WaitAsync(Deadline);
            owner = host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                exclusive.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            });
            release.Set(); await exclusive.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<IOException>(() => failed.WaitAsync(Deadline));
            Assert.Equal((0, 0, 1), host.PreferenceWriteCounts);
            await Assert.ThrowsAsync<IOException>(() => host.FlushPreferencesAsync().WaitAsync(Deadline));
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            await host.FlushPreferencesAsync().WaitAsync(Deadline); // Explicit retry under fresh authority.
            Assert.Equal(2010, Config.LoadConfig(directory.Path).WindowWidth);
            Assert.True(failed.IsFaulted);
        }
        finally
        {
            release.Set(); resume.TrySetResult(); host.BeforeFinalWriteIoForTests = null;
            await owner.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task TerminalOpenKeepsFinalCaptureWindowAndCheckpointUsesExistingOrdinary()
    {
        using TempDirectory directory = new(); Seed(directory.Path, preview: true);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync().WaitAsync(Deadline);
        Task engine = host.BeginShutdown();
        Assert.Throws<InvalidOperationException>(() => { using var refused = Lease(host).Admission.EnterOrdinaryWriter(); });
        await host.SaveViewPreferencesAsync(c => c.WindowWidth = 1550).WaitAsync(Deadline);
        await host.RememberReadingPositionAsync(Anchor, host.ReadingPositionGeneration).WaitAsync(Deadline);
        await host.FlushPreferencesAsync().WaitAsync(Deadline);
        await engine.WaitAsync(Deadline); // No Stop worker needed for final ordinary saves.
        Assert.Equal(1550, Config.LoadConfig(directory.Path).WindowWidth);
        await host.StopAsync().WaitAsync(Deadline);
        Assert.Equal(ProfileAdmissionPhase.Stopped, Lease(host).Admission.Phase);
        Assert.True(File.Exists(Path.Combine(directory.Path, "startup-preview.json")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
        Assert.False(host.FinalWriteAdmissionCounts.HasEpoch);
    }

    private sealed class ResumeFailureScenario
    {
        private int _starts;
        internal int Starts => Volatile.Read(ref _starts);
        internal TaskCompletionSource FreshEntered { get; } = Signal();
        internal TaskCompletionSource FreshCancellation { get; } = Signal();
        internal TaskCompletionSource ReleaseFreshDrain { get; } = Signal();
        internal TaskCompletionSource FreshStopped { get; } = Signal();
        internal void BeforeStart()
        {
            if (Interlocked.Increment(ref _starts) != 4) return;
            FreshEntered.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
            throw new IOException("Synthetic fresh second start");
        }
        internal async Task HoldFreshMonitorAsync(CancellationToken token)
        {
            FreshEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                FreshCancellation.TrySetResult();
                await ReleaseFreshDrain.Task.WaitAsync(Deadline);
            }
        }
    }
    private sealed class ResumeFailureThread(IBoard board, Uri uri, ResumeFailureScenario scenario) : BaseThread, IThread
    {
        private int _runs;
        public override IBoard Board => board;
        public override string ThreadId => uri.Segments[^1];
        public override string NiceName => ThreadId;
        public override Uri ThreadUri => uri;
        public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default) =>
            ThreadId == "101" && Volatile.Read(ref _runs) == 2
                ? scenario.HoldFreshMonitorAsync(cancellationToken) : Task.CompletedTask;
        void IThread.StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval)
        {
            scenario.BeforeStart();
            Interlocked.Increment(ref _runs);
            base.StartMonitoring(queue, outputDirectory, interval);
        }
        async Task IThread.StopMonitoringAsync()
        {
            await base.StopMonitoringAsync();
            if (ThreadId == "101" && Volatile.Read(ref _runs) == 2) scenario.FreshStopped.TrySetResult();
        }
    }
    private sealed class ResumeFailureBoard(IImageBoard provider) : BaseBoard
    {
        public override IImageBoard ImageBoard => provider;
        public override string BoardCode => "g";
        public override string Name { get; protected set; } = "Offline final-write fixture";
        public override string NiceName => Name;
        public override Uri BoardUri => new(provider.BaseUri, "g/");
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<IThread>>([]);
    }
    private sealed class ResumeFailureProvider : IImageBoard, IDisposable
    {
        internal static readonly string[] Urls = ["https://final-write.test/g/thread/101", "https://final-write.test/g/thread/102"];
        internal RejectNetworkTransport Transport { get; } = new();
        private readonly HttpClient _client;
        private readonly ResumeFailureBoard _board;
        private readonly Dictionary<string, IThread> _threads;
        internal ResumeFailureProvider(ResumeFailureScenario scenario)
        {
            _client = new(Transport); _board = new(this);
            _threads = Urls.ToDictionary(url => url, url => (IThread)new ResumeFailureThread(_board, new Uri(url), scenario));
        }
        public string Name => "final-write";
        public string NiceName => "Offline final-write fixture";
        public Uri BaseUri => new("https://final-write.test/");
        public bool CanHandle(Uri uri) => uri.Scheme == "https" && uri.Host == BaseUri.Host;
        public bool IsThreadUri(Uri uri) => _threads.ContainsKey(uri.AbsoluteUri);
        public bool IsBoardUri(Uri uri) => uri == _board.BoardUri;
        public IBoard GetBoard(Uri uri) => _board;
        public IThread GetThread(Uri uri) => _threads[uri.AbsoluteUri];
        public HttpClient GetHttpClient() => _client;
        public void Dispose() => _client.Dispose();
    }
    private sealed class RejectNetworkTransport : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Network forbidden in final-write fixture.");
        }
    }

    private sealed class RestoreFailureStore : ISessionStore
    {
        internal const string FailureMessage = "Synthetic final-write admission restore failure";
        private int _loads, _saves;
        internal int Loads => Volatile.Read(ref _loads);
        internal int Saves => Volatile.Read(ref _saves);
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loads);
            return Task.FromException<SessionLoadResult>(new IOException(FailureMessage));
        }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnceActivationStore : IWorkStateStore
    {
        private readonly MemoryWorkStateStore _inner = new();
        private int _failNextRead;
        internal bool FailNextRead { set => Volatile.Write(ref _failNextRead, value ? 1 : 0); }
        public IReadOnlyList<WorkResource> LoadResources()
        {
            if (Interlocked.Exchange(ref _failNextRead, 0) != 0) throw new IOException("Synthetic activation read");
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
