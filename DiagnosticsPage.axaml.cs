using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class DiagnosticsPage : UserControl
{
    private readonly Func<DiagnosticTarget, bool>? _openWatch;
    private readonly Func<string, Task>? _copy;
    private IReadOnlyList<DiagnosticEntry> _entries = Array.Empty<DiagnosticEntry>();
    private bool _available;
    private bool _retryBusy;
    private readonly Func<DiagnosticTarget, Task<CheckRequestStatus>>? _retry;
    private readonly Func<DiagnosticTarget, bool>? _canRetry;
    private bool _ended;
    private int _actionGeneration;
    private enum ActionContextKind { Global, Diagnostic, SessionFeedback }
    private readonly record struct ActionContext(ActionContextKind Kind, string? DiagnosticKey = null, long? FeedbackId = null);
    private sealed record ActionFeedback(ActionContext Context, string Message, bool Failed);
    private ActionFeedback? _actionFeedback;
    internal Func<IReadOnlyList<MeowChan.Downloader.DownloadTargetWaitState>>? GetTargetWaitStates { get; set; }
    internal Func<Task>? CheckTargetsAsync { get; set; }
    internal Func<Task>? ManageMediaRatingsAsync { get; set; }
    private async void OnManageMediaRatingsClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || ManageMediaRatingsAsync == null) return;
        int generation = BeginDiagnosticAction();
        try { await ManageMediaRatingsAsync(); }
        catch (Exception error) { ShowDiagnosticActionFeedback(generation, new(ActionContextKind.Global), error.Message); }
    }

    public DiagnosticsPage()
    {
        InitializeComponent(); InitializeContextMenu();
        CommandRouter? ratings = null;
        AttachedToVisualTree += (_, _) =>
        {
            ratings?.Dispose(); ratings = new(this);
            ratings.Register(new("media.rating.manage", this, () => IsEffectivelyVisible,
                () => !_ended && _available && ManageMediaRatingsAsync != null,
                () => OnManageMediaRatingsClick(this, new RoutedEventArgs()), Priority: 100));
        };
        DetachedFromVisualTree += (_, _) => { ratings?.Dispose(); ratings = null; };
        KeyDown += (_, e) => ratings?.Route(e);
    }
    internal DiagnosticsPage(Func<DiagnosticTarget, bool> openWatch, Func<string, Task> copy,
        Func<DiagnosticTarget, Task<CheckRequestStatus>>? retry = null, Func<DiagnosticTarget, bool>? canRetry = null) : this()
    {
        _openWatch = openWatch;
        _copy = copy;
        _retry = retry;
        _canRetry = canRetry;
    }

    internal void Refresh(DesktopSessionView view, bool available)
    {
        if (_ended) return;
        _available = available;
        DiagnosticsSnapshot snapshot = DiagnosticsSnapshot.From(view, GetTargetWaitStates?.Invoke());
        this.FindControl<TextBlock>("DiagnosticsSummary")!.Text = Texts.T("downloads.statusSummary",
            snapshot.Threads, snapshot.Boards, snapshot.Queued, snapshot.Active);
        DataGrid grid = this.FindControl<DataGrid>("DiagnosticsGrid")!;
        string? selected = (grid.SelectedItem as DiagnosticEntry)?.Key;
        if (!_entries.SequenceEqual(snapshot.Entries))
        {
            _entries = snapshot.Entries;
            grid.ItemsSource = _entries;
            grid.SelectedItem = _entries.FirstOrDefault(entry => entry.Key == selected);
        }
        this.FindControl<TextBlock>("DiagnosticsEmpty")!.IsVisible = _entries.Count == 0;
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string key || !control.Classes.Contains("localized")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button && !button.Classes.Contains("command-icon")) button.Content = Texts.T(key);
        }
        CommandBindings.ApplyLabeled(this.FindControl<Button>("CopyDiagnosticButton")!, "diagnostics.copy", Texts.T("diagnostics.copy"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("OpenDiagnosticWatchButton")!, "watch.show-in-downloads", Texts.T("diagnostics.openWatch"));
        grid.Columns[0].Header = Texts.T("diagnostics.category");
        grid.Columns[1].Header = Texts.T("diagnostics.resource");
        grid.Columns[2].Header = Texts.T("diagnostics.message");
        CommandBindings.Apply(this.FindControl<Button>("ManageMediaRatingsButton")!, "media.rating.manage");
        this.FindControl<Button>("ManageMediaRatingsButton")!.IsEnabled = _available && ManageMediaRatingsAsync != null;
        RefreshSelection();
        RefreshFeedback();
    }

    private DiagnosticEntry? Selected => this.FindControl<DataGrid>("DiagnosticsGrid")!.SelectedItem as DiagnosticEntry;
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshSelection();
    private void RefreshSelection()
    {
        this.FindControl<TextBox>("DiagnosticDetails")!.Text = Selected?.Details ?? string.Empty;
        this.FindControl<Button>("CopyDiagnosticButton")!.IsEnabled = _available && Selected != null;
        this.FindControl<Button>("OpenDiagnosticWatchButton")!.IsEnabled = _available && Selected?.Target != null;
        CommandBindings.ApplyLabeled(this.FindControl<Button>("RetryDiagnosticButton")!, "watch.check",
            Texts.T(Selected?.DownloadTargetPath != null ? "targets.check" : Selected?.Target?.Kind == DiagnosticTargetKind.Board ? "check.boardAction" : "check.action"));
        this.FindControl<Button>("RetryDiagnosticButton")!.IsEnabled = _available && !_retryBusy
            && (Selected?.DownloadTargetPath != null && CheckTargetsAsync != null
                || Selected?.Target is DiagnosticTarget target && _retry != null && _canRetry?.Invoke(target) == true);
        RenderDiagnosticActionFeedback();
    }
    private int BeginDiagnosticAction()
    {
        _actionFeedback = null;
        RenderDiagnosticActionFeedback();
        return ++_actionGeneration;
    }
    private void ShowDiagnosticActionFeedback(int generation, ActionContext context, string message, bool failed = true)
    {
        if (_ended || generation != _actionGeneration) return;
        if (context.Kind == ActionContextKind.SessionFeedback)
            AutomationProperties.SetLiveSetting(this.FindControl<TextBlock>("DiagnosticsActionError")!, AutomationLiveSetting.Assertive);
        _actionFeedback = new(context, message, failed);
        RenderDiagnosticActionFeedback();
    }
    private void RenderDiagnosticActionFeedback()
    {
        if (_ended) return;
        bool current = _actionFeedback?.Context is ActionContext context && (context.Kind switch
        {
            ActionContextKind.Global => true,
            ActionContextKind.Diagnostic => context.DiagnosticKey == Selected?.Key,
            ActionContextKind.SessionFeedback => context.FeedbackId == (this.FindControl<DataGrid>("SessionFeedbackGrid")!.SelectedItem as Desktop.Feedback.SessionFeedback)?.Id,
            _ => false
        });
        TextBlock text = this.FindControl<TextBlock>("DiagnosticsActionError")!;
        text.Text = current ? _actionFeedback!.Message : string.Empty;
        text.Classes.Set("error", !current || _actionFeedback!.Failed);
    }
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || Selected is not DiagnosticEntry selected || _copy == null) return;
        await CopyFrozenDiagnosticAsync(selected);
    }
    private void OnOpenWatchClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is DiagnosticEntry selected) OpenFrozenDiagnostic(selected);
    }
    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        DiagnosticEntry? selected = Selected;
        if (!_ended && _available && !_retryBusy && selected?.DownloadTargetPath != null && CheckTargetsAsync != null)
        {
            ActionContext context = new(ActionContextKind.Diagnostic, selected.Key);
            int generation = BeginDiagnosticAction();
            _retryBusy = true; RefreshSelection();
            string? error = null;
            try { await CheckTargetsAsync(); }
            catch (Exception exception) { error = exception.Message; }
            finally { _retryBusy = false; if (!_ended) RefreshSelection(); }
            if (error != null) ShowDiagnosticActionFeedback(generation, context, error);
            return;
        }
        if (selected != null) await RetryFrozenDiagnosticAsync(selected);
    }
    internal void EndLifetime() { _ended = true; _available = false; if (_feedback != null) _feedback.Changed -= RefreshFeedback; }
}
