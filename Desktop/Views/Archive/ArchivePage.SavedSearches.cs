using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    internal bool CanSaveCurrentSearch => _savedSearchHost?.State == DesktopSessionState.Ready && _available && !_ended && !IsFilenameSearchOpen && !_busy && AppliedSearchSpec != null;
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
                    && ArchiveListPane.IsEffectivelyVisible && ArchiveListPane.IsKeyboardFocusWithin,
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
    private bool CanOpenSavedSearches() => _savedSearchHost?.State == DesktopSessionState.Ready && _available && !_ended && !IsFilenameSearchOpen && !_savedSearchDialogOpen;
    private void RouteSavedSearchCommand(object? sender, KeyEventArgs e) => _savedSearchRouter?.Route(e);
    private void OnSavedSearchesClick(object? sender, RoutedEventArgs e) => RunSavedSearchMenu(false);
    // AP2: one split button. Its main part opens the saved searches; its arrow menu holds
    // "Save applied search…" and, for completeness, "Saved searches…".
    private MenuItem? _saveCurrentSearchItem, _openSavedSearchesItem;
    internal MenuItem SaveCurrentSearchMenuItem => EnsureSavedSearchMenu().Save;
    internal MenuItem OpenSavedSearchesMenuItem => EnsureSavedSearchMenu().Open;
    private (MenuItem Save, MenuItem Open) EnsureSavedSearchMenu()
    {
        if (_saveCurrentSearchItem != null && _openSavedSearchesItem != null) return (_saveCurrentSearchItem, _openSavedSearchesItem);
        _saveCurrentSearchItem = CommandBindings.CreateMenuItem("archive.saved-searches.save-current", () => RunSavedSearchMenu(true),
            () => CanOpenSavedSearches() && CanSaveCurrentSearch);
        _saveCurrentSearchItem.Name = "ArchiveSaveSearch";
        _openSavedSearchesItem = CommandBindings.CreateMenuItem("archive.saved-searches.open-manager", () => RunSavedSearchMenu(false),
            CanOpenSavedSearches);
        _openSavedSearchesItem.Name = "ArchiveSavedSearchesOpen";
        MenuFlyout menu = (MenuFlyout)ArchiveSavedSearches.Flyout!;
        menu.Items.Add(_saveCurrentSearchItem);
        menu.Items.Add(_openSavedSearchesItem);
        // The arrow part names itself, so assistive technology does not read a bare "button": at the first template pass here, and again
        // from ApplySavedSearchSplit (a template that was applied before this ran, and a language change).
        ArchiveSavedSearches.TemplateApplied += (_, _) => NameSavedSearchArrow();
        // The arrow part is not focusable (Semi template), so the keyboard opens its menu from the split button like the scope chip of the
        // search field: Alt+Down or F4. Enter and Space keep opening the saved searches (the main part).
        ArchiveSavedSearches.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || !((e.Key == Key.Down && e.KeyModifiers == KeyModifiers.Alt) || (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None))) return;
            if (ArchiveSavedSearches.IsEnabled && ArchiveSavedSearches.Flyout is { } flyout) { flyout.ShowAt(ArchiveSavedSearches); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        return (_saveCurrentSearchItem, _openSavedSearchesItem);
    }
    private void NameSavedSearchArrow()
    {
        string name = MeowChan.Desktop.Localization.Texts.T("savedSearch.split.more");
        foreach (Button more in ArchiveSavedSearches.GetTemplateChildren().OfType<Button>().Where(button => button.Name == "PART_SecondaryButton"))
        {
            AutomationProperties.SetName(more, name);
            ToolTip.SetTip(more, name);
        }
    }
    private void RunSavedSearchMenu(bool saveCurrent)
    {
        // A menu entry inside the toolbar overflow closes that overflow before the dialog takes over.
        if (ArchiveResultActions.More is { IsChecked: true } more) more.IsChecked = false;
        _ = OpenSavedSearchesAsync(saveCurrent);
    }
    private async Task OpenSavedSearchesAsync(bool saveCurrent)
    {
        if (!CanOpenSavedSearches() || _savedSearchHost == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        ArchiveSearchSpec? frozen = saveCurrent ? CaptureAppliedSearch() : null;
        if (saveCurrent && frozen == null) return;
        Control restore = ActionOriginFocusTarget(ArchiveSavedSearches) ?? ArchiveSavedSearches;
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
                if (dialog.Applied) { if (_threadTable) ArchiveThreadTable.Focus(); else ArchiveThreads.Focus(); }
                else restore.Focus();
            }
        }
        catch (Exception error) { if (!_ended) Message(error.Message); }
        finally { _savedSearchDialogOpen = false; if (!_ended) RefreshSavedSearchActions(); }
    }
    private void RefreshSavedSearchActions()
    {
        UpdateFilenameSearchActions();
        (MenuItem save, MenuItem open) = EnsureSavedSearchMenu();
        CommandBindings.Apply(save, "archive.saved-searches.save-current");
        CommandBindings.Apply(open, "archive.saved-searches.open-manager");
        CommandBindings.Refresh(save);
        CommandBindings.Refresh(open);
        ApplySavedSearchSplit();
    }
    // The split button is no Button, so it gets the catalog label, tooltip and help text here.
    private void ApplySavedSearchSplit()
    {
        const string id = "archive.saved-searches.open-manager";
        SplitButton split = ArchiveSavedSearches;
        EnsureSavedSearchMenu();
        string label = CommandCatalog.Label(id);
        if (!Equals(split.Content, label)) split.Content = label;
        string tip = CommandBindings.Tooltip(id) + " · " + CommandCatalog.Label("archive.saved-searches.save-current");
        ToolTip.SetTip(split, tip);
        AutomationProperties.SetName(split, label);
        AutomationProperties.SetHelpText(split, tip);
        split.IsEnabled = CanOpenSavedSearches();
        NameSavedSearchArrow();
    }
}
