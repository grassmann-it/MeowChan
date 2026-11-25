using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    internal static readonly string NewSearchTabName = "BooruNewSearchTab";
    internal static readonly string DownloadAllName = "BooruDownloadAll";
    internal static readonly string BulkStatusName = "BooruBulkStatus";

    internal static readonly string SubmitCommand = "booru.search.submit";
    internal static readonly string AlternateSubmitCommand = "booru.search.alternate";
    internal static readonly string OpenWatchersName = "BooruOpenWatchers";
    internal static readonly string WatchersTabName = "BooruWatchersTab";
    internal static readonly string CloseWatchersName = "BooruCloseWatchers";
    internal static readonly string CloseSearchTabName = "BooruCloseSearchTab";
    internal static readonly string TabDraftMarkerName = "BooruTabPendingDraft";
    internal static readonly string TabLimitNoticeName = "BooruTabLimitNotice";

    private sealed record TabHeader(TextBlock Title, Button Close, ContentControl Draft);
    private sealed record TabResults(string? Cursor, HashSet<string> Selected);
    private sealed class SearchTabControl(TabItem item, TabHeader header, Func<ContextMenuBuilder> compose, Action fallbackFocus) : IDisposable
    {
        private readonly ContextMenuHost _menu = new(item, compose, fallbackFocus);
        internal TabItem Item { get; } = item;
        internal TabHeader Header { get; } = header;
        internal void CloseMenu() => _menu.Close();
        public void Dispose() => _menu.Dispose();
    }

    private readonly Button _openWatchers = new() { Name = OpenWatchersName };
    private readonly TextBlock _tabLimitText = new() { Name = TabLimitNoticeName, TextWrapping = TextWrapping.Wrap };
    private readonly Border _tabLimitHost = new() { Classes = { "workspace-status", "workspace-status-warning" }, IsVisible = false };
    private TabHeader? _watcherHeader;
    private int? _refusedTabLimit;

    internal const string AlternateSubmitName = "BooruSubmitNewTab";
    private readonly Button _alternateSubmit = new() { Name = AlternateSubmitName, Focusable = false, IsTabStop = false, Classes = { "subtle" } };
    private readonly Button _downloadAll = new() { Name = DownloadAllName, Focusable = false, IsTabStop = false, Classes = { "subtle" } };
    private readonly Border _bulkHost = new() { Name = BulkStatusName, Classes = { "workspace-status" }, IsVisible = false };
    private readonly TextBlock _bulkText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _newTabButton = new() { Name = NewSearchTabName, Focusable = false, IsTabStop = false, Classes = { "booru-add-tab-button" } };
    private readonly TabItem _addTab = new() { Focusable = false, IsTabStop = false, Classes = { "booru-add-tab" } };
    private readonly Dictionary<int, SearchTabControl> _searchItems = new();
    private readonly MiddleClickTracker _tabMiddleClick = new();
    private IInputElement? _backgroundTabFocus;
    private readonly Dictionary<int, TabResults> _tabResults = new();
    private BooruSearchTabs? _searchTabs;
    private int? _renderedSearchTab;
    private Control? _searchContent;
    private TabItem? _searchContentOwner;
    private bool _watchersOpen;
    private bool _syncingTabs;
    private bool _selectNewTab;

    /// <summary>The setting that limits the total number of search tabs (read at each use); the default without a host.</summary>
    internal Func<int>? SearchTabsLimit { get; set; }

    internal BooruSearchTabs SearchTabs => _searchTabs ?? throw new InvalidOperationException("The search tabs are built with the body.");

    internal void RestoreSearchTabs(BooruTabSnapshot snapshot)
    {
        _selectNewTab = true;
        try { SearchTabs.Restore(snapshot); }
        finally { _selectNewTab = false; }
        Render(force: true);
    }

    private void InitializeSearchTabs(Control content)
    {
        _searchTabs = new BooruSearchTabs(_model.Search,
            () => SearchTabsLimit?.Invoke() ?? _listProfile?.BooruSearchTabs ?? Config.BooruSearchTabsDefault);
        AutomationProperties.SetAutomationId(_tabs, TabsName);
        AutomationProperties.SetAutomationId(_newTabButton, NewSearchTabName);
        AutomationProperties.SetAutomationId(_openWatchers, OpenWatchersName);
        AutomationProperties.SetAutomationId(_watchersTab, WatchersTabName);
        AutomationProperties.SetAutomationId(_tabLimitText, TabLimitNoticeName);
        AutomationProperties.SetAutomationId(_downloadAll, DownloadAllName);
        AutomationProperties.SetAutomationId(_bulkHost, BulkStatusName);
        _searchContent = content;
        _addTab.Header = _newTabButton;
        _tabs.Items.Add(_addTab);
        CommandBindings.EnsureIconBeforeText(_newTabButton, "plus");
        _newTabButton.Click += (_, _) =>
        {
            if (OpenSearchTab()) return;
            if (IsSearchItem(_tabs.SelectedItem)) FocusSearchInput();
            else _watchersTab.Focus();
        };
        _tabs.SizeChanged += (_, e) => { if (e.WidthChanged) QueueActiveTabReveal(); };
        _watcherHeader = MakeClosableHeader(_watchersTab, CloseWatchers, CloseWatchersName);
        _searchTabs.Changed += SyncSearchTabs;
        _tabs.SelectionChanged += OnTabsSelectionChanged;
        _tabs.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _tabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        SyncSearchTabs();
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        TabItem? target = MiddleClickTracker.IsPress(e, _tabs) ? ClosableTabAt(e.Source) : null;
        _tabMiddleClick.Pressed(target);
        _backgroundTabFocus = target is { IsSelected: false } ? TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() : null;
        if (target is not null) e.Handled = true;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_closed || !MiddleClickTracker.IsRelease(e)) return;
        IInputElement? focus = _backgroundTabFocus;
        _backgroundTabFocus = null;
        TabItem? tab = ClosableTabAt(e.Source);
        // The release can still be routed to the pressed header through pointer capture.
        if (tab is null || !new Rect(tab.Bounds.Size).Contains(e.GetPosition(tab))) tab = null;
        if (!_tabMiddleClick.Released(tab)) return;
        e.Handled = true;
        if (tab?.Tag is int id) CloseSearchTab(id);
        else CloseWatchers();
        focus?.Focus();
    }

    private TabItem? ClosableTabAt(object? source)
    {
        if (source is not Visual visual) return null;
        foreach (Visual ancestor in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(ancestor, _tabs) || ancestor is Button) break;
            if (ancestor is TabItem item && (item.Tag is int || ReferenceEquals(item, _watchersTab)))
                return item;
        }
        return null;
    }

    private Control BuildBulkStatus() => _bulkHost;

    // ---- result line and pager under the results, view switch with labels --------------------------------

    private readonly Border _bottomStatusHost = new() { Name = "BooruResultsLine", IsVisible = false };
    private readonly TextBlock _bottomStatusText = new() { TextWrapping = TextWrapping.Wrap };
    private bool _wideViewToggle;
    private string? _viewToggleKey;
    /// <summary>The page is wide enough for the words "List" and "Grid" beside the icons of the view switch (dip).</summary>
    private const double ViewToggleLabelsMinimumWidth = 720;

    /// <summary>The result status and pager under the results. In the gallery the pager announces successful searches.</summary>
    private Control BuildBottomBar()
    {
        _bottomStatusHost.Child = _bottomStatusText;
        AutomationProperties.SetLiveSetting(_bottomStatusText, AutomationLiveSetting.Polite);
        _tabs.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty || _closed) return;
            bool wide = _tabs.Bounds.Width >= ViewToggleLabelsMinimumWidth;
            if (wide != _wideViewToggle) { _wideViewToggle = wide; RenderViewToggleLabels(); }
        };
        _bottomBar.Children.AddRange([_bottomStatusHost, _pagerRow, _pageLimit]);
        return _bottomBar;
    }

    /// <summary>A successful or empty result is reported under the list; failures, pauses and the repair route stay above it.</summary>
    private void RenderBottomStatus(BooruSearchView view)
    {
        bool below = view.Status is BooruSearchStatus.Results or BooruSearchStatus.Empty && !_retry.IsVisible;
        bool compact = below && view.Status == BooruSearchStatus.Results && _pager.IsVisible
            && _resultView == BooruResultView.Gallery;
        _bottomStatusHost.IsVisible = below && !compact;
        WorkViewElement.SetStateVisible(_statusHost, !below);
        string? executed = ExecutedSearchText(view);
        string? summary = compact ? string.Join(Environment.NewLine, _statusText.Text, executed) : executed;
        ToolTip.SetTip(_pageInfo, summary);
        AutomationProperties.SetLiveSetting(_pageInfo, compact ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
        AutomationProperties.SetName(_pageInfo, compact ? $"{_pageInfo.Text} · {_statusText.Text}" : null);
        AutomationProperties.SetHelpText(_pageInfo, executed);
        if (!below) return;
        _bottomStatusText.Text = _statusText.Text;
        AutomationProperties.SetName(_bottomStatusText, _statusText.Text);
        ApplyTone(_bottomStatusHost, ToneOf(view.Status));
    }

    /// <summary>The List/Grid switch shows the words next to its icons while there is room; narrow, it is the icons alone (the words stay tooltip and name).</summary>
    private void RenderViewToggleLabels()
    {
        string key = Texts.Language + "|" + _wideViewToggle;
        foreach ((ToggleButton button, string icon, string textKey) in new[] { (_viewToggle.First, "list", "tagging.page.view.list"), (_viewToggle.Second, "grid", "tagging.page.view.grid"), (_viewToggle.Third, "image", "booru.gallery.name") })
        {
            if (!_wideViewToggle) { if (_viewToggleKey is not null && _viewToggleKey.EndsWith("True", StringComparison.Ordinal)) button.Content = CommandBindings.NamedIcon(icon); continue; }
            StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(CommandBindings.NamedIcon(icon));
            content.Children.Add(new TextBlock { Text = Texts.T(textKey), VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
        }
        _viewToggleKey = key;
    }

    // ---- tabs ------------------------------------------------------------------------------------------

    private async Task SubmitSearchAsync(bool alternate = false)
    {
        if (_closed) return;
        BooruSearchModel search = _model.Search;
        search.SetInput(_input.Text); // The field's change event may still be queued.
        if (!search.CanSubmit) return;
        if (alternate && SearchTabs.ActiveId.HasValue)
        {
            BooruSearchView draft = search.View;
            if (!OpenSearchTab()) return;
            search.SetMode(draft.Mode);
            search.SetInput(draft.Input);
        }
        else ClearTabLimitNotice();
        bool keepInputFocus = SearchTabs.ActiveId is null && _input.IsKeyboardFocusWithin;
        Task pending = search.SubmitAsync();
        if (keepInputFocus && SearchTabs.ActiveId is { } first) QueueSearchInputFocus(first);
        await pending;
    }

    private void RenderSearchActions(BooruSearchView view)
    {
        BooruSearchModel model = _model.Search;
        string label = Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.lookup" : "booru.ui.search");
        _submit.Content = label;
        _submit.IsEnabled = model.CanSubmit;
        string? tip = view.Parsed.Kind == BooruInputKind.Invalid ? BooruText.Issue(view.Parsed.Issue)
            : model.IsPaused ? BooruText.SearchStatus(view, paused: true) : null;
        if (_submit.IsEnabled && CommandCatalog.ShortcutTooltipDisplay(SubmitCommand, "booru") is { } shortcut)
            tip = label + " · " + shortcut;
        string alternateLabel = Texts.T("booru.tabs.searchNewShort");
        _alternateSubmit.Content = alternateLabel;
        _alternateSubmit.IsEnabled = model.CanSubmit;
        AutomationProperties.SetName(_alternateSubmit, alternateLabel);
        string? alternateKey = CommandCatalog.ShortcutTooltipDisplay(AlternateSubmitCommand, "booru");
        ToolTip.SetTip(_alternateSubmit, alternateKey == null ? alternateLabel : alternateLabel + " · " + alternateKey);
        ToolTip.SetTip(_submit, tip);
        ToolTip.SetShowOnDisabled(_submit, true);
        AutomationProperties.SetHelpText(_submit, tip ?? string.Empty);
    }

    internal static bool MatchesSearchShortcut(KeyEventArgs e, string command, EffectiveShortcutBindings bindings) =>
        bindings.Matches(e, command, "booru");

    internal bool TryShowQueryInNewTab(string query)
    {
        if (!OpenSearchTab()) return false;
        ShowQuery(query);
        return true;
    }

    private bool OpenSearchTab(int? copyFrom = null)
    {
        if (_closed || _searchTabs is not { } tabs) return false;
        string? copiedCursor = copyFrom is not { } source ? null
            : source == tabs.ActiveId ? _resultViewer?.CurrentPostId ?? _cursorPostId
            : _tabResults.GetValueOrDefault(source)?.Cursor;
        int id;
        _selectNewTab = true;
        try { id = tabs.Add(copyFrom); }
        finally { _selectNewTab = false; }
        if (id < 0)
        {
            _refusedTabLimit = tabs.Limit;
            RenderTabLimitNotice();
            return false;
        }
        ClearTabLimitNotice();
        SetCursor(copiedCursor);
        QueueSearchInputFocus(id);
        return true;
    }

    private void QueueSearchInputFocus(int? id) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && SearchTabs.ActiveId == id && IsSearchItem(_tabs.SelectedItem)) FocusSearchInput();
    }, DispatcherPriority.Loaded);

    private void ClearTabLimitNotice()
    {
        if (_refusedTabLimit is null) return;
        _refusedTabLimit = null;
        RenderTabLimitNotice();
    }

    private void RenderTabLimitNotice()
    {
        _tabLimitText.Text = _refusedTabLimit is { } limit ? Texts.T("booru.tabs.limit", limit) : string.Empty;
        _tabLimitHost.IsVisible = _refusedTabLimit.HasValue;
    }

    private TabItem ActiveSearchItem => SearchTabs.ActiveId is { } id ? _searchItems[id].Item : _addTab;

    internal void ShowWatchers()
    {
        if (_closed || _searchTabs is null) return;
        _watchersOpen = true;
        SyncSearchTabs();
        _tabs.SelectedItem = _watchersTab;
        _watchersTab.Focus();
    }

    internal void CloseWatchers()
    {
        if (_closed || !_watchersOpen) return;
        bool wasActive = ReferenceEquals(_tabs.SelectedItem, _watchersTab);
        _watchersOpen = false;
        SyncSearchTabs();
        if (wasActive) QueueSearchInputFocus(SearchTabs.ActiveId);
    }

    private bool IsSearchItem(object? item) => item is TabItem { Tag: int } || ReferenceEquals(item, _addTab);

    private void OnTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabs || _closed || e.Source != _tabs || _tabs.SelectedItem is not TabItem item) return;
        if (ReferenceEquals(item, _addTab) && SearchTabs.ActiveId.HasValue)
        {
            _tabs.SelectedItem = ActiveSearchItem;
            return;
        }
        _resultMiddleClick.Cancel();
        _more.ContextMenu?.Close();
        ClearTabLimitNotice();
        QueueActiveTabReveal();
        OnModelChanged();
        if (item.Tag is int searchId) SearchTabs.Select(searchId);
    }

    private void AttachSearchContent(TabItem owner)
    {
        if (ReferenceEquals(owner, _searchContentOwner)) return;
        if (_searchContentOwner is not null) _searchContentOwner.Content = null;
        _searchContentOwner = owner;
        owner.Content = _searchContent;
    }

    private TabResults CurrentTabResults() => new(_resultViewer?.CurrentPostId ?? _cursorPostId, _selectedResults);

    /// <summary>Brings the strip and shared search surface in line with the tab model.</summary>
    private void SyncSearchTabs()
    {
        if (_closed || _searchTabs is not { } tabs) return;
        if (_renderedSearchTab != tabs.ActiveId)
        {
            if (_renderedSearchTab is { } previous) _tabResults[previous] = CurrentTabResults();
            _renderedSearchTab = tabs.ActiveId;
            DismissResultViewer();
            ReleaseGallery();
            TabResults? restored = tabs.ActiveId is { } active ? _tabResults.GetValueOrDefault(active) : null;
            _cursorPostId = restored?.Cursor;
            _selectedResults = restored?.Selected ?? new HashSet<string>(StringComparer.Ordinal);
            _resultsKey = null;
        }
        _syncingTabs = true;
        try
        {
            IReadOnlyList<int> ids = tabs.Ids;
            HashSet<int> wanted = ids.ToHashSet();
            bool keepWatchers = _watchersOpen && !_selectNewTab && ReferenceEquals(_tabs.SelectedItem, _watchersTab);
            foreach (int id in _tabResults.Keys.Where(id => !wanted.Contains(id)).ToArray())
                _tabResults.Remove(id);
            foreach (int id in _searchItems.Keys.Where(id => !wanted.Contains(id)).ToArray())
            {
                _tabs.Items.Remove(_searchItems[id].Item);
                _searchItems[id].Dispose();
                _searchItems.Remove(id);
            }
            foreach (int id in ids)
            {
                if (_searchItems.ContainsKey(id)) continue;
                TabItem item = new() { Tag = id };
                TabHeader header = MakeClosableHeader(item, () => CloseSearchTab(id));
                _searchItems[id] = new(item, header, () => BuildSearchTabMenu(id), FocusSearchInput);
                _tabs.Items.Insert(_tabs.Items.IndexOf(_addTab), item);
            }

            if (_watchersOpen && !_tabs.Items.Contains(_watchersTab)) _tabs.Items.Add(_watchersTab);
            if (!_watchersOpen) _tabs.Items.Remove(_watchersTab);
            TabItem activeItem = ActiveSearchItem;
            AttachSearchContent(activeItem);
            _tabs.SelectedItem = keepWatchers ? _watchersTab : activeItem;
        }
        finally { _syncingTabs = false; }
        QueueActiveTabReveal();
        OnModelChanged();
    }

    private ContextMenuBuilder BuildSearchTabMenu(int id) => new ContextMenuBuilder()
        .Item("booru.tabs.duplicate", Texts.T("booru.tabs.duplicate"), () => OpenSearchTab(id),
            SearchTabs.CanAdd, reason: SearchTabs.CanAdd ? null : Texts.T("booru.tabs.limit", SearchTabs.Limit),
            availableWhen: () => SearchTabs.CanAdd && _searchItems.ContainsKey(id), icon: "copy")
        .Item("booru.tabs.close", Texts.T("booru.tabs.close"), () => CloseSearchTab(id), icon: "x")
        .Item("booru.tabs.closeOthers", Texts.T("booru.tabs.closeOthers"), () =>
        {
            foreach (int other in SearchTabs.Ids.Where(other => other != id)) CloseSearchTab(other);
        }, SearchTabs.Ids.Count > 1, availableWhen: () => _searchItems.ContainsKey(id) && SearchTabs.Ids.Count > 1, icon: "x");

    private TabHeader MakeClosableHeader(TabItem item, Action closeTab, string? closeName = null)
    {
        item.Classes.Add("booru-tab");
        TextBlock title = new() { MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        closeName ??= CloseSearchTabName;
        Button close = new() { Name = closeName, Focusable = false, IsTabStop = false, Classes = { "ghost", "booru-tab-close" } };
        AutomationProperties.SetAutomationId(close, closeName);
        close.AttachedToVisualTree += (_, _) => { if (close.Content is not Control) close.Content = CommandBindings.NamedIcon("x", 12); };
        close.Click += (_, e) => { e.Handled = true; closeTab(); };
        ContentControl draft = new()
        {
            Name = TabDraftMarkerName, VerticalAlignment = VerticalAlignment.Center, IsVisible = false
        };
        AutomationProperties.SetAutomationId(draft, TabDraftMarkerName);
        AutomationProperties.SetAccessibilityView(draft, AccessibilityView.Raw);
        draft.AttachedToVisualTree += (_, _) => draft.Content ??= CommandBindings.NamedIcon("edit-2", 12);
        StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { title, draft, close } };
        item.Header = header;
        item.SizeChanged += (_, e) => { if (e.WidthChanged && item.IsSelected) QueueActiveTabReveal(); };
        return new TabHeader(title, close, draft);
    }

    private void QueueActiveTabReveal() => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || _tabs.SelectedItem is not TabItem { IsEffectivelyVisible: true } item) return;
        item.BringIntoView();
        int addIndex = _tabs.Items.IndexOf(_addTab);
        if (addIndex > 0 && ReferenceEquals(_tabs.Items[addIndex - 1], item)) _newTabButton.BringIntoView();
    }, DispatcherPriority.Loaded);

    private void CloseSearchTab(int id)
    {
        if (_closed || _searchTabs is not { } tabs) return;
        bool wasActive = tabs.ActiveId == id;
        if (!tabs.Close(id)) return;
        ClearTabLimitNotice();
        if (wasActive && IsSearchItem(_tabs.SelectedItem)) QueueSearchInputFocus(tabs.ActiveId);
    }

    private static void RenderTabHeader(TabItem item, TabHeader header, string title, BooruSearchView? search = null)
    {
        header.Title.Text = title;
        string? draft = search != null ? DraftSearchText(search) : null;
        string? executed = search != null ? ExecutedSearchText(search) : null;
        string tip = draft == null ? title : string.Join(Environment.NewLine, new[] { executed ?? title, draft });
        header.Draft.IsVisible = draft != null;
        ToolTip.SetTip(header.Title, tip);
        ToolTip.SetTip(header.Draft, tip);
        ToolTip.SetTip(item, tip);
        AutomationProperties.SetHelpText(item, draft == null ? string.Empty : tip);
        string closeName = Texts.T("booru.tabs.closeFor", title);
        ToolTip.SetTip(header.Close, closeName);
        AutomationProperties.SetName(header.Close, closeName);
        AutomationProperties.SetName(item, title);
    }

    // ---- render ----------------------------------------------------------------------------------------

    /// <summary>Tab titles, the buttons next to "Create watcher" and the bulk status; called with every render.</summary>
    private void RenderSearchTabs(BooruSearchView view, BooruWatcherListView watchers)
    {
        if (_searchTabs is not { } tabs) return;
        RenderBottomStatus(view);
        RenderViewToggleLabels();
        foreach (var (id, tab) in _searchItems)
        {
            BooruSearchView? snapshot = tabs.ViewOf(id);
            RenderTabHeader(tab.Item, tab.Header,
                BooruSearchTabs.Title(snapshot, Texts.T("booru.tabs.blank"), postId => Texts.T("booru.tabs.post", postId)), snapshot);
        }
        if (_watcherHeader is { } header)
            RenderTabHeader(_watchersTab, header, Texts.Count("booru.ui.tab.watchers", _model.Watchers.Count));
        int activeWatchers = watchers.Rows.Count(row => row.Watcher.Status is not (BooruWatcherStatus.Paused or BooruWatcherStatus.Removed));
        string watchersLabel = Texts.Count("booru.ui.tab.watchers", activeWatchers);
        string watchersTip = Texts.Count("booru.tabs.watchersTip", activeWatchers);
        _openWatchers.Content = watchersLabel;
        AutomationProperties.SetName(_openWatchers, watchersLabel);
        AutomationProperties.SetHelpText(_openWatchers, watchersTip);
        ToolTip.SetTip(_openWatchers, watchersTip);
        RenderTabLimitNotice();
        string newName = Texts.T("booru.tabs.new");
        _newTabButton.Content = Texts.T("booru.ui.search");
        AutomationProperties.SetName(_newTabButton, newName);
        ToolTip.SetTip(_newTabButton, newName);

        BooruSearchModel model = _model.Search;
        BooruBulkView bulk = model.Bulk;
        bool belongsToTab = bulk.TabId == tabs.ActiveId;
        bool running = bulk.IsRunning && belongsToTab;
        string downloadIcon = running ? "square" : "download";
        if (IconButtons.KeyOf(_downloadAll) != downloadIcon)
            CommandBindings.ShowIconBeforeText(_downloadAll, downloadIcon);
        string downloadName = Texts.T(running ? "booru.bulk.stop" : "booru.bulk.button");
        if (running && bulk.ResultId != view.ResultId) downloadName += " · " + bulk.Query;
        _downloadAll.Content = running ? downloadName : Texts.T("booru.tabs.downloadShort");
        _downloadAll.IsEnabled = running || model.CanDownloadAll;
        string bulkHint = running ? string.Empty
            : bulk.IsRunning ? Texts.T("booru.bulk.otherTab", bulk.Query)
            : model.CanDownloadAll ? Texts.T("booru.bulk.hint", view.PagerLimits.Pages, view.PagerLimits.PostsPerPage)
            : Texts.T("booru.bulk.needsSearch");
        ToolTip.SetTip(_downloadAll, running ? downloadName : downloadName + "\n" + bulkHint);
        ToolTip.SetShowOnDisabled(_downloadAll, true);
        AutomationProperties.SetName(_downloadAll, downloadName);
        AutomationProperties.SetHelpText(_downloadAll, bulkHint);

        // The counts belong to one search: they are shown only beside the results of that same search.
        bool showBulk = belongsToTab && bulk.Phase != BooruBulkPhase.Idle && bulk.ResultId == view.ResultId;
        _bulkHost.IsVisible = showBulk;
        if (!showBulk) return;
        if (running)
        {
            _bulkText.Text = Texts.T("booru.bulk.running", bulk.Query, bulk.PagesRead, bulk.PagerLimits.Pages, bulk.Found, bulk.Queued);
            ApplyTone(_bulkHost, BooruNoticeKind.Info);
            return;
        }
        string end = Texts.T(bulk.End switch
        {
            BooruBulkEnd.PageLimit => "booru.bulk.endLimit",
            BooruBulkEnd.SourceEnd => "booru.bulk.endEnd",
            BooruBulkEnd.Canceled => "booru.bulk.endCanceled",
            _ => "booru.bulk.endBlocked"
        });
        _bulkText.Text = Texts.T("booru.bulk.result", bulk.Query, bulk.Found, bulk.PagesRead, bulk.PagerLimits.Pages, bulk.Queued,
            bulk.AlreadyQueued, bulk.AlreadySaved, bulk.NoOriginal, bulk.Failed) + " " + end;
        ApplyTone(_bulkHost, bulk.End is BooruBulkEnd.Blocked or BooruBulkEnd.Canceled || bulk.Failed > 0 ? BooruNoticeKind.Warning : BooruNoticeKind.Info);
    }
}
