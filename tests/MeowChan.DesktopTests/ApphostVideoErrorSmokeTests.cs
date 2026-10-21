using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ApphostVideoErrorSmokeTests
{
    private static string FreshProfile() => Path.Combine(Path.GetTempPath(), "meowchan-video-error-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("SystemBackendUnavailable")]
    [InlineData("SystemBackendUnsupportedVersion")]
    [InlineData("BundleDamaged")]
    [InlineData("InvalidPackageMetadata")]
    public void VideoErrorModeRequiresSpecificRealErrorAndUnwrittenProfile(string kind)
    {
        string profile = FreshProfile();
        var smoke = ApphostStartupSmoke.Parse([ApphostStartupSmoke.VideoErrorSwitch, kind], profile, null)!;
        Assert.Equal(kind, smoke.ExpectedVideoError.ToString());
        Assert.False(Directory.Exists(profile));
        Assert.Equal(1, smoke.Complete(2)); // Exit code alone is insufficient.
        Assert.False(File.Exists(profile + ".video-error.json"));
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.VideoErrorSwitch, kind], null, null));
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.VideoErrorSwitch, kind], profile, "legacy"));
        Directory.CreateDirectory(profile);
        try { Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.VideoErrorSwitch, kind], profile, null)); }
        finally { Directory.Delete(profile); }
    }

    [Theory]
    [InlineData("None")]
    [InlineData("1")]
    [InlineData("systembackendunavailable")]
    [InlineData("Unknown")]
    public void InvalidExpectedErrorCannotEnterAvalonia(string kind) =>
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.VideoErrorSwitch, kind], FreshProfile(), null));

    [Fact]
    public void InvalidArityAndCombinedModesAreRejected()
    {
        foreach (string[] args in new string[][]
        {
            [ApphostStartupSmoke.VideoErrorSwitch],
            [ApphostStartupSmoke.VideoErrorSwitch, "BundleDamaged", "extra"],
            [ApphostStartupSmoke.VideoErrorSwitch, "BundleDamaged", ApphostStartupSmoke.Switch, "3"],
            ["--smoke-video-error=BundleDamaged"],
            ["extra", ApphostStartupSmoke.VideoErrorSwitch, "BundleDamaged"]
        }) Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse(args, FreshProfile(), null));
    }
}
