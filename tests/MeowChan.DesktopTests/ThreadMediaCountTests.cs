using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class ThreadMediaCountTests
{
    private static readonly ResourceId ThreadId = new("4chan", "g", "100");

    [Fact]
    public void GalleryAndMissingIndexEntriesUseTheSameCapturedSlotsAsFilesArrive()
    {
        CapturedPost first = Post("100", new CapturedAttachment(0, "same"), new CapturedAttachment(2, "same"));
        CapturedPost second = Post("101", new CapturedAttachment(1, "later"), new CapturedAttachment(2, "deleted", IsDeleted: true),
            new CapturedAttachment(3, null));
        ArchiveThreadSummary summary = new(ThreadId, "https://offline.invalid/100", "Captured media", "/captured-media",
            ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 2, 1);
        ArchivedThreadView view = new(summary, [second, first]);
        CapturedAttachment later = second.Attachments![0];
        GalleryEntry a = SavedEntry(first, first.Attachments![0]), b = SavedEntry(first, first.Attachments[1]), c = SavedEntry(second, later);
        ThreadGalleryPositions empty = ThreadGalleryPositions.Create(view.MediaPositions, [])!;
        ThreadGalleryPositions partial = ThreadGalleryPositions.Create(view.MediaPositions, [b, c])!;
        ThreadGalleryPositions complete = ThreadGalleryPositions.Create(view.MediaPositions, [a, b, c])!;

        Assert.Equal((3, 3), (empty.Count, empty.Unavailable));
        Assert.Equal((3, 1, 2, 3), (partial.Count, partial.Unavailable, partial.NumberAt(0), partial.NumberAt(1)));
        Assert.Equal((3, 0, 2, 3), (complete.Count, complete.Unavailable, complete.NumberAt(1), complete.NumberAt(2)));
        Assert.Null(view.MediaPositions.NumberOf("101", 2));
        Assert.Null(view.MediaPositions.NumberOf("101", 3));
        Assert.Equal(3, MediaLibraryIndexBuilder.EntriesOf(view, []).Count(entry => entry.Presence == MediaLibraryPresence.Missing));

        GalleryEntry loose = c with { Post = null, Attachment = null };
        ThreadGalleryPositions withLoose = ThreadGalleryPositions.Create(view.MediaPositions, [b, loose])!;
        Assert.Equal((3, 2), (withLoose.Count, withLoose.Unavailable));
        Assert.Null(withLoose.NumberAt(1));
        ThreadGalleryPositions sorted = ThreadGalleryPositions.Create(view.MediaPositions, [c, b], capturedOrder: false)!;
        Assert.Equal((3, 1, 2), (sorted.Count, sorted.NumberAt(0), sorted.NumberAt(1)));
        Assert.Null(ThreadGalleryPositions.Create(new ThreadMediaPositions([]), [loose]));
    }

    [Theory]
    [InlineData(false, false, 150)]
    [InlineData(true, false, 150)]
    [InlineData(false, true, 149)]
    [InlineData(true, true, 150)]
    public void MediaGalleryWindowsKeepTheFullViewRanks(bool reversed, bool missing, int middleRank)
    {
        CapturedPost[] posts = Enumerable.Range(100, 300).Select(n => Post(n.ToString(), new CapturedAttachment(0, "m" + n))).ToArray();
        ArchiveThreadSummary summary = new(ThreadId, "https://offline.invalid/100", "Captured media", "/captured-media",
            ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, posts.Length, 1);
        ArchivedThreadView view = new(summary, posts);
        IEnumerable<MediaLibraryEntry> indexed = MediaLibraryIndexBuilder.EntriesOf(view, [])
            .Select(entry => entry with { Presence = missing && entry.PostId == "176" ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present });
        IReadOnlyList<MediaLibraryEntry> shown = MediaLibraryView.Select(indexed, MediaLibrarySort.ByThread,
            includeMissing: true, direction: reversed ? MediaLibrarySortDirection.Reversed : MediaLibrarySortDirection.Default);
        int[] present = MediaLibraryView.PresentIndex(shown);
        Dictionary<string, GalleryEntry> saved = posts.ToDictionary(post => post.PostId, post => SavedEntry(post, post.Attachments![0]));
        IReadOnlyDictionary<int, int> ranks = ThreadGalleryPositions.RankMediaView(view.MediaPositions, shown);
        foreach ((int tile, int rank) in new[] { (0, 1), (149, middleRank), (299, missing ? 299 : 300) })
        {
            (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(shown, present, tile);
            Assert.Equal(200, window.Count);
            Assert.Equal(shown[tile], window[start]);
            GalleryEntry[] resolved = window.Select(entry => saved[entry.PostId!]).ToArray();
            ThreadGalleryPositions positions = ThreadGalleryPositions.ForMediaView(view.MediaPositions, ranks, window, resolved, [])!;
            int slot = view.MediaPositions.NumberOf(shown[tile].PostId, shown[tile].Position)!.Value;
            Assert.Equal(rank, ranks[slot]);
            Assert.Equal((300, missing ? 1 : 0, rank), (positions.Count, positions.Unavailable, positions.NumberAt(start)));
        }
    }

    [Fact]
    public void GalleryAvailabilityAccountsForGroupedOccurrencesAndAnEntryLostDuringResolution()
    {
        CapturedPost[] posts = [Post("100", new CapturedAttachment(0, "a")), Post("101", new CapturedAttachment(0, "b")),
            Post("102", new CapturedAttachment(0, "c"))];
        ArchiveThreadSummary summary = new(ThreadId, "https://offline.invalid/100", "Captured media", "/captured-media",
            ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, posts.Length, 1);
        ArchivedThreadView view = new(summary, posts);
        MediaLibraryEntry[] present = MediaLibraryIndexBuilder.EntriesOf(view, [])
            .Select(entry => entry with { Presence = MediaLibraryPresence.Present })
            .OrderBy(entry => entry.PostId, StringComparer.Ordinal).ToArray();
        MediaLibraryEntry[] shown = [present[0], present[2]];
        ThreadGalleryPositions positions = ThreadGalleryPositions.ForMediaView(view.MediaPositions,
            ThreadGalleryPositions.RankMediaView(view.MediaPositions, shown), shown, [SavedEntry(posts[2], posts[2].Attachments![0])],
            [present[0], present[1]])!;
        Assert.Equal((3, 1, 2), (positions.Count, positions.Unavailable, positions.NumberAt(0)));
    }

    [Fact]
    public async Task ArchiveAndMediaPageShowCapturedCountBeforeAnyFilesAreIndexed()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        string directory = Path.Combine(temp.Path, "media");
        Directory.CreateDirectory(directory);
        await CaptureAsync(catalog, directory, InitialPosts());

        OnDispatcher(() =>
        {
            TaskCompletionSource releaseManifest = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ArchiveReader> OpenReader(CancellationToken token) => ArchiveReader.OpenAsync(temp.Path, token);
            async Task<MediaAssetSnapshot> OpenManifest(string path, CancellationToken token)
            {
                await releaseManifest.Task.WaitAsync(token);
                return await MediaAssetReader.OpenAsync(path, token);
            }
            ArchivePage archive = new(OpenReader, (_, _) => Task.CompletedTask, _ => { }) { IsReadable = true };
            MediaPage media = new(OpenReader, OpenManifest) { IsAvailable = true };
            try
            {
                ArchivedThreadView view = BooruComponentDispatcher.Wait(ReadViewAsync(OpenReader));
                Await(archive.LoadThreadAsync(ThreadId, true, view));
                TextBlock archiveCount = archive.FindControl<TextBlock>("ArchiveThreadMediaTotal")!;
                Assert.Equal(Texts.T("thread.media.total", 100), archiveCount.Text);

                media.ShowOnlyThread(ThreadId, "Captured media");
                Task build = media.ActivateAsync();
                TextBlock status = media.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "MediaStatus");
                WaitUntil(() => status.Text?.Contains(Texts.T("thread.media.total", 100), StringComparison.Ordinal) == true);
                Assert.False(build.IsCompleted);
                Assert.Empty(media.Tiles);

                releaseManifest.TrySetResult();
                Await(build);
                Assert.Equal(100, media.Snapshot?.Entries.Count);
                Assert.Contains(Texts.T("thread.media.total", 100), status.Text);

                // A newer capture deletes one old slot and adds two. An open view keeps its old snapshot until refreshed.
                Await(CaptureAsync(catalog, directory, [Post("100", new CapturedAttachment(0, null, IsDeleted: true)),
                    Post("200", new CapturedAttachment(0, "b"), new CapturedAttachment(1, "c"))]));
                Assert.Equal(100, view.MediaPositions.Count);
                Await(media.RebuildAsync());
                WaitUntil(() => status.Text?.Contains(Texts.T("thread.media.total", 101), StringComparison.Ordinal) == true);
                media.ResetFilters();
                Assert.DoesNotContain(Texts.T("thread.media.total", 101), status.Text ?? string.Empty);
            }
            finally
            {
                releaseManifest.TrySetResult();
                media.EndLifetime();
                archive.EndLifetime();
            }
        });
    }

    [Fact]
    public void ReaderAndGalleryPutTheThreadPositionInTheirSidebarHeaders() => OnDispatcher(() =>
    {
        CapturedPost post = Post("100", new CapturedAttachment(0, "first"), new CapturedAttachment(1, "second"));
        ThreadMediaPositions media = new([post]);
        ThreadGalleryPositions positions = ThreadGalleryPositions.Create(media, [SavedEntry(post, post.Attachments![1])])!;
        GalleryViewer viewer = new() { ThreadPositions = positions };
        string label = Texts.T("reader.sidebar.position", positions.NumberAt(0), positions.Count);
        viewer.SetPosition(label, false, false);
        Assert.Equal("2 of 2", viewer.PositionText);
        Assert.Equal(label, Assert.IsType<TextBlock>(viewer.SidePanel.HeaderCounter).Text);
        Assert.Equal("Medium 2 of 2", GalleryTagPanelCommands.CounterText(new(positions.NumberAt(0)!.Value, positions.Count, false)));
        Assert.Equal("Thread attachments: 2", GalleryTagPanelCommands.CounterText(new(0, positions.Count, false)));

        ReaderMediaSidebar sidebar = new();
        sidebar.SetThreadPosition(label);
        TextBlock counter = sidebar.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ReaderMediaSidebarThreadPosition");
        Assert.True(counter.IsVisible);
        Assert.Equal(label, counter.Text);
        sidebar.ShowEmpty();
        Assert.False(counter.IsVisible);
    });

    private static CapturedPost[] InitialPosts() =>
        Enumerable.Range(100, 100).Select(number => Post(number.ToString(), new CapturedAttachment(0, "m" + number))).ToArray();

    private static CapturedPost Post(string id, params CapturedAttachment[] attachments) =>
        new(id, 0, PostFieldPresence.All, Attachments: attachments);

    private static Task CaptureAsync(ArchiveCatalog catalog, string directory, CapturedPost[] posts, CancellationToken token = default) =>
        catalog.CaptureAsync(new(ThreadId, "https://offline.invalid/100", "Captured media",
            ThreadSourceState.Active, DateTimeOffset.UtcNow, directory, posts), token);

    private static async Task<ArchivedThreadView> ReadViewAsync(Func<CancellationToken, Task<ArchiveReader>> open)
    {
        await using ArchiveReader reader = await open(CancellationToken.None);
        return (await reader.ReadThreadAsync(ThreadId))!;
    }

    private static GalleryEntry SavedEntry(CapturedPost post, CapturedAttachment attachment) =>
        new(new(ThreadId, "/captured-media", attachment.MediaId!), null!, null!, post, attachment, false, false);

    private static void OnDispatcher(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        test();
    });

    private static void Await(Task task)
    {
        WaitUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.True(condition());
    }
}
