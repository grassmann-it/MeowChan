using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace MeowChan.Tags;

/// <summary>Shared caller-supplied local identity; not a portable reference.</summary>
internal readonly record struct TagId(Guid Value);
internal sealed record PrivateTagDefinition(TagId Id, PrivateTagName Name);
internal enum PrivateTagCatalogError
{
    None, StaleRevision, InvalidTagId, InvalidName, UnknownTag,
    DefinitionConflict, NameCollision, CapacityExceeded, RevisionOverflow
}

/// <summary>Single immutable own-tag vocabulary. No delete, merge, storage or target semantics.</summary>
internal sealed class PrivateTagCatalog
{
    /// <summary>
    /// Technical ceiling of one vocabulary (reading and the configurable own-tag limit, see
    /// <see cref="PrivateTagLimits"/>). Lookups are indexed; a change copies the definitions once (O(n)).
    /// </summary>
    internal const int MaximumDefinitions = 20_000;
    private readonly PrivateTagDefinition[] definitions;
    // Hash indexes over the same immutable array: identity by ID, names by their ordinal comparison key
    // (exactly the PrivateTagName equality). Built once per instance, so every lookup is O(1).
    private readonly Dictionary<TagId, int> indexById;
    private readonly Dictionary<string, int> indexByKey;
    // Retain only ancestry tokens, not historical definition arrays.
    private sealed record Lineage(long Revision, Lineage? Parent);
    private readonly Lineage lineage;

    internal PrivateTagCatalog(long initialRevision = 0)
    {
        if (initialRevision < 0) throw new ArgumentOutOfRangeException(nameof(initialRevision));
        Revision = initialRevision;
        lineage = new(initialRevision, null);
        definitions = Array.Empty<PrivateTagDefinition>();
        Definitions = Array.AsReadOnly(definitions);
        indexById = new();
        indexByKey = new(StringComparer.Ordinal);
    }

    private PrivateTagCatalog(long revision, PrivateTagDefinition[] definitions, PrivateTagCatalog parent)
    {
        Revision = revision;
        this.definitions = definitions;
        lineage = new(revision, parent.lineage);
        Definitions = Array.AsReadOnly(definitions);
        indexById = new(definitions.Length);
        indexByKey = new(definitions.Length, StringComparer.Ordinal);
        for (int index = 0; index < definitions.Length; index++)
        {
            indexById.Add(definitions[index].Id, index);
            indexByKey.Add(definitions[index].Name.Key, index);
        }
    }

    /// <summary>
    /// The catalog that defining each item in order with <see cref="TryDefine"/>, starting from a new catalog at
    /// revision 0, would end with: same definitions, order, revision, and the same first error. Linear time.
    /// </summary>
    internal static bool TryCreate(IEnumerable<PrivateTagDefinition> items,
        [NotNullWhen(true)] out PrivateTagCatalog? result, out PrivateTagCatalogError error)
    {
        ArgumentNullException.ThrowIfNull(items);
        result = null;
        PrivateTagCatalog root = new();
        List<PrivateTagDefinition> accepted = new();
        Dictionary<TagId, PrivateTagName> names = new();
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (PrivateTagDefinition item in items)
        {
            if (item is null) throw new ArgumentException("A definition is required.", nameof(items));
            if (!CheckId(item.Id, out error)) return false;
            if (item.Name is null) return Fail(PrivateTagCatalogError.InvalidName, out error);
            if (names.TryGetValue(item.Id, out PrivateTagName? existing))
            {
                if (string.Equals(existing.Display, item.Name.Display, StringComparison.Ordinal)) continue;
                return Fail(PrivateTagCatalogError.DefinitionConflict, out error);
            }
            if (keys.Contains(item.Name.Key)) return Fail(PrivateTagCatalogError.NameCollision, out error);
            if (accepted.Count >= MaximumDefinitions) return Fail(PrivateTagCatalogError.CapacityExceeded, out error);
            names.Add(item.Id, item.Name);
            keys.Add(item.Name.Key);
            accepted.Add(item);
        }
        error = PrivateTagCatalogError.None;
        result = accepted.Count == 0 ? root : new(accepted.Count, accepted.ToArray(), root);
        return true;
    }

    internal long Revision { get; }
    internal IReadOnlyList<PrivateTagDefinition> Definitions { get; }
    internal bool Contains(TagId id) => indexById.ContainsKey(id);

    /// <summary>The definition whose name has the same comparison key, if any.</summary>
    internal PrivateTagDefinition? Find(PrivateTagName? name) =>
        name is not null && indexByKey.TryGetValue(name.Key, out int index) ? definitions[index] : null;

    // Reference ancestry rejects unrelated vocabularies and same-revision divergent branches.
    internal bool DescendsFrom(PrivateTagCatalog ancestor)
    {
        for (Lineage? current = lineage; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor.lineage)) return true;
            if (current.Revision <= ancestor.Revision) return false;
        }
        return false;
    }

    internal bool TryDefine(TagId id, PrivateTagName? name, long expectedRevision,
        out PrivateTagCatalog result, out PrivateTagCatalogError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error) || !CheckId(id, out error)) return false;
        if (name is null) return Fail(PrivateTagCatalogError.InvalidName, out error);
        if (indexById.TryGetValue(id, out int existing))
            return string.Equals(definitions[existing].Name.Display, name.Display, StringComparison.Ordinal)
                || Fail(PrivateTagCatalogError.DefinitionConflict, out error);
        if (indexByKey.ContainsKey(name.Key)) return Fail(PrivateTagCatalogError.NameCollision, out error);
        if (definitions.Length >= MaximumDefinitions) return Fail(PrivateTagCatalogError.CapacityExceeded, out error);
        if (!CanChange(out error)) return false;
        var grown = new PrivateTagDefinition[definitions.Length + 1];
        definitions.CopyTo(grown, 0);
        grown[^1] = new(id, name);
        result = new(Revision + 1, grown, this);
        return true;
    }

    internal bool TryRename(TagId id, PrivateTagName? name, long expectedRevision,
        out PrivateTagCatalog result, out PrivateTagCatalogError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error) || !CheckKnownId(id, out error)) return false;
        if (name is null) return Fail(PrivateTagCatalogError.InvalidName, out error);
        int index = indexById[id];
        if (string.Equals(definitions[index].Name.Display, name.Display, StringComparison.Ordinal)) return true;
        if (indexByKey.TryGetValue(name.Key, out int other) && other != index)
            return Fail(PrivateTagCatalogError.NameCollision, out error);
        if (!CanChange(out error)) return false;
        var copy = (PrivateTagDefinition[])definitions.Clone();
        copy[index] = new(id, name);
        result = new(Revision + 1, copy, this);
        return true;
    }

    private bool CheckRevision(long expected, out PrivateTagCatalogError error)
    {
        error = expected == Revision ? PrivateTagCatalogError.None : PrivateTagCatalogError.StaleRevision;
        return error == PrivateTagCatalogError.None;
    }

    private static bool CheckId(TagId id, out PrivateTagCatalogError error)
    {
        error = id.Value != Guid.Empty ? PrivateTagCatalogError.None : PrivateTagCatalogError.InvalidTagId;
        return error == PrivateTagCatalogError.None;
    }

    private bool CheckKnownId(TagId id, out PrivateTagCatalogError error)
    {
        if (!CheckId(id, out error)) return false;
        return indexById.ContainsKey(id) || Fail(PrivateTagCatalogError.UnknownTag, out error);
    }

    private bool CanChange(out PrivateTagCatalogError error)
    {
        error = Revision == long.MaxValue ? PrivateTagCatalogError.RevisionOverflow : PrivateTagCatalogError.None;
        return error == PrivateTagCatalogError.None;
    }

    private static bool Fail(PrivateTagCatalogError reason, out PrivateTagCatalogError error)
    {
        error = reason;
        return false;
    }
}
