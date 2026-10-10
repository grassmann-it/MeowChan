using System;
using System.Net;
using System.Text;

namespace oChan.Archive;

/// <summary>Decodes source HTML as inert readable text. No browser, provider or network is involved.</summary>
public static class ArchiveText
{
    public static string PlainText(string? html) => WebUtility.HtmlDecode(Process(html, false))
        .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    internal static string WithoutActiveContent(string? html) => Process(html, true);

    // A linear, quote-aware pass avoids regex timeouts on large captured posts. Escaped markup
    // is decoded afterward and stays visible text; actual script/style/comment content is omitted.
    private static string Process(string? html, bool keepTags)
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
            if (keepTags) result.Append(html.AsSpan(index, end - index + 1));
            else if (name.Equals("br", StringComparison.OrdinalIgnoreCase) || closing &&
                (name.Equals("p", StringComparison.OrdinalIgnoreCase) || name.Equals("div", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("blockquote", StringComparison.OrdinalIgnoreCase))) result.Append('\n');
            index = end + 1;
        }
        return result.ToString();
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
