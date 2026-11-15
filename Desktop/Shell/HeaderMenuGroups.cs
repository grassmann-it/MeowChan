using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>The labelled groups of the header menu (the "⋯" button), in the order they appear.</summary>
internal enum HeaderMenuGroup
{
    /// <summary>"Reading": continue reading, the reading history, post bookmarks, reader groups.</summary>
    Read,
    /// <summary>"Collection": import, Booru, profile.</summary>
    Collection,
    /// <summary>"App": pause, appearance, settings, shortcuts, updates, about.</summary>
    App,
    /// <summary>Exit, set apart by a separator and without a label of its own.</summary>
    Exit
}

internal enum HeaderMenuEntryKind { Label, Item, Separator }

/// <summary>One line of the arranged header menu: a group label, an entry (by its key) or a separator.</summary>
internal sealed record HeaderMenuEntry(HeaderMenuEntryKind Kind, string Key, HeaderMenuGroup Group);

/// <summary>
/// Review of 15.11.2026, item 13: the entries of the header menu in three labelled groups (Reading, Collection, App) with Exit apart,
/// instead of one list that the tools inserted themselves into. Pure: <see cref="Arrange"/> takes the keys of the entries that exist
/// and says what the menu looks like; the window maps the keys to its controls. "Go through the tagging queue" is not an entry any more
/// (the command and its shortcut stay; the Media page and the Tagging page open it).
/// </summary>
internal static class HeaderMenuGroups
{
    private static readonly (HeaderMenuGroup Group, string[] Keys)[] Layout =
    [
        (HeaderMenuGroup.Read, ["resume", "history", "postBookmarks", "readerGroups"]),
        (HeaderMenuGroup.Collection, ["import", "booru", "profile"]),
        (HeaderMenuGroup.App, ["pause", "theme", "fullscreen", "view", "settings", "shortcuts", "updates", "about"]),
        (HeaderMenuGroup.Exit, ["exit"])
    ];

    /// <summary>The groups in display order.</summary>
    internal static IReadOnlyList<HeaderMenuGroup> Groups { get; } = Layout.Select(item => item.Group).ToArray();

    /// <summary>The keys of a group in display order.</summary>
    internal static IReadOnlyList<string> Keys(HeaderMenuGroup group) => Layout.First(item => item.Group == group).Keys;

    /// <summary>The text key of the label of a group, or null for the unlabelled one.</summary>
    internal static string? LabelKey(HeaderMenuGroup group) => group switch
    {
        HeaderMenuGroup.Read => "menu.group.read",
        HeaderMenuGroup.Collection => "menu.group.collection",
        HeaderMenuGroup.App => "menu.group.app",
        _ => null
    };

    /// <summary>The group of an entry; an entry this table does not know belongs to the collection (it is not lost).</summary>
    internal static HeaderMenuGroup GroupOf(string key)
    {
        foreach ((HeaderMenuGroup group, string[] keys) in Layout)
            if (keys.Contains(key)) return group;
        return HeaderMenuGroup.Collection;
    }

    /// <summary>
    /// The menu for the entries that exist: per group its label (when it has one) and its entries, a separator between two groups, nothing
    /// for a group without entries. Unknown keys follow the known entries of the collection.
    /// </summary>
    internal static IReadOnlyList<HeaderMenuEntry> Arrange(IEnumerable<string> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        string[] keys = present.Distinct().ToArray();
        List<HeaderMenuEntry> result = [];
        foreach ((HeaderMenuGroup group, string[] known) in Layout)
        {
            IEnumerable<string> inGroup = known.Where(keys.Contains);
            if (group == HeaderMenuGroup.Collection) inGroup = inGroup.Concat(keys.Where(key => !Layout.Any(item => item.Keys.Contains(key))));
            string[] items = inGroup.ToArray();
            if (items.Length == 0) continue;
            if (result.Count > 0) result.Add(new HeaderMenuEntry(HeaderMenuEntryKind.Separator, "|", group));
            if (LabelKey(group) is { } label) result.Add(new HeaderMenuEntry(HeaderMenuEntryKind.Label, label, group));
            result.AddRange(items.Select(key => new HeaderMenuEntry(HeaderMenuEntryKind.Item, key, group)));
        }
        return result;
    }
}
