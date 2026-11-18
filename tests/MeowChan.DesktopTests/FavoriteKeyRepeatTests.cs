using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

/// <summary>
/// The favourite keys outside the gallery write explicit targets and act once per press (review of 15.11.2026): M twice stays a favourite, U twice stays none,
/// a held key is one write, F is relative to the effective value (the pending target while a write is pending), and a key pressed while the last write runs
/// is ignored. The decisions are pure; the routing of a real window is the smokes'.
/// </summary>
public sealed class FavoriteKeyRepeatTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static MediaLibraryEntry Entry(string asset) =>
        new(new ResourceId("s", "b", "1"), "T1", 1, Day, Day, "1", 1, null, 0, "att-v1:" + new string('a', 64),
            "/dir/1", asset, new string('a', 64), 1, "m1", "original", asset + ".png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);

    private static MediaLibraryTileRow Row(MediaLibraryEntry entry, MediaLibraryFlag favorite) =>
        new(new MediaLibraryTile(entry, [entry]), 0, _ =>
            new MediaLibraryEntryState(favorite, true, null, true, Array.Empty<string>(), Array.Empty<string>(), null));

    /// <summary>A stand-in for the host: the saved states, the pending queue and what each key press would write (the page's decision, then the host's write).</summary>
    private sealed class Host
    {
        internal readonly Dictionary<string, bool> Saved = new();
        internal readonly GalleryFavoriteCommandQueue<string> Queue = new();
        internal readonly List<(string Asset, bool Selected)> Writes = new();
        internal readonly TaskCompletionSource Release = new();
        internal readonly HeldKeyLatch Latch = new();
        internal Task KeyTask = Task.CompletedTask;
        // The rows keep the state they were built with (stale until the page refreshes), exactly as the page's rows do.
        internal readonly Dictionary<string, MediaLibraryTileRow> Rows = new();

        internal MediaLibraryTileRow Add(string asset, bool saved)
        {
            Saved[asset] = saved;
            return Rows[asset] = Row(Entry(asset), saved ? MediaLibraryFlag.Yes : MediaLibraryFlag.No);
        }

        internal void Refresh()
        {
            foreach (string asset in Rows.Keys.ToArray()) Rows[asset] = Row(Entry(asset), Saved[asset] ? MediaLibraryFlag.Yes : MediaLibraryFlag.No);
        }

        internal bool? Pending(MediaLibraryEntry entry) => Queue.IsPending(entry.AssetId!) ? Queue.Target(entry.AssetId!, false) : null;

        /// <summary>A star click or the gallery: a pending write that is not finished until <see cref="Release"/>.</summary>
        internal void StartPending(string asset, bool selected) =>
            _ = Queue.SetAsync(asset, selected, async value => { Writes.Add((asset, value)); await Release.Task; Saved[asset] = value; }, () => { });

        /// <summary>One KeyDown of a favourite key on the selected rows, as the page routes it.</summary>
        internal void Press(Key key, FavoriteKeyAction action, params string[] assets)
        {
            if (Latch.IsRepeat(key) || !KeyTask.IsCompleted) return;
            MediaLibraryTileRow[] rows = assets.Select(asset => Rows[asset]).ToArray();
            bool?[] effective = rows.Select(row => MediaPage.FavoriteKeyState(row, Pending)).ToArray();
            KeyTask = WriteAsync(FavoriteKeys.Writes(action, effective), rows);
        }

        private async Task WriteAsync(IReadOnlyList<(int Index, bool Selected)> writes, MediaLibraryTileRow[] rows)
        {
            foreach ((int index, bool selected) in writes)
            {
                string asset = rows[index].Entry.AssetId!;
                await Queue.SetAsync(asset, selected, value => { Writes.Add((asset, value)); Saved[asset] = value; return Task.CompletedTask; }, () => { });
            }
        }
    }

    [Fact]
    public async Task MTwiceStaysAFavouriteAndUTwiceStaysNone()
    {
        Host host = new();
        host.Add("a", saved: false);
        host.Press(Key.M, FavoriteKeyAction.Add, "a");
        host.Latch.Release(Key.M);
        await host.KeyTask;
        // The row still shows the old state (no refresh yet): the second M must not flip the favourite back (at most it writes "true" again).
        host.Press(Key.M, FavoriteKeyAction.Add, "a");
        await host.KeyTask;
        Assert.All(host.Writes, write => Assert.True(write.Selected));
        Assert.True(host.Saved["a"]);

        Host other = new();
        other.Add("b", saved: true);
        other.Press(Key.U, FavoriteKeyAction.Remove, "b");
        other.Latch.Release(Key.U);
        await other.KeyTask;
        other.Press(Key.U, FavoriteKeyAction.Remove, "b");
        await other.KeyTask;
        Assert.All(other.Writes, write => Assert.False(write.Selected));
        Assert.False(other.Saved["b"]);
    }

    [Fact]
    public void AStaleRowStateIsOverriddenByThePendingTargetSoMTwiceDuringAWriteStaysAFavourite()
    {
        Host host = new();
        MediaLibraryTileRow row = host.Add("a", saved: false);
        host.StartPending("a", true); // the star was clicked and the write is not finished
        Assert.True(MediaPage.FavoriteKeyState(row, host.Pending));
        Assert.False(MediaPage.FavoriteKeyState(row)); // the saved row state alone would say "none" and M would write the favourite again
        host.Press(Key.M, FavoriteKeyAction.Add, "a");
        Assert.True(host.KeyTask.IsCompleted);
        Assert.Equal(new[] { ("a", true) }, host.Writes); // only the star's own write
        host.Release.SetResult();
    }

    [Fact]
    public async Task FWithAPendingWriteTogglesThePendingTargetNotTheSavedState()
    {
        Host host = new();
        host.Add("a", saved: true);
        host.StartPending("a", false); // a removal is pending: the effective value is "none"
        host.Press(Key.F, FavoriteKeyAction.Toggle, "a");
        host.Release.SetResult();
        await host.KeyTask;
        Assert.Equal(new[] { ("a", false), ("a", true) }, host.Writes); // F brought it back instead of removing it a second time
        Assert.True(host.Saved["a"]);
    }

    [Fact]
    public async Task AMultiTileSelectionWithAPendingWriteIsOneDecisionOverTheEffectiveValues()
    {
        Host host = new();
        host.Add("a", saved: false);
        host.Add("b", saved: false);
        host.Add("c", saved: true);
        host.StartPending("a", true);
        // Effective: a yes (pending), b no, c yes: mixed, so F adds the missing one only.
        host.Press(Key.F, FavoriteKeyAction.Toggle, "a", "b", "c");
        host.Release.SetResult();
        await host.KeyTask;
        Assert.Equal(new[] { ("a", true), ("b", true) }, host.Writes.OrderBy(write => write.Asset).ToArray());

        // Everything effective a favourite (b is saved now, c too, a lands): F removes all of them.
        host.Latch.Release(Key.F);
        host.Refresh(); // the page's refresh after the writes
        host.Press(Key.F, FavoriteKeyAction.Toggle, "a", "b", "c");
        await host.KeyTask;
        Assert.All(new[] { "a", "b", "c" }, asset => Assert.False(host.Saved[asset]));
    }

    [Fact]
    public async Task AHeldKeyIsOneWriteAndAKeyDuringTheWriteIsIgnored()
    {
        Host host = new();
        host.Add("a", saved: false);
        for (int repeat = 0; repeat < 8; repeat++) host.Press(Key.F, FavoriteKeyAction.Toggle, "a"); // KeyDown, KeyDown, ... without a KeyUp
        await host.KeyTask;
        Assert.Equal(new[] { ("a", true) }, host.Writes);
        Assert.True(host.Latch.IsLatched);
        host.Latch.Release(Key.F); // the KeyUp
        Assert.False(host.Latch.IsLatched);

        // A key pressed while the last key's write still runs is ignored (FavoriteKeyTask is not finished).
        Host busy = new();
        busy.Add("b", saved: false);
        busy.KeyTask = new TaskCompletionSource().Task;
        busy.Press(Key.M, FavoriteKeyAction.Add, "b");
        Assert.Empty(busy.Writes);
    }

    [Fact]
    public void TheLatchIsPerKeyAndAnythingThatLosesTheKeyUpClearsIt()
    {
        HeldKeyLatch latch = new();
        Assert.False(latch.IsRepeat(Key.M));
        Assert.True(latch.IsRepeat(Key.M));
        Assert.False(latch.IsRepeat(Key.U)); // another key is its own press
        latch.Release(Key.M);
        Assert.False(latch.IsRepeat(Key.M));
        latch.Clear(); // a lost activation never sees the KeyUp
        Assert.False(latch.IsLatched);
        Assert.False(latch.IsRepeat(Key.M));
    }

    [Fact]
    public void TheWritesAreExplicitTargets()
    {
        Assert.Equal(new[] { (0, true) }, FavoriteKeys.Writes(FavoriteKeyAction.Add, new bool?[] { false }));
        Assert.Empty(FavoriteKeys.Writes(FavoriteKeyAction.Add, new bool?[] { true }));
        Assert.Equal(new[] { (0, false) }, FavoriteKeys.Writes(FavoriteKeyAction.Remove, new bool?[] { true }));
        Assert.Empty(FavoriteKeys.Writes(FavoriteKeyAction.Remove, new bool?[] { false }));
        Assert.Equal(new[] { (0, false), (2, false) }, FavoriteKeys.Writes(FavoriteKeyAction.Toggle, new bool?[] { true, null, true }));
        Assert.Equal(new[] { (1, true) }, FavoriteKeys.Writes(FavoriteKeyAction.Toggle, new bool?[] { true, false }));
        Assert.Empty(FavoriteKeys.Writes(FavoriteKeyAction.None, new bool?[] { true }));
    }

    [Fact]
    public void BothPagesLatchTheKeyAndSkipWhileTheLastKeyWriteRuns()
    {
        string media = Source("Desktop/Views/Media/MediaPage.FavoriteKeys.cs"), archive = Source("Desktop/Views/Archive/ArchivePage.FavoriteKeys.cs");
        Assert.Contains("_favoriteKeyLatch.IsRepeat(e.Key) || !FavoriteKeyTask.IsCompleted", media);
        Assert.Contains("_favoriteKeyLatch.IsRepeat(e.Key) || !FavoriteKeyTask.IsCompleted", archive);
        Assert.Contains("FavoriteKeys.Writes(action, effective)", media);
        Assert.Contains("FavoriteKeys.Writes(action, marked)", archive);
        Assert.DoesNotContain("ToggleTileFavoriteAsync", media);
        // The host answers the explicit actions and the archive toggle reads the effective value.
        string window = Source("Desktop/Shell/MainWindow.Media.cs");
        Assert.Contains("case \"favorites.add\": await SetGalleryFavoriteAsync(entry, true)", window);
        Assert.Contains("case \"favorites.remove\": await SetGalleryFavoriteAsync(entry, false)", window);
        Assert.Contains("PendingFavorite = PendingMediaFavorite", window);
        Assert.Contains("SetArchiveFavoriteAsync(entry, !EffectiveArchiveFavorite(entry))", Source("Desktop/Shell/MainWindow.Navigation.cs"));
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
