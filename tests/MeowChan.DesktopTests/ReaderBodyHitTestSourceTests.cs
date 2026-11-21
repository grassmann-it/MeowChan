using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

// F07/F08: the reader body must not decide "is this point over text" with TextLayout.HitTestPoint(...).IsInside, which answers false for
// every line after the first in many layouts (so links and references on later lines were underlined but dead). The real-window proof is the
// smoke check ReaderIdeaChecks.TextLinks; this guard keeps the unreliable answer out of the hit-test path.
public sealed class ReaderBodyHitTestSourceTests
{
    [Fact]
    public void TheBodyWalksTheLinesInsteadOfTrustingTheLayoutsIsInsideAnswer()
    {
        string source = File.ReadAllText(Root("Desktop/ReaderBodyTextBlock.cs")).ReplaceLineEndings("\n");
        Assert.DoesNotContain(".IsInside", source);
        Assert.DoesNotContain("HitTestPoint(", source);
        Assert.Contains("private int? CharacterIndexAt(Point point)", source);
        Assert.Contains("GetCharacterHitFromDistance(x)", source);
    }

    private static string Root(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
