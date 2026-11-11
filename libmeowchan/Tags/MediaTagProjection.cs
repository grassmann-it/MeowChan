using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tags;

/// <summary>
/// Exact in-process identity of a keyed attachment occurrence and its recorded bytes.
/// Uses the existing AttachmentRevisionReference, without manufacturing revision keys from hashes.
/// This bounded slice deliberately excludes legacy null revisions and unassigned assets;
/// it is not a complete GP-31 target resolver. A caller must separately resolve the occurrence and
/// verify its bytes before authoritative use. Syntax validation here is NOT that verification.
/// Paths, manifest-local asset IDs, URLs, current/retained flags and acceptance grants are not identity.
/// </summary>
internal sealed record PrivateMediaTagTarget
{
    private PrivateMediaTagTarget(AttachmentRevisionReference reference, string mediaId, string variant,
        TransferMediaRole role, string sha256)
    {
        Reference = reference;
        MediaId = mediaId;
        Variant = variant;
        Role = role;
        Sha256 = sha256;
    }

    internal AttachmentRevisionReference Reference { get; }

    /// <summary>
    /// The attachment and bytes this target names, without the revision key: thread, post, position, media id,
    /// variant, role and SHA-256. The same file under an earlier and a later revision key (after a source changed what
    /// it reports) has one identity; a different file, or the same bytes in another post or position, has another.
    /// Counting and removing use it; storage and lookup stay on the exact target.
    /// </summary>
    internal MediaIdentity Identity => new(Reference.Resource, Reference.PostId, Reference.Position, MediaId, Variant, Role, Sha256);
    internal string MediaId { get; }
    // Role and variant are both retained explicitly; neither is inferred from a filename or hash.
    internal string Variant { get; }
    internal TransferMediaRole Role { get; }
    internal string Sha256 { get; }

    internal static bool TryCreate(AttachmentRevisionReference? reference, string? mediaId, string? variant,
        TransferMediaRole role, string? sha256, out PrivateMediaTagTarget? target, out MediaTagError error)
    {
        target = null;
        error = MediaTagError.InvalidTarget;
        if (reference is null) return false;
        try { ArchiveValidation.ValidateId(reference.Resource); }
        catch (ArgumentException) { return false; }
        if (!Identifier(reference.PostId) || reference.Position < 0 || !Identifier(mediaId)
            || !Identifier(variant) || !Enum.IsDefined(role) || !CanonicalSha256(sha256)
            || !AttachmentKey(reference.RevisionKey)) return false;
        target = new(reference, mediaId!, variant!, role, sha256!);
        error = MediaTagError.None;
        return true;
    }

    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256 && !value.Any(char.IsControl);

    private static bool CanonicalSha256(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool AttachmentKey(string? value) => value is { Length: 71 }
        && (value.StartsWith("att-v1:", StringComparison.Ordinal) || value.StartsWith("att-v2:", StringComparison.Ordinal))
        && CanonicalSha256(value[7..]);
}

/// <summary>One file of one attachment, independent of which revision key currently names it.</summary>
internal readonly record struct MediaIdentity(ResourceId Thread, string PostId, int Position, string MediaId,
    string Variant, TransferMediaRole Role, string Sha256);

/// <summary>Caller-supplied local identity. Not a portable tag reference or a profile authority.</summary>
// Compatibility adapter only: no category-local vocabulary is stored.
internal readonly record struct PrivateMediaTagId(Guid Value)
{
    public static implicit operator TagId(PrivateMediaTagId id) => new(id.Value);
    public static implicit operator PrivateMediaTagId(TagId id) => new(id.Value);
}

internal sealed record PrivateMediaTagDefinition(PrivateMediaTagId Id, PrivateTagName Name);
internal sealed record PrivateMediaTagAssignment(PrivateMediaTagId TagId, PrivateMediaTagTarget Target);

/// <summary>Constant diagnostics; no rejected names or resource identities are echoed.</summary>
internal enum MediaTagError
{
    None,
    StaleRevision,
    InvalidTagId,
    InvalidName,
    UnknownTag,
    DefinitionConflict,
    NameCollision,
    InvalidTarget,
    InvalidScope,
    CapacityExceeded,
    RevisionOverflow
}

/// <summary>
/// Passive, selected-only copy. Empty selected and unselected are deliberately different.
/// Local IDs remain in-process; this is neither a wire format nor consent to export/apply.
/// </summary>
internal sealed class MediaTagSnapshot
{
    internal MediaTagSnapshot(bool selected, long revision, IEnumerable<PrivateMediaTagTarget> scope,
        IEnumerable<PrivateMediaTagDefinition> definitions, IEnumerable<PrivateMediaTagAssignment> assignments)
    {
        IsSelected = selected;
        Revision = revision;
        Scope = Array.AsReadOnly(scope.ToArray());
        Definitions = Array.AsReadOnly(definitions.ToArray());
        Assignments = Array.AsReadOnly(assignments.ToArray());
    }

    internal bool IsSelected { get; }
    internal long Revision { get; }
    internal IReadOnlyList<PrivateMediaTagTarget> Scope { get; }
    internal IReadOnlyList<PrivateMediaTagDefinition> Definitions { get; }
    internal IReadOnlyList<PrivateMediaTagAssignment> Assignments { get; }
}

/// <summary>
/// Pure immutable media-private-tag reducer for keyed occurrences only. It knows supplied
/// identities, not archive existence, byte freshness or provider authority. No store, admission token,
/// source tags, favorite semantics, import, or deletion of definitions. Admission order is stable;
/// rename retains the caller-supplied local ID. Definitions belong to the shared catalog; compatibility IDs are adapters only.
/// </summary>
internal sealed class MediaTagProjection
{
    // Defensive in-memory work caps, not product quotas or approved wire/storage limits.
    internal const int MaximumDefinitions = PrivateTagSet.MaximumCount;
    internal const int MaximumAssignments = 50_000;
    internal const int MaximumScopeCount = 50_000;

    internal PrivateTagCatalog Catalog { get; }
    private PrivateMediaTagDefinition[] definitions => Catalog.Definitions
        .Select(item => new PrivateMediaTagDefinition(new(item.Id.Value), item.Name)).ToArray();
    private readonly PrivateMediaTagAssignment[] assignments;

    /// <summary>An explicit in-memory revision seed, not a persisted version or freshness proof.</summary>
    internal MediaTagProjection(long initialRevision = 0)
        : this(initialRevision, new PrivateTagCatalog(), Array.Empty<PrivateMediaTagAssignment>())
    {
        if (initialRevision < 0) throw new ArgumentOutOfRangeException(nameof(initialRevision));
    }

    // Arrays are fresh implementation-owned arrays or shared immutable arrays from a prior state.
    private MediaTagProjection(long revision, PrivateTagCatalog catalog,
        PrivateMediaTagAssignment[] assignments)
    {
        Revision = revision;
        Catalog = catalog;
        this.assignments = assignments;
    }

    internal long Revision { get; }

    /// <summary>Bind both typed projections to the same catalog instance at their composition boundary.</summary>
    internal MediaTagProjection(PrivateTagCatalog catalog) : this(0,
        catalog ?? throw new ArgumentNullException(nameof(catalog)), Array.Empty<PrivateMediaTagAssignment>()) { }

    /// <summary>Explicitly consume a shared rename/create revision; never adopt a divergent vocabulary.</summary>
    internal bool TryUseCatalog(PrivateTagCatalog catalog, long expectedRevision,
        long expectedCatalogRevision, out MediaTagProjection result, out MediaTagError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error)) return false;
        if (Catalog.Revision != expectedCatalogRevision)
            return Fail(MediaTagError.StaleRevision, out error);
        if (catalog is null || !catalog.DescendsFrom(Catalog))
            return Fail(MediaTagError.DefinitionConflict, out error);
        if (ReferenceEquals(catalog, Catalog)) return true;
        if (!CanChange(out error)) return false;
        result = new(Revision + 1, catalog, assignments);
        return true;
    }

    // Legacy entry points delegate every vocabulary decision to the common core.
    internal bool TryDefine(PrivateMediaTagId id, PrivateTagName? name, long expectedRevision,
        out MediaTagProjection result, out MediaTagError error)
        => TryDefinition(id, name, expectedRevision, false, out result, out error);

    internal bool TryRename(PrivateMediaTagId id, PrivateTagName? name, long expectedRevision,
        out MediaTagProjection result, out MediaTagError error)
        => TryDefinition(id, name, expectedRevision, true, out result, out error);

    private bool TryDefinition(TagId id, PrivateTagName? name, long expectedRevision, bool rename,
        out MediaTagProjection result, out MediaTagError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error)) return false;
        PrivateTagCatalog next;
        PrivateTagCatalogError catalogError;
        bool success = rename
            ? Catalog.TryRename(id, name, Catalog.Revision, out next, out catalogError)
            : Catalog.TryDefine(id, name, Catalog.Revision, out next, out catalogError);
        if (!success) return Fail(Enum.Parse<MediaTagError>(catalogError.ToString()), out error);
        if (ReferenceEquals(next, Catalog)) return true;
        if (!CanChange(out error)) return false;
        result = new(Revision + 1, next, assignments);
        return true;
    }

    /// <summary>Explicit local membership add; existing membership is a successful no-op.</summary>
    internal bool TryAdd(PrivateMediaTagId id, PrivateMediaTagTarget? target, long expectedRevision,
        out MediaTagProjection result, out MediaTagError error)
        => TryMembership(id, target, expectedRevision, true, out result, out error);

    /// <summary>Explicit local unassignment only; a missing assignment is a successful no-op.</summary>
    internal bool TryRemove(PrivateMediaTagId id, PrivateMediaTagTarget? target, long expectedRevision,
        out MediaTagProjection result, out MediaTagError error)
        => TryMembership(id, target, expectedRevision, false, out result, out error);

    private bool TryMembership(PrivateMediaTagId id, PrivateMediaTagTarget? target, long expectedRevision, bool add,
        out MediaTagProjection result, out MediaTagError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error) || !CheckKnownId(id, out error)) return false;
        if (target is null) return Fail(MediaTagError.InvalidTarget, out error);
        var assignment = new PrivateMediaTagAssignment(id, target!);
        bool exists = assignments.Contains(assignment);
        if (exists == add) return true;
        if (add && assignments.Length >= MaximumAssignments) return Fail(MediaTagError.CapacityExceeded, out error);
        if (!CanChange(out error)) return false;
        result = new(Revision + 1, Catalog, add
            ? assignments.Append(assignment).ToArray()
            : assignments.Where(item => item != assignment).ToArray());
        return true;
    }

    /// <summary>
    /// Default-off. Unselected does not inspect scope or disclose definitions/assignments.
    /// Selected copies only exact scoped occurrences/versions and definitions used by their assignments.
    /// There is no thread inheritance, hash-only fan-out, source refresh, or automatic version retargeting.
    /// Scope membership does not assert archive existence or grant export/apply authority.
    /// </summary>
    internal bool TrySelectMedia(IReadOnlyCollection<PrivateMediaTagTarget>? scope, out MediaTagSnapshot? snapshot,
        out MediaTagError error, bool selected = false)
    {
        snapshot = null;
        error = MediaTagError.None;
        if (!selected)
        {
            snapshot = new(false, Revision, Array.Empty<PrivateMediaTagTarget>(),
                Array.Empty<PrivateMediaTagDefinition>(), Array.Empty<PrivateMediaTagAssignment>());
            return true;
        }
        if (scope is null) return Fail(MediaTagError.InvalidScope, out error);
        if (scope.Count > MaximumScopeCount) return Fail(MediaTagError.CapacityExceeded, out error);
        var scoped = new HashSet<PrivateMediaTagTarget>();
        var ordered = new List<PrivateMediaTagTarget>();
        int inspected = 0;
        foreach (PrivateMediaTagTarget target in scope)
        {
            if (++inspected > MaximumScopeCount) return Fail(MediaTagError.CapacityExceeded, out error);
            if (target is null) return Fail(MediaTagError.InvalidTarget, out error);
            if (scoped.Add(target)) ordered.Add(target);
        }
        PrivateMediaTagAssignment[] chosen = assignments.Where(item => scoped.Contains(item.Target)).ToArray();
        var used = chosen.Select(item => item.TagId).ToHashSet();
        snapshot = new(true, Revision, ordered, definitions.Where(item => used.Contains(item.Id)), chosen);
        return true;
    }

    private bool CheckRevision(long expected, out MediaTagError error)
    {
        error = expected == Revision ? MediaTagError.None : MediaTagError.StaleRevision;
        return error == MediaTagError.None;
    }

    private static bool CheckId(PrivateMediaTagId id, out MediaTagError error)
    {
        error = id.Value != Guid.Empty ? MediaTagError.None : MediaTagError.InvalidTagId;
        return error == MediaTagError.None;
    }

    private bool CheckKnownId(PrivateMediaTagId id, out MediaTagError error)
    {
        if (!CheckId(id, out error)) return false;
        return Catalog.Contains(id) || Fail(MediaTagError.UnknownTag, out error);
    }

    private bool CanChange(out MediaTagError error)
    {
        error = Revision == long.MaxValue ? MediaTagError.RevisionOverflow : MediaTagError.None;
        return error == MediaTagError.None;
    }

    private static bool Fail(MediaTagError reason, out MediaTagError error)
    {
        error = reason;
        return false;
    }
}
