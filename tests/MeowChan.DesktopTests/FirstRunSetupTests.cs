using System.Globalization;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopTests;

public sealed class FirstRunSetupTests
{
    [Fact]
    public void RawDraftSynchronizationDeduplicatesNotificationsAlreadyRepresentedByActionSnapshot()
    {
        var state = new FirstRunSetupDraftState(new("initial", "1", "5"));
        var immediateDraft = new FirstRunSetupRawDraft("chosen ü 日本", "0", "5");
        Assert.True(state.Synchronize(immediateDraft)); // action reads current controls before queued events
        long validatedRevision = state.Revision;
        for (int eventIndex = 0; eventIndex < 12; eventIndex++)
            Assert.False(state.Synchronize(immediateDraft));
        Assert.Equal(validatedRevision, state.Revision);
        Assert.Equal(immediateDraft, state.Current);
    }

    [Fact]
    public void EveryActualRawFieldChangeInvalidatesThePreviousRevisionWithoutNormalization()
    {
        var original = new FirstRunSetupRawDraft("chosen", "1", "5");
        foreach (FirstRunSetupRawDraft changed in new[]
        {
            original with { Folder = "chosen/../chosen" },
            original with { Bandwidth = "1.0" },
            original with { Parallel = "05" },
            original with { Folder = null },
            original with { Folder = "" }
        })
        {
            var state = new FirstRunSetupDraftState(original);
            long requestRevision = state.Revision;
            Assert.True(state.Synchronize(changed));
            Assert.NotEqual(requestRevision, state.Revision);
            Assert.Equal(changed, state.Current);
            Assert.False(state.Synchronize(changed));
            Assert.True(state.Synchronize(original));
            Assert.Equal(2, state.Revision);
        }
    }

    [Theory]
    [InlineData("1", "5", 1048576L, 5)]
    [InlineData("0.5", "1", 524288L, 1)]
    [InlineData("2.25", "12", 2359296L, 12)]
    public void PositiveLimitsUseExistingByteModel(string rate, string jobs, long expectedRate, int expectedCount)
    {
        Assert.True(FirstRunSetupCoordinator.TryParseLimits(rate, jobs, CultureInfo.InvariantCulture, out long bytes, out int count));
        Assert.Equal(expectedRate, bytes);
        Assert.Equal(expectedCount, count);
    }

    [Theory]
    [InlineData("0", "5")]
    [InlineData("-1", "5")]
    [InlineData("", "5")]
    [InlineData("unlimited", "5")]
    [InlineData("1", "0")]
    [InlineData("1", "1.5")]
    [InlineData("1", "2147483648")]
    [InlineData("8796093022208", "5")]
    [InlineData("79228162514264337593543950335", "5")]
    [InlineData("0.000000001", "5")]
    public void InvalidOrOverflowOrZeroLimitsNeverBecomeUnlimited(string rate, string jobs)
        => Assert.False(FirstRunSetupCoordinator.TryParseLimits(rate, jobs, CultureInfo.InvariantCulture, out _, out _));

    [Fact]
    public void LocaleAndDefaultsStayExplicit()
    {
        Assert.True(FirstRunSetupCoordinator.TryParseLimits("1,5", "5", CultureInfo.GetCultureInfo("de-DE"), out long bytes, out int count));
        Assert.Equal(1572864, bytes);
        Assert.Equal(5, count);
        Config defaults = new();
        Assert.Equal(1048576, defaults.BandwidthLimiter);
        Assert.Equal(5, defaults.MaxParallelDownloads);
        Assert.False(defaults.FirstRunSetupPending);
        Assert.Equal(0, defaults.FirstRunSetupVersion);
        Assert.False(FirstRunSetupCoordinator.CanOffer(defaults, DesktopSessionState.Ready)); // unbound != confirmed new
    }

    [Fact]
    public async Task OnlyConfirmedNewAfterReadyCanPrepareAndLaterDoesNotComplete()
    {
        using var fixture = new Fixture();
        var session = new FakeSession(fixture.LoadConfirmedNew());
        int shown = 0;
        Task<FirstRunSetupRunResult> Show(Config config, Func<FirstRunSetupValues, Task<FirstRunSetupCommitResult>> accept, CancellationToken token)
        {
            shown++;
            Assert.True(config.FirstRunSetupPending);
            Assert.Equal(0, config.FirstRunSetupVersion);
            return Task.FromResult(new FirstRunSetupRunResult(FirstRunSetupOutcome.Deferred));
        }
        session.State = DesktopSessionState.Starting;
        Assert.Equal(FirstRunSetupOutcome.NotOffered, (await FirstRunSetupCoordinator.RunCoreAsync(session, Show)).Outcome);
        Assert.Equal(0, session.PendingWrites);
        session.State = DesktopSessionState.Ready;
        Assert.Equal(FirstRunSetupOutcome.Deferred, (await FirstRunSetupCoordinator.RunCoreAsync(session, Show)).Outcome);
        Assert.Equal(1, shown);
        Assert.Equal(1, session.PendingWrites);
        Assert.Equal(0, session.Completions);
        Assert.True(session.Snapshot.FirstRunSetupPending);
        Assert.Equal(0, session.Snapshot.FirstRunSetupVersion);
    }

    [Fact]
    public async Task ExistingValidWithoutPendingAndAllUnsafeOriginsNeverOffer()
    {
        using var fixture = new Fixture();
        string existing = System.IO.Path.Combine(fixture.Path, "existing");
        new Config().SaveConfig(existing);
        Config old = Config.LoadConfig(existing);
        Assert.Equal(ProfileOrigin.ExistingValid, old.Origin);
        Assert.False(FirstRunSetupCoordinator.CanOffer(old, DesktopSessionState.Ready));
        foreach (DesktopSessionState state in new[] { DesktopSessionState.RecoveryFailed, DesktopSessionState.Stopping, DesktopSessionState.Stopped })
            Assert.False(FirstRunSetupCoordinator.CanOffer(fixture.LoadConfirmedNew(System.IO.Path.Combine(fixture.Path, state.ToString())), state));

        foreach (string kind in new[] { "damaged", "unsupported", "missing", "backup" })
        {
            string path = System.IO.Path.Combine(fixture.Path, kind);
            Directory.CreateDirectory(path);
            string settings = System.IO.Path.Combine(path, "settings.json");
            if (kind == "damaged") File.WriteAllText(settings, "{invalid");
            if (kind == "unsupported") File.WriteAllText(settings, "{\"SchemaVersion\":999999,\"FirstRunSetupPending\":true}");
            if (kind == "missing") File.WriteAllText(System.IO.Path.Combine(path, "session.json"), "{}");
            if (kind == "backup")
            {
                new Config { FirstRunSetupPending = true }.SaveConfig(path);
                File.Copy(settings, settings + ".bak");
                File.WriteAllText(settings, "{invalid");
            }
            Config config = fixture.LoadUnderLease(path);
            Assert.Equal(kind switch
            {
                "damaged" => ProfileOrigin.Damaged,
                "unsupported" => ProfileOrigin.Unsupported,
                "missing" => ProfileOrigin.ExistingMissingSettings,
                "backup" => ProfileOrigin.BackupRecovery,
                _ => throw new InvalidOperationException("Unknown private profile fixture.")
            }, config.Origin);
            var session = new FakeSession(config);
            FirstRunSetupRunResult result = await FirstRunSetupCoordinator.RunCoreAsync(session,
                (_, _, _) => throw new InvalidOperationException("Unsafe profile must not show setup."));
            Assert.Equal(FirstRunSetupOutcome.NotOffered, result.Outcome);
            Assert.Equal(0, session.PendingWrites);
            Assert.Equal(0, session.Completions);
        }
    }

    [Fact]
    public async Task DurablePendingReopensWithoutAnotherProvenanceWriteAndCompletionSuppressesNextOffer()
    {
        using var fixture = new Fixture();
        new Config { FirstRunSetupPending = true }.SaveConfig(fixture.Path);
        var session = new FakeSession(Config.LoadConfig(fixture.Path));
        FirstRunSetupRunResult result = await FirstRunSetupCoordinator.RunCoreAsync(session, async (config, accept, _) =>
        {
            var values = new FirstRunSetupValues(System.IO.Path.Combine(fixture.Path, "chosen"), 1048576, 5);
            Assert.True((await accept(values)).Persisted);
            return new(FirstRunSetupOutcome.Completed);
        });
        Assert.Equal(FirstRunSetupOutcome.Completed, result.Outcome);
        Assert.Equal(0, session.PendingWrites);
        Assert.Equal(1, session.Completions);
        Assert.False(FirstRunSetupCoordinator.CanOffer(session.Snapshot, DesktopSessionState.Ready));
    }

    [Fact]
    public async Task PendingTimeoutOrFailureDoesNotShowDialogOrCompleteAndDoesNotCancelUnderlyingWrite()
    {
        using var fixture = new Fixture();
        var session = new FakeSession(fixture.LoadConfirmedNew());
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingOverride = () => pending.Task;
        int shown = 0;
        FirstRunSetupRunResult timedOut = await FirstRunSetupCoordinator.RunCoreAsync(session,
            (_, _, _) => { shown++; return Task.FromResult(new FirstRunSetupRunResult(FirstRunSetupOutcome.Deferred)); },
            pendingBudget: TimeSpan.FromMilliseconds(20));
        Assert.Equal(FirstRunSetupOutcome.PreparationFailed, timedOut.Outcome);
        Assert.False(pending.Task.IsCompleted);
        Assert.Equal(0, shown);
        Assert.Equal(0, session.Completions);
        pending.SetResult();
        session.PendingOverride = () => Task.FromException(new IOException("fixture denied"));
        Assert.Equal(FirstRunSetupOutcome.PreparationFailed, (await FirstRunSetupCoordinator.RunCoreAsync(session,
            (_, _, _) => throw new Exception("Must not show"))).Outcome);
    }

    [Fact]
    public async Task CancelledPreparationAndSessionStoppingAfterPendingNeverComplete()
    {
        using var fixture = new Fixture();
        var session = new FakeSession(fixture.LoadConfirmedNew());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Equal(FirstRunSetupOutcome.Deferred, (await FirstRunSetupCoordinator.RunCoreAsync(session,
            (_, _, _) => throw new Exception("Must not show"), cancel.Token)).Outcome);
        Assert.Equal(0, session.PendingWrites);
        session.PendingOverride = () => { session.State = DesktopSessionState.Stopping; return Task.CompletedTask; };
        Assert.Equal(FirstRunSetupOutcome.NotOffered, (await FirstRunSetupCoordinator.RunCoreAsync(session,
            (_, _, _) => throw new Exception("Must not show"))).Outcome);
    }

    [Fact]
    public async Task CompletionCallbackRechecksReadinessAndRejectsInvalidValues()
    {
        using var fixture = new Fixture();
        var config = fixture.LoadConfirmedNew();
        config.FirstRunSetupPending = true;
        var session = new FakeSession(config);
        FirstRunSetupRunResult result = await FirstRunSetupCoordinator.RunCoreAsync(session, async (_, accept, _) =>
        {
            var valid = new FirstRunSetupValues(System.IO.Path.Combine(fixture.Path, "chosen"), 1, 1);
            await Assert.ThrowsAsync<ArgumentException>(() => accept(valid with { DownloadPath = "relative" }));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => accept(valid with { BandwidthBytesPerSecond = 0 }));
            session.State = DesktopSessionState.Stopping;
            await Assert.ThrowsAsync<InvalidOperationException>(() => accept(valid));
            return new(FirstRunSetupOutcome.Deferred);
        });
        Assert.Equal(FirstRunSetupOutcome.Deferred, result.Outcome);
        Assert.Equal(0, session.Completions);
    }

    [Fact]
    public async Task GenuineHostFailedRestoreNeverWritesPendingOrSettings()
    {
        using var fixture = new Fixture();
        var store = new FailedStore();
        await using DesktopSessionHost host = new(fixture.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "settings.json")));
        Assert.False(FirstRunSetupCoordinator.CanOffer(host.Config, host.State));
    }

    [Fact]
    public async Task GenuineNewHostPersistsPendingOnlyAfterReadyAndRestartRetainsProvenance()
    {
        using var fixture = new Fixture();
        string file = System.IO.Path.Combine(fixture.Path, "settings.json");
        await using (DesktopSessionHost host = new(fixture.Path, Array.Empty<IImageBoard>()))
        {
            Assert.False(File.Exists(file));
            Assert.Equal(ProfileOrigin.ConfirmedNew, host.Config.Origin);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Config pending = Config.LoadConfig(fixture.Path);
            Assert.True(pending.FirstRunSetupPending);
            Assert.Equal(0, pending.FirstRunSetupVersion);
            Assert.True(FirstRunSetupCoordinator.CanOffer(pending, host.State));
        }
        await using DesktopSessionHost restarted = new(fixture.Path, Array.Empty<IImageBoard>());
        await restarted.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(ProfileOrigin.ExistingValid, restarted.Config.Origin);
        Assert.True(restarted.Config.FirstRunSetupPending);
        Assert.Equal(0, restarted.Config.FirstRunSetupVersion);
        Assert.True(FirstRunSetupCoordinator.CanOffer(restarted.Config, restarted.State));
    }

    [Fact]
    public async Task NarrowHostCompletionPersistsValuesAndVersionTogetherPreservingOtherSettings()
    {
        using var fixture = new Fixture();
        string chosen = System.IO.Path.Combine(fixture.Path, "chosen");
        Config initial = new() { FirstRunSetupPending = true, Theme = "Light", VideoVolume = 37,
            RecheckTimer = 120, SaveUrlsOnExit = false, AutomaticUpdateChecks = false };
        initial.SaveConfig(fixture.Path);
        Directory.CreateDirectory(chosen); // only a private fixture directory
        await using DesktopSessionHost host = new(fixture.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DesktopSessionState.Ready, host.State);
        await host.SaveViewPreferencesAsync(config => config.WindowWidth = 1120).WaitAsync(TimeSpan.FromSeconds(15));
        FirstRunSetupCommitResult result = await host.CompleteFirstRunSetupAsync(new(chosen, 2097152, 3)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Persisted);
        Config saved = Config.LoadConfig(fixture.Path);
        Assert.False(saved.FirstRunSetupPending);
        Assert.Equal(1, saved.FirstRunSetupVersion);
        Assert.Equal(chosen, saved.DownloadPath);
        Assert.Equal(2097152, saved.BandwidthLimiter);
        Assert.Equal(3, saved.MaxParallelDownloads);
        Assert.Equal("Light", saved.Theme);
        Assert.Equal(37, saved.VideoVolume);
        Assert.Equal(120, saved.RecheckTimer);
        Assert.Equal(1120, saved.WindowWidth);
        Assert.False(saved.SaveUrlsOnExit);
        Assert.False(saved.AutomaticUpdateChecks);
        Assert.False(FirstRunSetupCoordinator.CanOffer(saved, host.State));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.CompleteFirstRunSetupAsync(new(chosen, 1048576, 5)));
        Assert.Equal(1, Config.LoadConfig(fixture.Path).FirstRunSetupVersion);
    }

    [Fact]
    public async Task NarrowHostSaveFailureCannotPublishCompletionOrNewValues()
    {
        using var fixture = new Fixture();
        new Config { FirstRunSetupPending = true }.SaveConfig(fixture.Path);
        await using DesktopSessionHost host = new(fixture.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Config before = host.Config.Clone();
        string file = System.IO.Path.Combine(fixture.Path, "settings.json");
        string retained = file + ".fixture";
        byte[] original = File.ReadAllBytes(file);
        File.Move(file, retained);
        Directory.CreateDirectory(file);
        try
        {
            try
            {
                FirstRunSetupCommitResult result = await host.CompleteFirstRunSetupAsync(new(fixture.Path, 2097152, 3)).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(result.Persisted);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            Assert.True(host.Config.FirstRunSetupPending);
            Assert.Equal(0, host.Config.FirstRunSetupVersion);
            Assert.Equal(before.DownloadPath, host.Config.DownloadPath);
            Assert.Equal(before.BandwidthLimiter, host.Config.BandwidthLimiter);
            Assert.Equal(original, File.ReadAllBytes(retained));
            Assert.True(Directory.Exists(file));
        }
        finally
        {
            Directory.Delete(file);
            File.Move(retained, file);
        }
    }

    private sealed class FakeSession(Config config) : IFirstRunSetupSession
    {
        public DesktopSessionState State { get; set; } = DesktopSessionState.Ready;
        public Config Snapshot => config.Clone();
        public int PendingWrites, Completions;
        public Func<Task>? PendingOverride;
        public async Task PersistPendingAsync()
        {
            PendingWrites++;
            if (PendingOverride != null) { await PendingOverride(); return; }
            config.FirstRunSetupPending = true;
        }
        public Task<FirstRunSetupCommitResult> CompleteAsync(FirstRunSetupValues values)
        {
            Completions++;
            config.DownloadPath = values.DownloadPath;
            config.BandwidthLimiter = values.BandwidthBytesPerSecond;
            config.MaxParallelDownloads = values.ParallelDownloads;
            config.FirstRunSetupPending = false;
            config.FirstRunSetupVersion = 1;
            return Task.FromResult(new FirstRunSetupCommitResult(true));
        }
    }

    private sealed class FailedStore : ISessionStore
    {
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<SessionLoadResult>(new IOException("fixture failed restore"));
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-first-setup-unit-");
        private readonly List<ProfileLease> _leases = new();

        public Config LoadUnderLease(string? path = null)
        {
            string profile = path ?? Path;
            ProfileLease lease = ProfileLease.Acquire(profile);
            _leases.Add(lease); // Keep authority alive for the entire fixture/config lifetime.
            return Config.LoadConfig(profile, lease);
        }

        public Config LoadConfirmedNew(string? path = null)
        {
            Config config = LoadUnderLease(path);
            Assert.Equal(ProfileOrigin.ConfirmedNew, config.Origin);
            return config;
        }

        public void Dispose()
        {
            for (int index = _leases.Count - 1; index >= 0; index--) _leases[index].Dispose();
            Directory.Delete(Path, recursive: true);
        }
    }
}
