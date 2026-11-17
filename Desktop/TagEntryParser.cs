using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// One name of a tag field's text, resolved: <see cref="Id"/> is the existing own tag it gives (by name or through
/// <see cref="Alias"/>), null for a name that creates a new tag. <see cref="Typed"/> is the text as it was typed.
/// </summary>
internal sealed record TagEntryItem(string Typed, PrivateTagName Name, TagId? Id, PrivateTagAlias? Alias)
{
    /// <summary>True when an alias mapped the typed name to another tag (<c>pee</c> gives <c>piss</c>).</summary>
    internal bool Mapped => Alias != null;
}

/// <summary>
/// The names of one entry, in typed order, each once after resolution; <see cref="Invalid"/> holds texts that are no tag name.
/// <see cref="TooMany"/>: the entry names more tags than <see cref="TagEntryParser.MaximumNames"/> (or is longer than
/// <see cref="TagEntryParser.MaximumTextLength"/>); it has no items and the whole text as its one invalid text, so a field that does
/// not know the flag refuses it like an invalid name instead of adding part of it.
/// </summary>
internal sealed record TagEntry(IReadOnlyList<TagEntryItem> Items, IReadOnlyList<string> Invalid, bool TooMany = false)
{
    internal static readonly TagEntry Empty = new(Array.Empty<TagEntryItem>(), Array.Empty<string>());

    internal static TagEntry Overflow(string text) => new(Array.Empty<TagEntryItem>(), new[] { text }, true);
}

/// <summary>
/// One token of a tag field's draft (<see cref="TagEntryParser.TokenAt"/>): the range <see cref="Start"/>..<see cref="End"/>
/// (end exclusive; a quoted token with its quotes) that a completion replaces, and the name as typed (quotes and escapes
/// removed, trimmed). <see cref="Hard"/>: the draft is in hard-separator mode, where spaces belong to the name.
/// </summary>
internal sealed record TagEntryToken(int Start, int End, string Typed, bool Quoted, bool Hard);

/// <summary>
/// The shared parser of the tag fields: several tags in one entry (<c>piss pee cute anal</c> + Enter adds four). Tag names MAY
/// contain spaces, commas and semicolons (only control and format characters are refused, see <see cref="PrivateTagName"/>), so whitespace
/// alone cannot always separate; the rules keep every existing name usable:
/// <list type="bullet">
/// <item>Text that is, as a whole, an existing tag or alias stays one tag (names with spaces or commas keep working).</item>
/// <item>A double-quoted part is one tag (<c>"big tits" anal</c> = 2); inside quotes <c>\"</c> and <c>\\</c> are escapes,
/// like <c>tag:"x"</c> in the media search. An underscore is part of the name and never separates (<c>big_tits</c> is one word; only an alias with the same folded text can map it).</item>
/// <item>With a hard separator outside quotes (comma, semicolon, line break, and the full-width comma, ideographic comma and semicolon
/// of CJK input) the hard separators separate and each part is one tag with its spaces (<c>big tits; anal</c> = 2).</item>
/// <item>Otherwise spaces (space, no-break and ideographic space; not a tab, which the name rule refuses) separate, but consecutive words that together name an existing
/// tag or alias stay together (longest first), so <c>big tits anal</c> with an existing tag <c>big tits</c> gives 2.</item>
/// <item>Each name resolves through the alias table (same rules as the store: name, alias, folded alias); names that resolve
/// to the same tag, or are the same new name, are added once, whatever the case.</item>
/// <item>One entry names at most <see cref="MaximumNames"/> tags (a documented ceiling that only guards a pasted text; the stored limits
/// are the own tag limit setting and the stamp size setting); more is refused whole (<see cref="TagEntry.TooMany"/>).</item>
/// </list>
/// Pure: no UI, no storage.
/// </summary>
internal static class TagEntryParser
{
    private readonly record struct Token(string Text, bool Quoted);

    /// <summary>Most tags one entry may name. A fixed, documented ceiling (a pasted paragraph is not a tag list); it is no setting.</summary>
    internal const int MaximumNames = 100;
    /// <summary>Longest entry text that is parsed at all (UTF-16 units): names are at most 128 scalars each, so this holds the maximum names with quotes.</summary>
    internal const int MaximumTextLength = 32 * 1024;

    /// <summary>A separator that always splits (and then makes each part one tag with its spaces): comma, semicolon, line breaks, CJK forms.</summary>
    internal static bool IsHardSeparator(char c) =>
        c is ',' or ';' or '\r' or '\n' or '\u0085' or '\u2028' or '\u2029' or '\uFF0C' or '\u3001' or '\uFF1B';

    /// <summary>
    /// A space that separates words: any space separator (space, no-break space, ideographic space of CJK input, thin spaces). A tab or
    /// other control character is none: it stays in the name, which the name rule refuses as before.
    /// </summary>
    internal static bool IsSoftSeparator(char c) => char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    internal static TagEntry Parse(string? text, PrivateTagStoreSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(text)) return TagEntry.Empty;
        if (text.Length > MaximumTextLength) return TagEntry.Overflow(text);
        List<string> names = new();
        if (snapshot?.Resolve(text, out _) != null) names.Add(text.Trim());
        else
        {
            List<List<Token>> parts = Tokenize(text, out bool comma);
            int maxWords = snapshot == null ? 1 : MaximumWords(snapshot);
            foreach (List<Token> part in parts)
            {
                if (comma)
                {
                    // Comma mode: the part is one tag; a quoted part keeps its inner text.
                    string joined = string.Join(" ", part.Select(token => token.Text)).Trim();
                    if (joined.Length > 0) names.Add(joined);
                    continue;
                }
                for (int index = 0; index < part.Count;)
                {
                    if (part[index].Quoted) { names.Add(part[index].Text); index++; continue; }
                    int take = 1;
                    for (int length = Math.Min(maxWords, Unquoted(part, index)); length > 1; length--)
                        if (snapshot!.Resolve(string.Join(" ", part.Skip(index).Take(length).Select(token => token.Text)), out _) != null)
                        { take = length; break; }
                    names.Add(string.Join(" ", part.Skip(index).Take(take).Select(token => token.Text)));
                    index += take;
                }
            }
        }

        List<TagEntryItem> items = new();
        List<string> invalid = new();
        HashSet<TagId> ids = new();
        HashSet<string> fresh = new(StringComparer.Ordinal);
        foreach (string typed in names)
        {
            if (!PrivateTagName.TryCreate(typed, out PrivateTagName? name, out _) || name == null) { invalid.Add(typed); continue; }
            PrivateTagAlias? alias = null;
            PrivateTagDefinition? tag = snapshot?.Resolve(name, out alias);
            if (tag != null ? !ids.Add(tag.Id) : !fresh.Add(name.Key)) continue;
            items.Add(tag != null ? new TagEntryItem(typed, tag.Name, tag.Id, alias) : new TagEntryItem(typed, name, null, null));
            if (items.Count > MaximumNames) return TagEntry.Overflow(text);
        }
        return new TagEntry(items, invalid);
    }

    /// <summary>
    /// True when the entry is just the typed text as one unmapped name (or nothing valid at all): the field's ordinary single add
    /// handles it exactly as before, including its own error for an invalid name.
    /// </summary>
    internal static bool IsPlain(TagEntry entry, string? text) => !entry.TooMany && entry.Invalid.Count > 0 && entry.Items.Count == 0
        || !entry.TooMany && entry.Invalid.Count == 0 && entry.Items is [{ Mapped: false } only] && string.Equals(only.Typed, text?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Adds the items one after another through the owner's own writes (a new name by name, an existing tag by ID) and stops at the
    /// first refused write (<paramref name="failed"/>). Returns the items that were attempted without failure. A dialog that keeps
    /// one undo step passes <paramref name="lastChange"/> (its newest accepted change, null when a write changed nothing) and
    /// <paramref name="combine"/>: the changes this run made, in order, become one step, so one undo takes all of them back
    /// (also those written before a refused one).
    /// </summary>
    internal static async System.Threading.Tasks.Task<IReadOnlyList<TagEntryItem>> AddEachAsync(TagEntry entry,
        Func<string, System.Threading.Tasks.Task> addNew, Func<TagId, System.Threading.Tasks.Task> addExisting, Func<bool> failed,
        Func<TagDialogChange?>? lastChange = null, Action<IReadOnlyList<TagDialogChange>>? combine = null)
    {
        List<TagEntryItem> done = new();
        List<TagDialogChange> changes = new();
        foreach (TagEntryItem item in entry.Items)
        {
            await (item.Id is { } id ? addExisting(id) : addNew(item.Name.Display));
            if (lastChange?.Invoke() is { } change && !changes.Any(existing => ReferenceEquals(existing, change))) changes.Add(change);
            if (failed()) break;
            done.Add(item);
        }
        if (changes.Count > 1) combine?.Invoke(changes);
        return done;
    }

    private static readonly char[] LineBreaks = { '\r', '\n', '\u0085', '\u2028', '\u2029' };

    /// <summary>
    /// A pasted text for the single-line tag field: every run of line breaks becomes ", " (blank lines and edge spaces dropped), so no
    /// line is lost and the draft reads as the comma list it is. Text without a line break stays as it is.
    /// </summary>
    internal static string ForField(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOfAny(LineBreaks) < 0) return text;
        return string.Join(", ", text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>"piss (from pee), cute (new), anal (new)": what an entry gave, with the names an alias mapped and the tags it created.</summary>
    internal static string Describe(IEnumerable<TagEntryItem> items) => string.Join(", ", items.Select(item =>
        item.Mapped ? Texts.T("tags.entry.mapped", item.Name.Display, item.Typed.Trim())
        : item.Id == null ? Texts.T("tags.entry.new", item.Name.Display)
        : item.Name.Display));

    /// <summary>
    /// The token of the draft under the caret (the selection start), the one the suggestions concern and a completion replaces;
    /// null while a selection spans more than one token (no list then). The rules are the parser's: in space mode a word or a
    /// double-quoted part (an unclosed quote runs to the end), with a hard separator outside quotes the whole part between them
    /// (its spaces belong to the name). A token is current while the caret is inside it or right after it, and at the very start
    /// of the draft the first token (a text set by the program leaves the caret there); right after a separator or in blank text
    /// the token is empty (<see cref="TagEntryToken.Typed"/> "").
    /// </summary>
    internal static TagEntryToken? TokenAt(string? text, int selectionStart, int selectionEnd)
    {
        text ??= string.Empty;
        int start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        int end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);
        List<TagEntryToken> tokens = Tokens(text, out bool hard);
        TagEntryToken? Containing(int at) => tokens.FirstOrDefault(token => token.Start < at && at <= token.End || at == 0 && token.Start == 0);
        TagEntryToken empty = new(start, start, string.Empty, false, hard);
        if (start == end) return Containing(start) ?? empty;
        // A selection inside one token keeps that token (typing replaces only the selected part of it); anything wider has no list.
        TagEntryToken? within = tokens.FirstOrDefault(token => token.Start <= start && end <= token.End);
        if (within != null) return within;
        return tokens.Any(token => token.Start < end && start < token.End) ? null : empty;
    }

    /// <summary>
    /// The unquoted words right before <paramref name="token"/> that may form one name with it (space mode only), nearest last, at
    /// most <paramref name="limit"/>: <c>hot big ti</c> gives <c>hot</c>, <c>big</c> for the token <c>ti</c>, so a name with spaces
    /// (<c>big tits</c>) replaces the words already typed instead of being appended to them.
    /// </summary>
    internal static IReadOnlyList<TagEntryToken> WordsBefore(string? text, TagEntryToken token, int limit)
    {
        if (token.Hard || token.Quoted || token.Typed.Length == 0 || limit <= 0) return Array.Empty<TagEntryToken>();
        List<TagEntryToken> tokens = Tokens(text ?? string.Empty, out _);
        int at = tokens.FindIndex(item => item.Start == token.Start && item.End == token.End);
        List<TagEntryToken> words = new();
        for (int index = at - 1; index >= 0 && words.Count < limit && !tokens[index].Quoted; index--) words.Insert(0, tokens[index]);
        return words;
    }

    /// <summary>
    /// The draft with the range <paramref name="start"/>..<paramref name="end"/> (a token, or the words of a name with spaces)
    /// replaced by <paramref name="name"/>, and the caret after it. Everything before and after stays as it was. The name is quoted
    /// when it needs it (space mode: spaces, separators, quotes; hard-separator mode only hard separators and quotes, since spaces
    /// belong to the name there); exactly one separator follows it when the rest of the draft does not already start with one. A
    /// name that becomes the whole draft is written bare: as a whole it is one tag.
    /// </summary>
    internal static (string Text, int Caret) Replace(string? text, int start, int end, string name, bool hard)
    {
        text ??= string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        string prefix = text[..start], suffix = text[end..];
        if (prefix.Trim().Length == 0 && suffix.Trim().Length == 0) return (name, name.Length);
        string written = hard ? QuoteHard(name) : Quote(name);
        // At the end of the draft nothing needs separating (Enter adds it, Tab keeps its completion as before).
        string separator = suffix.Length == 0 || IsSoftSeparator(suffix[0]) || IsHardSeparator(suffix[0])
            ? string.Empty : hard ? ", " : " ";
        return (prefix + written + separator + suffix, prefix.Length + written.Length + separator.Length);
    }

    /// <summary>True when the text after a completed token names a further tag (the completion then only edits the draft).</summary>
    internal static bool NamesMore(string? suffix) => suffix?.Any(c => !IsSoftSeparator(c) && !IsHardSeparator(c) && c != '"') == true;

    /// <summary>The draft with its last word replaced by <paramref name="name"/>, quoted when the name needs it.</summary>
    internal static string Complete(string prefix, string name) => prefix + Quote(name);

    /// <summary>The name as a field token: bare when it has no space, comma or quote, else in double quotes with escapes.</summary>
    internal static string Quote(string name)
    {
        if (!name.Any(c => IsSoftSeparator(c) || IsHardSeparator(c) || c is '"' or '\\')) return name;
        return Quoted(name);
    }

    // Hard-separator mode: spaces are part of the name, only a separator or a quote needs the quotes.
    private static string QuoteHard(string name) => name.Any(c => IsHardSeparator(c) || c == '"') ? Quoted(name) : name;

    private static string Quoted(string name) =>
        "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // The tokens of a draft with their ranges, by the parser's rules (Tokenize): words and quoted parts, or with a hard separator
    // the parts between them, trimmed. A quoted range includes its quotes; an unclosed quote runs to the end.
    private static List<TagEntryToken> Tokens(string text, out bool hard)
    {
        List<(int Start, int End, string Text, bool Quoted, int Part)> words = new();
        hard = false;
        int part = 0;
        for (int index = 0; index < text.Length;)
        {
            char c = text[index];
            if (c == '"')
            {
                int start = index;
                StringBuilder quoted = new();
                for (index++; index < text.Length && text[index] != '"'; index++)
                {
                    if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] is '"' or '\\') index++;
                    quoted.Append(text[index]);
                }
                if (index < text.Length) index++; // the closing quote
                words.Add((start, index, quoted.ToString().Trim(), true, part));
                continue;
            }
            if (IsHardSeparator(c)) { hard = true; part++; index++; continue; }
            if (IsSoftSeparator(c)) { index++; continue; }
            int begin = index;
            while (index < text.Length && text[index] != '"' && !IsHardSeparator(text[index]) && !IsSoftSeparator(text[index])) index++;
            words.Add((begin, index, text[begin..index], false, part));
        }
        if (!hard) return words.Select(word => new TagEntryToken(word.Start, word.End, word.Text, word.Quoted, false)).ToList();
        // Comma mode: one token per part, like the parser's joined part.
        return words.GroupBy(word => word.Part).Select(group => group.ToList()).Select(items => new TagEntryToken(items[0].Start, items[^1].End,
            string.Join(" ", items.Select(item => item.Text)).Trim(), items is [{ Quoted: true }], true)).ToList();
    }

    private static int Unquoted(List<Token> part, int index)
    {
        int count = 0;
        while (index + count < part.Count && !part[index + count].Quoted) count++;
        return count;
    }

    // The most words any tag or alias name has: how far the longest-match looks ahead. Cached per snapshot.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PrivateTagStoreSnapshot, object> WordCounts = new();

    internal static int MaximumWords(PrivateTagStoreSnapshot snapshot) => (int)WordCounts.GetValue(snapshot, static current =>
        current.Tags.Select(tag => tag.Name).Concat(current.Aliases.Select(alias => alias.Name))
            .Select(name => name.Display.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length).DefaultIfEmpty(1).Max());

    private static List<List<Token>> Tokenize(string text, out bool comma)
    {
        List<List<Token>> parts = new() { new() };
        comma = false;
        StringBuilder word = new();
        void Flush()
        {
            if (word.Length > 0) parts[^1].Add(new Token(word.ToString(), false));
            word.Clear();
        }
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if (c == '"')
            {
                Flush();
                StringBuilder quoted = new();
                for (index++; index < text.Length && text[index] != '"'; index++)
                {
                    if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] is '"' or '\\') index++;
                    quoted.Append(text[index]);
                }
                string inner = quoted.ToString().Trim();
                if (inner.Length > 0) parts[^1].Add(new Token(inner, true));
                continue;
            }
            if (IsHardSeparator(c)) { Flush(); comma = true; parts.Add(new()); continue; }
            // Spaces between words separate; line breaks are hard separators above.
            if (IsSoftSeparator(c)) { Flush(); continue; }
            word.Append(c);
        }
        Flush();
        return parts;
    }
}
