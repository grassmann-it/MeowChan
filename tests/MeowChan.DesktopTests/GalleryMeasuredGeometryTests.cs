using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class GalleryMeasuredGeometryTests
{
    private static void OnDispatcher(Action test)
    {
        using IDisposable platform = AvaloniaTestPlatform.EnterSkia();
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            test();
        });
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        ((ILogical)root).GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static Point OriginIn(Control control, Control ancestor)
    {
        double x = 0, y = 0;
        for (Visual? current = control; current != null && !ReferenceEquals(current, ancestor); current = current.GetVisualParent())
        {
            if (current is Control child) { x += child.Bounds.X; y += child.Bounds.Y; }
        }
        return new Point(x, y);
    }

    [Theory]
    [InlineData(760)]
    [InlineData(1100)]
    [InlineData(1440)]
    public void GalleryPaginationIsCentredOnTheWholeToolbarAtTargetWidths(double width) => OnDispatcher(() =>
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            GalleryViewer viewer = new();
            ThemeVariantScope themeScope = new() { RequestedThemeVariant = theme, Child = viewer };
            themeScope.Measure(new Size(width, 720));
            themeScope.Arrange(new Rect(0, 0, width, 720));
            themeScope.UpdateLayout();
            Control toolbar = Find<Control>(viewer, "GalleryToolbar");
            toolbar.Measure(new Size(width - 32, 64));
            toolbar.Arrange(new Rect(0, 0, width - 32, toolbar.DesiredSize.Height));
            Control middle = Find<Control>(viewer, "GalleryToolbarMiddle");
            Control navigation = Find<Control>(viewer, "GalleryNavigationTools");
            Point middleOrigin = OriginIn(middle, toolbar);
            Point navigationOrigin = OriginIn(navigation, toolbar);
            Assert.InRange(Math.Abs(middleOrigin.X + middle.Bounds.Width / 2 - toolbar.Bounds.Width / 2), 0, 0.5);
            Assert.True(navigationOrigin.X >= 0 && navigationOrigin.X + navigation.Bounds.Width <= toolbar.Bounds.Width + 0.5);
            Assert.InRange(toolbar.Bounds.Height, 1, 64);
        }
    });

    [Theory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(480)]
    public void MediumCounterIsGeometricallyCentredInThePanelHeader(double width) => OnDispatcher(() =>
    {
        GallerySidePanel panel = new(new Button());
        Border counter = new() { Width = 92, Height = 18, Child = new TextBlock { Text = "Medium 1 of 4", TextAlignment = Avalonia.Media.TextAlignment.Center } };
        panel.HeaderCounter = counter;
        panel.Measure(new Size(width, 500));
        panel.Arrange(new Rect(0, 0, width, 500));
        panel.UpdateLayout();
        ContentControl slot = Assert.IsType<ContentControl>(counter.Parent);
        Point origin = OriginIn(slot, panel);
        Assert.InRange(Math.Abs(origin.X + slot.Bounds.Width / 2 - panel.Bounds.Width / 2), 0, 0.5);
        Assert.True(origin.X >= 0 && origin.X + slot.Bounds.Width <= panel.Bounds.Width + 0.5);
    });

    [Fact]
    public void SidePanelScrollersOwnEqualTokenInsetsOutsideTheirScrollbarGutter() => OnDispatcher(() =>
    {
        GalleryViewer viewer = new();
        typeof(GalleryViewer).GetMethod("SetContextOpen", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewer, new object[] { true, false, false });
        ThemeVariantScope galleryScope = new() { RequestedThemeVariant = ThemeVariant.Light, Child = viewer };
        galleryScope.Resources["Workspace.Inset.Section"] = new Thickness(16);
        galleryScope.Measure(new Size(1100, 720));
        galleryScope.Arrange(new Rect(0, 0, 1100, 720));
        ScrollViewer post = Find<ScrollViewer>(viewer, "GalleryPanelPostScroll");
        Border frame = Find<Border>(viewer, "GalleryContext");
        Assert.Equal(frame.Padding.Left, frame.Padding.Right);
        Assert.Equal(post.Padding.Left, post.Padding.Right);
        Assert.Equal(16, post.Padding.Left);

        ReaderMediaSidebar reader = new();
        ThemeVariantScope readerScope = new() { RequestedThemeVariant = ThemeVariant.Dark, Child = reader };
        readerScope.Resources["Workspace.Inset.Section"] = new Thickness(16);
        readerScope.Measure(new Size(420, 720));
        readerScope.Arrange(new Rect(0, 0, 420, 720));
        Assert.Equal(reader.Padding.Left, reader.Padding.Right);
        Assert.Equal(16, reader.Padding.Left);
    });
}
