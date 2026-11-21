using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Gallery;

namespace MeowChan.FolderSources;

/// <summary>How a scan of a folder source ended.</summary>
internal enum FolderSourceScanStatus
{
    /// <summary>Every folder was read; files not found are now missing.</summary>
    Complete = 0,
    /// <summary>Some folders could not be read; nothing below them was marked missing.</summary>
    Incomplete = 1,
    /// <summary>The file limit was reached; files beyond it were not added.</summary>
    LimitReached = 2,
    /// <summary>The folder itself was missing or unreadable; nothing was marked missing.</summary>
    RootUnavailable = 3
}

/// <summary>One media file a scan found: relative path (forward slashes), length, last write time (UTC ticks) and kind.</summary>
internal sealed record FolderSourceScannedFile(string RelativePath, long Length, long ModifiedTicks, SavedMediaKind Kind);

/// <summary>
/// What one directory walk saw, without any hashing. <see cref="Complete"/> is false when the walk was stopped or any folder
/// could not be read; <see cref="UnreadablePrefixes"/> names those folders so their contents are never called missing.
/// </summary>
internal sealed class FolderSourceScan
{
    internal FolderSourceScan(IReadOnlyList<FolderSourceScannedFile> files, bool rootAvailable, bool complete, bool limitReached,
        IReadOnlyList<string> unreadablePrefixes, int skippedLinks, IReadOnlyCollection<string>? unlistedPaths = null)
    {
        UnlistedPaths = unlistedPaths ?? Array.Empty<string>();
        Files = files;
        RootAvailable = rootAvailable;
        Complete = complete && rootAvailable;
        LimitReached = limitReached;
        UnreadablePrefixes = unreadablePrefixes;
        SkippedLinks = skippedLinks;
    }

    internal IReadOnlyList<FolderSourceScannedFile> Files { get; }
    internal bool RootAvailable { get; }
    internal bool Complete { get; }
    internal bool LimitReached { get; }
    internal IReadOnlyList<string> UnreadablePrefixes { get; }
    /// <summary>Paths seen on disk but not listed in <see cref="Files"/> (past the file limit, or attributes unreadable): present or unverified, never missing.</summary>
    internal IReadOnlyCollection<string> UnlistedPaths { get; }
    internal int UnreadableFolders => UnreadablePrefixes.Count;
    internal int SkippedLinks { get; }

    internal bool IsBelowUnreadable(string relativePath) =>
        UnreadablePrefixes.Any(prefix => relativePath.StartsWith(prefix + "/", StringComparison.Ordinal));
}

/// <summary>Counts a scan reports while it runs.</summary>
internal sealed record FolderSourceScanProgress(int Folders, int Files);

/// <summary>The result of applying one scan to the shadow catalog.</summary>
internal sealed record FolderSourceScanResult(FolderSourceScanStatus Status, int Files, int Added, int Changed, int Replaced,
    int Revived, int Missing, int Unchanged, int UnreadableFolders, int SkippedLinks);

/// <summary>
/// Walks a folder source level by level and lists its media files (images and videos) with length and last write time.
/// Read-only by construction: it only enumerates directory entries, it opens no file and creates, renames or changes
/// nothing. Symbolic links, junctions and unknown reparse points (files and folders) are never followed; cloud files placeholders
/// are listed (see <see cref="FolderSourceEntries"/>). Folders in
/// <c>excluded</c> (the profile, the download folder) are skipped with everything below them.
/// </summary>
internal static class FolderSourceScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = false, ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.None, MatchType = MatchType.Simple
    };

    internal static FolderSourceScan Scan(string rootPath, bool includeSubfolders, int maximumFiles, IReadOnlyCollection<string> excluded,
        IProgress<FolderSourceScanProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(excluded);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        try
        {
            DirectoryInfo info = new(root);
            if (!info.Exists || FolderSourceEntries.IsLink(info, info.Attributes))
                return new(Array.Empty<FolderSourceScannedFile>(), false, false, false, Array.Empty<string>(), 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(Array.Empty<FolderSourceScannedFile>(), false, false, false, Array.Empty<string>(), 0); }

        string[] skip = excluded.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
        List<FolderSourceScannedFile> files = new();
        List<string> unreadable = new();
        HashSet<string> unlisted = new(StringComparer.Ordinal);
        int links = 0, folders = 0;
        bool limitReached = false, complete = true;
        Queue<(string Absolute, string Relative)> pending = new();
        pending.Enqueue((root, string.Empty));
        DateTime lastReport = DateTime.UtcNow;
        while (pending.Count > 0)
        {
            if (token.IsCancellationRequested) { complete = false; break; }
            (string directory, string relative) = pending.Dequeue();
            folders++;
            List<FileSystemInfo> children;
            try { children = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Options).ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (relative.Length == 0) return new(Array.Empty<FolderSourceScannedFile>(), false, false, false, Array.Empty<string>(), links);
                unreadable.Add(relative);
                complete = false;
                continue;
            }
            // Ordinal order keeps the walk (and so which files a limit keeps) stable between runs.
            foreach (FileSystemInfo child in children.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (token.IsCancellationRequested) { complete = false; break; }
                FileAttributes attributes;
                try { attributes = child.Attributes; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Unverified, not gone: the entry exists, only its attributes could not be read.
                    if (FolderSourceIdentity.TryNormalizeRelative(relative.Length == 0 ? child.Name : relative + "/" + child.Name, out string unverified)) unlisted.Add(unverified);
                    continue;
                }
                // A link is never followed; a cloud placeholder (OneDrive) is listed like any file or folder (W3-08).
                if (FolderSourceEntries.IsLink(child, attributes)) { links++; continue; }
                string childRelative = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!includeSubfolders || IsExcluded(child.FullName, skip)) continue;
                    if (!FolderSourceIdentity.TryNormalizeRelative(childRelative, out _)) continue;
                    pending.Enqueue((child.FullName, childRelative));
                    continue;
                }
                if (child is not FileInfo file) continue;
                SavedMediaKind kind = SavedMediaTypes.Classify(file.Name);
                if (kind is not (SavedMediaKind.Image or SavedMediaKind.Video)) continue;
                if (!FolderSourceIdentity.TryNormalizeRelative(childRelative, out string normalized)) continue;
                long length, modified;
                try { length = file.Length; modified = file.LastWriteTimeUtc.Ticks; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unlisted.Add(normalized); continue; }
                if (files.Count >= maximumFiles) { limitReached = true; unlisted.Add(normalized); continue; }
                files.Add(new(normalized, length, modified, kind));
            }
            if (progress != null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = DateTime.UtcNow;
                progress.Report(new(folders, files.Count));
            }
        }
        progress?.Report(new(folders, files.Count));
        return new(files.AsReadOnly(), true, complete, limitReached, unreadable.AsReadOnly(), links, unlisted);
    }

    private static bool IsExcluded(string directory, string[] excluded)
    {
        string full = Path.TrimEndingDirectorySeparator(directory);
        return excluded.Any(path => FolderSourceValidation.SameOrBelow(full, path));
    }
}

/// <summary>Checks a folder before it becomes a source: never the profile, never the download folder, never twice.</summary>
internal static class FolderSourceValidation
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> or lies below it.</summary>
    internal static bool SameOrBelow(string path, string ancestor)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor));
        return string.Equals(a, b, Comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, Comparison)
            || b.EndsWith(Path.DirectorySeparatorChar) && a.StartsWith(b, Comparison);
    }

    /// <summary>True when either folder contains the other (or they are the same).</summary>
    internal static bool Overlaps(string left, string right) => SameOrBelow(left, right) || SameOrBelow(right, left);

    /// <summary>The reason a folder cannot become a source, or <see cref="FolderSourceError.None"/>.</summary>
    internal static FolderSourceError Check(string? folder, string profileDirectory, string downloadRoot, IEnumerable<FolderSourceRoot> existing)
    {
        if (string.IsNullOrWhiteSpace(folder)) return FolderSourceError.FolderUnavailable;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return FolderSourceError.FolderUnavailable; }
        if (full.Length > FolderSourceCatalog.MaximumPathLength) return FolderSourceError.FolderUnavailable;
        try
        {
            DirectoryInfo info = new(full);
            if (!info.Exists || FolderSourceEntries.IsLink(info, info.Attributes)) return FolderSourceError.FolderUnavailable;
            // Readable: listing the first entry fails for a folder the person may not read (nothing is created or opened).
            using IEnumerator<string> probe = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            probe.MoveNext();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return FolderSourceError.FolderUnavailable; }
        // The profile and the download folder may lie inside a source (the scan skips both), but a source never lies inside them.
        if (SameOrBelow(full, profileDirectory)) return FolderSourceError.FolderIsProfile;
        if (SameOrBelow(full, downloadRoot)) return FolderSourceError.FolderIsDownloads;
        if (existing.Any(root => Overlaps(root.Path, full))) return FolderSourceError.FolderOverlaps;
        return FolderSourceError.None;
    }
}
