using Avalonia;
using Avalonia.Automation;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArchiveAndReaderActionsShareTheFlatStyleAcrossWorkViews(bool tiles) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ArchivePage page = ReaderMediaSidebarLayoutTests.Page();
        Window window = TestWindow(page);
        try
        {
            page.ArchiveSplitPreference = 840;
            window.Show();
            var tabs = ByAutomationId<TabControl>(page, "ArchiveContentTabs");
            tabs.IsVisible = true;
            Settle(window);
            tabs.SelectedItem = ByAutomationId<TabItem>(page, "ArchiveMediaTab");
            if (!tiles) page.MediaTilesPreference = false;
            Assert.Equal(tiles, page.MediaTilesPreference);

            var find = ByAutomationId<ToggleButton>(page, "ReaderFindOpen");
            var tags = ByAutomationId<ToggleButton>(page, "ReaderTagsOpen");
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
                Button[] mediaTools = AssertBarActions(page, "ReaderMediaTools", mayBeEmpty: view == WorkView.Barebones);
                // AP5 §1.5 (WV3) puts the favorite filter directly in Simple and Advanced.
                // F01 puts Columns on the table layout, and WV3 puts it on the bar only in Advanced.
                Assert.Equal(view != WorkView.Barebones,
                    mediaTools.Any(action => AutomationProperties.GetAutomationId(action) == "ArchiveFavoritesFilter"));
                Assert.Equal(!tiles && view == WorkView.Advanced,
                    mediaTools.Any(action => AutomationProperties.GetAutomationId(action) == "ArchiveMediaColumnsButton"));
                AssertBarActions(page, "ReaderMediaActions", mayBeEmpty: view == WorkView.Barebones);

                Assert.Equal(view == WorkView.Barebones, find.Classes.Contains(ActionToolBarPanel.ReducedPseudoClass));
                Assert.Equal(view == WorkView.Barebones, tags.Classes.Contains(ActionToolBarPanel.ReducedPseudoClass));
                if (view == WorkView.Barebones) continue;
                AssertGap(ByAutomationId<Button>(page, "ArchiveOpenGallery"), find, ActionToolBarPanel.ItemGap);
                AssertGap(find, tags, ActionToolBarPanel.GroupGap);
                AssertGap(tags, ByAutomationId<Button>(page, "ExportArchiveButton"), ActionToolBarPanel.ItemGap);
                AssertInlineToggle(find, ByAutomationId<StackPanel>(page, "ReaderFindBar"));
                AssertInlineToggle(tags, ByAutomationId<StackPanel>(page, "ReaderTagBar"));
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

    private static Button[] AssertBarActions(ArchivePage page, string name, bool mayBeEmpty = false)
    {
        var bar = ByAutomationId<ActionToolBar>(page, name);
        Assert.True(bar.IsEffectivelyVisible, name);
        Button[] actions = bar.Items.OfType<Button>().Where(button => button.IsEffectivelyVisible
            && !button.Classes.Contains(ActionToolBarPanel.ReducedPseudoClass)
            && button is not ArchiveThreadFavoriteButton
            && (button is not ToggleButton || AutomationProperties.GetAutomationId(button) is "ReaderFindOpen" or "ReaderTagsOpen" or "ArchiveFavoritesFilter")).ToArray();
        if (!mayBeEmpty) Assert.True(actions.Length > 0, $"No visible flat actions in {name} for {page.ReaderWorkView}.");
        foreach (Button action in actions)
            AssertFlatAction(action, ActionPadding);
        return actions;
    }

    private static T ByAutomationId<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

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
        // TOGGLE-2 review: “Den Öffner unfokussierbar lassen”; these openers have their own commands.
        string? id = AutomationProperties.GetAutomationId(action);
        if (id is "ReaderFindOpen" or "ReaderTagsOpen" or "ReaderThreadOpen" or "ReaderSettingsOpen"
            or "ArchiveThreadColumnsButton" or "ArchiveMediaColumnsButton")
        {
            Assert.False(action.Focusable);
            Assert.False(action.Focus(NavigationMethod.Tab), id);
        }
        else
        {
            Assert.True(action.Focus(NavigationMethod.Tab), action.Name);
            Assert.Contains(":focus-visible", action.Classes);
            Assert.Equal(Colors.Transparent, ColorOf(presenter.BorderBrush));
        }

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
        // Popup UX audit, 25 November: “Dezenter Strich … Keine dauernde Akzentfüllung”.
        ContentPresenter presenter = Presenter(action);
        Assert.Equal(Token("Workspace.Hover"), ColorOf(presenter.Background));
        Assert.Contains(presenter.GetVisualDescendants().OfType<TextBlock>(), HasUnderline);
        Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(action));
        inlineBar.IsVisible = false;
        Assert.False(action.IsChecked);
        Assert.Equal(Colors.Transparent, ColorOf(presenter.BorderBrush));
        Assert.DoesNotContain(presenter.GetVisualDescendants().OfType<TextBlock>(), HasUnderline);
        Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(action));

        static bool HasUnderline(TextBlock label) => label.TextDecorations?.Any(decoration => decoration.Location == TextDecorationLocation.Underline) == true;
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
