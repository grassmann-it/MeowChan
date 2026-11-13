using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MeowChan.DesktopTests;

// Source seam only. The shared 48 px title bar (WorkspaceMetrics.TitleBarHeight) takes client height, so a
// framed window needs room for title bar + heading + footer + a usable body at its MinHeight. The pixel
// check at the minimum size is the dialog-frame smoke (MEOWCHAN_DIALOG_FRAME_ONLY=1).
public sealed class DialogMinimumHeightTests
{
    /// <summary>Title bar 48 + heading ~50 + footer ~64 + about 100 px of body.</summary>
    private const int MinimumFramedHeight = 260;

    private static readonly Regex WindowMinimum = new(
        @"(?:MinWidth\s*=\s*""?\d+""?[;,\s]+|\bHeight\s*=\s*""?\d+""?[;,\s]+)MinHeight\s*=\s*""?(\d+)",
        RegexOptions.Compiled);

    [Fact]
    public void FramedWindowsKeepRoomForTitleBarFooterAndBody()
    {
        string root = Source("Desktop");
        var tooLow = new List<string>();
        int checkedWindows = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".axaml", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains("WindowChrome.", StringComparison.Ordinal) && !text.Contains("<WindowChromeBar", StringComparison.Ordinal)
                && !text.Contains("DialogFrame", StringComparison.Ordinal)) continue;
            foreach (Match match in WindowMinimum.Matches(text))
            {
                checkedWindows++;
                int minimum = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (minimum < MinimumFramedHeight)
                    tooLow.Add(Path.GetRelativePath(root, file) + ": MinHeight " + minimum);
            }
        }
        Assert.True(checkedWindows >= 20, "The scan found only " + checkedWindows + " window minimums; the pattern no longer matches.");
        Assert.True(tooLow.Count == 0, "Raise these minimums above " + MinimumFramedHeight + ": " + string.Join(", ", tooLow));
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
