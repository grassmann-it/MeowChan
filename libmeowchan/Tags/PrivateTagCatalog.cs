using System;
using System.Collections.Generic;
using System.Linq;

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
    internal const int MaximumDefinitions = PrivateTagSet.MaximumCount;
    private readonly PrivateTagDefinition[] definitions;
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
    }

    private PrivateTagCatalog(long revision, PrivateTagDefinition[] definitions, PrivateTagCatalog parent)
    {
        Revision = revision;
        this.definitions = definitions;
        lineage = new(revision, parent.lineage);
        Definitions = Array.AsReadOnly(definitions);
    }

    internal long Revision { get; }
    internal IReadOnlyList<PrivateTagDefinition> Definitions { get; }
    internal bool Contains(TagId id) => definitions.Any(item => item.Id == id);

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
        PrivateTagDefinition? existing = definitions.FirstOrDefault(item => item.Id == id);
        if (existing is not null)
            return string.Equals(existing.Name.Display, name.Display, StringComparison.Ordinal)
                || Fail(PrivateTagCatalogError.DefinitionConflict, out error);
        if (definitions.Any(item => item.Name == name)) return Fail(PrivateTagCatalogError.NameCollision, out error);
        if (definitions.Length >= MaximumDefinitions) return Fail(PrivateTagCatalogError.CapacityExceeded, out error);
        if (!CanChange(out error)) return false;
        result = new(Revision + 1, definitions.Append(new(id, name)).ToArray(), this);
        return true;
    }

    internal bool TryRename(TagId id, PrivateTagName? name, long expectedRevision,
        out PrivateTagCatalog result, out PrivateTagCatalogError error)
    {
        result = this;
        if (!CheckRevision(expectedRevision, out error) || !CheckKnownId(id, out error)) return false;
        if (name is null) return Fail(PrivateTagCatalogError.InvalidName, out error);
        int index = Array.FindIndex(definitions, item => item.Id == id);
        if (string.Equals(definitions[index].Name.Display, name.Display, StringComparison.Ordinal)) return true;
        if (definitions.Any(item => item.Id != id && item.Name == name)) return Fail(PrivateTagCatalogError.NameCollision, out error);
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
        return definitions.Any(item => item.Id == id) || Fail(PrivateTagCatalogError.UnknownTag, out error);
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
