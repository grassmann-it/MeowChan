using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

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

    private static void WithSidePanel(double width, Action<GallerySidePanel> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        string oldLanguage = Texts.Language;
        Texts.SetLanguage("en");
        GallerySidePanel panel = new(new Button { Width = 32, Height = 32 }) { Width = width };
        Window window = new() { Width = 520, Height = 500, Content = panel };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            test(panel);
        }
        finally
        {
            window.Close();
            Texts.SetLanguage(oldLanguage);
        }
    });

    private static Rect BoundsIn(Control control, GallerySidePanel panel)
    {
        Point origin = Assert.IsType<Point>(control.TranslatePoint(default, panel));
        return new Rect(origin, control.Bounds.Size);
    }

    private static void AssertHeaderRow(Control counter, GallerySidePanel panel)
    {
        Rect bounds = BoundsIn(counter, panel);
        Rect post = BoundsIn(panel.PostButton, panel);
        Rect tags = BoundsIn(panel.TagsButton, panel);
        Rect close = BoundsIn(panel.CloseButton, panel);
        Assert.True(counter.IsEffectivelyVisible);
        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"Counter bounds: {bounds}");
        Assert.InRange(bounds.Left, -0.5, 0.5);
        Assert.True(bounds.Right + 8 <= post.Left + 0.5);
        Assert.True(post.Right + 4 <= tags.Left + 0.5);
        Assert.True(tags.Right + 8 <= close.Left + 0.5);
        Assert.Equal(panel.Bounds.Width, close.Right, 0.5);
        foreach (Rect tabOrClose in new[] { post, tags, close })
        {
            Assert.True(tabOrClose.Width > 0);
            Assert.Equal(32, tabOrClose.Height);
            Assert.Equal(0, tabOrClose.Top, 0.5);
            Assert.Equal(tabOrClose.Center.Y, bounds.Center.Y, 0.5);
        }
    }

    [Theory]
    [InlineData(280)]
    [InlineData(320)]
    [InlineData(480)]
    public void ThreadMediaCounterSharesTheHeaderRowWithTabsAndClose(double width) => WithSidePanel(width, panel =>
    {
        TextBlock counter = new() { Text = "1 of 18" };
        panel.HeaderCounter = counter;
        panel.UpdateLayout();
        AssertHeaderRow(counter, panel);

        panel.HeaderCounter = null;
        panel.UpdateLayout();
        Assert.Equal(0, BoundsIn(panel.PostButton, panel).Left, 0.5);
        Assert.Equal(0, BoundsIn(panel.PostButton, panel).Top, 0.5);
    });

    [Theory]
    [InlineData("en", 248)]
    [InlineData("en", 480)]
    [InlineData("de", 248)]
    [InlineData("cs", 248)]
    [InlineData("es", 248)] // 280 dip minus the panel's two 16-dip insets
    [InlineData("fr", 248)]
    [InlineData("it", 248)]
    [InlineData("ja", 248)]
    [InlineData("ko", 248)]
    [InlineData("pl", 248)]
    [InlineData("pt-BR", 248)]
    [InlineData("ru", 248)]
    [InlineData("zh-Hans", 248)]
    public void LocalizedTagCounterKeepsTheTabsOnOneRow(string language, double width) => WithSidePanel(width, panel =>
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        panel.Localize();
        panel.UpdateLayout();
        double tabHeight = ((Control)panel.PostButton.Parent!).Bounds.Height;

        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        using QuickTagSession session = new(QuickTagTarget.For(QuickTagSurfaceTests.Single(store, QuickTagSurfaceTests.Entry("a", 'a'))));
        using QuickTagSurface surface = new(session);
        GalleryTagPanelPosition position = new(183, 197, false);
        surface.CounterShortText = GalleryTagPanelCommands.ShortCounterText(position, marked: true);
        surface.CounterText = GalleryTagPanelCommands.CounterText(position, marked: true);
        Border counter = surface.DetachCounter();
        panel.HeaderCounter = counter;
        panel.UpdateLayout();

        AssertHeaderRow(counter, panel);
        TextBlock label = Assert.IsType<TextBlock>(counter.Child);
        Assert.Equal(surface.CounterShortText, label.Text);
        Assert.True(label.Bounds.Width > 0 && label.Bounds.Height > 0);
        Assert.Equal(tabHeight, ((Control)panel.PostButton.Parent!).Bounds.Height, 0.5);
    });

    [Fact]
    public void AtVeryNarrowWidthsTheCounterEllipsizesAndRecoversWhenWidened() => WithSidePanel(140, panel =>
    {
        TextBlock counter = new() { Text = "Medium 12345 of at least 67890", TextTrimming = TextTrimming.CharacterEllipsis };
        panel.HeaderCounter = counter;
        panel.UpdateLayout();

        AssertHeaderRow(counter, panel);
        Assert.True(Assert.Single(counter.TextLayout.TextLines).HasCollapsed);

        for (int width = 144; width <= 480; width += 4)
        {
            panel.Width = width;
            panel.UpdateLayout();
            AssertHeaderRow(counter, panel);
        }
        Assert.False(Assert.Single(counter.TextLayout.TextLines).HasCollapsed);
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
        Assert.Equal(8, frame.Padding.Top);
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
