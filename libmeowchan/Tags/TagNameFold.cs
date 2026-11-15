using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MeowChan.Tags;

/// <summary>Why two own tags are offered as probable duplicates.</summary>
internal enum TagDuplicateReason
{
    /// <summary>Only case, spacing, underscores, a leading <c>#</c> or surrounding slashes (<c>/x/</c>) differ.</summary>
    Spelling,
    /// <summary>Additionally a trailing plural <c>s</c>, hyphens, dots or apostrophes differ.</summary>
    Similar
}

/// <summary>A group of own tags that probably name the same thing, most used first. Only a suggestion; nothing is merged.</summary>
internal sealed record TagDuplicateGroup(IReadOnlyList<PrivateTagDefinition> Tags, TagDuplicateReason Reason);

/// <summary>
/// The name normaliser of tag merging and aliases. Two levels:
/// <list type="bullet">
/// <item><see cref="Fold"/>: the tag comparison key (<see cref="PrivateTagName.Key"/>) without the decoration people type around
/// a name: surrounding slashes (<c>/abdl/</c>), leading <c>#</c>, underscores as spaces and repeated spaces. Aliases resolve with
/// it, so <c>/PEE/</c> finds the alias <c>pee</c>.</item>
/// <item><see cref="Loose"/>: <see cref="Fold"/> plus a trailing plural <c>s</c>, hyphens, dots and apostrophes. Used for
/// duplicate suggestions only, never to map a typed name by itself.</item>
/// </list>
/// Pure and culture-neutral (invariant upper case, NFC, like the tag key).
/// </summary>
internal static class TagNameFold
{
    /// <summary>The decoration-free comparison text of a name; the plain key when nothing would remain.</summary>
    internal static string Fold(PrivateTagName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Fold(name.Key);
    }

    /// <summary><see cref="Fold(PrivateTagName)"/> for a comparison key (or any text, which is upper-cased first).</summary>
    internal static string Fold(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string upper = key.Normalize(NormalizationForm.FormC).ToUpperInvariant();
        StringBuilder text = new(upper.Length);
        bool space = false;
        foreach (char c in upper)
        {
            if (c == '_' || char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
            if (space) { text.Append(' '); space = false; }
            text.Append(c);
        }
        string folded = text.ToString().Trim('/', ' ');
        int hash = 0;
        while (hash < folded.Length && folded[hash] == '#') hash++;
        folded = folded[hash..].Trim('/', ' ');
        return folded.Length == 0 ? upper.Trim() : folded;
    }

    /// <summary>The looser comparison text of duplicate suggestions (see the type summary).</summary>
    internal static string Loose(PrivateTagName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string folded = Fold(name.Key);
        StringBuilder text = new(folded.Length);
        foreach (char c in folded)
            if (c is not ('-' or '.' or '\'' or '’' or ' ')) text.Append(c);
        string loose = text.Length == 0 ? folded : text.ToString();
        // "CATS" → "CAT", but never "BUS"/"KISS" style words of three letters or a double s.
        if (loose.Length > 3 && loose[^1] == 'S' && loose[^2] != 'S') loose = loose[..^1];
        return loose;
    }

    /// <summary>
    /// Groups of two or more tags whose <see cref="Loose"/> texts are equal, most used first inside a group; groups with more
    /// uses first. The reason is <see cref="TagDuplicateReason.Spelling"/> when all of a group's <see cref="Fold"/> texts are equal.
    /// Suggestions only: the caller shows them, the person decides.
    /// </summary>
    internal static IReadOnlyList<TagDuplicateGroup> Duplicates(IEnumerable<PrivateTagDefinition> tags, IReadOnlyDictionary<TagId, int>? uses = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        int Uses(PrivateTagDefinition tag) => uses?.GetValueOrDefault(tag.Id) ?? 0;
        return tags.GroupBy(tag => Loose(tag.Name), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                PrivateTagDefinition[] ordered = group.OrderByDescending(Uses)
                    .ThenBy(tag => tag.Name.Display, StringComparer.InvariantCultureIgnoreCase)
                    .ThenBy(tag => tag.Name.Key, StringComparer.Ordinal).ToArray();
                bool spelling = ordered.Select(tag => Fold(tag.Name)).Distinct(StringComparer.Ordinal).Count() == 1;
                return new TagDuplicateGroup(ordered, spelling ? TagDuplicateReason.Spelling : TagDuplicateReason.Similar);
            })
            .OrderByDescending(group => group.Tags.Sum(Uses))
            .ThenBy(group => group.Tags[0].Name.Display, StringComparer.InvariantCultureIgnoreCase)
            .ToArray();
    }
}
