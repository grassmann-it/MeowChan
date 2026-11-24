using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class SegmentPressedStyleTests
{
    [Theory, InlineData(false), InlineData(true)]
    public void PressingAndCancellingKeepTheConfirmedSelection(bool dark) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ThemeVariant theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Button primary = new() { Content = "Apply", Classes = { "primary" } };
        ThreeWayToggle segments = new();
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 8, Children = { primary, segments } };
        Window window = new() { Width = 320, Height = 200, Content = content, RequestedThemeVariant = theme, Classes = { MotionPolicy.OffClass } };
        int primaryClicks = 0;
        int selections = 0;
        primary.Click += (_, _) => primaryClicks++;
        segments.SelectionChanged += (_, _) => selections++;
        Point outside = new(4, 4);

        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            ContentPresenter primaryPresenter = Presenter(primary);
            Color rest = ColorOf(primaryPresenter.Background);
            Point primaryPoint = Center(primary, window);
            window.MouseMove(primaryPoint);
            Color hover = ColorOf(primaryPresenter.Background);
            window.MouseDown(primaryPoint, MouseButton.Left);
            Color pressed = ColorOf(primaryPresenter.Background);
            Assert.NotEqual(rest, pressed);
            Assert.NotEqual(hover, pressed);
            Assert.Equal(Token("ButtonSolidPrimaryPressedBackground", theme), pressed);
            window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            window.MouseUp(outside, MouseButton.Left);
            Assert.False(primary.IsPressed);
            Assert.Equal(rest, ColorOf(primaryPresenter.Background));
            Assert.Equal(0, primaryClicks);

            ToggleButton candidate = segments.Second;
            ContentPresenter presenter = Presenter(candidate);
            Color uncheckedEdge = Token("Workspace.ControlBorder", theme);
            Color accent = Token("Workspace.Accent", theme);
            Color pressedFill = Token("Workspace.SegmentPressed", theme);
            Color uncheckedFill = ColorOf(presenter.Background);
            Point candidatePoint = Center(candidate, window);
            window.MouseMove(candidatePoint);
            Assert.NotEqual(pressedFill, ColorOf(presenter.Background));
            window.MouseDown(candidatePoint, MouseButton.Left);
            Assert.True(candidate.IsPressed);
            Assert.Equal(pressedFill, ColorOf(presenter.Background));
            Assert.Equal(uncheckedEdge, ColorOf(presenter.BorderBrush));
            Assert.Equal(accent, ColorOf(Presenter(segments.First).BorderBrush));
            Assert.Equal(0, segments.SelectedIndex);
            window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            window.MouseUp(outside, MouseButton.Left);
            Assert.False(candidate.IsPressed);
            Assert.Equal(uncheckedFill, ColorOf(presenter.Background));
            Assert.Equal(0, selections);

            Assert.True(segments.First.Focus(NavigationMethod.Tab));
            Assert.True(candidate.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.True(candidate.IsPressed);
            Assert.Equal(pressedFill, ColorOf(presenter.Background));
            Assert.Equal(uncheckedEdge, ColorOf(presenter.BorderBrush));
            Assert.False(candidate.IsChecked);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.False(candidate.IsPressed);
            Assert.True(candidate.IsChecked);
            Assert.Equal(1, segments.SelectedIndex);
            Assert.Equal(1, selections);
            Dispatcher.UIThread.RunJobs();
            Color checkedFill = Token("Workspace.AccentSurface", theme);
            Assert.Equal(checkedFill, ColorOf(presenter.Background));
            Assert.Equal(accent, ColorOf(presenter.BorderBrush));
            Border ring = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                border => ReferenceEquals(border.GetValue(AdornerLayer.AdornedElementProperty), candidate));
            Assert.Equal(accent, ColorOf(ring.BorderBrush));

            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.Equal(pressedFill, ColorOf(presenter.Background));
            Assert.Equal(accent, ColorOf(presenter.BorderBrush));
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.Equal(1, selections);
            Assert.Equal(checkedFill, ColorOf(presenter.Background));

            candidate.IsEnabled = false;
            window.MouseMove(candidatePoint);
            window.MouseDown(candidatePoint, MouseButton.Left);
            window.MouseUp(candidatePoint, MouseButton.Left);
            Assert.False(candidate.IsPressed);
            Assert.Equal(checkedFill, ColorOf(presenter.Background));
            Assert.Equal(accent, ColorOf(presenter.BorderBrush));
            Assert.Equal(0.75, candidate.Opacity);
            Assert.Equal(1, selections);
        }
        finally { window.Close(); }
    });

    [Theory, InlineData(1), InlineData(1.5)]
    public void TheThreeWayToggleSharesOneBorderAtBothSeams(double scale) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ThreeWayToggle segments = new() { Margin = new Thickness(16) };
        Window window = new() { Width = 320, Height = 140, Content = segments, RequestedThemeVariant = ThemeVariant.Dark };
        try
        {
            window.Show();
            window.SetRenderScaling(scale);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(scale, window.RenderScaling);

            AssertSeam(segments.First, segments.Second);
            AssertSeam(segments.Second, segments.Third);

            void AssertSeam(ToggleButton left, ToggleButton right)
            {
                ContentPresenter a = Presenter(left);
                ContentPresenter b = Presenter(right);
                double leftEnd = a.TranslatePoint(new Point(a.Bounds.Width, 0), window)!.Value.X;
                double rightStart = b.TranslatePoint(default, window)!.Value.X;
                double expected = LayoutHelper.RoundLayoutThickness(a.BorderThickness, scale).Right;
                Assert.Equal(expected, leftEnd - rightStart, precision: 6);
                Assert.Equal(expected, LayoutHelper.RoundLayoutThickness(b.BorderThickness, scale).Left, precision: 6);
            }
        }
        finally { window.Close(); }
    });

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static ContentPresenter Presenter(Control control) => control.GetVisualDescendants().OfType<ContentPresenter>()
        .First(presenter => presenter.Name == "PART_ContentPresenter");

    private static Color Token(string key, ThemeVariant theme)
    {
        Assert.True(Application.Current!.TryGetResource(key, theme, out object? resource), key);
        return ColorOf(Assert.IsAssignableFrom<IBrush>(resource));
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
