using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Views.Shortcuts;

/// <summary>
/// Where the shortcuts window was opened from (modals plan KS2). F1 passes the area the user works in, the commands that
/// matter there in their order (the former F1 list), which of them this work view keeps under More, and the notes of the
/// old help; the window opens on that area, expanded and on top, with "Assigned only" set. Settings opens it without a context.
/// </summary>
public sealed record ShortcutHelpContext(
    string? Area,
    IReadOnlyList<string> Preferred,
    Func<string, bool>? UnderMore = null,
    CommandScope ScopeContext = CommandScope.None,
    IReadOnlyList<string>? NoteKeys = null,
    bool AssignedOnly = true)
{
    /// <summary>The context of the Settings button: every area, every action, nothing preselected.</summary>
    public static ShortcutHelpContext Settings { get; } = new(null, Array.Empty<string>(), AssignedOnly: false);

    /// <summary>The group key of a workspace page name ("Archive" gives "archive"); the large reader is the reader.</summary>
    public static string? AreaFor(string pageName, bool largeReaderOpen)
    {
        if (largeReaderOpen) return ShortcutGroups.Reader;
        string key = pageName.ToLowerInvariant();
        return ShortcutGroups.Order.Contains(key) ? key : null;
    }
}
