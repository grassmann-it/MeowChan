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
using Avalonia.Markup.Xaml.MarkupExtensions;
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
/// <see cref="TaggingQueueController"/>; this class only shows it. Tab order: queue actions, stamp, list controls, selection, list.
/// </summary>
public sealed partial class TaggingPage : UserControl, IDisposable
{
    private readonly DesktopSessionHost _host;
    private readonly TaggingQueueController _queueWork;
    private readonly TextBlock _title = new() { Name = "TaggingTitle", Classes = { "page-title" } };
    // Queue mutations and feedback share a measured row; visible-result controls live directly above the list.
    private readonly ActionToolBar _actions = new() { Name = "TaggingActions", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TaggingControlRow _listControls = new() { Name = "TaggingListControls" };
    private readonly ActionToolBar _listPresentation = new() { Name = "TaggingListPresentation" };
    private readonly Border _error = new() { Name = "TaggingError", Classes = { "workspace-error" }, IsVisible = false };
    private readonly TextBlock _errorText = new() { Name = "TaggingErrorText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _errorSource = new() { Name = "TaggingErrorSource", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _retryLoad = new() { Name = "TaggingRetryLoad", Classes = { "subtle" } };
    // Counters, mini bar and percentage are supporting feedback in the queue-action row.
    private readonly StackPanel _progress = new() { Name = "TaggingProgress", Spacing = 2 };
    private readonly TextBlock _progressText = (new TextBlock { Name = "TaggingProgressText", Classes = { "tnum" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }).MetaText();
    internal const double MiniBarWidth = 96;
    private readonly ProgressBar _bar = new() { Name = "TaggingProgressBar", Minimum = 0, Maximum = 100, Width = MiniBarWidth, Height = 6, MinHeight = 0, MaxHeight = 6 };
    private readonly TextBlock _percent = (new TextBlock { Name = "TaggingPercent", Classes = { "muted", "tnum" }, VerticalAlignment = VerticalAlignment.Center }).MetaText();
    private readonly Button _start = new() { Name = "TaggingStart", Classes = { "primary" } };
    // Passive supporting context for Start: never a second spatially separated continue action.
    private readonly TextBlock _lastShown = new() { Name = "TaggingLastShown", Classes = { "muted", "tnum" }, MaxWidth = 280,
        TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
    private readonly StackPanel _startContext = new() { Name = "TaggingStartContext", Spacing = 4 };
    // TQ3: browsing the gallery marks nothing as finished; said once, quietly, while a walk is under way.
    private readonly TextBlock _browseHint = new() { Name = "TaggingBrowseHint", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, IsVisible = false };
    private readonly TaggingControlRow _queueActionRow = new() { Name = "TaggingQueueActionRow" };
    private readonly StackPanel _filterPanel = new() { Name = "TaggingFilterPanel", Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _filterLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _filter = new() { Name = "TaggingFilter", MinWidth = 160 };
    private readonly HelpButton _help = new() { Name = "TaggingHelp", HelpKey = "help.taggingPage" };
    private readonly Grid _columnHeader = new() { Name = "TaggingColumns", ColumnDefinitions = TaggingQueueRowView.BuildColumns(true), ColumnSpacing = TaggingQueueRowView.ColumnGap, Margin = new Thickness(12, 0, 12, 0),
        // A right-click between the header words has to find the header too (it carries the column menu), so it takes a transparent fill.
        Background = Brushes.Transparent };
    private readonly Border _columnHeaderSurface = new() { Name = "TaggingColumnHeaderSurface", Classes = { "workspace-table-header" } };
    private readonly ListBox _list = new() { Name = "TaggingList", Classes = { "tagging-table" }, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _empty = new() { Name = "TaggingEmpty" };
    private readonly WorkspaceEmptyState _emptyState = new() { Name = "TaggingEmptyState", IsVisible = false };
    // Empty-state actions side by side, centred; they wrap to a second line on a narrow page instead of running off both edges (like the Media page's).
    private readonly WrapPanel _emptyActions = new() { Name = "TaggingEmptyActions", ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _emptyMedia = new() { Name = "TaggingEmptyMedia" };
    private readonly Button _emptyHow = new() { Name = "TaggingEmptyHow", Classes = { "link" } };
    private readonly Button _emptyDeleteGroup = new() { Name = "TaggingEmptyDeleteGroup", Classes = { "danger" } };
    private readonly Button _emptyClearDone = new() { Name = "TaggingEmptyClearDone" };
    private readonly Button _emptyReopen = new() { Name = "TaggingEmptyReopen" };
    private readonly Button _emptyShowAll = new() { Name = "TaggingEmptyShowAll" };
    private readonly TextBlock _status = new() { Name = "TaggingStatus", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaggingThumbnails _thumbnails;
    private readonly IDisposable _keyContext;
    private MediaLibraryUserData? _userData;
    private bool _compactRows;
    private bool _compactTable;
    // TQ6: the Type and Group columns stand only while the shown rows differ in them (they said the same thing on every row of a typical queue).
    private bool _showKindColumn = true, _showGroupColumn = true;
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
        _keyContext = MeowChan.Desktop.Controls.KeyContextHost.Install(this, _ => "tagging");
        Content = BuildLayout();
        _start.Click += (_, _) => OpenActions.Start(() => StartAsync(null), "tagging.start");
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
    /// <summary>
    /// The media index the media page holds in memory (null while it has not been built or read yet). Queue entries without an in-memory
    /// attachment revision key use this index. A missing or ambiguous match leaves the tag count unknown (a dash).
    /// </summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>?>? ReadIndexEntries { get; set; }
    /// <summary>True when the profile asks for the compact row density (Barebones always does); read whenever the list is shown.</summary>
    internal Func<bool>? CompactDensity { get; set; }

    internal TagQueueSnapshot Queue => _queueWork.Queue;
    internal IReadOnlyList<TaggingQueueRow> Rows => _rows;
    internal ListBox List => _list;
    internal Button StartButton => _start;
    internal ComboBox FilterBox => _filter;
    internal TextBlock StatusText => _status;
    internal TextBlock ProgressText => _progressText;
    internal TextBlock LastShownContext => _lastShown;
    internal TextBlock BrowseHint => _browseHint;
    internal Border ErrorPanel => _error;
    internal TextBlock ErrorSourceText => _errorSource;
    internal Button RetryLoadButton => _retryLoad;
    internal bool IsBusy => _queueWork.Busy || _queueWork.Loading;

    internal TaggingQueueFilter Filter
    {
        get => _filter.SelectedIndex is >= 0 and <= (int)TaggingQueueFilter.Untagged ? (TaggingQueueFilter)_filter.SelectedIndex : TaggingQueueFilter.All;
        set => _filter.SelectedIndex = (int)value;
    }

    private Control BuildLayout()
    {
        StackPanel heading = new() { Name = "TaggingHeading", Classes = { "workspace-page-header" },
            [!StackPanel.SpacingProperty] = new DynamicResourceExtension("Workspace.Space.8"), Children = { _title } };

        _error.Child = new StackPanel { [!StackPanel.SpacingProperty] = new DynamicResourceExtension("Workspace.Space.8"), Children = { _errorText, _errorSource, _retryLoad } };
        AutomationProperties.SetLiveSetting(_errorText, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(_errorSource, AutomationLiveSetting.Polite);

        foreach (TaggingControlRow row in new[] { _queueActionRow, _listControls })
        {
            row[!TaggingControlRow.GroupSpacingProperty] = new DynamicResourceExtension("Workspace.Space.16");
            row[!TaggingControlRow.LineSpacingProperty] = new DynamicResourceExtension("Workspace.Space.8");
        }
        _listControls.Margin = new Thickness(0, 0, 0, 8);
        _bar.VerticalAlignment = VerticalAlignment.Center;
        WrapPanel meter = new() { Name = "TaggingProgressMeter", ItemSpacing = 8, LineSpacing = 2, Children = { _bar, _percent } };
        _progress.Children.Add(_progressText);
        _progress.Children.Add(meter);
        _progress.VerticalAlignment = VerticalAlignment.Center;
        // TQ2: the progress stands beside the one primary action (Start / Continue), not at the far edge of the page; the passive
        // "last shown" line and the browse hint stand under both.
        StackPanel startLine = new() { Name = "TaggingStartLine", Orientation = Orientation.Horizontal,
            [!StackPanel.SpacingProperty] = new DynamicResourceExtension("Workspace.Space.16"), Children = { _start, _progress } };
        _startContext.Children.Add(startLine);
        _startContext.Children.Add(_lastShown);
        _startContext.Children.Add(_browseHint);
        _queueActionRow.Children.Add(_actions);

        _filterPanel.Children.Add(_filterLabel);
        _filterPanel.Children.Add(_filter);

        // The title starts where every page title starts (PageHeader.PageTopOffset, LY-03); the page has no margin of its own.
        StackPanel header = new() { Name = "TaggingSections",
            [!StackPanel.SpacingProperty] = new DynamicResourceExtension("Workspace.Space.16"),
            Margin = new Thickness(12, PageHeader.PageTopOffset, 12, 16) };
        header.Children.Add(heading);
        header.Children.Add(_error);
        // A confirmation belongs immediately after its trigger row, before the next major section.
        StackPanel queueControls = new() { Name = "TaggingQueueControls",
            [!StackPanel.SpacingProperty] = new DynamicResourceExtension("Workspace.Space.8"),
            Children = { _queueActionRow, _status, BuildClearConfirmation() } };
        header.Children.Add(queueControls);
        header.Children.Add(BuildStamp());
        SizeChanged += (_, _) =>
        {
            UpdateTableLayout();
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _list.ItemTemplate = RowTemplate();
        _list.ContainerPrepared += (_, e) =>
        {
            TaggingQueueRow? row = e.Container.DataContext as TaggingQueueRow ?? (e.Index >= 0 && e.Index < _rows.Count ? _rows[e.Index] : null);
            if (e.Container is ListBoxItem item && row != null)
            {
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
        _emptyActions.Children.Add(_emptyDeleteGroup);
        _emptyState.Children.Add(_emptyActions);
        _emptyState.Children.Add(_emptyHow);
        _emptyHow.HorizontalAlignment = HorizontalAlignment.Center;
        Panel body = new();
        body.Children.Add(_list);
        body.Children.Add(_grid);
        body.Children.Add(_emptyState);
        // The selection bar (only with a selection) stands above the column header; the toolbar above it never moves.
        Grid listArea = new() { Name = "TaggingListArea", RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 0 };
        listArea.Children.Add(_listControls);
        Control selection = BuildSelectionBar();
        Grid.SetRow(selection, 1);
        listArea.Children.Add(selection);
        _columnHeaderSurface.Child = _columnHeader;
        Grid table = new() { Name = "TaggingTable", RowDefinitions = new RowDefinitions("Auto,*"), Children = { _columnHeaderSurface } };
        Grid.SetRow(body, 1);
        table.Children.Add(body);
        Border surface = new() { Name = "TaggingListSurface", Child = table, Classes = { "workspace-section-flush" } };
        Grid.SetRow(surface, 2);
        listArea.Children.Add(surface);

        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(header);
        Border list = new() { Child = listArea, Margin = new Thickness(12, 0, 12, 4) };
        Grid.SetRow(list, 1);
        layout.Children.Add(list);
        return layout;
    }

    // The row template reads the density when the rows are built; a change of density builds it again (ApplyDensity).
    private FuncDataTemplate<TaggingQueueRow> RowTemplate()
    {
        bool compact = _compactRows;
        bool kind = _showKindColumn, group = _showGroupColumn;
        return new FuncDataTemplate<TaggingQueueRow>((_, _) => new TaggingQueueRowView(_thumbnails, TagCountOf, compact, ShowPreviewColumn, _compactTable, kind, group));
    }

    /// <summary>The number of own media tags of an entry, or null when the tag store is not known (shown as a dash).</summary>
    internal int? TagCountOf(TagQueueItem item)
    {
        if (_userData == null) return null;
        MediaLibraryEntryState state = MediaLibraryEntryState.Of(EntryOf(item), _userData);
        return state.TagsKnown && state.TagSubject != null ? state.MediaTags.Count : null;
    }

    private IReadOnlyList<MediaLibraryEntry>? _resolverSource;
    private TagQueueKeyResolver? _keyResolver;

    /// <summary>The entry with the attachment revision key of its bound version: the stored one, else the media index's (never guessed).</summary>
    internal TagQueueItem Effective(TagQueueItem item)
    {
        if (item.AttachmentRevisionKey != null || item.PostId == null || item.Position == null) return item;
        IReadOnlyList<MediaLibraryEntry>? entries = ReadIndexEntries?.Invoke();
        if (entries == null) return item;
        if (!ReferenceEquals(entries, _resolverSource) || _keyResolver == null)
        {
            _keyResolver = new TagQueueKeyResolver(entries);
            _resolverSource = entries;
        }
        return _keyResolver.Resolve(item);
    }

    /// <summary>The media index entry the page uses for tags: <see cref="TagQueueItem.ToLibraryEntry"/> of <see cref="Effective"/>.</summary>
    internal MediaLibraryEntry EntryOf(TagQueueItem item) => Effective(item).ToLibraryEntry();

    // The profile's density (or Barebones) changed what a row should be: the rows are built again.
    private void ApplyDensity()
    {
        bool compact = WorkViewDensity.Compact(_workView) || CompactDensity?.Invoke() == true;
        if (compact == _compactRows) return;
        _compactRows = compact;
        _list.Classes.Set("compact", compact);
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

    // TQ6: a column that carries one value for every shown row is left out; it returns as soon as the rows differ again.
    private void UpdateOptionalColumns()
    {
        bool kind = _rows.Select(row => row.Item.Kind).Distinct().Skip(1).Any();
        bool group = _rows.Select(row => row.GroupName).Distinct().Skip(1).Any();
        if (kind == _showKindColumn && group == _showGroupColumn) return;
        _showKindColumn = kind;
        _showGroupColumn = group;
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
        Dispatcher.UIThread.Post(FocusOnOpen, DispatcherPriority.Input);
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
        if (_queueWork.ErrorText == null) FocusOnOpen();
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

    // TQ1: opening the page selects nothing (a selection raises the selection bar with its destructive "Remove"); the keyboard lands on Start,
    // the one primary action, or on the list when there is nothing to start. A row the person selected keeps its selection and focus.
    internal void FocusOnOpen()
    {
        if (_queueWork.Ended || !IsEffectivelyVisible) return;
        if (_rows.Count > 0 && ActiveList.IsEffectivelyVisible && ActiveList.SelectedIndex >= 0)
            (ActiveList.ContainerFromIndex(ActiveList.SelectedIndex) as Control ?? ActiveList).Focus();
        else if (_start.IsEffectivelyEnabled) _start.Focus();
        else if (_rows.Count > 0 && ActiveList.IsEffectivelyVisible) ActiveList.Focus();
    }

    // Returning the keyboard to the content after a command or a closed menu: the list keeps a place to stand (the next open row).
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
            UpdateOptionalColumns();
            // Only the shown list is bound: the other one holds no rows (no tiles or rows built behind the scenes).
            // Clear both native selection collections before replacing ItemsSource. Avalonia can retain the old
            // selected item until the new containers are materialized; adding the carried row then used to leave
            // the stale row beside it, making the counter grow every time the work view was revisited.
            ListBox hidden = _viewMode == TaggingViewMode.Grid ? _list : _grid;
            hidden.SelectedItems?.Clear();
            hidden.SelectedItem = null;
            ActiveList.SelectedItems?.Clear();
            ActiveList.SelectedItem = null;
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
        _progressText.Text = TaggingQueueView.DoneText(queue, _selectedGroupId, _ungroupedSelected, Scope);
        // The short sentence says how far the queue is; the open / finished / skipped breakdown is its tooltip and help text.
        string breakdown = TaggingQueueView.ProgressText(queue, _selectedGroupId, _ungroupedSelected, Scope);
        ToolTip.SetTip(_progressText, breakdown);
        _bar.Value = TaggingQueueView.Share(queue, _selectedGroupId, _ungroupedSelected, Scope) * 100;
        _percent.Text = TaggingQueueView.PercentText(queue, _selectedGroupId, _ungroupedSelected, Scope);
        RenderGroups();
        AutomationProperties.SetName(_bar, Texts.T("tagging.page.progressName"));
        AutomationProperties.SetHelpText(_bar, _progressText.Text + " · " + _percent.Text + " · " + breakdown);
        bool empty = _rows.Count == 0;
        _emptyState.IsVisible = empty && _queueWork.Loaded && _queueWork.ErrorText == null;
        _list.IsVisible = !empty && _viewMode == TaggingViewMode.List;
        _grid.IsVisible = !empty && _viewMode == TaggingViewMode.Grid;
        _columnHeader.IsVisible = !empty && _viewMode == TaggingViewMode.List;
        _columnHeaderSurface.IsVisible = _columnHeader.IsVisible;
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

    // The work-view gate controls supporting context; the only activation target is Start.
    private void RenderLastShown(TagQueueSnapshot queue)
    {
        string? text = TaggingQueueView.LastShownText(queue, _selectedGroupId, _ungroupedSelected, Scope);
        _lastShown.Text = text; // its visibility is the work-view gate's (RefreshWorkView)
        string? done = CommandCatalog.ShortcutDisplay("tags.queue.done-next");
        _browseHint.Text = text == null ? null : Texts.T("tagging.strip.browseHint", done ?? CommandCatalog.Label("tags.queue.done-next"));
        if (text != null)
        {
            AutomationProperties.SetName(_lastShown, text);
            AutomationProperties.SetHelpText(_lastShown, Texts.T("tagging.page.lastShown.help"));
            ToolTip.SetTip(_lastShown, text);
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
        AutomationProperties.SetHelpText(_start, _lastShown.Text);
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
        _columnHeader.ColumnDefinitions = TaggingQueueRowView.BuildColumns(ShowPreviewColumn, _compactTable, _showKindColumn, _showGroupColumn);
        _columnHeader.ColumnSpacing = TaggingQueueRowView.ColumnGap;
        _columnHeader.Children.Clear();
        string[] keys = { "", "tagging.page.col.preview", "tagging.page.col.kind", "tagging.page.col.name", "tagging.page.col.source", "tagging.page.col.group", "tagging.page.col.tags", "tagging.page.col.state" };
        for (int column = 0; column < keys.Length; column++)
        {
            // The preview column has no header word: the picture needs none, and "Preview" / "Vorschau" does not fit 56 dip (the column
            // menu keeps the word).
            // One line per header word: a narrow column trims it with the full word as the tooltip instead of breaking the word over two lines.
            TextBlock header = new TrimTipTextBlock
            {
                Text = column switch { 0 => "#", 1 => string.Empty, _ => Texts.T(keys[column]) }, Classes = { "workspace-meta" },
                TextAlignment = column is 0 or 6 ? TextAlignment.Right : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = column == 0 ? TaggingQueueRowView.NumberMargin(ShowPreviewColumn, _showKindColumn)
                    : column == 6 ? TaggingQueueRowView.TagsMargin(_showGroupColumn) : default
            };
            header.IsVisible = (column != 1 || ShowPreviewColumn) && (column != 2 || _showKindColumn) && (column != 5 || _showGroupColumn);
            Grid.SetColumn(header, column);
            _columnHeader.Children.Add(header);
        }
        // The list items name themselves (number, file, state); the header row is only a visual aid.
        AutomationProperties.SetAccessibilityView(_columnHeader, AccessibilityView.Raw);
    }

    /// <summary>The window closes: the page stops following the host and the language.</summary>
    internal void End() => Dispose();

    /// <summary>Releases the key-context lease, menus and cancellation lifetime owned by this page.</summary>
    public void Dispose()
    {
        if (_queueWork.Ended) return;
        _queueWork.End();
        _keyContext.Dispose();
        EndTagging();
        _menuHost?.Dispose();
        _gridMenuHost?.Dispose();
        _tagDialog?.Dispose();
        _tagDialog = null;
        EndColumns();
        _stateMenuHost?.Dispose();
        _groupMenuHost?.Dispose();
        _groupManageHost?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        Texts.Changed -= OnLanguageChanged;
        _host.StateChanged -= OnHostChanged;
    }
}
