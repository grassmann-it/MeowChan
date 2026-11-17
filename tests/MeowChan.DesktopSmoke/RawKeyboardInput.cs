using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;

namespace MeowChan.DesktopSmoke;

// Keys and text through a window's own platform input callback, the way the windowing backend delivers them: the input manager
// and keyboard device route them to the focused element (tunnel, class handlers, bubble; TextInput separately), so no handler is
// called directly. Still managed input in the private Xvfb session, not OS keyboard or IME acceptance.
internal static class RawKeyboardInput
{
    private static IInputRoot Root(TopLevel top) =>
        typeof(TopLevel).GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(top) as IInputRoot
        ?? throw new InvalidOperationException("The window has no input root.");

    private static IKeyboardDevice Device() =>
        typeof(KeyboardDevice).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as IKeyboardDevice
        ?? throw new InvalidOperationException("No keyboard device.");

    private static Action<RawInputEventArgs> Input(TopLevel top) => top.PlatformImpl?.Input
        ?? throw new InvalidOperationException("The window has no platform input callback.");

    private static ulong Now => (ulong)Environment.TickCount64;

    /// <summary>One key press: KeyDown, then KeyUp.</summary>
    internal static void Press(TopLevel top, Key key, RawInputModifiers modifiers = RawInputModifiers.None, int times = 1)
    {
        for (int index = 0; index < times; index++)
        {
            Input(top)(new RawKeyEventArgs(Device(), Now, Root(top), RawKeyEventType.KeyDown, key, modifiers, PhysicalKey.None, null, KeyDeviceType.Keyboard));
            Input(top)(new RawKeyEventArgs(Device(), Now, Root(top), RawKeyEventType.KeyUp, key, modifiers, PhysicalKey.None, null, KeyDeviceType.Keyboard));
        }
    }

    /// <summary>Typed text, one character after the other (each its own TextInput, like a keyboard).</summary>
    internal static void Type(TopLevel top, string text)
    {
        foreach (char c in text) Input(top)(new RawTextInputEventArgs(Device(), Now, Root(top), c.ToString()));
    }
}
