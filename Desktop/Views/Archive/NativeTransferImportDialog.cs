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
using MeowChan.Desktop.Localization;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

/// <summary>
/// Owns the user-facing native transfer import session. Inspection, planning and apply remain
/// in <see cref="NativeTransferImportSession"/>; this window only exposes immutable snapshots and
/// explicit commands. It never receives a source stream, prepared plan or profile writer.
/// </summary>
internal sealed class NativeTransferImportDialog : Window
{
    private readonly NativeTransferImportSession _session;
    private readonly Func<Task<string?>> _chooseFile;
    private readonly Func<string, string> _label;
    private readonly WindowChromeBar _chrome = new();
    private readonly Button _choose;
    private readonly Button _cancel;
    private readonly Button _apply;
    private readonly Button _close;
    private readonly TextBlock _heading = new() { Name = "NativeTransferImportHeading" };
    private readonly TextBlock _source = new() { Name = "NativeTransferImportSource", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _state = new() { Name = "NativeTransferImportState", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { Name = "NativeTransferImportSummary", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { Name = "NativeTransferImportDetails", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notice = new() { Name = "NativeTransferImportNotice", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Name = "NativeTransferImportError", TextWrapping = TextWrapping.Wrap };
    private NativeTransferImportSnapshot _display;
    private long _operation, _pickerOperation;
    private bool _closed, _pickerPending, _readPending, _applyPending;
    private string? _fileName;
    private Task? _cleanupTask;

    internal NativeTransferImportDialog(NativeTransferImportSession session, Func<Task<string?>> chooseFile,
        Func<string, string>? label = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _chooseFile = chooseFile ?? throw new ArgumentNullException(nameof(chooseFile));
        _label = label ?? (key => Texts.T(key));
        _display = session.Current;
        Name = "NativeTransferImportDialog";
        Width = 760;
        Height = 620;
        MinWidth = 480;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 48;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        _choose = CreateButton("NativeTransferImportChoose", "transfer.import.choose", OnChooseAsync);
        _cancel = CreateButton("NativeTransferImportCancel", "transfer.import.cancel", OnCancel);
        _apply = CreateButton("NativeTransferImportApply", "transfer.import.apply", OnApplyAsync);
        _close = CreateButton("NativeTransferImportClose", "transfer.import.close", () => Close());
        _cancel.IsEnabled = false;
        _apply.IsEnabled = false;
        _notice.Classes.Add("workspace-help");
        _error.Classes.Add("workspace-error");
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);

        _heading.Classes.Add("section-title");
        _source.Classes.Add("workspace-meta");
        _state.Classes.Add("workspace-meta");
        _summary.Classes.Add("workspace-meta");
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16) };
        body.Children.Add(_heading);
        body.Children.Add(_source);
        body.Children.Add(_state);
        body.Children.Add(_summary);
        body.Children.Add(_details);
        body.Children.Add(_notice);
        body.Children.Add(_error);

        WrapPanel actions = new()
        {
            ItemSpacing = 8,
            LineSpacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(_choose);
        actions.Children.Add(_cancel);
        actions.Children.Add(_apply);
        actions.Children.Add(_close);
        Border footer = new() { Child = actions, Margin = new Thickness(16, 0, 16, 16) };
        footer.Classes.Add("workspace-dialog-footer");

        Grid layout = new() { RowDefinitions = new RowDefinitions("48,*,Auto") };
        layout.Children.Add(_chrome);
        ScrollViewer scroll = new()
        {
            Content = body,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        layout.Children.Add(scroll);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(footer);
        Grid.SetRow(footer, 2);
        Content = layout;

        Opened += (_, _) => _chrome.Attach(this);
        Closed += (_, _) => _ = TerminateAsync();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (CanCancel()) OnCancel();
            else Close();
        }, RoutingStrategies.Tunnel);
        Texts.Changed += OnLanguageChanged;
        IsLanguageSubscribedForTests = true;
        ApplyLabels();
        Render(_display);
    }

    internal Button ChooseButton => _choose;
    internal Button CancelButton => _cancel;
    internal Button ApplyButton => _apply;
    internal Button CloseButton => _close;
    internal TextBlock StateText => _state;
    internal TextBlock SummaryText => _summary;
    internal TextBlock NoticeText => _notice;
    internal TextBlock ErrorText => _error;
    internal Task CleanupCompletion => _cleanupTask ?? Task.CompletedTask;
    internal bool IsLanguageSubscribedForTests { get; private set; }
    internal int SessionDisposalCountForTests { get; private set; }
    internal NativeTransferImportSnapshot DisplayForTests => _display;

    internal Task ChooseForTestsAsync() => OnChooseAsync();
    internal Task ApplyForTestsAsync() => OnApplyAsync();

    // UI-thread terminal ownership is explicit even if ShowDialog never reaches Closed.
    internal Task TerminateAsync()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        if (_cleanupTask != null) return _cleanupTask;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _cleanupTask = completion.Task;
        Volatile.Write(ref _closed, true);
        ++_operation;
        Texts.Changed -= OnLanguageChanged;
        IsLanguageSubscribedForTests = false;
        _choose.IsEnabled = _cancel.IsEnabled = _apply.IsEnabled = _close.IsEnabled = false;
        ++SessionDisposalCountForTests;
        _ = DisposeSessionAsync(completion);
        return _cleanupTask;
    }

    private async Task DisposeSessionAsync(TaskCompletionSource completion)
    {
        try { await _session.DisposeAsync(); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }

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
        NativeTransferImportSnapshot current = _session.Current;
        if (_closed || _pickerPending || _readPending || _applyPending
            || current.State is NativeTransferImportState.Reading or NativeTransferImportState.Applying)
            return;

        long operation = ++_operation;
        _pickerOperation = operation;
        _pickerPending = true;
        _fileName = null;
        // The session deliberately keeps a previously verified plan while the native
        // picker is open. That plan is still useful to the session, but it must not
        // remain actionable in this dialog while the user is choosing a replacement.
        // Keep the reset dialog-local: the current session contract has no
        // replacement-selection operation, and a valid result below will establish a
        // new generation through InspectAsync.
        NativeTransferImportSnapshot replacement = current with
        {
            State = NativeTransferImportState.Idle,
            Error = NativeTransferImportError.None,
            Inventory = null,
            Plan = null,
            Result = null
        };
        Render(replacement);
        // Do not cancel the session merely because the picker opened. Cancel starts
        // an owned cleanup task, and InspectAsync cannot admit a replacement until
        // that task has drained. Cancelling here therefore races a valid picker
        // result with the previous cleanup. A picker cancellation invalidates this
        // dialog operation; an active read/apply is cancelled only by OnCancel.
        try
        {
            string? path;
            try { path = await _chooseFile(); }
            finally
            {
                // A native picker is not necessarily dismissible by our Cancel
                // button. Keep the gate closed until it actually returns.
                _pickerPending = false;
                RefreshControls();
            }
            if (_closed || operation != _operation) return;
            if (string.IsNullOrWhiteSpace(path))
            {
                // Do not render _session.Current here: it may still be the old Ready
                // snapshot because the picker never started a replacement inspection.
                // A cancelled replacement is terminal for this dialog selection and
                // must not restore the old inventory or re-enable Apply.
                Render(replacement with { State = NativeTransferImportState.Canceled });
                return;
            }

            _fileName = Path.GetFileName(path);
            _readPending = true;
            Task<NativeTransferImportSnapshot> pending = _session.InspectAsync(path);
            // InspectAsync publishes Reading synchronously before returning its
            // task. Render that snapshot before awaiting so the UI is responsive.
            Render(_session.Current);
            NativeTransferImportSnapshot result = await pending;
            _readPending = false;
            if (!_closed && operation == _operation) Render(result);
        }
        catch (Exception)
        {
            // Picker/provider details deliberately never become user-facing
            // diagnostics. The generation belongs to the session, not the dialog
            // operation counter.
            if (!_closed && operation == _operation)
            {
                NativeTransferImportSnapshot failed = _session.Current;
                Render(new(failed.Generation, NativeTransferImportState.Failed,
                    NativeTransferImportError.ReadFailure, null, null, null,
                    failed.CleanupRetained));
            }
        }
        finally
        {
            _readPending = false;
            // A stale operation may only contribute a later cleanup warning. It
            // must never restore an old inventory/error over a Cancel/Close state.
            if (!_closed && operation == _operation) Render(_display);
            else if (!_closed && _session.Current.CleanupRetained && !_display.CleanupRetained)
                Render(_display with { CleanupRetained = true });
        }
    }

    private async Task OnApplyAsync()
    {
        if (_closed || _pickerPending || _readPending || _applyPending
            || _display.State != NativeTransferImportState.Ready)
            return;

        NativeTransferImportSnapshot ready = _session.Current;
        if (ready.State != NativeTransferImportState.Ready || ready.Generation != _display.Generation)
        {
            Render(ready);
            return;
        }

        long operation = ++_operation;
        long generation = ready.Generation;
        _applyPending = true;
        try
        {
            Task<NativeTransferImportSnapshot> pending = _session.ApplyAsync(generation);
            // ApplyAsync publishes Applying synchronously before returning its
            // task. Do not wait for completion before exposing that state.
            Render(_session.Current);
            NativeTransferImportSnapshot result = await pending;
            if (!_closed && operation == _operation) Render(result);
        }
        catch (Exception)
        {
            if (!_closed && operation == _operation)
            {
                NativeTransferImportSnapshot failed = _session.Current;
                Render(new(failed.Generation, NativeTransferImportState.Failed,
                    NativeTransferImportError.ApplyFailure, _display.Inventory, _display.Plan,
                    null, failed.CleanupRetained));
            }
        }
        finally
        {
            _applyPending = false;
            if (!_closed) Render(_session.Current);
        }
    }

    private void OnCancel()
    {
        if (_closed) return;

        if (_pickerPending && IsChoosing)
        {
            // The OS picker may outlive this button click. Invalidate only the
            // dialog operation; do not cancel the session and race its cleanup with
            // the next selection. The picker gate remains closed until it returns.
            ++_operation;
            NativeTransferImportSnapshot current = _session.Current;
            Render(new(current.Generation, NativeTransferImportState.Canceled,
                NativeTransferImportError.None, null, null, null, current.CleanupRetained));
            return;
        }

        if (!_readPending && !_applyPending) return;
        ++_operation;
        _session.Cancel();
        Render(_session.Current);
    }

    private bool CanCancel() => !_closed && (IsChoosing || _readPending || _applyPending);

    private void RefreshControls()
    {
        if (_closed) return;
        _choose.IsEnabled = !_pickerPending && !_readPending && !_applyPending;
        _cancel.IsEnabled = IsChoosing
            || _readPending && _display.State == NativeTransferImportState.Reading
            || _applyPending && _display.State == NativeTransferImportState.Applying;
        _apply.IsEnabled = !_pickerPending && !_readPending && !_applyPending
            && _display.State == NativeTransferImportState.Ready;
    }

    private void Render(NativeTransferImportSnapshot snapshot)
    {
        if (_closed) return;
        NativeTransferImportSnapshot current = _session.Current;
        snapshot = snapshot with { CleanupRetained = snapshot.CleanupRetained || current.CleanupRetained };
        _display = snapshot;
        RefreshControls();
        Title = Label("transfer.import.title");
        _heading.Text = Label("transfer.import.heading");
        _source.Text = _fileName == null ? Label("transfer.import.noSource") : Label("transfer.import.source", _fileName);
        _state.Text = Label(IsChoosing ? "transfer.import.choosing" : StateKey(snapshot));
        _notice.Text = Label("transfer.import.inventory") + Environment.NewLine + Label("transfer.import.privacy");
        if (snapshot.CleanupRetained) _notice.Text += Environment.NewLine + Label("transfer.import.cleanupRetained");
        _error.Text = snapshot.Error == NativeTransferImportError.None ? string.Empty : Label(ErrorKey(snapshot.Error));

        if (snapshot.Inventory is not { } inventory)
        {
            _summary.Text = ResultText(snapshot.Result);
            _details.Text = string.Empty;
            return;
        }

        _summary.Text = string.Join(Environment.NewLine,
            Label("transfer.import.summary", inventory.Threads.Length, inventory.Posts,
                inventory.UniquePayloads, inventory.UniquePayloadBytes),
            Counts("transfer.import.originals", inventory.CurrentOriginals),
            Counts("transfer.import.previews", inventory.CurrentPreviews),
            Counts("transfer.import.retained", inventory.RetainedBindings),
            PlanText(snapshot.Plan),
            ResultText(snapshot.Result));
        _details.Text = string.Join(Environment.NewLine, inventory.Threads.Select(thread =>
            Label("transfer.import.thread", thread.Id.Source, thread.Id.Board, thread.Id.Thread, thread.Posts,
                Counts("transfer.import.originals", thread.CurrentOriginals),
                Counts("transfer.import.previews", thread.CurrentPreviews),
                Counts("transfer.import.retained", thread.RetainedBindings))));
    }

    private string PlanText(NativeTransferImportPlanSummary? plan)
    {
        if (plan is not { } value) return string.Empty;
        return value.IsMerge
            ? Label("transfer.import.planMerge", value.NewPosts, value.UpdatedPosts, value.NewAttachments,
                value.Conflicts, value.EvidenceAdditions, value.DerivedDescriptorAdditions)
            : Label("transfer.import.planNew", value.NewPosts, value.NewAttachments,
                value.EvidenceAdditions, value.DerivedDescriptorAdditions);
    }

    private string ResultText(NativeTransferImportResult? result)
    {
        if (result is not { } value) return string.Empty;
        string text = Label("transfer.import.result", value.NewThreads, value.NewPosts,
            value.UpdatedPosts, value.NewAttachments, value.Conflicts);
        if (value.IsReplay) text += Environment.NewLine + Label("transfer.import.replay");
        return text;
    }

    private string Counts(string key, TransferImportStatusCounts counts) =>
        Label(key, counts.Contained, counts.Omitted, counts.Unknown, counts.Unavailable, counts.Failed, counts.Cancelled);

    private static string StateKey(NativeTransferImportSnapshot snapshot) => snapshot.State switch
    {
        NativeTransferImportState.Reading => "transfer.import.reading",
        NativeTransferImportState.Ready => "transfer.import.ready",
        NativeTransferImportState.Applying => "transfer.import.applying",
        NativeTransferImportState.Completed => "transfer.import.completed",
        NativeTransferImportState.NeedsReview => "transfer.import.needsReview",
        NativeTransferImportState.Unsupported => "transfer.import.unsupported",
        NativeTransferImportState.Canceled => "transfer.import.canceled",
        NativeTransferImportState.RecoveryRequired => "transfer.import.recoveryRequired",
        NativeTransferImportState.Closed => "transfer.import.closed",
        NativeTransferImportState.Failed => "transfer.import.failed",
        _ => "transfer.import.idle"
    };

    private static string ErrorKey(NativeTransferImportError error) => error switch
    {
        NativeTransferImportError.InvalidSource => "transfer.import.error.source",
        NativeTransferImportError.InvalidWorkDirectory => "transfer.import.error.workDirectory",
        NativeTransferImportError.InvalidPackage or NativeTransferImportError.UnsupportedPackage => "transfer.import.error.package",
        NativeTransferImportError.PrivateMetadataUnsupported => "transfer.import.error.private",
        NativeTransferImportError.LimitExceeded => "transfer.import.error.limit",
        NativeTransferImportError.TargetUnavailable => "transfer.import.error.target",
        NativeTransferImportError.ProfileUnavailable => "transfer.import.error.profile",
        NativeTransferImportError.ApplyFailure => "transfer.import.error.apply",
        _ => "transfer.import.error.read"
    };

    private void ApplyLabels()
    {
        if (_closed) return;
        SetButtonLabel(_choose, "transfer.import.choose");
        SetButtonLabel(_cancel, "transfer.import.cancel");
        SetButtonLabel(_apply, "transfer.import.apply");
        SetButtonLabel(_close, "transfer.import.close");
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
