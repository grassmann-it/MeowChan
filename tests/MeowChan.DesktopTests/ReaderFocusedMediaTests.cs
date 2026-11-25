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
    private const string PictureUrl = "https://example.invalid/picture.png";

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

    [Fact]
    public async Task TextlessPortraitAt360PixelsNeverShrinksBetween150And200Percent()
    {
        const int post = 108;
        const string postId = "108";
        using TempDirectory profile = new();
        MediaAssetSnapshot media = WritePicture(profile.Path, 540, 960);
        await CaptureThreadAsync(profile.Path, post, embedded: true, width: 540, height: 960);

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token),
                (_, _) => Task.CompletedTask, _ => { }, (_, _) => Task.FromResult(media))
            {
                IsReadable = true
            };
            Window window = new() { Width = 900, Height = 700, Content = page };
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
                Size previous = default;
                List<string> measurements = [];
                foreach (int percent in new[] { 100, 150, 175, 200 })
                {
                    config.ReaderFocusedMediaScalePercent = percent;
                    ReaderSettingsPreferences.Apply(page, config);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();

                    ListBoxItem card = Assert.IsAssignableFrom<ListBoxItem>(posts.ContainerFromItem(row));
                    Assert.True(card.IsSelected);
                    Image picture = Assert.IsType<Image>(Picture(card));
                    Size size = picture.Bounds.Size;
                    measurements.Add($"{percent}%: {size}");
                    output.WriteLine($"At {percent}%: portrait {size}, viewport {scroll.Viewport}, card {card.Bounds.Size} DIP.");
                    Assert.InRange(size.Width, 1, scroll.Viewport.Width + 0.5);
                    Assert.InRange(size.Height, 1, scroll.Viewport.Height + 0.5);
                    string series = string.Join(" → ", measurements);
                    if (percent == 150)
                        Assert.True(size.Width > previous.Width && size.Height > previous.Height, series);
                    Assert.True(size.Width >= previous.Width - 0.5 && size.Height >= previous.Height - 0.5, series);
                    previous = size;
                }
            }
            finally
            {
                page.EndLifetime();
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(960, 540, false)]
    [InlineData(540, 960, true)]
    public async Task ClickingAPictureInThePostFillsItsColumnWithinTheViewportAndCollapsesAgain(
        int width, int height, bool viewportLimited)
    {
        const string postId = "100";
        const int thumbnailWidth = 240;
        const int embeddedMediaColumnWidth = 268;
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
            Window window = new() { Width = 1920, Height = 620, Content = page };
            try
            {
                Config config = new()
                {
                    ReaderThumbnailWidth = thumbnailWidth,
                    ReaderTextlessLargeImages = false,
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

                ClickPicture(window, picture);
                Size expanded = CurrentPicture().Bounds.Size;
                output.WriteLine($"Thumbnail {thumbnail} → expanded {expanded} DIP, expected width {expectedWidth:0.##} DIP.");
                Assert.True(expanded.Width > thumbnail.Width, $"The first click changed the picture from {thumbnail} to {expanded} DIP.");
                Assert.InRange(expanded.Width, expectedWidth - 1, expectedWidth + 1);

                ClickPicture(window, CurrentPicture());
                Size collapsed = CurrentPicture().Bounds.Size;
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

    private static void ClickPicture(Window window, Image image)
    {
        Point center = new(image.Bounds.Width / 2, image.Bounds.Height / 2);
        Point at = Assert.IsType<Point>(image.TranslatePoint(center, window));
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
        string? pictureBody = null)
    {
        CapturedAttachment picture = new(0, "picture", Url: PictureUrl, OriginalFileName: FileName) { Width = width, Height = height };
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
        using ProfileLease lease = ProfileLease.Acquire(directory);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
        await catalog.CapturePreservingRevisionsAsync(new(ThreadId, "https://boards.4chan.org/g/thread/100", "Focused picture",
            ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, directory, posts));
    }

    private static MediaAssetSnapshot WritePicture(string directory, int width = 960, int height = 540)
    {
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = encoded.ToArray();
        string path = Path.Combine(directory, FileName);
        File.WriteAllBytes(path, bytes);
        MediaAssetReference asset = new("fixture-asset", "picture", "original", FileName, path, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), PictureUrl, FileName, true);
        return new(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
    }
}
