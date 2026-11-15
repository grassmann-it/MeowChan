using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>Why a token of the media search field was not turned into a filter chip; it stays text and the page says why (M3).</summary>
internal enum MediaFilterQueryProblem
{
    /// <summary>"foo:bar" where foo is no field of the query (and no alias of the current language).</summary>
    UnknownField,
    /// <summary>The field is known but the value is not one (a tag you do not own, a source the index lacks, rating 9).</summary>
    UnknownValue,
    /// <summary>"tag:" with nothing after the colon.</summary>
    NeedsValue,
    /// <summary>"-rating:3": only tag, type, source and board can exclude.</summary>
    CannotExclude,
    /// <summary>"board:g" (or a thread number) that exists on several sources; "source/board" says which.</summary>
    Ambiguous,
    /// <summary>The term itself is fine but does not make one filter with the others (page-side, <see cref="MediaFilterQuery.Apply"/>).</summary>
    Refused,
    /// <summary>One more tag than the filter's tag lists hold (page-side).</summary>
    TooManyTags
}

internal enum MediaFilterQueryTokenKind { Text, Term, Problem }

/// <summary>
/// One token of a query as typed: free text, a recognized term, or a problem token (kept as text). <see cref="Raw"/> is what the user
/// typed; <see cref="Text"/> is what the token contributes to the free text, in the syntax of <see cref="MediaLibraryFilter.WordsOf"/>: a quoted
/// phrase that holds white space keeps its quotes (it is one word), an empty phrase is empty, any other token is its raw or unquoted spelling.
/// </summary>
internal sealed record MediaFilterQueryToken(MediaFilterQueryTokenKind Kind, string Raw, string Text, MediaFilterTerm? Term = null,
    MediaFilterQueryProblem? Problem = null);

/// <summary>
/// What the query parser needs to know besides the text: the localized field aliases and how names become identities. Every member
/// is optional (nothing found), so the parser stays pure and a test supplies fixed answers. The page fills it from the language pack
/// (<c>media.query.alias.*</c>), the own tags and the media index.
/// </summary>
internal sealed class MediaFilterQueryVocabulary
{
    internal static readonly MediaFilterQueryVocabulary Empty = new();

    /// <summary>Alias (lower case, letters only) to field, besides the English names (<see cref="MediaFilterTerm.KeyOf"/>), which always work.</summary>
    internal IReadOnlyDictionary<string, MediaFilterField> Aliases { get; init; } = new Dictionary<string, MediaFilterField>();
    /// <summary>The own tag with this name (compared the way tag names are), or null.</summary>
    internal Func<string, Guid?> FindTag { get; init; } = _ => null;
    /// <summary>The display name of an own tag, or null when it is unknown (the query then carries the id).</summary>
    internal Func<Guid, string?> TagName { get; init; } = _ => null;
    /// <summary>The source an identifier or display name stands for (ignoring case), or null.</summary>
    internal Func<string, string?> FindSource { get; init; } = _ => null;
    /// <summary>Every (source, board) pair of the index with this board name (ignoring case).</summary>
    internal Func<string, IReadOnlyList<(string Source, string Board)>> FindBoards { get; init; } = _ => [];
    /// <summary>Every thread of the index with this thread number.</summary>
    internal Func<string, IReadOnlyList<ResourceId>> FindThreads { get; init; } = _ => [];

    /// <summary>
    /// Reads the aliases of one language: each field has one text <c>media.query.alias.{key}</c> holding its words separated by
    /// commas or spaces. Words that are not lower-case letters, or that another field already owns, are left out.
    /// </summary>
    internal static IReadOnlyDictionary<string, MediaFilterField> AliasesFrom(Func<string, string?> text)
    {
        Dictionary<string, MediaFilterField> aliases = new(StringComparer.Ordinal);
        foreach (MediaFilterField field in Enum.GetValues<MediaFilterField>())
            foreach (string word in (text("media.query.alias." + MediaFilterTerm.KeyOf(field)) ?? string.Empty)
                .Split([' ', ',', ';', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string alias = word.ToLowerInvariant();
                if (MediaFilterQuery.IsFieldWord(alias) && MediaFilterTerm.FieldOf(alias) == null) aliases.TryAdd(alias, field);
            }
        return aliases;
    }

    internal MediaFilterField? FieldOf(string key)
    {
        string lower = key.ToLowerInvariant();
        return MediaFilterTerm.FieldOf(lower) ?? (Aliases.TryGetValue(lower, out MediaFilterField field) ? field : null);
    }
}

/// <summary>The parse of one query: its tokens in the order typed, and the views the page needs.</summary>
internal sealed class MediaFilterQueryResult(IReadOnlyList<MediaFilterQueryToken> tokens)
{
    internal IReadOnlyList<MediaFilterQueryToken> Tokens { get; } = tokens;
    /// <summary>The recognized terms, in the order typed (the name is never among them; see <see cref="FreeText"/>).</summary>
    internal IReadOnlyList<MediaFilterTerm> Terms => Tokens.Where(token => token.Term != null).Select(token => token.Term!).ToList();
    internal IReadOnlyList<MediaFilterQueryToken> Problems => Tokens.Where(token => token.Kind == MediaFilterQueryTokenKind.Problem).ToList();
    /// <summary>What is left for the file name: the free words and the tokens that were not understood, joined by single spaces.</summary>
    internal string FreeText => string.Join(' ', Tokens.Where(token => token.Term == null && token.Text.Length > 0).Select(token => token.Text));
    /// <summary>The terms plus the free text as a name term: the whole query as one term set (what <see cref="MediaFilterQuery.Serialize"/> writes).</summary>
    internal IReadOnlyList<MediaFilterTerm> AllTerms => FreeText is { Length: > 0 } name ? [.. Terms, MediaFilterTerm.Name(name)] : Terms;
}

/// <summary>What <see cref="MediaFilterQuery.Apply"/> made of a query: the next term set, the remaining text and what was refused.</summary>
internal sealed record MediaFilterQueryApplication(IReadOnlyList<MediaFilterTerm> Terms, string Remainder,
    IReadOnlyList<MediaFilterTerm> Added, IReadOnlyList<(string Raw, MediaFilterQueryProblem Problem)> Kept);

/// <summary>
/// M3: the query syntax of the media search field, pure and culture-neutral. Whitespace separates tokens; a token is one of
/// <list type="bullet">
/// <item><c>field:value</c>, <c>-field:value</c> (exclude; tag, type, source and board only) with the fields of
/// <see cref="MediaFilterTerm.KeyOf"/> in English plus the aliases of the vocabulary: <c>tag:cats</c>, <c>type:video</c>,
/// <c>sound:with|none</c>, <c>source:4chan</c>, <c>board:g</c> or <c>board:4chan/g</c>, <c>rating:&gt;=3</c>, <c>thread:1234</c>, <c>name:x</c>;</item>
/// <item>the bare flags <c>fav</c>, <c>missing</c>, <c>untagged</c> and <c>queue</c> (their aliases too; only <c>-queue</c> may exclude);
/// <c>queue:yes|no</c> and the provider's tags <c>booru:name</c>, <c>-booru:name</c>;</item>
/// <item>free text, which stays the search text: every word of it must occur in the file name, the thread title, the board or the source
/// (<see cref="MediaLibraryFilter.TextWords"/>); slashes are ordinary characters, so a title pseudo-tag such as <c>/kot/</c> is free text. A value in double quotes may hold spaces (<c>tag:"two words"</c>; inside quotes
/// <c>\"</c> and <c>\\</c> are escapes); a bare quoted phrase is free text that stays one word (<c>"cat girl"</c> keeps its quotes in the text), and quoting a token (<c>"type:video"</c>) keeps it free text.</item>
/// </list>
/// A token that looks like a field but is not understood (unknown field or value, nothing after the colon, an exclusion of a field that
/// cannot exclude, an ambiguous board) is never dropped: it stays in the free text and is reported as a problem. The text is read once,
/// left to right, in time linear in its length; nothing here limits it (the field's own limit does).
/// </summary>
internal static class MediaFilterQuery
{
    private readonly record struct Raw(string Text, string Decoded, int Colon, bool Quoted);

    /// <summary>Whether a word can be a field name or alias: letters only (so "12:30" and "http://x" are never fields).</summary>
    internal static bool IsFieldWord(string word) => word.Length > 0 && word.All(char.IsLetter);

    /// <summary>Reads the query. Never throws; the same text and vocabulary always give the same tokens.</summary>
    internal static MediaFilterQueryResult Parse(string? text, MediaFilterQueryVocabulary? vocabulary = null) => Parse(text, vocabulary ?? MediaFilterQueryVocabulary.Empty, nested: false);

    private static MediaFilterQueryResult Parse(string? text, MediaFilterQueryVocabulary vocabulary, bool nested)
    {
        List<MediaFilterQueryToken> tokens = [];
        List<(int Index, string Value, bool Exclude, string Raw)> pendingBoards = [];
        foreach (Raw raw in Tokenize(text ?? string.Empty))
        {
            MediaFilterQueryToken token = Classify(raw, vocabulary, nested, pendingBoards, tokens.Count);
            tokens.Add(token);
        }
        // A board name on several sources is narrowed by the sources the query includes ("source:4chan board:g").
        if (pendingBoards.Count > 0)
        {
            HashSet<string> included = tokens.Where(token => token.Term is { Field: MediaFilterField.Source, Exclude: false }).Select(token => token.Term!.Value).ToHashSet(StringComparer.Ordinal);
            foreach ((int index, string value, bool exclude, string rawText) in pendingBoards)
            {
                (string Source, string Board)[] candidates = vocabulary.FindBoards(value).Where(pair => included.Contains(pair.Source)).Distinct().ToArray();
                tokens[index] = candidates.Length == 1
                    ? new MediaFilterQueryToken(MediaFilterQueryTokenKind.Term, rawText, rawText, MediaFilterTerm.Board(candidates[0].Source, candidates[0].Board, exclude))
                    : Problem(rawText, MediaFilterQueryProblem.Ambiguous);
            }
        }
        return new MediaFilterQueryResult(tokens);
    }

    // The tokens of a query: split at white space outside double quotes; the quotes are removed from the value ("\" and "\\" escape inside
    // them) and the first colon before any quote separates the field from its value.
    private static IEnumerable<Raw> Tokenize(string text)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n) yield break;
            int start = i;
            StringBuilder decoded = new();
            bool inQuote = false, quoted = false;
            int colon = -1;
            while (i < n && (inQuote || !char.IsWhiteSpace(text[i])))
            {
                char c = text[i];
                if (inQuote)
                {
                    if (c == '\\' && i + 1 < n && (text[i + 1] == '"' || text[i + 1] == '\\')) { decoded.Append(text[i + 1]); i += 2; continue; }
                    if (c == '"') { inQuote = false; i++; continue; }
                    decoded.Append(c); i++;
                    continue;
                }
                if (c == '"') { inQuote = true; quoted = true; i++; continue; }
                if (c == ':' && colon < 0 && !quoted) colon = decoded.Length;
                decoded.Append(c); i++;
            }
            yield return new Raw(text[start..i], decoded.ToString(), colon, quoted);
        }
    }

    private static MediaFilterQueryToken Plain(Raw raw, MediaFilterQueryVocabulary vocabulary, bool nested)
    {
        // The free text is read by MediaLibraryFilter.WordsOf, which honours double quotes: a quoted phrase has to stay one word there. So it
        // keeps its quotes when its value holds white space, a quote or a backslash (inside them \" and \\ escape); an empty phrase "" is no
        // text at all; a quoted word that needs no quotes loses them. A phrase that would read as a filter again keeps its raw spelling
        // (the quotes are the escape).
        string text = raw.Text;
        if (raw.Quoted && (nested || Parse(raw.Decoded, vocabulary, nested: true).Tokens.All(token => token.Kind == MediaFilterQueryTokenKind.Text)))
            text = raw.Decoded.Length == 0 ? string.Empty
                : raw.Decoded.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\') ? Quote(raw.Decoded, always: true) : raw.Decoded;
        return new MediaFilterQueryToken(MediaFilterQueryTokenKind.Text, raw.Text, text);
    }

    private static MediaFilterQueryToken Problem(string raw, MediaFilterQueryProblem problem) =>
        new(MediaFilterQueryTokenKind.Problem, raw, raw, Problem: problem);

    private static MediaFilterQueryToken Term(string raw, MediaFilterTerm term) => new(MediaFilterQueryTokenKind.Term, raw, raw, term);

    private static MediaFilterQueryToken Classify(Raw raw, MediaFilterQueryVocabulary vocabulary, bool nested,
        List<(int Index, string Value, bool Exclude, string Raw)> pendingBoards, int index)
    {
        string decoded = raw.Decoded;
        string key, value;
        bool exclude = false;
        if (raw.Colon >= 0)
        {
            key = decoded[..raw.Colon];
            value = decoded[(raw.Colon + 1)..];
        }
        else
        {
            // No colon: only a bare flag word is a term ("fav", "-fav").
            if (raw.Quoted) return Plain(raw, vocabulary, nested);
            string word = decoded.StartsWith('-') ? decoded[1..] : decoded;
            if (!IsFieldWord(word) || vocabulary.FieldOf(word) is not { } flag || !IsFlag(flag)) return Plain(raw, vocabulary, nested);
            // "queue" and "-queue": in the tagging queue or not (the one flag that can exclude).
            if (flag == MediaFilterField.Queue) return Term(raw.Text, MediaFilterTerm.InQueue(!decoded.StartsWith('-')));
            return decoded.StartsWith('-') ? Problem(raw.Text, MediaFilterQueryProblem.CannotExclude) : Term(raw.Text, MediaFilterTerm.Flag(flag));
        }
        if (key.StartsWith('-')) { exclude = true; key = key[1..]; }
        if (!IsFieldWord(key)) return Plain(raw, vocabulary, nested); // "12:30", "http://x", "--tag:x": ordinary text
        if (vocabulary.FieldOf(key) is not { } field) return Problem(raw.Text, MediaFilterQueryProblem.UnknownField);
        if (exclude && !MediaFilterTerm.CanExclude(field)) return Problem(raw.Text, MediaFilterQueryProblem.CannotExclude);
        value = value.Trim();
        if (value.Length == 0 && !IsFlag(field)) return Problem(raw.Text, MediaFilterQueryProblem.NeedsValue);
        switch (field)
        {
            case MediaFilterField.Tag:
                return vocabulary.FindTag(value) is { } tag ? Term(raw.Text, MediaFilterTerm.Tag(tag, exclude)) : Problem(raw.Text, MediaFilterQueryProblem.UnknownValue);
            case MediaFilterField.Type:
                return MediaFilterTerm.TypeOf(value) is { } kind ? Term(raw.Text, MediaFilterTerm.Type(kind, exclude)) : Problem(raw.Text, MediaFilterQueryProblem.UnknownValue);
            case MediaFilterField.Sound:
                return value.ToLowerInvariant() switch
                {
                    "with" or "yes" or "on" or "true" => Term(raw.Text, MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithSound)),
                    "without" or "none" or "no" or "off" or "false" => Term(raw.Text, MediaFilterTerm.Sound(MediaLibrarySoundFilter.WithoutSound)),
                    _ => Problem(raw.Text, MediaFilterQueryProblem.UnknownValue)
                };
            case MediaFilterField.Source:
                return vocabulary.FindSource(value) is { } source ? Term(raw.Text, MediaFilterTerm.Source(source, exclude)) : Problem(raw.Text, MediaFilterQueryProblem.UnknownValue);
            case MediaFilterField.Board:
                return ClassifyBoard(raw.Text, value, exclude, vocabulary, pendingBoards, index);
            case MediaFilterField.Rating:
                return ParseRating(value) is { } least ? Term(raw.Text, MediaFilterTerm.Rating(least)) : Problem(raw.Text, MediaFilterQueryProblem.UnknownValue);
            case MediaFilterField.Favorite or MediaFilterField.Missing or MediaFilterField.Untagged:
                return value.Length == 0 || value.ToLowerInvariant() is "yes" or "true" or "on" or "1"
                    ? Term(raw.Text, MediaFilterTerm.Flag(field)) : Problem(raw.Text, MediaFilterQueryProblem.UnknownValue);
            case MediaFilterField.Thread:
                return ClassifyThread(raw.Text, value, vocabulary);
            // MP1: "queue:yes" / "queue:no" ("-queue:yes" is "queue:no").
            case MediaFilterField.Queue:
                return value.ToLowerInvariant() switch
                {
                    "" or "yes" or "true" or "on" or "1" => Term(raw.Text, MediaFilterTerm.InQueue(!exclude)),
                    "no" or "false" or "off" or "0" => Term(raw.Text, MediaFilterTerm.InQueue(exclude)),
                    _ => Problem(raw.Text, MediaFilterQueryProblem.UnknownValue)
                };
            // MP4: "booru:cat_ears", "-booru:comic" - the provider's tag name as written (compared without case).
            case MediaFilterField.SourceTag:
                return MediaFilterTerm.IsSourceTagName(value) ? Term(raw.Text, MediaFilterTerm.SourceTag(value, exclude)) : Problem(raw.Text, MediaFilterQueryProblem.UnknownValue);
            default: // Name: the file name text, written as a field
                return new MediaFilterQueryToken(MediaFilterQueryTokenKind.Text, raw.Text, value);
        }
    }

    private static bool IsFlag(MediaFilterField field) => field is MediaFilterField.Favorite or MediaFilterField.Missing or MediaFilterField.Untagged or MediaFilterField.Queue;

    private static MediaFilterQueryToken ClassifyBoard(string raw, string value, bool exclude, MediaFilterQueryVocabulary vocabulary,
        List<(int Index, string Value, bool Exclude, string Raw)> pendingBoards, int index)
    {
        // "source/board": the source names itself (the source's own name may hold a slash, so every slash is tried).
        for (int slash = value.IndexOf('/'); slash > 0; slash = value.IndexOf('/', slash + 1))
        {
            if (vocabulary.FindSource(value[..slash]) is not { } source) continue;
            (string Source, string Board)[] named = vocabulary.FindBoards(value[(slash + 1)..]).Where(pair => pair.Source == source).Distinct().ToArray();
            if (named.Length == 1) return Term(raw, MediaFilterTerm.Board(named[0].Source, named[0].Board, exclude));
        }
        (string Source, string Board)[] found = vocabulary.FindBoards(value).Distinct().ToArray();
        if (found.Length == 0) return Problem(raw, MediaFilterQueryProblem.UnknownValue);
        if (found.Length == 1) return Term(raw, MediaFilterTerm.Board(found[0].Source, found[0].Board, exclude));
        pendingBoards.Add((index, value, exclude, raw));
        return Problem(raw, MediaFilterQueryProblem.Ambiguous); // replaced once the query's sources are known
    }

    private static MediaFilterQueryToken ClassifyThread(string raw, string value, MediaFilterQueryVocabulary vocabulary)
    {
        string[] parts = value.Split('/');
        if (parts.Length == 3)
        {
            string source = vocabulary.FindSource(parts[0]) ?? parts[0];
            ResourceId[] named = vocabulary.FindThreads(parts[2]).Where(thread => thread.Source == source
                && string.Equals(thread.Board, parts[1], StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
            if (named.Length == 1) return Term(raw, MediaFilterTerm.InThread(named[0]));
            return Problem(raw, MediaFilterQueryProblem.UnknownValue);
        }
        ResourceId[] found = vocabulary.FindThreads(value).Distinct().ToArray();
        return found.Length switch
        {
            0 => Problem(raw, MediaFilterQueryProblem.UnknownValue),
            1 => Term(raw, MediaFilterTerm.InThread(found[0])),
            _ => Problem(raw, MediaFilterQueryProblem.Ambiguous)
        };
    }

    // ">=3", "≥3", "3+" and "3" are the least rating 3; ">3" is 4. Whole values from 1 to 5.
    private static int? ParseRating(string value)
    {
        string text = value.Trim();
        bool greater = false;
        if (text.StartsWith(">=", StringComparison.Ordinal)) text = text[2..];
        else if (text.StartsWith('≥')) text = text[1..];
        else if (text.StartsWith('>')) { greater = true; text = text[1..]; }
        if (text.EndsWith('+')) text = text[..^1];
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return null;
        if (greater) number++;
        return number is >= 1 and <= 5 ? number : null;
    }

    // ---- applying a query to a term set --------------------------------------------------------------------------------

    /// <summary>
    /// Turns the recognized terms of <paramref name="parsed"/> into the next term set, in the order typed. <paramref name="admit"/> judges
    /// each term against the set built so far (the page: the tag lists' limit, and whether the terms still make one filter) and returns
    /// null to admit it or the reason it is refused. A refused term stays in the text like any token that was not understood. The
    /// <see cref="MediaFilterQueryApplication.Remainder"/> is the text that is left for the file name field.
    /// </summary>
    internal static MediaFilterQueryApplication Apply(IReadOnlyList<MediaFilterTerm> current, MediaFilterQueryResult parsed,
        Func<IReadOnlyList<MediaFilterTerm>, MediaFilterTerm, MediaFilterQueryProblem?> admit)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(admit);
        IReadOnlyList<MediaFilterTerm> terms = current;
        List<MediaFilterTerm> added = [];
        List<(string Raw, MediaFilterQueryProblem Problem)> kept = [];
        List<string> text = [];
        foreach (MediaFilterQueryToken token in parsed.Tokens)
        {
            if (token.Term == null)
            {
                if (token.Text.Length > 0) text.Add(token.Text); // an empty phrase "" contributes no text
                if (token.Problem is { } problem) kept.Add((token.Raw, problem));
                continue;
            }
            if (terms.Contains(token.Term)) continue; // already in the filter: the token has done its job
            if (admit(terms, token.Term) is { } refusal)
            {
                text.Add(token.Raw);
                kept.Add((token.Raw, refusal));
                continue;
            }
            terms = MediaFilterTermSet.With(terms, token.Term);
            added.Add(token.Term);
        }
        return new MediaFilterQueryApplication(terms, string.Join(' ', text), added, kept);
    }

    // ---- term set to text ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The terms as a query: <c>parse(serialize(terms))</c> gives the same terms back (with the same vocabulary). English field names
    /// whatever the language; a value is quoted when it needs it; a board or thread is written in full (<c>4chan/g</c>) unless the
    /// index makes the short form unique; the name goes last, as free text where that reads back as the same text, else as
    /// <c>name:"…"</c>.
    /// </summary>
    internal static string Serialize(IEnumerable<MediaFilterTerm> terms, MediaFilterQueryVocabulary? vocabulary = null)
    {
        ArgumentNullException.ThrowIfNull(terms);
        MediaFilterQueryVocabulary vocab = vocabulary ?? MediaFilterQueryVocabulary.Empty;
        IReadOnlyList<MediaFilterTerm> all = terms.ToList();
        List<string> parts = [];
        string? name = null;
        foreach (MediaFilterTerm term in all)
        {
            string minus = term.Exclude ? "-" : string.Empty;
            string key = MediaFilterTerm.KeyOf(term.Field);
            switch (term.Field)
            {
                case MediaFilterField.Tag:
                    parts.Add(minus + key + ":" + Quote(Guid.TryParseExact(term.Value, "D", out Guid id) ? vocab.TagName(id) ?? term.Value : term.Value));
                    break;
                case MediaFilterField.Sound:
                    parts.Add(key + ":" + (term.Value == "with" ? "with" : "none"));
                    break;
                case MediaFilterField.Source or MediaFilterField.Type:
                    parts.Add(minus + key + ":" + Quote(term.Value));
                    break;
                case MediaFilterField.Board:
                    parts.Add(minus + key + ":" + Quote(BoardText(term, all, vocab)));
                    break;
                case MediaFilterField.Rating:
                    parts.Add(key + ":>=" + term.Value);
                    break;
                case MediaFilterField.Favorite or MediaFilterField.Missing or MediaFilterField.Untagged:
                    parts.Add(key);
                    break;
                case MediaFilterField.Thread:
                    parts.Add(key + ":" + Quote(ThreadText(term, vocab)));
                    break;
                case MediaFilterField.Queue:
                    parts.Add(key + (term.Exclude ? ":no" : ":yes"));
                    break;
                case MediaFilterField.SourceTag:
                    parts.Add(minus + key + ":" + Quote(term.Value));
                    break;
                case MediaFilterField.Name:
                    name = name == null ? term.Value : name + " " + term.Value;
                    break;
            }
        }
        if (!string.IsNullOrWhiteSpace(name)) parts.Add(NameText(name, vocab));
        return string.Join(' ', parts);
    }

    private static string BoardText(MediaFilterTerm term, IReadOnlyList<MediaFilterTerm> all, MediaFilterQueryVocabulary vocab)
    {
        (string Source, string Board)[] same = vocab.FindBoards(term.Value).Distinct().ToArray();
        bool unique = same.Length == 1 && same[0].Source == term.Scope && same[0].Board == term.Value;
        bool named = all.Any(other => other.Field == MediaFilterField.Source && !other.Exclude && other.Value == term.Scope)
            && same.Count(pair => pair.Source == term.Scope) == 1;
        return unique || named ? term.Value : (term.Scope ?? string.Empty) + "/" + term.Value;
    }

    private static string ThreadText(MediaFilterTerm term, MediaFilterQueryVocabulary vocab)
    {
        ResourceId thread = term.Resource ?? new ResourceId(string.Empty, string.Empty, term.Value);
        ResourceId[] same = vocab.FindThreads(thread.Thread ?? string.Empty).Distinct().ToArray();
        return same.Length == 1 && same[0] == thread ? thread.Thread ?? string.Empty : thread.Source + "/" + thread.Board + "/" + thread.Thread;
    }

    // Free text reads back as itself when it is one plain run of single-spaced words none of which reads as a filter.
    private static string NameText(string name, MediaFilterQueryVocabulary vocab)
    {
        string text = name.Trim();
        bool plain = text.Length > 0 && text.IndexOf('"') < 0 && text.IndexOf('\\') < 0 && !text.Any(c => char.IsWhiteSpace(c) && c != ' ')
            && !text.Contains("  ", StringComparison.Ordinal)
            && Parse(text, vocab, nested: true).Tokens.All(token => token.Kind == MediaFilterQueryTokenKind.Text && token.Text == token.Raw);
        return plain ? text : "name:" + Quote(text, always: true);
    }

    /// <summary>A value as the query spells it: bare when it can be, else in double quotes with <c>\"</c> and <c>\\</c> escaped.</summary>
    internal static string Quote(string value, bool always = false)
    {
        if (!always && value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\')) return value;
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
