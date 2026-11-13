using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MeowChan.Booru;

internal enum DanbooruAddressKind
{
    /// <summary>A single post, <c>/posts/&lt;id&gt;</c>.</summary>
    Post,
    /// <summary>The post list with a non-empty <c>tags</c> parameter.</summary>
    Search,
    /// <summary>The site root or the post list without tags.</summary>
    Site
}

/// <summary>
/// What a user-pasted Danbooru address means: a post (one-shot), a search or the bare site (both only prefill the
/// Danbooru window). Pure: it never requests anything and never keeps more of the address than the post id or the
/// tag text. <c>safebooru.donmai.us</c> is the same server in safe mode, so it maps to the same identity.
/// The tag text is a user query: do not log it (see <see cref="DanbooruTagQuery.ProviderQuery"/>).
/// </summary>
internal sealed record DanbooruAddress(DanbooruAddressKind Kind, string? PostId, string Tags, bool IsSafebooru)
{
    /// <summary>The visible, removable term a Safebooru search address adds to the search field.</summary>
    internal const string SafeRatingTerm = "rating:g";

    private const int MaximumTagTextLength = DanbooruTagQuery.MaximumInputUtf16CodeUnits;

    // A booru address inside free text: https://danbooru.donmai.us or https://safebooru.donmai.us, at the start of a
    // token, running to whitespace, but ending before a comma that introduces the next URL. Commas inside the address
    // (an unencoded "rating:g,s") belong to it.
    private static readonly Regex SpanPattern = new(
        @"(?<=^|[\s,])https?://(?:danbooru|safebooru)\.donmai\.us(?::\d+)?(?=[/?#,\s]|$)(?:[/?#](?:(?!,\s*https?://)\S)*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Any mention of a Danbooru host, wherever it sits: inside <...>, (...) or quotes, after punctuation, with a trailing
    // dot or user info. Looser than SpanPattern on purpose: redaction may over-match, splitting must not.
    private static readonly Regex HostMention = new(
        @"(?:danbooru|safebooru)\.donmai\.us", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the text names one of the Danbooru hosts; the whole text then counts as sensitive for logging.</summary>
    internal static bool MentionsKnownHost(string? text) => text is not null && HostMention.IsMatch(text);

    /// <summary>
    /// The address spans of a line, trailing commas trimmed, in order. Entry splitting uses them so a comma inside an
    /// address does not cut it. A space does not end an address either unless another URL follows: an unencoded
    /// <c>tags=blue_sky cat</c> stays one entry, as it did when only commas and line breaks split. Words after a
    /// comma start the next entry.
    /// </summary>
    internal static IEnumerable<(int Start, int Length)> FindSpans(string text)
    {
        int floor = 0;
        foreach (Match match in SpanPattern.Matches(text))
        {
            if (match.Index < floor) continue;
            int end = match.Index + match.Length;
            if (text[end - 1] != ',') end = ExtendOverSpaces(text, end);
            while (end > match.Index && text[end - 1] == ',') end--;
            floor = end;
            if (end > match.Index) yield return (match.Index, end - match.Index);
        }
    }

    // Appends the words that follow the address after blanks, up to a comma (a new entry), a line break or the next URL.
    private static int ExtendOverSpaces(string text, int end)
    {
        while (true)
        {
            int word = end;
            while (word < text.Length && text[word] != '\r' && text[word] != '\n' && char.IsWhiteSpace(text[word])) word++;
            if (word == end || word >= text.Length || text[word] == ',' || StartsUrl(text, word)) return end;
            int stop = word;
            while (stop < text.Length && !char.IsWhiteSpace(text[stop]) && !CommaBeforeUrl(text, stop)) stop++;
            if (stop == word) return end;
            end = stop;
            if (text[end - 1] == ',') return end;
        }
    }

    private static bool StartsUrl(string text, int index) =>
        string.Compare(text, index, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0
        || string.Compare(text, index, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool CommaBeforeUrl(string text, int index)
    {
        if (text[index] != ',') return false;
        int next = index + 1;
        while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        return StartsUrl(text, next);
    }

    /// <summary>
    /// Text that is safe to write to a log: any text that mentions a Danbooru host (the address may carry a private query)
    /// is replaced by a fixed marker; anything else is returned unchanged.
    /// </summary>
    internal static string RedactForLog(string? text) =>
        text is null ? string.Empty : MentionsKnownHost(text) ? "[Danbooru address]" : text;

    internal static bool TryParse(string? text, out DanbooruAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4 * MaximumTagTextLength) return false;
        string value = text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
        bool safebooru = uri.Host.Equals(DanbooruPostReference.SafebooruHost, StringComparison.OrdinalIgnoreCase);
        if (!safebooru && !uri.Host.Equals(DanbooruPostReference.MetadataHost, StringComparison.OrdinalIgnoreCase)) return false;

        string path = uri.AbsolutePath;
        if (path.Length > 1 && path.EndsWith('/')) path = path[..^1];
        if (path == "/") { address = new(DanbooruAddressKind.Site, null, string.Empty, safebooru); return true; }

        if (path == "/posts")
        {
            if (!TryReadTags(uri.Query, out string tags)) return false;
            address = new(tags.Length == 0 ? DanbooruAddressKind.Site : DanbooruAddressKind.Search, null, tags, safebooru);
            return true;
        }

        // A post link keeps no query or fragment: Danbooru adds "?q=<tags>" when it links from a search.
        if (path.StartsWith("/posts/", StringComparison.Ordinal)
            && DanbooruPostReference.TryParseUserInput("https://" + DanbooruPostReference.MetadataHost + path,
                out DanbooruPostReference? reference, out _) && reference is not null)
        {
            address = new(DanbooruAddressKind.Post, reference.PostId, string.Empty, safebooru);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The text for the Danbooru search field. A Safebooru address adds the visible <see cref="SafeRatingTerm"/> unless the
    /// tags already carry a rating term; the user can delete it in the field. Nothing else is added or removed.
    /// </summary>
    internal string PrefilledQuery(out bool addedSafeRating)
    {
        addedSafeRating = false;
        string query = string.Join(' ', Tags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!IsSafebooru || Kind == DanbooruAddressKind.Post) return query;
        foreach (string term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (term.StartsWith("rating:", StringComparison.OrdinalIgnoreCase)
                || term.StartsWith("-rating:", StringComparison.OrdinalIgnoreCase)) return query;
        addedSafeRating = true;
        return query.Length == 0 ? SafeRatingTerm : query + " " + SafeRatingTerm;
    }

    // The first "tags" parameter, '+' and %xx decoded as a form value. A control character or an over-long value
    // is refused; parameters other than "tags" (page, utf8, ...) are ignored.
    private static bool TryReadTags(string query, out string tags)
    {
        tags = string.Empty;
        if (query.Length <= 1) return true;
        foreach (string part in query[1..].Split('&'))
        {
            int equals = part.IndexOf('=');
            string name = equals < 0 ? part : part[..equals];
            if (!name.Equals("tags", StringComparison.Ordinal)) continue;
            string raw = equals < 0 ? string.Empty : part[(equals + 1)..];
            if (raw.Length > 3 * MaximumTagTextLength) return false;
            string value;
            try { value = Uri.UnescapeDataString(raw.Replace('+', ' ')); }
            catch (UriFormatException) { return false; }
            value = value.Trim();
            if (value.Length > MaximumTagTextLength) return false;
            foreach (char character in value)
                if (char.IsControl(character)) return false;
            tags = value;
            return true;
        }
        return true;
    }
}
