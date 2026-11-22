using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MeowChan.Persistence;

/// <summary>Single-writer atomic replacement within one filesystem directory.</summary>
internal static class AtomicJsonFile
{
    // .NET's Windows PAL does not support File.Replace when the destination is a symbolic link.
    // Do not fall back to delete/move: that would silently replace a user-controlled link.
    internal static bool CanAtomicallyReplaceSymbolicLink => !OperatingSystem.IsWindows();

    private enum PreviousFile { RotateToBackup, PreserveForRecovery, Replace }

    public static void WriteAllText(string filePath, string contents, bool backupCurrentFile, CancellationToken cancellationToken = default) =>
        WriteAllText(filePath, contents, backupCurrentFile ? PreviousFile.RotateToBackup : PreviousFile.PreserveForRecovery, cancellationToken);

    // The caller has already prepared a compatible backup of the previous snapshot.
    internal static void ReplaceAllText(string filePath, string contents, CancellationToken cancellationToken) =>
        WriteAllText(filePath, contents, PreviousFile.Replace, cancellationToken);

    private static void WriteAllText(string filePath, string contents, PreviousFile previousFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("An absolute destination path is required.", nameof(filePath));
        Directory.CreateDirectory(directory);
        if (IsReparsePoint(filePath) && !CanAtomicallyReplaceSymbolicLink)
            throw new IOException("Atomic replacement of a symbolic-link destination is not supported on this platform.");
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
            // File.Exists is not a reliable link test (it follows the final component
            // on some platforms and falls back to the link itself on others). Treat
            // any link entry as an occupied destination so a replacement never falls
            // through to a platform-specific create/move error (and never exposes
            // that error's private path).
            bool destinationExists = File.Exists(filePath) || IsReparsePoint(filePath);
            if (!destinationExists)
            {
                File.Move(temporaryPath, filePath);
            }
            else if (IsDanglingSymbolicLink(filePath))
            {
                // A dangling link is an unusable primary whatever the caller asked for:
                // File.Copy (PreserveFile) cannot read through it, and on macOS
                // File.Replace inspects the destination through the link and reports
                // ENOENT. Keep the user's link first as a sibling link with the same
                // raw target text, then let rename(2) replace the link entry itself,
                // so the primary path is never absent. An existing backup stays as it
                // is. A crash between the two steps leaves one extra sidecar link.
                PreserveDanglingLink(filePath);
                File.Move(temporaryPath, filePath, overwrite: true);
            }
            else if (previousFile == PreviousFile.RotateToBackup)
            {
                File.Replace(temporaryPath, filePath, filePath + ".bak");
            }
            else
            {
                // A recovery save must not replace the last valid backup with damaged data.
                if (previousFile == PreviousFile.PreserveForRecovery) PreserveFile(filePath);
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

    // File.Exists cannot tell: on Unix .NET falls back to lstat and reports true for a dangling
    // link. Resolve the chain instead and look at the final entry, which is never a link.
    private static bool IsDanglingSymbolicLink(string filePath)
    {
        FileInfo info = new(filePath);
        if (info.LinkTarget == null) return false;
        try
        {
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target == null || !(File.Exists(target.FullName) || Directory.Exists(target.FullName));
        }
        catch (IOException)
        {
            // A loop or an over-long chain never resolves either; it is as unusable as a dangling link.
            return true;
        }
    }

    /// <summary>
    /// Keeps a copy of <paramref name="filePath"/> next to it. A dangling symbolic link has no bytes to copy; it is kept as a
    /// sibling link with the same target text instead (see <see cref="PreserveDanglingLink"/>).
    /// </summary>
    public static string PreserveFile(string filePath)
    {
        if (IsDanglingSymbolicLink(filePath))
            return PreserveDanglingLink(filePath);
        string preservedPath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        File.Copy(filePath, preservedPath, overwrite: false);
        return preservedPath;
    }

    /// <summary>
    /// Creates <c>{path}.dangling-link-{timestamp}-{guid}</c> as a new symbolic link carrying the raw (possibly relative) target
    /// text of the dangling link at <paramref name="filePath"/>. Nothing is renamed: <c>File.Move</c> without overwrite goes through
    /// link(2), which follows a symbolic link on macOS and fails with ENOENT for a dangling one. Throws when the sibling cannot be
    /// created, so the caller does not replace a link it could not keep.
    /// </summary>
    private static string PreserveDanglingLink(string filePath)
    {
        string target = new FileInfo(filePath).LinkTarget
            ?? throw new IOException("The symbolic link changed while it was being preserved.");
        string preservedPath = $"{filePath}.dangling-link-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        File.CreateSymbolicLink(preservedPath, target);
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
