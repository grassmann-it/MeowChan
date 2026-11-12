using MeowChan.Booru;

namespace MeowChan.Desktop.Booru;

internal enum BooruInputKind { Empty, Tags, Post, Invalid }

/// <summary>Why an input was refused, mapped one-to-one to a localized sentence. Never echoes the input.</summary>
internal enum BooruInputIssue
{
    None,
    TooManyTags,
    TooManyTerms,
    Unsupported,
    MetaTag,
    Rating,
    TooLong,
    Characters,
    Negative,
    PostId,
    PostLink
}

/// <summary>Result of reading the search field. Pure; it never requests anything.</summary>
internal sealed record BooruParsedInput(BooruInputKind Kind, string? Canonical = null, string? PostId = null,
    BooruInputIssue Issue = BooruInputIssue.None)
{
    internal bool IsValid => Kind is BooruInputKind.Tags or BooruInputKind.Post;
}

/// <summary>
/// Reads the search field with the host's own parsers, so what the UI shows as the query is exactly
/// what the host would send. No term (in particular no rating term) is ever added or removed.
/// </summary>
internal static class BooruInput
{
    internal static BooruParsedInput Parse(BooruInputMode mode, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new BooruParsedInput(BooruInputKind.Empty);
        // A full link can never be a tag query (':' and '/' are outside the tag subset), so a pasted
        // post link is recognized in either mode instead of failing with a tag error.
        if (mode == BooruInputMode.Post || text.Contains("://", System.StringComparison.Ordinal))
            return ParsePost(text);
        return ParseTags(text);
    }

    private static BooruParsedInput ParsePost(string text)
    {
        if (DanbooruPostReference.TryParse(text, out DanbooruPostReference? reference, out DanbooruPostReferenceError error)
            && reference is not null)
            return new BooruParsedInput(BooruInputKind.Post, PostId: reference.PostId);
        BooruInputIssue issue = error == DanbooruPostReferenceError.InvalidId ? BooruInputIssue.PostId : BooruInputIssue.PostLink;
        return new BooruParsedInput(BooruInputKind.Invalid, Issue: issue);
    }

    private static BooruParsedInput ParseTags(string text)
    {
        if (DanbooruTagQuery.TryParse(text, out DanbooruTagQuery? query, out DanbooruTagQueryError error) && query is not null)
            return new BooruParsedInput(BooruInputKind.Tags, query.ProviderQuery);
        return new BooruParsedInput(BooruInputKind.Invalid, Issue: error switch
        {
            DanbooruTagQueryError.TooManyTagTerms => BooruInputIssue.TooManyTags,
            DanbooruTagQueryError.TooManyTerms => BooruInputIssue.TooManyTerms,
            DanbooruTagQueryError.UnsupportedExpression => BooruInputIssue.Unsupported,
            DanbooruTagQueryError.UnsupportedMetaTag => BooruInputIssue.MetaTag,
            DanbooruTagQueryError.InvalidRating or DanbooruTagQueryError.DuplicateRating => BooruInputIssue.Rating,
            DanbooruTagQueryError.InputTooLong or DanbooruTagQueryError.TermTooLong => BooruInputIssue.TooLong,
            DanbooruTagQueryError.InvalidNegativeTerm => BooruInputIssue.Negative,
            _ => BooruInputIssue.Characters
        });
    }
}
