using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>The order of the Booru mode's tag list: most frequent first, or by name.</summary>
internal enum MediaTagListSort { Count, Name }

/// <summary>What a row of the tag list is: a group heading (collapsible), a tag, the "Show all / Show fewer" switch of a group, or a note.</summary>
internal enum MediaTagListKind { Header, Tag, More, Note }

/// <summary>What a gesture on a tag row asks for (<see cref="MediaTagList.Resolve"/> turns it into a change of the filter).</summary>
internal enum MediaTagListGesture { Include, Exclude }

/// <summary>The change of the filter a gesture on a tag row makes.</summary>
internal enum MediaTagListChange { None, Include, Exclude, Remove }

/// <summary>
/// One row of the tag list. A tag row carries the term it stands for (<see cref="Term"/>, included polarity) and whether the filter
/// holds it now (<see cref="Polarity"/>: 1 included, -1 excluded, 0 not); a heading its tag count and whether it is collapsed; the
/// "More" row how many tags it hides (<see cref="Hidden"/>) or that everything is shown (<see cref="ShowingAll"/>).
/// </summary>
internal sealed record MediaTagListItem(MediaTagListKind Kind, MediaTagGroup Group, string Key, string Name, int Count = 0, int Polarity = 0,
    bool Collapsed = false, int Hidden = 0, bool ShowingAll = false, MediaFilterTerm? Term = null)
{
    /// <summary>A stable identity of the row across rebuilds (to keep selection and keyboard focus on it).</summary>
    internal string Identity => Kind + "/" + Group + "/" + Key;

    /// <summary>What a screen reader hears for the row: name, count and state (and the group state for a heading).</summary>
    internal string AccessibleName => Kind switch
    {
        MediaTagListKind.Tag => Texts.Count("media.tags.itemName", Count, Name, Count.ToString("N0", Texts.Culture))
            + (Polarity > 0 ? ", " + Texts.T("media.tags.state.included") : Polarity < 0 ? ", " + Texts.T("media.tags.state.excluded") : string.Empty),
        MediaTagListKind.Header => Texts.T(Collapsed ? "media.tags.group.collapsedName" : "media.tags.group.expandedName", Name),
        _ => Name
    };
}

/// <summary>What the tag list shows: order, the text typed into its search box, the top-N limit (a setting) and the open/closed groups.</summary>
internal sealed record MediaTagListOptions(MediaTagListSort Sort, string? Query, int Top, IReadOnlySet<MediaTagGroup> ShowAll, IReadOnlySet<MediaTagGroup> Collapsed)
{
    internal static MediaTagListOptions Default { get; } = new(MediaTagListSort.Count, null, MeowChan.Config.MediaTagListTopDefault, new HashSet<MediaTagGroup>(), new HashSet<MediaTagGroup>());
}

/// <summary>
/// MP4: the rows of the Booru mode's tag list, pure. Per group (own tags, then source tags): a heading with the number of distinct tags,
/// then the tags the filter holds (included first, then excluded; always shown, also with a count of 0, since they are the way back),
/// then the other tags that match the search text, ordered by count (ties by name) or by name, cut to the top-N unless the group shows
/// all, then "Show all (n)" / "Show fewer". A collapsed group shows only its heading. The source group appears only when the results hold
/// provider media or the filter holds a source tag.
/// </summary>
internal static class MediaTagList
{
    internal static IReadOnlyList<MediaTagListItem> Arrange(MediaTagCountResult? counts, IReadOnlyList<MediaFilterTerm> terms, MediaTagListOptions options,
        Func<Guid, string?> ownName)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ownName);
        MediaTagCountResult result = counts ?? MediaTagCountResult.Empty;
        List<MediaTagListItem> rows = new();
        AddGroup(rows, MediaTagGroup.Own, result.Own, terms.Where(term => term.Field == MediaFilterField.Tag).ToArray(), result.OwnAvailable, options,
            term => ownName(Guid.Parse(term.Value)) ?? Texts.T("tags.filter.deleted"));
        MediaFilterTerm[] sourceTerms = terms.Where(term => term.Field == MediaFilterField.SourceTag).ToArray();
        if (result.SourceMedia > 0 || result.Source.Count > 0 || sourceTerms.Length > 0)
            AddGroup(rows, MediaTagGroup.Source, result.Source, sourceTerms, result.SourceAvailable, options, term => term.Value);
        return rows;
    }

    private static void AddGroup(List<MediaTagListItem> rows, MediaTagGroup group, IReadOnlyList<MediaTagCount> counts, MediaFilterTerm[] active,
        bool available, MediaTagListOptions options, Func<MediaFilterTerm, string> nameOf)
    {
        bool collapsed = options.Collapsed.Contains(group);
        string heading = Texts.T(group == MediaTagGroup.Own ? "media.tags.group.own" : "media.tags.group.source", counts.Count.ToString("N0", Texts.Culture));
        rows.Add(new MediaTagListItem(MediaTagListKind.Header, group, "header", heading, counts.Count, Collapsed: collapsed));
        if (collapsed) return;
        if (!available)
        {
            rows.Add(Note(group, "unavailable", Texts.T(group == MediaTagGroup.Own ? "media.tags.ownUnavailable" : "media.tags.sourceUnavailable")));
            if (active.Length == 0) return;
        }
        Dictionary<string, MediaTagCount> byKey = new(StringComparer.Ordinal);
        foreach (MediaTagCount count in counts) byKey.TryAdd(count.Key, count);
        HashSet<string> held = new(StringComparer.Ordinal);
        foreach (MediaFilterTerm term in active.OrderBy(term => term.Exclude))
        {
            if (!held.Add(term.Value)) continue;
            int count = byKey.TryGetValue(term.Value, out MediaTagCount found) ? found.Count : 0;
            string name = byKey.TryGetValue(term.Value, out found) ? found.Name : nameOf(term);
            rows.Add(new MediaTagListItem(MediaTagListKind.Tag, group, term.Value, name, count, term.Exclude ? -1 : 1, Term: term with { Exclude = false }));
        }
        string query = (options.Query ?? string.Empty).Trim();
        IEnumerable<MediaTagCount> others = counts.Where(count => !held.Contains(count.Key));
        if (query.Length > 0) others = others.Where(count => Matches(count.Name, query));
        MediaTagCount[] ordered = (options.Sort == MediaTagListSort.Name
            ? others.OrderBy(count => count.Name, StringComparer.OrdinalIgnoreCase).ThenBy(count => count.Name, StringComparer.Ordinal)
            : others.OrderByDescending(count => count.Count).ThenBy(count => count.Name, StringComparer.OrdinalIgnoreCase).ThenBy(count => count.Name, StringComparer.Ordinal))
            .ToArray();
        int top = Math.Max(1, options.Top);
        bool showAll = options.ShowAll.Contains(group);
        int shown = showAll ? ordered.Length : Math.Min(top, ordered.Length);
        for (int i = 0; i < shown; i++)
        {
            MediaTagCount count = ordered[i];
            rows.Add(new MediaTagListItem(MediaTagListKind.Tag, group, count.Key, count.Name, count.Count,
                Term: group == MediaTagGroup.Own ? MediaFilterTerm.Tag(Guid.Parse(count.Key)) : MediaFilterTerm.SourceTag(count.Key)));
        }
        if (ordered.Length > top)
            rows.Add(showAll
                ? new MediaTagListItem(MediaTagListKind.More, group, "more", Texts.T("media.tags.showFewer"), ShowingAll: true)
                : new MediaTagListItem(MediaTagListKind.More, group, "more", Texts.T("media.tags.showAll", ordered.Length.ToString("N0", Texts.Culture)), Hidden: ordered.Length - shown));
        if (ordered.Length == 0 && held.Count == 0 && available)
            rows.Add(Note(group, "none", query.Length > 0 ? Texts.T("media.tags.noMatch", query) : Texts.T("media.tags.none")));
    }

    private static MediaTagListItem Note(MediaTagGroup group, string key, string text) => new(MediaTagListKind.Note, group, key, text);

    // The search box finds a tag by any part of its name; case and the underscores of provider names are ignored ("blue eyes" finds blue_eyes).
    internal static bool Matches(string name, string query)
    {
        static string Fold(string text) => text.Replace('_', ' ').Trim();
        return Fold(name).Contains(Fold(query), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What a gesture on a tag row does with the filter. A click (include) adds the tag, takes an included tag out again and turns an
    /// excluded one into an included one; Alt+click, Shift+Enter or the menu's "Exclude" (exclude) works the same way the other round.
    /// </summary>
    internal static MediaTagListChange Resolve(int polarity, MediaTagListGesture gesture) => (polarity, gesture) switch
    {
        (> 0, MediaTagListGesture.Include) => MediaTagListChange.Remove,
        (< 0, MediaTagListGesture.Exclude) => MediaTagListChange.Remove,
        (_, MediaTagListGesture.Include) => MediaTagListChange.Include,
        _ => MediaTagListChange.Exclude
    };
}
