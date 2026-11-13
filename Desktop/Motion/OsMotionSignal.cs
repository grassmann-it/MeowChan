using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Tmds.DBus.Protocol;

namespace MeowChan.Desktop.Motion;

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

    /// <summary>Reads the signal now and keeps <paramref name="changed"/> informed where the platform reports changes.</summary>
    internal static void Start(Action<bool> changed)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (ReadKdeGlobals() is { } kde) changed(kde);
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
        catch (Exception) { }
    }

    /// <summary>The Windows or macOS answer; null when it cannot be read.</summary>
    internal static bool? ReadNow()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out int enabled, 0) ? enabled == 0 : null;
            if (OperatingSystem.IsMacOS()) return ReadMacReduceMotion();
        }
        catch (Exception) { }
        return null;
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
            if (string.IsNullOrEmpty(DBusAddress.Session)) return;
            DBusConnection connection = new(DBusAddress.Session);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await connection.ConnectAsync().AsTask().WaitAsync(timeout.Token);
            bool? initial = await connection.CallMethodAsync(ReadOne(connection),
                static (message, _) => ReadBoolVariant(message), null).WaitAsync(timeout.Token);
            if (initial is { } enabled) Dispatcher.UIThread.Post(() => changed(ReducedFromEnableAnimations(enabled)));
            MatchRule rule = new()
            {
                Type = MessageType.Signal, Sender = "org.freedesktop.portal.Desktop", Path = "/org/freedesktop/portal/desktop",
                Interface = "org.freedesktop.portal.Settings", Member = "SettingChanged"
            };
            await connection.AddMatchAsync<(string Namespace, string Key, bool? Value)>(rule, static (message, _) => ReadSettingChanged(message),
                (exception, value, _, _) =>
                {
                    if (exception == null && value.Namespace == PortalNamespace && value.Key == PortalKey && value.Value is { } now)
                        Dispatcher.UIThread.Post(() => changed(ReducedFromEnableAnimations(now)));
                }, ObserverFlags.None, null, null, false).AsTask().WaitAsync(timeout.Token);
            _portal = connection; // Keep the subscription for the life of the process.
        }
        catch (Exception) { }
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

    private static bool? ReadMacReduceMotion()
    {
        IntPtr workspaceClass = objc_getClass("NSWorkspace");
        if (workspaceClass == IntPtr.Zero) return null;
        IntPtr workspace = SendObject(workspaceClass, sel_registerName("sharedWorkspace"));
        return workspace == IntPtr.Zero ? null : SendBool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion")) != 0;
    }
}
