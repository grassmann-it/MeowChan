using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Input;

namespace MeowChan.DesktopTests;

// No window, native input, profile or application startup. Exercise the actual XAML layout
// with deterministic boxes in place of text/templates; Windows' recorded segment was 760x196.
[Collection("Avalonia global state")]
public sealed class DiagnosticsFooterLayoutTests
{
    [Theory]
    [InlineData(760, 196, 124, 16)] // CI: old scroll viewport + footer exceeded the segment by 4 DIP.
    [InlineData(640, 160, 124, 16)] // A narrower, wrapped action row.
    [InlineData(760, 196, 480, 64)] // Bounded error plus a much larger scroll-content minimum.
    [InlineData(640, 196, 480, 64)]
    [InlineData(1380, 550, 480, 16)]
    [InlineData(760, 196, 124, 16, 0)]
    [InlineData(640, 196, 124, 64, 0)]
    public void ScrollContentCannotPushFixedActionsBelowTheSegment(double width, double height, double contentMinimum, double errorHeight, double lateShrink = 16) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            DiagnosticsPage page = new();
            try
            {
                Panel layout = Assert.IsAssignableFrom<Panel>(page.Content);
                StackPanel actions = layout.Children.OfType<StackPanel>().Single();
                WrapPanel toolbar = actions.Children.OfType<WrapPanel>().Single();
                ScrollViewer error = actions.Children.OfType<ScrollViewer>().Single();
                Assert.Equal(64, error.MaxHeight);

                // Keep the real footer's stack/wrap structure, spacing and inset. Fixed-size boxes
                // remove platform font dependencies and reproduce the native action dimensions.
                Border errorBox = new() { Height = errorHeight };
                actions.Children[0] = errorBox;
                toolbar.Children.Clear();
                foreach (double buttonWidth in width == 640 ? new[] { 142d, 230d, 90d, 200d } : new[] { 111d, 189d, 75d, 161d })
                    toolbar.Children.Add(new Border { Width = buttonWidth, Height = 32 });
                // A retained scroll minimum must affect only the scroll child, never footer placement.
                int scrollIndex = layout.Children.IndexOf(page.FindControl<ScrollViewer>("DiagnosticsContentScroll")!);
                layout.Children[scrollIndex] = new Border { MinHeight = contentMinimum };
                page.Content = null;
                toolbar.ItemSpacing = toolbar.LineSpacing = 8;
                // Native resize can arrange a smaller segment than the last measure pass. The old
                // star/auto Grid retained that star-row minimum and put the footer 16 DIP too low.
                layout.Measure(new Size(width, height + lateShrink));
                layout.Arrange(new Rect(0, 0, width, height));
                Assert.Equal(height - 12, layout.Bounds.Bottom, 3);
                Assert.Equal(layout.Bounds.Height, actions.Bounds.Bottom, 3);
                Assert.True(actions.Bounds.Top >= 0);
                Assert.Equal(width == 640 ? 72 : 32, toolbar.Bounds.Height);
                foreach (Control button in toolbar.Children)
                {
                    Point origin = button.TranslatePoint(default, layout)!.Value;
                    Assert.True(origin.Y >= 0 && origin.Y + button.Bounds.Height <= layout.Bounds.Height,
                        $"Action at {origin} ({button.Bounds.Size}) escaped {layout.Bounds}.");
                }
            }
            finally { page.EndLifetime(); }
        });

    [Fact]
    public void ContentRemainsBeforeTheDockedFooterInKeyboardOrder() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        DiagnosticsPage page = new();
        try
        {
            DockPanel layout = Assert.IsType<DockPanel>(page.Content);
            StackPanel actions = page.FindControl<StackPanel>("DiagnosticsActions")!;
            ScrollViewer content = page.FindControl<ScrollViewer>("DiagnosticsContentScroll")!;
            Assert.Same(actions, layout.Children[0]);
            Assert.Same(content, layout.Children[^1]);
            Assert.Equal(Dock.Bottom, DockPanel.GetDock(actions));
            Assert.True(layout.LastChildFill);
            Assert.Equal(KeyboardNavigationMode.Local, KeyboardNavigation.GetTabNavigation(content));
            Assert.Equal(KeyboardNavigationMode.Local, KeyboardNavigation.GetTabNavigation(actions));
            Assert.True(KeyboardNavigation.GetTabIndex(content) < KeyboardNavigation.GetTabIndex(actions));
        }
        finally { page.EndLifetime(); }
    });
}
