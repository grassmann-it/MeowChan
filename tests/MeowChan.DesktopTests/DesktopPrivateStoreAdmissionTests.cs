using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>Synthetic P08d host/store handoff regressions, with no providers or original media.</summary>
public sealed class DesktopPrivateStoreAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId Thread = new("fixture", "g", "42");
    private static readonly PostBookmarkKey Bookmark = new(Thread, "43");

    [Theory]
    [InlineData("favorite")]
    [InlineData("thread-favorite")]
    [InlineData("bookmark")]
    public async Task AcceptedStoreCommandSurvivesPendingExclusiveAndRetainsAdmissionThroughProjection(string family)
    {
        await using Fixture f = await Fixture.StartedAsync();
        await f.HoldCommandsAsync();
        await using ProjectionBarrier projection = await ProjectionBarrier.HoldAsync(f.Host);
        using CancellationTokenSource cancellation = new();
        Task command = StartCommand(f, family);
        OwnerExclusive? exclusive = null;
        try
        {
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            Assert.False(command.IsCompleted);
            Assert.False(File.Exists(f.StorePath(family)));
            // Bookmark is deliberately the first writer: its lazy cleanup constructor
            // must borrow the command token too, not enter a second Ordinary lane.
            if (family == "bookmark") Assert.Null(Field<PostBookmarkStore?>(f.Host, "_postBookmarks"));
            exclusive = new OwnerExclusive(f.Host, cancellation.Token);
            Assert.False(exclusive.Entered.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartCommand(f, family, other: true).WaitAsync(Deadline));
            Assert.Equal(1, f.Host.AcceptedCommandCount); // Refusal did not register another slot.

            f.ReleaseCommands();
            Until(() => IsCommitted(f, family), "Accepted store write did not persist and publish while Exclusive was pending.");
            AssertPersisted(f, family);
            // The store and feature snapshot have completed, but the forced capture
            // cannot pass _viewCapture. Release at the file commit would grant too soon.
            Assert.False(command.IsCompleted);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            Assert.False(exclusive.Entered.IsCompleted);

            projection.Release();
            await command.WaitAsync(Deadline);
            ProfileAdmissionToken granted = await exclusive.Entered.WaitAsync(Deadline);
            Assert.Equal(ProfileAdmissionKind.Exclusive, granted.Kind);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            AssertPersisted(f, family);
        }
        finally
        {
            f.ReleaseCommands();
            projection.Release();
            cancellation.Cancel();
            if (exclusive is not null) await exclusive.ReleaseAsync();
            await ObserveAsync(command);
        }
        Assert.Equal(DesktopSessionState.Ready, f.Host.State);
        Assert.Equal(ProfileAdmissionPhase.Ready, f.Lease.Admission.Phase);
        // Repeat the exact previously refused target, detecting both leaked slots
        // and stale action-dedup entries after the reservation was released.
        await StartCommand(f, family, other: true).WaitAsync(Deadline);
        Assert.Equal(0, f.Host.AcceptedCommandCount);
    }

    [Fact]
    public async Task FirstBookmarkReadPreservesExactOwnLegacyPendingWithoutCreatingWriterStore()
    {
        await using Fixture f = await Fixture.StartedAsync();
        string pending = Path.Combine(f.Directory.Path,
            "post-bookmarks.json.pending-0123456789abcdef0123456789abcdef");
        byte[] evidence = [0x50, 0x30, 0x38, 0x64, 0x0a];
        File.WriteAllBytes(pending, evidence);
        Assert.Null(Field<PostBookmarkStore?>(f.Host, "_postBookmarks"));
        string[] before = System.IO.Directory.GetFiles(f.Directory.Path).Order().ToArray();

        PostBookmarkSnapshot snapshot = await f.Host.ReadPostBookmarksAsync().WaitAsync(Deadline);

        Assert.Equal(0, snapshot.Revision);
        Assert.Empty(snapshot.Bookmarks);
        Assert.Equal(evidence, File.ReadAllBytes(pending));
        Assert.Null(Field<PostBookmarkStore?>(f.Host, "_postBookmarks"));
        Assert.False(File.Exists(f.StorePath("bookmark")));
        Assert.Equal(before, System.IO.Directory.GetFiles(f.Directory.Path).Order().ToArray());
        Assert.Equal(0, f.Host.AcceptedCommandCount);
        using CancellationTokenSource cancellation = new();
        OwnerExclusive exclusive = new(f.Host, cancellation.Token);
        try
        {
            ProfileAdmissionToken granted = await exclusive.Entered.WaitAsync(Deadline);
            Assert.Equal(ProfileAdmissionKind.Exclusive, granted.Kind);
        }
        finally
        {
            cancellation.Cancel();
            await exclusive.ReleaseAsync();
        }
    }

    // GalleryFavoriteSequenceTests already covers mark/unmark in Stopping and
    // early Dispose + host Dispose/Shutdown. Add only the missing pending-Exclusive
    // case here, including exact dictionary-token identity and projection lifetime.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FavoriteSequenceRetainsExactTokenAcrossPendingExclusiveAndEarlyClose(bool disposeEarly)
    {
        await using Fixture f = await Fixture.StartedAsync();
        using DesktopSessionHost.FavoriteWriteSequence sequence = f.Host.AdmitFavoriteSequence(f.Entry);
        ProfileAdmissionToken accepted = SequenceToken(f.Host, sequence);
        await f.HoldCommandsAsync();
        await using ProjectionBarrier projection = await ProjectionBarrier.HoldAsync(f.Host);
        using CancellationTokenSource cancellation = new();
        Task<bool> mark = sequence.SetAsync(true);
        OwnerExclusive exclusive = new(f.Host, cancellation.Token);
        try
        {
            if (disposeEarly) sequence.Dispose();
            Assert.Same(accepted, SequenceToken(f.Host, sequence));
            Assert.False(exclusive.Entered.IsCompleted);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            if (disposeEarly)
                await Assert.ThrowsAsync<ObjectDisposedException>(() => sequence.SetAsync(false).WaitAsync(Deadline));
            Assert.Throws<InvalidOperationException>(() => f.Host.AdmitFavoriteSequence(f.OtherEntry));
            Assert.Equal(1, f.Host.AcceptedCommandCount);

            f.ReleaseCommands();
            Until(() => f.Host.IsFavorite(f.Entry), "Sequence did not publish its accepted mark.");
            AssertPersisted(f, "favorite");
            Assert.False(mark.IsCompleted);
            Assert.False(exclusive.Entered.IsCompleted);
            Assert.Same(accepted, SequenceToken(f.Host, sequence));
            projection.Release();
            Assert.True(await mark.WaitAsync(Deadline));
            if (!disposeEarly)
            {
                Assert.False(exclusive.Entered.IsCompleted); // Idle gap still belongs to the sequence.
                Assert.Same(accepted, SequenceToken(f.Host, sequence));
                Assert.True(await sequence.SetAsync(false).WaitAsync(Deadline));
                Assert.Empty(new FavoriteStore(f.Directory.Path, f.Lease).List());
                Assert.False(f.Host.IsFavorite(f.Entry));
                Assert.Same(accepted, SequenceToken(f.Host, sequence));
                Assert.False(exclusive.Entered.IsCompleted);
                sequence.Dispose();
            }
            ProfileAdmissionToken granted = await exclusive.Entered.WaitAsync(Deadline);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
        }
        finally
        {
            f.ReleaseCommands();
            projection.Release();
            sequence.Dispose();
            cancellation.Cancel();
            await exclusive.ReleaseAsync();
            await ObserveAsync(mark);
        }
    }

    private static Task StartCommand(Fixture f, string family, bool other = false) => family switch
    {
        "favorite" => f.Host.SetFavoriteAsync(other ? f.OtherEntry : f.Entry, true),
        "thread-favorite" => f.Host.SetThreadFavoriteAsync(other ? Thread with { Thread = "44" } : Thread, true),
        "bookmark" => f.Host.SavePostBookmarkAsync(other ? new PostBookmarkKey(Thread, "44") : Bookmark,
            "synthetic note", other ? 1 : 0, false),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    private static bool IsCommitted(Fixture f, string family) => family switch
    {
        "favorite" => f.Host.IsFavorite(f.Entry),
        "thread-favorite" => f.Host.GetPublishedThreadFavoriteSnapshot().Snapshot?.Threads.Contains(Thread) == true,
        "bookmark" => PostBookmarkStore.ReadOnly(f.Directory.Path, f.Lease).Revision == 1,
        _ => false
    };

    private static void AssertPersisted(Fixture f, string family)
    {
        Assert.True(File.Exists(f.StorePath(family)));
        switch (family)
        {
            case "favorite":
                Assert.Equal(FavoriteStore.From(f.Entry.Key.Thread, f.Entry.Asset),
                    Assert.Single(new FavoriteStore(f.Directory.Path, f.Lease).List()));
                Assert.True(f.Host.IsFavorite(f.Entry));
                break;
            case "thread-favorite":
                var threads = new ThreadFavoriteStore(f.Directory.Path, f.Lease).ReadSnapshot();
                Assert.Equal(1, threads.Revision);
                Assert.Equal(Thread, Assert.Single(threads.Threads));
                Assert.Equal(threads.Fingerprint, f.Host.GetPublishedThreadFavoriteSnapshot().Snapshot!.Fingerprint);
                break;
            case "bookmark":
                var bookmarks = PostBookmarkStore.ReadOnly(f.Directory.Path, f.Lease);
                Assert.Equal(1, bookmarks.Revision);
                Assert.Equal(new PostBookmark(Bookmark, "synthetic note"), Assert.Single(bookmarks.Bookmarks));
                break;
        }
    }

    private static T Field<T>(DesktopSessionHost host, string name) =>
        (T)typeof(DesktopSessionHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private static ProfileAdmissionToken SequenceToken(DesktopSessionHost host, DesktopSessionHost.FavoriteWriteSequence sequence)
    {
        lock (Field<object>(host, "_admission"))
            return Field<Dictionary<DesktopSessionHost.FavoriteWriteSequence, ProfileAdmissionToken>>(host, "_favoriteWriteSequences")[sequence];
    }

    private static void Until(Func<bool> condition, string message) =>
        Assert.True(SpinWait.SpinUntil(condition, Deadline), message);

    // P08e adds a counted producer epoch even for a paused empty host. A raw
    // Admission.EnterExclusiveAsync waits for that live epoch forever by design;
    // use the real owner lane to suspend/drain it, then keep its borrowed token
    // held until the same pending-command/projection assertions have completed.
    private sealed class OwnerExclusive
    {
        private readonly TaskCompletionSource<ProfileAdmissionToken> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task<ProfileExclusiveOutcome<bool>> _operation;
        internal Task<ProfileAdmissionToken> Entered => _entered.Task;

        internal OwnerExclusive(DesktopSessionHost host, CancellationToken token)
        {
            _operation = host.RunWithProfileProducerExclusiveAsync(async admission =>
            {
                _entered.TrySetResult(admission);
                // Cancellation governs pending admission only. This synthetic non-transfer
                // hold finishes successfully only when ReleaseAsync opens its own gate.
                await _release.Task;
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            }, token);
        }

        internal async Task ReleaseAsync()
        {
            _release.TrySetResult();
            try
            {
                ProfileExclusiveOutcome<bool> outcome = await _operation.WaitAsync(Deadline);
                Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
                Assert.True(outcome.Result);
            }
            catch (OperationCanceledException)
            {
                // Never swallow an entered operation's cancellation as a successful release.
                Assert.False(_entered.Task.IsCompleted);
            }
        }
    }

    private static async Task ObserveAsync(Task command)
    {
        // Cleanup must observe faults without hiding the assertion that caused it.
        try { await command.WaitAsync(Deadline); }
        catch (Exception error) when (error is not TimeoutException) { }
    }

    private sealed class ProjectionBarrier : IAsyncDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _holder;

        private ProjectionBarrier(DesktopSessionHost host)
        {
            _holder = Task.Factory.StartNew(() =>
            {
                lock (Field<object>(host, "_viewCapture"))
                {
                    _entered.TrySetResult();
                    if (!_release.Wait(Deadline)) throw new TimeoutException("Projection barrier was not released.");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal static async Task<ProjectionBarrier> HoldAsync(DesktopSessionHost host)
        {
            ProjectionBarrier barrier = new(host);
            try { await barrier._entered.Task.WaitAsync(Deadline); return barrier; }
            catch { await barrier.DisposeAsync(); throw; }
        }

        internal void Release() => _release.Set();
        public async ValueTask DisposeAsync()
        {
            Release();
            try { await _holder.WaitAsync(Deadline); }
            finally { _release.Dispose(); }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Directory { get; } = new();
        internal DesktopSessionHost Host { get; }
        internal ProfileLease Lease => Field<ProfileLease>(Host, "_profileLease");
        internal ProfileAdmission Admission => Lease.Admission;
        internal GalleryEntry Entry => MakeEntry(0);
        internal GalleryEntry OtherEntry => MakeEntry(1);
        private bool _held;

        private Fixture()
        {
            try
            {
                new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                    DownloadPath = Path.Combine(Directory.Path, "absent-media") }.SaveConfig(Directory.Path);
                Host = new(Directory.Path, Array.Empty<IImageBoard>());
            }
            catch { Directory.Dispose(); throw; }
        }

        internal static async Task<Fixture> StartedAsync()
        {
            Fixture f = new();
            try
            {
                await f.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State);
                Assert.Equal(ProfileAdmissionPhase.Ready, f.Admission.Phase);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal string StorePath(string family) => Path.Combine(Directory.Path, family switch
        {
            "favorite" => "media-favorites.json",
            "thread-favorite" => "thread-favorites.json",
            "bookmark" => "post-bookmarks.json",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        });

        private GalleryEntry MakeEntry(int index)
        {
            string directory = Path.Combine(Directory.Path, "absent-originals");
            MediaAssetReference asset = new("asset-" + index, "image-" + index, "original", "image.png",
                Path.Combine(directory, "image-" + index + ".png"), 7, new string('a', 64),
                "https://fixture.invalid/image.png", "image.png", true);
            MediaAssetSnapshot manifest = new(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
            return new(new(Thread, directory, asset.AssetId), manifest, asset, null, null, false, true);
        }

        internal async Task HoldCommandsAsync()
        {
            Assert.False(_held);
            Assert.True(await Field<SemaphoreSlim>(Host, "_commands").WaitAsync(Deadline));
            _held = true;
        }

        internal void ReleaseCommands()
        {
            if (!_held) return;
            _held = false;
            Field<SemaphoreSlim>(Host, "_commands").Release();
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseCommands();
            try { await Host.DisposeAsync().AsTask().WaitAsync(Deadline); }
            finally { if (Host.IsProfileReleased) Directory.Dispose(); }
        }
    }
}
