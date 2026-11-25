using System;
using Avalonia.Controls;
using MeowChan.Tags;

namespace MeowChan.Desktop.Controls;

internal static class OwnTagChipMenu
{
    internal static ContextMenuBuilder IdentityActions(Control chip, string name, TagId? id, Action<TagId>? filter)
    {
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(name);
        if (id is { } key && filter != null)
            menu.Command("tags.filter-include", () => filter(key), showShortcut: false);
        return menu.Command("tags.copy-name",
            () => OpenActions.Start(() => MenuClipboard.CopyAsync(chip, name), "tags.copy-name"), showShortcut: false);
    }
}
