using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using oChan.Desktop;
using oChan.Desktop.Localization;

namespace oChan;

public partial class DiagnosticsPage : UserControl
{
    private readonly Func<DiagnosticTarget, bool>? _openWatch;
    private readonly Func<string, Task>? _copy;
    private IReadOnlyList<DiagnosticEntry> _entries = Array.Empty<DiagnosticEntry>();
    private bool _available;
    private bool _ended;

    public DiagnosticsPage() { InitializeComponent(); }
    internal DiagnosticsPage(Func<DiagnosticTarget, bool> openWatch, Func<string, Task> copy) : this()
    {
        _openWatch = openWatch;
        _copy = copy;
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
            if (control is Button button) button.Content = Texts.T(key);
        }
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
        this.FindControl<TextBlock>("DiagnosticsActionError")!.Text = string.Empty;
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
    internal void EndLifetime() { _ended = true; _available = false; }
}
