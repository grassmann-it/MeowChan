using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Archive;

[Flags]
internal enum ArchiveTextStyle { None = 0, Greentext = 1, Spoiler = 2, Code = 4, Link = 8 }

/// <summary>A slice of a post's plain text with one combination of imageboard styles.</summary>
internal readonly record struct ArchiveTextRun(int Start, int Length, ArchiveTextStyle Style)
{
    public int End => Start + Length;
}

/// <summary>A candidate link range of a body: an anchor mark (<see cref="IsMark"/>) or a bare http(s) address.</summary>
internal readonly record struct ArchiveLinkRange(int Start, int End, bool IsMark);

/// <summary>
/// Splits a plain post body into runs of equal style: greentext lines, the marked spoilers, code and
/// external links, and bare http(s) addresses. Shared by the reader and the HTML export, so both format alike.
/// The runs cover the text exactly once and never change it.
/// </summary>
internal static class ArchiveBodyRuns
{
    internal static IReadOnlyList<ArchiveTextRun> Split(string text, IReadOnlyList<ArchiveTextMark>? marks = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return Array.Empty<ArchiveTextRun>();
        List<(int Start, int End, ArchiveTextStyle Style)> ranges = new();
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int newline = text.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? text.Length : newline + 1; // The break belongs to its line.
            if (ArchiveText.IsGreentextLine(text.AsSpan(lineStart, lineEnd - lineStart)))
                ranges.Add((lineStart, lineEnd, ArchiveTextStyle.Greentext));
            lineStart = lineEnd;
        }
        if (marks != null)
            foreach (ArchiveTextMark mark in marks)
            {
                int start = Math.Max(0, mark.Start), end = Math.Min(text.Length, mark.Start + mark.Length);
                if (end > start) ranges.Add((start, end, mark.Kind switch
                {
                    ArchiveTextMarkKind.Spoiler => ArchiveTextStyle.Spoiler,
                    ArchiveTextMarkKind.Code => ArchiveTextStyle.Code,
                    _ => ArchiveTextStyle.Link
                }));
            }
        foreach ((int start, int end) in Addresses(text)) ranges.Add((start, end, ArchiveTextStyle.Link));
        SortedSet<int> bounds = new() { 0, text.Length };
        foreach ((int start, int end, _) in ranges) { bounds.Add(start); bounds.Add(end); }
        int[] points = bounds.ToArray();
        List<ArchiveTextRun> runs = new(points.Length);
        for (int index = 0; index + 1 < points.Length; index++)
        {
            ArchiveTextStyle style = ArchiveTextStyle.None;
            foreach ((int start, int end, ArchiveTextStyle value) in ranges)
                if (start <= points[index] && end >= points[index + 1]) style |= value;
            if (runs.Count > 0 && runs[^1].Style == style) runs[^1] = runs[^1] with { Length = runs[^1].Length + points[index + 1] - points[index] };
            else runs.Add(new ArchiveTextRun(points[index], points[index + 1] - points[index], style));
        }
        return runs.AsReadOnly();
    }

    /// <summary>
    /// The candidate link ranges of a body, before the styles are combined: the marked link ranges (anchors) and the bare
    /// http(s) addresses. A reader that opens links needs the ranges themselves, because <see cref="Split"/> divides a link
    /// wherever a spoiler or code mark cuts it.
    /// </summary>
    internal static IReadOnlyList<ArchiveLinkRange> LinkRanges(string text, IReadOnlyList<ArchiveTextMark>? marks = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<ArchiveLinkRange> ranges = new();
        if (marks != null)
            foreach (ArchiveTextMark mark in marks)
            {
                if (mark.Kind != ArchiveTextMarkKind.Link) continue;
                int start = Math.Max(0, mark.Start), end = Math.Min(text.Length, mark.Start + mark.Length);
                if (end > start) ranges.Add(new ArchiveLinkRange(start, end, true));
            }
        foreach ((int start, int end) in Addresses(text)) ranges.Add(new ArchiveLinkRange(start, end, false));
        return ranges.AsReadOnly();
    }

    // Bare http(s) addresses (4chan posts carry most links as text, not anchors).
    private static IEnumerable<(int Start, int End)> Addresses(string text)
    {
        int index = 0;
        int found = 0;
        while (found < 256 && index < text.Length)
        {
            int http = text.IndexOf("http", index, StringComparison.OrdinalIgnoreCase);
            if (http < 0) yield break;
            int schemeEnd = http + 4;
            if (schemeEnd < text.Length && (text[schemeEnd] is 's' or 'S')) schemeEnd++;
            if (!text.AsSpan(schemeEnd).StartsWith("://", StringComparison.Ordinal) || (http > 0 && char.IsLetterOrDigit(text[http - 1])))
            { index = http + 4; continue; }
            int end = schemeEnd + 3;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"')) end++;
            while (end > schemeEnd + 3 && text[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '\'') end--;
            index = Math.Max(end, http + 4);
            if (end > schemeEnd + 3) { found++; yield return (http, end); }
        }
    }
}
