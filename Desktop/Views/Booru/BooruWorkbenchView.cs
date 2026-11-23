using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.PluginSdk;
using Serilog;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Reusable Danbooru workbench content. All provider actions still go through the existing models.
/// The host supplies layout/chrome and calls ActivateAsync when first shown; detaching only suspends
/// the clock, not the model or its in-flight work. The host must Dispose when permanently done.
/// </summary>
internal sealed partial class BooruWorkbenchView : UserControl, IDisposable
{
    internal const string TabsName = "BooruTabs";
    internal const string ModeName = "BooruMode";
    internal const string InputName = "BooruInput";
    internal const string SubmitName = "BooruSubmit";
    internal const string CancelName = "BooruCancel";
    internal const string RetryName = "BooruRetry";
    internal const string SearchHelpName = "BooruSearchHelp";
    internal const string StatusName = "BooruStatus";
    internal const string PreviousName = "BooruPreviousPage";
    internal const string NextName = "BooruNextPage";
    internal const string CreateWatcherName = "BooruCreateWatcher";
    internal const string ResultsName = "BooruResults";
    internal const string WatchersName = "BooruWatchers";
    internal const string RemoveNoteName = "BooruRemoveNote";
    internal const string LoadPrefix = "BooruLoad:";
    internal const string CancelLoadPrefix = "BooruCancelLoad:";
    internal const string RetryLoadPrefix = "BooruRetryLoad:";
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
    internal const string ShowInMediaPrefix = "BooruShowInMedia:";
    internal const string WatcherShowInMediaPrefix = "BooruWatcherShowInMedia:";

    private readonly HashSet<string> _expandedTagPosts = new(StringComparer.Ordinal);
    private const int MaximumPreviewBytes = 8 * 1024 * 1024;

    private sealed record CachedBitmap(byte[] Bytes, Bitmap Bitmap);

    private readonly BooruWorkbenchModel _model;
    private readonly bool _ownsModel;
    private readonly Dictionary<(string PostId, int DecodeWidth), CachedBitmap> _bitmaps = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<ContextMenuHost> _resultTagMenus = new();
    private readonly IDisposable _keyContextHost;

    private readonly TextBlock _title = new() { Classes = { "page-title" }, TextWrapping = TextWrapping.Wrap };
    private readonly HelpButton _searchHelp = new() { Name = SearchHelpName };
    private readonly TabControl _tabs = new() { Name = TabsName, Classes = { "booru-tabs" } };
    private readonly TabItem _searchTab = new() { Tag = BooruSearchTabs.PrimaryId, Classes = { "booru-tab" } };
    private readonly TabItem _watchersTab = new() { Name = WatchersTabName };
    private readonly ComboBox _mode = new() { Name = ModeName, MinWidth = 136 };
    // Escape empties the typed query first (SearchBox); in an empty field it closes the window as before. No debounce: a search goes out only on "Search".
    private readonly SearchBox _input = new() { Name = InputName, AcceptsReturn = false };
    private readonly Button _submit = new() { Name = SubmitName };
    private readonly Button _cancel = new() { Name = CancelName };
    // Why the field is not accepted, shown under it: the red border alone is colour only.
    private readonly TextBlock _inputIssue = new() { Name = InputIssueName, Classes = { "workspace-error" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Border _statusHost = new() { Name = StatusName };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new() { Name = RetryName, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _pager = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _previous = new() { Name = PreviousName, Classes = { "booru-pager-button" } };
    private readonly Button _next = new() { Name = NextName, Classes = { "booru-pager-button" } };
    private readonly TextBlock _pageInfo = new() { Classes = { "workspace-meta", "tnum" }, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _pageLimit = new() { Classes = { "workspace-help" }, TextAlignment = TextAlignment.Center };
    private readonly Button _createWatcher = new() { Name = CreateWatcherName, HorizontalAlignment = HorizontalAlignment.Left };
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
    private Func<string, Task<bool>>? _showInMedia;
    private Func<string, bool>? _isPostSavedInMedia;
    private bool _paused;
    private string _cooling = string.Empty;
    private bool _applying;
    private bool _renderQueued;
    private bool _closed;
    private long _linkActionRevision;

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
        // Every key of the workbench, on the Danbooru page and in its window, runs in the "booru" context (S3 P2-4): a default that a saved binding took
        // away only elsewhere (a partial yield) stays active here (EffectiveShortcutBindings.EnterKeyContext).
        _keyContextHost = MeowChan.Desktop.Controls.KeyContextHost.Install(this, _ => "booru");
        Header = BuildHeader();
        Body = BuildBody();
        Footer = BuildFooter();
        Content = buildLayout is null ? BuildLayout(Header, Body, Footer) : buildLayout(Header, Body, Footer);

        _model.Search.Changed += OnModelChanged;
        _model.Watchers.Changed += OnModelChanged;
        Texts.Changed += OnTextsChanged;
        ShortcutService.Changed += OnModelChanged;
        _clock.Tick += OnClockTick;
        RegisterWorkViewIds();
        InitializeHitList();
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
        ScrollViewer scroll = new()
        {
            Name = "BooruWorkbenchScroll", Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
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
        if (_attached)
        {
            _clock.Start();
            RenderSearchActions(_model.Search.View);
        }
        FocusSearchInput();
        return _initialization ??= _model.InitializeAsync();
    }

    internal void FocusSearchInput()
    {
        if (!_closed) _input.Focus();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.RootVisual is TopLevel top) top.ScalingChanged += OnListScalingChanged;
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
        DismissResultViewer();
        if (e.RootVisual is TopLevel top) top.ScalingChanged -= OnListScalingChanged;
        base.OnDetachedFromVisualTree(e);
    }

    internal BooruWorkbenchModel Model => _model;

    // The shell owns navigation; this callback never selects or authorizes a build itself.
    internal Func<Task>? ReviewCurrentBuild { get; set; }

    /// <summary>Host navigation to the local media entry of an already saved post. The callback never performs provider I/O.</summary>
    internal Func<string, Task<bool>>? ShowInMedia
    {
        get => _showInMedia;
        set
        {
            _showInMedia = value;
            // Replacing the host callback retires a pending navigation even when the host has not
            // disposed the view yet (for example during a profile/service handover).
            Interlocked.Increment(ref _linkActionRevision);
            Render(force: true);
        }
    }

    /// <summary>E2: how many source tag chips a result shows before "+N more" (the setting, read at each render); the default without a host.</summary>
    internal Func<int>? ResultTagsShown { get; set; }
    private Config? _listProfile;
    private BooruListLayoutSettings ListLayout => _listProfile?.BooruListLayout ?? BooruListLayoutSettings.Default;

    internal void ApplyListProfile(Config config) => _listProfile = config;

    private int TagsShown() => Math.Clamp(ResultTagsShown?.Invoke() ?? _listProfile?.BooruResultTagsShown ?? MeowChan.Config.BooruResultTagsShownDefault,
        MeowChan.Config.BooruResultTagsShownMinimum, MeowChan.Config.BooruResultTagsShownMaximum);

    /// <summary>Passive local predicate used for watcher cards; it must require the exact Danbooru post identity.</summary>
    internal Func<string, bool>? IsPostSavedInMedia
    {
        get => _isPostSavedInMedia;
        set { _isPostSavedInMedia = value; Render(force: true); }
    }

    /// <summary>
    /// Puts a query into the active search tab and brings it forward. The field itself is set (not only the
    /// model), because a field that has not been laid out yet would otherwise report its initial empty text back and erase the
    /// query. Nothing is requested.
    /// </summary>
    internal void ShowQuery(string query)
    {
        if (_closed) return;
        _tabs.SelectedItem = ActiveSearchItem;
        _model.Search.SetMode(BooruInputMode.Tags);
        _input.Text = query;
        _model.Search.SetInput(query);
        _input.Focus();
        _input.CaretIndex = query.Length;
    }

    // ---- construction ------------------------------------------------------------------------------

    private Control BuildHeader()
    {
        StackPanel details = new() { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom, Children = { _title } };
        _searchHelp.Source = () => BuildSearchHelp(_model.Search.View);
        CommandBindings.EnsureIconBeforeText(_openWatchers, "eye");
        _openWatchers.Click += (_, _) => ShowWatchers();
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Top, Children = { _openWatchers, _more } };
        PageHeader.SetRole(actions, PageHeaderRole.Actions);
        PageHeader.SetRole(_searchHelp, PageHeaderRole.Help);
        PageHeader heading = new() { Spacing = 8, Children = { details, actions, _searchHelp } };
        _tabLimitHost.Child = _tabLimitText;
        AutomationProperties.SetLiveSetting(_tabLimitText, AutomationLiveSetting.Polite);
        return new StackPanel { Spacing = 4, Children = { heading, _tabLimitHost } };
    }

    private Control BuildBody()
    {
        _searchTab.Content = BuildSearchTab();
        _watchersTab.Content = BuildWatchersTab();
        _tabs.ItemsSource = CreateTabItems();
        return _tabs;
    }

    private Control BuildFooter()
    {
        AutomationProperties.SetLiveSetting(_linkNote, AutomationLiveSetting.Polite);
        return new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _linkNote } };
    }

    private Control BuildSearchTab()
    {
        _mode.SelectionChanged += (_, _) =>
        {
            if (_applying || _mode.SelectedItem is not ComboBoxItem { Tag: BooruInputMode mode }) return;
            _model.Search.SetMode(mode);
        };
        // SearchBox raises this synchronously, so closing can capture the latest keystroke.
        _input.QueryChanged += (_, _) =>
        {
            if (!_applying) _model.Search.SetInput(_input.Text);
        };
        _input.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Handled || ShortcutServiceCapturing()) return;
            EffectiveShortcutBindings bindings = ShortcutService.Current;
            bool alternate = MatchesSearchShortcut(e, AlternateSubmitCommand, bindings);
            if (!alternate && !MatchesSearchShortcut(e, SubmitCommand, bindings)) return;
            e.Handled = true;
            await SubmitSearchAsync(alternate);
        }, RoutingStrategies.Tunnel);
        _submit.Classes.Add("primary");
        _cancel.Classes.Add("subtle");
        _createWatcher.Classes.Add("subtle");
        ToolTip.SetShowOnDisabled(_createWatcher, true);
        CommandBindings.EnsureIconBeforeText(_submit, "search");
        CommandBindings.EnsureIconBeforeText(_createWatcher, "plus");
        CommandBindings.EnsureIconBeforeText(_cancel, CardMenuIcons.Cancel);
        CommandBindings.EnsureIconBeforeText(_retry, CardMenuIcons.Retry);
        _submit.Click += async (_, _) => await SubmitSearchAsync();
        _cancel.Click += (_, _) => _model.Search.Cancel();
        _retry.Click += async (_, _) => await _model.Search.RetryAsync();
        _previous.Click += async (_, _) => await ChangePageAsync(-1);
        _next.Click += async (_, _) => await ChangePageAsync(1);
        _createWatcher.Click += async (_, _) => await _model.CreateWatcherAsync();

        SearchToolbar searchToolbar = new(_mode, _input, _submit, BuildSearchTabActions());

        StackPanel status = new() { Spacing = 8 };
        status.Children.Add(_statusText);
        status.Children.Add(_retry);
        // The repair route's actions sit under the status text, left-aligned like "Create watcher"; they never stretch across the box.
        WireRepair(_searchRepair, status);
        _statusHost.Child = status;
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);

        _pager.Children.Add(_previous);
        Grid.SetColumn(_pageInfo, 1);
        _pager.Children.Add(_pageInfo);
        Grid.SetColumn(_next, 2);
        _pager.Children.Add(_next);

        _noticeHost.Child = _noticeText;
        AutomationProperties.SetLiveSetting(_noticeText, AutomationLiveSetting.Polite);

        StackPanel tab = new() { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        tab.Children.Add(searchToolbar);
        AutomationProperties.SetLiveSetting(_inputIssue, AutomationLiveSetting.Polite);
        tab.Children.Add(_inputIssue);
        tab.Children.Add(_noticeHost);
        tab.Children.Add(BuildBulkStatus());
        tab.Children.Add(_statusHost);
        tab.Children.Add(BuildResultViewBar()); // the list/tile switch and the tile size (BooruWorkbenchView.Grid.cs)
        Grid.SetIsSharedSizeScope(_results, true);
        _resultsArea.Children.Add(_results);
        _resultsArea.Children.Add(BuildBottomBar());
        _resultScroll.Content = _resultsArea;
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8, Children = { tab, _resultScroll } };
        Grid.SetRow(_resultScroll, 1);
        return layout;
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
        CommandBindings.EnsureIconBeforeText(_watchersReload, CardMenuIcons.Retry);
        CommandBindings.EnsureIconBeforeText(_resetCooldown, "skip-forward");
        _watchersReload.Click += async (_, _) => await _model.Watchers.RefreshAsync();
        _resetCooldown.Click += async (_, _) => await _model.Watchers.ResetProviderCooldownAsync();
        StackPanel tab = new() { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        tab.Children.Add(_watchersIntro);
        // The same repair route as on the Search tab: watcher checks fail the same way while the build mismatches.
        tab.Children.Add(BuildWatcherRepair());
        tab.Children.Add(_removeNoteHost);
        tab.Children.Add(_watcherNoticeHost);
        tab.Children.Add(_storageHost);
        tab.Children.Add(_resetCooldown);
        tab.Children.Add(_watchersState);
        tab.Children.Add(_watchersReload);
        tab.Children.Add(_watchers);
        return new ScrollViewer
        {
            Content = tab, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
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
        // Model notifications can arrive from background requests. Queue on this view's dispatcher.
        Dispatcher.Post(() =>
        {
            lock (this) _renderQueued = false;
            if (!_closed) Render(force: false);
        });
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.Post(() =>
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
            RenderChrome();
            RenderSearch(search, force);
            RenderWatchers(watchers, force);
            RenderSearchTabs(search, watchers);
            RefreshWorkViewPresentation();
        }
        finally { _applying = false; }
    }

    private void RenderChrome()
    {
        _title.Text = Texts.T("booru.ui.title");
        _searchTab.Header = Texts.T("booru.ui.tab.search");
        Footer.IsVisible = _linkNote.IsVisible = _linkNoteState is not null;
        _linkNote.Text = _linkNoteState is { } note ? Texts.T(note.Key, note.Argument) : string.Empty;
        _linkNote.Classes.Set("workspace-error", _linkNoteState is { Problem: true });
        ChromeChanged?.Invoke();
    }

    private void RenderSearch(BooruSearchView view, bool force)
    {
        BooruSearchModel model = _model.Search;
        bool paused = model.IsPaused;
        _paused = paused;

        // The closed combo caches its selected label, so replace its items when their translations change.
        string tagsLabel = Texts.T("booru.ui.mode.tags");
        string postLabel = Texts.T("booru.ui.mode.post");
        if (_mode.ItemsSource is not ComboBoxItem[] modes || modes[0].Content as string != tagsLabel || modes[1].Content as string != postLabel)
        {
            modes = [new ComboBoxItem { Tag = BooruInputMode.Tags, Content = tagsLabel },
                     new ComboBoxItem { Tag = BooruInputMode.Post, Content = postLabel }];
            _mode.ItemsSource = modes;
        }
        ComboBoxItem selected = modes[view.Mode == BooruInputMode.Post ? 1 : 0];
        if (!ReferenceEquals(_mode.SelectedItem, selected)) _mode.SelectedItem = selected;
        AutomationProperties.SetName(_mode, Texts.T("booru.ui.mode.label"));
        if (_input.Text != view.Input) _input.Text = view.Input;
        _input.PlaceholderText = Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.input.placeholder.post" : "booru.ui.input.placeholder.tags");
        AutomationProperties.SetName(_input, Texts.T("booru.ui.input.label"));
        bool invalid = view.Parsed.Kind == BooruInputKind.Invalid;
        _input.Classes.Set("workspace-invalid", invalid);
        string issue = invalid ? BooruText.Issue(view.Parsed.Issue) : string.Empty;
        _inputIssue.Text = issue;
        _inputIssue.IsVisible = invalid;

        RenderSearchActions(view);
        _cancel.Content = Texts.T("booru.ui.cancel");
        _cancel.IsVisible = view.IsBusy;

        string help = string.Join(Environment.NewLine, new[]
        {
            issue, ExecutedSearchText(view), DraftSearchText(view),
            view.Mode == BooruInputMode.Tags ? Texts.T("booru.ui.query.hint") : null,
            Texts.T("booru.ui.noFilterNote")
        }.Where(text => !string.IsNullOrEmpty(text)));
        ToolTip.SetTip(_input, help);
        AutomationProperties.SetHelpText(_input, help);
        _searchHelp.Refresh();

        string createName = Texts.T("booru.tabs.watchShort");
        _createWatcher.Content = createName;
        bool creatingWatcher = _model.Watchers.View.Creating;
        _createWatcher.IsEnabled = model.CanCreateWatcher && !creatingWatcher;
        string createHelp = Texts.T(creatingWatcher ? "booru.ui.watcher.creating"
            : view.Mode == BooruInputMode.Post ? "booru.ui.watcher.createNeedsTags"
            : model.CanCreateWatcher ? "booru.ui.watcher.createHint"
            : "booru.ui.watcher.createInvalidTags");
        AutomationProperties.SetName(_createWatcher, createName);
        AutomationProperties.SetHelpText(_createWatcher, createHelp);
        ToolTip.SetTip(_createWatcher, createHelp);

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
        // The work-view gate owns presentation, while this state remains the model's notice state.
        WorkViewElement.SetStateVisible(_noticeHost, _noticeHost.IsVisible);

        // Errors interrupt (as the dialog error strips do); progress and limits wait their turn. A running build that cannot
        // bind to this SDK replaces the plain failure (and the idle prompt) with its repair route; a confirmed repair is calm.
        bool repairShown = BooruText.SearchShowsRepair(view, _repair);
        BooruNoticeKind tone = repairShown ? RepairTone() : ToneOf(view.Status);
        AutomationProperties.SetLiveSetting(_statusText, tone == BooruNoticeKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        _statusText.Text = repairShown ? BooruText.RepairStatus(_repair) : BooruText.SearchStatus(view, paused);
        AutomationProperties.SetName(_statusText, _statusText.Text);
        ApplyTone(_statusHost, tone);
        _retry.Content = Texts.T("booru.ui.status.retry");
        // Searching again before the restart fails the same way, so the route replaces "Search again".
        _retry.IsVisible = view.CanRetry && !repairShown;
        _retry.IsEnabled = !paused;
        RenderRepairControls(_searchRepair, repairShown);

        bool paging = view.Status is BooruSearchStatus.Results or BooruSearchStatus.Empty && view.ExecutedQuery is not null && !view.IsPostResult;
        // Keep recovery navigation reachable even when the failed/cancelled page has no results.
        _pager.IsVisible = paging || view.HasPreviousPage;
        // The pager is icon-only like the Archive's: the chevron, with the text as tooltip and accessible name.
        IconButtons.Apply(_previous, "chevron-left", Texts.T("booru.ui.page.previous"));
        _previous.IsEnabled = view.HasPreviousPage && !paused;
        IconButtons.Apply(_next, "chevron-right", Texts.T("booru.ui.page.next"));
        _next.IsEnabled = view.HasNextPage && !paused;
        _pageInfo.Text = Texts.T("booru.ui.page.info", view.Page, view.PagerLimits.Pages, view.PagerLimits.PostsPerPage);
        _pageLimit.IsVisible = view.PageLimitReached;
        _pageLimit.Text = Texts.T("booru.ui.page.limit", view.PagerLimits.Pages, view.PagerLimits.PostsPerPage);
        RenderResultViewBar(view);

        string key = string.Join('\n', Texts.Language, view.Status, view.Page, view.ExecutedPostId, ListLayout, TagsShown(), string.Join('|',
            view.Rows.Select(row => $"{row.Post.PostId}:{row.Load}:{row.LoadCode}:{row.Preview}:{row.PreviewBytes?.Length}")));
        if (force || key != _resultsKey)
        {
            _resultsKey = key;
            _selectedResults.IntersectWith(view.Rows.Select(row => row.Post.PostId));
            _expandedTagPosts.IntersectWith(view.Rows.Select(row => row.Post.PostId));
            _resultWorkViewActions.Clear();
            DisposeResultTagMenus();
            RebuildResults(view);
            PruneBitmaps(view.Rows);
        }
    }

    private void RenderWatchers(BooruWatcherListView view, bool force)
    {
        BooruWatcherListModel model = _model.Watchers;
        _watchersIntro.Text = Texts.T("booru.ui.watchers.intro");
        RenderWatcherRepair();
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
        bool storageVisible = view.Storage is { NearLimit: true };
        WorkViewGate.Apply(_storageHost, _workView, storageVisible, _catalogUsageRevealed);
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
            _watcherWorkViewActions.Clear();
            RebuildPanel(_watchers, () => NumberHits(view.Rows.Select(CreateWatcherCard).ToList()), moveTo, () => _watchersTab, waitWhileDisabled: true);
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
        bool waitWhileDisabled = false, bool preserveFocus = true)
    {
        var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
        Visual? focused = preserveFocus ? focusManager?.GetFocusedElement() as Visual : null;
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
        IInputElement? focusAfterRebuild = focusManager?.GetFocusedElement();
        Dispatcher.Post(() =>
        {
            if (_closed || !ReferenceEquals(focusAfterRebuild, focusManager?.GetFocusedElement())) return;
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
        BooruRowLoad.Failed or BooruRowLoad.DownloadFailed or BooruRowLoad.LinkFailed or BooruRowLoad.RefusalPending
            => BooruNoticeKind.Error,
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

    private Button? AddResultCommands(BooruResultRowView row, Panel actions, bool compact)
    {
        BooruPostView post = row.Post;
        bool withdrawn = post.IsDeleted || post.IsBanned;
        bool retry = row.CanRetry;
        bool cancel = row.CanCancel && !retry;
        string actionText = cancel ? Texts.T("booru.ui.cancel")
            : retry ? BooruText.RetryOriginal(row) : Texts.T("booru.ui.load");
        string actionName = cancel || retry ? $"{actionText}: {Texts.T("booru.ui.post.title", post.PostId)}"
            : Texts.T("booru.ui.loadFor", post.PostId);
        string actionPrefix = cancel ? CancelLoadPrefix : retry ? RetryLoadPrefix : LoadPrefix;
        Button load = new() { Name = actionPrefix + post.PostId, Content = actionText, HorizontalAlignment = HorizontalAlignment.Left };
        load.Classes.Add("primary");
        string icon = cancel ? CardMenuIcons.Cancel : retry ? CardMenuIcons.Retry : CardMenuIcons.Load;
        if (compact) CompactAction(load, icon);
        else CommandBindings.ShowIconBeforeText(load, icon);
        RegisterWorkViewAction(load, cancel ? "booru.result.cancel" : retry ? "booru.result.retry" : "booru.result.load",
            _resultWorkViewActions);
        load.IsEnabled = cancel || retry || row.CanLoad;
        AutomationProperties.SetName(load, actionName);
        string? why = post.OriginalAccess != BooruOriginalAccess.Available ? BooruText.Original(post.OriginalAccess, withdrawn)
            : BooruText.Load(row);
        ToolTip.SetTip(load, why ?? (post.OriginalAccess == BooruOriginalAccess.Available && !withdrawn
            ? actionName + Environment.NewLine + BooruText.Original(post.OriginalAccess, withdrawn) : actionName));
        ToolTip.SetShowOnDisabled(load, true);
        if (why is not null) AutomationProperties.SetHelpText(load, why);
        if (cancel) load.Click += async (_, _) => await _model.Search.CancelOriginalAsync(post.PostId);
        else if (retry) load.Click += async (_, _) => await _model.Search.RetryOriginalAsync(post.PostId);
        else load.Click += async (_, _) => await _model.Search.LoadOriginalAsync(post.PostId);

        actions.Children.Add(load);
        if (retry && row.CanCancel)
        {
            Button cancelPending = new() { Name = CancelLoadPrefix + post.PostId, Content = Texts.T("booru.ui.cancel") };
            if (compact) CompactAction(cancelPending, CardMenuIcons.Cancel);
            else CommandBindings.EnsureIconBeforeText(cancelPending, CardMenuIcons.Cancel);
            AutomationProperties.SetName(cancelPending, $"{Texts.T("booru.ui.cancel")}: {Texts.T("booru.ui.post.title", post.PostId)}");
            ToolTip.SetTip(cancelPending, AutomationProperties.GetName(cancelPending));
            cancelPending.Click += async (_, _) => await _model.Search.CancelOriginalAsync(post.PostId);
            RegisterWorkViewAction(cancelPending, "booru.result.cancel", _resultWorkViewActions);
            actions.Children.Add(cancelPending);
        }
        if (BooruPostLinks.TryCanonical(post, out string canonical))
            foreach (Button link in CreateLinkButtons(OpenPostPrefix + post.PostId, CopyLinkPrefix + post.PostId, canonical,
                Texts.T("booru.ui.post.openFor", post.PostId), Texts.T("booru.ui.post.copyFor", post.PostId), _resultWorkViewActions, iconOnly: true))
            {
                if (compact) SizeCompactAction(link);
                actions.Children.Add(link);
            }

        Button? showInMedia = null;
        if (row.Load == BooruRowLoad.AlreadyLoaded && _showInMedia != null
            && (_isPostSavedInMedia == null || _isPostSavedInMedia(post.PostId)))
        {
            showInMedia = CreateShowInMediaAction(ShowInMediaPrefix + post.PostId,
                Texts.T("booru.ui.result.showInMediaFor", post.PostId), post.PostId);
            if (compact) CompactAction(showInMedia, CommandCatalog.Get("thread.show-in-media").Icon);
            actions.Children.Add(showInMedia);
        }
        return showInMedia;

    }

    private Button CreateShowInMediaAction(string name, string accessibleName, string postId)
    {
        Button show = new() { Name = name, Content = Texts.T("booru.ui.result.showInMedia") };
        CommandBindings.EnsureIconBeforeText(show, CommandCatalog.Get("thread.show-in-media").Icon);
        show.Classes.Add("ghost");
        AutomationProperties.SetName(show, accessibleName);
        ToolTip.SetTip(show, accessibleName);
        show.Click += async (_, _) =>
        {
            if (_showInMedia is not { } navigate || !TryBeginLinkAction(out long revision)) return;
            try
            {
                bool shown = await navigate(postId);
                if (shown || !IsCurrentLinkAction(revision)) return;
                SetLinkNote(("booru.crossLinks.missingLocal", postId, true), revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A host/profile transition may retire the navigation callback while it is awaited.
                // This is an action failure, not evidence that the local original is missing.
                Log.Warning(exception, "Could not show a Danbooru post in local media");
                SetLinkNote(("booru.crossLinks.navigationFailed", postId, true), revision);
            }
        };
        return show;
    }

    /// <summary>"Open in browser" and "Copy link" for one canonical post URL; nothing else is ever opened or copied.</summary>
    private Control CreateLinkActions(string openName, string copyName, string url, string openAccessibleName, string copyAccessibleName,
        ICollection<Button> workViewActions)
    {
        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        foreach (Button button in CreateLinkButtons(openName, copyName, url, openAccessibleName, copyAccessibleName, workViewActions))
            actions.Children.Add(button);
        return actions;
    }

    // The buttons without a panel of their own: a card that already has a toolbar adds them to it. Handing out the panel's children instead
    // would reparent controls that still belong to it, which Avalonia refuses ("already has a visual parent").
    private IReadOnlyList<Button> CreateLinkButtons(string openName, string copyName, string url, string openAccessibleName, string copyAccessibleName,
        ICollection<Button> workViewActions, bool iconOnly = false)
    {
        // iconOnly (E2, the result list): an icon button; the tooltip says what it does, the automation name names the post.
        Button open = new() { Name = openName };
        if (!iconOnly) open.Content = Texts.T("booru.ui.post.open");
        open.Classes.Add("ghost");
        if (iconOnly) IconButtons.Apply(open, "external-link", openAccessibleName);
        else CommandBindings.ShowIconBeforeText(open, "external-link");
        RegisterWorkViewAction(open, "booru.result.open", workViewActions);
        AutomationProperties.SetName(open, openAccessibleName);
        ToolTip.SetTip(open, Texts.T("booru.ui.post.openTip", url));
        open.Click += async (_, _) => await OpenLinkAsync(url);
        Button copy = new() { Name = copyName };
        if (!iconOnly) copy.Content = Texts.T("booru.ui.post.copy");
        copy.Classes.Add("ghost");
        if (iconOnly) IconButtons.Apply(copy, "copy", copyAccessibleName);
        else CommandBindings.ShowIconBeforeText(copy, "copy");
        RegisterWorkViewAction(copy, "booru.result.copy-link", workViewActions);
        AutomationProperties.SetName(copy, copyAccessibleName);
        ToolTip.SetTip(copy, iconOnly ? Texts.T("booru.ui.post.copy") + Environment.NewLine + url : url);
        copy.Click += async (_, _) => await CopyLinkAsync(url);
        return new[] { open, copy };
    }

    private async Task OpenLinkAsync(string url)
    {
        // Only canonical post pages reach this method (built by BooruPostLinks); a launcher failure is shown, never retried.
        if (!TryBeginLinkAction(out long revision)) return;
        try
        {
            await _model.Services.Links.Open(url);
            SetLinkNote(null, revision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(exception, "Could not open a Danbooru post in the browser");
            SetLinkNote(("booru.ui.link.openFailed", string.Empty, true), revision);
        }
    }

    private async Task CopyLinkAsync(string url)
    {
        if (!TryBeginLinkAction(out long revision)) return;
        try
        {
            if (_model.Services.Links.Copy is { } copy) await copy(url);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
            else throw new InvalidOperationException("The clipboard is unavailable.");
            SetLinkNote(("booru.ui.link.copied", url, false), revision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(exception, "Could not copy a Danbooru post link");
            SetLinkNote(("booru.ui.link.copyFailed", string.Empty, true), revision);
        }
    }

    private bool TryBeginLinkAction(out long revision)
    {
        if (_closed)
        {
            revision = 0;
            return false;
        }
        revision = Interlocked.Increment(ref _linkActionRevision);
        return true;
    }

    private bool IsCurrentLinkAction(long revision) =>
        !_closed && Volatile.Read(ref _linkActionRevision) == revision;

    /// <summary>
    /// Retires only the current host navigation's notice continuation. User takeover is not a
    /// missing original, and an old cancellation must never invalidate a newer link action.
    /// </summary>
    internal Action CaptureLinkActionCancellation()
    {
        long revision = Volatile.Read(ref _linkActionRevision);
        return () => Interlocked.CompareExchange(ref _linkActionRevision, unchecked(revision + 1), revision);
    }

    private void SetLinkNote((string Key, string Argument, bool Problem)? state)
    {
        if (!TryBeginLinkAction(out long revision)) return;
        SetLinkNote(state, revision);
    }

    private void SetLinkNote((string Key, string Argument, bool Problem)? state, long revision)
    {
        if (!IsCurrentLinkAction(revision)) return;
        _linkNoteState = state;
        _applying = true;
        try { RenderChrome(); }
        finally { _applying = false; }
    }

    private Border CreatePreview(BooruResultRowView row, double size, int decodeWidth = 0)
    {
        Border frame = new()
        {
            Name = "BooruPreviewFrame:" + row.Post.PostId,
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true,
            CornerRadius = new CornerRadius(5)
        };
        if (row.Preview == BooruPreviewState.Ready && PreviewBitmap(row, decodeWidth) is { } bitmap)
        {
            // D3: the thumbnail is flat on the card (no box, no edge of its own); only the placeholder has a quiet slot.
            // Decorative next to the card's title line; not a second "Post #n".
            // Never enlarged beyond its own pixels: a small thumbnail stays small (and sharp) in a big frame.
            Image image = new() { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
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
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8)
            };
            AutomationProperties.SetAccessibilityView(placeholder, AccessibilityView.Raw);
            frame.Child = placeholder;
        }
        return frame;
    }

    /// <summary>
    /// I7: fixed corners on the result preview, from the post data already shown (no request): the type (GIF, video) at the top
    /// right and a "deleted"/"banned" mark at the bottom left. Static borders over the preview; the card keeps its size.
    /// </summary>
    private static Control WithPreviewCorners(Control preview, BooruPostView post, double size)
    {
        string? kind = post.Original?.Extension?.TrimStart('.').ToLowerInvariant() switch
        {
            "gif" => Texts.T("media.tile.kind.gif"),
            "mp4" or "webm" or "zip" => Texts.T("media.tile.kind.video"),
            _ => null
        };
        string? withdrawn = post.IsDeleted ? Texts.T("booru.post.deleted") : post.IsBanned ? Texts.T("booru.post.banned") : null;
        if (kind == null && withdrawn == null) return preview;
        Panel corners = new() { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top, Children = { preview } };
        Border badge = WorkspaceChips.CreateCornerBadge("BooruResultKind:" + post.PostId, HorizontalAlignment.Right, VerticalAlignment.Top);
        badge.Margin = new Thickness(0, 4, 4, 0);
        badge.IsVisible = kind != null;
        ((TextBlock)badge.Child!).Text = kind;
        corners.Children.Add(badge);
        Border mark = WorkspaceChips.CreateCornerMark("BooruResultWithdrawn:" + post.PostId, HorizontalAlignment.Left, VerticalAlignment.Bottom);
        mark.Margin = new Thickness(4, 0, 0, 4);
        WorkspaceChips.SetCornerMark(mark, withdrawn == null ? null : WorkspaceChips.MarkWithdrawn, withdrawn);
        corners.Children.Add(mark);
        return corners;
    }

    private static string Size(long bytes) => SizeText.Format(bytes);

    /// <summary>
    /// I8: a source tag chip of a result adds its tag to the search field (tag search) up to the parser's limit. Only the field
    /// changes; nothing is requested until Search. The outcome is said in the polite link note line.
    /// </summary>
    internal void AddTagToSearch(string tag, bool exclude = false)
    {
        if (_closed) return;
        string current = _model.Search.View.Mode == BooruInputMode.Tags ? _input.Text ?? string.Empty : string.Empty;
        string[] terms = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // An exclusion is the provider's "-tag" term. It replaces the same tag's inclusion (and the other way round): both would never match anything.
        string term = exclude ? "-" + tag : tag, opposite = exclude ? tag : "-" + tag;
        if (terms.Contains(term, StringComparer.Ordinal)) { SetLinkNote(("booru.ui.tags.alreadyInSearch", term, false)); return; }
        if (!BooruSourceTagSearch.Plan(new[] { tag }).IsValid) { SetLinkNote(("booru.ui.tags.unusable", tag, true)); return; }
        bool replaces = terms.Contains(opposite, StringComparer.Ordinal);
        if (!replaces && terms.Length >= BooruSourceTagSearch.MaximumTags)
        {
            SetLinkNote(("booru.ui.tags.searchFull", BooruSourceTagSearch.MaximumTags.ToString(Texts.Culture), true));
            return;
        }
        string query = string.Join(' ', replaces ? terms.Select(existing => existing == opposite ? term : existing) : terms.Append(term));
        // The parser has the last word (an exclusion alone is not a search of its own on every provider).
        if (exclude && BooruInput.Parse(BooruInputMode.Tags, query).Kind != BooruInputKind.Tags) { SetLinkNote(("booru.ui.tags.unusable", term, true)); return; }
        _model.Search.SetMode(BooruInputMode.Tags);
        _input.Text = query;
        _model.Search.SetInput(query);
        SetLinkNote(("booru.ui.tags.addedToSearch", term, false));
    }

    /// <summary>The menu of a source-tag chip: add it to the search, exclude it from the search, copy its name.</summary>
    internal ContextMenuBuilder? ComposeTagMenu(string tag)
    {
        if (_closed) return null;
        return new ContextMenuBuilder().Named(tag)
            .Item(MenuTagAdd, Texts.T("booru.ui.tags.menuAdd"), () => AddTagToSearch(tag), icon: "plus")
            .Item(MenuTagExclude, Texts.T("booru.ui.tags.menuExclude"), () => AddTagToSearch(tag, exclude: true), icon: "minus")
            .Section()
            .Command("tags.copy-name", () => _ = CopyTagNameAsync(tag));
    }

    private async Task CopyTagNameAsync(string tag)
    {
        if (!TryBeginLinkAction(out long revision)) return;
        if (await MenuClipboard.CopyAsync(this, tag, _ => SetLinkNote(("booru.ui.link.copyFailed", string.Empty, true), revision)))
            SetLinkNote(("booru.ui.tags.copied", tag, false), revision);
    }

    private void DisposeResultTagMenus()
    {
        foreach (ContextMenuHost menu in _resultTagMenus) menu.Dispose();
        _resultTagMenus.Clear();
    }

    private Bitmap? PreviewBitmap(BooruResultRowView row, int decodeWidth)
    {
        if (row.PreviewBytes is not { Length: > 0 } bytes || bytes.Length > MaximumPreviewBytes) return null;
        var key = (row.Post.PostId, decodeWidth);
        if (_bitmaps.TryGetValue(key, out CachedBitmap? cached) && ReferenceEquals(cached.Bytes, bytes)) return cached.Bitmap;
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using SKManagedStream managedStream = new(stream, disposeManagedStream: false);
            using SKCodec? codec = SKCodec.Create(managedStream);
            if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > 40_000_000) return null;
            stream.Position = 0;
            PixelSize display = ImageOrientation.DisplaySize(codec);
            int width = decodeWidth == 0 ? Math.Min(display.Width, PreviewDecodeWidth)
                : Math.Max(1, (int)Math.Ceiling(display.Width * Math.Min(1.0, (double)decodeWidth / Math.Max(display.Width, display.Height))));
            Bitmap bitmap = ImageOrientation.DecodeToWidth(stream, width);
            if (cached is not null) cached.Bitmap.Dispose();
            _bitmaps[key] = new CachedBitmap(bytes, bitmap);
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
        foreach (var key in _bitmaps.Keys.Where(key => !keep.Contains(key.PostId) || !UsedDecodeWidth(key.DecodeWidth)).ToArray())
        {
            // The old cards are already detached by the rebuild, so the image can be released.
            _bitmaps[key].Bitmap.Dispose();
            _bitmaps.Remove(key);
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
        if (watcher.LatestPostId is { } latest)
        {
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.latest", latest), Classes = { "workspace-meta" } });
            if (_showInMedia != null && _isPostSavedInMedia?.Invoke(latest) == true)
                panel.Children.Add(CreateShowInMediaAction(WatcherShowInMediaPrefix + watcher.WatcherId,
                    Texts.T("booru.ui.watcher.showInMediaFor", latest, watcher.Query), latest));
            if (BooruPostLinks.TryCanonical(latest, out string latestUrl))
                panel.Children.Add(CreateLinkActions(WatcherOpenPrefix + watcher.WatcherId, WatcherCopyPrefix + watcher.WatcherId, latestUrl,
                    Texts.T("booru.ui.watcher.latestOpenFor", latest, watcher.Query), Texts.T("booru.ui.watcher.latestCopyFor", latest, watcher.Query), _watcherWorkViewActions));
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
        Button check = WatcherButton(CheckPrefix + watcher.WatcherId, "booru.ui.watcher.checkNow", "booru.ui.watcher.checkNowFor",
            watcher.Query, checkBlock, cooling, async () => await model.CheckNowAsync(watcher.WatcherId), CommandCatalog.Get("watch.check").Icon);
        RegisterWorkViewAction(check, "booru.watcher.check-now", _watcherWorkViewActions);
        actions.Children.Add(check);
        if (watcher.Status == BooruWatcherStatus.Paused)
        {
            Button resume = WatcherButton(ResumePrefix + watcher.WatcherId, "booru.ui.watcher.resume", "booru.ui.watcher.resumeFor",
                watcher.Query, model.ResumeBlock(row), cooling, async () => await model.ResumeAsync(watcher.WatcherId), CardMenuIcons.Resume, ghost: true);
            RegisterWorkViewAction(resume, "booru.watcher.pause", _watcherWorkViewActions);
            actions.Children.Add(resume);
        }
        else
        {
            Button pause = WatcherButton(PausePrefix + watcher.WatcherId, "booru.ui.watcher.pause", "booru.ui.watcher.pauseFor",
                watcher.Query, model.PauseBlock(row), cooling, async () => await model.PauseAsync(watcher.WatcherId), CardMenuIcons.Pause, ghost: true);
            RegisterWorkViewAction(pause, "booru.watcher.pause", _watcherWorkViewActions);
            actions.Children.Add(pause);
        }
        string removeIcon = CommandCatalog.Get("watch.remove").Icon;
        Button remove = WatcherButton(RemovePrefix + watcher.WatcherId, "booru.ui.watcher.remove", "booru.ui.watcher.removeFor",
            watcher.Query, model.RemoveBlock(row), cooling, () => { model.RequestRemoval(watcher.WatcherId); return Task.CompletedTask; }, removeIcon);
        remove.Classes.Add("danger");
        RegisterWorkViewAction(remove, "booru.watcher.remove", _watcherWorkViewActions);
        actions.Children.Add(remove);
        if (BooruWatcherListModel.CanRestartFromNow(watcher))
        {
            Button restart = WatcherButton(RestartPrefix + watcher.WatcherId, "booru.ui.watcher.restart", "booru.ui.watcher.restartFor",
                watcher.Query, model.RestartBlock(row), cooling, async () => await model.RestartFromNowAsync(watcher.WatcherId), CardMenuIcons.Restart, ghost: true);
            RegisterWorkViewAction(restart, "booru.watcher.restart", _watcherWorkViewActions);
            actions.Children.Add(restart);
        }
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
            CommandBindings.EnsureIconBeforeText(no, "x");
            no.Click += (_, _) => model.CancelRemoval(watcher.WatcherId);
            Button yes = new() { Name = RemoveConfirmPrefix + watcher.WatcherId, Content = Texts.T("booru.ui.watcher.removeConfirm") };
            CommandBindings.EnsureIconBeforeText(yes, removeIcon);
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
        AttachCardMenu(card, () => ComposeWatcherMenu(card, watcher.WatcherId, watcher.Query));
        ConfigureWatcherCard(card, watcher.Query);
        return card;
    }

    private static Button WatcherButton(string name, string labelKey, string nameKey, string query, BooruWatcherBlock block,
        DateTimeOffset? cooling, Func<Task> action, string icon, bool ghost = false)
    {
        Button button = new() { Name = name, Content = Texts.T(labelKey), IsEnabled = block == BooruWatcherBlock.None };
        CommandBindings.EnsureIconBeforeText(button, icon);
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
        DismissResultViewer();
        _gallery.Dispose();
        Interlocked.Increment(ref _linkActionRevision);
        _clock.Stop();
        _clock.Tick -= OnClockTick;
        _keyContextHost.Dispose();
        DisposeResultTagMenus();
        ChromeChanged = null;
        ReviewCurrentBuild = null;
        ReviewUnavailableReason = null;
        OpenPluginDetails = null;
        Texts.Changed -= OnTextsChanged;
        ShortcutService.Changed -= OnModelChanged;
        _model.Search.Changed -= OnModelChanged;
        _model.Watchers.Changed -= OnModelChanged;
        foreach (CachedBitmap cached in _bitmaps.Values) cached.Bitmap.Dispose();
        _bitmaps.Clear();
        if (_ownsModel) _model.Dispose();
    }
}
