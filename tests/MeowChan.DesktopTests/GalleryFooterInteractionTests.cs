using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class GalleryFooterInteractionTests
{
    [Fact]
    public void TooltipIsPassiveAndHoverSurvivesLayoutAndIdlePolling() => WithGallery((viewer, host, footer) =>
    {
        Button button = viewer.OverflowButton;
        ToolTip.SetShowDelay(button, 0);
        int openings = 0;
        ToolTip.AddToolTipOpeningHandler(button, (_, _) => openings++);
        Assert.True(viewer.Focus(NavigationMethod.Tab));
        Point center = Center(button, host);
        host.MouseMove(center);
        Assert.True(button.IsPointerOver);
        Assert.True(ToolTip.GetIsOpen(button));
        Popup popup = Assert.Single(host.OpenedPopups, p => p.Child is ToolTip);
        Assert.True(popup.IsUsingOverlayLayer);
        Assert.False(popup.IsHitTestVisible);
        Assert.Equal(PlacementMode.Top, popup.Placement);
        Assert.Same(viewer, host.FocusManager!.GetFocusedElement());

        Button slideshow = Assert.IsAssignableFrom<Button>(viewer.SlideshowSplit.Children[0]);
        object? slideshowContent = slideshow.Content;
        viewer.SetPosition("1 of 33", false, true);
        Invoke(viewer, "RefreshPlaybackControls");
        host.UpdateLayout();
        Assert.Same(slideshowContent, slideshow.Content);
        Assert.Equal(center, Center(button, host));
        Tick(viewer, age: true);
        Assert.True(footer.IsVisible);
        Assert.True(button.IsPointerOver);
        Assert.True(ToolTip.GetIsOpen(button));
        Assert.Equal(1, openings);

        // A relayout can invalidate the last sampled gallery coordinates without closing an open tip.
        Field("_lastPointerScreen").SetValue(viewer, null);
        Tick(viewer, age: true);
        Assert.True(footer.IsVisible);

        host.MouseMove(new Point(200, 160));
        Assert.False(ToolTip.GetIsOpen(button));
        Tick(viewer);
        Assert.True(footer.IsVisible);
        Tick(viewer, age: true);
        Assert.False(footer.IsVisible);
        Assert.False(footer.IsHitTestVisible);
    });

    [Fact]
    public void KeyboardFocusAndOpenSplitMenuKeepTheFooterUntilLeavingAndWaiting() => WithGallery((viewer, host, footer) =>
    {
        Button menu = viewer.SlideshowMenuButton;
        Assert.True(menu.Focus(NavigationMethod.Tab));
        host.MouseMove(new Point(200, 160));
        Tick(viewer, age: true);
        Assert.True(footer.IsVisible);
        Assert.Same(menu, host.FocusManager!.GetFocusedElement());

        Assert.True(viewer.SetSlideshowMenuOpen(true));
        Dispatcher.UIThread.RunJobs();
        Tick(viewer, age: true);
        Assert.True(footer.IsVisible);
        Assert.True(viewer.IsSlideshowMenuOpen);

        Assert.True(viewer.SetSlideshowMenuOpen(false));
        Assert.True(viewer.Focus(NavigationMethod.Tab));
        Tick(viewer);
        Assert.True(footer.IsVisible);
        Tick(viewer, age: true);
        Assert.False(footer.IsVisible);
    });

    [Fact]
    public void CapturedPressHoldsUntilCaptureIsLostEvenWhenReleaseIsOutsideTheGallery() => WithGallery((viewer, host, footer) =>
    {
        Button button = viewer.OverflowButton;
        Point center = Center(button, host);
        host.MouseMove(center);
        host.MouseDown(center, MouseButton.Left);
        IPointer pointer = (IPointer)Field("_lastPointer").GetValue(viewer)!;
        Assert.NotNull(pointer.Captured);
        host.MouseMove(new Point(200, 160), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        Tick(viewer, age: true);
        Assert.True(footer.IsVisible);

        pointer.Capture(null);
        Assert.Empty((HashSet<IPointer>)Field("_pressedPointers").GetValue(viewer)!);
        Assert.True(viewer.Focus(NavigationMethod.Tab));
        Tick(viewer);
        Assert.True(footer.IsVisible);
        Tick(viewer, age: true);
        Assert.False(footer.IsVisible);
    });

    [Theory]
    [InlineData("en", "Info", "Edit", "Help")]
    [InlineData("de", "Info", "Bearbeiten", "Hilfe")]
    [InlineData("ja", "情報", "編集", "ヘルプ")]
    public void WideLabelsFitBesideCentredNavigationAndShrinkWithoutReplacingButtons(
        string language, string info, string edit, string help) => WithGallery((viewer, host, _) =>
    {
        Texts.SetLanguage(language);
        host.UpdateLayout();
        Button context = Find<Button>(viewer, "GalleryToggleContext");
        Button tags = Find<Button>(viewer, "GalleryTags");
        Button shortcuts = Find<Button>(viewer, "GalleryHelp");
        GalleryToolbarPanel toolbar = Find<GalleryToolbarPanel>(viewer, "GalleryToolbar");
        var content = Assert.IsType<GalleryToolbarActionContent>(context.Content);
        Assert.Equal(info, Label(context));
        Assert.Equal(edit, Label(tags));
        Assert.Equal(help, Label(shortcuts));
        string? name = AutomationProperties.GetName(context);
        object? tip = ToolTip.GetTip(context);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.NotNull(tip);

        host.Width = 760;
        Dispatcher.UIThread.RunJobs();
        host.UpdateLayout();
        Assert.Same(content, context.Content);
        Assert.False(content.ShowLabel);
        Assert.Equal(name, AutomationProperties.GetName(context));
        Assert.Equal(tip, ToolTip.GetTip(context));

        // With enough room for some labels, a long label farther along the row must not suppress Info.
        host.Width = 2000;
        Dispatcher.UIThread.RunJobs();
        host.UpdateLayout();
        Assert.Equal(info, Label(context));

        host.Width = 2304;
        Dispatcher.UIThread.RunJobs();
        host.UpdateLayout();
        Assert.Same(content, context.Content);
        Assert.Equal(info, Label(context));
        Control navigation = toolbar.Children[1];
        Assert.InRange(Math.Abs(navigation.Bounds.Center.X - toolbar.Bounds.Width / 2), 0, 1);
        Assert.True(toolbar.Children[2].Bounds.Left >= navigation.Bounds.Right + 8);
    });

    private static string? Label(Button button) => Assert.IsType<GalleryToolbarActionContent>(button.Content) is { ShowLabel: true } content
        ? Assert.IsType<TextBlock>(content.Children[1]).Text : null;

    private static void WithGallery(Action<GalleryViewer, Window, Control> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        TransientPopupPolicy.Install();
        BooruComponentDispatcher.Wait(Exercise());

        async Task<bool> Exercise()
        {
            await using var viewer = new GalleryViewer();
            viewer.ApplyWorkView(WorkView.Advanced);
            viewer.ConfigurePlayback(new GalleryPlaybackPreferences(Mode: GalleryPlaybackMode.Slideshow, OverlayControls: true, AutoHideControls: true));
            viewer.SetPosition("10 of 333", true, true);
            Window host = new() { Width = 2304, Height = 680, Content = viewer, Classes = { MotionPolicy.OffClass } };
            try
            {
                host.Show();
                host.PlatformImpl!.Activated?.Invoke();
                Dispatcher.UIThread.RunJobs();
                Control footer = Find<Control>(viewer, "GalleryOverlayFooter");
                Assert.True(footer.IsVisible);
                test(viewer, host, footer);
            }
            finally { host.Close(); }
            return true;
        }
    });

    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        Assert.Single(viewer.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Rect(control.Bounds.Size).Center, window)!.Value;

    private static void Tick(GalleryViewer viewer, bool age = false)
    {
        if (age) Field("_lastOverlayActivity").SetValue(viewer, Stopwatch.GetTimestamp() - 60 * Stopwatch.Frequency);
        Invoke(viewer, "OnViewerInteractionTick");
    }

    private static FieldInfo Field(string name) => typeof(GalleryViewer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Invoke(GalleryViewer viewer, string name) =>
        typeof(GalleryViewer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, null);
}
