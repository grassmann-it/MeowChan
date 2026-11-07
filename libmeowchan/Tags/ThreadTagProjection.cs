using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Tags;

/// <summary>Caller-supplied local identity. Not a portable tag reference or a profile authority.</summary>
// Compatibility adapter only: no category-local vocabulary is stored.
internal readonly record struct PrivateThreadTagId(Guid Value)
{
    public static implicit operator TagId(PrivateThreadTagId id) => new(id.Value);
    public static implicit operator PrivateThreadTagId(TagId id) => new(id.Value);
}

internal sealed record PrivateThreadTagDefinition(PrivateThreadTagId Id, PrivateTagName Name);
internal sealed record PrivateThreadTagAssignment(PrivateThreadTagId TagId, ResourceId Thread);

/// <summary>Constant diagnostics; no rejected names or resource identities are echoed.</summary>
internal enum ThreadTagError
{
    None,
    StaleRevision,
    InvalidTagId,
    InvalidName,
    UnknownTag,
    DefinitionConflict,
    NameCollision,
    InvalidThread,
    InvalidScope,
    CapacityExceeded,
    RevisionOverflow
}

/// <summary>
/// Passive, selected-only copy. Empty selected and unselected are deliberately different.
/// Local IDs remain in-process; this is neither a wire format nor consent to export/apply.
/// </summary>
internal sealed class ThreadTagSnapshot
{
    internal ThreadTagSnapshot(bool selected, long revision, IEnumerable<ResourceId> scope,
        IEnumerable<PrivateThreadTagDefinition> definitions, IEnumerable<PrivateThreadTagAssignment> assignments)
    {
        IsSelected = selected;
        Revision = revision;
        Scope = Array.AsReadOnly(scope.ToArray());
        Definitions = Array.AsReadOnly(definitions.ToArray());
        Assignments = Array.AsReadOnly(assignments.ToArray());
    }

    internal bool IsSelected { get; }
    internal long Revision { get; }
    internal IReadOnlyList<ResourceId> Scope { get; }
    internal IReadOnlyList<PrivateThreadTagDefinition> Definitions { get; }
    internal IReadOnlyList<PrivateThreadTagAssignment> Assignments { get; }
}

/// <summary>
/// Pure immutable thread-private-tag reducer. It knows only supplied identities, not live thread
/// existence. No store, admission token, source tags, favorite semantics, import, or deletion of
/// definitions. Admission order is stable; rename retains the caller-supplied local ID.
/// </summary>
internal sealed class ThreadTagProjection
{
    // Defensive in-memory work caps, not product quotas or approved wire/storage limits.
    internal const int MaximumDefinitions = PrivateTagSet.MaximumCount;
    internal const int MaximumAssignments = 50_000;
    internal const int MaximumScopeCount = 50_000;

    internal PrivateTagCatalog Catalog { get; }
    private PrivateThreadTagDefinition[] definitions => Catalog.Definitions
        .Select(item => new PrivateThreadTagDefinition(new(item.Id.Value), item.Name)).ToArray();
    private readonly PrivateThreadTagAssignment[] assignments;

    /// <summary>An explicit in-memory revision seed, not a persisted version or freshness proof.</summary>
    internal ThreadTagProjection(long initialRevision = 0)
        : this(initialRevision, new PrivateTagCatalog(), Array.Empty<PrivateThreadTagAssignment>())
    {
        if (initialRevision < 0) throw new ArgumentOutOfRangeException(nameof(initialRevision));
    }

    // Arrays are fresh implementation-owned arrays or shared immutable arrays from a prior state.
    private ThreadTagProjection(long revision, PrivateTagCatalog catalog,
        PrivateThreadTagAssignment[] assignments)
    {
        Revision = revision;
        Catalog = catalog;
        this.assignments = assignments;
    }

    internal long Revision { get; }

    /// <summary>Bind both typed projections to the same catalog instance at their composition boundary.</summary>
    internal ThreadTagProjection(PrivateTagCatalog catalog) : this(0,
        catalog ?? throw new ArgumentNullException(nameof(catalog)), Array.Empty<PrivateThreadTagAssignment>()) { }

    /// <summary>Explicitly consume a shared rename/create revision; never adopt a divergent vocabulary.</summary>
    internal bool TryUseCatalog(PrivateTagCatalog catalog, long expectedRevision,
        long expectedCatalogRevision, out ThreadTagProjection result, out ThreadTagError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error)) return false;
        if (Catalog.Revision != expectedCatalogRevision)
            return Fail(ThreadTagError.StaleRevision, out error);
        if (catalog is null || !catalog.DescendsFrom(Catalog))
            return Fail(ThreadTagError.DefinitionConflict, out error);
        if (ReferenceEquals(catalog, Catalog)) return true;
        if (!CanChange(out error)) return false;
        result = new(Revision + 1, catalog, assignments);
        return true;
    }

    // Legacy entry points delegate every vocabulary decision to the common core.
    internal bool TryDefine(PrivateThreadTagId id, PrivateTagName? name, long expectedRevision,
        out ThreadTagProjection result, out ThreadTagError error)
        => TryDefinition(id, name, expectedRevision, false, out result, out error);

    internal bool TryRename(PrivateThreadTagId id, PrivateTagName? name, long expectedRevision,
        out ThreadTagProjection result, out ThreadTagError error)
        => TryDefinition(id, name, expectedRevision, true, out result, out error);

    private bool TryDefinition(TagId id, PrivateTagName? name, long expectedRevision, bool rename,
        out ThreadTagProjection result, out ThreadTagError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error)) return false;
        PrivateTagCatalog next;
        PrivateTagCatalogError catalogError;
        bool success = rename
            ? Catalog.TryRename(id, name, Catalog.Revision, out next, out catalogError)
            : Catalog.TryDefine(id, name, Catalog.Revision, out next, out catalogError);
        if (!success) return Fail(Enum.Parse<ThreadTagError>(catalogError.ToString()), out error);
        if (ReferenceEquals(next, Catalog)) return true;
        if (!CanChange(out error)) return false;
        result = new(Revision + 1, next, assignments);
        return true;
    }

    /// <summary>Explicit local membership add; existing membership is a successful no-op.</summary>
    internal bool TryAdd(PrivateThreadTagId id, ResourceId? thread, long expectedRevision,
        out ThreadTagProjection result, out ThreadTagError error)
        => TryMembership(id, thread, expectedRevision, true, out result, out error);

    /// <summary>Explicit local unassignment only; a missing assignment is a successful no-op.</summary>
    internal bool TryRemove(PrivateThreadTagId id, ResourceId? thread, long expectedRevision,
        out ThreadTagProjection result, out ThreadTagError error)
        => TryMembership(id, thread, expectedRevision, false, out result, out error);

    private bool TryMembership(PrivateThreadTagId id, ResourceId? thread, long expectedRevision, bool add,
        out ThreadTagProjection result, out ThreadTagError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error) || !CheckKnownId(id, out error)) return false;
        if (!IsThread(thread)) return Fail(ThreadTagError.InvalidThread, out error);
        var assignment = new PrivateThreadTagAssignment(id, thread!);
        bool exists = assignments.Contains(assignment);
        if (exists == add) return true;
        if (add && assignments.Length >= MaximumAssignments) return Fail(ThreadTagError.CapacityExceeded, out error);
        if (!CanChange(out error)) return false;
        result = new(Revision + 1, Catalog, add
            ? assignments.Append(assignment).ToArray()
            : assignments.Where(item => item != assignment).ToArray());
        return true;
    }

    /// <summary>
    /// Default-off. Unselected does not inspect scope or disclose definitions/assignments.
    /// Selected copies only exact scoped threads and definitions used by their assignments.
    /// Scope membership does not assert that a thread exists in an archive or on a provider.
    /// </summary>
    internal bool TrySelectThreads(IReadOnlyCollection<ResourceId>? scope, out ThreadTagSnapshot? snapshot,
        out ThreadTagError error, bool selected = false)
    {
        snapshot = null;
        error = ThreadTagError.None;
        if (!selected)
        {
            snapshot = new(false, Revision, Array.Empty<ResourceId>(),
                Array.Empty<PrivateThreadTagDefinition>(), Array.Empty<PrivateThreadTagAssignment>());
            return true;
        }
        if (scope is null) return Fail(ThreadTagError.InvalidScope, out error);
        if (scope.Count > MaximumScopeCount) return Fail(ThreadTagError.CapacityExceeded, out error);
        var scoped = new HashSet<ResourceId>();
        var ordered = new List<ResourceId>();
        int inspected = 0;
        foreach (ResourceId thread in scope)
        {
            if (++inspected > MaximumScopeCount) return Fail(ThreadTagError.CapacityExceeded, out error);
            if (!IsThread(thread)) return Fail(ThreadTagError.InvalidThread, out error);
            if (scoped.Add(thread)) ordered.Add(thread);
        }
        PrivateThreadTagAssignment[] chosen = assignments.Where(item => scoped.Contains(item.Thread)).ToArray();
        var used = chosen.Select(item => item.TagId).ToHashSet();
        snapshot = new(true, Revision, ordered, definitions.Where(item => used.Contains(item.Id)), chosen);
        return true;
    }

    private bool CheckRevision(long expected, out ThreadTagError error)
    {
        error = expected == Revision ? ThreadTagError.None : ThreadTagError.StaleRevision;
        return error == ThreadTagError.None;
    }

    private static bool CheckId(PrivateThreadTagId id, out ThreadTagError error)
    {
        error = id.Value != Guid.Empty ? ThreadTagError.None : ThreadTagError.InvalidTagId;
        return error == ThreadTagError.None;
    }

    private bool CheckKnownId(PrivateThreadTagId id, out ThreadTagError error)
    {
        if (!CheckId(id, out error)) return false;
        return Catalog.Contains(id) || Fail(ThreadTagError.UnknownTag, out error);
    }

    private bool CanChange(out ThreadTagError error)
    {
        error = Revision == long.MaxValue ? ThreadTagError.RevisionOverflow : ThreadTagError.None;
        return error == ThreadTagError.None;
    }

    private static bool IsThread(ResourceId? thread)
    {
        try { ArchiveValidation.ValidateId(thread!); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool Fail(ThreadTagError reason, out ThreadTagError error)
    {
        error = reason;
        return false;
    }
}
