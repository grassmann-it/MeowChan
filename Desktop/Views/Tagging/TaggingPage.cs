using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

/// <summary>
/// The tagging page (PT2, PLAN-POWER-TAGGEN §1.3): a thin working page over the tagging queue. It shows the progress, edits the
/// stamp set, lists the entries with their state (open, finished, skipped) and lets the user reorder, remove and clear them;
/// "Start tagging" hands over to the gallery (<see cref="StartTagging"/>, the seam of the tagging mode). The queue work itself is
/// <see cref="TaggingQueueController"/>; this class only shows it. Tab order: title, actions, progress, stamp set, start and filter, list.
/// </summary>
public sealed partial class TaggingPage : UserControl
{
    private readonly DesktopSessionHost _host;
    private readonly TaggingQueueController _queueWork;
    private readonly TextBlock _title = new() { Name = "TaggingTitle", Classes = { "page-title" } };
    private readonly TextBlock _subtitle = new() { Name = "TaggingSubtitle", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private readonly ActionToolBar _actions = new() { Name = "TaggingActions", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Border _error = new() { Name = "TaggingError", Classes = { "workspace-error" }, IsVisible = false };
    private readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _progress = new() { Name = "TaggingProgress", Spacing = 4 };
    private readonly TextBlock _progressText = new() { Name = "TaggingProgressText", Classes = { "tnum" }, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Name = "TaggingProgressBar", Minimum = 0, Maximum = 100, Height = 6, MinHeight = 0, MaxHeight = 6 };
    private readonly TextBlock _percent = new() { Name = "TaggingPercent", Classes = { "muted", "tnum" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _start = new() { Name = "TaggingStart", Classes = { "primary" } };
    // T1: "Last shown: file · position n of m"; the link continues exactly there (the same action as the start button, which stands at
    // the same entry).
    private readonly Button _lastShown = new() { Name = "TaggingLastShown", Classes = { "link", "tnum" }, Padding = new Thickness(0), MinHeight = 24,
        HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly StackPanel _filterPanel = new() { Name = "TaggingFilterPanel", Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _filterLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _filter = new() { Name = "TaggingFilter", MinWidth = 160 };
    private readonly Grid _columnHeader = new() { Name = "TaggingColumns", ColumnDefinitions = new ColumnDefinitions(TaggingQueueRowView.Columns), ColumnSpacing = 8, Margin = new Thickness(12, 0, 12, 0) };
    private readonly ListBox _list = new() { Name = "TaggingList", SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _empty = new() { Name = "TaggingEmpty" };
    private readonly WorkspaceEmptyState _emptyState = new() { Name = "TaggingEmptyState", IsVisible = false };
    private readonly TextBlock _keysHint = new() { Name = "TaggingKeysHint", Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "TaggingStatus", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private IReadOnlyList<TaggingQueueRow> _rows = Array.Empty<TaggingQueueRow>();
    private bool _localizing, _refreshPosted;

    internal TaggingPage(DesktopSessionHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _queueWork = new TaggingQueueController(host);
        Name = "TaggingPage";
        Content = BuildLayout();
        _start.Click += (_, _) => _ = StartAsync(null);
        _lastShown.Click += (_, _) => _ = StartAsync(null);
        _filter.SelectionChanged += (_, _) => { if (!_localizing) { Render(); RefreshWorkView(); } };
        InitializeCommands();
        InitializeStamp();
        InitializeWorkView();
        Texts.Changed += OnLanguageChanged;
        _host.StateChanged += OnHostChanged;
        Localize();
        Render();
    }

    /// <summary>
    /// Opens the gallery over the open entries, starting where the queue's cursor stands (the shell's walk-through, PT1). The tagging
    /// mode of PT3 replaces what the shell passes here; the page only moves the cursor first when the user picked an entry.
    /// </summary>
    internal Func<Task>? StartTagging { get; set; }
    /// <summary>"Add…": the shell opens the media page, where a selection is added to the queue.</summary>
    internal Action? AddRequested { get; set; }

    internal TagQueueSnapshot Queue => _queueWork.Queue;
    internal IReadOnlyList<TaggingQueueRow> Rows => _rows;
    internal ListBox List => _list;
    internal Button StartButton => _start;
    internal ComboBox FilterBox => _filter;
    internal TextBlock StatusText => _status;
    internal TextBlock ProgressText => _progressText;
    internal Button LastShownLink => _lastShown;
    internal Border ErrorPanel => _error;
    internal bool IsBusy => _queueWork.Busy || _queueWork.Loading;

    internal TaggingQueueFilter Filter
    {
        get => _filter.SelectedIndex is >= 0 and <= 3 ? (TaggingQueueFilter)_filter.SelectedIndex : TaggingQueueFilter.All;
        set => _filter.SelectedIndex = (int)value;
    }

    private Control BuildLayout()
    {
        StackPanel heading = new() { Classes = { "workspace-page-header" }, Children = { _title, _subtitle } };
        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(heading);
        Grid.SetColumn(_actions, 1);
        top.Children.Add(_actions);

        _error.Child = _errorText;
        AutomationProperties.SetLiveSetting(_errorText, AutomationLiveSetting.Assertive);

        Grid barRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        _bar.VerticalAlignment = VerticalAlignment.Center;
        barRow.Children.Add(_bar);
        Grid.SetColumn(_percent, 1);
        barRow.Children.Add(_percent);
        _progress.Children.Add(_progressText);
        _progress.Children.Add(barRow);
        _progress.Children.Add(_lastShown);

        _filterPanel.Children.Add(_filterLabel);
        _filterPanel.Children.Add(_filter);
        Grid startRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        startRow.Children.Add(_start);
        Grid.SetColumn(_filterPanel, 2);
        startRow.Children.Add(_filterPanel);

        StackPanel header = new() { Spacing = 8, Margin = new Thickness(12, 8, 12, 8) };
        header.Children.Add(top);
        header.Children.Add(_error);
        header.Children.Add(BuildClearConfirmation());
        header.Children.Add(_progress);
        header.Children.Add(BuildStamp());
        header.Children.Add(startRow);
        header.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _list.ItemTemplate = new FuncDataTemplate<TaggingQueueRow>((_, _) => new TaggingQueueRowView());
        _list.ContainerPrepared += (_, e) =>
        {
            TaggingQueueRow? row = e.Container.DataContext as TaggingQueueRow ?? (e.Index >= 0 && e.Index < _rows.Count ? _rows[e.Index] : null);
            if (e.Container is ListBoxItem item && row != null)
            {
                item.MinHeight = TaggingQueueRowView.RowHeight;
                AutomationProperties.SetName(item, row.AccessibleName);
            }
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _empty.Classes.Add(WorkspaceEmptyState.TextClass);
        _emptyState.Children.Add(_empty);
        Panel body = new();
        body.Children.Add(_list);
        body.Children.Add(_emptyState);
        Grid listArea = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        listArea.Children.Add(_columnHeader);
        Border surface = new() { Name = "TaggingListSurface", Child = body, Classes = { "workspace-section-flush" } };
        Grid.SetRow(surface, 1);
        listArea.Children.Add(surface);

        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(header);
        Border list = new() { Child = listArea, Margin = new Thickness(12, 0, 12, 4) };
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        _keysHint.Margin = new Thickness(12, 0, 12, 12);
        Grid.SetRow(_keysHint, 2);
        layout.Children.Add(_keysHint);
        return layout;
    }

    /// <summary>Reads the queue file (the page was opened) and shows it. An unreadable file is shown as an error, never emptied.</summary>
    internal async Task ActivateAsync()
    {
        if (_queueWork.Ended || _queueWork.Loading) return;
        bool first = !_queueWork.Loaded;
        if (first) ShowStatus(Texts.T("tagging.page.loading"));
        Task load = _queueWork.LoadAsync();
        UpdateActions();
        await load;
        if (_queueWork.Ended) return;
        if (first) ShowStatus(null);
        Show();
        Dispatcher.UIThread.Post(FocusContent, DispatcherPriority.Input);
    }

    /// <summary>Shows the host's last committed queue when it differs from the shown one (another surface added or decided).</summary>
    internal void Refresh()
    {
        if (_queueWork.Refresh()) Show();
    }

    private void OnHostChanged()
    {
        if (_refreshPosted || _queueWork.Ended) return;
        _refreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            if (IsEffectivelyVisible) Refresh();
        }, DispatcherPriority.Background);
    }

    internal void FocusContent()
    {
        if (_queueWork.Ended || !IsEffectivelyVisible) return;
        if (_rows.Count > 0 && _list.IsEffectivelyVisible)
        {
            if (_list.SelectedIndex < 0)
                _list.SelectedIndex = Math.Max(0, _rows.ToList().FindIndex(row => row.Next));
            (_list.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) as Control ?? _list).Focus();
        }
        else if (_start.IsEffectivelyEnabled) _start.Focus();
    }

    // The queue changed (loaded, refreshed or written): the list, the progress, the stamp and the view placement follow it.
    private void Show()
    {
        Render();
        RenderStamp();
        RefreshWorkView();
        ApplyPendingSelect();
    }

    private void Render()
    {
        TagQueueSnapshot queue = _queueWork.Queue;
        TagQueueKey[] selected = SelectedKeys();
        TagQueueKey? lead = (_list.SelectedItem as TaggingQueueRow)?.Key;
        _rows = TaggingQueueView.Rows(queue, Filter);
        _list.ItemsSource = _rows;
        if (selected.Length > 0)
        {
            HashSet<TagQueueKey> keep = selected.ToHashSet();
            foreach (TaggingQueueRow row in _rows.Where(row => keep.Contains(row.Key))) _list.SelectedItems?.Add(row);
            if (lead is { } stands && _rows.FirstOrDefault(row => row.Key == stands) is { } leadRow) _list.ScrollIntoView(leadRow);
        }
        _progressText.Text = TaggingQueueView.ProgressText(queue);
        _bar.Value = TaggingQueueView.Share(queue) * 100;
        _percent.Text = TaggingQueueView.PercentText(queue);
        AutomationProperties.SetName(_bar, Texts.T("tagging.page.progressName"));
        AutomationProperties.SetHelpText(_bar, _progressText.Text + " · " + _percent.Text);
        bool empty = _rows.Count == 0;
        _emptyState.IsVisible = empty && _queueWork.Loaded;
        _list.IsVisible = !empty;
        _columnHeader.IsVisible = !empty;
        _empty.Text = queue.Count == 0 ? Texts.T("queue.empty") : Texts.T("tagging.page.filterEmpty");
        _errorText.Text = _queueWork.ErrorText;
        UpdateActions();
    }

    // T1: the link follows the queue; it continues like the start button, so it is as enabled as that button. Whether it is shown at all
    // is the work view's call (WorkViewGate in RefreshWorkView) together with the queue's state.
    private void RenderLastShown(TagQueueSnapshot queue)
    {
        string? text = TaggingQueueView.LastShownText(queue);
        _lastShown.Content = text; // its visibility is the work-view gate's (RefreshWorkView)
        _lastShown.IsEnabled = text != null && CanStart;
        if (text != null)
        {
            AutomationProperties.SetName(_lastShown, text);
            AutomationProperties.SetHelpText(_lastShown, Texts.T("tagging.page.lastShown.help"));
            ToolTip.SetTip(_lastShown, Texts.T("tagging.page.lastShown.help"));
        }
    }

    private TagQueueKey[] SelectedKeys() => _list.SelectedItems?.OfType<TaggingQueueRow>().Select(row => row.Key).ToArray() ?? Array.Empty<TagQueueKey>();

    private bool CanChange => _queueWork.CanChange;

    private bool CanStart => _queueWork.Loaded && !_queueWork.Busy && !_queueWork.Loading && _queueWork.LoadError == null && !_queueWork.Ended
        && StartTagging != null && _queueWork.HostReady && _queueWork.Queue.ResumeIndex() != null;

    private void UpdateActions()
    {
        string? label = TaggingQueueView.StartLabel(_queueWork.Queue);
        CommandBindings.ApplyLabeled(_start, "tagging.start", label ?? Texts.T("command.tagging.start"));
        _start.IsEnabled = label != null && CanStart;
        if (label == null)
            ToolTip.SetTip(_start, Texts.T(_queueWork.Queue.Count == 0 ? "queue.empty" : "queue.allDone"));
        RenderLastShown(_queueWork.Queue);
        RefreshCommandStates();
    }

    private void ShowStatus(string? text)
    {
        _status.Text = text;
        _status.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_queueWork.Ended) return;
        Dispatcher.UIThread.Post(() => { if (!_queueWork.Ended) { Localize(); Show(); } });
    }

    private void Localize()
    {
        _localizing = true;
        try
        {
            _title.Text = Texts.T("tagging.page.title");
            _subtitle.Text = Texts.T("tagging.page.subtitle");
            _filterLabel.Text = Texts.T("tagging.page.filter");
            int filter = Math.Max(0, _filter.SelectedIndex);
            _filter.ItemsSource = Enum.GetValues<TaggingQueueFilter>().Select(TaggingQueueView.FilterText).ToArray();
            _filter.SelectedIndex = filter;
            AutomationProperties.SetName(_filter, Texts.T("tagging.page.filter"));
            AutomationProperties.SetName(_list, Texts.T("tagging.page.listName"));
            LocalizeColumns();
            LocalizeCommands();
            LocalizeStamp();
        }
        finally { _localizing = false; }
    }

    private void LocalizeColumns()
    {
        _columnHeader.Children.Clear();
        string[] keys = { "", "tagging.page.col.kind", "tagging.page.col.name", "tagging.page.col.source", "tagging.page.col.state" };
        for (int column = 0; column < keys.Length; column++)
        {
            TextBlock header = new()
            {
                Text = column == 0 ? "#" : Texts.T(keys[column]), Classes = { "workspace-meta" },
                TextAlignment = column == 0 ? TextAlignment.Right : TextAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(header, column);
            _columnHeader.Children.Add(header);
        }
        // The list items name themselves (number, file, state); the header row is only a visual aid.
        AutomationProperties.SetAccessibilityView(_columnHeader, AccessibilityView.Raw);
    }

    /// <summary>The window closes: the page stops following the host and the language.</summary>
    internal void End()
    {
        if (_queueWork.Ended) return;
        _queueWork.End();
        Texts.Changed -= OnLanguageChanged;
        _host.StateChanged -= OnHostChanged;
    }
}
