using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MeowChan.Booru;

/// <summary>Constant diagnostics: never echo potentially private rejected input.</summary>
internal enum DanbooruTagQueryError
{
    None,
    NullInput,
    Empty,
    InputTooLong,
    InvalidUnicode,
    InvalidCharacter,
    TooManyTerms,
    TermTooLong,
    InvalidNegativeTerm,
    UnsupportedExpression,
    UnsupportedMetaTag,
    InvalidRating,
    DuplicateRating,
    TooManyTagTerms
}

internal enum DanbooruTagTermKind { Tag, Rating }

/// <summary>Exact source spelling, including the exclusion operator if supplied.</summary>
internal sealed record DanbooruTagTerm(string Text, DanbooruTagTermKind Kind, bool IsNegative);

/// <summary>
/// Pure, deliberately restricted public Danbooru query syntax, not a universal booru DSL
/// and not a grant to make requests. Accepts up to two literal tag occurrences (positive
/// OR negative) and one optional rating: term. Rating short/long names and comma lists
/// retain their exact spelling and polarity. Unsupported syntax is rejected in full.
///
/// Basis: gp20-danbooru-pilot-handoff-sol-02.md §2.3 and danbooru/REVIEW.md §1;
/// pinned upstream post_query.rb UNLIMITED_METATAGS and term_count show that rating
/// is free, but exclusions are not. Occurrence counting is intentionally conservative:
/// duplicates consume this local limit even if the provider would count unique tags.
/// No aliases, casing, Unicode normalization, sorting, implicit rating or order terms.
/// Fixed ID-descending traversal belongs to the future adapter, not this value.
/// </summary>
internal sealed class DanbooruTagQuery
{
    // Local parsing budgets, not an assertion of live API acceptance or closed G1 gates.
    internal const int MaximumQueryUtf8Bytes = 1024;
    internal const int MaximumInputUtf16CodeUnits = MaximumQueryUtf8Bytes;
    internal const int MaximumTermUtf8Bytes = 256;
    internal const int MaximumTagTerms = 2;
    internal const int MaximumTerms = MaximumTagTerms + 1;

    private DanbooruTagQuery(DanbooruTagTerm[] terms, string providerQuery, int tagTermCount)
    {
        Terms = Array.AsReadOnly(terms);
        ProviderQuery = providerQuery;
        TagTermCount = tagTermCount;
    }

    internal IReadOnlyList<DanbooruTagTerm> Terms { get; }

    /// <summary>
    /// Separators only are normalized to ASCII space. Not URL-encoded; any future
    /// transport must encode the parameter exactly once. Do not log this value.
    /// </summary>
    internal string ProviderQuery { get; }
    internal int TagTermCount { get; }

    /// <summary>
    /// Accepts only explicitly supplied provider-query text, never private tag sets,
    /// sidecars, favorites or history. This parser cannot infer a string's provenance:
    /// the caller must keep local/private suggestions out of this input. Valid syntax
    /// is NOT consent to disclose a query or proof that it contains no private text.
    /// </summary>
    internal static bool TryParse(string? explicitSourceQuery, out DanbooruTagQuery? query,
        out DanbooruTagQueryError error)
    {
        query = null;
        error = DanbooruTagQueryError.None;
        if (explicitSourceQuery is null) return Fail(DanbooruTagQueryError.NullInput, out error);
        // O(1) guard before even validating Unicode or allocating term buffers.
        if (explicitSourceQuery.Length > MaximumInputUtf16CodeUnits)
            return Fail(DanbooruTagQueryError.InputTooLong, out error);
        if (!Inspect(explicitSourceQuery, out int utf8Bytes, out error)) return false;
        if (utf8Bytes > MaximumQueryUtf8Bytes) return Fail(DanbooruTagQueryError.InputTooLong, out error);

        List<DanbooruTagTerm> terms = new(MaximumTerms);
        int tagCount = 0;
        bool hasRating = false;
        for (int offset = 0; offset < explicitSourceQuery.Length;)
        {
            if (IsSeparator(explicitSourceQuery[offset])) { offset++; continue; }
            if (terms.Count == MaximumTerms) return Fail(DanbooruTagQueryError.TooManyTerms, out error);
            int start = offset;
            while (offset < explicitSourceQuery.Length && !IsSeparator(explicitSourceQuery[offset])) offset++;
            string text = explicitSourceQuery[start..offset];
            // Inspect already established strict Unicode; no replacement fallback here.
            if (Encoding.UTF8.GetByteCount(text) > MaximumTermUtf8Bytes)
                return Fail(DanbooruTagQueryError.TermTooLong, out error);
            if (!TryTerm(text, out DanbooruTagTerm? term, out error)) return false;
            if (term!.Kind == DanbooruTagTermKind.Rating)
            {
                if (hasRating) return Fail(DanbooruTagQueryError.DuplicateRating, out error);
                hasRating = true;
            }
            else if (++tagCount > MaximumTagTerms)
                return Fail(DanbooruTagQueryError.TooManyTagTerms, out error);
            terms.Add(term);
        }
        if (terms.Count == 0) return Fail(DanbooruTagQueryError.Empty, out error);

        DanbooruTagTerm[] owned = terms.ToArray();
        string[] textTerms = Array.ConvertAll(owned, term => term.Text);
        query = new DanbooruTagQuery(owned, string.Join(" ", textTerms), tagCount);
        return true;
    }

    private static bool TryTerm(string text, out DanbooruTagTerm? term, out DanbooruTagQueryError error)
    {
        term = null;
        error = DanbooruTagQueryError.None;
        bool negative = text[0] == '-';
        string body = negative ? text[1..] : text;
        if (body.Length == 0 || body[0] == '-') return Fail(DanbooruTagQueryError.InvalidNegativeTerm, out error);

        // No flattening of boolean, quoted or escaped expressions. Even parentheses
        // inside tag names are outside this deliberately small literal subset.
        foreach (char character in body)
            if (character is '(' or ')' or '"' or '\\' or '~' or '*' or '{' or '}' or '[' or ']' or '|')
                return Fail(DanbooruTagQueryError.UnsupportedExpression, out error);
        if (body.Equals("or", StringComparison.OrdinalIgnoreCase) || body.Equals("and", StringComparison.OrdinalIgnoreCase)
            || body.Equals("not", StringComparison.OrdinalIgnoreCase))
            return Fail(DanbooruTagQueryError.UnsupportedExpression, out error);

        int colon = body.IndexOf(':');
        if (colon >= 0)
        {
            // Do not treat every colon token as a free metatag (order is not free),
            // translate Gelbooru's rating vocabulary, or accept unknown operators.
            if (!body[..colon].Equals("rating", StringComparison.Ordinal))
                return Fail(DanbooruTagQueryError.UnsupportedMetaTag, out error);
            string[] ratings = body[(colon + 1)..].Split(',');
            if (ratings.Length > 4) return Fail(DanbooruTagQueryError.InvalidRating, out error);
            foreach (string rating in ratings)
                if (rating is not ("g" or "general" or "s" or "sensitive" or "q" or "questionable" or "e" or "explicit"))
                    return Fail(DanbooruTagQueryError.InvalidRating, out error);
            term = new(text, DanbooruTagTermKind.Rating, negative);
        }
        else
        {
            // Literal subset. Do not repair unsupported punctuation, % escapes or
            // leading '+' into a different query. Unicode letters/marks survive as-is.
            foreach (Rune rune in body.EnumerateRunes())
                if (!Rune.IsLetterOrDigit(rune) && Rune.GetUnicodeCategory(rune) is not
                        (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                    && rune.Value is not ('_' or '-' or '.'))
                    return Fail(DanbooruTagQueryError.UnsupportedExpression, out error);
            term = new(text, DanbooruTagTermKind.Tag, negative);
        }
        return true;
    }

    private static bool Inspect(string value, out int bytes, out DanbooruTagQueryError error)
    {
        bytes = 0;
        error = DanbooruTagQueryError.None;
        for (int offset = 0; offset < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(offset), out Rune rune, out int consumed) != OperationStatus.Done)
                return Fail(DanbooruTagQueryError.InvalidUnicode, out error);
            // Only ordinary ASCII whitespace is a separator. Other whitespace,
            // invisible formatting and controls must not vanish through Trim/Split.
            if (rune.Value <= char.MaxValue && IsSeparator((char)rune.Value)) { bytes++; offset += consumed; continue; }
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
                return Fail(DanbooruTagQueryError.InvalidCharacter, out error);
            bytes += rune.Utf8SequenceLength;
            offset += consumed;
        }
        return true;
    }

    private static bool IsSeparator(char value) => value is ' ' or '\t' or '\r' or '\n';
    private static bool Fail(DanbooruTagQueryError code, out DanbooruTagQueryError error) { error = code; return false; }
}
