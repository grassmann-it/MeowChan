using MeowChan.Desktop;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopTests;

// Platform check preparation: the OS reduce-motion read must say what happened. "Signal not read" and "signal says off" are
// different answers for a tester on Windows or macOS, and a failed read still falls back to the setting.
[Collection("OsMotionSignalStatus")]
public sealed class OsMotionSignalDiagnosticsTests : IDisposable
{
    public OsMotionSignalDiagnosticsTests() => OsMotionSignal.ResetForTests();
    public void Dispose() => OsMotionSignal.ResetForTests();

    [Fact]
    public void DetectedOnDetectedOffAndUnavailableAreThreeDifferentStates()
    {
        OsMotionStatus on = OsMotionStatus.FromValue("test", true), off = OsMotionStatus.FromValue("test", false),
            none = OsMotionStatus.Unavailable("test", "boom");
        Assert.Equal(true, on.Reduced);
        Assert.Equal(false, off.Reduced);
        Assert.Null(none.Reduced);
        Assert.Null(OsMotionStatus.None.Reduced);
        Assert.Equal(4, new[] { on.State, off.State, none.State, OsMotionStatus.None.State }.Distinct().Count());
        Assert.Contains("ON", on.ToString());
        Assert.Contains("OFF", off.ToString());
        Assert.Contains("unavailable", none.ToString());
        Assert.Contains("boom", none.ToString());
        Assert.Contains("setting decides", none.ToString());
    }

    [Fact]
    public void RecordingKeepsTheLatestAndReportsOnlyChanges()
    {
        Assert.Equal(OsMotionState.NotRead, OsMotionSignal.Status.State);
        Assert.True(OsMotionSignal.Record(OsMotionStatus.FromValue("test", false)));
        Assert.False(OsMotionSignal.Record(OsMotionStatus.FromValue("test", false))); // a window activation re-reads: silent
        Assert.True(OsMotionSignal.Record(OsMotionStatus.FromValue("test", true)));
        Assert.Equal(OsMotionState.DetectedOn, OsMotionSignal.Status.State);
        Assert.True(OsMotionSignal.Record(OsMotionStatus.Unavailable("test", "gone")));
        Assert.Null(OsMotionSignal.Status.Reduced);
    }

    [Fact]
    public void ReasonsAreOneShortLine()
    {
        OsMotionStatus status = OsMotionStatus.Unavailable("test", "first line\nsecond line with /home/someone/file");
        Assert.Equal("first line", status.Reason);
        Assert.DoesNotContain("\n", OsMotionStatus.Unavailable("test", new string('x', 1000)).Reason);
        Assert.True(OsMotionStatus.Unavailable("test", new string('x', 1000)).Reason!.Length < 200);
    }

    [Fact]
    public void ReadingWithoutAReaderIsUnavailableWithAReasonAndNeverThrows()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return; // the real readers are checked on those machines
        OsMotionStatus status = OsMotionSignal.ReadPlatform();
        Assert.Equal(OsMotionState.Unavailable, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Reason));
        Assert.Null(OsMotionSignal.ReadNow());
        Assert.Equal(OsMotionState.Unavailable, OsMotionSignal.Status.State);
    }

    [Theory]
    [InlineData(true, "detected ON")]
    [InlineData(false, "detected OFF")]
    public void DiagnosticsEntryNamesTheDetectedValue(bool reduced, string expected)
    {
        OsMotionSignal.Record(OsMotionStatus.FromValue("SystemParametersInfo", reduced));
        DiagnosticEntry entry = Assert.Single(PlatformDiagnostics.Entries(), item => item.Key == PlatformDiagnostics.MotionKey);
        Assert.Contains(expected, entry.Summary);
        Assert.Contains("SystemParametersInfo", entry.Details);
        Assert.Equal(DiagnosticKind.Information, entry.Kind);
        Assert.False(entry.IsProblem);
        Assert.Null(entry.Target);
    }

    [Fact]
    public void DiagnosticsEntryShowsWhyTheSignalWasNotRead()
    {
        DiagnosticEntry unread = Assert.Single(PlatformDiagnostics.Entries(), item => item.Key == PlatformDiagnostics.MotionKey);
        Assert.Contains("not read", unread.Summary);
        OsMotionSignal.Record(OsMotionStatus.Unavailable("NSWorkspace", "the NSWorkspace class was not found"));
        DiagnosticEntry failed = Assert.Single(PlatformDiagnostics.Entries(), item => item.Key == PlatformDiagnostics.MotionKey);
        Assert.Contains("UNAVAILABLE", failed.Summary);
        Assert.Contains("NSWorkspace class was not found", failed.Details);
        Assert.DoesNotContain("detected", failed.Summary);
    }

    [Fact]
    public void ThePlatformEntryIsNeverARetryOrProblemTarget()
    {
        DiagnosticEntry[] entries = PlatformDiagnostics.Entries().ToArray();
        Assert.All(entries, entry => Assert.False(entry.IsProblem));
        Assert.Empty(DiagnosticRetryScope.Capture(entries));
    }
}
