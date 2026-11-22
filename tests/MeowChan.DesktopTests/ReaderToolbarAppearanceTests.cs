using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderToolbarAppearanceTests
{
    private static readonly Thickness ActionPadding = new(8, 4);

    [Fact]
    public void ArchiveAndReaderActionsShareTheFlatStyleAcrossWorkViews() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ArchivePage page = ReaderMediaSidebarLayoutTests.Page();
        Window window = TestWindow(page);
        try
        {
            page.ArchiveSplitPreference = 840;
            // The search field caches its vector geometry across test dispatchers. The toolbar can lay out without the field.
            page.FindControl<Grid>("ArchiveSearchRow")!.IsVisible = false;
            window.Show();
            var tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            tabs.IsVisible = true;
            tabs.SelectedItem = page.FindControl<TabItem>("ArchiveMediaTab");

            var find = page.FindControl<ToggleButton>("ReaderFindOpen")!;
            var tags = page.FindControl<ToggleButton>("ReaderTagsOpen")!;
            foreach (WorkView view in WorkViewSetting.All)
            {
                page.ArchiveColumnModePreference = view.ToSetting();
                // The page fixture has no saved thread. Enable the two controls whose open state is exercised below.
                find.IsEnabled = tags.IsEnabled = true;
                tags.Content = Texts.T("tags.openCount", 1);
                Settle(window);
                AssertBarActions(page, "ArchiveHeaderActions", mayBeEmpty: view == WorkView.Barebones);
                AssertBarActions(page, "ArchiveResultActions");
                AssertBarActions(page, "ReaderToolbar");
                AssertBarActions(page, "ReaderMediaTools", mayBeEmpty: view == WorkView.Barebones);
                AssertBarActions(page, "ReaderMediaActions", mayBeEmpty: view == WorkView.Barebones);

                Assert.Equal(view == WorkView.Barebones, find.Classes.Contains(ActionToolBarPanel.ReducedPseudoClass));
                Assert.Equal(view == WorkView.Barebones, tags.Classes.Contains(ActionToolBarPanel.ReducedPseudoClass));
                if (view == WorkView.Barebones) continue;
                AssertGap(page.FindControl<Button>("ArchiveOpenGallery")!, find, ActionToolBarPanel.ItemGap);
                AssertGap(find, tags, ActionToolBarPanel.GroupGap);
                AssertGap(tags, page.FindControl<Button>("ExportArchiveButton")!, ActionToolBarPanel.ItemGap);
                AssertInlineToggle(find, page.FindControl<StackPanel>("ReaderFindBar")!);
                AssertInlineToggle(tags, page.FindControl<StackPanel>("ReaderTagBar")!);
            }
        }
        finally
        {
            try { window.Close(); }
            finally { page.EndLifetime(); }
        }
    });

    [Fact]
    public void MediaLayoutMenuKeepsTheAppearanceOfItsNeighbourActionsWhenOpen() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaPage page = new(_ => throw new InvalidOperationException("This test does not read the media library."));
        Window window = TestWindow(page);
        try
        {
            window.Show();
            Settle(window);
            var groups = page.ToolbarRow.Children.OfType<StackPanel>().ToArray();
            var end = Assert.Single(groups, item => item.Name == "MediaToolbarEnd");
            Assert.Equal(ActionToolBarPanel.ItemGap, end.Spacing);
            var actions = groups.SelectMany(group => group.Children.OfType<Button>())
                .Where(button => button is not ToggleButton && button.IsEffectivelyVisible).ToArray();
            Assert.Contains(page.LayoutButton, actions);
            Assert.Contains(actions, button => button.Name == "MediaRefresh");
            foreach (Button action in actions)
                AssertFlatAction(action, action.Classes.Contains("icon-square") ? default : ActionPadding);

            page.LayoutFlyout.ShowAt(page.LayoutButton);
            Settle(window);
            Assert.True(page.LayoutFlyout.IsOpen);
            Assert.Equal(Colors.Transparent, ColorOf(Presenter(page.LayoutButton).Background));
            Assert.Equal(Colors.Transparent, ColorOf(Presenter(page.LayoutButton).BorderBrush));
            page.LayoutFlyout.Hide();
        }
        finally
        {
            try { window.Close(); }
            finally { page.EndLifetime(); }
        }
    });

    private static Window TestWindow(Control page) => new()
    {
        Content = page, Width = 1900, Height = 800, RequestedThemeVariant = ThemeVariant.Dark, Classes = { MotionPolicy.OffClass }
    };

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void AssertBarActions(ArchivePage page, string name, bool mayBeEmpty = false)
    {
        var bar = page.FindControl<ActionToolBar>(name)!;
        Assert.True(bar.IsEffectivelyVisible, name);
        Button[] actions = bar.Items.OfType<Button>().Where(button => button.IsEffectivelyVisible
            && !button.Classes.Contains(ActionToolBarPanel.ReducedPseudoClass)
            && button is not ArchiveThreadFavoriteButton
            && (button is not ToggleButton || button.Name is "ReaderFindOpen" or "ReaderTagsOpen")).ToArray();
        if (!mayBeEmpty) Assert.NotEmpty(actions);
        foreach (Button action in actions)
            AssertFlatAction(action, ActionPadding);
    }

    private static void AssertFlatAction(Button action, Thickness padding)
    {
        Assert.DoesNotContain("bar-button", action.Classes);
        if (action is not ToggleButton) Assert.DoesNotContain("toolbar-toggle", action.Classes);
        Assert.Equal(32, action.Bounds.Height);
        Assert.True(action.Padding == padding, $"{action.Name}: expected padding {padding}, actual {action.Padding}");
        ContentPresenter presenter = Presenter(action);
        Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
        Assert.Equal(Colors.Transparent, ColorOf(presenter.BorderBrush));
        Assert.NotNull(action.FocusAdorner);

        if (!action.IsEffectivelyEnabled) return;
        Assert.True(action.Focus(NavigationMethod.Tab), action.Name);
        Assert.Contains(":focus-visible", action.Classes);
        Assert.Equal(Colors.Transparent, ColorOf(presenter.BorderBrush));

        var state = (IPseudoClasses)action.Classes;
        state.Set(":pointerover", true);
        Assert.Equal(Token("Workspace.Hover"), ColorOf(presenter.Background));
        Assert.Equal(Token("Workspace.ControlBorder"), ColorOf(presenter.BorderBrush));
        state.Set(":pressed", true);
        Assert.Equal(Token("Workspace.AccentSurface"), ColorOf(presenter.Background));
        Assert.Equal(Token("Workspace.Accent"), ColorOf(presenter.BorderBrush));
        state.Set(":pressed", false);
        state.Set(":pointerover", false);
    }

    private static void AssertInlineToggle(ToggleButton action, StackPanel inlineBar)
    {
        Assert.Contains("toolbar-toggle", action.Classes);
        Assert.False(action.IsChecked);
        inlineBar.IsVisible = true;
        Assert.True(action.IsChecked);
        Assert.Equal(Token("Workspace.ToolbarToggleSurface"), ColorOf(Presenter(action).Background));
        Assert.Equal(Token("Workspace.Accent"), ColorOf(Presenter(action).BorderBrush));
        inlineBar.IsVisible = false;
        Assert.False(action.IsChecked);
        Assert.Equal(Colors.Transparent, ColorOf(Presenter(action).BorderBrush));
    }

    private static void AssertGap(Button left, Button right, double expected)
    {
        Assert.IsType<ActionToolBarPanel>(left.GetVisualParent());
        Assert.Same(left.GetVisualParent(), right.GetVisualParent());
        Assert.Equal(expected, right.Bounds.Left - left.Bounds.Right, precision: 1);
    }

    private static ContentPresenter Presenter(Button button) => button.GetVisualDescendants().OfType<ContentPresenter>()
        .First(presenter => presenter.Name == "PART_ContentPresenter");

    private static Color Token(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Dark, out object? resource), key);
        return ColorOf(Assert.IsAssignableFrom<IBrush>(resource));
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
