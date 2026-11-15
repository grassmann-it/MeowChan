using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan;

// Interaction audit 1.1.0: in both tables a double click on a row and Enter run the row's primary action, "Show in Downloads" (the
// toolbar button and the context menu entry), for a row that names a thread or board; the platform facts have none and stay as they
// are. Ctrl/Cmd+C copies the row's details like "Copy details" (the table's own copy of the cell texts would lose the details).
public partial class DiagnosticsPage
{
    private void InitializeRowInteraction()
    {
        foreach (string name in new[] { "DiagnosticsGrid", "PlatformGrid" })
        {
            DataGrid grid = this.FindControl<DataGrid>(name)!;
            grid.DoubleTapped += OnDiagnosticRowDoubleTapped;
            // Tunnel: the table moves to the next row on Enter and copies its cells on Ctrl+C before a bubbling handler would see the key.
            grid.AddHandler(KeyDownEvent, OnDiagnosticRowKeyDown, RoutingStrategies.Tunnel);
        }
    }

    /// <summary>Whether the row has a primary action (a target that can be shown in Downloads).</summary>
    internal static bool HasPrimaryAction(DiagnosticEntry entry) => entry.Target is DiagnosticTarget && entry.Kind != DiagnosticKind.Storage;

    private void OnDiagnosticRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid || !ItemActivation.IsItemDoubleClick(e, grid)
            || ItemActivation.ItemAt<DiagnosticEntry>(e.Source, grid) is not { } entry || !HasPrimaryAction(entry)) return;
        e.Handled = true;
        OpenFrozenDiagnostic(entry);
    }

    private async void OnDiagnosticRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid || e.Handled || ShortcutService.IsCapturing || !MainWindow.IsListCommandSource(e.Source, grid)
            || grid.SelectedItem is not DiagnosticEntry entry) return;
        if (ItemActivation.IsPrimaryKey(e))
        {
            if (!HasPrimaryAction(entry) || grid.SelectedItems.Count != 1) return;
            e.Handled = true;
            OpenFrozenDiagnostic(entry);
            return;
        }
        if (CommandBindings.Matches(e, "diagnostics.copy"))
        {
            e.Handled = true;
            if (!_ended && _available && _copy != null) await CopyFrozenDiagnosticAsync(entry);
        }
    }
}
