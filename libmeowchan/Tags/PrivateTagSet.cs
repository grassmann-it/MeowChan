using System;
using System.Collections.Generic;

namespace MeowChan.Tags;

/// <summary>Constant error codes; rejected input is never included in an error.</summary>
internal enum PrivateTagSetError
{
    None,
    NullName,
    CapacityExceeded
}

/// <summary>
/// An immutable, bounded in-memory set of validated private tag names. Equality of membership
/// uses only the name value's invariant key, compared ordinally. On collision the first admitted
/// name (and its display) survives; snapshots enumerate in admission order, never hash order.
/// This is not a tag catalog, persistent identity, target binding, source-tag or favorite model.
/// </summary>
internal sealed class PrivateTagSet
{
    /// <summary>
    /// Defensive allocation/work budget for this in-memory primitive, NOT a product tag quota
    /// or wire/storage limit. 1,024 names bound display payload to 512 KiB of UTF-8 (using the
    /// existing name limit), plus keys, UTF-16 strings and object/array overhead. Each operation
    /// scans at most this many keys and a successful change copies at most this many references.
    /// Revisit with measured integration budgets before exposing any user-facing/storage policy.
    /// </summary>
    internal const int MaximumCount = 1024;

    private readonly PrivateTagName[] names;

    // All callers are internal implementation paths which transfer ownership of a fresh array.
    // Neither the array nor a mutable view is exposed, even to same-assembly consumers.
    private PrivateTagSet(PrivateTagName[] ownedNames)
    {
        names = ownedNames;
        Items = Array.AsReadOnly(ownedNames);
    }

    internal static PrivateTagSet Empty { get; } = new(Array.Empty<PrivateTagName>());

    internal int Count => names.Length;

    /// <summary>Read-only snapshot in admission order; names themselves are immutable.</summary>
    internal IReadOnlyList<PrivateTagName> Items { get; }

    /// <summary>Null is not a member. No text parsing or new normalization policy occurs here.</summary>
    internal bool Contains(PrivateTagName? name) => name is not null && IndexOf(name) >= 0;

    /// <summary>
    /// Returns this set on rejection or an existing key, including at capacity. A duplicate is
    /// a successful no-op, not a rename. The original set is unchanged on every path.
    /// </summary>
    internal bool TryAdd(PrivateTagName? name, out PrivateTagSet result, out PrivateTagSetError error)
    {
        result = this;
        if (name is null)
        {
            error = PrivateTagSetError.NullName;
            return false;
        }

        // Membership comes before capacity so a retry cannot fail merely because the set is full.
        if (IndexOf(name) >= 0)
        {
            error = PrivateTagSetError.None;
            return true;
        }
        if (names.Length >= MaximumCount)
        {
            error = PrivateTagSetError.CapacityExceeded;
            return false;
        }

        var copy = new PrivateTagName[names.Length + 1];
        Array.Copy(names, copy, names.Length);
        copy[names.Length] = name;
        result = new PrivateTagSet(copy);
        error = PrivateTagSetError.None;
        return true;
    }

    /// <summary>
    /// Removes by the same key used for admission. Missing keys are successful no-ops; null
    /// is rejected without changing the result. This removes only local membership, not a
    /// global definition, a target assignment, or any source metadata.
    /// </summary>
    internal bool TryRemove(PrivateTagName? name, out PrivateTagSet result, out PrivateTagSetError error)
    {
        result = this;
        if (name is null)
        {
            error = PrivateTagSetError.NullName;
            return false;
        }

        int index = IndexOf(name);
        if (index >= 0)
        {
            if (names.Length == 1)
            {
                result = Empty;
            }
            else
            {
                var copy = new PrivateTagName[names.Length - 1];
                Array.Copy(names, 0, copy, 0, index);
                Array.Copy(names, index + 1, copy, index, names.Length - index - 1);
                result = new PrivateTagSet(copy);
            }
        }

        error = PrivateTagSetError.None;
        return true;
    }

    private int IndexOf(PrivateTagName name)
    {
        for (int index = 0; index < names.Length; index++)
        {
            if (string.Equals(names[index].Key, name.Key, StringComparison.Ordinal)) return index;
        }
        return -1;
    }
}
