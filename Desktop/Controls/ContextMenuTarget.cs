using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Serilog;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Remembers what a context menu of a list or a page is about: the control under the pointer when the menu was asked for with a
/// right-click, else the control that holds the keyboard focus (Menu key, Shift+F10). A <see cref="ContextMenuHost"/> composes its
/// menu without the event, so a surface with several targets (rows of a table, the rows of a settings page) reads the target
/// here. The pointer target is dropped by the next key press, so a menu asked for with the keyboard never reuses an old click.
/// </summary>
internal sealed class ContextMenuTarget
{
    private readonly Control _origin;
    private Visual? _pointer;

    internal ContextMenuTarget(Control origin)
    {
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        // Tunnel and handled-too: a list that handles its own presses (selection) must not hide the click from the menu.
        origin.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        origin.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The control the menu is for: the last right-clicked control, else the focused one; null when there is neither.</summary>
    internal Visual? Source
    {
        get
        {
            if (_pointer is { } clicked && clicked.IsAttachedToVisualTree()) return clicked;
            return TopLevel.GetTopLevel(_origin)?.FocusManager?.GetFocusedElement() as Visual;
        }
    }

    /// <summary>True while the target is the last right-click (and not the focus).</summary>
    internal bool IsPointer => _pointer != null;

    /// <summary>The nearest ancestor-or-self of the target of the given type inside the origin.</summary>
    internal T? Find<T>() where T : class
    {
        for (Visual? node = Source; node != null; node = node.GetVisualParent())
        {
            if (node is T found) return found;
            if (ReferenceEquals(node, _origin)) break;
        }
        return null;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_origin).Properties.IsRightButtonPressed) _pointer = e.Source as Visual;
        else _pointer = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => _pointer = null;

    /// <summary>Sets the target by hand (tests).</summary>
    internal void SetPointerTarget(Visual? target) => _pointer = target;
}

/// <summary>The clipboard for the entries of the context menus: one place that reports a failure instead of swallowing it.</summary>
internal static class MenuClipboard
{
    /// <summary>Copies the text; a failure is logged and handed to <paramref name="failed"/> with the localized sentence.</summary>
    internal static async Task<bool> CopyAsync(Visual? owner, string text, Action<string>? failed = null)
    {
        try
        {
            if (TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) throw new InvalidOperationException("No clipboard.");
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception error)
        {
            Log.Warning(error, "The text could not be copied from a context menu.");
            failed?.Invoke(Localization.Texts.T("ctxmenu.copyFailed"));
            return false;
        }
    }
}
