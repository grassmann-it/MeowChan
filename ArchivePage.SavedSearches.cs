using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class ArchivePage
{
    private DesktopSessionHost? _savedSearchHost;
    private bool _savedSearchDialogOpen;
    private bool _savedSearchLifecycleHooked;
    private CommandRouter? _savedSearchRouter;
    private readonly List<IDisposable> _savedSearchRegistrations = new();
    internal bool CanSaveCurrentSearch => _savedSearchHost?.State == DesktopSessionState.Ready && _available && !_ended && !_busy && AppliedSearchSpec != null;
    internal ArchiveSearchSpec? CaptureAppliedSearch() => CanSaveCurrentSearch && AppliedSearchSpec is { } spec
        ? ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(spec)) : null;

    internal void AttachSavedSearches(DesktopSessionHost host)
    {
        _savedSearchHost = host;
        if (!_savedSearchLifecycleHooked)
        {
            _savedSearchLifecycleHooked = true;
            AttachedToVisualTree += (_, _) => AttachSavedSearches(_savedSearchHost!);
            DetachedFromVisualTree += (_, _) => StopSavedSearchRouting();
        }
        if (_savedSearchRouter != null) return;
        _savedSearchRouter = new CommandRouter(this);
        void Register(string suffix, Action execute, Func<bool> canExecute)
        {
            string id = "archive.saved-searches." + suffix;
            _savedSearchRegistrations.Add(_savedSearchRouter.Register(new CommandRegistration(id, this,
                () => !_ended && !_savedSearchDialogOpen && !_largeReader && IsEffectivelyVisible
                    && Control<Grid>("ArchiveListPane").IsEffectivelyVisible && Control<Grid>("ArchiveListPane").IsKeyboardFocusWithin,
                canExecute, execute, Priority: 100)));
        }
        Register("open-manager", () => _ = OpenSavedSearchesAsync(false), CanOpenSavedSearches);
        Register("save-current", () => _ = OpenSavedSearchesAsync(true), () => CanOpenSavedSearches() && CanSaveCurrentSearch);
        AddHandler(KeyDownEvent, RouteSavedSearchCommand, RoutingStrategies.Bubble);
        SearchStateChanged += RefreshSavedSearchActions;
        RefreshSavedSearchActions();
    }
    private void StopSavedSearchRouting()
    {
        foreach (IDisposable registration in _savedSearchRegistrations) registration.Dispose();
        _savedSearchRegistrations.Clear(); _savedSearchRouter?.Dispose(); _savedSearchRouter = null;
        SearchStateChanged -= RefreshSavedSearchActions;
        RemoveHandler(KeyDownEvent, RouteSavedSearchCommand);
    }
    private bool CanOpenSavedSearches() => _savedSearchHost?.State == DesktopSessionState.Ready && _available && !_ended && !_savedSearchDialogOpen;
    private void RouteSavedSearchCommand(object? sender, KeyEventArgs e) => _savedSearchRouter?.Route(e);
    private void OnSaveCurrentSearchClick(object? sender, RoutedEventArgs e) => _ = OpenSavedSearchesAsync(true);
    private void OnSavedSearchesClick(object? sender, RoutedEventArgs e) => _ = OpenSavedSearchesAsync(false);
    private async Task OpenSavedSearchesAsync(bool saveCurrent)
    {
        if (!CanOpenSavedSearches() || _savedSearchHost == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        ArchiveSearchSpec? frozen = saveCurrent ? CaptureAppliedSearch() : null;
        if (saveCurrent && frozen == null) return;
        Control restore = Control<Button>(saveCurrent ? "ArchiveSaveSearch" : "ArchiveSavedSearches");
        _savedSearchDialogOpen = true; RefreshSavedSearchActions();
        try
        {
            var dialog = new SavedArchiveSearchesDialog(_savedSearchHost, this, frozen)
            {
                Width = Math.Max(360, Math.Min(650, owner.ClientSize.Width - 32)),
                Height = Math.Max(330, Math.Min(510, owner.ClientSize.Height - 32))
            };
            using CancellationTokenRegistration ending = _lifetime.Token.Register(() =>
                Dispatcher.UIThread.Post(dialog.CloseForLifetime));
            await dialog.ShowDialog(owner);
            if (!_ended)
            {
                if (dialog.Applied) { if (_threadTable) Control<DataGrid>("ArchiveThreadTable").Focus(); else Control<ListBox>("ArchiveThreads").Focus(); }
                else restore.Focus();
            }
        }
        catch (Exception error) { if (!_ended) Message(error.Message); }
        finally { _savedSearchDialogOpen = false; if (!_ended) RefreshSavedSearchActions(); }
    }
    private void RefreshSavedSearchActions()
    {
        foreach (var item in new[] { ("ArchiveSaveSearch", "save-current"), ("ArchiveSavedSearches", "open-manager") })
        {
            Button button = Control<Button>(item.Item1);
            CommandBindings.Apply(button, "archive.saved-searches." + item.Item2);
            button.IsEnabled = CanOpenSavedSearches() && (item.Item2 != "save-current" || CanSaveCurrentSearch);
            AutomationProperties.SetHelpText(button, CommandBindings.Tooltip("archive.saved-searches." + item.Item2));
        }
    }
}
