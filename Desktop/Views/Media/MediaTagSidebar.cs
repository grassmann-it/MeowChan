using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// MP4: the tag list of the media page's Booru mode, left of the grid. One virtualized list (one stop of the Tab key) holds both groups,
/// their headings and their "Show all" rows (MediaTagList.Arrange). Above it: the title with the order switch (count | name) and a search
/// box over the tag names; below the title a polite status line ("Counting…"). A click on a tag shows only media with it, Alt+click (or
/// Shift+click) hides them; Enter and Shift+Enter do the same from the keyboard, Delete takes the tag out of the filter, and the Menu key,
/// Shift+F10 or a right-click open the row's menu (include, exclude, show only this, copy the name, remove). Enter or a click on a heading
/// collapses or opens its group (Left/Right too), on "Show all" it lists the rest. The page does the filtering; this control only says what
/// the user asked for. Every row has a name with its count and state for a screen reader.
/// </summary>
internal sealed class MediaTagSidebar : UserControl
{
    /// <summary>The default width of the column in dip (MP5: the user's width is a setting, <see cref="MeowChan.Config.MediaTagListWidthMinimum"/> to <see cref="MeowChan.Config.MediaTagListWidthMaximum"/>; the page's body row sets it).</summary>
    internal const double ColumnWidth = MeowChan.Config.MediaTagListWidthDefault;

    // Narrow-window sweep: the title shares the head with the order box in a list that can be 180 dip narrow; trimmed, its full text is the tooltip.
    private readonly TextBlock _title = new TrimTipTextBlock { Name = "MediaTagSidebarTitle", Classes = { "section-title" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TwoWayToggle _sort = new() { Name = "MediaTagSidebarSort", VerticalAlignment = VerticalAlignment.Center };
    private readonly SearchBox _search = new() { Name = "MediaTagSidebarSearch", Debounce = TimeSpan.FromMilliseconds(150), MaxLength = MediaFilterTerm.MaximumSourceTagLength };
    private readonly TextBlock _status = new() { Name = "MediaTagSidebarStatus", Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ListBox _list = new() { Name = "MediaTagSidebarList", SelectionMode = SelectionMode.Single };
    private readonly HashSet<MediaTagGroup> _showAll = new();
    private readonly HashSet<MediaTagGroup> _collapsed = new();
    private MediaTagCountResult? _counts;
    private IReadOnlyList<MediaFilterTerm> _terms = Array.Empty<MediaFilterTerm>();
    private IReadOnlyList<MediaTagListItem> _items = Array.Empty<MediaTagListItem>();
    private MediaTagListItem? _menuRow;
    private bool _localizing;

    /// <summary>Called for a gesture on a tag row: the row's term (included polarity) and the change the gesture makes.</summary>
    internal Action<MediaFilterTerm, MediaTagListChange>? Changed { get; set; }
    /// <summary>"Show only this" from a row's menu: the tag included, every other tag of its group out of the filter.</summary>
    internal Action<MediaFilterTerm>? ShowOnly { get; set; }
    /// <summary>Copies a tag's name.</summary>
    internal Action<string>? CopyName { get; set; }
    /// <summary>The order changed by the user (the page saves it).</summary>
    internal Action<MediaTagListSort>? SortChanged { get; set; }
    /// <summary>The name of an own tag by id (null: deleted).</summary>
    internal Func<Guid, string?> OwnName { get; set; } = _ => null;
    /// <summary>The top-N of each group (a setting, read at every rebuild).</summary>
    internal Func<int> Top { get; set; } = () => MeowChan.Config.MediaTagListTopDefault;

    internal ListBox List => _list;
    internal SearchBox Search => _search;
    internal TwoWayToggle SortSwitch => _sort;
    internal TextBlock Status => _status;
    /// <summary>The rows shown now (tests and smokes).</summary>
    internal IReadOnlyList<MediaTagListItem> Items => _items;
    internal MediaTagListSort Sort
    {
        get => _sort.SelectedIndex == 1 ? MediaTagListSort.Name : MediaTagListSort.Count;
        set
        {
            _localizing = true;
            try { _sort.SelectedIndex = value == MediaTagListSort.Name ? 1 : 0; }
            finally { _localizing = false; }
            Rebuild();
        }
    }

    internal MediaTagSidebar()
    {
        Name = "MediaTagSidebar";
        _search.ShowSearchIcon = true;
        _search.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid head = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        head.Children.Add(_title);
        head.Children.Add(_sort); Grid.SetColumn(_sort, 1);
        _list.ItemTemplate = new FuncDataTemplate<MediaTagListItem>((_, _) => new MediaTagListRowView(), supportsRecycling: true);
        _list.ContainerPrepared += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < _items.Count) Describe(e.Container, _items[e.Index]);
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), RowSpacing = 8 };
        layout.Children.Add(head);
        layout.Children.Add(_search); Grid.SetRow(_search, 1);
        layout.Children.Add(_status); Grid.SetRow(_status, 2);
        layout.Children.Add(_list); Grid.SetRow(_list, 3);
        Content = new Border { Child = layout, Padding = new Thickness(8), Classes = { "workspace-section-flush" } };
        _sort.SelectionChanged += (_, _) =>
        {
            if (_localizing) return;
            Rebuild();
            SortChanged?.Invoke(Sort);
        };
        _search.QueryChanged += (_, _) => Rebuild();
        _list.AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(_list).Properties.IsRightButtonPressed) _menuRow = RowAt(e.Source);
        }, RoutingStrategies.Tunnel);
        WorkspaceChips.AttachMenu(_list, ComposeMenu, () => _list.Focus(NavigationMethod.Tab));
        Localize();
    }

    internal void Localize()
    {
        _localizing = true;
        try
        {
            _title.Text = Texts.T("media.tags.title");
            _sort.SetItems(Texts.T("media.tags.sort.count"), Texts.T("media.tags.sort.name"));
            AutomationProperties.SetName(_sort, Texts.T("media.tags.sort"));
            _search.PlaceholderText = Texts.T("media.tags.search");
            AutomationProperties.SetName(_search, Texts.T("media.tags.search"));
            AutomationProperties.SetName(_list, Texts.T("media.tags.title"));
            string hint = Texts.T("media.tags.hint", PlatformKeyNames.Alt());
            AutomationProperties.SetHelpText(_list, hint);
            AutomationProperties.SetName(this, Texts.T("media.tags.title"));
        }
        finally { _localizing = false; }
        Rebuild();
    }

    /// <summary>The counts of the results now (null: none yet). <paramref name="counting"/> says a newer count is on its way.</summary>
    internal void SetCounts(MediaTagCountResult? counts, bool counting)
    {
        _counts = counts;
        SetCounting(counting);
        Rebuild();
    }

    internal void SetCounting(bool counting)
    {
        string? text = counting ? Texts.T("media.tags.counting") : null;
        if (_status.Text == text) return;
        _status.Text = text;
        _status.IsVisible = text != null;
    }

    /// <summary>The terms of the filter now (which rows are held, and their polarity).</summary>
    internal void SetTerms(IReadOnlyList<MediaFilterTerm> terms)
    {
        _terms = terms.ToArray();
        Rebuild();
    }

    private void Rebuild()
    {
        MediaTagListOptions options = new(Sort, _search.Text, Math.Max(1, Top()), _showAll, _collapsed);
        IReadOnlyList<MediaTagListItem> items = MediaTagList.Arrange(_counts, _terms, options, OwnName);
        if (items.SequenceEqual(_items)) return;
        string? selected = (_list.SelectedItem as MediaTagListItem)?.Identity;
        bool focused = _list.IsKeyboardFocusWithin;
        Control? focusedElement = focused ? TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control : null;
        _items = items;
        _list.ItemsSource = items;
        if (selected == null) return;
        int index = IndexOf(selected);
        if (index < 0) return;
        _list.SelectedIndex = index;
        if (!focused) return;
        // The containers are replaced with the rows: keyboard focus goes back on the same row once it is laid out.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_list.IsEffectivelyVisible || _list.SelectedIndex != index
                || (_list.SelectedItem as MediaTagListItem)?.Identity != selected) return;
            object? current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (current is Control currentControl && !ReferenceEquals(currentControl, focusedElement)
                && !ReferenceEquals(currentControl, _list) && RowAt(currentControl)?.Identity != selected) return;
            _list.ScrollIntoView(index);
            (_list.ContainerFromIndex(index) ?? (Control)_list).Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private int IndexOf(string identity)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Identity == identity) return i;
        return -1;
    }

    private static void Describe(Control container, MediaTagListItem item)
    {
        AutomationProperties.SetName(container, item.AccessibleName);
        if (item.Kind == MediaTagListKind.Header) AutomationProperties.SetHelpText(container, Texts.T("media.tags.group.hint"));
    }

    private static MediaTagListItem? RowAt(object? source) => source is Visual visual
        ? visual.GetSelfAndVisualAncestors().OfType<Control>().Select(control => control.DataContext).OfType<MediaTagListItem>().FirstOrDefault()
        : null;

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (RowAt(e.Source) is not { } row) return;
        bool exclude = e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (Activate(row, exclude ? MediaTagListGesture.Exclude : MediaTagListGesture.Include)) e.Handled = true;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_list.SelectedItem is not MediaTagListItem row) return;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                if (Activate(row, e.KeyModifiers == KeyModifiers.Shift ? MediaTagListGesture.Exclude : MediaTagListGesture.Include)) e.Handled = true;
                break;
            case Key.Delete or Key.Back when e.KeyModifiers == KeyModifiers.None && row is { Kind: MediaTagListKind.Tag, Polarity: not 0, Term: { } held }:
                Changed?.Invoke(held, MediaTagListChange.Remove);
                e.Handled = true;
                break;
            case Key.Left or Key.Right when e.KeyModifiers == KeyModifiers.None && row.Kind == MediaTagListKind.Header:
                bool collapse = e.Key == Key.Left;
                if (collapse != _collapsed.Contains(row.Group)) ToggleCollapsed(row.Group);
                e.Handled = true;
                break;
        }
    }

    /// <summary>What a click or Enter on a row does; false when the row does nothing (a note).</summary>
    internal bool Activate(MediaTagListItem row, MediaTagListGesture gesture)
    {
        switch (row.Kind)
        {
            case MediaTagListKind.Header:
                ToggleCollapsed(row.Group);
                return true;
            case MediaTagListKind.More:
                if (!_showAll.Remove(row.Group)) _showAll.Add(row.Group);
                Rebuild();
                return true;
            case MediaTagListKind.Tag when row.Term is { } term:
                MediaTagListChange change = MediaTagList.Resolve(row.Polarity, gesture);
                if (change != MediaTagListChange.None) Changed?.Invoke(term, change);
                return true;
            default:
                return false;
        }
    }

    private void ToggleCollapsed(MediaTagGroup group)
    {
        if (!_collapsed.Remove(group)) _collapsed.Add(group);
        Rebuild();
    }

    // The menu of a tag row: the row under the pointer of a right-click, else the selected one (the Menu key, Shift+F10).
    private ContextMenuBuilder? ComposeMenu()
    {
        MediaTagListItem? row = _menuRow ?? _list.SelectedItem as MediaTagListItem;
        _menuRow = null;
        if (row is not { Kind: MediaTagListKind.Tag, Term: { } term }) return null;
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(row.AccessibleName);
        if (row.Polarity <= 0) menu.Command("tags.filter-include", () => Changed?.Invoke(term, MediaTagListChange.Include));
        if (row.Polarity >= 0) menu.Command("tags.filter-exclude", () => Changed?.Invoke(term, MediaTagListChange.Exclude));
        menu.Command("tags.filter-only", () => ShowOnly?.Invoke(term));
        menu.Section().Command("tags.copy-name", () => CopyName?.Invoke(row.Name));
        if (row.Polarity != 0) menu.Section().Command("archive.clear-filter", () => Changed?.Invoke(term, MediaTagListChange.Remove));
        return menu;
    }
}

/// <summary>One row of the tag list: a heading (chevron and group name), a tag (state mark, name, count), "Show all" or a note.</summary>
internal sealed class MediaTagListRowView : Border
{
    private const string IncludeMark = "M6,2 L6,10 M2,6 L10,6";
    private readonly Avalonia.Controls.Shapes.Path _mark = new()
    {
        Name = "MediaTagRowMark", Width = 12, Height = 12, Stretch = Stretch.None, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };
    private readonly TextBlock _name = new() { Name = "MediaTagRowName", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { Name = "MediaTagRowCount", Classes = { "workspace-meta", "tnum" }, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private string? _geometry;

    public MediaTagListRowView()
    {
        AutomationProperties.SetAccessibilityView(_mark, AccessibilityView.Raw);
        _mark.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, this.GetObservable(TextElement.ForegroundProperty));
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("16,*,Auto"), ColumnSpacing = 4, MinHeight = 24 };
        grid.Children.Add(_mark);
        grid.Children.Add(_name); Grid.SetColumn(_name, 1);
        grid.Children.Add(_count); Grid.SetColumn(_count, 2);
        Child = grid;
        _mark.AttachedToVisualTree += (_, _) => ApplyMark();
        DataContextChanged += (_, _) => Present();
    }

    private void Present()
    {
        MediaTagListItem? row = DataContext as MediaTagListItem;
        _name.Text = row?.Name;
        _name.FontWeight = row is { Kind: MediaTagListKind.Header } or { Polarity: not 0 } ? FontWeight.SemiBold : FontWeight.Normal;
        _name.TextWrapping = row?.Kind == MediaTagListKind.Note ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _name.Classes.Set("workspace-meta", row?.Kind == MediaTagListKind.Note);
        _name.Classes.Set("link-text", row?.Kind == MediaTagListKind.More);
        _count.Text = row?.Kind == MediaTagListKind.Tag ? row.Count.ToString("N0", Texts.Culture) : null;
        _geometry = row switch
        {
            { Kind: MediaTagListKind.Header, Collapsed: true } => "M4,2 L8,6 L4,10",
            { Kind: MediaTagListKind.Header } => "M2,4 L6,8 L10,4",
            { Kind: MediaTagListKind.Tag, Polarity: > 0 } => IncludeMark,
            { Kind: MediaTagListKind.Tag, Polarity: < 0 } => WorkspaceChips.MarkMissing,
            _ => null
        };
        ApplyMark();
        ToolTip.SetTip(this, row?.Kind == MediaTagListKind.Tag ? row.AccessibleName : null);
    }

    private void ApplyMark()
    {
        _mark.IsVisible = _geometry != null;
        if (!_mark.IsAttachedToVisualTree() || _geometry == null || Equals(_mark.Tag, _geometry)) return;
        _mark.Data = Geometry.Parse(_geometry);
        _mark.Tag = _geometry;
    }
}
