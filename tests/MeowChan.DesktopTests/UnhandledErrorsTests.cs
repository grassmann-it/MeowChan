using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Testing;
using Serilog;
using Serilog.Core;

namespace MeowChan.DesktopTests;

// Presenter and the UI dispatcher are process-wide: these cases must not run in parallel with each other.
[Collection(nameof(UnhandledErrorsTests))]
public sealed class UnhandledErrorsTests
{
    [Fact]
    public void AsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning()
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
    public void SmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode()
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
    public void LocalRecoveryLogsAndShowsButNeverOutOfMemory()
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

    // The UI dispatcher belongs to the thread that first used it, and xunit moves tests between threads.
    // One dedicated thread runs every dispatcher body of this class.
    private static readonly BlockingCollection<Action> UiWork = StartUiThread();

    private static BlockingCollection<Action> StartUiThread()
    {
        BlockingCollection<Action> work = new();
        Thread thread = new(() => { foreach (Action action in work.GetConsumingEnumerable()) action(); })
        { IsBackground = true, Name = nameof(UnhandledErrorsTests) };
        thread.Start();
        return work;
    }

    private static void RunOnDispatcher(UnhandledErrors errors, Action body)
    {
        ExceptionDispatchInfo? failure = null;
        using ManualResetEventSlim done = new();
        UiWork.Add(() =>
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
        });
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "The dispatcher body did not finish.");
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
