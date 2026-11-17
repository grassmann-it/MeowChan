using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Testing;
using Serilog;
using Serilog.Core;
using Xunit.Sdk;

namespace MeowChan.DesktopTests;

// Presenter and the UI dispatcher are process-wide: Avalonia's UIThread belongs to whichever
// test initializes it first, and xunit reorders and parallelizes classes. The three dispatcher
// cases therefore each run in their own fresh vstest process, following the MediaStoreTests
// lease-probe pattern: a dedicated child fact, never the same test name re-filtered.
public sealed class UnhandledErrorsTests
{
    private const string ProbeVariable = "MEOWCHAN_UNHANDLED_ERRORS_PROBE";
    private const string ProbeCompletionVariable = "MEOWCHAN_UNHANDLED_ERRORS_PROBE_COMPLETION";

    [Fact]
    public Task AsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning() =>
        RunInIsolatedProcessAsync(nameof(ProbeAsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning));

    [Fact]
    public void ProbeAsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning() =>
        RunProbe(nameof(ProbeAsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunning), AsyncVoidHandlerFailureIsLoggedShownAndTheAppKeepsRunningCore,
            Environment.GetEnvironmentVariable(ProbeVariable), Environment.GetEnvironmentVariable(ProbeCompletionVariable));

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
    public Task SmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode() =>
        RunInIsolatedProcessAsync(nameof(ProbeSmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode));

    [Fact]
    public void ProbeSmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode() =>
        RunProbe(nameof(ProbeSmokeModeDoesNotSwallowAndEndsWithNonZeroExitCode), SmokeModeDoesNotSwallowAndEndsWithNonZeroExitCodeCore,
            Environment.GetEnvironmentVariable(ProbeVariable), Environment.GetEnvironmentVariable(ProbeCompletionVariable));

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
    public Task LocalRecoveryLogsAndShowsButNeverOutOfMemory() =>
        RunInIsolatedProcessAsync(nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory));

    [Fact]
    public void ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory() =>
        RunProbe(nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory), LocalRecoveryLogsAndShowsButNeverOutOfMemoryCore,
            Environment.GetEnvironmentVariable(ProbeVariable), Environment.GetEnvironmentVariable(ProbeCompletionVariable));

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
        Assert.Contains("UnhandledErrors.Install(failHard: false);", program);
        Assert.Contains("UnhandledErrors.Install(failHard: true);", Source("Program.Development.cs"));
        Assert.Contains("AfterSetup(_ => UnhandledErrors.AttachDispatcher())", program);
    }

    private static async void FailingHandler()
    {
        await Task.Yield();
        throw new InvalidOperationException("handler broke");
    }

    // Probe facts are inert in the ordinary suite. A zero vstest exit code is not proof of
    // execution (an empty filter can also succeed), so only a completed body writes the receipt.
    private static void RunProbe(string probe, Action body, string? selectedProbe, string? completionPath)
    {
        if (selectedProbe != probe) return;
        Assert.False(string.IsNullOrEmpty(completionPath), "The selected dispatcher probe needs a parent-owned completion path.");
        body();
        File.WriteAllText(completionPath!, probe);
    }

    private static ProcessStartInfo CreateProbeStartInfo(string probe, string completionPath, string? activeProbe)
    {
        // Fail before Process.Start even if a future broken filter selects a parent fact in a child.
        Assert.True(string.IsNullOrEmpty(activeProbe), "An isolated dispatcher probe must not launch another probe process.");
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
        start.Environment[ProbeCompletionVariable] = completionPath;
        return start;
    }

    private static void AssertProbeCompleted(int exitCode, string probe, string completionPath, string output, string errors)
    {
        Assert.True(exitCode == 0, $"Isolated dispatcher test {probe} exited with {exitCode}.\n{output}\n{errors}");
        Assert.True(File.Exists(completionPath), $"Isolated dispatcher test {probe} did not complete its assertions.\n{output}\n{errors}");
        Assert.Equal(probe, File.ReadAllText(completionPath));
    }

    /// <summary>
    /// Runs one dispatcher case in a fresh vstest process using the already-built assembly.
    /// The unchanged 90s deadline covers both process exit and output drainage; cleanup reaping
    /// is bounded separately instead of waiting forever. No restore/build happens here.
    /// </summary>
    private static async Task RunInIsolatedProcessAsync(string probe)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-unhandled-probe-");
        try
        {
            string completionPath = Path.Combine(root, "completed");
            ProcessStartInfo start = CreateProbeStartInfo(probe, completionPath, Environment.GetEnvironmentVariable(ProbeVariable));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated dispatcher test.");
            Task<string> output = child.StandardOutput.ReadToEndAsync(timeout.Token), errors = child.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await child.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(output, errors).WaitAsync(timeout.Token);
                AssertProbeCompleted(child.ExitCode, probe, completionPath, await output, await errors);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Isolated dispatcher test {probe} exceeded its 90s process/output deadline; killing any running process tree.\n{Captured(output)}\n{Captured(errors)}");
            }
            finally
            {
                if (!child.HasExited)
                {
                    try { child.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (child.HasExited) { } // Exit raced the kill.
                    catch (System.ComponentModel.Win32Exception) when (child.HasExited) { }
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }

        static string Captured(Task<string> text) => text.IsCompletedSuccessfully ? text.Result : "<output incomplete at deadline>";
    }

    [Fact]
    public void SelectedProbeWritesItsReceiptOnlyAfterItsAssertionsComplete()
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-unhandled-receipt-");
        try
        {
            string completionPath = Path.Combine(root, "completed");
            const string probe = nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory);
            bool ran = false;
            RunProbe(probe, () => { Assert.False(File.Exists(completionPath)); ran = true; }, probe, completionPath);
            Assert.True(ran);
            AssertProbeCompleted(0, probe, completionPath, "", "");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("another-probe")]
    public void UnselectedProbeCannotSupplyACompletionReceipt(string? selectedProbe)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-unhandled-receipt-");
        try
        {
            string completionPath = Path.Combine(root, "completed");
            const string probe = nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory);
            bool ran = false;
            RunProbe(probe, () => ran = true, selectedProbe, completionPath);
            Assert.False(ran);
            Assert.False(File.Exists(completionPath));
            Assert.ThrowsAny<XunitException>(() => AssertProbeCompleted(0, probe, completionPath, "", ""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailedProbeCannotSupplyACompletionReceipt()
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-unhandled-receipt-");
        try
        {
            string completionPath = Path.Combine(root, "completed");
            const string probe = nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory);
            Assert.Throws<InvalidOperationException>(() => RunProbe(probe, () => throw new InvalidOperationException("probe failed"), probe, completionPath));
            Assert.False(File.Exists(completionPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SelectedProbeRequiresACompletionPathBeforeRunning()
    {
        bool ran = false;
        Assert.ThrowsAny<XunitException>(() => RunProbe("probe", () => ran = true, "probe", null));
        Assert.False(ran);
    }

    [Theory]
    [InlineData(0, "another-probe")]
    [InlineData(1, "probe")]
    public void WrongReceiptOrFailedChildCannotPass(int exitCode, string receipt)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-unhandled-receipt-");
        try
        {
            string completionPath = Path.Combine(root, "completed");
            File.WriteAllText(completionPath, receipt);
            Assert.ThrowsAny<XunitException>(() => AssertProbeCompleted(exitCode, "probe", completionPath, "", ""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory))]
    [InlineData("another-probe")]
    public void ChildCannotLaunchAnotherProbeProcess(string activeProbe) =>
        Assert.ThrowsAny<XunitException>(() => CreateProbeStartInfo("probe", "unused", activeProbe));

    [Fact]
    public void ChildStartInfoSelectsOnlyItsDedicatedProbeAndSuppliesTheReceiptPath()
    {
        const string probe = nameof(ProbeLocalRecoveryLogsAndShowsButNeverOutOfMemory);
        ProcessStartInfo start = CreateProbeStartInfo(probe, "receipt-path", null);
        Assert.Equal(new[] { "vstest", typeof(UnhandledErrorsTests).Assembly.Location,
            $"--TestCaseFilter:FullyQualifiedName={typeof(UnhandledErrorsTests).FullName}.{probe}" }, start.ArgumentList);
        Assert.Equal(probe, start.Environment[ProbeVariable]);
        Assert.Equal("receipt-path", start.Environment[ProbeCompletionVariable]);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }

    /// <summary>
    /// The Avalonia UIThread belongs to the thread that first touches it. Inside the isolated
    /// child process nothing else has, so a dedicated thread owns it for the duration of the body.
    /// </summary>
    private static void RunOnDispatcher(UnhandledErrors errors, Action body)
    {
        ExceptionDispatchInfo? failure = null;
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
            }
        })
        { IsBackground = true, Name = "UnhandledErrorsTests.Dispatcher" };
        thread.Start();
        // A timed-out thread must not later signal an already-disposed completion event.
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The dispatcher body did not finish.");
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
