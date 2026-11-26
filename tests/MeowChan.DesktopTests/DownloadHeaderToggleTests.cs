using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Views.Shortcuts;
using Semi.Avalonia.DataGrid;
using Xunit.Abstractions;
using static MeowChan.DesktopTests.DownloadWindowFixture;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class DownloadHeaderToggleTests(ITestOutputHelper output)
{
    [Fact]
    public void SearchClickShortcutAndEscapeFoldOnlyTheEditor() => RunWithGrid(async fixture =>
    {
        MainWindow window = fixture.Window;
        fixture.ShowThreads([Thread(1), Thread(2)]);
        fixture.Select(1);
        var button = ById<ToggleButton>(window, "DownloadSearchButton");
        var field = ById<TextBox>(window, "DownloadSearchInput");
        var details = ById<ToggleButton>(window, "DetailsToggleButton");
        var disclosure = Assert.IsAssignableFrom<IExpandCollapseProvider>(ControlAutomationPeer.CreatePeerForElement(button));
        Assert.False(button.Focusable);

        Click(fixture, button);
        Assert.True(field.IsVisible && field.IsKeyboardFocusWithin && button.IsChecked == true);
        Assert.Equal(ExpandCollapseState.Expanded, disclosure.ExpandCollapseState);
        Click(fixture, button);
        Assert.True(!field.IsVisible && button.IsChecked == false && fixture.Threads.IsKeyboardFocusWithin);
        Assert.Equal(ExpandCollapseState.Collapsed, disclosure.ExpandCollapseState);
        disclosure.Expand();
        disclosure.Expand();
        Assert.True(field.IsVisible && field.IsKeyboardFocusWithin);
        disclosure.Collapse();
        Assert.True(!field.IsVisible && fixture.Threads.IsKeyboardFocusWithin);

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
    public void EscapeDismissesOnlyTheFocusedAdmissionSurfaceBeforeTheSearch() => RunWithGrid(async fixture =>
    {
        MainWindow window = fixture.Window;
        TextBox url = ById<TextBox>(window, "UrlInput");
        url.Text = "https://admission.invalid/a/thread/123";
        Click(fixture, ById<Button>(window, "AddUrlButton"));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!window.GetVisualDescendants().OfType<Button>().Any(button =>
            AutomationProperties.GetAutomationId(button) == "AdmissionResultsDismiss" && button.IsEffectivelyVisible))
            await Task.Delay(10, deadline.Token);

        Button repair = ById<Button>(window, "AdmissionRepairDismiss");
        Button report = ById<Button>(window, "AdmissionResultsDismiss");
        TextBox field = ById<TextBox>(window, "DownloadSearchInput");
        window.FocusDownloadSearch();
        fixture.Layout();
        Assert.True(field.IsKeyboardFocusWithin);
        Assert.True(repair.Focus());
        Press(fixture, window, new KeyGesture(Key.Escape));
        Assert.False(repair.IsEffectivelyVisible);
        Assert.True(report.IsEffectivelyVisible && field.IsEffectivelyVisible && url.IsKeyboardFocusWithin);

        Assert.True(report.Focus());
        Press(fixture, window, new KeyGesture(Key.Escape));
        Assert.False(report.IsEffectivelyVisible);
        Assert.True(field.IsEffectivelyVisible && url.IsKeyboardFocusWithin);
    });

    [Fact]
    public void DownloadsSearchLeavesItsTextAndFocusWithAnActiveComposition() => RunWithGrid(fixture =>
    {
        MainWindow window = fixture.Window;
        window.FocusDownloadSearch();
        fixture.Layout();
        TextBox field = ById<TextBox>(window, "DownloadSearchInput");
        field.Text = "fixture";
        TextPresenter presenter = Assert.Single(field.GetVisualDescendants().OfType<TextPresenter>());
        presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, "candidate");
        Press(fixture, window, CommandBindings.Gesture("input.focus-download-search")!);
        Press(fixture, window, new KeyGesture(Key.Escape));
        Assert.True(field.IsEffectivelyVisible && field.IsKeyboardFocusWithin);
        Assert.Equal("fixture", field.Text);
        presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, null);
        Press(fixture, window, new KeyGesture(Key.Escape));
        Assert.False(field.IsEffectivelyVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public void SettingsSectionShortcutAlsoClosesNavigationFromTheShellFooter() => RunWithGrid(fixture =>
    {
        MainWindow window = fixture.Window;
        window.Width = 740;
        fixture.Layout();
        Button footer = ById<Button>(window, "SettingsNavigationButton");
        Click(fixture, footer);
        Expander sections = ById<Expander>(window, "SettingsSections");
        Assert.True(sections.IsEffectivelyVisible);

        ShortcutBindingsSection before = ShortcutService.CurrentSection;
        ShortcutService.Publish(before.Set("sections.open-navigation", [ShortcutGesture.Parse("Primary+Shift+J")]));
        try
        {
            sections.IsExpanded = false;
            KeyGesture gesture = Assert.IsType<KeyGesture>(CommandBindings.Gesture("sections.open-navigation"));
            Assert.True(footer.Focus());
            Press(fixture, window, gesture);
            Assert.True(sections.IsExpanded);
            Assert.True(footer.Focus());
            Press(fixture, window, gesture);
            Assert.False(sections.IsExpanded);
        }
        finally { ShortcutService.Publish(before); }
        return Task.CompletedTask;
    });

    [Fact]
    public void TheInspectionPanelFollowsBothButtonAndEffectiveShortcutAtBothWidths() => RunWithGrid(fixture =>
    {
        fixture.ShowThreads([Thread(1)]);
        fixture.Select(1);
        var button = ById<ToggleButton>(fixture.Window, "DetailsToggleButton");
        var panel = fixture.Find<Border>("WorkspaceInspector");
        var disclosure = Assert.IsAssignableFrom<IExpandCollapseProvider>(ControlAutomationPeer.CreatePeerForElement(button));
        Assert.False(button.Focusable);
        foreach (double width in new[] { 1240d, 840d })
        {
            fixture.Window.Width = width;
            fixture.Layout();
            if (panel.IsVisible) Click(fixture, button);
            Click(fixture, button);
            Assert.True(panel.IsVisible && button.IsChecked == true);
            Assert.Equal(ExpandCollapseState.Expanded, disclosure.ExpandCollapseState);

            Point point = button.TranslatePoint(new Rect(button.Bounds.Size).Center, fixture.Window)!.Value;
            IInputElement? hit = fixture.Window.InputHitTest(point);
            bool hitsToggle = hit is Visual visual && (ReferenceEquals(button, visual) || button.IsVisualAncestorOf(visual));
            StyledElement? hitElement = hit as StyledElement;
            string hitMessage = $"width={width}, hit={hit?.GetType().Name ?? "<none>"}, " +
                $"AutomationId={(hitElement == null ? null : AutomationProperties.GetAutomationId(hitElement)) ?? "<none>"}, " +
                $"Name={hitElement?.Name ?? "<none>"}, toggle.Bounds={BoundsInWindow(button)}, inspector.Bounds={BoundsInWindow(panel)}";
            output.WriteLine(hitMessage);
            if (width == 1240)
            {
                Assert.True(hitsToggle, hitMessage);
                Click(fixture, button);
            }
            else
            {
                Assert.False(hitsToggle, hitMessage);
                Button close = Assert.Single(fixture.Window.GetVisualDescendants().OfType<Button>(), candidate =>
                    ControlAutomationPeer.CreatePeerForElement(candidate)?.GetAutomationId() == "InspectorCloseButton");
                Click(fixture, close);
            }
            Assert.True(!panel.IsVisible && button.IsChecked == false,
                $"width={width}, panel.IsVisible={panel.IsVisible}, button.IsChecked={button.IsChecked}");

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

        Rect BoundsInWindow(Control control) =>
            new(control.TranslatePoint(default, fixture.Window)!.Value, control.Bounds.Size);
    });

    [Fact]
    public void ColumnsClosesOnSecondPointerClickAndUsesTheCurrentTabWhenReopened() => RunWithGrid(fixture =>
    {
        fixture.ShowThreads([Thread(1)]);
        var columns = fixture.Find<ToggleButton>("DownloadsColumnsButton");
        Assert.False(columns.Focusable);
        Click(fixture, columns);
        var threadsMenu = Assert.IsType<ContextMenu>(columns.ContextMenu);
        Assert.True(threadsMenu.IsOpen && columns.IsChecked == true);
        Assert.Contains(threadsMenu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "ThreadsDataGrid.ThreadNumber"));
        Click(fixture, columns);
        Assert.True(!threadsMenu.IsOpen && columns.IsChecked == false,
            $"width={fixture.Window.Width}, threadsMenu.IsOpen={threadsMenu.IsOpen}, columns.IsChecked={columns.IsChecked}, " +
            $"columns.ContextMenu != null={columns.ContextMenu != null}");
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

    [Theory]
    [InlineData("Barebones")]
    [InlineData("Simple")]
    public void ColumnsShortcutOpensAtAVisibleAnchorWhileColumnsSitsUnderTheClosedMore(string mode) => RunWithGrid(fixture =>
    {
        fixture.ShowThreads([Thread(1), Thread(2)]);
        fixture.Select(1);
        var columns = fixture.Find<ToggleButton>("DownloadsColumnsButton");
        Assert.True(columns.IsVisible && !columns.IsAttachedToVisualTree(),
            $"{mode}: Columns is expected under the closed More (IsVisible={columns.IsVisible}, attached={columns.IsAttachedToVisualTree()}).");

        ShortcutBindingsSection original = ShortcutService.CurrentSection;
        try
        {
            ShortcutService.Publish(original.Set("table.choose-columns", [ShortcutGesture.Parse("F8")]));
            var gesture = new KeyGesture(Key.F8);
            foreach (KeyGesture closing in new[] { gesture, new KeyGesture(Key.Escape) })
            {
                Assert.True(fixture.Threads.Focus());
                Press(fixture, fixture.Window, gesture);
                var menu = Assert.IsType<ContextMenu>(columns.ContextMenu);
                Control? anchor = menu.PlacementTarget;
                Assert.True(menu.IsOpen && anchor is { IsEffectivelyVisible: true } && anchor.IsAttachedToVisualTree()
                    && anchor.Bounds.Width > 0 && anchor.Bounds.Height > 0,
                    $"{mode}: menu.IsOpen={menu.IsOpen}, anchor={Describe(anchor)}, attached={anchor?.IsAttachedToVisualTree()}, " +
                    $"visible={anchor?.IsEffectivelyVisible}, bounds={anchor?.Bounds}");
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "archive.columnsReset"));
                Assert.True(menu.IsKeyboardFocusWithin, $"{mode}: the opened menu takes the keyboard, focus is on {Focused()}.");

                Press(fixture, TopLevel.GetTopLevel(menu)!, closing);
                Assert.True(!menu.IsOpen && columns.IsChecked == false && columns.ContextMenu == null,
                    $"{mode}, {closing}: menu.IsOpen={menu.IsOpen}, columns.IsChecked={columns.IsChecked}");
                Assert.True(fixture.Threads.IsKeyboardFocusWithin, $"{mode}, {closing}: focus returns to the table, it is on {Focused()}.");
            }
        }
        finally { ShortcutService.Publish(original); }
        return Task.CompletedTask;

        string Focused() => Describe(fixture.Window.FocusManager?.GetFocusedElement() as Control);
        static string Describe(Control? control) => control == null ? "<none>"
            : $"{control.GetType().Name}#{AutomationProperties.GetAutomationId(control) ?? control.Name ?? "<unnamed>"}";
    }, mode);

    [Theory]
    [InlineData("Barebones")]
    [InlineData("Simple")]
    public void ColumnsShortcutWorksFromTheOpenDownloadsOverflow(string mode) => RunWithGrid(fixture =>
    {
        fixture.ShowThreads([Thread(1)]);
        var toolbar = ById<ActionToolBar>(fixture.Window, "WorkspaceListActions");
        var more = Assert.IsAssignableFrom<ToggleButton>(toolbar.More);
        Click(fixture, more);
        Assert.True(more.IsChecked);
        var overflow = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(toolbar.OverflowHost));
        var columns = ById<ToggleButton>(overflow, "DownloadsColumnsButton");
        Assert.False(columns.Focusable);

        ShortcutBindingsSection original = ShortcutService.CurrentSection;
        try
        {
            ShortcutService.Publish(original.Set("table.choose-columns", [ShortcutGesture.Parse("F8")]));
            var gesture = new KeyGesture(Key.F8);
            Press(fixture, overflow, gesture);
            var menu = Assert.IsType<ContextMenu>(columns.ContextMenu);
            Assert.True(menu.IsOpen, "The shortcut opens the columns menu.");
            Assert.False(more.IsChecked);
            // The shortcut uses the visible More anchor while Columns stays out of the focus order.
            Assert.Same(more, menu.PlacementTarget);
            Assert.True(menu.IsKeyboardFocusWithin,
                $"The columns menu receives focus. Focused: {fixture.Window.FocusManager?.GetFocusedElement()}");
            Press(fixture, TopLevel.GetTopLevel(menu)!, gesture);
            Assert.False(menu.IsOpen);
        }
        finally { ShortcutService.Publish(original); }
        return Task.CompletedTask;
    }, mode);

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

    private static void RunWithGrid(Func<DownloadWindowFixture, Task> exercise, string workspaceMode = "Advanced") =>
        Run(exercise, () => Application.Current!.Styles.Insert(1, new DataGridSemiTheme()), workspaceMode);

    private static T ById<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

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
        TopLevel receiver = target.PlatformImpl != null ? target : fixture.Window;
        receiver.KeyRelease(gesture.Key, modifiers, PhysicalKey.None, null);
        fixture.Layout();
    }
}
