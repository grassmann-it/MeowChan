using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>Real synthetic profiles, host admission/command gate and on-disk favorites; no shell/native simulation.</summary>
public sealed class GalleryFavoriteSequenceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AcceptedMarkThenDeferredUnmarkSurviveShutdownAndStopWaitsForSequenceDispose()
    {
        await using Fixture f = await Fixture.StartedAsync();
        var sequence = f.Admit();
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        await f.HoldCommandsAsync();
        Task<bool> mark = sequence.SetAsync(true);
        Assert.False(mark.IsCompleted);
        Assert.False(f.Host.IsFavorite(f.Entry));
        Task engine = f.Host.BeginShutdown();
        Task stop = f.Host.StopAsync();
        Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
        Assert.False(stop.IsCompleted);
        Assert.False(f.Host.IsProfileReleased);
        f.AssertProfileHeld();

        f.ReleaseCommands();
        Assert.True(await mark.WaitAsync(Deadline));
        Assert.True(f.Host.IsFavorite(f.Entry));
        Assert.Equal(f.Candidate, Assert.Single(f.Persisted())); // M really reached disk before submitting U.
        await engine.WaitAsync(Deadline);
        Assert.False(stop.IsCompleted);
        Assert.Equal(1, f.Host.AcceptedCommandCount); // The between-writes gap still belongs to this sequence.

        Assert.True(await sequence.SetAsync(false).WaitAsync(Deadline));
        Assert.False(f.Host.IsFavorite(f.Entry));
        Assert.Empty(f.Persisted());
        Assert.False(stop.IsCompleted);
        sequence.Dispose();
        await stop.WaitAsync(Deadline);
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        Assert.Equal(DesktopSessionState.Stopped, f.Host.State);
        Assert.False(f.Host.IsProfileReleased); // Stop is not Dispose.
        await f.Host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.True(f.Host.IsProfileReleased);
        Assert.Empty(f.ReopenPersisted());
    }

    [Fact]
    public async Task DisposeClosesNewSetsButRetainsAdmissionAndProfileForTheGatedAcceptedWrite()
    {
        await using Fixture f = await Fixture.StartedAsync();
        var sequence = f.Admit();
        await f.HoldCommandsAsync();
        Task<bool> mark = sequence.SetAsync(true);
        sequence.Dispose();
        sequence.Dispose();
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sequence.SetAsync(false));
        Task dispose = f.Host.DisposeAsync().AsTask();
        Assert.False(mark.IsCompleted);
        Assert.False(dispose.IsCompleted);
        Assert.False(f.Host.IsProfileReleased);
        f.AssertProfileHeld();
        f.ReleaseCommands();
        Assert.True(await mark.WaitAsync(Deadline));
        await dispose.WaitAsync(Deadline);
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        Assert.True(f.Host.IsProfileReleased);
        Assert.Equal(f.Candidate, Assert.Single(f.ReopenPersisted()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sequence.SetAsync(false));
    }

    [Fact]
    public async Task ShutdownRejectsNewSequencesAndNormalWritesButExistingIdleSequenceCanFinish()
    {
        await using Fixture f = await Fixture.StartedAsync();
        var accepted = f.Admit();
        await f.Host.BeginShutdown().WaitAsync(Deadline);
        Assert.Throws<InvalidOperationException>(() => f.Host.AdmitFavoriteSequence(f.Entry));
        Assert.Throws<InvalidOperationException>(() => f.Host.AdmitFavoriteSequence(f.OtherEntry(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SetFavoriteAsync(f.Entry, true));
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        Assert.True(await accepted.SetAsync(true).WaitAsync(Deadline));
        Assert.Equal(f.Candidate, Assert.Single(f.Persisted()));
        accepted.Dispose();
        await f.Host.StopAsync().WaitAsync(Deadline);
        Assert.Throws<InvalidOperationException>(() => f.Host.AdmitFavoriteSequence(f.OtherEntry(2)));
        Assert.Equal(0, f.Host.AcceptedCommandCount);
    }

    [Fact]
    public async Task SequenceAdmissionRequiresReadySessionAndDoesNotLeakSlotOnRejection()
    {
        await using Fixture f = new();
        Assert.Equal(DesktopSessionState.Starting, f.Host.State);
        Assert.Throws<InvalidOperationException>(() => f.Host.AdmitFavoriteSequence(f.Entry));
        Assert.Throws<ArgumentNullException>(() => f.Host.AdmitFavoriteSequence(null!));
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        await f.Host.StartAsync().WaitAsync(Deadline);
        var sequence = f.Admit();
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        sequence.Dispose();
        Assert.Equal(0, f.Host.AcceptedCommandCount);
    }

    [Fact]
    public async Task SequencesShareEightSlotBudgetAndExactActionDeduplicationWithOrdinaryWrites()
    {
        await using Fixture f = await Fixture.StartedAsync();
        await f.HoldCommandsAsync();
        Task<bool> ordinary = f.Host.SetFavoriteAsync(f.Entry, true);
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        Assert.Throws<DesktopSessionBusyException>(() => f.Host.AdmitFavoriteSequence(f.Entry));
        var sequences = Enumerable.Range(1, DesktopSessionHost.InteractiveCommandLimit - 1)
            .Select(index => f.Admit(f.OtherEntry(index))).ToArray();
        Assert.Equal(8, f.Host.AcceptedCommandCount);
        Assert.Throws<DesktopSessionBusyException>(() => f.Host.AdmitFavoriteSequence(f.OtherEntry(20)));
        await Assert.ThrowsAsync<DesktopSessionBusyException>(() => f.Host.SetFavoriteAsync(f.OtherEntry(21), true));
        sequences[0].Dispose();
        Assert.Equal(7, f.Host.AcceptedCommandCount);
        var replacement = f.Admit(f.OtherEntry(20));
        Assert.Equal(8, f.Host.AcceptedCommandCount);
        Assert.Throws<DesktopSessionBusyException>(() => f.Host.AdmitFavoriteSequence(f.OtherEntry(20)));
        f.ReleaseCommands();
        Assert.True(await ordinary.WaitAsync(Deadline));
        foreach (var sequence in sequences) sequence.Dispose();
        replacement.Dispose();
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        Assert.Equal(f.Candidate, Assert.Single(f.Persisted()));
    }

    [Fact]
    public async Task IdleDisposedLeaseCannotUseOrReleaseAReplacementAdmissionForTheSameFavorite()
    {
        await using Fixture f = await Fixture.StartedAsync();
        var first = f.Admit();
        Assert.Throws<DesktopSessionBusyException>(() => f.Host.AdmitFavoriteSequence(f.Entry));
        first.Dispose();
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        var replacement = f.Admit();
        first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.SetAsync(true));
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        Assert.True(await replacement.SetAsync(true).WaitAsync(Deadline));
        Assert.Equal(f.Candidate, Assert.Single(f.Persisted()));
        replacement.Dispose();
        Assert.Equal(0, f.Host.AcceptedCommandCount);
    }

    [Fact]
    public async Task LeaseAllowsOnlyOneInFlightWriteAndCanAcceptNextAfterActualCompletion()
    {
        await using Fixture f = await Fixture.StartedAsync();
        var sequence = f.Admit();
        await f.HoldCommandsAsync();
        Task<bool> mark = sequence.SetAsync(true);
        for (int duplicate = 0; duplicate < 16; duplicate++)
            await Assert.ThrowsAsync<DesktopSessionBusyException>(() => sequence.SetAsync(false));
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        Assert.False(mark.IsCompleted);
        Assert.False(f.Host.IsFavorite(f.Entry));
        f.ReleaseCommands();
        Assert.True(await mark.WaitAsync(Deadline));
        Assert.True(await sequence.SetAsync(false).WaitAsync(Deadline));
        Assert.False(await sequence.SetAsync(false).WaitAsync(Deadline)); // Preserve actual store Changed result.
        Assert.Empty(f.Persisted());
        sequence.Dispose();
        Assert.Equal(0, f.Host.AcceptedCommandCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStoppingWritePreservesPersistedTruthAndReleasesSlotAfterWriteAndDispose(bool disposeEarly)
    {
        await using Fixture f = await Fixture.StartedAsync();
        Assert.True(await f.Host.SetFavoriteAsync(f.Entry, true).WaitAsync(Deadline));
        var sequence = f.Admit();
        string backup = Path.Combine(f.Directory.Path, "media-favorites.json.bak");
        Directory.CreateDirectory(backup); // A real forbidden backup target, not a fake save delegate.
        await f.HoldCommandsAsync();
        Task<bool> unmark = sequence.SetAsync(false);
        if (disposeEarly) sequence.Dispose();
        Task engine = f.Host.BeginShutdown();
        Task stop = f.Host.StopAsync();
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        f.ReleaseCommands();
        await AssertPersistenceFailureAsync(unmark);
        Assert.True(unmark.IsFaulted);
        Assert.True(f.Host.IsFavorite(f.Entry));
        Assert.Equal(f.Candidate, Assert.Single(f.Persisted()));
        Assert.Equal(disposeEarly ? 0 : 1, f.Host.AcceptedCommandCount);
        if (!disposeEarly)
        {
            Assert.False(stop.IsCompleted);
            sequence.Dispose();
        }
        await Task.WhenAll(engine, stop).WaitAsync(Deadline);
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        Directory.Delete(backup);
        await f.Host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(f.Candidate, Assert.Single(f.ReopenPersisted()));
    }

    [Fact]
    public async Task FailedWriteCanBeExplicitlyRetriedOnTheSameLeaseWithoutLeakingAdmission()
    {
        await using Fixture f = await Fixture.StartedAsync();
        Assert.True(await f.Host.SetFavoriteAsync(f.Entry, true).WaitAsync(Deadline));
        var sequence = f.Admit();
        string backup = Path.Combine(f.Directory.Path, "media-favorites.json.bak");
        Directory.CreateDirectory(backup);
        await AssertPersistenceFailureAsync(sequence.SetAsync(false));
        Assert.True(f.Host.IsFavorite(f.Entry));
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        Directory.Delete(backup);
        Assert.True(await sequence.SetAsync(false).WaitAsync(Deadline));
        Assert.Empty(f.Persisted());
        sequence.Dispose();
        Assert.Equal(0, f.Host.AcceptedCommandCount);
    }

    [Fact]
    public async Task OrdinaryAcceptedWriteDoesNotGainSequencePermissionWhenShutdownOvertakesTheGate()
    {
        await using Fixture f = await Fixture.StartedAsync();
        await f.HoldCommandsAsync();
        Task<bool> ordinary = f.Host.SetFavoriteAsync(f.Entry, true);
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        Task engine = f.Host.BeginShutdown();
        f.ReleaseCommands();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ordinary.WaitAsync(Deadline));
        Assert.False(f.Host.IsFavorite(f.Entry));
        Assert.Empty(f.Persisted());
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        await engine.WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedLeaseStillChecksOrdinaryConfigWritePermission(bool stopping)
    {
        await using Fixture f = await Fixture.StartedAsync();
        var sequence = f.Admit();
        if (stopping) await f.Host.BeginShutdown().WaitAsync(Deadline);
        PropertyInfo origin = typeof(Config).GetProperty("Origin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object previous = origin.GetValue(f.Host.Config)!;
        try
        {
            // Synthetic permission transition only; no damaged personal profile or
            // bypass of the real Config guard is needed to prove its execution.
            origin.SetValue(f.Host.Config, ProfileOrigin.BackupRecovery);
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sequence.SetAsync(true).WaitAsync(Deadline));
            Assert.Contains("recovery", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(f.Host.IsFavorite(f.Entry));
            Assert.Empty(f.Persisted());
            Assert.Equal(1, f.Host.AcceptedCommandCount);
        }
        finally { origin.SetValue(f.Host.Config, previous); sequence.Dispose(); }
        Assert.Equal(0, f.Host.AcceptedCommandCount);
    }

    [Fact]
    public async Task SynchronousAdmissionAndSetDispatchDoNotWaitForProductLockOrDoIoOnCaller()
    {
        await using Fixture f = await Fixture.StartedAsync();
        object favoritesSync = typeof(DesktopSessionHost).GetField("_favoritesSync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(f.Host)!;
        using ManualResetEventSlim release = new();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holder = Task.Factory.StartNew(() =>
        {
            lock (favoritesSync)
            {
                entered.SetResult();
                if (!release.Wait(Deadline)) throw new TimeoutException("Favorite product-lock fixture was not released.");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task.WaitAsync(Deadline);
        try
        {
            // Return the lease/task from a dedicated caller before releasing the
            // store lock. A synchronous product operation here would block it.
            var sequence = await Task.Factory.StartNew(() => f.Admit(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(Deadline);
            Task<Task<bool>> dispatch = Task.Factory.StartNew(() => sequence.SetAsync(true), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task<bool> write = await dispatch.WaitAsync(Deadline);
            Assert.False(write.IsCompleted);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            release.Set();
            Assert.True(await write.WaitAsync(Deadline));
            Assert.Equal(f.Candidate, Assert.Single(f.Persisted()));
            sequence.Dispose();
            Assert.Equal(0, f.Host.AcceptedCommandCount);
        }
        finally { release.Set(); await holder.WaitAsync(Deadline); }
    }

    private static async Task AssertPersistenceFailureAsync(Task<bool> write)
    {
        Exception? error = await Record.ExceptionAsync(() => write.WaitAsync(Deadline));
        // File.Replace against a directory reports a platform-specific IO/access
        // error. A timeout or a successful write is not an acceptable failure.
        Assert.True(error is IOException or UnauthorizedAccessException,
            "Expected real favorite persistence failure, got: " + error);
        Assert.True(write.IsFaulted);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Directory { get; } = new();
        internal DesktopSessionHost Host { get; }
        internal GalleryEntry Entry { get; }
        internal FavoriteEntry Candidate => FavoriteStore.From(Entry.Key.Thread, Entry.Asset);
        private readonly List<DesktopSessionHost.FavoriteWriteSequence> _sequences = [];
        private readonly SemaphoreSlim _commands;
        private bool _held;

        internal Fixture()
        {
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                DownloadPath = Path.Combine(Directory.Path, "media") }.SaveConfig(Directory.Path);
            Host = new(Directory.Path, Array.Empty<IImageBoard>());
            _commands = (SemaphoreSlim)typeof(DesktopSessionHost).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Host)!;
            Entry = OtherEntry(0);
        }

        internal static async Task<Fixture> StartedAsync()
        {
            Fixture f = new();
            try { await f.Host.StartAsync().WaitAsync(Deadline); Assert.Equal(DesktopSessionState.Ready, f.Host.State); return f; }
            catch { await f.DisposeAsync(); throw; }
        }

        internal GalleryEntry OtherEntry(int index)
        {
            string directory = Path.Combine(Directory.Path, "absent-originals");
            var asset = new MediaAssetReference("asset-" + index, "image-" + index, "original", "image.png",
                Path.Combine(directory, "image-" + index + ".png"), 7, new string('a', 64),
                "https://fixture.invalid/image.png", "image.png", true);
            var manifest = new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
            return new(new(new("fixture", "g", "42"), directory, asset.AssetId), manifest, asset, null, null, false, true);
        }

        internal DesktopSessionHost.FavoriteWriteSequence Admit(GalleryEntry? entry = null)
        {
            var sequence = Host.AdmitFavoriteSequence(entry ?? Entry);
            _sequences.Add(sequence);
            return sequence;
        }

        internal async Task HoldCommandsAsync()
        {
            Assert.False(_held);
            Assert.True(await _commands.WaitAsync(Deadline));
            _held = true;
        }
        internal void ReleaseCommands()
        {
            if (!_held) return;
            _held = false;
            _commands.Release();
        }
        internal void AssertProfileHeld()
        {
            Assert.Throws<IOException>(() =>
            {
                using ProfileLease unexpected = ProfileLease.Acquire(Directory.Path);
            });
        }
        internal IReadOnlyList<FavoriteEntry> Persisted()
        {
            var lease = (ProfileLease)typeof(DesktopSessionHost).GetField("_profileLease", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Host)!;
            return new FavoriteStore(Directory.Path, lease).List();
        }
        internal IReadOnlyList<FavoriteEntry> ReopenPersisted()
        {
            using ProfileLease lease = ProfileLease.Acquire(Directory.Path);
            return new FavoriteStore(Directory.Path, lease).List();
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseCommands();
            foreach (var sequence in _sequences) sequence.Dispose();
            try { await Host.DisposeAsync().AsTask().WaitAsync(Deadline); }
            finally { if (Host.IsProfileReleased) Directory.Dispose(); }
        }
    }
}
