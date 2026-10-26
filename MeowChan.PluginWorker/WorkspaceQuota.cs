using System.Globalization;

namespace MeowChan.PluginWorker;

/// <summary>The host reserves this workspace allowance before starting the process, including descendants.</summary>
internal static class WorkspaceQuota
{
    internal static void Accept()
    {
        string? text = Environment.GetEnvironmentVariable("MEOWCHAN_WORKSPACE_BYTE_QUOTA");
        // Older hosts do not send a quota. Current hosts always send the reserved allowance.
        if (text == null) return;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes)
            || bytes < 1 || bytes > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Invalid reserved worker workspace quota.");
    }
}
