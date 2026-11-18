using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Controls;

/// <summary>What an entry of a context menu is.</summary>
internal enum ContextMenuEntryKind
{
    /// <summary>An action; with a catalog command id it shows the command's icon and shortcut.</summary>
    Command,
    /// <summary>One value of a group of exclusive choices; exactly one entry of a group is checked.</summary>
    Radio,
    /// <summary>A switch.</summary>
    Check,
    /// <summary>A nested menu, used when a choice has a variable number of values.</summary>
    Submenu,
    /// <summary>A disabled explanatory sentence.</summary>
    Note
}

/// <summary>One entry of a context menu, as plain data (no control); <see cref="Invoke"/> is what a click does. <see cref="Icon"/> is the
/// catalog mask of an entry that is not a catalog command (a command brings its own), so the icon column stays filled beside commands.</summary>
internal sealed record ContextMenuEntry(ContextMenuEntryKind Kind, string Id, string Label, string? CommandId, string? Group,
    bool Checked, bool Available, string? Reason, Action? Invoke, bool ShowShortcut = true,
    Func<bool>? Availability = null, Func<string?>? ReasonFactory = null, Func<string>? LabelFactory = null,
    IReadOnlyList<IReadOnlyList<ContextMenuEntry>>? Children = null, string? Icon = null);

/// <summary>
/// A composed context menu: sections of entries (a separator sits between two sections). Pure data, so what a menu offers can be
/// tested without a window; <see cref="ContextMenuBuilder.Build"/> turns it into the Avalonia menu.
/// </summary>
internal sealed class ContextMenuModel
{
    /// <summary>The most entries one section should hold (a documented ceiling of the menu design, not a setting): longer groups become a submenu or a dialog.</summary>
    internal const int MaxEntriesPerSection = 9;

    internal ContextMenuModel(IReadOnlyList<IReadOnlyList<ContextMenuEntry>> sections) => Sections = sections;

    internal IReadOnlyList<IReadOnlyList<ContextMenuEntry>> Sections { get; }

    internal IEnumerable<ContextMenuEntry> Entries => Sections.SelectMany(section => section);

    /// <summary>The indexes of the sections that are longer than <see cref="MaxEntriesPerSection"/>.</summary>
    internal IReadOnlyList<int> OversizedSections =>
        Enumerable.Range(0, Sections.Count).Where(index => Sections[index].Count > MaxEntriesPerSection).ToArray();

    internal bool IsEmpty => Sections.Count == 0;
}
