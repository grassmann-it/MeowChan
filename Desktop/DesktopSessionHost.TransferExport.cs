using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

/// <summary>What a transfer package holds besides the thread text (posts and attachment metadata are always written).</summary>
internal enum ArchiveTransferExportMedia
{
    /// <summary>No media bytes; attachments are listed as omitted. The media folders are not opened.</summary>
    MetadataOnly,
    /// <summary>The verified original files of the passive media inventory.</summary>
    Originals,
    /// <summary>Originals plus derived previews and video posters of the passive media inventory.</summary>
    Everything
}

internal enum ArchiveTransferExportPhase { Counting, Reading, Writing }

internal enum ArchiveTransferExportError
{
    None, Canceled, Busy, ProfileUnavailable, EmptySelection, SelectionLimit, InvalidDestination, DestinationExists,
    StaleSnapshot, ValidationFailed, MediaUnavailable, ReadFailure, WriteFailure
}

/// <summary>
/// Threads selected for one export: an explicit ordered list, or <c>null</c> for every archived thread. With
/// <paramref name="Split"/> (Full export) the selection is written as several packages when one cannot hold it.
/// </summary>
internal sealed record ArchiveTransferExportRequest(IReadOnlyList<ResourceId>? Threads, ArchiveTransferExportMedia Media,
    bool Split = false);

/// <summary>
/// The counted selection with the fixed ceilings of the native transfer engine. The ceilings are constants of the
/// package format and its exporter (NativeTransferArchiveSelection, NativeTransferArchiveExporter, TransferEnvelopeLimits),
/// which callers cannot raise, so they are documented here and shown in the dialog instead of being settings. Full export
/// does not raise them either: it writes as many packages as needed, each within them.
/// </summary>
internal sealed record ArchiveTransferExportCount(int Threads, long Posts, int? MediaDirectories, bool MediaDirectoriesTruncated,
    long? EstimatedMediaBytes, int MissingThreads = 0, int? EstimatedMediaFiles = null)
{
    internal const int MaxThreads = NativeTransferArchiveSelection.MaxThreads;
    internal const int MaxPosts = 100_000;
    internal const int MaxAttachments = 100_000;
    internal const int MaxMediaDirectories = NativeTransferArchiveExporter.MaxSelectionDirectories;
    internal const int MaxMediaFiles = NativeTransferArchiveExporter.MaxPayloads;
    internal const long MaxMediaBytes = NativeTransferArchiveExporter.MaxTotal;

    internal bool ThreadsOverLimit => Threads > MaxThreads;
    internal bool PostsOverLimit => Posts > MaxPosts;
    internal bool MediaDirectoriesOverLimit => MediaDirectoriesTruncated || MediaDirectories > MaxMediaDirectories;
    internal bool IsEmpty => Threads == 0;
    /// <summary>The counted ceilings of ONE package (threads, posts, media folders). The media size is an estimate, see below.</summary>
    internal bool WithinLimits => !IsEmpty && !ThreadsOverLimit && !PostsOverLimit && !MediaDirectoriesOverLimit;
    /// <summary>The manifests record more media than one package can carry; the freeze, not this estimate, decides.</summary>
    internal bool LikelyTooLargeForOnePackage => EstimatedMediaBytes > MaxMediaBytes || EstimatedMediaFiles > MaxMediaFiles;
}

/// <summary>
/// Progress of an export. <see cref="Package"/>/<see cref="Packages"/> number the package being written; with Full export the
/// planned count can grow when a package had to be split again.
/// </summary>
internal sealed record ArchiveTransferExportProgress(ArchiveTransferExportPhase Phase, int Done, int Total, long? PayloadBytes = null,
    int Package = 1, int Packages = 1);

internal sealed record ArchiveTransferExportPart(string Path, long Bytes, int Threads);

internal sealed record ArchiveTransferExportSkip(ResourceId Thread, ArchiveTransferExportError Error);

internal sealed record ArchiveTransferExportResult(ArchiveTransferExportError Error, ArchiveTransferExportCount? Count = null,
    string? Destination = null, long? BytesWritten = null, bool StageRetained = false)
{
    internal bool Succeeded => Error == ArchiveTransferExportError.None;
    /// <summary>Every complete package written, in order, also when a later one failed (each is importable on its own).</summary>
    internal IReadOnlyList<ArchiveTransferExportPart> Parts { get; init; } = Array.Empty<ArchiveTransferExportPart>();
    /// <summary>Full export: threads too large for any package with media, written as metadata only instead.</summary>
    internal IReadOnlyList<ResourceId> WithoutMedia { get; init; } = Array.Empty<ResourceId>();
    /// <summary>Full export: threads that cannot be written into any package, with the reason.</summary>
    internal IReadOnlyList<ArchiveTransferExportSkip> Skipped { get; init; } = Array.Empty<ArchiveTransferExportSkip>();
}

/// <summary>
/// How Full export packs threads before freezing: kept below the engine ceilings by a margin, because the media numbers are
/// manifest estimates. A package that still does not fit is split in half and frozen again.
/// </summary>
internal sealed record ArchiveTransferPackageBudget(int Threads, long Posts, int MediaDirectories, int MediaFiles, long MediaBytes)
{
    internal static ArchiveTransferPackageBudget Default { get; } = new(ArchiveTransferExportCount.MaxThreads, 90_000,
        ArchiveTransferExportCount.MaxMediaDirectories, 9_000, 1792L * 1024 * 1024);
}

internal sealed partial class DesktopSessionHost
{
    // Observer/pause seam only: the hook never supplies a snapshot, destination or result.
    internal Func<ArchiveTransferExportPhase, CancellationToken, Task>? ArchiveTransferExportCheckpointForTests { get; set; }
    // Smaller packing/metadata budgets for synthetic tests; production uses the defaults, which callers cannot enlarge.
    internal ArchiveTransferPackageBudget? ArchiveTransferPackageBudgetForTests { get; set; }
    internal NativeTransferArchiveMetadataLimits? ArchiveTransferMetadataLimitsForTests { get; set; }

    /// <summary>
    /// Counts an export selection without freezing it: threads and posts from the archive summaries, distinct media folders,
    /// and (for a media export) the size and number of the distinct payloads recorded in their manifests.
    /// </summary>
    internal async Task<ArchiveTransferExportCount> CountArchiveTransferExportAsync(ArchiveTransferExportRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ResourceId[]? frozen = request.Threads?.ToArray();
        return await RunInteractiveAsync(nameof(CountArchiveTransferExportAsync), "archive-transfer", async () =>
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _startupCancellation.Token);
            EnsureReady();
            await using ArchiveReader reader = await _content.OpenReaderAsync(lifetime.Token).ConfigureAwait(false);
            var counted = await CountArchiveTransferCoreAsync(reader, frozen, request.Media, null, estimate: true, lifetime.Token)
                .ConfigureAwait(false);
            return counted.Count;
        }, captureAfter: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Freezes the selection with <see cref="NativeTransferArchiveExporter"/> and publishes it create-only with
    /// <see cref="NativeTransferArchivePublisher"/>. Runs on a worker under an ordinary profile admission, like the other
    /// archive operations: a profile-exclusive transfer import or profile transition cannot overlap it, and shutdown cancels
    /// it. It does not hold the host's mutation serializer because it never writes the profile; concurrent captures are
    /// detected by the exporter's own frozen-snapshot checks and reported as <see cref="ArchiveTransferExportError.StaleSnapshot"/>.
    /// Full export (<see cref="ArchiveTransferExportRequest.Split"/>) freezes and publishes one package at a time, so only one
    /// bounded snapshot is held; every package passes the unchanged format limits and validation.
    /// </summary>
    internal async Task<ArchiveTransferExportResult> ExportArchiveTransferAsync(ArchiveTransferExportRequest request,
        string destination, IProgress<ArchiveTransferExportProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArchiveTransferExportRequest frozen = request with { Threads = request.Threads?.ToArray() };
        try
        {
            return await RunInteractiveAsync(nameof(ExportArchiveTransferAsync), "archive-transfer",
                () => ExportArchiveTransferCoreAsync(frozen, destination, progress, token), captureAfter: false).ConfigureAwait(false);
        }
        catch (DesktopSessionBusyException) { return new(ArchiveTransferExportError.Busy); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(ArchiveTransferExportError.Canceled); }
        catch (InvalidOperationException) { return new(ArchiveTransferExportError.ProfileUnavailable); }
    }

    private async Task<ArchiveTransferExportResult> ExportArchiveTransferCoreAsync(ArchiveTransferExportRequest request,
        string destination, IProgress<ArchiveTransferExportProgress>? progress, CancellationToken token)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _startupCancellation.Token);
        CancellationToken linked = lifetime.Token;
        ArchiveTransferExportCount? count = null;
        List<ArchiveTransferExportPart> parts = new();
        List<ResourceId> withoutMedia = new();
        List<ArchiveTransferExportSkip> skipped = new();
        bool retained = false;
        ArchiveTransferExportResult Done(ArchiveTransferExportError error) => new(error, count,
            parts.Count > 0 ? parts[0].Path : null, parts.Count > 0 ? parts[0].Bytes : null, retained)
        { Parts = parts.ToArray(), WithoutMedia = withoutMedia.ToArray(), Skipped = skipped.ToArray() };
        try
        {
            EnsureReady();
            linked.ThrowIfCancellationRequested();
            string? full = ArchiveTransferExportDestination.Normalize(destination);
            if (full == null) return new(ArchiveTransferExportError.InvalidDestination);
            // Early, advisory refusal so a long freeze is not wasted. The publisher repeats it atomically (no-clobber link/move).
            if (ArchiveTransferExportDestination.Exists(full)) return new(ArchiveTransferExportError.DestinationExists, Destination: full);

            await using ArchiveReader reader = await _content.OpenReaderAsync(linked).ConfigureAwait(false);
            (ResourceId[] ids, count) = await CountArchiveTransferCoreAsync(reader, request.Threads, request.Media, progress,
                estimate: false, linked).ConfigureAwait(false);
            // A selected thread that was deleted after the dialog counted it is never silently dropped from the package.
            if (count.MissingThreads > 0) return new(ArchiveTransferExportError.StaleSnapshot, count);
            if (count.IsEmpty) return new(ArchiveTransferExportError.EmptySelection, count);
            if (!request.Split && !count.WithinLimits) return new(ArchiveTransferExportError.SelectionLimit, count);

            LinkedList<ResourceId[]> pending = new(request.Split
                ? await PlanArchiveTransferPackagesAsync(reader, ids, request.Media, ArchiveTransferPackageBudgetForTests
                    ?? ArchiveTransferPackageBudget.Default, linked).ConfigureAwait(false)
                : new[] { ids });
            if (pending.Count > 1)
                for (int part = 1; part <= pending.Count; part++)
                    if (ArchiveTransferExportDestination.Exists(ArchiveTransferExportDestination.PartPath(full, part)))
                        return new(ArchiveTransferExportError.DestinationExists, count, ArchiveTransferExportDestination.PartPath(full, part));
            DateTimeOffset exportedAt = DateTimeOffset.UtcNow;
            bool? numbered = null;
            while (pending.First is { } next)
            {
                pending.RemoveFirst();
                ResourceId[] group = next.Value;
                int package = parts.Count + 1, packages = parts.Count + 1 + pending.Count;
                await CheckpointAsync(ArchiveTransferExportPhase.Reading, linked).ConfigureAwait(false);
                progress?.Report(new(ArchiveTransferExportPhase.Reading, parts.Count, packages, null, package, packages));
                ArchiveTransferExportMedia used = request.Media;
                TransferArchiveFreezeResult frozen = await FreezeGroupAsync(reader, group, used, exportedAt, linked).ConfigureAwait(false);
                if (!frozen.IsFrozen)
                {
                    ArchiveTransferExportError error = MapFreezeError(frozen.Error);
                    if (!request.Split || error is not (ArchiveTransferExportError.SelectionLimit
                        or ArchiveTransferExportError.MediaUnavailable or ArchiveTransferExportError.ValidationFailed))
                        return Done(error);
                    if (group.Length > 1)
                    {
                        // Halve and retry in order: limits the plan could only estimate (text, catalog, payloads) still hold.
                        int half = group.Length / 2;
                        pending.AddFirst(group[half..]);
                        pending.AddFirst(group[..half]);
                        continue;
                    }
                    // One thread alone: keep its text rather than lose it, and say so in the result.
                    if (used != ArchiveTransferExportMedia.MetadataOnly && error is not ArchiveTransferExportError.ValidationFailed)
                    {
                        used = ArchiveTransferExportMedia.MetadataOnly;
                        frozen = await FreezeGroupAsync(reader, group, used, exportedAt, linked).ConfigureAwait(false);
                    }
                    if (!frozen.IsFrozen)
                    {
                        skipped.Add(new(group[0], frozen.Error == TransferArchiveError.None ? error : MapFreezeError(frozen.Error)));
                        continue;
                    }
                }
                // One package keeps the chosen name; as soon as a second one exists or is planned, all are numbered.
                numbered ??= pending.Count > 0;
                string path = numbered.Value ? ArchiveTransferExportDestination.PartPath(full, parts.Count + 1) : full;
                long payloadBytes = frozen.Snapshot!.Payloads.Sum(payload => payload.Descriptor.Length);
                progress?.Report(new(ArchiveTransferExportPhase.Writing, parts.Count, packages, payloadBytes, package, packages));
                await CheckpointAsync(ArchiveTransferExportPhase.Writing, linked).ConfigureAwait(false);
                TransferArchivePublication published = await NativeTransferArchivePublisher.PublishAsync(frozen.Snapshot, path, linked)
                    .ConfigureAwait(false);
                retained |= published.StageRetained;
                if (!published.IsPublished)
                {
                    ArchiveTransferExportResult failed = Done(MapPublicationError(published));
                    return parts.Count == 0 ? failed with { Destination = path } : failed;
                }
                parts.Add(new(path, published.BytesWritten ?? 0, group.Length));
                if (used != request.Media) withoutMedia.Add(group[0]);
            }
            return Done(parts.Count > 0 ? ArchiveTransferExportError.None
                : skipped.Count > 0 ? skipped[0].Error : ArchiveTransferExportError.EmptySelection);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        { return Done(ArchiveTransferExportError.Canceled); }
        catch (FileNotFoundException) { return Done(ArchiveTransferExportError.EmptySelection); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException or FormatException)
        { return Done(ArchiveTransferExportError.ReadFailure); }
    }

    private Task<TransferArchiveFreezeResult> FreezeGroupAsync(ArchiveReader reader, ResourceId[] ids,
        ArchiveTransferExportMedia media, DateTimeOffset exportedAt, CancellationToken token)
    {
        NativeTransferArchiveMediaOptions options = media switch
        {
            ArchiveTransferExportMedia.MetadataOnly => new(ReadInventory: false, IncludeOriginals: false, IncludeDerived: false),
            ArchiveTransferExportMedia.Originals => new(ReadInventory: true, IncludeOriginals: true, IncludeDerived: false),
            _ => new(ReadInventory: true, IncludeOriginals: true, IncludeDerived: true)
        };
        return NativeTransferArchiveExporter.FreezeAsync(reader, new NativeTransferArchiveSelection(ids), exportedAt, Guid.NewGuid(),
            options, token, ArchiveTransferMetadataLimitsForTests);
    }

    // Greedy packing in archive order. Holds one small cost record per thread and one per media folder, never a view,
    // post or payload; each folder's manifest is read once and closed.
    private static async Task<List<ResourceId[]>> PlanArchiveTransferPackagesAsync(ArchiveReader reader, ResourceId[] ids,
        ArchiveTransferExportMedia media, ArchiveTransferPackageBudget budget, CancellationToken token)
    {
        Dictionary<string, (long Bytes, int Files)> folders = new(StringComparer.Ordinal);
        List<ResourceId[]> groups = new();
        List<ResourceId> current = new();
        HashSet<string> currentFolders = new(StringComparer.Ordinal);
        long posts = 0, bytes = 0;
        int files = 0;
        foreach (ResourceId id in ids)
        {
            token.ThrowIfCancellationRequested();
            ArchiveThreadSummary? summary = await reader.ReadThreadSummaryAsync(id, token).ConfigureAwait(false);
            string[] roots = (await reader.GetMediaDirectoriesAsync(id, token).ConfigureAwait(false)).Distinct(StringComparer.Ordinal).ToArray();
            long threadBytes = 0;
            int threadFiles = 0;
            if (media != ArchiveTransferExportMedia.MetadataOnly)
                foreach (string root in roots)
                {
                    if (!folders.TryGetValue(root, out var cost))
                        folders[root] = cost = await EstimateFolderAsync(root, media, token).ConfigureAwait(false);
                    threadBytes += cost.Bytes; threadFiles += cost.Files;
                }
            long threadPosts = Math.Max(0, summary?.PostCount ?? 0);
            int newFolders = roots.Count(root => !currentFolders.Contains(root));
            if (current.Count > 0 && (current.Count + 1 > budget.Threads || posts + threadPosts > budget.Posts
                || currentFolders.Count + newFolders > budget.MediaDirectories || files + threadFiles > budget.MediaFiles
                || bytes + threadBytes > budget.MediaBytes))
            {
                groups.Add(current.ToArray());
                current.Clear(); currentFolders.Clear(); posts = 0; bytes = 0; files = 0;
            }
            current.Add(id); currentFolders.UnionWith(roots);
            posts += threadPosts; bytes += threadBytes; files += threadFiles;
        }
        if (current.Count > 0) groups.Add(current.ToArray());
        return groups;
    }

    private static async Task<(long Bytes, int Files)> EstimateFolderAsync(string directory, ArchiveTransferExportMedia media,
        CancellationToken token)
    {
        (long? bytes, int? files) = await EstimateMediaAsync(new[] { directory }, media, token).ConfigureAwait(false);
        return (bytes ?? 0, files ?? 0);
    }

    private async Task CheckpointAsync(ArchiveTransferExportPhase phase, CancellationToken token)
    {
        if (ArchiveTransferExportCheckpointForTests is { } checkpoint) await checkpoint(phase, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    // One read per summary and per media-folder list; no thread view, post or media byte is materialized.
    private static async Task<(ResourceId[] Ids, ArchiveTransferExportCount Count)> CountArchiveTransferCoreAsync(
        ArchiveReader reader, IReadOnlyList<ResourceId>? threads, ArchiveTransferExportMedia media,
        IProgress<ArchiveTransferExportProgress>? progress, bool estimate, CancellationToken token)
    {
        List<ArchiveThreadSummary> summaries = new();
        int missing = 0;
        if (threads == null)
        {
            int total = -1;
            HashSet<ResourceId> seen = new();
            for (int offset = 0; total < 0 || offset < total; offset += 500)
            {
                token.ThrowIfCancellationRequested();
                ArchiveThreadPage page = await reader.ListThreadsAsync(offset, 500, token).ConfigureAwait(false);
                if (total < 0) total = page.TotalCount;
                if (page.Items.Count == 0) break;
                foreach (ArchiveThreadSummary summary in page.Items) if (seen.Add(summary.Id)) summaries.Add(summary);
                progress?.Report(new(ArchiveTransferExportPhase.Counting, summaries.Count, Math.Max(total, summaries.Count)));
            }
        }
        else
        {
            ResourceId[] distinct = threads.Distinct().ToArray();
            foreach (ResourceId id in distinct)
            {
                token.ThrowIfCancellationRequested();
                ArchiveThreadSummary? summary = await reader.ReadThreadSummaryAsync(id, token).ConfigureAwait(false);
                if (summary == null) missing++;
                else summaries.Add(summary);
                progress?.Report(new(ArchiveTransferExportPhase.Counting, summaries.Count + missing, distinct.Length));
            }
        }
        long posts = summaries.Sum(summary => (long)Math.Max(0, summary.PostCount));
        int? directoryCount = null;
        bool truncated = false;
        HashSet<string> directories = new(StringComparer.Ordinal);
        if (summaries.Count <= ArchiveTransferExportCount.MaxThreads)
        {
            foreach (ArchiveThreadSummary summary in summaries)
            {
                token.ThrowIfCancellationRequested();
                // The same registered roots the exporter freezes (ArchivedThreadView.MediaDirectories), compared ordinally.
                foreach (string directory in await reader.GetMediaDirectoriesAsync(summary.Id, token).ConfigureAwait(false))
                    directories.Add(directory);
                if (directories.Count > ArchiveTransferExportCount.MaxMediaDirectories) { truncated = true; break; }
            }
            directoryCount = directories.Count;
        }
        ArchiveTransferExportCount count = new(summaries.Count, posts, directoryCount, truncated, null, missing);
        if (estimate && media != ArchiveTransferExportMedia.MetadataOnly && directoryCount != null && !truncated)
        {
            (long? bytes, int? files) = await EstimateMediaAsync(directories, media, token).ConfigureAwait(false);
            count = count with { EstimatedMediaBytes = bytes, EstimatedMediaFiles = files };
        }
        return (summaries.Select(summary => summary.Id).ToArray(), count);
    }

    // Reads at most 1000 manifests (the folder ceiling), one at a time, and sums the distinct payloads they record, the way the
    // package deduplicates payloads by SHA-256. It is an estimate: the exporter later verifies each file and leaves
    // unavailable ones out.
    private static async Task<(long? Bytes, int? Files)> EstimateMediaAsync(IEnumerable<string> directories,
        ArchiveTransferExportMedia media, CancellationToken token)
    {
        Dictionary<string, long> payloads = new(StringComparer.Ordinal);
        try
        {
            foreach (string directory in directories)
            {
                token.ThrowIfCancellationRequested();
                MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory, token).ConfigureAwait(false);
                if (manifest.Status == MediaManifestStatus.Corrupt) return (null, null);
                for (int offset = 0; offset < manifest.Count; offset += 500)
                    foreach (MediaAssetReference asset in manifest.GetPage(offset, 500))
                        if (asset.Variant == "original") payloads.TryAdd(asset.Sha256, asset.Length);
                if (media == ArchiveTransferExportMedia.Everything)
                    foreach (var derived in manifest.DerivedRepresentations)
                        if (derived.Blob is { } blob) payloads.TryAdd(blob.Actual.Sha256, blob.Actual.Length);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or FormatException or System.Text.Json.JsonException)
        { return (null, null); }
        return (payloads.Values.Sum(), payloads.Count);
    }

    private static ArchiveTransferExportError MapFreezeError(TransferArchiveError error) => error switch
    {
        TransferArchiveError.SelectionLimit => ArchiveTransferExportError.SelectionLimit,
        TransferArchiveError.StaleSnapshot or TransferArchiveError.MissingThread => ArchiveTransferExportError.StaleSnapshot,
        TransferArchiveError.InvalidMedia or TransferArchiveError.UnrepresentedAssets => ArchiveTransferExportError.MediaUnavailable,
        TransferArchiveError.InvalidArchive or TransferArchiveError.InvalidCatalog => ArchiveTransferExportError.ValidationFailed,
        _ => ArchiveTransferExportError.ReadFailure
    };

    private static ArchiveTransferExportError MapPublicationError(TransferArchivePublication publication) => publication.Error switch
    {
        TransferPublicationError.InvalidDestination => ArchiveTransferExportError.InvalidDestination,
        TransferPublicationError.DestinationExists => ArchiveTransferExportError.DestinationExists,
        TransferPublicationError.StaleSnapshot => ArchiveTransferExportError.StaleSnapshot,
        TransferPublicationError.StageValidationFailed => ArchiveTransferExportError.ValidationFailed,
        TransferPublicationError.ExportFailed => publication.ArchiveError switch
        {
            TransferArchiveError.StaleSnapshot => ArchiveTransferExportError.StaleSnapshot,
            TransferArchiveError.InvalidMedia or TransferArchiveError.UnrepresentedAssets => ArchiveTransferExportError.MediaUnavailable,
            TransferArchiveError.InvalidCatalog or TransferArchiveError.InvalidArchive => ArchiveTransferExportError.ValidationFailed,
            _ => ArchiveTransferExportError.WriteFailure
        },
        _ => ArchiveTransferExportError.WriteFailure
    };
}

/// <summary>The default package name and the destination shape the create-only publisher accepts.</summary>
internal static class ArchiveTransferExportDestination
{
    /// <summary>The extension the import picker accepts (<c>*.zip</c>).</summary>
    internal const string Extension = "zip";

    internal static string DefaultFileName(DateTime date) =>
        "meowchan-archive-" + date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "." + Extension;

    /// <summary>A fully qualified file path whose parent folder exists, or <c>null</c>. Nothing is created.</summary>
    internal static string? Normalize(string? destination)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destination) || destination.Contains('\0') || !Path.IsPathFullyQualified(destination)
                || Path.EndsInDirectorySeparator(destination)) return null;
            string full = Path.GetFullPath(destination);
            string? parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(Path.GetFileName(full)) || !Directory.Exists(parent)) return null;
            return full;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static bool Exists(string full) => File.Exists(full) || Directory.Exists(full);

    /// <summary>Full export's numbered name next to the chosen one: <c>name-part-01.zip</c>, <c>name-part-02.zip</c>, …</summary>
    internal static string PartPath(string full, int part)
    {
        string directory = Path.GetDirectoryName(full)!;
        string extension = Path.GetExtension(full);
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(full) + "-part-"
            + part.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + extension);
    }
}
