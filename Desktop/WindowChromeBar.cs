using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// Shared app-owned caption for the two secondary windows. Ordinary Buttons keep
// keyboard focus/Space/Enter and automation; native roles retain platform hints.
public sealed class WindowChromeBar : UserControl
{
    private static readonly Geometry MaximizeGeometry = Geometry.Parse(WindowChromePolicy.MaximizePath);
    private static readonly Geometry RestoreGeometry = Geometry.Parse(WindowChromePolicy.RestorePath);
    private readonly TextBlock _title;
    private readonly Button _minimize;
    private readonly Button _maximize;
    private readonly Button _close;
    private readonly Path _maximizeIcon;
    private readonly CaptionRestoreTracker _restore = new();
    private Window? _window;
    private bool _restoreCheckQueued;

    public WindowChromeBar()
    {
        Height = WorkspaceMetrics.TitleBarHeight;
        var frame = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        frame.Classes.Add("shell-bar");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Brushes.Transparent };
        WindowDecorationProperties.SetElementRole(header, WindowDecorationsElementRole.User);
        header.PointerPressed += OnTitleBarPointerPressed;
        _title = new TextBlock
        {
            Name = "WindowCaptionTitle", TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(16, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold
        };
        header.Children.Add(_title);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(controls, 1);
        _minimize = CaptionButton("WindowMinimizeButton", "M 0,5 L 10,5", WindowDecorationsElementRole.MinimizeButton);
        _maximize = CaptionButton("WindowMaximizeButton", WindowChromePolicy.MaximizePath, WindowDecorationsElementRole.MaximizeButton);
        _maximizeIcon = (Path)_maximize.Content!;
        _close = CaptionButton("WindowCloseButton", "M 0,0 L 10,10 M 10,0 L 0,10", WindowDecorationsElementRole.CloseButton);
        _close.Classes.Add("window-close");
        _minimize.Click += (_, _) => { if (_window is { CanMinimize: true } window) window.WindowState = WindowState.Minimized; };
        _maximize.Click += (_, _) => ToggleMaximize();
        // Never bypass Window.OnClosing (Settings' in-flight save guard).
        _close.Click += (_, _) => _window?.Close();
        controls.Children.Add(_minimize);
        controls.Children.Add(_maximize);
        controls.Children.Add(_close);
        header.Children.Add(controls);
        frame.Child = header;
        Content = frame;
    }

    internal void Attach(Window window)
    {
        if (ReferenceEquals(window, _window)) return;
        Detach();
        _window = window;
        window.PropertyChanged += OnWindowPropertyChanged;
        window.Closed += OnWindowClosed;
        Texts.Changed += OnTextsChanged;
        Refresh();
    }

    private static Button CaptionButton(string name, string path, WindowDecorationsElementRole role)
    {
        var icon = new Path { Data = Geometry.Parse(path) };
        icon.Classes.Add("window-caption-icon");
        var button = new Button { Name = name, Content = icon };
        button.Classes.Add("window-caption");
        WindowDecorationProperties.SetElementRole(button, role);
        return button;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_window is not { } window || e.Handled || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source)
            .Any(visual => visual is Button or Menu or MenuItem)) return;
        if (window.WindowState == WindowState.FullScreen) return;
        e.Handled = true;
        if (e.ClickCount == 2) ToggleMaximize();
        else window.BeginMoveDrag(e);
    }

    private void ToggleMaximize()
    {
        if (_window is not { } window) return;
        WindowState next = WindowChromePolicy.ToggleMaximize(window.WindowState, window.CanMaximize, window.CanResize);
        if (next == window.WindowState) return;
        if (next == WindowState.Maximized && window.WindowState == WindowState.Normal) _restore.BeforeMaximize(window.Bounds.Size);
        else if (next == WindowState.Normal) _restore.BeforeRestore(window.Bounds.Size);
        window.WindowState = next;
    }

    // Window.Width/Height and Bounds are what the stale X11 report changes; the check runs after the
    // layout pass of the same dispatch so a queued ConfigureNotify and the layout resize have both landed.
    private void ObserveRestore(AvaloniaPropertyChangedEventArgs e)
    {
        if (_window is not { } window) return;
        if (e.Property == Window.WindowStateProperty && !Equals(e.OldValue, e.NewValue)) _restore.StateChanged(window.WindowState);
        else if (e.Property != Visual.BoundsProperty && e.Property != Layoutable.WidthProperty && e.Property != Layoutable.HeightProperty
            && e.Property != Window.WindowStateProperty) return;
        if (_restoreCheckQueued || !_restore.Pending || window.WindowState != WindowState.Normal) return;
        _restoreCheckQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _restoreCheckQueued = false;
            if (_window is { IsVisible: true } current
                && _restore.StaleMaximum(current.WindowState, current.Bounds.Size, current.Width, current.Height) is Size normal)
            {
                current.Width = normal.Width;
                current.Height = normal.Height;
            }
        }, DispatcherPriority.Background);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        ObserveRestore(e);
        if (e.Property == Window.TitleProperty || e.Property == Window.WindowStateProperty || e.Property == Window.CanMinimizeProperty
            || e.Property == Window.CanMaximizeProperty || e.Property == Window.CanResizeProperty) Refresh();
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_window != null) Refresh();
    });

    private void Refresh()
    {
        if (_window is not { } window) return;
        _title.Text = window.Title;
        _minimize.IsEnabled = window.CanMinimize;
        _maximize.IsEnabled = WindowChromePolicy.CanToggleMaximize(window.WindowState, window.CanMaximize, window.CanResize);
        _maximizeIcon.Data = window.WindowState == WindowState.Maximized ? RestoreGeometry : MaximizeGeometry;
        SetLabel(_minimize, "window.minimize");
        SetLabel(_maximize, WindowChromePolicy.MaximizeLabel(window.WindowState));
        SetLabel(_close, "action.close");
    }

    internal static void SetLabel(Button button, string key)
    {
        string label = Texts.T(key);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Detach();

    private void Detach()
    {
        if (_window is not { } window) return;
        window.PropertyChanged -= OnWindowPropertyChanged;
        window.Closed -= OnWindowClosed;
        Texts.Changed -= OnTextsChanged;
        _window = null;
    }
}
