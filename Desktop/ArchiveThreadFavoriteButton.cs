using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>A normal button: the displayed mark only changes after a successful persistent commit.</summary>
public sealed class ArchiveThreadFavoriteButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    private ArchiveThreadRow? _row;
    private bool _attached;
    private readonly Action _shortcutRefresh;

    public ArchiveThreadFavoriteButton()
    {
        _shortcutRefresh = RefreshForShortcuts;
        ShortcutService.Observe(_shortcutRefresh);
        Classes.Add(WorkspaceIcons.SquareClass);
        Width = MinWidth = 32;
        Height = MinHeight = 32;
        Padding = new Thickness(4);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        DataContextChanged += (_, _) => SetRow();
        AttachedToVisualTree += (_, _) => { _attached = true; SetRow(); };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            if (_row != null) _row.PropertyChanged -= OnRowChanged;
        };
        Click += async (_, e) =>
        {
            e.Handled = true;
            ArchiveThreadRow? row = _row;
            if (row?.CanToggleThreadFavorite != true || row.ToggleThreadFavoriteAsync == null) return;
            bool restoreFocus = IsFocused;
            await row.ToggleThreadFavoriteAsync();
            if (restoreFocus && _attached && IsEffectivelyVisible && IsEnabled && ReferenceEquals(DataContext, row)) Focus();
        };
        Refresh();
    }

    private void SetRow()
    {
        if (_row != null) _row.PropertyChanged -= OnRowChanged;
        _row = DataContext as ArchiveThreadRow;
        if (_attached && _row != null) _row.PropertyChanged += OnRowChanged;
        Refresh();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void RefreshForShortcuts()
    {
        Refresh();
        ShortcutService.Observe(_shortcutRefresh);
    }

    private void Refresh()
    {
        bool marked = _row?.ThreadFavorite == true;
        IsEnabled = _row?.CanToggleThreadFavorite == true;
        // An unknown state is an unmarked star, dimmed, that says it is loading; a "?" reads as help or as an error.
        bool loading = _row?.ThreadFavorite == null;
        // A drawn star (outline when not marked, filled when marked), not a font glyph; the icon keeps its Tag as the state.
        string icon = marked ? WorkspaceIcons.StarFilled : WorkspaceIcons.Star;
        if (WorkspaceIcons.KeyOf(Content) != icon) Content = WorkspaceIcons.Create(icon);
        if (Content is Control star) star.Opacity = loading ? 0.5 : 1;
        string label = ThreadFavoriteText.Action(marked);
        string description = loading && _row?.ThreadFavoriteError == null && _row != null
            ? Texts.T("archive.threadFavorite.loading") : ThreadFavoriteText.Hint(marked);
        ToolTip.SetShowOnDisabled(this, true);
        if (CommandCatalog.ShortcutDisplay("thread.favorite-toggle") is string shortcut)
            description += " · " + shortcut;
        if (_row?.ThreadFavoriteError is string error) description += "\n" + error;
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, label + (_row?.ThreadFavoriteError is string problem ? ": " + problem : ""));
        AutomationProperties.SetHelpText(this, description);
    }
}
