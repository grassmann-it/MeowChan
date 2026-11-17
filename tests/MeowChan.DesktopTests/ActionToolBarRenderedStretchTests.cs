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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndActionsDockWithoutChangingNaturalWidthsAndEmptyOverflowDisappears(bool hidePrimary)
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
            Button primary = new() { Width = 64, Height = 32, IsVisible = !hidePrimary };
            Button overflowAction = new() { Width = 90, Height = 32 };
            Button columns = new() { Width = 64, Height = 32 };
            Button details = new() { Width = 64, Height = 32 };
            ToolBar.SetOverflowMode(overflowAction, OverflowMode.Always);
            foreach (Button item in new[] { primary, overflowAction, columns, details }) bar.Items.Add(item);
            ActionToolBar.SetEnd(columns, true);
            ActionToolBar.SetEnd(details, true);
            ActionToolBar.SetGroup(columns, 1);
            ActionToolBar.SetGroup(details, 1);
            Root root = new() { Child = bar };
            bar.Theme = (ControlTheme)app.FindResource(typeof(ToolBar))!;
            bar.ApplyStyling();
            bar.ApplyTemplate();
            for (int pass = 0; pass < 5; pass++)
                foreach (Control control in bar.GetVisualDescendants().OfType<Control>().ToArray())
                {
                    control.ApplyStyling();
                    if (control is TemplatedControl templated) templated.ApplyTemplate();
                    if (control is ItemsPresenter presenter) presenter.ApplyTemplate();
                }
            void Layout(double width)
            {
                for (int pass = 0; pass < 6; pass++)
                {
                    // As in the stretch fixture, drive the unattached root's layout explicitly.
                    root.InvalidateMeasure();
                    bar.InvalidateMeasure();
                    foreach (Control control in bar.GetVisualDescendants().OfType<Control>()) control.InvalidateMeasure();
                    root.Measure(new Size(width, 32));
                    bar.More?.ApplyStyling();
                    root.Arrange(new Rect(0, 0, width, 32));
                }
            }
            try
            {
                foreach (double width in new double[] { 600, 330, 760, 330 })
                {
                    Layout(width);
                    Assert.Contains(overflowAction, bar.OverflowHost!.Children);
                    Assert.True(bar.More!.IsVisible);
                    Assert.Equal(64, columns.Bounds.Width);
                    Assert.Equal(64, details.Bounds.Width);
                    Assert.Equal(ActionToolBarPanel.ItemGap, details.Bounds.X - columns.Bounds.Right);
                    Assert.InRange(Math.Abs(bar.DirectHost!.Bounds.Width - details.Bounds.Right), 0, 1);
                    Assert.Empty(bar.DirectHost.GroupSeparators);
                    Rect previous = details.Bounds;
                    Layout(width);
                    Assert.Equal(previous, details.Bounds);
                }
                // An overflow item is not a visual child of the bar: hiding it must still invalidate the bar.
                overflowAction.IsVisible = false;
                Assert.False(bar.DirectHost!.IsMeasureValid);
                Layout(330);
                Assert.Empty(bar.OverflowHost!.Children.Where(item => item.IsVisible));
                Assert.False(bar.More!.IsEffectivelyVisible);
                Assert.InRange(Math.Abs(bar.DirectHost.Bounds.Width - details.Bounds.Right), 0, 1);
                ActionToolBar.SetEnd(details, false);
                Assert.False(bar.DirectHost.IsArrangeValid);
                Layout(330);
                Assert.True(details.Bounds.Right < bar.DirectHost.Bounds.Width - 1);
            }
            finally { root.Child = null; }
        });
    }

    [Theory]
    [InlineData(180, false)]
    [InlineData(330, false)]
    [InlineData(600, false)]
    [InlineData(1000, false)]
    [InlineData(180, true)]
    [InlineData(330, true)]
    [InlineData(600, true)]
    [InlineData(1000, true)]
    public void ArchiveResultRowsFillTheSameContentEdgeAndNeverOverflowTheLayoutPair(double width, bool initiallyTable)
    {
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            Application app = new() { RequestedThemeVariant = ThemeVariant.Dark };
            AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
            app.Styles.Add(new SemiTheme());
            app.Styles.Add(new UrsaSemiTheme());
            // Read the production item's layout metadata, not a hard-coded corrected pair.
            // Fixed natural action widths make overflow breakpoints independent of fonts/locales.
            string source = Path.Combine(Path.GetDirectoryName(SourcePath())!, "../../Desktop/Views/Archive/ArchivePage.axaml");
            System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var markup = System.Xml.Linq.XDocument.Load(source);
            var toolbar = markup.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ArchiveResultActions");
            ActionToolBar bar = new() { Margin = Thickness.Parse((string)toolbar.Attribute("Margin")!) };
            foreach (var element in toolbar.Elements())
            {
                string? Attribute(string name) => element.Attributes().SingleOrDefault(a => a.Name.LocalName == name)?.Value;
                Control item = element.Name.LocalName == "ToggleButton"
                    ? new ToggleButton { MinWidth = 64, HorizontalAlignment = Enum.Parse<HorizontalAlignment>(Attribute("HorizontalAlignment")!) }
                    : new Button { Width = 70 };
                item.Name = (string?)element.Attribute(x + "Name");
                item.Height = 32;
                foreach (string cls in (Attribute("Classes") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) item.Classes.Add(cls);
                ActionToolBar.SetGroup(item, int.Parse(Attribute("ActionToolBar.Group")!));
                ActionToolBar.SetEnd(item, Attribute("ActionToolBar.End") == "True");
                ActionToolBar.SetStretch(item, Attribute("ActionToolBar.Stretch") == "True");
                ToolBar.SetOverflowMode(item, Enum.Parse<OverflowMode>(Attribute("ToolBar.OverflowMode") ?? "AsNeeded"));
                item.IsVisible = Attribute("IsVisible") != "False";
                bar.Items.Add(item);
            }
            Control[] items = bar.Items.Cast<Control>().ToArray();
            Control columns = items.Single(i => i.Name == "ArchiveThreadColumnsButton");
            Control first = items[^2], last = items[^1];
            // Same shared Grid/ScrollViewer and row margins as the archive search area.
            // Search/filter stand-ins isolate geometry from services and page startup.
            Grid rows = new() { RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,Auto,Auto") };
            Border search = new() { Height = 32 };
            Border filter = new() { Height = 32, Margin = new Thickness(0, 8, 0, 0) };
            Grid.SetRow(filter, 1); Grid.SetRow(bar, 4);
            rows.Children.Add(search); rows.Children.Add(filter); rows.Children.Add(bar);
            ScrollViewer scroll = new() { Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 260 };
            Root root = new() { Child = scroll };
            bar.Theme = (ControlTheme)app.FindResource(typeof(ToolBar))!;
            bar.ApplyStyling(); bar.ApplyTemplate();
            for (int pass = 0; pass < 5; pass++)
                foreach (Control control in bar.GetVisualDescendants().OfType<Control>().ToArray())
                {
                    control.ApplyStyling();
                    if (control is TemplatedControl templated) templated.ApplyTemplate();
                    if (control is ItemsPresenter presenter) presenter.ApplyTemplate();
                }
            void Layout()
            {
                for (int pass = 0; pass < 6; pass++)
                {
                    root.InvalidateMeasure(); scroll.InvalidateMeasure(); rows.InvalidateMeasure(); bar.InvalidateMeasure();
                    foreach (Control control in root.GetVisualDescendants().OfType<Control>()) control.InvalidateMeasure();
                    root.Measure(new Size(width, 200));
                    bar.More?.ApplyStyling();
                    root.Arrange(new Rect(0, 0, width, 200));
                }
            }
            try
            {
                foreach (bool table in new[] { initiallyTable, !initiallyTable, initiallyTable })
                {
                    columns.IsVisible = table;
                    Layout();
                    Assert.DoesNotContain(first, bar.OverflowHost!.Children);
                    Assert.DoesNotContain(last, bar.OverflowHost.Children);
                    Assert.Contains(first, bar.DirectHost!.Children);
                    Assert.Contains(last, bar.DirectHost.Children);
                    Control[] shown = bar.DirectHost.Children.Where(c => c.IsVisible).ToArray();
                    double unused = shown[0].Bounds.X;
                    for (int i = 1; i < shown.Length; i++)
                        unused += Math.Max(0, shown[i].Bounds.X - shown[i - 1].Bounds.Right - ActionToolBarPanel.LeadingGap(shown[i - 1], shown[i]));
                    double trailing = bar.DirectHost.Bounds.Width - last.Bounds.Right;
                    Console.WriteLine($"UX-LEDGER-25 width={width} columns={table} occupied={width - unused - trailing:F2} unused={unused:F2} trailing={trailing:F2} pair={first.Bounds.Width:F2}/{last.Bounds.Width:F2} overflow={bar.OverflowHost.Children.Count} panel={bar.DirectHost.Bounds} more={bar.More!.Bounds} margin={bar.More.Margin}");
                    Assert.InRange(Math.Abs(trailing), 0, 1);
                    Assert.InRange(unused, 0, 1);
                    Assert.True(first.Bounds.Width >= first.DesiredSize.Width);
                    Assert.True(last.Bounds.Width >= last.DesiredSize.Width);
                    Assert.Equal(first.Bounds.Right, last.Bounds.X, 6);
                    if (shown.Contains(columns))
                    {
                        Assert.Equal(70, columns.Bounds.Width);
                        Assert.Equal(ActionToolBarPanel.ItemGap, first.Bounds.X - columns.Bounds.Right, 6);
                    }
                    Assert.Equal(0, shown[0].Bounds.X, 6);
                    Assert.All(shown.Where(i => ActionToolBar.GetGroup(i) < 3), i => Assert.False(ActionToolBar.GetEnd(i)));
                    double Right(Control control) => control.TranslatePoint(new Point(control.Bounds.Width, 0), rows)!.Value.X;
                    Assert.Equal(width, Right(search), 6);
                    Assert.Equal(Right(search), Right(filter), 6);
                    Assert.Equal(Right(search), Right(bar), 6);
                    if (bar.More!.IsVisible)
                    {
                        Assert.InRange(Math.Abs(Right(bar.More) - Right(search)), 0, 1);
                        Assert.InRange(Math.Abs(Right(bar.More) - bar.More.Bounds.Width - Right(last) - bar.More.Margin.Left), 0, 1);
                    }
                    else Assert.InRange(Math.Abs(Right(last) - Right(search)), 0, 1);
                    Rect[] before = shown.Select(i => i.Bounds).ToArray();
                    Layout();
                    Assert.Equal(before, shown.Select(i => i.Bounds).ToArray());
                }
            }
            finally { root.Child = null; }
        });
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string source = "") => source;

    private sealed class Root : Decorator, Avalonia.LogicalTree.ILogicalRoot;
}
