using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The tag part of a filter surface: a button "Add tag…" that opens a popover with a search field over the own tags that are not
/// in the filter yet. It replaces the tag combo box with its two text buttons: Enter (or a click) adds the highlighted tag as an
/// inclusion, Shift+Enter, Shift+click or a leading "-" as an exclusion, and the "Add as" choice does the same without a modifier.
/// The tags the filter already holds are shown as chips by the page (with their include/exclude state); this control only adds.
/// The ranking is the tag fields' own (<see cref="TagSuggestionIndex"/>: exact, prefix, word start, anywhere; more uses first).
/// <para>M2: a thin "tags only" form of <see cref="FilterTermPicker"/> (one component): no group headings, no comma, the
/// <c>tags.filter.*</c> texts and part names of before (<c>TagFilterPickerSearch</c>, <c>TagFilterPickerList</c>, …).</para>
/// </summary>
internal sealed class TagFilterPicker : FilterTermPicker
{
    public TagFilterPicker() : base(tagsOnly: true, "TagFilterPicker") => InitializeListMenu();

    private ContextMenuHost? _listMenu;

    // KM2: a right-click, the Menu key or Shift+F10 on a tag of the list: add it as an inclusion or an exclusion, copy its name.
    private void InitializeListMenu()
    {
        ContextMenuTarget target = new(List);
        _listMenu = new ContextMenuHost(List, () =>
        {
            FilterTermChoice? choice = (target.IsPointer ? target.Find<ListBoxItem>()?.DataContext : List.SelectedItem) as FilterTermChoice;
            TagSuggestion? suggestion = choice?.Tag;
            return suggestion?.Id is not { } id ? null : TagPickerMenu.Compose(suggestion.Name.Display,
                include: () => PickFromMenu(id.Value, exclude: false), exclude: () => PickFromMenu(id.Value, exclude: true),
                copyName: () => _ = MenuClipboard.CopyAsync(this, suggestion.Name.Display, _ => { }));
        }, fallbackFocus: () => Search.Focus());
    }

    private void PickFromMenu(Guid id, bool exclude)
    {
        if (Picked(id, exclude) != null) return; // the page shows its own reason; the popover stays open
        if (KeepQueryOnDismiss) ClearPending();
        Close();
    }

    /// <summary>The menu host of the tag list; read by tests.</summary>
    internal ContextMenuHost? ListMenuHost => _listMenu;

    /// <summary>Adds a tag (id, exclude); returns null when it was added, else the localized reason it was refused (shown in the popover).</summary>
    internal Func<Guid, bool, string?> Picked { get; set; } = (_, _) => null;

    /// <summary>The tags the list shows now, in their order.</summary>
    internal IReadOnlyList<TagSuggestion> Shown => ShownChoices.Select(choice => choice.Tag).OfType<TagSuggestion>().ToArray();

    protected override string? OnPicked(FilterTermChoice choice, bool exclude) =>
        choice.Field == MediaFilterField.Tag && choice.Tag?.Id is { } id ? Picked(id.Value, exclude) : null;
}
