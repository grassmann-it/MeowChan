using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using SkiaSharp;
using Xunit.Abstractions;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class ReaderFocusedMediaTests(ITestOutputHelper output)
{
    private static readonly ResourceId ThreadId = new("4chan", "g", "100");
    private const int ReplyCount = 5;
    private const string FileName = "picture.png";
    private const string PosterVideoFileName = "reader-portrait-poster.mp4";
    private const string FixtureUrl = "https://example.invalid/";

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task EnlargePercentGrowsTheFocusedPictureWithinItsColumn(bool autoOpenReplies, bool embedded)
    {
        int picturePost = embedded ? 108 : 100;
        string postId = picturePost.ToString();
        using TempDirectory profile = new();
        MediaAssetSnapshot media = WritePicture(profile.Path);
        await CaptureThreadAsync(profile.Path, picturePost, embedded);

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token),
                (_, _) => Task.CompletedTask, _ => { }, (_, _) => Task.FromResult(media))
            {
                IsReadable = true
            };
            Window window = new() { Width = embedded ? 1920 : 1180, Height = embedded ? 1040 : 800 };
            try
            {
                Config config = new()
                {
                    ReaderExpandReplies = autoOpenReplies,
                    ReaderFocusedMediaEnlarge = true,
                    ReaderFocusedMediaScalePercent = 100,
                    ReaderMediaSidebar = false
                };
                ReaderSettingsPreferences.Apply(page, config);
                window.Content = embedded ? page : page.DetachReader();
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId));
                BooruComponentDispatcher.Wait(page.MediaLoadTask);
                ArchivedThreadView thread = Assert.IsType<ArchivedThreadView>(page.CurrentThread);
                CapturedPost captured = Assert.Single(thread.Posts, post => post.PostId == postId);
                CapturedAttachment attachment = Assert.Single(captured.Attachments!);
                Assert.NotNull(MediaAttachmentResolver.Resolve(media, ThreadId, postId, attachment));

                ListBox posts = Posts(window);
                if (embedded) BooruComponentDispatcher.Wait(page.FocusPostAsync(postId));
                ArchivePostRow row = Assert.Single(posts.Items.OfType<ArchivePostRow>(), candidate => candidate.Id == postId);
                ListBoxItem? focused = null;
                string layout = string.Empty;
                try
                {
                    BooruComponentDispatcher.WaitUntil(() =>
                    {
                        window.UpdateLayout();
                        focused = posts.ContainerFromItem(row) as ListBoxItem;
                        Image? picture = focused == null ? null : Picture(focused);
                        int? replies = focused == null ? null : VisibleReplyCards(focused);
                        layout = $"List {posts.Bounds.Size}, card {focused?.Bounds.Size}, backlinks {row.BacklinkCount}, " +
                            $"visible replies {replies}, picture {picture?.Bounds.Size}, reply error {row.RepliesError}";
                        return focused is { IsArrangeValid: true, Bounds.Width: > 0, Bounds.Height: > 0 }
                            && row.BacklinkCount == ReplyCount && replies == (autoOpenReplies ? ReplyCount : 0)
                            && picture is { IsArrangeValid: true, Bounds.Width: > 0, Bounds.Height: > 0 };
                    }, "the archived picture and reply cards to be laid out");
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"{error.Message}. {layout}", error);
                }

                Assert.NotNull(focused);
                Assert.True(focused.Focus());
                Assert.Equal(postId, page.CurrentReaderPostId);
                ScrollViewer scroll = posts.Scroll as ScrollViewer ?? throw new InvalidOperationException("Reader scroll viewer is missing.");
                ScrollContentPresenter viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();

                List<Size> sizes = [];
                int[] percentages = embedded ? [100, 150, 200, 250, 300, 350, 400] : [100, 150, 200];
                foreach (int percent in percentages)
                {
                    config.ReaderFocusedMediaScalePercent = percent;
                    ReaderSettingsPreferences.Apply(page, config);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(postId, page.CurrentReaderPostId);
                    Assert.True(focused.IsSelected);
                    Assert.Equal(autoOpenReplies ? ReplyCount : 0, VisibleReplyCards(focused));
                    Image picture = Assert.IsType<Image>(Picture(focused));
                    Size size = picture.Bounds.Size;
                    Point origin = Assert.IsType<Point>(picture.TranslatePoint(default, viewport));
                    Assert.True(origin.X >= 0 && origin.X + size.Width <= viewport.Bounds.Width + 0.5,
                        $"At {percent}% the picture spans {origin.X:0.##} to {origin.X + size.Width:0.##} DIP in a {viewport.Bounds.Width:0.##} DIP viewport.");
                    sizes.Add(size);
                    output.WriteLine($"At {percent}%: picture {size}, viewport {viewport.Bounds.Size}, card {focused.Bounds.Size} DIP.");
                }

                Assert.True(sizes[0].Width < sizes[1].Width && sizes[1].Width < sizes[2].Width
                    && sizes[0].Height < sizes[1].Height && sizes[1].Height < sizes[2].Height,
                    $"Auto-open replies: {autoOpenReplies}. Picture at 100%, 150%, 200%: {string.Join(" → ", sizes.Take(3))} DIP.");
                for (int step = 3; step < sizes.Count; step++)
                    Assert.True(sizes[step].Width >= sizes[step - 1].Width - 0.5 && sizes[step].Height >= sizes[step - 1].Height - 0.5,
                        $"Auto-open replies: {autoOpenReplies}. Picture at {percentages[step - 1]}% and {percentages[step]}%: " +
                        $"{sizes[step - 1]} → {sizes[step]} DIP.");
            }
            finally
            {
                page.EndLifetime();
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextlessPortraitAt360PixelsNeverShrinksBetween150And200Percent(bool video)
    {
        const int post = 108;
        const string postId = "108";
        const int width = 288, height = 512;
        using TempDirectory profile = new();
        string fileName = video ? PosterVideoFileName : FileName;
        MediaAssetSnapshot media = video ? WriteMedia(profile.Path, fileName, ReadVideoFixture()) : WritePicture(profile.Path, width, height);
        await CaptureThreadAsync(profile.Path, post, embedded: true, width, height, fileName: fileName);

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token),
                (_, _) => Task.CompletedTask, _ => { }, (_, _) => Task.FromResult(media))
            {
                IsReadable = true
            };
            Window window = new() { Width = 900, Height = 700, Content = page };
            string? previousProfile = Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE");
            Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", profile.Path);
            try
            {
                Config config = new()
                {
                    ReaderMediaSidebar = false,
                    ReaderTextlessLargeImages = true,
                    ReaderTextlessMediaWidth = 360,
                    ReaderFocusedMediaEnlarge = true,
                    ReaderFocusedMediaScalePercent = 100
                };
                config.ChooseReaderMediaClick(ReaderMediaClickMode.Sidebar);
                ReaderSettingsPreferences.Apply(page, config);
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId));
                BooruComponentDispatcher.Wait(page.MediaLoadTask);
                BooruComponentDispatcher.Wait(page.FocusPostAsync(postId));
                Assert.False(page.IsMediaSidebarShown);

                ListBox posts = Posts(window);
                ArchivePostRow row = Assert.IsType<ArchivePostRow>(posts.SelectedItem);
                Assert.Equal(postId, row.Id);
                BooruComponentDispatcher.WaitUntil(() =>
                {
                    window.UpdateLayout();
                    return posts.ContainerFromItem(row) is ListBoxItem card
                        && Picture(card) is { IsArrangeValid: true, Bounds.Width: > 0, Bounds.Height: > 0 };
                }, "the textless post's portrait preview to be laid out");

                ScrollViewer scroll = Assert.IsType<ScrollViewer>(posts.Scroll);
                Image CurrentPicture()
                {
                    ListBoxItem card = Assert.IsAssignableFrom<ListBoxItem>(posts.ContainerFromItem(row));
                    Assert.True(card.IsSelected);
                    return Assert.IsType<Image>(Picture(card));
                }

                Size previous = default;
                List<string> measurements = [];
                void Measure(int percent, string stage)
                {
                    Image picture = CurrentPicture();
                    Size size = picture.Bounds.Size;
                    Point origin = Assert.IsType<Point>(picture.TranslatePoint(default, scroll));
                    Size visible = new Rect(origin, size).Intersect(new Rect(scroll.Viewport)).Size;
                    measurements.Add($"{percent}% {stage}: {visible}");
                    output.WriteLine($"At {percent}% ({stage}): visible {visible}, preview {size}, viewport {scroll.Viewport} DIP.");
                    Assert.InRange(size.Width, 1, scroll.Viewport.Width + 0.5);
                    Assert.InRange(size.Height, 1, scroll.Viewport.Height + 0.5);
                    Assert.InRange(visible.Width, 1, size.Width + 0.5);
                    Assert.InRange(visible.Height, 1, size.Height + 0.5);
                    string series = string.Join(" → ", measurements);
                    if (percent == 150 && stage == "layout")
                        Assert.True(visible.Width > previous.Width && visible.Height > previous.Height, series);
                    Assert.True(visible.Width >= previous.Width - 0.5 && visible.Height >= previous.Height - 0.5, series);
                    previous = visible;
                }

                foreach (int percent in new[] { 100, 150, 175, 200 })
                {
                    var priorSource = CurrentPicture().Source;
                    config.ReaderFocusedMediaScalePercent = percent;
                    ReaderSettingsPreferences.Apply(page, config);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    Measure(percent, "layout");
                    if (!video || percent == 100) continue;
                    BooruComponentDispatcher.WaitUntil(() =>
                    {
                        window.UpdateLayout();
                        return !ReferenceEquals(priorSource, CurrentPicture().Source);
                    }, $"the sized MP4 poster at {percent}% to replace its previous preview");
                    Measure(percent, "sized poster");
                }
            }
            finally
            {
                page.EndLifetime();
                window.Close();
                Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", previousProfile);
            }
        });
    }

    [Theory]
    [InlineData(960, 540, false, 620)]
    [InlineData(540, 960, true, 620)]
    [InlineData(540, 960, true, 550)]
    [InlineData(540, 960, true, 500)]
    public async Task ClickingAPictureInThePostFillsItsColumnWithinTheViewportAndCollapsesAgain(
        int width, int height, bool viewportLimited, int windowHeight)
    {
        const string postId = "100";
        const int thumbnailWidth = 240;
        const int embeddedMediaColumnWidth = 268;
        bool lowReader = windowHeight < 620;
        using TempDirectory profile = new();
        MediaAssetSnapshot media = WritePicture(profile.Path, width, height);
        await CaptureThreadAsync(profile.Path, 100, embedded: true, width, height);

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token),
                (_, _) => Task.CompletedTask, _ => { }, (_, _) => Task.FromResult(media))
            {
                IsReadable = true
            };
            Window window = new() { Width = 1920, Height = windowHeight, Content = page };
            try
            {
                Config config = new()
                {
                    ReaderThumbnailWidth = thumbnailWidth,
                    ReaderTextlessLargeImages = lowReader,
                    ReaderTextlessMediaWidth = 360,
                    ReaderExpandReplies = false,
                    ReaderMediaSidebar = true,
                    ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways
                };
                config.ChooseReaderMediaClick(ReaderMediaClickMode.Post);
                ReaderSettingsPreferences.Apply(page, config);
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId));
                BooruComponentDispatcher.Wait(page.MediaLoadTask);
                BooruComponentDispatcher.Wait(page.FocusPostAsync(postId));

                ListBox posts = Posts(window);
                ArchivePostRow row = Assert.IsType<ArchivePostRow>(posts.SelectedItem);
                Assert.Equal(postId, row.Id);
                BooruComponentDispatcher.WaitUntil(() =>
                {
                    window.UpdateLayout();
                    return posts.ContainerFromItem(row) is ListBoxItem card
                        && Picture(card) is { IsArrangeValid: true, Bounds.Width: > 0, Bounds.Height: > 0 };
                }, "the clickable picture to be laid out");
                Assert.True(page.IsMediaSidebarShown);

                Image CurrentPicture() => Assert.IsType<Image>(Picture(Assert.IsAssignableFrom<ListBoxItem>(posts.ContainerFromItem(row))));
                Image picture = CurrentPicture();
                Size thumbnail = picture.Bounds.Size;
                ArchiveAttachmentView view = Assert.IsType<ArchiveAttachmentView>(picture.FindAncestorOfType<ArchiveAttachmentView>());
                double viewportWidthLimit = view.VisibleFrameHeight() * width / height;
                Assert.Equal(viewportLimited, viewportWidthLimit < embeddedMediaColumnWidth);
                double expectedWidth = Math.Min(embeddedMediaColumnWidth, viewportWidthLimit);
                if (lowReader)
                {
                    Assert.InRange(thumbnail.Height, 269.5, 270.5);
                    Assert.True(viewportWidthLimit < thumbnail.Width,
                        $"The low reader has room for a {viewportWidthLimit:0.##} DIP wide picture, with {thumbnail.Width:0.##} DIP already visible.");
                    expectedWidth = thumbnail.Width;
                }

                ClickPicture(window, picture, 0.25);
                Size expanded = CurrentPicture().Bounds.Size;
                output.WriteLine($"Thumbnail {thumbnail} → expanded {expanded} DIP, expected width {expectedWidth:0.##} DIP.");
                Assert.True(view.IsExpanded);
                if (lowReader)
                {
                    Assert.InRange(expanded.Width, thumbnail.Width - 0.5, thumbnail.Width + 0.5);
                    Assert.InRange(expanded.Height, thumbnail.Height - 0.5, thumbnail.Height + 0.5);
                }
                else
                    Assert.True(expanded.Width > thumbnail.Width, $"The first click changed the picture from {thumbnail} to {expanded} DIP.");
                Assert.InRange(expanded.Width, expectedWidth - 1, expectedWidth + 1);

                // Separate the two clicks even when the first one leaves the picture unchanged in size.
                ClickPicture(window, CurrentPicture(), 0.75);
                Size collapsed = CurrentPicture().Bounds.Size;
                Assert.False(view.IsExpanded);
                Assert.InRange(collapsed.Width, thumbnail.Width - 0.5, thumbnail.Width + 0.5);
                Assert.InRange(collapsed.Height, thumbnail.Height - 0.5, thumbnail.Height + 0.5);
            }
            finally
            {
                page.EndLifetime();
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 0)]
    [InlineData(true, 8)]
    [InlineData(true, 25)]
    public async Task EnlargedPostAlignsOnEnableAndFocusChangeAndStaysWhereTheReaderScrolls(bool tallPost, int paddingPercent)
    {
        const int post = 108;
        const string postId = "108";
        using TempDirectory profile = new();
        MediaAssetSnapshot media = WritePicture(profile.Path);
        string body = tallPost ? string.Join("<br>", Enumerable.Repeat("A line in the selected post.", 48)) : "A short post.";
        await CaptureThreadAsync(profile.Path, post, embedded: true, pictureBody: body);

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token),
                (_, _) => Task.CompletedTask, _ => { }, (_, _) => Task.FromResult(media)) { IsReadable = true };
            Window window = new() { Width = 1920, Height = 800, Content = page };
            try
            {
                Config config = new()
                {
                    ReaderMediaSidebar = false,
                    ReaderExpandReplies = tallPost,
                    ReaderFoldLines = 100,
                    ReaderFocusedPostTopPaddingPercent = paddingPercent
                };
                ReaderSettingsPreferences.Apply(page, config);
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId));
                BooruComponentDispatcher.Wait(page.MediaLoadTask);
                BooruComponentDispatcher.Wait(page.FocusPostAsync(postId));

                ListBox posts = Posts(window);
                ArchivePostRow row = Assert.IsType<ArchivePostRow>(posts.SelectedItem);
                Assert.Equal(postId, row.Id);
                BooruComponentDispatcher.WaitUntil(() =>
                {
                    window.UpdateLayout();
                    return posts.ContainerFromItem(row) is ListBoxItem card && Picture(card) is { IsArrangeValid: true }
                        && (!tallPost || (row.RepliesExpanded && row.Replies.Count == ReplyCount));
                }, "the focused card, picture and automatic replies to lay out");
                ScrollViewer scroll = Assert.IsType<ScrollViewer>(posts.Scroll);

                void AssertAlignment(string operation)
                {
                    ListBoxItem card = Assert.IsAssignableFrom<ListBoxItem>(posts.ContainerFromItem(row));
                    Assert.Same(row, posts.SelectedItem);
                    Point at = Assert.IsType<Point>(card.TranslatePoint(default, scroll));
                    double viewportHeight = scroll.Viewport.Height;
                    Assert.Equal(tallPost, card.Bounds.Height > viewportHeight);
                    double expected = tallPost ? viewportHeight * paddingPercent / 100 : (viewportHeight - card.Bounds.Height) / 2;
                    Assert.True(Math.Abs(expected - at.Y) <= 2,
                        $"{operation} placed a {card.Bounds.Height:0.##} DIP post at {at.Y:0.##} DIP in a {viewportHeight:0.##} DIP viewport. Expected {expected:0.##} DIP.");
                }

                config.ReaderFocusedMediaEnlarge = true;
                ReaderSettingsPreferences.Apply(page, config);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                AssertAlignment("Enabling focused media enlargement");

                BooruComponentDispatcher.Wait(page.FocusPostAsync("107"));
                Assert.Equal("107", page.CurrentReaderPostId);
                BooruComponentDispatcher.Wait(page.FocusPostAsync(postId));
                AssertAlignment("Changing the focused post");

                double alignedOffset = scroll.Offset.Y;
                Point pointer = Assert.IsType<Point>(scroll.TranslatePoint(new Point(scroll.Viewport.Width / 2, scroll.Viewport.Height / 2), window));
                window.MouseWheel(pointer, new Vector(0, -1), RawInputModifiers.None);
                window.UpdateLayout();
                double scrolledOffset = scroll.Offset.Y;
                Assert.True(scrolledOffset > alignedOffset + 2, "The wheel should scroll the focused card away from its alignment.");
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.InRange(scroll.Offset.Y, scrolledOffset - 1, scrolledOffset + 1);
                Assert.Same(row, posts.SelectedItem);
            }
            finally
            {
                page.EndLifetime();
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrowKeysAlignTheListEdgesWithAnEnlargedLastPicture(bool scrollBeforePictureLoads)
    {
        int openingPost = scrollBeforePictureLoads ? 200 : 300;
        ResourceId thread = new("4chan", "g", openingPost.ToString());
        const int postCount = 9;
        const int tallPostIndex = 3;
        string longBody = string.Join("<br>", Enumerable.Repeat("A line in a tall post.", 48));
        string mediaBody = string.Join("<br>", Enumerable.Repeat("A line in the media post.", 12));
        CapturedAttachment attachment = new(0, "picture", Url: FixtureUrl + FileName, OriginalFileName: FileName);
        CapturedPost[] captured = Enumerable.Range(0, postCount).Select(index => new CapturedPost(
            (openingPost + index).ToString(), index, PostFieldPresence.All,
            Html: index == tallPostIndex ? longBody : index == postCount - 1 ? mediaBody : "A short post.",
            Attachments: index == postCount - 1 ? [attachment] : [])).ToArray();
        using TempDirectory profile = new();
        MediaAssetSnapshot media = WritePicture(profile.Path, 360, 1200);
        await CapturePostsAsync(profile.Path, thread, captured);

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            TaskCompletionSource<MediaAssetSnapshot> inventory = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token),
                (_, _) => Task.CompletedTask, _ => { }, (_, token) => inventory.Task.WaitAsync(token)) { IsReadable = true };
            Window window = new() { Width = 680, Height = 620 };
            try
            {
                ReaderSettingsPreferences.Apply(page, new Config
                {
                    ReaderMediaSidebar = false, ReaderFocusedMediaEnlarge = true,
                    ReaderFocusedMediaScalePercent = 200, ReaderFoldLines = 100
                });
                window.Content = page.DetachReader();
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                BooruComponentDispatcher.Wait(page.LoadThreadAsync(thread));
                ListBox posts = Posts(window);
                ArchivePostRow lastRow = Assert.IsType<ArchivePostRow>(posts.Items[postCount - 1]);
                ArchiveAttachmentRow lastAttachment = Assert.Single(lastRow.Attachments);
                Assert.NotNull(MediaAttachmentResolver.Resolve(media, thread, lastRow.Id, lastAttachment.Attachment));
                // Start with a wide reserved frame so the portrait's decoded aspect changes the card's height.
                lastAttachment.KnownAspect = 4;
                ScrollViewer scroll = Assert.IsType<ScrollViewer>(posts.Scroll);
                ScrollContentPresenter viewport = Assert.Single(scroll.GetVisualDescendants().OfType<ScrollContentPresenter>());
                double Bottom() => scroll.Extent.Height - scroll.Viewport.Height;
                ListBoxItem SelectedCard() => Assert.IsAssignableFrom<ListBoxItem>(posts.ContainerFromIndex(posts.SelectedIndex));
                void Layout()
                {
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
                void Step(Key key, PhysicalKey physicalKey, int index)
                {
                    window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
                    window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
                    BooruComponentDispatcher.Wait(page.ReaderNavigationTask);
                    Layout();
                    Assert.Equal(index, posts.SelectedIndex);
                    Assert.True(SelectedCard().IsFocused);
                }

                Assert.Equal(0, posts.SelectedIndex);
                Assert.True(SelectedCard().Focus());
                for (int index = 1; index < postCount; index++)
                {
                    Step(Key.Down, PhysicalKey.ArrowDown, index);
                    if (index == postCount - 1) break;
                    ListBoxItem card = SelectedCard();
                    Point top = Assert.IsType<Point>(card.TranslatePoint(default, viewport));
                    Assert.InRange(top.Y, 0, scroll.Viewport.Height - 1);
                    if (index == tallPostIndex) Assert.True(card.Bounds.Height > scroll.Viewport.Height);
                    else Assert.True(top.Y + card.Bounds.Height <= scroll.Viewport.Height + 1);
                }

                double beforeLoad = Bottom();
                Assert.True(beforeLoad > 0);
                Assert.Equal(beforeLoad, scroll.Offset.Y);
                Step(Key.Down, PhysicalKey.ArrowDown, postCount - 1);
                Assert.Equal(Bottom(), scroll.Offset.Y);
                Assert.Null(Picture(SelectedCard()));
                Image preview = Assert.Single(SelectedCard().GetVisualDescendants().OfType<Image>(),
                    image => AutomationProperties.GetAutomationId(image) == "ReaderAttachmentPreview");
                ArchiveAttachmentView medium = Assert.IsType<ArchiveAttachmentView>(preview.FindAncestorOfType<ArchiveAttachmentView>());
                Rect frameBeforeLoad = Assert.IsType<Rect>(medium.FrameBoundsIn(viewport));
                Assert.True(frameBeforeLoad.Intersects(new Rect(scroll.Viewport)),
                    $"The reserved frame {frameBeforeLoad} must intersect the viewport {scroll.Viewport} to load.");
                double heightBeforeLoad = SelectedCard().Bounds.Height;
                if (scrollBeforePictureLoads)
                {
                    Point wheel = Assert.IsType<Point>(viewport.TranslatePoint(new Point(24, scroll.Viewport.Height / 2), window));
                    window.MouseWheel(wheel, new Vector(0, 1), RawInputModifiers.None);
                    Layout();
                    Assert.True(scroll.Offset.Y < Bottom() - 1);
                }
                else
                {
                    // The endpoint must still hold after the ordinary frame re-fit window has expired.
                    BooruComponentDispatcher.Wait(Task.Delay(TimeSpan.FromSeconds(2), BooruComponentDispatcher.Cancellation));
                }

                inventory.SetResult(media);
                BooruComponentDispatcher.Wait(page.MediaLoadTask);
                try
                {
                    BooruComponentDispatcher.WaitUntil(() =>
                    {
                        Layout();
                        return Picture(SelectedCard()) is { IsArrangeValid: true } && SelectedCard().Bounds.Height > heightBeforeLoad + 1;
                    }, "the last post's portrait to load and grow its card");
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"{error.Message}. Preview status: {medium.PreviewStatus}, aspect: {lastAttachment.KnownAspect}, "
                        + $"frame {frameBeforeLoad} → {medium.FrameBoundsIn(viewport)}, card height {heightBeforeLoad} → {SelectedCard().Bounds.Height}.", error);
                }
                Assert.True(SelectedCard().Bounds.Height > scroll.Viewport.Height);
                Assert.True(Bottom() > beforeLoad);
                if (scrollBeforePictureLoads) Assert.True(scroll.Offset.Y < Bottom() - 1);
                else Assert.Equal(Bottom(), scroll.Offset.Y);

                for (int index = postCount - 2; index >= 0; index--)
                    Step(Key.Up, PhysicalKey.ArrowUp, index);
                Assert.Equal(0, scroll.Offset.Y);
            }
            finally
            {
                page.EndLifetime();
                BooruComponentDispatcher.Wait(page.MediaLoadTask);
                window.Close();
            }
        });
    }

    private static void ClickPicture(Window window, Image image, double horizontalPosition)
    {
        Point inside = new(image.Bounds.Width * horizontalPosition, image.Bounds.Height / 2);
        Point at = Assert.IsType<Point>(image.TranslatePoint(inside, window));
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static ListBox Posts(Window window) => window.GetVisualDescendants().OfType<ListBox>()
        .Single(list => AutomationProperties.GetAutomationId(list) == "ArchivePosts");

    private static Image? Picture(ListBoxItem card) => card.GetVisualDescendants().OfType<Image>()
        .SingleOrDefault(image => AutomationProperties.GetAutomationId(image) == "ReaderAttachmentPreview"
            && image.IsEffectivelyVisible && image.Source != null);

    private static int VisibleReplyCards(ListBoxItem card) => card.GetVisualDescendants().OfType<Button>()
        .Count(button => button.IsEffectivelyVisible && button.Classes.Contains("reader-reply-card"));

    private static async Task CaptureThreadAsync(string directory, int picturePost, bool embedded, int width = 960, int height = 540,
        string? pictureBody = null, string fileName = FileName)
    {
        CapturedAttachment picture = new(0, "picture", Url: FixtureUrl + fileName, OriginalFileName: fileName) { Width = width, Height = height };
        List<CapturedPost> posts = [];
        int lastPost = picturePost + ReplyCount + (embedded ? 16 : 0);
        for (int number = 100; number <= lastPost; number++)
        {
            int index = number - 100;
            if (number == picturePost)
                posts.Add(new(number.ToString(), index, PostFieldPresence.All,
                    Html: pictureBody ?? (embedded ? string.Empty : "Opening post with a picture"), Attachments: [picture]));
            else if (number > picturePost && number <= picturePost + ReplyCount)
                posts.Add(new(number.ToString(), index, PostFieldPresence.All,
                    Html: $"<a href='#p{picturePost}' class='quotelink'>&gt;&gt;{picturePost}</a><br>" +
                    "A reply with enough text to wrap inside its card when the attachment expands. " +
                    "The archived conversation stays visible next to the opening post."));
            else
                posts.Add(new(number.ToString(), index, PostFieldPresence.All, Html: "Another post in the thread."));
        }
        await CapturePostsAsync(directory, ThreadId, posts);
    }

    private static async Task CapturePostsAsync(string directory, ResourceId thread, IReadOnlyList<CapturedPost> posts)
    {
        using ProfileLease lease = ProfileLease.Acquire(directory);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
        await catalog.CapturePreservingRevisionsAsync(new(thread, "https://boards.4chan.org/g/thread/" + thread.Thread, "Focused picture",
            ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, directory, posts));
    }

    private static MediaAssetSnapshot WritePicture(string directory, int width = 960, int height = 540)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return WriteMedia(directory, FileName, encoded.ToArray());
    }

    private static MediaAssetSnapshot WriteMedia(string directory, string fileName, byte[] bytes)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, bytes);
        MediaAssetReference asset = new("fixture-asset", "picture", "original", fileName, path, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), FixtureUrl + fileName, fileName, true);
        return new(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
    }

    private static byte[] ReadVideoFixture()
    {
        for (DirectoryInfo? parent = new(AppContext.BaseDirectory); parent != null; parent = parent.Parent)
        {
            string candidate = Path.Combine(parent.FullName, "tests", "Fixtures", PosterVideoFileName);
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
        }
        throw new FileNotFoundException("The portrait MP4 fixture is missing.", PosterVideoFileName);
    }
}
