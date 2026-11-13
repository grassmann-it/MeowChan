using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

/// <summary>
/// Native transfer inventory only; no Apply command. Sole session owner after construction.
/// The UI-thread picker delegate returns a local absolute file path or null, never a URI/stream.
/// The optional label delegate returns composite-format templates (not package-authored text).
/// The caller must await TerminateAsync in its finally path, including a failed ShowDialog,
/// before releasing the operation root. Closed and explicit termination share one task.
/// </summary>
internal sealed class NativeTransferPreviewDialog : Window
{
    private readonly NativeTransferPreviewSession _session;
    private readonly Func<Task<string?>> _chooseFile;
    private readonly Func<string, string> _label;
    private readonly WindowChromeBar _chrome = new();
    private readonly Button _choose;
    private readonly Button _cancel;
    private readonly Button _close;
    private readonly TextBlock _heading = new() { Name = "NativeTransferPreviewHeading" };
    private readonly TextBlock _source = new() { Name = "NativeTransferPreviewSource", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _state = new() { Name = "NativeTransferPreviewState", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { Name = "NativeTransferPreviewSummary", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { Name = "NativeTransferPreviewDetails", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notice = new() { Name = "NativeTransferPreviewNotice", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Name = "NativeTransferPreviewError", TextWrapping = TextWrapping.Wrap };
    private NativeTransferPreviewSnapshot _display;
    private long _operation, _pickerOperation;
    private bool _closed, _pickerPending;
    private string? _fileName;
    private Task? _cleanupTask;

    internal NativeTransferPreviewDialog(NativeTransferPreviewSession session, Func<Task<string?>> chooseFile,
        Func<string, string>? label = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _chooseFile = chooseFile ?? throw new ArgumentNullException(nameof(chooseFile));
        _label = label ?? (key => Texts.T(key));
        _display = session.Current;
        Name = "NativeTransferPreviewDialog";
        Width = 720;
        Height = 560;
        MinWidth = 440;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.Apply(this);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        _choose = CreateButton("NativeTransferPreviewChoose", "transfer.preview.choose", OnChooseAsync);
        _cancel = CreateButton("NativeTransferPreviewCancel", "action.cancel", OnCancel);
        _close = CreateButton("NativeTransferPreviewClose", "action.close", () => Close());
        _cancel.IsEnabled = false;
        _notice.Classes.Add("workspace-help");
        _error.Classes.Add("workspace-error");
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);

        _heading.Classes.Add("section-title");
        _source.Classes.Add("workspace-meta");
        _state.Classes.Add("workspace-meta");
        _summary.Classes.Add("workspace-meta");
        // Header: title and chosen source; error strip: the local read/package error; body: inventory and
        // inventory-only notice; footer: the polite state line above Choose/Cancel/Close.
        StackPanel header = new() { Spacing = 12 };
        header.Children.Add(_heading);
        header.Children.Add(_source);
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(_summary);
        body.Children.Add(_details);
        body.Children.Add(_notice);

        WrapPanel actions = new()
        {
            ItemSpacing = 8,
            LineSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(_choose);
        actions.Children.Add(_cancel);
        actions.Children.Add(_close);
        StackPanel footerContent = new() { Spacing = 8 };
        footerContent.Children.Add(_state);
        footerContent.Children.Add(actions);
        Border footer = new() { Child = footerContent, Margin = new Thickness(16, 0, 16, 16) };
        footer.Classes.Add("workspace-dialog-footer");

        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(_chrome);
        Grid frame = DialogFrame.Build(header, body, footer, DialogFrame.ErrorStrip(_error));
        layout.Children.Add(frame);
        Grid.SetRow(frame, 1);
        Content = layout;

        Opened += (_, _) => _chrome.Attach(this);
        Closed += (_, _) => _ = TerminateAsync();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_cancel.IsEnabled) OnCancel();
            else Close();
        }, RoutingStrategies.Tunnel);
        Texts.Changed += OnLanguageChanged;
        IsLanguageSubscribedForTests = true;
        ApplyLabels();
        Render(_display);
    }

    internal Button ChooseButton => _choose;
    internal Button CancelButton => _cancel;
    internal Button CloseButton => _close;
    internal TextBlock StateText => _state;
    internal TextBlock SummaryText => _summary;
    internal TextBlock ErrorText => _error;
    internal Task CleanupCompletion => _cleanupTask ?? Task.CompletedTask;

    // UI-thread terminal ownership is explicit even if ShowDialog never reaches Closed.
    // Retain the session's own idempotency as defense-in-depth, not as two-owner cleanup.
    internal Task TerminateAsync()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        if (_cleanupTask != null) return _cleanupTask;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _cleanupTask = completion.Task; // Publish ownership before cancellation callbacks can reenter.
        Volatile.Write(ref _closed, true);
        ++_operation;
        Texts.Changed -= OnLanguageChanged;
        IsLanguageSubscribedForTests = false;
        _choose.IsEnabled = _cancel.IsEnabled = _close.IsEnabled = false;
        ++SessionDisposalCountForTests;
        _ = DisposeSessionAsync(completion);
        return _cleanupTask;
    }

    private async Task DisposeSessionAsync(TaskCompletionSource completion)
    {
        try { await _session.DisposeAsync(); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal bool IsLanguageSubscribedForTests { get; private set; }
    internal int SessionDisposalCountForTests { get; private set; }
    internal Task ChooseForTestsAsync() => OnChooseAsync();

    private Button CreateButton(string name, string key, Func<Task> action)
    {
        Button button = new() { Name = name };
        button.Click += async (_, _) => await action();
        SetButtonLabel(button, key);
        return button;
    }

    private Button CreateButton(string name, string key, Action action)
    {
        Button button = new() { Name = name };
        button.Click += (_, _) => action();
        SetButtonLabel(button, key);
        return button;
    }

    private string Label(string key, params object?[] arguments)
    {
        string template;
        try { template = _label(key) ?? key; }
        catch { return key; }
        if (arguments.Length == 0) return template;
        try { return string.Format(Texts.Culture, template, arguments); }
        catch (FormatException) { return template; }
    }

    private void SetButtonLabel(Button button, string key)
    {
        string text = Label(key);
        button.Content = text;
        AutomationProperties.SetName(button, text);
        ToolTip.SetTip(button, text);
    }

    private bool IsChoosing => _pickerPending && _pickerOperation == _operation;

    private async Task OnChooseAsync()
    {
        if (_closed || _pickerPending) return;
        long operation = ++_operation;
        _pickerOperation = operation;
        _pickerPending = true;
        _fileName = null;
        _session.Cancel();
        Render(_session.Current);
        try
        {
            string? path;
            try { path = await _chooseFile(); }
            finally
            {
                // Cancel invalidates the result, but does not dismiss a native picker. Do not
                // allow another picker until the first actually returns (including after Esc).
                _pickerPending = false;
                RefreshControls();
            }
            if (_closed || operation != _operation) return;
            if (string.IsNullOrWhiteSpace(path)) { Render(_session.Current); return; }

            _fileName = Path.GetFileName(path);
            Task<NativeTransferPreviewSnapshot> pending = _session.InspectAsync(path);
            Render(_session.Current);
            NativeTransferPreviewSnapshot result = await pending;
            if (!_closed && operation == _operation) Render(result);
        }
        catch (Exception)
        {
            // Picker/provider details deliberately never become user-facing diagnostics.
            if (!_closed && operation == _operation)
                Render(new(operation, NativeTransferPreviewState.Rejected,
                    NativeTransferPreviewError.ReadFailure, null, _session.Current.CleanupRetained));
        }
        finally
        {
            // A stale operation may refresh ONLY the sticky cleanup warning, not inventory,
            // errors, the chosen file or state. This also exposes cleanup after Cancel.
            if (!_closed) Render(_display);
        }
    }

    private void OnCancel()
    {
        if (_closed || !_cancel.IsEnabled) return;
        ++_operation;
        _session.Cancel();
        Render(_session.Current);
    }

    private void RefreshControls()
    {
        if (_closed) return;
        _choose.IsEnabled = !_pickerPending;
        _cancel.IsEnabled = IsChoosing || _display.State == NativeTransferPreviewState.Reading;
    }

    private void Render(NativeTransferPreviewSnapshot snapshot)
    {
        if (_closed) return;
        snapshot = snapshot with { CleanupRetained = snapshot.CleanupRetained || _session.Current.CleanupRetained };
        _display = snapshot;
        RefreshControls();
        Title = Label("transfer.preview.title");
        _heading.Text = Label("transfer.preview.heading");
        _source.Text = _fileName == null ? Label("transfer.preview.noSource") : Label("transfer.preview.source", _fileName);
        _state.Text = Label(IsChoosing ? "transfer.preview.choosing" : StateKey(snapshot));
        _notice.Text = snapshot.CleanupRetained
            ? Label("transfer.preview.inventoryOnly") + Environment.NewLine + Label("transfer.preview.cleanupRetained")
            : Label("transfer.preview.inventoryOnly");
        _error.Text = snapshot.Error == NativeTransferPreviewError.None ? string.Empty : Label(ErrorKey(snapshot.Error));
        if (snapshot.Inventory is not { } inventory)
        {
            _summary.Text = string.Empty;
            _details.Text = string.Empty;
            return;
        }

        _summary.Text = string.Join(Environment.NewLine,
            Label("transfer.preview.summary", inventory.Threads.Length, inventory.Posts,
                inventory.UniquePayloads, inventory.UniquePayloadBytes),
            Counts("transfer.preview.originals", inventory.CurrentOriginals),
            Counts("transfer.preview.previews", inventory.CurrentPreviews),
            Counts("transfer.preview.retained", inventory.RetainedBindings))
            + DerivedCounts(inventory.DerivedPreviews, inventory.VideoPosters);
        if (inventory.DerivedPreviews.Total + inventory.VideoPosters.Total > 0)
            _notice.Text += Environment.NewLine + Label("transfer.preview.derivedNotice");
        // Plain TextBlocks only: no post HTML, media, title, hyperlinks or source activation.
        _details.Text = string.Join(Environment.NewLine, inventory.Threads.Select(thread =>
            Label("transfer.preview.thread", thread.Id.Source, thread.Id.Board, thread.Id.Thread, thread.Posts,
                Counts("transfer.preview.originals", thread.CurrentOriginals),
                Counts("transfer.preview.previews", thread.CurrentPreviews),
                Counts("transfer.preview.retained", thread.RetainedBindings))
            + DerivedCounts(thread.DerivedPreviews, thread.VideoPosters)));
    }

    private string DerivedCounts(TransferImportStatusCounts previews, TransferImportStatusCounts posters) =>
        previews.Total + posters.Total == 0 ? string.Empty : Environment.NewLine
            + Counts("transfer.preview.derivedPreviews", previews) + Environment.NewLine
            + Counts("transfer.preview.videoPosters", posters);

    private string Counts(string key, TransferImportStatusCounts counts) =>
        Label(key, counts.Contained, counts.Omitted, counts.Unknown, counts.Unavailable, counts.Failed, counts.Cancelled);

    private static string StateKey(NativeTransferPreviewSnapshot snapshot) => snapshot.State switch
    {
        NativeTransferPreviewState.Reading => "transfer.preview.reading",
        NativeTransferPreviewState.Inventory => "transfer.preview.ready",
        NativeTransferPreviewState.Canceled => "transfer.preview.canceled",
        NativeTransferPreviewState.Closed => "transfer.preview.closed",
        NativeTransferPreviewState.Rejected => "transfer.preview.rejected",
        _ => "transfer.preview.idle"
    };

    private static string ErrorKey(NativeTransferPreviewError error) => error switch
    {
        NativeTransferPreviewError.InvalidSource => "transfer.preview.error.source",
        NativeTransferPreviewError.InvalidWorkDirectory => "transfer.preview.error.workDirectory",
        NativeTransferPreviewError.UnsupportedPlatform => "transfer.preview.error.platform",
        NativeTransferPreviewError.InvalidPackage => "transfer.preview.error.package",
        NativeTransferPreviewError.PrivateMetadataNotYetSupported => "transfer.preview.error.private",
        NativeTransferPreviewError.LimitExceeded => "transfer.preview.error.limit",
        _ => "transfer.preview.error.read"
    };

    private void ApplyLabels()
    {
        if (_closed) return;
        SetButtonLabel(_choose, "transfer.preview.choose");
        SetButtonLabel(_cancel, "action.cancel");
        SetButtonLabel(_close, "action.close");
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _closed)) return;
        void Refresh()
        {
            if (_closed) return;
            ApplyLabels();
            Render(_display);
        }
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        else Refresh();
    }

}
