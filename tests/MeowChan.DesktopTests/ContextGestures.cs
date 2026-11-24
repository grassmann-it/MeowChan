using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.DesktopTests;

/// <summary>Delivers input through the arranged headless window or popup. Pointer coordinates are local to the target control.</summary>
internal static class ContextGestures
{
    internal static void RightClick(Control target, Point? point = null, RawInputModifiers modifiers = RawInputModifiers.None) =>
        Click(target, MouseButton.Right, point, modifiers);

    internal static void MiddleClick(Control target, Point? point = null, RawInputModifiers modifiers = RawInputModifiers.None) =>
        Click(target, MouseButton.Middle, point, modifiers);

    internal static void DoubleClick(Control target, Point? point = null, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var (root, position) = Locate(target, point);
        // Send the pair together so Avalonia can recognize the double tap from the raw clicks.
        ClickAt(root, position, MouseButton.Left, modifiers);
        ClickAt(root, position, MouseButton.Left, modifiers);
        Settle(root);
    }

    internal static void MenuKey(Control target) => KeyStroke(target, Key.Apps, PhysicalKey.ContextMenu, RawInputModifiers.None);

    internal static void ShiftF10(Control target) => KeyStroke(target, Key.F10, PhysicalKey.F10, RawInputModifiers.Shift);

    internal static void Click(Control target, MouseButton button = MouseButton.Left, Point? point = null,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var (root, position) = Locate(target, point);
        ClickAt(root, position, button, modifiers);
        Settle(root);
    }

    // Keep press and release available separately for gestures that cross rows or open a popup on press.
    internal static void Press(Control target, MouseButton button, Point? point = null, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var (root, position) = Locate(target, point);
        root.MouseDown(position, button, modifiers);
        Settle(root);
    }

    internal static void Release(Control target, MouseButton button, Point? point = null, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var (root, position) = Locate(target, point);
        root.MouseUp(position, button, modifiers);
        Settle(root);
    }

    private static void ClickAt(TopLevel root, Point position, MouseButton button, RawInputModifiers modifiers)
    {
        root.MouseDown(position, button, modifiers);
        root.MouseUp(position, button, modifiers);
    }

    private static void KeyStroke(Control target, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers)
    {
        TopLevel root = Arrange(target);
        if (!ReferenceEquals(root.FocusManager?.GetFocusedElement(), target)) Assert.True(target.Focus());
        Assert.Same(target, root.FocusManager?.GetFocusedElement());
        root.KeyPress(key, modifiers, physicalKey, null);
        root.KeyRelease(key, modifiers, physicalKey, null);
        Settle(root);
    }

    private static (TopLevel Root, Point Position) Locate(Control target, Point? point)
    {
        TopLevel root = Arrange(target);
        Rect bounds = new(target.Bounds.Size);
        Assert.True(bounds.Width > 0 && bounds.Height > 0, "The gesture target needs arranged bounds.");
        Point local = point ?? bounds.Center;
        Assert.True(bounds.Contains(local), $"The gesture point {local} is outside the target bounds {bounds}.");
        Point position = Assert.IsType<Point>(target.TranslatePoint(local, root));
        Assert.True(new Rect(root.ClientSize).Contains(position), $"The gesture point {position} is outside the window or popup.");
        return (root, position);
    }

    private static TopLevel Arrange(Control target)
    {
        TopLevel root = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(target));
        Settle(root);
        Assert.True(target.IsEffectivelyVisible, "The gesture target needs to be visible.");
        return root;
    }

    internal static void Settle(TopLevel root)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
    }
}
