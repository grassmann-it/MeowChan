using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.MediaActions;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;
using Semi.Avalonia.DataGrid;

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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TaggingEmptySpaceKeepsTheSelectionWithoutOpeningItsMenu(bool macControlClick, bool useGrid) => BooruComponentDispatcher.Run(() =>
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
            await page.ActivateAsync();
            if (useGrid) page.SetViewMode(TaggingViewMode.Grid, save: false);
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
                ContextGestures.Settle(window);

                Point empty = new(24, list.Bounds.Height - 12);
                if (macControlClick) ContextGestures.ControlClick(list, empty); else ContextGestures.RightClick(list, empty);
                Assert.Null(page.OpenListMenu);
                Assert.Equal(3, list.Selection.Count);

                if (macControlClick) ContextGestures.ControlClick(rows[1]); else ContextGestures.RightClick(rows[1]);
                ContextMenu menu = Assert.IsType<ContextMenu>(page.OpenListMenu);
                Assert.True(menu.IsOpen);
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
    public void MediaRightPressAndReleaseKeepOneMenuForTheShownMedium(bool usePanel) => RateAndTagWindowFixture.RunMediaPage(async (window, page) =>
    {
        ListBoxItem tile = Assert.IsType<ListBoxItem>(page.Grid.ContainerFromIndex(0));
        MediaLibraryTileRow row = Assert.IsType<MediaLibraryTileRow>(tile.DataContext);
        Control target = tile;
        if (usePanel)
        {
            page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
            await BooruTestSupport.WaitAsync(() => ReferenceEquals(page.PanelRow, row), "the panel to show the selected medium");
            target = Assert.IsType<ReaderMediaSidebar>(page.Panel).Title;
        }

        ContextGestures.Press(target, MouseButton.Right);
        ContextMenu menu = Assert.IsType<ContextMenu>(page.MediaMenu);
        MediaMenuSession session = Assert.IsType<MediaMenuSession>(page.MediaMenuSession);
        Assert.True(menu.IsOpen);
        Assert.Equal(MediaMenuTarget.Of(row.Entry), session.Target);
        Assert.Contains(MenuProbe.Entries(menu), entry => entry.Id == "media.open-external");

        ContextGestures.Release(target, MouseButton.Right);
        Assert.Same(menu, page.MediaMenu);
        Assert.Same(session, page.MediaMenuSession);
        Assert.True(menu.IsOpen);
        menu.Close();
        ContextGestures.Settle(window);
    });

    [Fact]
    public void ReaderTagChipOpensItsOwnMenuAndRemovesOnlyThatTag() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruComponentDispatcher.Wait(ExerciseAsync());

        static async Task ExerciseAsync()
        {
            using TempDirectory profile = new();
            using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false);
            ArchivedThreadView thread = RateAndTagWindowFixture.View(media);
            new Config { DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1 }.SaveConfig(profile.Path);
            await RateAndTagWindowFixture.SeedAsync(profile.Path, thread);
            await using DesktopSessionHost host = new(profile.Path, []);
            await host.StartAsync();
            TagId keep = (await host.DefinePrivateTagAsync("keep")).Tag;
            TagId remove = (await host.DefinePrivateTagAsync("remove")).Tag;
            await host.SetThreadTagAsync(keep, thread.Summary.Id, true);
            await host.SetThreadTagAsync(remove, thread.Summary.Id, true);

            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            page.AttachPrivateTags(host);
            Window window = new() { Width = 1500, Height = 860, Content = page };
            try
            {
                window.Show();
                await page.ActivateAsync();
                await page.OpenThreadTagsAsync(thread.Summary.Id);
                WrapPanel chips = Assert.IsType<WrapPanel>(page.FindControl<WrapPanel>("ReaderTagChips"));
                Button chip = Assert.Single(chips.Children.OfType<Button>(),
                    button => Equals(ToolTip.GetTip(button), MeowChan.Desktop.Localization.Texts.T("tags.remove", "remove")));
                ContextGestures.RightClick(chip);
                ContextMenu menu = Assert.IsType<ContextMenu>(chip.ContextMenu);
                Assert.True(menu.IsOpen);
                IReadOnlyList<MenuProbe.Entry> entries = MenuProbe.Entries(menu);
                Assert.Equal(new[] { "reader.tag.filter", "reader.tag.copy-name", "reader.tag.manage", "reader.tag.remove" },
                    entries.Select(entry => entry.Id));
                Assert.True(entries[^1].Section > entries[^2].Section);
                entries[^1].Activate();
                await page.PrivateTagTask;
                PrivateTagStoreSnapshot tags = Assert.IsType<PrivateTagStoreSnapshot>(host.GetPublishedPrivateTagSnapshot().Snapshot);
                Assert.Equal(keep, Assert.Single(tags.TagsOf(thread.Summary.Id)).Id);
            }
            finally { page.EndLifetime(); window.Close(); }
        }
    });

    [Fact]
    public void MediaMenuChecksThatTheResolvedFileStillExists() => RateAndTagWindowFixture.RunMediaPage(async (window, page) =>
    {
        ListBoxItem tile = Assert.IsType<ListBoxItem>(page.Grid.ContainerFromIndex(0));
        MediaLibraryTileRow row = Assert.IsType<MediaLibraryTileRow>(tile.DataContext);
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(row.Entry.Directory!);
        MediaAssetReference asset = Assert.Single(manifest.GetPage(), candidate => candidate.AssetId == row.Entry.AssetId);
        List<string> opened = [];
        window.OpenFileForTests = path => { opened.Add(path); return Task.CompletedTask; };

        ContextMenu OpenMenu()
        {
            ContextGestures.MenuKey(tile);
            ContextMenu menu = Assert.IsType<ContextMenu>(page.MediaMenu);
            Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, menu.Placement);
            Assert.Same(menu, tile.ContextMenu);
            return menu;
        }
        MenuItem Original(ContextMenu menu) => Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "media.open-external"));

        ContextMenu ready = OpenMenu();
        await BooruTestSupport.WaitAsync(() => Original(ready).IsEnabled, "the original to be checked");
        Assert.Single(MenuProbe.Entries(ready), item => item.Id == "media.open-external").Activate();
        await BooruTestSupport.WaitAsync(() => opened.Count == 1, "the exact original to open");
        Assert.Equal(asset.Path, Assert.Single(opened));

        ready = OpenMenu();
        await BooruTestSupport.WaitAsync(() => Original(ready).IsEnabled, "the original to be rechecked");
        File.Delete(asset.Path);
        Assert.Single(MenuProbe.Entries(ready), item => item.Id == "media.open-external").Activate();
        await BooruTestSupport.WaitAsync(() => !ready.IsOpen, "the attempted original to close its menu");
        ContextMenu missing = OpenMenu();
        await BooruTestSupport.WaitAsync(() => !Original(missing).IsEnabled
            && (Avalonia.Automation.AutomationProperties.GetHelpText(Original(missing)) ?? string.Empty)
                .Contains(MediaMenuReasons.NotSaved, StringComparison.Ordinal),
            "the missing-file explanation");
        Assert.Equal(asset.Path, Assert.Single(opened));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchiveEmptySpaceKeepsTheSelectionAndShownThread(bool macControlClick) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        using var media = BooruComponentDispatcher.Wait(ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false));
        ArchivedThreadView first = RateAndTagWindowFixture.View(media);
        ArchivedThreadView second = new(first.Summary with { Id = new ResourceId("4chan", "g", "142"),
            Url = "https://offline.invalid/thread/142", Title = "Second thread", MediaDirectory = Path.Combine(profile.Path, "second-media"),
            PostCount = 1 }, [new CapturedPost("142", 0, PostFieldPresence.Html, Html: "Second thread")], [], decimalPostOrder: true);
        BooruComponentDispatcher.Wait(RateAndTagWindowFixture.SeedAsync(profile.Path, first));
        BooruComponentDispatcher.Wait(RateAndTagWindowFixture.SeedAsync(profile.Path, second));
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
        Window window = new() { Width = 1500, Height = 860, Content = page };
        ContextClick.MacOSForTests = macControlClick;
        try
        {
            window.Show();
            BooruComponentDispatcher.Wait(page.ActivateAsync());
            ListBox list = page.FindControl<ListBox>("ArchiveThreads")!;
            ContextGestures.Settle(window);
            Assert.Equal(2, list.ItemCount);
            list.Selection.SelectRange(0, 1);
            BooruComponentDispatcher.Wait(page.LoadThreadAsync(first.Summary.Id));
            ContextGestures.Settle(window);
            Assert.Equal(2, list.Selection.Count);
            ArchivedThreadView shown = Assert.IsType<ArchivedThreadView>(page.CurrentThread);
            Assert.Equal(first.Summary.Id, shown.Summary.Id);
            Point empty = new(24, list.Bounds.Height - 12);
            if (macControlClick) ContextGestures.ControlClick(list, empty); else ContextGestures.RightClick(list, empty);
            Assert.Equal(2, list.Selection.Count);
            Assert.Same(shown, page.CurrentThread);
            Assert.False(list.ContextMenu?.IsOpen == true);
        }
        finally { ContextClick.MacOSForTests = null; page.EndLifetime(); window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownloadsEmptySpaceKeepsTheSelectedWatches(bool macControlClick) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Application.Current!.Styles.Add(new DataGridSemiTheme());
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            using TempDirectory profile = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "downloads"), FirstRunSetupVersion = 1,
                DownloadsPaused = true, WorkspaceMode = "Advanced", Language = "en"
            }.SaveConfig(profile.Path);
            using HttpClient client = new(new RejectDownloadRequests());
            await using DesktopSessionHost session = new(profile.Path, [new FourChanImageBoard(client)],
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(session) { Width = 1440, Height = 900 };
            ContextClick.MacOSForTests = macControlClick;
            try
            {
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await window.StartupTimingRestoreTask;
                Assert.Equal(AdmissionStatus.Added, (await session.AddAsync("https://boards.4chan.org/g/thread/123")).Status);
                Assert.Equal(AdmissionStatus.Added, (await session.AddAsync("https://boards.4chan.org/g/thread/124")).Status);
                window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(MainWindow.WorkspacePage.Downloads, window.SelectedPage);
                DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
                await BooruTestSupport.WaitAsync(() =>
                {
                    window.UpdateLayout();
                    return grid.GetVisualDescendants().OfType<DataGridRow>().Count(row => row.DataContext is ThreadRow) == 2;
                }, "the two Downloads watches");
                ThreadRow[] watches = window.VisibleThreads.ToArray();
                Assert.Equal(2, watches.Length);
                foreach (ThreadRow watch in watches) grid.SelectedItems.Add(watch);
                ContextGestures.Settle(window);
                Point empty = new(24, grid.Bounds.Height - 12);
                if (macControlClick) ContextGestures.ControlClick(grid, empty); else ContextGestures.RightClick(grid, empty);
                Assert.Equal(watches.Select(row => row.WatchId), grid.SelectedItems.Cast<ThreadRow>().Select(row => row.WatchId));
                Assert.False(grid.ContextMenu?.IsOpen == true);

                DataGridColumnHeader header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                    .First(item => item.IsEffectivelyVisible && item.Bounds.Width > 4);
                ContextGestures.RightClick(header);
                Assert.Equal(2, grid.SelectedItems.Count);
                Assert.True(header.ContextMenu?.IsOpen);
                Assert.False(grid.ContextMenu?.IsOpen == true);
            }
            finally { ContextClick.MacOSForTests = null; }
        }
    });

    private sealed class RejectDownloadRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Paused Downloads watches must not make network requests.");
    }

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
                MenuProbe.Entry remove = Assert.Single(MenuProbe.Entries(opened), entry => entry.Id == TaggingCommands.Remove);
                Assert.True(remove.Enabled, remove.DisabledReason);
                ContextGestures.KeyStroke(remove.Item, Key.Enter, PhysicalKey.Enter);
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
