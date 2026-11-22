using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    internal static readonly string NewSearchTabName = "BooruNewSearchTab";
    internal static readonly string OpenInNewTabName = "BooruOpenInNewTab";
    internal static readonly string DownloadAllName = "BooruDownloadAll";
    internal static readonly string BulkStatusName = "BooruBulkStatus";

    internal static readonly string SubmitCommand = "booru.search.submit";
    internal static readonly string AlternateSubmitCommand = "booru.search.alternate";
    internal static readonly string AlternateSubmitName = "BooruAlternateSubmit";
    internal static readonly string OpenWatchersName = "BooruOpenWatchers";
    internal static readonly string WatchersTabName = "BooruWatchersTab";
    internal static readonly string CloseWatchersName = "BooruCloseWatchers";
    internal static readonly string TabLimitNoticeName = "BooruTabLimitNotice";

    private sealed record TabHeader(TextBlock Title, Button Close);

    private readonly Button _alternateSubmit = new() { Name = AlternateSubmitName, Classes = { "subtle" } };
    private readonly Button _openWatchers = new() { Name = OpenWatchersName };
    private readonly TextBlock _tabLimitText = new() { Name = TabLimitNoticeName, TextWrapping = TextWrapping.Wrap };
    private readonly Border _tabLimitHost = new() { Classes = { "workspace-status", "workspace-status-warning" }, IsVisible = false };
    private TabHeader? _watcherHeader;
    private int? _refusedTabLimit;

    internal Func<string>? NewSearchTarget { get; set; }
    private bool OpensNewSearchTab => NewSearchTarget?.Invoke() != MeowChan.Config.BooruSearchTargetCurrentTab;

    private readonly Button _openInNewTab = new() { Name = OpenInNewTabName, Classes = { "subtle" } };
    private readonly Button _downloadAll = new() { Name = DownloadAllName, Classes = { "subtle" } };
    private readonly Border _bulkHost = new() { Name = BulkStatusName, Classes = { "workspace-status" }, IsVisible = false };
    private readonly TextBlock _bulkText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _newTabButton = new() { Name = NewSearchTabName, Classes = { "booru-add-tab-button" } };
    private readonly TabItem _plusTab = new() { Classes = { "booru-tab", "booru-add-tab" } };
    private readonly Dictionary<int, (TabItem Item, TabHeader Header)> _extraTabs = new();
    private List<TabItem>? _tabItems;
    private BooruSearchTabs? _searchTabs;
    private int _renderedSearchTab = BooruSearchTabs.PrimaryId;
    private Control? _searchContent;
    private TabItem? _lastRealTab;
    private bool _syncingTabs;
    private bool _selectNewTab;

    /// <summary>The setting that limits the extra search tabs (read at each use); the default without a host.</summary>
    internal Func<int>? SearchTabsLimit { get; set; }

    internal BooruSearchTabs SearchTabs => _searchTabs ?? throw new InvalidOperationException("The search tabs are built with the body.");

    internal void RestoreSearchTabs(BooruTabSnapshot snapshot)
    {
        _selectNewTab = true;
        try { SearchTabs.Restore(snapshot); }
        finally { _selectNewTab = false; }
        Render(force: true);
    }

    /// <summary>Start with the permanent search and the button for adding searches.</summary>
    private TabItem[] CreateTabItems()
    {
        _searchTabs = new BooruSearchTabs(_model.Search, () => SearchTabsLimit?.Invoke() ?? MeowChan.Config.BooruSearchTabsDefault);
        _searchContent = _searchTab.Content as Control;
        _newTabButton.Click += (_, _) => OpenSearchTab(copyCurrent: false);
        _plusTab.Header = _newTabButton;
        _plusTab.IsTabStop = false;
        _plusTab.Focusable = false;
        _watcherHeader = MakeClosableHeader(_watchersTab, CloseWatchers, CloseWatchersName);
        _tabItems = new List<TabItem> { _searchTab, _plusTab };
        _lastRealTab = _searchTab;
        _searchTabs.Changed += SyncSearchTabs;
        _tabs.SelectionChanged += OnTabsSelectionChanged;
        return _tabItems.ToArray();
    }

    private WrapPanel BuildSearchTabActions()
    {
        CommandBindings.EnsureIconBeforeText(_alternateSubmit, "search");

        CommandBindings.EnsureIconBeforeText(_openWatchers, CommandCatalog.Get("admission.monitor-all").Icon);
        _alternateSubmit.Click += async (_, _) => await SubmitSearchAsync(alternate: true);
        CommandBindings.EnsureIconBeforeText(_openInNewTab, "copy");
        _openInNewTab.Click += (_, _) => OpenSearchTab(copyCurrent: true);
        _downloadAll.Click += async (_, _) =>
        {
            if (_model.Search.Bulk.IsRunning) _model.Search.CancelBulk();
            else await _model.Search.DownloadAllAsync();
        };
        _bulkHost.Child = _bulkText;
        AutomationProperties.SetLiveSetting(_bulkText, AutomationLiveSetting.Polite);
        return new WrapPanel { Name = "BooruSearchActions", Classes = { "workspace-toolbar" },
            Children = { _alternateSubmit, _openInNewTab, _createWatcher, _downloadAll, _cancel } };
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
        return new StackPanel { Spacing = 8, Children = { _bottomStatusHost, _pager, _pageLimit } };
    }

    /// <summary>A successful or empty result is reported under the list; failures, pauses and the repair route stay above it.</summary>
    private void RenderBottomStatus(BooruSearchView view)
    {
        bool below = view.Status is BooruSearchStatus.Results or BooruSearchStatus.Empty && !_retry.IsVisible;
        bool compact = below && view.Status == BooruSearchStatus.Results && _pager.IsVisible
            && _resultView == BooruResultView.Gallery;
        _bottomStatusHost.IsVisible = below && !compact;
        WorkViewElement.SetStateVisible(_statusHost, !below);
        ToolTip.SetTip(_pageInfo, compact ? _statusText.Text : null);
        AutomationProperties.SetLiveSetting(_pageInfo, compact ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
        AutomationProperties.SetName(_pageInfo, compact ? $"{_pageInfo.Text} · {_statusText.Text}" : null);
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
        if (OpensNewSearchTab != alternate)
        {
            BooruSearchView draft = search.View;
            if (!OpenSearchTab(copyCurrent: false)) return;
            search.SetMode(draft.Mode);
            search.SetInput(draft.Input);
        }
        else ClearTabLimitNotice();
        await search.SubmitAsync();
    }

    private void RenderSearchActions(BooruSearchView view)
    {
        BooruSearchModel model = _model.Search;
        bool canSubmit = model.CanSubmit;
        bool opensNewTab = OpensNewSearchTab;
        _submit.Content = Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.lookup" : "booru.ui.search");
        _submit.IsEnabled = canSubmit;
        _alternateSubmit.IsEnabled = canSubmit;
        string? blocked = view.Parsed.Kind == BooruInputKind.Invalid ? BooruText.Issue(view.Parsed.Issue)
            : model.IsPaused ? BooruText.SearchStatus(view, paused: true) : null;
        string defaultTarget = Texts.T(opensNewTab ? "booru.tabs.searchNew" : "booru.tabs.searchCurrent");
        string alternateTarget = Texts.T(opensNewTab ? "booru.tabs.searchCurrent" : "booru.tabs.searchNew");
        DescribeSearchAction(_submit, SubmitCommand, defaultTarget, blocked);
        string alternateLabel = Texts.T(opensNewTab ? "booru.tabs.searchCurrentShort" : "booru.tabs.searchNewShort");
        _alternateSubmit.Content = alternateLabel;
        AutomationProperties.SetName(_alternateSubmit, alternateLabel);
        DescribeSearchAction(_alternateSubmit, AlternateSubmitCommand, alternateTarget,
            blocked is null ? alternateTarget : alternateTarget + "\n" + blocked);
    }

    private static void DescribeSearchAction(Button action, string command, string destination, string? disabledTip)
    {
        string? tip = disabledTip;
        if (action.IsEnabled)
        {
            string? shortcut = CommandCatalog.ShortcutTooltipDisplay(command, "booru");
            tip = shortcut is null ? destination : destination + " · " + shortcut;
        }
        ToolTip.SetTip(action, tip);
        ToolTip.SetShowOnDisabled(action, true);
        AutomationProperties.SetHelpText(action, tip ?? string.Empty);
    }

    internal static bool MatchesSearchShortcut(KeyEventArgs e, string command, EffectiveShortcutBindings bindings) =>
        bindings.Matches(e, command, "booru");

    internal bool TryShowQueryInNewTab(string query)
    {
        if (!OpenSearchTab(copyCurrent: false)) return false;
        ShowQuery(query);
        return true;
    }

    private bool OpenSearchTab(bool copyCurrent)
    {
        if (_closed || _searchTabs is not { } tabs) return false;
        int id;
        _selectNewTab = true;
        try { id = tabs.Add(copyCurrent); }
        finally { _selectNewTab = false; }
        if (id < 0)
        {
            _refusedTabLimit = tabs.Limit;
            RenderTabLimitNotice();
            return false;
        }
        ClearTabLimitNotice();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && tabs.ActiveId == id && IsSearchItem(_tabs.SelectedItem)) FocusSearchInput();
        }, DispatcherPriority.Loaded);
        return true;
    }

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

    private TabItem ActiveSearchItem => SearchTabs.ActiveId == BooruSearchTabs.PrimaryId ? _searchTab : _extraTabs[SearchTabs.ActiveId].Item;

    internal void ShowWatchers()
    {
        if (_closed || _tabItems is not { } items) return;
        if (!items.Contains(_watchersTab))
        {
            items.Insert(items.IndexOf(_plusTab), _watchersTab);
            SyncSearchTabs();
        }
        _tabs.SelectedItem = _watchersTab;
        _watchersTab.Focus();
    }

    internal void CloseWatchers()
    {
        if (_closed || _tabItems is not { } items) return;
        if (ReferenceEquals(_tabs.SelectedItem, _watchersTab)) _tabs.SelectedItem = ActiveSearchItem;
        items.Remove(_watchersTab);
        SyncSearchTabs();
        if (IsSearchItem(_tabs.SelectedItem)) FocusSearchInput();
    }

    private static bool IsSearchItem(object? item) => item is TabItem { Tag: int };

    private void OnTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabs || _closed || e.Source != _tabs) return;
        object? selected = _tabs.SelectedItem;
        // The "+" is a button, not a place: arriving on it (arrow keys) goes back where it came from.
        if (ReferenceEquals(selected, _plusTab))
        {
            _syncingTabs = true;
            try { _tabs.SelectedItem = _lastRealTab ?? _searchTab; }
            finally { _syncingTabs = false; }
            return;
        }
        if (selected is not TabItem item) return;
        ClearTabLimitNotice();
        _lastRealTab = item;
        UpdateGalleryScroll();
        if (item.Tag is not int searchId) return;
        AttachSearchContent(item);
        _searchTabs?.Select(searchId);
    }

    private void AttachSearchContent(TabItem owner)
    {
        if (_searchContent is null || ReferenceEquals(owner.Content, _searchContent)) return;
        if (ReferenceEquals(_searchTab.Content, _searchContent)) _searchTab.Content = null;
        foreach (var extra in _extraTabs.Values)
            if (ReferenceEquals(extra.Item.Content, _searchContent)) extra.Item.Content = null;
        owner.Content = _searchContent;
    }

    /// <summary>Brings the tab items in line with the tab model (a tab opened, closed or switched).</summary>
    private void SyncSearchTabs()
    {
        if (_closed || _searchTabs is not { } tabs || _tabItems is not { } items) return;
        if (_renderedSearchTab != tabs.ActiveId)
        {
            _renderedSearchTab = tabs.ActiveId;
            ReleaseGallery();
            _cursorPostId = null;
            _resultsKey = null;
        }
        _syncingTabs = true;
        try
        {
            HashSet<int> wanted = tabs.Ids.Where(id => id != BooruSearchTabs.PrimaryId).ToHashSet();
            bool selectionWasHere = IsSearchItem(_tabs.SelectedItem);
            foreach (int id in _extraTabs.Keys.Where(id => !wanted.Contains(id)).ToList())
            {
                TabItem gone = _extraTabs[id].Item;
                if (ReferenceEquals(gone.Content, _searchContent)) gone.Content = null;
                if (ReferenceEquals(_tabs.SelectedItem, gone)) selectionWasHere = true;
                items.Remove(gone);
                _extraTabs.Remove(id);
            }
            foreach (int id in tabs.Ids.Where(wanted.Contains))
            {
                if (_extraTabs.ContainsKey(id)) continue;
                TabItem item = new() { Tag = id };
                _extraTabs[id] = (item, MakeClosableHeader(item, () => CloseSearchTab(id)));
                items.Insert(items.IndexOf(_plusTab), item);
            }
            object? previous = _tabs.SelectedItem;
            // A plain array as the items source (like the two fixed tabs had): no collection listener, so no thread rule for the list itself.
            if (!items.SequenceEqual(_tabs.Items.OfType<TabItem>())) _tabs.ItemsSource = items.ToArray();
            TabItem active = ActiveSearchItem;
            AttachSearchContent(active);
            if (selectionWasHere || _selectNewTab)
            {
                _tabs.SelectedItem = active;
                _lastRealTab = active;
            }
            else if (previous is TabItem keep && items.Contains(keep)) _tabs.SelectedItem = keep;
        }
        finally { _syncingTabs = false; }
    }

    private TabHeader MakeClosableHeader(TabItem item, Action closeTab, string? closeName = null)
    {
        item.Classes.Add("booru-tab");
        TextBlock title = new() { MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Button close = new() { Name = closeName, Classes = { "ghost", "booru-tab-close" } };
        close.AttachedToVisualTree += (_, _) => { if (close.Content is not Control) close.Content = CommandBindings.NamedIcon("x", 12); };
        close.Click += (_, e) => { e.Handled = true; closeTab(); };
        StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 8, Children = { title, close } };
        item.Header = header;
        MiddleClickTracker middle = new();
        // A middle click on the tab closes it (press and release on the same tab); the close button keeps its own click.
        header.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!MiddleClickTracker.IsPress(e, header)) { middle.Cancel(); return; }
            middle.Pressed(ReferenceEquals(e.Source, close) ? null : header);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        header.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (_closed || !MiddleClickTracker.IsRelease(e) || !middle.Released(ReferenceEquals(e.Source, close) ? null : header)) return;
            e.Handled = true;
            closeTab();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return new TabHeader(title, close);
    }

    private void CloseSearchTab(int id)
    {
        if (_closed || _searchTabs is not { } tabs) return;
        if (!tabs.Close(id)) return;
        ClearTabLimitNotice();
        if (IsSearchItem(_tabs.SelectedItem)) FocusSearchInput();
    }

    private static void RenderTabHeader(TabItem item, TabHeader header, string title)
    {
        header.Title.Text = title;
        ToolTip.SetTip(header.Title, title);
        string closeName = Texts.T("booru.tabs.closeFor", title);
        ToolTip.SetTip(header.Close, closeName);
        AutomationProperties.SetName(header.Close, closeName);
        AutomationProperties.SetName(item, title);
    }

    // ---- render ----------------------------------------------------------------------------------------

    /// <summary>Tab titles, the buttons next to "Create watcher" and the bulk status; called with every render.</summary>
    private void RenderSearchTabs(BooruSearchView view)
    {
        if (_searchTabs is not { } tabs) return;
        RenderBottomStatus(view);
        UpdateGalleryScroll();
        RenderViewToggleLabels();
        foreach (var (id, tab) in _extraTabs)
            RenderTabHeader(tab.Item, tab.Header,
                BooruSearchTabs.Title(tabs.ViewOf(id), Texts.T("booru.tabs.blank"), id => Texts.T("booru.tabs.post", id)));
        if (_watcherHeader is { } header)
            RenderTabHeader(_watchersTab, header, Texts.T("booru.ui.tab.watchers", _model.Watchers.Count));
        string watchersName = Texts.T("booru.tabs.watchers");
        _openWatchers.Content = watchersName;
        AutomationProperties.SetName(_openWatchers, watchersName);
        ToolTip.SetTip(_openWatchers, watchersName);
        RenderTabLimitNotice();
        string newName = Texts.T("booru.tabs.new");
        IconButtons.Apply(_newTabButton, "plus", newName);
        _newTabButton.IsEnabled = true;

        string openName = Texts.T("booru.tabs.duplicate");
        _openInNewTab.Content = openName;
        _openInNewTab.IsEnabled = !view.IsBusy && (view.Parsed.IsValid || view.ExecutedQuery is not null || view.ExecutedPostId is not null);
        string openHint = Texts.T("booru.tabs.openCurrentHint");
        ToolTip.SetTip(_openInNewTab, openName + "\n" + openHint);
        ToolTip.SetShowOnDisabled(_openInNewTab, true);
        AutomationProperties.SetName(_openInNewTab, openName);
        AutomationProperties.SetHelpText(_openInNewTab, openHint);

        BooruSearchModel model = _model.Search;
        BooruBulkView bulk = model.Bulk;
        bool belongsToTab = bulk.TabId == tabs.ActiveId;
        bool running = bulk.IsRunning && belongsToTab;
        string bulkIcon = running ? "x" : "download";
        if (IconButtons.KeyOf(_downloadAll) != bulkIcon)
            CommandBindings.ShowIconBeforeText(_downloadAll, bulkIcon);
        string downloadName = Texts.T(running ? "booru.bulk.stop" : "booru.bulk.button");
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
        bool showBulk = belongsToTab && (running || bulk.Phase == BooruBulkPhase.Finished
            && string.Equals(bulk.Query, view.ExecutedQuery, StringComparison.Ordinal));
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
