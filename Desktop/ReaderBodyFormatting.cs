using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MeowChan.Archive;

namespace MeowChan.Desktop;

internal enum ReaderBodySegmentKind { Text, Greentext, Quote, DeadQuote, DeadNote, QuoteMarker }

/// <summary>
/// A contiguous slice of the document text; segments cover the text exactly once. <see cref="Style"/> carries
/// the spoiler, code and link flags (never Greentext, which is the kind). <see cref="Link"/> is the address a link
/// opens: the visible text of the whole link range (which a spoiler or code mark may divide into several segments)
/// parsed as an absolute http(s) address; a link text that is anything else is plain text. <see cref="LinkSpansSpoiler"/>
/// is set when a spoiler lies anywhere inside that link range.
/// </summary>
internal sealed record ReaderBodySegment(ReaderBodySegmentKind Kind, int Start, int Length, ArchiveQuoteRow? Quote = null,
    ArchiveTextStyle Style = ArchiveTextStyle.None, Uri? Link = null, bool LinkSpansSpoiler = false)
{
    public int End => Start + Length;
    public bool IsSpoiler => (Style & ArchiveTextStyle.Spoiler) != 0;
    /// <summary>The link of this segment is not open to use while a spoiler of its own text, or one inside the same link, is hidden.</summary>
    public bool LinkNeedsReveal => Link != null && (IsSpoiler || LinkSpansSpoiler);
}

/// <summary>
/// Imageboard formatting of a plain post body: greentext lines, inline <c>&gt;&gt;No.</c> references, references to
/// posts that were never captured (struck through, followed by a short note), a "(OP)" or "(other thread)" marker behind a
/// reference to an opening post or another thread, spoilers, code and external links.
/// Text is the plain post body; only a dead reference gets its note, and a marked reference its marker, inserted behind it, so selection, copy and
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
        IReadOnlyList<ResolvedLink> links = ResolveLinks(text, marks, runs);
        SortedSet<int> cuts = new() { 0, text.Length };
        foreach (ArchiveTextRun run in runs) cuts.Add(run.Start);
        foreach (ResolvedLink resolved in links) { cuts.Add(resolved.Start); cuts.Add(resolved.End); }
        foreach ((int start, int end, _) in references) { cuts.Add(start); cuts.Add(end); }
        int[] points = cuts.ToArray();
        StringBuilder shown = new(text.Length + 16);
        List<ReaderBodySegment> segments = new();
        int runIndex = 0, referenceIndex = 0, linkIndex = 0;
        for (int index = 0; index + 1 < points.Length; index++)
        {
            int start = points[index], end = points[index + 1];
            while (runIndex + 1 < runs.Count && runs[runIndex].End <= start) runIndex++;
            ArchiveTextStyle style = runs.Count == 0 ? ArchiveTextStyle.None : runs[runIndex].Style;
            while (referenceIndex < references.Count && references[referenceIndex].End <= start) referenceIndex++;
            (int Start, int End, ArchiveQuoteRow Row)? reference = referenceIndex < references.Count && references[referenceIndex].Start <= start
                ? references[referenceIndex] : null;
            // Only a range that parsed as an address is a link; the style flag of a run alone is not (see ResolveLinks).
            ArchiveTextStyle flags = style & ~(ArchiveTextStyle.Greentext | ArchiveTextStyle.Link);
            while (linkIndex < links.Count && links[linkIndex].End <= start) linkIndex++;
            ResolvedLink? resolvedLink = linkIndex < links.Count && links[linkIndex].Start <= start ? links[linkIndex] : null;
            Uri? link = resolvedLink?.Uri;
            if (resolvedLink != null) flags |= ArchiveTextStyle.Link;
            ReaderBodySegmentKind kind = reference != null ? reference.Value.Row.IsDead ? ReaderBodySegmentKind.DeadQuote : ReaderBodySegmentKind.Quote
                : (style & ArchiveTextStyle.Greentext) != 0 ? ReaderBodySegmentKind.Greentext : ReaderBodySegmentKind.Text;
            segments.Add(new ReaderBodySegment(kind, shown.Length, end - start, reference?.Row, flags, link, resolvedLink?.SpansSpoiler ?? false));
            shown.Append(text, start, end - start);
            // "(OP)" / "(other thread)" behind the last piece of a reference, before the note of a dead one.
            if (reference != null && end == reference.Value.End && reference.Value.Row.Marker is { Length: > 0 } marker)
            {
                segments.Add(new ReaderBodySegment(ReaderBodySegmentKind.QuoteMarker, shown.Length, marker.Length + 1, null, flags & ~ArchiveTextStyle.Link));
                shown.Append(' ').Append(marker);
            }
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
                && previous.Kind == segment.Kind && previous.Style == segment.Style && previous.Link == segment.Link
                && previous.LinkSpansSpoiler == segment.LinkSpansSpoiler)
                merged[^1] = previous with { Length = previous.Length + segment.Length };
            else merged.Add(segment);
        }
        return new ReaderBodyDocument(shown.ToString(), merged.AsReadOnly(), labels.Any(pair => !placed.Contains(pair.Label)));
    }

    private readonly record struct ResolvedLink(int Start, int End, Uri Uri, bool SpansSpoiler);

    /// <summary>
    /// The links of a body as ranges of the plain text, sorted and without overlap. An anchor opens its whole text when that
    /// text is an address; an anchor with other text (words plus an address) is no link itself, but the bare addresses inside
    /// it stay links of their own. A link is one range however spoilers or code marks divide it into styled runs, so its
    /// target is always the address the reader shows.
    /// </summary>
    private static IReadOnlyList<ResolvedLink> ResolveLinks(string text, IReadOnlyList<ArchiveTextMark>? marks, IReadOnlyList<ArchiveTextRun> runs)
    {
        IReadOnlyList<ArchiveLinkRange> candidates = ArchiveBodyRuns.LinkRanges(text, marks);
        if (candidates.Count == 0) return Array.Empty<ResolvedLink>();
        List<ResolvedLink> accepted = new();
        // Anchors first (their whole text is the address), then the bare addresses that no accepted link already covers.
        foreach (bool anchors in new[] { true, false })
            foreach (ArchiveLinkRange range in candidates.Where(candidate => candidate.IsMark == anchors).OrderBy(candidate => candidate.Start))
            {
                if (accepted.Any(other => range.Start < other.End && range.End > other.Start)) continue;
                if (ReaderExternalLink.Parse(text.Substring(range.Start, range.End - range.Start)) is not { } uri) continue;
                bool spansSpoiler = runs.Any(run => (run.Style & ArchiveTextStyle.Spoiler) != 0 && run.Start < range.End && run.End > range.Start);
                accepted.Add(new ResolvedLink(range.Start, range.End, uri, spansSpoiler));
            }
        accepted.Sort((left, right) => left.Start.CompareTo(right.Start));
        return accepted.AsReadOnly();
    }
}
