using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;

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
        Content = new TextBlock { Text = _row?.ThreadFavorite == null ? "?" : marked ? "★" : "☆", FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        string label = ThreadFavoriteText.Action(marked);
        string description = ThreadFavoriteText.Hint(marked);
        if (CommandCatalog.ShortcutDisplay("thread.favorite-toggle") is string shortcut)
            description += " · " + shortcut;
        if (_row?.ThreadFavoriteError is string error) description += "\n" + error;
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, label + (_row?.ThreadFavoriteError is string problem ? ": " + problem : ""));
        AutomationProperties.SetHelpText(this, description);
    }
}
