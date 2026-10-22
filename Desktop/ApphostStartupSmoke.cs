using System;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media.Imaging;
using System.Reflection;
using System.Text.Json;
using Avalonia.VisualTree;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>Opt-in acceptance of the actual packaged apphost; never uses a default profile.</summary>
internal sealed class ApphostStartupSmoke
{
    internal const string Switch = "--smoke-startup-seconds";
    internal const string VideoErrorSwitch = "--smoke-video-error";
    internal const string PlaybackSwitch = "--smoke-playback-webm";
    internal int Seconds { get; }
    internal string? PlaybackFixture { get; }
    internal ApphostWorkflowSmoke? Workflow { get; }
    internal ApphostPluginWorkflowSmoke? PluginWorkflow { get; }
    internal LinuxVideoPreflightErrorKind? ExpectedVideoError { get; }
    private readonly string? _errorProfile;
    private string? _errorMessage;
    private double _errorScaling;
    private bool _passed;

    private ApphostStartupSmoke(int seconds, string? playbackFixture = null, ApphostWorkflowSmoke? workflow = null,
        LinuxVideoPreflightErrorKind? expectedVideoError = null, string? errorProfile = null, ApphostPluginWorkflowSmoke? pluginWorkflow = null)
    { PluginWorkflow = pluginWorkflow; Seconds = seconds; PlaybackFixture = playbackFixture; Workflow = workflow; ExpectedVideoError = expectedVideoError; _errorProfile = errorProfile; }

    internal static ApphostStartupSmoke? Parse(string[] args, string? profile, string? legacyProfile)
    {
        // Finder and other launchers may supply their own arguments during normal use.
        bool videoError = Array.Exists(args, value => value.StartsWith("--smoke-video-error", StringComparison.Ordinal));
        bool pluginWorkflow = args.Length > 0 && args[0] == ApphostPluginWorkflowSmoke.Switch;
        bool workflow = Array.Exists(args, value => value.StartsWith("--smoke-workflow", StringComparison.Ordinal));
        bool playback = Array.Exists(args, value => value.StartsWith("--smoke-playback", StringComparison.Ordinal));
        if (!videoError && !workflow && !pluginWorkflow && !playback && !Array.Exists(args, value => value.StartsWith("--smoke-startup", StringComparison.Ordinal))) return null;
        int seconds = 30;
        LinuxVideoPreflightErrorKind expectedError = LinuxVideoPreflightErrorKind.None;
        if (videoError && (args.Length != 2 || args[0] != VideoErrorSwitch ||
            !Enum.TryParse(args[1], out expectedError) || !Enum.IsDefined(expectedError) || expectedError == LinuxVideoPreflightErrorKind.None ||
            args[1] != expectedError.ToString()))
            throw new ArgumentException("Usage: MeowChan --smoke-video-error <SystemBackendUnavailable|SystemBackendUnsupportedVersion|BundleDamaged|InvalidPackageMetadata>");
        if (!videoError && (pluginWorkflow ? args.Length != 2 : workflow ? args.Length != 2 || args[0] != ApphostWorkflowSmoke.Switch : playback ? args.Length != 2 || args[0] != PlaybackSwitch : args.Length != 2 || args[0] != Switch ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 10))
            throw new ArgumentException("Usage: MeowChan --smoke-startup-seconds <1..10> | --smoke-playback-webm <absolute local WebM> | --smoke-workflow <absolute owned descriptor.json> | --smoke-plugin-workflow <absolute owned descriptor.json>");
        if (playback && (!Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1]) ||
            !string.Equals(Path.GetExtension(args[1]), ".webm", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(args[1]).Length is <= 0 or > 2 * 1024 * 1024 || new FileInfo(args[1]).LinkTarget != null))
            throw new ArgumentException("Playback smoke requires an existing local .webm fixture of at most 2 MiB, without a symlink.");
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile) || !string.IsNullOrEmpty(legacyProfile))
            throw new ArgumentException("Startup smoke requires an absolute MEOWCHAN_PROFILE and no OCHAN_PROFILE override.");
        string full = Path.GetFullPath(profile);
        if (Directory.Exists(full) || File.Exists(full) || new DirectoryInfo(full).LinkTarget != null)
            throw new ArgumentException("Startup smoke requires a new, nonexistent MEOWCHAN_PROFILE directory.");
        if (videoError) return new ApphostStartupSmoke(10, expectedVideoError: expectedError, errorProfile: full);
        if (pluginWorkflow) return new ApphostStartupSmoke(90, pluginWorkflow: ApphostPluginWorkflowSmoke.Parse(args[1], full));
        return workflow ? new ApphostStartupSmoke(90, workflow: ApphostWorkflowSmoke.Parse(args[1], full))
            : new ApphostStartupSmoke(seconds, playback ? Path.GetFullPath(args[1]) : null);
    }

    internal void Attach(IClassicDesktopStyleApplicationLifetime desktop, Window window, LinuxVideoPreflightResult? video = null)
    {
        if (ExpectedVideoError is { } expected)
        {
            if (video?.Kind != expected || window is not StartupErrorWindow error)
            {
                Console.Error.WriteLine($"APPHOST VIDEO ERROR FAIL: expected={expected}, actual={video?.Kind}, window={window.GetType().Name}.");
                Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
                return;
            }
            error.Opened += async (_, _) =>
            {
                try
                {
                    // Opened can fire inside lifetime.StartCore; defer acknowledgment
                    // until the native window's opening stack has returned.
                    await Task.Yield();
                    var layoutDeadline = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        error.UpdateLayout();
                        bool ready = error.IsVisible && error.GetVisualDescendants().OfType<SelectableTextBlock>()
                            .Any(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.Bounds.Height > 0) &&
                            error.GetVisualDescendants().OfType<Button>()
                            .Any(button => button.IsEffectivelyVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0);
                        if (ready) break;
                        if (layoutDeadline.Elapsed >= TimeSpan.FromSeconds(5))
                            throw new TimeoutException("Startup error window did not reach visible layout within five seconds.");
                        await Task.Delay(20);
                    }
                    if (!error.IsVisible || desktop.Windows.Any(w => w is MainWindow) ||
                        Directory.Exists(_errorProfile) || File.Exists(_errorProfile))
                        throw new InvalidOperationException("Startup failure opened a session/profile or has no visible error window.");
                    var detail = error.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
                    var exit = error.GetVisualDescendants().OfType<Button>().Single();
                    if (detail.Text != Texts.T(video.MessageKey) || string.IsNullOrWhiteSpace(detail.Text) ||
                        !detail.IsEffectivelyVisible || detail.Bounds.Width <= 0 || detail.Bounds.Height <= 0 ||
                        !exit.IsEffectivelyVisible || exit.Bounds.Width <= 0 || exit.Bounds.Height <= 0 ||
                        !Equals(exit.Content, Texts.T("action.exit")))
                        throw new InvalidOperationException("Startup failure guidance or acknowledgment is not visible/localized.");
                    _errorMessage = detail.Text;
                    _errorScaling = error.RenderScaling;
                    using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(error.Bounds.Width * error.RenderScaling),
                        (int)Math.Ceiling(error.Bounds.Height * error.RenderScaling)), new Vector(96 * error.RenderScaling, 96 * error.RenderScaling)))
                    {
                        bitmap.Render(error);
                        using var png = new FileStream(_errorProfile + ".video-error.png", FileMode.CreateNew, FileAccess.Write);
                        bitmap.Save(png, PngBitmapEncoderOptions.Default);
                    }
                    bool closed = false;
                    error.Closed += (_, _) => closed = true;
                    exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!closed || Directory.Exists(_errorProfile) || File.Exists(_errorProfile))
                        throw new InvalidOperationException("Acknowledgment did not close the error window without profile writes.");
                    _passed = true;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("APPHOST VIDEO ERROR FAIL: " + exception);
                    desktop.Shutdown(1);
                }
            };
            return;
        }
        // The acceptance check must finish observing disposal after the last native window closes.
        // Normal startup retains the application's existing shutdown policy.
        if (PlaybackFixture != null || Workflow != null || PluginWorkflow != null) desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
                if (PluginWorkflow != null) await main.CompleteApphostPluginWorkflowSmokeAsync(PluginWorkflow);
                else if (Workflow != null) await main.CompleteApphostWorkflowSmokeAsync(Workflow);
                else if (PlaybackFixture != null) await main.CompleteApphostMediaSmokeAsync(PlaybackFixture);
                else await main.CompleteApphostStartupSmokeAsync(TimeSpan.FromSeconds(Seconds));
                _passed = true;
                if (PlaybackFixture != null || Workflow != null || PluginWorkflow != null) desktop.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("APPHOST STARTUP FAIL: " + exception);
                desktop.Shutdown(1);
            }
            finally { Workflow?.Dispose(); PluginWorkflow?.Dispose(); }
        };
    }

    internal int Complete(int exitCode)
    {
        if (ExpectedVideoError is { } expected)
        {
            bool accepted = exitCode == 2 && _passed && !Directory.Exists(_errorProfile) && !File.Exists(_errorProfile);
            if (accepted)
            {
                var evidence = new
                {
                    schemaVersion = 1, completed = true, failure = (string?)null,
                    expectedKind = expected.ToString(), observedKind = expected.ToString(),
                    checks = new[] { "preflight-real", "error-window-visible", "localized-hint", "exit-button", "session-not-created" },
                    processId = Environment.ProcessId, workingDirectory = Environment.CurrentDirectory,
                    profile = _errorProfile,
                    assemblyInformationalVersion = typeof(ApphostStartupSmoke).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    renderScaling = _errorScaling, message = _errorMessage,
                    windowVisible = true, profileAbsent = true, normalExitButtonClicked = true, exitCode = exitCode
                };
                try
                {
                    using var json = new FileStream(_errorProfile + ".video-error.json", FileMode.CreateNew, FileAccess.Write);
                    JsonSerializer.Serialize(json, evidence, new JsonSerializerOptions { WriteIndented = true });
                    Console.WriteLine("APPHOST VIDEO ERROR PASS");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("APPHOST VIDEO ERROR FAIL: evidence could not be saved: " + exception);
                    return 1;
                }
            }
            else Console.WriteLine($"APPHOST VIDEO ERROR FAIL: expected={expected}, exit={exitCode}, lifecycleCompleted={_passed}.");
            return accepted ? 2 : 1;
        }
        bool passed = exitCode == 0 && _passed;
        Console.WriteLine(passed ? PluginWorkflow != null
            ? "APPHOST PLUGIN WORKFLOW PASS"
            : Workflow != null
            ? "APPHOST WORKFLOW PASS: actual packaged application, offline production commands, fresh process restore, native clipboard and normal shutdown; external handoff boundaries recorded."
            : PlaybackFixture != null
            ? "APPHOST PLAYBACK PASS: verified local media, native decoded/displayed frames, transport UI, navigation, disposal and graceful shutdown completed; no audio or hardware-acceleration claim."
            : "APPHOST STARTUP PASS: main window opened, session ready, UI dwell and graceful shutdown completed."
            : $"APPHOST STARTUP FAIL: exit={exitCode}, lifecycleCompleted={_passed}.");
        return passed ? 0 : 1;
    }
}
