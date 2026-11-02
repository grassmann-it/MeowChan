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
    private static readonly Geometry MaximizeGeometry = Geometry.Parse(WindowChromePolicy.MaximizePath);
    private static readonly Geometry RestoreGeometry = Geometry.Parse(WindowChromePolicy.RestorePath);

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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
        Button minimize = WorkspaceControl<Button>("WindowMinimizeButton");
        Button maximize = WorkspaceControl<Button>("WindowMaximizeButton");
        minimize.IsEnabled = CanMinimize;
        maximize.IsEnabled = WindowChromePolicy.CanToggleMaximize(WindowState, CanMaximize, CanResize);
        WorkspaceControl<Path>("WindowMaximizeIcon").Data = maximized ? RestoreGeometry : MaximizeGeometry;
        SetWindowControlLabel(minimize, "window.minimize");
        SetWindowControlLabel(maximize, WindowChromePolicy.MaximizeLabel(WindowState));
        SetWindowControlLabel(WorkspaceControl<Button>("WindowCloseButton"), "action.close");
    }

    private static void SetWindowControlLabel(Button button, string key) => WindowChromeBar.SetLabel(button, key);
}
