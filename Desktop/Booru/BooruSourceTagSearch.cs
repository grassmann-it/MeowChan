using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Booru;

namespace MeowChan.Desktop.Booru;

internal enum BooruTagSearchProblem { None, NothingSelected, TooMany, Unusable }

/// <summary>
/// What "use as Danbooru search" would do with a set of source tags: the query that goes into the search field, or the one
/// reason it cannot. Tags are never dropped silently, shortened or reordered to make a selection fit.
/// </summary>
internal sealed record BooruTagSearchPlan(string? Query, BooruTagSearchProblem Problem, int Count = 0, string? Tag = null,
    BooruInputIssue Issue = BooruInputIssue.None)
{
    internal bool IsValid => Problem == BooruTagSearchProblem.None;
}

/// <summary>Source tags of a downloaded provider post as text for the clipboard and as a search query. Pure.</summary>
internal static class BooruSourceTagSearch
{
    /// <summary>The most tags one search takes (the parser's own limit, not a second constant).</summary>
    internal const int MaximumTags = DanbooruTagQuery.MaximumTagTerms;

    /// <summary>Tag names exactly as the provider spells them, separated by single spaces like Danbooru's own tag string.</summary>
    internal static string Written(IEnumerable<string> names) => string.Join(' ', names);

    private static bool IsPlainTag(string tag) =>
        DanbooruTagQuery.TryParse(tag, out DanbooruTagQuery? query, out _) && query is not null
        && query.Terms is [{ Kind: DanbooruTagTermKind.Tag, IsNegative: false } term] && term.Text == tag;

    /// <summary>Checks the selected tags with the host's own query parser, so the field shows what the host would send.</summary>
    internal static BooruTagSearchPlan Plan(IEnumerable<string> names)
    {
        string[] tags = names.Distinct(StringComparer.Ordinal).ToArray();
        if (tags.Length == 0) return new BooruTagSearchPlan(null, BooruTagSearchProblem.NothingSelected);
        if (tags.Length > MaximumTags) return new BooruTagSearchPlan(null, BooruTagSearchProblem.TooMany, tags.Length);
        // One tag at a time first, so a tag such as "name_(series)" is named instead of blamed on the whole selection.
        foreach (string tag in tags)
        {
            BooruParsedInput single = BooruInput.Parse(BooruInputMode.Tags, tag);
            // A source tag is always a plain tag: the parser would also accept "-tag" (an exclusion) and "rating:g" (a rating
            // term), which would change what the search means, so only a single plain, positive tag term passes.
            if (single.Kind != BooruInputKind.Tags || single.Canonical != tag || !IsPlainTag(tag))
                return new BooruTagSearchPlan(null, BooruTagSearchProblem.Unusable, tags.Length, tag,
                    single.Issue == BooruInputIssue.None ? BooruInputIssue.Characters : single.Issue);
        }
        BooruParsedInput all = BooruInput.Parse(BooruInputMode.Tags, Written(tags));
        return all.Kind == BooruInputKind.Tags && all.Canonical is { } query
            ? new BooruTagSearchPlan(query, BooruTagSearchProblem.None, tags.Length)
            : new BooruTagSearchPlan(null, BooruTagSearchProblem.Unusable, tags.Length, Written(tags),
                all.Issue == BooruInputIssue.None ? BooruInputIssue.Characters : all.Issue);
    }
}
