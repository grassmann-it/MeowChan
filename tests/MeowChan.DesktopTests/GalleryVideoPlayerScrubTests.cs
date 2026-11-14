using System.IO;
using System.Runtime.CompilerServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// UI admission tests for NH-3. They exercise only the seek-hover math and stale-result fence;
/// no Avalonia window, native video decoder or LibVLC runtime is needed.
/// </summary>
public sealed class GalleryVideoPlayerScrubTests
{
    [Theory]
    [InlineData(0, 400, 0)]
    [InlineData(20, 400, 0.05)]
    [InlineData(200, 400, 0.5)]
    [InlineData(400, 400, 1)]
    [InlineData(-20, 400, 0)]
    [InlineData(420, 400, 1)]
    public void PointerPositionIsClampedToSeekFraction(double pointerX, double seekWidth, double expected)
    {
        Assert.Equal(expected, GalleryVideoPlayer.ScrubFractionForPointer(pointerX, seekWidth), precision: 10);
    }

    [Fact]
    public void InvalidOrUnmeasurableSeekWidthDoesNotInventARequest()
    {
        Assert.Equal(0, GalleryVideoPlayer.ScrubFractionForPointer(double.NaN, 400));
        Assert.Equal(0, GalleryVideoPlayer.ScrubFractionForPointer(100, 0));
        Assert.Equal(0, GalleryVideoPlayer.ScrubFractionForPointer(100, double.NaN));
    }

    [Fact]
    public void APreviewMayPublishOnlyForTheSameGenerationRevisionSlotAndKnownDuration()
    {
        Assert.True(GalleryVideoPlayer.IsCurrentScrubRequest(7, 4, 10, 7, 4, 10, durationKnown: true));
        Assert.False(GalleryVideoPlayer.IsCurrentScrubRequest(6, 4, 10, 7, 4, 10, durationKnown: true));
        Assert.False(GalleryVideoPlayer.IsCurrentScrubRequest(7, 3, 10, 7, 4, 10, durationKnown: true));
        Assert.False(GalleryVideoPlayer.IsCurrentScrubRequest(7, 4, 9, 7, 4, 10, durationKnown: true));
        Assert.False(GalleryVideoPlayer.IsCurrentScrubRequest(7, 4, 10, 7, 4, 10, durationKnown: false));
    }

    [Fact]
    public void LengthChangesPublishDurationAndAResetPublishesNull()
    {
        string source = ReadDesktopSource("GalleryVideoPlayer.cs");
        Assert.Contains("internal event Action<TimeSpan?>? DurationChanged;", source);
        Assert.Contains("_length = 0; DurationChanged?.Invoke(null); UpdateTime(0);", source);
        Assert.Contains("DurationChanged?.Invoke(TimeSpan.FromMilliseconds(args.Length));", source);
        Assert.Contains("player.LengthChanged += (_, args) => Post(generation, () =>", source);
    }

    [Fact]
    public void HoverPreviewUsesTheExistingPlayerTimeFormat()
    {
        Assert.Equal("0:00", GalleryVideoPlayer.FormatVideoTime(0));
        Assert.Equal("1:05", GalleryVideoPlayer.FormatVideoTime(65_000));
        Assert.Equal("1:01:05", GalleryVideoPlayer.FormatVideoTime(3_665_000));
    }

    private static string ReadDesktopSource(string name, [CallerFilePath] string testPath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "..", "..", "Desktop", name)));
}
