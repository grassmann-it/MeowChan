using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class ArchiveReaderPopupToggleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindAndTagsCanBeClosedIndependentlyInBothReaderHosts(bool large) => WithArchive(async fixture =>
    {
        ArchivePage page = await OpenReader(fixture, large);
        MainWindow window = fixture.Window;
        var find = Find<ToggleButton>(window, "ReaderFindOpen");
        var tags = Find<ToggleButton>(window, "ReaderTagsOpen");
        var posts = Find<ListBox>(window, "ArchivePosts");
        Assert.False(find.Focusable);
        Assert.False(tags.Focusable);

        Click(fixture, find);
        var query = Find<SearchBox>(window, "ReaderFindQuery");
        query.Text = "fixture";
        Click(fixture, tags);
        Assert.True(tags.IsChecked);
        Assert.True(find.IsChecked);
        Click(fixture, tags);
        Assert.False(tags.IsChecked);
        Assert.True(find.IsChecked);
        Click(fixture, find);
        Assert.False(find.IsChecked);
        Assert.True(posts.IsKeyboardFocusWithin);

        KeyGesture findKey = CommandBindings.Gesture("reader.find")!;
        Press(fixture, findKey);
        Assert.True(query.IsKeyboardFocusWithin);
        Press(fixture, findKey);
        Assert.False(find.IsChecked);
        Assert.True(posts.IsKeyboardFocusWithin);
        Press(fixture, findKey);
        Press(fixture, new KeyGesture(Key.Escape));
        Assert.False(find.IsChecked);
        Assert.Equal("fixture", query.Text);
        Assert.Equal(large, page.IsLargeReader);

        KeyGesture tagsKey = CommandBindings.Gesture("thread.tags")!;
        Press(fixture, tagsKey);
        Assert.True(tags.IsChecked);
        Press(fixture, tagsKey);
        Assert.False(tags.IsChecked);
        Assert.True(posts.IsKeyboardFocusWithin);
    });

    [Fact]
    public void ArchiveFilterOpenerAndShortcutCloseTheSurfaceAndFocusTheResults() => WithArchive(fixture =>
    {
        MainWindow window = fixture.Window;
        window.ArchivePage.ArchiveLayoutPreference = "List";
        fixture.Layout();
        var opener = Find<ToggleButton>(window, "ArchiveFilters");
        var results = Find<ListBox>(window, "ArchiveThreads");
        Rect bounds = new(results.TranslatePoint(default, window)!.Value, results.Bounds.Size);
        Assert.False(opener.Focusable);
        Click(fixture, opener);
        Assert.True(opener.IsChecked);
        Assert.Equal(bounds, new Rect(results.TranslatePoint(default, window)!.Value, results.Bounds.Size));
        Click(fixture, opener);
        Assert.False(opener.IsChecked);
        Assert.Equal(bounds, new Rect(results.TranslatePoint(default, window)!.Value, results.Bounds.Size));
        Assert.True(results.IsKeyboardFocusWithin, FocusState(window, results));

        KeyGesture gesture = CommandBindings.Gesture("archive.filters-toggle")!;
        Press(fixture, gesture);
        Assert.True(opener.IsChecked);
        Press(fixture, gesture);
        Assert.False(opener.IsChecked);
        Press(fixture, gesture);
        Press(fixture, new KeyGesture(Key.Escape));
        Assert.False(opener.IsChecked);
        Assert.True(results.IsKeyboardFocusWithin);
        return Task.CompletedTask;
    });

    [Fact]
    public void ScopeChipClosesOnSecondClickShortcutAndEscape() => WithArchive(fixture =>
    {
        MainWindow window = fixture.Window;
        var chip = Find<SearchScopeChip>(window, "ArchiveSearchScope");
        var field = Find<SearchBox>(window, "ArchiveSearchText");
        VerifyClickAndEscape(fixture, chip, () => chip.OpenMenu?.IsOpen == true);
        Assert.True(field.Focus());
        foreach (KeyGesture gesture in new[] { new KeyGesture(Key.F4), new KeyGesture(Key.Down, KeyModifiers.Alt) })
        {
            Press(fixture, gesture);
            Assert.True(chip.OpenMenu?.IsOpen);
            Press(fixture, gesture);
            Assert.Null(chip.OpenMenu);
            Assert.True(field.IsKeyboardFocusWithin);
        }
        Assert.Equal(SearchScope.Text, chip.Scope);
        return Task.CompletedTask;
    });

    [Fact]
    public void ReaderThreadMenuAndSettingsCloseOnSecondClickAndEscape() => WithArchive(async fixture =>
    {
        ArchivePage page = await OpenReader(fixture, large: true);
        MainWindow window = fixture.Window;
        var thread = Find<Button>(window, "ReaderThreadOpen");
        var settings = Find<Button>(window, "ReaderSettingsOpen");
        VerifyClickAndEscape(fixture, thread, () => thread.ContextMenu?.IsOpen == true);
        Assert.True(page.IsLargeReader, "Escape in the thread menu closed the reader.");
        VerifyClickAndEscape(fixture, settings, () => page.IsQuickSettingsOpen);
        Assert.True(page.IsLargeReader, "Escape in quick settings closed the reader.");

        var posts = Find<ListBox>(window, "ArchivePosts");
        Assert.True(posts.IsKeyboardFocusWithin || posts.Focus(NavigationMethod.Tab), FocusState(window, posts));
        KeyGesture settingsKey = Assert.IsType<KeyGesture>(CommandBindings.Gesture("reader.quick-settings"));
        Press(fixture, settingsKey);
        Assert.True(page.IsQuickSettingsOpen);
        Press(fixture, settingsKey);
        Assert.False(page.IsQuickSettingsOpen);
        Assert.True(posts.IsKeyboardFocusWithin);
    });

    [Fact]
    public void ReaderMenusUseTheStableMoreAnchorInTheReducedToolbar() => WithArchive(async fixture =>
    {
        ArchivePage page = await OpenReader(fixture, large: true);
        page.ArchiveColumnModePreference = WorkViewSetting.BarebonesName;
        fixture.Window.Width = 920;
        fixture.Layout();
        var toolbar = Find<ActionToolBar>(fixture.Window, "ReaderToolbar");
        var more = Find<ToggleButton>(fixture.Window, "ReaderToolbarMore");

        Click(fixture, more);
        Click(fixture, Find<Button>(TopLevel.GetTopLevel(toolbar.OverflowHost!)!, "ReaderThreadOpen"));
        Assert.True(more.ContextMenu?.IsOpen);
        Click(fixture, more);
        Assert.Null(more.ContextMenu);
        Assert.False(more.IsChecked);

        Click(fixture, more);
        Click(fixture, Find<Button>(TopLevel.GetTopLevel(toolbar.OverflowHost!)!, "ReaderSettingsOpen"));
        Assert.True(page.IsQuickSettingsOpen);
        Assert.Same(more, page.ReaderQuickSettings!.Flyout.Target);
        Click(fixture, more);
        Assert.False(page.IsQuickSettingsOpen);
        Assert.False(more.IsChecked);
    });

    [Fact]
    public void ArchiveColumnsComposeFreshMenuAfterClickShortcutAndEscape() => WithArchive(fixture =>
    {
        MainWindow window = fixture.Window;
        window.ArchivePage.ArchiveLayoutPreference = "Table";
        window.ArchivePage.ArchiveSplitPreference = Config.ArchiveSplitWidthMaximum;
        fixture.Layout();
        var toolbar = Find<ActionToolBar>(window, "ArchiveResultActions");
        var columns = Assert.Single(toolbar.Items.OfType<Button>(),
            button => AutomationProperties.GetAutomationId(button) == "ArchiveThreadColumnsButton");
        Assert.False(columns.Focusable);
        if (TopLevel.GetTopLevel(columns) == window)
            VerifyClickAndEscape(fixture, columns, () => columns.ContextMenu?.IsOpen == true);
        else
        {
            var more = Find<ToggleButton>(window, "ArchiveResultActionsMore");
            Click(fixture, more);
            Click(fixture, columns);
            Assert.True(columns.ContextMenu?.IsOpen);
            Click(fixture, more);
            Assert.Null(columns.ContextMenu);
            Assert.False(more.IsChecked);
            Click(fixture, more);
            Click(fixture, columns);
            Assert.True(columns.ContextMenu?.IsOpen);
            Press(fixture, new KeyGesture(Key.Escape));
            Assert.Null(columns.ContextMenu);
            Assert.False(more.IsChecked);
        }
        var table = Find<DataGrid>(window, "ArchiveThreadTable");
        Assert.True(table.IsKeyboardFocusWithin || table.Focus(NavigationMethod.Tab), FocusState(window, table));
        KeyGesture gesture = Assert.IsType<KeyGesture>(CommandBindings.Gesture("table.choose-columns"));
        Press(fixture, gesture);
        ContextMenu first = Assert.IsType<ContextMenu>(columns.ContextMenu);
        Press(fixture, gesture);
        Assert.Null(columns.ContextMenu);
        Press(fixture, gesture);
        Assert.IsType<ContextMenu>(columns.ContextMenu);
        Assert.NotSame(first, columns.ContextMenu);
        Press(fixture, new KeyGesture(Key.Escape));
        Assert.Null(columns.ContextMenu);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ThreadMenuShortcutTogglesFromPostsWithOverflowClosed(bool large, bool rebind) => WithArchive(async fixture =>
    {
        ArchivePage page = await OpenReader(fixture, large);
        var posts = Find<ListBox>(fixture.Window, "ArchivePosts");
        var toolbar = Find<ActionToolBar>(fixture.Window, "ReaderToolbar");
        var opener = Assert.Single(toolbar.Items.OfType<Button>(),
            b => AutomationProperties.GetAutomationId(b) == "ReaderThreadOpen");
        Assert.False(opener.Focusable);
        page.ArchiveColumnModePreference = WorkViewSetting.BarebonesName;
        fixture.Layout();
        var more = Find<ToggleButton>(fixture.Window, "ReaderToolbarMore");
        Assert.False(more.IsChecked);
        Assert.False(toolbar.IsVisualAncestorOf(opener));
        FocusPost(posts);
        var bindings = ShortcutService.CurrentSection.Set("reader.quick-settings", []);
        if (rebind) bindings = bindings.Set("reader.thread-menu", [ShortcutGesture.Parse("Primary+F9")]);
        ShortcutService.Publish(bindings);
        KeyGesture key = Assert.IsType<KeyGesture>(CommandBindings.Gesture("reader.thread-menu"));
        Press(fixture, key);
        Assert.True(more.ContextMenu?.IsOpen);
        Assert.False(more.IsChecked);
        Assert.True(more.ContextMenu!.IsKeyboardFocusWithin);
        Press(fixture, key);
        Assert.Null(more.ContextMenu);
        Assert.True(posts.IsKeyboardFocusWithin);
        Press(fixture, key);
        Press(fixture, new KeyGesture(Key.Escape));
        Assert.Null(more.ContextMenu);
        Assert.True(posts.IsKeyboardFocusWithin);
    });

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void ThreadMenuDefaultIsAvailableAndUniqueAcrossTheCatalog(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        var gesture = Assert.Single(bindings.For("reader.thread-menu"));
        KeyGesture key = gesture.Resolve(platform);
        Assert.Equal(Key.J, key.Key);
        Assert.Equal(KeyModifiers.Shift | (platform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control), key.KeyModifiers);
        Assert.Null(EffectiveShortcutBindings.InvalidReason(gesture, platform));
        Assert.DoesNotContain(CommandCatalog.All, command => command.Id != "reader.thread-menu"
            && bindings.For(command.Id).Any(other => other.Resolve(platform).Equals(key)));
    }

    [Fact]
    public void EmbeddedFindShortcutDuringPreeditLeavesTheReaderEditorFocused() => WithArchive(async fixture =>
    {
        await OpenReader(fixture, large: false);
        Click(fixture, Find<Button>(fixture.Window, "ReaderFindOpen"));
        var query = Find<SearchBox>(fixture.Window, "ReaderFindQuery");
        var archive = Find<SearchBox>(fixture.Window, "ArchiveSearchText");
        Assert.True(query.IsFocused);
        var presenter = Assert.Single(query.GetVisualDescendants().OfType<TextPresenter>());
        presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, "candidate");
        Press(fixture, Assert.IsType<KeyGesture>(CommandBindings.Gesture("reader.find")));
        Assert.True(query.IsFocused);
        Assert.False(archive.IsKeyboardFocusWithin);
        Assert.Equal("candidate", presenter.PreeditText);
        Assert.True(query.IsEffectivelyVisible);
    });

    [Fact]
    public void SharedColumnsAndReaderGestureFollowsTheFocusedView() => WithArchive(async fixture =>
    {
        ArchivePage page = await OpenReader(fixture, large: false);
        page.ArchiveLayoutPreference = "Table";
        ShortcutService.Publish(ShortcutService.CurrentSection.Set("table.choose-columns", [ShortcutGesture.Parse("Primary+Shift+J")]));
        fixture.Layout();
        var posts = Find<ListBox>(fixture.Window, "ArchivePosts");
        var table = Find<DataGrid>(fixture.Window, "ArchiveThreadTable");
        var toolbar = Find<ActionToolBar>(fixture.Window, "ArchiveResultActions");
        var columns = Assert.Single(toolbar.Items.OfType<Button>(), b => AutomationProperties.GetAutomationId(b) == "ArchiveThreadColumnsButton");
        Assert.True(table.IsEffectivelyVisible && posts.IsEffectivelyVisible);
        FocusPost(posts);
        var shared = new KeyGesture(Key.J, KeyModifiers.Shift | (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control));
        Press(fixture, shared);
        Assert.True(page.IsQuickSettingsOpen);
        Assert.Null(columns.ContextMenu);
        Press(fixture, new KeyGesture(Key.Escape));
        Assert.True(table.Focus());
        Press(fixture, shared);
        Assert.True(columns.ContextMenu?.IsOpen);
        Assert.False(page.IsQuickSettingsOpen);
    });

    [Fact]
    public void ColumnsShortcutFromPostsCannotOpenTheOtherPanesTable() => WithArchive(async fixture =>
    {
        ArchivePage page = await OpenReader(fixture, large: false);
        page.ArchiveLayoutPreference = "Table";
        fixture.Layout();
        var toolbar = Find<ActionToolBar>(fixture.Window, "ArchiveResultActions");
        var columns = Assert.Single(toolbar.Items.OfType<Button>(), b => AutomationProperties.GetAutomationId(b) == "ArchiveThreadColumnsButton");
        var posts = Find<ListBox>(fixture.Window, "ArchivePosts");
        FocusPost(posts);
        Press(fixture, Assert.IsType<KeyGesture>(CommandBindings.Gesture("table.choose-columns")));
        Assert.Null(columns.ContextMenu);
        Assert.True(posts.IsKeyboardFocusWithin);
    });

    private static void FocusPost(ListBox posts)
    {
        posts.SelectedItem = Assert.Single(posts.Items);
        var post = Assert.IsAssignableFrom<Control>(posts.ContainerFromItem(posts.SelectedItem));
        Assert.True(post.Focus());
        Assert.True(posts.IsKeyboardFocusWithin);
    }

    [Fact]
    public void HiddenTagsOfTheSelectedThreadAreUncheckedAndReopenItsReader() => WithArchive(async fixture =>
    {
        ArchivePage page = await OpenReader(fixture, large: false);
        page.ArchiveLayoutPreference = "Table";
        fixture.Window.Width = 1000;
        fixture.Layout();
        var bar = Find<StackPanel>(fixture.Window, "ReaderTagBar");
        var table = Find<DataGrid>(fixture.Window, "ArchiveThreadTable");
        var row = new ArchiveThreadRow(page.CurrentThread!.Summary);
        table.ItemsSource = new[] { row };
        await page.OpenThreadTagsAsync(row.Summary.Id);
        Assert.True(bar.IsEffectivelyVisible);
        Click(fixture, Find<Button>(fixture.Window, "ArchiveBackButton"));
        Assert.False(bar.IsEffectivelyVisible);
        table.SelectedItem = row;
        Assert.True(table.Focus());
        table.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        fixture.Layout();
        var menu = Assert.IsType<ContextMenu>(table.ContextMenu);
        Assert.True(menu.IsOpen);
        var tags = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "thread.tags"));
        Assert.False(tags.IsChecked);
        tags.Command!.Execute(null);
        menu.Close();
        fixture.Layout();
        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(row.Summary.Id, page.CurrentThread?.Summary.Id);
    });

    [Fact]
    public void FilterOffersOnlyItsHeaderCloseAndReturnsFocusToResults() => WithArchive(fixture =>
    {
        fixture.Window.ArchivePage.ArchiveLayoutPreference = "List";
        Click(fixture, Find<Button>(fixture.Window, "ArchiveFilters"));
        var surface = Find<Border>(fixture.Window, "ArchiveFilterSurface");
        var headerClose = Find<Button>(surface, "ArchiveFilterSurfaceClose");
        Assert.True(headerClose.IsEffectivelyVisible);
        Assert.DoesNotContain(surface.GetVisualDescendants().OfType<Button>(), b =>
            b.IsEffectivelyVisible && (Equals(b.Tag, "action.close") || AutomationProperties.GetName(b) == Texts.T("action.close"))
                && !ReferenceEquals(b, headerClose));
        Click(fixture, headerClose);
        Assert.False(surface.IsEffectivelyVisible);
        Assert.True(Find<ListBox>(fixture.Window, "ArchiveThreads").IsKeyboardFocusWithin);
        return Task.CompletedTask;
    });

    private static void WithArchive(Func<DownloadWindowFixture, Task> test) => DownloadWindowFixture.Run(async fixture =>
    {
        PopupEscape.Install();
        ShortcutBindingsSection before = ShortcutService.CurrentSection;
        var bindings = before
            .Set("navigation.archive", [ShortcutGesture.Parse("F8")])
            .Set("reader.quick-settings", [ShortcutGesture.Parse("Primary+Shift+J")])
            .Set("table.choose-columns", [ShortcutGesture.Parse("Primary+Shift+K")]);
        ShortcutService.Publish(bindings);
        try
        {
            fixture.Window.Width = 1960;
            fixture.Layout();
            Press(fixture, new KeyGesture(Key.F8));
            Assert.True(fixture.Window.ArchivePage.IsEffectivelyVisible);
            fixture.Window.ArchivePage.ArchiveColumnModePreference = WorkViewSetting.AdvancedName;
            fixture.Layout();
            await test(fixture);
        }
        finally { ShortcutService.Publish(before); }
    }, () => Application.Current!.Styles.Insert(1, new DataGridSemiTheme()));

    private static async Task<ArchivePage> OpenReader(DownloadWindowFixture fixture, bool large)
    {
        ArchivePage page = fixture.Window.ArchivePage;
        page.IsReadable = true;
        ResourceId id = new("fixture", "board", "100");
        ArchivedThreadView thread = new(
            new(id, "https://fixture.invalid/thread/100", "Fixture", "", ThreadSourceState.NotFound,
                true, DateTimeOffset.UnixEpoch, null, 1, 1),
            [new CapturedPost("100", 1, PostFieldPresence.All, Html: "fixture post")], decimalPostOrder: false);
        await page.LoadThreadAsync(id, preparedThread: thread);
        if (large) page.OpenReaderRequested?.Invoke(null);
        fixture.Layout();
        return page;
    }

    private static void VerifyClickAndEscape(DownloadWindowFixture fixture, Button opener, Func<bool> isOpen)
    {
        Assert.False(opener.Focusable);
        Click(fixture, opener);
        Assert.True(isOpen());
        Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
        Click(fixture, opener);
        Assert.False(isOpen());
        Click(fixture, opener);
        Assert.True(isOpen());
        Press(fixture, new KeyGesture(Key.Escape));
        Assert.False(isOpen());
        Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(opener));
    }

    private static T Find<T>(Visual root, string id) where T : Control => Assert.Single(
        root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static string FocusState(Window window, Control target) =>
        $"Focused {window.FocusManager?.GetFocusedElement()}; target {target.Name}, visible {target.IsEffectivelyVisible}, "
        + $"enabled {target.IsEffectivelyEnabled}, focusable {target.Focusable}, attached {target.IsAttachedToVisualTree()}";

    private static void Click(DownloadWindowFixture fixture, Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control)!;
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        fixture.Layout();
    }

    private static void Press(DownloadWindowFixture fixture, KeyGesture gesture)
    {
        MainWindow window = fixture.Window;
        TopLevel root = window.FocusManager?.GetFocusedElement() is Visual focused
            ? TopLevel.GetTopLevel(focused) ?? window : window;
        root.KeyPress(gesture.Key, (RawInputModifiers)gesture.KeyModifiers, PhysicalKey.None, null);
        TopLevel receiver = root.PlatformImpl != null ? root : window;
        receiver.KeyRelease(gesture.Key, (RawInputModifiers)gesture.KeyModifiers, PhysicalKey.None, null);
        fixture.Layout();
    }
}
