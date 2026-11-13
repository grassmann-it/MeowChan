using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

internal enum ReaderBodySegmentKind { Text, Greentext, Quote }

/// <summary>A contiguous slice of the plain post text; segments cover the text exactly once.</summary>
internal sealed record ReaderBodySegment(ReaderBodySegmentKind Kind, int Start, int Length, ArchiveQuoteRow? Quote = null)
{
    public int End => Start + Length;
}

/// <summary>
/// Imageboard formatting of a plain post body: greentext lines and inline <c>&gt;&gt;No.</c> references.
/// The text itself is never changed, so selection, copy and the reader find offsets stay those of the plain text.
/// </summary>
internal sealed record ReaderBodyDocument(string Text, IReadOnlyList<ReaderBodySegment> Segments, bool HasUnplacedQuotes)
{
    public bool IsFormatted => Segments.Any(segment => segment.Kind != ReaderBodySegmentKind.Text);
}

internal static class ReaderBodyFormatting
{
    // Shared with the HTML export: ">>123" and ">>>/board/" start a reference, not greentext.
    internal static bool IsGreentextLine(ReadOnlySpan<char> line) => MeowChan.Archive.ArchiveText.IsGreentextLine(line);

    internal static ReaderBodyDocument Format(string text, IReadOnlyList<ArchiveQuoteRow> quotes)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Longest label first, so ">>1234" is never linked as ">>123" followed by "4".
        (string Label, ArchiveQuoteRow Row)[] labels = quotes.Where(row => row.Quote.Label.StartsWith(">>", StringComparison.Ordinal))
            .GroupBy(row => row.Quote.Label, StringComparer.Ordinal).Select(group => (group.Key, group.First()))
            .OrderByDescending(pair => pair.Key.Length).ToArray();
        HashSet<string> placed = new(StringComparer.Ordinal);
        List<ReaderBodySegment> segments = new();
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int newline = text.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? text.Length : newline + 1; // The break belongs to its line.
            ReaderBodySegmentKind lineKind = IsGreentextLine(text.AsSpan(lineStart, lineEnd - lineStart))
                ? ReaderBodySegmentKind.Greentext : ReaderBodySegmentKind.Text;
            int runStart = lineStart;
            for (int index = lineStart; index < lineEnd; index++)
            {
                if (text[index] != '>' || index + 1 >= lineEnd || text[index + 1] != '>' || index > 0 && text[index - 1] == '>') continue;
                foreach ((string label, ArchiveQuoteRow row) in labels)
                {
                    if (!text.AsSpan(index).StartsWith(label, StringComparison.Ordinal)) continue;
                    int after = index + label.Length;
                    if (after < text.Length && char.IsLetterOrDigit(text[after])) continue;
                    if (index > runStart) segments.Add(new ReaderBodySegment(lineKind, runStart, index - runStart));
                    segments.Add(new ReaderBodySegment(ReaderBodySegmentKind.Quote, index, label.Length, row));
                    placed.Add(label);
                    runStart = after;
                    index = after - 1;
                    break;
                }
            }
            if (lineEnd > runStart) segments.Add(new ReaderBodySegment(lineKind, runStart, lineEnd - runStart));
            lineStart = lineEnd;
        }
        // Merge neighbours of the same plain kind, so an unformatted body stays one run.
        List<ReaderBodySegment> merged = new(segments.Count);
        foreach (ReaderBodySegment segment in segments)
        {
            if (merged.Count > 0 && merged[^1] is { Kind: not ReaderBodySegmentKind.Quote } previous && previous.Kind == segment.Kind)
                merged[^1] = previous with { Length = previous.Length + segment.Length };
            else merged.Add(segment);
        }
        return new ReaderBodyDocument(text, merged.AsReadOnly(), labels.Any(pair => !placed.Contains(pair.Label)));
    }
}
