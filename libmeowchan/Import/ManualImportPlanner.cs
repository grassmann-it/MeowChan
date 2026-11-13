using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Gallery;
using MeowChan.Persistence;

namespace MeowChan.Import;

// Contract: manual-import-v1.md §4 (selection, kinds, limits) and §5 (dedupe).

/// <summary>What the user selected: files and folders, and two switches.</summary>
internal sealed record ManualImportRequest(IReadOnlyList<string> Roots, bool IncludeSubfolders = true, bool IncludeOtherFiles = false);

/// <summary>
/// The configured limits of one batch (user settings). <see cref="MaximumFiles"/> counts importable files (new and
/// already present); <see cref="MaximumBatchBytes"/> their total size; <see cref="MaximumFileBytes"/> one file.
/// </summary>
internal sealed record ManualImportLimits(int MaximumFiles, long MaximumBatchBytes, long MaximumFileBytes)
{
    internal const int DefaultMaximumFiles = 2_000;
    internal const long DefaultMaximumBatchBytes = 50L * 1024 * 1024 * 1024;
    internal const long DefaultMaximumFileBytes = 4L * 1024 * 1024 * 1024;
    internal static ManualImportLimits Default { get; } = new(DefaultMaximumFiles, DefaultMaximumBatchBytes, DefaultMaximumFileBytes);

    internal void Validate()
    {
        if (MaximumFiles is < 1 or > ManualImportCatalogStore.MaximumItemsPerBatch || MaximumBatchBytes < 1 || MaximumFileBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(ManualImportLimits));
    }
}

/// <summary>What happens to one selected file.</summary>
internal enum ManualImportDisposition
{
    /// <summary>New bytes: will be copied.</summary>
    New,
    /// <summary>The media library already has these bytes: only linked to the batch, not copied.</summary>
    AlreadyInLibrary,
    /// <summary>The same bytes were selected earlier in this selection: imported once.</summary>
    DuplicateInSelection,
    /// <summary>A file type MeowChan cannot show or keep as media.</summary>
    Unsupported,
    /// <summary>PDF, ZIP or audio while "also other files" is off.</summary>
    OtherFilesOff,
    /// <summary>Larger than the configured single-file limit.</summary>
    TooLarge,
    /// <summary>Could not be read (permissions, vanished, I/O error).</summary>
    Unreadable,
    /// <summary>A link, or already inside the download folder: never followed or imported.</summary>
    Skipped
}

/// <summary>One selected file. <see cref="SourcePath"/> stays in memory only; it is never stored or logged.</summary>
internal sealed record ManualImportCandidate(string SourcePath, string Name, long Length, SavedMediaKind Kind, string Extension,
    ManualImportDisposition Disposition, string? Sha256 = null, int ExistingOccurrences = 0)
{
    internal bool Importable => Disposition is ManualImportDisposition.New or ManualImportDisposition.AlreadyInLibrary
        or ManualImportDisposition.DuplicateInSelection;
}

/// <summary>Why a selection cannot become a batch at all (reported before any file is hashed).</summary>
internal enum ManualImportPlanStatus { Ready, TooManyFiles, BatchTooLarge, Cancelled }

/// <summary>
/// The checked selection. <see cref="DuplicateCheckComplete"/> is false when the media index the check used was incomplete;
/// files whose bytes were not found then count as new (one copy too many rather than a silent omission).
/// </summary>
internal sealed record ManualImportPlan(IReadOnlyList<ManualImportCandidate> Items, ManualImportPlanStatus Status,
    bool DuplicateCheckComplete, int ImportableFiles, long ImportableBytes, bool IncludeOtherFiles, string? SourceLabel)
{
    internal int Count(ManualImportDisposition disposition) => Items.Count(item => item.Disposition == disposition);
    internal IEnumerable<ManualImportCandidate> New => Items.Where(item => item.Disposition == ManualImportDisposition.New);
    internal long NewBytes => New.Sum(item => item.Length);
}

/// <summary>Progress of the check: files and bytes hashed so far of the importable total.</summary>
internal sealed record ManualImportCheckProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

/// <summary>
/// What the library already holds, by SHA-256: the number of present occurrences per digest, from a freshly built media
/// index (archived threads, Danbooru originals and earlier imports). <see cref="Complete"/> false: the index could not
/// read everything.
/// </summary>
internal sealed class ManualImportKnownMedia
{
    private readonly Dictionary<string, int> _counts;

    internal ManualImportKnownMedia(IReadOnlyDictionary<string, int> counts, bool complete)
    {
        _counts = new Dictionary<string, int>(counts ?? throw new ArgumentNullException(nameof(counts)), StringComparer.Ordinal);
        Complete = complete;
    }

    internal static ManualImportKnownMedia Empty { get; } = new(new Dictionary<string, int>(), true);

    internal bool Complete { get; }
    internal int Occurrences(string sha256) => _counts.GetValueOrDefault(sha256);

    internal static ManualImportKnownMedia From(MediaLibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (MediaLibraryEntry entry in snapshot.Entries)
            if (entry.Presence == MediaLibraryPresence.Present && entry.Sha256 is { Length: 64 } sha)
                counts[sha] = counts.GetValueOrDefault(sha) + 1;
        return new(counts, snapshot.Complete);
    }
}

/// <summary>
/// Turns a selection into a checked plan, read-only: walks folders (never following links, skipping hidden and MeowChan
/// metadata names and everything inside the download folder), sorts out unsupported kinds, refuses selections over the
/// configured limits before hashing, hashes every importable file once (progress, cancellation) and finds duplicates
/// within the selection and against the library. Never writes, moves or deletes anything.
/// </summary>
internal static class ManualImportPlanner
{
    private const int BufferSize = 1024 * 1024;

    internal static async Task<ManualImportPlan> PlanAsync(ManualImportRequest request, ManualImportLimits limits, string? downloadRoot,
        Func<CancellationToken, Task<ManualImportKnownMedia>> knownMedia, IProgress<ManualImportCheckProgress>? progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(knownMedia);
        limits.Validate();
        string? library = downloadRoot == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadRoot));
        List<ManualImportCandidate> items = new();
        HashSet<string> seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int importable = 0;
        long importableBytes = 0;
        bool overCount = false;

        void Add(FileInfo file)
        {
            string path = file.FullName;
            if (!seen.Add(path)) return;
            string name = DisplayName(file.Name);
            string extension = Path.GetExtension(file.Name).ToLowerInvariant();
            SavedMediaKind kind = SavedMediaTypes.Classify(extension);
            if (Inside(library, path) || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            { items.Add(new(path, name, 0, kind, extension, ManualImportDisposition.Skipped)); return; }
            // Only regular files are media: a pipe, socket or device must never be opened (a pipe's open waits for a writer).
            if (RegularProfileFile.IsUnixSpecialFile(path))
            { items.Add(new(path, name, 0, SavedMediaKind.Unknown, extension, ManualImportDisposition.Unsupported)); return; }
            if (kind == SavedMediaKind.Unknown || !ManualImportCatalogStore.ValidExtension(extension))
            { items.Add(new(path, name, SafeLength(file), SavedMediaKind.Unknown, extension, ManualImportDisposition.Unsupported)); return; }
            if (kind == SavedMediaKind.Other && !request.IncludeOtherFiles)
            { items.Add(new(path, name, SafeLength(file), kind, extension, ManualImportDisposition.OtherFilesOff)); return; }
            long length = SafeLength(file);
            if (length < 0) { items.Add(new(path, name, 0, kind, extension, ManualImportDisposition.Unreadable)); return; }
            if (length > limits.MaximumFileBytes) { items.Add(new(path, name, length, kind, extension, ManualImportDisposition.TooLarge)); return; }
            if (++importable > limits.MaximumFiles) { overCount = true; return; }
            importableBytes = checked(importableBytes + length);
            items.Add(new(path, name, length, kind, extension, ManualImportDisposition.New));
        }

        string? sourceLabel = null;
        foreach (string root in request.Roots ?? Array.Empty<string>())
        {
            token.ThrowIfCancellationRequested();
            if (overCount) break;
            if (string.IsNullOrWhiteSpace(root)) continue;
            string full;
            try { full = Path.GetFullPath(root); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            FileAttributes attributes;
            try { attributes = File.GetAttributes(full); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                string name = DisplayName(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)));
                if (seen.Add(full)) items.Add(new(full, name, 0, SavedMediaTypes.Classify(name), Path.GetExtension(name).ToLowerInvariant(), ManualImportDisposition.Unreadable));
                continue;
            }
            if ((attributes & FileAttributes.Directory) == 0) { Add(new FileInfo(full)); continue; }
            DirectoryInfo directory = new(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || Inside(library, full))
            {
                if (seen.Add(full)) items.Add(new(full, DisplayName(directory.Name), 0, SavedMediaKind.Unknown, string.Empty, ManualImportDisposition.Skipped));
                continue;
            }
            // A single selected folder names the batch's source; several roots name nothing.
            sourceLabel = request.Roots!.Count == 1 ? DisplayLabel(directory.Name) : null;
            // Walk the folder level by level so links, hidden folders and the download folder itself are pruned, never entered.
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                // Links and junctions are never followed or imported; hidden and system entries are not the user's media.
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                MatchType = MatchType.Simple,
                ReturnSpecialDirectories = false
            };
            Stack<DirectoryInfo> pending = new();
            pending.Push(directory);
            while (pending.Count > 0 && !overCount)
            {
                token.ThrowIfCancellationRequested();
                DirectoryInfo current = pending.Pop();
                FileInfo[] files;
                DirectoryInfo[] children;
                try
                {
                    files = current.EnumerateFiles("*", options).OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
                    children = request.IncludeSubfolders
                        ? current.EnumerateDirectories("*", options).OrderByDescending(child => child.Name, StringComparer.Ordinal).ToArray()
                        : Array.Empty<DirectoryInfo>();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (seen.Add(current.FullName))
                        items.Add(new(current.FullName, DisplayName(current.Name), 0, SavedMediaKind.Unknown, string.Empty, ManualImportDisposition.Unreadable));
                    continue;
                }
                foreach (FileInfo file in files)
                {
                    token.ThrowIfCancellationRequested();
                    // Dot files (Unix hidden files, MeowChan's own ".meowchan-*" metadata) are not the user's media.
                    if (file.Name.StartsWith('.') || Inside(library, file.FullName)) continue;
                    Add(file);
                    if (overCount) break;
                }
                foreach (DirectoryInfo child in children)
                    if (!child.Name.StartsWith('.') && !Inside(library, child.FullName)) pending.Push(child);
            }
        }
        if (overCount)
            return new(Array.AsReadOnly(items.ToArray()), ManualImportPlanStatus.TooManyFiles, true, importable, importableBytes, request.IncludeOtherFiles, sourceLabel);
        if (importableBytes > limits.MaximumBatchBytes)
            return new(Array.AsReadOnly(items.ToArray()), ManualImportPlanStatus.BatchTooLarge, true, importable, importableBytes, request.IncludeOtherFiles, sourceLabel);

        // Hash every importable file once. The measured length replaces the listed one.
        ManualImportCandidate[] result = items.ToArray();
        int total = result.Count(item => item.Disposition == ManualImportDisposition.New), done = 0;
        long bytesTotal = importableBytes, bytesDone = 0;
        progress?.Report(new(0, total, 0, bytesTotal));
        byte[] buffer = new byte[BufferSize];
        for (int index = 0; index < result.Length; index++)
        {
            ManualImportCandidate item = result[index];
            if (item.Disposition != ManualImportDisposition.New) continue;
            token.ThrowIfCancellationRequested();
            long before = bytesDone;
            try
            {
                (string sha, long length) = await HashAsync(item.SourcePath, buffer, limits.MaximumFileBytes,
                    read => progress?.Report(new(done, total, before + read, bytesTotal)), token).ConfigureAwait(false);
                result[index] = length > limits.MaximumFileBytes ? item with { Length = length, Disposition = ManualImportDisposition.TooLarge }
                    : item with { Length = length, Sha256 = sha };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or PlatformNotSupportedException)
            { result[index] = item with { Disposition = ManualImportDisposition.Unreadable }; }
            bytesDone = before + item.Length;
            progress?.Report(new(++done, total, bytesDone, bytesTotal));
        }

        ManualImportKnownMedia known = await knownMedia(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        HashSet<string> selected = new(StringComparer.Ordinal);
        int finalImportable = 0;
        long finalBytes = 0;
        for (int index = 0; index < result.Length; index++)
        {
            ManualImportCandidate item = result[index];
            if (item.Disposition != ManualImportDisposition.New || item.Sha256 == null) continue;
            finalImportable++;
            finalBytes += item.Length;
            int existing = known.Occurrences(item.Sha256);
            if (existing > 0) result[index] = item with { Disposition = ManualImportDisposition.AlreadyInLibrary, ExistingOccurrences = existing };
            else if (!selected.Add(item.Sha256)) result[index] = item with { Disposition = ManualImportDisposition.DuplicateInSelection };
        }
        ManualImportPlanStatus status = finalBytes > limits.MaximumBatchBytes ? ManualImportPlanStatus.BatchTooLarge : ManualImportPlanStatus.Ready;
        return new(Array.AsReadOnly(result), status, known.Complete, finalImportable, finalBytes, request.IncludeOtherFiles, sourceLabel);
    }

    private static async Task<(string Sha256, long Length)> HashAsync(string path, byte[] buffer, long maximum, Action<long> report,
        CancellationToken token)
    {
        await using FileStream stream = OpenSource(path);
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0, reported = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            sha.AppendData(buffer, 0, read);
            length += read;
            if (length > maximum) return (string.Empty, length);
            if (length - reported >= 16 * BufferSize) { report(length); reported = length; }
        }
        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), length);
    }

    /// <summary>
    /// Opens a selected file for reading, only if it is a regular file at open time (the entry may have been replaced since it
    /// was listed): final links, folders, pipes, sockets and devices are refused with <see cref="InvalidDataException"/>
    /// before anything can block. On Unix the open itself is non-blocking and the opened object is checked, not the name.
    /// </summary>
    internal static FileStream OpenSource(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("The selected file is no regular file.");
        return RegularProfileFile.OpenRead(path);
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return -1; }
    }

    private static bool Inside(string? library, string path)
    {
        if (library == null) return false;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string normalized = Path.TrimEndingDirectorySeparator(path);
        return string.Equals(normalized, library, comparison)
            || normalized.StartsWith(library + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The file name as the catalog may keep it: trimmed, bounded and free of control characters.</summary>
    internal static string DisplayName(string? name)
    {
        string text = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > ManualImportCatalogStore.MaximumNameLength)
        {
            string extension = Path.GetExtension(text);
            text = text[..(ManualImportCatalogStore.MaximumNameLength - Math.Min(extension.Length, 16))].TrimEnd() + extension[..Math.Min(extension.Length, 16)];
        }
        return ManualImportCatalogStore.ValidName(text, ManualImportCatalogStore.MaximumNameLength) && text is not ("." or "..") ? text : "file";
    }

    private static string? DisplayLabel(string? name)
    {
        string text = DisplayName(name);
        return text == "file" && name != "file" ? null : text;
    }
}
