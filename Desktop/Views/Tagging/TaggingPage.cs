using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
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
    // TG1: one toolbar row: the primary start button, the icon buttons, then "Show", the view switch and the help. Items that do not fit
    // move into the toolbar's "More" in the order of the work view.
    private readonly ActionToolBar _actions = new() { Name = "TaggingActions", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly WrapPanel _secondaryToolbar = new() { Name = "TaggingSecondaryToolbar", ItemSpacing = 16, LineSpacing = 8, IsVisible = false };
    private readonly Border _error = new() { Name = "TaggingError", Classes = { "workspace-error" }, IsVisible = false };
    private readonly TextBlock _errorText = new() { Name = "TaggingErrorText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _errorSource = new() { Name = "TaggingErrorSource", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _retryLoad = new() { Name = "TaggingRetryLoad", Classes = { "subtle" } };
    // The counters row: "Open n · Finished n · Skipped n", a 120 dip mini bar and the percentage; the "last shown" link follows them
    // directly (it wraps below them on a narrow page instead of drifting to the far edge of a wide one).
    private readonly WrapPanel _progress = new() { Name = "TaggingProgress", ItemSpacing = 16, LineSpacing = 4 };
    private readonly TextBlock _progressText = new() { Name = "TaggingProgressText", Classes = { "tnum" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    internal const double MiniBarWidth = 120;
    private readonly ProgressBar _bar = new() { Name = "TaggingProgressBar", Minimum = 0, Maximum = 100, Width = MiniBarWidth, Height = 6, MinHeight = 0, MaxHeight = 6 };
    private readonly TextBlock _percent = new() { Name = "TaggingPercent", Classes = { "muted", "tnum" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _start = new() { Name = "TaggingStart", Classes = { "primary" } };
    // T1: "Last shown: file · position n of m"; the link continues exactly there (the same action as the start button, which stands at
    // the same entry).
    private readonly Button _lastShown = new() { Name = "TaggingLastShown", Classes = { "link", "tnum" }, Padding = new Thickness(0), MinHeight = 24,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly StackPanel _filterPanel = new() { Name = "TaggingFilterPanel", Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _filterLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _filter = new() { Name = "TaggingFilter", MinWidth = 160 };
    private readonly HelpButton _help = new() { Name = "TaggingHelp", HelpKey = "help.taggingPage" };
    private readonly Grid _columnHeader = new() { Name = "TaggingColumns", ColumnDefinitions = TaggingQueueRowView.BuildColumns(true), ColumnSpacing = TaggingQueueRowView.ColumnGap, Margin = new Thickness(12, 0, 12, 0),
        // A right-click between the header words has to find the header too (it carries the column menu), so it takes a transparent fill.
        Background = Brushes.Transparent };
    private readonly ListBox _list = new() { Name = "TaggingList", SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _empty = new() { Name = "TaggingEmpty" };
    private readonly WorkspaceEmptyState _emptyState = new() { Name = "TaggingEmptyState", IsVisible = false };
    private readonly StackPanel _emptyActions = new() { Name = "TaggingEmptyActions", Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _emptyMedia = new() { Name = "TaggingEmptyMedia", Classes = { "primary" } };
    private readonly Button _emptyHow = new() { Name = "TaggingEmptyHow", Classes = { "link" } };
    private readonly Button _emptyClearDone = new() { Name = "TaggingEmptyClearDone" };
    private readonly Button _emptyReopen = new() { Name = "TaggingEmptyReopen" };
    private readonly Button _emptyShowAll = new() { Name = "TaggingEmptyShowAll", Classes = { "primary" } };
    private readonly TextBlock _keysHint = new() { Name = "TaggingKeysHint", Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "TaggingStatus", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaggingThumbnails _thumbnails;
    private MediaLibraryUserData? _userData;
    private bool _compactRows;
    private bool _compactTable;
    private IReadOnlyList<TaggingQueueRow> _rows = Array.Empty<TaggingQueueRow>();
    private bool _localizing, _refreshPosted;

    internal TaggingPage(DesktopSessionHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _queueWork = new TaggingQueueController(host);
        _thumbnails = new TaggingThumbnails(_lifetime.Token);
        Name = "TaggingPage";
        // Every key of the page runs in the "tagging" context, as the commands of its queue list are declared (tagging.remove, media.tile-size.*): a default
        // that a saved binding took away only elsewhere (a partial yield) stays active here (EffectiveShortcutBindings.EnterKeyContext).
        MeowChan.Desktop.Controls.KeyContextHost.Install(this, _ => "tagging");
        Content = BuildLayout();
        _start.Click += (_, _) => OpenActions.Start(() => StartAsync(null), "tagging.start");
        _lastShown.Click += (_, _) => OpenActions.Start(() => StartAsync(null), "tagging.start");
        _filter.SelectionChanged += (_, _) => { if (!_localizing) { Render(); RefreshWorkView(); } };
        InitializeGrid();
        InitializeCommands();
        InitializeColumns();
        InitializeGroups();
        InitializeStamp();
        InitializeEmptyStates();
        InitializeWorkView();
        Texts.Changed += OnLanguageChanged;
        _host.StateChanged += OnHostChanged;
        Localize();
        Render();
        AttachedToVisualTree += (_, _) => UpdateToolbarRows();
        UpdateToolbarRows();
    }

    /// <summary>
    /// Opens the gallery over the open entries, starting where the queue's cursor stands (the shell's walk-through, PT1). The tagging
    /// mode of PT3 replaces what the shell passes here; the page only moves the cursor first when the user picked an entry.
    /// </summary>
    internal Func<Task>? StartTagging { get; set; }
    /// <summary>"Add…": the shell opens the media page, where a selection is added to the queue.</summary>
    internal Action? AddRequested { get; set; }
    /// <summary>The profile's tags, favourites and ratings as the media page reads them (passive, in memory); the tag counts come from it. Null: no counts.</summary>
    internal Func<MediaLibraryUserData>? ReadUserData { get; set; }
    /// <summary>True when the profile asks for the compact row density (Barebones always does); read whenever the list is shown.</summary>
    internal Func<bool>? CompactDensity { get; set; }

    internal TagQueueSnapshot Queue => _queueWork.Queue;
    internal IReadOnlyList<TaggingQueueRow> Rows => _rows;
    internal ListBox List => _list;
    internal Button StartButton => _start;
    internal ComboBox FilterBox => _filter;
    internal TextBlock StatusText => _status;
    internal TextBlock ProgressText => _progressText;
    internal Button LastShownLink => _lastShown;
    internal Border ErrorPanel => _error;
    internal TextBlock ErrorSourceText => _errorSource;
    internal Button RetryLoadButton => _retryLoad;
    internal bool IsBusy => _queueWork.Busy || _queueWork.Loading;

    internal TaggingQueueFilter Filter
    {
        get => _filter.SelectedIndex is >= 0 and <= 3 ? (TaggingQueueFilter)_filter.SelectedIndex : TaggingQueueFilter.All;
        set => _filter.SelectedIndex = (int)value;
    }

    private Control BuildLayout()
    {
        StackPanel heading = new() { Classes = { "workspace-page-header" }, Children = { _title, _subtitle } };

        _error.Child = new StackPanel { Spacing = 8, Children = { _errorText, _errorSource, _retryLoad } };
        AutomationProperties.SetLiveSetting(_errorText, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(_errorSource, AutomationLiveSetting.Polite);

        _bar.VerticalAlignment = VerticalAlignment.Center;
        StackPanel counters = new() { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _progressText, _bar, _percent } };
        _progress.Children.Add(counters);
        _progress.Children.Add(_lastShown);

        _filterPanel.Children.Add(_filterLabel);
        _filterPanel.Children.Add(_filter);

        StackPanel header = new() { Spacing = 8, Margin = new Thickness(12, 8, 12, 8) };
        header.Children.Add(heading);
        header.Children.Add(_error);
        header.Children.Add(BuildClearConfirmation());
        header.Children.Add(_progress);
        header.Children.Add(_actions);
        header.Children.Add(_secondaryToolbar);
        header.Children.Add(BuildStamp());
        SizeChanged += (_, _) =>
        {
            UpdateToolbarRows();
            UpdateTableLayout();
        };
        header.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _list.ItemTemplate = RowTemplate();
        _list.ContainerPrepared += (_, e) =>
        {
            TaggingQueueRow? row = e.Container.DataContext as TaggingQueueRow ?? (e.Index >= 0 && e.Index < _rows.Count ? _rows[e.Index] : null);
            if (e.Container is ListBoxItem item && row != null)
            {
                item.MinHeight = _compactRows ? TaggingQueueRowView.CompactHeight : TaggingQueueRowView.RowHeight;
                AutomationProperties.SetName(item, row.AccessibleName);
                AutomationProperties.SetHelpText(item, row.AccessibleDetails(TagCountOf(row.Item)));
            }
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _empty.Classes.Add(WorkspaceEmptyState.TextClass);
        _emptyState.Children.Add(_empty);
        _emptyActions.Children.Add(_emptyShowAll);
        _emptyActions.Children.Add(_emptyMedia);
        _emptyActions.Children.Add(_emptyClearDone);
        _emptyActions.Children.Add(_emptyReopen);
        _emptyState.Children.Add(_emptyActions);
        _emptyState.Children.Add(_emptyHow);
        _emptyHow.HorizontalAlignment = HorizontalAlignment.Center;
        Panel body = new();
        body.Children.Add(_list);
        body.Children.Add(_grid);
        body.Children.Add(_emptyState);
        // The selection bar (only with a selection) stands above the column header; the toolbar above it never moves.
        Grid listArea = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        listArea.Children.Add(BuildSelectionBar());
        Grid.SetRow(_columnHeader, 1);
        listArea.Children.Add(_columnHeader);
        Border surface = new() { Name = "TaggingListSurface", Child = body, Classes = { "workspace-section-flush" } };
        Grid.SetRow(surface, 2);
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

    // The row template reads the density when the rows are built; a change of density builds it again (ApplyDensity).
    private FuncDataTemplate<TaggingQueueRow> RowTemplate()
    {
        bool compact = _compactRows;
        return new FuncDataTemplate<TaggingQueueRow>((_, _) => new TaggingQueueRowView(_thumbnails, TagCountOf, compact, ShowPreviewColumn, _compactTable));
    }

    /// <summary>The number of own media tags of an entry, or null when the tag store is not known (shown as a dash).</summary>
    internal int? TagCountOf(TagQueueItem item)
    {
        if (_userData == null) return null;
        MediaLibraryEntryState state = MediaLibraryEntryState.Of(item.ToLibraryEntry(), _userData);
        return state.TagsKnown && state.TagSubject != null ? state.MediaTags.Count : null;
    }

    // The profile's density (or Barebones) changed what a row should be: the rows are built again.
    private void ApplyDensity()
    {
        bool compact = WorkViewDensity.Compact(_workView) || CompactDensity?.Invoke() == true;
        if (compact == _compactRows) return;
        _compactRows = compact;
        _list.ItemTemplate = RowTemplate();
    }

    /// <summary>
    /// Keeps the table's information columns in the viewport at small widths. The compact geometry trims text with a tooltip and the
    /// row help text carries every value for keyboard and assistive-technology users; no column is removed.
    /// </summary>
    private void UpdateTableLayout()
    {
        bool compact = TaggingQueueRowView.UseCompactColumns(Bounds.Width);
        if (compact == _compactTable) return;
        _compactTable = compact;
        _list.ItemTemplate = RowTemplate();
        LocalizeColumns();
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

    /// <summary>Retries the queue read without presenting a failed read as an empty queue.</summary>
    internal async Task RetryLoadAsync()
    {
        if (!_queueWork.CanReload) return;
        bool hadFocus = _retryLoad.IsFocused;
        ShowStatus(Texts.T("tagging.page.loading"));
        _retryLoad.IsEnabled = false;
        UpdateActions();
        await _queueWork.LoadAsync();
        if (_queueWork.Ended) return;
        Show();
        ShowStatus(null);
        // The banner (and with it the button that had the focus) is gone when the file could be read: the keyboard goes on to the list.
        if (!hadFocus) return;
        if (_queueWork.ErrorText == null) FocusContent();
        else if (_retryLoad.IsEffectivelyEnabled) _retryLoad.Focus(); // still unreadable: the button the keyboard was on stays its place
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
        if (_rows.Count > 0 && ActiveList.IsEffectivelyVisible)
        {
            if (ActiveList.SelectedIndex < 0)
                ActiveList.SelectedIndex = Math.Max(0, _rows.ToList().FindIndex(row => row.Next));
            (ActiveList.ContainerFromIndex(Math.Max(0, ActiveList.SelectedIndex)) as Control ?? ActiveList).Focus();
        }
        else if (_start.IsEffectivelyEnabled) _start.Focus();
    }

    // The queue changed (loaded, refreshed or written): the list, the progress, the stamp and the view placement follow it.
    private void Show()
    {
        _userData = ReadUserData?.Invoke();
        ApplyTileAppearance();
        ApplyDensity();
        Render();
        RenderStamp();
        RefreshWorkView();
        ApplyPendingSelect();
    }

    // The selection that stays over a new set of rows is the shown list's (by key); a change of view hands the old list's keys over.
    private void Render(TagQueueKey[]? carry = null, TagQueueKey? carryLead = null)
    {
        TagQueueSnapshot queue = _queueWork.Queue;
        TagQueueKey[] selected = carry ?? SelectedKeys();
        TagQueueKey? lead = carry != null ? carryLead : Lead?.Key;
        _rendering = true;
        try
        {
            _rows = TaggingQueueView.Rows(queue, Filter, EffectiveScope, _selectedGroupId, _ungroupedSelected);
            // Only the shown list is bound: the other one holds no rows (no tiles or rows built behind the scenes).
            ListBox hidden = _viewMode == TaggingViewMode.Grid ? _list : _grid;
            hidden.ItemsSource = null;
            ActiveList.ItemsSource = _rows;
            if (selected.Length > 0)
            {
                HashSet<TagQueueKey> keep = selected.ToHashSet();
                TaggingQueueRow? leadRow = lead is { } stands ? _rows.FirstOrDefault(row => row.Key == stands) : null;
                if (leadRow != null && keep.Contains(leadRow.Key)) ActiveList.SelectedItems?.Add(leadRow);
                foreach (TaggingQueueRow row in _rows.Where(row => keep.Contains(row.Key) && !ReferenceEquals(row, leadRow))) ActiveList.SelectedItems?.Add(row);
                if (leadRow != null) ActiveList.ScrollIntoView(leadRow);
            }
        }
        finally { _rendering = false; }
        _progressText.Text = TaggingQueueView.ProgressText(queue, _selectedGroupId, _ungroupedSelected, Scope);
        _bar.Value = TaggingQueueView.Share(queue, _selectedGroupId, _ungroupedSelected, Scope) * 100;
        _percent.Text = TaggingQueueView.PercentText(queue, _selectedGroupId, _ungroupedSelected, Scope);
        RenderGroups();
        AutomationProperties.SetName(_bar, Texts.T("tagging.page.progressName"));
        AutomationProperties.SetHelpText(_bar, _progressText.Text + " · " + _percent.Text);
        bool empty = _rows.Count == 0;
        _emptyState.IsVisible = empty && _queueWork.Loaded && _queueWork.ErrorText == null;
        _list.IsVisible = !empty && _viewMode == TaggingViewMode.List;
        _grid.IsVisible = !empty && _viewMode == TaggingViewMode.Grid;
        _columnHeader.IsVisible = !empty && _viewMode == TaggingViewMode.List;
        // "Choose columns…" only means something where there are columns: the list view (the work view still decides first, by the
        // toolbar's own omission of the item; a hidden item takes no width and no group line).
        _columnsButton.IsVisible = _viewMode == TaggingViewMode.List;
        RenderEmptyState(queue);
        _errorText.Text = _queueWork.ErrorText;
        _errorSource.Text = _queueWork.ErrorSource;
        _retryLoad.IsVisible = _queueWork.ErrorText != null;
        _retryLoad.IsEnabled = _queueWork.CanReload;
        UpdateActions();
        // Error visibility is work-view gated, so refresh it after the text has been replaced.
        RefreshWorkView();
        UpdateSelectionBar();
    }

    // T1: the link follows the queue; it continues like the start button, so it is as enabled as that button. Whether it is shown at all
    // is the work view's call (WorkViewGate in RefreshWorkView) together with the queue's state.
    private void RenderLastShown(TagQueueSnapshot queue)
    {
        string? text = TaggingQueueView.LastShownText(queue, _selectedGroupId, _ungroupedSelected, Scope);
        _lastShown.Content = text; // its visibility is the work-view gate's (RefreshWorkView)
        _lastShown.IsEnabled = text != null && CanStart;
        if (text != null)
        {
            AutomationProperties.SetName(_lastShown, text);
            AutomationProperties.SetHelpText(_lastShown, Texts.T("tagging.page.lastShown.help"));
            ToolTip.SetTip(_lastShown, Texts.T("tagging.page.lastShown.help"));
        }
    }

    private TagQueueKey[] SelectedKeys() => ActiveList.SelectedItems?.OfType<TaggingQueueRow>().Select(row => row.Key).ToArray() ?? Array.Empty<TagQueueKey>();

    private bool CanChange => _queueWork.CanChange;

    private bool CanStart => _queueWork.Loaded && !_queueWork.Busy && !_queueWork.Loading && _queueWork.LoadError == null && !_queueWork.Ended
        && StartTagging != null && _queueWork.HostReady && TaggingQueueView.Resume(_queueWork.Queue, _selectedGroupId, _ungroupedSelected, Scope) != null;

    private void UpdateActions()
    {
        string? label = TaggingQueueView.StartLabel(_queueWork.Queue, _selectedGroupId, _ungroupedSelected, Scope);
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
            LocalizeEmptyStates();
            _retryLoad.Content = Texts.T("action.retry");
            AutomationProperties.SetName(_retryLoad, Texts.T("action.retry"));
            LocalizeGroups();
            _help.Refresh();
            LocalizeColumns();
            LocalizeCommands();
            LocalizeView();
            LocalizeStamp();
            LocalizeSelection();
        }
        finally { _localizing = false; }
    }

    private void LocalizeColumns()
    {
        // The same helper as the rows, so header and rows stand on the same columns and the same gap (preview shown or not).
        _columnHeader.ColumnDefinitions = TaggingQueueRowView.BuildColumns(ShowPreviewColumn, _compactTable);
        _columnHeader.ColumnSpacing = TaggingQueueRowView.ColumnGap;
        _columnHeader.Children.Clear();
        string[] keys = { "", "tagging.page.col.preview", "tagging.page.col.kind", "tagging.page.col.name", "tagging.page.col.source", "tagging.page.col.group", "tagging.page.col.tags", "tagging.page.col.state" };
        for (int column = 0; column < keys.Length; column++)
        {
            // The preview column has no header word: the picture needs none, and "Preview" / "Vorschau" does not fit 56 dip (the column
            // menu keeps the word).
            TextBlock header = new()
            {
                Text = column switch { 0 => "#", 1 => string.Empty, _ => Texts.T(keys[column]) }, Classes = { "workspace-meta" },
                TextAlignment = column is 0 or 6 ? TextAlignment.Right : TextAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = column switch { 0 => TaggingQueueRowView.NumberMargin(ShowPreviewColumn), 7 => TaggingQueueRowView.StateMargin, _ => default }
            };
            header.IsVisible = column != 1 || ShowPreviewColumn;
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
        EndTagging();
        _menuHost?.Dispose();
        _gridMenuHost?.Dispose();
        EndColumns();
        _stateMenuHost?.Dispose();
        _groupMenuHost?.Dispose();
        _groupManageHost?.Dispose();
        _lifetime.Cancel();
        Texts.Changed -= OnLanguageChanged;
        _host.StateChanged -= OnHostChanged;
    }
}
