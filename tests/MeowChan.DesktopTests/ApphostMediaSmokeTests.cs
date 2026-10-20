using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ApphostMediaSmokeTests
{
    [Fact]
    public void LocalBoundedFixtureSelectsPlaybackWithoutCreatingProfile()
    {
        using TempDirectory directory = new();
        string fixture = Path.Combine(directory.Path, "fixture.webm");
        File.WriteAllBytes(fixture, [0x1a, 0x45, 0xdf, 0xa3]);
        string profile = Path.Combine(directory.Path, "profile");
        var smoke = ApphostStartupSmoke.Parse([ApphostStartupSmoke.PlaybackSwitch, fixture], profile, null)!;
        Assert.Equal(fixture, smoke.PlaybackFixture);
        Assert.Equal(30, smoke.Seconds); // Existing outer in-process watchdog adds 30 seconds.
        Assert.False(Directory.Exists(profile));
        Assert.Equal(1, smoke.Complete(0)); // A parsed request alone is never playback evidence.
    }

    [Theory]
    [InlineData("missing.webm", 4)]
    [InlineData("wrong.png", 4)]
    [InlineData("empty.webm", 0)]
    [InlineData("huge.webm", 2097153)]
    public void InvalidFixtureFailsBeforeProfileMutation(string name, int length)
    {
        using TempDirectory directory = new();
        string fixture = Path.Combine(directory.Path, name);
        if (name != "missing.webm") File.WriteAllBytes(fixture, new byte[length]);
        string profile = Path.Combine(directory.Path, "profile");
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.PlaybackSwitch, fixture], profile, null));
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public void PlaybackRequiresFreshAbsoluteUnambiguousProfileAndAbsoluteFixture()
    {
        using TempDirectory directory = new();
        string fixture = Path.Combine(directory.Path, "fixture.webm");
        File.WriteAllBytes(fixture, [1]);
        string fresh = Path.Combine(directory.Path, "fresh");
        foreach (string? profile in new[] { null, "", "relative", directory.Path, fixture })
            Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.PlaybackSwitch, fixture], profile, null));
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.PlaybackSwitch, fixture], fresh, fresh));
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.PlaybackSwitch, "relative.webm"], fresh, null));
        Assert.False(Directory.Exists(fresh));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void PlaybackAndStartupArgumentsCannotBeCombinedOrTruncated()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "fresh");
        foreach (string[] arguments in new string[][]
        {
            [ApphostStartupSmoke.PlaybackSwitch], [ApphostStartupSmoke.PlaybackSwitch, "a.webm", "extra"],
            [ApphostStartupSmoke.PlaybackSwitch, "a.webm", ApphostStartupSmoke.Switch, "3"],
            [ApphostStartupSmoke.Switch, "3", ApphostStartupSmoke.PlaybackSwitch, "a.webm"],
            ["--smoke-playback-webm=a.webm"], ["--smoke-playback-unknown", "a.webm"]
        })
            Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse(arguments, profile, null));
        Assert.False(Directory.Exists(profile));
    }
}
