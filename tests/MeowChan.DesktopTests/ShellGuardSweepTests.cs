using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Lifecycle sweep S4 (16.11.2026): page-switch video pause, abandoned row frames, shell busy flags, stale plugin worker workspaces.
[Collection("Localization")]
public sealed class ShellGuardSweepTests
{
    // ---- F6: the shell hides the Archive host, not the page ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingTheArchivePagePausesTheReaderVideos(bool playInSidebar) =>
        WithShell(async (window, _, _) =>
        {
            Click(window.FindControl<Button>("ArchiveNavigationButton")!);
            Assert.Equal(MainWindow.WorkspacePage.Archive, window.SelectedPage);
            ArchivePage page = window.ArchivePage;
            page.ApplyReaderMediaPreferences(page.ReaderMedia with
            {
                Sidebar = true, SidebarAlways = true,
                SidebarMinimumReaderWidth = ReaderDisplayPreferences.MediaSidebarReaderWidthMinimum,
                Video = page.ReaderMedia.Video with { Enabled = true, SidebarAutoplay = true }
            });
            window.UpdateLayout();
            if (!page.IsMediaSidebarShown && page.CanToggleThreadList) page.ToggleThreadList();
            await BooruTestSupport.WaitAsync(() =>
            {
                window.UpdateLayout();
                return page.IsMediaSidebarShown;
            }, "the embedded reader sidebar");
            ReaderInlineVideoSlot sidebar = Assert.IsType<ReaderInlineVideoSlot>(page.SidebarInlineVideo);
            ReaderInlineVideoSlot post = new(new Grid(), activatorWhenIdle: false);
            RecordingPlayer player = new();

            // The player substitutes for the native backend. The real page schedules its one-shot autoplay.
            sidebar.Offer(page.InlineVideo, "fixture", _ => Task.FromResult<MeowChan.Gallery.GalleryEntry?>(null), "clip.webm");
            var owner = playInSidebar ? sidebar : post;
            Method<ArchivePage>("ScheduleSidebarVideoAutoplay").Invoke(page, null);
            DispatcherTimer timer = Assert.IsType<DispatcherTimer>(typeof(ArchivePage)
                .GetField("_sidebarVideoAutoplay", PrivateInstance)!.GetValue(page));
            Assert.True(timer.IsEnabled);
            timer.Interval = TimeSpan.FromMinutes(1);
            Assert.True(await page.InlineVideo.PlayAsync(owner, () => player));
            Assert.True(player.IsPlaying);

            Click(window.FindControl<Button>("DownloadsNavigationButton")!);

            Assert.Equal(MainWindow.WorkspacePage.Downloads, window.SelectedPage);
            Assert.False(timer.IsEnabled);
            Assert.Equal(1, player.Pauses);
            Assert.False(player.IsPlaying);
            Assert.Equal(1, page.InlineVideo.LiveCount);
        });

    // ---- F7: an abandoned row frame asks for the next one ----

    [Fact]
    public void AnAbandonedRowFrameAsksForTheNextFrameAFewTimesOnly()
    {
        string window = Source("Desktop/Shell/MainWindow.axaml.cs");
        string abandon = window[window.IndexOf("private void AbandonSessionRows()", StringComparison.Ordinal)..];
        abandon = abandon[..abandon.IndexOf("private void BeginSessionRows(", StringComparison.Ordinal)];
        int closing = abandon.IndexOf("if (_isClosing) return;", StringComparison.Ordinal);
        int request = abandon.IndexOf("_sessionRefresh.RequestImmediate();", StringComparison.Ordinal);
        Assert.True(closing >= 0 && request > closing, "The refresh is requested after the closing check, never during shutdown.");
        Assert.Contains("_pendingSessionProjection = null;", abandon);
        Assert.Contains("++_abandonedRowFramesInARow <= MaxAbandonedRowFrameRetries", abandon); // a deterministic failure must not spin the UI
        Assert.Contains("_abandonedRowFramesInARow = 0;", window); // a completed frame ends the streak
        Assert.Equal(1, Occurrences(window, "_abandonedRowFramesInARow = 0;").Count());
    }

    [Fact]
    public void TheShellAllowsAnotherCommandAfterOpeningTheBoardArchiveFails() =>
        WithShell(async (window, session, _) =>
        {
            Button add = window.FindControl<Button>("AddUrlButton")!;
            BoardArchiveLocation archive = Assert.IsType<BoardArchiveLocation>(
                session.FindBoardArchive(BoardArchiveHostTests.BoardUrl, fromBoard: true));
            Task OpenArchive() => (Task)Method<MainWindow>("OpenBoardArchiveAsync").Invoke(window, [archive, add])!;

            // A dialog cannot open against a hidden owner.
            window.Hide();
            await Assert.ThrowsAsync<InvalidOperationException>(OpenArchive);
            window.Show();
            await BooruTestSupport.WaitAsync(() => add.IsEffectivelyEnabled, "Downloads admission after the archive failed to open");

            Task reopened = OpenArchive();
            BoardArchiveWindow dialog = Assert.Single(window.OwnedWindows.OfType<BoardArchiveWindow>());
            try
            {
                ListBox threads = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "BoardArchiveThreads");
                await BooruTestSupport.WaitAsync(() => threads.ItemCount == 2, "the archive to show both saved thread IDs");
            }
            finally { dialog.Close(); }
            await reopened;
            Assert.True(add.IsEffectivelyEnabled);
        }, useBoardArchive: true);

    [Fact]
    public void TheShellAllowsAnotherSettingsApplyAfterPersistenceFails() =>
        WithShell(async (window, session, profile) =>
        {
            TextBox urlInput = window.FindControl<TextBox>("UrlInput")!;
            Assert.True(urlInput.Focus());
            string previousLanguage = session.Config.Language;
            Assert.Equal("en", previousLanguage);
            Config candidate = session.Config.Clone();
            candidate.Language = "de";
            Task Apply() => (Task)Method<MainWindow>("ApplySettingsAsync").Invoke(window, [candidate])!;

            const string failure = "Settings storage unavailable";
            session.Config.BeforeAdmittedSaveMutationForTests = _ => throw new IOException(failure);
            try
            {
                Task applying = Apply();
                Assert.False(urlInput.IsEffectivelyEnabled);
                IOException error = await Assert.ThrowsAsync<IOException>(() => applying);
                Assert.Equal(failure, error.Message);
                Assert.Equal(previousLanguage, session.Config.Language);
                Assert.Equal(previousLanguage, Config.LoadConfig(profile).Language);
            }
            finally { session.Config.BeforeAdmittedSaveMutationForTests = null; }

            Assert.True(urlInput.IsEffectivelyEnabled);
            Assert.True(urlInput.IsKeyboardFocused);

            await Apply();
            Assert.Equal(candidate.Language, session.Config.Language);
            Assert.Equal(candidate.Language, Config.LoadConfig(profile).Language);
            Assert.Equal(candidate.Language, Texts.Language);
        });

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static MethodInfo Method<T>(string name) => typeof(T).GetMethod(name, PrivateInstance)!;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void WithShell(Func<MainWindow, DesktopSessionHost, string, Task> test, bool useBoardArchive = false) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruComponentDispatcher.Wait(Exercise());

            async Task Exercise()
            {
                using TempDirectory profile = new();
                using BoardArchiveHostTests.ArchiveTransport transport = new() { EmptyLiveCatalog = true };
                new Config
                {
                    DownloadPath = Path.Combine(profile.Path, "media"), FirstRunSetupVersion = 1,
                    WorkspaceMode = "Advanced", Language = "en", DownloadsPaused = true
                }.SaveConfig(profile.Path);
                await using DesktopSessionHost session = useBoardArchive
                    ? BoardArchiveHostTests.CreateHost(profile.Path, transport)
                    : new(profile.Path, Array.Empty<IImageBoard>(),
                        updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
                await using MainWindow window = new(session) { Width = 1440, Height = 800, Classes = { MotionPolicy.OffClass } };
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await window.StartupTimingRestoreTask;
                await BooruTestSupport.WaitAsync(() => window.FindControl<Button>("AddUrlButton")!.IsEffectivelyEnabled,
                    "the restored Downloads workspace");
                await test(window, session, profile.Path);
            }
        });

    private sealed class RecordingPlayer : IReaderInlineVideoPlayer
    {
        public bool IsPlaying { get; private set; }
        internal int Pauses { get; private set; }
        public Task StartAsync() => PlayAsync();
        public Task PlayAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync() { Pauses++; IsPlaying = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsPlaying = false; return ValueTask.CompletedTask; }
    }

    // ---- F9: stale plugin worker workspaces ----

    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheSweepRemovesOnlyOldIdleWorkerWorkspacesInsideItsRoot()
    {
        using TempDirectory directory = new();
        string root = System.IO.Path.Combine(directory.Path, "plugin-workers");
        string stale = Workspace(root, "worker-" + Guid.NewGuid().ToString("N"), Now.AddHours(-3));
        File.WriteAllText(System.IO.Path.Combine(stale, "scratch.bin"), "x");
        Directory.SetLastWriteTimeUtc(stale, Now.AddHours(-3)); // creating the file just touched the folder's own time (the real clock is after Now)
        string young = Workspace(root, "worker-" + Guid.NewGuid().ToString("N"), Now.AddMinutes(-2));
        string busy = Workspace(root, "worker-" + Guid.NewGuid().ToString("N"), Now.AddHours(-3));
        string foreign = Workspace(root, "worker-notes", Now.AddHours(-3));
        string upper = Workspace(root, "worker-" + Guid.NewGuid().ToString("N").ToUpperInvariant(), Now.AddHours(-3));
        string other = Workspace(root, "filter-stage-" + Guid.NewGuid().ToString("N"), Now.AddHours(-3));
        string outside = Workspace(directory.Path, "worker-" + Guid.NewGuid().ToString("N"), Now.AddHours(-3)); // sibling of the root, not inside it
        string loose = System.IO.Path.Combine(root, "worker-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(loose, "keep");

        PluginWorkerWorkspaceSweep.Result result = PluginWorkerWorkspaceSweep.Sweep(root, nowUtc: Now, isInUse: path => path == busy);

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.KeptYoung);
        Assert.Equal(1, result.KeptInUse);
        Assert.False(result.Truncated);
        Assert.False(Directory.Exists(stale));
        foreach (string kept in new[] { young, busy, foreign, upper, other, outside }) Assert.True(Directory.Exists(kept), kept);
        Assert.True(File.Exists(loose));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void TheSweepIsBoundedAndTolerantOfAMissingRootOrALinkedRoot()
    {
        using TempDirectory directory = new();
        string root = System.IO.Path.Combine(directory.Path, "plugin-workers");
        Assert.Equal(default(PluginWorkerWorkspaceSweep.Result), PluginWorkerWorkspaceSweep.Sweep(root, nowUtc: Now)); // nothing created, nothing thrown
        Assert.False(Directory.Exists(root));

        for (int index = 0; index < 5; index++) Workspace(root, "worker-" + Guid.NewGuid().ToString("N"), Now.AddHours(-3));
        PluginWorkerWorkspaceSweep.Result first = PluginWorkerWorkspaceSweep.Sweep(root, maximumFolders: 2, nowUtc: Now, isInUse: _ => false);
        Assert.Equal(2, first.Removed);
        Assert.True(first.Truncated);
        Assert.Equal(3, Directory.GetDirectories(root).Length);
        PluginWorkerWorkspaceSweep.Result rest = PluginWorkerWorkspaceSweep.Sweep(root, nowUtc: Now, isInUse: _ => false);
        Assert.Equal(3, rest.Removed);
        Assert.Empty(Directory.GetDirectories(root));

        if (OperatingSystem.IsWindows()) return;
        // A root that is a link could lead outside the profile: nothing is swept through it.
        string target = System.IO.Path.Combine(directory.Path, "elsewhere");
        string victim = Workspace(target, "worker-" + Guid.NewGuid().ToString("N"), Now.AddHours(-3));
        string link = System.IO.Path.Combine(directory.Path, "linked-workers");
        Directory.CreateSymbolicLink(link, target);
        Assert.Equal(default(PluginWorkerWorkspaceSweep.Result), PluginWorkerWorkspaceSweep.Sweep(link, nowUtc: Now, isInUse: _ => false));
        Assert.True(Directory.Exists(victim));
    }

    [Fact]
    public void TheSweepRunsOnceBeforeTheFirstWorkerOfTheSession()
    {
        string session = Source("Desktop/PluginSession.cs");
        string probe = session[session.IndexOf("private async Task<StartupProbe> ProbeAsync(CancellationToken token)", StringComparison.Ordinal)..];
        probe = probe[..probe.IndexOf("private async Task<StartupProbe> ProbeCoreAsync", StringComparison.Ordinal)];
        int sweep = probe.IndexOf("PluginWorkerWorkspaceSweep.Sweep(_workerWorkspaceRoot", StringComparison.Ordinal);
        Assert.True(sweep >= 0);
        Assert.True(sweep < probe.IndexOf("MeasureStartupAsync(\"probe\"", StringComparison.Ordinal));
        Assert.Contains("Interlocked.Exchange(ref _workspaceSweepStarted, 1) == 0", probe);
    }

    // ---- plumbing ----

    private static string Workspace(string root, string name, DateTime lastWriteUtc)
    {
        string path = System.IO.Path.Combine(root, name);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
            yield return index;
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = MeowChan.Testing.TemporaryWorkspacePaths.CreateDirectory("shell-sweep-");
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    private static string Root([CallerFilePath] string sourcePath = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!, "../.."));

    private static string Source(string relative) => File.ReadAllText(System.IO.Path.Combine(Root(), relative));
}
