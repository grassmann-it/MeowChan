using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// Work views (plan AP5, WV1): the app menu's complete "view" part (R3, R8) and the bindable view-switch command.
// Group 3 of the app menu gains: Change theme, Enter / exit fullscreen, and "View" with the three views as a radio group
// plus "Switch work view". Nothing here depends on which view is active except the radio's checked entry.
public partial class MainWindow
{
    private MenuItem? _themeMenu, _fullscreenMenu, _viewCycleMenu, _workViewGroupMenu;
    private readonly Dictionary<WorkView, MenuItem> _workViewRadios = new();

    private void InitializeWorkViewMenu()
    {
        if (_workViewGroupMenu != null) return;
        InitializeWorkViewSwitchNotices();
        _themeMenu = new MenuItem { Name = "ThemeMenuItem" };
        // The button's own handler owns the busy/restore guards and saves the choice; the menu entry only presses it.
        _themeMenu.Click += (_, _) => ThemeToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _fullscreenMenu = new MenuItem { Name = "FullscreenMenuItem" };
        _fullscreenMenu.Click += (_, _) => ToggleFullscreen();
        _viewCycleMenu = new MenuItem { Name = "WorkViewCycleMenuItem" };
        _viewCycleMenu.Click += (_, _) => _ = CycleWorkViewAsync();
        _workViewGroupMenu = new MenuItem { Name = "WorkViewMenuItem" };
        foreach (WorkView view in WorkViewSetting.All)
        {
            MenuItem radio = new() { Name = view.ToSetting() + "WorkViewMenuItem", ToggleType = MenuItemToggleType.Radio, GroupName = "WorkViewMenu" };
            WorkView captured = view;
            radio.Click += (_, _) => _ = SetWorkViewAsync(captured);
            _workViewRadios[view] = radio;
            _workViewGroupMenu.Items.Add(radio);
        }
        _workViewGroupMenu.Items.Add(new Separator());
        _workViewGroupMenu.Items.Add(_viewCycleMenu);
        WorkViewElement.SetId(_workViewGroupMenu, "shell.appmenu.view-group");
        WorkViewElement.SetId(_viewCycleMenu, "shell.appmenu.view-cycle");
        WorkViewElement.SetId(_themeMenu, "shell.appmenu.theme");
        WorkViewElement.SetId(_fullscreenMenu, "shell.appmenu.fullscreen");
        // After "Pause downloads": the application group reads Pause, Theme, Fullscreen, View, Settings, Shortcuts, About, Exit.
        int at = WorkspaceMenuItem.Items.IndexOf(PauseMenuItem) + 1;
        WorkspaceMenuItem.Items.Insert(at, _themeMenu);
        WorkspaceMenuItem.Items.Insert(at + 1, _fullscreenMenu);
        WorkspaceMenuItem.Items.Insert(at + 2, _workViewGroupMenu);
        _shortcutRouter?.Register(new CommandRegistration("workspace.view-cycle", this, () => true, () => CanSwitchWorkView,
            () => _ = CycleWorkViewAsync()));
        ActualThemeVariantChanged += (_, _) => RefreshWorkViewMenu();
        RefreshWorkViewMenu();
    }

    /// <summary>Whether a view switch may run now: the same guard as the footer's view selector.</summary>
    private bool CanSwitchWorkView => _workspaceInitialized && _restoreCompleted && !_isClosing && IsEnabled
        && !WorkspaceCommandBusy && WorkspaceModeComboBox.IsEnabled && !HasVisibleShellDialog();

    /// <summary>Saves <paramref name="view"/> as the work view; the window applies it at once (no restart, same session).</summary>
    private async Task SetWorkViewAsync(WorkView view, WorkViewSwitchNoticeState.Notice? undoNotice = null)
    {
        WorkViewSwitchNoticeState.UndoRequest? undo = null;
        try
        {
            if (CanSwitchWorkView && view != ConfiguredWorkView)
            {
                if (undoNotice != null)
                {
                    if (!WorkViewSwitchNoticeEnabled) return;
                    undo = _workViewSwitchNotices.BeginUndo(undoNotice, ConfiguredWorkView);
                    if (undo == null) return; // Expired, dismissed, or superseded by a more recent switch.
                }
                else _workViewSwitchNotices.BeginSwitch();
                CloseWorkViewSwitchToast();
                Config candidate = _config.Clone();
                candidate.WorkspaceMode = view.ToSetting();
                await SaveAppearanceAsync(candidate);
            }
        }
        finally
        {
            if (undo != null) _workViewSwitchNotices.EndUndo(undo);
            // A refused or failed change must not leave a different radio checked.
            if (!_isClosing) RefreshWorkViewMenu();
        }
    }

    /// <summary>Barebones, Simple, Advanced, and back to Barebones: the bindable "workspace.view-cycle" command.</summary>
    internal Task CycleWorkViewAsync() => SetWorkViewAsync(NextWorkView(ConfiguredWorkView));

    internal static WorkView NextWorkView(WorkView view) =>
        WorkViewSetting.All[(WorkViewSetting.All.ToList().IndexOf(view) + 1) % WorkViewSetting.All.Count];

    private string ThemeMenuLabel() => Texts.T(ActualThemeVariant == ThemeVariant.Dark ? "shell.switchToLight" : "shell.switchToDark");

    /// <summary>Labels, checked view and availability of the entries above; called when the language, theme, view or readiness changes.</summary>
    private void RefreshWorkViewMenu()
    {
        RefreshWorkViewSwitchNoticeAvailability();
        RefreshBooruToolsPresentation();
        if (_workViewGroupMenu == null || _themeMenu == null || _fullscreenMenu == null || _viewCycleMenu == null) return;
        WorkView current = ConfiguredWorkView;
        CommandBindings.Apply(_themeMenu, "appearance.theme", label: ThemeMenuLabel);
        CommandBindings.Apply(_fullscreenMenu, "window.fullscreen");
        CommandBindings.Apply(_viewCycleMenu, "workspace.view-cycle");
        _workViewGroupMenu.Header = Texts.T("shell.mode");
        AutomationProperties.SetName(_workViewGroupMenu, Texts.T("shell.mode"));
        foreach ((WorkView view, MenuItem radio) in _workViewRadios)
        {
            string key = "mode." + view.ToSetting().ToLowerInvariant();
            radio.Header = Texts.T(key);
            radio.IsChecked = view == current;
            AutomationProperties.SetName(radio, Texts.T(key));
            AutomationProperties.SetHelpText(radio, Texts.T(key + "Description"));
        }
        bool ready = WorkspaceModeComboBox.IsEnabled;
        _themeMenu.IsEnabled = ThemeToggleButton.IsEnabled;
        _workViewGroupMenu.IsEnabled = ready;
    }

    /// <summary>The header buttons whose presence the policy owns. All rows are neutral today, so every view shows them.</summary>
    private void ApplyHeaderWorkView(WorkView view)
    {
        WorkViewGate.Apply(FullscreenButton, view);
        WorkViewGate.Apply(ThemeToggleButton, view);
    }

    /// <summary>R13: on entering a view the Downloads sections return to the policy's state (collapsed); the user's later choice stays.</summary>
    private void ApplyDownloadsSections(WorkView view, bool viewChanged)
    {
        WorkViewPresenter.ApplyExpander(AdmissionResultsExpander, view, viewChanged);
        WorkViewPresenter.ApplyExpander(InspectorSourceDetails, view, viewChanged);
    }
}
