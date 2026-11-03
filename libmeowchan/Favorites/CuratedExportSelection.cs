using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Favorites;

internal enum CuratedExportScope { Thread, Threads, ExplicitFavorites }
internal enum CuratedExportMode { Files, Html }
internal enum CuratedExportStatus
{
    OriginalIncluded, OriginalMissing, OriginalVerificationFailed, OriginalAmbiguous,
    IntentionallyOmitted, PreviewNotRequested, PreviewUnsupported, PreviewFailed, Cancelled
}
internal enum CuratedOriginalVerification { Required, Verified, NotApplicable }
internal enum CuratedExportSourceFreshness { SameManifestFences, Stale }
internal enum CuratedExportValidationError
{
    InvalidSnapshot, InvalidScope, InvalidKey, DuplicateIdentity, SelectionNotInSnapshot,
    InvalidManifest, InvalidContext, LimitExceeded, InvalidLedgerUpdate
}

internal sealed class CuratedExportValidationException : Exception
{
    internal CuratedExportValidationException(CuratedExportValidationError error)
        : base("The curated export input is invalid (" + error + ").") => Error = error;
    internal CuratedExportValidationError Error { get; }
}

/// <summary>The caller supplies only locally read manifests and their proven resource association.</summary>
internal sealed record CuratedExportManifest(ResourceId Thread, MediaAssetSnapshot Snapshot);
internal sealed record CuratedExportManifestFence(ResourceId Thread, string Directory,
    MediaManifestStatus Status, string? Generation);
/// <summary>Inert provenance only; deliberately excludes acceptance grants and source permissions.</summary>
internal sealed record CuratedExportOccurrence(ResourceId Resource, string PostId, int Position,
    string? RevisionKey, string? AcquisitionRevisionKey, bool IsLegacy, MediaAssetSnapshot Manifest, string AssetId)
{
    // Only bound rows expose the old revision-shaped view; legacy rows stay null.
    internal AttachmentRevisionReference? Reference => IsLegacy || RevisionKey == null
        ? null : new(Resource, PostId, Position, RevisionKey!);

    internal static CuratedExportOccurrence Bound(MediaAttachmentBinding binding, MediaAssetSnapshot manifest) =>
        new(binding.Reference.Resource, binding.Reference.PostId, binding.Reference.Position,
            binding.Reference.RevisionKey, binding.AcquisitionRevisionKey, false, manifest, binding.AssetId);

    internal static CuratedExportOccurrence Legacy(ResourceId resource, string postId, int position,
        MediaAssetSnapshot manifest, string assetId) =>
        new(resource, postId, position, null, null, true, manifest, assetId);
}
internal sealed record CuratedExportOriginal(FavoriteEntry Entry, string NameToken,
    CuratedExportStatus Status, FavoriteExportAsset? Asset, ImmutableArray<FavoriteExportAsset> Candidates,
    ImmutableArray<CuratedExportOccurrence> Occurrences);
internal sealed record CuratedExportThreadContext(ArchiveThreadSummary Summary, ImmutableArray<CapturedPost> Posts,
    ImmutableArray<string> MediaDirectories, bool? DecimalPostOrder);
internal sealed record CuratedExportLedgerItem(FavoriteAssetKey Key, CuratedExportStatus Status,
    CuratedOriginalVerification Verification);

/// <summary>
/// Operation-owned inclusion snapshot, not a file/HTML writer, byte-verification receipt, or admission token.
/// Every selected key must be explicit and present in the supplied store snapshot. Neither thread scope,
/// manifests nor subsequent favorite toggles can add keys. No reads, repair, decoder or network work occurs.
/// </summary>
internal sealed class CuratedExportSelection
{
    // Defensive in-memory validation ceilings, NOT measured decoder/copy/disk budgets or release promises.
    internal const int MaximumSelectionEntries = FavoriteStore.MaximumEntries;
    internal const int MaximumManifests = 1_024;
    internal const int MaximumRows = 200_000;
    internal const int MaximumStringCharacters = 65_536;
    internal const int MaximumTextCharacters = 8_000_000;

    private CuratedExportSelection(FavoriteStoreSnapshot snapshot, CuratedExportScope scope,
        CuratedExportMode mode, bool previewsRequested, ImmutableArray<FavoriteEntry> favorites,
        ImmutableArray<ResourceId> threads, ImmutableArray<CuratedExportManifestFence> fences,
        ImmutableArray<CuratedExportThreadContext> contexts, ImmutableArray<CuratedExportOriginal> originals,
        ImmutableArray<CuratedExportLedgerItem> omissions)
    {
        InstanceId = snapshot.InstanceId;
        Revision = snapshot.Revision;
        Fingerprint = snapshot.Fingerprint;
        Scope = scope;
        Mode = mode;
        PreviewsRequested = previewsRequested;
        FavoriteEntries = favorites;
        Threads = threads;
        ManifestFences = fences;
        ThreadContexts = contexts;
        Originals = originals;
        Omissions = omissions;
    }

    internal Guid? InstanceId { get; }
    internal long Revision { get; }
    internal string Fingerprint { get; }
    internal CuratedExportScope Scope { get; }
    internal CuratedExportMode Mode { get; }
    internal bool PreviewsRequested { get; }
    internal ImmutableArray<FavoriteEntry> FavoriteEntries { get; }
    internal ImmutableArray<ResourceId> Threads { get; }
    internal ImmutableArray<CuratedExportManifestFence> ManifestFences { get; }
    internal ImmutableArray<CuratedExportThreadContext> ThreadContexts { get; }
    internal ImmutableArray<CuratedExportOriginal> Originals { get; }
    internal ImmutableArray<CuratedExportLedgerItem> Omissions { get; }

    internal static CuratedExportSelection Freeze(FavoriteStoreSnapshot snapshot, CuratedExportScope scope,
        CuratedExportMode mode, bool previewsRequested, IEnumerable<ResourceId> threads,
        IEnumerable<FavoriteAssetKey> selectedKeys, IEnumerable<CuratedExportManifest> manifests,
        IEnumerable<ArchivedThreadView> contexts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Budget budget = new(cancellationToken);
        if (snapshot == null) throw Invalid(CuratedExportValidationError.InvalidSnapshot);
        budget.Text(snapshot.Fingerprint);
        if (snapshot.Revision < 0 || snapshot.InstanceId == Guid.Empty
            || (snapshot.InstanceId == null ? snapshot.Revision != 0 : snapshot.Revision < 1)
            || (snapshot.Fingerprint != "missing" && !Digest(snapshot.Fingerprint))
            || (snapshot.Fingerprint == "missing" && (snapshot.InstanceId != null || snapshot.Revision != 0 || snapshot.Entries.Count != 0)))
            throw Invalid(CuratedExportValidationError.InvalidSnapshot);
        if (!Enum.IsDefined(scope) || !Enum.IsDefined(mode)) throw Invalid(CuratedExportValidationError.InvalidScope);

        Dictionary<FavoriteAssetKey, FavoriteEntry> favoriteIndex = new();
        foreach (FavoriteEntry entry in budget.Read(snapshot.Entries, MaximumSelectionEntries))
        {
            if (entry == null || entry.Length < 0) throw Invalid(CuratedExportValidationError.InvalidSnapshot);
            budget.Key(entry.Key);
            budget.Text(entry.OriginalFileName, optional: true);
            if (!favoriteIndex.TryAdd(entry.Key, entry)) throw Invalid(CuratedExportValidationError.DuplicateIdentity);
        }
        HashSet<ResourceId> scopeThreads = new();
        foreach (ResourceId thread in budget.Read(threads, MaximumSelectionEntries))
        {
            budget.Thread(thread);
            if (!scopeThreads.Add(thread)) throw Invalid(CuratedExportValidationError.DuplicateIdentity);
        }
        if ((scope == CuratedExportScope.Thread && scopeThreads.Count != 1)
            || (scope == CuratedExportScope.Threads && scopeThreads.Count == 0)
            || (scope == CuratedExportScope.ExplicitFavorites && scopeThreads.Count != 0))
            throw Invalid(CuratedExportValidationError.InvalidScope);

        HashSet<FavoriteAssetKey> keys = new();
        foreach (FavoriteAssetKey key in budget.Read(selectedKeys, MaximumSelectionEntries))
        {
            budget.Key(key);
            if (key.Variant != "original") throw Invalid(CuratedExportValidationError.InvalidKey);
            if (!keys.Add(key)) throw Invalid(CuratedExportValidationError.DuplicateIdentity);
            if (!favoriteIndex.ContainsKey(key)) throw Invalid(CuratedExportValidationError.SelectionNotInSnapshot);
            if (scope != CuratedExportScope.ExplicitFavorites && !scopeThreads.Contains(key.Thread))
                throw Invalid(CuratedExportValidationError.InvalidScope);
        }
        if (scope == CuratedExportScope.ExplicitFavorites)
            scopeThreads.UnionWith(keys.Select(key => key.Thread));

        List<CuratedExportManifest> frozenManifests = new();
        HashSet<ResourceId> corruptThreads = new();
        HashSet<(ResourceId, string)> manifestIds = new();
        Dictionary<FavoriteAssetKey, List<FavoriteExportAsset>> candidates = new();
        Dictionary<(ResourceId, string, string), List<CuratedExportOccurrence>> occurrences = new();
        foreach (CuratedExportManifest input in budget.Read(manifests, MaximumManifests))
        {
            if (input == null || input.Snapshot == null) throw Invalid(CuratedExportValidationError.InvalidManifest);
            budget.Thread(input.Thread);
            MediaAssetSnapshot manifest = input.Snapshot;
            budget.Text(manifest.Directory);
            budget.Text(manifest.Generation, optional: true);
            if (!scopeThreads.Contains(input.Thread)) throw Invalid(CuratedExportValidationError.InvalidScope);
            if (!Enum.IsDefined(manifest.Status)
                || (manifest.Status == MediaManifestStatus.Available ? !Digest(manifest.Generation)
                    : manifest.Generation != null || manifest.Count != 0 || manifest.AttachmentBindings.Count != 0))
                throw Invalid(CuratedExportValidationError.InvalidManifest);
            if (!manifestIds.Add((input.Thread, manifest.Directory))) throw Invalid(CuratedExportValidationError.DuplicateIdentity);
            frozenManifests.Add(input);
            if (manifest.Status == MediaManifestStatus.Corrupt) corruptThreads.Add(input.Thread);
            Dictionary<string, MediaAssetReference> assets = new(StringComparer.Ordinal);
            for (int offset = 0; offset < manifest.Count; offset += 500)
                foreach (MediaAssetReference asset in budget.Read(manifest.GetPage(offset, 500), 500))
                {
                    budget.Key(new FavoriteAssetKey(input.Thread, asset.MediaIdentifier, asset.Variant, asset.Sha256));
                    budget.Text(asset.AssetId);
                    budget.Text(asset.Path);
                    budget.Text(asset.RequestedFileName);
                    budget.Text(asset.OriginalFileName, optional: true);
                    budget.Text(asset.SourceUrl, optional: true);
                    budget.Text(asset.Md5, optional: true);
                    budget.Text(asset.Sha1, optional: true);
                    if (asset.Length < 0) throw Invalid(CuratedExportValidationError.InvalidManifest);
                    assets.Add(asset.AssetId, asset);
                    if (asset.Variant != "original") continue;
                    FavoriteExportAsset candidate = FavoriteExportAsset.From(input.Thread, manifest, asset);
                    if (!candidates.TryGetValue(candidate.Key, out List<FavoriteExportAsset>? matches))
                        candidates.Add(candidate.Key, matches = new());
                    matches.Add(candidate);
                }
            Dictionary<string, List<CuratedExportOccurrence>> perAsset = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, MediaAttachmentBinding> pair in budget.Read(manifest.AttachmentBindings, MaximumRows))
            {
                budget.Binding(pair.Key, pair.Value);
                MediaAttachmentBinding binding = pair.Value;
                if (!assets.TryGetValue(binding.AssetId, out MediaAssetReference? asset)
                    || !MediaAttachmentResolver.BindingMatchesAsset(binding, asset))
                    throw Invalid(CuratedExportValidationError.InvalidManifest);
                // Shared directories may contain valid bindings for other resources. Validate them,
                // but never relabel their occurrences as belonging to this resource.
                if (binding.Reference.Resource != input.Thread) continue;
                if (!perAsset.TryGetValue(binding.AssetId, out List<CuratedExportOccurrence>? rows))
                    perAsset.Add(binding.AssetId, rows = new());
                rows.Add(CuratedExportOccurrence.Bound(binding, manifest));
            }
            foreach (var pair in perAsset)
            {
                if (!occurrences.TryGetValue((input.Thread, manifest.Directory, pair.Key), out List<CuratedExportOccurrence>? rows))
                    occurrences.Add((input.Thread, manifest.Directory, pair.Key), rows = new());
                rows.AddRange(pair.Value);
            }
        }

        ImmutableArray<CuratedExportThreadContext> frozenContexts = FreezeContexts(contexts, scopeThreads, budget);
        if (mode == CuratedExportMode.Files && !frozenContexts.IsEmpty)
            throw Invalid(CuratedExportValidationError.InvalidContext);
        AddLegacyOccurrences(frozenContexts, frozenManifests, candidates, occurrences, budget);
        var originals = ImmutableArray.CreateBuilder<CuratedExportOriginal>();
        foreach (FavoriteAssetKey key in OrderKeys(keys))
        {
            budget.Check();
            FavoriteEntry entry = favoriteIndex[key];
            candidates.TryGetValue(key, out List<FavoriteExportAsset>? matches);
            // Count all exact identities before checking length: a convenient valid replica must not
            // conceal an ambiguous mapping or a conflicting manifest. IsCurrent is never consulted.
            CuratedExportStatus status = corruptThreads.Contains(key.Thread) ? CuratedExportStatus.OriginalVerificationFailed
                : matches == null ? CuratedExportStatus.OriginalMissing
                : matches.Count != 1 ? CuratedExportStatus.OriginalAmbiguous
                : matches[0].Length != entry.Length ? CuratedExportStatus.OriginalVerificationFailed
                : CuratedExportStatus.OriginalIncluded;
            FavoriteExportAsset? chosen = status == CuratedExportStatus.OriginalIncluded ? matches![0] : null;
            ImmutableArray<FavoriteExportAsset> frozenCandidates = (matches ?? new List<FavoriteExportAsset>())
                .OrderBy(value => value.Manifest.Directory, StringComparer.Ordinal).ThenBy(value => value.AssetId, StringComparer.Ordinal).ToImmutableArray();
            ImmutableArray<CuratedExportOccurrence> bindings = frozenCandidates.SelectMany(value =>
                occurrences.TryGetValue((key.Thread, value.Manifest.Directory, value.AssetId), out List<CuratedExportOccurrence>? rows)
                    ? rows : Enumerable.Empty<CuratedExportOccurrence>())
                .OrderBy(row => row.PostId, StringComparer.Ordinal).ThenBy(row => row.Position)
                .ThenBy(row => row.RevisionKey, StringComparer.Ordinal).ThenBy(row => row.IsLegacy)
                .ThenBy(row => row.Manifest.Directory, StringComparer.Ordinal).ThenBy(row => row.AssetId, StringComparer.Ordinal).ToImmutableArray();
            originals.Add(new(entry, "original-" + (originals.Count + 1).ToString("D6", CultureInfo.InvariantCulture), status, chosen, frozenCandidates, bindings));
        }
        var omitted = ImmutableArray.CreateBuilder<CuratedExportLedgerItem>();
        foreach (FavoriteAssetKey key in OrderKeys(candidates.Keys.Where(key => !keys.Contains(key))))
        {
            budget.Check();
            omitted.Add(new(key, CuratedExportStatus.IntentionallyOmitted, CuratedOriginalVerification.NotApplicable));
            omitted.Add(new(key, previewsRequested ? CuratedExportStatus.PreviewUnsupported : CuratedExportStatus.PreviewNotRequested,
                CuratedOriginalVerification.NotApplicable));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(snapshot, scope, mode, previewsRequested, OrderKeys(favoriteIndex.Keys).Select(key => favoriteIndex[key]).ToImmutableArray(),
            OrderThreads(scopeThreads).ToImmutableArray(), frozenManifests.OrderBy(value => value.Thread, ThreadComparer)
                .ThenBy(value => value.Snapshot.Directory, StringComparer.Ordinal)
                .Select(value => new CuratedExportManifestFence(value.Thread, value.Snapshot.Directory, value.Snapshot.Status, value.Snapshot.Generation)).ToImmutableArray(),
            frozenContexts, originals.ToImmutable(), omitted.ToImmutable());
    }

    /// <summary>Metadata-only comparison. Does NOT prove root/admission, archive or current byte freshness.</summary>
    internal CuratedExportSourceFreshness CompareManifestFences(IEnumerable<CuratedExportManifestFence> observed,
        CancellationToken cancellationToken = default)
    {
        Budget budget = new(cancellationToken);
        Dictionary<(ResourceId, string), CuratedExportManifestFence> values = new();
        foreach (CuratedExportManifestFence fence in budget.Read(observed, MaximumManifests))
        {
            if (fence == null) throw Invalid(CuratedExportValidationError.InvalidManifest);
            budget.Thread(fence.Thread);
            budget.Text(fence.Directory);
            budget.Text(fence.Generation, optional: true);
            if (!values.TryAdd((fence.Thread, fence.Directory), fence)) throw Invalid(CuratedExportValidationError.DuplicateIdentity);
        }
        budget.Check();
        return values.Count == ManifestFences.Length && ManifestFences.All(fence =>
            values.TryGetValue((fence.Thread, fence.Directory), out var current) && current == fence)
            ? CuratedExportSourceFreshness.SameManifestFences : CuratedExportSourceFreshness.Stale;
    }

    internal CuratedExportLedger CreateLedger() => CuratedExportLedger.Start(this);

    private static void AddLegacyOccurrences(ImmutableArray<CuratedExportThreadContext> contexts,
        IEnumerable<CuratedExportManifest> manifests,
        Dictionary<FavoriteAssetKey, List<FavoriteExportAsset>> candidates,
        Dictionary<(ResourceId, string, string), List<CuratedExportOccurrence>> occurrences, Budget budget)
    {
        Dictionary<ResourceId, CuratedExportManifest[]> byThread = manifests
            .GroupBy(value => value.Thread).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (CuratedExportThreadContext context in contexts)
        {
            budget.Check();
            if (!byThread.TryGetValue(context.Summary.Id, out CuratedExportManifest[]? available)) continue;
            MediaAssetSnapshot[] snapshots = available.Select(value => value.Snapshot).ToArray();
            foreach (CapturedPost post in context.Posts)
            foreach (CapturedAttachment attachment in post.Attachments ?? Array.Empty<CapturedAttachment>())
            {
                budget.Check();
                // Bound rows were inventoried from the manifest bindings above. Legacy is
                // intentionally only the null-revision branch of the existing resolver.
                if (attachment.AttachmentRevisionKey != null) continue;
                // Resolve(IEnumerable<...>) scans these frozen manifests for this legacy row.
                // Charge a conservative scan bound before entering the existing resolver.
                budget.RowsForResolver(snapshots);
                ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(snapshots,
                    context.Summary.Id, post.PostId, attachment);
                budget.Check();
                if (resolved is not { IsLegacy: true } || resolved.Binding != null) continue;
                // The multi-manifest resolver can consider identical asset replicas equivalent.
                // Keep the selection's stricter concrete-location ambiguity unresolved as well.
                FavoriteAssetKey key = FavoriteAssetKey.From(context.Summary.Id, resolved.Asset);
                if (!candidates.TryGetValue(key, out List<FavoriteExportAsset>? matches) || matches.Count != 1) continue;
                var occurrence = CuratedExportOccurrence.Legacy(context.Summary.Id, post.PostId, attachment.Position,
                    resolved.Snapshot, resolved.Asset.AssetId);
                var location = (context.Summary.Id, resolved.Snapshot.Directory, resolved.Asset.AssetId);
                if (!occurrences.TryGetValue(location, out List<CuratedExportOccurrence>? rows))
                    occurrences.Add(location, rows = new());
                // Frozen contexts already enforce unique resource/post/position rows.
                rows.Add(occurrence);
            }
        }
    }

    private static ImmutableArray<CuratedExportThreadContext> FreezeContexts(IEnumerable<ArchivedThreadView> source,
        HashSet<ResourceId> threads, Budget budget)
    {
        List<CuratedExportThreadContext> result = new();
        HashSet<ResourceId> seen = new();
        foreach (ArchivedThreadView view in budget.Read(source, MaximumManifests))
        {
            if (view == null || view.Summary == null) throw Invalid(CuratedExportValidationError.InvalidContext);
            ArchiveThreadSummary summary = view.Summary;
            budget.Thread(summary.Id);
            budget.Text(summary.Url); budget.Text(summary.Title, optional: true); budget.Text(summary.MediaDirectory);
            if (!threads.Contains(summary.Id) || summary.Revision < 0 || summary.PostCount != view.PostCount)
                throw Invalid(CuratedExportValidationError.InvalidContext);
            if (!seen.Add(summary.Id)) throw Invalid(CuratedExportValidationError.DuplicateIdentity);
            var directories = ImmutableArray.CreateBuilder<string>();
            foreach (string directory in budget.Read(view.MediaDirectories, MaximumManifests))
            { budget.Text(directory); directories.Add(directory); }
            var posts = ImmutableArray.CreateBuilder<CapturedPost>();
            HashSet<string> postIds = new(StringComparer.Ordinal);
            foreach (CapturedPost post in budget.Read(view.Posts, MaximumRows))
            {
                if (post == null) throw Invalid(CuratedExportValidationError.InvalidContext);
                budget.Text(post.PostId); budget.Text(post.Subject, true); budget.Text(post.Author, true); budget.Text(post.Html, true);
                if (post.Ordinal < 0 || (post.Fields & ~PostFieldPresence.All) != 0 || !postIds.Add(post.PostId)) throw Invalid(CuratedExportValidationError.InvalidContext);
                ImmutableArray<CapturedAttachment>? attachments = null;
                if (post.Attachments != null)
                {
                    var copied = ImmutableArray.CreateBuilder<CapturedAttachment>();
                    HashSet<int> positions = new();
                    foreach (CapturedAttachment attachment in budget.Read(post.Attachments, MaximumRows))
                    {
                        if (attachment == null || attachment.Position < 0 || !positions.Add(attachment.Position))
                            throw Invalid(CuratedExportValidationError.InvalidContext);
                        budget.Text(attachment.MediaId, true); budget.Text(attachment.Variant); budget.Text(attachment.Url, true);
                        budget.Text(attachment.OriginalFileName, true); budget.Text(attachment.PlannedPath, true);
                        budget.Text(attachment.ExpectedMd5, true); budget.Text(attachment.ExpectedSha256, true);
                        budget.Text(attachment.ExpectedSha1, true); budget.Text(attachment.AttachmentRevisionKey, true);
                        budget.Text(attachment.ProviderSizeHint?.RawValue, true); budget.Text(attachment.ProviderSizeHint?.UnitLabel, true);
                        copied.Add(attachment);
                    }
                    attachments = copied.OrderBy(value => value.Position).ToImmutableArray();
                }
                posts.Add(post with { Attachments = attachments.HasValue ? attachments.Value : null });
            }
            // The archive already owns provider ordering; do not invent a different post order here.
            result.Add(new(summary, posts.ToImmutable(), directories.Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray(), view.DecimalPostOrder));
        }
        return result.OrderBy(context => context.Summary.Id, ThreadComparer).ToImmutableArray();
    }

    private static readonly IComparer<ResourceId> ThreadComparer = Comparer<ResourceId>.Create((a, b) =>
    {
        int compared = StringComparer.Ordinal.Compare(a.Source, b.Source);
        if (compared == 0) compared = StringComparer.Ordinal.Compare(a.Board, b.Board);
        return compared != 0 ? compared : StringComparer.Ordinal.Compare(a.Thread, b.Thread);
    });
    private static IOrderedEnumerable<ResourceId> OrderThreads(IEnumerable<ResourceId> values) => values.OrderBy(value => value, ThreadComparer);
    private static IOrderedEnumerable<FavoriteAssetKey> OrderKeys(IEnumerable<FavoriteAssetKey> keys) => keys
        .OrderBy(key => key.Thread, ThreadComparer).ThenBy(key => key.MediaIdentifier, StringComparer.Ordinal)
        .ThenBy(key => key.Variant, StringComparer.Ordinal).ThenBy(key => key.Sha256, StringComparer.Ordinal);
    private static bool Digest(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    internal static CuratedExportValidationException Invalid(CuratedExportValidationError error) => new(error);

    private sealed class Budget
    {
        private readonly CancellationToken _token;
        private int _rows;
        private int _text;
        internal Budget(CancellationToken token) => _token = token;
        internal void Check() => _token.ThrowIfCancellationRequested();
        internal void Rows(int amount)
        {
            Check();
            if (amount < 0 || amount > MaximumRows - _rows)
                throw Invalid(CuratedExportValidationError.LimitExceeded);
            _rows += amount;
            Check();
        }
        internal void RowsForResolver(IEnumerable<MediaAssetSnapshot> snapshots)
        {
            foreach (MediaAssetSnapshot snapshot in snapshots)
            {
                if (snapshot == null) throw Invalid(CuratedExportValidationError.InvalidManifest);
                Rows(1); // the resolver also visits/sorts empty manifest views
                Rows(snapshot.Count);
            }
        }
        internal IEnumerable<T> Read<T>(IEnumerable<T> source, int limit)
        {
            Check();
            if (source == null) throw Invalid(CuratedExportValidationError.InvalidScope);
            int count = 0;
            foreach (T item in source)
            {
                Check();
                if (++count > limit) throw Invalid(CuratedExportValidationError.LimitExceeded);
                Rows(1);
                yield return item;
            }
            Check();
        }
        internal void Text(string? value, bool optional = false)
        {
            Check();
            if (value == null && optional) return;
            if (value == null || (!optional && string.IsNullOrWhiteSpace(value))) throw Invalid(CuratedExportValidationError.InvalidKey);
            if (value.Length > MaximumStringCharacters || value.Length > MaximumTextCharacters - _text)
                throw Invalid(CuratedExportValidationError.LimitExceeded);
            _text += value.Length;
            // Reject malformed Unicode without normalizing identities or prohibiting separators.
            for (int i = 0; i < value.Length; i++)
                if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i])))
                    throw Invalid(CuratedExportValidationError.InvalidKey);
        }
        internal void Thread(ResourceId thread)
        {
            if (thread == null) throw Invalid(CuratedExportValidationError.InvalidKey);
            Text(thread.Source); Text(thread.Board); Text(thread.Thread);
        }
        internal void Key(FavoriteAssetKey key)
        {
            if (key == null) throw Invalid(CuratedExportValidationError.InvalidKey);
            Thread(key.Thread); Text(key.MediaIdentifier); Text(key.Variant); Text(key.Sha256);
            try { FavoriteStore.Validate(key); }
            catch (ArgumentException) { throw Invalid(CuratedExportValidationError.InvalidKey); }
        }
        internal void Binding(string key, MediaAttachmentBinding binding)
        {
            if (binding == null || binding.Reference == null || binding.Provider == null || binding.Actual == null || binding.Acceptance == null)
                throw Invalid(CuratedExportValidationError.InvalidManifest);
            Text(key); Thread(binding.Reference.Resource); Text(binding.Reference.PostId); Text(binding.Reference.RevisionKey);
            Text(binding.AcquisitionRevisionKey); Text(binding.AssetId);
            Text(binding.Provider.MediaId); Text(binding.Provider.Variant); Text(binding.Provider.Url);
            Text(binding.Provider.OriginalFileName, true); Text(binding.Provider.ExpectedMd5, true);
            Text(binding.Provider.ExpectedSha256, true); Text(binding.Provider.ExpectedSha1, true);
            Text(binding.Provider.ProviderSizeHint?.RawValue, true); Text(binding.Provider.ProviderSizeHint?.UnitLabel, true);
            Text(binding.Actual.Sha256); Text(binding.Actual.Md5); Text(binding.Actual.Sha1, true);
            Text(binding.Acceptance.ProfileId, true); Text(binding.Acceptance.PluginOwnerId, true); Text(binding.Acceptance.SourceId, true);
            if (binding.PassiveImport != null)
            {
                Text(binding.PassiveImport.OperationId); Text(binding.PassiveImport.PackageFingerprint);
                Text(binding.PassiveImport.IncomingRevisionKey, true);
            }
            try { MediaIntegrityValidation.ValidateBinding(key, binding); }
            catch (Exception error) when (error is ArgumentException or System.IO.InvalidDataException or FormatException)
            { throw Invalid(CuratedExportValidationError.InvalidManifest); }
        }
    }
}

/// <summary>
/// Immutable verification ledger over exactly one frozen plan. Included means selected, not copied.
/// A caller may record a verified-read result but cannot resolve, add or substitute an original here.
/// Preview requests remain unsupported until a separately reviewed producer contract exists.
/// </summary>
internal sealed class CuratedExportLedger
{
    private readonly ImmutableDictionary<FavoriteAssetKey, int> _indices;
    private CuratedExportLedger(CuratedExportSelection selection, ImmutableList<CuratedExportLedgerItem> originals,
        ImmutableDictionary<FavoriteAssetKey, int> indices)
    { Selection = selection; Originals = originals; _indices = indices; }
    internal CuratedExportSelection Selection { get; }
    internal ImmutableList<CuratedExportLedgerItem> Originals { get; }
    internal ImmutableArray<CuratedExportLedgerItem> Omissions => Selection.Omissions;
    internal bool AllOriginalsVerified => Originals.All(item => item.Status == CuratedExportStatus.OriginalIncluded
        && item.Verification == CuratedOriginalVerification.Verified);
    internal int Count(CuratedExportStatus status) => Originals.Count(item => item.Status == status) + Omissions.Count(item => item.Status == status);

    internal static CuratedExportLedger Start(CuratedExportSelection selection)
    {
        ImmutableList<CuratedExportLedgerItem> rows = selection.Originals
            .Select(item => new CuratedExportLedgerItem(item.Entry.Key, item.Status, item.Status == CuratedExportStatus.OriginalIncluded
                ? CuratedOriginalVerification.Required : CuratedOriginalVerification.NotApplicable)).ToImmutableList();
        return new(selection, rows, rows.Select((row, index) => (row.Key, Index: index))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Index));
    }

    /// <summary>Terminal result supplied only after checking the plan's concrete manifest/asset handle.</summary>
    internal CuratedExportLedger RecordOriginal(FavoriteAssetKey key, CuratedExportStatus outcome)
    {
        if (outcome is not (CuratedExportStatus.OriginalIncluded or CuratedExportStatus.OriginalMissing
            or CuratedExportStatus.OriginalVerificationFailed or CuratedExportStatus.Cancelled))
            throw CuratedExportSelection.Invalid(CuratedExportValidationError.InvalidLedgerUpdate);
        if (key == null || !_indices.TryGetValue(key, out int index) || Originals[index].Status != CuratedExportStatus.OriginalIncluded
            || Originals[index].Verification != CuratedOriginalVerification.Required)
            throw CuratedExportSelection.Invalid(CuratedExportValidationError.InvalidLedgerUpdate);
        return new(Selection, Originals.SetItem(index, new(key, outcome, outcome == CuratedExportStatus.OriginalIncluded
            ? CuratedOriginalVerification.Verified : CuratedOriginalVerification.NotApplicable)), _indices);
    }

    internal CuratedExportLedger CancelPending() => new(Selection, Originals.Select(item =>
        item.Verification == CuratedOriginalVerification.Required
            ? item with { Status = CuratedExportStatus.Cancelled, Verification = CuratedOriginalVerification.NotApplicable } : item).ToImmutableList(), _indices);
}
