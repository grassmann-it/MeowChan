using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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

                ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
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

    private static Image? Picture(ListBoxItem card) => card.GetVisualDescendants().OfType<ArchiveAttachmentView>()
        .SelectMany(view => view.PreviewButton.GetVisualDescendants().OfType<Image>())
        .SingleOrDefault(image => image.IsEffectivelyVisible && image.Source != null);

    private static int VisibleReplyCards(ListBoxItem card) => card.GetVisualDescendants().OfType<Button>()
        .Count(button => button.IsEffectivelyVisible && button.Classes.Contains("reader-reply-card"));

    private static async Task CaptureThreadAsync(string directory, int picturePost, bool embedded)
    {
        CapturedAttachment picture = new(0, "picture", Url: PictureUrl, OriginalFileName: FileName) { Width = 960, Height = 540 };
        List<CapturedPost> posts = [];
        int lastPost = picturePost + ReplyCount + (embedded ? 16 : 0);
        for (int number = 100; number <= lastPost; number++)
        {
            int index = number - 100;
            if (number == picturePost)
                posts.Add(new(number.ToString(), index, PostFieldPresence.All,
                    Html: embedded ? string.Empty : "Opening post with a picture", Attachments: [picture]));
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

    private static MediaAssetSnapshot WritePicture(string directory)
    {
        using SKBitmap bitmap = new(960, 540);
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
