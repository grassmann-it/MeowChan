using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static partial class GalleryPlaybackFeatureChecks
{
    private sealed class ModeMenuInputTrace : IDisposable
    {
        private readonly GalleryViewer _viewer;
        private readonly Control _mode;
        private readonly Window[] _roots;
        private readonly PixelPoint _requested;
        private int _remaining = 40;
        private int _opened, _closed;
        internal bool ModePressed { get; private set; }
        internal bool ModeReleased { get; private set; }
        internal bool ViewportCaptured { get; private set; }
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        internal ModeMenuInputTrace(Window host, GalleryViewer viewer, Control mode)
        {
            _viewer = viewer; _mode = mode;
            _requested = mode.PointToScreen(new Point(mode.Bounds.Width / 2, mode.Bounds.Height / 2));
            _roots = new[] { host, TopLevel.GetTopLevel(mode) as Window }.OfType<Window>().Distinct().ToArray();
            foreach (Control target in _roots.Cast<Control>().Append(mode))
            {
                target.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                target.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            }
            mode.PropertyChanged += Changed;
            if (mode is ComboBox selector)
            {
                selector.DropDownOpened += Opened;
                selector.DropDownClosed += Closed;
            }
        }
        private void Pressed(object? sender, PointerPressedEventArgs e)
        {
            if (ReferenceEquals(sender, _mode)) ModePressed = true;
            Record("press", sender as Control, e);
        }
        private void Released(object? sender, PointerReleasedEventArgs e)
        {
            if (ReferenceEquals(sender, _mode)) ModeReleased = true;
            Record("release", sender as Control, e);
        }
        private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ComboBox.IsDropDownOpenProperty || e.Property == Visual.IsVisibleProperty
                || e.Property == Visual.BoundsProperty) Record("property-" + e.Property.Name);
        }
        private void Opened(object? sender, EventArgs args) { _opened++; Record("opened"); }
        private void Closed(object? sender, EventArgs args) { _closed++; Record("closed"); }
        internal void Record(string phase, Control? sender = null, PointerEventArgs? pointer = null)
        {
            if (pointer?.Pointer.Captured is Control { Name: "GalleryViewport" }) ViewportCaptured = true;
            if (_remaining-- <= 0) return;
            string Label(object? value) => value is Control control ? control.GetType().Name + ":" + control.Name
                : value?.GetType().Name ?? "null";
            var currentRoot = TopLevel.GetTopLevel(_mode);
            var center = _mode.IsAttachedToVisualTree()
                ? _mode.PointToScreen(new Point(_mode.Bounds.Width / 2, _mode.Bounds.Height / 2)).ToString() : "detached";
            object? hit = currentRoot?.IsAttachedToVisualTree() == true
                ? currentRoot.InputHitTest(currentRoot.PointToClient(_requested)) : null;
            Console.WriteLine("SMOKE mode-menu input: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                phase, elapsedMs = _clock.ElapsedMilliseconds, sender = Label(sender),
                routing = pointer?.Route.ToString(), source = Label(pointer?.Source), handled = pointer?.Handled,
                pointerId = pointer?.Pointer.Id, capture = Label(pointer?.Pointer.Captured),
                opened = _opened, closed = _closed, IsDropDownOpen = (_mode as ComboBox)?.IsDropDownOpen,
                pressed = _mode.Classes.Contains(":pressed"),
                bounds = _mode.Bounds.ToString(), requestedScreen = _requested.ToString(), currentCenterScreen = center,
                _mode.IsArrangeValid, _mode.IsEffectivelyVisible, _mode.IsEffectivelyEnabled,
                hit = Label(hit), hitInMode = hit is Visual visual && visual.GetSelfAndVisualAncestors().Contains(_mode),
                overlayHidden = ErrorField<bool>(_viewer, "_overlayHidden"),
                hoverContext = ErrorField<bool>(_viewer, "_hoverContextOpen"),
                revealPointer = ErrorField<long?>(_viewer, "_revealPointerId"), swallowTap = ErrorField<bool>(_viewer, "_swallowRevealTap"),
                roots = _roots.Select(root => new { root.IsActive, root.IsVisible, root.IsEnabled,
                    bounds = root.Bounds.ToString(), position = root.Position.ToString(), handle = root.TryGetPlatformHandle()?.Handle.ToString("x") }).ToArray(),
                native = NativeTarget(_requested),
                time = Find<TextBlock>(_viewer, "GalleryVideoTime").Text
            }));
        }
        private static object? NativeTarget(PixelPoint point)
        {
            if (!OperatingSystem.IsWindows()) return null;
            nint hit = WindowFromPoint(new WinPoint { X = point.X, Y = point.Y });
            GetWindowThreadProcessId(hit, out uint process);
            GetCursorPos(out WinPoint cursor);
            return new { hit = hit.ToString("x"), root = GetAncestor(hit, 2).ToString("x"), process,
                cursor = new { cursor.X, cursor.Y }, foreground = GetForegroundWindow().ToString("x") };
        }
        public void Dispose()
        {
            Record("final");
            foreach (Control target in _roots.Cast<Control>().Append(_mode))
            {
                target.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
                target.RemoveHandler(InputElement.PointerReleasedEvent, Released);
            }
            _mode.PropertyChanged -= Changed;
            if (_mode is ComboBox selector)
            {
                selector.DropDownOpened -= Opened;
                selector.DropDownClosed -= Closed;
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct WinPoint { internal int X, Y; }
        [DllImport("user32.dll")] private static extern nint WindowFromPoint(WinPoint point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out WinPoint point);
        [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    }
}
