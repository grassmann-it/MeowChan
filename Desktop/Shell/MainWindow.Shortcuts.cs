using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.WorkViews;

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
    // Settings and Plugins are built after the first frame, so their registrations are installed with them.
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
        if (_settingsEditor != null)
            _shortcutRouter.Register(new CommandRegistration("input.focus-settings-search", _settingsEditor,
                () => _selectedPage == WorkspacePage.Settings && !_largeReaderOpen && _gallery == null,
                () => true, _settingsEditor.FocusSearch, Priority: 100, AllowInTextInput: true));
        if (_pluginsPage != null)
        {
            foreach (string id in PluginsPage.PluginActionCommandIds)
                _shortcutRouter.Register(new CommandRegistration(id, _pluginsPage,
                    () => _selectedPage == WorkspacePage.Sources && !_largeReaderOpen && _gallery == null,
                    () => _pluginsPage.CanExecutePluginAction(id),
                    () => _ = _pluginsPage.ExecuteSelectedPluginActionAsync(id),
                    () => _pluginsPage.PluginActionDisabledReason(id), Priority: 100));
        }
    }

    // Tags and the archive filter surface act on a target that exists several times (list rows, reader thread,
    // tiles, gallery): each is resolved when the key is pressed, not taken from a visible button.
    private void RegisterTagAndFilterCommands()
    {
        if (_shortcutRouter == null) return;
        if (_archivePage is { } archive)
        {
            bool ArchiveActive() => _selectedPage == WorkspacePage.Archive && _gallery == null;
            _shortcutRouter.Register(new CommandRegistration("thread.tags", archive, ArchiveActive,
                () => archive.ThreadTagsShortcutTarget != null, archive.OpenThreadTagsByShortcut, Priority: 100));
            _shortcutRouter.Register(new CommandRegistration("archive.filters-toggle", archive,
                () => ArchiveActive() && !_largeReaderOpen, () => !archive.IsReaderDialogOpen,
                archive.ToggleFilterSurfaceByShortcut, Priority: 100, AllowInTextInput: true));
        }
        // Following live and showing the new posts belong to the open thread, in the embedded and in the large reader.
        if (_archivePage is { } live && live.FindControl<Control>("ArchiveReaderPane") is { } readerPane)
        {
            bool ReaderShown() => _gallery == null && !HasBlockingPageDecision()
                && (_largeReaderOpen || _selectedPage == WorkspacePage.Archive);
            // The thread's tags (Primary+T) in the large reader: it can be open over any page (Downloads, Media, the gallery's thread), where the archive's
            // own registration above has an invisible owner and an inactive page; the pane is the owner that is always on the screen with it.
            _shortcutRouter.Register(new CommandRegistration("thread.tags", readerPane, () => ReaderShown() && _largeReaderOpen,
                () => live.ThreadTagsShortcutTarget != null, live.OpenThreadTagsByShortcut, Priority: 100));
            _shortcutRouter.Register(new CommandRegistration("watch.follow-live", readerPane, ReaderShown,
                () => live.CanToggleFollowLiveByShortcut, live.ToggleFollowLiveByShortcut, Priority: 100));
            _shortcutRouter.Register(new CommandRegistration("reader.show-new-posts", readerPane, ReaderShown,
                () => live.CanShowNewPostsByShortcut, () => _ = live.ShowReaderUpdatesAsync(), Priority: 100));
            // R8: the media sidebar switch works in both readers and switches the sidebar of the view it is used in (the archive view shows it
            // with "Always"); its steps act wherever it is shown (CanStepMediaSidebar).
            _shortcutRouter.Register(new CommandRegistration("reader.sidebar.toggle", readerPane, ReaderShown,
                () => live.CanToggleMediaSidebar, live.ToggleMediaSidebar, Priority: 100));
            _shortcutRouter.Register(new CommandRegistration("reader.sidebar.previous-media", readerPane, ReaderShown,
                () => live.CanStepMediaSidebar(-1), () => live.StepMediaSidebar(-1), Priority: 100));
            _shortcutRouter.Register(new CommandRegistration("reader.sidebar.next-media", readerPane, ReaderShown,
                () => live.CanStepMediaSidebar(1), () => live.StepMediaSidebar(1), Priority: 100));
        }
        // The saved-ratings manager is a dialog of the shell; it opens from the media page, the archive and the gallery (which routes it itself).
        _shortcutRouter.Register(new CommandRegistration("media.rating.manage", this,
            () => _gallery == null && _selectedPage is WorkspacePage.Media or WorkspacePage.Archive,
            () => !_isClosing && !_ratingManagerOpen, () => _ = ManageMediaRatingsAsync(), Priority: 100));
        if (_mediaPage is { } media)
        {
            _shortcutRouter.Register(new CommandRegistration("media.tags.edit", media,
                () => _selectedPage == WorkspacePage.Media && !_largeReaderOpen && _gallery == null,
                () => media.SelectedTiles.Count > 0, () => _ = media.EditTagsByShortcutAsync(), Priority: 100));
            // The focus mode (M4): Shift+F is a typed capital in a text field, so the router leaves it to the field (no AllowInTextInput).
            _shortcutRouter.Register(new CommandRegistration(MediaFocusMode.CommandId, media,
                () => _selectedPage == WorkspacePage.Media && !_largeReaderOpen && _gallery == null,
                () => media.CanToggleFocusMode, media.ToggleFocusModeByShortcut, Priority: 100));
            // The media panel's switch (S-4): Primary+Shift+M, as the reader's media sidebar; the choice is the Layout menu row's (saved).
            _shortcutRouter.Register(new CommandRegistration(MeowChan.Desktop.MediaPanelKeys.ToggleCommandId, media,
                () => _selectedPage == WorkspacePage.Media && !_largeReaderOpen && _gallery == null,
                () => media.CanTogglePanel, media.TogglePanelByShortcut, Priority: 100));
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

    /// <summary>
    /// A key the person assigned to a command that only lives in the ⋯ menu or in the window frame (About, Quit, Check for updates, the full
    /// reading history, Danbooru, Maximize, Close, Show MeowChan): none of them has a button on the page, so neither the router nor the visible
    /// actions ever found them and the key did nothing. The menu entries are the executors: the entry that carries the command (visible, or kept
    /// off the screen by the work view like any reduced action) is pressed exactly as a click would; the window commands call what the caption
    /// buttons call. Commands with an explicit registration (import, continue reading, profiles, the view switch) keep their own route.
    /// </summary>
    private bool TryRunUnroutedShellCommand(KeyEventArgs e)
    {
        if (CommandBindings.Matches(e, "window.maximize")) { e.Handled = true; ToggleWindowMaximized(); return true; }
        if (CommandBindings.Matches(e, "window.close")) { e.Handled = true; Close(); return true; }
        if (CommandBindings.Matches(e, "app.show")) { e.Handled = true; RestoreWindow(); return true; }
        MenuItem[] hits = WorkspaceMenuItem.GetLogicalDescendants().OfType<MenuItem>()
            .Where(item => item.ItemCount == 0 && (item.IsVisible || WorkViewGate.HiddenByView(item))
                && CommandBindings.ActionId(item) is { } id && !CommandRouter.HasExplicitRegistration(id, this) && CommandBindings.Matches(e, id))
            .ToArray();
        if (hits.Length == 0) return false;
        e.Handled = true;
        // Several entries for one gesture are ambiguous (the bindings table reports such a pair); a disabled one swallows the key like a disabled button.
        if (hits.Length == 1) WorkViewActionStateGuard.Invoke(hits[0]);
        return true;
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
        if (ShellHelpOverlay.IsVisible)
        { DismissShellHelp(); ShowShellHelp(); }
    }
    private bool HasBlockingPageDecision() =>
        _selectedPage == WorkspacePage.Rules && _rulesPage?.IsRuleConfirmationOpen == true
        || _selectedPage == WorkspacePage.Sources && _pluginsPage?.IsDiscardConfirmationOpen == true;
    /// <summary>Whether an open gallery overlay (help, post panel, favourite question) keeps the workspace shortcuts away: only while the gallery is
    /// the active surface. A suspended gallery keeps its overlay state (the post panel stays open behind the large reader) but owns no key.</summary>
    internal static bool GalleryOwnsShortcutRouting(bool overlayOpen, bool suspended) => overlayOpen && !suspended;

    /// <summary>Whether the gallery blocks the shell's power commands (page navigation and the like): its help or favourite question owns the keys
    /// while it is the active surface; its side panel does not, and a suspended gallery never does.</summary>
    internal static bool GalleryBlocksShellPowerCommands(bool blocksUnfocusedHostKeys, bool suspended) => blocksUnfocusedHostKeys && !suspended;

    private bool RouteWorkspaceShortcut(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || !IsEnabled || _isClosing || HasVisibleShellDialog()
            || HasBlockingPageDecision() || _archivePage?.IsReaderDialogOpen == true
            || ShellHelpOverlay.IsVisible) return false;
        if (!CommandRouter.IsRoutingContextActive(this, e.Source)) return false;
        // An open gallery panel keeps the workspace shortcuts away, but not the gallery's own keys while nothing has the focus.
        // A suspended gallery (the large reader is open over it) is hidden and owns no key: its panel or help state must not refuse the reader's routes.
        if (_gallery is { } openGallery && GalleryOwnsShortcutRouting(openGallery.IsOverlayOpen, _gallerySuspended))
            return !openGallery.BlocksUnfocusedHostKeys && openGallery.RouteUnfocusedHostShortcut(e);
        if (_shortcutRouter?.Route(e) == true) return true;
        // Gallery's tunnel handler owns its native-control and effective-gesture
        // policy. Only a genuine unfocused Window input may enter that same policy
        // here; focused controls and foreign native roots are never retried.
        if (_gallery != null && !_gallerySuspended) return _gallery.RouteUnfocusedHostShortcut(e);
        if (!CanExecuteShellPowerCommand(e.Source, e.Key)) return false;
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
