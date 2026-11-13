using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Desktop;

/// <summary>
/// The rules for opening an external link from a post body. A link opens only its visible text, parsed again
/// here as an absolute http or https address without user information; everything else stays plain text.
/// Nothing in this class launches anything: the page asks, and only a validated <see cref="Uri"/> is launched.
/// </summary>
internal static class ReaderExternalLink
{
    internal const int MaxLength = 2048;

    /// <summary>The visible link text as an absolute http(s) URI, or null when it is anything else.</summary>
    internal static Uri? Parse(string? visibleText)
    {
        if (visibleText == null) return null;
        string text = visibleText.Trim();
        // Unicode format characters (bidi overrides and isolates, zero-width characters, the BOM) are refused, not stripped:
        // stripping would open an address that differs from the text the reader shows, and the whole rule is that only the
        // visible text opens. A refused run simply stays plain text.
        if (text.Length == 0 || text.Length > MaxLength || text.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\'
            || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
            return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || !IsOpenable(uri)) return null;
        // The authority of the text itself must not carry user information either (Uri may normalize it away).
        int start = text.IndexOf("://", StringComparison.Ordinal) + 3;
        int end = text.IndexOfAny(['/', '?', '#'], start);
        string authority = text[start..(end < 0 ? text.Length : end)];
        return authority.Contains('@') ? null : uri;
    }

    /// <summary>Whether this is an address the reader may ever hand to the system: http or https, a host, no user information.</summary>
    internal static bool IsOpenable(Uri? uri) => uri is { IsAbsoluteUri: true, UserInfo.Length: 0 }
        && uri.Scheme is "http" or "https" && uri.Host.Length > 0
        && !uri.GetLeftPart(UriPartial.Authority).Contains('@', StringComparison.Ordinal)
        && HasAsciiHost(uri);

    /// <summary>
    /// The confirmation shows the ASCII (punycode) host, so a host that has none is no link. <see cref="Uri.IdnHost"/> throws
    /// <see cref="UriFormatException"/> for a Unicode host the IDN mapping rejects, which the IRI parser can let through.
    /// </summary>
    private static bool HasAsciiHost(Uri uri)
    {
        try { return uri.IdnHost.Length > 0 && System.Text.Ascii.IsValid(uri.IdnHost); }
        catch (UriFormatException) { return false; }
    }

    /// <summary>The address as the confirmation shows it: the host in its ASCII (punycode) form, so look-alike hosts are visible.</summary>
    internal static (string Prefix, string Host, string Tail) Display(Uri uri)
    {
        string prefix = uri.Scheme + "://";
        string rest = (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + uri.PathAndQuery + uri.Fragment;
        return (prefix, uri.IdnHost, rest);
    }

    /// <summary>
    /// The link a click on this segment opens. Never while a spoiler over it, or inside the same link, is still hidden (the
    /// click reveals it) and never while text is selected (a drag that selected text is not a click).
    /// </summary>
    internal static Uri? ForActivation(ReaderBodySegment? segment, bool spoilersRevealed, bool selectionActive)
        => segment is { Link: { } link } && !selectionActive && (!segment.LinkNeedsReveal || spoilersRevealed) && IsOpenable(link) ? link : null;

    /// <summary>
    /// The link Enter opens on a focused body: the first link inside the selection, or the only link of the body when
    /// nothing is selected. With several links and no selection nothing opens.
    /// </summary>
    internal static Uri? ForKeyboard(ReaderBodyDocument? document, int selectionStart, int selectionEnd, bool spoilersRevealed)
    {
        if (document == null) return null;
        int from = Math.Min(selectionStart, selectionEnd), to = Math.Max(selectionStart, selectionEnd);
        IEnumerable<ReaderBodySegment> links = document.Segments.Where(segment => segment.Link != null && (!segment.LinkNeedsReveal || spoilersRevealed));
        if (to > from)
            return links.FirstOrDefault(segment => segment.Start < to && segment.End > from)?.Link is { } inSelection && IsOpenable(inSelection) ? inSelection : null;
        Uri[] all = links.Select(segment => segment.Link!).Distinct().ToArray();
        return all.Length == 1 && IsOpenable(all[0]) ? all[0] : null;
    }
}
