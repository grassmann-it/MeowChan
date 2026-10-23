using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static class MediaTilePanelChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        bool baseline = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_PANEL_BASELINE") == "1";
        foreach (int count in new[] { 100, 1000, 10000 })
        {
            ObservableCollection<string> data = new(Enumerable.Range(0, count).Select(i => "Archive fixture " + i));
            ListBox list = new()
            {
                Padding = new Thickness(13, 17),
                AutoScrollToSelectedItem = false,
                ItemsSource = data,
                ItemsPanel = new FuncTemplate<Panel?>(() => baseline
                    ? new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 190, ItemHeight = 210 }
                    : new MediaTilePanel()),
                ItemTemplate = new FuncDataTemplate<string>((item, _) => new Border
                {
                    Width = 170, Height = 190, Padding = new Thickness(4),
                    Child = new StackPanel { Spacing = 4, Children =
                    {
                        new Border { Height = 125, Background = Brushes.DimGray },
                        new TextBlock { Text = item, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = "1920 × 1080 · PNG", FontSize = 11 }
                    }}
                })
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            Window fixture = new() { Width = 835, Height = 590, Content = list, Title = "Media panel fixture" };
            Stopwatch timer = Stopwatch.StartNew();
            fixture.Show(owner);
            try
            {
                await Settle(fixture);
                long initialMs = timer.ElapsedMilliseconds;
                ScrollContentPresenter presenter = list.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
                MediaTilePanel? panel = list.GetVisualDescendants().OfType<MediaTilePanel>().SingleOrDefault();
                int initialContainers = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
                long initialMemory = Process.GetCurrentProcess().WorkingSet64;
                if (!baseline)
                {
                    Require(panel != null && panel.Columns == (int)Math.Floor(panel.Bounds.Width / 190), "Columns do not follow finite inner width.");
                    Require(initialContainers < 80, "Initial tile generation is not bounded.");
                }

                timer.Restart();
                foreach (int target in new[] { count / 2, count - 1, 0, count - 1 })
                {
                    list.ScrollIntoView(data[target]);
                    // The container must exist before native GetControl returns.
                    Require(list.ContainerFromIndex(target) != null, "ScrollIntoView did not synchronously realize target " + target);
                    await Settle(fixture);
                    CheckVisible(list, presenter, target);
                }
                long scrollMs = timer.ElapsedMilliseconds;
                if (!baseline)
                {
                    foreach ((Key key, int expected) in new[] { (Key.End, count - 1), (Key.Home, 0), (Key.End, count - 1) })
                    {
                        Press(list, key);
                        Require(list.SelectedIndex == expected, $"Native {key} selected {list.SelectedIndex}, expected {expected}.");
                        CheckVisible(list, presenter, expected);
                    }
                    // Exercise native grid-axis movement from a realized row.
                    int bottom = list.SelectedIndex;
                    Press(list, Key.Up);
                    Require(list.SelectedIndex == bottom - panel!.Columns, "Native Up did not move one grid row.");
                    CheckVisible(list, presenter, list.SelectedIndex);
                    int rightEdge = list.SelectedIndex;
                    Press(list, Key.Left);
                    Require(list.SelectedIndex == rightEdge - 1, "Native Left did not move one grid cell.");
                    CheckVisible(list, presenter, list.SelectedIndex);
                    Press(list, Key.Right);
                    Require(list.SelectedIndex == rightEdge, "Native Right did not move one grid cell.");
                    CheckVisible(list, presenter, list.SelectedIndex);
                    int beforePage = list.SelectedIndex;
                    Press(list, Key.PageUp);
                    Require(list.SelectedIndex < beforePage, "Native PageUp did not move.");
                    CheckVisible(list, presenter, list.SelectedIndex);
                    beforePage = list.SelectedIndex;
                    Press(list, Key.PageDown);
                    Require(list.SelectedIndex > beforePage, "Native PageDown did not move.");
                    CheckVisible(list, presenter, list.SelectedIndex);

                    // All performance sizes divide by four: remove one item to
                    // explicitly exercise the final row with a missing last cell.
                    string tail = data[^1];
                    data.RemoveAt(data.Count - 1);
                    await Settle(fixture);
                    int columns = panel!.Columns;
                    int partialCount = data.Count;
                    int lastRow = (partialCount - 1) / columns;
                    int aboveLast = Math.Max(0, (lastRow - 1) * columns + columns - 1);
                    list.SelectedIndex = aboveLast;
                    list.ScrollIntoView(data[aboveLast]);
                    Press(list, Key.Down);
                    Require(list.SelectedIndex == Math.Min(partialCount - 1, aboveLast + columns), "Down skipped the partial final row.");
                    CheckVisible(list, presenter, list.SelectedIndex);
                    data.Add(tail);
                    await Settle(fixture);
                    list.ScrollIntoView(data[^1]);
                }

                (int Index, double Offset)? resizeAnchor = panel?.CaptureAnchor();
                timer.Restart();
                fixture.Width = 430;
                await Program.WaitUntilAsync(() => Math.Abs(fixture.ClientSize.Width - 430) < 1);
                await Settle(fixture);
                Console.WriteLine($"SMOKE media-resize immediate old={resizeAnchor} current={panel?.CaptureAnchor()} range={panel?.FirstRealizedIndex}..{panel?.LastRealizedIndex} presenterOffset={presenter.Offset} extent={presenter.Extent} bounds={panel?.Bounds}");
                if (!baseline)
                    await Program.WaitUntilAsync(() =>
                    {
                        fixture.UpdateLayout();
                        return panel!.Columns == 2 && panel.CaptureAnchor() is { } current
                            && resizeAnchor is { } previous && current.Index == previous.Index
                            && Math.Abs(current.Offset - previous.Offset) < 1;
                    });
                long resizeMs = timer.ElapsedMilliseconds;
                if (!baseline)
                {
                    Require(panel!.Columns == 2, "Deep resize did not recompute columns from the inner viewport.");
                    Require(resizeAnchor.HasValue, "Deep resize did not have an anchor.");
                    Console.WriteLine($"SMOKE media-resize old={resizeAnchor} current={panel.CaptureAnchor()} range={panel.FirstRealizedIndex}..{panel.LastRealizedIndex} presenterOffset={presenter.Offset} extent={presenter.Extent} bounds={panel.Bounds}");
                    CheckVisible(list, presenter, resizeAnchor!.Value.Index);
                    (int Index, double Offset)? restored = panel.CaptureAnchor();
                    Require(restored.HasValue && restored.Value.Index == resizeAnchor.Value.Index
                        && Math.Abs(restored.Value.Offset - resizeAnchor.Value.Offset) < 1,
                        "Deep resize lost the first visible item/offset.");
                    list.ScrollIntoView(data[count / 2]);
                    data.Insert(0, "inserted");
                    await Settle(fixture);
                    CheckRealizedData(list, data);
                    data.RemoveAt(0);
                    data.Move(0, data.Count - 1);
                    data[data.Count / 2] = "replacement";
                    await Settle(fixture);
                    CheckRealizedData(list, data);
                    list.ItemsSource = data.Reverse().ToArray();
                    await Settle(fixture);
                    CheckRealizedData(list, list.Items.Cast<string>().ToList());
                    list.ItemsSource = null;
                    await Settle(fixture);
                    Require(!list.GetVisualDescendants().OfType<ListBoxItem>().Any(x => x.IsVisible), "Empty collection retained visible tiles.");
                    list.ItemsSource = data;
                    await Settle(fixture);
                    list.ScrollIntoView(data[^1]);
                    CheckVisible(list, presenter, count - 1);
                    fixture.Width = 180;
                    await Program.WaitUntilAsync(() => Math.Abs(fixture.ClientSize.Width - 180) < 1);
                    await Settle(fixture);
                    Require(panel.Columns == 1 && panel.ItemWidth < 190, "Narrow viewport does not shrink its single cell.");
                    fixture.Width = 430;
                    await Program.WaitUntilAsync(() => Math.Abs(fixture.ClientSize.Width - 430) < 1);
                    await Settle(fixture);
                }
                int realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
                if (!baseline) Require(realized < 80, "Deep scrolling accumulated tile containers.");
                Console.WriteLine($"SMOKE media-panel mode={(baseline ? "wrap-baseline" : "ursa-adapter")} n={count} initial={initialMs}ms scroll-cycle={scrollMs}ms resize={resizeMs}ms containers={initialContainers}/{realized} working-set={initialMemory}/{Process.GetCurrentProcess().WorkingSet64}");
            }
            finally { fixture.Close(); }
        }
    }

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static void Press(ListBox list, Key key)
    {
        Control target = list.ContainerFromIndex(list.SelectedIndex) ?? list;
        target.Focus();
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
    }

    private static void CheckVisible(ListBox list, ScrollContentPresenter viewport, int index)
    {
        Control container = list.ContainerFromIndex(index) ?? throw new InvalidOperationException("Missing target " + index);
        Point? position = container.TranslatePoint(default, viewport);
        Require(position.HasValue && container.IsEffectivelyVisible
            && new Rect(position.Value, container.Bounds.Size).Intersects(new Rect(viewport.Bounds.Size)),
            "Realized target is outside the actual viewport: " + index + " at " + position);
    }

    private static void CheckRealizedData(ListBox list, IReadOnlyList<string> data)
    {
        foreach (ListBoxItem item in list.GetVisualDescendants().OfType<ListBoxItem>().Where(x => x.IsVisible))
        {
            int index = list.IndexFromContainer(item);
            Require(index >= 0 && index < data.Count && Equals(item.DataContext, data[index]), "Collection mutation left stale realized index/data.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
