using System;
using System.Linq;
using MeowChan.Booru;

namespace MeowChan.Desktop.Booru;

internal enum BooruTagDraftAction { Include, Exclude, Remove }
internal enum BooruTagDraftState { Absent, Included, Excluded, Both }

/// <summary>A local edit of one provider tag. A null replacement keeps the original text, including its spacing.</summary>
internal readonly record struct BooruTagDraftEdit(
    string? Replacement = null, BooruInputIssue Issue = BooruInputIssue.None, bool UnusableTag = false);

internal static class BooruTagDraft
{
    internal static BooruTagDraftState State(DanbooruTagQuery? query, string tag)
    {
        if (query is null) return BooruTagDraftState.Absent;
        bool included = query.Terms.Any(term => !term.IsNegative && Matches(term, tag));
        bool excluded = query.Terms.Any(term => term.IsNegative && Matches(term, tag));
        if (included && excluded) return BooruTagDraftState.Both;
        if (excluded) return BooruTagDraftState.Excluded;
        return included ? BooruTagDraftState.Included : BooruTagDraftState.Absent;
    }

    internal static BooruTagDraftEdit Edit(BooruInputMode mode, string draft, string tag, BooruTagDraftAction action)
    {
        BooruInputIssue issue;
        DanbooruTagQuery? query = null;
        if (mode == BooruInputMode.Tags) query = BooruInput.ReadTagDraft(draft, out issue);
        else issue = BooruInput.Parse(mode, draft).Issue;
        if (issue != BooruInputIssue.None) return new(Issue: issue);
        if (!BooruSourceTagSearch.Plan([tag]).IsValid) return new(UnusableTag: true);

        BooruTagDraftState state = State(query, tag);
        BooruTagDraftState requested = action switch
        {
            BooruTagDraftAction.Include => BooruTagDraftState.Included,
            BooruTagDraftAction.Exclude => BooruTagDraftState.Excluded,
            _ => BooruTagDraftState.Absent
        };
        if (state == requested) return default;
        if (state == BooruTagDraftState.Absent && query?.TagTermCount == BooruSourceTagSearch.MaximumTags)
            return new(Issue: BooruInputIssue.TooManyTags);

        // Keep the tag's spelling and position when switching polarity. Leave unrelated terms intact.
        var terms = (query?.Terms ?? []).ToList();
        int index = terms.FindIndex(term => Matches(term, tag));
        if (index >= 0)
        {
            tag = Name(terms[index]);
            terms.RemoveAll(term => Matches(term, tag));
        }
        if (requested != BooruTagDraftState.Absent)
        {
            bool exclude = requested == BooruTagDraftState.Excluded;
            terms.Insert(index < 0 ? terms.Count : index, new(exclude ? "-" + tag : tag, DanbooruTagTermKind.Tag, exclude));
        }

        string replacement = string.Join(' ', terms.Select(term => term.Text));
        BooruInput.ReadTagDraft(replacement, out issue);
        return issue == BooruInputIssue.None ? new(Replacement: replacement) : new(Issue: issue);
    }

    private static bool Matches(DanbooruTagTerm term, string tag) =>
        term.Kind == DanbooruTagTermKind.Tag
        && term.Text.AsSpan(term.IsNegative ? 1 : 0).Equals(tag.AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static string Name(DanbooruTagTerm term) => term.IsNegative ? term.Text[1..] : term.Text;
}
