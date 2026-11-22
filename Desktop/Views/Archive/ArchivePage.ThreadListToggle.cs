using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The reader toolbar's "Threads" switch: the archive's thread list on or off while the window is wide enough for list and
// reader side by side. It reuses the narrow-window path of UpdateLayoutMode (list column and splitter collapse, the reader takes the width); the width
// the list had stays in ArchiveSplitWidth. A narrow window has its own "Back to the thread list" and keeps it; the large reader hosts the reader
// elsewhere. The state is Config.ArchiveThreadListHidden (default off, no default shortcut). With the list hidden there is no Left/Right
// thread stepping (the list owns that scope); the large reader keeps its own.
public partial class ArchivePage
{
    private const string ThreadListCommandId = "archive.thread-list.toggle";
    private bool _threadListHidden;

    /// <summary>Saves the switch (Config.ArchiveThreadListHidden); set by the window.</summary>
    internal Func<bool, Task>? SaveThreadListHiddenAsync { get; set; }

    /// <summary>The stored state of the switch; applying it relays the layout.</summary>
    internal bool ArchiveThreadListHiddenPreference
    {
        get => _threadListHidden;
        set { if (_threadListHidden == value) return; _threadListHidden = value; UpdateLayoutMode(); }
    }

    /// <summary>Whether the list pane is hidden by the switch right now (a narrow window or the large reader never counts: they have their own paths).</summary>
    internal bool IsThreadListHiddenBySwitch => _threadListHidden && CanShowArchivePreviewBesideList && !_largeReader;

    /// <summary>Whether the switch is offered: wide enough for list and reader side by side, embedded reader.</summary>
    internal bool CanToggleThreadList => !_ended && !_largeReader && CanShowArchivePreviewBesideList;

    private void InitializeThreadListToggle()
    {
        CommandBindings.ShowIconBeforeText(ArchiveThreadListToggle, CommandCatalog.Get(ThreadListCommandId).Icon);
        ArchiveThreadListToggle.Click += (_, e) => { e.Handled = true; ToggleThreadList(); };
    }

    internal void ToggleThreadList()
    {
        if (!CanToggleThreadList) return;
        SetThreadListHidden(!_threadListHidden);
    }

    /// <summary>The search field and the filter surface live in the list column: a request for either shows the list again.</summary>
    private void ShowThreadListForListCommand()
    {
        if (_threadListHidden && CanToggleThreadList) SetThreadListHidden(false);
    }

    private void SetThreadListHidden(bool hidden)
    {
        if (_threadListHidden == hidden) return;
        // The focus leaves a column that is about to collapse: it goes to the reader.
        bool focusInList = hidden && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
            && focused.GetSelfAndVisualAncestors().Any(visual => ReferenceEquals(visual, ArchiveListPane));
        _threadListHidden = hidden;
        UpdateLayoutMode();
        if (focusInList) FocusSelectedPostOrList();
        if (SaveThreadListHiddenAsync is { } save) OpenActions.Start(() => save(hidden), "archive.threadList.save");
    }

    /// <summary>Visibility, state, tooltip and name of the switch; called by the layout and by the language refresh.</summary>
    private void RefreshThreadListToggle()
    {
        if (ArchiveThreadListToggle is not { } button) return;
        bool offered = CanToggleThreadList;
        bool listShown = !IsThreadListHiddenBySwitch;
        if (button.IsVisible != offered) button.IsVisible = offered;
        if (button.IsChecked != listShown) button.IsChecked = listShown;
        string label = CommandCatalog.Label(ThreadListCommandId);
        button.Content = Texts.T("archive.threadList.compact");
        string tip = CommandBindings.Tooltip(ThreadListCommandId, null, Texts.T(listShown ? "archive.threadList.hide" : "archive.threadList.show"));
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, label + ": " + Texts.T(listShown ? "window.fullscreen.on" : "window.fullscreen.off"));
        Avalonia.Automation.AutomationProperties.SetHelpText(button, tip);
    }

    /// <summary>The command of the switch (a user shortcut); the window registers it for the embedded archive.</summary>
    internal void ToggleThreadListByShortcut() => ToggleThreadList();
}
