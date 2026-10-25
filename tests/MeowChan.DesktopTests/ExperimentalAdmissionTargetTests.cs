using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ExperimentalAdmissionTargetTests
{
    private static ExperimentalSourceActivationTarget Target() => new(Guid.NewGuid(), "/owned/profile",
        "meowchan.source.kohlchan", "kohlchan", "Kohlchan", "https://kohlchan.net/demo/res/123.html",
        "selected.plugin.json", "execution", "fingerprint", new string('a', 64), 10, 2, 4, false, false);

    [Fact]
    public void MissingCurrentAuthorityCannotTurnAFrozenGroupIntoNormalAdmission()
    {
        Assert.False(MainWindow.IsSameExperimentalAdmissionTarget(Target(), null));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("profile")]
    [InlineData("owner")]
    [InlineData("source")]
    [InlineData("url")]
    [InlineData("candidate")]
    [InlineData("execution")]
    [InlineData("fingerprint")]
    [InlineData("assembly")]
    [InlineData("permissionIntent")]
    public void EarlierBatchDialogCannotRetargetLaterGroupToAnotherAuthorityOrBuild(string change)
    {
        var frozen = Target();
        var current = change switch
        {
            "session" => frozen with { SessionId = Guid.NewGuid() },
            "profile" => frozen with { ProfileId = "/other/profile" },
            "owner" => frozen with { PluginOwnerId = "other-owner" },
            "source" => frozen with { SourceId = "other-source" },
            "url" => frozen with { Url = "https://kohlchan.net/demo/res/124.html" },
            "candidate" => frozen with { CandidateId = "other.plugin.json" },
            "execution" => frozen with { ExecutionRevision = "other-execution" },
            "fingerprint" => frozen with { CompilationFingerprint = "other-fingerprint" },
            "permissionIntent" => frozen with { RequestsMismatchPermission = false },
            _ => frozen with { AssemblySha256 = new string('b', 64) }
        };
        Assert.False(MainWindow.IsSameExperimentalAdmissionTarget(frozen, current));
    }

    [Fact]
    public void SameSourceExplicitlyEnabledInTheMeantimeRetainsItsCurrentStrictPolicy()
    {
        var frozen = Target();
        var current = frozen with { ProfileRevision = 17, LifecycleRevision = 3, PolicyRevision = 5,
            IsEnabled = true, IsGranted = false, SourceName = "Localized source name" };
        Assert.True(MainWindow.IsSameExperimentalAdmissionTarget(frozen, current));
        Assert.False(current.IsGranted);
    }
}
