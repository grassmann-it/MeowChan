using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Activation;

namespace MeowChan.DesktopTests;

// Real isolated Host/Config/Preview stores. Barriers observe existing accepted
// work inside its store pin; they neither replace I/O nor manufacture success.
[Collection("Delivered plugin hosts")]
public sealed class DirectSettingsHostAdmissionIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly LocalReadingPosition Anchor = new("fixture", "b", "123", "124", -12, "125");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static ProfileLease Lease(DesktopSessionHost host) => Field<ProfileLease>(host, "_profileLease");
    private static ProfileProducerEpoch Epoch(DesktopSessionHost host) => Field<ProfileProducerEpoch>(host, "_finalWriteEpoch");
    private static string Settings(string path) => Path.Combine(path, "settings.json");
    private static string Cache(string path) => Path.Combine(path, "startup-preview.json");

    private static void Seed(string path, bool preview = false)
    {
        new Config
        {
            Theme = "Dark", AutomaticUpdateChecks = false, DownloadsPaused = true,
            SaveUrlsOnExit = preview, RememberReadingPosition = true,
            FirstRunSetupPending = true, MinimumFreeSpaceGiB = 0,
            DownloadPath = Path.Combine(path, "media")
        }.SaveConfig(path);
        if (!preview) return;
        using ProfileLease lease = ProfileLease.Acquire(path);
        new StartupPreviewStore(path, lease).Save(new(
            new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), 1, 0,
            Array.AsReadOnly(new[] { new StartupPreviewRow(false, "Cached only", "b", "Not a watch or permission", 9, 3) })));
    }

    private static DesktopSessionHost Host(string path, DesktopPluginFixtures? network = null) => network == null
        ? new(path, Array.Empty<IImageBoard>())
        : new(path,
            updateSessionFactory: (profile, config) => new UpdateSession(profile, config, network.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: network.Client);

    // Keep the Host's live and confirmed snapshots paired for subsequent clones.
    // Both observers are instance-local; clones only carry the approved observers.
    private static void ObserveConfig(DesktopSessionHost host, Action<ProfileAdmissionToken>? before,
        Action<ProfileAdmissionToken>? after)
    {
        host.Config.BeforeAdmittedSaveMutationForTests = before;
        host.Config.AfterAdmittedSaveMutationForTests = after;
        if (Field<Config?>(host, "_confirmedConfig") is { } confirmed)
        {
            confirmed.BeforeAdmittedSaveMutationForTests = before;
            confirmed.AfterAdmittedSaveMutationForTests = after;
        }
    }

    private static void AssertNoOwnedTemps(string path)
    {
        Assert.Empty(Directory.EnumerateFiles(path, ".settings.json.*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(path, ".startup-preview-*.tmp"));
    }

    // Narrow file-family evidence, not a hash/quiescence assertion about SQLite/WAL
    // or all other profile writers. Includes backups, missing files and owned temps.
    private static string[] SettingsFamily(string path) => Directory.EnumerateFiles(path)
        .Where(file => Path.GetFileName(file).Contains("settings.json", StringComparison.Ordinal)
            || Path.GetFileName(file).Contains("reading-position", StringComparison.Ordinal))
        .OrderBy(file => file, StringComparer.Ordinal)
        .Select(file => Path.GetFileName(file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
        .ToArray();

    private static void AssertSetting(Config config, string edge, bool committed)
    {
        switch (edge)
        {
            case "C1": Assert.Equal(committed ? "Light" : "Dark", config.Theme); break;
            case "C2":
                Assert.Equal(!committed, config.FirstRunSetupPending);
                if (committed)
                {
                    Assert.Equal(1, config.FirstRunSetupVersion);
                    Assert.Equal(1048576, config.BandwidthLimiter);
                    Assert.Equal(5, config.MaxParallelDownloads);
                }
                break;
            case "C3": Assert.Equal(!committed, config.DownloadsPaused); break;
            case "S1":
            case "S2":
                bool granted = config.SourceMediaIntegrityPermissions.TryGetValue(
                    SourceMediaIntegrityPermission.CreateKey("meowchan.source.kohlchan", "kohlchan"), out var permission)
                    && permission.AllowProviderSizeMd5Mismatch;
                Assert.Equal(edge == "S1" ? !committed : committed, granted);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(edge));
        }
    }

    [Theory]
    [InlineData("C1")]
    [InlineData("C2")]
    [InlineData("C3")]
    [InlineData("S1")]
    [InlineData("S2")]
    public async Task AcceptedHostConfigCommandsBorrowAcrossReservedExclusive(string edge)
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        using DesktopPluginFixtures network = new() { RejectAll = true };
        await using DesktopSessionHost host = Host(profile.Path, edge is "S1" or "S2" ? network : null);
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State);
        ExperimentalSourceActivationTarget? target = null;
        long policyRevision = 0;
        if (edge is "S1" or "S2")
        {
            var projection = await host.ProjectExperimentalSourceActivationAsync(DesktopPluginFixtures.ThreadUrl("kohlchan")).WaitAsync(Deadline);
            Assert.Equal(ExperimentalSourceActivationProjectionStatus.ConfirmationRequired, projection.Status);
            target = Assert.IsType<ExperimentalSourceActivationTarget>(projection.Target);
            Assert.False(target.IsEnabled); Assert.False(target.IsGranted);
            if (edge == "S1")
            {
                var activation = await host.ActivateAndAllowExperimentalSourceAsync(target).WaitAsync(Deadline);
                Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, activation.Status);
                Assert.True(activation.ActualState!.IsEffective);
                policyRevision = Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.SourceId == "kohlchan").Snapshot.Revision;
            }
        }
        AssertSetting(host.Config, edge, committed: false);
        byte[] beforeBytes = File.ReadAllBytes(Settings(profile.Path));
        ProfileProducerEpoch oldEpoch = Epoch(host);
        long beforeVersion = host.Projection.Version;
        SemaphoreSlim commands = Field<SemaphoreSlim>(host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        TaskCompletionSource beforeIo = Signal(), afterIo = Signal(), exclusive = Signal(), resume = Signal(), captureHeld = Signal();
        using ManualResetEventSlim releaseBefore = new(), releaseAfter = new(), releaseCapture = new();
        ProfileAdmissionToken? beforeToken = null, afterToken = null;
        Config? configAtExclusive = null;
        DesktopSessionProjection? projectionAtExclusive = null;
        int beforeCalls = 0, afterCalls = 0;
        ObserveConfig(host, token =>
        {
            beforeToken = token; Interlocked.Increment(ref beforeCalls);
            beforeIo.TrySetResult(); Assert.True(releaseBefore.Wait(Deadline));
        }, token =>
        {
            afterToken = token; Interlocked.Increment(ref afterCalls);
            afterIo.TrySetResult(); Assert.True(releaseAfter.Wait(Deadline));
        });
        async Task ExecuteCommand()
        {
            switch (edge)
            {
                case "C1":
                    Config draft = host.Config.Clone(); draft.Theme = "Light";
                    await host.ApplySettingsAsync(draft); break;
                case "C2":
                    FirstRunSetupCommitResult firstRun = await host.CompleteFirstRunSetupAsync(new(host.Config.DownloadPath, 1048576, 5));
                    Assert.True(firstRun.Persisted); Assert.Null(firstRun.ActivationWarning); break;
                case "C3": await host.SetPausedAsync(false); break;
                case "S1": await host.SetSourceMediaIntegrityPermissionAsync(target!.PluginOwnerId, target.SourceId, false, policyRevision); break;
                case "S2":
                    var activated = await host.ActivateAndAllowExperimentalSourceAsync(target!);
                    Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, activated.Status);
                    Assert.True(activated.ActualState!.IsEnabled); Assert.True(activated.ActualState.IsActive);
                    Assert.True(activated.ActualState.IsGranted); Assert.True(activated.ActualState.IsEffective); break;
                default: throw new ArgumentOutOfRangeException(nameof(edge));
            }
        }
        Task command = Task.CompletedTask, owner = Task.CompletedTask, captureHold = Task.CompletedTask;
        try
        {
            command = ExecuteCommand(); // Dispatch/kernel acceptance is synchronous before _commands.
            Assert.False(command.IsCompleted);
            if (edge == "C3")
            {
                Assert.Equal(0, host.AcceptedCommandCount); // Controls are deliberately a separate finite lane.
                lock (Field<object>(host, "_admission"))
                    Assert.Single(Field<Dictionary<string, (string Target, Task Task)>>(host, "_acceptedControls"));
            }
            else Assert.Equal(1, host.AcceptedCommandCount);
            owner = host.RunWithProfileProducerExclusiveAsync(async admission =>
            {
                Assert.Equal(ProfileAdmissionKind.Exclusive, admission.Kind);
                configAtExclusive = host.Config.Clone(); projectionAtExclusive = host.Projection;
                exclusive.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            });
            Assert.Throws<InvalidOperationException>(() => { using var refused = oldEpoch.Enter(); });
            Assert.Throws<InvalidOperationException>(() => { using var refused = Lease(host).Admission.EnterOrdinaryWriter(); });
            Assert.False(exclusive.Task.IsCompleted);
            captureHold = Task.Run(() =>
            {
                lock (Field<object>(host, "_viewCapture"))
                { captureHeld.TrySetResult(); Assert.True(releaseCapture.Wait(Deadline)); }
            });
            await captureHeld.Task.WaitAsync(Deadline);
            commands.Release(); held = false;
            await beforeIo.Task.WaitAsync(Deadline);
            Assert.NotNull(beforeToken); Assert.Equal(ProfileAdmissionKind.Ordinary, beforeToken.Kind);
            Assert.NotSame(oldEpoch.Admission, beforeToken); // Actual command/control token, not the finalwrite family's.
            Assert.True(Lease(host).Admission.RunWithToken(beforeToken, () => true, ProfileAdmissionKind.Ordinary));
            Assert.False(command.IsCompleted); Assert.False(exclusive.Task.IsCompleted);
            Assert.Equal(beforeBytes, File.ReadAllBytes(Settings(profile.Path)));
            AssertSetting(host.Config, edge, committed: false);
            releaseBefore.Set();
            await afterIo.Task.WaitAsync(Deadline);
            Assert.Same(beforeToken, afterToken);
            Assert.False(command.IsCompleted); Assert.False(exclusive.Task.IsCompleted);
            // Disk/cleanup really completed, but Save has not returned for PublishConfig/policy publication.
            AssertSetting(Config.LoadConfig(profile.Path), edge, committed: true);
            AssertSetting(host.Config, edge, committed: false);
            AssertNoOwnedTemps(profile.Path);
            Task<bool> coreReturned = commands.WaitAsync(Deadline);
            releaseAfter.Set();
            Assert.True(await coreReturned); held = true; // Actual core released _commands after PublishConfig.
            AssertSetting(host.Config, edge, committed: true);
            Assert.False(command.IsCompleted); Assert.False(exclusive.Task.IsCompleted);
            Assert.True(Lease(host).Admission.RunWithToken(beforeToken!, () => true, ProfileAdmissionKind.Ordinary));
            // The command still owns its token while its required real projection is blocked.
            releaseCapture.Set(); commands.Release(); held = false;
            await captureHold.WaitAsync(Deadline);
            await command.WaitAsync(Deadline); // No tolerated save/partial activation failure in this positive case.
            await exclusive.Task.WaitAsync(Deadline);
            AssertSetting(host.Config, edge, committed: true);
            AssertSetting(Config.LoadConfig(profile.Path), edge, committed: true);
            AssertSetting(Assert.IsType<Config>(configAtExclusive), edge, committed: true);
            Assert.NotNull(projectionAtExclusive); Assert.False(projectionAtExclusive.IsStale);
            Assert.True(projectionAtExclusive.Version > beforeVersion);
            if (edge == "C3") Assert.False(projectionAtExclusive.IsPaused);
            if (edge is "S1" or "S2")
            {
                var policy = Assert.Single(projectionAtExclusive.SourcePolicies, row => row.Snapshot.SourceId == "kohlchan");
                Assert.Equal(edge == "S2", policy.IsGranted);
                Assert.True(Assert.Single((await host.Plugins!.ProfileStore.ReadAsync()).Plugins,
                    row => row.PluginId == target!.PluginOwnerId).Enabled);
                Assert.Empty(host.Plugins.Runtime.GetSnapshots());
            }
            Assert.Equal(1, beforeCalls); Assert.Equal(1, afterCalls);
            Assert.Empty(network.Requests); Assert.Empty(host.GetView().Threads); Assert.Empty(host.GetView().Boards);
        }
        finally
        {
            releaseBefore.Set(); releaseAfter.Set(); releaseCapture.Set(); if (held) commands.Release(); resume.TrySetResult();
            try { await Task.WhenAll(command, owner, captureHold).WaitAsync(Deadline); }
            finally { ObserveConfig(host, null, null); }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CutWatermarksAndResumeKeepFreshAuthority(bool firstRun, bool lateAccepted)
    {
        using TempDirectory profile = new(); Seed(profile.Path);
        await using DesktopSessionHost host = Host(profile.Path);
        await host.StartAsync().WaitAsync(Deadline);
        ProfileProducerEpoch oldEpoch = Epoch(host);
        var saves = new List<(ProfileAdmissionToken Token, double Width)>();
        ObserveConfig(host, null, token =>
        {
            double width = Config.LoadConfig(profile.Path).WindowWidth; // Unbound read, no cross-thread lease-monitor wait.
            lock (saves) saves.Add((token, width));
        });
        SemaphoreSlim commands = Field<SemaphoreSlim>(host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
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
                if (firstRun)
                {
                    var result = await host.CompleteFirstRunSetupAsync(new(host.Config.DownloadPath, 1048576, 5));
                    Assert.True(result.Persisted); Assert.Null(result.ActivationWarning);
                }
                else { Config draft = host.Config.Clone(); draft.Theme = "Light"; await host.ApplySettingsAsync(draft); }
            });
            await registered.Task.WaitAsync(Deadline);
            owner = host.RunWithProfileProducerExclusiveAsync(async _ =>
            {
                exclusive.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            });
            Assert.Throws<InvalidOperationException>(() => { using var refused = oldEpoch.Enter(); });
            Task successor = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1450);
            LocalReadingPosition next = Anchor with { Top = -30 };
            Task successorReading = host.RememberReadingPositionAsync(next, host.ReadingPositionGeneration);
            Assert.Equal(2, host.FinalWriteAdmissionCounts.Parked);
            Assert.False(successor.IsCompleted); Assert.False(successorReading.IsCompleted);
            accept.Set(); commands.Release(); held = false;
            await Task.WhenAll(command, prior, priorReading).WaitAsync(Deadline);
            await exclusive.Task.WaitAsync(Deadline); // Fixed watermark, not dynamic parked successor.
            Assert.False(successor.IsCompleted); Assert.False(successorReading.IsCompleted);
            Assert.Equal(1250, Config.LoadConfig(profile.Path).WindowWidth);
            Assert.Equal(1250, host.Config.WindowWidth);
            AssertSetting(host.Config, firstRun ? "C2" : "C1", committed: true);
            Assert.Equal(Anchor, JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(profile.Path, "reading-position.json")),
                ReadingPositionJsonContext.Default.LocalReadingPosition));
            lock (saves)
            {
                Assert.Equal(2, saves.Count); Assert.All(saves, save => Assert.Equal(1250, save.Width));
                Assert.Single(saves, save => ReferenceEquals(save.Token, oldEpoch.Admission));
                Assert.Single(saves, save => !ReferenceEquals(save.Token, oldEpoch.Admission));
            }
            // Old owner really released, not merely closed. Rejection reaches the admitted consumer.
            Config stale = host.Config.Clone(); stale.WindowWidth = 1999;
            string[] before = SettingsFamily(profile.Path);
            Assert.Throws<InvalidOperationException>(() => stale.SaveConfigAdmitted(profile.Path, oldEpoch.Admission!));
            Assert.Equal(before, SettingsFamily(profile.Path));
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            await Task.WhenAll(successor, successorReading).WaitAsync(Deadline);
            ProfileProducerEpoch fresh = Epoch(host);
            Assert.NotSame(oldEpoch, fresh); Assert.NotSame(oldEpoch.Admission, fresh.Admission);
            Assert.Equal(1450, host.Config.WindowWidth); Assert.Equal(1450, Config.LoadConfig(profile.Path).WindowWidth);
            Assert.Equal(next, await host.ReadReadingPositionAsync().WaitAsync(Deadline));
            lock (saves)
            {
                Assert.Equal(3, saves.Count);
                var replay = Assert.Single(saves, save => save.Width == 1450);
                Assert.Same(fresh.Admission, replay.Token);
            }
            Assert.False(host.Projection.IsStale);
            Assert.Equal((0, 0, 0), host.PreferenceWriteCounts);
            Assert.Equal(0, host.FinalWriteAdmissionCounts.Parked);
            AssertNoOwnedTemps(profile.Path);
        }
        finally
        {
            host.BeforeCommandFinalWriteBindForTests = null; host.AfterCommandFinalWriteBindForTests = null;
            accept.Set(); if (held) commands.Release(); resume.TrySetResult();
            try { await Task.WhenAll(command, owner).WaitAsync(Deadline); }
            finally { ObserveConfig(host, null, null); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointRejectsForeignOrReleasedTokenInsteadOfReportingCacheSuccess(bool released)
    {
        using TempDirectory profile = new(); Seed(profile.Path, preview: true);
        using TempDirectory other = new();
        using ProfileLease foreignLease = ProfileLease.Acquire(other.Path);
        foreignLease.Admission.CompleteStartup();
        using ProfileAdmissionToken foreign = foreignLease.Admission.EnterOrdinaryWriter();
        await using DesktopSessionHost host = Host(profile.Path);
        await host.StartAsync().WaitAsync(Deadline);
        using ProfileAdmissionToken ended = Lease(host).Admission.EnterOrdinaryWriter();
        ended.Dispose();
        byte[] cacheBefore = File.ReadAllBytes(Cache(profile.Path));
        string[] authoritativeBefore = SettingsFamily(profile.Path);
        Task engine = host.BeginShutdown(); // Captures the real Ready projection and reserves terminal ownership.
        Assert.NotNull(Field<StartupPreviewSnapshot?>(host, "_startupPreviewCheckpoint"));
        var method = typeof(DesktopSessionHost).GetMethod("SaveStartupPreviewCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var wrapped = Assert.Throws<TargetInvocationException>(() => method.Invoke(host, new object[] { released ? ended : foreign }));
        Assert.IsType<InvalidOperationException>(wrapped.InnerException);
        Assert.Equal(cacheBefore, File.ReadAllBytes(Cache(profile.Path)));
        Assert.Equal(authoritativeBefore, SettingsFamily(profile.Path));
        AssertNoOwnedTemps(profile.Path);
        // The refused invocation did not release the separate legitimate terminal owner.
        Assert.NotNull(Field<ProfileProducerEpoch.Use?>(host, "_terminalFinalWriteUse"));
        Assert.False(Lease(host).Admission.DrainAsync().IsCompleted);
        await engine.WaitAsync(Deadline);
        await host.StopAsync().WaitAsync(Deadline); // Its own valid checkpoint remains allowed.
        Assert.Equal(ProfileAdmissionPhase.Stopped, Lease(host).Admission.Phase);
        Assert.Empty(new StartupPreviewStore(profile.Path, Lease(host)).Load()!.Rows);
    }

    [Fact]
    public async Task PreviewCancellationBeforeMoveKeepsOldCacheAndDeletesOnlyItsOwnTemporary()
    {
        using TempDirectory profile = new(); Seed(profile.Path, preview: true);
        await using DesktopSessionHost host = Host(profile.Path);
        await host.StartAsync().WaitAsync(Deadline);
        StartupPreviewStore store = new(profile.Path, Lease(host));
        using ProfileAdmissionToken admitted = Lease(host).Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancellation = new();
        byte[] cacheBefore = File.ReadAllBytes(Cache(profile.Path));
        string[] authoritativeBefore = SettingsFamily(profile.Path);
        string unrelated = Path.Combine(profile.Path, ".startup-preview-unrelated.tmp");
        byte[] sentinel = { 1, 3, 5, 7 }; File.WriteAllBytes(unrelated, sentinel);
        int entered = 0, completed = 0;
        store.BeforeAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(admitted, token); Interlocked.Increment(ref entered);
            cancellation.Cancel(); // Accepted/pinned, before create/write/move; core must clean its own new temporary.
        };
        store.AfterAdmittedSaveMutationForTests = _ => Interlocked.Increment(ref completed);
        try
        {
            StartupPreviewSnapshot changed = new(DateTimeOffset.UtcNow, 0, 0, Array.Empty<StartupPreviewRow>());
            Assert.ThrowsAny<OperationCanceledException>(() => store.SaveAdmitted(changed, admitted, cancellation.Token));
            Assert.Equal(1, entered); Assert.Equal(0, completed);
            Assert.Equal(cacheBefore, File.ReadAllBytes(Cache(profile.Path)));
            Assert.Equal(authoritativeBefore, SettingsFamily(profile.Path));
            Assert.Equal(new[] { unrelated }, Directory.EnumerateFiles(profile.Path, ".startup-preview-*.tmp").ToArray());
            Assert.Equal(sentinel, File.ReadAllBytes(unrelated));
            Assert.True(Lease(host).Admission.RunWithToken(admitted, () => true, ProfileAdmissionKind.Ordinary));
            Assert.Equal(DesktopSessionState.Ready, host.State); Assert.Empty(host.GetView().Threads);
        }
        finally { store.BeforeAdmittedSaveMutationForTests = null; store.AfterAdmittedSaveMutationForTests = null; File.Delete(unrelated); }
    }

    [Fact]
    public async Task RealInvalidCacheTargetDoesNotChangeSettingsOrPreventShutdownAndLeaseRelease()
    {
        using TempDirectory profile = new(); Seed(profile.Path, preview: true);
        DesktopSessionHost host = Host(profile.Path);
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            string retained = Path.Combine(profile.Path, "preview.retained");
            byte[] oldCache = File.ReadAllBytes(Cache(profile.Path));
            File.Move(Cache(profile.Path), retained);
            Directory.CreateDirectory(Cache(profile.Path)); // Real same-leaf file-type refusal, not a mocked return.
            string blocker = Path.Combine(Cache(profile.Path), "owned-by-fixture"); File.WriteAllText(blocker, "retain");
            string[] authoritativeBefore = SettingsFamily(profile.Path);
            Task engine = host.BeginShutdown();
            Assert.NotNull(Field<StartupPreviewSnapshot?>(host, "_startupPreviewCheckpoint"));
            await host.FlushPreferencesAsync().WaitAsync(Deadline);
            await engine.WaitAsync(Deadline);
            await host.StopAsync().WaitAsync(Deadline); // Optional I/O/InvalidData cache failure is not an authority failure.
            Assert.Equal(DesktopSessionState.Stopped, host.State);
            Assert.Equal(ProfileAdmissionPhase.Stopped, Lease(host).Admission.Phase);
            Assert.False(host.IsProfileReleased);
            Assert.True(Directory.Exists(Cache(profile.Path))); Assert.Equal("retain", File.ReadAllText(blocker));
            Assert.Equal(oldCache, File.ReadAllBytes(retained));
            Assert.Equal(authoritativeBefore, SettingsFamily(profile.Path));
            AssertNoOwnedTemps(profile.Path);
            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Assert.True(host.IsProfileReleased);
            using ProfileLease reacquired = ProfileLease.Acquire(profile.Path);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    [Fact]
    public async Task FinalBatchAndTerminalPreviewBorrowWithoutReentryThroughRealStoreReturn()
    {
        using TempDirectory profile = new(); Seed(profile.Path, preview: true);
        DesktopSessionHost host = Host(profile.Path);
        TaskCompletionSource configIo = Signal(), previewBefore = Signal(), previewAfter = Signal();
        using ManualResetEventSlim releaseConfig = new(), releasePreviewBefore = new(), releasePreviewAfter = new();
        ProfileAdmissionToken? configBeforeToken = null, configAfterToken = null, previewBeforeToken = null, previewAfterToken = null;
        int configCalls = 0, previewBeforeCalls = 0, previewAfterCalls = 0;
        Task stop = Task.CompletedTask, disposal = Task.CompletedTask;
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            ProfileProducerEpoch oldEpoch = Epoch(host);
            StartupPreviewStore directStore = new(profile.Path, Lease(host)); // Construct BEFORE any hook holds the lease monitor.
            byte[] oldCache = File.ReadAllBytes(Cache(profile.Path));
            ObserveConfig(host, token =>
            {
                configBeforeToken = token; Interlocked.Increment(ref configCalls);
                configIo.TrySetResult(); Assert.True(releaseConfig.Wait(Deadline));
            }, token => configAfterToken = token);
            host.BeforeStartupPreviewAdmittedSaveMutationForTests = token =>
            {
                previewBeforeToken = token; Interlocked.Increment(ref previewBeforeCalls);
                previewBefore.TrySetResult(); Assert.True(releasePreviewBefore.Wait(Deadline));
            };
            host.AfterStartupPreviewAdmittedSaveMutationForTests = token =>
            {
                previewAfterToken = token; Interlocked.Increment(ref previewAfterCalls);
                previewAfter.TrySetResult(); Assert.True(releasePreviewAfter.Wait(Deadline));
            };
            Task batch = host.SaveViewPreferencesAsync(c => c.WindowWidth = 1550);
            await configIo.Task.WaitAsync(Deadline); // Actual RunStoreWrite pin, not just queued batch work.
            Assert.Same(oldEpoch.Admission, configBeforeToken);
            Task engine = host.BeginShutdown();
            ProfileProducerEpoch.Use terminal = Assert.IsType<ProfileProducerEpoch.Use>(Field<ProfileProducerEpoch.Use?>(host, "_terminalFinalWriteUse"));
            Assert.Same(oldEpoch.Admission, terminal.Admission);
            Assert.Equal(ProfileAdmissionPhase.Stopping, Lease(host).Admission.Phase);
            Assert.Throws<InvalidOperationException>(() => { using var refused = Lease(host).Admission.EnterOrdinaryWriter(); });
            Assert.Throws<InvalidOperationException>(() => host.Config.SaveConfig(profile.Path));
            Assert.Throws<InvalidOperationException>(() => directStore.Save(new(DateTimeOffset.UtcNow, 0, 0, Array.Empty<StartupPreviewRow>())));
            Task reading = host.RememberReadingPositionAsync(Anchor, host.ReadingPositionGeneration);
            Task flush = host.FlushPreferencesAsync();
            Task drain = Lease(host).Admission.DrainAsync();
            Assert.False(batch.IsCompleted); Assert.False(flush.IsCompleted); Assert.False(drain.IsCompleted);
            Assert.False(host.IsProfileReleased);
            Assert.Throws<IOException>(() => { using ProfileLease refused = ProfileLease.Acquire(profile.Path); });
            releaseConfig.Set();
            await Task.WhenAll(batch, reading, flush).WaitAsync(Deadline);
            Assert.Same(configBeforeToken, configAfterToken); Assert.Equal(1, configCalls);
            Assert.Equal(1550, host.Config.WindowWidth); Assert.Equal(1550, Config.LoadConfig(profile.Path).WindowWidth);
            Assert.Equal(1550, Field<Config>(host, "_confirmedConfig").WindowWidth);
            Assert.Equal(Anchor, JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(profile.Path, "reading-position.json")),
                ReadingPositionJsonContext.Default.LocalReadingPosition));
            await engine.WaitAsync(Deadline); // Shell BeginShutdown -> Flush -> Stop, not Stop to unstick Flush.
            Assert.Equal(oldCache, File.ReadAllBytes(Cache(profile.Path)));
            string[] committedSettings = SettingsFamily(profile.Path);
            stop = host.StopAsync();
            await previewBefore.Task.WaitAsync(Deadline);
            Assert.Same(terminal.Admission, previewBeforeToken);
            Assert.True(Lease(host).Admission.RunWithToken(previewBeforeToken!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.False(stop.IsCompleted); Assert.False(drain.IsCompleted);
            Assert.Equal(oldCache, File.ReadAllBytes(Cache(profile.Path)));
            releasePreviewBefore.Set();
            await previewAfter.Task.WaitAsync(Deadline);
            Assert.Same(previewBeforeToken, previewAfterToken);
            Assert.Equal(1, previewBeforeCalls); Assert.Equal(1, previewAfterCalls);
            // Read raw bytes: Load on the held lease from this thread would itself wait for this hook.
            using (JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(Cache(profile.Path))))
            {
                Assert.Equal(0, saved.RootElement.GetProperty("threadCount").GetInt32());
                Assert.Equal(0, saved.RootElement.GetProperty("boardCount").GetInt32());
                Assert.Equal(0, saved.RootElement.GetProperty("rows").GetArrayLength());
            }
            Assert.Equal(committedSettings, SettingsFamily(profile.Path)); AssertNoOwnedTemps(profile.Path);
            disposal = host.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted); Assert.False(disposal.IsCompleted); Assert.False(drain.IsCompleted);
            Assert.False(host.IsProfileReleased);
            Assert.Throws<IOException>(() => { using ProfileLease refused = ProfileLease.Acquire(profile.Path); });
            releasePreviewAfter.Set();
            await Task.WhenAll(stop, disposal, drain).WaitAsync(Deadline);
            Assert.True(host.IsProfileReleased); Assert.Equal(DesktopSessionState.Stopped, host.State);
            Assert.False(host.FinalWriteAdmissionCounts.HasEpoch);
            Assert.Equal(committedSettings, SettingsFamily(profile.Path));
            using ProfileLease reacquired = ProfileLease.Acquire(profile.Path);
            StartupPreviewSnapshot checkpoint = new StartupPreviewStore(profile.Path, reacquired).Load()!;
            Assert.Empty(checkpoint.Rows); Assert.Equal(0, checkpoint.ThreadCount); Assert.Equal(0, checkpoint.BoardCount);
        }
        finally
        {
            releaseConfig.Set(); releasePreviewBefore.Set(); releasePreviewAfter.Set();
            ObserveConfig(host, null, null);
            host.BeforeStartupPreviewAdmittedSaveMutationForTests = null;
            host.AfterStartupPreviewAdmittedSaveMutationForTests = null;
            try { await Task.WhenAll(stop, disposal).WaitAsync(Deadline); }
            finally { await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopBeforeReadyNeverInventsTerminalPreviewAuthority(bool startupEntered)
    {
        using TempDirectory profile = new(); Seed(profile.Path, preview: true);
        byte[] oldCache = File.ReadAllBytes(Cache(profile.Path));
        DesktopSessionHost host = Host(profile.Path);
        TaskCompletionSource previewRead = Signal(), never = Signal();
        int saves = 0;
        host.BeforeStartupPreviewAdmittedSaveMutationForTests = _ => Interlocked.Increment(ref saves);
        Task start = Task.CompletedTask;
        try
        {
            if (startupEntered)
            {
                host.AfterStartupPreviewForTests = async cancellation =>
                { previewRead.TrySetResult(); await never.Task.WaitAsync(cancellation); };
                start = host.StartAsync(); await previewRead.Task.WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Starting, host.State);
                Assert.NotNull(host.StartupPreview); // Cached text still cannot furnish an owner.
            }
            Task engine = host.BeginShutdown();
            Assert.Null(Field<ProfileProducerEpoch.Use?>(host, "_terminalFinalWriteUse"));
            Assert.Null(Field<StartupPreviewSnapshot?>(host, "_startupPreviewCheckpoint"));
            await engine.WaitAsync(Deadline);
            await host.StopAsync().WaitAsync(Deadline);
            await start.WaitAsync(Deadline);
            Assert.Equal(0, saves); Assert.Equal(oldCache, File.ReadAllBytes(Cache(profile.Path)));
            Assert.False(host.FinalWriteAdmissionCounts.HasEpoch); AssertNoOwnedTemps(profile.Path);
            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Assert.True(host.IsProfileReleased);
            using ProfileLease reacquired = ProfileLease.Acquire(profile.Path);
        }
        finally
        {
            never.TrySetResult(); host.AfterStartupPreviewForTests = null;
            host.BeforeStartupPreviewAdmittedSaveMutationForTests = null;
            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InnerPreviewAuthorityFailuresRemainVisibleInsteadOfBecomingCacheWarnings(bool disposedError)
    {
        using TempDirectory profile = new(); Seed(profile.Path, preview: true);
        DesktopSessionHost host = Host(profile.Path);
        Exception authorityFailure = disposedError
            ? new ObjectDisposedException("synthetic-admitted-preview", "Fixture inner store authority failure")
            : new InvalidOperationException("Fixture inner store authority failure");
        int beforeCalls = 0, afterCalls = 0;
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            byte[] oldCache = File.ReadAllBytes(Cache(profile.Path));
            string[] authoritativeBefore = SettingsFamily(profile.Path);
            ProfileAdmissionToken terminal = Epoch(host).Admission!;
            host.BeforeStartupPreviewAdmittedSaveMutationForTests = token =>
            {
                Assert.Same(terminal, token); Interlocked.Increment(ref beforeCalls);
                throw authorityFailure; // Inside REAL inner store/catch, before mutation. Never a successful-save fixture.
            };
            host.AfterStartupPreviewAdmittedSaveMutationForTests = _ => Interlocked.Increment(ref afterCalls);
            await host.BeginShutdown().WaitAsync(Deadline);
            await host.FlushPreferencesAsync().WaitAsync(Deadline);
            AggregateException stopped = await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync().WaitAsync(Deadline));
            Assert.Same(authorityFailure, Assert.Single(stopped.Flatten().InnerExceptions));
            Assert.Equal(1, beforeCalls); Assert.Equal(0, afterCalls);
            Assert.Equal(oldCache, File.ReadAllBytes(Cache(profile.Path)));
            Assert.Equal(authoritativeBefore, SettingsFamily(profile.Path)); AssertNoOwnedTemps(profile.Path);
            Assert.Equal(ProfileAdmissionPhase.Stopped, Lease(host).Admission.Phase);
            AggregateException disposed = await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask().WaitAsync(Deadline));
            Assert.Same(authorityFailure, Assert.Single(disposed.Flatten().InnerExceptions));
            Assert.True(host.IsProfileReleased);
            using ProfileLease reacquired = ProfileLease.Acquire(profile.Path);
        }
        finally
        {
            host.BeforeStartupPreviewAdmittedSaveMutationForTests = null;
            host.AfterStartupPreviewAdmittedSaveMutationForTests = null;
            if (!host.IsProfileReleased)
            {
                try { await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
                catch (AggregateException error) when (error.Flatten().InnerExceptions.Count == 1
                    && ReferenceEquals(error.Flatten().InnerExceptions[0], authorityFailure)) { }
            }
        }
    }
}
