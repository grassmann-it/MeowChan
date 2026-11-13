using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MeowChan.Archive;

namespace MeowChan.Desktop;

internal enum ReaderBodySegmentKind { Text, Greentext, Quote, DeadQuote, DeadNote }

/// <summary>
/// A contiguous slice of the document text; segments cover the text exactly once. <see cref="Style"/> carries
/// the spoiler, code and link flags (never Greentext, which is the kind). <see cref="Link"/> is the address a link
/// opens: its visible text parsed as an absolute http(s) address; a link text that is anything else is plain text.
/// </summary>
internal sealed record ReaderBodySegment(ReaderBodySegmentKind Kind, int Start, int Length, ArchiveQuoteRow? Quote = null,
    ArchiveTextStyle Style = ArchiveTextStyle.None, Uri? Link = null)
{
    public int End => Start + Length;
    public bool IsSpoiler => (Style & ArchiveTextStyle.Spoiler) != 0;
}

/// <summary>
/// Imageboard formatting of a plain post body: greentext lines, inline <c>&gt;&gt;No.</c> references, references to
/// posts that were never captured (struck through, followed by a short note), spoilers, code and external links.
/// Text is the plain post body; only a dead reference gets its note inserted behind it, so selection, copy and
/// hit-testing always address the displayed text. The reader find works on the thread index, not on this text.
/// </summary>
internal sealed record ReaderBodyDocument(string Text, IReadOnlyList<ReaderBodySegment> Segments, bool HasUnplacedQuotes)
{
    public bool IsFormatted => Segments.Any(segment => segment.Kind != ReaderBodySegmentKind.Text || segment.Style != ArchiveTextStyle.None);
    public bool HasSpoilers => Segments.Any(segment => segment.IsSpoiler);
}

internal static class ReaderBodyFormatting
{
    // Shared with the HTML export: ">>123" and ">>>/board/" start a reference, not greentext.
    internal static bool IsGreentextLine(ReadOnlySpan<char> line) => MeowChan.Archive.ArchiveText.IsGreentextLine(line);

    internal static ReaderBodyDocument Format(string text, IReadOnlyList<ArchiveQuoteRow> quotes,
        IReadOnlyList<ArchiveTextMark>? marks = null, string? deadNote = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Longest label first, so ">>1234" is never linked as ">>123" followed by "4".
        (string Label, ArchiveQuoteRow Row)[] labels = quotes.Where(row => row.Quote.Label.StartsWith(">>", StringComparison.Ordinal))
            .GroupBy(row => row.Quote.Label, StringComparer.Ordinal).Select(group => (group.Key, group.First()))
            .OrderByDescending(pair => pair.Key.Length).ToArray();
        HashSet<string> placed = new(StringComparer.Ordinal);
        List<(int Start, int End, ArchiveQuoteRow Row)> references = new();
        for (int index = 0; index + 1 < text.Length && labels.Length > 0; index++)
        {
            if (text[index] != '>' || text[index + 1] != '>' || index > 0 && text[index - 1] == '>') continue;
            foreach ((string label, ArchiveQuoteRow row) in labels)
            {
                if (!text.AsSpan(index).StartsWith(label, StringComparison.Ordinal)) continue;
                int after = index + label.Length;
                if (after < text.Length && char.IsLetterOrDigit(text[after])) continue;
                references.Add((index, after, row));
                placed.Add(label);
                index = after - 1;
                break;
            }
        }
        IReadOnlyList<ArchiveTextRun> runs = ArchiveBodyRuns.Split(text, marks);
        SortedSet<int> cuts = new() { 0, text.Length };
        foreach (ArchiveTextRun run in runs) cuts.Add(run.Start);
        foreach ((int start, int end, _) in references) { cuts.Add(start); cuts.Add(end); }
        int[] points = cuts.ToArray();
        StringBuilder shown = new(text.Length + 16);
        List<ReaderBodySegment> segments = new();
        int runIndex = 0, referenceIndex = 0;
        for (int index = 0; index + 1 < points.Length; index++)
        {
            int start = points[index], end = points[index + 1];
            while (runIndex + 1 < runs.Count && runs[runIndex].End <= start) runIndex++;
            ArchiveTextStyle style = runs.Count == 0 ? ArchiveTextStyle.None : runs[runIndex].Style;
            while (referenceIndex < references.Count && references[referenceIndex].End <= start) referenceIndex++;
            (int Start, int End, ArchiveQuoteRow Row)? reference = referenceIndex < references.Count && references[referenceIndex].Start <= start
                ? references[referenceIndex] : null;
            ArchiveTextStyle flags = style & ~ArchiveTextStyle.Greentext;
            Uri? link = null;
            if ((flags & ArchiveTextStyle.Link) != 0)
            {
                // The whole link run is the visible text, however references or other cuts divide it into segments.
                ArchiveTextRun linkRun = runs[runIndex];
                link = ReaderExternalLink.Parse(text.Substring(linkRun.Start, linkRun.Length));
                if (link == null) flags &= ~ArchiveTextStyle.Link;
            }
            ReaderBodySegmentKind kind = reference != null ? reference.Value.Row.IsDead ? ReaderBodySegmentKind.DeadQuote : ReaderBodySegmentKind.Quote
                : (style & ArchiveTextStyle.Greentext) != 0 ? ReaderBodySegmentKind.Greentext : ReaderBodySegmentKind.Text;
            segments.Add(new ReaderBodySegment(kind, shown.Length, end - start, reference?.Row, flags, link));
            shown.Append(text, start, end - start);
            if (kind == ReaderBodySegmentKind.DeadQuote && end == reference!.Value.End && !string.IsNullOrEmpty(deadNote))
            {
                segments.Add(new ReaderBodySegment(ReaderBodySegmentKind.DeadNote, shown.Length, deadNote.Length, null, flags & ~ArchiveTextStyle.Link));
                shown.Append(deadNote);
            }
        }
        // Merge neighbours of the same plain kind and style, so an unformatted body stays one run.
        List<ReaderBodySegment> merged = new(segments.Count);
        foreach (ReaderBodySegment segment in segments)
        {
            if (merged.Count > 0 && merged[^1] is { Kind: ReaderBodySegmentKind.Text or ReaderBodySegmentKind.Greentext } previous
                && previous.Kind == segment.Kind && previous.Style == segment.Style && previous.Link == segment.Link)
                merged[^1] = previous with { Length = previous.Length + segment.Length };
            else merged.Add(segment);
        }
        return new ReaderBodyDocument(shown.ToString(), merged.AsReadOnly(), labels.Any(pair => !placed.Contains(pair.Label)));
    }
}
