using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MeowChan.Persistence;

/// <summary>Single-writer atomic replacement within one filesystem directory.</summary>
internal static class AtomicJsonFile
{
    public static void WriteAllText(string filePath, string contents, bool backupCurrentFile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("An absolute destination path is required.", nameof(filePath));
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            // Persist JSON with a platform-independent newline so byte-stable files remain
            // interchangeable between Windows and Unix writers. Escaped JSON string newlines
            // are backslash sequences, not affected by this normalization.
            byte[] bytes = Encoding.UTF8.GetBytes(contents.Replace("\r\n", "\n", StringComparison.Ordinal));
            using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // Cancellation must not report failure after a new snapshot has been committed.
            cancellationToken.ThrowIfCancellationRequested();
            // File.Exists follows the final component and therefore reports false for
            // a dangling link. Treat the link itself as an occupied destination so a
            // replacement never falls through to a platform-specific create/move
            // error (and never exposes that error's private path).
            bool destinationExists = File.Exists(filePath) || IsReparsePoint(filePath);
            if (!destinationExists)
            {
                File.Move(temporaryPath, filePath);
            }
            else if (backupCurrentFile)
            {
                File.Replace(temporaryPath, filePath, filePath + ".bak");
            }
            else
            {
                // A recovery save must not replace the last valid backup with damaged data.
                PreserveFile(filePath);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, filePath, overwrite: true);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsReparsePoint(string filePath)
    {
        FileInfo info = new(filePath);
        return info.LinkTarget != null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    public static string PreserveFile(string filePath)
    {
        string preservedPath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        File.Copy(filePath, preservedPath, overwrite: false);
        return preservedPath;
    }

    /// <summary>
    /// Moves a damaged backup next to itself as <c>.bad</c> (a numbered name if that exists) so the next save can rotate a good
    /// primary into its place. Nothing is overwritten and nothing is deleted. A directory or link is not moved.
    /// </summary>
    /// <returns>The new path, or null when <paramref name="backupPath"/> is not a regular file.</returns>
    public static string? SetAsideDamagedBackup(string backupPath)
    {
        FileInfo info = new(backupPath);
        if (!info.Exists || info.LinkTarget != null || (info.Attributes & FileAttributes.Directory) != 0) return null;
        string target = backupPath + ".bad";
        if (File.Exists(target) || Directory.Exists(target))
            target = $"{backupPath}.bad-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        File.Move(backupPath, target, overwrite: false);
        return target;
    }
}
