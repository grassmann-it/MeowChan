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

public sealed record ArchiveCollectionMember(ResourceId Thread, string SourceUrl, string PagePath,
    DateTimeOffset? LastCapturedAt, long CaptureRevision, int PostCount);
public sealed record ArchiveCollectionExportMetadata(int Version, Guid ExportId, int Generation,
    IReadOnlyList<ArchiveCollectionMember> Members, DateTimeOffset ExportedAt, ArchiveHtmlExportOptions Options,
    IReadOnlyList<ArchiveExportFile> Files);
public sealed record ArchiveCollectionExportInspection(ArchiveCollectionExportMetadata Metadata, string Directory,
    IReadOnlyList<string> ModifiedPaths, IReadOnlyList<string> ExtraPaths)
{
    public bool RequiresBackup => ModifiedPaths.Count != 0 || ExtraPaths.Count != 0;
    public string ObservedFingerprint { get; init; } = string.Empty;
}
public sealed record ArchiveCollectionExportOperationResult(ArchiveCollectionHtmlExportResult Export, ArchiveCollectionExportMetadata Metadata,
    string? BackupDirectory, bool HistoryRecorded, string? HistoryError);

public static class ArchiveCollectionExportMetadataFile
{
    public const string Name = "meowchan-collection-export.json";
    private const int MaxMarkerBytes = 8_000_000;
    private const int MaxFiles = 100_000;
    private const long MaxFileBytes = 4L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<ArchiveCollectionExportMetadata> CreateAsync(string stage, IReadOnlyList<ArchiveCollectionMember> members,
        ArchiveHtmlExportOptions options, Guid id, int generation, CancellationToken token)
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
        ArchiveCollectionExportMetadata metadata = new(1, id, generation, members, DateTimeOffset.UtcNow, options,
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
        string marker = System.IO.Path.Combine(stage, Name);
        string pendingMarker = marker + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
            if (bytes.Length > MaxMarkerBytes) throw new InvalidDataException("The collection marker exceeds its safety limit.");
            await File.WriteAllBytesAsync(pendingMarker, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(pendingMarker, marker);
        }
        finally { if (File.Exists(pendingMarker)) File.Delete(pendingMarker); }
        return metadata;
    }

    public static async Task<ArchiveCollectionExportInspection> InspectAsync(string directory, IReadOnlyList<ResourceId>? expectedMembers = null,
        CancellationToken token = default)
    {
        expectedMembers = expectedMembers == null ? null : ArchiveCollectionHtmlExporter.FreezeMembers(expectedMembers);
        string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        RejectLinks(root);
        FileInfo marker = new(System.IO.Path.Combine(root, Name));
        if (!marker.Exists || marker.LinkTarget != null || marker.Length > MaxMarkerBytes)
            throw new InvalidDataException("No supported export marker exists at this destination.");
        ArchiveCollectionExportMetadata? metadata;
        byte[] markerBytes;
        try
        {
            await using FileStream input = new(marker.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (input.Length > MaxMarkerBytes) throw new InvalidDataException("Export marker exceeds its size limit.");
            markerBytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(markerBytes, token).ConfigureAwait(false);
            metadata = JsonSerializer.Deserialize<ArchiveCollectionExportMetadata>(markerBytes);
        }
        catch (JsonException ex) { throw new InvalidDataException("Export marker JSON is invalid.", ex); }
        if (metadata == null || metadata.Version != 1 || metadata.ExportId == Guid.Empty || metadata.Generation < 1 ||
            metadata.Options == null || metadata.Files == null || metadata.Files.Count > MaxFiles ||
            metadata.Members == null || metadata.Members.Count == 0)
            throw new InvalidDataException("Collection export marker identity or version is invalid.");
        HashSet<ResourceId> identities = new();
        foreach (ArchiveCollectionMember member in metadata.Members)
        {
            if (member == null || member.Thread == null || string.IsNullOrWhiteSpace(member.Thread.Source) ||
                string.IsNullOrWhiteSpace(member.Thread.Board) || string.IsNullOrWhiteSpace(member.Thread.Thread) ||
                string.IsNullOrWhiteSpace(member.SourceUrl) || member.PostCount < 0 || !identities.Add(member.Thread) ||
                member.PagePath != ArchiveCollectionHtmlExporter.PagePath(member.Thread))
                throw new InvalidDataException("Collection member identity is invalid.");
        }
        if (expectedMembers != null && !metadata.Members.Select(member => member.Thread).SequenceEqual(
            ArchiveCollectionHtmlExporter.FreezeMembers(expectedMembers)))
            throw new InvalidDataException("The collection contains different thread members or ordering.");
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
        RejectLinks(System.IO.Path.Combine(root, "index.html"));
        if (new FileInfo(System.IO.Path.Combine(root, "index.html")) is not { Exists: true, Length: <= MaxFileBytes })
            throw new InvalidDataException("The collection start page is unavailable.");
        foreach (ArchiveCollectionMember member in metadata.Members)
        {
            if (!paths.Contains(member.PagePath)) throw new InvalidDataException("A member HTML page is absent from inventory.");
            string htmlPath = System.IO.Path.Combine(root, member.PagePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            RejectLinks(htmlPath);
            if (new FileInfo(htmlPath) is not { Exists: true, Length: <= MaxFileBytes })
                throw new InvalidDataException("A member HTML page is unavailable.");
            using FileStream htmlFile = new(htmlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] sample = new byte[65_536];
            int count = htmlFile.Read(sample);
            string header = Encoding.UTF8.GetString(sample, 0, count);
            string identity = WebUtility.HtmlEncode(member.Thread.Source + " /" + member.Thread.Board + "/ " + member.Thread.Thread);
            if (!header.Contains(identity, StringComparison.Ordinal))
                throw new InvalidDataException("A member page does not match its declared thread identity.");
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
            if (!info.Exists)
            {
                modified.Add(file.Path);
                observed.Append(file.Path).Append(":missing;");
                continue;
            }
            if (info.Length > MaxFileBytes) throw new InvalidDataException("An export file exceeds its safety limit.");
            if (info.Length != file.Length) modified.Add(file.Path);
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            observed.Append(file.Path).Append(':').Append(hash).Append(';');
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) && !modified.Contains(file.Path)) modified.Add(file.Path);
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
                    if (relative != "media" && relative != "threads" && !paths.Any(known => known.StartsWith(relative + "/", StringComparison.Ordinal)))
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
                    if (info.Length > MaxFileBytes) throw new InvalidDataException("An extra export file exceeds its safety limit.");
                    await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    extraObservations.Add(relative + ":" + info.Length + ":" + Convert.ToHexString(
                        await SHA256.HashDataAsync(input, token).ConfigureAwait(false)));
                }
            }
        }
        foreach (string item in extraObservations.OrderBy(item => item, StringComparer.Ordinal)) observed.Append(item).Append(';');
        return new ArchiveCollectionExportInspection(metadata, root, modified, extra.OrderBy(path => path, StringComparer.Ordinal).ToArray())
        { ObservedFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observed.ToString()))) };
    }

    internal static void RejectLinks(string path) => ArchiveExportMetadataFile.RejectLinks(path);
    internal static void ValidateRelative(string path)
    {
        ArchiveExportMetadataFile.ValidateRelative(path);
        if (path == Name) throw new InvalidDataException("Export inventory contains its own marker.");
    }
}
