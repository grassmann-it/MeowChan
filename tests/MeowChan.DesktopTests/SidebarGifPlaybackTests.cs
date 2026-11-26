using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.PluginSdk;
using MeowChan.Tags;
using MeowChan.Tests.Shared;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SidebarGifPlaybackTests
{
    // Two full 8×8 frames (red and blue), 90 ms per frame, with an infinite encoded loop.
    private const string EncodedGif =
        "R0lGODlhCAAIAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACQAAACwAAAAACAAIAAAIDwABCBxIsKDBgwgTKkwYEAAh+QQBCQABACwAAAAACAAIAIEAAP8AAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAA7";

    [Theory]
    [InlineData("Reader")]
    [InlineData("Media")]
    [InlineData("Tagging")]
    public void SavedGifShowsBothFrames(string host) => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false,
            image: Convert.FromBase64String(EncodedGif), fileName: "a.gif");
        ArchivedThreadView thread = RateAndTagWindowFixture.View(media);
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "downloads"), FirstRunSetupVersion = 1, DownloadsPaused = true,
            WorkspaceMode = "Advanced", Language = "en", Animations = MotionPolicy.On, WindowWidth = 1600, WindowHeight = 900,
            ReaderMediaSidebar = true, ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways,
            ReaderMediaSidebarMinimumWidth = Config.ReaderMediaSidebarMinimumWidthMinimum,
            ReaderSidebarVideoAutoplay = false, MediaSidebarAutoHide = false,
            MediaLayout = (new MediaPageLayout { Panel = true }).Format(),
            TaggingPagePanel = true, TaggingPageView = "List"
        }.SaveConfig(profile.Path);
        await RateAndTagWindowFixture.SeedAsync(profile.Path, thread);

        await using DesktopSessionHost session = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        await session.StartAsync();
        if (host == "Tagging")
        {
            GalleryEntry entry = Assert.Single(GallerySelection.Build(thread, [media.Snapshot]).Entries);
            await session.EnqueueTagQueueAsync([Assert.IsType<TagQueueItem>(TagQueueItem.From(entry, DateTimeOffset.UnixEpoch))]);
        }
        string setting = MotionPolicy.Setting;
        try
        {
            await using MainWindow window = new(session);
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask;
            await SidebarPreviewVerificationTests.ShowSidebarAsync(window, host, thread.Summary.Id);
            Image picture = await AssertFramesAsync(window, "ReaderMediaSidebarPicture", host);
            if (host == "Reader")
            {
                ListBox posts = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(),
                    list => list.IsEffectivelyVisible && ControlAutomationPeer.CreatePeerForElement(list)?.GetAutomationId() == "ArchivePosts");
                posts.SelectedIndex = 1; // The fixture's second post has no attachment.
                await BooruTestSupport.WaitAsync(() => picture.Source is null, "the Reader to release the previous post's GIF");
                await AssertStillAsync(picture);
            }
        }
        finally { MotionPolicy.SetSetting(setting); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BooruGifPlaysWhileVisibleAndMotionIsEnabled(bool gifIsSample) => BooruMediaSidebarTests.InWindow((harness, model, view, window) =>
    {
        AvaloniaSynchronizationContext.InstallIfNeeded();
        string setting = MotionPolicy.Setting;
        MotionPolicy.SetSetting(MotionPolicy.On);
        try
        {
            BooruPost post = BooruUiFixtures.Post("101", sample: gifIsSample).Post;
            var media = post.Media!.Select(variant => variant.Role == BooruMediaRole.Original
                ? variant with { Extension = "gif", Url = new Uri("https://cdn.donmai.us/original/101.gif"), Width = 8, Height = 8 }
                : variant).ToArray();
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, BooruPostView.From(post with { Media = media }))));
            byte[] gif = Convert.FromBase64String(EncodedGif);
            harness.Previews.OnPreview = _ => gifIsSample ? BooruUiFixtures.Png(1) : gif;
            harness.Previews.OnSample = (_, _) => Task.FromResult<byte[]?>(gif);
            BooruMediaSidebarTests.Search(model, view);
            if (gifIsSample) view.SetCursor("101", requestSidebarSample: true);
            BooruComponentDispatcher.Wait(ObserveAsync());

            async Task ObserveAsync()
            {
                const string id = "BooruMediaPreview";
                Image picture = await AssertFramesAsync(window, id, "Booru");
                MotionPolicy.SetSetting(MotionPolicy.Off);
                await AssertStillAsync(picture);
                MotionPolicy.SetSetting(MotionPolicy.On);
                await AssertFramesAsync(window, id, "Booru after re-enabling motion");
                view.IsVisible = false;
                Assert.False(picture.IsEffectivelyVisible);
                await AssertStillAsync(picture);
                view.IsVisible = true;
                await AssertFramesAsync(window, id, "Booru after showing the page again");
            }
        }
        finally { MotionPolicy.SetSetting(setting); }
    });

    private static async Task<Image> AssertFramesAsync(Window window, string id, string host)
    {
        Image? picture = null;
        await BooruTestSupport.WaitAsync(() =>
        {
            window.UpdateLayout();
            picture = window.GetVisualDescendants().OfType<Image>()
                .FirstOrDefault(image => image.IsEffectivelyVisible && AutomationProperties.GetAutomationId(image) == id);
            return picture?.Source is Bitmap;
        }, $"the {host} sidebar to show the GIF");

        HashSet<SKColor?> colors = [];
        await BooruTestSupport.WaitAsync(() =>
        {
            colors.Add(ReadPixel(picture!));
            return colors.Contains(SKColors.Red) && colors.Contains(SKColors.Blue);
        }, $"the {host} sidebar GIF to show both frame colors");
        return picture!;
    }

    private static async Task AssertStillAsync(Image picture)
    {
        SKColor? color = ReadPixel(picture);
        long start = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(start) < TimeSpan.FromMilliseconds(360))
        {
            await Task.Delay(15);
            Assert.Equal(color, ReadPixel(picture));
        }
    }

    private static SKColor? ReadPixel(Image picture)
    {
        if (picture.Source is not Bitmap bitmap) return null;
        using MemoryStream stream = new();
        bitmap.Save(stream);
        stream.Position = 0;
        using SKBitmap pixels = SKBitmap.Decode(stream);
        return pixels.GetPixel(pixels.Width / 2, pixels.Height / 2);
    }
}
