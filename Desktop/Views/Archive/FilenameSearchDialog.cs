using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Pure request coordination, no UI or search predicate. Each completed page replaces count+rows
// together. Edits, cancellation, supersession and close invalidate both results and selection.
internal sealed class FilenameSearchSession
{
    internal const int PageSize = 100;
    internal sealed record View(string Text, bool Busy, string StatusKey, int Offset,
        long Total, ImmutableArray<ArchiveFilenameHit> Rows, bool Closed)
    {
        internal bool CanPrevious => !Busy && StatusKey == "filename.search.ready" && Offset > 0;
        internal bool CanNext => !Busy && StatusKey == "filename.search.ready" &&
            Rows.Length > 0 && Offset <= int.MaxValue - PageSize && (long)Offset + Rows.Length < Total;
    }
    private readonly object _gate = new();
    private readonly Func<ArchiveFilenameSearch, int, int, CancellationToken, Task<ArchiveFilenamePage>> _search;
    private readonly HashSet<Task> _pending = new();
    private CancellationTokenSource? _request;
    private long _generation;
    private View _view = new("", false, "filename.search.idle", 0, 0, ImmutableArray<ArchiveFilenameHit>.Empty, false);
    internal View Current { get { lock (_gate) return _view; } }
    internal event Action? Changed;

    internal FilenameSearchSession(Func<CancellationToken, Task<ArchiveReader>> readerFactory)
        : this(async (query, offset, limit, token) =>
        {
            // Factory transfers ownership of a NEW passive reader per request to this operation.
            await using ArchiveReader reader = await readerFactory(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await reader.SearchFilenamesAsync(query, offset, limit, token).ConfigureAwait(false);
        }) { ArgumentNullException.ThrowIfNull(readerFactory); }

    // Synthetic pure seam. Production constructor above is the only catalog operation.
    internal FilenameSearchSession(Func<ArchiveFilenameSearch, int, int, CancellationToken, Task<ArchiveFilenamePage>> search)
    { _search = search ?? throw new ArgumentNullException(nameof(search)); }

    internal void Edit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        CancellationTokenSource? old;
        lock (_gate)
        {
            if (_view.Closed || _view.Text == text) return;
            old = Invalidate();
            _view = Empty(text, "filename.search.idle");
        }
        Cancel(old); Changed?.Invoke();
    }
    private static View Empty(string text, string status, bool closed = false) =>
        new(text, false, status, 0, 0, ImmutableArray<ArchiveFilenameHit>.Empty, closed);
    private CancellationTokenSource? Invalidate()
    { _generation++; var old = _request; _request = null; return old; }
    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { } // finishing request already released its own source
    }
    internal Task SearchAsync() => Start(0, pageMove: false);
    internal Task PreviousAsync() => Start(-PageSize, pageMove: true);
    internal Task NextAsync() => Start(PageSize, pageMove: true);
    private Task Start(int offsetOrDelta, bool pageMove)
    {
        CancellationTokenSource? old;
        CancellationTokenSource? request = null;
        ArchiveFilenameSearch? query = null;
        long generation = 0;
        int offset = 0;
        Task task = Task.CompletedTask;
        lock (_gate)
        {
            if (_view.Closed) return Task.CompletedTask;
            if (pageMove && (offsetOrDelta < 0 ? !_view.CanPrevious : !_view.CanNext)) return Task.CompletedTask;
            offset = pageMove ? Math.Max(0, _view.Offset + offsetOrDelta) : 0;
            old = Invalidate();
            try { query = new ArchiveFilenameSearch(_view.Text); }
            catch (ArgumentException) { _view = Empty(_view.Text, "filename.search.invalid"); }
            if (query != null)
            {
                request = _request = new CancellationTokenSource();
                generation = _generation;
                _view = Empty(_view.Text, "filename.search.reading") with { Busy = true, Offset = offset };
                var capturedQuery = query;
                var capturedRequest = request;
                var token = request.Token;
                // Register before releasing the gate: Close cannot miss a starting operation.
                task = Task.Run(() => RunAsync(capturedQuery, offset, generation, capturedRequest, token));
                _pending.Add(task);
            }
        }
        Cancel(old); Changed?.Invoke();
        _ = task.ContinueWith(done => { lock (_gate) _pending.Remove(done); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    private async Task RunAsync(ArchiveFilenameSearch query, int offset, long generation,
        CancellationTokenSource request, CancellationToken token)
    {
        ArchiveFilenamePage? page = null;
        string status = "filename.search.ready";
        try
        {
            token.ThrowIfCancellationRequested();
            page = await _search(query, offset, PageSize, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Defensive bounds for the synthetic seam too. Do not recompute count or matching.
            if (page.Offset != offset || page.TotalCount < 0 || page.Items.Count > PageSize)
                throw new InvalidOperationException();
        }
        catch (OperationCanceledException) { status = "filename.search.canceled"; }
        catch (Exception) { status = "filename.search.failed"; } // no provider/path/exception details
        bool publish = false;
        lock (_gate)
        {
            if (!_view.Closed && generation == _generation && ReferenceEquals(_request, request) && !token.IsCancellationRequested)
            {
                _request = null;
                _view = status == "filename.search.ready" && page != null
                    ? new(query.Text, false, status, page.Offset, page.TotalCount, page.Items.ToImmutableArray(), false)
                    : Empty(query.Text, status);
                publish = true;
            }
        }
        request.Dispose();
        if (publish) Changed?.Invoke();
    }
    internal void CancelSearch()
    {
        CancellationTokenSource? old;
        lock (_gate)
        {
            if (_view.Closed) return;
            old = Invalidate(); _view = Empty(_view.Text, "filename.search.canceled");
        }
        Cancel(old); Changed?.Invoke();
    }
    internal bool CanReturn(ArchiveFilenameHit? hit)
    {
        lock (_gate) return hit != null && !_view.Closed && !_view.Busy &&
            _view.StatusKey == "filename.search.ready" && _view.Rows.Any(row => ReferenceEquals(row, hit));
    }
    internal Task CloseAsync()
    {
        CancellationTokenSource? old;
        Task[] pending;
        lock (_gate)
        {
            old = Invalidate(); _view = Empty(_view.Text, "filename.search.canceled", closed: true);
            pending = _pending.ToArray();
        }
        Cancel(old); return Task.WhenAll(pending);
    }
}

internal sealed class FilenameSearchDialog : Window
{
    private sealed record Row(ArchiveFilenameHit Hit, string Label) { public override string ToString() => Label; }
    private readonly FilenameSearchSession _session;
    private readonly WindowChromeBar _chrome = new() { Name = "FilenameSearchChrome" };
    private readonly TextBox _query = new() { Name = "FilenameSearchQuery" };
    private readonly TextBlock _label = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _scope = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "FilenameSearchStatus", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _results = new() { Name = "FilenameSearchResults" };
    private readonly Button _search = new() { Name = "FilenameSearchSubmit" };
    private readonly Button _cancel = new() { Name = "FilenameSearchCancel" };
    private readonly Button _previous = new() { Name = "FilenameSearchPrevious" };
    private readonly Button _next = new() { Name = "FilenameSearchNext" };
    private readonly Button _select = new() { Name = "FilenameSearchSelect" };
    private readonly Button _close = new() { Name = "FilenameSearchClose" };
    private bool _terminated;
    internal FilenameSearchSession Session => _session;
    // Await ShowDialog<ArchiveFilenameHit?>(owner). Explicit selection returns the SAME metadata hit.
    internal FilenameSearchDialog(Func<CancellationToken, Task<ArchiveReader>> readerFactory)
    {
        _session = new(readerFactory);
        Name = "FilenameSearchDialog";
        Width = 820; Height = 680; MinWidth = 360; MinHeight = 380;
        DialogFit.OnOpen(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.Apply(this);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        // UX103 P7: the draft, its actions and the request status stay in the fixed header;
        // results scroll in the body (the list keeps its own virtualized scrolling, bounded
        // to the body viewport), the scope note follows below, and paging/return stay fixed.
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(_label); header.Children.Add(_query);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(_search); actions.Children.Add(_cancel); header.Children.Add(actions); header.Children.Add(_status);
        var footer = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _previous, _next, _select, _close }) footer.Children.Add(button);
        _results.ItemTemplate = new FuncDataTemplate<Row>((row, _) => new TextBlock
        { Text = row?.Label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(_results); body.Children.Add(_scope);
        var frame = DialogFrame.Build(header, body, footer);
        var scroll = frame.Children.OfType<ScrollViewer>().Single(child => child.Name == DialogFrame.BodyName);
        scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                _results.MaxHeight = Math.Max(0, scroll.Viewport.Height - body.Margin.Top - body.Margin.Bottom);
        };
        // The caption bar sits outside the frame; the window keeps cycling Tab through both.
        KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Continue);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(_chrome); layout.Children.Add(frame); Grid.SetRow(frame, 1); Content = layout;
        _query.PropertyChanged += OnQueryChanged;
        _query.AddHandler(KeyDownEvent, OnQueryKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _search.Click += OnSearch; _cancel.Click += OnCancel;
        _previous.Click += OnPrevious; _next.Click += OnNext; _select.Click += OnSelect; _close.Click += OnClose;
        _results.SelectionChanged += OnSelection;
        Opened += OnOpened; Closed += OnClosed;
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _session.Changed += OnChanged; Texts.Changed += OnLanguageChanged;
        Refresh();
    }
    private void OnOpened(object? sender, EventArgs e) { _chrome.Attach(this); _query.Focus(); }
    private void OnQueryChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // TextChanged may be deferred. Revoke old rows/selection and cancel the current
        // request at the Text mutation, before another action can return a stale hit.
        if (e.Property != TextBox.TextProperty) return;
        _session.Edit(_query.Text ?? "");
        Refresh();
    }
    private void OnChanged() => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);
    private void OnSelection(object? sender, SelectionChangedEventArgs e) => _select.IsEnabled = _session.CanReturn((_results.SelectedItem as Row)?.Hit);
    private async void OnSearch(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _session.SearchAsync();
    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _session.CancelSearch();
    private async void OnPrevious(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _session.PreviousAsync();
    private async void OnNext(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _session.NextAsync();
    private void OnSelect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var hit = (_results.SelectedItem as Row)?.Hit;
        if (_session.CanReturn(hit)) Close(hit);
    }
    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
    private async void OnQueryKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        { e.Handled = true; await _session.SearchAsync(); }
    }
    private void OnKey(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
    internal static string DescribeHit(ArchiveFilenameHit hit, Func<string, object?[], string> text) =>
        text("filename.search.row", new object?[] { hit.OriginalName, hit.Thread.Source, hit.Thread.Board,
            hit.Thread.Thread, hit.Post, hit.Variant, hit.Position,
            hit.AttachmentRevisionKey ?? text("filename.search.unknown", Array.Empty<object?>()), hit.CatalogThreadRevision,
            text(hit.PostIsDeleted ? "filename.search.deleted" : "filename.search.notDeleted", Array.Empty<object?>()),
            text(hit.AttachmentIsDeleted ? "filename.search.deleted" : "filename.search.notDeleted", Array.Empty<object?>()) });
    private void Refresh()
    {
        if (_terminated) return;
        var view = _session.Current;
        Title = FilenameSearchCatalog.T("filename.search.title");
        _label.Text = FilenameSearchCatalog.T("filename.search.query");
        _scope.Text = FilenameSearchCatalog.T("filename.search.scope");
        AutomationProperties.SetName(_query, _label.Text);
        AutomationProperties.SetName(_results, FilenameSearchCatalog.T("filename.search.results"));
        void Label(Button button, string key)
        { button.Content = FilenameSearchCatalog.T("filename.search." + key); AutomationProperties.SetName(button, (string)button.Content); }
        Label(_search, "search"); Label(_cancel, "cancel"); Label(_previous, "previous"); Label(_next, "next"); Label(_select, "select"); Label(_close, "close");
        _cancel.IsEnabled = view.Busy; _previous.IsEnabled = view.CanPrevious; _next.IsEnabled = view.CanNext;
        _status.Text = view.StatusKey == "filename.search.ready"
            ? FilenameSearchCatalog.T(view.StatusKey, view.Total, view.Rows.Length, view.Offset) : FilenameSearchCatalog.T(view.StatusKey);
        var selected = (_results.SelectedItem as Row)?.Hit;
        var rows = view.Rows.Select(hit => new Row(hit, DescribeHit(hit, (key, args) => FilenameSearchCatalog.T(key, args)))).ToArray();
        _results.ItemsSource = rows;
        _results.SelectedItem = rows.FirstOrDefault(row => ReferenceEquals(row.Hit, selected));
        _select.IsEnabled = _session.CanReturn((_results.SelectedItem as Row)?.Hit);
    }
    private void OnClosed(object? sender, EventArgs e) => _ = TerminateAsync();
    internal Task TerminateAsync()
    {
        if (!_terminated)
        {
            _terminated = true;
            _session.Changed -= OnChanged; Texts.Changed -= OnLanguageChanged;
            _query.PropertyChanged -= OnQueryChanged; _query.RemoveHandler(KeyDownEvent, OnQueryKey);
            _search.Click -= OnSearch; _cancel.Click -= OnCancel; _previous.Click -= OnPrevious; _next.Click -= OnNext;
            _select.Click -= OnSelect; _close.Click -= OnClose; _results.SelectionChanged -= OnSelection;
            Opened -= OnOpened; Closed -= OnClosed; RemoveHandler(KeyDownEvent, OnKey);
        }
        return _session.CloseAsync();
    }
}
