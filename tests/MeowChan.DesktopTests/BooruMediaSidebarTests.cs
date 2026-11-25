using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class BooruMediaSidebarTests
{
    [Fact]
    public void SidebarFollowsTheCursorAndKeepsItsPictureAcrossAListRebuild() => InWindow((harness, model, view, window) =>
    {
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("101"), BooruUiFixtures.Post("102", score: 0))));
        Search(model, view);
        Focus(Hit(view, "101"));
        Hit(view, "101").RaiseEvent(KeyPress(Key.Space, Hit(view, "101")));
        window.UpdateLayout();
        Focus(Hit(view, "102"));
        window.UpdateLayout();

        Control sidebar = Sidebar(view);
        Assert.True(sidebar.IsVisible);
        Assert.Equal(Texts.T("booru.ui.post.title", "102"), ById<TextBlock>(sidebar, "BooruMediaTitle").Text);
        // Variante A, §1: “Die Fakten stehen in der Infoleiste unten.”
        Assert.Equal("0", ById<SelectableTextBlock>(sidebar, "BooruMediaScore").Text);
        Assert.Contains("2", AutomationProperties.GetName(ById<TextBlock>(sidebar, "BooruMediaPosition")));
        Assert.True(ById<CheckBox>(Hit(view, "101"), "BooruSelect:101").IsChecked);
        Assert.False(ById<CheckBox>(Hit(view, "102"), "BooruSelect:102").IsChecked);
        Image preview = ById<Image>(sidebar, "BooruMediaPreview");
        Bitmap bitmap = Assert.IsAssignableFrom<Bitmap>(preview.Source);
        double wide = ById<Control>(Hit(view, "102"), "BooruPreviewFrame:102").Bounds.Width;

        Resize(window, 900);
        Assert.True(ById<Control>(Hit(view, "102"), "BooruPreviewFrame:102").Bounds.Width < wide);
        view.SetResultView(BooruResultView.Grid, save: false);
        view.SetResultView(BooruResultView.List, save: false);
        window.UpdateLayout();

        Assert.Same(bitmap, preview.Source);
        using MemoryStream encoded = new();
        bitmap.Save(encoded);
        Assert.True(encoded.Length > 0);
    });

    [Theory]
    [InlineData(1001, 1000, 0, 0)]
    [InlineData(1000, 1000, 512, 512)]
    [InlineData(1, 2048, 1, 512)]
    [InlineData(2048, 1, 512, 1)]
    public void FallbackPreviewRespectsTheProfilePixelLimitAndBothDisplayEdges(
        int width, int height, int expectedWidth, int expectedHeight) => InWindow((harness, model, view, window) =>
    {
        Config profile = new() { BooruGalleryMaximumPixels = 1_000_000 };
        view.GalleryMaximumPixels = () => profile.BooruGalleryMaximumPixels;
        harness.Serve(pages: 1, perPage: 1);
        byte[] bytes = Png(width, height);
        harness.Previews.OnPreview = _ => bytes;
        Search(model, view);
        Settle(window);

        Image image = ById<Image>(Sidebar(view), "BooruMediaPreview");
        if (expectedWidth == 0)
            Assert.Null(image.Source);
        else
            Assert.Equal(new PixelSize(expectedWidth, expectedHeight), Assert.IsAssignableFrom<Bitmap>(image.Source).PixelSize);
        Assert.False(Hit(view, "101").IsFocused);
        Assert.Empty(harness.Previews.SampleRequests);
    });

    [Fact]
    public void FiveArrowMovesRequestOneSampleAndKeepFocusOnTheLastHit() => InWindow((harness, model, view, window) =>
    {
        SampleTime time = new(Sidebar(view));
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, Enumerable.Range(101, 6)
                .Select(id => BooruUiFixtures.Post(id.ToString(), sample: true)).ToArray())));
        harness.Previews.OnSample = (_, _) => Task.FromResult<byte[]?>(BooruUiFixtures.Png(44));
        Search(model, view);
        time.Advance(200);
        Assert.Empty(harness.Previews.SampleRequests);
        Assert.Equal(0, time.Scheduled);
        Settle(window);
        Focus(Hit(view, "101"));
        for (int id = 101; id <= 105; ++id)
        {
            Border card = Hit(view, id.ToString());
            Assert.True(card.IsFocused);
            card.RaiseEvent(KeyPress(Key.Down, card));
            if (id < 105) time.Advance(40);
        }
        Assert.True(Hit(view, "106").IsFocused);
        Image image = ById<Image>(Sidebar(view), "BooruMediaPreview");
        var preview = image.Source;
        Button stage = ById<Button>(Sidebar(view), "BooruMediaOpenGallery");
        double height = stage.Bounds.Height;
        time.Advance(199);
        Assert.Empty(harness.Previews.SampleRequests);
        Assert.Same(preview, image.Source);
        time.Advance(1);
        BooruComponentDispatcher.Wait(Sidebar(view).SampleLoad);
        Assert.NotSame(preview, Assert.IsAssignableFrom<Bitmap>(image.Source));
        window.UpdateLayout();
        Assert.Equal(new[] { "106" }, harness.Previews.SampleRequests);
        Assert.Equal(height, stage.Bounds.Height);
        Assert.True(Hit(view, "106").IsFocused);
    });

    [Fact]
    public void ClosedRestoredAndNewPagesDoNotStartSamplesAndClosingCancelsTheActiveOne() => InWindow((harness, model, view, window) =>
    {
        SampleTime time = new(Sidebar(view));
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, page == 2, BooruUiFixtures.Post("101", sample: true), BooruUiFixtures.Post("102", sample: true))));
        Search(model, view);
        Button toggle = ById<Button>(view, "BooruMediaToggle");
        Click(toggle);
        Focus(Hit(view, "102"));
        time.Advance(200);
        Assert.Empty(harness.Previews.SampleRequests);
        Assert.Equal(0, time.Scheduled);
        Click(toggle);

        int original = Assert.IsType<int>(view.SearchTabs.ActiveId);
        int copied = view.SearchTabs.Add(copyCurrent: true);
        Assert.NotEqual(original, copied);
        Assert.True(view.SearchTabs.Select(original));
        Settle(window);
        Assert.Equal(Texts.T("booru.ui.post.title", "102"), ById<TextBlock>(Sidebar(view), "BooruMediaTitle").Text);
        time.Advance(200);
        Assert.Empty(harness.Previews.SampleRequests);
        Assert.Equal(0, time.Scheduled);
        BooruComponentDispatcher.Wait(model.Search.GoToPageAsync(2));
        Settle(window);
        time.Advance(200);
        Assert.Empty(harness.Previews.SampleRequests);
        Assert.Equal(0, time.Scheduled);

        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Previews.OnSample = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { canceled.TrySetResult(); }
            return null;
        };
        Focus(Hit(view, "101"));
        time.Advance(200);
        BooruComponentDispatcher.WaitUntil(() => harness.Previews.SampleRequests.Count == 1, "an active sample");
        Task activeLoad = Sidebar(view).SampleLoad;
        Click(toggle);
        BooruComponentDispatcher.Wait(canceled.Task);
        BooruComponentDispatcher.Wait(activeLoad);
        Assert.Equal(new[] { "101" }, harness.Previews.SampleRequests);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingATabCancelsItsSampleAndALateReplyKeepsTheOtherSearchImage(bool viaWatchers) =>
        InWindow((harness, model, view, window) =>
        {
            BooruMediaSidebar sidebar = Sidebar(view);
            SampleTime time = new(sidebar);
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("101", sample: true))));
            Search(model, view);
            int original = Assert.IsType<int>(view.SearchTabs.ActiveId);
            int copied = view.SearchTabs.Add(copyCurrent: true);
            Assert.NotEqual(original, copied);
            Assert.True(view.SearchTabs.Select(original));
            Settle(window);

            TaskCompletionSource<CancellationToken> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<byte[]?> lateReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] nextSample = Png(56, 28);
            harness.Previews.OnSample = async (_, token) =>
            {
                if (!entered.TrySetResult(token)) return nextSample;
                using var registration = token.Register(() => canceled.TrySetResult());
                return await lateReply.Task; // Model a port that replies even after cancellation.
            };

            try
            {
                Focus(Hit(view, "101"));
                time.Advance(200);
                CancellationToken firstToken = BooruComponentDispatcher.Wait(entered.Task);
                Task firstLoad = sidebar.SampleLoad;
                Assert.False(firstToken.IsCancellationRequested);
                Assert.Equal("101", Assert.Single(harness.Previews.SampleRequests));

                if (viaWatchers) view.ShowWatchers();
                else Assert.True(view.SearchTabs.Select(copied));
                BooruComponentDispatcher.Wait(canceled.Task);
                Assert.True(firstToken.IsCancellationRequested);

                if (viaWatchers)
                {
                    view.CloseWatchers();
                    Assert.True(view.SearchTabs.Select(copied));
                }
                Settle(window);
                time.Advance(200);
                Assert.Equal("101", Assert.Single(harness.Previews.SampleRequests));
                Image image = ById<Image>(sidebar, "BooruMediaPreview");
                Bitmap preview = Assert.IsAssignableFrom<Bitmap>(image.Source);
                Focus(Hit(view, "101"));
                time.Advance(200);
                BooruComponentDispatcher.Wait(sidebar.SampleLoad);
                Bitmap sample = Assert.IsAssignableFrom<Bitmap>(image.Source);
                Assert.NotSame(preview, sample);
                Assert.Equal(new PixelSize(56, 28), sample.PixelSize);

                lateReply.SetResult(Png(24, 48));
                BooruComponentDispatcher.Wait(firstLoad);
                Assert.Same(sample, image.Source);
                Assert.Equal(new[] { "101", "101" }, harness.Previews.SampleRequests);
            }
            finally { lateReply.TrySetResult(null); }
        });

    [Fact]
    public void SidebarCommandsActOnlyOnItsHitAndMatchTheCardAfterSaving() => InWindow((harness, model, view, window) =>
    {
        harness.Serve(pages: 1, perPage: 2);
        List<string> shown = new();
        view.ShowInMedia = id => { shown.Add(id); return Task.FromResult(true); };
        Search(model, view);
        Focus(Hit(view, "101"));
        Hit(view, "101").RaiseEvent(KeyPress(Key.Space, Hit(view, "101")));
        Settle(window);
        Control sidebar = Sidebar(view);
        Button stale = ById<Button>(sidebar, "BooruPanelBooruLoad:101");
        Focus(Hit(view, "102"));
        Click(stale);
        Assert.Empty(harness.Queue.Loads);

        Click(ById<Button>(sidebar, "BooruPanelBooruOpenPost:102"));
        Click(ById<Button>(sidebar, "BooruPanelBooruCopyLink:102"));
        Assert.Equal("https://danbooru.donmai.us/posts/102", Assert.Single(harness.Links.Opened));
        Assert.Equal("https://danbooru.donmai.us/posts/102", Assert.Single(harness.Links.Copied));
        harness.Queue.OnLoad = (_, _) => Task.FromResult(new BooruLoadResult(BooruLoadStatus.AlreadyLoaded));
        Click(ById<Button>(sidebar, "BooruPanelBooruLoad:102"));
        BooruComponentDispatcher.WaitUntil(() => model.Search.View.Rows[1].Load == BooruRowLoad.AlreadyLoaded
            && sidebar.GetVisualDescendants().OfType<Button>().Any(button =>
                AutomationProperties.GetAutomationId(button) == "BooruPanelBooruShowInMedia:102"), "the saved hit");
        Assert.Equal(("102", BooruLoadOrigin.Search), Assert.Single(harness.Queue.Loads));
        Settle(window);
        Assert.True(ById<Button>(Hit(view, "102"), "BooruShowInMedia:102").IsEnabled);
        // Tom, 25.11. 14:48: no local rating, tag count or tag editor in the Booru sidebar, saved or not.
        AssertNoLocalEditing(sidebar);
        Click(ById<Button>(sidebar, "BooruPanelBooruShowInMedia:102"));
        Assert.Equal("102", Assert.Single(shown));
    });

    [Fact]
    public void SidebarOmitsUnknownRatingAndMissingScoreAndTagClicksOnlyEditTheDraft() => InWindow((harness, model, view, window) =>
    {
        BooruPost post = BooruUiFixtures.Post("101", rating: BooruRating.Unknown).Post;
        BooruPostView row = BooruPostView.From(post with { Fields = post.Fields & ~BooruFieldPresence.Score });
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, row)));
        Search(model, view);
        Settle(window);
        // Variante A, §1: “Die Fakten stehen in der Infoleiste unten.”
        Control facts = ById<Control>(Sidebar(view), "BooruMediaFacts");
        Assert.DoesNotContain(facts.GetVisualDescendants(), control => control is Control item
            && AutomationProperties.GetAutomationId(item) == "BooruMediaRating");
        Assert.DoesNotContain(facts.GetVisualDescendants(), control => control is Control item
            && AutomationProperties.GetAutomationId(item) == "BooruMediaScore");

        BooruMediaSidebar sidebar = Sidebar(view);
        Focus(Hit(view, "101"));
        Button blueEyes = ById<Button>(sidebar, "BooruPanelTag:blue_eyes");
        PressPointer(window, blueEyes);
        Assert.Contains("blue_eyes", model.Search.View.Input.Split(' '));
        Assert.True(Hit(view, "101").IsFocused);
        blueEyes = ById<Button>(sidebar, "BooruPanelTag:blue_eyes");
        PressPointer(window, blueEyes, RawInputModifiers.Alt);
        Assert.Equal("cat -blue_eyes", model.Search.View.Input);
        Assert.True(Hit(view, "101").IsFocused);

        Button artist = ById<Button>(sidebar, "BooruPanelTag:sky");
        PressPointer(window, artist, RawInputModifiers.Alt);
        // DanbooruTagQuery.MaximumTagTerms limits the draft to two tag terms.
        Assert.Equal("cat -blue_eyes", model.Search.View.Input);
        Assert.True(Hit(view, "101").IsFocused);

        // Tom's 1.1.2 focus rule keeps action buttons out of the tab order while source chips remain tab stops.
        Assert.True(ById<Button>(sidebar, "BooruPanelTag:cat").Focus(NavigationMethod.Tab));
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
        blueEyes = ById<Button>(sidebar, "BooruPanelTag:blue_eyes");
        Assert.True(blueEyes.IsFocused);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Settle(window);
        Assert.Equal("cat blue_eyes", model.Search.View.Input);
        Assert.True(blueEyes.IsFocused);
        Assert.Equal(1, harness.Search.Requests);
        Assert.Empty(harness.Previews.SampleRequests);
    });

    [Theory]
    [InlineData("cat", "blue_eyes", "cat blue_eyes")]
    [InlineData("cat -blue_eyes", "sky", "cat -blue_eyes")]
    public void FirstTagClickReturnsFocusWithoutRequestingASample(string draft, string tag, string expectedDraft) =>
        InWindow((harness, model, view, window) =>
        {
            BooruMediaSidebar sidebar = Sidebar(view);
            SampleTime time = new(sidebar);
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("101", sample: true))));
            Search(model, view);
            model.Search.SetInput(draft);
            Settle(window);
            Assert.Null(view.CursorPostId);

            PressPointer(window, ById<Button>(sidebar, "BooruPanelTag:" + tag));

            Assert.True(Hit(view, "101").IsFocused);
            Assert.Equal("101", view.CursorPostId);
            // DanbooruTagQuery.MaximumTagTerms limits the draft to two tag terms.
            Assert.Equal(expectedDraft, model.Search.View.Input);
            time.Advance(200);
            BooruComponentDispatcher.Wait(sidebar.SampleLoad);
            Assert.Empty(harness.Previews.SampleRequests);
            Assert.Equal(1, harness.Search.Requests);
        });

    [Theory]
    [InlineData("de")]
    [InlineData("ru")]
    public void SidebarKeepsAFullActionRowAndEnoughResultsWidth(string language) => InWindow((harness, model, view, window) =>
    {
        Texts.SetLanguage(language);
        harness.Serve(pages: 1, perPage: 2);
        Search(model, view);
        foreach (BooruResultView mode in new[] { BooruResultView.List, BooruResultView.Grid })
        {
            view.SetResultView(mode, save: false);
            // W112-74 (Tom): “feste Breite 360 DIP”.
            foreach (int width in new[] { 900, 1152, 1840 })
            {
                Resize(window, width);
                Control sidebar = Sidebar(view);
                Assert.True(sidebar.IsVisible);
                Assert.Equal(360, sidebar.Bounds.Width);
                ScrollViewer results = ById<ScrollViewer>(view, "BooruResultScroll");
                Assert.True(results.Viewport.Width >= 480);
                Assert.True(results.Extent.Width <= results.Viewport.Width + 1);
                Control actions = ById<Control>(sidebar, "BooruMediaActions");
                Button[] buttons = new[] { "BooruLoad", "BooruOpenPost", "BooruCopyLink" }
                    .Select(action => ById<Button>(sidebar, $"BooruPanel{action}:101")).ToArray();
                Rect[] bounds = buttons.Select(button => In(button, actions)).ToArray();
                Assert.Single(bounds.Select(button => button.Y).Distinct());
                Assert.All(buttons, button =>
                {
                    Assert.False(button.Focusable);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                    Assert.InRange(In(button, actions).Right, 0, actions.Bounds.Width);
                    AssertActionLabelFits(button, requireLabel: false);
                });

                // Variante A, §2: “Icon zuerst, Label nur bei Platz”. Give the labels room to check the secondary buttons too;
                // Tom, 25.11. 14:55: “das kleine Icon ‚Im Browser öffnen‘” stays an icon at any width.
                if (width == 1840)
                {
                    sidebar.Width = 800;
                    Settle(window);
                    Assert.All(new[] { buttons[0], buttons[2] }, button => AssertActionLabelFits(button, requireLabel: true));
                    Assert.False(ById<TextBlock>(buttons[1], "BooruPanelBooruOpenPost:101:Label").IsEffectivelyVisible);
                    Assert.Equal(buttons[1].Bounds.Height, buttons[1].Bounds.Width);
                    sidebar.Width = 360;
                    Settle(window);
                }
            }
        }
        Resize(window, 800);
        Assert.False(Sidebar(view).IsVisible);
        Resize(window, 1152);
        Assert.True(Sidebar(view).IsVisible);
        view.SetResultView(BooruResultView.Gallery, save: false);
        Assert.False(Sidebar(view).IsVisible);
        Assert.False(ById<Button>(view, "BooruMediaToggle").IsVisible);
    });

    [Fact]
    public void SidebarLinkButtonsFollowTheWorkViewOverflowPolicy() => InWindow((harness, model, view, window) =>
    {
        view.ApplyWorkView(WorkView.Barebones, viewChanged: true);
        harness.Serve(pages: 1, perPage: 1);
        Search(model, view);
        Settle(window);

        Control sidebar = Sidebar(view);
        Button load = ById<Button>(sidebar, "BooruPanelBooruLoad:101");
        Button open = ById<Button>(sidebar, "BooruPanelBooruOpenPost:101");
        Button copy = ById<Button>(sidebar, "BooruPanelBooruCopyLink:101");
        // WorkViewPolicy.Booru assigns load to Direct and both link actions to Overflow in Barebones.
        Assert.True(load.IsEffectivelyVisible);
        Assert.False(open.IsEffectivelyVisible);
        Assert.False(copy.IsEffectivelyVisible);

        view.ApplyWorkView(WorkView.Advanced, viewChanged: true);
        Assert.True(open.IsEffectivelyVisible);
        Assert.True(copy.IsEffectivelyVisible);

        view.ApplyWorkView(WorkView.Barebones, viewChanged: true);
        Assert.True(load.IsEffectivelyVisible);
        Assert.False(open.IsEffectivelyVisible);
        Assert.False(copy.IsEffectivelyVisible);
    });

    [Fact]
    public void SavedBooruShortcutTakesPrecedenceOverTheMacSidebarFactsGestureInBooru()
    {
        const string facts = "media.sidebar.info";
        const string tileSize = "media.tile-size.reset";
        ShortcutGesture gesture = ShortcutGesture.Parse("Shift+I");
        KeyEventArgs key = new() { Key = Key.I, KeyModifiers = KeyModifiers.Shift };
        EffectiveShortcutBindings defaults = EffectiveShortcutBindings.Build(new ShortcutBindingsSection(), ShortcutPlatform.MacOS);
        Assert.True(defaults.Matches(key, facts, "booru"));

        ShortcutBindingsSection profile = new ShortcutBindingsSection().Set(tileSize, [gesture]);
        EffectiveShortcutBindings saved = EffectiveShortcutBindings.Build(profile, ShortcutPlatform.MacOS);
        Assert.True(saved.Matches(key, tileSize, "booru"));
        Assert.False(saved.Matches(key, facts, "booru"));
        Assert.True(saved.Matches(key, facts, "reader"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DockedFactsToggleWithoutHidingActionsOrCoveringTheMedia(bool grid) => InWindow((harness, model, view, window) =>
    {
        Texts.SetLanguage("en");
        harness.Serve(pages: 1, perPage: 2);
        Search(model, view);
        if (grid) view.SetResultView(BooruResultView.Grid, save: false);
        window.Activate();
        Settle(window);
        BooruMediaSidebar sidebar = Sidebar(view);
        ToggleButton info = ById<ToggleButton>(sidebar, "BooruMediaInfo");
        Control facts = ById<Control>(sidebar, "BooruMediaFacts");
        Control actions = ById<Control>(sidebar, "BooruMediaActions");
        Control tags = ById<Control>(sidebar, "BooruMediaTags");
        Control media = ById<Control>(sidebar, "BooruMediaOpenGallery");
        Control close = ById<Control>(sidebar, "BooruMediaClose");
        TextBlock position = ById<TextBlock>(sidebar, "BooruMediaPosition");
        Assert.Equal("1 / 2", position.Text);
        Assert.Contains("101.png", ById<TextBlock>(sidebar, "BooruMediaSummary").Text);
        Assert.Contains(SizeText.Format(1_234_567), ById<TextBlock>(sidebar, "BooruMediaSummary").Text);
        // Tom, 25.11. 14:55: “Kopf mit Post-ID und Provider”. With the provider the meta line may take the header's second
        // row (as in Tom's screenshot); the position stays on that line, inside the header and above the media.
        Assert.Equal(In(ById<TextBlock>(sidebar, "BooruMediaSummary"), sidebar).Center.Y, In(position, sidebar).Center.Y, precision: 0);
        Assert.True(In(position, sidebar).Top >= In(info, sidebar).Top);
        Assert.True(In(position, sidebar).Bottom <= In(media, sidebar).Top);
        Assert.True(In(media, sidebar).Height > 0);
        Assert.True(In(media, sidebar).Bottom <= In(actions, sidebar).Top);
        Assert.True(In(tags, sidebar).Bottom <= In(facts, sidebar).Top);
        Assert.True(info.IsChecked);
        Assert.True(facts.IsEffectivelyVisible);
        PressPointer(window, info);
        Assert.False(info.IsChecked);
        Assert.False(facts.IsEffectivelyVisible);
        Assert.All(new[] { close, media, actions, tags }, control => Assert.True(control.IsEffectivelyVisible));

        Border hit = Hit(view, "101");
        Focus(hit);
        KeyEventArgs key = KeyPress(Key.I, hit, KeyModifiers.Shift);
        hit.RaiseEvent(key);
        Assert.True(key.Handled);
        Assert.True(facts.IsEffectivelyVisible);
        Assert.True(info.IsChecked);
        Assert.True(hit.IsFocused);

        // Tom, 25.11. 14:48: the disabled local stars, the tag count "0" and "Edit tags…" were useless here and are gone.
        AssertNoLocalEditing(sidebar);
        Assert.True(ById<Button>(sidebar, "BooruPanelTag:blue_eyes").IsEffectivelyEnabled);

        foreach (int width in new[] { 280, 320 })
        {
            sidebar.Width = width;
            Settle(window);
            Assert.True(close.IsEffectivelyVisible);
            Assert.InRange(In(close, sidebar).Right, 1, width);
            Assert.True(In(media, sidebar).Top >= In(position, sidebar).Bottom);
        }
    });

    [Fact]
    public void CloseAndEscapeReturnToTheHitAndThePreferenceSurvivesConfigurationRoundTrips() => InWindow((harness, model, view, window) =>
    {
        harness.Serve(pages: 1, perPage: 2);
        Search(model, view);
        Assert.True(new Config().BooruMediaSidebar);
        Assert.True(JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!.BooruMediaSidebar);
        Config config = new();
        view.SaveMediaSidebar = open => config.BooruMediaSidebar = open;
        Focus(Hit(view, "102"));
        Button tag = ById<Button>(Sidebar(view), "BooruPanelTag:blue_eyes");
        Focus(tag);
        tag.RaiseEvent(KeyPress(Key.Escape, tag));
        Assert.False(Sidebar(view).IsVisible);
        Assert.False(config.BooruMediaSidebar);
        Assert.True(Hit(view, "102").IsFocused);

        Config copied = new();
        copied.CopyFrom(config.Clone());
        string json = SettingsTransfer.Export(copied, "1.1.2").ToJsonString();
        Config restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)!;
        Assert.False(restored.BooruMediaSidebar);
        view.RestoreMediaSidebar(restored.BooruMediaSidebar);
        view.SetResultView(BooruResultView.Grid, save: false);
        Assert.False(Sidebar(view).IsVisible);
        Click(ById<Button>(view, "BooruMediaToggle"));
        Settle(window);
        Focus(ById<Button>(Sidebar(view), "BooruPanelTag:blue_eyes"));
        Button close = ById<Button>(Sidebar(view), "BooruMediaClose");
        Assert.False(close.Focusable);
        PressPointer(window, close);
        Assert.False(Sidebar(view).IsVisible);
        Assert.True(Hit(view, "102").IsFocused);
        Assert.False(config.BooruMediaSidebar);
    });

    /// <summary>No visible local rating, favorite, tag count or tag editor anywhere in the Booru sidebar.</summary>
    internal static void AssertNoLocalEditing(Control sidebar)
    {
        string[] local = ["BooruMediaLocalActions", "BooruMediaEditTags", "BooruMediaFavorite", "ReaderMediaSidebarInfoBar",
            "ReaderMediaSidebarTagsEmpty", "ReaderMediaSidebarTagsToggle", "ReaderMediaSidebarTags", "ReaderMediaSidebarExpand"];
        Assert.DoesNotContain(sidebar.GetVisualDescendants().OfType<Control>(), control => control.IsEffectivelyVisible
            && AutomationProperties.GetAutomationId(control) is { } id
            && (local.Contains(id) || id.StartsWith("BooruMediaDecision", StringComparison.Ordinal)));
    }

    internal static void InWindow(Action<BooruUiHarness, BooruWorkbenchModel, BooruWorkbenchView, Window> test) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruUiHarness harness = new();
            using BooruWorkbenchModel model = new(harness.Services);
            using BooruWorkbenchView view = new(model, ownsModel: false);
            view.RestoreMediaSidebar(open: true);
            Window window = new() { Content = view, Width = 1840, Height = 920 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                test(harness, model, view, window);
            }
            finally { window.Close(); }
        });

    internal static void Search(BooruWorkbenchModel model, BooruWorkbenchView view)
    {
        BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(model.Search, "cat"));
        BooruComponentDispatcher.WaitUntil(() => !model.Search.View.Rows.IsEmpty
            && model.Search.View.Rows.All(row => row.Preview == BooruPreviewState.Ready)
            && Sidebar(view).IsVisible, "the sidebar and result previews");
    }

    internal static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    internal static void Resize(Window window, int width)
    {
        BooruReviewTestPlatform.Resize(window, width);
        Settle(window);
    }

    internal static BooruMediaSidebar Sidebar(BooruWorkbenchView view) => ById<BooruMediaSidebar>(view, "BooruMediaSidebar");

    internal static T ById<T>(Control root, string id) where T : Control => SidePanelDesignTests.ById<T>(root, id);

    internal static Rect In(Control control, Control root) => new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);

    internal static void AssertActionLabelFits(Button button, bool requireLabel)
    {
        TextBlock label = ById<TextBlock>(button, AutomationProperties.GetAutomationId(button) + ":Label");
        if (!label.IsEffectivelyVisible)
        {
            Assert.False(requireLabel, $"The action label '{label.Text}' should be visible when there is room.");
            // Tom, 25.11. 14:55: the labeled actions share the row's width, so an icon-only one keeps at least a square.
            Assert.True(button.Bounds.Width >= button.Bounds.Height - 0.5, $"{button.Name} is narrower than its height.");
            return;
        }

        Assert.InRange(In(label, button).Right, 0, button.Bounds.Width - button.BorderThickness.Right - button.Padding.Right + 0.5);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void PressPointer(Window window, Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        AvaloniaSynchronizationContext.InstallIfNeeded();
        Settle(window);
        Point point = default;
        Visual? hit = null;
        BooruComponentDispatcher.WaitUntil(() =>
        {
            window.UpdateLayout();
            control.BringIntoView();
            window.UpdateLayout();
            point = control.TranslatePoint(new Rect(control.Bounds.Size).Center, window)!.Value;
            hit = window.InputHitTest(point) as Visual;
            return hit?.GetSelfAndVisualAncestors().Contains(control) == true;
        }, $"a pointer hit on {AutomationProperties.GetAutomationId(control)}");
        Control? target = hit?.GetSelfAndVisualAncestors().OfType<Control>()
            .FirstOrDefault(ancestor => AutomationProperties.GetAutomationId(ancestor) != null);
        Assert.True(hit is not null && hit.GetSelfAndVisualAncestors().Contains(control),
            $"Pointer at {point} hit {hit} in {target?.Name} instead of {control.Name} at {In(control, window)}.");
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Settle(window);
    }

    private static void Focus(Control control) => Assert.True(control.Focus(NavigationMethod.Directional));

    internal static Border Hit(BooruWorkbenchView view, string id) => ById<Border>(view, "BooruResult:" + id);

    private static byte[] Png(int width, int height)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static KeyEventArgs KeyPress(Key key, Control source, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = source };

    private sealed class SampleTime
    {
        private readonly List<(TimeSpan Due, TaskCompletionSource Ready)> _waiting = new();
        private TimeSpan _now;
        internal int Scheduled { get; private set; }

        internal SampleTime(BooruMediaSidebar sidebar) => sidebar.SampleDelayAsync = WaitAsync;

        private async Task WaitAsync(TimeSpan delay, CancellationToken token)
        {
            ++Scheduled;
            TaskCompletionSource ready = new();
            using var registration = token.Register(() => ready.TrySetCanceled(token));
            _waiting.Add((_now + delay, ready));
            await ready.Task;
        }

        internal void Advance(int milliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            _now += TimeSpan.FromMilliseconds(milliseconds);
            var due = _waiting.Where(wait => wait.Due <= _now).ToArray();
            _waiting.RemoveAll(wait => wait.Due <= _now || wait.Ready.Task.IsCompleted);
            foreach (var wait in due) wait.Ready.TrySetResult();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
