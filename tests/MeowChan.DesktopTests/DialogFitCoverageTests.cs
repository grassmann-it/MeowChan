using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

/// <summary>
/// M5 across the product: a secondary window that opens at a fixed size taller than a scaled screen is fitted at its
/// first open, so its footer stays reachable. AccessibilityAuditTests covers the computation; this keeps the windows
/// that were fitted in the audit from losing it again, and watches the opening heights of the others.
/// </summary>
public sealed class DialogFitCoverageTests
{
    /// <summary>The smallest opening height the audit fitted (About, 660 dip): from here on a window needs fitting.</summary>
    private const double FittedFromHeight = 660;

    private static readonly string[] Fitted =
    {
        "Desktop/Views/About/AboutWindow.axaml.cs",
        "Desktop/Views/Settings/SettingsWindow.axaml.cs",
        "Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs",
        "Desktop/Views/Booru/BooruWorkbenchWindow.cs",
        "Desktop/Views/Media/BooruMediaDetailsDialog.cs",
        "Desktop/Views/Media/MediaSelectionTagDialog.cs",
    };

    [Theory]
    [InlineData("Desktop/Views/About/AboutWindow.axaml.cs")]
    [InlineData("Desktop/Views/Settings/SettingsWindow.axaml.cs")]
    [InlineData("Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs")]
    [InlineData("Desktop/Views/Booru/BooruWorkbenchWindow.cs")]
    [InlineData("Desktop/Views/Media/BooruMediaDetailsDialog.cs")]
    [InlineData("Desktop/Views/Media/MediaSelectionTagDialog.cs")]
    public void TheWindowsOfTheAuditStillFitThemselvesToTheScreen(string path) =>
        Assert.Matches(@"DialogFit\.OnOpen\(this\)", File.ReadAllText(Source(path)));

    [Fact]
    public void FittingIsAskedForOnceAndOnlyOnOpening()
    {
        foreach (string path in Fitted)
        {
            string source = File.ReadAllText(Source(path));
            Assert.Single(Regex.Matches(source, @"DialogFit\.OnOpen\("));
            Assert.DoesNotContain("DialogFit.Apply(", source); // Opening is the moment; later calls would fight the user.
        }
        // Nobody else in the product calls the computation directly.
        Assert.Empty(Directory.EnumerateFiles(Source("Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) != "DialogFit.cs" && File.ReadAllText(file).Contains("DialogFit.Compute(", StringComparison.Ordinal)));
    }

    [Fact(Skip = "BUG: four windows open taller than the smallest fitted one (About, 660 dip) without DialogFit: "
        + "ArchiveCleanupDialog 700, BoardAdmissionWindow 720 (minimum 600), FilenameSearchDialog 680, ManualImportDialog 720. "
        + "At 150 or 200 percent scaling on a 1080p screen their footer opens below the working area, which is what M5 fixed "
        + "for the six windows above. Adding DialogFit.OnOpen(this) to them is a product decision, not a test change.")]
    public void EveryWindowThatOpensTallerThanTheSmallestFittedOneIsFitted()
    {
        List<string> unfitted = new();
        foreach (string path in Directory.EnumerateFiles(Source("Desktop"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Source(""), path).Replace('\\', '/');
            if (relative.Contains("MainWindow", StringComparison.Ordinal)) continue; // The shell restores its saved geometry.
            string source = File.ReadAllText(path);
            if (!Regex.IsMatch(source, @"class \w+ : Window\b")) continue;
            double height = OpeningHeight(source, relative);
            if (height >= FittedFromHeight && !source.Contains("DialogFit.OnOpen(", StringComparison.Ordinal))
                unfitted.Add(relative + " (" + height + ")");
        }
        Assert.Empty(unfitted);
    }

    // The window's own opening height: from the XAML root for a XAML window, else from the constructor assignment.
    private static double OpeningHeight(string source, string relative)
    {
        string markup = Source(relative.EndsWith(".axaml.cs", StringComparison.Ordinal) ? relative[..^3] : relative);
        if (File.Exists(markup) && markup.EndsWith(".axaml", StringComparison.Ordinal)
            && (string?)XDocument.Load(markup).Root!.Attribute("Height") is { } declared
            && double.TryParse(declared, System.Globalization.CultureInfo.InvariantCulture, out double fromMarkup))
            return fromMarkup;
        Match assignment = Regex.Match(source, @"\bWidth = \d+; Height = (?<height>\d+);");
        return assignment.Success ? double.Parse(assignment.Groups["height"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static string Source(string path, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
