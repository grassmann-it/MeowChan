using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

/// <summary>
/// M5 across the product: a secondary window that opens at a fixed size taller than a scaled screen is fitted at its
/// first open, so its footer stays reachable. AccessibilityAuditTests covers the computation; this keeps the windows
/// that were fitted from losing it again, and requires it of every window that opens at least as tall as the smallest one.
/// A fit may lower a window's minimum on a small screen; the body then scrolls and the footer with Close stays in view.
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
        "Desktop/ArchiveCleanupDialog.cs",
        "Desktop/Views/Admission/BoardAdmissionWindow.cs",
        "Desktop/Views/Archive/FilenameSearchDialog.cs",
        "Desktop/Views/Media/ManualImportDialog.cs",
        "Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs",
    };

    [Theory]
    [InlineData("Desktop/Views/About/AboutWindow.axaml.cs")]
    [InlineData("Desktop/Views/Settings/SettingsWindow.axaml.cs")]
    [InlineData("Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs")]
    [InlineData("Desktop/Views/Booru/BooruWorkbenchWindow.cs")]
    [InlineData("Desktop/Views/Media/BooruMediaDetailsDialog.cs")]
    [InlineData("Desktop/Views/Media/MediaSelectionTagDialog.cs")]
    [InlineData("Desktop/ArchiveCleanupDialog.cs")]
    [InlineData("Desktop/Views/Admission/BoardAdmissionWindow.cs")]
    [InlineData("Desktop/Views/Archive/FilenameSearchDialog.cs")]
    [InlineData("Desktop/Views/Media/ManualImportDialog.cs")]
    [InlineData("Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs")]
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

    [Fact]
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

    [Fact]
    public void TheImportDialogKeepsItsFooterOutsideTheScrollingBodySoALoweredMinimumScrolls()
    {
        string source = File.ReadAllText(Source("Desktop/Views/Media/ManualImportDialog.cs"));
        Assert.Contains("RowDefinitions = new RowDefinitions(\"Auto,*,Auto\")", source);
        Assert.Matches(@"ScrollViewer scroll = new\(\) \{ Content = body,", source);
        Assert.Contains("Grid.SetRow(scroll, 1);", source);
        Assert.Contains("Grid.SetRow(footer, 2);", source);
        Assert.Contains("footer.Children.Add(_close);", source);
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
