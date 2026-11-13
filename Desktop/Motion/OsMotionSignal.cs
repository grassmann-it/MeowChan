using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;
using Tmds.DBus.Protocol;

namespace MeowChan.Desktop.Motion;

/// <summary>What the last attempt to read the operating system's reduce-motion signal found.</summary>
internal enum OsMotionState
{
    /// <summary>No read has happened yet (before startup, or a platform without a reader).</summary>
    NotRead,
    /// <summary>The system says: reduce motion.</summary>
    DetectedOn,
    /// <summary>The system says: motion is fine.</summary>
    DetectedOff,
    /// <summary>The signal could not be read; <see cref="OsMotionStatus.Reason"/> says why. Motion then follows the setting.</summary>
    Unavailable
}

/// <summary>Outcome of one signal read: the state, where it came from and, when unavailable, a short reason (no paths).</summary>
internal sealed record OsMotionStatus(OsMotionState State, string Source, string? Reason = null)
{
    internal static readonly OsMotionStatus None = new(OsMotionState.NotRead, "none");

    /// <summary>True or false when the system answered, null when it did not.</summary>
    internal bool? Reduced => State switch { OsMotionState.DetectedOn => true, OsMotionState.DetectedOff => false, _ => null };

    internal static OsMotionStatus FromValue(string source, bool reduced) =>
        new(reduced ? OsMotionState.DetectedOn : OsMotionState.DetectedOff, source);

    internal static OsMotionStatus Unavailable(string source, string reason) =>
        new(OsMotionState.Unavailable, source, Shorten(reason));

    /// <summary>One line for logs and the diagnostics view: tells "signal not read" apart from "signal says off".</summary>
    public override string ToString() => State switch
    {
        OsMotionState.DetectedOn => $"reduce motion is ON (read from {Source})",
        OsMotionState.DetectedOff => $"reduce motion is OFF (read from {Source})",
        OsMotionState.Unavailable => $"signal unavailable ({Source}): {Reason}; the Animations setting decides",
        _ => "not read yet"
    };

    // Exception messages can carry file names; keep one short line only, without the home directory (the reason is shown in
    // Diagnostics, whose platform rows a tester pastes into reports). Sanitized before cutting, so no path is split.
    private static string Shorten(string reason)
    {
        string line = PlatformSummary.Sanitize(reason.Split('\n', 2)[0]).Trim();
        return line.Length <= 160 ? line : line[..160] + "...";
    }
}

/// <summary>
/// The operating system's "reduce motion" signal (Avalonia 12 does not expose one). Every read is best effort: no signal is
/// the same as "motion allowed", and nothing here may fail startup.
/// <list type="bullet">
/// <item>Windows: <c>SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)</c>, read at start and whenever a window is activated.</item>
/// <item>macOS: <c>NSWorkspace.accessibilityDisplayShouldReduceMotion</c>, read at start and whenever a window is activated.</item>
/// <item>Linux: the settings portal's <c>org.gnome.desktop.interface enable-animations</c> (read once, then watched through the
/// portal's <c>SettingChanged</c> signal on the session bus), and KDE's <c>AnimationDurationFactor=0</c> in kdeglobals at start.</item>
/// </list>
/// </summary>
internal static class OsMotionSignal
{
    private static DBusConnection? _portal;
    private static OsMotionStatus _status = OsMotionStatus.None;

    /// <summary>The outcome of the latest read; shown in Diagnostics and written to the log whenever it changes.</summary>
    internal static OsMotionStatus Status => Volatile.Read(ref _status);

    /// <summary>
    /// Stores <paramref name="status"/> and logs it once per change (a window activation re-reads Windows and macOS often, so
    /// an unchanged answer stays silent). Returns whether it changed.
    /// </summary>
    internal static bool Record(OsMotionStatus status)
    {
        OsMotionStatus previous = Interlocked.Exchange(ref _status, status);
        if (previous == status) return false;
        if (status.State == OsMotionState.Unavailable)
            Log.Warning("OS reduce-motion signal: {OsMotionState} via {OsMotionSource}: {OsMotionReason}", status.State, status.Source, status.Reason);
        else Log.Information("OS reduce-motion signal: {OsMotionState} via {OsMotionSource}", status.State, status.Source);
        return true;
    }

    /// <summary>A later failure (the portal) never hides an answer another source already gave.</summary>
    private static void RecordUnavailableUnlessDetected(string source, string reason)
    {
        if (Status.Reduced == null) Record(OsMotionStatus.Unavailable(source, reason));
    }

    internal static void ResetForTests() => Volatile.Write(ref _status, OsMotionStatus.None);

    /// <summary>Reads the signal now and keeps <paramref name="changed"/> informed where the platform reports changes.</summary>
    internal static void Start(Action<bool> changed)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (ReadKdeGlobals() is { } kde)
                {
                    Record(OsMotionStatus.FromValue("kdeglobals", kde));
                    changed(kde);
                }
                _ = WatchPortalAsync(changed);
            }
            else
            {
                if (ReadNow() is { } reduced) changed(reduced);
                // The system setting is not observable from here on Windows and macOS; a window activation is the cheap re-read.
                Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<Avalonia.Controls.Window>((window, _) =>
                    window.Activated += (_, _) =>
                    {
                        if (ReadNow() is { } now) changed(now);
                    });
            }
        }
        catch (Exception exception) { Record(OsMotionStatus.Unavailable("startup", exception.GetType().Name + ": " + exception.Message)); }
    }

    /// <summary>The Windows or macOS answer; null when it cannot be read (the reason is in <see cref="Status"/> and the log).</summary>
    internal static bool? ReadNow()
    {
        OsMotionStatus status = ReadPlatform();
        Record(status);
        return status.Reduced;
    }

    /// <summary>Reads the Windows or macOS signal once without recording it; never throws.</summary>
    internal static OsMotionStatus ReadPlatform()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (SystemParametersInfo(SpiGetClientAreaAnimation, 0, out int enabled, 0))
                    return OsMotionStatus.FromValue("SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)", enabled == 0);
                return OsMotionStatus.Unavailable("SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)",
                    "the call returned false (Win32 error " + Marshal.GetLastWin32Error() + ")");
            }
            if (OperatingSystem.IsMacOS()) return ReadMacReduceMotion();
            return OsMotionStatus.Unavailable("none", "no reader on this platform");
        }
        catch (Exception exception)
        {
            string source = OperatingSystem.IsWindows() ? "SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)"
                : "NSWorkspace.accessibilityDisplayShouldReduceMotion";
            return OsMotionStatus.Unavailable(source, exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>KDE Plasma: "Animation speed" at "Instant" writes <c>AnimationDurationFactor=0</c> to the [KDE] group.</summary>
    internal static bool? ReadKdeGlobals(string? path = null)
    {
        try
        {
            path ??= Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "kdeglobals");
            return File.Exists(path) ? ParseKdeGlobals(File.ReadAllLines(path)) : null;
        }
        catch (Exception) { return null; }
    }

    internal static bool? ParseKdeGlobals(string[] lines)
    {
        bool inKde = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { inKde = line == "[KDE]"; continue; }
            if (!inKde || !line.StartsWith("AnimationDurationFactor", StringComparison.Ordinal)) continue;
            int equals = line.IndexOf('=');
            if (equals > 0 && double.TryParse(line[(equals + 1)..].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double factor))
                return factor <= 0;
        }
        return null;
    }

    /// <summary>The portal value means "animations enabled"; reduced motion is its negation.</summary>
    internal static bool ReducedFromEnableAnimations(bool enabled) => !enabled;

    private static async Task WatchPortalAsync(Action<bool> changed)
    {
        try
        {
            if (string.IsNullOrEmpty(DBusAddress.Session))
            {
                RecordUnavailableUnlessDetected("settings portal", "no D-Bus session bus address");
                return;
            }
            DBusConnection connection = new(DBusAddress.Session);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await connection.ConnectAsync().AsTask().WaitAsync(timeout.Token);
            bool? initial = await connection.CallMethodAsync(ReadOne(connection),
                static (message, _) => ReadBoolVariant(message), null).WaitAsync(timeout.Token);
            if (initial is { } enabled)
            {
                Record(OsMotionStatus.FromValue("settings portal", ReducedFromEnableAnimations(enabled)));
                Dispatcher.UIThread.Post(() => changed(ReducedFromEnableAnimations(enabled)));
            }
            else RecordUnavailableUnlessDetected("settings portal", "enable-animations is not a boolean");
            MatchRule rule = new()
            {
                Type = MessageType.Signal, Sender = "org.freedesktop.portal.Desktop", Path = "/org/freedesktop/portal/desktop",
                Interface = "org.freedesktop.portal.Settings", Member = "SettingChanged"
            };
            await connection.AddMatchAsync<(string Namespace, string Key, bool? Value)>(rule, static (message, _) => ReadSettingChanged(message),
                (exception, value, _, _) =>
                {
                    if (exception == null && value.Namespace == PortalNamespace && value.Key == PortalKey && value.Value is { } now)
                    {
                        Record(OsMotionStatus.FromValue("settings portal", ReducedFromEnableAnimations(now)));
                        Dispatcher.UIThread.Post(() => changed(ReducedFromEnableAnimations(now)));
                    }
                }, ObserverFlags.None, null, null, false).AsTask().WaitAsync(timeout.Token);
            _portal = connection; // Keep the subscription for the life of the process.
        }
        catch (OperationCanceledException) { RecordUnavailableUnlessDetected("settings portal", "no answer from the settings portal within 3 s"); }
        catch (Exception exception) { RecordUnavailableUnlessDetected("settings portal", exception.GetType().Name + ": " + exception.Message); }
    }

    private const string PortalNamespace = "org.gnome.desktop.interface", PortalKey = "enable-animations";

    private static MessageBuffer ReadOne(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Desktop", path: "/org/freedesktop/portal/desktop",
            @interface: "org.freedesktop.portal.Settings", member: "ReadOne", signature: "ss");
        writer.WriteString(PortalNamespace);
        writer.WriteString(PortalKey);
        return writer.CreateMessage();
    }

    private static bool? ReadBoolVariant(Message message)
    {
        var reader = message.GetBodyReader();
        return reader.ReadSignatureAsString() == "b" ? reader.ReadBool() : null;
    }

    private static (string, string, bool?) ReadSettingChanged(Message message)
    {
        var reader = message.GetBodyReader();
        string ns = reader.ReadString(), key = reader.ReadString();
        bool? value = reader.ReadSignatureAsString() == "b" ? reader.ReadBool() : null;
        return (ns, key, value);
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr SendObject(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr receiver, IntPtr selector);

    private static OsMotionStatus ReadMacReduceMotion()
    {
        const string source = "NSWorkspace.accessibilityDisplayShouldReduceMotion";
        IntPtr workspaceClass = objc_getClass("NSWorkspace");
        if (workspaceClass == IntPtr.Zero) return OsMotionStatus.Unavailable(source, "the NSWorkspace class was not found");
        IntPtr workspace = SendObject(workspaceClass, sel_registerName("sharedWorkspace"));
        return workspace == IntPtr.Zero ? OsMotionStatus.Unavailable(source, "NSWorkspace.sharedWorkspace returned nil")
            : OsMotionStatus.FromValue(source, SendBool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion")) != 0);
    }
}
