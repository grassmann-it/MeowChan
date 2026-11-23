using Avalonia;
using System;
using Serilog;
using MeowChan.Desktop;

namespace MeowChan;

internal static partial class Program
{
    // Optional development host; the call and declaration are erased when no implementation is compiled.
    static partial void RunDevelopmentHost(string[] args, ref int? exitCode);

    [STAThread]
    public static int Main(string[] args)
    {
        int? developmentExitCode = null;
        RunDevelopmentHost(args, ref developmentExitCode);
        if (developmentExitCode is int completed) return completed;

        string? startupFolder;
        try
        {
            startupFolder = StartupFolderSession.ReadPositionalPath(args);
            // --profile <folder> chooses the profile like MEOWCHAN_PROFILE (PR1); it is removed before the remaining
            // arguments are read.
            args = ProfileCommandLine.Extract(args, out string? profile);
            // --media-folder <folder> comes from the "New profile" dialog: the new window adds it as a read-only folder source on
            // its first start. A problem with it never stops the start; the window explains it (see StartupMediaFolderRegistration).
            args = StartupMediaFolderCommandLine.Extract(args, hasProfile: profile != null, out StartupMediaFolderArgument? mediaFolder);
            Config.CommandLineProfile = profile;
            ProfileStartup.MediaFolder = mediaFolder;
            ProfileStartup.RecordsInProfileList = !ProfileStartup.SelectedByEnvironment(
                Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE"), Environment.GetEnvironmentVariable("OCHAN_PROFILE"), profile);
            if (startupFolder != null)
            {
                args = Array.Empty<string>();
                ProfileStartup.RecordsInProfileList = false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        using StartupFolderSession? folderSession = startupFolder == null ? null : new StartupFolderSession(startupFolder);
        ProfileStartup.FolderSession = folderSession;
        // The desktop host owns logging; constructing a library session never changes it.
        var logging = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console(DesktopLog.ConsoleFormatter);
        Log.Logger = (folderSession == null ? DesktopLog.Configure(logging, DesktopLog.ResolveDirectory()) : logging).CreateLogger();
        UnhandledErrors.Install(failHard: false);
        int exitCode;
        bool removed;
        try
        {
            exitCode = BuildAvaloniaApp().AfterSetup(_ => UnhandledErrors.AttachDispatcher())
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            removed = folderSession?.RemoveTemporaryProfile() != false;
            ProfileStartup.FolderSession = null;
            Log.CloseAndFlush();
        }
        return !removed && exitCode == 0 ? 2 : exitCode;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(MeowChan.Desktop.ReaderFonts.ManagerOptions())
            .LogToTrace();
}
