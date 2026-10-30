using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class VideoAudioAvailabilityTests
{
    [Theory]
    [InlineData(2, 0, 0, false)]
    [InlineData(2, 100, 0, false)]
    [InlineData(2, 0, 1, true)]
    [InlineData(2, 70, 100, true)]
    [InlineData(0, 70, 100, false)]
    [InlineData(-1, 70, 100, false)]
    [InlineData(2, -1, 100, false)]
    [InlineData(2, 100, -1, false)]
    public void AvailabilityRequiresRenderedAudioNotJustTrackOrCachedVolume(int tracks, int volume, int rendered, bool expected)
        => Assert.Equal(expected, VideoAudioAvailability.IsAvailable(tracks, volume, rendered));
}
