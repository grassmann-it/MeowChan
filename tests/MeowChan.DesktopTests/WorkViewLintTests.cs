using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// Plan AP5 §3.3 lint (pattern of the design-system lints): what a view shows is decided by WorkViewPolicy, not by
/// comparisons of the setting string scattered over Desktop/. The baseline may only shrink.
/// </summary>
public sealed class WorkViewLintTests
{
    // Storage normalization and the policy itself may name the views.
    private static readonly string[] Allowed = ["Desktop/Config.cs", "Desktop/WorkViews/"];

    // A view special case: comparing, matching or switching on a view name.
    private static readonly Regex ViewNameComparison = new(
        @"[!=]=\s*""(Barebones|Simple|Advanced)""|""(Barebones|Simple|Advanced)""\s*[!=]=" +
        @"|\b(is|or|and)\s+(not\s+)?""(Barebones|Simple|Advanced)""|\bcase\s+""(Barebones|Simple|Advanced)""" +
        @"|""(Barebones|Simple|Advanced)""\s*=>|Equals\([^;]*""(Barebones|Simple|Advanced)""",
        RegexOptions.Compiled);

    // Remaining comparisons of the stored setting with another stored value ("still the view this started in").
    // They are not special cases of one view, but they should become WorkView comparisons; the count only shrinks.
    private static readonly Regex WorkspaceModeComparison = new(@"WorkspaceMode\s*[!=]=|[!=]=\s*[\w.]*WorkspaceMode\b", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> WorkspaceModeComparisonBaseline = new(StringComparer.Ordinal)
    {
        ["Desktop/Shell/MainWindow.axaml.cs"] = 2,
        ["Desktop/Shell/MainWindow.Columns.cs"] = 5,
        ["Desktop/Shell/MainWindow.Fullscreen.cs"] = 4,
        ["Desktop/Shell/MainWindow.Presentation.cs"] = 3,
        ["Desktop/Shell/MainWindow.Restore.cs"] = 2,
        ["Desktop/Views/Settings/SettingsEditor.axaml.cs"] = 1,
    };

    [Fact]
    public void NoViewSpecialCaseOutsideThePolicy()
    {
        string[] offenders = SourceFiles().Where(file => !Allowed.Any(file.Relative.StartsWith))
            .SelectMany(file => Lines(file).Where(line => ViewNameComparison.IsMatch(line.Text))
                .Select(line => file.Relative + ":" + line.Number + ": " + line.Text.Trim())).ToArray();
        Assert.True(offenders.Length == 0, "Ask WorkViewPolicy (or WorkViewDensity) instead:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void WorkspaceModeComparisonsOnlyShrink()
    {
        Dictionary<string, int> counts = SourceFiles().Where(file => !Allowed.Any(file.Relative.StartsWith))
            .Select(file => (file.Relative, Count: Lines(file).Count(line => WorkspaceModeComparison.IsMatch(line.Text))))
            .Where(item => item.Count > 0).ToDictionary(item => item.Relative, item => item.Count, StringComparer.Ordinal);
        foreach ((string file, int count) in counts)
            Assert.True(count <= WorkspaceModeComparisonBaseline.GetValueOrDefault(file),
                $"{file} compares WorkspaceMode {count} times (baseline {WorkspaceModeComparisonBaseline.GetValueOrDefault(file)}); compare WorkView values.");
        // Lower the baseline with the code: a stale entry would let the count grow back.
        foreach ((string file, int baseline) in WorkspaceModeComparisonBaseline)
            Assert.True(counts.GetValueOrDefault(file) == baseline,
                $"{file}: {counts.GetValueOrDefault(file)} WorkspaceMode comparisons left; set its baseline to that number.");
    }

    // Every id used in XAML (wv:WorkViewElement.Id="…") or code (WorkViewElement.SetId(…, "…")) has a policy row.
    [Fact]
    public void EveryRegisteredIdHasAPolicyRow()
    {
        Regex xaml = new(@"WorkViewElement\.Id=""([^""]+)""");
        Regex code = new(@"WorkViewElement\.SetId\([^,]+,\s*""([^""]+)""\)");
        Regex fallback = new(@"(?:ApplyToolBarItem|ApplyToolBar)\([^;]*?""([a-z]+\.[^""]+)""");
        List<(string File, string Id)> uses = new();
        foreach (string path in Directory.EnumerateFiles(Root("Desktop"), "*.axaml", SearchOption.AllDirectories))
            uses.AddRange(xaml.Matches(File.ReadAllText(path)).Select(match => (Relative(path), match.Groups[1].Value)));
        foreach ((string Relative, string Path) file in SourceFiles())
        {
            // Code lines only: doc comments may show an example id.
            string text = string.Join("\n", Lines(file).Select(line => line.Text));
            uses.AddRange(code.Matches(text).Concat(fallback.Matches(text)).Select(match => (file.Relative, match.Groups[1].Value)));
        }
        Assert.NotEmpty(uses);
        string[] unknown = uses.Where(use => WorkViewPolicy.Find(use.Id) == null).Select(use => use.File + ": " + use.Id).ToArray();
        Assert.True(unknown.Length == 0, "No policy row for:\n" + string.Join("\n", unknown));
        // A registered id that is shared by two controls would make presence checks and the smoke's orphan check ambiguous.
        string[] duplicated = uses.Where(use => !use.Id.EndsWith(".other", StringComparison.Ordinal))
            .GroupBy(use => use.Id).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert.Empty(duplicated);
    }

    private static IEnumerable<(string Relative, string Path)> SourceFiles() =>
        Directory.EnumerateFiles(Root("Desktop"), "*.cs", SearchOption.AllDirectories).Select(path => (Relative(path), path));

    private static IEnumerable<(int Number, string Text)> Lines((string Relative, string Path) file) =>
        File.ReadLines(file.Path).Select((text, index) => (index + 1, text)).Where(line => !line.text.TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static string Relative(string path) => Path.GetRelativePath(Root(""), path).Replace('\\', '/');

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
