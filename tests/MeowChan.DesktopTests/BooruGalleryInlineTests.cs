using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>
/// F13a and F13 stage 1: the inline Danbooru gallery (third result view), the cursor of the list and the grid, and the progressive loading of
/// the open post's sample. Fixtures only: fake ports, no provider request, no window.
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
            BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
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
            BooruUiHarness harness = Harness(out BooruWorkbenchModel model, out BooruWorkbenchView view);
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

    private static void OnDispatcher(Func<Task> test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousDispatcher = primary.GetValue(null);
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            SynchronizationContext.SetSynchronizationContext(new ActionContext(owner));
            Texts.Initialize();
            Texts.SetLanguage("en");
            Task pending = test();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!pending.IsCompleted)
            {
                owner.RunJobs();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Booru tile test dispatcher did not finish.");
                if (!pending.IsCompleted) Thread.Sleep(1);
            }
            pending.GetAwaiter().GetResult();
        }
        finally
        {
            Texts.SetLanguage(language);
            SynchronizationContext.SetSynchronizationContext(previousContext);
            primary.SetValue(null, previousDispatcher);
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
