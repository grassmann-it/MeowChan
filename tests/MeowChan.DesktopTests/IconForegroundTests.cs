using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Theming;
using Xunit;

namespace MeowChan.DesktopTests;

// Tom, 26.11.2026: an icon on a coloured or accent surface and in a badge has the colour of the text beside it (it was the page's text colour:
// a black tag beside a white count); hearts are red and rating stars gold, each with a theme colour of its own.
[Collection("Localization")]
public sealed class IconForegroundTests
{
    [Fact]
    public void ACatalogIconTakesTheTextColourOfTheSurfaceThatHoldsItInEveryTheme() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Window window = new() { Width = 320, Height = 240, Classes = { MotionPolicy.OffClass } };
        try
        {
            // A badge on the accent: the surface gives its text colour to everything inside it.
            Border icon = Assert.IsType<Border>(CommandBindings.IconFromKey("tag", 16));
            TextBlock count = new() { Text = "4" };
            Border badge = new() { Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { icon, count } } };
            // A count with a colour of its own (muted): the icon beside it follows the count, not the page.
            TextBlock muted = new() { Text = "13", Classes = { "workspace-meta" } };
            Border mutedIcon = Assert.IsType<Border>(CommandBindings.FollowLabel(CommandBindings.IconFromKey("tag", 16), muted));
            window.Content = new StackPanel { Children = { badge, new StackPanel { Children = { mutedIcon, muted } } } };
            window.Show();

            var host = new ThemeRuntimeHost(window.Resources, ThemePaletteTests.Base(ThemeTokenTheme.Dark), ThemePaletteTests.Base(ThemeTokenTheme.Light));
            foreach (ThemeTokenTheme theme in Enum.GetValues<ThemeTokenTheme>())
            {
                window.RequestedThemeVariant = ThemeRuntime.Variant(theme);
                ThemePalette palette = ThemePalette.Build(ThemePaletteTests.Base(theme));
                host.Use(palette);
                badge.Background = new SolidColorBrush(palette.Colors["Workspace.Accent"]);
                TextElement.SetForeground(badge, new SolidColorBrush(palette.Colors["Workspace.OnAccent"]));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Color onAccent = palette.Colors["Workspace.OnAccent"];
                Assert.Equal(onAccent, ColorOf(count.Foreground));
                Assert.Equal(onAccent, ColorOf(icon.Background));
                Assert.True(ContrastRules.Contrast(ThemePalette.Hex(onAccent), ThemePalette.Hex(palette.Colors["Workspace.Accent"])) >= 4.5, theme + " badge");

                Assert.Equal(ColorOf(muted.Foreground), ColorOf(mutedIcon.Background));
                Assert.NotEqual(palette.Colors["Workspace.Text"], ColorOf(mutedIcon.Background));
                Assert.True(ContrastRules.Contrast(ThemePalette.Hex(ColorOf(mutedIcon.Background)), ThemePalette.Hex(palette.Colors["Workspace.Panel"])) >= 3, theme + " muted");
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ACheckedSidebarSwitchOutsideAToolbarShowsAWhiteIconOnTheToggleSurfaceInEveryTheme() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Window window = new() { Width = 320, Height = 240, Classes = { MotionPolicy.OffClass } };
        try
        {
            // The Booru page's sidebar switch: an icon-only toggle in a plain panel (no toolbar around it).
            ToggleButton toggle = new() { IsChecked = true, Focusable = false, Classes = { "toolbar-toggle" } };
            IconButtons.Apply(toggle, "panel-right", "Sidebar");
            window.Content = new StackPanel { Children = { toggle } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var host = new ThemeRuntimeHost(window.Resources, ThemePaletteTests.Base(ThemeTokenTheme.Dark), ThemePaletteTests.Base(ThemeTokenTheme.Light));
            foreach (ThemeTokenTheme theme in Enum.GetValues<ThemeTokenTheme>())
            {
                window.RequestedThemeVariant = ThemeRuntime.Variant(theme);
                ThemePalette palette = ThemePalette.Build(ThemePaletteTests.Base(theme));
                host.Use(palette);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Border icon = Assert.IsType<Border>(toggle.Content);
                ContentPresenter presenter = Assert.Single(toggle.GetVisualDescendants().OfType<ContentPresenter>());
                Color surface = ColorOf(presenter.Background);
                Assert.Equal(palette.Colors["Workspace.ToolbarToggleSurface"], surface);
                Assert.Equal(Colors.White, ColorOf(icon.Background));
                Assert.True(ContrastRules.Contrast("#FFFFFF", ThemePalette.Hex(surface)) >= 3, theme + " switch");
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheHeartIsRedAndTheRatingStarGoldEachWithAThemeColourOfItsOwn()
    {
        Assert.Equal("Workspace.Favorite", ThemeUserColors.Require(ThemeUserColors.Favorite).Seed);
        Assert.Equal("Workspace.Rating", ThemeUserColors.Require(ThemeUserColors.Rating).Seed);
        foreach (ThemeTokenTheme theme in Enum.GetValues<ThemeTokenTheme>())
        {
            ThemePalette palette = ThemePalette.Build(ThemePaletteTests.Base(theme));
            Color heart = palette.Colors["Workspace.Favorite"], star = palette.Colors["Workspace.Rating"];
            Assert.NotEqual(heart, star);
            // Red: the red channel leads clearly. Gold: red and green lead, blue is low.
            Assert.True(heart.R > heart.G + 80 && heart.R > heart.B + 80, theme + " heart is red: " + ThemePalette.Hex(heart));
            Assert.True(star.R > star.B + 80 && star.G > star.B + 60 && star.R >= star.G, theme + " star is gold: " + ThemePalette.Hex(star));
            foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.ChipSurface", "Workspace.AccentSurface" })
            {
                string hex = ThemePalette.Hex(palette.Colors[surface]);
                Assert.True(ContrastRules.Contrast(ThemePalette.Hex(heart), hex) >= 3, theme + " heart on " + surface);
                Assert.True(ContrastRules.Contrast(ThemePalette.Hex(star), hex) >= 3, theme + " star on " + surface);
            }
        }
    }

    [Fact]
    public void TheColourPreviewShowsTheHeartAndTheStarInTheirOwnColours() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ThemeColorPreviewCard card = new();
        foreach (ThemeTokenTheme theme in Enum.GetValues<ThemeTokenTheme>())
        {
            ThemePalette palette = ThemePalette.Build(ThemePaletteTests.Base(theme));
            card.Show(palette, theme.ToString());
            Assert.Equal(palette.Colors["Workspace.Favorite"], ColorOf(card.FavoriteMark.Fill));
            Assert.Equal(palette.Colors["Workspace.Rating"], ColorOf(card.RatingMark.Fill));
            Assert.Equal(WorkspaceIcons.HeartFilled, WorkspaceIcons.KeyOf(card.FavoriteMark));
            Assert.Equal(WorkspaceIcons.StarFilled, WorkspaceIcons.KeyOf(card.RatingMark));
        }
    });

    [Fact]
    public void OnATileBadgeTheHeartIsRedTheStarGoldAndEveryOtherIconWhiteLikeItsText()
    {
        Assert.Equal(VideoPlayMark.FavoriteColor, MediaBadgeLayout.IconColor(MediaBadgeId.Favorite));
        Assert.Equal(VideoPlayMark.RatingColor, MediaBadgeLayout.IconColor(MediaBadgeId.Rating));
        Assert.Equal(ThemePalette.Build(ThemePaletteTests.Base(ThemeTokenTheme.Dark)).Colors["Workspace.Favorite"], VideoPlayMark.FavoriteColor);
        Assert.Equal(ThemePalette.Build(ThemePaletteTests.Base(ThemeTokenTheme.Dark)).Colors["Workspace.Rating"], VideoPlayMark.RatingColor);
        foreach (MediaBadgeId id in new[] { MediaBadgeId.TagCount, MediaBadgeId.Queued, MediaBadgeId.Tagging, MediaBadgeId.Missing, MediaBadgeId.Booru })
            Assert.Equal(Colors.White, MediaBadgeLayout.IconColor(id));
        // The disc is dark in both themes; the worst picture under it is white.
        Color disc = VideoPlayMark.DiscColor;
        string overWhite = ContrastRules.CompositeOver($"#{disc.A:X2}{disc.R:X2}{disc.G:X2}{disc.B:X2}", "#FFFFFF");
        Assert.True(ContrastRules.Contrast(ThemePalette.Hex(VideoPlayMark.FavoriteColor), overWhite) >= 3);
        Assert.True(ContrastRules.Contrast(ThemePalette.Hex(VideoPlayMark.RatingColor), overWhite) >= 3);
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
