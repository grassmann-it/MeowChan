using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        var opener = Find<ToggleButton>(window, "ArchiveFilters");
        var results = Find<ListBox>(window, "ArchiveThreads");
        Assert.False(opener.Focusable);
        Click(fixture, opener);
        Assert.True(opener.IsChecked);
        Click(fixture, opener);
        Assert.False(opener.IsChecked);
        Assert.True(results.IsKeyboardFocusWithin);

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
        VerifyClickAndEscape(fixture, settings, () => page.IsQuickSettingsOpen);

        var posts = Find<ListBox>(window, "ArchivePosts");
        Assert.True(posts.Focus());
        KeyGesture settingsKey = CommandBindings.Gesture("reader.quick-settings")!;
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
        ToggleButton more = Assert.IsType<ToggleButton>(toolbar.More);

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
        fixture.Layout();
        var columns = Find<Button>(window, "ArchiveThreadColumnsButton");
        VerifyClickAndEscape(fixture, columns, () => columns.ContextMenu?.IsOpen == true);
        Assert.True(Find<DataGrid>(window, "ArchiveThreadTable").Focus());
        KeyGesture gesture = CommandBindings.Gesture("table.choose-columns")!;
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

    private static void WithArchive(Func<DownloadWindowFixture, Task> test) => DownloadWindowFixture.Run(async fixture =>
    {
        PopupEscape.Install();
        ShortcutBindingsSection before = ShortcutService.CurrentSection;
        ShortcutService.Publish(before
            .Set("navigation.archive", [ShortcutGesture.Parse("F8")])
            .Set("reader.quick-settings", [ShortcutGesture.Parse("Primary+Alt+J")])
            .Set("table.choose-columns", [ShortcutGesture.Parse("Primary+Alt+K")]));
        try
        {
            fixture.Window.Width = 1960;
            fixture.Layout();
            Press(fixture, new KeyGesture(Key.F8));
            Assert.True(fixture.Window.ArchivePage.IsEffectivelyVisible);
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
        root.KeyRelease(gesture.Key, (RawInputModifiers)gesture.KeyModifiers, PhysicalKey.None, null);
        fixture.Layout();
    }
}
