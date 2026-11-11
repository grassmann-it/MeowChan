using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MEDIA_PANEL_ONLY: ListBox.ScrollIntoView after the grid is refilled (new row objects, as MediaPage.Present does)
// while a container is focused, at the top or far down; then Focus() on the target as MediaPage.FocusGrid does.
internal static class MediaTileRefillChecks
{
    private sealed record Row(int Index, string Name);

    internal static async Task RunAsync(MainWindow owner)
    {
        List<string> failures = new();
        foreach (string variant in new[] { "focus-deep", "focus-top-scrolled-away", "focus-deep-shrink", "focus-deep-same-items", "focus-deep-shrink-to-one" })
        foreach (int target in new[] { 0, 5, 37 })
        {
            if (variant == "focus-deep-shrink-to-one" && target != 0) continue;
            ListBox list = new()
            {
                SelectionMode = SelectionMode.Multiple,
                ItemsPanel = new FuncTemplate<Panel?>(() => new MediaTilePanel { ItemHeight = 250 }),
                ItemTemplate = new FuncDataTemplate<Row>((row, _) => new Border
                {
                    Width = 170, Height = 230, Background = Brushes.DimGray,
                    Child = new TextBlock { Text = row?.Name }
                })
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            Window fixture = new() { Width = 835, Height = 590, Content = list, Title = "Media refill fixture" };
            fixture.Show(owner);
            try
            {
                Row[] Rows(int count) => Enumerable.Range(0, count).Select(i => new Row(i, "Tile " + i)).ToArray();
                Row[] first = Rows(200);
                list.ItemsSource = first;
                await Settle(fixture);
                ScrollContentPresenter presenter = list.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
                if (variant == "focus-top-scrolled-away")
                {
                    (list.ContainerFromIndex(0) as Control)!.Focus();
                    list.Selection.Select(0);
                    presenter.Offset = new Vector(0, 120 * 250);
                }
                else
                {
                    list.ScrollIntoView(150);
                    await Settle(fixture);
                    (list.ContainerFromIndex(150) as Control)!.Focus();
                    list.Selection.Select(150);
                }
                await Settle(fixture);
                list.ItemsSource = variant switch
                {
                    "focus-deep-shrink" => Rows(60),
                    "focus-deep-shrink-to-one" => Rows(1),
                    "focus-deep-same-items" => first.ToArray(),
                    _ => Rows(200)
                };
                list.ScrollIntoView(target);
                string immediate = Describe(list, presenter, target);
                bool immediateOk = InView(list, presenter, target);
                await Settle(fixture);
                bool settledOk = InView(list, presenter, target);
                string settled = Describe(list, presenter, target);
                // MediaPage.FocusGrid: ScrollIntoView, then focus the container (focus brings it into view by its own bounds).
                (list.ContainerFromIndex(target) as Control)?.Focus();
                await Settle(fixture);
                bool focusedOk = InView(list, presenter, target);
                string focused = Describe(list, presenter, target);
                string line = $"SMOKE media-refill variant={variant} target={target} immediate={(immediateOk ? "ok" : "FAIL")}[{immediate}] settled={(settledOk ? "ok" : "FAIL")}[{settled}] focus={(focusedOk ? "ok" : "FAIL")}[{focused}]";
                Console.WriteLine(line);
                if (!immediateOk || !settledOk || !focusedOk) failures.Add(line);
            }
            finally { fixture.Close(); }
        }
        if (failures.Count > 0) throw new InvalidOperationException("ScrollIntoView after a refill missed its target:\n" + string.Join("\n", failures));
    }

    private static bool InView(ListBox list, ScrollContentPresenter viewport, int index)
    {
        if (list.ContainerFromIndex(index) is not Control container || !container.IsEffectivelyVisible) return false;
        Point? position = container.TranslatePoint(default, viewport);
        return position.HasValue && new Rect(position.Value, container.Bounds.Size).Intersects(new Rect(viewport.Bounds.Size))
            && Equals((container.DataContext as Row)?.Index, index);
    }

    private static string Describe(ListBox list, ScrollContentPresenter viewport, int index)
    {
        Control? container = list.ContainerFromIndex(index) as Control;
        return $"offset={viewport.Offset.Y} extent={viewport.Extent.Height} container={(container is null ? "none" : container.Bounds + " at " + container.TranslatePoint(default, viewport) + " row=" + (container.DataContext as Row)?.Index + " focused=" + container.IsFocused)}";
    }

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }
}
