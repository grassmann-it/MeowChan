using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class ThreadFavoriteHydrationTests
{
    private static readonly ResourceId ThreadId = new("source", "board", "42");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static void Seed(string profile, bool marked)
    {
        new Config { SaveUrlsOnExit = true, DownloadsPaused = true }.SaveConfig(profile);
        File.WriteAllText(Path.Combine(profile, "thread-favorites.json"), JsonSerializer.Serialize(
            new { Version = 1, Revision = 17, Threads = marked ? new[] { ThreadId } : Array.Empty<ResourceId>() }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishedMarksAreReadyAtTheReadableBarrierBeforeRegistryAndWithoutWriteAuthority(bool marked)
    {
        using TempDirectory profile = new();
        Seed(profile.Path, marked);
        byte[] before = File.ReadAllBytes(Path.Combine(profile.Path, "thread-favorites.json"));
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterCatalogReadableForTests = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        ThreadFavoriteLoadSnapshot? atPublication = null;
        host.CatalogReadableChanged += () => atPublication = host.GetPublishedThreadFavoriteSnapshot();
        Task startup = host.StartAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(startup.IsCompleted);
            Assert.Equal(DesktopSessionState.Starting, host.State);
            Assert.True(host.CatalogReadable);
            Assert.NotNull(atPublication);
            Assert.Equal(ThreadFavoriteLoadState.Ready, atPublication.State);
            Assert.Equal(marked, atPublication.Snapshot!.Threads.Contains(ThreadId));
            Assert.Equal(17, atPublication.Snapshot.Revision);
            Assert.Same(atPublication, host.GetPublishedThreadFavoriteSnapshot());
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetThreadFavoriteAsync(ThreadId, !marked));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(profile.Path, "thread-favorites.json")));
        }
        finally { release.TrySetResult(); }
        await startup.WaitAsync(Deadline);
        // The late passive-metadata phase must not reread or replace this index.
        Assert.Same(atPublication, host.GetPublishedThreadFavoriteSnapshot());
    }

    [Fact]
    public async Task CorruptIndexIsUnavailableAtEarlyReadAndNeverAnEmptyUnmarkedSet()
    {
        using TempDirectory profile = new();
        Seed(profile.Path, true);
        string path = Path.Combine(profile.Path, "thread-favorites.json");
        File.WriteAllText(path, "{damaged");
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>());
        ThreadFavoriteLoadSnapshot? published = null;
        host.CatalogReadableChanged += () => published = host.GetPublishedThreadFavoriteSnapshot();
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State);
        Assert.NotNull(published);
        Assert.Equal(ThreadFavoriteLoadState.Unavailable, published.State);
        Assert.Null(published.Snapshot);
        Assert.False(string.IsNullOrWhiteSpace(published.Error));
        Assert.Same(published, host.GetPublishedThreadFavoriteSnapshot());
        Assert.Equal("{damaged", File.ReadAllText(path));
    }

    [Fact]
    public async Task ShutdownAtReadableBarrierCancelsStartupWithoutLateReadyOrFavoriteWrites()
    {
        using TempDirectory profile = new();
        Seed(profile.Path, true);
        byte[] before = File.ReadAllBytes(Path.Combine(profile.Path, "thread-favorites.json"));
        DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterCatalogReadableForTests = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        Task startup = host.StartAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.Contains(ThreadId, host.GetPublishedThreadFavoriteSnapshot().Snapshot!.Threads);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
        await startup.WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.False(host.CatalogReadable);
        Assert.True(host.IsProfileReleased);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(profile.Path, "thread-favorites.json")));
        using ProfileLease next = ProfileLease.Acquire(profile.Path);
    }

    [Fact]
    public void AlreadyLoadedRowsAndReaderDetailFollowPublicationWithoutReloadOrRepeatedSetConstruction() => WithPage(page =>
    {
        ArchiveThreadRow row = Row();
        page.FindControl<DataGrid>("ArchiveThreadTable")!.ItemsSource = new[] { row };
        Set(page, "_thread", new ArchivedThreadView(row.Summary, Array.Empty<CapturedPost>(), decimalPostOrder: false));
        ThreadFavoriteLoadSnapshot published = new(ThreadFavoriteLoadState.Loading, null, null);
        Set(page, "_loadThreadFavorites", (Func<ThreadFavoriteLoadSnapshot>)(() => published));
        Set(page, "_setThreadFavorite", (Func<ResourceId, bool, CancellationToken, Task<ThreadFavoriteCommit>>)
            ((_, _, _) => throw new InvalidOperationException("No write expected")));
        page.IsAvailable = false;
        Assert.Null(row.ThreadFavorite);
        Assert.False(row.CanToggleThreadFavorite);
        Assert.Null(row.ThreadFavoriteError);
        published = Ready(true, 17);
        page.IsReadable = true; // existing host UI action refresh, not a new list load
        Assert.True(row.ThreadFavorite);
        ArchiveThreadRow detail = Assert.IsType<ArchiveThreadRow>(page.FindControl<ArchiveThreadFavoriteButton>("ArchiveCurrentThreadFavorite")!.DataContext);
        Assert.True(detail.ThreadFavorite);
        Assert.False(detail.CanToggleThreadFavorite); // read readiness is not write readiness
        object ids = Get(page, "_threadFavoriteIds")!;
        page.IsAvailable = false;
        Assert.Same(ids, Get(page, "_threadFavoriteIds"));
        page.IsAvailable = true;
        Assert.True(row.CanToggleThreadFavorite);
        published = Ready(false, 18);
        page.IsAvailable = true;
        Assert.False(row.ThreadFavorite);
        Assert.False(detail.ThreadFavorite);
        published = new(ThreadFavoriteLoadState.Unavailable, null, "damaged index");
        page.IsAvailable = true;
        Assert.Null(row.ThreadFavorite);
        Assert.False(row.CanToggleThreadFavorite);
        Assert.Contains("damaged index", row.ThreadFavoriteError);
        published = Ready(true, 19);
        page.IsAvailable = true;
        Assert.True(row.ThreadFavorite);
        Assert.Null(row.ThreadFavoriteError);
        // A newly published, revalidated repair is authoritative even if its stored revision is lower.
        published = Ready(false, 1);
        page.IsAvailable = true;
        Assert.False(row.ThreadFavorite);
        page.EndLifetime();
        Set(page, "_loadThreadFavorites", (Func<ThreadFavoriteLoadSnapshot>)(() => throw new ObjectDisposedException("old host")));
        page.IsAvailable = false; // queued UI refresh cannot consult a disposed owner
        Assert.False(row.ThreadFavorite);
    });

    [Fact]
    public async Task RebindingCannotPublishAnOldProfilesDelayedAcknowledgementOrWriteToTheNewProfile()
    {
        using TempDirectory first = new(), second = new();
        await using DesktopSessionHost oldHost = new(first.Path, Array.Empty<IImageBoard>());
        await using DesktopSessionHost newHost = new(second.Path, Array.Empty<IImageBoard>());
        WithPage(page =>
        {
            ArchiveThreadRow row = Row();
            page.FindControl<DataGrid>("ArchiveThreadTable")!.ItemsSource = new[] { row };
            page.AttachThreadFavorites(oldHost);
            Set(page, "_loadThreadFavorites", (Func<ThreadFavoriteLoadSnapshot>)(() => Ready(false, 90)));
            TaskCompletionSource<ThreadFavoriteCommit> pending = new();
            int oldWrites = 0, newWrites = 0;
            Set(page, "_setThreadFavorite", (Func<ResourceId, bool, CancellationToken, Task<ThreadFavoriteCommit>>)((id, desired, token) =>
            { Assert.Equal(ThreadId, id); Assert.True(desired); oldWrites++; return pending.Task; }));
            page.IsAvailable = true;
            Task toggle = (Task)Invoke(page, "ToggleThreadFavoriteAsync", ThreadId)!;
            Assert.Equal(1, oldWrites);
            Assert.False(toggle.IsCompleted);
            page.AttachThreadFavorites(newHost);
            Assert.Null(row.ThreadFavorite); // never borrow old profile's cached true/false or revision
            Set(page, "_loadThreadFavorites", (Func<ThreadFavoriteLoadSnapshot>)(() => Ready(false, 1)));
            Set(page, "_setThreadFavorite", (Func<ResourceId, bool, CancellationToken, Task<ThreadFavoriteCommit>>)((_, _, _) =>
            { newWrites++; throw new InvalidOperationException("No write expected"); }));
            page.IsAvailable = true;
            pending.SetResult(new(ThreadId, true, true, Ready(true, 91).Snapshot!));
            Dispatcher.UIThread.RunJobs();
            Assert.True(toggle.IsCompletedSuccessfully);
            Assert.False(row.ThreadFavorite);
            Assert.True(row.CanToggleThreadFavorite);
            Assert.Equal(0, newWrites);
            Assert.Equal(1, ((ThreadFavoriteSnapshot)Get(page, "_threadFavoriteSnapshot")!).Revision);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldCompletionCannotClearNewProfilesPendingCommandOrPublishItsError(bool oldFails)
    {
        using TempDirectory first = new(), second = new();
        await using DesktopSessionHost oldHost = new(first.Path, Array.Empty<IImageBoard>());
        await using DesktopSessionHost newHost = new(second.Path, Array.Empty<IImageBoard>());
        WithPage(page =>
        {
            ArchiveThreadRow row = Row();
            page.FindControl<DataGrid>("ArchiveThreadTable")!.ItemsSource = new[] { row };
            page.AttachThreadFavorites(oldHost);
            Set(page, "_loadThreadFavorites", (Func<ThreadFavoriteLoadSnapshot>)(() => Ready(false, 90)));
            TaskCompletionSource<ThreadFavoriteCommit> oldPending = new(), newPending = new();
            Set(page, "_setThreadFavorite", (Func<ResourceId, bool, CancellationToken, Task<ThreadFavoriteCommit>>)
                ((_, _, _) => oldPending.Task));
            page.IsAvailable = true;
            Task oldCommand = (Task)Invoke(page, "ToggleThreadFavoriteAsync", ThreadId)!;
            page.AttachThreadFavorites(newHost);
            ThreadFavoriteLoadSnapshot current = Ready(false, 1);
            Set(page, "_loadThreadFavorites", (Func<ThreadFavoriteLoadSnapshot>)(() => current));
            Set(page, "_setThreadFavorite", (Func<ResourceId, bool, CancellationToken, Task<ThreadFavoriteCommit>>)
                ((_, _, _) => newPending.Task));
            page.IsAvailable = true;
            Task newCommand = (Task)Invoke(page, "ToggleThreadFavoriteAsync", ThreadId)!;
            if (oldFails) oldPending.SetException(new IOException("old profile failure"));
            else oldPending.SetResult(new(ThreadId, true, true, Ready(true, 91).Snapshot!));
            Dispatcher.UIThread.RunJobs();
            Assert.True(oldCommand.IsCompletedSuccessfully);
            Assert.False(newCommand.IsCompleted);
            Assert.False(row.CanToggleThreadFavorite);
            Assert.False(row.ThreadFavorite);
            Assert.Null(row.ThreadFavoriteError);
            Assert.DoesNotContain("old profile failure", page.FindControl<TextBlock>("ArchiveErrorText")!.Text ?? "");
            current = Ready(true, 2);
            newPending.SetResult(new(ThreadId, true, true, current.Snapshot!));
            Dispatcher.UIThread.RunJobs();
            Assert.True(newCommand.IsCompletedSuccessfully);
            Assert.True(row.ThreadFavorite);
            Assert.True(row.CanToggleThreadFavorite);
        });
    }

    private static ThreadFavoriteLoadSnapshot Ready(bool marked, long revision) => new(ThreadFavoriteLoadState.Ready,
        new ThreadFavoriteSnapshot(marked ? new[] { ThreadId } : Array.Empty<ResourceId>(), revision, revision.ToString()), null);
    private static ArchiveThreadRow Row() => new(new ArchiveThreadSummary(ThreadId, "", "", "", ThreadSourceState.NotFound,
        true, DateTimeOffset.UnixEpoch, null, 1, 1));
    private static object? Get(ArchivePage page, string name) => typeof(ArchivePage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
    private static void Set(ArchivePage page, string name, object value) => typeof(ArchivePage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static object? Invoke(ArchivePage page, string name, params object[] args) =>
        typeof(ArchivePage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static void WithPage(Action<ArchivePage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        ArchivePage page = new();
        try { test(page); }
        finally { page.EndLifetime(); }
    });
}
