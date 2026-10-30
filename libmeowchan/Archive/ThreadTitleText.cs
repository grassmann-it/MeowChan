using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MeowChan.Archive;

/// <summary>Display-only title derivation; never write its result as a provider subject.</summary>
internal static class ThreadTitleText
{
    internal const int MaximumTextElements = 160;

    // Position zero is not proof of an OP: partial responses can start with a reply.
    // The current source contract has no separate OP identifier. Only use an exact
    // thread/post identity match; catalogs separately supply explicit OpeningText.
    internal static CapturedPost? OpeningPost(ResourceId id, IEnumerable<CapturedPost> posts) => posts
        .FirstOrDefault(post => post.PostId == id.Thread && post.Fields.HasFlag(PostFieldPresence.Html));

    internal static string? OpeningHtml(ResourceId id, IEnumerable<CapturedPost> posts) => OpeningPost(id, posts)?.Html;

    internal static string FromPlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        StringBuilder line = new();
        bool space = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { space = line.Length > 0; continue; }
            if (Rune.IsControl(rune) || rune.Value is 0x200E or 0x200F or >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)
                continue;
            if (space) { line.Append(' '); space = false; }
            line.Append(rune.ToString());
        }
        string normalized = line.ToString();
        int[] elements = StringInfo.ParseCombiningCharacters(normalized);
        return elements.Length <= MaximumTextElements ? normalized
            : normalized[..elements[MaximumTextElements]].TrimEnd() + "…";
    }
}
