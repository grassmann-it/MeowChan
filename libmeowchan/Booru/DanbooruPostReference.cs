using System;

namespace MeowChan.Booru;

internal enum DanbooruPostReferenceError
{
    None,
    Empty,
    InvalidId,
    InvalidUrl
}

/// <summary>Strict, provider-specific identity for a Danbooru post.</summary>
internal sealed record DanbooruPostReference(string PostId, Uri CanonicalUrl)
{
    internal const string ProviderId = "danbooru";
    internal const string MetadataHost = "danbooru.donmai.us";
    internal const int MaximumPostIdLength = 18;

    internal static bool TryParse(string? input, out DanbooruPostReference? reference,
        out DanbooruPostReferenceError error)
    {
        reference = null;
        error = DanbooruPostReferenceError.None;
        if (input is null) return Fail(DanbooruPostReferenceError.Empty, out error);

        string value = TrimAsciiSeparators(input);
        if (value.Length == 0) return Fail(DanbooruPostReferenceError.Empty, out error);
        if (IsAsciiDigits(value)) return TryCreateFromId(value, out reference, out error);

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(MetadataHost, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Port != 443 && !uri.IsDefaultPort))
            return Fail(DanbooruPostReferenceError.InvalidUrl, out error);

        string path = uri.AbsolutePath;
        if (path.EndsWith("//", StringComparison.Ordinal))
            return Fail(DanbooruPostReferenceError.InvalidUrl, out error);
        if (path.EndsWith("/", StringComparison.Ordinal)) path = path[..^1];
        string[] segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length != 3 || segments[0].Length != 0
            || !segments[1].Equals("posts", StringComparison.Ordinal))
            return Fail(DanbooruPostReferenceError.InvalidUrl, out error);

        string postSegment = segments[2];
        if (postSegment.Length == 0)
            return Fail(DanbooruPostReferenceError.InvalidUrl, out error);

        string postId = postSegment.EndsWith(".json", StringComparison.Ordinal)
            ? postSegment[..^5]
            : postSegment;
        if (!TryCreateFromId(postId, out reference, out DanbooruPostReferenceError idError))
            return Fail(DanbooruPostReferenceError.InvalidUrl, out error);
        error = idError;
        return true;
    }

    internal static bool TryCreateFromId(string? postId, out DanbooruPostReference? reference,
        out DanbooruPostReferenceError error)
    {
        reference = null;
        error = DanbooruPostReferenceError.None;
        if (postId is null || !IsValidPostId(postId))
            return Fail(DanbooruPostReferenceError.InvalidId, out error);

        reference = new DanbooruPostReference(postId,
            new Uri($"https://{MetadataHost}/posts/{postId}", UriKind.Absolute));
        return true;
    }

    internal static bool IsValidPostId(string? postId) => postId is { Length: > 0 and <= MaximumPostIdLength }
        && postId[0] != '0'
        && IsAsciiDigits(postId);

    /// <summary>Numeric comparison without an Int64 overflow or culture-dependent parsing.</summary>
    internal static int ComparePostIds(string left, string right)
    {
        if (!IsValidPostId(left) || !IsValidPostId(right))
            throw new ArgumentException("Both post IDs must be valid positive decimal IDs.");
        int lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }

    private static bool IsAsciiDigits(string value)
    {
        foreach (char character in value)
            if (character is < '0' or > '9') return false;
        return value.Length > 0;
    }

    private static string TrimAsciiSeparators(string value) => value.Trim(' ', '\t', '\r', '\n');

    private static bool Fail(DanbooruPostReferenceError value, out DanbooruPostReferenceError error)
    {
        error = value;
        return false;
    }
}
