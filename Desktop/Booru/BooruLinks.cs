using System;
using System.Threading.Tasks;
using MeowChan.Booru;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The only URL the Danbooru window ever opens or copies: the canonical post page <c>https://danbooru.donmai.us/posts/&lt;id&gt;</c>,
/// built from a validated post id. A URL that the provider sent with a post (<see cref="BooruPostView.PostUrl"/>) is displayed
/// but never used here, so provider data cannot choose where the browser goes.
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
/// What the window may do with a canonical post link. Injected through <see cref="BooruWorkbenchServices"/> so tests and
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
