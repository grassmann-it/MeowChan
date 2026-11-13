using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal enum DirectoryProbeResult { Exists, Missing, Unknown }

/// <summary>
/// Checks a folder without blocking the caller. A network share that stops answering can hold
/// <see cref="Directory.Exists(string)"/> for the network timeout, so the check runs on the thread pool
/// and the caller stops waiting after a short limit (the pool thread is released by the OS later).
/// </summary>
internal static class DirectoryProbe
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <returns><see cref="DirectoryProbeResult.Unknown"/> when the check did not finish within <paramref name="timeout"/>.</returns>
    internal static async Task<DirectoryProbeResult> CheckAsync(string directory, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, Func<string, bool>? exists = null)
    {
        Func<string, bool> probe = exists ?? Directory.Exists;
        Task<bool> check = Task.Run(() =>
        {
            try { return probe(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }, cancellationToken);
        try
        {
            return await check.WaitAsync(timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(true)
                ? DirectoryProbeResult.Exists : DirectoryProbeResult.Missing;
        }
        catch (TimeoutException) { return DirectoryProbeResult.Unknown; }
    }
}
