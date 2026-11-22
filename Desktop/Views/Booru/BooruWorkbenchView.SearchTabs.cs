using System;
using System.Collections.Generic;
using System.Linq;
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

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Rudimentary browser-style search tabs and "Download all of this search". "Search" and "Watchers" stay first; the tabs of single
/// searches follow, then a "+" button. All search tabs show the one search content; the model swaps the state per tab
/// (<see cref="BooruSearchTabs"/>), so switching a tab never sends a request. The tabs live for the session only.
/// </summary>
internal sealed partial class BooruWorkbenchView
{
    internal static readonly string NewSearchTabName = "BooruNewSearchTab";
    internal static readonly string OpenInNewTabName = "BooruOpenInNewTab";
    internal static readonly string DownloadAllName = "BooruDownloadAll";
    internal static readonly string BulkStatusName = "BooruBulkStatus";

    private sealed record ExtraTab(int Id, TabItem Item, TextBlock Title, Button Close, MiddleClickTracker Middle);

    private readonly Button _openInNewTab = new() { Name = OpenInNewTabName };
    private readonly Button _downloadAll = new() { Name = DownloadAllName };
    private readonly Border _bulkHost = new() { Name = BulkStatusName, Classes = { "workspace-status" }, IsVisible = false };
    private readonly TextBlock _bulkText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _newTabButton = new() { Name = NewSearchTabName };
    private readonly TabItem _plusTab = new();
    private readonly Dictionary<int, ExtraTab> _extraTabs = new();
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

    /// <summary>The tab items in strip order: Search, Watchers, the search tabs, the "+" button.</summary>
    private TabItem[] CreateTabItems()
    {
        _searchTabs = new BooruSearchTabs(_model.Search, () => SearchTabsLimit?.Invoke() ?? MeowChan.Config.BooruSearchTabsDefault);
        _searchContent = _searchTab.Content as Control;
        _newTabButton.Classes.Add("subtle");
        _newTabButton.Padding = new Thickness(6, 0);
        _newTabButton.MinHeight = 0;
        _newTabButton.Click += (_, _) => OpenSearchTab(copyCurrent: false);
        _plusTab.Header = _newTabButton;
        _plusTab.IsTabStop = false;
        _plusTab.Focusable = false;
        _tabItems = new List<TabItem> { _searchTab, _watchersTab, _plusTab };
        _lastRealTab = _searchTab;
        _searchTabs.Changed += SyncSearchTabs;
        _tabs.SelectionChanged += OnTabsSelectionChanged;
        return _tabItems.ToArray();
    }

    /// <summary>Called once from the search tab's construction: the two buttons next to "Create watcher" and the bulk status.</summary>
    private void AddSearchTabActions(StackPanel searchActions)
    {
        CommandBindings.EnsureIconBeforeText(_downloadAll, "download");
        CommandBindings.EnsureIconBeforeText(_openInNewTab, "copy");
        _openInNewTab.Click += (_, _) => OpenSearchTab(copyCurrent: true);
        _downloadAll.Click += async (_, _) =>
        {
            if (_model.Search.Bulk.IsRunning) _model.Search.CancelBulk();
            else await _model.Search.DownloadAllAsync();
        };
        int at = searchActions.Children.IndexOf(_createWatcher);
        searchActions.Children.Insert(at + 1, _downloadAll);
        searchActions.Children.Insert(at + 2, _openInNewTab);
        _bulkHost.Child = _bulkText;
        AutomationProperties.SetLiveSetting(_bulkText, AutomationLiveSetting.Polite);
    }

    private Control BuildBulkStatus() => _bulkHost;

    // ---- result line and pager under the results, view switch with labels --------------------------------

    private readonly Border _bottomStatusHost = new() { Name = "BooruResultsLine", IsVisible = false };
    private readonly TextBlock _bottomStatusText = new() { TextWrapping = TextWrapping.Wrap };
    private bool _wideViewToggle;
    private string? _viewToggleKey;
    /// <summary>The page is wide enough for the words "List" and "Grid" beside the icons of the view switch (dip).</summary>
    private const double ViewToggleLabelsMinimumWidth = 720;

    /// <summary>The result line ("20 results on page 1 for ...") and the pager, in this order, under the result list.</summary>
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
        return new StackPanel { Spacing = 12, Children = { _bottomStatusHost, _pager } };
    }

    /// <summary>A successful or empty result is reported under the list; failures, pauses and the repair route stay above it.</summary>
    private void RenderBottomStatus(BooruSearchView view)
    {
        bool below = view.Status is BooruSearchStatus.Results or BooruSearchStatus.Empty && !_retry.IsVisible;
        _bottomStatusHost.IsVisible = below;
        _statusHost.IsVisible = !below;
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
            StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(CommandBindings.NamedIcon(icon));
            content.Children.Add(new TextBlock { Text = Texts.T(textKey), VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
        }
        _viewToggleKey = key;
    }

    // ---- tabs ------------------------------------------------------------------------------------------

    private void OpenSearchTab(bool copyCurrent)
    {
        if (_closed || _searchTabs is not { } tabs) return;
        _selectNewTab = true;
        int id = tabs.Add(copyCurrent);
        _selectNewTab = false;
        if (id < 0)
        {
            _model.Search.SetNotice(new BooruNotice(BooruNoticeKind.Warning, "booru.tabs.limit", new object?[] { tabs.Limit }));
            return;
        }
        Dispatcher.UIThread.Post(() => { if (!_closed) _input.Focus(); }, DispatcherPriority.Loaded);
    }

    private bool IsSearchItem(object? item) => ReferenceEquals(item, _searchTab) || (item is TabItem tab && _extraTabs.Values.Any(extra => ReferenceEquals(extra.Item, tab)));

    private int IdOf(object? item) => ReferenceEquals(item, _searchTab) ? BooruSearchTabs.PrimaryId
        : _extraTabs.Values.FirstOrDefault(extra => ReferenceEquals(extra.Item, item))?.Id ?? -1;

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
        _lastRealTab = item;
        if (!IsSearchItem(item)) return;
        AttachSearchContent(item);
        _searchTabs?.Select(IdOf(item));
    }

    private void AttachSearchContent(TabItem owner)
    {
        if (_searchContent is null || ReferenceEquals(owner.Content, _searchContent)) return;
        if (ReferenceEquals(_searchTab.Content, _searchContent)) _searchTab.Content = null;
        foreach (ExtraTab extra in _extraTabs.Values)
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
            foreach (ExtraTab gone in _extraTabs.Values.Where(extra => !wanted.Contains(extra.Id)).ToList())
            {
                if (ReferenceEquals(gone.Item.Content, _searchContent)) gone.Item.Content = null;
                if (ReferenceEquals(_tabs.SelectedItem, gone.Item)) selectionWasHere = true;
                items.Remove(gone.Item);
                _extraTabs.Remove(gone.Id);
            }
            foreach (int id in tabs.Ids.Where(wanted.Contains))
            {
                if (_extraTabs.ContainsKey(id)) continue;
                ExtraTab extra = CreateExtraTab(id);
                _extraTabs[id] = extra;
                items.Insert(items.IndexOf(_plusTab), extra.Item);
            }
            object? previous = _tabs.SelectedItem;
            // A plain array as the items source (like the two fixed tabs had): no collection listener, so no thread rule for the list itself.
            _tabs.ItemsSource = items.ToArray();
            TabItem active = tabs.ActiveId == BooruSearchTabs.PrimaryId ? _searchTab : _extraTabs[tabs.ActiveId].Item;
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

    private ExtraTab CreateExtraTab(int id)
    {
        TextBlock title = new() { MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Button close = new() { Classes = { "subtle" }, Padding = new Thickness(2), MinHeight = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        close.AttachedToVisualTree += (_, _) => { if (close.Content is not Control) close.Content = CommandBindings.NamedIcon("x", 12); };
        close.Click += (_, e) => { e.Handled = true; CloseSearchTab(id); };
        StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { title, close } };
        TabItem item = new() { Header = header };
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
            CloseSearchTab(id);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return new ExtraTab(id, item, title, close, middle);
    }

    private void CloseSearchTab(int id)
    {
        if (_closed || _searchTabs is not { } tabs) return;
        tabs.Close(id);
        FocusSearchInput();
    }

    // ---- render ----------------------------------------------------------------------------------------

    /// <summary>Tab titles, the buttons next to "Create watcher" and the bulk status; called with every render.</summary>
    private void RenderSearchTabs(BooruSearchView view)
    {
        if (_searchTabs is not { } tabs) return;
        RenderBottomStatus(view);
        RenderViewToggleLabels();
        foreach (ExtraTab extra in _extraTabs.Values)
        {
            string text = BooruSearchTabs.Title(tabs.ViewOf(extra.Id), Texts.T("booru.tabs.blank"), id => Texts.T("booru.tabs.post", id));
            extra.Title.Text = text;
            ToolTip.SetTip(extra.Title, text);
            string closeName = Texts.T("booru.tabs.closeFor", text);
            ToolTip.SetTip(extra.Close, closeName);
            AutomationProperties.SetName(extra.Close, closeName);
            AutomationProperties.SetName(extra.Item, text);
        }
        string newName = Texts.T("booru.tabs.new");
        ToolTip.SetTip(_newTabButton, newName);
        AutomationProperties.SetName(_newTabButton, newName);
        if (_newTabButton.Content is null) _newTabButton.Content = "+";
        _newTabButton.IsEnabled = true;

        _openInNewTab.Content = Texts.T("booru.tabs.openCurrent");
        string openHint = Texts.T("booru.tabs.openCurrentHint");
        ToolTip.SetTip(_openInNewTab, openHint);
        AutomationProperties.SetName(_openInNewTab, _openInNewTab.Content.ToString());
        AutomationProperties.SetHelpText(_openInNewTab, openHint);

        BooruSearchModel model = _model.Search;
        BooruBulkView bulk = model.Bulk;
        bool belongsToTab = bulk.TabId == tabs.ActiveId;
        bool running = bulk.IsRunning && belongsToTab;
        _downloadAll.Content = Texts.T(running ? "booru.bulk.stop" : "booru.bulk.button");
        _downloadAll.IsEnabled = running || model.CanDownloadAll;
        string bulkHint = running ? string.Empty
            : bulk.IsRunning ? Texts.T("booru.bulk.otherTab", bulk.Query)
            : model.CanDownloadAll ? Texts.T("booru.bulk.hint", BooruSearchModel.MaximumPage, BooruSearchModel.PageSize)
            : Texts.T("booru.bulk.needsSearch");
        ToolTip.SetTip(_downloadAll, bulkHint.Length == 0 ? null : bulkHint);
        ToolTip.SetShowOnDisabled(_downloadAll, true);
        AutomationProperties.SetName(_downloadAll, _downloadAll.Content.ToString());
        AutomationProperties.SetHelpText(_downloadAll, bulkHint);

        // The counts belong to one search: they are shown only beside the results of that same search.
        bool showBulk = belongsToTab && (running || bulk.Phase == BooruBulkPhase.Finished
            && string.Equals(bulk.Query, view.ExecutedQuery, StringComparison.Ordinal));
        _bulkHost.IsVisible = showBulk;
        if (!showBulk) return;
        if (running)
        {
            _bulkText.Text = Texts.T("booru.bulk.running", bulk.Query, bulk.PagesRead, BooruSearchModel.MaximumPage, bulk.Found, bulk.Queued);
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
        _bulkText.Text = Texts.T("booru.bulk.result", bulk.Query, bulk.Found, bulk.PagesRead, BooruSearchModel.MaximumPage, bulk.Queued,
            bulk.AlreadyQueued, bulk.AlreadySaved, bulk.NoOriginal, bulk.Failed) + " " + end;
        ApplyTone(_bulkHost, bulk.End is BooruBulkEnd.Blocked or BooruBulkEnd.Canceled || bulk.Failed > 0 ? BooruNoticeKind.Warning : BooruNoticeKind.Info);
    }
}
