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

    internal static void AssertSidebarHeader(Control header, Control segment, MediaPositionPill pill, Button close)
    {
        Rect BoundsIn(Control child) => new(Assert.IsType<Point>(child.TranslatePoint(default, header)), child.Bounds.Size);
        Rect tabs = BoundsIn(segment);
        Rect position = BoundsIn(pill);
        Rect cross = BoundsIn(close);
        Assert.True(pill.IsEffectivelyVisible);
        Assert.True(position.Width > 0 && position.Height > 0, $"Position bounds: {position}");
        Assert.Equal(0, tabs.Left, 0.5);
        Assert.Equal(0, tabs.Top, 0.5);
        Assert.Equal(32, tabs.Height, 0.5);
        Assert.Equal(32, cross.Height, 0.5);
        Assert.Equal(tabs.Right + MediaSidebarHeader.HeaderGap, position.Left, 0.5);
        Assert.Equal(cross.Left - MediaSidebarHeader.HeaderGap, position.Right, 0.5);
        Assert.Equal(header.Bounds.Width, cross.Right, 0.5);
        Assert.Equal(cross.Center.Y, position.Center.Y, 0.5);
    }

    private static void AssertHeaderRow(GallerySidePanel panel)
    {
        Control segment = Assert.IsAssignableFrom<Control>(panel.PostButton.Parent);
        AssertSidebarHeader(panel, segment, panel.Position, panel.CloseButton);
        Assert.Equal(panel.PostButton.Bounds.Right + 4, panel.TagsButton.Bounds.Left, 0.5);
        Assert.Equal(32, panel.PostButton.Bounds.Height);
        Assert.Equal(32, panel.TagsButton.Bounds.Height);
    }

    [Theory]
    [InlineData(280)]
    [InlineData(360)]
    [InlineData(480)]
    public void ThreadMediaCounterAndRatingShareTheSameLayoutInBothTabs(double width) => WithSidePanel(width, panel =>
    {
        GalleryDecisionRow post = new("PostDecision", compact: true);
        GalleryDecisionRow tags = new("TagDecision", compact: true);
        post.Show(new GalleryDecisionView(1, GalleryDecisionCoverage.None, 3, false, true));
        tags.Show(new GalleryDecisionView(1, GalleryDecisionCoverage.None, null, false, true));
        panel.PostContent = post;
        panel.TagContent = tags;
        panel.Position.Show(new(16, 23, false));

        foreach (GallerySidePanelSegment tab in Enum.GetValues<GallerySidePanelSegment>())
        {
            panel.Segment = tab;
            panel.UpdateLayout();
            AssertHeaderRow(panel);
            MediaPositionPill pill = Assert.Single(panel.Children.OfType<MediaPositionPill>());
            Assert.Same(panel.Position, pill);
            Assert.Contains(WorkspaceChips.Badge, pill.Classes);
            Assert.Equal("No. 16 / 23", Assert.IsType<TextBlock>(pill.Child).Text);
            Assert.Equal("Medium 16 of 23", ToolTip.GetTip(pill));

            GalleryDecisionRow row = tab == GallerySidePanelSegment.Post ? post : tags;
            Assert.True(row.IsEffectivelyVisible);
            Assert.Equal(tab == GallerySidePanelSegment.Post ? "3 of 5" : "Unrated", row.Summary.Text);
            Assert.True(row.StarButtons[0].Bounds.Bottom < row.FavoriteButton.Bounds.Top);
            Assert.True(row.FavoriteButton.Bounds.Width < panel.Bounds.Width);
            Assert.Equal(row.StarButtons[0].Bounds.Left, row.FavoriteButton.Bounds.Left, 0.5);
            Assert.True(row.FavoriteButton.Bounds.Right <= row.StarButtons[^1].Bounds.Right);
            Assert.True(row.ClearButton.Bounds.Bottom < row.Summary.Bounds.Top);
            Assert.Equal(row.ClearButton.Bounds.Left, row.Summary.Bounds.Left, 0.5);
        }

        panel.Position.Show(null);
        panel.UpdateLayout();
        Assert.False(panel.Position.IsVisible);
        Assert.Equal(0, ((Control)panel.PostButton.Parent!).Bounds.Left, 0.5);
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

        GalleryTagPanelPosition position = new(183, 197, false);
        panel.Position.Show(position, marked: true);
        panel.Segment = GallerySidePanelSegment.Tags;
        panel.UpdateLayout();

        AssertHeaderRow(panel);
        TextBlock label = Assert.IsType<TextBlock>(panel.Position.Child);
        Assert.Equal(GalleryTagPanelCommands.ShortCounterText(position, marked: true), label.Text);
        Assert.True(label.Bounds.Width > 0 && label.Bounds.Height > 0);
        Assert.Equal(tabHeight, ((Control)panel.PostButton.Parent!).Bounds.Height, 0.5);
    });

    [Fact]
    public void AtVeryNarrowWidthsTheCounterEllipsizesAndRecoversWhenWidened() => WithSidePanel(140, panel =>
    {
        panel.Position.Show(new(12345, 67890, true));
        panel.UpdateLayout();

        AssertHeaderRow(panel);
        TextBlock label = Assert.IsType<TextBlock>(panel.Position.Child);
        Assert.True(Assert.Single(label.TextLayout.TextLines).HasCollapsed);

        for (int width = 144; width <= 480; width += 4)
        {
            panel.Width = width;
            panel.UpdateLayout();
            AssertHeaderRow(panel);
        }
        Assert.False(Assert.Single(label.TextLayout.TextLines).HasCollapsed);
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
