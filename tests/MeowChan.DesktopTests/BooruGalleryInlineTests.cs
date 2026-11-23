using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
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
    public void TheStatusLineNamesRatingPictureSampleAndOriginal() => OnDispatcher(() =>
    {
        BooruResultRowView row = new(BooruUiFixtures.Post("55", sample: true, rating: BooruRating.Questionable), Preview: BooruPreviewState.Ready);
        string loading = BooruGalleryStatus.Line(row, BooruGalleryPicture.Preview, BooruGallerySampleState.Loading, 8);
        Assert.Equal("Post #55 · Danbooru rating: Questionable · Shown: preview · Larger version loading… · Original available", loading);
        Assert.Contains("Shown: reduced-size version (sample)", BooruGalleryStatus.Line(row, BooruGalleryPicture.Sample, BooruGallerySampleState.Shown, 8));
        Assert.Contains("larger than 8 MiB", BooruGalleryStatus.Line(row, BooruGalleryPicture.Preview, BooruGallerySampleState.Failed, 8));
        Assert.Contains("offers no reduced-size version", BooruGalleryStatus.Line(row, BooruGalleryPicture.Preview, BooruGallerySampleState.NotOffered, 8));
        Assert.Contains("Loading preview", BooruGalleryStatus.Line(row with { Preview = BooruPreviewState.Loading }, BooruGalleryPicture.None, BooruGallerySampleState.Loading, 8));
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
                TextBlock position = GalleryPosition(gallery);
                Assert.Equal("1 of 3", position.Text);
                Assert.False(gallery.PreviousButton.IsEnabled);
                Assert.True(gallery.NextButton.IsEnabled);

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
                Assert.Equal("2 of 3", position.Text);
                Assert.Equal(BooruGalleryPicture.Preview, gallery.Picture);
                await BooruTestSupport.WaitAsync(() => harness.Previews.SampleRequests.Count == 2, "the request of 102");
                Assert.Equal(BooruGallerySampleState.Loading, gallery.SampleState);
                gallery.Stage.RaiseEvent(Press(Key.Right, gallery.Stage));
                Assert.Equal("103", gallery.CurrentPostId);
                Assert.Equal("3 of 3", position.Text);
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
    public void PopoutReplacesThePreviewWithOneBoundedSampleAndCancelsWhenBrowsingAway() => OnDispatcher(async wait =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        using (WindowScope window = new(view))
        {
            TaskCompletionSource<byte[]?> firstSample = new(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("101", tags: [], sample: true),
                    BooruUiFixtures.Post("102", tags: [], sample: true), BooruUiFixtures.Post("103", tags: []))));
            int canceledSamples = 0;
            harness.Previews.OnSample = async (post, token) =>
            {
                if (post.PostId == "101") return await firstSample.Task.WaitAsync(token);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { Interlocked.Increment(ref canceledSamples); throw; }
                return null;
            };
            view.GallerySampleMiB = () => 2;
            view.GalleryCacheMiB = () => 16;
            view.GalleryMaximumWidth = () => 300;
            await wait.TaskAsync(SearchAsync(harness, model, view), "the search cards and previews");
            Assert.Empty(harness.Previews.SampleRequests);
            Assert.True(view.OpenResultViewer("101"));
            Window host = Assert.IsType<Window>(TopLevel.GetTopLevel(view));
            BooruResultViewerWindow popup = Assert.Single(host.OwnedWindows.OfType<BooruResultViewerWindow>());
            Image image = popup.GetVisualDescendants().OfType<Image>().Single(picture => picture.Name == "GalleryImage");
            TextBlock caption = popup.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "BooruViewerCaption");
            await wait.UntilAsync(() => image.Source is Bitmap && harness.Previews.SampleRequests.Count == 1, "the preview and one sample request");
            Bitmap preview = Assert.IsType<Bitmap>(image.Source);
            Assert.Contains(Texts.T("booru.gallery.shown.preview"), caption.Text);

            using SKBitmap source = new(640, 320);
            using SKImage pixels = SKImage.FromBitmap(source);
            using SKData bytes = pixels.Encode(SKEncodedImageFormat.Png, 100);
            byte[] sampleBytes = bytes.ToArray();
            // The popout now expects another preview identity while the viewer still has the old one.
            typeof(BooruResultViewerWindow).GetField("_shownEntry", Private)!
                .SetValue(popup, BooruResultViewerWindow.CreatePreviewEntry(model.Search.View.Rows[0]));
            firstSample.SetResult(sampleBytes);
            await wait.UntilAsync(() => !caption.Text!.Contains(Texts.T("booru.gallery.sample.loading")), "the rejected sample to finish");
            Assert.Same(preview, image.Source);
            Assert.DoesNotContain(Texts.T("booru.gallery.shown.sample"), caption.Text);

            image.RaiseEvent(Press(Key.Space, image)); // Refresh the same post.
            await wait.UntilAsync(() => caption.Text!.Contains(Texts.T("booru.gallery.shown.sample")), "the sample caption");
            Assert.NotSame(preview, image.Source);
            Assert.Equal(300, Assert.IsType<Bitmap>(image.Source).PixelSize.Width);
            Assert.Equal(new[] { "101", "101" }, harness.Previews.SampleRequests);
            Assert.Equal((2 * MiB, 16L * MiB), harness.Previews.SampleLimits[0]);

            image.RaiseEvent(Press(Key.Right, image));
            await wait.UntilAsync(() => harness.Previews.SampleRequests.Count == 3, "the next post's sample request");
            image.RaiseEvent(Press(Key.Right, image));
            await wait.UntilAsync(() => Volatile.Read(ref canceledSamples) == 1, "cancellation after leaving the next post");
            Assert.Equal("103", popup.CurrentPostId);
            Assert.Contains(Texts.T("booru.gallery.sample.none"), caption.Text);
            Assert.Equal(new[] { "101", "101", "102" }, harness.Previews.SampleRequests);
            Assert.Empty(harness.Queue.Loads);
            image.RaiseEvent(Press(Key.Left, image));
            await wait.UntilAsync(() => harness.Previews.SampleRequests.Count == 4, "the revisited post's sample request");
            popup.Close();
            await wait.TaskAsync(popup.Cleanup, "the popout gallery to release its resources");
            await wait.UntilAsync(() => Volatile.Read(ref canceledSamples) == 2, "cancellation after closing the popout");
        }
    });

    [Fact]
    public void GalleryTagsScrollWithTheKeyboardWithoutMovingThePost() => OnDispatcher(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        using (WindowScope window = new(view))
        {
            string[] tags = Enumerable.Range(1, 250).Select(index => $"long_gallery_tag_{index}").ToArray();
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("101", tags: tags),
                    BooruUiFixtures.Post("102"), BooruUiFixtures.Post("103"))));
            await SearchAsync(harness, model, view);
            Assert.True(view.OpenGallery("101"));
            Window host = Assert.IsType<Window>(TopLevel.GetTopLevel(view));
            await CheckTagsAsync(host, view.Gallery, () => view.Gallery.CurrentPostId);

            TextBox input = new() { Text = "Editable text" };
            view.Gallery.SetDock(BooruGalleryDock.Left, input);
            host.UpdateLayout();
            Assert.True(input.Focus());
            input.RaiseEvent(Press(Key.End, input));
            Assert.True(input.IsFocused);
            Assert.Equal(input.Text!.Length, input.CaretIndex);
            Assert.Equal("101", view.Gallery.CurrentPostId);

            Assert.True(view.OpenResultViewer("101"));
            BooruResultViewerWindow popup = Assert.Single(host.OwnedWindows.OfType<BooruResultViewerWindow>());
            try { await CheckTagsAsync(popup, popup, () => popup.CurrentPostId); }
            finally
            {
                popup.Close();
                await popup.Cleanup;
            }
        }

        static async Task CheckTagsAsync(Window host, Control gallery, Func<string?> currentPost)
        {
            host.UpdateLayout();
            BooruGalleryTags tags = Assert.Single(gallery.GetVisualDescendants().OfType<BooruGalleryTags>());
            Assert.True(tags.Extent.Height > tags.Viewport.Height);
            Assert.True(tags.Focus());
            double before = tags.Offset.Y;
            tags.RaiseEvent(Press(Key.Down, tags));
            await BooruTestSupport.WaitAsync(() => tags.Offset.Y > before, "the tag panel to scroll down");
            Assert.True(tags.IsFocused);
            Assert.Equal("101", currentPost());

            SelectableTextBlock text = tags.GetVisualDescendants().OfType<SelectableTextBlock>().First();
            Assert.True(text.Focus());
            text.RaiseEvent(Press(Key.Right, text));
            Assert.True(text.IsFocused);
            Assert.Equal("101", currentPost());
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
    public void RestoringTabsInGalleryUsesTheSnapshotAndDoesNotRequestASample() => OnDispatcher(async wait =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        using (WindowScope window = new(view))
        {
            await wait.TaskAsync(SearchAsync(harness, model, view), "the search cards and previews");
            Assert.True(view.OpenGallery("101"));
            await wait.UntilAsync(() => harness.Previews.SampleRequests.Count == 1, "the initial gallery sample");
            int previews = harness.Previews.Requests;
            int searches = harness.Search.Requests;
            int tab = view.SearchTabs.Add(copyCurrent: true);
            await wait.UntilAsync(() => view.Gallery.CurrentPostId == "101", "the copied gallery");
            view.SearchTabs.Select(BooruSearchTabs.PrimaryId);
            view.SearchTabs.Select(tab);
            await wait.UntilAsync(() => view.Gallery.CurrentPostId == "101", "the restored gallery");
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
    public void GalleryToolbarUpdatesThePositionAndSelectionHintWhenTheLanguageChanges() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
        using (model)
        using (view)
        {
            await SearchAsync(harness, model, view);
            Texts.SetLanguage("de");
            Assert.True(view.OpenGallery("101"));
            CheckBox selection = view.Gallery.SelectBox;
            StackPanel content = Assert.IsType<StackPanel>(selection.Content);
            TextBlock label = Assert.IsType<TextBlock>(content.Children[0]);
            Border keycap = Assert.IsType<Border>(content.Children[1]);
            Assert.Contains(KeyCapText.CapClass, keycap.Classes);
            TextBlock key = Assert.IsType<TextBlock>(keycap.Child);
            TextBlock position = GalleryPosition(view.Gallery);
            Assert.Equal("1 von 3", position.Text);
            Assert.Equal("Ausgewählt", label.Text);
            Assert.Equal("Leertaste", key.Text);
            Assert.Equal(key.Text, AutomationProperties.GetAcceleratorKey(selection));

            Texts.SetLanguage("ja");
            await BooruTestSupport.WaitAsync(() => key.Text == Texts.Own("key.space"), "the localized gallery shortcut");
            Assert.Equal(Texts.T("booru.gallery.position", 1, 3), position.Text);
            Assert.Equal(Texts.Own("media.tile.selected"), label.Text);
            Assert.Equal(key.Text, AutomationProperties.GetAcceleratorKey(selection));
        }
    });

    private static TextBlock GalleryPosition(BooruGalleryPanel gallery) => gallery.GetLogicalDescendants().OfType<TextBlock>()
        .Single(block => block.Name == "BooruGalleryPosition");

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

    [Fact]
    public async Task ATimeoutNamesThePendingGalleryState()
    {
        GalleryWait wait = new("gallery fixture");
        TaskCompletionSource pending = new();
        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(
            () => wait.TaskAsync(pending.Task, "the popup image to appear", milliseconds: 0));
        Assert.Contains("the popup image to appear", error.Message, StringComparison.Ordinal);
        Assert.Equal("the popup image to appear", wait.Description);
    }

    private static void OnDispatcher(Func<Task> test, [CallerMemberName] string scenario = "") =>
        OnDispatcher(_ => test(), scenario);

    private static void OnDispatcher(Func<GalleryWait, Task> test, [CallerMemberName] string scenario = "")
    {
        GalleryWait wait = new(scenario);
        BooruComponentDispatcher.Run(() =>
        {
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            Dispatcher owner = Dispatcher.UIThread;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new ActionContext(owner));
                Task pending = test(wait);
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (!pending.IsCompleted)
                {
                    owner.RunJobs();
                    if (!pending.IsCompleted && DateTime.UtcNow >= deadline)
                        throw new TimeoutException("The Booru gallery test dispatcher timed out waiting for " + wait.Description);
                    if (!pending.IsCompleted) Thread.Sleep(1);
                }
                pending.GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }, () => wait.Description);
    }

    private sealed class GalleryWait(string scenario)
    {
        private volatile string _description = scenario;
        internal string Description => _description;

        internal Task UntilAsync(Func<bool> condition, string what)
        {
            _description = what;
            return BooruTestSupport.WaitAsync(condition, what);
        }

        internal async Task TaskAsync(Task task, string what, int milliseconds = 5000)
        {
            _description = what;
            try { await task.WaitAsync(TimeSpan.FromMilliseconds(milliseconds)); }
            catch (TimeoutException error) { throw new TimeoutException("Timed out waiting for " + what, error); }
        }
    }

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
