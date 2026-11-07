using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace MeowChan.Transfer;

/// <summary>Explicit ordered identities only; never a live query or media/path authority.</summary>
internal sealed class NativeTransferArchiveSelection
{
    internal const int MaxThreads = 1000;

    internal NativeTransferArchiveSelection(IEnumerable<ResourceId> threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        var result = ImmutableArray.CreateBuilder<ResourceId>();
        HashSet<ResourceId> seen = new();
        foreach (ResourceId id in threads)
        {
            if (result.Count == MaxThreads) throw new ArgumentException("Selection exceeds the thread limit.", nameof(threads));
            if (id == null || !seen.Add(id)) throw new ArgumentException("Selection contains a null or duplicate identity.", nameof(threads));
            result.Add(id);
        }
        if (result.Count == 0) throw new ArgumentException("Selection must not be empty.", nameof(threads));
        Threads = result.ToImmutable();
    }

    internal ImmutableArray<ResourceId> Threads { get; }
}
