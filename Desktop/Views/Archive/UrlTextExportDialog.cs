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
using MeowChan.Desktop.Controls;
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
    private bool _started, _ended, _canceling;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal string StatusKey { get; private set; } = "urltext.export.scope";
    internal UrlTextExportFileWriteResult? Result { get; private set; }
    internal int LimitMiB { get; }
    internal bool Started => _started;
    internal bool Ended => _ended;
    internal bool IsCanceling => _canceling;
    internal event Action? Changed;
    internal UrlTextExportOperation(ArchiveUrlTextExportPlan plan, Func<Task<string?>> choose,
        Func<ArchiveUrlTextExportPlan, string, CancellationToken, Task<UrlTextExportFileWriteResult>> write,
        CancellationToken token, int limitMiB = UrlTextExportFileWriter.DefaultLimitMiB)
    {
        if (limitMiB is < UrlTextExportFileWriter.MinimumLimitMiB or > UrlTextExportFileWriter.MaximumLimitMiB)
            throw new ArgumentOutOfRangeException(nameof(limitMiB));
        LimitMiB = limitMiB;
        _plan = plan; _choose = choose; _write = write; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
    }
    internal Task ConfirmAsync()
    {
        if (_started || _ended || _lifetime.IsCancellationRequested) return Completion;
        _started = true;
        return Completion = RunAsync();
    }
    private void Status(string key)
    {
        StatusKey = "urltext.export." + key;
        if (key != "canceling") _canceling = false;
        if (!_ended) Changed?.Invoke();
    }
    private async Task RunAsync()
    {
        bool writeStarted = false;
        try
        {
            Status("choosing");
            string? target = await _choose();
            if (_ended) return;
            if (_lifetime.IsCancellationRequested) { Status("canceled"); return; }
            if (target == null) { Status("canceled"); return; }
            Status("writing");
            // Changed callbacks cannot authorize a write after cancellation.
            if (_ended) return;
            if (_lifetime.IsCancellationRequested) { Status("canceled"); return; }
            writeStarted = true;
            var result = await _write(_plan, target, _lifetime.Token);
            Result = result; // Reconcile even after close/cancellation; never touch stale UI.
            if (_ended) return;
            // A cancellation request is not the outcome. Keep the dialog open until the
            // writer settles, then show the actual receipt (Saved or PartialRetained).
            Status(result.Status switch
            {
                UrlTextExportFileWriteStatus.Saved => "saved",
                UrlTextExportFileWriteStatus.Canceled => "canceled",
                UrlTextExportFileWriteStatus.PartialRetained => result.Error == UrlTextExportFileWriteError.CreationUncertain
                    ? "creationUncertain" : "partialRetained",
                _ => result.Error switch
                {
                    UrlTextExportFileWriteError.DestinationExists => "error.exists",
                    // The writer also uses LimitExceeded for path length/components, not just output bytes.
                    UrlTextExportFileWriteError.LimitExceeded => LimitFailureKey(_plan.ByteCount, LimitMiB),
                    _ => "error.write"
                }
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
    // The plan still has a separate 1 MiB format ceiling. A larger writer budget does not lift that contract.
    internal static string LimitFailureKey(int byteCount, int limitMiB) =>
        (long)byteCount > (long)limitMiB * 1024 * 1024 ? "error.outputLimit" : "error.pathLimit";

    internal Task CancelAsync()
    {
        if (!_started || _ended || Completion.IsCompleted) return Completion;
        if (!_canceling)
        {
            _canceling = true;
            Status("canceling");
            _lifetime.Cancel();
        }
        return Completion;
    }

    internal Task TerminateAsync() { _ended = true; _lifetime.Cancel(); return Completion; }
    public void Dispose() { _lifetime.Dispose(); }
}

internal sealed class UrlTextExportDialog : Window
{
    private readonly ArchiveUrlTextExportPlan _plan;
    private readonly UrlTextExportOperation _operation;
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
        CancellationToken token, int limitMiB = UrlTextExportFileWriter.DefaultLimitMiB)
    {
        _plan = plan;
        _operation = new(plan, choose, write, token, limitMiB);
        Name = "UrlTextExportDialog";
        // Modals plan MD2: a modal form-step dialog; the title stands in the title bar only.
        DialogWindow.Configure(this, DialogSize.M, modal: true);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _status.Classes.Add("workspace-meta");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        // Close doubles as Cancel until the save starts, so it stays and is right-most; Save comes before it.
        var footer = DialogFooter.Build(_close, new Control[] { _save }, _status, statusAbove: true);
        _labels.Margin = new Thickness(16, 0, 16, 16);
        // Consent labels scroll (the title is in the title bar), failures in the local strip, actions and status in the footer.
        var frame = DialogFrame.Build(header: null, _labels, footer, DialogFrame.ErrorStrip(_error));
        DialogWindow.Frame(this, frame, modal: true, captionName: "UrlTextExportChrome");
        _save.Click += OnSave;
        _close.Click += OnClose;
        DialogEscape.Attach(this);
        Closed += OnClosed;
        _operation.Changed += Refresh;
        Texts.Changed += OnTextsChanged;
        Refresh();
    }
    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        if (_terminated) return;
        Title = Texts.T("urltext.export.title");
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
        _close.Content = Texts.T(_operation.Started ? "action.close" : "urltext.export.cancel");
        _close.IsEnabled = !_operation.IsCanceling;
        AutomationProperties.SetName(_save, (string)_save.Content!);
        AutomationProperties.SetName(_close, (string)_close.Content!);
        // Failures go to the local error strip; progress and outcomes stay in the footer status.
        bool failed = _operation.StatusKey.StartsWith("urltext.export.error.", StringComparison.Ordinal);
        string message = Texts.T(_operation.StatusKey, _operation.LimitMiB);
        _status.Text = failed ? string.Empty : message;
        _error.Text = failed ? message : string.Empty;
    }
    private async void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _operation.ConfirmAsync();
    private async void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_operation.Started && !_operation.Ended && !_operation.Completion.IsCompleted)
        {
            // Once writing started, keep the dialog alive for the owned drain. This
            // exposes canceling and the final partial/saved receipt instead of making
            // Close look like a successful or silent retry boundary.
            await _operation.CancelAsync();
            return;
        }
        Close();
    }
    private void OnClosed(object? sender, EventArgs e) => _ = TerminateAsync();
    internal Task TerminateAsync()
    {
        if (_termination != null) return _termination;
        _terminated = true;
        Texts.Changed -= OnTextsChanged;
        _operation.Changed -= Refresh;
        _save.Click -= OnSave; _close.Click -= OnClose;
        Closed -= OnClosed;
        return _termination = DrainAsync();
    }
    private async Task DrainAsync() { await _operation.TerminateAsync(); _operation.Dispose(); }
}
