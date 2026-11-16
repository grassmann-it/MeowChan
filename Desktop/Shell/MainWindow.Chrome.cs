using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    // Parsed on first use from the UI path, never in a static initializer: Geometry.Parse needs the render platform, and
    // tests reach static MainWindow members (e.g. ApphostFixturePluginEnabled) without Avalonia set up. An initializer here
    // made MainWindow's type initializer throw "Unable to locate IPlatformRenderInterface" for them.
    private static Geometry? _maximizeGeometry;
    private static Geometry? _restoreGeometry;
    private static Geometry MaximizeGeometry => _maximizeGeometry ??= Geometry.Parse(WindowChromePolicy.MaximizePath);
    private static Geometry RestoreGeometry => _restoreGeometry ??= Geometry.Parse(WindowChromePolicy.RestorePath);

    // The shell's drawn marks that XAML cannot name (P6): the "more" icon of the application menu, the close icons of the
    // feedback banner and the details panel. Their geometry is built when first shown (WorkspaceIcons).
    private void InitializeGlyphIcons()
    {
        WorkspaceMenuItem.Header = MeowChan.Desktop.Controls.WorkspaceIcons.Create(MeowChan.Desktop.Controls.WorkspaceIcons.More);
        MeowChan.Desktop.Controls.WorkspaceIcons.Attach(ShellFeedbackDismissIcon, MeowChan.Desktop.Controls.WorkspaceIcons.Close);
        InspectorCloseButton.Content = MeowChan.Desktop.Controls.WorkspaceIcons.Create(MeowChan.Desktop.Controls.WorkspaceIcons.Close);
    }

    // Square corners on Windows (DWM) and native corners and traffic lights on macOS (header
    // starts after them). MEOWCHAN_WINDOW_CHROME=system fallback: the platform caption returns,
    // the header row stays as an in-window bar (branding, menu, header commands) without own
    // caption buttons or window dragging.
    private void ApplyWindowChrome()
    {
        WindowChrome.Apply(this);
        WindowCaptionButtons.IsVisible = WindowChrome.ShowsOwnCaptionButtons(WindowChrome.Mode);
        // On the frame, not the header: workspace modes reset the header margin.
        WorkspaceTitleBarFrame.Padding = new Thickness(WindowChrome.LeadingInset(WindowChrome.Mode), 0, 0, 0);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !WindowChrome.ShowsCaption(WindowChrome.Mode) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Header commands keep their own input; only branding and empty space drag.
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source)
            .Any(visual => visual is Button or Menu or MenuItem)) return;

        e.Handled = true;
        if (e.ClickCount == 2) ToggleWindowMaximized();
        else BeginMoveDrag(e);
    }

    private void OnWindowMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (CanMinimize) MinimizeWindow(_config.MinimizeToTray);
    }

    private void OnWindowMaximizeClick(object? sender, RoutedEventArgs e) => ToggleWindowMaximized();

    private void ToggleWindowMaximized()
    {
        if (WindowState == WindowState.FullScreen) return;
        if (WindowState == WindowState.Maximized)
        {
            if (CanResize)
            {
                _captionRestorePending = _captionRestoreSize.HasValue;
                _captionRestoreFromSize = Bounds.Size;
                WindowState = WindowState.Normal;
            }
        }
        else if (CanMaximize)
        {
            if (WindowState == WindowState.Normal)
            {
                // A queued X11 ConfigureNotify can briefly report the old maximum
                // after Normal was acknowledged. Preserve the real normal snapshot.
                if (!_captionRestorePending || Bounds.Size != _captionRestoreFromSize)
                {
                    _captionRestoreSize = Bounds.Size;
                    _captionRestoreMode = _config.WorkspaceMode;
                }
            }
            WindowState = WindowState.Maximized;
        }
    }

    // Use the same close path as the former OS caption, including CloseToTray
    // and the existing asynchronous session shutdown.
    private void OnWindowCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowChromePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // X11 can report the same state while a request awaits the window manager.
        // Keep the restore snapshot until an actual state transition arrives.
        if (e.Property == WindowStateProperty && e.OldValue is WindowState previous && e.NewValue is WindowState current)
            _minimizeRestore.StateChanged(previous, current);
        if (e.Property == WindowStateProperty && !Equals(e.OldValue, e.NewValue)) CompleteCaptionRestore();
        else if (e.Property == BoundsProperty || e.Property == WidthProperty || e.Property == HeightProperty)
            ContinueCaptionRestore();
        if (e.Property == ActualThemeVariantProperty)
        {
            UpdateThemeIcon();
            RefreshTrayMenuIcons();
        }
        if (e.Property == WindowStateProperty || e.Property == CanMinimizeProperty
            || e.Property == CanMaximizeProperty || e.Property == CanResizeProperty)
            UpdateWindowControls();
    }

    private void UpdateWindowControls()
    {
        bool maximized = WindowState == WindowState.Maximized;
        Button minimize = WindowMinimizeButton;
        Button maximize = WindowMaximizeButton;
        minimize.IsEnabled = CanMinimize;
        maximize.IsEnabled = WindowChromePolicy.CanToggleMaximize(WindowState, CanMaximize, CanResize);
        WindowMaximizeIcon.Data = maximized ? RestoreGeometry : MaximizeGeometry;
        SetWindowControlLabel(minimize, "window.minimize");
        SetWindowControlLabel(maximize, WindowChromePolicy.MaximizeLabel(WindowState));
        SetWindowControlLabel(WindowCloseButton, "action.close");
    }

    private static void SetWindowControlLabel(Button button, string key) => WindowChromeBar.SetLabel(button, key);
}
