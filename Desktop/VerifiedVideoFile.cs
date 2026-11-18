using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>
/// Chooses the native input boundary used for verified video. macOS Intel's bundled VLC
/// callback/imem path can reject its callback table while an ordinary file input remains
/// fully seekable and repeatable.
/// </summary>
internal static class VideoNativeInput
{
    internal static bool UsesFileInput => OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    internal static Task<VerifiedVideoFile> MaterializeAsync(Stream verified, string? sourceName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verified);
        return VerifiedVideoFile.CreateAsync(verified, Extension(sourceName), cancellationToken);
    }

    private static string Extension(string? sourceName)
    {
        string extension = Path.GetExtension(sourceName ?? string.Empty);
        if (extension.Length is < 2 or > 12 || extension[0] != '.'
            || !extension.Skip(1).All(char.IsAsciiLetterOrDigit)) return ".media";
        return extension.ToLowerInvariant();
    }
}

/// <summary>
/// A private file copied from an already hash-verified stream. VLC owns only this path;
/// the verified source stream remains owned by the caller until native media release.
/// </summary>
internal sealed class VerifiedVideoFile : IDisposable
{
    private string? _path;

    private VerifiedVideoFile(string path) => _path = path;

    internal string Path => Volatile.Read(ref _path)
        ?? throw new ObjectDisposedException(nameof(VerifiedVideoFile));

    internal static async Task<VerifiedVideoFile> CreateAsync(Stream verified, string extension,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verified);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "meowchan-vlc-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            if (verified.CanSeek) verified.Position = 0;
            await using (FileStream destination = new(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await verified.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (verified.CanSeek) verified.Position = 0;
            return new VerifiedVideoFile(path);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public void Dispose()
    {
        string? path = Interlocked.Exchange(ref _path, null);
        if (path == null) return;
        try { File.Delete(path); }
        catch
        {
            // Retain the path if native cleanup cannot prove deletion. The caller's
            // cleanup failure then keeps ownership visible instead of leaking silently.
            Volatile.Write(ref _path, path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
