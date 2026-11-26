using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderMediaSidebarHostClickTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostDetailLoadsOnlyTheCurrentReplyPageWhenTheLargeReaderAppears(bool attachWhileHidden)
    {
        using ReaderArchiveTestFixture fixture = new();
        ArchivedThreadView thread = await fixture.CaptureAsync(new("4chan", "g", "100"), "Sidebar replies",
        [
            new("100", 0, PostFieldPresence.All, Html: "First post"),
            new("101", 1, PostFieldPresence.All, Html: "Second post"),
            new("102", 2, PostFieldPresence.All, Html: "<a href=\"#p100\" class=\"quotelink\">&gt;&gt;100</a> First reply"),
            new("103", 3, PostFieldPresence.All, Html: "<a href=\"#p101\" class=\"quotelink\">&gt;&gt;101</a> Second reply")
        ]);
        fixture.Run(page =>
        {
            Grid host = new() { IsVisible = !attachWhileHidden };
            Window window = new() { Width = 1920, Height = 900, Content = new Grid { Children = { page, host } } };
            try
            {
                page.ApplyReaderMediaPreferences(ReaderMediaPreferences.Default);
                window.Show();
                ReaderArchiveTestFixture.Load(page, thread);
                BooruComponentDispatcher.Wait(page.WaitForReaderViewAsync());
                window.UpdateLayout();
                Assert.False(page.IsMediaSidebarShown);

                Control frame = page.DetachReader();
                ListBox posts = SidePanelDesignTests.ById<ListBox>(frame, "ArchivePosts");
                ScrollViewer detail = SidePanelDesignTests.ById<ScrollViewer>(frame, "ReaderSidebarPostDetail");
                var first = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == "100");
                var second = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == "101");
                Assert.Same(first, posts.SelectedItem);
                Assert.True(detail.IsVisible);
                Assert.False(detail.IsAttachedToVisualTree());
                Assert.False(first.RepliesLoading);
                Assert.False(first.RepliesLoaded);

                List<string> requests = [];
                second.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ArchivePostRow.RepliesLoading) && second.RepliesLoading)
                        requests.Add(second.Id);
                };
                posts.SelectedItem = second;
                Assert.Empty(requests);

                host.Children.Add(frame);
                Assert.True(detail.IsAttachedToVisualTree());
                if (attachWhileHidden)
                {
                    Assert.False(detail.IsEffectivelyVisible);
                    Assert.Empty(requests);
                    host.IsVisible = true;
                }
                window.UpdateLayout();
                Assert.True(detail.IsEffectivelyVisible);
                BooruComponentDispatcher.WaitUntil(() => second.RepliesLoaded && !second.RepliesLoading, "replies for the visible sidebar post");
                window.UpdateLayout();
                Assert.Equal(second.Id, Assert.Single(requests));
                Assert.False(first.RepliesLoaded);
                Button reply = SidePanelDesignTests.ById<Button>(detail, "ReaderSidebarReply");
                Assert.True(reply.IsEffectivelyVisible);
                Assert.Contains("Second reply", Assert.IsType<ArchiveQuoteRow>(reply.DataContext).ReplyExcerpt);
            }
            finally { window.Close(); }
        }, hosted: true);
    }

    [Theory]
    [InlineData("ReaderMediaSidebarDecisionRating3")]
    [InlineData("ReaderMediaSidebarEditTags")]
    public void FreshSidebarClickWaitsForTheSelectedPost(string actionId) =>
        SavedMediaReaderTests.RunWithShell(async (page, window, _, _) =>
        {
            page.UseManualReaderTicks(static () => 0);
            ListBox posts = SidePanelDesignTests.ById<ListBox>(window, "ArchivePosts");
            var a = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == "42");
            var b = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == "43");
            List<string> targets = [];
            page.SetMediaRatingAsync = (key, _) => { targets.Add(key.Sha256); return Task.CompletedTask; };
            page.TagPopoverAsync = (entry, _, _, _, _) => { targets.Add(entry.Asset.Sha256); return Task.CompletedTask; };
            posts.SelectedItem = a;
            page.TickMediaSidebarFollow();
            await page.MediaSidebarTask;
            var sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
            Assert.Contains("post.png", sidebar.HeaderLine);
            Button action = SidePanelDesignTests.ById<Button>(sidebar, actionId);
            Assert.True(action.IsEffectivelyEnabled);

            posts.SelectedItem = b;
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(targets);

            page.TickMediaSidebarFollow();
            await page.MediaSidebarTask;
            Assert.Contains("same.png", sidebar.HeaderLine);
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // 73-2: the action first checks the shown original, then writes.
            await BooruTestSupport.WaitAsync(() => targets.Count > 0, "the click on the followed post's medium");
            var attachment = Assert.Single(b.Attachments);
            GalleryEntry expected = Assert.IsType<GalleryEntry>(await attachment.ResolveEntry!(CancellationToken.None));
            Assert.Equal(expected.Asset.Sha256, Assert.Single(targets));
        }, replyHasMedia: true);

    [Fact]
    public void PostPopoverChipAfterSelectionChangePreservesBothTagAssignments() =>
        SavedMediaReaderTests.RunWithShell(async (page, window, host, subjects) =>
        {
            TagId offered = (await host.DefinePrivateTagAsync("post-chip")).Tag;
            await host.SetThreadTagAsync(offered, page.CurrentThread!.Summary.Id, true);
            ListBox posts = SidePanelDesignTests.ById<ListBox>(window, "ArchivePosts");
            var a = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == "42");
            var b = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == "43");
            posts.SelectedItem = a;
            posts.ScrollIntoView(a);
            window.UpdateLayout();
            Assert.True(Assert.IsType<ReaderPostItem>(posts.ContainerFromItem(a)).Focus());
            QuickTagPopover editor = await RateAndTagWindowFixture.OpenEditorAsync(window);
            Assert.Same(posts, editor.Anchor);
            QuickTagSurface surface = Assert.IsType<QuickTagSurface>(editor.Surface);
            await BooruTestSupport.WaitAsync(() => surface.Shown.Any(tag => tag.Id == offered), "the thread tag on the open post");
            int number = surface.Shown.ToList().FindIndex(tag => tag.Id == offered) + 1;
            Button chip = SidePanelDesignTests.ById<Button>(editor.Frame, "ArchiveTagPopoverSuggestion" + number);
            Assert.True(chip.IsEffectivelyEnabled);

            posts.SelectedItem = b;
            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BooruTestSupport.WaitAsync(() => !surface.Session.Busy, "the tag click to finish");
            var saved = Assert.IsType<PrivateTagStoreSnapshot>(host.GetPrivateTagSnapshot().Snapshot);
            Assert.False(saved.Carries(offered, subjects['c']));
            Assert.False(saved.Carries(offered, subjects['a']));
            Assert.Equal(QuickTagPopoverCloseReason.Stale, editor.Reason);
        }, replyHasMedia: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostSelectionDrivesTheSidebarAndRateAndTagSurvivesThePostLayout(bool largeReader) => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        TransientPopupPolicy.Install();
        using TempDirectory profile = new();
        using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false);
        ArchivedThreadView baseThread = RateAndTagWindowFixture.View(media);
        ArchivedThreadView thread = new(baseThread.Summary with { PostCount = 3 },
            [.. baseThread.GetPosts(), new("45", 2, PostFieldPresence.All,
                Html: "<a href=\"#44\" class=\"quotelink\">&gt;&gt;44</a> Sidebar reply")],
            baseThread.MediaDirectories, decimalPostOrder: true);
        await RateAndTagWindowFixture.SeedAsync(profile.Path, thread);
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        PrivateTagStore tags = new(profile.Path, lease);
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token), (_, _) => Task.CompletedTask,
            _ => { }, (_, _) => Task.FromResult(media.Snapshot)) { IsAvailable = true };
        page.ApplyReaderMediaPreferences(ReaderMediaPreferences.Default with { Sidebar = true, SidebarAlways = true });
        ContentControl host = new() { IsVisible = false };
        Window window = new() { Width = 1920, Height = 900, Content = new Grid { Children = { page, host } } };
        List<(GalleryEntry Entry, Control Anchor)> opened = [];
        page.TagPopoverAsync = (entry, anchor, _, current, request) =>
        {
            opened.Add((entry, anchor));
            var target = Assert.IsType<MediaTagSubject.Bound>(MediaTagSubjects.Of(entry));
            QuickTagSession session = new(QuickTagTarget.For(new MediaTagSession(target,
                () => new(PrivateTagLoadState.Ready, tags.ReadSnapshot(), null),
                (_, _, _) => throw new InvalidOperationException("Unexpected tag write."),
                (_, _, _, _) => throw new InvalidOperationException("Unexpected tag write."))));
            return QuickTagPopover.Open(anchor, session, entry.Asset.OriginalFileName!,
                new QuickTagPopoverOptions { IsCurrent = current, FocusFallback = request.FocusFallback }).Result;
        };

        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await page.LoadThreadAsync(thread.Summary.Id, preparedThread: thread);
            await page.MediaLoadTask;
            if (largeReader) page.HostLargeReader(host, () => page.IsVisible = false, () => page.SetReaderScope([thread.Summary.Id]));
            window.UpdateLayout();
            ListBox posts = SidePanelDesignTests.ById<ListBox>(window, "ArchivePosts");
            await BooruTestSupport.WaitAsync(() => page.IsMediaSidebarShown, "the reader media sidebar");
            ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);

            ArchivePostRow text = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == "44");
            posts.SelectedItem = text;
            await BooruTestSupport.WaitAsync(() => sidebar.HeaderLine.Contains("44", StringComparison.Ordinal), "the selected text-only post in the sidebar");
            Assert.Null(sidebar.Shown);
            ScrollViewer detail = SidePanelDesignTests.ById<ScrollViewer>(sidebar, "ReaderSidebarPostDetail");
            Assert.True(detail.IsEffectivelyVisible);
            Assert.Equal(Texts.T("reader.sidebar.postDetail", text.Id), AutomationProperties.GetName(detail));
            Assert.False(sidebar.Open.IsEffectivelyVisible);
            Assert.False(sidebar.InfoToggle.IsEffectivelyVisible);
            Assert.False(SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarFavorite").IsEffectivelyVisible);
            await BooruTestSupport.WaitAsync(() => detail.GetVisualDescendants().OfType<Button>().Any(button =>
                AutomationProperties.GetAutomationId(button) == "ReaderSidebarReply" && button.IsEffectivelyVisible), "the text post's reply in the sidebar");
            ReaderBodyTextBlock body = SidePanelDesignTests.ById<ReaderBodyTextBlock>(detail, "ReaderSidebarPostBody");
            Assert.Equal("Text only", body.Document?.Text);
            Button reply = SidePanelDesignTests.ById<Button>(detail, "ReaderSidebarReply");
            Assert.Contains("Sidebar reply", Assert.IsType<ArchiveQuoteRow>(reply.DataContext).ReplyExcerpt);
            Assert.False(text.RepliesExpanded);

            ArchivePostRow picture = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == "43");
            posts.SelectedItem = picture;
            posts.ScrollIntoView(picture);
            await BooruTestSupport.WaitAsync(() => sidebar.HeaderLine.Contains(media.Asset.OriginalFileName!, StringComparison.Ordinal),
                "the selected picture in the sidebar");
            Assert.False(detail.IsEffectivelyVisible);
            Assert.True(sidebar.Open.IsEffectivelyVisible);
            window.UpdateLayout();
            var card = Assert.IsType<ReaderPostItem>(posts.ContainerFromItem(picture));
            Assert.True(card.Focus());
            QuickTagPopover editor = await RateAndTagWindowFixture.OpenEditorAsync(window);
            var edit = Assert.Single(opened);
            Assert.Same(posts, edit.Anchor);
            Assert.Equal(picture.Id, edit.Entry.Post?.PostId);
            Assert.Equal(media.Asset.AssetId, edit.Entry.Asset.AssetId);

            window.Height -= 80;
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(editor.IsOpen);

            editor.Surface!.Input.Dismiss();
            RateAndTagWindowFixture.Escape(window);
            Assert.Equal(QuickTagPopoverCloseReason.Closed, editor.Reason);
            Assert.True(card.IsFocused);

            Assert.True(page.ToggleReaderViewOption("reader.view.hide-media"));
            await page.WaitForReaderViewAsync();
            posts.SelectedItem = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == picture.Id && row.HasMediaHiddenLine);
            Assert.Equal(Texts.T("reader.sidebar.hidden", picture.Id), sidebar.StatusText);
            Assert.False(detail.IsEffectivelyVisible);
            Assert.False(sidebar.Open.IsEffectivelyVisible);

            PostFilterRule rule = new(Guid.NewGuid(), true, PostFilterField.Text, PostFilterMatch.Contains,
                "Text only", PostFilterAction.Stub, false, null, null);
            page.PostFilter.Update(new(1, HiddenPostsAction.Stub, [rule], [], PostFilterStoreState.Ready));
            page.RequestProjectionRebuild();
            await page.WaitForReaderViewAsync();
            posts.SelectedItem = Assert.Single(posts.Items.OfType<ArchivePostRow>(), row => row.Id == text.Id && row.IsStub);
            Assert.Equal(Texts.T("reader.sidebar.hidden", text.Id), sidebar.StatusText);
            Assert.False(detail.IsEffectivelyVisible);
        }
        finally { QuickTagPopover.Current?.Close(); window.Close(); page.EndLifetime(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InfoClicksRecoverLegacyProfilesAndKeepBothHostActionRows(bool mediaPage, bool legacyOff) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            using var media = BooruComponentDispatcher.Wait(ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false));
            var thread = RateAndTagWindowFixture.View(media);
            BooruComponentDispatcher.Wait(RateAndTagWindowFixture.SeedAsync(profile.Path, thread));
            Config settings = new() { MediaSidebarInfo = legacyOff ? "Off" : "Auto",
                ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways, ReaderMediaSidebarMinimumWidth = 800 };
            var preferences = ReaderMediaPreferences.From(settings);
            Window window = new() { Width = 1600, Height = 850 };
            Task<ArchiveReader> OpenReader(CancellationToken token) => ArchiveReader.OpenAsync(profile.Path, token);
            if (mediaPage)
            {
                MediaPage page = new(OpenReader) { IsAvailable = true, SavePanelSetting = update => update(settings) };
                try
                {
                    page.ConfigurePanelInfo(preferences);
                    window.Content = page;
                    window.Show();
                    BooruComponentDispatcher.Wait(page.ActivateAsync());
                    page.Grid.SelectedIndex = 0;
                    if (!page.PageLayout.Panel) Click(window, page.PanelToggleButton);
                    BooruComponentDispatcher.WaitUntil(() => page.IsPanelShown && page.PanelRow != null, "selected Media tile");
                    BooruComponentDispatcher.Wait(page.PanelTask);
                    var sidebar = Assert.IsType<ReaderMediaSidebar>(page.Panel);
                    CheckFacts(window, sidebar, legacyOff);
                    CheckTagExpansion(window, sidebar, () => page.MediaMenu, settings);
                }
                finally { window.Close(); page.EndLifetime(); }
            }
            else
            {
                ArchivePage page = new(OpenReader, (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
                bool marked = false;
                List<GalleryEntry> writes = [];
                page.IsArchiveFavorite = _ => marked;
                page.ToggleArchiveFavoriteAsync = entry => { writes.Add(entry); marked = !marked; return Task.CompletedTask; };
                page.SaveReaderMediaSwitches = (update, _) => { update(settings); return Task.CompletedTask; };
                try
                {
                    page.ApplyReaderMediaPreferences(preferences);
                    window.Content = page;
                    window.Show();
                    BooruComponentDispatcher.Wait(page.LoadThreadAsync(thread.Summary.Id));
                    page.FindControl<ListBox>("ArchivePosts")!.SelectedIndex = 0;
                    BooruComponentDispatcher.Wait(page.MediaLoadTask);
                    BooruComponentDispatcher.Wait(page.MediaSidebarTask);
                    ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
                    Assert.True(page.IsMediaSidebarShown);
                    CheckFacts(window, sidebar, legacyOff);
                    Assert.True(sidebar.FavoriteButton.IsEffectivelyEnabled);
                    Click(window, sidebar.FavoriteButton);
                    Assert.Equal(media.Asset.AssetId, Assert.Single(writes).Asset.AssetId);
                    Assert.True(sidebar.FavoriteButton.IsChecked);
                    CheckTagExpansion(window, sidebar, () => page.ArchiveMediaMenu, settings);
                }
                finally { window.Close(); page.EndLifetime(); }
            }
            Assert.Equal(!legacyOff, settings.SidebarFactsVisible);
        });

    [Fact]
    public void ReaderWithoutASavedCopyExplainsItsDisabledFavorite() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using var media = BooruComponentDispatcher.Wait(ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false));
        var thread = RateAndTagWindowFixture.View(media);
        using TempDirectory profile = new();
        BooruComponentDispatcher.Wait(RateAndTagWindowFixture.SeedAsync(profile.Path, thread));
        var empty = new MediaAssetSnapshot(media.Directory, MediaManifestStatus.Missing, null, null, []);
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token), (_, _) => Task.CompletedTask,
            _ => { }, (_, _) => Task.FromResult(empty)) { IsAvailable = true };
        int writes = 0;
        page.ToggleArchiveFavoriteAsync = _ => { writes++; return Task.CompletedTask; };
        page.ApplyReaderMediaPreferences(ReaderMediaPreferences.From(new Config {
            ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways, ReaderMediaSidebarMinimumWidth = 800 }));
        Window window = new() { Width = 1600, Height = 850, Content = page };
        try
        {
            window.Show();
            BooruComponentDispatcher.Wait(page.LoadThreadAsync(thread.Summary.Id));
            page.FindControl<ListBox>("ArchivePosts")!.SelectedIndex = 0;
            BooruComponentDispatcher.Wait(page.MediaLoadTask);
            BooruComponentDispatcher.Wait(page.MediaSidebarTask);
            var sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
            Assert.True(sidebar.FavoriteButton.IsEffectivelyVisible);
            Assert.False(sidebar.FavoriteButton.IsEffectivelyEnabled);
            Assert.Equal(Texts.T("archive.notSaved"), ToolTip.GetTip(sidebar.FavoriteButton));
            Click(window, sidebar.FavoriteButton);
            Assert.Equal(0, writes);
        }
        finally { window.Close(); page.EndLifetime(); }
    });

    private static void CheckFacts(Window window, ReaderMediaSidebar sidebar, bool legacyOff)
    {
        window.UpdateLayout();
        var facts = SidePanelDesignTests.ById<MediaSidebarFacts>(sidebar, "ReaderMediaSidebarFacts");
        var tags = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarTagsEmpty");
        var edit = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarEditTags");
        void Expect(bool visible)
        {
            Assert.Equal(visible, facts.IsEffectivelyVisible);
            Assert.Equal(visible, sidebar.InfoToggle.IsChecked);
            Assert.True(sidebar.DecisionRow.StarButtons.All(star => star.IsEffectivelyVisible));
            Assert.True(tags.IsEffectivelyVisible);
            Assert.True(edit.IsEffectivelyVisible);
            Assert.True(sidebar.Details.TranslatePoint(default, sidebar)!.Value.Y >=
                sidebar.Card.TranslatePoint(new Point(0, sidebar.Card.Bounds.Height), sidebar)!.Value.Y);
            Assert.True(sidebar.Details.Bounds.Height >= (visible ? 100 : 40));
        }
        Expect(!legacyOff);
        double initial = sidebar.Details.Bounds.Height;
        Click(window, sidebar.InfoToggle);
        Expect(legacyOff);
        Assert.True(legacyOff ? sidebar.Details.Bounds.Height > initial : sidebar.Details.Bounds.Height < initial);
        Click(window, sidebar.InfoToggle);
        Expect(!legacyOff);
        Assert.Equal(initial, sidebar.Details.Bounds.Height);
    }

    private static void Click(Window window, Control button)
    {
        window.UpdateLayout();
        var point = button.TranslatePoint(new Rect(button.Bounds.Size).Center, window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void CheckTagExpansion(Window window, ReaderMediaSidebar sidebar, Func<ContextMenu?> openMenu, Config settings)
    {
        string[] names = Enumerable.Range(1, 8).Select(index => "long_fixture_tag_" + index).ToArray();
        sidebar.SetTags(names, null);
        window.UpdateLayout();
        var more = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarMoreTags");
        var frame = SidePanelDesignTests.ById<Control>(sidebar, "ReaderMediaSidebarFrame");
        var expanded = SidePanelDesignTests.ById<ScrollViewer>(sidebar, "ReaderMediaSidebarExpanded");
        Assert.True(more.IsEffectivelyVisible);
        Assert.False(expanded.IsEffectivelyVisible);
        Assert.Equal(Texts.T("media.sidebar.showAllTags"), ToolTip.GetTip(more));
        Click(window, more);
        CheckRevealed();

        Click(window, SidePanelDesignTests.ById<ToggleButton>(sidebar, "ReaderMediaSidebarExpand"));
        Assert.False(expanded.IsEffectivelyVisible);
        Assert.True(frame.Focus(NavigationMethod.Tab));
        ActivateWithKeyboard("media.sidebar.showAllTags");
        CheckRevealed();
        ActivateWithKeyboard("action.hideDetails");
        Assert.False(expanded.IsEffectivelyVisible);
        CheckFocusAndPin();

        void ActivateWithKeyboard(string label)
        {
            window.KeyPressQwerty(PhysicalKey.F10, RawInputModifiers.Shift);
            window.KeyReleaseQwerty(PhysicalKey.F10, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();
            ContextMenu menu = Assert.IsType<ContextMenu>(openMenu());
            Assert.True(menu.IsOpen);
            MenuItem action = Assert.Single(menu.Items.OfType<MenuItem>(),
                item => AutomationProperties.GetAutomationId(item) == "media.sidebar.showAllTags");
            Assert.Equal(Texts.T(label), action.Header);
            Assert.True(action.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(menu.IsOpen);
        }

        void CheckRevealed()
        {
            Assert.True(expanded.IsEffectivelyVisible);
            var allTags = SidePanelDesignTests.ById<WrapPanel>(expanded, "ReaderMediaSidebarAllTags");
            Assert.Equal(names, allTags.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            CheckFocusAndPin();
        }

        void CheckFocusAndPin()
        {
            Assert.Same(frame, window.FocusManager!.GetFocusedElement());
            Assert.Null(QuickTagPopover.Current);
            Assert.False(settings.MediaSidebarPinned);
        }
    }
}
