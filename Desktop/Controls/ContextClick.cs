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

    internal static bool IsKey(KeyEventArgs e) => !e.Handled && !CommandRouter.IsNativeInput(e.Source, e.Key)
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

    internal static void ConsumeOpeningKeyRelease(ContextMenu? menu, Key key)
    {
        if (menu is not { IsOpen: true }) return;
        menu.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
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
            menu.Closed -= OnClosed;
        }
    }
}
