using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.PluginHost.Http;
using SkiaSharp;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>
/// F13a and F13 stage 1: the inline Danbooru gallery (third result view), the cursor of the list and the grid, and the progressive loading of
/// the open post's sample. The window tests use the scoped headless platform and fake provider ports.
/// </summary>
[Collection("Localization")]
public sealed class BooruGalleryInlineTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int MiB = 1024 * 1024;

    [Fact]
    public void TheTwoGallerySettingsHaveDefaultMinimumAndMaximumAndAreValidated()
    {
        Assert.Equal((8, 1, 32), (new Config().BooruGallerySampleMiB, Config.BooruGallerySampleMiBMinimum, Config.BooruGallerySampleMiBMaximum));
        Assert.Equal((32, 8, 256), (new Config().BooruGalleryCacheMiB, Config.BooruGalleryCacheMiBMinimum, Config.BooruGalleryCacheMiBMaximum));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruGallerySampleMiB = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruGallerySampleMiB = 33 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruGalleryCacheMiB = 7 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config { BooruGalleryCacheMiB = 257 }.Validate());
        Config copy = new() { BooruGallerySampleMiB = 3, BooruGalleryCacheMiB = 64 };
        Config clone = copy.Clone();
        Assert.Equal((3, 64), (clone.BooruGallerySampleMiB, clone.BooruGalleryCacheMiB));
    }

    [Fact]
    public void TheSavedViewKnowsTheGalleryAndTheThreeWaySwitchFollowsItsIndex()
    {
        Assert.Equal(BooruResultView.Gallery, BooruResultViews.Parse("gallery"));
        Assert.Equal(BooruResultView.List, BooruResultViews.Parse("Mosaic"));
        Assert.Equal(new Config { BooruPageView = "Gallery" }.BooruPageView, "Gallery");

        ThreeWayToggle toggle = new();
        List<int> seen = new();
        toggle.SelectionChanged += (_, _) => seen.Add(toggle.SelectedIndex);
        toggle.SelectedIndex = 2;
        toggle.SelectedIndex = 2;
        toggle.SelectedIndex = 9;
        toggle.SelectedIndex = -4;
        Assert.Equal(new[] { 2, 0 }, seen);
        Assert.True(toggle.First.IsChecked);
    }

    [Fact]
    public void TheStatusLineNamesPositionRatingPictureSampleAndOriginal() => OnDispatcher(() =>
    {
        BooruResultRowView row = new(BooruUiFixtures.Post("55", sample: true, rating: BooruRating.Questionable), Preview: BooruPreviewState.Ready);
        string loading = BooruGalleryStatus.Line(row, 2, 20, BooruGalleryPicture.Preview, BooruGallerySampleState.Loading, 8);
        Assert.Equal("Post #55 · 3 of 20 · Danbooru rating: Questionable · Shown: preview · Larger version loading… · Original available", loading);
        Assert.Contains("Shown: reduced-size version (sample)", BooruGalleryStatus.Line(row, 0, 1, BooruGalleryPicture.Sample, BooruGallerySampleState.Shown, 8));
        Assert.Contains("larger than 8 MiB", BooruGalleryStatus.Line(row, 0, 1, BooruGalleryPicture.Preview, BooruGallerySampleState.Failed, 8));
        Assert.Contains("offers no reduced-size version", BooruGalleryStatus.Line(row, 0, 1, BooruGalleryPicture.Preview, BooruGallerySampleState.NotOffered, 8));
        Assert.Contains("Loading preview", BooruGalleryStatus.Line(row with { Preview = BooruPreviewState.Loading }, 0, 1, BooruGalleryPicture.None, BooruGallerySampleState.Loading, 8));
        return Task.CompletedTask;
    });

    [Fact]
    public void ThePostViewReadsTheSampleUrlOnlyWhenTheProviderOffersOne()
    {
        Assert.Equal(new Uri("https://cdn.donmai.us/sample/9.jpg"), BooruUiFixtures.Post("9", sample: true).SampleUrl);
        Assert.Null(BooruUiFixtures.Post("9").SampleUrl);
    }

    [Fact]
    public void TheGalleryShowsThePreviewAtOnceThenTheSampleAndLeavingAPostCancelsItsSample() => OnDispatcher(async () =>
    {
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
            using WindowScope window = new(view);
            using (model) using (view)
            {
                bool secondCanceled = false;
                harness.Previews.OnSample = async (post, token) =>
                {
                    if (post.PostId == "101") return BooruUiFixtures.Png(7);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { secondCanceled = true; throw; }
                    return null;
                };
                List<BooruResultView> saved = new();
                view.SaveResultView = saved.Add;
                List<Border> cards = await SearchAsync(harness, model, view);

                // Nothing is fetched for a page ahead: the list and the grid never ask for a sample.
                view.SetResultView(BooruResultView.Grid, save: false);
                view.SetResultView(BooruResultView.List, save: false);
                Assert.Empty(harness.Previews.SampleRequests);

                // Enter on a hit that is not saved opens the gallery on it (not saved as the person's choice); the preview is on the stage at once.
                cards = Cards(view);
                KeyEventArgs enter = Press(Key.Enter, cards[0]);
                cards[0].RaiseEvent(enter);
                Assert.True(enter.Handled);
                Assert.Equal(BooruResultView.Gallery, view.ResultView);
                Assert.Empty(saved);
                BooruGalleryPanel gallery = view.Gallery;
                Assert.Equal("101", gallery.CurrentPostId);
                Assert.Equal("101", view.CursorPostId);
                Assert.Same(gallery, Results(view).Children.Single());
                Assert.Equal(BooruGalleryPicture.Preview, gallery.Picture);
                Assert.Contains("1 of 3", gallery.StatusText);

                await BooruTestSupport.WaitAsync(() => gallery.Picture == BooruGalleryPicture.Sample, "the sample of 101");
                Assert.Equal(BooruGallerySampleState.Shown, gallery.SampleState);
                Assert.Contains("reduced-size version", gallery.StatusText);
                Assert.Equal(new[] { "101" }, harness.Previews.SampleRequests);
                // The limits are the settings' defaults, in bytes.
                Assert.Equal((8 * MiB, 32L * MiB), harness.Previews.SampleLimits[0]);

                // Next: the preview of 102 first, its sample is asked for; moving on cancels it and 103 (no sample offered) asks for nothing.
                KeyEventArgs right = Press(Key.Right, gallery.Stage);
                gallery.Stage.RaiseEvent(right);
                Assert.True(right.Handled);
                Assert.Equal("102", gallery.CurrentPostId);
                Assert.Equal(BooruGalleryPicture.Preview, gallery.Picture);
                await BooruTestSupport.WaitAsync(() => harness.Previews.SampleRequests.Count == 2, "the request of 102");
                Assert.Equal(BooruGallerySampleState.Loading, gallery.SampleState);
                gallery.Stage.RaiseEvent(Press(Key.Right, gallery.Stage));
                Assert.Equal("103", gallery.CurrentPostId);
                await BooruTestSupport.WaitAsync(() => secondCanceled, "the cancellation of the sample of 102");
                Assert.Equal(BooruGallerySampleState.NotOffered, gallery.SampleState);
                Assert.Contains("offers no reduced-size version", gallery.StatusText);
                Assert.Equal(new[] { "101", "102" }, harness.Previews.SampleRequests);
                Assert.False(gallery.NextButton.IsEnabled);
                Assert.True(gallery.PreviousButton.IsEnabled);

                // Escape goes back to the view the person came from; the cursor stays on the post.
                gallery.Stage.RaiseEvent(Press(Key.Escape, gallery.Stage));
                Assert.Equal(BooruResultView.List, view.ResultView);
                Assert.Null(gallery.CurrentPostId);
                Assert.Equal("103", view.CursorPostId);
                Assert.Contains(Cards(view), card => card.Name == "BooruResult:103" && card.Classes.Contains("booru-cursor"));
                Assert.Single(Cards(view), card => card.Classes.Contains("booru-cursor"));
                Assert.Empty(saved);

                // The switch is the person's choice and is saved.
                view.ViewToggle.SelectedIndex = 2;
                Assert.Equal(new[] { BooruResultView.Gallery }, saved);
                Assert.Equal("103", gallery.CurrentPostId);
            }
        }
    });

    [Fact]
    public void ASampleThatCannotBeLoadedLeavesThePreviewAndSaysWhy() => OnDispatcher(async () =>
    {
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
            using WindowScope window = new(view);
            using (model) using (view)
            {
                harness.Previews.OnSample = (_, _) => Task.FromResult<byte[]?>(null); // refused by the limit or not delivered
                view.GallerySampleMiB = () => 2;
                view.GalleryCacheMiB = () => 16;
                await SearchAsync(harness, model, view);
                Assert.True(view.OpenGallery("101"));
                await BooruTestSupport.WaitAsync(() => view.Gallery.SampleState == BooruGallerySampleState.Failed, "the failed sample");
                Assert.Equal(BooruGalleryPicture.Preview, view.Gallery.Picture);
                Assert.Contains("larger than 2 MiB", view.Gallery.StatusText);
                Assert.Equal((2 * MiB, 16L * MiB), harness.Previews.SampleLimits[0]);
            }
        }
    });

    [Fact]
    public void TheProjectionDiscardsOnlyDisplayUrisEqualToTheOriginal()
    {
        Uri original = new("https://cdn.donmai.us/original/a.jpg");
        Uri alias = new("https://CDN.DONMAI.US:443/original/a.jpg");
        Uri distinct = new("https://cdn.donmai.us/original/A.jpg");
        var post = BooruUiFixtures.Post("9").Post;
        BooruPostView projected = BooruPostView.From(post with
        {
            Media = [new(BooruMediaRole.Original, original), new(BooruMediaRole.Preview, alias), new(BooruMediaRole.Sample, alias)]
        });
        Assert.Null(projected.PreviewUrl);
        Assert.Null(projected.SampleUrl);
        Assert.Equal(original, projected.Original!.Url);
        Assert.Equal(distinct, BooruPostView.From(post with
        {
            Media = [new(BooruMediaRole.Original, original), new(BooruMediaRole.Sample, distinct)]
        }).SampleUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewerClosesSafelyWhenItsSearchHasNoRows(bool openBlankTab) => OnDispatcher(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        using (WindowScope window = new(view))
        {
            await SearchAsync(harness, model, view);
            Assert.True(view.OpenGallery("102"));
            Assert.True(view.OpenResultViewer("102"));
            var popup = Assert.IsType<BooruResultViewerWindow>(typeof(BooruWorkbenchView).GetField("_resultViewer", Private)!.GetValue(view));
            if (openBlankTab) view.SearchTabs.Add(copyCurrent: false);
            else
            {
                harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true)));
                await BooruTestSupport.SearchAsync(model.Search, "dog");
            }
            await BooruTestSupport.WaitAsync(() => !popup.IsVisible && view.Gallery.CurrentPostId is null, "the empty gallery and closed popup");
            Assert.Empty(model.Search.View.Rows);
        }
    });

    [Fact]
    public void WatchersCancelDelayedSamplesAndAHiddenShellPageCancelsAnActiveSample() => OnDispatcher(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        Border shellHost = new() { Child = view };
        using (model)
        using (view)
        using (WindowScope window = new(shellHost))
        {
            TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Previews.OnSample = async (_, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { canceled.TrySetResult(); }
                return null;
            };
            await SearchAsync(harness, model, view);
            TabControl tabs = ControlField<TabControl>(view, "_tabs");
            view.Gallery.SampleDelay = TimeSpan.FromMilliseconds(150);
            Assert.True(view.OpenGallery("101"));
            await BooruTestSupport.WaitAsync(() => view.Gallery.SampleState == BooruGallerySampleState.Loading, "the pending sample delay");
            view.ShowWatchers();
            await BooruTestSupport.WaitAsync(() => view.Gallery.SampleState == BooruGallerySampleState.Deferred, "the canceled sample delay");
            await Task.Delay(200);
            Assert.Empty(harness.Previews.SampleRequests);

            view.Gallery.SampleDelay = TimeSpan.Zero;
            tabs.SelectedItem = ControlField<TabItem>(view, "_searchTab");
            await BooruTestSupport.WaitAsync(() => harness.Previews.SampleRequests.Count == 1, "a sample after returning to Search");
            shellHost.IsVisible = false;
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shellHost.IsVisible = true;
            await BooruTestSupport.WaitAsync(() => harness.Previews.SampleRequests.Count == 2, "a sample after returning to the shell page");
            Assert.Equal(new[] { "101", "101" }, harness.Previews.SampleRequests);
        }
    });

    [Fact]
    public void RestoringTabsInGalleryUsesTheSnapshotAndDoesNotRequestASample() => OnDispatcher(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        using (WindowScope window = new(view))
        {
            await SearchAsync(harness, model, view);
            Assert.True(view.OpenGallery("101"));
            await BooruTestSupport.WaitAsync(() => harness.Previews.SampleRequests.Count == 1, "the initial gallery sample");
            int previews = harness.Previews.Requests;
            int searches = harness.Search.Requests;
            int tab = view.SearchTabs.Add(copyCurrent: true);
            await BooruTestSupport.WaitAsync(() => view.Gallery.CurrentPostId == "101", "the copied gallery");
            view.SearchTabs.Select(BooruSearchTabs.PrimaryId);
            view.SearchTabs.Select(tab);
            await BooruTestSupport.WaitAsync(() => view.Gallery.CurrentPostId == "101", "the restored gallery");
            Assert.Equal(BooruGalleryPicture.Preview, view.Gallery.Picture);
            Assert.Single(harness.Previews.SampleRequests);
            Assert.Equal(previews, harness.Previews.Requests);
            Assert.Equal(searches, harness.Search.Requests);
        }
    });

    [Fact]
    public void DownloadAllCanBeStoppedOnlyInTheTabThatStartedItEvenForTheSameQuery() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        {
            await SearchAsync(harness, model, view);
            harness.Search.OnSearch = async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            };
            Button button = ControlField<Button>(view, "_downloadAll");
            Border status = ControlField<Border>(view, "_bulkHost");
            Task bulk = model.Search.DownloadAllAsync();
            await BooruTestSupport.WaitAsync(() => status.IsVisible && button.IsEnabled, "the original tab's cancel button");
            view.SearchTabs.Add(copyCurrent: true);
            await BooruTestSupport.WaitAsync(() => !status.IsVisible && !button.IsEnabled, "the copy's disabled download button");
            Assert.Equal(Texts.T("booru.tabs.downloadShort"), button.Content);
            Assert.StartsWith(Texts.T("booru.bulk.button"), Assert.IsType<string>(ToolTip.GetTip(button)));
            model.Search.CancelBulk();
            Assert.False(bulk.IsCompleted);

            view.SearchTabs.Select(BooruSearchTabs.PrimaryId);
            await BooruTestSupport.WaitAsync(() => status.IsVisible && button.IsEnabled, "the restored cancel button");
            Assert.Equal(Texts.T("booru.bulk.stop"), button.Content);
            model.Search.CancelBulk();
            await bulk.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BooruBulkEnd.Canceled, model.Search.Bulk.End);
        }
    });

    [Fact]
    public void TheGalleryKeepsItsPreviewAndReportsThePixelLimitAndResponseDeadline() => OnDispatcher(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        using (WindowScope window = new(view))
        {
            using SKBitmap bitmap = new(1024, 1024);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] bytes = png.ToArray();
            view.Gallery.Decoder = null;
            view.GalleryMaximumPixels = () => 1_000_000;
            view.GalleryMaximumWidth = () => 300;
            view.GalleryTimeoutSeconds = () => 3;
            harness.Previews.SampleResult = timeout =>
            {
                Assert.Equal(TimeSpan.FromSeconds(3), timeout);
                return new(bytes);
            };

            await SearchAsync(harness, model, view);
            view.OpenGallery("101");
            await BooruTestSupport.WaitAsync(() => view.Gallery.SampleState == BooruGallerySampleState.PixelLimit, "the pixel limit");
            Assert.Equal(BooruGalleryPicture.Preview, view.Gallery.Picture);
            Assert.Contains(Texts.T("booru.gallery.sample.pixelLimit", 1_000_000), view.Gallery.StatusText);

            view.GalleryMaximumPixels = () => 2_000_000;
            view.OpenGallery("102");
            await BooruTestSupport.WaitAsync(() => view.Gallery.SampleState == BooruGallerySampleState.Shown, "the accepted sample");
            Assert.Equal(300, Assert.IsAssignableFrom<Avalonia.Media.IImage>(ControlField<Image>(view.Gallery, "_image").Source).Size.Width);

            harness.Previews.SampleResult = _ => new BooruSampleResult(null, TimedOut: true);
            view.OpenGallery("101");
            await BooruTestSupport.WaitAsync(() => view.Gallery.SampleState == BooruGallerySampleState.TimedOut, "the response timeout");
            Assert.Equal(BooruGalleryPicture.Preview, view.Gallery.Picture);
            Assert.Contains(Texts.T("booru.gallery.sample.timeout", 3), view.Gallery.StatusText);
        }
    });

    [Fact]
    public void TheGallerySelectionUsesTheLocalizedSpaceKeyName() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        {
            await SearchAsync(harness, model, view);
            Texts.SetLanguage("de");
            Assert.True(view.OpenGallery("101"));
            Assert.EndsWith(" · Leertaste", Assert.IsType<string>(view.Gallery.SelectBox.Content));
            Texts.SetLanguage("ja");
            view.OpenGallery("102");
            Assert.EndsWith(" · " + Texts.Own("key.space"), Assert.IsType<string>(view.Gallery.SelectBox.Content));
        }
    });

    private static T ControlField<T>(Control owner, string name) where T : Control =>
        Assert.IsType<T>(owner.GetType().GetField(name, Private)!.GetValue(owner));

    private sealed class WindowScope : IDisposable
    {
        private readonly Window _window;

        internal WindowScope(Control content)
        {
            _window = new Window { Content = content, Width = 1100, Height = 800 };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => _window.Close();
    }

    [Fact]
    public void SpaceMarksTheFocusedHitAndTheCursorFollowsFocusAndIsDrawnOnEveryView() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model) using (view)
        {
            await SearchAsync(harness, model, view);
            Assert.Null(view.CursorPostId);
            Assert.Empty(Cards(view).Where(card => card.Classes.Contains("booru-cursor")));

            Border second = Cards(view)[1];
            second.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent) { Source = second });
            Assert.Equal("102", view.CursorPostId);
            Assert.Equal("BooruResult:102", Assert.Single(Cards(view), card => card.Classes.Contains("booru-cursor")).Name);

            // Space on the card itself marks the hit; the cards are rebuilt and the cursor is still on it.
            KeyEventArgs space = Press(Key.Space, second);
            second.RaiseEvent(space);
            Assert.True(space.Handled);
            Border again = Cards(view)[1];
            Assert.True(again.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "BooruSelect:102").IsChecked);
            Assert.Contains("booru-cursor", again.Classes);
            // Space on a part inside the card is not the card's key.
            TextBlock part = again.GetLogicalDescendants().OfType<TextBlock>().First();
            KeyEventArgs inner = Press(Key.Space, part);
            part.RaiseEvent(inner);
            Assert.False(inner.Handled);
            Border third = Cards(view)[2];
            KeyEventArgs space3 = Press(Key.Space, third);
            third.RaiseEvent(space3);
            Border afterSecond = Cards(view)[1];
            KeyEventArgs unmark = Press(Key.Space, afterSecond);
            afterSecond.RaiseEvent(unmark);
            Assert.False(Cards(view)[1].GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "BooruSelect:102").IsChecked);

            // The grid draws the same cursor on its tiles.
            view.SetResultView(BooruResultView.Grid, save: false);
            Border tile = view.TilePanel!.Children.OfType<Border>().Single(candidate => candidate.Name == "BooruResult:102");
            Assert.Contains("booru-cursor", tile.Classes);
            Assert.Single(view.TilePanel.Children.OfType<Border>(), candidate => candidate.Classes.Contains("booru-cursor"));

            // The gallery marks through the same selection: Space on its stage.
            Assert.True(view.OpenGallery("102"));
            Assert.False(view.Gallery.SelectBox.IsChecked);
            view.Gallery.Stage.RaiseEvent(Press(Key.Space, view.Gallery.Stage));
            Assert.True(view.Gallery.SelectBox.IsChecked);
        }
    });

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static KeyEventArgs Press(Key key, object source) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = source };

    private static StackPanel Results(BooruWorkbenchView view) => (StackPanel)typeof(BooruWorkbenchView).GetField("_results", Private)!.GetValue(view)!;

    private static List<Border> Cards(BooruWorkbenchView view) => Results(view).Children.OfType<Border>().ToList();

    /// <summary>A picture without a rendering platform: only its size and whether it was released are of interest.</summary>
    private sealed class FakeImage : Avalonia.Media.IImage, IDisposable
    {
        internal bool Released { get; private set; }
        public Size Size => new(8, 8);
        public void Draw(Avalonia.Media.DrawingContext context, Rect sourceRect, Rect destRect) { }
        public void Dispose() => Released = true;
    }

    private static BooruUiHarness Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view)
    {
        BooruUiHarness harness = new();
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true,
            BooruUiFixtures.Post("101", sample: true), BooruUiFixtures.Post("102", sample: true), BooruUiFixtures.Post("103"))));
        model = new BooruWorkbenchModel(harness.Services);
        view = new BooruWorkbenchView(model, ownsModel: false);
        view.Gallery.SampleDelay = TimeSpan.Zero;
        view.Gallery.Decoder = (bytes, _) => bytes.Length > 0 ? new FakeImage() : null;
        return harness;
    }

    private static async Task<List<Border>> SearchAsync(BooruUiHarness harness, BooruWorkbenchModel model, BooruWorkbenchView view)
    {
        _ = harness;
        await BooruTestSupport.SearchAsync(model.Search, "cat");
        await BooruTestSupport.WaitAsync(() => Cards(view).Count == 3 && model.Search.View.Rows.All(row => row.Preview == BooruPreviewState.Ready), "the cards with their previews");
        return Cards(view);
    }

    private static void OnDispatcher(Func<Task> test) => BooruComponentDispatcher.Run(() =>
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Dispatcher owner = Dispatcher.UIThread;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new ActionContext(owner));
            Task pending = test();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!pending.IsCompleted)
            {
                owner.RunJobs();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("The Booru gallery test dispatcher did not finish.");
                if (!pending.IsCompleted) Thread.Sleep(1);
            }
            pending.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    });

    private sealed class ActionContext(Dispatcher owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => owner.Post(() =>
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        });
    }
}
