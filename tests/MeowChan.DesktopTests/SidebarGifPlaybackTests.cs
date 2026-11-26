using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
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
        await using MainWindow window = new(session);
        window.Show();
        window.PlatformImpl!.Activated?.Invoke();
        await window.StartupTimingRestoreTask;
        await SidebarPreviewVerificationTests.ShowSidebarAsync(window, host, thread.Summary.Id);
        await AssertFramesAsync(window, "ReaderMediaSidebarPicture", host);
    });

    [Fact]
    public void BooruGifPreviewShowsBothFrames() => BooruMediaSidebarTests.InWindow((harness, model, view, window) =>
    {
        string setting = MotionPolicy.Setting;
        MotionPolicy.SetSetting(MotionPolicy.On);
        try
        {
            BooruPost post = BooruUiFixtures.Post("101").Post;
            var media = post.Media!.Select(variant => variant.Role == BooruMediaRole.Original
                ? variant with { Extension = "gif", Url = new Uri("https://cdn.donmai.us/original/101.gif"), Width = 8, Height = 8 }
                : variant).ToArray();
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, BooruPostView.From(post with { Media = media }))));
            byte[] gif = Convert.FromBase64String(EncodedGif);
            harness.Previews.OnPreview = _ => gif;
            BooruMediaSidebarTests.Search(model, view);
            BooruComponentDispatcher.Wait(AssertFramesAsync(window, "BooruMediaPreview", "Booru"));
        }
        finally { MotionPolicy.SetSetting(setting); }
    });

    private static async Task AssertFramesAsync(Window window, string id, string host)
    {
        Image? picture = null;
        await BooruTestSupport.WaitAsync(() =>
        {
            window.UpdateLayout();
            picture = window.GetVisualDescendants().OfType<Image>()
                .FirstOrDefault(image => image.IsEffectivelyVisible && AutomationProperties.GetAutomationId(image) == id);
            return picture?.Source is Bitmap;
        }, $"the {host} sidebar to show the GIF");

        HashSet<SKColor> colors = [];
        await BooruTestSupport.WaitAsync(() =>
        {
            if (picture!.Source is Bitmap bitmap)
            {
                using MemoryStream stream = new();
                bitmap.Save(stream);
                stream.Position = 0;
                using SKBitmap pixels = SKBitmap.Decode(stream);
                colors.Add(pixels.GetPixel(pixels.Width / 2, pixels.Height / 2));
            }
            return colors.Contains(SKColors.Red) && colors.Contains(SKColors.Blue);
        }, $"the {host} sidebar GIF to show both frame colors");
    }
}
