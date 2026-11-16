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
    /// Splits the draft for completion: the text before the word being typed and that word (inside an open quote: everything
    /// after the quote). <c>"piss pe"</c> gives ("piss ", "pe"); a draft without a separator gives ("", draft).
    /// </summary>
    internal static (string Prefix, string Last) SplitLast(string? text)
    {
        text ??= string.Empty;
        bool quoted = false;
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if (quoted && c == '\\' && index + 1 < text.Length) { index++; continue; }
            if (c == '"') { quoted = !quoted; start = index + 1; continue; }
            if (!quoted && (IsSoftSeparator(c) || IsHardSeparator(c))) start = index + 1;
        }
        // Inside an open quote the word starts after it; the prefix ends before the quote, which the completion writes again.
        int prefixEnd = quoted ? start - 1 : start;
        return (text[..prefixEnd], text[start..]);
    }

    /// <summary>The draft with its last word replaced by <paramref name="name"/>, quoted when the name needs it.</summary>
    internal static string Complete(string prefix, string name) => prefix + Quote(name);

    /// <summary>The name as a field token: bare when it has no space, comma or quote, else in double quotes with escapes.</summary>
    internal static string Quote(string name)
    {
        if (!name.Any(c => IsSoftSeparator(c) || IsHardSeparator(c) || c is '"' or '\\')) return name;
        return "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static int Unquoted(List<Token> part, int index)
    {
        int count = 0;
        while (index + count < part.Count && !part[index + count].Quoted) count++;
        return count;
    }

    // The most words any tag or alias name has: how far the longest-match looks ahead. Cached per snapshot.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PrivateTagStoreSnapshot, object> WordCounts = new();

    private static int MaximumWords(PrivateTagStoreSnapshot snapshot) => (int)WordCounts.GetValue(snapshot, static current =>
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
