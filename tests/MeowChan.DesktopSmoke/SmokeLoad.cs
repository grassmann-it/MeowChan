namespace MeowChan.DesktopSmoke;

// Harness-only: names host load in timeout diagnostics so a slow-machine miss is distinguishable
// from a product regression. Never an assertion input.
internal static class SmokeLoad
{
    internal static string Describe()
    {
        try
        {
            return "loadavg=" + File.ReadAllText("/proc/loadavg").Split(' ')[0] + ", cpus=" + Environment.ProcessorCount;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or IndexOutOfRangeException)
        {
            return "load unavailable";
        }
    }
}
