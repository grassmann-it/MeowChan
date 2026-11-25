using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SavedMediaGalleryFocusTests
{
    [Fact]
    public void GalleryReturnFollowsTheFinalSavedSelectionWithThePictureAndKeyboard() =>
        SavedMediaReaderTests.Run((page, window) =>
        {
            ListBox tiles = SidePanelDesignTests.ById<ListBox>(window, "ArchiveMediaTiles");
            ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
            ArchiveAssetRow Current(bool current) => tiles.Items.OfType<ArchiveAssetRow>().Single(row => row.Asset.IsCurrent == current);
            ListBoxItem Tile(ArchiveAssetRow row) => tiles.GetVisualDescendants().OfType<ListBoxItem>().Single(tile =>
                AutomationProperties.GetAutomationId(tile) == "ArchiveMediaTile" && ReferenceEquals(tile.DataContext, row));

            tiles.SelectedItem = Current(true);
            Assert.True(Tile(Current(true)).Focus());
            BooruComponentDispatcher.WaitUntil(() => sidebar.Load.ShownSource == new PixelSize(17, 11), "the original selection");
            Action restore = page.CaptureGalleryOrigin();
            tiles.SelectedItem = Current(false);
            Assert.True(Tile(Current(false)).Focus());
            BooruComponentDispatcher.WaitUntil(() => sidebar.Load.ShownSource == new PixelSize(19, 13), "the intervening selection");

            restore();
            ContextGestures.Settle(window);
            Assert.Same(Current(true), tiles.SelectedItem);
            BooruComponentDispatcher.WaitUntil(() => sidebar.Load.ShownSource == new PixelSize(17, 11), "the restored revision's picture");
            Assert.True(Tile(Current(true)).IsKeyboardFocusWithin);
            Assert.Same(Tile(Current(true)), page.GalleryOriginFocusTarget(sidebar.FrameArea));
        });

    [Fact]
    public void SelectedVideoShowsItsPosterWhileAllTileDecodesAreHeld()
    {
        using TempDirectory profile = new();
        string? previous = Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE");
        Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", profile.Path);
        try { SavedMediaReaderTests.Run(SelectVideo, extraAsset: SeedVideo); }
        finally { Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", previous); }

        MediaAssetReference SeedVideo(string directory)
        {
            byte[] original = [1, 9, 2, 8];
            string hash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            string file = Path.Combine(directory, "selected.webm");
            File.WriteAllBytes(file, original);
            string cache = Path.Combine(profile.Path, "cache", "video-posters-v1");
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, hash + ".png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAE0lEQVR4nGNgmMlAMhrVM6oHiADlWm/EglqV/gAAAABJRU5ErkJggg=="));
            return new(new string('f', 64), "video", "original", "selected.webm", file, original.Length,
                hash, "https://offline.invalid/selected.webm", "selected.webm", true);
        }

        static void SelectVideo(ArchivePage page, Window window)
        {
            ListBox tiles = SidePanelDesignTests.ById<ListBox>(window, "ArchiveMediaTiles");
            ArchiveAssetRow video = tiles.Items.OfType<ArchiveAssetRow>().Single(row => row.Asset.RequestedFileName == "selected.webm");
            var gate = (ThumbnailDecodeGate)typeof(ArchivePage).GetField("_thumbnailGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int running = 0;
            Task[] held = Enumerable.Range(0, ThumbnailDecodeGate.ArchiveSlots).Select(_ =>
                gate.RunAsync(ThumbnailDecodePriority.Image, () =>
                {
                    Interlocked.Increment(ref running);
                    return release.Task;
                }, CancellationToken.None)).ToArray();
            try
            {
                BooruComponentDispatcher.WaitUntil(() => Volatile.Read(ref running) == held.Length, "tile decodes to occupy every slot");
                tiles.SelectedItem = video;
                ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
                BooruComponentDispatcher.WaitUntil(() => sidebar.HeaderLine.Contains("selected.webm", StringComparison.Ordinal),
                    "the selected video's header");
                BooruComponentDispatcher.WaitUntil(() => sidebar.Shown?.PixelSize == new PixelSize(17, 11) && !sidebar.Load.Awaiting && !sidebar.IsShowingStalePicture,
                    "the selected video's poster while every tile slot is busy");
            }
            finally
            {
                release.SetResult(true);
                BooruComponentDispatcher.Wait(Task.WhenAll(held));
                BooruComponentDispatcher.Wait(page.MediaSidebarTask);
            }
        }
    }
}
