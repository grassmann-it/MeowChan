using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace MeowChan.Archive;

/// <summary>Decodes source HTML as inert readable text. No browser, provider or network is involved.</summary>
public static class ArchiveText
{
    public static string PlainText(string? html) => WebUtility.HtmlDecode(Process(html, false))
        .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    internal static string WithoutActiveContent(string? html) => Process(html, true);

    /// <summary>
    /// The same text as <see cref="PlainText"/> plus the character ranges of spoilers, code and external links
    /// (imageboard formatting that plain text would lose). The ranges address the returned text.
    /// </summary>
    internal static (string Text, IReadOnlyList<ArchiveTextMark> Marks) PlainTextWithMarks(string? html)
    {
        if (string.IsNullOrEmpty(html) || !MayCarryMarks(html)) return (PlainText(html), Array.Empty<ArchiveTextMark>());
        MarkState state = new MarkState();
        foreach (char character in html) if (character is >= OpenSpoiler and <= CloseLink) return (PlainText(html), Array.Empty<ArchiveTextMark>());
        string raw = Process(html, false, state);
        int expected = state.Expected;
        string decoded = WebUtility.HtmlDecode(raw).Replace("\r\n", "\n", StringComparison.Ordinal);
        int found = 0;
        foreach (char character in decoded) if (character is >= OpenSpoiler and <= CloseLink) found++;
        // A character reference that decodes into a marker character cannot be told apart: no marks then.
        if (found != expected) return (PlainText(html), Array.Empty<ArchiveTextMark>());
        StringBuilder text = new StringBuilder(decoded.Length);
        List<ArchiveTextMark> marks = new List<ArchiveTextMark>();
        int[] openAt = new int[3];
        foreach (char character in decoded)
        {
            switch (character)
            {
                case OpenSpoiler: openAt[0] = text.Length; break;
                case OpenCode: openAt[1] = text.Length; break;
                case OpenLink: openAt[2] = text.Length; break;
                case CloseSpoiler: marks.Add(new ArchiveTextMark(ArchiveTextMarkKind.Spoiler, openAt[0], text.Length - openAt[0])); break;
                case CloseCode: marks.Add(new ArchiveTextMark(ArchiveTextMarkKind.Code, openAt[1], text.Length - openAt[1])); break;
                case CloseLink: marks.Add(new ArchiveTextMark(ArchiveTextMarkKind.Link, openAt[2], text.Length - openAt[2])); break;
                default: text.Append(character); break;
            }
        }
        // Same trim as PlainText: the marks shift by the removed leading whitespace.
        string untrimmed = text.ToString();
        string plain = untrimmed.Trim();
        int lead = untrimmed.Length - untrimmed.TrimStart().Length;
        List<ArchiveTextMark> result = new List<ArchiveTextMark>(marks.Count);
        foreach (ArchiveTextMark mark in marks)
        {
            int start = Math.Max(0, mark.Start - lead), end = Math.Min(plain.Length, mark.Start + mark.Length - lead);
            if (end <= start) continue;
            // An anchor that carries a post reference is an internal link, not an external one.
            if (mark.Kind == ArchiveTextMarkKind.Link && plain.AsSpan(start, end - start).StartsWith(">>", StringComparison.Ordinal)) continue;
            if (result.Count == MaxMarks) break;
            result.Add(new ArchiveTextMark(mark.Kind, start, end - start));
        }
        return (plain, result.AsReadOnly());
    }

    private const int MaxMarks = 512;
    private const char OpenSpoiler = '\uE001', CloseSpoiler = '\uE002', OpenCode = '\uE003', CloseCode = '\uE004', OpenLink = '\uE005', CloseLink = '\uE006';

    private static bool MayCarryMarks(string html) =>
        html.Contains("<s>", StringComparison.OrdinalIgnoreCase) || html.Contains("<s ", StringComparison.OrdinalIgnoreCase)
        || html.Contains("spoiler", StringComparison.OrdinalIgnoreCase) || html.Contains("<pre", StringComparison.OrdinalIgnoreCase)
        || html.Contains("<code", StringComparison.OrdinalIgnoreCase) || html.Contains("<a ", StringComparison.OrdinalIgnoreCase);

    private sealed class MarkState { public readonly List<(string Name, char Close)> Open = new(); public int Expected; }

    /// <summary>Imageboard greentext: a plain-text line starting with one '&gt;'. "&gt;&gt;123" and
    /// "&gt;&gt;&gt;/board/" start a reference instead.</summary>
    internal static bool IsGreentextLine(ReadOnlySpan<char> line) => line.Length > 0 && line[0] == '>'
        && !(line.Length > 2 && line[1] == '>' && (char.IsAsciiDigit(line[2]) || line[2] == '>'));

    // A linear, quote-aware pass avoids regex timeouts on large captured posts. Escaped markup
    // is decoded afterward and stays visible text; actual script/style/comment content is omitted.
    private static string Process(string? html, bool keepTags, MarkState? marks = null)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        StringBuilder result = new StringBuilder(html.Length);
        int index = 0;
        while (index < html.Length)
        {
            if (html[index] != '<') { result.Append(html[index++]); continue; }
            if (html.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                int endComment = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = endComment < 0 ? html.Length : endComment + 3;
                continue;
            }
            int nameStart = index + 1;
            bool closing = nameStart < html.Length && html[nameStart] == '/';
            if (closing) nameStart++;
            int nameEnd = nameStart;
            while (nameEnd < html.Length && char.IsAsciiLetterOrDigit(html[nameEnd])) nameEnd++;
            if (nameEnd == nameStart || !char.IsAsciiLetter(html[nameStart])) { result.Append(html[index++]); continue; }
            int end = TagEnd(html, nameEnd);
            if (end < 0) { result.Append(html.AsSpan(index)); break; }
            ReadOnlySpan<char> name = html.AsSpan(nameStart, nameEnd - nameStart);
            if (!closing && (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                string close = "</" + name.ToString();
                int candidate = end + 1;
                while (true)
                {
                    candidate = html.IndexOf(close, candidate, StringComparison.OrdinalIgnoreCase);
                    if (candidate < 0) { index = html.Length; break; }
                    int boundary = candidate + close.Length;
                    if (boundary < html.Length && (char.IsWhiteSpace(html[boundary]) || html[boundary] == '>'))
                    {
                        int closeEnd = TagEnd(html, boundary);
                        index = closeEnd < 0 ? html.Length : closeEnd + 1;
                        break;
                    }
                    candidate = boundary;
                }
                continue;
            }
            if (marks != null) AppendMarkers(result, marks, html.AsSpan(nameStart, nameEnd - nameStart), html.AsSpan(nameEnd, end - nameEnd), closing);
            if (keepTags) result.Append(html.AsSpan(index, end - index + 1));
            else if (name.Equals("br", StringComparison.OrdinalIgnoreCase) || closing &&
                (name.Equals("p", StringComparison.OrdinalIgnoreCase) || name.Equals("div", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("blockquote", StringComparison.OrdinalIgnoreCase))) result.Append('\n');
            index = end + 1;
        }
        if (marks != null) for (int open = marks.Open.Count - 1; open >= 0; open--) result.Append(marks.Open[open].Close);
        return result.ToString();
    }

    // Spoilers (<s>, class "spoiler"), code (<pre>, <code>) and absolute http(s) anchors open a marker pair;
    // the matching close tag, or the end of the post, closes it. Markers never survive into the text.
    private static void AppendMarkers(StringBuilder result, MarkState marks, ReadOnlySpan<char> name, ReadOnlySpan<char> attributes, bool closing)
    {
        string tag = name.ToString().ToLowerInvariant();
        if (closing)
        {
            int index = marks.Open.FindLastIndex(open => open.Name == tag);
            if (index < 0) return;
            for (int open = marks.Open.Count - 1; open >= index; open--) result.Append(marks.Open[open].Close);
            marks.Open.RemoveRange(index, marks.Open.Count - index);
            return;
        }
        if (attributes.EndsWith("/")) return;
        (char Open, char Close)? pair = tag switch
        {
            "s" => (OpenSpoiler, CloseSpoiler),
            "span" when attributes.Contains("spoiler", StringComparison.OrdinalIgnoreCase) => (OpenSpoiler, CloseSpoiler),
            "pre" or "code" => (OpenCode, CloseCode),
            "a" when attributes.Contains("href=\"http", StringComparison.OrdinalIgnoreCase)
                || attributes.Contains("href='http", StringComparison.OrdinalIgnoreCase) => (OpenLink, CloseLink),
            _ => null
        };
        if (pair == null) return;
        result.Append(pair.Value.Open);
        marks.Open.Add((tag, pair.Value.Close));
        marks.Expected += 2;
    }

    private static int TagEnd(string html, int index)
    {
        char quote = '\0';
        for (; index < html.Length; index++)
        {
            char character = html[index];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (character is '\'' or '"') quote = character;
            else if (character == '>') return index;
        }
        return -1;
    }
}

internal enum ArchiveTextMarkKind { Spoiler, Code, Link }

/// <summary>A formatted range of a post's plain text (UTF-16 offsets into that text).</summary>
internal readonly record struct ArchiveTextMark(ArchiveTextMarkKind Kind, int Start, int Length);
