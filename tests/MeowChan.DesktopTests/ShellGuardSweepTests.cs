using System.Runtime.CompilerServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Lifecycle sweep S4 (16.11.2026): page-switch video pause, abandoned row frames, shell busy flags, stale plugin worker workspaces.
public sealed class ShellGuardSweepTests
{
    // ---- F6: the shell hides the Archive host, not the page ----

    [Fact]
    public void LeavingTheArchivePagePausesTheReaderVideos()
    {
        string navigation = Source("Desktop/Shell/MainWindow.Navigation.cs");
        int hook = navigation.IndexOf("ArchivePageHost.PropertyChanged +=", StringComparison.Ordinal);
        Assert.True(hook >= 0, "The Archive host needs the same visibility hook as the Media host.");
        string line = navigation[hook..navigation.IndexOf('\n', hook)];
        Assert.Contains("IsVisibleProperty", line);
        Assert.Contains("e.NewValue is false", line);
        Assert.Contains("_archivePage?.PauseInlineVideos()", line);
        // Hooked before the page becomes the host's content, like the media page.
        Assert.True(hook < navigation.IndexOf("ArchivePageHost.Content = _archivePage;", StringComparison.Ordinal));
        // The method pauses the sidebar video too: its autoplay is stopped and every inline player pauses.
        string video = Source("Desktop/Views/Archive/ArchivePage.InlineVideo.cs");
        string pause = video[video.IndexOf("internal Task PauseInlineVideos()", StringComparison.Ordinal)..];
        pause = pause[..pause.IndexOf("private void WatchInlineVideoWindow", StringComparison.Ordinal)];
        Assert.Contains("_sidebarVideoAutoplay?.Stop();", pause);
        Assert.Contains("_inlineVideo.PauseAllAsync()", pause);
    }

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

    // ---- F8: the shell's busy flags are set right before the try that clears them ----

    [Fact]
    public void TheShellSetsItsBusyFlagsOnlyRightBeforeTheTryThatClearsThem()
    {
        int found = 0;
        foreach (string file in Directory.GetFiles(System.IO.Path.Combine(Root(), "Desktop", "Shell"), "MainWindow*.cs"))
        {
            string text = File.ReadAllText(file).Replace("\r\n", "\n");
            foreach (string flag in new[] { "_importing = true;", "_applyingPreferences = true;" })
            {
                foreach (int index in Occurrences(text, flag))
                {
                    string after = text[(index + flag.Length)..];
                    int nextTry = after.IndexOf("try", StringComparison.Ordinal);
                    Assert.True(nextTry >= 0, System.IO.Path.GetFileName(file) + ": " + flag + " without a try");
                    string between = after[..nextTry];
                    Assert.DoesNotContain("(", between);
                    Assert.DoesNotContain("UpdateInteractionState", between);
                    // The first statement in the try refreshes the interaction state, so a throw still reaches the finally.
                    string inside = after[nextTry..];
                    Assert.True(inside.IndexOf("UpdateInteractionState();", StringComparison.Ordinal) is >= 0 and < 80,
                        System.IO.Path.GetFileName(file) + ": " + flag + " must refresh the interaction state inside its try");
                    Assert.Contains("finally", inside);
                    found++;
                }
            }
        }
        Assert.Equal(8, found);
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
