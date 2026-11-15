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

/// <summary>Threads selected for one export: an explicit ordered list, or <c>null</c> for every archived thread.</summary>
internal sealed record ArchiveTransferExportRequest(IReadOnlyList<ResourceId>? Threads, ArchiveTransferExportMedia Media);

/// <summary>
/// The counted selection with the fixed ceilings of the native transfer engine. The ceilings are constants of the
/// package format and its exporter (NativeTransferArchiveSelection, NativeTransferArchiveExporter), which callers cannot
/// raise, so they are documented here and shown in the dialog instead of being settings.
/// </summary>
internal sealed record ArchiveTransferExportCount(int Threads, long Posts, int? MediaDirectories, bool MediaDirectoriesTruncated,
    long? EstimatedMediaBytes, int MissingThreads = 0)
{
    internal const int MaxThreads = NativeTransferArchiveSelection.MaxThreads;
    internal const int MaxPosts = 100_000;
    internal const int MaxAttachments = 100_000;
    internal const int MaxMediaDirectories = 32;

    internal bool ThreadsOverLimit => Threads > MaxThreads;
    internal bool PostsOverLimit => Posts > MaxPosts;
    internal bool MediaDirectoriesOverLimit => MediaDirectoriesTruncated || MediaDirectories > MaxMediaDirectories;
    internal bool IsEmpty => Threads == 0;
    internal bool WithinLimits => !IsEmpty && !ThreadsOverLimit && !PostsOverLimit && !MediaDirectoriesOverLimit;
}

internal sealed record ArchiveTransferExportProgress(ArchiveTransferExportPhase Phase, int Done, int Total, long? PayloadBytes = null);

internal sealed record ArchiveTransferExportResult(ArchiveTransferExportError Error, ArchiveTransferExportCount? Count = null,
    string? Destination = null, long? BytesWritten = null, bool StageRetained = false)
{
    internal bool Succeeded => Error == ArchiveTransferExportError.None;
}

internal sealed partial class DesktopSessionHost
{
    // Observer/pause seam only: the hook never supplies a snapshot, destination or result.
    internal Func<ArchiveTransferExportPhase, CancellationToken, Task>? ArchiveTransferExportCheckpointForTests { get; set; }

    /// <summary>
    /// Counts an export selection without freezing it: threads and posts from the archive summaries, distinct media folders,
    /// and (for a media export within the limits) the size of the distinct payloads recorded in their manifests.
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
        try
        {
            EnsureReady();
            linked.ThrowIfCancellationRequested();
            string? full = ArchiveTransferExportDestination.Normalize(destination);
            if (full == null) return new(ArchiveTransferExportError.InvalidDestination);
            // Early, advisory refusal so a long freeze is not wasted. The publisher repeats it atomically (no-clobber link/move).
            if (File.Exists(full) || Directory.Exists(full)) return new(ArchiveTransferExportError.DestinationExists, Destination: full);

            await using ArchiveReader reader = await _content.OpenReaderAsync(linked).ConfigureAwait(false);
            (ResourceId[] ids, count) = await CountArchiveTransferCoreAsync(reader, request.Threads, request.Media, progress,
                estimate: false, linked).ConfigureAwait(false);
            // A selected thread that was deleted after the dialog counted it is never silently dropped from the package.
            if (count.MissingThreads > 0) return new(ArchiveTransferExportError.StaleSnapshot, count);
            if (count.IsEmpty) return new(ArchiveTransferExportError.EmptySelection, count);
            if (!count.WithinLimits) return new(ArchiveTransferExportError.SelectionLimit, count);

            await CheckpointAsync(ArchiveTransferExportPhase.Reading, linked).ConfigureAwait(false);
            progress?.Report(new(ArchiveTransferExportPhase.Reading, 0, ids.Length));
            NativeTransferArchiveMediaOptions options = request.Media switch
            {
                ArchiveTransferExportMedia.MetadataOnly => new(ReadInventory: false, IncludeOriginals: false, IncludeDerived: false),
                ArchiveTransferExportMedia.Originals => new(ReadInventory: true, IncludeOriginals: true, IncludeDerived: false),
                _ => new(ReadInventory: true, IncludeOriginals: true, IncludeDerived: true)
            };
            TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader,
                new NativeTransferArchiveSelection(ids), DateTimeOffset.UtcNow, Guid.NewGuid(), options, linked).ConfigureAwait(false);
            if (!frozen.IsFrozen) return new(MapFreezeError(frozen.Error), count);

            long payloadBytes = frozen.Snapshot!.Payloads.Sum(payload => payload.Descriptor.Length);
            progress?.Report(new(ArchiveTransferExportPhase.Writing, 0, ids.Length, payloadBytes));
            await CheckpointAsync(ArchiveTransferExportPhase.Writing, linked).ConfigureAwait(false);
            TransferArchivePublication published = await NativeTransferArchivePublisher.PublishAsync(frozen.Snapshot, full, linked)
                .ConfigureAwait(false);
            return published.IsPublished
                ? new(ArchiveTransferExportError.None, count, full, published.BytesWritten, published.StageRetained)
                : new(MapPublicationError(published), count, full, StageRetained: published.StageRetained);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        { return new(ArchiveTransferExportError.Canceled, count); }
        catch (FileNotFoundException) { return new(ArchiveTransferExportError.EmptySelection, count); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException or FormatException)
        { return new(ArchiveTransferExportError.ReadFailure, count); }
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
        if (estimate && media != ArchiveTransferExportMedia.MetadataOnly && count.WithinLimits)
            count = count with { EstimatedMediaBytes = await EstimateMediaBytesAsync(directories, media, token).ConfigureAwait(false) };
        return (summaries.Select(summary => summary.Id).ToArray(), count);
    }

    // Reads at most 32 manifests (the folder ceiling) and sums the distinct payload lengths they record, the way the package
    // deduplicates payloads by SHA-256. It is an estimate: the exporter later verifies each file and leaves unavailable ones out.
    private static async Task<long?> EstimateMediaBytesAsync(IEnumerable<string> directories, ArchiveTransferExportMedia media,
        CancellationToken token)
    {
        Dictionary<string, long> payloads = new(StringComparer.Ordinal);
        try
        {
            foreach (string directory in directories)
            {
                token.ThrowIfCancellationRequested();
                MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory, token).ConfigureAwait(false);
                if (manifest.Status == MediaManifestStatus.Corrupt) return null;
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
        { return null; }
        return payloads.Values.Sum();
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
}
