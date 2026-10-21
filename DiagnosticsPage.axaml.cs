using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    public DiagnosticsPage() { InitializeComponent(); InitializeContextMenu(); }
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
        DiagnosticsSnapshot snapshot = DiagnosticsSnapshot.From(view);
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
        RefreshSelection();
    }

    private DiagnosticEntry? Selected => this.FindControl<DataGrid>("DiagnosticsGrid")!.SelectedItem as DiagnosticEntry;
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshSelection();
    private void RefreshSelection()
    {
        this.FindControl<TextBox>("DiagnosticDetails")!.Text = Selected?.Details ?? string.Empty;
        this.FindControl<Button>("CopyDiagnosticButton")!.IsEnabled = _available && Selected != null;
        this.FindControl<Button>("OpenDiagnosticWatchButton")!.IsEnabled = _available && Selected?.Target != null;
        CommandBindings.ApplyLabeled(this.FindControl<Button>("RetryDiagnosticButton")!, "watch.check",
            Texts.T(Selected?.Target?.Kind == DiagnosticTargetKind.Board ? "check.boardAction" : "check.action"));
        this.FindControl<Button>("RetryDiagnosticButton")!.IsEnabled = _available && !_retryBusy
            && Selected?.Target is DiagnosticTarget target && _retry != null && _canRetry?.Invoke(target) == true;
        this.FindControl<TextBlock>("DiagnosticsActionError")!.Text = string.Empty;
        this.FindControl<TextBlock>("DiagnosticsActionError")!.Classes.Set("error", true);
    }
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || Selected is not DiagnosticEntry selected || _copy == null) return;
        try { await _copy(selected.Details); }
        catch (Exception)
        {
            if (!_ended) this.FindControl<TextBlock>("DiagnosticsActionError")!.Text = Texts.T("clipboard.writeFailed");
        }
    }
    private void OnOpenWatchClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || Selected?.Target is not DiagnosticTarget target) return;
        if (_openWatch?.Invoke(target) != true)
            this.FindControl<TextBlock>("DiagnosticsActionError")!.Text = Texts.T("diagnostics.removed");
    }
    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || _retryBusy || Selected?.Target is not DiagnosticTarget target
            || _retry == null || _canRetry?.Invoke(target) != true) return;
        _retryBusy = true;
        RefreshSelection();
        string message;
        bool failed;
        try
        {
            CheckRequestStatus result = await _retry(target);
            message = CheckActionText.ForResult(result, target.Kind);
            failed = result is not (CheckRequestStatus.Scheduled or CheckRequestStatus.AlreadyScheduled);
        }
        catch (Exception exception) { message = Texts.T("check.failed", exception.Message); failed = true; }
        finally { _retryBusy = false; }
        if (_ended) return;
        RefreshSelection();
        if (Selected?.Target != target) return;
        this.FindControl<TextBlock>("DiagnosticsActionError")!.Text = message;
        this.FindControl<TextBlock>("DiagnosticsActionError")!.Classes.Set("error", failed);
    }
    internal void EndLifetime() { _ended = true; _available = false; }
}
