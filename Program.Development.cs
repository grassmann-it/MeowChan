#if DEBUG
using System;
using System.Threading;
using Avalonia;
using MeowChan.Desktop;
using Serilog;

namespace MeowChan;

internal static partial class Program
{
    // Development-only implementation of the single startup hook. All acceptance policy lives here,
    // not in the production entry point. Ordinary Debug launches still take the production path.
    static partial void RunDevelopmentHost(string[] args, ref int? exitCode)
    {
        ApphostStartupSmoke? smoke;
        try
        {
            string[] remaining = ProfileCommandLine.Extract(args, out string? profile);
            remaining = StartupMediaFolderCommandLine.Extract(remaining, hasProfile: profile != null,
                out StartupMediaFolderArgument? mediaFolder);
            smoke = ApphostStartupSmoke.Parse(remaining, Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE"),
                Environment.GetEnvironmentVariable("OCHAN_PROFILE"));
            if (smoke == null) return;
            if (profile != null || mediaFolder != null)
                throw new ArgumentException("--profile and --media-folder cannot be combined with a startup smoke; use MEOWCHAN_PROFILE.");
        }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
            exitCode = 2;
            return;
        }

        Config.CommandLineProfile = null;
        ProfileStartup.MediaFolder = null;
        ProfileStartup.RecordsInProfileList = false;
        bool previousExitAllowed = ShutdownExit.ProcessExitAllowed;
        var previousFactory = App.SmokeMainWindowFactory;
        var previousReady = App.StartupWindowReady;
        ShutdownExit.ProcessExitAllowed = false;
        App.SmokeMainWindowFactory = () => smoke.IntegrityProcess is { } policy ? new MainWindow(policy.CreateSession())
            : smoke.Workflow is { } workflow ? new MainWindow(workflow.CreateSession())
            : smoke.PluginWorkflow is { } plugins ? new MainWindow(plugins.CreateSession()) : new MainWindow();
        App.StartupWindowReady = smoke.Attach;
        using Timer deadline = new(_ =>
        {
            Console.Error.WriteLine("APPHOST STARTUP FAIL: lifecycle watchdog expired.");
            Environment.Exit(124);
        }, null, TimeSpan.FromSeconds(smoke.Seconds + 30), Timeout.InfiniteTimeSpan);
        Log.Logger = DesktopLog.Configure(new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console(DesktopLog.ConsoleFormatter),
            DesktopLog.ResolveDirectory()).CreateLogger();
        UnhandledErrors.Install(failHard: true);
        try
        {
            exitCode = smoke.Complete(BuildAvaloniaApp().AfterSetup(_ => UnhandledErrors.AttachDispatcher())
                .StartWithClassicDesktopLifetime(Array.Empty<string>()));
        }
        finally
        {
            App.SmokeMainWindowFactory = previousFactory;
            App.StartupWindowReady = previousReady;
            ShutdownExit.ProcessExitAllowed = previousExitAllowed;
            Log.CloseAndFlush();
        }
    }
}
#endif
