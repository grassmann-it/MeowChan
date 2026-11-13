using System;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Desktop;

/// <summary>What the reader's post menu copies. Local text only; the link exists only where the source's address scheme is known.</summary>
internal static class PostMenuText
{
    /// <summary>The post as it is quoted in the sources: <c>&gt;&gt;123</c>.</summary>
    internal static string Number(string postId) => ">>" + postId;

    /// <summary>The visible text of the post, preceded by its subject when it has one.</summary>
    internal static string Text(ArchivePostRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.IsNullOrWhiteSpace(row.Subject) ? row.Body : row.Subject.Trim() + Environment.NewLine + row.Body;
    }

    /// <summary>
    /// The address of one post, or null when it cannot be built. Only 4chan has a known scheme here: the thread
    /// address (without query or fragment) plus <c>#p&lt;number&gt;</c>.
    /// </summary>
    internal static string? Link(ResourceId thread, string? threadUrl, string postId)
    {
        if (thread.Source != "4chan" || string.IsNullOrEmpty(postId) || !postId.All(char.IsAsciiDigit)
            || !Uri.TryCreate(threadUrl, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https")
            || url.Host is not ("boards.4chan.org" or "boards.4channel.org")) return null;
        return new UriBuilder(url) { Query = string.Empty, Fragment = "p" + postId }.Uri.AbsoluteUri;
    }
}
