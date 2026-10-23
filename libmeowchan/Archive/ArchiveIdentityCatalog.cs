using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Archive;

public sealed record ArchiveBoardIdentity(string Source, string Board);

/// <summary>A bounded immutable catalog; HasMore describes identities omitted by the limit.</summary>
public sealed class ArchiveIdentityPage<T>
{
    public ArchiveIdentityPage(IEnumerable<T> items, bool hasMore)
    {
        Items = Array.AsReadOnly(items.ToArray());
        HasMore = hasMore;
    }
    public IReadOnlyList<T> Items { get; }
    public bool HasMore { get; }
}
