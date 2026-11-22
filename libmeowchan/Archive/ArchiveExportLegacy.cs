using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Archive;

public sealed record ArchiveLegacyExportCandidate(string Directory, bool SourceHeaderMatches);

public static class ArchiveExportLegacy
{
    /// <summary>Explicit one-time association of a bounded, source-matching legacy export.</summary>
    public static async Task<ArchiveExportInspection> AssociateAsync(ArchiveReader reader, ResourceId thread,
        string directory, CancellationToken token = default)
    {
        string destination = ValidateDestination(reader, directory, token);
        using ArchiveExportTargetLease targetLease = ArchiveExportTargetLease.Acquire(destination);
        await WriteMarkerAsync(reader, thread, destination, token).ConfigureAwait(false);
        // The marker has been committed. Finish returning its inspection even if cancellation arrives now.
        return await ArchiveExportMetadataFile.InspectAsync(destination, thread, CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task AdoptAsync(ArchiveReader reader, ResourceId thread,
        string directory, CancellationToken token)
    {
        string destination = ValidateDestination(reader, directory, token);
        using ArchiveExportTargetLease targetLease = ArchiveExportTargetLease.Acquire(destination);
        await WriteMarkerAsync(reader, thread, destination, token).ConfigureAwait(false);
    }

    private static string ValidateDestination(ArchiveReader reader, string directory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reader);
        token.ThrowIfCancellationRequested();
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string profile = Path.GetDirectoryName(reader.DatabasePath)!;
        if (ArchiveHtmlExporter.IsSameOrAncestor(destination, profile) ||
            ArchiveHtmlExporter.IsSameOrAncestor(profile, destination))
            throw new InvalidDataException("A legacy export cannot be associated inside or over its source profile.");
        return destination;
    }

    private static async Task WriteMarkerAsync(ArchiveReader reader, ResourceId thread, string destination, CancellationToken token)
    {
        string parent = Path.GetDirectoryName(destination)!;
        ArchiveExportReplacement.Recover(destination);
        if (!FindCandidates(parent, thread, token).Any(candidate => candidate.Directory == destination && candidate.SourceHeaderMatches))
            throw new InvalidDataException("This legacy folder does not establish the selected thread identity.");
        if (File.Exists(Path.Combine(destination, ArchiveExportMetadataFile.Name)))
            throw new IOException("This export already has metadata.");
        ArchivedThreadView view = await reader.ReadThreadAsync(thread, token).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The thread is not in the local archive.");
        if (view.MediaDirectories.Any(media => ArchiveHtmlExporter.IsSameOrAncestor(destination, media) ||
            ArchiveHtmlExporter.IsSameOrAncestor(media, destination)))
            throw new InvalidDataException("A legacy export cannot be associated over source media.");
        await ArchiveExportMetadataFile.CreateAsync(destination, view, new ArchiveHtmlExportOptions(true),
            Guid.NewGuid(), 1, token, adoptedLegacy: true).ConfigureAwait(false);
    }

    /// <summary>Bounded hints only. A legacy folder has no safe update identity and remains create-only.</summary>
    public static IReadOnlyList<ArchiveLegacyExportCandidate> FindCandidates(string parentDirectory,
        ResourceId thread, CancellationToken token = default)
    {
        ArchiveExportMetadataFile.RejectLinks(parentDirectory);
        List<ArchiveLegacyExportCandidate> matches = new();
        int inspected = 0;
        string expected = WebUtility.HtmlEncode(thread.Source + " /" + thread.Board + "/ " + thread.Thread);
        foreach (string directory in Directory.EnumerateDirectories(parentDirectory))
        {
            token.ThrowIfCancellationRequested();
            if (++inspected > 500) break;
            string name = Path.GetFileName(directory);
            if (!name.StartsWith("ochan-", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("meowchan-", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                ArchiveExportMetadataFile.RejectLinks(directory);
                if (File.Exists(Path.Combine(directory, ArchiveExportMetadataFile.Name))) continue;
                string htmlPath = Path.Combine(directory, "index.html");
                ArchiveExportMetadataFile.RejectLinks(htmlPath);
                FileInfo info = new(htmlPath);
                if (!info.Exists || info.Length > 32_000_000) continue;
                using FileStream input = new(htmlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] sample = new byte[Math.Min(65_536, (int)info.Length)];
                int count = input.Read(sample);
                string html = Encoding.UTF8.GetString(sample, 0, count);
                bool sourceMatches = html.Contains(expected, StringComparison.Ordinal);
                if (sourceMatches) matches.Add(new ArchiveLegacyExportCandidate(directory, true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return matches;
    }
}
