using System;
using System.IO;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Booru;

internal enum BooruOriginalSelectionError
{
    None,
    MissingOriginal,
    OriginalUrlMissing,
    OriginalUrlInvalid,
    InvalidSize,
    InvalidMd5,
    InvalidExtension,
    /// <summary>Queue admission requires the provider's exact size and MD5 so damaged bytes fail closed.</summary>
    MissingIntegrityClaims,
    /// <summary>The original URL is not on the declared provider media host.</summary>
    MediaHostNotAllowed
}

/// <summary>Validated immutable input for one original-variant queue admission.</summary>
internal sealed record BooruOriginalDownloadPlan(
    BooruMediaKey Identity,
    Uri? PostUrl,
    Uri DownloadUri,
    string Extension,
    long? ExpectedSize,
    string? ExpectedMd5)
{
    internal const string Variant = "original";
    internal string? ExpectedMimeType => MediaMimeValidation.FromExtension(Extension);
    internal string SourceId => Identity.Post.SourceId;
    internal string PostId => Identity.Post.PostId;
    internal string MediaIdentifier => Identity.StorageId;
}

/// <summary>
/// Selects only the provider's explicit original media claim. Sample and preview claims are never
/// silently promoted to an original download. Queue ownership and local publication happen later.
/// </summary>
internal static class BooruOriginalDownloadSelector
{
    internal static bool TrySelect(BooruPostClaim post, out BooruOriginalDownloadPlan? plan,
        out BooruOriginalSelectionError error)
    {
        ArgumentNullException.ThrowIfNull(post);
        plan = null;
        error = BooruOriginalSelectionError.None;
        if (!post.Identity.IsValid)
            throw new ArgumentException("The Booru post identity is invalid.", nameof(post));

        BooruPostClaimMedia? original = post.Media.FirstOrDefault(media => media.Role == BooruPostClaimMediaRole.Original);
        if (original is null)
        {
            error = BooruOriginalSelectionError.MissingOriginal;
            return false;
        }
        if (original.Url is null)
        {
            error = BooruOriginalSelectionError.OriginalUrlMissing;
            return false;
        }
        if (!Uri.TryCreate(original.Url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || !uri.IsDefaultPort || uri.Fragment.Length != 0)
        {
            error = BooruOriginalSelectionError.OriginalUrlInvalid;
            return false;
        }
        if (original.ExpectedSize is < 0)
        {
            error = BooruOriginalSelectionError.InvalidSize;
            return false;
        }
        if (original.ExpectedMd5 is not null)
        {
            Span<byte> digest = stackalloc byte[16];
            if (!Convert.TryFromBase64String(original.ExpectedMd5, digest, out int written)
                || written != digest.Length)
            {
                error = BooruOriginalSelectionError.InvalidMd5;
                return false;
            }
        }

        string? extension = original.Extension ?? Path.GetExtension(uri.AbsolutePath);
        if (extension is null or { Length: < 2 or > 33 }
            || extension[0] != '.'
            || extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            // A provider may omit a filename extension. A bounded opaque extension is safe and
            // keeps the original claim selectable without trusting path text as a filename.
            if (original.Extension is not null)
            {
                error = BooruOriginalSelectionError.InvalidExtension;
                return false;
            }
            extension = ".bin";
        }

        plan = new BooruOriginalDownloadPlan(BooruMediaKey.Original(post.Identity), post.Identity.CanonicalDanbooruUrl
            ?? (post.PostUrl is { } postUrl ? new Uri(postUrl, UriKind.Absolute) : null), uri, extension.ToLowerInvariant(),
            original.ExpectedSize, original.ExpectedMd5);
        return true;
    }
}
