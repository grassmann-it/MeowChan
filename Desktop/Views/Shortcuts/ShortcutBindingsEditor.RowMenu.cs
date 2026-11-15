using System;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Views.Shortcuts;

// The context menu of the shortcuts table (audit V16): right-click on an action row, the Menu key or Shift+F10 on the selected one. It offers what the edit bar
// below the table offers for the selected action (record a new shortcut, record an additional one, restore the default, clear the binding) and runs the very same
// actions, so nothing the menu does can differ from the buttons. A section row (a group's name) has no menu; while a shortcut is being recorded there is none either.
public sealed partial class ShortcutBindingsEditor
{
    private ContextMenuHost? _rowMenu;

    private void InitializeRowMenu()
    {
        ContextMenuTarget target = new(_actions);
        _rowMenu = new ContextMenuHost(_actions,
            () => ComposeRowMenu(target.IsPointer ? target.Find<ListBoxItem>() : _actions.SelectedItem as ListBoxItem),
            () => _actions.Focus());
    }

    /// <summary>The menu host of the table; read by tests.</summary>
    internal ContextMenuHost? RowMenuHost => _rowMenu;

    /// <summary>The menu of an action row as it would open now (null: no menu); the row becomes the selected one first, as it does for a click. Also the test seam.</summary>
    internal ContextMenuBuilder? ComposeRowMenu(ListBoxItem? row)
    {
        if (row?.Tag is not string id || ShortcutService.IsCapturing || !CommandCatalog.TryGet(id, out CommandDefinition? command) || command is null) return null;
        if (!ReferenceEquals(_actions.SelectedItem, row)) _actions.SelectedItem = row;
        // The selection change refreshed the edit bar: its enabled state is the rule of every action below (a read-only draft, an inactive command, a running save).
        bool editable = _selected == id && _detail.IsEnabled;
        string? Unavailable() => editable ? null : Texts.T("settings.shortcutsEditor.unavailable");
        int count = _session.Requested(id).Count;
        return new ContextMenuBuilder().Named(Texts.T("shortcuts.window.shortcut"))
            .Item("shortcuts.record", Texts.T("shortcuts.window.recordReplace"), () => StartRecording(replace: true), editable, Unavailable(), icon: "edit-2")
            .Item("shortcuts.record-additional", Texts.T("settings.shortcutsEditor.recordAdditional"), () => StartRecording(replace: false),
                editable && count < ShortcutDraftSession.GestureLimit,
                editable ? Texts.T("settings.shortcutsEditor.gestureLimit") : Unavailable(), icon: "plus")
            .Section()
            .Item("shortcuts.restore-default", Texts.T("settings.shortcutsEditor.restoreDefault"), () => _session.RestoreDefault(id), editable, Unavailable(), icon: "rotate-ccw")
            .Item("shortcuts.clear", Texts.T("settings.shortcutsEditor.clearBinding"), () => _session.Clear(id), editable && count > 0,
                editable ? Texts.T("settings.shortcutsEditor.unbound") : Unavailable(), icon: "x");
    }
}
