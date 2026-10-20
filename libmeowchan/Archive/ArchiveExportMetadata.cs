using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Archive;

public sealed record ArchiveExportFile(string Path, long Length, string Sha256);
public sealed record ArchiveExportMetadata(int Version, Guid ExportId, int Generation, ResourceId Thread,
    string SourceUrl, DateTimeOffset ExportedAt, ArchiveHtmlExportOptions Options,
    DateTimeOffset? LastCapturedAt, long? CaptureRevision, int? PostCount,
    IReadOnlyList<ArchiveExportFile> Files)
{
    public bool AdoptedLegacy { get; init; }
}
public sealed record ArchiveExportInspection(ArchiveExportMetadata Metadata, string Directory,
    IReadOnlyList<string> ModifiedPaths, IReadOnlyList<string> ExtraPaths)
{
    public bool RequiresBackup => ModifiedPaths.Count != 0 || ExtraPaths.Count != 0;
    public string ObservedFingerprint { get; init; } = string.Empty;
}
public sealed record ArchiveExportOperationResult(ArchiveHtmlExportResult Export, ArchiveExportMetadata Metadata,
    string? BackupDirectory, bool HistoryRecorded, string? HistoryError);

public static class ArchiveExportMetadataFile
{
    public const string Name = "meowchan-export.json";
    private const int MaxMarkerBytes = 2_000_000;
    private const int MaxFiles = 100_000;
    private const long MaxFileBytes = 4L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<ArchiveExportMetadata> CreateAsync(string stage, ArchivedThreadView view,
        ArchiveHtmlExportOptions options, Guid id, int generation, CancellationToken token, bool adoptedLegacy = false)
    {
        List<ArchiveExportFile> files = new();
        Stack<string> pending = new();
        pending.Push(stage);
        while (pending.Count != 0)
        {
            foreach (string file in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(file);
                if (Directory.Exists(file)) { pending.Push(file); continue; }
                string relative = System.IO.Path.GetRelativePath(stage, file).Replace('\\', '/');
                ValidateRelative(relative);
                FileInfo info = new(file);
                if (info.Length > MaxFileBytes || files.Count >= MaxFiles)
                    throw new InvalidDataException("Export inventory exceeds its safety limits.");
                await using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                files.Add(new ArchiveExportFile(relative, info.Length,
                    Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))));
            }
        }
        ArchiveExportMetadata metadata = new(1, id, generation, view.Summary.Id, view.Summary.Url,
            DateTimeOffset.UtcNow, options, adoptedLegacy ? null : view.Summary.LastCapturedAt,
            adoptedLegacy ? null : view.Summary.Revision,
            adoptedLegacy ? null : view.PostCount,
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()) { AdoptedLegacy = adoptedLegacy };
        string marker = System.IO.Path.Combine(stage, Name);
        string pendingMarker = marker + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(pendingMarker, JsonSerializer.Serialize(metadata, JsonOptions), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(pendingMarker, marker);
        }
        finally { if (File.Exists(pendingMarker)) File.Delete(pendingMarker); }
        return metadata;
    }

    public static async Task<ArchiveExportInspection> InspectAsync(string directory, ResourceId? expectedThread = null,
        CancellationToken token = default)
    {
        string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        RejectLinks(root);
        FileInfo marker = new(System.IO.Path.Combine(root, Name));
        if (!marker.Exists || marker.LinkTarget != null || marker.Length > MaxMarkerBytes)
            throw new InvalidDataException("No supported export marker exists at this destination.");
        ArchiveExportMetadata? metadata;
        byte[] markerBytes;
        try
        {
            await using FileStream input = new(marker.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (input.Length > MaxMarkerBytes) throw new InvalidDataException("Export marker exceeds its size limit.");
            markerBytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(markerBytes, token).ConfigureAwait(false);
            metadata = JsonSerializer.Deserialize<ArchiveExportMetadata>(markerBytes);
        }
        catch (JsonException ex) { throw new InvalidDataException("Export marker JSON is invalid.", ex); }
        if (metadata == null || metadata.Version != 1 || metadata.ExportId == Guid.Empty || metadata.Generation < 1 ||
            metadata.Thread == null || string.IsNullOrWhiteSpace(metadata.Thread.Source) ||
            string.IsNullOrWhiteSpace(metadata.Thread.Board) || string.IsNullOrWhiteSpace(metadata.Thread.Thread) ||
            string.IsNullOrWhiteSpace(metadata.SourceUrl) || metadata.Options == null || metadata.Files == null ||
            metadata.Files.Count > MaxFiles || (expectedThread != null && metadata.Thread != expectedThread))
            throw new InvalidDataException("Export marker identity or version is invalid.");
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (ArchiveExportFile file in metadata.Files)
        {
            if (file == null) throw new InvalidDataException("Export inventory is invalid.");
            ValidateRelative(file.Path);
            if (!paths.Add(file.Path) || file.Length < 0 || file.Length > MaxFileBytes ||
                file.Sha256 == null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Export inventory is invalid.");
        }
        if (!paths.Contains("index.html")) throw new InvalidDataException("Export HTML is absent from inventory.");
        string htmlPath = System.IO.Path.Combine(root, "index.html");
        RejectLinks(htmlPath);
        if (new FileInfo(htmlPath) is not { Exists: true, Length: <= MaxFileBytes })
            throw new InvalidDataException("Export HTML is unavailable.");
        using (FileStream htmlFile = new(htmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] sample = new byte[65_536];
            int count = htmlFile.Read(sample);
            string header = System.Text.Encoding.UTF8.GetString(sample, 0, count);
            string identity = WebUtility.HtmlEncode(metadata.Thread.Source + " /" + metadata.Thread.Board + "/ " + metadata.Thread.Thread);
            if (!header.Contains(identity, StringComparison.Ordinal))
                throw new InvalidDataException("Export HTML does not match its declared thread identity.");
        }
        List<string> modified = new();
        StringBuilder observed = new();
        observed.Append(Convert.ToHexString(SHA256.HashData(markerBytes)));
        foreach (ArchiveExportFile file in metadata.Files)
        {
            token.ThrowIfCancellationRequested();
            string path = System.IO.Path.Combine(root, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            RejectLinks(path);
            FileInfo info = new(path);
            if (!info.Exists || info.Length != file.Length)
            {
                modified.Add(file.Path);
                observed.Append(file.Path).Append(':').Append(info.Exists ? info.Length : -1).Append(';');
                continue;
            }
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            observed.Append(file.Path).Append(':').Append(hash).Append(';');
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) modified.Add(file.Path);
        }
        List<string> extra = new();
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(root);
        int traversed = 0;
        List<string> extraObservations = new();
        while (pendingDirectories.Count != 0)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(pendingDirectories.Pop()))
            {
                token.ThrowIfCancellationRequested();
                if (++traversed > MaxFiles * 2) throw new InvalidDataException("Export contains too many entries.");
                RejectLinks(path);
                string relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                if (Directory.Exists(path))
                {
                    pendingDirectories.Push(path);
                    if (relative != "media" && !paths.Any(known => known.StartsWith(relative + "/", StringComparison.Ordinal)))
                    {
                        extra.Add(relative + "/");
                        extraObservations.Add(relative + "/");
                    }
                    continue;
                }
                if (relative != Name && !paths.Contains(relative))
                {
                    extra.Add(relative);
                    FileInfo info = new(path);
                    extraObservations.Add(relative + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks);
                }
            }
        }
        foreach (string item in extraObservations.OrderBy(item => item, StringComparer.Ordinal)) observed.Append(item).Append(';');
        return new ArchiveExportInspection(metadata, root, modified, extra.OrderBy(path => path, StringComparer.Ordinal).ToArray())
        { ObservedFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observed.ToString()))) };
    }

    internal static void RejectLinks(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string? current = full;
        while (current != null)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("A symbolic link or reparse point cannot be used as an export target.");
            string? parent = System.IO.Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }

    internal static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Split('/').Any(part => part is "" or "." or ".." || part.Contains(':') || part.Any(char.IsControl)) ||
            path == Name)
            throw new InvalidDataException("Export inventory contains an unsafe relative path.");
    }
}
