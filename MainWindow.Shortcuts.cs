using System;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class MainWindow
{
    // Feature registrations are installed after both this router and their page/owner exist.
    private CommandRouter? _shortcutRouter;
    private void InitializeShortcutRouting()
    {
        _shortcutRouter ??= new CommandRouter(this);
        ShortcutService.Changed += OnShortcutsChanged;
    }
    private void RegisterSectionCommands()
    {
        if (_shortcutRouter == null) return;
        void Register(Control owner, Func<bool> active, Func<bool> canOpen, Action open,
            Func<string?> selected, Func<string, bool> canGo, Action<string> go)
        {
            _shortcutRouter.Register(new CommandRegistration("sections.open-navigation", owner, active,
                canOpen, open, Priority: 100));
            _shortcutRouter.Register(new CommandRegistration("sections.go-to", owner, active,
                () => selected() is string id && canGo(id),
                () => { if (selected() is string id) go(id); }, Priority: 100));
        }
        if (_settingsEditor != null)
            Register(_settingsEditor, () => _selectedPage == WorkspacePage.Settings && !_largeReaderOpen && _gallery == null,
                () => _settingsEditor.CanOpenSectionNavigation, _settingsEditor.OpenSectionNavigation,
                () => _settingsEditor.SelectedSectionId, _settingsEditor.CanGoToSection, _settingsEditor.GoToSection);
        if (_pluginsPage != null)
        {
            Register(_pluginsPage, () => _selectedPage == WorkspacePage.Plugins && !_largeReaderOpen && _gallery == null,
                () => _pluginsPage.CanOpenSectionNavigation, _pluginsPage.OpenSectionNavigation,
                () => _pluginsPage.SelectedSectionId, _pluginsPage.CanGoToSection, _pluginsPage.GoToSection);
            foreach (string id in PluginsPage.PluginActionCommandIds)
                _shortcutRouter.Register(new CommandRegistration(id, _pluginsPage,
                    () => _selectedPage == WorkspacePage.Plugins && !_largeReaderOpen && _gallery == null,
                    () => _pluginsPage.CanExecutePluginAction(id),
                    () => _ = _pluginsPage.ExecuteSelectedPluginActionAsync(id),
                    () => _pluginsPage.PluginActionDisabledReason(id), Priority: 100));
        }
    }

    private void RegisterRuleTemplateCommands()
    {
        if (_shortcutRouter == null || _rulesPage == null) return;
        foreach (string suffix in new[] { "create", "edit", "delete", "preview", "apply", "undo-apply" })
        {
            string id = "rules.template-" + suffix;
            _shortcutRouter.Register(new CommandRegistration(id, _rulesPage,
                () => _selectedPage == WorkspacePage.Rules && !_largeReaderOpen && _gallery == null,
                () => _rulesPage.CanExecuteTemplateCommand(id),
                () => _ = _rulesPage.ExecuteTemplateCommandAsync(id), Priority: 100));
        }
    }

    private void DisposeShortcutRouting()
    {
        ShortcutService.Changed -= OnShortcutsChanged;
        _shortcutRouter?.Dispose(); _shortcutRouter = null;
    }
    private void OnShortcutsChanged()
    {
        LegacyCommandAdapters.Register(this);
        RefreshShellCommands();
        // Rebuild an already visible help panel from the same committed snapshot.
        if (WorkspaceControl<Border>("ShellHelpOverlay").IsVisible)
        { DismissShellHelp(); ShowShellHelp(); }
    }
    private bool HasBlockingPageDecision() =>
        _selectedPage == WorkspacePage.Rules && _rulesPage?.IsRuleConfirmationOpen == true
        || _selectedPage == WorkspacePage.Plugins && _pluginsPage?.IsDiscardConfirmationOpen == true;
    private bool RouteWorkspaceShortcut(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || !IsEnabled || _isClosing || HasVisibleShellDialog()
            || HasBlockingPageDecision() || _archivePage?.IsReaderDialogOpen == true
            || WorkspaceControl<Border>("ShellHelpOverlay").IsVisible || (_gallery?.IsOverlayOpen ?? false)) return false;
        if (_shortcutRouter?.Route(e) == true) return true;
        if (!CanExecuteShellPowerCommand(this) || CommandRouter.IsNativeInput(e.Source, e.Key)) return false;
        if (_gallery != null && !_gallerySuspended) return CommandBindings.RouteVisibleActions(_gallery, e);
        if (_largeReaderOpen && _archivePage?.FindControl<Control>("ArchiveReaderPane") is Control pane)
        {
            // The reader's tunnel handler exclusively owns these navigation
            // commands, including its native-control exclusions. Retrying an
            // unhandled gesture via the visible buttons bypasses that policy.
            if (ShortcutService.Current.Matches(e, "reader.previous-post")
                || ShortcutService.Current.Matches(e, "reader.next-post")
                || ShortcutService.Current.Matches(e, "reader.previous-thread")
                || ShortcutService.Current.Matches(e, "reader.next-thread")) return false;
            return CommandBindings.RouteVisibleActions(pane, e);
        }
        return CommandBindings.RouteVisibleActions(this, e);
    }
}
