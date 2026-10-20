using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Archive;

public sealed record ArchiveCollectionExportOmission(ResourceId Thread, string? PostId, string? MediaId,
    ArchiveExportOmissionReason Reason);
public sealed record ArchiveCollectionHtmlExportProgress(int ThreadsCompleted, int ThreadCount,
    int PostsWritten, int FilesCopied);
public sealed record ArchiveCollectionHtmlExportResult(string Directory, string IndexPath, int ThreadCount,
    int PostCount, int FileCount, IReadOnlyList<ArchiveCollectionExportOmission> Omissions)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public ArchiveCollectionExportMetadata? Metadata { get; init; }
    public string? BackupDirectory { get; init; }
}

/// <summary>Creates portable offline collections from frozen local per-thread captures. Never fetches or repairs media.</summary>
public static class ArchiveCollectionHtmlExporter
{
    internal static ResourceId[] FreezeMembers(IReadOnlyList<ResourceId> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        ResourceId[] frozen = members.Distinct().ToArray();
        if (frozen.Length == 0) throw new ArgumentException("Select at least one captured thread.", nameof(members));
        foreach (ResourceId id in frozen) ArchiveValidation.ValidateId(id);
        return frozen;
    }

    internal static string PagePath(ResourceId id) => "threads/" + Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new[] { id.Source, id.Board, id.Thread }))).ToLowerInvariant() + ".html";

    public static Task<ArchiveCollectionExportInspection> InspectAsync(string destinationDirectory,
        IReadOnlyList<ResourceId>? expectedMembers = null, CancellationToken cancellationToken = default) =>
        ArchiveCollectionExportMetadataFile.InspectAsync(destinationDirectory, expectedMembers, cancellationToken);

    public static Task<ArchiveCollectionHtmlExportResult> ExportAsync(ArchiveReader reader,
        IReadOnlyList<ResourceId> members, string destinationDirectory, ArchiveHtmlExportOptions? options = null,
        IProgress<ArchiveCollectionHtmlExportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(reader, FreezeMembers(members), destinationDirectory, options ?? new(), progress, cancellationToken, null);

    public static async Task<ArchiveCollectionHtmlExportResult> UpdateAsync(ArchiveReader reader,
        IReadOnlyList<ResourceId> members, string destinationDirectory, Guid expectedExportId, int expectedGeneration,
        string expectedObservedFingerprint, ArchiveHtmlExportOptions? options = null, bool confirmPreserveBackup = false,
        IProgress<ArchiveCollectionHtmlExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (expectedExportId == Guid.Empty || expectedGeneration < 1 || string.IsNullOrWhiteSpace(expectedObservedFingerprint))
            throw new ArgumentException("A previously inspected export identity, generation and fingerprint are required.");
        ResourceId[] frozen = FreezeMembers(members);
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        ArchiveCollectionExportInspection existing;
        using (ArchiveExportTargetLease lease = ArchiveExportTargetLease.Acquire(destination))
        {
            ArchiveCollectionExportReplacement.Recover(destination);
            existing = await InspectAsync(destination, frozen, cancellationToken).ConfigureAwait(false);
            if (existing.Metadata.ExportId != expectedExportId || existing.Metadata.Generation != expectedGeneration ||
                existing.ObservedFingerprint != expectedObservedFingerprint)
                throw new IOException("The selected collection changed; inspect it again before updating.");
        }
        return await RunAsync(reader, frozen, destinationDirectory, options ?? new(), progress, cancellationToken,
            new ArchiveCollectionExportUpdate(existing, confirmPreserveBackup)).ConfigureAwait(false);
    }

    private static async Task<ArchiveCollectionHtmlExportResult> RunAsync(ArchiveReader reader, ResourceId[] members,
        string destinationDirectory, ArchiveHtmlExportOptions options, IProgress<ArchiveCollectionHtmlExportProgress>? progress,
        CancellationToken token, ArchiveCollectionExportUpdate? update)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        string? parent = Path.GetDirectoryName(destination);
        if (parent == null || !System.IO.Directory.Exists(parent)) throw new DirectoryNotFoundException("The export parent directory must exist.");
        using ArchiveExportTargetLease targetLease = ArchiveExportTargetLease.Acquire(destination);
        ArchiveCollectionExportReplacement.Recover(destination);
        if (update == null && (System.IO.Directory.Exists(destination) || File.Exists(destination)))
            throw new IOException("The export destination already exists; inspect it or choose a new folder.");
        if (update != null)
        {
            ArchiveCollectionExportInspection current = await InspectAsync(destination, members, token).ConfigureAwait(false);
            if (current.Metadata.ExportId != update.Existing.Metadata.ExportId || current.Metadata.Generation != update.Existing.Metadata.Generation ||
                current.ObservedFingerprint != update.Existing.ObservedFingerprint)
                throw new IOException("The selected collection changed; inspect it again before updating.");
        }
        List<ArchiveExportSnapshot> snapshots = new();
        foreach (ResourceId member in members)
        {
            token.ThrowIfCancellationRequested();
            snapshots.Add(await reader.ReadExportAsync(member, token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("A selected thread is not in the local archive."));
        }
        if (update != null)
        {
            string profile = Path.GetDirectoryName(reader.DatabasePath)!;
            foreach (string source in snapshots.SelectMany(snapshot => snapshot.View.MediaDirectories).Append(profile))
                if (ArchiveHtmlExporter.IsSameOrAncestor(destination, source) || ArchiveHtmlExporter.IsSameOrAncestor(source, destination))
                    throw new InvalidDataException("An export cannot replace its profile or source media directory.");
        }
        return await Task.Run(() => WriteAsync(snapshots, destination, options, progress, token, update), token).ConfigureAwait(false);
    }

    private static async Task<ArchiveCollectionHtmlExportResult> WriteAsync(IReadOnlyList<ArchiveExportSnapshot> snapshots,
        string destination, ArchiveHtmlExportOptions options, IProgress<ArchiveCollectionHtmlExportProgress>? progress,
        CancellationToken token, ArchiveCollectionExportUpdate? update)
    {
        string stage = Path.Combine(Path.GetDirectoryName(destination)!, ".meowchan-collection-stage-" + Guid.NewGuid().ToString("N"));
        token.ThrowIfCancellationRequested();
        System.IO.Directory.CreateDirectory(stage);
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(stage, "threads"));
            Dictionary<ResourceId, HashSet<string>> captured = snapshots.ToDictionary(snapshot => snapshot.View.Summary.Id,
                snapshot => snapshot.View.Posts.Select(post => post.PostId).ToHashSet(StringComparer.Ordinal));
            Dictionary<string, string> copied = new(StringComparer.Ordinal);
            List<ArchiveCollectionExportOmission> omissions = new();
            List<string> warnings = new();
            List<ArchiveCollectionMember> members = new();
            int posts = 0, completed = 0;
            foreach (ArchiveExportSnapshot snapshot in snapshots)
            {
                token.ThrowIfCancellationRequested();
                ArchivedThreadView view = snapshot.View;
                string page = PagePath(view.Summary.Id);
                string? Resolve(ArchiveQuote quote) => quote.Target != null && captured.TryGetValue(quote.Target.Thread, out HashSet<string>? ids)
                    && ids.Contains(quote.Target.PostId) ? (quote.Target.Thread == view.Summary.Id ? "" : Path.GetFileName(PagePath(quote.Target.Thread)))
                        + "#" + ArchiveHtmlExporter.PostAnchor(quote.Target.PostId) : null;
                ArchiveHtmlExportResult rendered = await ArchiveHtmlExporter.RenderAsync(snapshot, stage, page, "../style.css", "../media/",
                    copied, Resolve, true, options, new ForwardProgress(value => progress?.Report(new(completed, snapshots.Count,
                        posts + value.PostsWritten, copied.Count))), token).ConfigureAwait(false);
                posts += rendered.PostCount;
                completed++;
                omissions.AddRange(rendered.Omissions.Select(item => new ArchiveCollectionExportOmission(view.Summary.Id,
                    item.PostId, item.MediaId, item.Reason)));
                warnings.AddRange(rendered.Warnings.Select(warning => view.Summary.Id.Source + " /" + view.Summary.Id.Board + "/ " + view.Summary.Id.Thread + ": " + warning));
                members.Add(new(view.Summary.Id, view.Summary.Url, page, view.Summary.LastCapturedAt, view.Summary.Revision, view.PostCount));
                progress?.Report(new(completed, snapshots.Count, posts, copied.Count));
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "style.css"), ArchiveHtmlExporter.Css, new UTF8Encoding(false), token).ConfigureAwait(false);
            await using (StreamWriter index = new(Path.Combine(stage, "index.html"), false, new UTF8Encoding(false)))
            {
                await index.WriteLineAsync("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'self'; img-src 'self'; media-src 'self'; base-uri 'none'; form-action 'none'\"><title>MeowChan collection</title><link rel=\"stylesheet\" href=\"style.css\"></head><body><header><h1>MeowChan collection</h1><p>Independent local thread captures, with shared verified media. Only captured posts are included.</p></header><main>");
                foreach (ArchiveExportSnapshot snapshot in snapshots)
                {
                    token.ThrowIfCancellationRequested();
                    ArchivedThreadView view = snapshot.View;
                    string page = PagePath(view.Summary.Id);
                    await index.WriteLineAsync("<article><h2><a href=\"" + page + "\">" + ArchiveHtmlExporter.Encode(view.Summary.Title) + "</a></h2><p>" + ArchiveHtmlExporter.Encode(view.Summary.Id.Source + " /" + view.Summary.Id.Board + "/ " + view.Summary.Id.Thread) + "</p><p>Last observed capture: " + ArchiveHtmlExporter.Encode(view.Summary.LastCapturedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "No captured posts") + " · " + view.PostCount + " posts</p>");
                    foreach (CapturedPost post in view.Posts)
                        await index.WriteLineAsync("<a href=\"" + page + "#" + ArchiveHtmlExporter.PostAnchor(post.PostId) + "\">#" + ArchiveHtmlExporter.Encode(post.PostId) + "</a> ");
                    await index.WriteLineAsync("</article>");
                }
                await index.WriteLineAsync("</main><footer><p>" + snapshots.Count + " threads · " + posts + " captured posts · " + copied.Count + " copied media files · " + omissions.Count + " attachment omissions</p><p>Requires no MeowChan profile, server or network connection.</p></footer></body></html>");
                await index.FlushAsync(token).ConfigureAwait(false);
            }
            ArchiveCollectionExportMetadata metadata = await ArchiveCollectionExportMetadataFile.CreateAsync(stage, members, options,
                update?.Existing.Metadata.ExportId ?? Guid.NewGuid(), checked((update?.Existing.Metadata.Generation ?? 0) + 1), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ArchiveExportPublication? publication = null;
            if (update == null) System.IO.Directory.Move(stage, destination);
            else publication = await ArchiveCollectionExportReplacement.PublishAsync(stage, destination, update, metadata, token).ConfigureAwait(false);
            if (publication?.Warning != null) warnings.Add(publication.Warning);
            return new(destination, Path.Combine(destination, "index.html"), snapshots.Count, posts, copied.Count, omissions.AsReadOnly())
            { Metadata = metadata, BackupDirectory = publication?.BackupDirectory, Warnings = warnings.Distinct().ToArray() };
        }
        finally { if (System.IO.Directory.Exists(stage)) System.IO.Directory.Delete(stage, true); }
    }

    private sealed class ForwardProgress(Action<ArchiveHtmlExportProgress> report) : IProgress<ArchiveHtmlExportProgress>
    { public void Report(ArchiveHtmlExportProgress value) => report(value); }
}
