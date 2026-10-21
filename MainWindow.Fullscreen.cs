using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class MainWindow
{
    private sealed record FullscreenOrigin(WindowState State, Size Client, PixelPoint Position, PixelRect? Monitor, Size Decoration);
    private FullscreenOrigin? _fullscreenOrigin;
    private Size _lastNormalClient = new(1380, 850);
    private PixelPoint _lastNormalPosition;
    private bool _restoringFullscreen;
    private Control? _helpOrigin;
    private Size _normalDecoration;
    private Size? _fullscreenRestoreClient;
    private Size _fullscreenRestoreFromClient;
    private bool _fullscreenCorrectionQueued;

    internal static Avalonia.Styling.ThemeVariant ThemeForPreference(string theme) => theme switch
    {
        "Light" => Avalonia.Styling.ThemeVariant.Light,
        "System" => Avalonia.Styling.ThemeVariant.Default,
        _ => Avalonia.Styling.ThemeVariant.Dark
    };

    private void InitializeShellPresentation()
    {
        AddHandler(KeyDownEvent, OnShellKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PositionChanged += OnShellPositionChanged;
        Resized += OnFullscreenRestoreResized;
        Screens.Changed += OnShellScreensChanged;
        WorkspaceControl<Button>("FullscreenButton").Click += (_, _) => ToggleFullscreen();
        WorkspaceControl<Button>("ShellHelpClose").Click += (_, _) => DismissShellHelp();
        WorkspaceControl<MenuItem>("ShortcutHelpMenuItem").Click += (_, _) => ShowShellHelp();
        RefreshShellCommands();
    }

    private void DisposeShellPresentation()
    {
        RemoveHandler(KeyDownEvent, OnShellKeyDown);
        PositionChanged -= OnShellPositionChanged;
        Resized -= OnFullscreenRestoreResized;
        Screens.Changed -= OnShellScreensChanged;
    }

    private void OnShellPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (WindowState == WindowState.Normal && _fullscreenOrigin == null && !_restoringFullscreen)
            { _lastNormalPosition = Position; QueueWindowPreferences(); }
    }

    private void RememberNormalGeometry()
    {
        if (WindowState != WindowState.Normal || _fullscreenOrigin != null || _restoringFullscreen || ClientSize.Width <= 0 || IsFullscreenRestoreStale(ClientSize)) return;
        _lastNormalClient = ClientSize;
        _lastNormalPosition = Position;
        if (FrameSize is Size frame)
            _normalDecoration = new Size(Math.Max(0, frame.Width - ClientSize.Width), Math.Max(0, frame.Height - ClientSize.Height));
    }

    private void OnShellScreensChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.FullScreen && Screens.ScreenFromWindow(this) == null && Screens.Primary is { } screen)
            Position = screen.WorkingArea.Position;
    }

    internal void ToggleFullscreen()
    {
        if (_isClosing || _restoringFullscreen || !IsEnabled || OwnedWindows.Any(window => window.IsVisible)) return;
        if (WindowState == WindowState.FullScreen) LeaveFullscreen();
        else
        {
            RememberNormalGeometry();
            _fullscreenRestoreClient = null;
            _fullscreenOrigin = new(WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal,
                _lastNormalClient, _lastNormalPosition, Screens.ScreenFromWindow(this)?.Bounds, _normalDecoration);
            WindowState = WindowState.FullScreen;
        }
        RefreshShellCommands();
    }

    private void LeaveFullscreen()
    {
        FullscreenOrigin origin = _fullscreenOrigin ?? new(WindowState.Normal, _lastNormalClient, _lastNormalPosition, null, _normalDecoration);
        _fullscreenOrigin = null;
        _fullscreenRestoreFromClient = ClientSize;
        _restoringFullscreen = true;
        WindowState = WindowState.Normal;
        // Apply only after the native state transition returns to the dispatcher.
        // The saved workspace/Reader/Gallery objects are never re-created here.
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing) { _restoringFullscreen = false; return; }
            Screen? screen = Screens.All.FirstOrDefault(candidate => candidate.Bounds == origin.Monitor)
                ?? Screens.ScreenFromPoint(origin.Position) ?? Screens.Primary;
            if (screen != null)
            {
                WindowStartupFit fit = WindowStartupGeometry.Fit(origin.Client, new Size(origin.Client.Width + origin.Decoration.Width, origin.Client.Height + origin.Decoration.Height),
                    new Size(MinWidth, MinHeight), screen.WorkingArea, screen.Scaling, origin.Position);
                MinWidth = fit.Minimum.Width; MinHeight = fit.Minimum.Height;
                Width = fit.Client.Width; Height = fit.Client.Height; Position = fit.Position;
                _lastNormalClient = fit.Client; _lastNormalPosition = fit.Position;
                if (origin.State == WindowState.Normal) _fullscreenRestoreClient = fit.Client;
            }
            else { Width = origin.Client.Width; Height = origin.Client.Height; Position = origin.Position; }
            if (origin.State == WindowState.Maximized) WindowState = WindowState.Maximized;
            _restoringFullscreen = false;
            ContinueFullscreenRestore();
            RefreshShellCommands();
        }, DispatcherPriority.Loaded);
    }

    private bool IsFullscreenRestoreStale(Size size) => _fullscreenRestoreClient is Size expected
        && (_normalDecoration.Width > 0 || _normalDecoration.Height > 0)
        && Math.Abs(size.Width - (expected.Width - _normalDecoration.Width)) <= 1
        && Math.Abs(size.Height - (expected.Height - _normalDecoration.Height)) <= 1;

    private void OnFullscreenRestoreResized(object? sender, WindowResizedEventArgs e)
    {
        if (e.Reason == WindowResizeReason.User) _fullscreenRestoreClient = null;
        else ContinueFullscreenRestore();
    }

    private void ContinueFullscreenRestore()
    {
        if (_fullscreenRestoreClient is not Size expected || WindowState != WindowState.Normal || _restoringFullscreen || _isClosing) return;
        if (!IsFullscreenRestoreStale(ClientSize))
        {
            // A new explicit normal resize supersedes this transition correction.
            if (ClientSize != expected && ClientSize != _fullscreenRestoreFromClient
                && (Math.Abs(ClientSize.Width - (_fullscreenRestoreFromClient.Width - _normalDecoration.Width)) > 1
                    || Math.Abs(ClientSize.Height - (_fullscreenRestoreFromClient.Height - _normalDecoration.Height)) > 1))
                _fullscreenRestoreClient = null;
            return;
        }
        if (_fullscreenCorrectionQueued) return;
        _fullscreenCorrectionQueued = true;
        // On X11 a queued ConfigureNotify may contain the undecorated requested
        // size after Normal restored its managed shadow. Avalonia subtracts that
        // shadow again. Repair only this exact stale size, never an unrelated resize.
        Dispatcher.UIThread.Post(() =>
        {
            _fullscreenCorrectionQueued = false;
            if (_isClosing || WindowState != WindowState.Normal || _fullscreenRestoreClient != expected || !IsFullscreenRestoreStale(ClientSize)) return;
            Width = expected.Width; Height = expected.Height;
        }, DispatcherPriority.Background);
    }

    private bool DismissShellHelp()
    {
        Border help = WorkspaceControl<Border>("ShellHelpOverlay");
        if (!help.IsVisible) return false;
        help.IsVisible = false;
        if (_helpOrigin?.IsEffectivelyVisible == true) _helpOrigin.Focus();
        _helpOrigin = null;
        return true;
    }

    private void ShowShellHelp()
    {
        Border help = WorkspaceControl<Border>("ShellHelpOverlay");
        if (help.IsVisible) { DismissShellHelp(); return; }
        _helpOrigin = FocusManager?.GetFocusedElement() as Control;
        var ids = new System.Collections.Generic.List<string> { "input.focus-url", "navigation.settings", "window.fullscreen", "viewer.shortcut-help" };
        if (_largeReaderOpen)
        {
            foreach (var pair in new[] { ("ReaderPreviousPost", "reader.previous-post"), ("ReaderNextPost", "reader.next-post"),
                ("ReaderPreviousThread", "reader.previous-thread"), ("ReaderNextThread", "reader.next-thread") })
                if (_archivePage?.FindControl<Button>(pair.Item1)?.IsEnabled == true) ids.Insert(0, pair.Item2);
        }
        else if (_selectedPage == WorkspacePage.Downloads && _restoreCompleted && !WorkspaceCommandBusy)
        {
            bool boards = WorkspaceControl<TabControl>("DownloadsTabs").SelectedIndex == 1;
            DownloadMenuTarget? target = CaptureDownloadMenu(boards);
            if (target != null)
            {
                ids.InsertRange(0, new[] { "thread.details", "selection.copy-links", "location.open-folder", "location.open-source", "rules.edit-scope", "watch.remove" });
                if (!boards) ids.InsertRange(0, new[] { "thread.read", "gallery.open", "export.open-html-dialog" });
                else ids.Insert(0, "board.filters");
                if (target.Checkable.Length > 0) ids.Insert(0, "watch.check");
                if (target.Retriable.Length > 0) ids.Insert(0, "watch.retry-media");
            }
        }
        if (_selectedPage == WorkspacePage.Archive && !_largeReaderOpen)
            ids.InsertRange(0, new[] { "input.focus-search", "archive.delete-permanently" });
        WorkspaceControl<TextBlock>("ShellHelpText").Text = string.Join(Environment.NewLine,
            ids.Distinct().Where(id => CommandCatalog.TryGet(id, out _))
                .Select(id => CommandCatalog.Label(id) + "   " + CommandCatalog.ShortcutDisplay(id)));
        help.MaxHeight = Math.Max(180, ClientSize.Height - 40);
        help.IsVisible = true;
        WorkspaceControl<Button>("ShellHelpClose").Focus();
    }

    private bool DismissShellMenu()
    {
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.ContextMenu?.IsOpen == true) { control.ContextMenu.Close(); return true; }
            if (control is Button { Flyout.IsOpen: true } button) { button.Flyout!.Hide(); return true; }
        }
        MenuItem? submenu = this.GetLogicalDescendants().OfType<MenuItem>().LastOrDefault(item => item.IsSubMenuOpen);
        if (submenu == null) return false;
        submenu.IsSubMenuOpen = false;
        return true;
    }

    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _isClosing || !IsEnabled || OwnedWindows.Any(window => window.IsVisible)) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            // Popups/editors own their first Escape; never cascade a handled key.
            if (e.Source is Control control && control.GetLogicalAncestors().Prepend(control).OfType<ComboBox>().Any(combo => combo.IsDropDownOpen)) return;
            if (DismissShellMenu() || DismissShellHelp() || (_gallery != null && !_gallerySuspended && _gallery.TryDismissOverlay()) ||
                (_archivePage?.TryDismissOverlay() ?? false))
            { e.Handled = true; return; }
            if (_archivePage?.IsReaderDialogOpen == true) return;
            if (WindowState == WindowState.FullScreen) { e.Handled = true; LeaveFullscreen(); return; }
            if (_gallery != null && !_gallerySuspended) { e.Handled = true; CloseGallery(); return; }
            if (_largeReaderOpen) { e.Handled = true; CloseLargeReader(); return; }
            if (_selectedPage == WorkspacePage.Downloads && WorkspaceControl<Border>("WorkspaceInspector").IsVisible)
            {
                e.Handled = true;
                OnDetailsToggleClick(sender, e);
                DownloadList(WorkspaceControl<TabControl>("DownloadsTabs").SelectedIndex == 1).Focus();
            }
            return;
        }
        if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None)
        {
            if (_archivePage?.IsReaderDialogOpen == true) return;
            e.Handled = true; ToggleFullscreen(); return;
        }
        if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None && (_gallery == null || _gallerySuspended))
        { e.Handled = true; ShowShellHelp(); return; }
        if (_archivePage?.IsReaderDialogOpen == true) return;
        if (e.KeyModifiers != ListCommandModifier) return;
        if (e.Key == Key.L)
        {
            e.Handled = true;
            if (_gallery != null) CloseGallery(restoreOrigin: false);
            if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
            if (NavigateWorkspace(WorkspacePage.Downloads)) WorkspaceControl<TextBox>("UrlInput").Focus();
        }
        else if (e.Key == Key.OemComma)
        {
            e.Handled = true;
            if (_gallery != null) CloseGallery(restoreOrigin: false);
            if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
            NavigateWorkspace(WorkspacePage.Settings);
        }
        else if (e.Key == Key.F && _selectedPage == WorkspacePage.Archive && !_largeReaderOpen && (_gallery == null || _gallerySuspended))
        { e.Handled = true; _archivePage?.FindControl<TextBox>("ArchiveSearchText")?.Focus(); }
    }

    private void RefreshShellCommands()
    {
        if (!_workspaceInitialized) return;
        CommandBindings.Apply(WorkspaceControl<Button>("FullscreenButton"), "window.fullscreen", iconOnly: true);
        WorkspaceControl<Button>("FullscreenButton").Content = CommandBindings.Icon("window.fullscreen", 16,
            WindowState == WindowState.FullScreen ? "fullscreen" : "windowed");
        WorkspaceControl<Button>("FullscreenButton").Classes.Set("selected", WindowState == WindowState.FullScreen);
        Avalonia.Automation.AutomationProperties.SetHelpText(WorkspaceControl<Button>("FullscreenButton"),
            CommandBindings.Tooltip("window.fullscreen") + (WindowState == WindowState.FullScreen ? (MeowChan.Desktop.Localization.Texts.Language == "de" ? " · Ein" : " · On") : (MeowChan.Desktop.Localization.Texts.Language == "de" ? " · Aus" : " · Off")));
        CommandBindings.Apply(WorkspaceControl<Button>("ShellHelpClose"), "window.close");
        foreach (var pair in new[] { ("ArchiveMenuItem", "navigation.archive"), ("RulesMenuItem", "navigation.rules"),
            ("SourcesMenuItem", "navigation.sources"), ("DiagnosticsMenuItem", "navigation.diagnostics"),
            ("SettingsMenuItem", "navigation.settings"), ("AboutMenuItem", "navigation.about"),
            ("ShortcutHelpMenuItem", "viewer.shortcut-help"), ("ExitMenuItem", "app.quit") })
            CommandBindings.Apply(WorkspaceControl<MenuItem>(pair.Item1), pair.Item2);
        if (WorkspaceControl<Button>("ToolsNavigationButton").Flyout is MenuFlyout tools)
            foreach (MenuItem item in tools.Items.OfType<MenuItem>())
            {
                string? id = (item.Tag as string) switch { "nav.archive" => "navigation.archive", "nav.rules" => "navigation.rules",
                    "nav.sources" => "navigation.sources", "nav.diagnostics" => "navigation.diagnostics", _ => null };
                if (id != null) CommandBindings.Apply(item, id);
            }
    }
}
