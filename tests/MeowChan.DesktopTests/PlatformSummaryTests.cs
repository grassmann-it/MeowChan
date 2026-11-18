using MeowChan.Desktop;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopTests;

// Platform check preparation: the start-up line a tester pastes into a report. Pure formatting is tested here; the real values
// (backend, render mode, scaling, resolved fonts) need a window and are read by the normal smoke (see the log line there).
public sealed class PlatformSummaryTests
{
    private static PlatformSummary.Facts Sample(string os = "Linux 6.1 X64") => new("1.1.0", os, ".NET10, Avalonia 12.1.4",
        "Avalonia.X11 12.1.4 (XID)", "software", "1.5 (screens 1: 1.5)", "Custom", "animations ON, decided by the setting (On)",
        "Inter", "Noto Sans", "Yu Gothic UI=yes,Microsoft YaHei UI=missing");

    [Fact]
    public void TheLineHasEveryFieldTheChecklistAsksFor()
    {
        string line = PlatformSummary.Format(Sample());
        foreach (string key in new[] { "MeowChan 1.1.0", "os=", "runtime=", "backend=", "render=", "scaling=", "chrome=", "motion=", "font.ui=", "font.post=", "cjk=" })
            Assert.Contains(key, line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void AHomeDirectoryNeverSurvivesInTheLine()
    {
        string line = PlatformSummary.Sanitize("font.ui=Inter | file=/home/someone/fonts/x.ttf | other=C:\\Users\\Someone\\f", "/home/someone");
        Assert.DoesNotContain("/home/someone", line);
        Assert.Contains("~/fonts/x.ttf", line);
        line = PlatformSummary.Sanitize("a=C:\\Users\\Someone\\f b=C:/Users/Someone/g", "C:\\Users\\Someone");
        Assert.DoesNotContain("Someone", line);
        Assert.Equal("a=~\\f b=~/g", line);
        Assert.Equal("a b", PlatformSummary.Sanitize("a\nb", "/home/x").Replace("  ", " "));
        Assert.Equal("keep", PlatformSummary.Sanitize("keep", null)); // no profile directory known: nothing to remove
        Assert.Equal("keep /", PlatformSummary.Sanitize("keep /", "/")); // a root "home" must not erase every slash
    }

    [Fact]
    public void TheRealHomeIsRemovedFromTheLine()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home) || home.Length < 2) return;
        Assert.DoesNotContain(home, PlatformSummary.Format(Sample("Linux at " + home + "/x")), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Linux 6.18.49-1-MANJARO #1 SMP PREEMPT_DYNAMIC Tue Nov  3 10:00:00 UTC 2026", "Linux 6.18.49-1-MANJARO")]
    [InlineData("Darwin 23.5.0 Darwin Kernel Version 23.5.0: Wed May  1 20:12:58 PDT 2024; root:xnu-10063.121.3~5/RELEASE_ARM64_T6000", "Darwin 23.5.0")]
    [InlineData("Microsoft Windows 10.0.22631", "Microsoft Windows 10.0.22631")]
    public void TheOsTextLosesItsBuildTail(string description, string expected) => Assert.Equal(expected, PlatformSummary.ShortOs(description));

    [Fact]
    public void TheOsTextIsBounded() => Assert.True(PlatformSummary.ShortOs(new string('x', 500)).Length <= 80);

    [Fact]
    public void BeforeTheWindowOpenedThereIsNoLineAndNoDiagnosticsRowForIt()
    {
        if (PlatformSummary.Line == null)
            Assert.DoesNotContain(PlatformDiagnostics.Entries(), entry => entry.Key == PlatformDiagnostics.SummaryKey);
    }

    [Theory]
    // environment motion/reduced, setting, OS reduced -> expected fragments
    [InlineData("off", null, "System", false, "animations OFF, decided by the environment (MEOWCHAN_MOTION=off)")]
    [InlineData("on", null, "Off", true, "animations ON, decided by the environment (MEOWCHAN_MOTION=on)")]
    [InlineData(null, "1", "On", false, "animations OFF, decided by the environment (MEOWCHAN_REDUCED_MOTION=1)")]
    [InlineData(null, null, "Off", false, "animations OFF, decided by the setting (Off)")]
    [InlineData(null, null, "On", true, "animations ON, decided by the setting (On)")]
    [InlineData(null, null, "System", true, "animations OFF, decided by the OS signal (detected ON)")]
    [InlineData(null, null, "System", false, "animations ON, decided by the OS signal (detected OFF)")]
    public void TheMotionSourceNamesWhoDecides(string? motion, string? reduced, string setting, bool os, string expected)
    {
        OsMotionStatus status = OsMotionStatus.FromValue("t", os);
        string text = MotionPolicy.DescribeSource(name => name switch { "MEOWCHAN_MOTION" => motion, "MEOWCHAN_REDUCED_MOTION" => reduced, _ => null },
            setting, os, status);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void AnUnreadOsSignalIsSaidSoInsteadOfLookingLikeOff()
    {
        string text = MotionPolicy.DescribeSource(_ => null, "System", false, OsMotionStatus.Unavailable("NSWorkspace", "class missing"));
        Assert.Contains("UNAVAILABLE (class missing)", text);
        Assert.StartsWith("animations ON", text); // behaviour on failure is unchanged: motion stays allowed
    }
}
