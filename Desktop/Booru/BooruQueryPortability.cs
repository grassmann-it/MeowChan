using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Booru;

/// <summary>Copies the tag terms of an executed query without carrying provider-specific metatags across.</summary>
internal sealed record BooruQueryPortability(string Query, string RemovedTerms)
{
    // Known metatag prefixes only: a regular tag with a colon (":3", ":d", "re:zero_kara_...") is a tag and travels along.
    private static readonly HashSet<string> Metatags = new(StringComparer.OrdinalIgnoreCase)
    {
        "rating", "order", "score", "id", "user", "md5", "pool", "fav", "source", "status", "type", "limit", "page",
        "favcount", "date", "age", "width", "height", "ratio", "mpixels", "filesize", "filetype", "parent", "child",
        "approver", "uploader", "commenter", "noter", "artcomm", "tagcount", "gentags", "arttags", "chartags", "copytags",
        "metatags", "isfav", "ordfav", "ordpool", "voted", "upvote", "downvote", "search", "downvotes", "upvotes"
    };

    internal static BooruQueryPortability ForOtherSource(string executedQuery)
    {
        List<string> tags = new();
        List<string> removed = new();
        foreach (string term in executedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsMetatag(term)) removed.Add(term);
            else tags.Add(term);
        }
        return new(string.Join(' ', tags), string.Join(' ', removed));
    }

    private static bool IsMetatag(string term)
    {
        ReadOnlySpan<char> body = term.AsSpan().TrimStart('-');
        int colon = body.IndexOf(':');
        return colon > 0 && Metatags.Contains(body[..colon].ToString());
    }
}
