using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ApphostStartupSmokeTests
{
    private static string FreshProfile() => Path.Combine(Path.GetTempPath(), "meowchan-apphost-parse-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("10")]
    public void ExplicitModeAcceptsBoundedDwellAndNewAbsoluteProfileWithoutCreatingIt(string seconds)
    {
        string profile = FreshProfile();
        ApphostStartupSmoke? smoke = ApphostStartupSmoke.Parse([ApphostStartupSmoke.Switch, seconds], profile, null);
        Assert.Equal(int.Parse(seconds), smoke!.Seconds);
        Assert.False(Directory.Exists(profile));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData(" 3")]
    [InlineData("three")]
    public void InvalidDwellIsRejectedWithoutCreatingAProfile(string seconds)
    {
        string profile = FreshProfile();
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.Switch, seconds], profile, null));
        Assert.False(Directory.Exists(profile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-profile")]
    public void ModeCannotFallBackToDefaultOrRelativeProfile(string? profile)
        => Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.Switch, "3"], profile, null));

    [Fact]
    public void ExistingProfileAndConflictingLegacyOverrideAreRejectedWithoutMutation()
    {
        string profile = FreshProfile();
        Directory.CreateDirectory(profile);
        try
        {
            string evidence = Path.Combine(profile, "untouched.txt");
            File.WriteAllText(evidence, "original");
            Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.Switch, "3"], profile, null));
            Assert.Equal("original", File.ReadAllText(evidence));
            Assert.Single(Directory.GetFileSystemEntries(profile));
            Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.Switch, "3"], FreshProfile(), profile));
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void ExistingFileCannotBeUsedAsSmokeProfile()
    {
        string profile = FreshProfile();
        File.WriteAllText(profile, "original");
        try
        {
            Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostStartupSmoke.Switch, "3"], profile, null));
            Assert.Equal("original", File.ReadAllText(profile));
        }
        finally { File.Delete(profile); }
    }

    [Fact]
    public void MissingExtraAndMalformedSmokeArgumentsFailBeforeAppInitialization()
    {
        foreach (string[] args in new string[][]
        {
            [ApphostStartupSmoke.Switch], [ApphostStartupSmoke.Switch, "3", "extra"],
            ["--smoke-startup-seconds=3"], ["--smoke-startup-seconds", "bad"],
            ["extra", ApphostStartupSmoke.Switch, "3"]
        })
            Assert.Equal(2, Program.Main(args));
    }

    [Fact]
    public void NormalLauncherArgumentsKeepTheirExistingStartPath()
    {
        Assert.Null(ApphostStartupSmoke.Parse([], null, null));
        Assert.Null(ApphostStartupSmoke.Parse(["-psn_0_12345"], null, null));
        Assert.Null(ApphostStartupSmoke.Parse(["--other-launcher-argument"], "existing", "legacy"));
    }

    [Fact]
    public void LifetimeExitWithoutCompletedNativeLifecycleCannotReportSuccess()
    {
        ApphostStartupSmoke smoke = ApphostStartupSmoke.Parse([ApphostStartupSmoke.Switch, "3"], FreshProfile(), null)!;
        Assert.Equal(1, smoke.Complete(0));
        Assert.Equal(1, smoke.Complete(7));
    }
}
