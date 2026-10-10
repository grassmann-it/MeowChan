using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace oChan.Archive;

public sealed record ArchivePostId(ResourceId Thread, string PostId);
/// <summary>A source reference. A target identity does not assert that its post was captured.</summary>
public sealed record ArchiveQuote(ArchivePostId Origin, ArchivePostId? Target, string Label, string? Url)
{
    public bool IsExternal { get; init; }
}
public enum ArchiveQuoteState { Captured, MissingPost, MissingThread, External, Unresolved }
public sealed record ArchiveQuoteResolution(ArchiveQuote Quote, ArchiveQuoteState State,
    ArchiveThreadSummary? Thread = null, CapturedPost? Post = null, int? PostIndex = null)
{
    /// <summary>The same immutable read that supplied PostIndex; subsequent captures cannot shift its pages.</summary>
    public ArchivedThreadView? View { get; init; }
}
public sealed record ArchiveQuotePage(IReadOnlyList<ArchiveQuote> Items, int TotalCount, int Offset);

/// <summary>Recognizes source quote anchors from the supported 4chan/8kun formats and same-thread fragments.</summary>
public static class ArchiveQuoteParser
{
    private static readonly Regex Anchors = new Regex("""<a\b(?<attributes>(?:[^>"']|"[^"]*"|'[^']*')*)>(?<label>[\s\S]*?)</a\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(2));
    private static readonly Regex ThreadPath = new Regex(@"^/(?<board>[^/]+)/(?:thread/(?<thread>[0-9]+)(?:/[^/]*)?|res/(?<thread>[0-9]+)\.html)/?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<ArchiveQuote> Parse(ResourceId thread, string threadUrl, CapturedPost post)
    {
        ArchiveValidation.ValidateId(thread);
        ArgumentNullException.ThrowIfNull(post);
        if (!Uri.TryCreate(threadUrl, UriKind.Absolute, out Uri? source)
            || source.Scheme is not ("http" or "https")) throw new ArgumentException("A thread URL must use HTTP(S).", nameof(threadUrl));
        ArchivePostId origin = new ArchivePostId(thread, post.PostId);
        List<ArchiveQuote> result = new List<ArchiveQuote>();
        foreach (Match anchor in Anchors.Matches(ArchiveText.WithoutActiveContent(post.Html)))
        {
            string? attribute = HrefAttribute(anchor.Groups["attributes"].Value);
            if (attribute == null) continue;
            string href = WebUtility.HtmlDecode(attribute).Trim();
            string label = ArchiveText.PlainText(anchor.Groups["label"].Value);
            if (!label.StartsWith(">>", StringComparison.Ordinal)) continue;
            Uri? url = Uri.TryCreate(source, href, out Uri? resolved) && resolved.Scheme is "http" or "https" ? resolved : null;
            ArchivePostId? target = null;
            if (url != null && url.Fragment.Length > 1)
            {
                string postId = Uri.UnescapeDataString(url.Fragment[1..]);
                if (thread.Source is "4chan" or "8kun" && postId.StartsWith('p') && postId.Length > 1 && postId[1..].All(char.IsAsciiDigit)) postId = postId[1..];
                bool validPost = postId.Length is > 0 and <= 256 && !postId.Any(char.IsControl);
                bool samePage = url.Host.Equals(source.Host, StringComparison.OrdinalIgnoreCase) && url.AbsolutePath == source.AbsolutePath;
                if (validPost && samePage) target = new ArchivePostId(thread, postId);
                else if (validPost && IsSourceHost(thread.Source, url.Host, source.Host))
                {
                    Match path = ThreadPath.Match(url.AbsolutePath);
                    if (path.Success) target = new ArchivePostId(new ResourceId(thread.Source,
                        Uri.UnescapeDataString(path.Groups["board"].Value), path.Groups["thread"].Value), postId);
                }
            }
            if (target != null)
            {
                try { ArchiveValidation.ValidateId(target.Thread); }
                catch (ArgumentException) { target = null; }
            }
            result.Add(new ArchiveQuote(origin, target, label, url?.AbsoluteUri)
            { IsExternal = url != null && !IsSourceHost(thread.Source, url.Host, source.Host) });
        }
        return Array.AsReadOnly(result.Distinct().ToArray());
    }

    private static string? HrefAttribute(string attributes)
    {
        int index = 0;
        while (index < attributes.Length)
        {
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index])) index++;
            int nameStart = index;
            while (index < attributes.Length && !char.IsWhiteSpace(attributes[index]) && attributes[index] is not ('=' or '/')) index++;
            string name = attributes[nameStart..index];
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index])) index++;
            if (index >= attributes.Length || attributes[index] != '=') { if (index == nameStart) index++; continue; }
            index++;
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index])) index++;
            char quote = index < attributes.Length && attributes[index] is '\'' or '"' ? attributes[index++] : '\0';
            int valueStart = index;
            while (index < attributes.Length && (quote != '\0' ? attributes[index] != quote : !char.IsWhiteSpace(attributes[index]))) index++;
            string value = attributes[valueStart..index];
            if (index < attributes.Length && quote != '\0') index++;
            if (name.Equals("href", StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    private static bool IsSourceHost(string source, string host, string currentHost) => host.Equals(currentHost, StringComparison.OrdinalIgnoreCase) || source switch
    {
        "4chan" => host is "boards.4chan.org" or "boards.4channel.org",
        "8kun" => host is "8kun.top" or "www.8kun.top",
        _ => false
    };
}
