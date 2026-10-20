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
            StartupSmoke = ApphostStartupSmoke.Parse(args, Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE"),
                Environment.GetEnvironmentVariable("OCHAN_PROFILE"));
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
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console().CreateLogger();
        try
        {
            int exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(StartupSmoke == null ? args : Array.Empty<string>());
            return StartupSmoke?.Complete(exitCode) ?? exitCode;
        }
        finally { Log.CloseAndFlush(); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
