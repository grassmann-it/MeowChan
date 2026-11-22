using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Theming;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using Xunit;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaToolbarLabelsTests
{
    [Fact]
    public void LabelsDependOnTheRowWidthOnlyAndTheFilterTextLeavesBeforeTheSidebarWordsAppear()
    {
        Assert.True(MediaToolbarLabels.FilterLabelWidth < MediaToolbarLabels.SidebarLabelsWidth);
        Assert.True(MediaToolbarLabels.FilterLabelWidth > MediaToolbarLayout.ReducedWidth);
        Assert.True(MediaToolbarLabels.ShowsFilterLabel(0));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(0));
        Assert.False(MediaToolbarLabels.ShowsFilterLabel(MediaToolbarLabels.FilterLabelWidth - 1));
        Assert.True(MediaToolbarLabels.ShowsFilterLabel(MediaToolbarLabels.FilterLabelWidth));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(MediaToolbarLabels.SidebarLabelsWidth - 1));
        Assert.True(MediaToolbarLabels.ShowsSidebarLabels(MediaToolbarLabels.SidebarLabelsWidth));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(double.NaN));
    }

    [Fact]
    public void TheDisplayedSidebarIconsKeepTheirParentsAndBothLabelsFollowResize() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaPage page = new(_ => throw new InvalidOperationException("This test does not read the media library."));
        Window window = new() { Width = 950, Height = 700, Content = page };
        // The toolbar's layout is independent of transitions and their process-wide animation clock.
        window.Classes.Add(MotionPolicy.OffClass);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tags = Assert.IsType<MediaToolbarLabels.SidebarContent>(page.BooruModeButton.Content);
            var media = Assert.IsType<MediaToolbarLabels.SidebarContent>(page.PanelToggleButton.Content);
            Control tagsIcon = Assert.IsAssignableFrom<Control>(tags.Children.OfType<ContentControl>().Single().Content);
            Control mediaIcon = Assert.IsType<Border>(media.Children.OfType<ContentControl>().Single().Content);
            var tagsLabel = Assert.Single(tags.Children.OfType<TextBlock>());
            var mediaLabel = Assert.Single(media.Children.OfType<TextBlock>());
            var tagsParent = tagsIcon.GetVisualParent();
            var mediaParent = mediaIcon.GetVisualParent();
            Assert.NotNull(tagsParent);
            Assert.NotNull(mediaParent);

            var group = Assert.IsType<SidebarToggleGroup>(page.BooruModeButton.Parent);
            Assert.Same(group, page.PanelToggleButton.Parent);
            foreach ((double width, bool showsLabels) in new[] { (950d, false), (1800d, true), (640d, false), (1800d, true) })
            {
                BooruReviewTestPlatform.Resize(window, width);
                Assert.Equal(showsLabels, page.ToolbarRow.Bounds.Width >= MediaToolbarLabels.SidebarLabelsWidth);
                Assert.True(page.BooruModeButton.IsEffectivelyVisible);
                Assert.True(page.PanelToggleButton.IsEffectivelyVisible);
                Assert.InRange(group.TranslatePoint(new Point(group.Bounds.Width, 0), page.ToolbarRow)!.Value.X, 0, page.ToolbarRow.Bounds.Width);
                Assert.Equal(showsLabels, tagsLabel.IsVisible);
                Assert.Equal(showsLabels, mediaLabel.IsVisible);
                Assert.Same(tagsParent, tagsIcon.GetVisualParent());
                Assert.Same(mediaParent, mediaIcon.GetVisualParent());
            }
        }
        finally
        {
            try { window.Close(); }
            finally { page.EndLifetime(); }
        }
    });

    [Fact]
    public void SidebarKeyboardKeepsBothSwitchesIndependentAndTheFocusRingVisible() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        using var lifetime = new WindowWithToolbars();
        SidebarToggleGroup group = new();
        ToggleButton first = new(), second = new();
        group.Children.Add(first);
        group.Children.Add(second);
        group.Describe(first, "Threads", "Threads");
        group.Describe(second, "Media sidebar", "Media sidebar");
        lifetime.Window.Content = group;
        lifetime.Show();

        Assert.True(first.Focus(NavigationMethod.Tab));
        Press(first, Key.Space);
        Assert.True(first.IsChecked);
        Assert.False(second.IsChecked);
        Press(first, Key.Right);
        Assert.Same(second, lifetime.Window.FocusManager!.GetFocusedElement());
        Assert.Contains(":focus-visible", second.Classes);
        Assert.NotNull(second.FocusAdorner);
        Press(second, Key.Enter);
        Assert.True(first.IsChecked);
        Assert.True(second.IsChecked);
        string state = Texts.T("window.fullscreen.on");
        Assert.Equal("Media sidebar: " + state, AutomationProperties.GetName(second));
        Assert.EndsWith(state, Assert.IsType<string>(ToolTip.GetTip(second)));
        Press(second, Key.Left);
        Assert.True(first.IsFocused);
        Press(first, Key.Enter);
        Assert.False(first.IsChecked);
        Assert.True(second.IsChecked);

        static void Press(Control control, Key key)
        {
            control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
        }
    });

    [Fact]
    public void CheckedToolbarBackgroundAndForegroundRenderWithEnoughContrastInEveryTheme() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using var lifetime = new WindowWithToolbars();
        SidebarToggleGroup group = new();
        Border icon = Assert.IsType<Border>(MeowChan.Desktop.Commands.CommandBindings.NamedIcon("panel-right"));
        TextBlock label = new() { Text = "Media" };
        ToggleButton segment = new() { IsChecked = true, Content = new StackPanel { Children = { icon, label } } };
        group.Children.Add(segment);
        lifetime.Window.Content = group;
        lifetime.Show();

        var host = new ThemeRuntimeHost(lifetime.Window.Resources, ThemePaletteTests.Base(ThemeTokenTheme.Dark), ThemePaletteTests.Base(ThemeTokenTheme.Light));
        foreach (ThemeTokenTheme theme in Enum.GetValues<ThemeTokenTheme>())
        {
            lifetime.Window.RequestedThemeVariant = ThemeRuntime.Variant(theme);
            foreach (ThemeAccentPreset preset in ThemeAccentPresets.All)
            {
                var overrides = new ThemeConfiguratorModel(ThemeColorOverrides.Empty, ThemePaletteTests.Base);
                overrides.ApplyPreset(preset);
                ThemePalette palette = ThemeCustomization.Preview(ThemePaletteTests.Base(theme), overrides.Draft.ColorsFor(theme)).Palette;
                host.Use(palette);
                Dispatcher.UIThread.RunJobs();
                lifetime.Window.UpdateLayout();
                ContentPresenter presenter = Assert.Single(segment.GetVisualDescendants().OfType<ContentPresenter>());
                Color surface = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
                Assert.Equal(palette.Colors["Workspace.ToolbarToggleSurface"], surface);
                Assert.Equal(palette.Colors["Workspace.Accent"], Assert.IsAssignableFrom<ISolidColorBrush>(presenter.BorderBrush).Color);
                Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);
                Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(icon.Background).Color);
                Assert.True(ContrastRules.Contrast("#FFFFFF", ThemePalette.Hex(surface)) >= 4.5);
            }
        }
    });

    private sealed class WindowWithToolbars : IDisposable
    {
        internal Window Window { get; } = new() { Width = 640, Height = 480, Classes = { MotionPolicy.OffClass } };

        internal void Show()
        {
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => Window.Close();
    }

    [Fact]
    public void TheSidebarWordsAreShortAndLocalizedInEnglishAndGerman()
    {
        Texts.Initialize();
        try
        {
            Texts.SetLanguage("en");
            Assert.Equal("Tags", Texts.T("media.toolbar.tagsLabel"));
            Assert.Equal("Media", Texts.T("media.toolbar.mediaLabel"));
            Texts.SetLanguage("de");
            Assert.Equal("Tags", Texts.T("media.toolbar.tagsLabel"));
            Assert.Equal("Medien", Texts.T("media.toolbar.mediaLabel"));
        }
        finally { Texts.SetLanguage("en"); }
    }
}
