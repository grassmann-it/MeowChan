using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class SelectionContextGestureTests
{
    [Fact]
    public void EmptyTagListSpaceDoesNotReuseTheSelectedTag() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        string? copied = null;
        MediaTagSidebar sidebar = new() { CopyName = value => copied = value };
        sidebar.SetCounts(new MediaTagCountResult([], [new(MediaTagGroup.Source, "cat", "cat", 1)], 1, 1, true, true), false);
        Window window = new() { Width = 400, Height = 560, Content = sidebar };
        try
        {
            window.Show();
            ContextGestures.Settle(window);
            ListBoxItem tag = Assert.Single(sidebar.List.GetVisualDescendants().OfType<ListBoxItem>(),
                item => item.DataContext is MediaTagListItem { Kind: MediaTagListKind.Tag });
            ContextGestures.Click(tag);
            ContextGestures.RightClick(sidebar.List, new Point(24, sidebar.List.Bounds.Height - 12));
            Assert.Null(sidebar.List.ContextMenu);
            Assert.Null(copied);
            ContextGestures.MenuKey(tag);
            ContextMenu menu = Assert.IsType<ContextMenu>(sidebar.List.ContextMenu);
            Assert.True(menu.IsOpen);
            Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == "tags.copy-name").Activate();
            Assert.Equal("cat", copied);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTagListUsesTheOpeningGestureAfterAnEarlierRightClick(bool macControlClick) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        string? copied = null;
        MediaTagSidebar sidebar = new() { CopyName = value => copied = value };
        sidebar.SetCounts(new MediaTagCountResult([], [new(MediaTagGroup.Source, "cat", "cat", 1),
            new(MediaTagGroup.Source, "sky", "sky", 1)], 2, 2, true, true), false);
        Window window = new() { Width = 400, Height = 560, Content = sidebar };
        ContextClick.MacOSForTests = macControlClick;
        try
        {
            window.Show();
            ContextGestures.Settle(window);
            ListBoxItem[] rows = sidebar.List.GetVisualDescendants().OfType<ListBoxItem>()
                .Where(item => item.DataContext is MediaTagListItem { Kind: MediaTagListKind.Tag }).ToArray();
            Assert.Equal(2, rows.Length);
            ContextGestures.RightClick(rows[0]);
            Assert.IsType<ContextMenu>(sidebar.List.ContextMenu).Close();
            if (macControlClick) ContextGestures.ControlClick(rows[1]);
            else ContextGestures.MenuKey(rows[1]);
            ContextMenu menu = Assert.IsType<ContextMenu>(sidebar.List.ContextMenu);
            Assert.True(menu.IsOpen);
            Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == "tags.copy-name").Activate();
            Assert.Equal("sky", copied);
        }
        finally { ContextClick.MacOSForTests = null; window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TaggingEmptySpaceKeepsTheSelectionWithoutOpeningItsMenu(bool macControlClick) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1 }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            await using DesktopSessionHost session = new(profile.Path, []);
            await session.StartAsync();
            await session.EnqueueTagQueueAsync([Item(1), Item(2), Item(3)]);
            using TaggingPage page = new(session);
            ListBox list = page.ShownList;
            Window window = new() { Width = 1000, Height = 680, Content = page };
            ContextClick.MacOSForTests = macControlClick;
            try
            {
                window.Show();
                ContextGestures.Settle(window);
                ListBoxItem[] rows = list.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.DataContext is TaggingQueueRow).ToArray();
                Assert.Equal(3, rows.Length);
                list.Selection.Select(2);
                list.Selection.Select(0);
                list.Selection.Select(1);

                Point empty = new(24, list.Bounds.Height - 12);
                if (macControlClick) ContextGestures.ControlClick(list, empty); else ContextGestures.RightClick(list, empty);
                Assert.Null(page.OpenListMenu);
                Assert.Equal(3, list.Selection.Count);

                if (macControlClick) ContextGestures.ControlClick(rows[1]); else ContextGestures.RightClick(rows[1]);
                ContextMenu menu = Assert.IsType<ContextMenu>(page.OpenListMenu);
                Assert.Equal(3, list.Selection.Count);
                Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == "media.copy-file-name").Activate();
                Assert.Equal(string.Join(Environment.NewLine, new[] { "file-1.png", "file-2.png", "file-3.png" }),
                    BooruComponentDispatcher.Wait(window.Clipboard!.TryGetTextAsync()));
            }
            finally { ContextClick.MacOSForTests = null; window.Close(); }
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TaggingRemoveKeepsThePressedKeyThroughReleaseAndSelectionChanges(bool changeSelection) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1 }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            TagQueueItem first = Item(1), second = Item(2);
            await using DesktopSessionHost session = new(profile.Path, []);
            await session.StartAsync();
            await session.EnqueueTagQueueAsync([first, second]);
            using TaggingPage page = new(session);
            await page.ActivateAsync();
            ListBox list = page.ShownList;
            Window window = new() { Width = 1000, Height = 680, Content = page };
            try
            {
                window.Show();
                ContextGestures.Settle(window);
                ListBoxItem[] rows = list.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.DataContext is TaggingQueueRow).ToArray();
                Assert.Equal(2, rows.Length);
                ContextGestures.Press(rows[0], MouseButton.Right);
                ContextMenu opened = Assert.IsType<ContextMenu>(page.OpenListMenu);
                ContextGestures.Release(rows[changeSelection ? 0 : 1], MouseButton.Right);
                Assert.Same(opened, page.OpenListMenu);
                if (changeSelection) list.SelectedIndex = 1;
                Assert.Single(MenuProbe.Entries(opened), entry => entry.Id == TaggingCommands.Remove).Activate();
                await BooruTestSupport.WaitAsync(() => page.Queue.Count == 1, "the menu removal to commit");
                Assert.True(page.Queue.Contains(second.Key));
                Assert.False(page.Queue.Contains(first.Key));
            }
            finally { window.Close(); }
        }
    });

    private static TagQueueItem Item(int number) => new(new ResourceId("4chan", "g", "123"), "/media/123", "asset-" + number,
        number + new string('a', 63), 100 + number, "media-" + number, "original", "123", null, null, null, false, null,
        "file-" + number + ".png", SavedMediaKind.Image, TagQueueState.Open, DateTimeOffset.UnixEpoch.AddSeconds(number));
}
