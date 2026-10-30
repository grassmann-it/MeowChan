using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class PreferenceStartupSafetyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StartingEditsStayBoundedAndReadyCommitsLatestIndependentValues()
    {
        using TempDirectory directory = new();
        var store = new HeldStore();
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), sessionStoreFactory: (_, _) => store);
        Task startup = host.StartAsync();
        await store.Entered.Task.WaitAsync(Deadline);
        List<Task> writes = new();
        for (int index = 0; index < 128; index++)
        {
            int value = index;
            writes.Add(host.SaveViewPreferencesAsync(config => config.WindowWidth = 1000 + value));
        }
        writes.Add(host.SaveViewPreferencesAsync(config => config.VideoVolume = 37));
        Assert.Equal((0, 1, 0), host.PreferenceWriteCounts);
        Assert.All(writes, task => Assert.Same(writes[0], task));
        Assert.All(writes, task => Assert.False(task.IsCompleted));
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        store.Release.TrySetResult(new(new SessionSnapshot(), null, false));
        await startup.WaitAsync(Deadline);
        await Task.WhenAll(writes).WaitAsync(Deadline);
        Config saved = Config.LoadConfig(directory.Path);
        Assert.Equal(1127, saved.WindowWidth);
        Assert.Equal(37, saved.VideoVolume);
        Assert.True(saved.FirstRunSetupPending); // Durable new provenance, never completion.
        Assert.Equal(0, saved.FirstRunSetupVersion);
        Assert.Equal((0, 0, 0), host.PreferenceWriteCounts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrOvertakenStartupRejectsPendingDraftWithoutWriteAndReleasesLease(bool shutdown)
    {
        using TempDirectory directory = new();
        var store = new HeldStore();
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), sessionStoreFactory: (_, _) => store);
        Task startup = host.StartAsync();
        await store.Entered.Task.WaitAsync(Deadline);
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        Task write = host.SaveViewPreferencesAsync(config => config.WindowWidth = 1199);
        Task? stopping = shutdown ? host.StopAsync() : null;
        store.Release.TrySetException(new IOException("Synthetic restore failed"));
        await startup.WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<Exception>(() => write.WaitAsync(Deadline));
        try { await (stopping ?? host.StopAsync()).WaitAsync(Deadline); } catch (AggregateException) { }
        try { await host.DisposeAsync().AsTask().WaitAsync(Deadline); } catch (AggregateException) { }
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        using ProfileLease next = ProfileLease.Acquire(directory.Path);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"SchemaVersion\":9}")]
    public void DamagedFutureAndFreshDefaultBypassCannotWriteOrStart(string document)
    {
        using TempDirectory directory = new();
        string settings = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settings, document);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.False(loaded.AllowsOrdinaryWrites);
        Assert.Throws<InvalidOperationException>(() => loaded.Clone().SaveConfig(directory.Path));
        Config copied = new(); copied.CopyFrom(loaded);
        Assert.Throws<InvalidOperationException>(() => copied.SaveConfig(directory.Path));
        loaded.CopyFrom(new Config());
        Assert.Throws<InvalidOperationException>(() => loaded.SaveConfig(directory.Path));
        Assert.Throws<InvalidOperationException>(() => new Config().SaveConfig(directory.Path));
        Assert.Throws<InvalidOperationException>(() => new DesktopSessionHost(directory.Path, Array.Empty<IImageBoard>()));
        Assert.Equal(document, File.ReadAllText(settings));
        Assert.False(File.Exists(Path.Combine(directory.Path, "archive.db")));
        using ProfileLease next = ProfileLease.Acquire(directory.Path);
    }

    [Theory]
    [InlineData("work-state.sqlite")]
    [InlineData("archive.db")]
    [InlineData("plugin-state/state.json")]
    [InlineData(".meowchan-profile.protocol")]
    public void ExistingEvidenceWithoutSettingsIsNeverConfirmedNew(string evidence)
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, evidence);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "existing-data");
        Config config = Config.LoadConfig(directory.Path);
        Assert.Equal(ProfileOrigin.ExistingMissingSettings, config.Origin);
        Assert.Throws<InvalidOperationException>(() => config.SaveConfig(directory.Path));
        Assert.Throws<InvalidOperationException>(() => new Config().SaveConfig(directory.Path));
        Assert.Equal("existing-data", File.ReadAllText(path));
    }

    [Fact]
    public void LeaseRemnantsDoNotBecomeProofOfNewOnReacquisition()
    {
        using TempDirectory directory = new();
        using (ProfileLease first = ProfileLease.Acquire(directory.Path)) Assert.True(first.WasEmptyAtAcquisition);
        using ProfileLease next = ProfileLease.Acquire(directory.Path);
        Assert.False(next.WasEmptyAtAcquisition);
        Assert.Equal(ProfileOrigin.ExistingMissingSettings, Config.LoadConfig(directory.Path, next).Origin);
    }

    [Fact]
    public void FutureBackupIsNotReplacedEvenWhenPrimaryIsValid()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        string primary = File.ReadAllText(Path.Combine(directory.Path, "settings.json"));
        const string future = "{\"SchemaVersion\":9}";
        File.WriteAllText(Path.Combine(directory.Path, "settings.json.bak"), future);
        Config config = Config.LoadConfig(directory.Path);
        config.Theme = "Light";
        Assert.Throws<InvalidOperationException>(() => config.SaveConfig());
        Assert.Equal(primary, File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(future, File.ReadAllText(Path.Combine(directory.Path, "settings.json.bak")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritableConfigCannotInitializeAnotherExistingMissingProfile(bool firstSave)
    {
        using TempDirectory a = new(); using TempDirectory b = new();
        Config config = Config.LoadConfig(a.Path);
        if (firstSave) config.SaveConfig();
        File.WriteAllText(Path.Combine(b.Path, "archive.db"), "unmodified");
        Assert.Throws<InvalidOperationException>(() => config.SaveConfig(b.Path));
        Assert.False(File.Exists(Path.Combine(b.Path, "settings.json")));
        Assert.Equal("unmodified", File.ReadAllText(Path.Combine(b.Path, "archive.db")));
    }

    [Fact]
    public void StandaloneNewLoadCannotAuthorizeLaterUnrelatedDataButLeaseBoundFirstSaveCan()
    {
        using TempDirectory standalone = new();
        Config config = Config.LoadConfig(standalone.Path);
        File.WriteAllText(Path.Combine(standalone.Path, "archive.db"), "not-owned");
        Assert.Throws<InvalidOperationException>(() => config.SaveConfig());
        Assert.False(File.Exists(Path.Combine(standalone.Path, "settings.json")));
        using TempDirectory owned = new();
        Config bound;
        using (ProfileLease lease = ProfileLease.Acquire(owned.Path))
        {
            bound = Config.LoadConfig(owned.Path, lease);
            File.WriteAllText(Path.Combine(owned.Path, "archive.db"), "own-session");
            bound.SaveConfig();
            Assert.True(Config.LoadConfig(owned.Path).FirstRunSetupPending);
            Assert.Equal(0, Config.LoadConfig(owned.Path).FirstRunSetupVersion);
        }
        Assert.Throws<ObjectDisposedException>(() => bound.SaveConfig());
    }

    [Fact]
    public void KnownSameProfileCanRepublishItsConfirmedSnapshotAfterTransientPrimaryDisappearance()
    {
        using TempDirectory directory = new();
        new Config { Theme = "Light" }.SaveConfig(directory.Path);
        Config config = Config.LoadConfig(directory.Path);
        File.Delete(Path.Combine(directory.Path, "settings.json"));
        File.WriteAllText(Path.Combine(directory.Path, "archive.db"), "preserved");
        config.SaveConfig(); // Explicit retry of a known snapshot, not a new-profile inference.
        Assert.Equal("Light", Config.LoadConfig(directory.Path).Theme);
        Assert.False(Config.LoadConfig(directory.Path).FirstRunSetupPending);
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(directory.Path, "archive.db")));
    }

    [Fact]
    public async Task ShutdownBeforeStartRejectsBothPendingUnitsAndReleasesOnlyAfterCleanup()
    {
        using TempDirectory directory = new();
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        Task write = host.SaveViewPreferencesAsync(config => config.WindowWidth = 1199);
        Task reading = host.RememberReadingPositionAsync(new("fixture", "b", "1", "1", 0, "1"), host.ReadingPositionGeneration);
        await Assert.ThrowsAnyAsync<Exception>(() => host.StopAsync().WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<Exception>(() => write.WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<Exception>(() => reading.WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<Exception>(() => host.DisposeAsync().AsTask().WaitAsync(Deadline));
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
        using ProfileLease next = ProfileLease.Acquire(directory.Path);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingPrimaryCannotAuthorizeDamagedBackupOrAppearingBackupOnNewLease(bool newLease, bool validBackup)
    {
        using TempDirectory directory = new();
        ProfileLease? lease = null;
        try
        {
            Config config;
            if (newLease) { lease = ProfileLease.Acquire(directory.Path); config = Config.LoadConfig(directory.Path, lease); }
            else { new Config().SaveConfig(directory.Path); config = Config.LoadConfig(directory.Path); File.Delete(Path.Combine(directory.Path, "settings.json")); }
            string backup = validBackup ? "{\"Theme\":\"Light\"}" : "{damaged";
            File.WriteAllText(Path.Combine(directory.Path, "settings.json.bak"), backup);
            Assert.Throws<InvalidOperationException>(() => config.SaveConfig());
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            Assert.Equal(backup, File.ReadAllText(Path.Combine(directory.Path, "settings.json.bak")));
        }
        finally { lease?.Dispose(); }
    }

    [Fact]
    public void KnownSameProfileRetryPreservesAValidBackupWithoutAdoptingItsOlderValues()
    {
        using TempDirectory directory = new();
        Config config = Config.LoadConfig(directory.Path);
        config.Theme = "Light"; config.SaveConfig();
        config.Theme = "Dark"; config.SaveConfig();
        string backup = File.ReadAllText(Path.Combine(directory.Path, "settings.json.bak"));
        File.Delete(Path.Combine(directory.Path, "settings.json"));
        config.SaveConfig();
        Assert.Equal("Dark", Config.LoadConfig(directory.Path).Theme);
        Assert.Equal(backup, File.ReadAllText(Path.Combine(directory.Path, "settings.json.bak")));
    }

    [Fact]
    public async Task UpdateDisposeFailureNeverReleasesExclusiveOwnership()
    {
        using TempDirectory directory = new();
        var handler = new FailingDisposeHandler();
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), updateSessionFactory: (path, config) =>
            new MeowChan.Desktop.Updates.UpdateSession(path, config, new HttpClient(handler), disableEnvironment: () => "1"));
        await host.StartAsync().WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<Exception>(() => host.DisposeAsync().AsTask().WaitAsync(Deadline));
        Assert.False(host.IsProfileReleased);
        Assert.NotEqual(DesktopSessionState.Stopped, host.State);
        Assert.True(handler.DisposalAttempted);
        Assert.Throws<IOException>(() => ProfileLease.Acquire(directory.Path));
        // Fixture owns a deliberately throwing, already-terminated fake transport (no background IO).
        // Explicit test-only teardown, NOT a product recovery/unlock path.
        var field = typeof(DesktopSessionHost).GetField("_profileLease", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((ProfileLease)field.GetValue(host)!).Dispose();
    }

    private sealed class FailingDisposeHandler : HttpMessageHandler
    {
        internal bool DisposalAttempted;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Network forbidden in ownership fixture.");
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing); DisposalAttempted = true; throw new IOException("Synthetic transport disposal failure."); }
    }

    private sealed class HeldStore : ISessionStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<SessionLoadResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); return Release.Task; }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
