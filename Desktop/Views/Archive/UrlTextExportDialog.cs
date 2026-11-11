using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// One operation, one immutable plan, at most one confirmed write. Used by the UI
// and synthetic tests alike; cancellation invalidates results even for an uncooperative picker.
internal sealed class UrlTextExportOperation : IDisposable
{
    private readonly ArchiveUrlTextExportPlan _plan;
    private readonly Func<Task<string?>> _choose;
    private readonly Func<ArchiveUrlTextExportPlan, string, CancellationToken, Task<UrlTextExportFileWriteResult>> _write;
    private readonly CancellationTokenSource _lifetime;
    private bool _started, _ended;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal string StatusKey { get; private set; } = "urltext.export.scope";
    internal UrlTextExportFileWriteResult? Result { get; private set; }
    internal bool Started => _started;
    internal bool Ended => _ended;
    internal event Action? Changed;
    internal UrlTextExportOperation(ArchiveUrlTextExportPlan plan, Func<Task<string?>> choose,
        Func<ArchiveUrlTextExportPlan, string, CancellationToken, Task<UrlTextExportFileWriteResult>> write,
        CancellationToken token)
    { _plan = plan; _choose = choose; _write = write; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); }
    internal Task ConfirmAsync()
    {
        if (_started || _ended || _lifetime.IsCancellationRequested) return Completion;
        _started = true;
        return Completion = RunAsync();
    }
    private void Status(string key) { StatusKey = "urltext.export." + key; if (!_ended) Changed?.Invoke(); }
    private async Task RunAsync()
    {
        bool writeStarted = false;
        try
        {
            Status("choosing");
            string? target = await _choose();
            if (_ended || _lifetime.IsCancellationRequested) return;
            if (target == null) { Status("canceled"); return; }
            Status("writing");
            // Changed callbacks cannot authorize a write after cancellation.
            if (_ended || _lifetime.IsCancellationRequested) return;
            writeStarted = true;
            var result = await _write(_plan, target, _lifetime.Token);
            Result = result; // Reconcile even after close/cancellation; never touch stale UI.
            if (_ended || _lifetime.IsCancellationRequested) return;
            Status(result.Status switch
            {
                UrlTextExportFileWriteStatus.Saved => "saved",
                UrlTextExportFileWriteStatus.Canceled => "canceled",
                UrlTextExportFileWriteStatus.PartialRetained => result.Error == UrlTextExportFileWriteError.CreationUncertain
                    ? "creationUncertain" : "partialRetained",
                _ => result.Error == UrlTextExportFileWriteError.DestinationExists ? "error.exists" : "error.write"
            });
        }
        catch (OperationCanceledException)
        {
            if (writeStarted) Result = new(UrlTextExportFileWriteStatus.PartialRetained, UrlTextExportFileWriteError.Canceled);
            if (!_ended) Status(writeStarted ? "partialRetained" : "canceled");
        }
        catch (Exception)
        {
            if (writeStarted) Result = new(UrlTextExportFileWriteStatus.PartialRetained, UrlTextExportFileWriteError.IoFailure);
            if (!_ended && !_lifetime.IsCancellationRequested) Status(writeStarted ? "partialRetained" : "error.target");
        }
    }
    internal Task TerminateAsync() { _ended = true; _lifetime.Cancel(); return Completion; }
    public void Dispose() { _lifetime.Dispose(); }
}

internal sealed class UrlTextExportDialog : Window
{
    private readonly ArchiveUrlTextExportPlan _plan;
    private readonly UrlTextExportOperation _operation;
    private readonly WindowChromeBar _chrome = new() { Name = "UrlTextExportChrome" };
    private readonly TextBlock _heading = new() { Name = "UrlTextExportHeading", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _labels = new() { Name = "UrlTextExportConsent", Spacing = 8 };
    private readonly TextBlock _source = new() { Name = "UrlTextExportSource", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _board = new() { Name = "UrlTextExportBoard", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _thread = new() { Name = "UrlTextExportThread", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _url = new() { Name = "UrlTextExportUrl", TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Name = "UrlTextExportSave" };
    private readonly Button _close = new() { Name = "UrlTextExportClose" };
    private readonly TextBlock _status = new() { Name = "UrlTextExportStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Name = "UrlTextExportError", TextWrapping = TextWrapping.Wrap };
    private bool _terminated;
    private Task? _termination;
    internal UrlTextExportOperation Operation => _operation;

    internal UrlTextExportDialog(ArchiveUrlTextExportPlan plan, Func<Task<string?>> choose,
        Func<ArchiveUrlTextExportPlan, string, CancellationToken, Task<UrlTextExportFileWriteResult>> write,
        CancellationToken token)
    {
        _plan = plan;
        _operation = new(plan, choose, write, token);
        Name = "UrlTextExportDialog";
        Width = 640; Height = 600; MinWidth = 360; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 48;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _heading.Classes.Add("section-title");
        _status.Classes.Add("workspace-meta");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var buttons = new WrapPanel
        {
            ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(_save); buttons.Children.Add(_close);
        var footerContent = new StackPanel { Spacing = 12 };
        footerContent.Children.Add(_status); footerContent.Children.Add(buttons);
        var footer = new Border { Child = footerContent };
        footer.Classes.Add("workspace-dialog-footer");
        _labels.Margin = new Thickness(16, 0, 16, 16);
        // Heading fixed, consent labels scroll, failures in the local strip, actions and status in the footer.
        var frame = MeowChan.Desktop.Controls.DialogFrame.Build(_heading, _labels, footer, MeowChan.Desktop.Controls.DialogFrame.ErrorStrip(_error));
        var layout = new Grid { RowDefinitions = new RowDefinitions("48,*") };
        layout.Children.Add(_chrome);
        layout.Children.Add(frame); Grid.SetRow(frame, 1);
        Content = layout;
        Opened += OnOpened;
        _save.Click += OnSave;
        _close.Click += OnClose;
        KeyDown += OnKeyDown;
        Closed += OnClosed;
        _operation.Changed += Refresh;
        Texts.Changed += OnTextsChanged;
        Refresh();
    }
    private void OnOpened(object? sender, EventArgs e) { if (!_terminated) _chrome.Attach(this); }
    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        if (_terminated) return;
        Title = Texts.T("urltext.export.title");
        _heading.Text = Title;
        _labels.Children.Clear();
        void Add(string key, params object?[] args)
        {
            // Keep identity controls themselves stable across language refreshes.
            TextBlock label = key switch
            {
                "source" => _source, "board" => _board, "thread" => _thread, "url" => _url,
                _ => new TextBlock { TextWrapping = TextWrapping.Wrap }
            };
            label.Text = Texts.T("urltext.export." + key, args);
            _labels.Children.Add(label);
        }
        Add("source", _plan.Id.Source); Add("board", _plan.Id.Board); Add("thread", _plan.Id.Thread);
        Add("url", _plan.Url); Add("scope"); Add("excluded"); Add("privacy"); Add("noClient"); Add("availability");
        _save.Content = Texts.T("urltext.export.save");
        _save.IsEnabled = !_operation.Started && !_operation.Ended;
        _close.Content = Texts.T(_operation.Started ? "urltext.export.close" : "urltext.export.cancel");
        AutomationProperties.SetName(_save, (string)_save.Content!);
        AutomationProperties.SetName(_close, (string)_close.Content!);
        // Failures go to the local error strip; progress and outcomes stay in the footer status.
        bool failed = _operation.StatusKey.StartsWith("urltext.export.error.", StringComparison.Ordinal);
        string message = Texts.T(_operation.StatusKey);
        _status.Text = failed ? string.Empty : message;
        _error.Text = failed ? message : string.Empty;
    }
    private async void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _operation.ConfirmAsync();
    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
    private void OnKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
    private void OnClosed(object? sender, EventArgs e) => _ = TerminateAsync();
    internal Task TerminateAsync()
    {
        if (_termination != null) return _termination;
        _terminated = true;
        Texts.Changed -= OnTextsChanged;
        _operation.Changed -= Refresh;
        _save.Click -= OnSave; _close.Click -= OnClose;
        Opened -= OnOpened;
        KeyDown -= OnKeyDown; Closed -= OnClosed;
        return _termination = DrainAsync();
    }
    private async Task DrainAsync() { await _operation.TerminateAsync(); _operation.Dispose(); }
}
