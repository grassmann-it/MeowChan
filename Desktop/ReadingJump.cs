using System;
using System.Collections.Generic;

namespace MeowChan.Desktop;

/// <summary>
/// Where "continue reading" lands in a thread read: the saved post when it was captured, else the nearest one that follows it. The
/// rule is plain data (post ids in thread order), so it is the same for the header button, the history menu, the history window and
/// the archive list, and testable without a reader.
/// </summary>
internal static class ReadingJump
{
    /// <param name="Index">Position in the ordered post list; -1 only for a thread without posts.</param>
    /// <param name="Exact">False when the saved post is not there and <paramref name="Index"/> is its replacement (the reader says so).</param>
    internal readonly record struct Target(int Index, bool Exact);

    /// <summary>
    /// <paramref name="orderedPostIds"/> are the posts of the read, oldest first. A missing post resolves to the first post with a
    /// larger numeric id (the next one that exists), else to the last post; ids that are not plain numbers cannot be compared, so such a
    /// post resolves to the first post of the read (what the reader showed before this rule existed).
    /// </summary>
    internal static Target Resolve(IReadOnlyList<string> orderedPostIds, string savedPostId)
    {
        ArgumentNullException.ThrowIfNull(orderedPostIds);
        ArgumentNullException.ThrowIfNull(savedPostId);
        if (orderedPostIds.Count == 0) return new Target(-1, false);
        for (int index = 0; index < orderedPostIds.Count; index++)
            if (string.Equals(orderedPostIds[index], savedPostId, StringComparison.Ordinal)) return new Target(index, true);
        if (!IsNumber(savedPostId)) return new Target(0, false);
        for (int index = 0; index < orderedPostIds.Count; index++)
            if (IsNumber(orderedPostIds[index]) && CompareNumbers(orderedPostIds[index], savedPostId) > 0) return new Target(index, false);
        return new Target(orderedPostIds.Count - 1, false);
    }

    private static bool IsNumber(string id)
    {
        if (id.Length == 0) return false;
        foreach (char character in id) if (character is < '0' or > '9') return false;
        return true;
    }

    // Plain decimal strings of any length: shorter (after leading zeros) is smaller, equal lengths compare as text.
    private static int CompareNumbers(string first, string second)
    {
        ReadOnlySpan<char> a = first.AsSpan().TrimStart('0');
        ReadOnlySpan<char> b = second.AsSpan().TrimStart('0');
        int comparison = a.Length.CompareTo(b.Length);
        return comparison != 0 ? comparison : a.SequenceCompareTo(b);
    }
}
