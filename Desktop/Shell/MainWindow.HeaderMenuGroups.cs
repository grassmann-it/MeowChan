using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    private bool _headerMenuArranged;

    /// <summary>
    /// Puts the entries of the header menu in the three labelled groups of <see cref="HeaderMenuGroups"/> (Reading, Collection, App, then
    /// Exit apart). Runs once after every part of the window has inserted its entry (the entries find their own place by anchors until
    /// then); calling it again keeps the arrangement.
    /// </summary>
    private void ArrangeHeaderMenu()
    {
        if (_headerMenuArranged) return;
        _headerMenuArranged = true;
        Dictionary<string, Control?> known = new()
        {
            ["resume"] = _resumeReadingMenu, ["history"] = _readingHistoryMenu, ["postBookmarks"] = _postBookmarksMenu,
            ["readerGroups"] = ReaderGroupsMenuItem, ["import"] = ManualImportMenuItem, ["booru"] = _booruMenu, ["profile"] = _profileMenu,
            ["pause"] = PauseMenuItem, ["theme"] = _themeMenu, ["fullscreen"] = _fullscreenMenu, ["view"] = _workViewGroupMenu,
            ["settings"] = SettingsMenuItem, ["shortcuts"] = ShortcutHelpMenuItem, ["updates"] = _checkUpdatesMenu, ["about"] = AboutMenuItem,
            ["exit"] = ExitMenuItem
        };
        // Whatever else is in the menu (an entry a later change adds) is kept, behind the collection's own entries.
        List<Control> others = WorkspaceMenuItem.Items.OfType<Control>()
            .Where(item => item is not Separator && !known.Values.Contains(item)).ToList();
        for (int index = 0; index < others.Count; index++) known["other:" + index] = others[index];
        Dictionary<string, Control> present = known.Where(pair => pair.Value != null).ToDictionary(pair => pair.Key, pair => pair.Value!);
        WorkspaceMenuItem.Items.Clear();
        foreach (HeaderMenuEntry entry in HeaderMenuGroups.Arrange(present.Keys))
        {
            switch (entry.Kind)
            {
                case HeaderMenuEntryKind.Separator:
                    WorkspaceMenuItem.Items.Add(new Separator());
                    break;
                case HeaderMenuEntryKind.Label:
                    WorkspaceMenuItem.Items.Add(HeaderMenuLabel(entry.Group, entry.Key));
                    break;
                default:
                    WorkspaceMenuItem.Items.Add(present[entry.Key]);
                    break;
            }
        }
    }

    // A disabled, unfocusable line that names the group; "localized" with its key as Tag, so a language change relabels it (LocalizeWorkspace).
    private static MenuItem HeaderMenuLabel(HeaderMenuGroup group, string textKey)
    {
        string text = MeowChan.Desktop.Localization.Texts.T(textKey);
        MenuItem label = new()
        {
            Name = "HeaderMenuLabel" + group, Header = text, Tag = textKey, IsEnabled = false, Focusable = false, IsHitTestVisible = false
        };
        label.Classes.Add("localized");
        label.Classes.Add("menu-group-label");
        AutomationProperties.SetName(label, text);
        return label;
    }

    /// <summary>The arranged menu as plain data: label text, entry names or separators (smoke checks and tests).</summary>
    internal IReadOnlyList<string> HeaderMenuShape() => WorkspaceMenuItem.Items.OfType<Control>()
        .Select(item => item is Separator ? "|" : item.Name ?? (item as MenuItem)?.Header?.ToString() ?? "?").ToArray();
}
