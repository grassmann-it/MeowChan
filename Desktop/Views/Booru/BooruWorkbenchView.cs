using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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


    private readonly BooruWorkbenchModel _model;
    private readonly bool _ownsModel;
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
    private readonly Button _retry = new() { Name = RetryName, HorizontalAlignment = HorizontalAlignment.Left };
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
        MeowChan.Desktop.Controls.KeyContextHost.Install(this, _ => "booru");
        Header = BuildHeader();
        Body = BuildBody();
        Footer = BuildFooter();
        Content = buildLayout is null ? BuildLayout(Header, Body, Footer) : buildLayout(Header, Body, Footer);

        _model.Search.Changed += OnModelChanged;
        _model.Watchers.Changed += OnModelChanged;
        Texts.Changed += OnTextsChanged;
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

    /// <summary>Passive local predicate used for watcher cards; it must require the exact Danbooru post identity.</summary>
    internal Func<string, bool>? IsPostSavedInMedia
    {
        get => _isPostSavedInMedia;
        set { _isPostSavedInMedia = value; Render(force: true); }
    }

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
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        StackPanel details = new() { Spacing = 4 };
        details.Children.Add(_title);
        details.Children.Add(_provider);
        details.Children.Add(_intro);
        header.Children.Add(details);
        Grid.SetColumn(_more, 1);
        header.Children.Add(_more);
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
            RefreshWorkViewPresentation();
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

    // ---- lifetime ----------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Interlocked.Increment(ref _linkActionRevision);
        _clock.Stop();
        _clock.Tick -= OnClockTick;
        ChromeChanged = null;
        ReviewCurrentBuild = null;
        ReviewUnavailableReason = null;
        OpenPluginDetails = null;
        Texts.Changed -= OnTextsChanged;
        _model.Search.Changed -= OnModelChanged;
        _model.Watchers.Changed -= OnModelChanged;
        _tiles.Clear();
        DisposePreviews();
        if (_ownsModel) _model.Dispose();
    }
}
