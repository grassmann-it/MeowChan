using Avalonia;
using System;
using Serilog;
using System.Threading;
using MeowChan.Desktop;

namespace MeowChan;

internal static class Program
{
    internal static ApphostStartupSmoke? StartupSmoke { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // --profile <folder> chooses the profile like MEOWCHAN_PROFILE (PR1); it is removed before the remaining
            // arguments are read. Smokes keep their own contract (MEOWCHAN_PROFILE only).
            args = ProfileCommandLine.Extract(args, out string? profile);
            // --media-folder <folder> comes from the "New profile" dialog: the new window adds it as a read-only folder source on
            // its first start. A problem with it never stops the start; the window explains it (see StartupMediaFolderRegistration).
            args = StartupMediaFolderCommandLine.Extract(args, hasProfile: profile != null, out StartupMediaFolderArgument? mediaFolder);
            StartupSmoke = ApphostStartupSmoke.Parse(args, Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE"),
                Environment.GetEnvironmentVariable("OCHAN_PROFILE"));
            if (profile != null && StartupSmoke != null)
                throw new ArgumentException("--profile cannot be combined with a startup smoke; smokes use MEOWCHAN_PROFILE.");
            if (mediaFolder != null && StartupSmoke != null)
                throw new ArgumentException("--media-folder cannot be combined with a startup smoke.");
            Config.CommandLineProfile = profile;
            ProfileStartup.MediaFolder = mediaFolder;
            ProfileStartup.RecordsInProfileList = StartupSmoke == null && !ProfileStartup.SelectedByEnvironment(
                Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE"), Environment.GetEnvironmentVariable("OCHAN_PROFILE"), profile);
        }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        using Timer? deadline = StartupSmoke == null ? null : new Timer(_ =>
        {
            Console.Error.WriteLine("APPHOST STARTUP FAIL: lifecycle watchdog expired.");
            Environment.Exit(124);
        }, null, TimeSpan.FromSeconds(StartupSmoke.Seconds + 30), Timeout.InfiniteTimeSpan);
        // The desktop host owns logging; constructing a library session never changes it.
        Log.Logger = DesktopLog.Configure(new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console(DesktopLog.ConsoleFormatter),
            DesktopLog.ResolveDirectory()).CreateLogger();
        // Smokes must fail hard: nothing they raise may be swallowed into a green result.
        UnhandledErrors.Install(failHard: StartupSmoke != null);
        try
        {
            int exitCode = BuildAvaloniaApp().AfterSetup(_ => UnhandledErrors.AttachDispatcher())
                .StartWithClassicDesktopLifetime(StartupSmoke == null ? args : Array.Empty<string>());
            return StartupSmoke?.Complete(exitCode) ?? exitCode;
        }
        finally { Log.CloseAndFlush(); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(MeowChan.Desktop.ReaderFonts.ManagerOptions())
            .LogToTrace();
}
