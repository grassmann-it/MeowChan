using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;

namespace MeowChan;

// Captured metadata only. Never opens MediaAssetSnapshot.Directory or asset paths.
// Production constructs this with one immutable snapshot and the existing hash query.
internal sealed class MediaHashSearchOperation
{
    internal const int PageSize = 100;
    internal enum Status { Ready, Invalid, Searching, Available, NoMeasurements, Missing, Corrupt, Failed, Canceled, Closed }
    internal sealed record View(long Request, Status Status, string? Generation, int Offset,
        int Count, ImmutableArray<MediaAssetReference> Page);
    internal sealed record WorkResult(MediaSnapshotHashSearchResult Result, bool HasMeasurements);
    private readonly object _gate = new();
    private readonly MediaAssetSnapshot _snapshot;
    private readonly CancellationToken _parent;
    private readonly CancellationTokenRegistration _parentRegistration;
    private readonly Func<MediaSnapshotHashQuery, CancellationToken, Task<WorkResult>> _execute;
    private readonly List<Task> _runs = new();
    private CancellationTokenSource? _requestCancellation;
    private MediaSnapshotHashSearchResult? _result;
    private long _request;
    private bool _closed;
    private Task? _termination;
    private View _view;
    internal event Action? Changed;
    internal View Current { get { lock (_gate) return _view; } }

    internal MediaHashSearchOperation(MediaAssetSnapshot snapshot, CancellationToken parent = default,
        Func<MediaSnapshotHashQuery, CancellationToken, Task<WorkResult>>? executeForTests = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _parent = parent;
        _execute = executeForTests ?? ExecuteAsync;
        _view = Empty(Status.Ready);
        _parentRegistration = parent.Register(OnParentCanceled);
    }
    private void OnParentCanceled()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_closed) return;
            ++_request;
            cancellation = _requestCancellation;
            _requestCancellation = null;
            _result = null;
            _view = Empty(Status.Canceled);
            Changed?.Invoke();
        }
        Cancel(cancellation);
    }
    private View Empty(Status status) => new(_request, status, _snapshot.Generation, 0, 0,
        ImmutableArray<MediaAssetReference>.Empty);

    internal static bool IsExactSha256(string? value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }
    private Task<WorkResult> ExecuteAsync(MediaSnapshotHashQuery query, CancellationToken token) => Task.Run(() =>
    {
        MediaSnapshotHashSearchResult result = query.Search(token);
        bool measured = false;
        if (result.Status == MediaSnapshotHashSearchStatus.Available)
        {
            // Distinguish absent metadata measurements from a measured inventory with no match.
            for (int offset = 0; offset < _snapshot.Count && !measured;)
            {
                token.ThrowIfCancellationRequested();
                foreach (var reference in _snapshot.GetPage(offset, PageSize))
                {
                    token.ThrowIfCancellationRequested();
                    if (IsExactSha256(reference.Sha256)) { measured = true; break; }
                }
                offset += Math.Min(PageSize, _snapshot.Count - offset);
            }
        }
        token.ThrowIfCancellationRequested();
        return new WorkResult(result, measured);
    }, token);

    // Invalidation is not a search. Editing the input immediately revokes old selection authority.
    internal void Invalidate()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_closed) return;
            _request++;
            cancel = _requestCancellation;
            _requestCancellation = null;
            _result = null;
            _view = Empty(_parent.IsCancellationRequested ? Status.Canceled : Status.Ready);
            Changed?.Invoke(); // UI subscriber only posts a refresh; never waits for the UI thread.
        }
        Cancel(cancel);
    }
    internal Task SearchAsync(string? input)
    {
        CancellationTokenSource? old;
        Task run;
        lock (_gate)
        {
            if (_closed) return Task.CompletedTask;
            old = _requestCancellation;
            _requestCancellation = null;
            long request = ++_request;
            _result = null;
            if (_parent.IsCancellationRequested)
            {
                _view = Empty(Status.Canceled);
                run = Task.CompletedTask;
            }
            else if (!IsExactSha256(input))
            {
                _view = Empty(Status.Invalid);
                run = Task.CompletedTask;
            }
            else
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_parent);
                _requestCancellation = cancellation;
                _view = Empty(Status.Searching);
                var query = MediaSnapshotHashSearch.CreateQuery(_snapshot, input!);
                run = RunAsync(query, request, cancellation);
                _runs.RemoveAll(task => task.IsCompleted);
                _runs.Add(run);
            }
            Changed?.Invoke();
        }
        Cancel(old);
        return run;
    }
    private async Task RunAsync(MediaSnapshotHashQuery query, long request, CancellationTokenSource cancellation)
    {
        try
        {
            WorkResult work = await _execute(query, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!Accept(request, cancellation)) return;
                _result = work.Result;
                Status status = work.Result.Status switch
                {
                    MediaSnapshotHashSearchStatus.MissingManifest => Status.Missing,
                    MediaSnapshotHashSearchStatus.CorruptManifest => Status.Corrupt,
                    _ => work.HasMeasurements ? Status.Available : Status.NoMeasurements
                };
                if (status == Status.Available)
                    _view = new(request, status, work.Result.Generation, 0, work.Result.Count,
                        ImmutableArray.CreateRange(work.Result.GetPage(0, PageSize)));
                else _view = Empty(status);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { FinishFailure(request, Status.Canceled); }
        catch (Exception) { FinishFailure(request, Status.Failed); } // No raw exception/path/URL diagnostics.
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null;
            }
            cancellation.Dispose();
        }
    }
    private bool Accept(long request, CancellationTokenSource cancellation) =>
        !_closed && request == _request && !_parent.IsCancellationRequested && !cancellation.IsCancellationRequested;
    private void FinishFailure(long request, Status status)
    {
        lock (_gate)
        {
            if (_closed || request != _request) return;
            _result = null;
            _view = Empty(status);
            Changed?.Invoke();
        }
    }
    internal void MovePage(int direction)
    {
        lock (_gate)
        {
            if (_closed || _parent.IsCancellationRequested || _view.Status != Status.Available || _result == null ||
                direction is not (-1 or 1)) return;
            long offset = (long)_view.Offset + direction * PageSize;
            if (offset < 0 || offset >= _view.Count) return;
            _view = _view with { Offset = (int)offset,
                Page = ImmutableArray.CreateRange(_result.GetPage((int)offset, PageSize)) };
            Changed?.Invoke();
        }
    }
    internal MediaAssetReference? Select(long request, int offset, int index)
    {
        lock (_gate)
        {
            if (_closed || _parent.IsCancellationRequested || request != _request || offset != _view.Offset ||
                _view.Status != Status.Available || index < 0 || index >= _view.Page.Length) return null;
            return _view.Page[index]; // Exact original reference, not an ID/URL/path reconstruction.
        }
    }
    internal Task TerminateAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_termination != null) return _termination;
            _closed = true;
            ++_request;
            cancellation = _requestCancellation;
            _requestCancellation = null;
            _result = null;
            _view = Empty(Status.Closed);
            _termination = Task.WhenAll(_runs.ToArray());
        }
        _parentRegistration.Dispose();
        Cancel(cancellation);
        return _termination;
    }
    private static void Cancel(CancellationTokenSource? cancellation)
    {
        // A concurrently finishing worker owns disposal; cancellation is best effort,
        // request/generation/lifetime checks, not cancellation, revoke result authority.
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }
}

internal sealed class MediaHashSearchDialog : Window
{
    private readonly MediaHashSearchOperation _operation;
    private readonly WindowChromeBar _chrome = new() { Name = "MediaHashSearchChrome" };
    private readonly TextBox _input = new() { Name = "MediaHashSearchInput", MaxLength = 256 };
    private readonly TextBlock _heading = Label("MediaHashSearchHeading");
    private readonly TextBlock _scope = Label("MediaHashSearchScope");
    private readonly TextBlock _inputLabel = Label("MediaHashSearchInputLabel");
    private readonly TextBlock _generation = Label("MediaHashSearchGeneration");
    private readonly TextBlock _status = Label("MediaHashSearchStatus");
    private readonly TextBlock _page = Label("MediaHashSearchPage");
    private readonly StackPanel _rows = new() { Name = "MediaHashSearchResults", Spacing = 6 };
    private readonly Button _search = new() { Name = "MediaHashSearchStart" };
    private readonly Button _previous = new() { Name = "MediaHashSearchPrevious" };
    private readonly Button _next = new() { Name = "MediaHashSearchNext" };
    private readonly Button _close = new() { Name = "MediaHashSearchClose" };
    private bool _closed;
    internal MediaHashSearchOperation Operation => _operation;

    private readonly bool _searchOnOpen;
    // initialHash: the draft of the scoped search field (AP2). A non-empty draft is searched once when the
    // dialog opens, through the same validation as the Search button.
    internal MediaHashSearchDialog(MediaAssetSnapshot snapshot, CancellationToken lifetime = default, string? initialHash = null)
    {
        _operation = new(snapshot, lifetime);
        if (!string.IsNullOrEmpty(initialHash)) { _input.Text = initialHash; _searchOnOpen = true; }
        Name = "MediaHashSearchDialog";
        Width = 760; Height = 640; MinWidth = 360; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.Apply(this);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _heading.Classes.Add("section-title");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        // UX103 P7: task, draft, search action and request status stay in the fixed header;
        // scope, captured generation, page range and result rows scroll; paging/close stay fixed.
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(_heading); header.Children.Add(_inputLabel); header.Children.Add(_input);
        header.Children.Add(_search); header.Children.Add(_status);
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(_scope); body.Children.Add(_generation);
        body.Children.Add(_page); body.Children.Add(_rows);
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_previous); buttons.Children.Add(_next); buttons.Children.Add(_close);
        var frame = DialogFrame.Build(header, body, buttons);
        // The caption bar sits outside the frame; the window keeps cycling Tab through both.
        KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Continue);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(_chrome);
        grid.Children.Add(frame); Grid.SetRow(frame, 1);
        Content = grid;
        _search.Click += OnSearch; _previous.Click += OnPrevious; _next.Click += OnNext; _close.Click += OnClose;
        _input.PropertyChanged += OnInputChanged;
        _operation.Changed += OnOperationChanged;
        Opened += OnOpened; Closed += OnClosed; KeyDown += OnKeyDown;
        Texts.Changed += OnTextsChanged;
        Refresh();
    }
    private static TextBlock Label(string name) => new() { Name = name, TextWrapping = TextWrapping.Wrap };
    // Opaque IDs are not paths/URLs. Unsafe display values are omitted, never shortened
    // into a different identity; the exact underlying reference still stays intact.
    internal static string DisplayToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return MediaHashSearchCatalog.T("mediahash.omitted");
        foreach (char c in value)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
                return MediaHashSearchCatalog.T("mediahash.omitted");
        return value;
    }
    internal static string RowLabel(MediaAssetReference asset) => MediaHashSearchCatalog.T("mediahash.row",
        DisplayToken(asset.AssetId), DisplayToken(asset.MediaIdentifier), DisplayToken(asset.Variant),
        MediaHashSearchCatalog.T(asset.IsCurrent ? "mediahash.yes" : "mediahash.no"));
    private void OnOpened(object? sender, EventArgs e)
    {
        if (_closed) return;
        _chrome.Attach(this); _input.Focus();
        if (_searchOnOpen && _search.IsEnabled) _ = _operation.SearchAsync(_input.Text);
    }
    private void OnOperationChanged() => Dispatcher.UIThread.Post(Refresh);
    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        if (_closed) return;
        var view = _operation.Current;
        Title = MediaHashSearchCatalog.T("mediahash.title"); _heading.Text = Title;
        _scope.Text = MediaHashSearchCatalog.T("mediahash.scope");
        _inputLabel.Text = MediaHashSearchCatalog.T("mediahash.input");
        AutomationProperties.SetName(_input, _inputLabel.Text);
        _generation.Text = MediaHashSearchCatalog.T("mediahash.generation", view.Generation == null
            ? MediaHashSearchCatalog.T("mediahash.unavailable") : DisplayToken(view.Generation));
        void ButtonText(Button button, string key)
        { button.Content = MediaHashSearchCatalog.T(key); AutomationProperties.SetName(button, (string)button.Content!); }
        ButtonText(_search, "mediahash.search"); ButtonText(_previous, "mediahash.previous");
        ButtonText(_next, "mediahash.next"); ButtonText(_close, "mediahash.close");
        _search.IsEnabled = view.Status is not (MediaHashSearchOperation.Status.Searching or MediaHashSearchOperation.Status.Closed);
        string key = view.Status switch
        {
            MediaHashSearchOperation.Status.Invalid => "invalid",
            MediaHashSearchOperation.Status.Searching => "searching",
            MediaHashSearchOperation.Status.Missing => "missing",
            MediaHashSearchOperation.Status.Corrupt => "corrupt",
            MediaHashSearchOperation.Status.NoMeasurements => "noMeasurements",
            MediaHashSearchOperation.Status.Failed => "failed",
            MediaHashSearchOperation.Status.Canceled => "canceled",
            MediaHashSearchOperation.Status.Available => view.Count == 0 ? "noMatches" : "matches",
            _ => "ready"
        };
        _status.Text = MediaHashSearchCatalog.T("mediahash." + key, view.Count);
        _page.Text = view.Page.Length == 0 ? "" : MediaHashSearchCatalog.T("mediahash.page",
            view.Offset + 1, view.Offset + view.Page.Length, view.Count);
        _previous.IsEnabled = view.Status == MediaHashSearchOperation.Status.Available && view.Offset > 0;
        _next.IsEnabled = view.Status == MediaHashSearchOperation.Status.Available && (long)view.Offset + MediaHashSearchOperation.PageSize < view.Count;
        _rows.Children.Clear();
        for (int index = 0; index < view.Page.Length; index++)
        {
            int capturedIndex = index;
            var button = new Button { Content = new TextBlock { Text = RowLabel(view.Page[index]), TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(button, RowLabel(view.Page[index]));
            button.Click += (_, _) =>
            {
                if (_closed) return;
                MediaAssetReference? selected = _operation.Select(view.Request, view.Offset, capturedIndex);
                if (selected != null) Close(selected); // Root explicitly navigates later; never here.
            };
            _rows.Children.Add(button);
        }
    }
    private async void OnSearch(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _operation.SearchAsync(_input.Text);
    private void OnInputChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // TextChanged can be deferred by the framework; revoke old row authority at
        // the Text property change itself, before any queued click/refresh.
        if (!_closed && e.Property == TextBox.TextProperty) { _operation.Invalidate(); Refresh(); }
    }
    private void OnPrevious(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { _operation.MovePage(-1); Refresh(); }
    private void OnNext(object? sender, Avalonia.Interactivity.RoutedEventArgs e) { _operation.MovePage(1); Refresh(); }
    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        // Enter in the hash box is the keyboard form of the explicit search button.
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && ReferenceEquals(e.Source, _input) && _search.IsEnabled)
        { e.Handled = true; OnSearch(sender, new Avalonia.Interactivity.RoutedEventArgs()); }
    }
    private void OnClosed(object? sender, EventArgs e) => _ = TerminateAsync();
    internal Task TerminateAsync()
    {
        if (!_closed)
        {
            _closed = true;
            Texts.Changed -= OnTextsChanged; _operation.Changed -= OnOperationChanged;
            _input.PropertyChanged -= OnInputChanged;
            _search.Click -= OnSearch; _previous.Click -= OnPrevious; _next.Click -= OnNext; _close.Click -= OnClose;
            Opened -= OnOpened; Closed -= OnClosed; KeyDown -= OnKeyDown;
        }
        return _operation.TerminateAsync();
    }
}
