using System;
using System.Linq;
using System.Text.RegularExpressions;
using MeowChan.Booru;

namespace MeowChan.Desktop.Booru;

internal static partial class BooruRatingDraft
{
    internal const string Codes = "gsqe";
    private const string Separators = " \t\r\n";

    internal static string Selected(string draft) => Selected(RatingTerm().Match(draft));

    internal static string Toggle(string draft, char rating)
    {
        Match term = RatingTerm().Match(draft);
        string current = Selected(term);
        char[] selected = Codes.Where(code => current.Contains(code) != (code == rating)).ToArray();
        if (selected.Length == 0)
        {
            int start = draft.AsSpan(0, term.Index).TrimEnd(Separators.AsSpan()).Length;
            int end = term.Index + term.Length;
            if (start == 0) end = draft.Length - draft.AsSpan(end).TrimStart(Separators.AsSpan()).Length;
            return draft.Remove(start, end - start);
        }

        string replacement = "rating:" + string.Join(",", selected);
        if (term.Success) return draft[..term.Index] + replacement + draft[(term.Index + term.Length)..];
        string separator = draft.Length == 0 || Separators.Contains(draft[^1]) ? string.Empty : " ";
        return draft + separator + replacement;
    }

    private static string Selected(Match term)
    {
        if (!term.Success || !DanbooruTagQuery.TryParse(term.Value, out DanbooruTagQuery? query, out _) || query is null)
            return string.Empty;
        DanbooruTagTerm parsed = query.Terms[0];
        string[] values = parsed.Text[(parsed.Text.IndexOf(':', StringComparison.Ordinal) + 1)..].Split(',');
        return new string(Codes.Where(code => values.Any(value => value[0] == code) != parsed.IsNegative).ToArray());
    }

    // Read the rating while neighboring terms are still being edited. The provider parser validates its value.
    [GeneratedRegex(@"(?<![^ \t\r\n])-?rating:[^ \t\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex RatingTerm();
}
