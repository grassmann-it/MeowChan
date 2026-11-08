using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Transfer;

internal enum TransferArchiveError
{
    None, MissingThread, InvalidArchive, InvalidCatalog, InvalidMedia, SelectionLimit,
    UnrepresentedAssets, StaleSnapshot, WriteFailure, IoFailure
}
internal sealed record TransferArchiveFreezeResult(NativeTransferArchiveSnapshot? Snapshot, TransferArchiveError Error)
{
    internal bool IsFrozen => Snapshot != null && Error == TransferArchiveError.None;
}
internal sealed record TransferArchiveWriteResult(NativeTransferPackage? Package, TransferArchiveError Error,
    TransferWriteError WriteError = TransferWriteError.None, long? BytesWritten = null)
{
    internal bool IsComplete => Package != null && Error == TransferArchiveError.None;
}
// No open media handles or operational authority. Private paths are deliberately not in ToString.
internal sealed class NativeTransferArchiveSnapshot
{
    internal NativeTransferArchiveSnapshot(ArchiveReader reader, ArchivedThreadView view, byte[] metadata,
        NativeTransferCatalog catalog, ImmutableArray<MediaAssetSnapshot> manifests,
        ImmutableArray<TransferPayloadSource> payloads, Guid packageId)
    { Reader = reader; View = view; Metadata = ImmutableArray.CreateRange(metadata); Catalog = catalog; Manifests = manifests; Payloads = payloads; PackageId = packageId; Views = [view]; }
    internal ArchiveReader Reader { get; }
    internal ArchivedThreadView View { get; }
    internal ImmutableArray<ArchivedThreadView> Views { get; init; }
    internal NativeTransferArchiveMetadataLimits MetadataLimits { get; init; } = NativeTransferArchiveMetadataLimits.Default;
    internal ImmutableArray<byte> Metadata { get; }
    internal NativeTransferCatalog Catalog { get; }
    internal ImmutableArray<MediaAssetSnapshot> Manifests { get; }
    internal ImmutableArray<TransferPayloadSource> Payloads { get; }
    internal Guid PackageId { get; }
}

// Admission limits, not a new wire schema. Smaller values provide a deterministic budget
// seam for synthetic tests; callers cannot enlarge the production ceilings.
internal sealed class NativeTransferArchiveMetadataLimits
{
    internal const long MaxStoredTextBytes = 32L * 1024 * 1024;
    internal const int MaxCatalogBytes = 16 * 1024 * 1024;
    internal static NativeTransferArchiveMetadataLimits Default { get; } = new();

    internal NativeTransferArchiveMetadataLimits(long storedTextBytes = MaxStoredTextBytes, int catalogBytes = MaxCatalogBytes)
    {
        if (storedTextBytes < 0 || storedTextBytes > MaxStoredTextBytes) throw new ArgumentOutOfRangeException(nameof(storedTextBytes));
        if (catalogBytes < 0 || catalogBytes > MaxCatalogBytes) throw new ArgumentOutOfRangeException(nameof(catalogBytes));
        StoredTextBytes = storedTextBytes; CatalogBytes = catalogBytes;
    }
    internal long StoredTextBytes { get; }
    internal int CatalogBytes { get; }
}

/// <summary>
/// Bounded explicit-selection offline archive-to-stream integration. Does not publish a destination,
/// mutate a profile or open a provider. Any failed/cancelled stage must be discarded by caller.
/// Current observations plus exact historical manifest bindings; unbound retained assets still reject.
/// </summary>
internal static class NativeTransferArchiveExporter
{
    private const int MaxDirectories = 32, MaxAssets = 100_000, MaxPayloads = 9998;
    private const long MaxPayload = 1024L * 1024 * 1024, MaxTotal = 2L * 1024 * 1024 * 1024;
    private const long MaxLegacyComparisons = 1_000_000;
    private const int MaxBindingVisits = 100_000;

    internal static Task<TransferArchiveFreezeResult> FreezeAsync(ArchiveReader reader, ResourceId id,
        DateTimeOffset exportedAtUtc, Guid packageId, bool includeOriginals = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(id);
        return FreezeAsync(reader, new NativeTransferArchiveSelection([id]), exportedAtUtc, packageId,
            includeOriginals, cancellationToken);
    }

    internal static async Task<TransferArchiveFreezeResult> FreezeAsync(ArchiveReader reader, NativeTransferArchiveSelection selection,
        DateTimeOffset exportedAtUtc, Guid packageId, bool includeOriginals = true, CancellationToken cancellationToken = default,
        NativeTransferArchiveMetadataLimits? metadataLimits = null)
    {
        ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (packageId == Guid.Empty) return FreezeFail(TransferArchiveError.InvalidArchive);
            ArchiveExportMetadataFile.RejectLinks(reader.DatabasePath);
            metadataLimits ??= NativeTransferArchiveMetadataLimits.Default;
            // The one-thread path keeps its existing reader/serializer admission semantics.
            // Multi-thread preflight holds no views and includes nonportable stored text.
            MetadataBudget? budget = null;
            if (selection.Threads.Length > 1)
            {
                if (!await PreflightMetadataAsync(reader, selection.Threads, metadataLimits, cancellationToken).ConfigureAwait(false))
                    return FreezeFail(TransferArchiveError.SelectionLimit);
                budget = new(metadataLimits, exportedAtUtc, cancellationToken);
            }
            ImmutableArray<ArchivedThreadView>.Builder views = ImmutableArray.CreateBuilder<ArchivedThreadView>();
            ImmutableArray<TransferThread>.Builder threads = ImmutableArray.CreateBuilder<TransferThread>();
            int postCount = 0, occurrenceCount = 0;
            foreach (ResourceId id in selection.Threads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArchivedThreadView? view = await reader.ReadNativeTransferAsync(id, cancellationToken).ConfigureAwait(false);
                if (view == null) return FreezeFail(TransferArchiveError.MissingThread);
                // Preflight is advisory, not a cross-thread snapshot. Charge the ACTUAL view
                // before projecting/retaining it; racing growth can cost at most one bounded read.
                if (budget != null && !budget.TryReserveView(view, cancellationToken)) return FreezeFail(TransferArchiveError.SelectionLimit);
                TransferThread thread = NativeTransferArchiveProjection.From(view, cancellationToken);
                if (budget != null && !budget.TryReserveCatalog(thread, cancellationToken)) return FreezeFail(TransferArchiveError.SelectionLimit);
                postCount = checked(postCount + thread.Posts.Length);
                occurrenceCount = checked(occurrenceCount + thread.Posts.Sum(p => p.Attachments.Length));
                if (postCount > 100_000 || occurrenceCount > 100_000) return FreezeFail(TransferArchiveError.SelectionLimit);
                views.Add(view); threads.Add(thread);
            }
            NativeTransferCatalog metadataCatalog = new(exportedAtUtc, threads.ToImmutable());
            byte[] metadata = NativeTransferCatalogSerializer.Serialize(metadataCatalog, cancellationToken);
            // Same validator's metadata-only profile. This is not a fabricated payload measurement.
            NativeTransferEnvelope metadataEnvelope = new(packageId.ToString("D"), "chan-v1", "",
                new(metadata.Length, Hash(metadata)), []);
            if (!NativeTransferCatalogValidator.Inspect(metadata, metadataEnvelope, cancellationToken).IsCatalogValid)
                return FreezeFail(TransferArchiveError.InvalidCatalog);
            string[] directories = views.SelectMany(v => v.MediaDirectories).Distinct(StringComparer.Ordinal).ToArray();
            if (directories.Length > MaxDirectories || views.Any(v => v.MediaDirectories.Count > MaxDirectories))
                return FreezeFail(TransferArchiveError.SelectionLimit);
            ImmutableArray<MediaAssetSnapshot>.Builder manifests = ImmutableArray.CreateBuilder<MediaAssetSnapshot>();
            Dictionary<string, MediaAssetSnapshot> manifestsByRoot = new(StringComparer.Ordinal);
            int assetCount = 0;
            if (includeOriginals)
            {
                foreach (string directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MediaAssetSnapshot manifest = await ReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
                    // Until the representation-aware projection is integrated, never silently drop
                    // passive absence/derived inventory through the original-only exporter.
                    if (manifest.Status == MediaManifestStatus.Corrupt || manifest.ManifestVersion == 6)
                        return FreezeFail(TransferArchiveError.InvalidMedia);
                    assetCount = checked(assetCount + manifest.Count);
                    if (assetCount > MaxAssets) return FreezeFail(TransferArchiveError.SelectionLimit);
                    manifests.Add(manifest);
                    manifestsByRoot.Add(directory, manifest);
                }
            }
            ImmutableArray<MediaAssetSnapshot> frozenManifests = manifests.ToImmutable();
            int bindingVisits = 0;
            var plans = new List<(TransferThread Thread, ImmutableArray<MediaAssetSnapshot> Manifests,
                Dictionary<string, (string PostId, TransferAttachment Attachment, ResolvedMediaAttachment Resolved)> Retained)>();
            foreach (TransferThread thread in metadataCatalog.Threads)
            {
                ResourceId id = thread.Id;
                ArchivedThreadView view = views[plans.Count];
                ImmutableArray<MediaAssetSnapshot> threadManifests = includeOriginals
                    ? view.MediaDirectories.Distinct(StringComparer.Ordinal).Select(root => manifestsByRoot[root]).ToImmutableArray()
                    : [];
                // A binding is a real historical occurrence, not an old captured post.
                // Plan/preflight all extra provider metadata before opening any media bytes.
                HashSet<string> currentKeys = thread.Posts.SelectMany(p => p.Attachments)
                    .Select(a => a.Metadata.AttachmentRevisionKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
                Dictionary<string, (string PostId, TransferAttachment Attachment, ResolvedMediaAttachment Resolved)> retainedPlans = new(StringComparer.Ordinal);

                foreach (MediaAssetSnapshot manifest in threadManifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (manifest.AttachmentBindings.Count > MaxBindingVisits - bindingVisits)
                        return FreezeFail(TransferArchiveError.SelectionLimit);
                    bindingVisits += manifest.AttachmentBindings.Count;
                    foreach (MediaAttachmentBinding binding in manifest.AttachmentBindings.Values)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (binding.Reference.Resource != id) continue;
                        if (binding.Provider.Variant != "original") return FreezeFail(TransferArchiveError.UnrepresentedAssets);
                        bool additional = !currentKeys.Contains(binding.Reference.RevisionKey) && !retainedPlans.ContainsKey(binding.Reference.RevisionKey);
                        if (additional && ++occurrenceCount > 100_000) return FreezeFail(TransferArchiveError.SelectionLimit);
                        CapturedAttachment raw = RetainedMetadata(binding);
                        // Even current and duplicate keys must agree across every frozen root.
                        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(threadManifests, id, binding.Reference.PostId, raw);
                        if (resolved == null || resolved.Binding != binding) return FreezeFail(TransferArchiveError.InvalidMedia);
                        if (!additional) continue;
                        retainedPlans.Add(binding.Reference.RevisionKey, (binding.Reference.PostId,
                            new(raw, TransferMediaRole.Original, TransferContentStatus.Unavailable, null, null), resolved));
                    }
                }
                plans.Add((thread, threadManifests, retainedPlans));
            }
            NativeTransferCatalog preflight = metadataCatalog with { Threads = plans.Select(plan => plan.Thread with
            {
                RetainedBindings = plan.Retained.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new TransferRetainedBinding(p.Value.PostId, p.Value.Attachment)).ToImmutableArray()
            }).ToImmutableArray() };
            byte[] preflightBytes = NativeTransferCatalogSerializer.Serialize(preflight, cancellationToken);
            NativeTransferEnvelope preflightEnvelope = new(packageId.ToString("D"),
                NativeTransferCatalogSerializer.ContentSchema(preflight), "", new(preflightBytes.Length, Hash(preflightBytes)), []);
            if (!NativeTransferCatalogValidator.Inspect(preflightBytes, preflightEnvelope, cancellationToken).IsCatalogValid)
                return FreezeFail(TransferArchiveError.InvalidCatalog);
            HashSet<(string Directory, string AssetId)> represented = new();
            Dictionary<string, TransferPayloadSource> payloads = new(StringComparer.Ordinal);
            ImmutableArray<TransferThread>.Builder completed = ImmutableArray.CreateBuilder<TransferThread>();
            long total = 0, legacyWork = 0;
            TransferArchiveVerificationCache verification = new();
            foreach (var plan in plans)
            {
                TransferThread thread = plan.Thread;
                ResourceId id = thread.Id;
                ImmutableArray<MediaAssetSnapshot> threadManifests = plan.Manifests;
                var retainedPlans = plan.Retained;
                ImmutableArray<TransferPost>.Builder posts = ImmutableArray.CreateBuilder<TransferPost>(thread.Posts.Length);
                foreach (TransferPost post in thread.Posts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ImmutableArray<TransferAttachment>.Builder attachments = ImmutableArray.CreateBuilder<TransferAttachment>(post.Attachments.Length);
                    foreach (TransferAttachment attachment in post.Attachments)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        TransferAttachment selected = attachment;
                        if (!includeOriginals) selected = attachment with { Status = TransferContentStatus.Omitted };
                        else if (attachment.Metadata.MediaId != null)
                        {
                            // Reuse exact resolver, with an explicit work bound for its legacy scan.
                            if (attachment.Metadata.AttachmentRevisionKey == null)
                            {
                                legacyWork = checked(legacyWork + assetCount);
                                if (legacyWork > MaxLegacyComparisons) return FreezeFail(TransferArchiveError.SelectionLimit);
                            }
                            ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(threadManifests, id, post.Metadata.PostId, attachment.Metadata);
                            if (resolved != null)
                            {
                                selected = await SelectContentAsync(attachment, resolved).ConfigureAwait(false);
                            }
                        }
                        attachments.Add(selected);
                    }
                    posts.Add(post with { Attachments = attachments.ToImmutable() });
                }
                ImmutableArray<TransferRetainedBinding>.Builder retained = ImmutableArray.CreateBuilder<TransferRetainedBinding>(retainedPlans.Count);
                foreach (var retainedPlan in retainedPlans.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TransferAttachment selected = await SelectContentAsync(retainedPlan.Attachment, retainedPlan.Resolved).ConfigureAwait(false);
                    retained.Add(new(retainedPlan.PostId, selected));
                }
                completed.Add(thread with { Posts = posts.ToImmutable(), RetainedBindings = retained.ToImmutable() });
            }
            foreach (MediaAssetSnapshot manifest in frozenManifests)
                for (int offset = 0; offset < manifest.Count; offset += 500)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (MediaAssetReference asset in manifest.GetPage(offset, 500))
                        if (!represented.Contains((manifest.Directory, asset.AssetId))) return FreezeFail(TransferArchiveError.UnrepresentedAssets);
                }
            NativeTransferArchiveSnapshot snapshot = new(reader, views[0], metadata,
                metadataCatalog with { Threads = completed.ToImmutable() }, frozenManifests,
                payloads.Values.OrderBy(p => p.Descriptor.Sha256, StringComparer.Ordinal).ToImmutableArray(), packageId) { Views = views.ToImmutable(), MetadataLimits = metadataLimits };
            if (!await IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false)) return FreezeFail(TransferArchiveError.StaleSnapshot);
            return new(snapshot, TransferArchiveError.None);

            async Task<TransferAttachment> SelectContentAsync(TransferAttachment attachment, ResolvedMediaAttachment resolved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaAssetSnapshot manifest = resolved.Snapshot; MediaAssetReference asset = resolved.Asset;
                represented.Add((manifest.Directory, asset.AssetId));
                if (asset.Length > MaxPayload) throw new FreezeException(TransferArchiveError.SelectionLimit);
                TransferAssetVerification availability = await verification.VerifyAsync(manifest, asset, cancellationToken).ConfigureAwait(false);
                if (availability == TransferAssetVerification.Limit) throw new FreezeException(TransferArchiveError.SelectionLimit);
                if (availability == TransferAssetVerification.Unavailable)
                    return attachment with { Status = TransferContentStatus.Unavailable, PayloadSha256 = null, History = null };
                TransferHistoricalIntegrity? history = resolved.Binding is { } binding ? HistoricalObservation(binding) : null;
                if (payloads.TryGetValue(asset.Sha256, out TransferPayloadSource? previous))
                {
                    if (previous.Descriptor.Length != asset.Length) throw new FreezeException(TransferArchiveError.InvalidMedia);
                }
                else
                {
                    total = checked(total + asset.Length);
                    if (payloads.Count >= MaxPayloads || total > MaxTotal) throw new FreezeException(TransferArchiveError.SelectionLimit);
                    payloads.Add(asset.Sha256, new(new(asset.Length, asset.Sha256), token =>
                    {
                        token.ThrowIfCancellationRequested();
                        ArchiveExportMetadataFile.RejectLinks(asset.Path);
                        return manifest.OpenVerifiedReadAsync(asset.AssetId, token);
                    }));
                }
                return attachment with { Status = TransferContentStatus.Contained, PayloadSha256 = asset.Sha256, History = history };
            }
        }
        catch (FreezeException e) { return FreezeFail(e.Error); }
        catch (Exception e) when (e is InvalidDataException or FormatException) { return FreezeFail(TransferArchiveError.InvalidArchive); }
        catch (Exception e) when (Environmental(e)) { return FreezeFail(TransferArchiveError.IoFailure); }
    }

    internal static async Task<TransferArchiveWriteResult> WriteAsync(NativeTransferArchiveSnapshot snapshot,
        Stream staging, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(staging);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!await IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false)) return new(null, TransferArchiveError.StaleSnapshot);
            TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(staging, snapshot.Catalog,
                snapshot.Payloads, snapshot.PackageId, cancellationToken).ConfigureAwait(false);
            if (!result.IsComplete) return new(null, TransferArchiveError.WriteFailure, result.Error);
            if (!await IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false)) return new(null, TransferArchiveError.StaleSnapshot);
            cancellationToken.ThrowIfCancellationRequested();
            return new(result.Package, TransferArchiveError.None, BytesWritten: result.BytesWritten);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException) { return new(null, TransferArchiveError.InvalidMedia); }
        catch (Exception e) when (Environmental(e)) { return new(null, TransferArchiveError.IoFailure); }
    }

    internal static async Task<bool> IsCurrentAsync(NativeTransferArchiveSnapshot snapshot, CancellationToken token)
    {
        ArchiveExportMetadataFile.RejectLinks(snapshot.Reader.DatabasePath);
        MetadataBudget? budget = null;
        if (snapshot.Views.Length > 1)
        {
            if (!await PreflightMetadataAsync(snapshot.Reader, snapshot.Views.Select(v => v.Summary.Id).ToImmutableArray(),
                snapshot.MetadataLimits, token).ConfigureAwait(false)) return false;
            budget = new(snapshot.MetadataLimits, snapshot.Catalog.ExportedAtUtc, token);
        }
        ImmutableArray<TransferThread>.Builder threads = ImmutableArray.CreateBuilder<TransferThread>();
        foreach (ArchivedThreadView frozen in snapshot.Views)
        {
            token.ThrowIfCancellationRequested();
            ArchivedThreadView? view = await snapshot.Reader.ReadNativeTransferAsync(frozen.Summary.Id, token).ConfigureAwait(false);
            if (view == null || view.Summary != frozen.Summary || !view.MediaDirectories.SequenceEqual(frozen.MediaDirectories, StringComparer.Ordinal)) return false;
            if (budget != null && !budget.TryReserveView(view, token)) return false;
            TransferThread thread = NativeTransferArchiveProjection.From(view, token);
            if (budget != null && !budget.TryReserveCatalog(thread, token)) return false;
            threads.Add(thread);
        }
        byte[] current = NativeTransferCatalogSerializer.Serialize(new(snapshot.Catalog.ExportedAtUtc, threads.ToImmutable()), token);
        if (!current.AsSpan().SequenceEqual(snapshot.Metadata.AsSpan())) return false;
        foreach (MediaAssetSnapshot manifest in snapshot.Manifests)
        {
            token.ThrowIfCancellationRequested();
            MediaAssetSnapshot latest = await ReadManifestAsync(manifest.Directory, token).ConfigureAwait(false);
            if (latest.Status != manifest.Status || latest.Generation != manifest.Generation) return false;
        }
        return true;
    }
    // No schema change or new reader authority. This read-only selection-wide size pass
    // mirrors NativeTransferArchiveReader's text columns, including optional old-schema
    // fields and private paths. CAST AS BLOB counts embedded NULs and UTF-8 bytes, not
    // character counts or substrings. It precedes the first materialized thread.
    internal static Task<bool> PreflightMetadataAsync(ArchiveReader reader, ImmutableArray<ResourceId> ids,
        NativeTransferArchiveMetadataLimits limits, CancellationToken token)
        => Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                ArchiveExportMetadataFile.RejectLinks(reader.DatabasePath);
                using SqliteConnection connection = ArchiveSql.OpenConnection(reader.DatabasePath, SqliteOpenMode.ReadOnly);
                using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
                ArchiveSql.ValidateSchema(connection, transaction);
                List<string> queries = new();
                foreach ((string table, string[] candidates) in new (string, string[])[]
                {
                    ("archive_threads", ["source", "board", "thread", "url", "title", "media_directory", "first_seen_at", "last_captured_at"]),
                    ("archive_posts", ["post", "posted_at", "subject", "author", "html"]),
                    ("archive_attachments", ["post", "media_id", "variant", "url", "original_name", "planned_path", "expected_md5", "expected_sha256", "attachment_revision_key", "expected_sha1", "provider_size_hint_json"]),
                    ("archive_media_directories", ["directory"])
                })
                {
                    token.ThrowIfCancellationRequested();
                    HashSet<string> columns = new(StringComparer.Ordinal);
                    using (SqliteCommand info = connection.CreateCommand())
                    {
                        info.Transaction = transaction; info.CommandText = "PRAGMA table_info(" + table + ")";
                        using SqliteDataReader schema = info.ExecuteReader();
                        while (schema.Read()) columns.Add(schema.GetString(1));
                    }
                    queries.Add("SELECT COALESCE(SUM(" + string.Join(" + ", candidates.Where(columns.Contains)
                        .Select(column => "COALESCE(length(CAST(" + column + " AS BLOB)),0)"))
                        + "),0) FROM " + table + " WHERE source=$source AND board=$board AND thread=$thread");
                }
                long remaining = limits.StoredTextBytes;
                foreach (ResourceId id in ids)
                {
                    token.ThrowIfCancellationRequested();
                    ArchiveValidation.ValidateId(id);
                    foreach (string query in queries)
                    {
                        token.ThrowIfCancellationRequested();
                        using SqliteCommand sizes = connection.CreateCommand(); sizes.Transaction = transaction;
                        sizes.CommandText = query;
                        sizes.Parameters.AddWithValue("$source", id.Source); sizes.Parameters.AddWithValue("$board", id.Board);
                        sizes.Parameters.AddWithValue("$thread", id.Thread);
                        long bytes = Convert.ToInt64(sizes.ExecuteScalar());
                        token.ThrowIfCancellationRequested();
                        if (bytes < 0 || bytes > remaining) return false;
                        remaining -= bytes;
                    }
                }
                token.ThrowIfCancellationRequested();
                transaction.Commit();
                return true;
            }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }, token);

    // Independent reservations for materialized view text and actual serialized v1 bytes.
    // It never multiplies an assumed per-post size or encodes an ever-growing catalog.
    // Shared strings/roots are conservatively charged for every occurrence retained.
    internal sealed class MetadataBudget
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly NativeTransferArchiveMetadataLimits limits;
        private readonly DateTimeOffset exportedAt;
        private readonly int emptyCatalogBytes;
        private long remainingText, catalogBytes;
        private int threads;

        internal MetadataBudget(NativeTransferArchiveMetadataLimits limits, DateTimeOffset exportedAt, CancellationToken token)
        {
            this.limits = limits; this.exportedAt = exportedAt;
            remainingText = limits.StoredTextBytes;
            emptyCatalogBytes = NativeTransferCatalogSerializer.Serialize(new(exportedAt, []), token).Length;
            catalogBytes = emptyCatalogBytes;
        }

        internal bool TryReserveView(ArchivedThreadView view, CancellationToken token)
        {
            ArchiveThreadSummary s = view.Summary;
            if (!Text(s.Id.Source) || !Text(s.Id.Board) || !Text(s.Id.Thread) || !Text(s.Url)
                || !Text(s.Title) || !Text(s.MediaDirectory)) return false;
            foreach (string root in view.MediaDirectories)
                if (!Text(root)) return false;
            foreach (CapturedPost post in view.Posts)
            {
                token.ThrowIfCancellationRequested();
                if (!Text(post.PostId) || !Text(post.Subject) || !Text(post.Author) || !Text(post.Html)) return false;
                foreach (CapturedAttachment a in post.Attachments ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    if (!Text(a.MediaId) || !Text(a.Variant) || !Text(a.Url) || !Text(a.OriginalFileName)
                        || !Text(a.PlannedPath) || !Text(a.ExpectedMd5) || !Text(a.ExpectedSha1)
                        || !Text(a.ExpectedSha256) || !Text(a.AttachmentRevisionKey)) return false;
                    if (a.ProviderSizeHint is { } hint && (!Text(hint.RawValue) || !Text(hint.UnitLabel))) return false;
                }
            }
            return true;

            bool Text(string? value)
            {
                token.ThrowIfCancellationRequested();
                if (value == null) return true;
                int bytes;
                try { bytes = StrictUtf8.GetByteCount(value); }
                catch (EncoderFallbackException) { throw new InvalidDataException("Invalid native export metadata text."); }
                if (bytes > remainingText) return false;
                remainingText -= bytes;
                return true;
            }
        }

        internal bool TryReserveCatalog(TransferThread thread, CancellationToken token)
        {
            // Projection is metadata-only v1. JSON escaping, nulls and all token lengths
            // come from the real serializer, not estimates based on string.Length.
            int bytes = NativeTransferCatalogSerializer.Serialize(new(exportedAt, [thread]), token).Length;
            long additional = bytes - emptyCatalogBytes + (threads == 0 ? 0 : 1); // array comma
            if (additional > limits.CatalogBytes - catalogBytes) return false;
            catalogBytes += additional; threads++;
            return true;
        }
    }

    internal static TransferHistoricalIntegrity? HistoricalObservation(MediaAttachmentBinding binding)
    {
        if (binding.Acceptance.Kind == MediaAcceptanceKind.PassiveImport)
        {
            // Local import provenance is not historical source permission and is never exported.
            if (binding.PassiveImport?.History is not { } imported) return null;
            return new(imported.Kind switch
            {
                MediaAcceptanceKind.Strict => "strict",
                MediaAcceptanceKind.ExplicitProviderMismatch => "provider-mismatch",
                _ => throw new InvalidDataException("Unsupported imported historical observation.")
            }, imported.AcceptedAtUtc, imported.SizeMismatch, imported.Md5Mismatch);
        }
        return new(binding.Acceptance.Kind switch
        {
            MediaAcceptanceKind.Strict => "strict",
            MediaAcceptanceKind.ExplicitProviderMismatch => "provider-mismatch",
            _ => throw new InvalidDataException("Unsupported media provenance.")
        }, binding.Acceptance.AcceptedAtUtc, binding.Acceptance.SizeMismatch, binding.Acceptance.Md5Mismatch);
    }

    // This surrogate records only the binding's exact provider/reference tuple. It is never
    // put into current Posts and carries no invented post ordinal/time/deletion observation.
    internal static CapturedAttachment RetainedMetadata(MediaAttachmentBinding binding)
    {
        MediaProviderMetadata p = binding.Provider;
        return new(binding.Reference.Position, p.MediaId, p.Variant, p.Url, p.OriginalFileName,
            ExpectedSize: p.ExpectedSize, ExpectedMd5: p.ExpectedMd5)
        {
            ExpectedSha1 = p.ExpectedSha1, ExpectedSha256 = p.ExpectedSha256,
            ProviderSizeHint = p.ProviderSizeHint, AttachmentRevisionKey = binding.Reference.RevisionKey
        };
    }
    private sealed class FreezeException(TransferArchiveError error) : Exception { internal TransferArchiveError Error => error; }
    private static Task<MediaAssetSnapshot> ReadManifestAsync(string directory, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                throw new InvalidDataException("The stored media root is invalid.");
            directory = Path.GetFullPath(directory);
        }
        catch (ArgumentException) { throw new InvalidDataException("The stored media root is invalid."); }
        ArchiveExportMetadataFile.RejectLinks(directory);
        ArchiveExportMetadataFile.RejectLinks(Path.Combine(directory, ".meowchan-manifest.json"));
        ArchiveExportMetadataFile.RejectLinks(Path.Combine(directory, ".ochan-manifest.json"));
        return MediaAssetReader.OpenAsync(directory, token);
    }
    private static TransferArchiveFreezeResult FreezeFail(TransferArchiveError error) => new(null, error);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Environmental(Exception e) => e is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException or OverflowException or Microsoft.Data.Sqlite.SqliteException;
}
