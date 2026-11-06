using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeowChan.Storage;

namespace MeowChan.Archive;

internal sealed record LocalMediaRevisionSnapshot(
    LocalMediaRevisionReference Reference, int Ordinal, LocalMediaRole Role,
    long Length, string Sha256, string? DisplayName, LocalSourceClaims? Claims);

/// <summary>
/// Fully frozen SQL metadata, not a file capability or evidence of available/verified bytes.
/// Page and Find never revisit SQL, inventory or the filesystem.
/// </summary>
internal sealed partial class LocalCollectionSnapshot
{
    private readonly ImmutableArray<LocalMediaRevisionSnapshot> _items;
    private readonly ImmutableDictionary<LocalMediaRevisionReference, BoundMetadata> _byReference;

    // Logical persisted binding only. No physical root anchor, open delegate or authority is issued.
    private sealed record BoundMetadata(LocalMediaRevisionSnapshot Metadata,
        string TrustedProfileDirectory, Guid OperationId, string PayloadRelativeSlot);

    // Only the validated SQL decoder constructs this type; no inventory/foreign DTO factory.
    internal LocalCollectionSnapshot(LocalCollectionSummary summary,
        ImmutableArray<LocalMediaRevisionSnapshot> items, string catalogInstance, long generation,
        string trustedProfileDirectory, Guid operationId, ImmutableArray<string> slots)
    {
        if (items.IsDefaultOrEmpty || items.Length != summary.ItemCount || slots.Length != items.Length)
            throw new ArgumentException("A complete local metadata snapshot is required.");
        Summary = summary;
        CatalogInstance = catalogInstance;
        ObservedCatalogGeneration = generation;
        _items = items;
        var bindings = ImmutableDictionary.CreateBuilder<LocalMediaRevisionReference, BoundMetadata>();
        for (int i = 0; i < items.Length; i++)
            bindings.Add(items[i].Reference, new(items[i], trustedProfileDirectory, operationId, slots[i]));
        _byReference = bindings.ToImmutable();
    }

    internal LocalCollectionSummary Summary { get; }
    internal int Count => _items.Length;
    internal string CatalogInstance { get; }
    internal long ObservedCatalogGeneration { get; }

    internal IReadOnlyList<LocalMediaRevisionSnapshot> GetPage(int offset = 0, int limit = 100)
    {
        LocalImportCodec.ValidatePage(offset, limit);
        return offset >= Count ? ImmutableArray<LocalMediaRevisionSnapshot>.Empty
            : _items.Slice(offset, Math.Min(limit, Count - offset));
    }

    internal LocalMediaRevisionSnapshot? Find(LocalMediaRevisionReference reference)
    {
        if (reference.CollectionId.Value == Guid.Empty || reference.ItemId.Value == Guid.Empty
            || reference.RevisionId.Value == Guid.Empty)
            throw new ArgumentException("A complete nonempty local reference is required.", nameof(reference));
        return _byReference.TryGetValue(reference, out BoundMetadata? binding) ? binding.Metadata : null;
    }
}
