using System.Runtime.CompilerServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// UX round 3 (13.11.2026): layout rules that are plain functions of the window size.
public sealed class UxRound3LayoutTests
{
    [Theory]
    [InlineData(1440, true)]
    [InlineData(1380, true)]
    [InlineData(886, true)]
    [InlineData(760, true)]
    [InlineData(640, true)]
    [InlineData(639.5, false)]
    [InlineData(480, false)]
    [InlineData(0, true)] // Not measured yet: the roomy form, corrected by the first layout pass.
    public void TheBannerActionSitsBesideTheMessageFromSixHundredFortyDip(double width, bool beside) =>
        Assert.Equal(beside, ShellFeedbackPlacement.ActionBeside(width));

    [Fact]
    public void TheBannerPlacementDoesNotDependOnTheWindowHeight()
    {
        string source = File.ReadAllText(Root("Desktop/Shell/MainWindow.Presentation.cs"));
        Assert.Contains("ShellFeedbackPlacement.ActionBeside(Bounds.Width)", source);
        Assert.DoesNotContain("Grid.SetColumn(diagnostics, retainedErrorLayout", source);
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
