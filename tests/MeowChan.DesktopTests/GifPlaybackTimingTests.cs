using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class GifPlaybackTimingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void FiniteEncodedRepeatCountIncludesTheInitialPass(int repeats, int passes)
    {
        var sequence = new GifPlaybackSequence(3, repeats);
        int frames = 1;
        while (sequence.Next is int next) { sequence.Commit(next); ++frames; }
        sequence.Finish();
        Assert.True(sequence.Ended); Assert.Equal(passes, sequence.CompletedPasses);
        Assert.Equal(2, sequence.Index); Assert.Equal(3 * passes, frames);
        sequence.Finish(); Assert.Equal(passes, sequence.CompletedPasses);
        sequence.Restart(); Assert.False(sequence.Ended); Assert.Equal(0, sequence.Index);
    }
    [Fact]
    public void InfiniteGifLoopsWithoutRequestingMediaNavigation()
    {
        var sequence = new GifPlaybackSequence(2, -1);
        for (int i = 0; i < 500; i++) { Assert.NotNull(sequence.Next); sequence.Commit(sequence.Next!.Value); }
        Assert.False(sequence.Ended); Assert.Equal(250, sequence.CompletedPasses);
    }
    [Fact]
    public void SingleFrameNeverNeedsARepeatedDecodeAndStaleCommitsAreRejected()
    {
        var still = new GifPlaybackSequence(1, -1); Assert.Null(still.Next);
        var sequence = new GifPlaybackSequence(3, 0);
        Assert.Throws<InvalidOperationException>(() => sequence.Commit(2));
        Assert.Equal(0, sequence.Index);
    }
}
