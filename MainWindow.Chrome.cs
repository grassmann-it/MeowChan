using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z");
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M 2.5,2.5 L 2.5,0.5 L 9.5,0.5 L 9.5,7.5 L 7.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z");

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
        if (CanMinimize) WindowState = WindowState.Minimized;
    }

    private void OnWindowMaximizeClick(object? sender, RoutedEventArgs e) => ToggleWindowMaximized();

    private void ToggleWindowMaximized()
    {
        if (WindowState == WindowState.FullScreen) return;
        if (WindowState == WindowState.Maximized)
        {
            if (CanResize) WindowState = WindowState.Normal;
        }
        else if (CanMaximize) WindowState = WindowState.Maximized;
    }

    // Use the same close path as the former OS caption, including MinimizeToTray
    // and the existing asynchronous session shutdown.
    private void OnWindowCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowChromePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
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
        maximize.IsEnabled = WindowState != WindowState.FullScreen && (maximized ? CanResize : CanMaximize);
        WorkspaceControl<Path>("WindowMaximizeIcon").Data = maximized ? RestoreGeometry : MaximizeGeometry;
        SetWindowControlLabel(minimize, "window.minimize");
        SetWindowControlLabel(maximize, maximized ? "window.restore" : "window.maximize");
        SetWindowControlLabel(WorkspaceControl<Button>("WindowCloseButton"), "action.close");
    }

    private static void SetWindowControlLabel(Button button, string key)
    {
        string label = Texts.T(key);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }
}
