using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one virtualized list of the tag dialogs: section labels and toggle rows (<see cref="TagToggleRow"/>). Up and Down
/// (and Home/End) move between tags and skip the labels, Space or a click toggles the row under the cursor; the owner
/// decides what a toggle writes (<see cref="Toggled"/>). Only realized rows have controls, so 20,000 tags cost a screenful.
/// </summary>
internal sealed class TagToggleListBox : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <summary>Raised for a click or Space on a tag row; never for a section label.</summary>
    internal event Action<TagToggleRow>? Toggled;

    internal TagToggleListBox()
    {
        SelectionMode = SelectionMode.Single;
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        ItemTemplate = new FuncDataTemplate<TagToggleRow>((_, _) => new TagToggleRowView());
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ContainerPrepared += OnContainerPrepared;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        GotFocus += OnGotFocus;
    }

    internal IReadOnlyList<TagToggleRow> Rows => ItemsSource as IReadOnlyList<TagToggleRow> ?? Array.Empty<TagToggleRow>();
    internal TagToggleRow? Current => SelectedItem is TagToggleRow { IsHeader: false } row ? row : null;

    /// <summary>Shows new rows. The cursor stays on <paramref name="keep"/> when it is still listed.</summary>
    internal void Show(IReadOnlyList<TagToggleRow> rows, TagToggleRow? keep = null)
    {
        bool focused = IsKeyboardFocusWithin;
        ItemsSource = rows;
        int at = keep == null ? -1 : IndexOf(rows, keep);
        SelectedIndex = at;
        if (at >= 0 && focused) MoveTo(at);
    }

    private static int IndexOf(IReadOnlyList<TagToggleRow> rows, TagToggleRow row)
    {
        for (int at = 0; at < rows.Count; at++) if (ReferenceEquals(rows[at], row)) return at;
        return -1;
    }

    /// <summary>Puts the cursor and the keyboard focus on a tag row.</summary>
    internal void MoveTo(int index)
    {
        if (index < 0 || index >= Rows.Count || Rows[index].IsHeader) return;
        SelectedIndex = index;
        ScrollIntoView(index);
        if (ContainerFromIndex(index) is ListBoxItem item) item.Focus(NavigationMethod.Directional);
    }

    internal void Focus(TagToggleRow row)
    {
        int at = IndexOf(Rows, row);
        if (at >= 0) MoveTo(at);
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ListBoxItem item) return;
        // Containers are recycled between labels and tags, so both values are set every time.
        bool header = item.DataContext is TagToggleRow { IsHeader: true };
        item.Focusable = !header;
        item.IsHitTestVisible = !header;
        item.MinHeight = header ? 0 : 28;
    }

    private void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        // Tab into the list lands on the remembered tag, or on the first one.
        if (!ReferenceEquals(e.Source, this)) return;
        int at = SelectedItem is TagToggleRow { IsHeader: false } ? SelectedIndex : TagToggleList.Edge(Rows, first: true);
        if (at >= 0) MoveTo(at);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || Rows.Count == 0) return;
        int target = e.Key switch
        {
            Key.Down => TagToggleList.Step(Rows, SelectedIndex, +1),
            Key.Up => TagToggleList.Step(Rows, SelectedIndex, -1),
            Key.Home => TagToggleList.Edge(Rows, first: true),
            Key.End => TagToggleList.Edge(Rows, first: false),
            _ => int.MinValue
        };
        if (target != int.MinValue)
        {
            e.Handled = true;
            MoveTo(target);
            return;
        }
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            if (Current is { } row) Toggled?.Invoke(row);
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is not TagToggleRow { IsHeader: false } row)
            return;
        e.Handled = true;
        Toggled?.Invoke(row);
    }

    /// <summary>Raises the toggle of a row as Space or a click would (smoke checks and tests).</summary>
    internal void ToggleForTests(TagToggleRow row) { if (!row.IsHeader) Toggled?.Invoke(row); }
}

/// <summary>
/// One realized row: a section label, or check mark, name, the "{n} of {total}" of a selection and the uses of a tag. It follows its row's changes, so a toggle
/// repaints one row; the list item it sits in carries the accessible name with the state.
/// </summary>
internal sealed class TagToggleRowView : Grid
{
    private readonly CheckBox _box = new()
    {
        Focusable = false, IsHitTestVisible = false, MinWidth = 0, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        [AutomationProperties.AccessibilityViewProperty] = AccessibilityView.Raw
    };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Classes = { "workspace-meta" } };
    private readonly TextBlock _uses = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), MinWidth = 28, TextAlignment = TextAlignment.Right, Classes = { "workspace-meta" } };
    private TagToggleRow? _row;

    internal TagToggleRowView()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto");
        Children.Add(_box);
        Children.Add(_name); SetColumn(_name, 1);
        Children.Add(_count); SetColumn(_count, 2);
        Children.Add(_uses); SetColumn(_uses, 3);
    }

    internal CheckBox Box => _box;
    internal TextBlock CountText => _count;
    internal TextBlock UsesText => _uses;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_row != null) _row.PropertyChanged -= OnRowChanged;
        _row = DataContext as TagToggleRow;
        if (_row != null) _row.PropertyChanged += OnRowChanged;
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        if (_row is not { } row) return;
        bool header = row.IsHeader;
        _box.IsVisible = !header;
        _box.IsThreeState = row.Many;
        _box.IsChecked = row.IsChecked;
        _name.Text = row.Label;
        _name.FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal;
        _name.Margin = header ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        _count.Text = row.CountText;
        _count.IsVisible = !header && row.CountText.Length > 0;
        _uses.Text = row.UsesText;
        _uses.IsVisible = !header;
        ToolTip.SetTip(this, header ? null : row.ToolTipText);
        if (this.FindAncestorOfType<ListBoxItem>() is { } item) AutomationProperties.SetName(item, row.AccessibleName);
    }
}
