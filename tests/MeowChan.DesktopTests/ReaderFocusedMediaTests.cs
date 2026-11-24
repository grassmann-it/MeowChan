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
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnlargePercentGrowsTheFocusedPictureWithinItsColumn(bool autoOpenReplies)
    {
        using TempDirectory profile = new();
        MediaAssetSnapshot media = WritePicture(profile.Path);
        await CaptureThreadAsync(profile.Path);

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token),
                (_, _) => Task.CompletedTask, _ => { }, (_, _) => Task.FromResult(media))
            {
                IsReadable = true
            };
            Window window = new() { Width = 1180, Height = 800 };
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
                window.Content = page.DetachReader();
                BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId));
                BooruComponentDispatcher.Wait(page.MediaLoadTask);
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
                BooruComponentDispatcher.WaitUntil(() =>
                {
                    window.UpdateLayout();
                    return posts.ContainerFromIndex(0) is ListBoxItem { DataContext: ArchivePostRow row } card
                        && row.BacklinkCount == ReplyCount
                        && VisibleReplyCards(card) == (autoOpenReplies ? ReplyCount : 0)
                        && Picture(card) is { Bounds.Width: > 0, Bounds.Height: > 0 };
                }, "the archived picture and reply cards to be laid out");

                ListBoxItem focused = Assert.IsAssignableFrom<ListBoxItem>(posts.ContainerFromIndex(0));
                Assert.True(focused.Focus());
                Assert.Equal(ThreadId.Thread, page.CurrentReaderPostId);
                ScrollContentPresenter viewport = posts.GetVisualDescendants().OfType<ScrollContentPresenter>().First();

                List<Size> sizes = [];
                foreach (int percent in new[] { 100, 150, 200 })
                {
                    config.ReaderFocusedMediaScalePercent = percent;
                    ReaderSettingsPreferences.Apply(page, config);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(ThreadId.Thread, page.CurrentReaderPostId);
                    Assert.True(focused.IsSelected);
                    Assert.Equal(autoOpenReplies ? ReplyCount : 0, VisibleReplyCards(focused));
                    Image picture = Assert.IsType<Image>(Picture(focused));
                    Size size = picture.Bounds.Size;
                    Point origin = Assert.IsType<Point>(picture.TranslatePoint(default, viewport));
                    Assert.True(origin.X >= 0 && origin.X + size.Width <= viewport.Bounds.Width + 0.5,
                        $"At {percent}% the picture spans {origin.X:0.##} to {origin.X + size.Width:0.##} DIP in a {viewport.Bounds.Width:0.##} DIP viewport.");
                    sizes.Add(size);
                }

                output.WriteLine($"Picture at 100%, 150%, 200%: {string.Join(" → ", sizes)} DIP.");
                Assert.True(sizes[0].Width < sizes[1].Width && sizes[1].Width < sizes[2].Width
                    && sizes[0].Height < sizes[1].Height && sizes[1].Height < sizes[2].Height,
                    $"Auto-open replies: {autoOpenReplies}. Picture at 100%, 150%, 200%: {string.Join(" → ", sizes)} DIP.");
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

    private static async Task CaptureThreadAsync(string directory)
    {
        CapturedAttachment picture = new(0, "picture", Url: PictureUrl, OriginalFileName: FileName) { Width = 960, Height = 540 };
        List<CapturedPost> posts = [new("100", 0, PostFieldPresence.All, Html: "Opening post with a picture", Attachments: [picture])];
        for (int index = 1; index <= ReplyCount; index++)
            posts.Add(new((100 + index).ToString(), index, PostFieldPresence.All,
                Html: "<a href='#p100' class='quotelink'>&gt;&gt;100</a><br>" +
                    "A reply with enough text to wrap inside its card when the attachment expands. " +
                    "The archived conversation stays visible next to the opening post."));
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
