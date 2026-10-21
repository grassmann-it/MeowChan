using System;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace MeowChan.Desktop;

/// <summary>Checks the actual StatusNotifier host and this process's registered icon.</summary>
internal static class LinuxTrayHost
{
    internal static Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        IsAvailableAsync(DBusAddress.Session, cancellationToken);

    internal static async Task<bool> IsAvailableAsync(string? address, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(address)) return false;
            using DBusConnection connection = new(address);
            // Bound a missing or unresponsive desktop service. Disposing this private
            // connection also ends any outstanding request when the timeout expires.
            return await QueryAsync(connection).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> QueryAsync(DBusConnection connection)
    {
        await connection.ConnectAsync();
        bool hosted = await connection.CallMethodAsync(GetProperty(connection, "IsStatusNotifierHostRegistered"),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                reader.ReadSignature("b");
                return reader.ReadBool();
            });
        if (!hosted) return false;
        string[] items = await connection.CallMethodAsync(GetProperty(connection, "RegisteredStatusNotifierItems"),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                reader.ReadSignature("as");
                return reader.ReadArrayOfString();
            });
        return HasRegisteredIcon(items, Environment.ProcessId);
    }

    internal static bool HasRegisteredIcon(string[] items, int processId)
    {
        string prefix = $"org.kde.StatusNotifierItem-{processId}-";
        // Avalonia owns org.kde.StatusNotifierItem-{pid}-{counter}. The production
        // desktop has one MainWindow/icon; rapid-window smoke also uses this process.
        return items.Any(item =>
        {
            string service = item.Split('/')[0];
            return service.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(service.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                    out int counter) && counter >= 0;
        });
    }

    private static MessageBuffer GetProperty(DBusConnection connection, string property)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.kde.StatusNotifierWatcher", path: "/StatusNotifierWatcher",
            @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString("org.kde.StatusNotifierWatcher");
        writer.WriteString(property);
        return writer.CreateMessage();
    }
}
