using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tags;

/// <summary>Constant domain rejections. Values never contain or echo names or identities.</summary>
internal enum PrivateTagStoreError
{
    None,
    StaleRevision,
    InvalidName,
    UnknownTag,
    NameCollision,
    InvalidThread,
    InvalidTarget,
    CapacityExceeded,
    RevisionOverflow,
    /// <summary>A local media write named a registration the registry no longer resolves the file to.</summary>
    StaleTarget,
    /// <summary>The host could not revalidate a loose file (gone, changed, no longer loose) before adding a tag.</summary>
    TargetUnavailable,
    /// <summary>A new tag would exceed the configured own-tag limit (<see cref="PrivateTagLimits.TagLimit"/>).</summary>
    TagLimitReached,
    /// <summary>The write would grow the file past its configured size limit (<see cref="PrivateTagLimits.DocumentLimitBytes"/>).</summary>
    DocumentLimitReached,
    /// <summary>A merge named no other tag, named the target among the merged tags, or an undo did not fit this store.</summary>
    InvalidMerge
}

/// <summary>
/// Another name of an own tag (private-tag-store-v3). Typing it, or a spelling of it that <see cref="TagNameFold.Fold(PrivateTagName)"/>
/// makes equal, gives the tag <see cref="Tag"/>. A merge keeps every merged-away name as an alias of the target; <see cref="Former"/>
/// then is the merged-away tag's ID, so a saved filter that still names that ID finds the target. Hand-added aliases have none.
/// </summary>
internal sealed record PrivateTagAlias(PrivateTagName Name, TagId Tag, TagId? Former = null);

/// <summary>A rejected tag operation. Nothing was written; the message is a constant.</summary>
internal sealed class PrivateTagStoreException : Exception
{
    internal PrivateTagStoreException(PrivateTagStoreError error)
        : base("The private tag operation was rejected: " + error + ".") => Error = error;

    internal PrivateTagStoreError Error { get; }
}

/// <summary>
/// One complete immutable view of the profile's own tags: a single shared vocabulary plus
/// separate thread and media assignments. There is no inheritance between the two target kinds.
/// Membership does not assert that a thread or media occurrence exists in the archive.
/// </summary>
internal sealed class PrivateTagStoreSnapshot
{
    private Dictionary<LocalMediaTagTarget, HashSet<TagId>>? _localIndex;

    internal PrivateTagStoreSnapshot(long revision, string fingerprint, IEnumerable<PrivateTagDefinition> tags,
        IEnumerable<PrivateThreadTagAssignment> threads, IEnumerable<PrivateMediaTagAssignment> media,
        LocalMediaRegistry? localRegistry = null, IEnumerable<PrivateLocalMediaTagAssignment>? local = null, int formatVersion = 1,
        IEnumerable<PrivateTagAlias>? aliases = null)
    {
        Revision = revision;
        Fingerprint = fingerprint;
        Tags = Array.AsReadOnly(tags.ToArray());
        ThreadAssignments = Array.AsReadOnly(threads.ToArray());
        MediaAssignments = Array.AsReadOnly(media.ToArray());
        LocalRegistry = localRegistry ?? LocalMediaRegistry.Empty;
        LocalAssignments = Array.AsReadOnly((local ?? Array.Empty<PrivateLocalMediaTagAssignment>()).ToArray());
        FormatVersion = formatVersion;
        Aliases = Array.AsReadOnly((aliases ?? Array.Empty<PrivateTagAlias>()).ToArray());
    }

    internal long Revision { get; }
    /// <summary>Other names of the tags (v3), in the order they were added. Empty for v1 and v2 documents.</summary>
    internal IReadOnlyList<PrivateTagAlias> Aliases { get; }

    /// <summary>The aliases of one tag, in the order they were added.</summary>
    internal IReadOnlyList<PrivateTagAlias> AliasesOf(TagId tag) => Aliases.Where(item => item.Tag == tag).ToArray();

    /// <summary>
    /// The tag a typed name stands for: the tag with that name, else the alias with that name, else the one tag that every alias
    /// with the same <see cref="TagNameFold.Fold(PrivateTagName)"/> text names. <paramref name="alias"/> is the alias that matched,
    /// null for a direct name. Null when nothing matches (the name would create a new tag).
    /// </summary>
    internal PrivateTagDefinition? Resolve(PrivateTagName name, out PrivateTagAlias? alias)
    {
        ArgumentNullException.ThrowIfNull(name);
        alias = null;
        _byKey ??= Tags.GroupBy(item => item.Name.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (_byKey.TryGetValue(name.Key, out PrivateTagDefinition? direct)) return direct;
        alias = PrivateTagAliasIndex.Find(Aliases, name);
        return alias is { } found ? Find(found.Tag) : null;
    }

    /// <summary>Resolves a typed text (see <see cref="Resolve(PrivateTagName, out PrivateTagAlias?)"/>); null for an invalid name.</summary>
    internal PrivateTagDefinition? Resolve(string? text, out PrivateTagAlias? alias)
    {
        alias = null;
        return PrivateTagName.TryCreate(text, out PrivateTagName? name, out _) && name != null ? Resolve(name, out alias) : null;
    }

    /// <summary>
    /// The tag an ID stands for now: the tag itself, or the target a merge gave it to (an alias whose <see cref="PrivateTagAlias.Former"/>
    /// is that ID). Null for an ID nothing knows, which a saved filter then reports as an unknown tag.
    /// </summary>
    internal TagId? ResolveId(TagId id)
    {
        if (Find(id) != null) return id;
        foreach (PrivateTagAlias alias in Aliases)
            if (alias.Former == id && Find(alias.Tag) != null) return alias.Tag;
        return null;
    }

    private Dictionary<string, PrivateTagDefinition>? _byKey;
    /// <summary>SHA-256 of the committed file bytes; the empty store hashes zero bytes.</summary>
    internal string Fingerprint { get; }
    /// <summary>1 for a v1 (or missing) document, 2 once a local write upgraded it. v2 stays v2.</summary>
    internal int FormatVersion { get; }
    internal IReadOnlyList<PrivateTagDefinition> Tags { get; }
    internal IReadOnlyList<PrivateThreadTagAssignment> ThreadAssignments { get; }
    /// <summary>Exact bound attachment-version assignments only. Local assignments are in <see cref="LocalAssignments"/>.</summary>
    internal IReadOnlyList<PrivateMediaTagAssignment> MediaAssignments { get; }
    /// <summary>Registered loose occurrences. A registration without assignments is kept so adoption stays idempotent.</summary>
    internal LocalMediaRegistry LocalRegistry { get; }
    internal IReadOnlyList<PrivateLocalMediaTagAssignment> LocalAssignments { get; }

    /// <summary>How the registry relates to this observation. Read-only; nothing is registered.</summary>
    internal LocalMediaResolution ResolveLocal(LocalMediaObservation observation) => LocalRegistry.Resolve(observation);

    /// <summary>Tags assigned to exactly this local occurrence revision, in vocabulary order.</summary>
    internal IReadOnlyList<PrivateTagDefinition> TagsOf(LocalMediaTagTarget target)
    {
        _localIndex ??= LocalAssignments.GroupBy(item => item.Target)
            .ToDictionary(group => group.Key, group => group.Select(item => item.TagId).ToHashSet());
        return _localIndex.TryGetValue(target, out HashSet<TagId>? ids)
            ? Tags.Where(item => ids.Contains(item.Id)).ToArray() : Array.Empty<PrivateTagDefinition>();
    }

    /// <summary>
    /// Tags of the subject: the exact bound version, or for a local file the registration the caller saw
    /// (<see cref="MediaTagSubject.Local.Expected"/>) or, when it saw none, whatever the observation resolves to now.
    /// Thread tags are never included.
    /// </summary>
    internal IReadOnlyList<PrivateTagDefinition> TagsOf(MediaTagSubject subject) => subject switch
    {
        MediaTagSubject.Bound bound => TagsOf(bound.Target),
        MediaTagSubject.Local { Expected: { } expected } => TagsOf(expected),
        MediaTagSubject.Local local => ResolveLocal(local.Observation) is { IsRegistered: true, Target: { } target }
            ? TagsOf(target) : Array.Empty<PrivateTagDefinition>(),
        _ => throw new ArgumentNullException(nameof(subject))
    };

    /// <summary>True when exactly this subject carries the tag (same rules as <see cref="TagsOf(MediaTagSubject)"/>).</summary>
    internal bool Carries(TagId tag, MediaTagSubject subject) => TagsOf(subject).Any(item => item.Id == tag);

    internal PrivateTagDefinition? Find(TagId id) => Tags.FirstOrDefault(item => item.Id == id);

    /// <summary>Tags assigned to exactly this thread, in vocabulary order. Media tags are not included.</summary>
    internal IReadOnlyList<PrivateTagDefinition> TagsOf(ResourceId thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var ids = ThreadAssignments.Where(item => item.Thread == thread).Select(item => (TagId)item.TagId).ToHashSet();
        return Tags.Where(item => ids.Contains(item.Id)).ToArray();
    }

    /// <summary>
    /// How many distinct files carry the tag. An earlier and a later revision key of the same file (a handed-over
    /// assignment) count once; two different files, or the same bytes in two attachments, count twice.
    /// </summary>
    internal int DistinctMediaCount(TagId tag) => DistinctMediaCounts().GetValueOrDefault(tag);

    /// <summary>
    /// Distinct files per tag in one pass; tags without media are absent. Bound files count by identity, local files by
    /// occurrence revision; a bound and a local assignment are always two (different namespaces, never merged by hash).
    /// </summary>
    internal IReadOnlyDictionary<TagId, int> DistinctMediaCounts()
    {
        Dictionary<TagId, int> counts = MediaAssignments.GroupBy(item => (TagId)item.TagId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Target.Identity).Distinct().Count());
        foreach (IGrouping<TagId, PrivateLocalMediaTagAssignment> group in LocalAssignments.GroupBy(item => item.TagId))
            counts[group.Key] = counts.GetValueOrDefault(group.Key) + group.Select(item => item.Target).Distinct().Count();
        return counts;
    }

    /// <summary>Tags assigned to exactly this media version, in vocabulary order. Thread tags are not included.</summary>
    internal IReadOnlyList<PrivateTagDefinition> TagsOf(PrivateMediaTagTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var ids = MediaAssignments.Where(item => item.Target == target).Select(item => (TagId)item.TagId).ToHashSet();
        return Tags.Where(item => ids.Contains(item.Id)).ToArray();
    }
}

/// <summary>Result of one accepted call. <see cref="Changed"/> is false for an idempotent no-op.</summary>
internal sealed record PrivateTagCommit(bool Changed, TagId Tag, PrivateTagStoreSnapshot Snapshot);

/// <summary>
/// How to take one merge back: the complete document content before it, valid only while the store is still at the revision the
/// merge wrote (<see cref="Revision"/>). Kept in memory by the caller, never persisted; the merge itself stays visible as aliases.
/// </summary>
internal sealed class PrivateTagMergeUndo
{
    internal PrivateTagMergeUndo(long revision, TagId target, IReadOnlyList<TagId> merged, byte[] prior)
    {
        Revision = revision;
        Target = target;
        Merged = merged;
        Prior = prior;
    }

    /// <summary>The revision the merge committed; any later write makes the undo stale.</summary>
    internal long Revision { get; }
    internal TagId Target { get; }
    internal IReadOnlyList<TagId> Merged { get; }
    internal byte[] Prior { get; }
}

/// <summary>An accepted merge: the commit and the way back.</summary>
internal sealed record PrivateTagMergeCommit(PrivateTagCommit Commit, PrivateTagMergeUndo Undo);

/// <summary>Alias lookups shared by the store and its snapshots.</summary>
internal static class PrivateTagAliasIndex
{
    /// <summary>The alias with exactly this name key, else the alias whose fold text matches when all such aliases name one tag.</summary>
    internal static PrivateTagAlias? Find(IReadOnlyList<PrivateTagAlias> aliases, PrivateTagName name)
    {
        if (aliases.Count == 0) return null;
        foreach (PrivateTagAlias alias in aliases)
            if (string.Equals(alias.Name.Key, name.Key, StringComparison.Ordinal)) return alias;
        string folded = TagNameFold.Fold(name);
        PrivateTagAlias? match = null;
        foreach (PrivateTagAlias alias in aliases)
        {
            if (!string.Equals(TagNameFold.Fold(alias.Name), folded, StringComparison.Ordinal)) continue;
            if (match != null && match.Tag != alias.Tag) return null;
            match ??= alias;
        }
        return match;
    }
}

/// <summary>Points of one changing write where tests inject a failure or a simulated crash.</summary>
internal enum PrivateTagWritePhase { BeforeBackupCheck, BeforeReplace, AfterReplace }

/// <summary>
/// Profile-owned persistence for own tags (contracts: private-tag-store-v1, private-tag-store-v2 with the
/// local-media-occurrence-v1 registry, private-tag-store-v3 with aliases and tag merging). One JSON document,
/// rewritten atomically with a backup, guarded by the profile lease and store-writer admission.
/// Invalid or newer documents are never overwritten. No transfer, no source tags and no
/// archive-existence checks live here.
/// </summary>
internal sealed class PrivateTagStore
{
    internal const string FileName = "private-tags.json";
    // Permanent copy of the v1 bytes taken by the first write that upgrades v1 to v2 (the way back to 1.0.2).
    // Created once, never rewritten, rotated or deleted by this store; unlike ".bak" it survives every later write.
    internal const string V1BackupFileName = "private-tags.v1.bak";
    // Defensive work bounds, not promised product quotas. Tags and bytes are read ceilings; the write limits below them
    // are settings (PrivateTagLimits). The per-item and assignment bounds stay fixed technical limits: a tag name has at
    // most 128 scalars / 512 UTF-8 bytes (PrivateTagName), and there are at most 50,000 thread, 50,000 bound media and
    // 50,000 local assignments, 50,000 local occurrences with at most 64 revisions each and 100,000 revisions in all.
    internal const int MaximumTags = PrivateTagCatalog.MaximumDefinitions;
    internal const int MaximumThreadAssignments = ThreadTagProjection.MaximumAssignments;
    internal const int MaximumMediaAssignments = MediaTagProjection.MaximumAssignments;
    internal const int MaximumLocalOccurrences = 50_000;
    internal const int MaximumLocalRevisionsPerOccurrence = 64;
    internal const int MaximumLocalRevisions = 100_000;
    internal const int MaximumLocalAssignments = 50_000;
    // Read ceiling: the whole document is parsed in memory and rewritten on every change, so it stays bounded.
    internal const int MaximumBytes = 128 * 1024 * 1024;
    // v1: vocabulary, thread and bound media assignments. v2 adds the local occurrence registry and local assignments.
    // A v1 document is read without rewriting it; only a write that commits local data upgrades it, and v2 stays v2.
    private const int BoundOnlyVersion = 1;
    private const int LocalVersion = 2;
    // v3 adds "Aliases" (other names of tags; merges keep the merged-away names there). Written only once an alias exists, then sticky.
    private const int AliasVersion = 3;
    /// <summary>Read ceiling of aliases, a fixed technical bound like the assignment counts; the document size limit governs growth.</summary>
    internal const int MaximumAliases = PrivateTagCatalog.MaximumDefinitions;
    private const string Invalid = "The private tag index is invalid.";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ProfileLease _lease;
    private readonly string _path;
    // SHA-256 of the last document bytes this instance validated in full with Parse (a pure function of the
    // bytes). A backup with exactly these bytes is valid without a second parse. Only touched under the store lock.
    private byte[]? _validatedDigest;

    internal PrivateTagStore(string profileDirectory, ProfileLease lease, PrivateTagLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
        Limits = limits ?? new PrivateTagLimits();
    }

    /// <summary>The configured write limits this instance enforces (defaults when none were given).</summary>
    internal PrivateTagLimits Limits { get; }

    private sealed class Model
    {
        internal int Version = BoundOnlyVersion;
        internal long Revision;
        internal PrivateTagCatalog Catalog = new();
        internal readonly List<PrivateThreadTagAssignment> Threads = new();
        internal readonly HashSet<PrivateThreadTagAssignment> ThreadSet = new();
        internal readonly List<PrivateMediaTagAssignment> Media = new();
        internal readonly HashSet<PrivateMediaTagAssignment> MediaSet = new();
        internal readonly List<LocalMediaOccurrence> Occurrences = new();
        internal readonly Dictionary<LocalMediaLocator, int> OccurrenceByLocator = new();
        internal readonly Dictionary<LocalMediaOccurrenceId, int> OccurrenceById = new();
        internal readonly HashSet<Guid> LocalIds = new();
        internal int LocalRevisionCount;
        internal readonly List<PrivateLocalMediaTagAssignment> Local = new();
        internal readonly HashSet<PrivateLocalMediaTagAssignment> LocalSet = new();
        internal readonly List<PrivateTagAlias> Aliases = new();

        internal bool HasLocalData => Occurrences.Count > 0 || Local.Count > 0;

        internal PrivateTagStoreSnapshot Snapshot(byte[] bytes) =>
            new(Revision, Convert.ToHexString(SHA256.HashData(bytes)), Catalog.Definitions, Threads, Media,
                new LocalMediaRegistry(Occurrences), Local, Version, Aliases);

        /// <summary>The tag a typed name stands for: by name, else through an alias (exact, then fold). Null: none.</summary>
        internal PrivateTagDefinition? Resolve(PrivateTagName name) =>
            Catalog.Find(name) ?? (PrivateTagAliasIndex.Find(Aliases, name) is { } alias
                ? Catalog.Definitions.FirstOrDefault(item => item.Id == alias.Tag) : null);

        /// <summary>Replaces every content section with <paramref name="other"/>'s; revision and version stay this model's.</summary>
        internal void ReplaceContent(Model other)
        {
            Catalog = other.Catalog;
            Threads.Clear(); Threads.AddRange(other.Threads);
            ThreadSet.Clear(); ThreadSet.UnionWith(other.ThreadSet);
            Media.Clear(); Media.AddRange(other.Media);
            MediaSet.Clear(); MediaSet.UnionWith(other.MediaSet);
            Occurrences.Clear(); OccurrenceByLocator.Clear(); OccurrenceById.Clear(); LocalRevisionCount = 0;
            foreach (LocalMediaOccurrence occurrence in other.Occurrences) AddOccurrence(occurrence);
            LocalIds.Clear(); LocalIds.UnionWith(other.LocalIds);
            Local.Clear(); Local.AddRange(other.Local);
            LocalSet.Clear(); LocalSet.UnionWith(other.LocalSet);
            Aliases.Clear(); Aliases.AddRange(other.Aliases);
        }

        internal LocalMediaResolution Resolve(LocalMediaObservation observation) => LocalMediaRegistry.Resolve(
            OccurrenceByLocator.TryGetValue(observation.Locator, out int index) ? Occurrences[index] : null, observation);

        internal bool Registered(LocalMediaTagTarget target) => OccurrenceById.TryGetValue(target.Occurrence, out int index)
            && Occurrences[index].Find(target.Revision) != null;

        /// <summary>
        /// Registers the observed file: a new occurrence for an unknown locator, or a new revision of the occurrence at that
        /// locator for an asset record it has not seen. Only called inside a write that also publishes an assignment.
        /// </summary>
        internal LocalMediaTagTarget Adopt(LocalMediaObservation observation)
        {
            LocalMediaResolution resolution = Resolve(observation);
            if (resolution is { IsRegistered: true, Target: { } known }) return known;
            if (resolution.State == LocalMediaResolutionState.Conflict)
                throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
            if (LocalRevisionCount >= MaximumLocalRevisions)
                throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            LocalMediaRevision revision = new(new LocalMediaRevisionId(NewId()), observation.AssetId, observation.Length, observation.Sha256);
            if (resolution.State == LocalMediaResolutionState.NewRevision)
            {
                int index = OccurrenceByLocator[observation.Locator];
                if (Occurrences[index].Revisions.Count >= MaximumLocalRevisionsPerOccurrence)
                    throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
                Occurrences[index] = Occurrences[index].WithRevision(revision);
                LocalRevisionCount++;
                return new(Occurrences[index].Id, revision.Id);
            }
            if (Occurrences.Count >= MaximumLocalOccurrences)
                throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            LocalMediaOccurrence occurrence = new(new LocalMediaOccurrenceId(NewId()), observation.Locator, [revision]);
            AddOccurrence(occurrence);
            return new(occurrence.Id, revision.Id);
        }

        internal void AddOccurrence(LocalMediaOccurrence occurrence)
        {
            OccurrenceByLocator.Add(occurrence.Locator, Occurrences.Count);
            OccurrenceById.Add(occurrence.Id, Occurrences.Count);
            Occurrences.Add(occurrence);
            LocalRevisionCount += occurrence.Revisions.Count;
        }

        /// <summary>Drops the given occurrences with every assignment that names them; tags stay.</summary>
        internal int RemoveOccurrences(Func<LocalMediaOccurrence, bool> selected)
        {
            HashSet<LocalMediaOccurrenceId> gone = Occurrences.Where(selected).Select(item => item.Id).ToHashSet();
            if (gone.Count == 0) return 0;
            int removed = Local.RemoveAll(item => gone.Contains(item.Target.Occurrence));
            LocalSet.RemoveWhere(item => gone.Contains(item.Target.Occurrence));
            List<LocalMediaOccurrence> kept = Occurrences.Where(item => !gone.Contains(item.Id)).ToList();
            Occurrences.Clear(); OccurrenceByLocator.Clear(); OccurrenceById.Clear(); LocalRevisionCount = 0;
            foreach (LocalMediaOccurrence occurrence in kept) AddOccurrence(occurrence);
            return removed + gone.Count;
        }

        private Guid NewId()
        {
            // Occurrence and revision IDs are unique across the whole registry document.
            Guid id;
            do id = Guid.NewGuid(); while (!LocalIds.Add(id));
            return id;
        }
    }

    /// <summary>Test seam: called at each phase of a changing write; throwing there aborts (or, after the replace, simulates a crash).</summary>
    internal Action<PrivateTagWritePhase>? WritePhaseForTests { get; init; }

    /// <summary>Test seam: called with the file name each time this instance parses a document file in full.</summary>
    internal Action<string>? DocumentParsedForTests { get; init; }

    internal PrivateTagStoreSnapshot ReadSnapshot() => _lease.RunExclusive(() =>
    {
        (Model model, byte[] bytes) = Read();
        return model.Snapshot(bytes);
    });

    /// <summary>Creates a tag, or returns the existing tag with the same comparison key unchanged.</summary>
    internal PrivateTagCommit Define(string? name, long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return DefineAdmitted(name, admission, expectedRevision, token);
    }

    internal PrivateTagCommit DefineAdmitted(string? name, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
    {
        PrivateTagName parsed = RequireName(name);
        return Mutate(admission, expectedRevision, token, model =>
        {
            // Create-or-get also gets through an alias: typing a merged-away name gives the tag it was merged into.
            PrivateTagDefinition? existing = model.Resolve(parsed);
            if (existing is not null) return (false, existing.Id);
            RequireTagRoom(model);
            TagId id = new(Guid.NewGuid());
            if (!model.Catalog.TryDefine(id, parsed, model.Catalog.Revision, out PrivateTagCatalog next,
                out PrivateTagCatalogError error))
                throw new PrivateTagStoreException(Map(error));
            model.Catalog = next;
            return (true, id);
        });
    }

    /// <summary>Renames in place; the ID and every assignment are retained.</summary>
    internal PrivateTagCommit Rename(TagId tag, string? name, long? expectedRevision = null,
        CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return RenameAdmitted(tag, name, admission, expectedRevision, token);
    }

    internal PrivateTagCommit RenameAdmitted(TagId tag, string? name, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
    {
        PrivateTagName parsed = RequireName(name);
        return Mutate(admission, expectedRevision, token, model =>
        {
            // A name that is an alias: of this tag, the alias becomes its name again; of another tag, it is taken.
            int alias = model.Aliases.FindIndex(item => item.Name.Key == parsed.Key);
            if (alias >= 0 && model.Aliases[alias].Tag != tag)
                throw new PrivateTagStoreException(model.Catalog.Contains(tag) ? PrivateTagStoreError.NameCollision : PrivateTagStoreError.UnknownTag);
            if (!model.Catalog.TryRename(tag, parsed, model.Catalog.Revision, out PrivateTagCatalog next,
                out PrivateTagCatalogError error))
                throw new PrivateTagStoreException(Map(error));
            bool changed = !ReferenceEquals(next, model.Catalog);
            model.Catalog = next;
            if (alias >= 0) { model.Aliases.RemoveAt(alias); changed = true; }
            return (changed, tag);
        });
    }

    /// <summary>Idempotent thread membership. Media of that thread are not affected.</summary>
    internal PrivateTagCommit SetThread(TagId tag, ResourceId? thread, bool assigned, long? expectedRevision = null,
        CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return SetThreadAdmitted(tag, thread, assigned, admission, expectedRevision, token);
    }

    internal PrivateTagCommit SetThreadAdmitted(TagId tag, ResourceId? thread, bool assigned,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        if (!IsThread(thread)) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidThread);
        var assignment = new PrivateThreadTagAssignment(tag, thread!);
        return Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            if (model.ThreadSet.Contains(assignment) == assigned) return (false, tag);
            if (assigned)
            {
                if (model.Threads.Count >= MaximumThreadAssignments)
                    throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
                model.Threads.Add(assignment);
            }
            else model.Threads.Remove(assignment);
            return (true, tag);
        });
    }

    /// <summary>Idempotent membership of one exact media version. No thread inheritance or hash fan-out.</summary>
    internal PrivateTagCommit SetMedia(TagId tag, PrivateMediaTagTarget? target, bool assigned,
        long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return SetMediaAdmitted(tag, target, assigned, admission, expectedRevision, token);
    }

    internal PrivateTagCommit SetMediaAdmitted(TagId tag, PrivateMediaTagTarget? target, bool assigned,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        if (!IsTarget(target)) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
        var assignment = new PrivateMediaTagAssignment(tag, target!);
        return Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            // Removing a tag removes it from the file, whatever revision key an assignment was stored under:
            // an earlier key kept by a hand-over would otherwise bring the tag back at the next reconcile.
            if (!assigned) return (model.Media.RemoveAll(item => item.TagId == assignment.TagId
                && item.Target.Identity == assignment.Target.Identity) > 0, tag);
            if (model.MediaSet.Contains(assignment)) return (false, tag);
            if (model.Media.Count >= MaximumMediaAssignments)
                throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            model.Media.Add(assignment);
            return (true, tag);
        });
    }

    /// <summary>
    /// One tag on several exact media versions in one write: all of them or none. Each target is
    /// idempotent like <see cref="SetMedia"/>; a target that already has (or lacks) the tag changes nothing.
    /// Removing works per file: assignments of the same file under any other revision key go too.
    /// An unknown tag, an invalid target or the capacity limit rejects the whole batch.
    /// </summary>
    internal PrivateTagCommit SetMediaBatch(TagId tag, IReadOnlyCollection<PrivateMediaTagTarget?> targets, bool assigned,
        long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return SetMediaBatchAdmitted(tag, targets, assigned, admission, expectedRevision, token);
    }

    internal PrivateTagCommit SetMediaBatchAdmitted(TagId tag, IReadOnlyCollection<PrivateMediaTagTarget?> targets, bool assigned,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Any(target => !IsTarget(target))) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
        List<PrivateMediaTagAssignment> wanted = targets.Select(target => new PrivateMediaTagAssignment(tag, target!)).Distinct().ToList();
        return Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            if (assigned)
            {
                List<PrivateMediaTagAssignment> fresh = wanted.Where(item => !model.MediaSet.Contains(item)).ToList();
                if (fresh.Count == 0) return (false, tag);
                if (model.Media.Count + fresh.Count > MaximumMediaAssignments)
                    throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
                model.Media.AddRange(fresh);
            }
            else
            {
                // By file, not by exact key: see SetMedia.
                HashSet<(PrivateMediaTagId, MediaIdentity)> gone = wanted.Select(item => (item.TagId, item.Target.Identity)).ToHashSet();
                if (model.Media.RemoveAll(item => gone.Contains((item.TagId, item.Target.Identity))) == 0) return (false, tag);
            }
            return (true, tag);
        });
    }

    /// <summary>
    /// One tag on several media subjects (bound versions and/or loose local files) in one write: all or none.
    /// Bound subjects behave exactly like <see cref="SetMediaBatch"/>. A local add registers the observed file in
    /// the same write when it is not registered yet (new occurrence, or new revision of the occurrence at that
    /// locator), so a published assignment never names an unpublished identity. A local subject that carries the
    /// registration the caller saw is rejected as <see cref="PrivateTagStoreError.StaleTarget"/> when the registry
    /// now resolves the file to anything else. Local removal removes exactly the seen (or resolved) target; it
    /// never touches another occurrence, revision, equal bytes or a bound assignment. The store does not verify
    /// files: the host revalidates membership and bytes of every local add before calling this.
    /// </summary>
    internal PrivateTagCommit SetMediaSubjects(TagId tag, IReadOnlyCollection<MediaTagSubject> subjects, bool assigned,
        long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return SetMediaSubjectsAdmitted(tag, subjects, assigned, admission, expectedRevision, token);
    }

    internal PrivateTagCommit SetMediaSubjectsAdmitted(TagId tag, IReadOnlyCollection<MediaTagSubject> subjects, bool assigned,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        MediaTagSubject[] frozen = RequireSubjects(subjects);
        return Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            return (ApplySubjects(model, tag, frozen, assigned), tag);
        });
    }

    /// <summary>
    /// Several tags on ONE media subject in one write: all or none. The same subject rules as <see cref="SetMediaSubjects"/> (a local
    /// add registers the observed file in the same write; a stale or conflicting registration rejects the whole write). Used where
    /// the tags of one file version are given to another version at once.
    /// </summary>
    internal PrivateTagCommit SetSubjectTagsAdmitted(IReadOnlyCollection<TagId> tags, MediaTagSubject subject, bool assigned,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        TagId[] ids = tags.Distinct().ToArray();
        if (ids.Length == 0) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
        MediaTagSubject[] frozen = RequireSubjects(new[] { subject });
        return Mutate(admission, expectedRevision, token, model =>
        {
            foreach (TagId tag in ids) RequireKnown(model, tag);
            bool changed = false;
            foreach (TagId tag in ids) changed |= ApplySubjects(model, tag, frozen, assigned);
            return (changed, ids[0]);
        });
    }

    /// <summary>
    /// Create-or-get a tag by name and give it to the subjects in ONE write, so a failed assignment can never leave a
    /// newly created, unassigned tag behind. Same subject rules as <see cref="SetMediaSubjects"/>.
    /// </summary>
    internal PrivateTagCommit DefineAndSetMediaSubjects(string? name, IReadOnlyCollection<MediaTagSubject> subjects,
        long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return DefineAndSetMediaSubjectsAdmitted(name, subjects, admission, expectedRevision, token);
    }

    internal PrivateTagCommit DefineAndSetMediaSubjectsAdmitted(string? name, IReadOnlyCollection<MediaTagSubject> subjects,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        PrivateTagName parsed = RequireName(name);
        MediaTagSubject[] frozen = RequireSubjects(subjects);
        return Mutate(admission, expectedRevision, token, model =>
        {
            bool defined = false;
            TagId id;
            if (model.Resolve(parsed) is { } existing) id = existing.Id;
            else
            {
                RequireTagRoom(model);
                id = new(Guid.NewGuid());
                if (!model.Catalog.TryDefine(id, parsed, model.Catalog.Revision, out PrivateTagCatalog next,
                    out PrivateTagCatalogError error))
                    throw new PrivateTagStoreException(Map(error));
                model.Catalog = next;
                defined = true;
            }
            return (ApplySubjects(model, id, frozen, assigned: true) | defined, id);
        });
    }

    /// <summary>
    /// Removes exactly these local assignments (for example orphans whose file can no longer be found), in one write
    /// bound to the revision the caller looked at. Registrations and tag definitions stay. Absent ones change nothing.
    /// </summary>
    internal PrivateTagCommit RemoveLocalAssignmentsAdmitted(IReadOnlyCollection<PrivateLocalMediaTagAssignment> assignments,
        ProfileAdmissionToken admission, long? expectedRevision = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Any(item => item is null)) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
        HashSet<PrivateLocalMediaTagAssignment> gone = assignments.ToHashSet();
        return Mutate(admission, expectedRevision, token, model =>
        {
            int removed = model.Local.RemoveAll(gone.Contains);
            model.LocalSet.ExceptWith(gone);
            return (removed > 0, default);
        });
    }

    private static MediaTagSubject[] RequireSubjects(IReadOnlyCollection<MediaTagSubject> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        MediaTagSubject[] frozen = subjects.ToArray();
        foreach (MediaTagSubject subject in frozen)
        {
            bool valid = subject switch
            {
                MediaTagSubject.Bound bound => IsTarget(bound.Target),
                MediaTagSubject.Local local => local.Observation is not null
                    && (local.Expected is not { } seen || seen.Occurrence.Value != Guid.Empty && seen.Revision.Value != Guid.Empty),
                _ => false
            };
            if (!valid) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
        }
        return frozen;
    }

    private static bool ApplySubjects(Model model, TagId tag, IReadOnlyList<MediaTagSubject> subjects, bool assigned)
    {
        bool changed = false;
        List<PrivateMediaTagAssignment> bound = subjects.OfType<MediaTagSubject.Bound>()
            .Select(item => new PrivateMediaTagAssignment(tag, item.Target)).Distinct().ToList();
        if (assigned)
        {
            List<PrivateMediaTagAssignment> fresh = bound.Where(item => !model.MediaSet.Contains(item)).ToList();
            if (model.Media.Count + fresh.Count > MaximumMediaAssignments)
                throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            foreach (PrivateMediaTagAssignment item in fresh) { model.Media.Add(item); model.MediaSet.Add(item); }
            changed |= fresh.Count > 0;
        }
        else if (bound.Count > 0)
        {
            // By file, not by exact key: see SetMedia.
            HashSet<(PrivateMediaTagId, MediaIdentity)> gone = bound.Select(item => (item.TagId, item.Target.Identity)).ToHashSet();
            int removed = model.Media.RemoveAll(item => gone.Contains((item.TagId, item.Target.Identity)));
            model.MediaSet.RemoveWhere(item => gone.Contains((item.TagId, item.Target.Identity)));
            changed |= removed > 0;
        }

        foreach (MediaTagSubject.Local local in subjects.OfType<MediaTagSubject.Local>().Distinct())
        {
            LocalMediaResolution resolution = model.Resolve(local.Observation);
            bool asSeen = local.Expected is not { } seen || resolution.IsRegistered && resolution.Target == seen;
            if (assigned)
            {
                if (resolution.State == LocalMediaResolutionState.Conflict)
                    throw new PrivateTagStoreException(PrivateTagStoreError.InvalidTarget);
                // Never write to an occurrence or revision other than the one the caller looked at.
                if (!asSeen) throw new PrivateTagStoreException(PrivateTagStoreError.StaleTarget);
                LocalMediaTagTarget target = resolution is { IsRegistered: true, Target: { } known } ? known : model.Adopt(local.Observation);
                PrivateLocalMediaTagAssignment assignment = new(tag, target);
                if (model.LocalSet.Contains(assignment)) continue;
                if (model.Local.Count >= MaximumLocalAssignments)
                    throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
                model.Local.Add(assignment);
                model.LocalSet.Add(assignment);
                changed = true;
            }
            else
            {
                // Remove what the caller saw, even when its file no longer resolves there (an orphan).
                LocalMediaTagTarget? target = local.Expected ?? (resolution.IsRegistered ? resolution.Target : null);
                if (target is not { } exact) continue;
                PrivateLocalMediaTagAssignment assignment = new(tag, exact);
                if (!model.LocalSet.Remove(assignment)) continue;
                model.Local.Remove(assignment);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Adds, for each handover, the media assignments of its earlier revision key to the new key, in one write.
    /// Only an assignment whose target names the same thread, post, position and bytes moves; the earlier
    /// assignments stay. Existing assignments are not duplicated, so repeating a handover changes nothing.
    /// </summary>
    internal PrivateTagCommit CarryMediaAssignmentsAdmitted(IReadOnlyCollection<MediaTagKeyHandover> handovers,
        ProfileAdmissionToken admission, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(handovers);
        return Mutate(admission, null, token, model =>
        {
            List<PrivateMediaTagAssignment> fresh = new();
            HashSet<PrivateMediaTagAssignment> seen = new();
            Dictionary<string, List<MediaTagKeyHandover>> byKey = new(StringComparer.Ordinal);
            foreach (MediaTagKeyHandover handover in handovers)
                (byKey.TryGetValue(handover.FromRevisionKey, out var list) ? list : byKey[handover.FromRevisionKey] = new()).Add(handover);
            foreach (PrivateMediaTagAssignment assignment in model.Media)
            {
                PrivateMediaTagTarget old = assignment.Target;
                if (!byKey.TryGetValue(old.Reference.RevisionKey, out List<MediaTagKeyHandover>? candidates)) continue;
                foreach (MediaTagKeyHandover handover in candidates)
                {
                    if (old.Reference.Resource != handover.To.Resource
                        || old.Reference.PostId != handover.To.PostId || old.Reference.Position != handover.To.Position
                        || old.Sha256 != handover.Sha256) continue;
                    if (!PrivateMediaTagTarget.TryCreate(handover.To, old.MediaId, old.Variant, old.Role, old.Sha256,
                        out PrivateMediaTagTarget? moved, out _)) continue;
                    PrivateMediaTagAssignment next = new(assignment.TagId, moved!);
                    if (!model.MediaSet.Contains(next) && seen.Add(next)) fresh.Add(next);
                }
            }
            if (fresh.Count == 0) return (false, default);
            if (model.Media.Count + fresh.Count > MaximumMediaAssignments)
                throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            model.Media.AddRange(fresh);
            return (true, default);
        });
    }

    /// <summary>
    /// Deletes one tag together with every thread and media assignment that uses it, in one write.
    /// An unknown tag is rejected rather than treated as already deleted.
    /// </summary>
    internal PrivateTagCommit Delete(TagId tag, long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return DeleteAdmitted(tag, admission, expectedRevision, token);
    }

    internal PrivateTagCommit DeleteAdmitted(TagId tag, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
        => Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            // The vocabulary type has no delete; rebuild it from the surviving definitions in order.
            if (!PrivateTagCatalog.TryCreate(model.Catalog.Definitions.Where(item => item.Id != tag),
                out PrivateTagCatalog? rebuilt, out _))
                throw new InvalidDataException(Invalid);
            model.Catalog = rebuilt;
            model.Threads.RemoveAll(item => (TagId)item.TagId == tag);
            model.Media.RemoveAll(item => (TagId)item.TagId == tag);
            model.Local.RemoveAll(item => item.TagId == tag);
            model.LocalSet.RemoveWhere(item => item.TagId == tag);
            // Its other names go with it; a later tag of such a name is a new tag.
            model.Aliases.RemoveAll(item => item.Tag == tag);
            return (true, tag);
        });

    /// <summary>
    /// Merges <paramref name="merged"/> into <paramref name="target"/> in ONE write: every thread, bound media and local media
    /// assignment of a merged tag moves to the target (an item that carried both keeps one assignment), the merged tags leave the
    /// vocabulary, their names become aliases of the target (with their IDs as <see cref="PrivateTagAlias.Former"/>), and aliases
    /// they had move to the target. All of it or nothing: a rejected or failed write leaves the document exactly as it was.
    /// The result carries an undo that restores the content before the merge while nothing else was written since.
    /// </summary>
    internal PrivateTagMergeCommit Merge(TagId target, IReadOnlyCollection<TagId> merged, long? expectedRevision = null,
        CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return MergeAdmitted(target, merged, admission, expectedRevision, token);
    }

    internal PrivateTagMergeCommit MergeAdmitted(TagId target, IReadOnlyCollection<TagId> merged, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(merged);
        TagId[] sources = merged.Distinct().ToArray();
        if (sources.Length == 0 || sources.Contains(target)) throw new PrivateTagStoreException(PrivateTagStoreError.InvalidMerge);
        byte[]? prior = null;
        PrivateTagCommit commit = Mutate(admission, expectedRevision, token, (model, bytes) =>
        {
            RequireKnown(model, target);
            foreach (TagId source in sources) RequireKnown(model, source);
            prior = bytes;
            ApplyMerge(model, target, sources.ToHashSet(), token);
            return (true, target);
        });
        return new PrivateTagMergeCommit(commit, new PrivateTagMergeUndo(commit.Snapshot.Revision, target, sources, prior!));
    }

    private static void ApplyMerge(Model model, TagId target, HashSet<TagId> sources, CancellationToken token)
    {
        PrivateThreadTagId threadTarget = new(target.Value);
        PrivateMediaTagId mediaTarget = new(target.Value);
        List<PrivateThreadTagAssignment> threads = new(model.Threads.Count);
        HashSet<PrivateThreadTagAssignment> threadSet = new();
        foreach (PrivateThreadTagAssignment item in model.Threads)
        {
            PrivateThreadTagAssignment next = sources.Contains((TagId)item.TagId) ? item with { TagId = threadTarget } : item;
            if (threadSet.Add(next)) threads.Add(next);
        }
        token.ThrowIfCancellationRequested();
        List<PrivateMediaTagAssignment> media = new(model.Media.Count);
        HashSet<PrivateMediaTagAssignment> mediaSet = new();
        foreach (PrivateMediaTagAssignment item in model.Media)
        {
            PrivateMediaTagAssignment next = sources.Contains((TagId)item.TagId) ? item with { TagId = mediaTarget } : item;
            if (mediaSet.Add(next)) media.Add(next);
        }
        token.ThrowIfCancellationRequested();
        List<PrivateLocalMediaTagAssignment> local = new(model.Local.Count);
        HashSet<PrivateLocalMediaTagAssignment> localSet = new();
        foreach (PrivateLocalMediaTagAssignment item in model.Local)
        {
            PrivateLocalMediaTagAssignment next = sources.Contains(item.TagId) ? item with { TagId = target } : item;
            if (localSet.Add(next)) local.Add(next);
        }
        token.ThrowIfCancellationRequested();
        List<PrivateTagAlias> aliases = model.Aliases.Select(item => sources.Contains(item.Tag) ? item with { Tag = target } : item).ToList();
        foreach (PrivateTagDefinition source in model.Catalog.Definitions.Where(item => sources.Contains(item.Id)))
            aliases.Add(new PrivateTagAlias(source.Name, target, source.Id));
        if (aliases.Count > MaximumAliases) throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
        if (!PrivateTagCatalog.TryCreate(model.Catalog.Definitions.Where(item => !sources.Contains(item.Id)),
            out PrivateTagCatalog? rebuilt, out _))
            throw new InvalidDataException(Invalid);
        model.Catalog = rebuilt;
        model.Threads.Clear(); model.Threads.AddRange(threads);
        model.ThreadSet.Clear(); model.ThreadSet.UnionWith(threadSet);
        model.Media.Clear(); model.Media.AddRange(media);
        model.MediaSet.Clear(); model.MediaSet.UnionWith(mediaSet);
        model.Local.Clear(); model.Local.AddRange(local);
        model.LocalSet.Clear(); model.LocalSet.UnionWith(localSet);
        model.Aliases.Clear(); model.Aliases.AddRange(aliases);
    }

    /// <summary>
    /// Takes one merge back in ONE write: the tags, assignments and aliases are exactly those before the merge (the merged tags
    /// return with their IDs, names and assignments; the aliases the merge made are gone). Only while the store is still at the
    /// revision the merge wrote; after any other write it is refused as <see cref="PrivateTagStoreError.StaleRevision"/>. The
    /// revision still counts up and the document version stays (never downgraded).
    /// </summary>
    internal PrivateTagCommit UndoMerge(PrivateTagMergeUndo undo, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return UndoMergeAdmitted(undo, admission, token);
    }

    internal PrivateTagCommit UndoMergeAdmitted(PrivateTagMergeUndo undo, ProfileAdmissionToken admission, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(undo);
        Model prior;
        try { prior = Parse(undo.Prior.Length == 0 ? Serialize(new Model()) : undo.Prior); }
        catch (InvalidDataException) { throw new PrivateTagStoreException(PrivateTagStoreError.InvalidMerge); }
        return Mutate(admission, undo.Revision, token, model =>
        {
            model.ReplaceContent(prior);
            return (true, undo.Target);
        });
    }

    /// <summary>
    /// Gives <paramref name="tag"/> another name. Refused as <see cref="PrivateTagStoreError.NameCollision"/> when a tag has that
    /// name or it is an alias of another tag; an alias the tag already has changes nothing.
    /// </summary>
    internal PrivateTagCommit AddAlias(TagId tag, string? name, long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return AddAliasAdmitted(tag, name, admission, expectedRevision, token);
    }

    internal PrivateTagCommit AddAliasAdmitted(TagId tag, string? name, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
    {
        PrivateTagName parsed = RequireName(name);
        return Mutate(admission, expectedRevision, token, model =>
        {
            RequireKnown(model, tag);
            if (model.Catalog.Find(parsed) != null) throw new PrivateTagStoreException(PrivateTagStoreError.NameCollision);
            if (model.Aliases.FirstOrDefault(item => item.Name.Key == parsed.Key) is { } known)
                return known.Tag == tag ? (false, tag) : throw new PrivateTagStoreException(PrivateTagStoreError.NameCollision);
            if (model.Aliases.Count >= MaximumAliases) throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            model.Aliases.Add(new PrivateTagAlias(parsed, tag));
            return (true, tag);
        });
    }

    /// <summary>
    /// Removes the alias with this name (whatever tag it names). An absent alias changes nothing. Removing a merged-away name also
    /// forgets its former ID: a saved filter that still names it then reports an unknown tag.
    /// </summary>
    internal PrivateTagCommit RemoveAlias(string? name, long? expectedRevision = null, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return RemoveAliasAdmitted(name, admission, expectedRevision, token);
    }

    internal PrivateTagCommit RemoveAliasAdmitted(string? name, ProfileAdmissionToken admission,
        long? expectedRevision = null, CancellationToken token = default)
    {
        PrivateTagName parsed = RequireName(name);
        return Mutate(admission, expectedRevision, token, model =>
        {
            int index = model.Aliases.FindIndex(item => item.Name.Key == parsed.Key);
            if (index < 0) return (false, default);
            TagId tag = model.Aliases[index].Tag;
            model.Aliases.RemoveAt(index);
            return (true, tag);
        });
    }

    /// <summary>
    /// Drops every thread assignment and every media assignment that belongs to the given threads, including the local
    /// occurrences those threads own (registration and assignments: a later re-archive is an independent occurrence).
    /// Tags themselves stay in the vocabulary. Used after a thread was permanently deleted.
    /// The returned commit carries no tag.
    /// </summary>
    internal PrivateTagCommit RemoveThreads(IReadOnlyCollection<ResourceId> threads, CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return RemoveThreadsAdmitted(threads, admission, token);
    }

    internal PrivateTagCommit RemoveThreadsAdmitted(IReadOnlyCollection<ResourceId> threads,
        ProfileAdmissionToken admission, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(threads);
        HashSet<ResourceId> selected = threads.Where(thread => thread is not null).ToHashSet();
        return Mutate(admission, null, token, model =>
        {
            int removed = model.Threads.RemoveAll(item => selected.Contains(item.Thread))
                + model.Media.RemoveAll(item => selected.Contains(item.Target.Reference.Resource))
                + model.RemoveOccurrences(occurrence => selected.Contains(occurrence.Locator.Thread));
            return (removed > 0, default);
        });
    }

    private PrivateTagCommit Mutate(ProfileAdmissionToken admission, long? expectedRevision, CancellationToken token,
        Func<Model, (bool Changed, TagId Tag)> change)
        => Mutate(admission, expectedRevision, token, (model, _) => change(model));

    // The change also sees the exact bytes it starts from (a merge keeps them for its undo).
    private PrivateTagCommit Mutate(ProfileAdmissionToken admission, long? expectedRevision, CancellationToken token,
        Func<Model, byte[], (bool Changed, TagId Tag)> change)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return _lease.RunStoreWrite(admission, () =>
        {
            token.ThrowIfCancellationRequested();
            (Model model, byte[] bytes) = Read();
            if (expectedRevision is long expected && expected != model.Revision)
                throw new PrivateTagStoreException(PrivateTagStoreError.StaleRevision);
            (bool changed, TagId tag) = change(model, bytes);
            if (!changed) return new PrivateTagCommit(false, tag, model.Snapshot(bytes));
            if (model.Revision == long.MaxValue)
                throw new PrivateTagStoreException(PrivateTagStoreError.RevisionOverflow);
            WritePhaseForTests?.Invoke(PrivateTagWritePhase.BeforeBackupCheck);
            // A newer or damaged backup must never be rotated away by this writer.
            try { CheckBackup(); }
            catch (FileNotFoundException) { }
            model.Revision++;
            // The first committed local data upgrades the document to v2, the first alias to v3; a version never goes back
            // (never silently downgraded). Only a document that was read as v1 from an existing primary has v1 bytes worth keeping.
            int needed = model.Aliases.Count > 0 ? AliasVersion : model.HasLocalData ? LocalVersion : BoundOnlyVersion;
            bool upgradesV1 = needed > BoundOnlyVersion && model.Version == BoundOnlyVersion && bytes.Length > 0;
            model.Version = Math.Max(model.Version, needed);
            byte[] next = Serialize(model);
            // Only growth past the configured limit is refused, so removals still work after the limit was lowered.
            // The limit never exceeds the read ceiling, so every written document stays readable.
            if (next.Length > Limits.DocumentLimitBytes && next.Length > bytes.Length)
                throw new PrivateTagStoreException(PrivateTagStoreError.DocumentLimitReached);
            // The exact v1 bytes are secured before the replace; ".bak" alone would lose them at the next write.
            if (upgradesV1) KeepV1Backup(bytes, token);
            WritePhaseForTests?.Invoke(PrivateTagWritePhase.BeforeReplace);
            // One atomic replace publishes registrations together with the assignments that name them.
            AtomicJsonFile.WriteAllText(_path, StrictUtf8.GetString(next), backupCurrentFile: true, token);
            // The replaced primary, parsed in full above, is what ".bak" now holds for the next write's check.
            if (bytes.Length > 0) _validatedDigest = SHA256.HashData(bytes);
            WritePhaseForTests?.Invoke(PrivateTagWritePhase.AfterReplace);
            return new PrivateTagCommit(true, tag, model.Snapshot(next));
        });
    }

    /// <summary>
    /// Writes the exact v1 bytes as <see cref="V1BackupFileName"/> unless anything already occupies that name
    /// (an earlier upgrade, a crash after this step, or the user's own file): an existing entry is never touched.
    /// Temporary file, flush to disk, then a move that cannot overwrite.
    /// </summary>
    private void KeepV1Backup(byte[] v1Bytes, CancellationToken token)
    {
        string target = Path.Combine(_lease.ProfileDirectory, V1BackupFileName);
        if (Occupied(target)) return;
        string temporary = Path.Combine(_lease.ProfileDirectory, $".{V1BackupFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(v1Bytes);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (Occupied(target)) { }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // True for a file, directory or link (also a dangling one) at the path.
    private static bool Occupied(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget != null;

    private static PrivateTagName RequireName(string? name) =>
        PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _)
            ? parsed! : throw new PrivateTagStoreException(PrivateTagStoreError.InvalidName);

    private void RequireTagRoom(Model model)
    {
        if (model.Catalog.Definitions.Count >= Limits.TagLimit)
            throw new PrivateTagStoreException(PrivateTagStoreError.TagLimitReached);
    }

    private static void RequireKnown(Model model, TagId tag)
    {
        if (tag.Value == Guid.Empty || !model.Catalog.Contains(tag))
            throw new PrivateTagStoreException(PrivateTagStoreError.UnknownTag);
    }

    private static PrivateTagStoreError Map(PrivateTagCatalogError error) => error switch
    {
        PrivateTagCatalogError.InvalidTagId or PrivateTagCatalogError.UnknownTag => PrivateTagStoreError.UnknownTag,
        PrivateTagCatalogError.NameCollision or PrivateTagCatalogError.DefinitionConflict => PrivateTagStoreError.NameCollision,
        PrivateTagCatalogError.CapacityExceeded => PrivateTagStoreError.CapacityExceeded,
        PrivateTagCatalogError.RevisionOverflow => PrivateTagStoreError.RevisionOverflow,
        PrivateTagCatalogError.InvalidName => PrivateTagStoreError.InvalidName,
        _ => PrivateTagStoreError.StaleRevision
    };

    private static bool IsThread(ResourceId? thread)
    {
        if (thread is null) return false;
        try { ArchiveValidation.ValidateId(thread); }
        catch (ArgumentException) { return false; }
        return WellFormed(thread.Source) && WellFormed(thread.Board) && WellFormed(thread.Thread);
    }

    // Targets are created by their own syntax validator; persistence additionally needs
    // well-formed UTF-16 so the document can always be written and read back exactly.
    private static bool IsTarget(PrivateMediaTagTarget? target) => target is not null
        && IsThread(target.Reference.Resource) && WellFormed(target.Reference.PostId)
        && WellFormed(target.MediaId) && WellFormed(target.Variant);

    private static bool WellFormed(string? value)
    {
        if (value is null) return false;
        try { StrictUtf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private (Model Model, byte[] Bytes) Read()
    {
        try { return ReadDocument(_path); }
        catch (FileNotFoundException)
        {
            // Only an explicitly missing primary AND backup establish a new, empty index.
            try { ReadDocument(_path + ".bak"); }
            catch (FileNotFoundException) { return (new Model(), Array.Empty<byte>()); }
            throw new InvalidDataException("The private tag index is missing but a backup exists. Explicit recovery is required.");
        }
    }

    /// <summary>
    /// Reads the backup with the same file checks as a document and parses it in full unless its bytes are exactly
    /// the bytes this instance last validated with the full parser; either way a damaged or newer backup throws.
    /// </summary>
    private void CheckBackup()
    {
        string path = _path + ".bak";
        byte[] bytes = ReadDocumentBytes(path);
        byte[] digest = SHA256.HashData(bytes);
        if (_validatedDigest is { } known && digest.AsSpan().SequenceEqual(known)) return;
        ParseFile(path, bytes);
        _validatedDigest = digest;
    }

    private (Model Model, byte[] Bytes) ReadDocument(string path)
    {
        byte[] bytes = ReadDocumentBytes(path);
        return (ParseFile(path, bytes), bytes);
    }

    private Model ParseFile(string path, byte[] bytes)
    {
        DocumentParsedForTests?.Invoke(Path.GetFileName(path));
        return Parse(bytes);
    }

    private static byte[] ReadDocumentBytes(string path)
    {
        try { return ReadDocumentCore(path); }
        // Paths and parser messages can expose private profile values; never attach raw exceptions.
        catch (FileNotFoundException) { throw new FileNotFoundException("The private tag index file is missing."); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("The private tag index could not be read."); }
        catch (InvalidDataException) { throw; }
        catch (IOException) { throw new IOException("The private tag index could not be read."); }
    }

    private static byte[] ReadDocumentCore(string path)
    {
        using FileStream stream = RegularProfileFile.OpenRead(path);
        long length = stream.Length;
        if (length > MaximumBytes) throw new InvalidDataException("The private tag index exceeds its size limit.");
        byte[] bytes = new byte[checked((int)length)];
        try { stream.ReadExactly(bytes); }
        catch (EndOfStreamException) { throw new InvalidDataException("The private tag index changed while reading."); }
        if (stream.ReadByte() != -1 || stream.Length != length)
            throw new InvalidDataException("The private tag index changed while reading.");
        return bytes;
    }

    private static Model Parse(byte[] bytes)
    {
        try
        {
            string json = StrictUtf8.GetString(bytes);
            if (json.Length > 0 && json[0] == '﻿') json = json[1..];
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException(Invalid);
            // Unknown and newer versions are rejected; the caller then never overwrites the file.
            int version = root.GetProperty("Version").GetInt32();
            CheckObject(root, version switch
            {
                BoundOnlyVersion => ["Version", "Revision", "Tags", "Threads", "Media"],
                LocalVersion => ["Version", "Revision", "Tags", "Threads", "Media", "LocalOccurrences", "LocalMedia"],
                AliasVersion => ["Version", "Revision", "Tags", "Threads", "Media", "LocalOccurrences", "LocalMedia", "Aliases"],
                _ => throw new InvalidDataException(Invalid)
            });
            Model model = new() { Version = version, Revision = root.GetProperty("Revision").GetInt64() };
            if (model.Revision < 0) throw new InvalidDataException(Invalid);

            List<PrivateTagDefinition> tags = new();
            HashSet<TagId> ids = new();
            foreach (JsonElement item in Items(root.GetProperty("Tags"), MaximumTags))
            {
                CheckObject(item, ["Id", "Name"]);
                TagId id = ReadTagId(item.GetProperty("Id"));
                string text = Text(item.GetProperty("Name"));
                // Stored names are canonical: they must round-trip through the name rule unchanged.
                if (!PrivateTagName.TryCreate(text, out PrivateTagName? name, out _)
                    || !string.Equals(name!.Display, text, StringComparison.Ordinal)
                    || !ids.Add(id))
                    throw new InvalidDataException(Invalid);
                tags.Add(new(id, name));
            }
            // One linear build with the catalog's own rules (name collisions, capacity) instead of a copy per tag.
            if (!PrivateTagCatalog.TryCreate(tags, out PrivateTagCatalog? catalog, out _)) throw new InvalidDataException(Invalid);
            model.Catalog = catalog;

            foreach (JsonElement item in Items(root.GetProperty("Threads"), MaximumThreadAssignments))
            {
                CheckObject(item, ["Tag", "Source", "Board", "Thread"]);
                TagId tag = ReadTagId(item.GetProperty("Tag"));
                ResourceId thread = ReadThread(item);
                var assignment = new PrivateThreadTagAssignment(tag, thread);
                if (!model.Catalog.Contains(tag) || !IsThread(thread) || !model.ThreadSet.Add(assignment))
                    throw new InvalidDataException(Invalid);
                model.Threads.Add(assignment);
            }

            foreach (JsonElement item in Items(root.GetProperty("Media"), MaximumMediaAssignments))
            {
                CheckObject(item, ["Tag", "Source", "Board", "Thread", "Post", "Position", "RevisionKey",
                    "MediaId", "Variant", "Role", "Sha256"]);
                TagId tag = ReadTagId(item.GetProperty("Tag"));
                TransferMediaRole role = Text(item.GetProperty("Role")) switch
                {
                    "original" => TransferMediaRole.Original,
                    "preview" => TransferMediaRole.Preview,
                    _ => throw new InvalidDataException(Invalid)
                };
                var reference = new AttachmentRevisionReference(ReadThread(item), Text(item.GetProperty("Post")),
                    item.GetProperty("Position").GetInt32(), Text(item.GetProperty("RevisionKey")));
                if (!PrivateMediaTagTarget.TryCreate(reference, Text(item.GetProperty("MediaId")),
                    Text(item.GetProperty("Variant")), role, Text(item.GetProperty("Sha256")),
                    out PrivateMediaTagTarget? target, out _) || !IsTarget(target))
                    throw new InvalidDataException(Invalid);
                var assignment = new PrivateMediaTagAssignment(tag, target!);
                if (!model.Catalog.Contains(tag) || !model.MediaSet.Add(assignment))
                    throw new InvalidDataException(Invalid);
                model.Media.Add(assignment);
            }
            if (version >= LocalVersion) ParseLocal(root, model);
            if (version >= AliasVersion) ParseAliases(root, model);
            return model;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw new InvalidDataException(Invalid); }
    }

    private static void ParseLocal(JsonElement root, Model model)
    {
        foreach (JsonElement item in Items(root.GetProperty("LocalOccurrences"), MaximumLocalOccurrences))
        {
            CheckObject(item, ["Id", "Source", "Board", "Thread", "Directory", "RequestedFile", "Revisions"]);
            Guid id = ReadGuid(item.GetProperty("Id"));
            ResourceId thread = ReadThread(item);
            string directory = Text(item.GetProperty("Directory")), file = Text(item.GetProperty("RequestedFile"));
            if (!model.LocalIds.Add(id) || !IsThread(thread)
                || !LocalMediaObservation.ValidText(directory, LocalMediaObservation.MaximumDirectoryLength)
                || !LocalMediaObservation.ValidText(file, LocalMediaObservation.MaximumFileNameLength))
                throw new InvalidDataException(Invalid);
            JsonElement revisions = item.GetProperty("Revisions");
            if (revisions.ValueKind != JsonValueKind.Array || revisions.GetArrayLength() == 0) throw new InvalidDataException(Invalid);
            List<LocalMediaRevision> parsed = new();
            HashSet<string> assets = new(StringComparer.Ordinal);
            foreach (JsonElement revision in Items(revisions, MaximumLocalRevisionsPerOccurrence))
            {
                CheckObject(revision, ["Id", "AssetId", "Length", "Sha256"]);
                Guid revisionId = ReadGuid(revision.GetProperty("Id"));
                string asset = Text(revision.GetProperty("AssetId")), sha = Text(revision.GetProperty("Sha256"));
                long length = revision.GetProperty("Length").GetInt64();
                if (!model.LocalIds.Add(revisionId) || !LocalMediaObservation.CanonicalHex(asset) || !assets.Add(asset)
                    || length < 0 || !LocalMediaObservation.CanonicalHex(sha))
                    throw new InvalidDataException(Invalid);
                parsed.Add(new LocalMediaRevision(new LocalMediaRevisionId(revisionId), asset, length, sha));
            }
            LocalMediaLocator locator = new(thread, directory, file);
            // One occurrence per locator; equal bytes or asset IDs at other locators are other occurrences.
            if (model.OccurrenceByLocator.ContainsKey(locator) || model.LocalRevisionCount + parsed.Count > MaximumLocalRevisions)
                throw new InvalidDataException(Invalid);
            model.AddOccurrence(new LocalMediaOccurrence(new LocalMediaOccurrenceId(id), locator, parsed));
        }
        foreach (JsonElement item in Items(root.GetProperty("LocalMedia"), MaximumLocalAssignments))
        {
            CheckObject(item, ["Tag", "Occurrence", "Revision"]);
            TagId tag = ReadTagId(item.GetProperty("Tag"));
            LocalMediaTagTarget target = new(new LocalMediaOccurrenceId(ReadGuid(item.GetProperty("Occurrence"))),
                new LocalMediaRevisionId(ReadGuid(item.GetProperty("Revision"))));
            PrivateLocalMediaTagAssignment assignment = new(tag, target);
            // Every published assignment names a committed registration of the same document.
            if (!model.Catalog.Contains(tag) || !model.Registered(target) || !model.LocalSet.Add(assignment))
                throw new InvalidDataException(Invalid);
            model.Local.Add(assignment);
        }
    }

    private static void ParseAliases(JsonElement root, Model model)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        HashSet<TagId> formers = new();
        foreach (JsonElement item in Items(root.GetProperty("Aliases"), MaximumAliases))
        {
            CheckObject(item, ["Name", "Tag", "Former"]);
            string text = Text(item.GetProperty("Name"));
            TagId tag = ReadTagId(item.GetProperty("Tag"));
            JsonElement former = item.GetProperty("Former");
            TagId? formerId = former.ValueKind == JsonValueKind.Null ? null : ReadTagId(former);
            // Canonical name, a known tag, no tag already carries the name, one alias per name, a former ID that no tag
            // has any more and that only one alias records.
            if (!PrivateTagName.TryCreate(text, out PrivateTagName? name, out _)
                || !string.Equals(name!.Display, text, StringComparison.Ordinal)
                || !model.Catalog.Contains(tag) || model.Catalog.Find(name) != null || !keys.Add(name.Key)
                || formerId is { } id && (model.Catalog.Contains(id) || !formers.Add(id)))
                throw new InvalidDataException(Invalid);
            model.Aliases.Add(new PrivateTagAlias(name, tag, formerId));
        }
    }

    /// <summary>
    /// Validates complete document bytes with the canonical parser (v1, v2 or v3), for protection inventories that must
    /// not accept what the store itself would reject. Throws <see cref="InvalidDataException"/>; returns the version.
    /// </summary>
    internal static int ValidateDocumentBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Parse(bytes).Version;
    }

    /// <summary>The owning archive threads of every local assignment (cleanup protection), each once.</summary>
    internal static IReadOnlyList<ResourceId> LocalAssignmentOwners(PrivateTagStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.LocalAssignments.Select(item => snapshot.LocalRegistry.Find(item.Target.Occurrence)?.Locator.Thread)
            .OfType<ResourceId>().Distinct().ToArray();
    }

    private static IEnumerable<JsonElement> Items(JsonElement array, int maximum)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
            throw new InvalidDataException(Invalid);
        return array.EnumerateArray();
    }

    private static TagId ReadTagId(JsonElement value) => new(ReadGuid(value));

    private static Guid ReadGuid(JsonElement value)
    {
        string text = Text(value);
        // Exactly the lowercase hyphenated form this store writes; no alternative spellings.
        if (!Guid.TryParseExact(text, "D", out Guid id) || id == Guid.Empty
            || !string.Equals(id.ToString("D"), text, StringComparison.Ordinal))
            throw new InvalidDataException(Invalid);
        return id;
    }

    private static ResourceId ReadThread(JsonElement item) => new(Text(item.GetProperty("Source")),
        Text(item.GetProperty("Board")), Text(item.GetProperty("Thread")));

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is string text
            ? text : throw new InvalidDataException(Invalid);

    private static void CheckObject(JsonElement value, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException(Invalid);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name) || !required.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException(Invalid);
        if (names.Count != required.Length) throw new InvalidDataException(Invalid);
    }

    private static byte[] Serialize(Model model)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", model.Version);
            writer.WriteNumber("Revision", model.Revision);
            writer.WriteStartArray("Tags");
            foreach (PrivateTagDefinition tag in model.Catalog.Definitions)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", tag.Id.Value.ToString("D"));
                writer.WriteString("Name", tag.Name.Display);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Threads");
            foreach (PrivateThreadTagAssignment item in model.Threads)
            {
                writer.WriteStartObject();
                writer.WriteString("Tag", item.TagId.Value.ToString("D"));
                WriteThread(writer, item.Thread);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Media");
            foreach (PrivateMediaTagAssignment item in model.Media)
            {
                PrivateMediaTagTarget target = item.Target;
                writer.WriteStartObject();
                writer.WriteString("Tag", item.TagId.Value.ToString("D"));
                WriteThread(writer, target.Reference.Resource);
                writer.WriteString("Post", target.Reference.PostId);
                writer.WriteNumber("Position", target.Reference.Position);
                writer.WriteString("RevisionKey", target.Reference.RevisionKey);
                writer.WriteString("MediaId", target.MediaId);
                writer.WriteString("Variant", target.Variant);
                writer.WriteString("Role", target.Role == TransferMediaRole.Original ? "original" : "preview");
                writer.WriteString("Sha256", target.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (model.Version >= LocalVersion)
            {
                writer.WriteStartArray("LocalOccurrences");
                foreach (LocalMediaOccurrence occurrence in model.Occurrences)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Id", occurrence.Id.Value.ToString("D"));
                    WriteThread(writer, occurrence.Locator.Thread);
                    writer.WriteString("Directory", occurrence.Locator.Directory);
                    writer.WriteString("RequestedFile", occurrence.Locator.RequestedFileName);
                    writer.WriteStartArray("Revisions");
                    foreach (LocalMediaRevision revision in occurrence.Revisions)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("Id", revision.Id.Value.ToString("D"));
                        writer.WriteString("AssetId", revision.AssetId);
                        writer.WriteNumber("Length", revision.Length);
                        writer.WriteString("Sha256", revision.Sha256);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("LocalMedia");
                foreach (PrivateLocalMediaTagAssignment item in model.Local)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Tag", item.TagId.Value.ToString("D"));
                    writer.WriteString("Occurrence", item.Target.Occurrence.Value.ToString("D"));
                    writer.WriteString("Revision", item.Target.Revision.Value.ToString("D"));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (model.Version >= AliasVersion)
            {
                writer.WriteStartArray("Aliases");
                foreach (PrivateTagAlias alias in model.Aliases)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Name", alias.Name.Display);
                    writer.WriteString("Tag", alias.Tag.Value.ToString("D"));
                    if (alias.Former is { } former) writer.WriteString("Former", former.Value.ToString("D"));
                    else writer.WriteNull("Former");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteThread(Utf8JsonWriter writer, ResourceId thread)
    {
        writer.WriteString("Source", thread.Source);
        writer.WriteString("Board", thread.Board);
        writer.WriteString("Thread", thread.Thread);
    }
}
