using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using Semi.Avalonia;
using Ursa.Controls;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopTests;

// Real installed Ursa template and Avalonia measure/arrange, not just the pure slot
// arithmetic. Native Archive screenshots additionally cover labels and shell widths.
[Collection("Avalonia global state")]
public sealed class ActionToolBarRenderedStretchTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstalledTemplateStretchesOnlyTheOptedInTrailingPairWithoutResizeOscillation(bool stretch)
    {
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            Application app = new() { RequestedThemeVariant = ThemeVariant.Dark };
            AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
            app.Styles.Add(new SemiTheme());
            app.Styles.Add(new UrsaSemiTheme());
            ActionToolBar bar = new();
            ToggleButton first = new() { MinWidth = 64, Height = 32 };
            ToggleButton last = new() { MinWidth = 64, Height = 32, Margin = new Thickness(-1, 0, 0, 0) };
            foreach (var item in new[] { first, last })
            {
                item.Classes.Add("segment");
                ActionToolBar.SetStretch(item, stretch);
                ToolBar.SetOverflowMode(item, OverflowMode.AsNeeded);
                bar.Items.Add(item);
            }
            Button action = new() { Width = 220, Height = 32 };
            ActionToolBar.SetGroup(action, 1);
            ToolBar.SetOverflowMode(action, OverflowMode.AsNeeded);
            bar.Items.Add(action);
            Root root = new() { Child = bar };
            bar.Theme = (ControlTheme)app.FindResource(typeof(ToolBar))!;
            bar.ApplyStyling();
            bar.ApplyTemplate();
            foreach (var itemsPresenter in bar.GetVisualDescendants().OfType<ItemsPresenter>()) itemsPresenter.ApplyTemplate();
            // Realize nested template parts as a native layout manager would. No
            // replacement presenter/template that could hide Ursa's alignment bug.
            for (int pass = 0; pass < 4; pass++)
                foreach (Control control in bar.GetVisualDescendants().OfType<Control>().ToArray())
                {
                    control.ApplyStyling();
                    if (control is TemplatedControl templated) templated.ApplyTemplate();
                }
            try
            {
                foreach (OverflowMode mode in new[] { OverflowMode.AsNeeded, OverflowMode.Always })
                {
                    ToolBar.SetOverflowMode(action, mode);
                    foreach (double width in new double[] { 330, 1440, 760, 600, 180, 140, 180, 600, 760, 1440, 330 })
                    {
                        // Repeated unchanged passes must preserve the same slots/overflow;
                        // stretched widths must never feed back into natural measurement.
                        Rect[]? previous = null;
                        Control[]? previousOverflow = null;
                        for (int pass = 0; pass < 6; pass++)
                        {
                            root.InvalidateMeasure();
                            bar.InvalidateMeasure();
                            bar.DirectHost?.InvalidateMeasure();
                            root.Measure(new Size(width, 32));
                            root.Arrange(new Rect(0, 0, width, 32));
                            if (pass < 2) continue;
                            Assert.NotNull(bar.DirectHost);
                            Assert.NotNull(bar.More);
                            var presenter = bar.GetVisualDescendants().OfType<ItemsPresenter>().Single();
                            Assert.Equal(HorizontalAlignment.Stretch, presenter.HorizontalAlignment);
                            Control[] overflow = bar.OverflowHost!.Children.ToArray();
                            bool pairReduced = overflow.Contains(first);
                            Assert.Equal(pairReduced, overflow.Contains(last));
                            Assert.Equal(3, bar.DirectHost.Children.Count + overflow.Length);
                            Assert.Equal(new Control[] { first, last, action }, bar.GeneratedItems);
                            Rect[] bounds = [first.Bounds, last.Bounds, action.Bounds, bar.More.Bounds, bar.DirectHost.Bounds];
                            if (previous != null)
                            {
                                Assert.Equal(previous, bounds);
                                Assert.Equal(previousOverflow, overflow);
                            }
                            previous = bounds; previousOverflow = overflow;
                            if (pairReduced) continue;
                            Assert.InRange(first.DesiredSize.Width, 63.99, 64.01);
                            Assert.InRange(last.DesiredSize.Width, 62.99, 63.01);
                            Assert.InRange(last.Bounds.X - first.Bounds.Right, -2.01, -0.99);
                            bool shouldStretch = stretch && overflow.Contains(action);
                            if (shouldStretch)
                            {
                                Assert.InRange(Math.Abs(first.Bounds.Width - last.Bounds.Width), 0, 1);
                                Assert.True(last.Bounds.Right > 127, $"width={width}; pair={first.Bounds}/{last.Bounds}; panel={bar.DirectHost.Bounds}");
                            }
                            else
                            {
                                Assert.Equal(64, first.Bounds.Width);
                                Assert.Equal(64, last.Bounds.Width);
                            }
                        }
                    }
                }
            }
            finally { root.Child = null; }
        });
    }

    private sealed class Root : Decorator, Avalonia.LogicalTree.ILogicalRoot;
}
