using System.Reflection;
using Pointer = Avalonia.Input.Pointer;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using MeowChan.Admission;

namespace MeowChan.DesktopSmoke;

// External row realization/input only: exact identity, real checkbox, original5s waits.
internal static class UdBoardVirtualizedRows
{
    internal static BoardCatalogThread Thread(object row) => row.GetType().GetField("Thread", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?.GetValue(row) as BoardCatalogThread ?? throw new InvalidOperationException("Preview item lost actual catalog identity.");
    internal static bool Included(object row) => row.GetType().GetProperty("Included", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?.GetValue(row) is true;
    internal static object[] ExactRemovedTargets(ListBox list, string board)
    {
        object[] items = list.Items.Cast<object>().ToArray();
        Require(items.Length == 4 && items.Select(item => Thread(item).Id).SequenceEqual(new[] { "2", "3", "1", "4" }),
            "Removed-match fixture lost four exact rows or matching2/3-first order.");
        object[] targets = new[] { "2", "3" }.Select(id => items.Single(item => Thread(item).Id == id)).ToArray();
        Require(targets.Length == 2 && targets.All(item => Thread(item).Suppressed && Thread(item).Url == board + "thread/" + Thread(item).Id
            && Thread(item).Title.Contains("/dpt/", StringComparison.Ordinal) && !Included(item)),
            "Expected exact two suppressed /dpt/ rows2/3 are not both initially excluded.");
        return targets;
    }
    internal static async Task<CheckBox> RealizeAsync(Window modal, ListBox list, object item, string stage)
    {
        BoardCatalogThread identity = Thread(item);
        list.ScrollIntoView(item);
        Control? container = null; CheckBox? checkbox = null;
        await Program.WaitUntilAsync(() =>
        {
            container = list.ContainerFromItem(item);
            if (container == null || !ReferenceEquals(container.DataContext, item)) return false;
            CheckBox[] exact = container.GetVisualDescendants().OfType<CheckBox>()
                .Where(c => c.Name == "BoardAdmissionInclude" && ReferenceEquals(c.DataContext, item)).ToArray();
            if (exact.Length != 1) return false;
            checkbox = exact[0];
            return checkbox.IsEffectivelyVisible && checkbox.Bounds.Width > 0 && checkbox.Bounds.Height > 0;
        });
        CheckBox target = checkbox!;
        Require(target.Content is TextBlock text && text.Text?.StartsWith("#" + identity.Id + " · /dpt/", StringComparison.Ordinal) == true,
            "Realized checkbox content does not belong to exact expected /dpt/ row" + identity.Id);
        target.BringIntoView();
        Require(target.Focus(NavigationMethod.Tab), "Realized row checkbox cannot receive focus: " + identity.Id);
        await Program.WaitUntilAsync(() => target.IsFocused && ReferenceEquals(target.DataContext, item) && ReferenceEquals(list.ContainerFromItem(item), container)
            && Fits(modal, target) && Hit(modal, target) != null);
        Trace(modal, list, item, target, stage);
        Program.CaptureWindow(modal, "smoke-board-removed-exact-" + identity.Id + "-" + stage + ".png");
        return target;
    }
    internal static void PointerClick(Window modal, CheckBox checkbox, object item)
    {
        Require(ReferenceEquals(checkbox.DataContext, item) && checkbox.IsFocused && Fits(modal, checkbox),
            "Checkbox identity/focus/viewport changed before single actual click.");
        (InputElement Target, Point Point)? hit = Hit(modal, checkbox);
        Require(hit != null, "Exact checkbox lost its actual hit target before click.");
        var actual = hit!.Value;
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        actual.Target.RaiseEvent(new PointerPressedEventArgs(actual.Target, pointer, modal, actual.Point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        actual.Target.RaiseEvent(new PointerReleasedEventArgs(actual.Target, pointer, modal, actual.Point, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        pointer.Capture(null);
    }
    private static (InputElement Target, Point Point)? Hit(Window modal, CheckBox target)
    {
        Point? point = target.TranslatePoint(new Point(Math.Min(16, target.Bounds.Width / 2), target.Bounds.Height / 2), modal);
        if (point == null || modal.InputHitTest(point.Value) is not InputElement input || !input.GetSelfAndVisualAncestors().Contains(target)) return null;
        return (input, point.Value);
    }
    private static bool Fits(Window modal, Control target)
    {
        Point? at = target.TranslatePoint(default, modal);
        if (at == null || at.Value.X < -1 || at.Value.Y < -1 || at.Value.X + target.Bounds.Width > modal.ClientSize.Width + 1
            || at.Value.Y + target.Bounds.Height > modal.ClientSize.Height + 1) return false;
        foreach (ScrollContentPresenter viewport in target.GetVisualAncestors().OfType<ScrollContentPresenter>())
        {
            Point? relative = target.TranslatePoint(default, viewport);
            if (relative == null || relative.Value.X < -1 || relative.Value.Y < -1
                || relative.Value.X + target.Bounds.Width > viewport.Bounds.Width + 1
                || relative.Value.Y + target.Bounds.Height > viewport.Bounds.Height + 1) return false;
        }
        return true;
    }
    internal static void Trace(Window modal, ListBox list, object item, CheckBox checkbox, string stage)
    {
        string line = JsonSerializer.Serialize(new { stage, identity = Thread(item), included = Included(item), checkbox.IsChecked,
            checkbox.IsFocused, exactDataContext = ReferenceEquals(checkbox.DataContext, item), actualContainer = list.ContainerFromItem(item)?.GetType().Name,
            actualClient = modal.ClientSize.ToString(), actualScale = modal.RenderScaling, checkboxBounds = checkbox.Bounds.ToString(),
            viewports = checkbox.GetVisualAncestors().OfType<ScrollContentPresenter>().Select(v => new { bounds = v.Bounds.ToString() }).ToArray(),
            input = "one actual hit-owned routed pointer press/release per activation; no physical/native-input claim" });
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ud-board-exact-rows.jsonl"), line + Environment.NewLine);
        Console.WriteLine("SMOKE board exact row: " + line);
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
