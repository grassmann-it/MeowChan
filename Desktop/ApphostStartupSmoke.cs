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
    internal const string PlaybackSwitch = "--smoke-playback-webm";
    internal int Seconds { get; }
    internal string? PlaybackFixture { get; }
    internal ApphostWorkflowSmoke? Workflow { get; }
    private bool _passed;

    private ApphostStartupSmoke(int seconds, string? playbackFixture = null, ApphostWorkflowSmoke? workflow = null)
    { Seconds = seconds; PlaybackFixture = playbackFixture; Workflow = workflow; }

    internal static ApphostStartupSmoke? Parse(string[] args, string? profile, string? legacyProfile)
    {
        // Finder and other launchers may supply their own arguments during normal use.
        bool workflow = Array.Exists(args, value => value.StartsWith("--smoke-workflow", StringComparison.Ordinal));
        bool playback = Array.Exists(args, value => value.StartsWith("--smoke-playback", StringComparison.Ordinal));
        if (!workflow && !playback && !Array.Exists(args, value => value.StartsWith("--smoke-startup", StringComparison.Ordinal))) return null;
        int seconds = 30;
        if (workflow ? args.Length != 2 || args[0] != ApphostWorkflowSmoke.Switch : playback ? args.Length != 2 || args[0] != PlaybackSwitch : args.Length != 2 || args[0] != Switch ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 10)
            throw new ArgumentException("Usage: MeowChan --smoke-startup-seconds <1..10> | --smoke-playback-webm <absolute local WebM> | --smoke-workflow <absolute owned descriptor.json>");
        if (playback && (!Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1]) ||
            !string.Equals(Path.GetExtension(args[1]), ".webm", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(args[1]).Length is <= 0 or > 2 * 1024 * 1024 || new FileInfo(args[1]).LinkTarget != null))
            throw new ArgumentException("Playback smoke requires an existing local .webm fixture of at most 2 MiB, without a symlink.");
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile) || !string.IsNullOrEmpty(legacyProfile))
            throw new ArgumentException("Startup smoke requires an absolute MEOWCHAN_PROFILE and no OCHAN_PROFILE override.");
        string full = Path.GetFullPath(profile);
        if (Directory.Exists(full) || File.Exists(full) || new DirectoryInfo(full).LinkTarget != null)
            throw new ArgumentException("Startup smoke requires a new, nonexistent MEOWCHAN_PROFILE directory.");
        return workflow ? new ApphostStartupSmoke(90, workflow: ApphostWorkflowSmoke.Parse(args[1], full))
            : new ApphostStartupSmoke(seconds, playback ? Path.GetFullPath(args[1]) : null);
    }

    internal void Attach(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        // The acceptance check must finish observing disposal after the last native window closes.
        // Normal startup retains the application's existing shutdown policy.
        if (PlaybackFixture != null || Workflow != null) desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
                if (Workflow != null) await main.CompleteApphostWorkflowSmokeAsync(Workflow);
                else if (PlaybackFixture != null) await main.CompleteApphostMediaSmokeAsync(PlaybackFixture);
                else await main.CompleteApphostStartupSmokeAsync(TimeSpan.FromSeconds(Seconds));
                _passed = true;
                if (PlaybackFixture != null || Workflow != null) desktop.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("APPHOST STARTUP FAIL: " + exception);
                desktop.Shutdown(1);
            }
            finally { Workflow?.Dispose(); }
        };
    }

    internal int Complete(int exitCode)
    {
        bool passed = exitCode == 0 && _passed;
        Console.WriteLine(passed ? Workflow != null
            ? "APPHOST WORKFLOW PASS: actual packaged application, offline production commands, fresh process restore, native clipboard and normal shutdown; external handoff boundaries recorded."
            : PlaybackFixture != null
            ? "APPHOST PLAYBACK PASS: verified local media, native decoded/displayed frames, transport UI, navigation, disposal and graceful shutdown completed; no audio or hardware-acceleration claim."
            : "APPHOST STARTUP PASS: main window opened, session ready, UI dwell and graceful shutdown completed."
            : $"APPHOST STARTUP FAIL: exit={exitCode}, lifecycleCompleted={_passed}.");
        return passed ? 0 : 1;
    }
}
