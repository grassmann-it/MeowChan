using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Views.Shortcuts;
using Semi.Avalonia.DataGrid;
using static MeowChan.DesktopTests.DownloadWindowFixture;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class DownloadHeaderToggleTests
{
    [Fact]
    public void SearchClickShortcutAndEscapeFoldOnlyTheEditor() => RunWithGrid(async fixture =>
    {
        MainWindow window = fixture.Window;
        fixture.ShowThreads([Thread(1), Thread(2)]);
        fixture.Select(1);
        var button = fixture.Find<ToggleButton>("DownloadSearchButton");
        var field = fixture.Find<TextBox>("DownloadSearchInput");
        var details = fixture.Find<ToggleButton>("DetailsToggleButton");
        Assert.False(button.Focusable);

        Click(fixture, button);
        Assert.True(field.IsVisible && field.IsKeyboardFocusWithin && button.IsChecked == true);
        Click(fixture, button);
        Assert.True(!field.IsVisible && button.IsChecked == false && fixture.Threads.IsKeyboardFocusWithin);

        ShortcutBindingsSection original = ShortcutService.CurrentSection;
        try
        {
            KeyGesture primary = CommandBindings.Gesture("input.focus-download-search")!;
            Assert.Equal(Key.F, primary.Key);
            Press(fixture, window, primary);
            Assert.True(field.IsVisible && field.IsKeyboardFocusWithin);
            Press(fixture, window, primary);
            Assert.False(field.IsVisible);

            ShortcutService.Publish(original.Set("input.focus-download-search", [ShortcutGesture.Parse("F8")]));
            KeyGesture remapped = CommandBindings.Gesture("input.focus-download-search")!;
            Assert.Equal(Key.F8, remapped.Key);
            Press(fixture, window, remapped);
            Assert.True(field.IsVisible && field.IsKeyboardFocusWithin);
            Press(fixture, window, remapped);
            Assert.False(field.IsVisible);
        }
        finally { ShortcutService.Publish(original); }

        if (details.IsChecked != true) Click(fixture, details);
        Click(fixture, button);
        field.Text = "Thread 1";
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (window.VisibleThreads.Count != 1) await Task.Delay(10, deadline.Token);
        Press(fixture, window, new KeyGesture(Key.Escape));
        Assert.True(!field.IsVisible && button.IsChecked == false && details.IsChecked == true);
        Assert.Equal("Thread 1", field.Text);
        Assert.Equal(1, window.VisibleResultCount);
        Assert.Equal(1L, Assert.Single(fixture.Threads.SelectedItems.OfType<ThreadRow>()).WatchId);
        Assert.True(fixture.Threads.IsKeyboardFocusWithin);
        Assert.True(fixture.Find<Button>("DownloadSearchClear").IsVisible);
    });

    [Fact]
    public void TheInspectionPanelFollowsBothButtonAndEffectiveShortcutAtBothWidths() => RunWithGrid(fixture =>
    {
        fixture.ShowThreads([Thread(1)]);
        fixture.Select(1);
        var button = fixture.Find<ToggleButton>("DetailsToggleButton");
        var panel = fixture.Find<Border>("WorkspaceInspector");
        Assert.False(button.Focusable);
        foreach (double width in new[] { 1240d, 840d })
        {
            fixture.Window.Width = width;
            fixture.Layout();
            if (panel.IsVisible) Click(fixture, button);
            Click(fixture, button);
            Assert.True(panel.IsVisible && button.IsChecked == true);
            Click(fixture, button);
            Assert.True(!panel.IsVisible && button.IsChecked == false);

            Assert.True(fixture.Threads.Focus());
            KeyGesture details = CommandBindings.Gesture("thread.details")!;
            Press(fixture, fixture.Window, details);
            Assert.True(panel.IsVisible);
            Press(fixture, fixture.Window, details);
            Assert.False(panel.IsVisible);
            Press(fixture, fixture.Window, details);
            Assert.True(panel.IsVisible);
            Press(fixture, fixture.Window, new KeyGesture(Key.Escape));
            Assert.True(!panel.IsVisible && fixture.Threads.IsKeyboardFocusWithin);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void ColumnsClosesOnSecondPointerClickAndUsesTheCurrentTabWhenReopened() => RunWithGrid(fixture =>
    {
        var columns = fixture.Find<ToggleButton>("DownloadsColumnsButton");
        Assert.False(columns.Focusable);
        Click(fixture, columns);
        var threadsMenu = Assert.IsType<ContextMenu>(columns.ContextMenu);
        Assert.True(threadsMenu.IsOpen && columns.IsChecked == true);
        Assert.Contains(threadsMenu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "ThreadsDataGrid.ThreadNumber"));
        Click(fixture, columns);
        Assert.True(!threadsMenu.IsOpen && columns.IsChecked == false);
        Assert.Null(columns.ContextMenu);

        fixture.Find<TabControl>("DownloadsTabs").SelectedIndex = 1;
        fixture.Layout();
        Click(fixture, columns);
        var boardsMenu = Assert.IsType<ContextMenu>(columns.ContextMenu);
        Assert.True(boardsMenu.IsOpen);
        Assert.Contains(boardsMenu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "BoardsDataGrid.NiceName"));
        Press(fixture, TopLevel.GetTopLevel(boardsMenu)!, new KeyGesture(Key.Escape));
        Assert.True(!boardsMenu.IsOpen && columns.IsChecked == false);
        Assert.Null(columns.ContextMenu);

        ShortcutBindingsSection original = ShortcutService.CurrentSection;
        try
        {
            ShortcutService.Publish(original.Set("table.choose-columns", [ShortcutGesture.Parse("F8")]));
            Assert.True(fixture.Find<DataGrid>("BoardsDataGrid").Focus());
            var gesture = new KeyGesture(Key.F8);
            Press(fixture, fixture.Window, gesture);
            var shortcutMenu = Assert.IsType<ContextMenu>(columns.ContextMenu);
            Assert.True(shortcutMenu.IsOpen);
            Press(fixture, TopLevel.GetTopLevel(shortcutMenu)!, gesture);
            Assert.True(!shortcutMenu.IsOpen && columns.IsChecked == false);
        }
        finally { ShortcutService.Publish(original); }
        return Task.CompletedTask;
    });

    [Fact]
    public void TheReadingHistoryArrowContextShortcutAndEscapeKeepTheSameOpenState() => RunWithGrid(async fixture =>
    {
        MainWindow window = fixture.Window;
        window.SessionHost.Config.RememberReadingPosition = true;
        await window.SessionHost.RememberReadingPositionAsync(
            new LocalReadingPosition("offline", "a", "1", "10", 0, "10", "A remembered thread"),
            window.SessionHost.ReadingPositionGeneration);
        await window.RefreshReadingHistorySnapshotAsync();
        fixture.Layout();

        var arrow = fixture.Find<ToggleButton>("ContinueReadingHistoryButton");
        var book = fixture.Find<Button>("ContinueReadingButton");
        var history = window.ReadingHistoryList!;
        Assert.False(arrow.Focusable);
        Click(fixture, arrow);
        Assert.True(history.IsOpen && arrow.IsChecked == true);
        Click(fixture, arrow);
        Assert.True(!history.IsOpen && arrow.IsChecked == false);

        Assert.True(book.Focus());
        var context = new KeyGesture(Key.F10, KeyModifiers.Shift);
        Press(fixture, window, context);
        Assert.True(history.IsOpen && arrow.IsChecked == true);
        Press(fixture, TopLevel.GetTopLevel(history.Panel)!, context);
        Assert.True(!history.IsOpen && arrow.IsChecked == false);
        Assert.True(book.Focus());
        Press(fixture, window, context);
        Press(fixture, TopLevel.GetTopLevel(history.Panel)!, new KeyGesture(Key.Escape));
        Assert.True(!history.IsOpen && arrow.IsChecked == false);
    });

    [Fact]
    public void TheShortcutHelpMenuAndKeyboardControlTheSameWindowAndCheckmark() => RunWithGrid(fixture =>
    {
        MainWindow window = fixture.Window;
        var item = fixture.Find<MenuItem>("ShortcutHelpMenuItem");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        fixture.Layout();
        var help = Assert.IsType<ShortcutsWindow>(ShortcutsWindow.For(window));
        Assert.True(help.IsVisible && item.IsChecked);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(help.IsVisible || item.IsChecked);

        KeyGesture shortcut = CommandBindings.Gesture("viewer.shortcut-help")!;
        Press(fixture, window, shortcut);
        help = Assert.IsType<ShortcutsWindow>(ShortcutsWindow.For(window));
        Assert.True(help.IsVisible && item.IsChecked);
        Press(fixture, help, shortcut);
        Assert.False(help.IsVisible || item.IsChecked);
        Press(fixture, window, shortcut);
        help = Assert.IsType<ShortcutsWindow>(ShortcutsWindow.For(window));
        Press(fixture, help, new KeyGesture(Key.Escape));
        Assert.False(help.IsVisible || item.IsChecked);
        return Task.CompletedTask;
    });

    [Fact]
    public void DownloadsSearchUsesCommandFOnMac() =>
        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Meta),
            Assert.Single(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS).For("input.focus-download-search"))
                .Resolve(ShortcutPlatform.MacOS));

    private static void RunWithGrid(Func<DownloadWindowFixture, Task> exercise) =>
        Run(exercise, () => Application.Current!.Styles.Insert(1, new DataGridSemiTheme()));

    private static void Click(DownloadWindowFixture fixture, Control control)
    {
        Assert.True(control.IsEffectivelyVisible);
        Point point = control.TranslatePoint(new Rect(control.Bounds.Size).Center, fixture.Window)!.Value;
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        fixture.Layout();
    }

    private static void Press(DownloadWindowFixture fixture, TopLevel target, KeyGesture gesture)
    {
        var modifiers = (RawInputModifiers)gesture.KeyModifiers;
        target.KeyPress(gesture.Key, modifiers, PhysicalKey.None, null);
        target.KeyRelease(gesture.Key, modifiers, PhysicalKey.None, null);
        fixture.Layout();
    }
}
