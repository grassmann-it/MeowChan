using System.Runtime.InteropServices;
using System.Text;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

public sealed class InstallChannelTests
{
    private const string Commit = "3e826a624c2f6dfce8e03ca8ff4d62a7f30cb5f3";
    private static readonly InstallFacts OfficialLinux = new(OSPlatform.Linux, "/home/fixture/meowchan",
        InformationalVersion: "1.2.0+" + Commit, BuildOrigin: "official", BuildRepository: "grassmann-it/MeowChan",
        BuildRunId: "173", InstallDirectoryWritable: true);

    [Theory]
    [InlineData("windows-portable-zip", true)]
    [InlineData("winget", false)]
    [InlineData("macos-app", false)]
    [InlineData("homebrew", false)]
    [InlineData("appimage", true)]
    [InlineData("linux-tarball-bundled-vlc", true)]
    [InlineData("linux-tarball-system-vlc", true)]
    [InlineData("aur", false)]
    [InlineData("distro", false)]
    [InlineData("flatpak", false)]
    [InlineData("source", false)]
    [InlineData("ci-fork", false)]
    [InlineData("ci-acceptance", false)]
    [InlineData("none", false)]
    public void ChannelMatrix(string channel, bool eligible)
    {
        InstallFacts facts = channel switch
        {
            "windows-portable-zip" => OfficialLinux with { Platform = OSPlatform.Windows, BaseDirectory = @"C:\portable\MeowChan" },
            "winget" => OfficialLinux with { Platform = OSPlatform.Windows,
                BaseDirectory = @"C:\Users\fixture\AppData\Local\Microsoft\WinGet\Packages\MeowChan\bin" },
            "macos-app" => OfficialLinux with { Platform = OSPlatform.OSX, BaseDirectory = "/Applications/MeowChan.app/Contents/MacOS/" },
            "homebrew" => OfficialLinux with { Platform = OSPlatform.OSX, BaseDirectory = "/opt/homebrew/Cellar/meowchan/1.2.0/bin",
                BuildOrigin = "local", DistributionChannel = "homebrew" },
            "appimage" => OfficialLinux with { BaseDirectory = "/tmp/.mount_fixture/usr/lib/meowchan",
                AppImage = "/home/fixture/MeowChan.AppImage", LinuxVideoMode = "bundled",
                InstallDirectoryWritable = false, AppImageWritable = true },
            "linux-tarball-bundled-vlc" => OfficialLinux with { LinuxVideoMode = "bundled" },
            "linux-tarball-system-vlc" => OfficialLinux with { LinuxVideoMode = "system" },
            "aur" => OfficialLinux with { BaseDirectory = "/usr/lib/meowchan",
                DistributionMarker = Marker("aur"), LinuxVideoMode = "bundled", InstallDirectoryWritable = false },
            "distro" => OfficialLinux with { BuildOrigin = "local", DistributionChannel = "distro" },
            "flatpak" => OfficialLinux with { FlatpakId = "it.grassmann.MeowChan" },
            "source" => new(OSPlatform.Linux, "/fixtures/bin", BuildOrigin: "local"),
            "ci-fork" => OfficialLinux with { BuildOrigin = "fork-ci", BuildRepository = "fixture/MeowChan", LinuxVideoMode = "system" },
            "ci-acceptance" => OfficialLinux with { Acceptance = true, LinuxVideoMode = "bundled" },
            "none" => OfficialLinux with { UpdatesDisabled = true, DistributionMarker = Marker("distro") },
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };

        InstallIdentity result = InstallChannelDetector.Detect(facts);
        Assert.Equal(channel, result.Channel);
        Assert.Equal(eligible, result.SelfUpdateEligible);
        Assert.Equal(facts.BuildOrigin, result.Origin);
    }

    [Fact]
    public void VersionIdentityIncludesShortCommitAndRetainsCiMetadata()
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { LinuxVideoMode = "system" });
        Assert.Equal("1.2.0 · linux-tarball-system-vlc · official · 3e826a6", result.FormatVersion("1.2.0"));
        Assert.Equal("grassmann-it/MeowChan", result.BuildRepository);
        Assert.Equal("173", result.BuildRunId);
    }

    [Fact]
    public void MarkerWinsOverPropertyAndPropertyWinsOverHeuristic()
    {
        InstallFacts facts = OfficialLinux with { DistributionMarker = Marker("aur"), DistributionChannel = "distro",
            LinuxVideoMode = "bundled" };
        Assert.Equal("aur", InstallChannelDetector.Detect(facts).Channel);
        Assert.Equal("distro", InstallChannelDetector.Detect(facts with { DistributionMarker = null }).Channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    [InlineData("fork-ci")]
    public void OfficialMarkerWithoutOfficialOriginFallsBackToSource(string? origin)
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { BuildOrigin = origin,
            DistributionMarker = Marker("linux-tarball-system-vlc") });
        Assert.Equal("source", result.Channel);
        Assert.False(result.SelfUpdateEligible);
        Assert.Contains("official build origin", result.Reason);
    }

    [Theory]
    [InlineData("/usr/lib/meowchan", true)]
    [InlineData("/opt/meowchan", true)]
    [InlineData("/tmp/meowchan", false)]
    public void ProtectedOrUnwritableTarballCanOnlyNotify(string path, bool writable)
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { BaseDirectory = path,
            LinuxVideoMode = "bundled", InstallDirectoryWritable = writable });
        Assert.Equal("notify", result.Channel);
        Assert.False(result.SelfUpdateEligible);
    }

    [Theory]
    [InlineData(@"D:\Program Files (x86)\MeowChan")]
    [InlineData(@"C:\Program Files\MeowChan")]
    [InlineData(@"C:\Microsoft\WinGet\Packages\MeowChan\bin")]
    public void ProtectedWindowsLayoutDowngradesExplicitPortableChannel(string path)
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { Platform = OSPlatform.Windows,
            BaseDirectory = path, DistributionMarker = Marker("windows-portable-zip") });
        Assert.Equal("notify", result.Channel);
        Assert.False(result.SelfUpdateEligible);
    }

    [Theory]
    [InlineData("/home/fixture/MeowChan.AppImage", false, "notify")]
    [InlineData("/opt/MeowChan.AppImage", true, "notify")]
    [InlineData("/home/fixture/MeowChan.AppImage", true, "appimage")]
    public void AppImageChecksTheOuterFile(string path, bool writable, string channel)
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { AppImage = path,
            BaseDirectory = "/tmp/.mount_fixture/usr/lib/meowchan", AppImageWritable = writable });
        Assert.Equal(channel, result.Channel);
        Assert.Equal(channel == "appimage", result.SelfUpdateEligible);
    }

    [Fact]
    public void AppImageStampWithoutAppImageLayoutCanOnlyNotify()
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { DistributionChannel = "appimage" });
        Assert.Equal("notify", result.Channel);
        Assert.False(result.SelfUpdateEligible);
    }

    [Fact]
    public void FlatpakInfoAndTestRunAreRecognized()
    {
        Assert.Equal("flatpak", InstallChannelDetector.Detect(OfficialLinux with { FlatpakInfoExists = true }).Channel);
        Assert.Equal("ci-acceptance", InstallChannelDetector.Detect(OfficialLinux with { TestRun = true }).Channel);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PortableMarkerCannotOverrideAcceptanceOrTestRun(bool acceptance, bool testRun)
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { Platform = OSPlatform.Windows,
            BaseDirectory = @"C:\portable\MeowChan", DistributionMarker = Marker("windows-portable-zip"),
            Acceptance = acceptance, TestRun = testRun });

        Assert.False(result.SelfUpdateEligible);
        Assert.Equal("ci-acceptance", result.Channel);
    }

    [Theory]
    [InlineData("aur")]
    [InlineData("distro")]
    [InlineData("homebrew")]
    public void PortableMarkerCannotOverrideManagedBuildStamp(string managedChannel)
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { DistributionChannel = managedChannel,
            DistributionMarker = Marker("linux-tarball-bundled-vlc"), LinuxVideoMode = "bundled" });

        Assert.False(result.SelfUpdateEligible);
        Assert.False(InstallChannel.IsOfficial(result.Channel));
    }

    [Theory]
    [InlineData("/usr/lib/meowchan")]
    [InlineData("/home/fixture/unrelated/usr/lib/meowchan")]
    [InlineData("/tmp/.mount_unrelated/usr/lib/meowchan")]
    public void InheritedAppImageCannotTurnAnUnrelatedInstallationIntoAnAppImage(string directory)
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with { BaseDirectory = directory,
            AppImage = "/home/fixture/MeowChan.AppImage", AppImageWritable = true,
            DistributionMarker = Marker("appimage"), LinuxVideoMode = "bundled" });

        Assert.False(result.SelfUpdateEligible);
        Assert.Equal("notify", result.Channel);
    }

    [Theory]
    [InlineData("linux-tarball-bundled-vlc", "system")]
    [InlineData("linux-tarball-system-vlc", "bundled")]
    [InlineData("linux-tarball-system-vlc", null)]
    public async Task UnconfirmedTarballFlavorUsesTheReleasePage(string claimedChannel, string? videoMode)
    {
        InstallIdentity identity = InstallChannelDetector.Detect(OfficialLinux with {
            DistributionMarker = Marker(claimedChannel), LinuxVideoMode = videoMode });
        const string release = "https://github.com/grassmann-it/MeowChan/releases/tag/v1.2.1";
        const string downloads = "https://github.com/grassmann-it/MeowChan/releases/download/v1.2.1/";
        string[] names = ["MeowChan-1.2.1-linux-x64-bundled-vlc.tar.gz", "MeowChan-1.2.1-linux-x64-system-vlc.tar.gz"];
        object[] assets = names.Select(name => (object)new { name, browser_download_url = downloads + name }).ToArray();
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("v1.2.1", assets: assets),
            new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.2.0"), RuntimeIdentifier = "linux-x64",
                Channel = identity.Channel });

        UpdateCheckResult result = await fixture.Service.CheckAsync(UpdateCheckKind.Manual);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(release, result.Update?.DownloadUri.AbsoluteUri);
        Assert.False(identity.SelfUpdateEligible);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("""{"SchemaVersion":2,"Channel":"aur"}""")]
    [InlineData("""{"SchemaVersion":1,"Channel":"new-channel"}""")]
    [InlineData("""{"SchemaVersion":1,"Channel":"aur","Channel":"distro"}""")]
    [InlineData("""{"SchemaVersion":1,"Channel":"aur","Extra":true}""")]
    [InlineData("""{"schemaVersion":1,"Channel":"aur"}""")]
    [InlineData("""{"SchemaVersion":1,"Channel":"\ud800"}""")]
    public void InvalidMarkerFallsThroughToTheStamp(string json)
    {
        InstallIdentity result = InstallChannelDetector.Detect(OfficialLinux with {
            DistributionMarker = Encoding.UTF8.GetBytes(json), DistributionChannel = "distro" });
        Assert.Equal("distro", result.Channel);
        Assert.Contains("marker ignored", result.Reason);
    }

    [Fact]
    public void ValidMarkerAtLimitIsAcceptedButOneMoreByteIsIgnored()
    {
        const string json = """{"SchemaVersion":1,"Channel":"aur"}""";
        InstallFacts facts = OfficialLinux with { LinuxVideoMode = "bundled",
            DistributionMarker = Encoding.UTF8.GetBytes(json.PadRight(4096)) };
        Assert.Equal("aur", InstallChannelDetector.Detect(facts).Channel);
        InstallIdentity oversized = InstallChannelDetector.Detect(facts with {
            DistributionMarker = Encoding.UTF8.GetBytes(json.PadRight(4097)) });
        Assert.Equal("linux-tarball-bundled-vlc", oversized.Channel);
        Assert.Contains("marker ignored", oversized.Reason);
    }

    private static byte[] Marker(string channel) => Encoding.UTF8.GetBytes($$"""{"SchemaVersion":1,"Channel":"{{channel}}"}""");
}
