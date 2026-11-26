using System;
using System.Threading.Tasks;
using MeowChan.Booru;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Builds canonical Danbooru post pages from validated post ids. A URL the provider sent with a
/// post (<see cref="BooruPostView.PostUrl"/>) cannot choose where the browser goes.
/// </summary>
internal static class BooruPostLinks
{
    /// <summary>The canonical page of a post of this provider, or false for another source or an invalid id.</summary>
    internal static bool TryCanonical(BooruPostView? post, out string url)
    {
        url = string.Empty;
        return post is not null
            && string.Equals(post.SourceId, DanbooruPostReference.ProviderId, StringComparison.Ordinal)
            && TryCanonical(post.PostId, out url);
    }

    internal static bool TryCanonical(string? postId, out string url)
    {
        url = string.Empty;
        if (!DanbooruPostReference.TryCreateFromId(postId, out DanbooruPostReference? reference, out _) || reference is null)
            return false;
        url = reference.CanonicalUrl.AbsoluteUri;
        return true;
    }
}

/// <summary>
/// What the window may do with a canonical Danbooru link. Injected through <see cref="BooruWorkbenchServices"/> so tests and
/// smokes never start a browser: <see cref="Open"/> hands the URL to the app's external launcher by default, and
/// <see cref="Copy"/> is null unless a test replaces it (the window then uses its own top-level clipboard).
/// Both run only on an explicit click.
/// </summary>
internal sealed class BooruLinkActions
{
    internal BooruLinkActions(Func<string, Task> open, Func<string, Task>? copy = null)
    {
        Open = open ?? throw new ArgumentNullException(nameof(open));
        Copy = copy;
    }

    internal Func<string, Task> Open { get; }
    internal Func<string, Task>? Copy { get; }

    /// <summary>The app's shared external launcher (default browser).</summary>
    internal static BooruLinkActions System { get; } = new(ExternalLauncher.OpenAsync);
}
