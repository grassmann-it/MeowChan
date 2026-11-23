using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruResultActionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGridHasOnePreviewPerHitAndRestoresTheScrolledHitAfterAnAction(bool separate) => Run(() =>
    {
        using Fixture fixture = new(separate, perPage: 10);
        fixture.Search("cat");
        ScrollViewer scroll = Find<ScrollViewer>(fixture.View, "BooruResultScroll");
        Border[] tiles = fixture.View.TilePanel!.Children.Cast<Border>().ToArray();
        Assert.Equal(10, tiles.Length);
        foreach (Border tile in tiles)
        {
            Assert.Equal(tile.Name, AutomationProperties.GetName(tile));
            Assert.Equal(tile.Name, AutomationProperties.GetAutomationId(tile));
            Assert.Single(tile.GetSelfAndVisualDescendants().OfType<Border>(), IsHit);
            Assert.Single(tile.GetSelfAndLogicalDescendants().OfType<Border>(), IsHit);
            Border frame = Assert.Single(tile.GetVisualDescendants().OfType<Border>(),
                border => border.Name?.StartsWith("BooruPreviewFrame:", StringComparison.Ordinal) == true);
            Assert.Single(tile.GetVisualDescendants().OfType<Image>(), image => image.Source is Bitmap);
            Assert.DoesNotContain(tile.GetLogicalDescendants().OfType<Button>(),
                button => button.Name?.StartsWith(BooruWorkbenchView.OpenPostPrefix, StringComparison.Ordinal) == true);
            Rect tileBounds = Bounds(tile, scroll);
            Assert.True(tileBounds.Contains(Bounds(frame, scroll)));
            Assert.InRange(frame.Bounds.Width, BooruTileMetrics.PictureSize(MediaTileSize.Large) - 1, BooruTileMetrics.PictureSize(MediaTileSize.Large) + 1);
        }

        Assert.True(tiles[0].Focus());
        Key(fixture.Window, PhysicalKey.End);
        fixture.Settle();
        Assert.True(scroll.Offset.Y > 0);
        Border last = fixture.Hit("110");
        Assert.Same(last, fixture.Window.FocusManager?.GetFocusedElement());
        Rect visible = Bounds(last, scroll);
        Assert.InRange(visible.Center.Y, 0, scroll.Viewport.Height);

        ContextMenu menu = OpenContext(fixture.Window, last);
        Click(Item(menu, BooruWorkbenchView.MenuLoad));
        fixture.Until(() => !ReferenceEquals(last, fixture.Hit("110"))
            && ReferenceEquals(fixture.Hit("110"), fixture.Window.FocusManager?.GetFocusedElement()), "the rebuilt hit's focus");
        Assert.Equal(("110", BooruLoadOrigin.Search, "cat"), Assert.Single(fixture.Queue.Loads));
        Assert.True(scroll.Offset.Y > 0);
        Assert.InRange(Bounds(fixture.Hit("110"), scroll).Center.Y, 0, scroll.Viewport.Height);
    });

    [Theory]
    [MemberData(nameof(BooruListLayoutTests.Languages), MemberType = typeof(BooruListLayoutTests))]
    public void PointerKeyboardMiddleAndMoreOperateOnTheirNamedGridHit(string language) => Run(() =>
    {
        using Fixture fixture = new();
        fixture.Queue.States["101"] = BooruLocalState.Loaded;
        fixture.View.ShowInMedia = id => { fixture.LocallyOpened.Add(id); return Task.FromResult(true); };
        fixture.Search("cat");
        Texts.SetLanguage(language);
        fixture.Settle();
        fixture.View.ApplyWorkView(WorkView.Barebones, viewChanged: true);
        fixture.Settle();
        Border first = fixture.Hit("101"), second = fixture.Hit("102");

        ContextMenu saved = OpenContext(fixture.Window, first);
        Assert.False(Item(saved, BooruWorkbenchView.MenuLoad).IsEnabled);
        Assert.Equal(Texts.T("booru.media.openPost"), Item(saved, BooruWorkbenchView.MenuOpenPost).Header);
        Click(Item(saved, BooruWorkbenchView.MenuShowInMedia));
        Assert.Equal("101", Assert.Single(fixture.LocallyOpened));

        Point a = fixture.Picture("101"), b = fixture.Picture("102");
        fixture.Window.MouseDown(a, MouseButton.Middle);
        fixture.Window.MouseUp(b, MouseButton.Middle);
        Assert.Empty(fixture.Harness.Links.Opened);
        fixture.Window.MouseDown(b, MouseButton.Middle);
        fixture.Window.MouseUp(b, MouseButton.Middle);
        Assert.Equal(Url("102"), Assert.Single(fixture.Harness.Links.Opened));

        fixture.Window.MouseDown(b, MouseButton.Right);
        fixture.Window.MouseUp(b, MouseButton.Right);
        fixture.Settle();
        ContextMenu pointerMenu = Assert.IsType<ContextMenu>(second.ContextMenu);
        Assert.True(pointerMenu.IsOpen);
        Assert.Equal(Texts.T("booru.ui.post.title", "102"), AutomationProperties.GetName(pointerMenu));
        Assert.Equal(Texts.T("booru.media.copyPost"), Item(pointerMenu, BooruWorkbenchView.MenuCopyLink).Header);
        Click(Item(pointerMenu, BooruWorkbenchView.MenuCopyLink));

        ContextMenu more = fixture.OpenMore();
        Click(MoreCopy(more, "101"));
        Assert.Equal(new[] { Url("102"), Url("101") }, fixture.Harness.Links.Copied);
        Assert.Empty(fixture.Queue.Loads);
        Assert.Single(fixture.Harness.Search.Searches);
    });

    [Fact]
    public void OldPopupAndPointerPressCannotActOnReboundRowsAndLateWorkCannotReachAnotherTab() => Run(() =>
    {
        using Fixture fixture = new();
        fixture.View.ApplyWorkView(WorkView.Barebones, viewChanged: true);
        fixture.Search("cat");
        ContextMenu context = OpenContext(fixture.Window, fixture.Hit("102"));
        MenuItem oldLoad = Item(context, BooruWorkbenchView.MenuLoad);
        TaskCompletionSource<BooruLoadResult> load = new();
        fixture.Queue.Load = () => load.Task;
        Click(oldLoad);
        fixture.Until(() => fixture.Row("102").Load == BooruRowLoad.Handing, "the original admission");

        TaskCompletionSource copy = new();
        fixture.Harness.Links.OnCopy = url => url == Url("101") ? copy.Task : Task.CompletedTask;
        ContextMenu more = fixture.OpenMore();
        MenuItem oldCopy = MoreCopy(more, "101");
        Click(oldCopy);
        Assert.Equal(Url("101"), Assert.Single(fixture.Harness.Links.Copied));

        Border oldTile = fixture.Hit("102");
        Point picture = fixture.Picture("102");
        fixture.Window.MouseDown(picture, MouseButton.Middle);
        fixture.View.ChooseTileSize(MediaTileSize.Medium);
        fixture.Settle();
        Assert.NotSame(oldTile, fixture.Hit("102"));
        fixture.Window.MouseUp(fixture.Picture("102"), MouseButton.Middle);
        Assert.Empty(fixture.Harness.Links.Opened);

        int originalTab = fixture.View.SearchTabs.ActiveId;
        Click(Find<Button>(fixture.View, BooruWorkbenchView.NewSearchTabName));
        fixture.Settle();
        Assert.NotEqual(originalTab, fixture.View.SearchTabs.ActiveId);
        fixture.Search("blue_eyes");
        Assert.Equal(BooruRowLoad.Idle, fixture.Row("102").Load);
        Assert.False(oldLoad.Command!.CanExecute(oldLoad.CommandParameter));
        Assert.False(oldCopy.Command!.CanExecute(oldCopy.CommandParameter));
        oldLoad.Command.Execute(oldLoad.CommandParameter);
        oldCopy.Command.Execute(oldCopy.CommandParameter);
        Assert.Equal(("102", BooruLoadOrigin.Search, "cat"), Assert.Single(fixture.Queue.Loads));
        Assert.Equal(Url("101"), Assert.Single(fixture.Harness.Links.Copied));

        load.SetResult(new BooruLoadResult(BooruLoadStatus.Failed, "old-admission"));
        copy.SetResult();
        fixture.Settle();
        Assert.Equal(BooruRowLoad.Idle, fixture.Row("102").Load);
        Assert.False(Find<TextBlock>(fixture.View.Footer, BooruWorkbenchView.LinkNoteName).IsVisible);

        fixture.Queue.Load = null;
        ContextMenu current = OpenContext(fixture.Window, fixture.Hit("102"));
        Click(Item(current, BooruWorkbenchView.MenuLoad));
        fixture.Until(() => fixture.Row("102").Load == BooruRowLoad.Queued, "the current admission");
        Assert.Equal(("102", BooruLoadOrigin.Search, "blue_eyes"), fixture.Queue.Loads[1]);
        Click(MoreCopy(fixture.OpenMore(), "102"));
        Assert.Equal(Url("102"), fixture.Harness.Links.Copied[1]);
        Assert.True(Find<TextBlock>(fixture.View.Footer, BooruWorkbenchView.LinkNoteName).IsVisible);
    });

    [Fact]
    public async Task CompletingAnOriginalAdmissionAfterANewQueryCannotUpdateTheSameId()
    {
        BooruUiHarness harness = new();
        harness.Serve(pages: 1);
        TaskCompletionSource<BooruLoadResult> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Queue.OnLoad = (_, _) => pending.Task;
        using BooruWorkbenchModel model = new(harness.Services);
        BooruSearchModel search = model.Search;
        await BooruTestSupport.SearchAsync(search, "cat");
        BooruSearchView first = search.View;
        BooruResultTarget target = BooruResultTarget.From(first, first.Rows[0]);
        Task admitted = search.LoadOriginalAsync("101", target);
        await BooruTestSupport.SearchAsync(search, "blue_eyes");
        pending.SetResult(new BooruLoadResult(BooruLoadStatus.Failed, "old-admission"));
        await admitted;

        Assert.Equal("cat", Assert.Single(harness.Queue.LoadQueries));
        Assert.Equal(BooruRowLoad.Idle, search.View.Rows[0].Load);
        Assert.Null(search.View.Rows[0].LoadCode);
        await search.LoadOriginalAsync("101", target);
        Assert.Single(harness.Queue.Loads);
    }

    [Fact]
    public void RetryAndCancelInTheGridUseTheSameRowStateAsListButtons() => Run(() =>
    {
        using Fixture fixture = new(grid: false);
        fixture.Queue.States["102"] = BooruLocalState.DownloadFailed;
        fixture.Search("cat");
        Border listRow = fixture.Hit("102");
        Assert.True(Find<Button>(listRow, BooruWorkbenchView.RetryLoadPrefix + "102").IsEnabled);
        Assert.True(Find<Button>(listRow, BooruWorkbenchView.CancelLoadPrefix + "102").IsEnabled);
        fixture.View.RestoreResultView(BooruResultView.Grid, MediaTileSize.Large);
        fixture.Settle();

        ContextMenu both = OpenContext(fixture.Window, fixture.Hit("102"));
        Assert.True(Item(both, "booru.menu.cancel-original").IsEnabled);
        Click(Item(both, BooruWorkbenchView.MenuLoad));
        fixture.Until(() => fixture.Row("102").Load == BooruRowLoad.Queued, "the retried admission");
        Assert.Equal("102", Assert.Single(fixture.Queue.Retries));
        Assert.False(Item(both, BooruWorkbenchView.MenuLoad).Command!.CanExecute(null));

        ContextMenu queued = OpenContext(fixture.Window, fixture.Hit("102"));
        Click(Item(queued, BooruWorkbenchView.MenuLoad));
        fixture.Until(() => fixture.Row("102").LoadCode == BooruCodes.Cancelled, "the cancelled admission");
        Assert.Equal("102", Assert.Single(fixture.Queue.Cancellations));
        Assert.Empty(fixture.Queue.Loads);
    });

    [Fact]
    public void WatcherContextRetainsItsCheckLinksAndLastRemoveAction() => Run(() =>
    {
        using Fixture fixture = new();
        fixture.Harness.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: fixture.Harness.Clock) with { LatestPostId = "102" });
        BooruComponentDispatcher.Wait(fixture.View.ActivateAsync());
        Click(Find<Button>(fixture.View, BooruWorkbenchView.OpenWatchersName));
        fixture.Settle();
        ContextMenu menu = OpenContext(fixture.Window, Find<Border>(fixture.View, "BooruWatcher:w1"));
        Assert.Equal(BooruWorkbenchView.MenuRemove, menu.Items.OfType<MenuItem>().Last().Tag);
        Assert.True(Item(menu, BooruWorkbenchView.MenuCheck).IsEnabled);
        Click(Item(menu, BooruWorkbenchView.MenuOpenPost));
        Assert.Equal(Url("102"), Assert.Single(fixture.Harness.Links.Opened));
        Assert.Empty(fixture.Queue.Loads);
    });

    [Fact]
    public void TheContextGestureResolvesOnMacAndMenuKeyWorksOnTheWorkbench() => Run(() =>
    {
        EffectiveShortcutBindings mac = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        Assert.True(mac.Matches(new KeyEventArgs { Key = Avalonia.Input.Key.F10, KeyModifiers = KeyModifiers.Shift }, "menu.context-open", "booru"));
        using Fixture fixture = new();
        fixture.Search("cat");
        Border hit = fixture.Hit("102");
        Assert.True(hit.Focus());
        fixture.Window.KeyPress(Avalonia.Input.Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
        fixture.Settle();
        Assert.True(hit.ContextMenu?.IsOpen);
        Click(Item(hit.ContextMenu!, BooruWorkbenchView.MenuOpenPost));
        Assert.Equal(Url("102"), Assert.Single(fixture.Harness.Links.Opened));
    });

    internal static ContextMenu OpenContext(Window window, Border hit)
    {
        Assert.True(hit.Focus());
        Key(window, PhysicalKey.F10, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        ContextMenu menu = Assert.IsType<ContextMenu>(hit.ContextMenu);
        Assert.True(menu.IsOpen);
        return menu;
    }

    internal static MenuItem Item(ContextMenu menu, string id) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, id));

    internal static void Click(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control)!;
        root.UpdateLayout();
        Point point = Bounds(control, root).Center;
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static MenuItem MoreCopy(ContextMenu menu, string id) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => AutomationProperties.GetName(item) == Texts.T("booru.ui.post.copyFor", id));

    private static bool IsHit(Border card) => card.Name?.StartsWith("BooruResult:", StringComparison.Ordinal) == true;
    private static string Url(string id) => "https://danbooru.donmai.us/posts/" + id;

    private static T Find<T>(Visual owner, string name) where T : Control =>
        Assert.Single(owner.GetVisualDescendants().OfType<T>(), candidate => candidate.Name == name);

    private static Rect Bounds(Control control, Visual owner) =>
        new(control.TranslatePoint(default, owner) ?? throw new InvalidOperationException(control.Name), control.Bounds.Size);

    private static void Key(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        test();
    });

    private sealed class Fixture : IDisposable
    {
        private readonly BooruPage? _page;
        internal BooruUiHarness Harness { get; } = new();
        internal QueuePort Queue { get; } = new();
        internal List<string> LocallyOpened { get; } = [];
        internal Window Window { get; }
        internal BooruWorkbenchView View { get; }

        internal Fixture(bool separate = false, int perPage = 3, bool grid = true)
        {
            Harness.Serve(pages: 1, perPage: perPage);
            BooruWorkbenchServices services = new(Harness.Search, Queue, Harness.Watchers, Harness.Previews, Harness.Clock, Harness.Links.Actions);
            if (separate)
            {
                BooruWorkbenchWindow workbench = new(services);
                Window = workbench;
                Window.Show();
                View = Assert.Single(Window.GetVisualDescendants().OfType<BooruWorkbenchView>());
            }
            else
            {
                _page = new BooruPage(services);
                View = _page.View;
                Window = new Window { Content = _page };
                Window.Show();
            }
            Window.RequestedThemeVariant = ThemeVariant.Dark;
            Window.Activate();
            Window.PlatformImpl!.Resize(new Size(780, 620), Avalonia.Controls.WindowResizeReason.User);
            View.NewSearchTarget = () => MeowChan.Config.BooruSearchTargetCurrentTab;
            if (grid) View.RestoreResultView(BooruResultView.Grid, MediaTileSize.Large);
            Settle();
        }

        internal void Search(string query)
        {
            Find<TextBox>(View, BooruWorkbenchView.InputName).Text = query;
            Settle();
            Click(Find<Button>(View, BooruWorkbenchView.SubmitName));
            Until(() => View.Model.Search.View.ExecutedQuery == query
                && View.Model.Search.View.Rows.Length > 0
                && View.Model.Search.View.Rows.All(row => row.Preview == BooruPreviewState.Ready)
                && View.GetVisualDescendants().OfType<Image>().Count(image => image.Source is Bitmap) >= View.Model.Search.View.Rows.Length,
                "the arranged search results");
        }

        internal Border Hit(string id) => Find<Border>(View, "BooruResult:" + id);
        internal Point Picture(string id) => Bounds(Find<Border>(Hit(id), "BooruPreviewFrame:" + id), Window).Center;
        internal BooruResultRowView Row(string id) => View.Model.Search.View.Rows.Single(row => row.Post.PostId == id);
        internal ContextMenu OpenMore()
        {
            Button more = Find<Button>(View, "BooruMore");
            Click(more);
            ContextMenu menu = Assert.IsType<ContextMenu>(more.ContextMenu);
            Assert.True(menu.IsOpen);
            return menu;
        }

        internal void Until(Func<bool> predicate, string description)
        {
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() =>
            {
                Settle();
                return predicate();
            }, description));
            Settle();
        }

        internal void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.Close();
            _page?.Dispose();
            Settle();
        }
    }

    private sealed class QueuePort : IBooruQueuePort
    {
        internal ConcurrentDictionary<string, BooruLocalState> States { get; } = new();
        internal List<(string Id, BooruLoadOrigin Origin, string? Query)> Loads { get; } = [];
        internal List<string> Cancellations { get; } = [];
        internal List<string> Retries { get; } = [];
        internal Func<Task<BooruLoadResult>>? Load { get; set; }

        public event Action? LocalStatesChanged { add { } remove { } }

        public Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, CancellationToken cancellationToken) =>
            LoadOriginalAsync(post, origin, null, cancellationToken);

        public Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, string? query, CancellationToken cancellationToken)
        {
            Loads.Add((post.PostId, origin, query));
            return Load?.Invoke() ?? Task.FromResult(new BooruLoadResult(BooruLoadStatus.Queued));
        }

        public Task<bool> CancelOriginalAsync(string postId, CancellationToken cancellationToken)
        {
            Cancellations.Add(postId);
            return Task.FromResult(true);
        }

        public Task<BooruLoadResult> RetryOriginalAsync(string postId, CancellationToken cancellationToken)
        {
            Retries.Add(postId);
            States.TryRemove(postId, out _);
            return Task.FromResult(new BooruLoadResult(BooruLoadStatus.Queued));
        }

        public Task<IReadOnlyDictionary<string, BooruLocalState>> GetLocalStatesAsync(IReadOnlyList<string> postIds, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, BooruLocalState>>(
                States.Where(pair => postIds.Contains(pair.Key)).ToDictionary());
    }
}
