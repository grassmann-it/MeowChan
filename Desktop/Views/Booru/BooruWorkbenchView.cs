using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using Serilog;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Reusable Danbooru workbench content. All provider actions still go through the existing models.
/// The host supplies layout/chrome and calls ActivateAsync when first shown; detaching only suspends
/// the clock, not the model or its in-flight work. The host must Dispose when permanently done.
/// </summary>
internal sealed class BooruWorkbenchView : UserControl, IDisposable
{
    internal const string TabsName = "BooruTabs";
    internal const string ModeName = "BooruMode";
    internal const string InputName = "BooruInput";
    internal const string SubmitName = "BooruSubmit";
    internal const string CancelName = "BooruCancel";
    internal const string RetryName = "BooruRetry";
    internal const string QueryName = "BooruQuery";
    internal const string StatusName = "BooruStatus";
    internal const string PreviousName = "BooruPreviousPage";
    internal const string NextName = "BooruNextPage";
    internal const string CreateWatcherName = "BooruCreateWatcher";
    internal const string ResultsName = "BooruResults";
    internal const string WatchersName = "BooruWatchers";
    internal const string RemoveNoteName = "BooruRemoveNote";
    internal const string NoFilterNoteName = "BooruNoFilterNote";
    internal const string LoadPrefix = "BooruLoad:";
    internal const string CheckPrefix = "BooruCheck:";
    internal const string PausePrefix = "BooruPause:";
    internal const string ResumePrefix = "BooruResume:";
    internal const string RemovePrefix = "BooruRemove:";
    internal const string RestartPrefix = "BooruRestart:";
    internal const string RemoveConfirmPrefix = "BooruRemoveConfirm:";
    internal const string RemoveCancelPrefix = "BooruRemoveCancel:";
    internal const string OpenPostPrefix = "BooruOpenPost:";
    internal const string CopyLinkPrefix = "BooruCopyLink:";
    internal const string WatcherOpenPrefix = "BooruWatcherOpen:";
    internal const string WatcherCopyPrefix = "BooruWatcherCopy:";
    internal const string LinkNoteName = "BooruLinkNote";
    internal const string InputIssueName = "BooruInputIssue";
    internal const string NoticeName = "BooruNotice";
    internal const string WatcherNoticeName = "BooruWatcherNotice";
    internal const string ResetCooldownName = "BooruResetProviderCooldown";
    internal const string ResultTagPrefix = "BooruResultTag:";
    internal const string DetailsPrefix = "BooruResultDetails:";
    internal const string MoreTagsPrefix = "BooruResultMoreTags:";

    private const int PreviewSize = 112;
    private const int MaximumTagsShown = 24;
    private const int MaximumHiddenTagsNamed = 60;
    private const int MaximumPreviewBytes = 8 * 1024 * 1024;

    private sealed record CachedBitmap(byte[] Bytes, Bitmap Bitmap);

    private readonly BooruWorkbenchModel _model;
    private readonly bool _ownsModel;
    private readonly Dictionary<string, CachedBitmap> _bitmaps = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly TextBlock _title = new() { Classes = { "page-title" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _intro = new() { Classes = { "workspace-help" } };
    private readonly TextBlock _provider = new() { Classes = { "workspace-meta" } };
    private readonly TabControl _tabs = new() { Name = TabsName };
    private readonly TabItem _searchTab = new();
    private readonly TabItem _watchersTab = new();
    private readonly ComboBox _mode = new() { Name = ModeName, MinWidth = 170 };
    // Escape empties the typed query first (SearchBox); in an empty field it closes the window as before. No debounce: a search goes out only on "Search".
    private readonly SearchBox _input = new() { Name = InputName, AcceptsReturn = false };
    private readonly Button _submit = new() { Name = SubmitName };
    private readonly Button _cancel = new() { Name = CancelName };
    private readonly SelectableTextBlock _queryLine = new() { Name = QueryName, Classes = { "workspace-meta", "mono" } };
    private readonly TextBlock _queryHint = new() { Classes = { "workspace-help" } };
    // Why the field is not accepted, shown under it: the red border alone is colour only.
    private readonly TextBlock _inputIssue = new() { Name = InputIssueName, Classes = { "workspace-error" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Border _statusHost = new() { Name = StatusName };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new() { Name = RetryName };
    private readonly WrapPanel _pager = new() { Classes = { "workspace-toolbar" } };
    private readonly Button _previous = new() { Name = PreviousName };
    private readonly Button _next = new() { Name = NextName };
    private readonly TextBlock _pageInfo = new() { Classes = { "workspace-meta", "tnum" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pageLimit = new() { Classes = { "workspace-help" } };
    private readonly Button _createWatcher = new() { Name = CreateWatcherName, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _createHint = new() { Classes = { "workspace-help" } };
    private readonly Border _noticeHost = new();
    private readonly TextBlock _noticeText = new() { Name = NoticeName, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _results = new() { Name = ResultsName, Spacing = 8 };
    private readonly TextBlock _watchersIntro = new() { Classes = { "workspace-help" } };
    private readonly Border _removeNoteHost = new() { Name = RemoveNoteName, Classes = { "workspace-status" } };
    private readonly TextBlock _removeNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _watcherNoticeHost = new();
    private readonly TextBlock _watcherNoticeText = new() { Name = WatcherNoticeName, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _watchersState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _watchersReload = new();
    private readonly Button _resetCooldown = new() { Name = ResetCooldownName };
    private readonly StackPanel _watchers = new() { Name = WatchersName, Spacing = 8 };
    private readonly TextBlock _footerNote = new() { Name = NoFilterNoteName, Classes = { "workspace-help" } };
    private readonly TextBlock _linkNote = new() { Name = LinkNoteName, Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    private (string Key, string Argument, bool Problem)? _linkNoteState;
    private string? _resultsKey;
    // The cards are rebuilt on every change; their newest state is repeated in the persistent notice lines (live regions).
    private readonly BooruLiveMirror<(BooruRowLoad, string?)?> _loadMirror = new();
    private readonly BooruLiveMirror<BooruNotice> _watcherMirror = new(ReferenceEqualityComparer.Instance);
    private string? _confirmingId;
    // A focused control that a running command disabled; focus returns to it once it is enabled again.
    private (Panel Panel, string Name, int Card)? _pendingFocus;
    private string? _watchersKey;
    private bool _paused;
    private string _cooling = string.Empty;
    private bool _applying;
    private bool _renderQueued;
    private bool _closed;

    private bool _attached;
    private bool _activated;
    private Task? _initialization;

    internal BooruWorkbenchView(BooruWorkbenchServices services) : this(new BooruWorkbenchModel(services), ownsModel: true) { }

    /// <param name="buildLayout">
    /// Optional host frame factory, called once with the still-unparented Header, Body and Footer.
    /// The returned control becomes this UserControl's Content, so the view stays in the visual tree
    /// (focus, clipboard and attach/detach work in either host). The footer contains notes only.
    /// </param>
    internal BooruWorkbenchView(BooruWorkbenchModel model, bool ownsModel,
        Func<Control, Control, Control, Control>? buildLayout = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _ownsModel = ownsModel;
        Name = "BooruWorkbenchView";
        Header = BuildHeader();
        Body = BuildBody();
        Footer = BuildFooter();
        Content = buildLayout is null ? BuildLayout(Header, Body, Footer) : buildLayout(Header, Body, Footer);

        _model.Search.Changed += OnModelChanged;
        _model.Watchers.Changed += OnModelChanged;
        Texts.Changed += OnTextsChanged;
        _clock.Tick += OnClockTick;
        Render(force: true);
    }

    internal Control Header { get; }
    internal Control Body { get; }
    internal Control Footer { get; }
    internal string TitleText => _title.Text ?? string.Empty;
    internal event Action? ChromeChanged;
    internal bool IsModeDropDownOpen => _mode.IsDropDownOpen;

    // An unframed embedded host gets the same content, without dialog/window chrome or a Close action.
    private static Control BuildLayout(Control header, Control body, Control footer)
    {
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(header);
        ScrollViewer scroll = new() { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        return layout;
    }

    /// <summary>
    /// First-show hook (UI thread). Reads the stored watcher list once, focuses the query, and enables
    /// the render-only clock while attached. Repeated activation/reparenting never starts a provider request.
    /// </summary>
    internal Task ActivateAsync()
    {
        if (_closed) return Task.CompletedTask;
        _activated = true;
        if (_attached) _clock.Start();
        _input.Focus();
        return _initialization ??= _model.InitializeAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (_closed) return;
        // Keep subscriptions through transient detaches: late results remain in the model and are
        // rendered here too, even if their queued notification ran while no host was attached.
        Render(force: true);
        if (_activated) _clock.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _clock.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    internal BooruWorkbenchModel Model => _model;

    /// <summary>
    /// Puts a query into the search field of the Search tab and brings that tab forward. The field itself is set (not only the
    /// model), because a field that has not been laid out yet would otherwise report its initial empty text back and erase the
    /// query. Nothing is requested.
    /// </summary>
    internal void ShowQuery(string query)
    {
        _tabs.SelectedItem = _searchTab;
        _model.Search.SetMode(BooruInputMode.Tags);
        _input.Text = query;
        _model.Search.SetInput(query);
        _input.Focus();
        _input.CaretIndex = query.Length;
    }

    // ---- construction ------------------------------------------------------------------------------

    private Control BuildHeader()
    {
        StackPanel header = new() { Spacing = 4 };
        header.Children.Add(_title);
        header.Children.Add(_provider);
        header.Children.Add(_intro);
        return header;
    }

    private Control BuildBody()
    {
        _searchTab.Content = BuildSearchTab();
        _watchersTab.Content = BuildWatchersTab();
        _tabs.ItemsSource = new[] { _searchTab, _watchersTab };
        return _tabs;
    }

    private Control BuildFooter()
    {
        AutomationProperties.SetLiveSetting(_linkNote, AutomationLiveSetting.Polite);
        StackPanel notes = new() { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        notes.Children.Add(_footerNote);
        notes.Children.Add(_linkNote);
        return notes;
    }

    private Control BuildSearchTab()
    {
        _mode.ItemsSource = new[] { new ComboBoxItem { Tag = BooruInputMode.Tags }, new ComboBoxItem { Tag = BooruInputMode.Post } };
        _mode.SelectionChanged += (_, _) =>
        {
            if (_applying || _mode.SelectedItem is not ComboBoxItem { Tag: BooruInputMode mode }) return;
            _model.Search.SetMode(mode);
        };
        _input.TextChanged += (_, _) =>
        {
            if (!_applying) _model.Search.SetInput(_input.Text);
        };
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.Handled || ShortcutServiceCapturing()) return;
            e.Handled = true;
            _model.Search.SetInput(_input.Text); // the field is what is sent, even if its change event is still queued
            await _model.Search.SubmitAsync();
        };
        _submit.Classes.Add("primary");
        _submit.Click += async (_, _) =>
        {
            _model.Search.SetInput(_input.Text);
            await _model.Search.SubmitAsync();
        };
        _cancel.Click += (_, _) => _model.Search.Cancel();
        _retry.Click += async (_, _) => await _model.Search.RetryAsync();
        _previous.Click += async (_, _) => await _model.Search.GoToPageAsync(_model.Search.View.Page - 1);
        _next.Click += async (_, _) => await _model.Search.GoToPageAsync(_model.Search.View.Page + 1);
        _createWatcher.Click += async (_, _) => await _model.CreateWatcherAsync();

        Grid inputRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        inputRow.Children.Add(_mode);
        Grid.SetColumn(_input, 1); inputRow.Children.Add(_input);
        Grid.SetColumn(_submit, 2); inputRow.Children.Add(_submit);
        Grid.SetColumn(_cancel, 3); inputRow.Children.Add(_cancel);

        _queryLine.TextWrapping = TextWrapping.Wrap;
        StackPanel status = new() { Spacing = 8 };
        status.Children.Add(_statusText);
        status.Children.Add(_retry);
        _statusHost.Child = status;
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);

        _pager.Children.Add(_previous);
        _pager.Children.Add(_pageInfo);
        _pager.Children.Add(_next);

        StackPanel watcherRow = new() { Spacing = 4 };
        watcherRow.Children.Add(_createWatcher);
        watcherRow.Children.Add(_createHint);

        _noticeHost.Child = _noticeText;
        AutomationProperties.SetLiveSetting(_noticeText, AutomationLiveSetting.Polite);

        StackPanel tab = new() { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        tab.Children.Add(inputRow);
        AutomationProperties.SetLiveSetting(_inputIssue, AutomationLiveSetting.Polite);
        tab.Children.Add(_inputIssue);
        tab.Children.Add(_queryLine);
        tab.Children.Add(_queryHint);
        tab.Children.Add(watcherRow);
        tab.Children.Add(_noticeHost);
        tab.Children.Add(_statusHost);
        tab.Children.Add(_pager);
        tab.Children.Add(_results);
        tab.Children.Add(_pageLimit);
        return tab;
    }

    internal const string CatalogStorageName = "BooruCatalogStorage";
    // Shown from 75 % of the configured catalog limit on (S2); the text names the setting that raises it.
    private readonly Border _storageHost = new() { Name = CatalogStorageName, IsVisible = false };
    private readonly TextBlock _storageText = new() { TextWrapping = TextWrapping.Wrap };

    private Control BuildWatchersTab()
    {
        _storageHost.Child = _storageText;
        AutomationProperties.SetLiveSetting(_storageText, AutomationLiveSetting.Polite);
        _removeNoteHost.Child = _removeNote;
        _watcherNoticeHost.Child = _watcherNoticeText;
        AutomationProperties.SetLiveSetting(_watcherNoticeText, AutomationLiveSetting.Polite);
        _watchersReload.Click += async (_, _) => await _model.Watchers.RefreshAsync();
        _resetCooldown.Click += async (_, _) => await _model.Watchers.ResetProviderCooldownAsync();
        StackPanel tab = new() { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        tab.Children.Add(_watchersIntro);
        tab.Children.Add(_removeNoteHost);
        tab.Children.Add(_watcherNoticeHost);
        tab.Children.Add(_storageHost);
        tab.Children.Add(_resetCooldown);
        tab.Children.Add(_watchersState);
        tab.Children.Add(_watchersReload);
        tab.Children.Add(_watchers);
        return tab;
    }

    private static bool ShortcutServiceCapturing() => MeowChan.Desktop.Commands.ShortcutService.IsCapturing;

    // ---- events ---------------------------------------------------------------------------------------

    private void OnModelChanged()
    {
        if (_closed) return;
        lock (this)
        {
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            lock (this) _renderQueued = false;
            if (!_closed) Render(force: false);
        });
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed) Render(force: true);
    });

    /// <summary>A pause or cooldown that ran out only re-renders the controls; it never sends anything.</summary>
    private void OnClockTick(object? sender, EventArgs e)
    {
        if (_closed) return;
        BooruWatcherListModel watchers = _model.Watchers;
        bool paused = _model.Search.IsPaused;
        string cooling = string.Join('|', watchers.View.Rows.Where(row => watchers.ActiveCooldown(row.Watcher) is not null).Select(row => row.WatcherId));
        if (paused != _paused || cooling != _cooling) Render(force: true);
    }

    /// <summary>Clears only the draft field; never submits, retries or cancels a running search.</summary>
    internal bool TryClearQuery()
    {
        if (_closed || !_input.HasText) return false;
        _input.ClearAndFocus();
        _model.Search.SetInput(_input.Text);
        return true;
    }

    /// <summary>The safe Escape answer while the watcher tab has an open removal question.</summary>
    internal bool TryCancelRemoval()
    {
        if (_closed || !ReferenceEquals(_tabs.SelectedItem, _watchersTab)
            || _model.Watchers.View.Rows.FirstOrDefault(row => row.ConfirmingRemoval) is not { } asking) return false;
        _model.Watchers.CancelRemoval(asking.WatcherId);
        return true;
    }

    // ---- render ---------------------------------------------------------------------------------------

    private void Render(bool force)
    {
        if (_closed) return;
        _applying = true;
        try
        {
            BooruSearchView search = _model.Search.View;
            BooruWatcherListView watchers = _model.Watchers.View;
            RenderChrome(watchers);
            RenderSearch(search, force);
            RenderWatchers(watchers, force);
        }
        finally { _applying = false; }
    }

    private void RenderChrome(BooruWatcherListView watchers)
    {
        _title.Text = Texts.T("booru.ui.title");
        _provider.Text = Texts.T("booru.ui.provider", _model.Search.View.ProviderName);
        _intro.Text = Texts.T("booru.ui.intro");
        _searchTab.Header = Texts.T("booru.ui.tab.search");
        _watchersTab.Header = Texts.T("booru.ui.tab.watchers", _model.Watchers.Count);
        AutomationProperties.SetName(_watchersTab, Texts.T("booru.ui.tab.watchers", _model.Watchers.Count));
        _footerNote.Text = Texts.T("booru.ui.noFilterNote");
        _linkNote.IsVisible = _linkNoteState is not null;
        _linkNote.Text = _linkNoteState is { } note ? Texts.T(note.Key, note.Argument) : string.Empty;
        _linkNote.Classes.Set("workspace-error", _linkNoteState is { Problem: true });
        ChromeChanged?.Invoke();
    }

    private void RenderSearch(BooruSearchView view, bool force)
    {
        BooruSearchModel model = _model.Search;
        bool paused = model.IsPaused;
        _paused = paused;

        // Mode selector items follow the language; the selection follows the model.
        if (_mode.ItemsSource is IEnumerable<ComboBoxItem> items)
        {
            foreach (ComboBoxItem item in items)
                item.Content = Texts.T(item.Tag is BooruInputMode.Post ? "booru.ui.mode.post" : "booru.ui.mode.tags");
            ComboBoxItem selected = items.First(item => (BooruInputMode)item.Tag! == view.Mode);
            if (!ReferenceEquals(_mode.SelectedItem, selected)) _mode.SelectedItem = selected;
        }
        AutomationProperties.SetName(_mode, Texts.T("booru.ui.mode.label"));
        if (_input.Text != view.Input) _input.Text = view.Input;
        _input.PlaceholderText = Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.input.placeholder.post" : "booru.ui.input.placeholder.tags");
        AutomationProperties.SetName(_input, Texts.T("booru.ui.input.label"));
        bool invalid = view.Parsed.Kind == BooruInputKind.Invalid;
        _input.Classes.Set("workspace-invalid", invalid);
        string issue = invalid ? BooruText.Issue(view.Parsed.Issue) : string.Empty;
        AutomationProperties.SetHelpText(_input, issue);
        _inputIssue.Text = issue;
        _inputIssue.IsVisible = invalid;

        _submit.Content = Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.lookup" : "booru.ui.search");
        _submit.IsEnabled = model.CanSubmit;
        // A disabled Search names its reason (the input issue or the provider pause) to the mouse and to assistive technology.
        string? submitWhy = model.CanSubmit ? null : invalid ? issue : paused ? BooruText.SearchStatus(view, paused) : null;
        ToolTip.SetTip(_submit, submitWhy);
        ToolTip.SetShowOnDisabled(_submit, true);
        AutomationProperties.SetHelpText(_submit, submitWhy ?? string.Empty);
        _cancel.Content = Texts.T("booru.ui.cancel");
        _cancel.IsVisible = view.IsBusy;

        // The exact query is always visible: what is sent is what the field canonicalizes to, nothing added.
        _queryLine.IsVisible = view.Parsed.Kind is BooruInputKind.Tags or BooruInputKind.Post;
        _queryLine.Text = view.Parsed.Kind switch
        {
            BooruInputKind.Tags => Texts.T("booru.ui.query.sent", view.Parsed.Canonical),
            BooruInputKind.Post => Texts.T("booru.ui.post.sent", view.Parsed.PostId),
            _ => string.Empty
        };
        _queryHint.IsVisible = view.Mode == BooruInputMode.Tags;
        _queryHint.Text = Texts.T("booru.ui.query.hint");

        _createWatcher.Content = Texts.T("booru.ui.watcher.create");
        _createWatcher.IsEnabled = model.CanCreateWatcher && !_model.Watchers.View.Creating;
        _createHint.Text = Texts.T("booru.ui.watcher.createHint");
        string createName = Texts.T("booru.ui.watcher.create");
        AutomationProperties.SetName(_createWatcher, createName);
        AutomationProperties.SetHelpText(_createWatcher, _createWatcher.IsEnabled ? _createHint.Text : Texts.T("booru.ui.watcher.createNeedsTags"));
        ToolTip.SetTip(_createWatcher, _createWatcher.IsEnabled ? _createHint.Text : Texts.T("booru.ui.watcher.createNeedsTags"));

        _loadMirror.Update(view.Rows.Select(row => (row.Post.PostId,
            row.Load == BooruRowLoad.Idle ? default((BooruRowLoad, string?)?) : (row.Load, row.LoadCode))), view.Notice);
        if (_loadMirror.RowId is { } mirrored && view.Rows.FirstOrDefault(row => row.Post.PostId == mirrored) is { } loadRow
            && BooruText.Load(loadRow) is { } loadText)
        {
            _noticeText.Text = Texts.T("booru.ui.post.title", loadRow.Post.PostId) + ": " + loadText;
            ApplyTone(_noticeHost, LoadTone(loadRow.Load));
            _noticeHost.IsVisible = true;
        }
        else
        {
            if (view.Notice is { } notice)
            {
                _noticeText.Text = BooruText.Notice(notice);
                ApplyTone(_noticeHost, notice.Kind);
            }
            _noticeHost.IsVisible = view.Notice is not null;
        }

        // Errors interrupt (as the dialog error strips do); progress and limits wait their turn.
        BooruNoticeKind tone = ToneOf(view.Status);
        AutomationProperties.SetLiveSetting(_statusText, tone == BooruNoticeKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        _statusText.Text = BooruText.SearchStatus(view, paused);
        _statusHost.IsVisible = true;
        ApplyTone(_statusHost, tone);
        _retry.Content = Texts.T("booru.ui.status.retry");
        _retry.IsVisible = view.Status is BooruSearchStatus.Offline or BooruSearchStatus.Failed;
        _retry.IsEnabled = !paused;

        bool paging = view.Status is BooruSearchStatus.Results or BooruSearchStatus.Empty && view.ExecutedQuery is not null && !view.IsPostResult;
        _pager.IsVisible = paging;
        _previous.Content = Texts.T("booru.ui.page.previous");
        _previous.IsEnabled = view.HasPreviousPage && !paused;
        _next.Content = Texts.T("booru.ui.page.next");
        _next.IsEnabled = view.HasNextPage && !paused;
        _pageInfo.Text = Texts.T("booru.ui.page.info", view.Page, BooruSearchModel.MaximumPage, BooruSearchModel.PageSize);
        _pageLimit.IsVisible = view.PageLimitReached;
        _pageLimit.Text = Texts.T("booru.ui.page.limit", BooruSearchModel.MaximumPage, BooruSearchModel.PageSize);

        string key = string.Join('\n', Texts.Language, view.Status, view.Page, view.ExecutedPostId, string.Join('|',
            view.Rows.Select(row => $"{row.Post.PostId}:{row.Load}:{row.LoadCode}:{row.Preview}:{row.PreviewBytes?.Length}")));
        if (force || key != _resultsKey)
        {
            _resultsKey = key;
            RebuildPanel(_results, () => view.Rows.Select(CreateResultCard).ToList(), fallback: () => null);
            PruneBitmaps(view.Rows);
        }
    }

    private void RenderWatchers(BooruWatcherListView view, bool force)
    {
        BooruWatcherListModel model = _model.Watchers;
        _watchersIntro.Text = Texts.T("booru.ui.watchers.intro");
        _removeNote.Text = Texts.T("booru.ui.watchers.removeNote");
        _watcherMirror.Update(view.Rows.Select(row => (row.WatcherId, row.Message)), view.Notice);
        if (_watcherMirror.RowId is { } mirrored && view.Rows.FirstOrDefault(row => row.WatcherId == mirrored) is { } messageRow)
        {
            // While the row's next command runs it has no message; the line keeps the previous one instead of flashing.
            if (messageRow.Message is { } message)
            {
                _watcherNoticeText.Text = messageRow.Watcher.Query + ": " + BooruText.Notice(message);
                ApplyTone(_watcherNoticeHost, message.Kind);
            }
            _watcherNoticeHost.IsVisible = true;
        }
        else
        {
            if (view.Notice is { } notice)
            {
                _watcherNoticeText.Text = BooruText.Notice(notice);
                ApplyTone(_watcherNoticeHost, notice.Kind);
            }
            _watcherNoticeHost.IsVisible = view.Notice is not null;
        }
        if (view.Storage is { NearLimit: true } storage)
        {
            _storageText.Text = Texts.T("booru.ui.watchers.catalogSize", Size(storage.DocumentBytes), Size(storage.LimitBytes),
                storage.Percent, storage.PostCount, Texts.T("settings.storageLimits"), Texts.T("budgets.booruCatalog"));
            ApplyTone(_storageHost, BooruNoticeKind.Warning);
        }
        _storageHost.IsVisible = view.Storage is { NearLimit: true };
        _watchersState.IsVisible = view.Status != BooruWatcherListStatus.Ready;
        _watchersState.Text = view.Status switch
        {
            BooruWatcherListStatus.Loading => Texts.T("booru.ui.watchers.loading"),
            BooruWatcherListStatus.Empty => Texts.T("booru.ui.watchers.empty"),
            BooruWatcherListStatus.Failed => Texts.T("booru.ui.watchers.loadFailed", BooruText.CodeText(view.Code)),
            _ => string.Empty
        };
        _watchersState.Classes.Set("workspace-error", view.Status == BooruWatcherListStatus.Failed);
        _resetCooldown.Content = Texts.T("booru.ui.watchers.resetCooldown");
        _resetCooldown.IsEnabled = !view.Resetting && (view.Status is BooruWatcherListStatus.Ready or BooruWatcherListStatus.Empty);
        AutomationProperties.SetName(_resetCooldown, Texts.T("booru.ui.watchers.resetCooldown"));
        AutomationProperties.SetHelpText(_resetCooldown, Texts.T("booru.ui.watchers.resetCooldownHint"));
        ToolTip.SetTip(_resetCooldown, Texts.T("booru.ui.watchers.resetCooldownHint"));
        _watchersReload.Content = Texts.T("booru.ui.watchers.retry");
        _watchersReload.IsVisible = view.Status == BooruWatcherListStatus.Failed;

        _cooling = string.Join('|', view.Rows.Where(row => model.ActiveCooldown(row.Watcher) is not null).Select(row => row.WatcherId));
        string key = string.Join('\n', Texts.Language, _cooling, string.Join('|', view.Rows.Select(row => string.Join(',',
            row.WatcherId, row.Watcher, row.Busy, row.ConfirmingRemoval, row.Message?.Key, row.Message is { } message
                ? string.Join(',', message.Args.Select(arg => arg?.ToString())) : string.Empty))));
        // Opening the removal question moves focus to its safe answer; closing it returns focus to "Remove".
        string? confirming = view.Rows.FirstOrDefault(row => row.ConfirmingRemoval)?.WatcherId;
        string? moveTo = null;
        if (!string.Equals(confirming, _confirmingId, StringComparison.Ordinal))
        {
            moveTo = confirming is not null ? RemoveCancelPrefix + confirming : RemovePrefix + _confirmingId;
            _confirmingId = confirming;
        }
        if (force || key != _watchersKey)
        {
            _watchersKey = key;
            RebuildPanel(_watchers, () => view.Rows.Select(CreateWatcherCard).ToList(), moveTo, () => _watchersTab, waitWhileDisabled: true);
        }
    }

    /// <summary>
    /// Replaces the children while keeping keyboard focus inside the panel when it was there: on <paramref name="moveTo"/>
    /// when given, else on the same named control (or its pause/resume counterpart) when it survives enabled. A surviving but
    /// disabled one is waited for when <paramref name="waitWhileDisabled"/> (a running command), otherwise, and when the
    /// control is gone, focus goes to the first enabled button of the same card or its nearest neighbour, then to
    /// <paramref name="fallback"/>. Without a fallback a vanished control leaves focus where the platform puts it.
    /// </summary>
    private void RebuildPanel(Panel panel, Func<List<Control>> build, string? moveTo = null, Func<Control?>? fallback = null,
        bool waitWhileDisabled = false)
    {
        Visual? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        int card = -1;
        if (focused is not null)
            for (int index = 0; index < panel.Children.Count; index++)
                if (ReferenceEquals(panel.Children[index], focused) || panel.Children[index].IsVisualAncestorOf(focused)) { card = index; break; }
        string? focusedName = card >= 0 && focused is Control { Name: { } name } ? name : null;
        if (_pendingFocus is { } pending && ReferenceEquals(pending.Panel, panel))
        {
            _pendingFocus = null;
            // Focus that fell out of the window when its button was disabled comes back; focus the user moved elsewhere stays.
            if (card < 0 && (focused is null || ReferenceEquals(focused, TopLevel.GetTopLevel(this)))) { card = pending.Card; focusedName = pending.Name; }
        }
        panel.Children.Clear();
        foreach (Control child in build()) panel.Children.Add(child);
        if (card < 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            Control? target = Enabled(panel, moveTo) ?? Enabled(panel, focusedName) ?? Enabled(panel, Counterpart(focusedName));
            if (target is null && waitWhileDisabled && focusedName is not null
                && panel.GetVisualDescendants().OfType<Control>().Any(control => control.Name == focusedName))
            {
                _pendingFocus = (panel, focusedName, card);
                return;
            }
            if (target is null && fallback is not null)
            {
                if (panel.Children.Count > 0)
                    target = panel.Children[Math.Min(card, panel.Children.Count - 1)].GetVisualDescendants().OfType<Button>()
                        .FirstOrDefault(button => button.IsEffectivelyEnabled && button.IsEffectivelyVisible && button.Focusable);
                target ??= fallback();
            }
            target?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private static Control? Enabled(Panel panel, string? name) => name is null ? null
        : panel.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyEnabled);

    private static string? Counterpart(string? name) =>
        name is null ? null
        : name.StartsWith(PausePrefix, StringComparison.Ordinal) ? ResumePrefix + name[PausePrefix.Length..]
        : name.StartsWith(ResumePrefix, StringComparison.Ordinal) ? PausePrefix + name[ResumePrefix.Length..]
        : null;

    private static BooruNoticeKind LoadTone(BooruRowLoad load) => load switch
    {
        BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued or BooruRowLoad.AlreadyLoaded => BooruNoticeKind.Success,
        BooruRowLoad.Handing => BooruNoticeKind.Info,
        BooruRowLoad.Failed => BooruNoticeKind.Error,
        _ => BooruNoticeKind.Warning
    };

    private static BooruNoticeKind ToneOf(BooruSearchStatus status) => status switch
    {
        BooruSearchStatus.Denied or BooruSearchStatus.Failed or BooruSearchStatus.Unavailable => BooruNoticeKind.Error,
        BooruSearchStatus.RateLimited or BooruSearchStatus.Cooldown or BooruSearchStatus.Offline
            or BooruSearchStatus.NotFound or BooruSearchStatus.InvalidInput => BooruNoticeKind.Warning,
        _ => BooruNoticeKind.Info
    };

    private static void ApplyTone(Border host, BooruNoticeKind kind)
    {
        host.Classes.Set("workspace-status", kind is BooruNoticeKind.Info or BooruNoticeKind.Success);
        host.Classes.Set("workspace-status-success", kind == BooruNoticeKind.Success);
        host.Classes.Set("workspace-warning", kind == BooruNoticeKind.Warning);
        host.Classes.Set("workspace-error", kind == BooruNoticeKind.Error);
    }

    // ---- result cards ----------------------------------------------------------------------------------

    private Control CreateResultCard(BooruResultRowView row)
    {
        BooruPostView post = row.Post;
        // The title line names the card; the border itself carries no name, so "Post #n" is read once.
        Border card = new() { Name = "BooruResult:" + post.PostId };
        card.Classes.Add("workspace-card");

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(WithPreviewCorners(CreatePreview(row), post));

        StackPanel info = new() { Spacing = 4 };
        info.Children.Add(new TextBlock { Text = Texts.T("booru.ui.post.title", post.PostId), Classes = { "card-title" } });
        // I8: the post facts as a definition list; the address is no longer a text line, it is the tooltip of the link buttons.
        info.Children.Add(CreateDetails(post));

        bool withdrawn = post.IsDeleted || post.IsBanned;
        TextBlock original = new()
        {
            Text = BooruText.Original(post.OriginalAccess, withdrawn), TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.Medium
        };
        original.Classes.Add(post.OriginalAccess == BooruOriginalAccess.Available ? "workspace-status-success" : "workspace-warning");
        info.Children.Add(original);
        info.Children.Add(CreateTags(post));
        // Secondary links after the content; the primary action (load original) keeps its place on the right.
        if (BooruPostLinks.TryCanonical(post, out string canonical))
            info.Children.Add(CreateLinkActions(OpenPostPrefix + post.PostId, CopyLinkPrefix + post.PostId, canonical,
                Texts.T("booru.ui.post.openFor", post.PostId), Texts.T("booru.ui.post.copyFor", post.PostId)));

        if (BooruText.Load(row) is { } loadText)
        {
            TextBlock state = new() { Text = loadText, TextWrapping = TextWrapping.Wrap };
            state.Classes.Add(row.Load switch
            {
                BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued or BooruRowLoad.AlreadyLoaded => "workspace-status-success",
                BooruRowLoad.Handing => "workspace-meta",
                BooruRowLoad.Failed => "workspace-error",
                _ => "workspace-warning"
            });
            // Announced through the persistent notice line (_noticeText); this rebuilt line would stay silent.
            info.Children.Add(state);
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        Button load = new() { Name = LoadPrefix + post.PostId, Content = Texts.T("booru.ui.load"), VerticalAlignment = VerticalAlignment.Top };
        load.Classes.Add("primary");
        load.IsEnabled = row.CanLoad;
        string loadName = Texts.T("booru.ui.loadFor", post.PostId);
        AutomationProperties.SetName(load, loadName);
        string? why = post.OriginalAccess != BooruOriginalAccess.Available ? BooruText.Original(post.OriginalAccess, withdrawn)
            : BooruText.Load(row);
        ToolTip.SetTip(load, why ?? loadName);
        ToolTip.SetShowOnDisabled(load, true);
        if (why is not null) AutomationProperties.SetHelpText(load, why);
        load.Click += async (_, _) => await _model.Search.LoadOriginalAsync(post.PostId);
        Grid.SetColumn(load, 2);
        grid.Children.Add(load);
        card.Child = grid;
        return card;
    }

    /// <summary>"Open in browser" and "Copy link" for one canonical post URL; nothing else is ever opened or copied.</summary>
    private Control CreateLinkActions(string openName, string copyName, string url, string openAccessibleName, string copyAccessibleName)
    {
        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        Button open = new() { Name = openName, Content = Texts.T("booru.ui.post.open") };
        open.Classes.Add("ghost");
        AutomationProperties.SetName(open, openAccessibleName);
        ToolTip.SetTip(open, Texts.T("booru.ui.post.openTip", url));
        open.Click += async (_, _) => await OpenLinkAsync(url);
        Button copy = new() { Name = copyName, Content = Texts.T("booru.ui.post.copy") };
        copy.Classes.Add("ghost");
        AutomationProperties.SetName(copy, copyAccessibleName);
        ToolTip.SetTip(copy, url);
        copy.Click += async (_, _) => await CopyLinkAsync(url);
        actions.Children.Add(open);
        actions.Children.Add(copy);
        return actions;
    }

    private async Task OpenLinkAsync(string url)
    {
        // Only canonical post pages reach this method (built by BooruPostLinks); a launcher failure is shown, never retried.
        try
        {
            await _model.Services.Links.Open(url);
            SetLinkNote(null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(exception, "Could not open a Danbooru post in the browser");
            SetLinkNote(("booru.ui.link.openFailed", string.Empty, true));
        }
    }

    private async Task CopyLinkAsync(string url)
    {
        try
        {
            if (_model.Services.Links.Copy is { } copy) await copy(url);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
            else throw new InvalidOperationException("The clipboard is unavailable.");
            SetLinkNote(("booru.ui.link.copied", url, false));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(exception, "Could not copy a Danbooru post link");
            SetLinkNote(("booru.ui.link.copyFailed", string.Empty, true));
        }
    }

    private void SetLinkNote((string Key, string Argument, bool Problem)? state)
    {
        if (_closed) return;
        _linkNoteState = state;
        _applying = true;
        try { RenderChrome(_model.Watchers.View); }
        finally { _applying = false; }
    }

    private Control CreatePreview(BooruResultRowView row)
    {
        Border frame = new()
        {
            Width = PreviewSize, Height = PreviewSize, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true,
            CornerRadius = new CornerRadius(5)
        };
        if (row.Preview == BooruPreviewState.Ready && PreviewBitmap(row) is { } bitmap)
        {
            // D3: the thumbnail is flat on the card (no box, no edge of its own); only the placeholder has a quiet slot.
            // Decorative next to the card's title line; not a second "Post #n".
            Image image = new() { Source = bitmap, Stretch = Stretch.Uniform };
            AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
            frame.Child = image;
        }
        else
        {
            frame.Classes.Add("workspace-thumb-placeholder");
            string key = row.Preview switch
            {
                BooruPreviewState.Loading => "booru.ui.post.previewLoading",
                BooruPreviewState.None => "booru.ui.post.noPreview",
                _ => "booru.ui.post.previewUnavailable"
            };
            TextBlock placeholder = new()
            {
                Text = Texts.T(key), Classes = { "workspace-meta" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6)
            };
            AutomationProperties.SetAccessibilityView(placeholder, AccessibilityView.Raw);
            frame.Child = placeholder;
        }
        return frame;
    }

    /// <summary>
    /// I8: the provider's facts of a post as a definition list (term | value) instead of a "·" chain. Each value is named
    /// "term: value" for screen readers; the term itself is not read twice.
    /// </summary>
    private static Control CreateDetails(BooruPostView post)
    {
        Grid list = new() { Name = DetailsPrefix + post.PostId, ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, RowSpacing = 1 };
        void Add(string termKey, string value)
        {
            int row = list.RowDefinitions.Count;
            list.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string termText = Texts.T(termKey);
            TextBlock term = new() { Text = termText, Classes = { "workspace-meta" }, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAccessibilityView(term, AccessibilityView.Raw);
            TextBlock detail = new() { Text = value, TextWrapping = TextWrapping.Wrap };
            detail[!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Text.Meta"); // D0 token, value in the text colour
            AutomationProperties.SetName(detail, termText + ": " + value);
            Grid.SetRow(term, row); Grid.SetRow(detail, row); Grid.SetColumn(detail, 1);
            list.Children.Add(term); list.Children.Add(detail);
        }
        string rating = BooruText.Rating(post.Rating);
        if (post.Rating != BooruRating.Unknown && !string.IsNullOrEmpty(post.RawRating)
            && !string.Equals(post.RawRating, post.Rating.ToString(), StringComparison.OrdinalIgnoreCase))
            rating += " (" + post.RawRating + ")";
        Add("booru.ui.post.field.rating", rating);
        if (post.Score is { } score) Add("booru.ui.post.field.score", score.ToString(Texts.Culture));
        if (post.FavoriteCount is { } favorites) Add("booru.ui.post.field.favorites", favorites.ToString(Texts.Culture));
        if (post.CreatedAt is { } created) Add("booru.ui.post.field.created", created.ToLocalTime().ToString("d", Texts.Culture));
        if (post.Original is { } original)
        {
            StringBuilder media = new();
            if (original.Width is { } width && original.Height is { } height)
                media.Append(width.ToString(CultureInfo.InvariantCulture)).Append('×').Append(height.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(original.Extension))
                media.Append(media.Length > 0 ? " " : string.Empty).Append(original.Extension.TrimStart('.'));
            if (original.ExpectedSize is { } size)
                media.Append(media.Length > 0 ? " " : string.Empty).Append(Size(size));
            if (media.Length > 0) Add("booru.ui.post.field.file", media.ToString());
        }
        return list;
    }

    /// <summary>
    /// I7: fixed corners on the result preview, from the post data already shown (no request): the type (GIF, video) at the top
    /// right and a "deleted"/"banned" mark at the bottom left. Static borders over the preview; the card keeps its size.
    /// </summary>
    private static Control WithPreviewCorners(Control preview, BooruPostView post)
    {
        string? kind = post.Original?.Extension?.TrimStart('.').ToLowerInvariant() switch
        {
            "gif" => Texts.T("media.tile.kind.gif"),
            "mp4" or "webm" or "zip" => Texts.T("media.tile.kind.video"),
            _ => null
        };
        string? withdrawn = post.IsDeleted ? Texts.T("booru.post.deleted") : post.IsBanned ? Texts.T("booru.post.banned") : null;
        if (kind == null && withdrawn == null) return preview;
        Panel corners = new() { Width = PreviewSize, Height = PreviewSize, VerticalAlignment = VerticalAlignment.Top, Children = { preview } };
        Border badge = WorkspaceChips.CreateCornerBadge("BooruResultKind:" + post.PostId, HorizontalAlignment.Right, VerticalAlignment.Top);
        badge.Margin = new Thickness(0, 3, 3, 0);
        badge.IsVisible = kind != null;
        ((TextBlock)badge.Child!).Text = kind;
        corners.Children.Add(badge);
        Border mark = WorkspaceChips.CreateCornerMark("BooruResultWithdrawn:" + post.PostId, HorizontalAlignment.Left, VerticalAlignment.Bottom);
        mark.Margin = new Thickness(3, 0, 0, 3);
        WorkspaceChips.SetCornerMark(mark, withdrawn == null ? null : WorkspaceChips.MarkWithdrawn, withdrawn);
        corners.Children.Add(mark);
        return corners;
    }

    private static string Size(long bytes) => SizeText.Format(bytes);

    private Control CreateTags(BooruPostView post)
    {
        StackPanel panel = new() { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.tags.label"), Classes = { "workspace-meta" }, FontWeight = FontWeight.Medium });
        if (post.Tags.IsEmpty)
        {
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.tags.none"), Classes = { "workspace-meta" } });
            return panel;
        }
        int shown = 0;
        HashSet<string> shownNames = new(StringComparer.Ordinal);
        foreach (BooruTagCategory category in new[] { BooruTagCategory.Artist, BooruTagCategory.Copyright, BooruTagCategory.Character,
            BooruTagCategory.General, BooruTagCategory.Meta, BooruTagCategory.Unknown })
        {
            string[] names = post.Tags.Where(tag => tag.Category == category).Select(tag => tag.Name).ToArray();
            if (names.Length == 0 || shown >= MaximumTagsShown) continue;
            string[] take = names.Take(MaximumTagsShown - shown).ToArray();
            shown += take.Length;
            shownNames.UnionWith(take);
            // C1: source tags as compact dashed chips with their category mark; the category is named in front, so the mark is
            // only an addition. I8: Danbooru's category order (artist, copyright, character, general, meta); a chip adds its tag
            // to the search field (nothing is requested until Search).
            string label = Texts.T("booru.ui.tagCategory." + category);
            WrapPanel line = new() { Name = "BooruResultTags:" + category, ItemSpacing = 4, LineSpacing = 4 };
            line.Children.Add(new TextBlock { Text = label + ":", VerticalAlignment = VerticalAlignment.Center, Classes = { "workspace-meta" } });
            foreach (string name in take)
            {
                Button chip = new()
                {
                    Name = ResultTagPrefix + post.PostId + ":" + name, Content = WorkspaceChips.CreateSourceTag(name, category, compact: true),
                    Classes = { WorkspaceChips.SourceButton }, MaxWidth = 240
                };
                string accessible = Texts.T("booru.ui.tags.addFor", name, label);
                AutomationProperties.SetName(chip, accessible);
                ToolTip.SetTip(chip, accessible);
                chip.Click += (_, _) => AddTagToSearch(name);
                line.Children.Add(chip);
            }
            AutomationProperties.SetName(line, label + ": " + string.Join(", ", take));
            panel.Children.Add(line);
        }
        if (post.Tags.Length > shown)
        {
            // The tags that do not fit are named for the mouse and for assistive technology; the details dialog of the saved medium lists all.
            string[] hidden = post.Tags.Select(tag => tag.Name).Where(name => !shownNames.Contains(name)).ToArray();
            string rest = string.Join(", ", hidden.Take(MaximumHiddenTagsNamed)) + (hidden.Length > MaximumHiddenTagsNamed ? "…" : string.Empty);
            TextBlock more = new() { Name = MoreTagsPrefix + post.PostId, Text = Texts.T("booru.ui.tags.more", post.Tags.Length - shown), Classes = { "workspace-meta" } };
            ToolTip.SetTip(more, rest);
            AutomationProperties.SetHelpText(more, rest);
            panel.Children.Add(more);
        }
        return panel;
    }

    /// <summary>
    /// I8: a source tag chip of a result adds its tag to the search field (tag search) up to the parser's limit. Only the field
    /// changes; nothing is requested until Search. The outcome is said in the polite link note line.
    /// </summary>
    internal void AddTagToSearch(string tag)
    {
        if (_closed) return;
        string current = _model.Search.View.Mode == BooruInputMode.Tags ? _input.Text ?? string.Empty : string.Empty;
        string[] terms = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Contains(tag, StringComparer.Ordinal)) { SetLinkNote(("booru.ui.tags.alreadyInSearch", tag, false)); return; }
        if (!BooruSourceTagSearch.Plan(new[] { tag }).IsValid) { SetLinkNote(("booru.ui.tags.unusable", tag, true)); return; }
        if (terms.Length >= BooruSourceTagSearch.MaximumTags)
        {
            SetLinkNote(("booru.ui.tags.searchFull", BooruSourceTagSearch.MaximumTags.ToString(Texts.Culture), true));
            return;
        }
        string query = string.Join(' ', terms.Append(tag));
        _model.Search.SetMode(BooruInputMode.Tags);
        _input.Text = query;
        _model.Search.SetInput(query);
        SetLinkNote(("booru.ui.tags.addedToSearch", tag, false));
    }

    private Bitmap? PreviewBitmap(BooruResultRowView row)
    {
        if (row.PreviewBytes is not { Length: > 0 } bytes || bytes.Length > MaximumPreviewBytes) return null;
        string id = row.Post.PostId;
        if (_bitmaps.TryGetValue(id, out CachedBitmap? cached) && ReferenceEquals(cached.Bytes, bytes)) return cached.Bitmap;
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using SKCodec? codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false));
            if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > 40_000_000) return null;
            stream.Position = 0;
            int width = Math.Min(codec.Info.Width, PreviewSize * 2);
            Bitmap bitmap = Bitmap.DecodeToWidth(stream, width);
            if (cached is not null) cached.Bitmap.Dispose();
            _bitmaps[id] = new CachedBitmap(bytes, bitmap);
            return bitmap;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void PruneBitmaps(IEnumerable<BooruResultRowView> rows)
    {
        HashSet<string> keep = rows.Select(row => row.Post.PostId).ToHashSet(StringComparer.Ordinal);
        foreach (string id in _bitmaps.Keys.Where(id => !keep.Contains(id)).ToArray())
        {
            // The old cards are already detached by the rebuild, so the image can be released.
            _bitmaps[id].Bitmap.Dispose();
            _bitmaps.Remove(id);
        }
    }

    // ---- watcher cards ------------------------------------------------------------------------------------

    private Control CreateWatcherCard(BooruWatcherRowView row)
    {
        BooruWatcherListModel model = _model.Watchers;
        BooruWatcherView watcher = row.Watcher;
        DateTimeOffset? cooling = model.ActiveCooldown(watcher);
        // The query line names the card; the border itself carries no name, so the query is read once.
        Border card = new() { Name = "BooruWatcher:" + watcher.WatcherId };
        card.Classes.Add("workspace-card");
        StackPanel panel = new() { Spacing = 8 };

        Grid head = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        head.Children.Add(new SelectableTextBlock
        {
            Text = watcher.Query, Classes = { "mono", "card-title" }, TextWrapping = TextWrapping.Wrap
        });
        Border chip = new() { Child = new TextBlock { Text = BooruText.WatcherStatus(row, cooling is not null) }, VerticalAlignment = VerticalAlignment.Top };
        chip.Classes.Add("workspace-status");
        chip.Classes.Add(watcher.Status switch
        {
            BooruWatcherStatus.Active or BooruWatcherStatus.Checking => "workspace-status-active",
            BooruWatcherStatus.Cooldown => cooling is not null ? "workspace-status-warning" : "workspace-status-active",
            BooruWatcherStatus.Error => "workspace-status-error",
            _ => "workspace-status-none"
        });
        Grid.SetColumn(chip, 1);
        head.Children.Add(chip);
        panel.Children.Add(head);

        string next = watcher.Status == BooruWatcherStatus.Paused ? Texts.T("booru.ui.watcher.pausedNext")
            : BooruText.Time(watcher.NextCheckAt, "booru.ui.watcher.notScheduled");
        panel.Children.Add(new TextBlock
        {
            Text = Texts.T("booru.ui.watcher.lastCheck", BooruText.Time(watcher.LastCheckedAt, "booru.ui.watcher.never"))
                + " · " + Texts.T("booru.ui.watcher.nextCheck", next),
            Classes = { "workspace-meta" }
        });
        panel.Children.Add(new TextBlock
        {
            Text = Texts.T("booru.ui.watcher.hits", watcher.HitCount, watcher.QueuedCount), Classes = { "workspace-meta" }
        });
        if (watcher.LatestPostId is { } latest && BooruPostLinks.TryCanonical(latest, out string latestUrl))
        {
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.latest", latest), Classes = { "workspace-meta" } });
            panel.Children.Add(CreateLinkActions(WatcherOpenPrefix + watcher.WatcherId, WatcherCopyPrefix + watcher.WatcherId, latestUrl,
                Texts.T("booru.ui.watcher.latestOpenFor", latest, watcher.Query), Texts.T("booru.ui.watcher.latestCopyFor", latest, watcher.Query)));
        }
        if (!watcher.BaselineEstablished)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.baseline"), Classes = { "workspace-help" } });
        if (watcher.BudgetLimited)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.budget"), Classes = { "workspace-help" } });
        if (cooling is { } until)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.cooldown", BooruText.Time(until)), Classes = { "workspace-warning" } });
        if (watcher.Status == BooruWatcherStatus.Error)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.error", BooruText.CodeText(watcher.ErrorCode)), Classes = { "workspace-error" } });
        if (row.Message is { } message)
        {
            TextBlock text = new() { Text = BooruText.Notice(message), TextWrapping = TextWrapping.Wrap };
            text.Classes.Add(message.Kind switch
            {
                BooruNoticeKind.Success => "workspace-status-success",
                BooruNoticeKind.Warning => "workspace-warning",
                BooruNoticeKind.Error => "workspace-error",
                _ => "workspace-meta"
            });
            // Announced through the persistent watcher notice line (_watcherNoticeText); this rebuilt line would stay silent.
            panel.Children.Add(text);
        }

        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        BooruWatcherBlock checkBlock = model.CheckBlock(row);
        actions.Children.Add(WatcherButton(CheckPrefix + watcher.WatcherId, "booru.ui.watcher.checkNow", "booru.ui.watcher.checkNowFor",
            watcher.Query, checkBlock, cooling, async () => await model.CheckNowAsync(watcher.WatcherId)));
        if (watcher.Status == BooruWatcherStatus.Paused)
            actions.Children.Add(WatcherButton(ResumePrefix + watcher.WatcherId, "booru.ui.watcher.resume", "booru.ui.watcher.resumeFor",
                watcher.Query, model.ResumeBlock(row), cooling, async () => await model.ResumeAsync(watcher.WatcherId), ghost: true));
        else
            actions.Children.Add(WatcherButton(PausePrefix + watcher.WatcherId, "booru.ui.watcher.pause", "booru.ui.watcher.pauseFor",
                watcher.Query, model.PauseBlock(row), cooling, async () => await model.PauseAsync(watcher.WatcherId), ghost: true));
        Button remove = WatcherButton(RemovePrefix + watcher.WatcherId, "booru.ui.watcher.remove", "booru.ui.watcher.removeFor",
            watcher.Query, model.RemoveBlock(row), cooling, () => { model.RequestRemoval(watcher.WatcherId); return Task.CompletedTask; });
        remove.Classes.Add("danger");
        actions.Children.Add(remove);
        if (BooruWatcherListModel.CanRestartFromNow(watcher))
            actions.Children.Add(WatcherButton(RestartPrefix + watcher.WatcherId, "booru.ui.watcher.restart", "booru.ui.watcher.restartFor",
                watcher.Query, model.RestartBlock(row), cooling, async () => await model.RestartFromNowAsync(watcher.WatcherId), ghost: true));
        panel.Children.Add(actions);

        if (row.ConfirmingRemoval)
        {
            Border confirm = new();
            confirm.Classes.Add("workspace-warning");
            StackPanel confirmPanel = new() { Spacing = 8 };
            confirmPanel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.removeAsk", watcher.Query), TextWrapping = TextWrapping.Wrap });
            WrapPanel confirmActions = new() { Classes = { "workspace-toolbar" } };
            // Safe answer first; it also gets the focus when the question opens (RenderWatchers).
            Button no = new() { Name = RemoveCancelPrefix + watcher.WatcherId, Content = Texts.T("booru.ui.cancel") };
            no.Click += (_, _) => model.CancelRemoval(watcher.WatcherId);
            Button yes = new() { Name = RemoveConfirmPrefix + watcher.WatcherId, Content = Texts.T("booru.ui.watcher.removeConfirm") };
            yes.Classes.Add("danger");
            AutomationProperties.SetName(yes, Texts.T("booru.ui.watcher.removeFor", watcher.Query));
            yes.IsEnabled = !row.Busy;
            yes.Click += async (_, _) => await model.ConfirmRemovalAsync(watcher.WatcherId);
            confirmActions.Children.Add(no);
            confirmActions.Children.Add(yes);
            confirmPanel.Children.Add(confirmActions);
            confirm.Child = confirmPanel;
            panel.Children.Add(confirm);
        }
        card.Child = panel;
        return card;
    }

    private static Button WatcherButton(string name, string labelKey, string nameKey, string query, BooruWatcherBlock block,
        DateTimeOffset? cooling, Func<Task> action, bool ghost = false)
    {
        Button button = new() { Name = name, Content = Texts.T(labelKey), IsEnabled = block == BooruWatcherBlock.None };
        // Check now keeps its outline as the card's one findable action, Remove keeps its red one; the rest is quiet (UX round 3).
        if (ghost) button.Classes.Add("ghost");
        string accessible = Texts.T(nameKey, query);
        AutomationProperties.SetName(button, accessible);
        string why = BooruText.WatcherBlock(block, cooling);
        ToolTip.SetTip(button, block == BooruWatcherBlock.None ? accessible : why);
        ToolTip.SetShowOnDisabled(button, true);
        if (block != BooruWatcherBlock.None) AutomationProperties.SetHelpText(button, why);
        button.Click += async (_, _) => await action();
        return button;
    }

    // ---- lifetime ----------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _clock.Stop();
        _clock.Tick -= OnClockTick;
        ChromeChanged = null;
        Texts.Changed -= OnTextsChanged;
        _model.Search.Changed -= OnModelChanged;
        _model.Watchers.Changed -= OnModelChanged;
        foreach (CachedBitmap cached in _bitmaps.Values) cached.Bitmap.Dispose();
        _bitmaps.Clear();
        if (_ownsModel) _model.Dispose();
    }
}
