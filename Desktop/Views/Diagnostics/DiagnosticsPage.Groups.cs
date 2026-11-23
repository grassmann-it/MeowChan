using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class DiagnosticsPage
{
    private bool _problemsCollapsed;
    private bool _activityCollapsed;
    private DiagnosticEntry[] _diagnosticSelection = [];
    private DiagnosticEntry? _diagnosticLead;
    private string? _diagnosticGroupLanguage;
    private bool _changingDiagnosticGrid;
    private DataGrid DiagnosticGrid => this.FindControl<DataGrid>("DiagnosticsGrid")!;
    private DiagnosticEntry? CurrentDiagnosticLead => _diagnosticLead is { } lead
        && (_changingDiagnosticGrid || _refreshingDiagnostics || DiagnosticGroupCollapsed(lead.IsProblem))
        ? lead : DiagnosticGrid.SelectedItem as DiagnosticEntry;

    private HashSet<string> DiagnosticSelectionKeys()
    {
        var selected = _diagnosticSelection.Where(entry => _changingDiagnosticGrid || DiagnosticGroupCollapsed(entry.IsProblem))
            .Concat(DiagnosticGrid.SelectedItems.OfType<DiagnosticEntry>())
            .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        if (CurrentDiagnosticLead is { } lead) selected.Add(lead.Key);
        return selected;
    }

    private sealed record DiagnosticRowGroup(bool Problems, string Label, int Count)
    {
        public override string ToString() => DiagnosticGroupHeading(Label, Count);
    }

    private static string DiagnosticGroupHeading(string label, int count) => label + " · " + Texts.Count("diagnostics.groupEntries", count);

    private sealed class DiagnosticGroupDescription(IReadOnlyDictionary<bool, DiagnosticRowGroup> groups) : DataGridGroupDescription
    {
        public override object GroupKeyFromItem(object item, int level, CultureInfo culture) => groups[((DiagnosticEntry)item).IsProblem];
    }

    private void InitializeDiagnosticGroups()
    {
        DiagnosticGrid.AddHandler(PointerPressedEvent, OnDiagnosticGroupPress, RoutingStrategies.Tunnel);
        DiagnosticGrid.AddHandler(KeyDownEvent, OnDiagnosticGroupKeyDown, RoutingStrategies.Tunnel);
        DiagnosticGrid.Sorting += (_, _) => PreserveDiagnosticSelectionDuringGridChange();
    }

    private void RefreshDiagnosticGroups()
    {
        _diagnosticGroupLanguage = Texts.Language;
        var groups = _visibleDiagnostics.GroupBy(entry => entry.IsProblem).ToDictionary(group => group.Key,
            group => new DiagnosticRowGroup(group.Key,
                Texts.T(group.Key ? "diagnostics.groupProblems" : "diagnostics.groupActivity"), group.Count()));
        DataGridCollectionView view = new(_visibleDiagnostics.OrderByDescending(entry => entry.IsProblem).ToArray());
        view.GroupDescriptions.Add(new DiagnosticGroupDescription(groups));
        _refreshingDiagnostics = true;
        try
        {
            DiagnosticGrid.ItemsSource = view;
            ApplyDiagnosticGroupVisibility();
        }
        finally { _refreshingDiagnostics = false; }
    }

    // Avalonia moves the current cell onto a group header on pointer-down. The header is a
    // disclosure control here: moving to it must leave the selected diagnostics and details alone.
    private void OnDiagnosticGroupPress(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            || !e.GetCurrentPoint(DiagnosticGrid).Properties.IsLeftButtonPressed
            || !source.GetSelfAndVisualAncestors().OfType<DataGridRowGroupHeader>().Any()) return;
        PreserveDiagnosticSelectionDuringGridChange();
    }

    private void PreserveDiagnosticSelectionDuringGridChange()
    {
        if (_ended || _changingDiagnosticGrid) return;
        _changingDiagnosticGrid = true;
        Dispatcher.UIThread.Post(() =>
        {
            _changingDiagnosticGrid = false;
            if (!_ended) ApplyDiagnosticGroupVisibility();
        }, DispatcherPriority.Input);
    }

    private void OnDiagnosticGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DataGridCollectionViewGroup { Key: DiagnosticRowGroup group } })
            SetDiagnosticGroupCollapsed(group.Problems, !DiagnosticGroupCollapsed(group.Problems));
    }

    private void OnDiagnosticGroupKeyDown(object? sender, KeyEventArgs e)
    {
        if (!DiagnosticGrid.IsFocused || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right)) return;
        var current = DiagnosticGrid.GetVisualDescendants().OfType<DataGridRowGroupHeader>()
            .FirstOrDefault(header => header.Classes.Contains(":current"));
        if (current?.DataContext is not DataGridCollectionViewGroup { Key: DiagnosticRowGroup group }) return;
        e.Handled = true;
        SetDiagnosticGroupCollapsed(group.Problems, e.Key == Key.Left);
    }

    private bool DiagnosticGroupCollapsed(bool problems) => problems ? _problemsCollapsed : _activityCollapsed;

    private void SetDiagnosticGroupCollapsed(bool problems, bool collapsed)
    {
        if (_ended || DiagnosticGroupCollapsed(problems) == collapsed) return;
        if (problems) _problemsCollapsed = collapsed;
        else _activityCollapsed = collapsed;
        ApplyDiagnosticGroupVisibility();
        RememberDiagnosticGroupLayout(problems, collapsed);
    }

    private void ApplyDiagnosticGroupVisibility()
    {
        if (DiagnosticGrid.ItemsSource is not DataGridCollectionView { Groups: { } groups }) return;
        bool refreshing = _refreshingDiagnostics;
        _refreshingDiagnostics = true;
        try
        {
            foreach (DataGridCollectionViewGroup group in groups.OfType<DataGridCollectionViewGroup>())
                if (DiagnosticGroupCollapsed(((DiagnosticRowGroup)group.Key).Problems))
                    DiagnosticGrid.CollapseRowGroup(group, false);
                else
                    DiagnosticGrid.ExpandRowGroup(group, false);
            RestoreExpandedDiagnosticSelection();
        }
        finally { _refreshingDiagnostics = refreshing; }
    }

    // A collapsed row cannot be made current without reopening it in DataGrid. Keep its marked
    // identity in the page and put only the expanded rows back into the native selection.
    private void RestoreExpandedDiagnosticSelection()
    {
        DiagnosticEntry[] expanded = _diagnosticSelection.Where(entry => !DiagnosticGroupCollapsed(entry.IsProblem)).ToArray();
        foreach (DiagnosticEntry hidden in DiagnosticGrid.SelectedItems.OfType<DiagnosticEntry>()
            .Where(entry => DiagnosticGroupCollapsed(entry.IsProblem)).ToArray())
            DiagnosticGrid.SelectedItems.Remove(hidden);
        DataGridSelection.Select(DiagnosticGrid, expanded, _diagnosticLead);
    }
}
