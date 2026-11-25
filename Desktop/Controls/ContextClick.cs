using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

internal static class ContextClick
{
    // Avalonia's headless backend can send a Cocoa-style Ctrl+left when exercising the macOS route.
    internal static bool? MacOSForTests { get; set; }

    internal static bool IsMacControlPress(PointerPressedEventArgs e, Visual origin) =>
        (MacOSForTests ?? OperatingSystem.IsMacOS()) && e.Pointer.Type == PointerType.Mouse
        && (e.KeyModifiers & KeyModifiers.Control) != 0
        && e.GetCurrentPoint(origin).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;

    internal static bool IsPress(PointerPressedEventArgs e, Visual origin) =>
        e.GetCurrentPoint(origin).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
        || IsMacControlPress(e, origin);

    internal static MouseButton Button(PointerPressedEventArgs e, Visual origin) =>
        IsMacControlPress(e, origin) ? MouseButton.Left : MouseButton.Right;

    internal static bool IsKey(KeyEventArgs e) => !e.Handled && !ShortcutService.IsCapturing && !CommandRouter.IsNativeInput(e.Source, e.Key)
        && (e.Key == Key.Apps && e.KeyModifiers == KeyModifiers.None
            || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift
            || CommandBindings.Matches(e, "menu.context-open"));

    internal static void RequestOnMacControlPress(PointerPressedEventArgs e, Control origin)
    {
        if (e.Handled || e.Source is not InputElement source || !IsMacControlPress(e, origin)) return;
        ContextRequestedEventArgs request = new(e) { RoutedEvent = InputElement.ContextRequestedEvent };
        source.RaiseEvent(request);
        e.Handled = request.Handled;
    }

    // Avalonia can request a menu on KeyUp, even when the KeyDown was rejected. Keep that gesture
    // consumed through repeats and release, including if focus moves within the window.
    internal static void RejectKeyPress(Control origin, KeyEventArgs press)
    {
        press.Handled = true;
        if (TopLevel.GetTopLevel(origin) is not Window window) return;
        Key key = press.Key;
        window.AddHandler(InputElement.KeyDownEvent, ConsumeRepeat, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyUpEvent, ConsumeRelease, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Deactivated += Detach;

        void ConsumeRepeat(object? sender, KeyEventArgs e)
        {
            if (e.Key == key) e.Handled = true;
        }

        void ConsumeRelease(object? sender, KeyEventArgs e)
        {
            if (e.Key != key) return;
            e.Handled = true;
            Detach(sender, e);
        }

        void Detach(object? sender, EventArgs e)
        {
            window.RemoveHandler(InputElement.KeyDownEvent, ConsumeRepeat);
            window.RemoveHandler(InputElement.KeyUpEvent, ConsumeRelease);
            window.Deactivated -= Detach;
        }
    }

    internal static void ConsumeOpeningKeyRelease(ContextMenu? menu, Key key, Control? origin = null)
    {
        if (menu is not { IsOpen: true }) return;
        menu.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        origin?.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        menu.Closed += OnClosed;

        void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key != key) return;
            e.Handled = true;
            Detach();
        }

        void OnClosed(object? sender, RoutedEventArgs e) => Detach();

        void Detach()
        {
            menu.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            origin?.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            menu.Closed -= OnClosed;
        }
    }
}
