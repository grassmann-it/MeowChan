using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class TaggingPageSelectionTests
{
    [Fact]
    public void TheViewSwitchAndSelectionActionsStayOnTheSurface() => WithPage((page, _) =>
    {
        Control[] controls = [page.SelectionBar, page.SelectionText, page.SelectionTagButton, page.SelectionDoneButton,
            page.SelectionMoreButton, page.SelectionClearButton];
        foreach (WorkView view in WorkViewSetting.All)
        {
            page.ApplyWorkView(view);
            Assert.True(page.ViewToggle.IsEffectivelyVisible);
            Assert.False(page.SelectionBar.IsEffectivelyVisible);

            page.ShownList.SelectedIndex = 0;
            Assert.All(controls, control => Assert.True(control.IsEffectivelyVisible, control.Name));
            Assert.Equal(Texts.T("media.selection.count", "1"), page.SelectionText.Text);
            page.SelectionClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, page.SelectedCount);
        }
    });

    [Fact]
    public void BothViewsShareTheSelectionTheMenuTheKeysAndTheDoubleClick() => WithPage((page, window) =>
    {
        TagQueueKey? continuedAt = null;
        page.StartTagging = walk => { continuedAt = walk.Start; return Task.CompletedTask; };
        string[]? menuItems = null;
        foreach (TaggingViewMode view in new[] { TaggingViewMode.List, TaggingViewMode.Grid })
        {
            page.RestoreViewMode(view);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            ListBox list = page.ShownList;
            list.Selection.Select(0);
            list.Selection.Select(1);
            Assert.Equal(2, page.SelectedCount);
            Assert.Equal(Texts.T("media.selection.count", "2"), page.SelectionText.Text);

            ListBoxItem first = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            first.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F10, KeyModifiers = KeyModifiers.Shift });
            Assert.True(page.OpenListMenu?.IsOpen);
            string[] items = page.ListMenuModel()!.Entries.Select(entry => entry.Id).ToArray();
            Assert.Contains(TaggingCommands.ContinueHere, items);
            menuItems ??= items;
            Assert.Equal(menuItems, items);
            page.OpenListMenu!.Close();
            Dispatcher.UIThread.RunJobs();

            KeyEventArgs escape = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            first.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.Empty(list.SelectedItems!);
            Assert.False(page.SelectionBar.IsEffectivelyVisible);

            list.SelectedIndex = 0;
            continuedAt = null;
            first.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent,
                new PointerEventArgs(InputElement.PointerReleasedEvent, first,
                    new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), first, default, 0,
                    PointerPointProperties.None, KeyModifiers.None)));
            BooruComponentDispatcher.WaitUntil(() => continuedAt.HasValue, "the selected entry is passed to the gallery after the cursor write");
            Assert.Equal(page.Rows[0].Key, continuedAt);
            page.ClearSelection();
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SelectionMoreOpensForOpenAndFinishedEntriesAndReturnsFocus(bool grid, bool finished) => WithPage((page, window) =>
    {
        TransientPopupPolicy.Install();
        page.StartTagging = _ => Task.CompletedTask;
        page.RestoreViewMode(grid ? TaggingViewMode.Grid : TaggingViewMode.List);
        ContextGestures.Settle(window);

        string entryId = (grid ? "TaggingGridEntry" : "TaggingListEntry") + (finished ? "3" : "1");
        ListBoxItem entry = Find<ListBoxItem>(window, entryId);
        ContextGestures.Click(entry);
        Assert.True(entry.IsSelected);
        Assert.Equal(1, page.SelectedCount);
        if (finished)
        {
            ContextGestures.KeyStroke(entry, Key.Enter, PhysicalKey.Enter);
            Assert.Equal(Texts.T("tagging.page.continue.notOpen"), Find<TextBlock>(window, "TaggingStatus").Text);
        }

        Button more = Find<Button>(window, "TaggingSelectionMore");
        int clicks = 0;
        List<string?> states = [AutomationProperties.GetItemStatus(more)];
        more.Click += (_, _) => clicks++;
        more.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationProperties.ItemStatusProperty) states.Add(AutomationProperties.GetItemStatus(more));
        };

        ContextMenu menu = OpenByClick();
        string action = finished ? TaggingCommands.MarkOpen : TaggingCommands.MarkSkipped;
        Assert.True(Find<MenuItem>(menu, action).IsEffectivelyEnabled);
        ContextGestures.Click(more);
        VerifyDismissed(menu);

        menu = OpenByClick();
        Assert.True(menu.IsKeyboardFocusWithin);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        ContextGestures.Settle(window);
        VerifyDismissed(menu);

        ContextMenu OpenByClick()
        {
            ContextGestures.Click(more);
            ContextMenu? current = OpenMenu();
            Assert.True(current?.IsOpen == true, $"More received {clicks} click(s). Item status: {string.Join(" → ", states)}.");
            return current!;
        }

        void VerifyDismissed(ContextMenu previous)
        {
            Assert.False(previous.IsOpen);
            Assert.Null(OpenMenu());
            Assert.Same(Find<ListBoxItem>(window, entryId), window.FocusManager?.GetFocusedElement());
            Assert.Equal(1, page.SelectedCount);
        }

        ContextMenu? OpenMenu() => window.OpenedPopups.Where(popup => ReferenceEquals(popup.PlacementTarget, more))
            .Select(popup => popup.Child).OfType<ContextMenu>().SingleOrDefault();
    });

    [Fact]
    public void SwitchingViewsAndFilteringKeepsOnlyTheCurrentSelectedRows() => WithPage((page, _) =>
    {
        page.List.Selection.Select(0);
        page.List.Selection.Select(2);
        TagQueueKey first = page.Rows[0].Key;
        TagQueueKey finished = page.Rows[2].Key;
        page.RestoreViewMode(TaggingViewMode.Grid);
        CheckSelection(first, finished);
        page.Filter = TaggingQueueFilter.Open;
        CheckSelection(first);
        page.Filter = TaggingQueueFilter.All;
        page.RestoreViewMode(TaggingViewMode.List);
        CheckSelection(first);

        void CheckSelection(params TagQueueKey[] expected)
        {
            ListBox hidden = ReferenceEquals(page.ShownList, page.List) ? page.TileGrid : page.List;
            Assert.Null(hidden.ItemsSource);
            Assert.Empty(hidden.SelectedItems!);
            TaggingQueueRow[] selected = page.ShownList.SelectedItems!.Cast<TaggingQueueRow>().ToArray();
            Assert.Equal(expected, selected.Select(row => row.Key));
            Assert.All(selected, row => Assert.Same(page.Rows.Single(current => current.Key == row.Key), row));
        }
    });

    [Fact]
    public void TheViewChoiceIsSavedPerProfileAndRestoredWhenThePageIsBuilt() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, FirstRunSetupVersion = 1,
            DownloadPath = Path.Combine(profile.Path, "media"), TaggingPageView = "Grid", WorkspaceMode = "Advanced" }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            await using DesktopSessionHost host = new(profile.Path, []);
            await host.StartAsync();
            await using MainWindow window = new(host);
            window.Show();
            Button footer = window.FindControl<Button>("TaggingNavigationButton")!;
            await BooruTestSupport.WaitAsync(() => footer.IsEffectivelyEnabled, "Tagging navigation is enabled");
            Assert.True(window.ShowTaggingPage());
            TaggingPage page = Assert.IsType<TaggingPage>(window.TaggingPage);
            Assert.Equal(1, page.ViewToggle.SelectedIndex);
            Assert.Same(page.TileGrid, page.ShownList);
            Assert.Equal("Grid", Config.LoadConfig(profile.Path).TaggingPageView);

            page.ViewToggle.First.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(page.List, page.ShownList);
            await BooruTestSupport.WaitAsync(() => host.Config.TaggingPageView == "List", "the list preference is saved");
            Assert.Equal("List", Config.LoadConfig(profile.Path).TaggingPageView);
        }
    });

    [Fact]
    public void TileBadgesCoverThePreviewWithoutBlockingFocusOrTheSelectionCheck() => WithPage((page, window) =>
    {
        page.RestoreViewMode(TaggingViewMode.Grid);
        page.ShownList.SelectedIndex = 2;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        ListBoxItem item = Assert.IsType<ListBoxItem>(page.ShownList.ContainerFromIndex(2));
        TaggingQueueTileView tile = Assert.Single(item.GetVisualDescendants().OfType<TaggingQueueTileView>());
        MediaBadgeView badges = Assert.Single(tile.GetVisualDescendants().OfType<MediaBadgeView>());

        Assert.True(item.Focus(NavigationMethod.Tab));
        Assert.False(badges.Focusable);
        Assert.False(badges.IsHitTestVisible);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(badges));
        Assert.Equal(BoundsIn(tile.Preview, tile), BoundsIn(badges, tile));
        PlacedBadge mark = Assert.Single(badges.Plan.Badges, placed => placed.Badge.Id == MediaBadgeId.Tagging);
        Assert.Equal("check", mark.Badge.Icon);
        Assert.True(tile.SelectedMark.IsEffectivelyVisible);
        Assert.False(BoundsIn(tile.SelectedMark, badges).Intersects(mark.Bounds));
        Assert.EndsWith(Texts.T("media.badge.tagging.done"), AutomationProperties.GetName(tile));
        Assert.Equal(AutomationProperties.GetName(tile), AutomationProperties.GetName(item));
    });

    [Fact]
    public void TheNewTextsExistInEveryLanguage() => BooruComponentDispatcher.Run(() =>
    {
        foreach (string language in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(language);
            foreach (string key in new[] { "tagging.page.view", "tagging.page.view.list", "tagging.page.view.list.hint", "tagging.page.view.grid",
                "tagging.page.view.grid.hint", "tagging.page.selection.name", "tagging.page.selection.state", "tagging.page.selection.state.hint",
                "tagging.page.selection.remove" })
                Assert.False(string.IsNullOrWhiteSpace(Texts.Own(key)), language + " · " + key);
        }
    });

    private static Rect BoundsIn(Control control, Control ancestor) =>
        new(control.TranslatePoint(default, ancestor)!.Value, control.Bounds.Size);

    private static T Find<T>(Control root, string automationId) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == automationId);

    private static void WithPage(Action<TaggingPage, Window> check) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false,
            DownloadPath = Path.Combine(profile.Path, "media") }.SaveConfig(profile.Path);
        DesktopSessionHost host = new(profile.Path, []);
        TaggingPage? page = null;
        Window window = new() { Width = 1100, Height = 640 };
        try
        {
            BooruComponentDispatcher.Wait(host.StartAsync());
            TagQueueItem[] items = Enumerable.Range(1, 3).Select(index => TaggingLayoutRhythmTests.Item() with
            {
                AssetId = "asset-" + index, MediaId = "media-" + index
            }).ToArray();
            BooruComponentDispatcher.Wait(host.EnqueueTagQueueAsync(items));
            BooruComponentDispatcher.Wait(host.SetTagQueueStatesAsync([KeyValuePair.Create(items[2].Key, TagQueueState.Done)]));
            page = new TaggingPage(host);
            window.Content = page;
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            BooruComponentDispatcher.Wait(page.ActivateAsync());
            page.Filter = TaggingQueueFilter.All;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(3, page.Rows.Count);
            check(page, window);
        }
        finally
        {
            window.Close();
            page?.End();
            BooruComponentDispatcher.Wait(host.DisposeAsync().AsTask());
        }
    });
}
