using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Type-ahead search of a list or table (audit V7): the first letters typed jump to the next row whose label starts with them. The Downloads and
/// Archive tables and the Plugins list use it; the DataGrid has no search of its own, so every surface gets the same behaviour from here (one
/// letter again and again steps through the rows with that letter, more letters narrow the match down). Letters that are shortcuts of the list
/// (the key goes to the command first) never reach it, and a text field inside a row keeps its own typing.
/// </summary>
internal sealed class TypeAheadSearch
{
    /// <summary>
    /// A pause this long starts a new word: letters typed within it add up, the next one after it begins again. One second is the usual
    /// time of a list's type-ahead (file managers, native lists); it is a typing rhythm, not a limit of the user's data, so no setting.
    /// </summary>
    internal static readonly TimeSpan ResetAfter = TimeSpan.FromMilliseconds(1000);

    private string _typed = string.Empty;
    private DateTime _last = DateTime.MinValue;

    /// <summary>The letters typed so far in the current word.</summary>
    internal string Typed => _typed;

    /// <summary>Whether <paramref name="text"/> (the text of a text input event) can be part of a word: printable, and not a space that starts it.</summary>
    internal bool Accepts(string? text) => !string.IsNullOrEmpty(text) && !char.IsControl(text[0]) && (_typed.Length > 0 || !char.IsWhiteSpace(text[0]));

    /// <summary>Forgets the word typed so far.</summary>
    internal void Reset() { _typed = string.Empty; _last = DateTime.MinValue; }

    /// <summary>
    /// Adds <paramref name="text"/> to the word (after a pause: a new word) and gives the index of the row to select, or -1 when no row matches. The
    /// search starts after <paramref name="current"/> for the first letter and for one letter repeated, and at <paramref name="current"/> when more
    /// letters narrow the word down (the row already selected stays if it still matches), and wraps around the list.
    /// </summary>
    internal int Next(IReadOnlyList<string?> labels, int current, string text, DateTime now)
    {
        if (now - _last > ResetAfter) _typed = string.Empty;
        _last = now;
        _typed += text;
        if (labels.Count == 0) return -1;
        bool cycle = IsOneLetterRepeated(_typed);
        string prefix = cycle ? _typed[..1] : _typed;
        int start = cycle || _typed.Length == text.Length ? current + 1 : Math.Max(current, 0);
        for (int offset = 0; offset < labels.Count; offset++)
        {
            int index = ((start + offset) % labels.Count + labels.Count) % labels.Count;
            if (labels[index] is { } label && label.AsSpan().TrimStart().StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) return index;
        }
        return -1;
    }

    /// <summary>"aaa" stands for the one letter "a" pressed again and again, never for the word "aaa".</summary>
    internal static bool IsOneLetterRepeated(string typed)
    {
        if (typed.Length < 2) return false;
        string first = typed[..1];
        for (int index = 1; index < typed.Length; index++)
            if (!string.Equals(typed.Substring(index, 1), first, StringComparison.CurrentCultureIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Type-ahead for a table: the rows are those of the table's view in the order shown (a sorted table jumps in its own order), <paramref name="label"/>
    /// is the text of a row the search compares, and a hit becomes the only selected row and scrolls into view like a move with the arrow keys.
    /// </summary>
    internal static TypeAheadSearch AttachDataGrid(DataGrid grid, Func<object, string?> label, Func<object?, bool> isListSource, Func<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(label);
        object[] items = [];
        return Attach(grid,
            () => { items = (grid.CollectionView?.Cast<object>() ?? grid.ItemsSource?.Cast<object>() ?? []).ToArray(); return items.Select(label).ToArray(); },
            () => grid.SelectedItem is { } current ? Array.IndexOf(items, current) : -1,
            index => { grid.SelectedItem = items[index]; grid.ScrollIntoView(items[index], null); },
            isListSource, enabled);
    }

    /// <summary>
    /// Makes <paramref name="surface"/> search by type: the rows come from <paramref name="rows"/> in the order shown (labels in the same order as the
    /// items), <paramref name="selected"/> is the index of the current row (-1 for none) and <paramref name="select"/> selects the row at an index and
    /// scrolls it into view. <paramref name="enabled"/> is the surface's own guard (a busy list, a closed page).
    /// </summary>
    internal static TypeAheadSearch Attach(Control surface, Func<IReadOnlyList<string?>> rows, Func<int> selected, Action<int> select,
        Func<object?, bool> isListSource, Func<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        TypeAheadSearch search = new();
        surface.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            if (e.Handled || enabled?.Invoke() == false || !isListSource(e.Source) || !search.Accepts(e.Text)) return;
            IReadOnlyList<string?> labels = rows();
            int index = search.Next(labels, selected(), e.Text!, DateTime.UtcNow);
            if (index < 0) return;
            e.Handled = true;
            select(index);
        }, RoutingStrategies.Tunnel);
        surface.AddHandler(InputElement.LostFocusEvent, (_, _) => search.Reset(), RoutingStrategies.Bubble);
        return search;
    }
}
