using System;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace MeowChan.Desktop;

/// <summary>Opt-in acceptance of the actual packaged apphost; never uses a default profile.</summary>
internal sealed class ApphostStartupSmoke
{
    internal const string Switch = "--smoke-startup-seconds";
    internal int Seconds { get; }
    private bool _passed;

    private ApphostStartupSmoke(int seconds) => Seconds = seconds;

    internal static ApphostStartupSmoke? Parse(string[] args, string? profile, string? legacyProfile)
    {
        // Finder and other launchers may supply their own arguments during normal use.
        if (!Array.Exists(args, value => value.StartsWith("--smoke-startup", StringComparison.Ordinal))) return null;
        if (args.Length != 2 || args[0] != Switch ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 10)
            throw new ArgumentException("Usage: MeowChan --smoke-startup-seconds <1..10>");
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile) || !string.IsNullOrEmpty(legacyProfile))
            throw new ArgumentException("Startup smoke requires an absolute MEOWCHAN_PROFILE and no OCHAN_PROFILE override.");
        string full = Path.GetFullPath(profile);
        if (Directory.Exists(full) || File.Exists(full) || new DirectoryInfo(full).LinkTarget != null)
            throw new ArgumentException("Startup smoke requires a new, nonexistent MEOWCHAN_PROFILE directory.");
        return new ApphostStartupSmoke(seconds);
    }

    internal void Attach(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        if (window is not MainWindow main)
        {
            Console.Error.WriteLine("APPHOST STARTUP FAIL: startup created an error window.");
            Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
            return;
        }
        main.Opened += async (_, _) =>
        {
            try
            {
                await main.CompleteApphostStartupSmokeAsync(TimeSpan.FromSeconds(Seconds));
                _passed = true;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("APPHOST STARTUP FAIL: " + exception);
                desktop.Shutdown(1);
            }
        };
    }

    internal int Complete(int exitCode)
    {
        bool passed = exitCode == 0 && _passed;
        Console.WriteLine(passed ? "APPHOST STARTUP PASS: main window opened, session ready, UI dwell and graceful shutdown completed."
            : $"APPHOST STARTUP FAIL: exit={exitCode}, lifecycleCompleted={_passed}.");
        return passed ? 0 : 1;
    }
}
