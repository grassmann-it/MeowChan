using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Testing;
using Serilog;
using Serilog.Core;

namespace MeowChan.DesktopTests;

// Presenter and the UI dispatcher are process-wide: Avalonia's UIThread belongs to whichever
// test initializes it first, and xunit reorders and parallelizes classes. The three dispatcher
// cases therefore each run in their own fresh vstest process, following the MediaStoreTests
// lease-probe pattern: a dedicated child fact, never the same test name re-filtered.
public sealed class UnhandledErrorsTests
{
    private const string ProbeVariable = "MEOWCHAN_UNHANDLED_ERRORS_PROBE";

    [Fact]
    public void AsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning() =>
        RunInIsolatedProcessAsync(nameof(ProbeAsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning));

    [Fact]
    public void ProbeAsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning()
    {
        if (Environment.GetEnvironmentVariable(ProbeVariable) != nameof(ProbeAsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning)) return;
        AsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunningCore();
    }

    private static void AsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunningCore()
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-unhandled-");
        List<Exception> shown = new();
        int? exit = null;
        bool later = false;
        try
        {
            using (Logger log = DesktopLog.Configure(new LoggerConfiguration(), root).CreateLogger())
            {
                UnhandledErrors errors = new(log, failHard: false, code => exit = code);
                RunOnDispatcher(errors, () =>
                {
                    UnhandledErrors.Presenter = shown.Add;
                    FailingHandler();
                    Dispatcher.UIThread.RunJobs();
                    // The dispatcher still runs work after the failure: the app did not end.
                    Dispatcher.UIThread.Post(() => later = true);
                    Dispatcher.UIThread.RunJobs();
                });
            }
            Exception error = Assert.Single(shown);
            Assert.Equal("handler broke", error.Message);
            Assert.Null(exit);
            Assert.True(later);
            string text = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "meowchan-*.log")));
            Assert.Contains("Unhandled exception on the UI thread.", text);
            Assert.Contains("handler broke", text);
            Assert.Contains(nameof(FailingHandler), text); // The full stack trace, not only the message.
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode() =>
        RunInIsolatedProcessAsync(nameof(ProbeSmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode));

    [Fact]
    public void ProbeSmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode()
    {
        if (Environment.GetEnvironmentVariable(ProbeVariable) != nameof(ProbeSmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode)) return;
        SmokeModeDoesNotSwallowAndEndsWithNonZeroExitCodeCore();
    }

    private static void SmokeModeDoesNotSwallowAndEndsWithNonZeroExitCodeCore()
    {
        List<Exception> shown = new();
        int? exit = null;
        UnhandledErrors errors = new(Logger.None, failHard: true, code => exit = code);
        RunOnDispatcher(errors, () =>
        {
            UnhandledErrors.Presenter = shown.Add;
            FailingHandler();
            // Not marked handled: without the injected exit the dispatcher rethrows and the process ends.
            Assert.Throws<InvalidOperationException>(() => Dispatcher.UIThread.RunJobs());
        });
        Assert.Equal(UnhandledErrors.SmokeExitCode, exit);
        Assert.NotEqual(0, exit);
        Assert.Empty(shown);
        Assert.False(errors.Recover(new InvalidOperationException("local"), "test"));
    }

    [Fact]
    public void LocalRecoveryLogsAndShowsButNeverOutOfMemory() =>
        RunInIsolatedProcessAsync(nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory));

    [Fact]
    public void ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory()
    {
        if (Environment.GetEnvironmentVariable(ProbeVariable) != nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory)) return;
        LocalRecoveryLogsAndShowsButNeverOutOfMemoryCore();
    }

    private static void LocalRecoveryLogsAndShowsButNeverOutOfMemoryCore()
    {
        List<Exception> shown = new();
        UnhandledErrors errors = new(Logger.None, failHard: false, _ => throw new InvalidOperationException("no exit expected"));
        RunOnDispatcher(errors, () =>
        {
            UnhandledErrors.Presenter = shown.Add;
            Assert.True(errors.Recover(new InvalidOperationException("local"), "test"));
            Assert.False(errors.Recover(new OutOfMemoryException(), "test"));
            Dispatcher.UIThread.RunJobs();
        });
        Assert.Equal("local", Assert.Single(shown).Message);
    }

    [Fact]
    public async Task GalleryDetachFinishesAfterFailedNativeCleanup()
    {
        VideoPlaybackLifetime lifetime = new(_ => Task.FromException(new IOException("native cleanup failed")), () => Task.CompletedTask);
        VideoPlaybackLifetime.ShowRequest show = lifetime.AcceptShow(CancellationToken.None)!;
        lifetime.FinishShow(show);
        Task stop = lifetime.StopAsync();
        // The precondition of the old crash: the awaited stop is faulted.
        await Assert.ThrowsAsync<IOException>(() => stop);
        Assert.True(lifetime.HasCleanupFailure);
        await GalleryVideoPlayer.StopAfterDetachAsync(() => lifetime.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void LogFileRotatesBySizeAndKeepsABoundedNumberOfFiles()
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-log-roll-");
        try
        {
            using (Logger log = DesktopLog.Configure(new LoggerConfiguration(), root, fileSizeLimitBytes: 512).CreateLogger())
                for (int index = 0; index < 400; index++) log.Information("Line {Index} {Padding}", index, new string('x', 100));
            string[] files = Directory.GetFiles(root, "meowchan-*.log");
            Assert.True(files.Length > 1, "The log did not roll over at its size limit.");
            Assert.True(files.Length <= DesktopLog.RetainedFileCount, $"{files.Length} log files were retained.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NamedAsyncVoidHandlersAreGuarded()
    {
        static string Source(string path) => ReadRepositorySource(path);
        Assert.Contains("await StopAfterDetachAsync(StopAsync);", Source("Desktop/GalleryVideoPlayer.cs"));
        Assert.Contains("UnhandledErrors.TryRecover(error, \"media.grid.key\")", Source("Desktop/Views/Media/MediaPage.cs"));
        Assert.Equal(2, Count(Source("Desktop/Views/Media/MediaSelectionTagDialog.cs"), "UnhandledErrors.TryRecover(error, \"media.selection.tags.add\")"));
        Assert.Contains("UnhandledErrors.TryRecover(error, \"inspector.gallery\")", Source("Desktop/Shell/MainWindow.ReaderActions.cs"));
        string program = Source("Program.cs");
        Assert.Contains("UnhandledErrors.Install(failHard: StartupSmoke != null);", program);
        Assert.Contains("AfterSetup(_ => UnhandledErrors.AttachDispatcher())", program);
    }

    private static async void FailingHandler()
    {
        await Task.Yield();
        throw new InvalidOperationException("handler broke");
    }

    /// <summary>
    /// Runs one dispatcher case in a fresh vstest process so that no other test can have
    /// initialized the process-wide Avalonia UIThread first. The child runs the already-built
    /// assembly; nothing is built or restored here. The parent asserts on exit code and captured
    /// output, and kills the whole tree on timeout.
    /// </summary>
    private static void RunInIsolatedProcessAsync(string probe)
    {
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(UnhandledErrorsTests).Assembly.Location);
        start.ArgumentList.Add($"--TestCaseFilter:FullyQualifiedName={typeof(UnhandledErrorsTests).FullName}.{probe}");
        start.Environment[ProbeVariable] = probe;
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated dispatcher test.");
        Task<string> output = child.StandardOutput.ReadToEndAsync(), errors = child.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        try
        {
            if (!child.WaitForExit(90_000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                Assert.Fail($"Isolated dispatcher test {probe} timed out and its process tree was killed.\n{output.Result}\n{errors.Result}");
            }
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            throw;
        }
        Assert.True(child.ExitCode == 0, $"Isolated dispatcher test {probe} failed.\n{output.Result}\n{errors.Result}");
    }

    /// <summary>
    /// The Avalonia UIThread belongs to the thread that first touches it. Inside the isolated
    /// child process nothing else has, so a dedicated thread owns it for the duration of the body.
    /// </summary>
    private static void RunOnDispatcher(UnhandledErrors errors, Action body)
    {
        ExceptionDispatchInfo? failure = null;
        using ManualResetEventSlim done = new();
        Thread thread = new(() =>
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            Action<Exception>? presenter = UnhandledErrors.Presenter;
            try
            {
                Assert.True(Dispatcher.UIThread.CheckAccess(), "Another thread owns the UI dispatcher of this test process.");
                Dispatcher.UIThread.UnhandledException += errors.OnDispatcherException;
                SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
                body();
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally
            {
                Dispatcher.UIThread.UnhandledException -= errors.OnDispatcherException;
                SynchronizationContext.SetSynchronizationContext(previous);
                UnhandledErrors.Presenter = presenter;
                done.Set();
            }
        })
        { IsBackground = true, Name = "UnhandledErrorsTests.Dispatcher" };
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "The dispatcher body did not finish.");
        failure?.Throw();
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string ReadRepositorySource(string path, [CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "..", "..", path)));
}
