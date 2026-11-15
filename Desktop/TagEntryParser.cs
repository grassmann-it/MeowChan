using System;
using System.Collections.Generic;
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

/// <summary>The names of one entry, in typed order, each once after resolution; <see cref="Invalid"/> holds texts that are no tag name.</summary>
internal sealed record TagEntry(IReadOnlyList<TagEntryItem> Items, IReadOnlyList<string> Invalid)
{
    internal static readonly TagEntry Empty = new(Array.Empty<TagEntryItem>(), Array.Empty<string>());
}

/// <summary>
/// The shared parser of the tag fields: several tags in one entry (<c>piss pee cute anal</c> + Enter adds four). Rules:
/// <list type="bullet">
/// <item>Text that is, as a whole, an existing tag or alias stays one tag (names with spaces or commas keep working).</item>
/// <item>A double-quoted part is one tag (<c>"big tits" anal</c> = 2); inside quotes <c>\"</c> and <c>\\</c> are escapes,
/// like <c>tag:"x"</c> in the media search.</item>
/// <item>With a comma outside quotes, commas separate and each part is one tag with its spaces (<c>big tits, anal</c> = 2).</item>
/// <item>Otherwise spaces (U+0020 only) separate, but consecutive words that together name an existing tag or alias stay together
/// (longest first), so <c>big tits anal</c> with an existing tag <c>big tits</c> gives 2.</item>
/// <item>Each name resolves through the alias table (same rules as the store: name, alias, folded alias); names that resolve
/// to the same tag, or are the same new name, are added once.</item>
/// </list>
/// Pure: no UI, no storage.
/// </summary>
internal static class TagEntryParser
{
    private readonly record struct Token(string Text, bool Quoted);

    internal static TagEntry Parse(string? text, PrivateTagStoreSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(text)) return TagEntry.Empty;
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
        }
        return new TagEntry(items, invalid);
    }

    /// <summary>
    /// True when the entry is just the typed text as one unmapped name (or nothing valid at all): the field's ordinary single add
    /// handles it exactly as before, including its own error for an invalid name.
    /// </summary>
    internal static bool IsPlain(TagEntry entry, string? text) => entry.Invalid.Count > 0 && entry.Items.Count == 0
        || entry.Invalid.Count == 0 && entry.Items is [{ Mapped: false } only] && string.Equals(only.Typed, text?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Adds the items one after another through the owner's own writes (a new name by name, an existing tag by ID) and stops at the
    /// first refused write (<paramref name="failed"/>). Returns the items that were attempted without failure.
    /// </summary>
    internal static async System.Threading.Tasks.Task<IReadOnlyList<TagEntryItem>> AddEachAsync(TagEntry entry,
        Func<string, System.Threading.Tasks.Task> addNew, Func<TagId, System.Threading.Tasks.Task> addExisting, Func<bool> failed)
    {
        List<TagEntryItem> done = new();
        foreach (TagEntryItem item in entry.Items)
        {
            await (item.Id is { } id ? addExisting(id) : addNew(item.Name.Display));
            if (failed()) break;
            done.Add(item);
        }
        return done;
    }

    /// <summary>"piss (from pee), cute, anal": what an entry added, with the names an alias mapped.</summary>
    internal static string Describe(IEnumerable<TagEntryItem> items) => string.Join(", ", items.Select(item => item.Mapped
        ? Texts.T("tags.entry.mapped", item.Name.Display, item.Typed.Trim()) : item.Name.Display));

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
            if (!quoted && (c == ' ' || c == ',')) start = index + 1;
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
        if (!name.Any(c => char.IsWhiteSpace(c) || c is ',' or '"' or '\\')) return name;
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
            if (c == ',') { Flush(); comma = true; parts.Add(new()); continue; }
            // Only the plain space separates: a tab or line break stays in the name, which the name rule then refuses as before.
            if (c == ' ') { Flush(); continue; }
            word.Append(c);
        }
        Flush();
        return parts;
    }
}
