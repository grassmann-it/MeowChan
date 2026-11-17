using MeowChan.Desktop;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

// Platform check preparation: MEOWCHAN_UPDATE_SIMULATE=<version> triggers the update notification on any machine without a newer
// GitHub release. It must never touch the network, never be active unless set to a valid version, and never write update-state.json.
public sealed class UpdateSimulationTests
{
    private static UpdateServiceOptions Options(bool prereleases = false) => new()
    {
        CurrentVersion = SemanticVersion.Parse("1.1.0"), RuntimeIdentifier = "linux-x64", IncludePrereleases = prereleases
    };

    private static UpdateService Service(UpdateServiceTests.Fixture fixture, string? simulate, string? disable = null,
        bool prereleases = false) =>
        new(fixture.Client, fixture.Store, Options(prereleases), fixture.Time, () => disable, (_, _) => { }, () => simulate);

    [Theory]
    [InlineData("9.9.9")]
    [InlineData("v9.9.9")]
    [InlineData(" 9.9.9 ")]
    [InlineData("1.1.1")]
    public async Task ASimulatedNewerVersionIsAvailableWithoutNetworkOrState(string value)
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("0.0.1"));
        using UpdateService service = Service(fixture, value);
        foreach (UpdateCheckKind kind in Enum.GetValues<UpdateCheckKind>())
        {
            UpdateCheckResult result = await service.CheckAsync(kind);
            Assert.Equal(UpdateCheckStatus.Available, result.Status);
            Assert.Equal(value.Trim().TrimStart('v'), result.Update?.Version.ToString());
            Assert.StartsWith("https://github.com/grassmann-it/MeowChan/releases/tag/v", result.Update!.ReleaseUri.AbsoluteUri);
        }
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.False(File.Exists(fixture.Store.FilePath));
        Assert.Equal(value.Trim().TrimStart('v'), (await service.GetStateAsync()).FoundUpdate?.Version.ToString());
        // Nothing reaches a later start without the switch.
        Assert.Null(fixture.Store.Load().FoundUpdate);
        using UpdateService withoutSimulation = Service(fixture, null);
        Assert.Null((await withoutSimulation.GetStateAsync()).FoundUpdate);
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.0.9")]
    [InlineData("1.2.0-beta.1")] // a prerelease is not offered while prereleases are off, as in a real check
    public async Task ASimulatedVersionThatIsNotNewerIsUpToDate(string value)
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("0.0.1"));
        using UpdateService service = Service(fixture, value);
        UpdateCheckResult result = await service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.False(File.Exists(fixture.Store.FilePath));
    }

    [Fact]
    public async Task ASimulatedPrereleaseIsOfferedWhenPrereleasesAreOn()
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("0.0.1"));
        using UpdateService service = Service(fixture, "1.2.0-beta.1", prereleases: true);
        UpdateCheckResult result = await service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("2")]
    [InlineData("2.0")]
    [InlineData("1.2.3.4")]
    [InlineData("x.y.z")]
    [InlineData("01.2.3")]
    [InlineData("2.0.0 --")]
    [InlineData("true")]
    public async Task UnsetOrInvalidValuesLeaveTheSwitchOffAndRunARealCheck(string? value)
    {
        Assert.Null(UpdateService.ParseSimulated(value));
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("0.0.1"));
        fixture.Handler.Body = UpdateServiceTests.Release("1.5.0");
        using UpdateService service = Service(fixture, value);
        UpdateCheckResult result = await service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("1.5.0", result.Update?.Version.ToString());
        Assert.Equal(1, fixture.Handler.Requests);
        // A real check persists as before.
        Assert.Equal(fixture.Time.GetUtcNow(), fixture.Store.Load().LastCheckedUtc);
        Assert.Equal("1.5.0", fixture.Store.Load().FoundUpdate?.Version.ToString());
    }

    [Fact]
    public async Task SkippingASimulatedVersionStaysInMemoryAndOnlyHidesAutomaticAnnouncements()
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("0.0.1"));
        using UpdateService service = Service(fixture, "9.9.9");
        Assert.Equal(UpdateCheckStatus.Available, (await service.CheckAsync(UpdateCheckKind.Automatic)).Status);
        await service.SkipVersionAsync(SemanticVersion.Parse("9.9.9"));
        Assert.Equal(UpdateCheckStatus.Skipped, (await service.CheckAsync(UpdateCheckKind.Automatic)).Status);
        Assert.Equal(UpdateCheckStatus.Available, (await service.CheckAsync(UpdateCheckKind.Manual)).Status);
        Assert.False(File.Exists(fixture.Store.FilePath));
        Assert.Null(fixture.Store.Load().SkippedVersion);
    }

    [Fact]
    public async Task TheRealSkippedVersionAndFoundUpdateAreLeftAloneWhileSimulating()
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("0.0.1"));
        fixture.Handler.Body = UpdateServiceTests.Release("1.5.0");
        using (UpdateService real = Service(fixture, null))
        {
            await real.CheckAsync(UpdateCheckKind.Manual);
            await real.SkipVersionAsync(SemanticVersion.Parse("1.5.0"));
        }
        string before = File.ReadAllText(fixture.Store.FilePath);
        using UpdateService simulated = Service(fixture, "9.9.9");
        await simulated.CheckAsync(UpdateCheckKind.Manual);
        await simulated.SkipVersionAsync(SemanticVersion.Parse("9.9.9"));
        Assert.Equal(before, File.ReadAllText(fixture.Store.FilePath));
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task TheDisableSwitchStillWinsAndAutomaticChecksOffStillApply()
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("0.0.1"));
        using UpdateService disabled = Service(fixture, "9.9.9", disable: "1");
        Assert.Equal(UpdateCheckStatus.Disabled, (await disabled.CheckAsync(UpdateCheckKind.Manual)).Status);
        using UpdateService off = new(fixture.Client, fixture.Store, Options() with { AutomaticChecksEnabled = false }, fixture.Time,
            () => null, (_, _) => { }, () => "9.9.9");
        Assert.Equal(UpdateCheckStatus.Disabled, (await off.CheckAsync(UpdateCheckKind.Automatic)).Status);
        Assert.Equal(UpdateCheckStatus.Available, (await off.CheckAsync(UpdateCheckKind.Manual)).Status);
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task TheSimulatedUpdateReachesTheToastDecisionLikeARealOne()
    {
        using UpdateServiceTests.Fixture fixture = new(UpdateServiceTests.Release("0.0.1"));
        using UpdateService service = Service(fixture, "9.9.9");
        UpdateCheckResult result = await service.CheckAsync(UpdateCheckKind.Automatic);
        UpdateNotifier notifier = new();
        UpdateView view = new(Update: result.Update);
        Assert.Equal("9.9.9", notifier.Evaluate(view, automaticChecks: true, informationAllowed: true)?.Version.ToString());
        Assert.Null(notifier.Evaluate(view, automaticChecks: true, informationAllowed: true)); // once per version and start
    }

    [Fact]
    public void TheSwitchIsOnlyReadFromTheDocumentedVariable() => Assert.Equal("MEOWCHAN_UPDATE_SIMULATE", UpdateService.SimulateVariable);
}
