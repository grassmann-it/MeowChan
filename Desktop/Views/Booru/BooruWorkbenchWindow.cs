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
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The Danbooru window: a Search tab (tags or one post link, bounded paging, result cards) and a Watchers
/// tab (list with the five explicit commands). It renders the models' immutable snapshots and sends every
/// request through them; it never loads a URL, polls, or acts on its own. Built from the shared dialog frame,
/// workspace classes and command-free buttons, like the other secondary tools.
/// </summary>
internal sealed class BooruWorkbenchWindow : Window, IDisposable
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

    private const int PreviewSize = 112;
    private const int MaximumTagsShown = 24;
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
    private readonly TextBox _input = new() { Name = InputName, AcceptsReturn = false };
    private readonly Button _submit = new() { Name = SubmitName };
    private readonly Button _cancel = new() { Name = CancelName };
    private readonly SelectableTextBlock _queryLine = new() { Name = QueryName, Classes = { "workspace-meta", "mono" } };
    private readonly TextBlock _queryHint = new() { Classes = { "workspace-help" } };
    private readonly Border _statusHost = new() { Name = StatusName };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new() { Name = RetryName };
    private readonly WrapPanel _pager = new() { Classes = { "workspace-toolbar" } };
    private readonly Button _previous = new() { Name = PreviousName };
    private readonly Button _next = new() { Name = NextName };
    private readonly TextBlock _pageInfo = new() { Classes = { "workspace-meta" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pageLimit = new() { Classes = { "workspace-help" } };
    private readonly Button _createWatcher = new() { Name = CreateWatcherName, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _createHint = new() { Classes = { "workspace-help" } };
    private readonly Border _noticeHost = new();
    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _results = new() { Name = ResultsName, Spacing = 8 };
    private readonly TextBlock _watchersIntro = new() { Classes = { "workspace-help" } };
    private readonly Border _removeNoteHost = new() { Name = RemoveNoteName, Classes = { "workspace-status" } };
    private readonly TextBlock _removeNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _watcherNoticeHost = new();
    private readonly TextBlock _watcherNoticeText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _watchersState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _watchersReload = new();
    private readonly StackPanel _watchers = new() { Name = WatchersName, Spacing = 8 };
    private readonly TextBlock _footerNote = new() { Name = NoFilterNoteName, Classes = { "workspace-help" } };
    private readonly Button _close = new();

    private string? _resultsKey;
    private string? _watchersKey;
    private bool _paused;
    private string _cooling = string.Empty;
    private bool _applying;
    private bool _renderQueued;
    private bool _closed;

    internal BooruWorkbenchWindow(BooruWorkbenchServices services) : this(new BooruWorkbenchModel(services), ownsModel: true) { }

    internal BooruWorkbenchWindow(BooruWorkbenchModel model, bool ownsModel)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _ownsModel = ownsModel;
        Name = "BooruWorkbenchWindow";
        Width = 1000; Height = 760; MinWidth = 600; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = DialogFrame.Build(BuildHeader(), new Border { Child = BuildBody(), Classes = { "workspace-dialog-body" } },
            BuildFooter(), DialogFrame.ErrorStrip(new TextBlock()));

        _model.Search.Changed += OnModelChanged;
        _model.Watchers.Changed += OnModelChanged;
        Texts.Changed += OnTextsChanged;
        _clock.Tick += (_, _) => OnClockTick();
        Opened += async (_, _) =>
        {
            _clock.Start();
            _input.Focus();
            // Reads the stored watcher list only; no provider request is made by opening the window.
            await _model.InitializeAsync();
        };
        Closed += (_, _) => Dispose();
        KeyDown += OnKeyDown;
        Render(force: true);
    }

    internal BooruWorkbenchModel Model => _model;

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
        _close.Click += (_, _) => Close();
        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        _footerNote.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_footerNote);
        _close.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_close, 1);
        footer.Children.Add(_close);
        Border host = new() { Child = footer };
        host.Classes.Add("workspace-dialog-footer");
        return host;
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

    private Control BuildWatchersTab()
    {
        _removeNoteHost.Child = _removeNote;
        _watcherNoticeHost.Child = _watcherNoticeText;
        AutomationProperties.SetLiveSetting(_watcherNoticeText, AutomationLiveSetting.Polite);
        _watchersReload.Click += async (_, _) => await _model.Watchers.RefreshAsync();
        StackPanel tab = new() { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        tab.Children.Add(_watchersIntro);
        tab.Children.Add(_removeNoteHost);
        tab.Children.Add(_watcherNoticeHost);
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
    private void OnClockTick()
    {
        if (_closed) return;
        BooruWatcherListModel watchers = _model.Watchers;
        bool paused = _model.Search.IsPaused;
        string cooling = string.Join('|', watchers.View.Rows.Where(row => watchers.ActiveCooldown(row.Watcher) is not null).Select(row => row.WatcherId));
        if (paused != _paused || cooling != _cooling) Render(force: true);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutServiceCapturing()) return;
        if (e.Key == Key.Escape && !(_mode.IsDropDownOpen))
        {
            e.Handled = true;
            Close();
        }
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
        Title = Texts.T("booru.ui.title");
        _title.Text = Texts.T("booru.ui.title");
        _provider.Text = Texts.T("booru.ui.provider", _model.Search.View.ProviderName);
        _intro.Text = Texts.T("booru.ui.intro");
        _searchTab.Header = Texts.T("booru.ui.tab.search");
        _watchersTab.Header = Texts.T("booru.ui.tab.watchers", _model.Watchers.Count);
        AutomationProperties.SetName(_watchersTab, Texts.T("booru.ui.tab.watchers", _model.Watchers.Count));
        _footerNote.Text = Texts.T("booru.ui.noFilterNote");
        _close.Content = Texts.T("action.close");
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
        AutomationProperties.SetHelpText(_input, invalid ? BooruText.Issue(view.Parsed.Issue) : string.Empty);

        _submit.Content = Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.lookup" : "booru.ui.search");
        _submit.IsEnabled = model.CanSubmit;
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

        if (view.Notice is { } notice)
        {
            _noticeText.Text = BooruText.Notice(notice);
            ApplyTone(_noticeHost, notice.Kind);
        }
        _noticeHost.IsVisible = view.Notice is not null;

        _statusText.Text = BooruText.SearchStatus(view, paused);
        _statusHost.IsVisible = true;
        ApplyTone(_statusHost, ToneOf(view.Status));
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
            RebuildPanel(_results, () => view.Rows.Select(CreateResultCard).ToList());
            PruneBitmaps(view.Rows);
        }
    }

    private void RenderWatchers(BooruWatcherListView view, bool force)
    {
        BooruWatcherListModel model = _model.Watchers;
        _watchersIntro.Text = Texts.T("booru.ui.watchers.intro");
        _removeNote.Text = Texts.T("booru.ui.watchers.removeNote");
        if (view.Notice is { } notice)
        {
            _watcherNoticeText.Text = BooruText.Notice(notice);
            ApplyTone(_watcherNoticeHost, notice.Kind);
        }
        _watcherNoticeHost.IsVisible = view.Notice is not null;
        _watchersState.IsVisible = view.Status != BooruWatcherListStatus.Ready;
        _watchersState.Text = view.Status switch
        {
            BooruWatcherListStatus.Loading => Texts.T("booru.ui.watchers.loading"),
            BooruWatcherListStatus.Empty => Texts.T("booru.ui.watchers.empty"),
            BooruWatcherListStatus.Failed => Texts.T("booru.ui.watchers.loadFailed", BooruText.CodeText(view.Code)),
            _ => string.Empty
        };
        _watchersState.Classes.Set("workspace-error", view.Status == BooruWatcherListStatus.Failed);
        _watchersReload.Content = Texts.T("booru.ui.watchers.retry");
        _watchersReload.IsVisible = view.Status == BooruWatcherListStatus.Failed;

        _cooling = string.Join('|', view.Rows.Where(row => model.ActiveCooldown(row.Watcher) is not null).Select(row => row.WatcherId));
        string key = string.Join('\n', Texts.Language, _cooling, string.Join('|', view.Rows.Select(row => string.Join(',',
            row.WatcherId, row.Watcher, row.Busy, row.ConfirmingRemoval, row.Message?.Key, row.Message is { } message
                ? string.Join(',', message.Args.Select(arg => arg?.ToString())) : string.Empty))));
        if (force || key != _watchersKey)
        {
            _watchersKey = key;
            RebuildPanel(_watchers, () => view.Rows.Select(CreateWatcherCard).ToList());
        }
    }

    /// <summary>Replaces the children while keeping keyboard focus on the same named control when it survives.</summary>
    private void RebuildPanel(Panel panel, Func<List<Control>> build)
    {
        string? focusedName = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control { Name: { } name }
            && panel.GetVisualDescendants().Contains((Visual)TopLevel.GetTopLevel(this)!.FocusManager!.GetFocusedElement()!)
            ? name : null;
        panel.Children.Clear();
        foreach (Control child in build()) panel.Children.Add(child);
        if (focusedName is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            Control? same = panel.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == focusedName && control.IsEffectivelyEnabled);
            same?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

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
        Border card = new() { Name = "BooruResult:" + post.PostId };
        card.Classes.Add("workspace-section");
        AutomationProperties.SetName(card, Texts.T("booru.ui.post.title", post.PostId));

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(CreatePreview(row));

        StackPanel info = new() { Spacing = 4 };
        info.Children.Add(new TextBlock { Text = Texts.T("booru.ui.post.title", post.PostId), FontWeight = FontWeight.SemiBold, FontSize = 14 });
        if (post.PostUrl is { } url)
        {
            SelectableTextBlock link = new() { Text = url.ToString(), Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(link, Texts.T("booru.ui.post.urlLabel"));
            info.Children.Add(link);
        }
        info.Children.Add(new TextBlock { Text = MetaLine(post), Classes = { "workspace-meta" } });

        bool withdrawn = post.IsDeleted || post.IsBanned;
        TextBlock original = new()
        {
            Text = BooruText.Original(post.OriginalAccess, withdrawn), TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.Medium
        };
        original.Classes.Add(post.OriginalAccess == BooruOriginalAccess.Available ? "workspace-status-success" : "workspace-warning");
        info.Children.Add(original);
        info.Children.Add(CreateTags(post));

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
            AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
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
        if (why is not null) AutomationProperties.SetHelpText(load, why);
        load.Click += async (_, _) => await _model.Search.LoadOriginalAsync(post.PostId);
        Grid.SetColumn(load, 2);
        grid.Children.Add(load);
        card.Child = grid;
        return card;
    }

    private Control CreatePreview(BooruResultRowView row)
    {
        Border frame = new()
        {
            Width = PreviewSize, Height = PreviewSize, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true,
            CornerRadius = new CornerRadius(5)
        };
        frame.Classes.Add("raised");
        if (row.Preview == BooruPreviewState.Ready && PreviewBitmap(row) is { } bitmap)
        {
            frame.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            AutomationProperties.SetName(frame, Texts.T("booru.ui.post.title", row.Post.PostId));
        }
        else
        {
            string key = row.Preview switch
            {
                BooruPreviewState.Loading => "booru.ui.post.previewLoading",
                BooruPreviewState.None => "booru.ui.post.noPreview",
                _ => "booru.ui.post.previewUnavailable"
            };
            frame.Child = new TextBlock
            {
                Text = Texts.T(key), Classes = { "workspace-meta" }, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6)
            };
        }
        return frame;
    }

    private static string MetaLine(BooruPostView post)
    {
        List<string> parts = new();
        string rating = BooruText.Rating(post.Rating);
        if (post.Rating != BooruRating.Unknown && !string.IsNullOrEmpty(post.RawRating)
            && !string.Equals(post.RawRating, post.Rating.ToString(), StringComparison.OrdinalIgnoreCase))
            rating += " (" + post.RawRating + ")";
        parts.Add(Texts.T("booru.ui.post.rating", rating));
        if (post.Score is { } score) parts.Add(Texts.T("booru.ui.post.score", score));
        if (post.FavoriteCount is { } favorites) parts.Add(Texts.T("booru.ui.post.favorites", favorites));
        if (post.CreatedAt is { } created) parts.Add(Texts.T("booru.ui.post.created", created.ToLocalTime().ToString("d", Texts.Culture)));
        if (post.Original is { } original)
        {
            StringBuilder media = new();
            if (original.Width is { } width && original.Height is { } height)
                media.Append(width.ToString(CultureInfo.InvariantCulture)).Append('×').Append(height.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(original.Extension))
                media.Append(media.Length > 0 ? " " : string.Empty).Append(original.Extension.TrimStart('.'));
            if (original.ExpectedSize is { } size)
                media.Append(media.Length > 0 ? " " : string.Empty).Append(Size(size));
            if (media.Length > 0) parts.Add(media.ToString());
        }
        return string.Join(" · ", parts);
    }

    private static string Size(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (unit == 0 ? value.ToString("0", Texts.Culture) : value.ToString("0.#", Texts.Culture)) + " " + units[unit];
    }

    private static Control CreateTags(BooruPostView post)
    {
        StackPanel panel = new() { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.tags.label"), Classes = { "workspace-meta" }, FontWeight = FontWeight.Medium });
        if (post.Tags.IsEmpty)
        {
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.tags.none"), Classes = { "workspace-meta" } });
            return panel;
        }
        int shown = 0;
        foreach (BooruTagCategory category in new[] { BooruTagCategory.Artist, BooruTagCategory.Copyright, BooruTagCategory.Character,
            BooruTagCategory.General, BooruTagCategory.Meta, BooruTagCategory.Unknown })
        {
            string[] names = post.Tags.Where(tag => tag.Category == category).Select(tag => tag.Name).ToArray();
            if (names.Length == 0 || shown >= MaximumTagsShown) continue;
            string[] take = names.Take(MaximumTagsShown - shown).ToArray();
            shown += take.Length;
            panel.Children.Add(new TextBlock
            {
                Text = Texts.T("booru.ui.tagCategory." + category) + ": " + string.Join(", ", take),
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (post.Tags.Length > shown)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.tags.more", post.Tags.Length - shown), Classes = { "workspace-meta" } });
        return panel;
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
        Border card = new() { Name = "BooruWatcher:" + watcher.WatcherId };
        card.Classes.Add("workspace-section");
        AutomationProperties.SetName(card, watcher.Query);
        StackPanel panel = new() { Spacing = 6 };

        Grid head = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        head.Children.Add(new SelectableTextBlock
        {
            Text = watcher.Query, Classes = { "mono" }, FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap
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
            AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
            panel.Children.Add(text);
        }

        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        BooruWatcherBlock checkBlock = model.CheckBlock(row);
        actions.Children.Add(WatcherButton(CheckPrefix + watcher.WatcherId, "booru.ui.watcher.checkNow", "booru.ui.watcher.checkNowFor",
            watcher.Query, checkBlock, cooling, async () => await model.CheckNowAsync(watcher.WatcherId)));
        if (watcher.Status == BooruWatcherStatus.Paused)
            actions.Children.Add(WatcherButton(ResumePrefix + watcher.WatcherId, "booru.ui.watcher.resume", "booru.ui.watcher.resumeFor",
                watcher.Query, model.ResumeBlock(row), cooling, async () => await model.ResumeAsync(watcher.WatcherId)));
        else
            actions.Children.Add(WatcherButton(PausePrefix + watcher.WatcherId, "booru.ui.watcher.pause", "booru.ui.watcher.pauseFor",
                watcher.Query, model.PauseBlock(row), cooling, async () => await model.PauseAsync(watcher.WatcherId)));
        Button remove = WatcherButton(RemovePrefix + watcher.WatcherId, "booru.ui.watcher.remove", "booru.ui.watcher.removeFor",
            watcher.Query, model.RemoveBlock(row), cooling, () => { model.RequestRemoval(watcher.WatcherId); return Task.CompletedTask; });
        remove.Classes.Add("danger");
        actions.Children.Add(remove);
        if (BooruWatcherListModel.CanRestartFromNow(watcher))
            actions.Children.Add(WatcherButton(RestartPrefix + watcher.WatcherId, "booru.ui.watcher.restart", "booru.ui.watcher.restartFor",
                watcher.Query, model.RestartBlock(row), cooling, async () => await model.RestartFromNowAsync(watcher.WatcherId)));
        panel.Children.Add(actions);

        if (row.ConfirmingRemoval)
        {
            Border confirm = new();
            confirm.Classes.Add("workspace-warning");
            StackPanel confirmPanel = new() { Spacing = 8 };
            confirmPanel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.removeAsk", watcher.Query), TextWrapping = TextWrapping.Wrap });
            WrapPanel confirmActions = new() { Classes = { "workspace-toolbar" } };
            Button yes = new() { Name = RemoveConfirmPrefix + watcher.WatcherId, Content = Texts.T("booru.ui.watcher.removeConfirm") };
            yes.Classes.Add("danger");
            AutomationProperties.SetName(yes, Texts.T("booru.ui.watcher.removeFor", watcher.Query));
            yes.IsEnabled = !row.Busy;
            yes.Click += async (_, _) => await model.ConfirmRemovalAsync(watcher.WatcherId);
            Button no = new() { Name = RemoveCancelPrefix + watcher.WatcherId, Content = Texts.T("booru.ui.cancel") };
            no.Click += (_, _) => model.CancelRemoval(watcher.WatcherId);
            confirmActions.Children.Add(yes);
            confirmActions.Children.Add(no);
            confirmPanel.Children.Add(confirmActions);
            confirm.Child = confirmPanel;
            panel.Children.Add(confirm);
        }
        card.Child = panel;
        return card;
    }

    private static Button WatcherButton(string name, string labelKey, string nameKey, string query, BooruWatcherBlock block,
        DateTimeOffset? cooling, Func<Task> action)
    {
        Button button = new() { Name = name, Content = Texts.T(labelKey), IsEnabled = block == BooruWatcherBlock.None };
        string accessible = Texts.T(nameKey, query);
        AutomationProperties.SetName(button, accessible);
        string why = BooruText.WatcherBlock(block, cooling);
        ToolTip.SetTip(button, block == BooruWatcherBlock.None ? accessible : why);
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
        Texts.Changed -= OnTextsChanged;
        _model.Search.Changed -= OnModelChanged;
        _model.Watchers.Changed -= OnModelChanged;
        foreach (CachedBitmap cached in _bitmaps.Values) cached.Bitmap.Dispose();
        _bitmaps.Clear();
        if (_ownsModel) _model.Dispose();
    }
}
