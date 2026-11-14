using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace MeowChan.DesktopTests.DesignLint;

// Design-system lint (UX-DESIGNSYSTEM-2026-11-13, package P9). Scans Desktop/**/*.axaml and *.cs as text, without an
// Avalonia application, and compares the result with the checked-in baseline design-lint-baseline.txt (today's debt).
//
//   * A violation that is not in the baseline fails the test of its rule: fix it or use the token/class instead.
//   * A baseline entry that no longer occurs fails BaselineHasNoStaleEntries: the debt shrank, so regenerate the baseline.
//     This is the ratchet: the list can only get shorter.
//
// Regenerate baseline and red list after burning violations down (writes both files in the source tree):
//   MEOWCHAN_DESIGN_LINT_UPDATE=1 dotnet test tests/MeowChan.DesktopTests --filter FullyQualifiedName~DesignLint
// The red list (red-list.md) names every current violation with its line; the baseline carries counts per
// (rule, file, snippet) and no line numbers, so moving code never touches it.
public sealed class DesignLintTests
{
    private const string UpdateVariable = "MEOWCHAN_DESIGN_LINT_UPDATE";

    public static IEnumerable<object[]> Rules => DesignRules.All.Select(r => new object[] { r });

    [Theory, MemberData(nameof(Rules))]
    public void NoNewViolationsBeyondBaseline(string rule)
    {
        Dictionary<string, int> baseline = Lint.Baseline;
        List<string> fresh = new();
        foreach (IGrouping<string, DesignViolation> group in Lint.Violations.Where(v => v.Rule == rule).GroupBy(Key))
        {
            baseline.TryGetValue(group.Key, out int allowed);
            if (group.Count() <= allowed) continue;
            foreach (DesignViolation v in group.Skip(allowed))
                fresh.Add($"  {v.File}:{v.Line}  {v.Snippet}");
        }
        Assert.True(fresh.Count == 0,
            $"New design-system violations of rule '{rule}' (not in design-lint-baseline.txt). Use the Workspace tokens/classes; "
            + "see docs in tests/MeowChan.DesktopTests/DesignLint/red-list.md and UX-DESIGNSYSTEM-2026-11-13.md.\n" + string.Join("\n", fresh));
    }

    [Fact]
    public void BaselineHasNoStaleEntries()
    {
        Dictionary<string, int> current = Lint.Violations.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        List<string> stale = new();
        foreach ((string key, int allowed) in Lint.Baseline)
        {
            current.TryGetValue(key, out int actual);
            if (actual < allowed) stale.Add($"  {key.Replace('\t', ' ')}: baseline {allowed}, now {actual}");
        }
        Assert.True(stale.Count == 0,
            "The design-lint baseline lists violations that are fixed. Regenerate it so the debt ratchets down: "
            + $"{UpdateVariable}=1 dotnet test tests/MeowChan.DesktopTests --filter FullyQualifiedName~DesignLint\n" + string.Join("\n", stale));
    }

    [Fact]
    public void ScanSeesTheDesktopSources()
    {
        // Guards against the lint silently scanning nothing (moved folders, wrong root).
        Assert.Contains(Lint.Violations, v => v.File.EndsWith(".axaml", StringComparison.Ordinal));
        Assert.Contains(Lint.Violations, v => v.File.EndsWith(".cs", StringComparison.Ordinal));
        Assert.DoesNotContain(Lint.Violations, v => v.File == DesignLintScanner.StylesFile);
        Assert.Equal(new double[] { 11, 12, 13, 14, 16, 20 }, DesignLintScanner.ReadTypeScale(Path.Combine(RepositoryRoot(), DesignLintScanner.StylesFile)));
    }

    [Fact]
    public void ScanIsFast()
    {
        _ = Lint.Violations; // forces the scan
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        DesignLintScanner.ScanRepository(RepositoryRoot());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Design lint scan took {sw.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void PermanentExceptionsHaveReasonsAndStillMatchCode()
    {
        foreach (DesignException e in DesignLintScanner.Exceptions)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Reason));
            string path = Path.Combine(RepositoryRoot(), e.File);
            Assert.True(File.Exists(path), e.File + " no longer exists; drop the exception.");
            Assert.Contains(e.SnippetContains, File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LayoutContractsHaveReasonsAndStillMatchTheSource()
    {
        foreach (DesignLayoutContract c in DesignLintScanner.LayoutContracts)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Reason));
            string path = Path.Combine(RepositoryRoot(), c.File);
            Assert.True(File.Exists(path), c.File + " no longer exists; drop the contract.");
            string content = File.ReadAllText(path);
            // Without the contracts the general rule finds the statement; with them it is covered, nothing else is.
            Assert.Contains(DesignLintScanner.ScanFileWith(c.File, content, null, Array.Empty<DesignLayoutContract>()),
                v => v.Rule == c.Rule && v.Snippet == c.Snippet);
            Assert.DoesNotContain(DesignLintScanner.ScanFile(c.File, content), v => v.Rule == c.Rule && v.Snippet == c.Snippet);
            Assert.NotNull(HeaderIoBody(content));
        }
    }

    [Fact]
    public void HeaderIoSpacingStaysFourAndTwo()
    {
        string content = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop/Shell/MainWindow.Transfer.cs"));
        Assert.Equal(new[] { "ColumnSpacing=4", "RowSpacing=2" }, HeaderIoSpacingAssignments(content));
        Assert.NotEqual(new[] { "ColumnSpacing=4", "RowSpacing=2" }, HeaderIoSpacingAssignments(content.Replace("RowSpacing = 2;", "RowSpacing = 8;", StringComparison.Ordinal)));
        Assert.NotEqual(new[] { "ColumnSpacing=4", "RowSpacing=2" }, HeaderIoSpacingAssignments(content.Replace("ColumnSpacing = 4;", "ColumnSpacing = 8;", StringComparison.Ordinal)));
        Assert.DoesNotContain(DesignLintScanner.LayoutContracts, c => c.File == TransferFile);
        string body = HeaderIoBody(content)!;
        Assert.DoesNotContain(DesignLintScanner.ScanFileWith(TransferFile, body, null, Array.Empty<DesignLayoutContract>()),
            v => v.Rule == DesignRules.SpacingOffGrid);
    }

    private static string? HeaderIoBody(string content)
    {
        string stripped = DesignLintScanner.StripCodeComments(content);
        return DesignLintScanner.TryFindContractBody(stripped, SyntheticHeaderContracts[0], out int start, out int end) ? stripped[start..end] : null;
    }

    private static string[] HeaderIoSpacingAssignments(string content)
    {
        string? body = HeaderIoBody(content);
        Assert.NotNull(body);
        return System.Text.RegularExpressions.Regex.Matches(body!, @"\b(?<n>(?:Column|Row)Spacing)\s*=\s*(?<v>[^;]+);")
            .Select(m => m.Groups["n"].Value + "=" + m.Groups["v"].Value.Trim()).ToArray();
    }

    // ---- the scanner itself, on synthetic sources --------------------------------------------------------------------

    private static string[] Run(string file, string content)
    {
        if (file.EndsWith(".axaml", StringComparison.Ordinal) && !content.StartsWith("<UserControl", StringComparison.Ordinal))
            content = "<UserControl>" + content + "</UserControl>";
        return DesignLintScanner.ScanFile(file, content, new double[] { 11, 12, 13, 14, 16, 20 }).Select(v => v.Rule + "|" + v.Snippet).ToArray();
    }

    [Fact]
    public void FontSizeLiteralsAreSplitIntoOnScaleAndOffScale()
    {
        string[] xaml = Run("Desktop/X.axaml", "<TextBlock FontSize=\"12\" /><TextBlock FontSize=\"23\" /><TextBlock FontSize=\"{StaticResource Workspace.Text.Meta}\" />"
            + "<Style Selector=\"TextBlock.a\"><Setter Property=\"FontSize\" Value=\"15\" /></Style>");
        Assert.Equal(new[] { "font-size-literal|FontSize=\"12\"", "font-size-off-scale|FontSize=\"23\"", "font-size-off-scale|FontSize=\"15\"" }, xaml);
        string[] code = Run("Desktop/X.cs", "var a = new TextBlock { FontSize = 12, FontSize = 18d }; b.FontSize = Workspace.Size; // FontSize = 99");
        Assert.Equal(new[] { "font-size-literal|FontSize=12", "font-size-off-scale|FontSize=18" }, code);
    }

    [Fact]
    public void ColourLiteralsAreFoundOutsideStringsAndComments()
    {
        Assert.Equal(new[] { "color-hex|#A6000000" }, Run("Desktop/X.axaml", "<Border Background=\"#A6000000\" /><!-- #FF0000 --><TextBlock Tag=\"&#x2715;\" />").Where(r => r.StartsWith("color", StringComparison.Ordinal)));
        string[] code = Run("Desktop/X.cs", "var a = Color.FromRgb(1, 2, 3); var b = Brushes.Red; var c = Brushes.Transparent; var d = \"#112233\"; // Brushes.Blue\nvar e = \"issue #123456 item\";");
        Assert.Contains("color-code|Color.FromRgb", code);
        Assert.Contains("color-code|Brushes.Red", code);
        Assert.Contains("color-hex|#112233", code);
        Assert.DoesNotContain(code, c => c.Contains("Transparent", StringComparison.Ordinal) || c.Contains("Blue", StringComparison.Ordinal));
    }

    [Fact]
    public void SpacingMarginAndPaddingAreCheckedAgainstTheGrid()
    {
        string[] xaml = Run("Desktop/X.axaml",
            "<StackPanel Spacing=\"6\" /><StackPanel Spacing=\"8\" /><Border Margin=\"0,8,0,0\" Padding=\"10,5\" /><Border Margin=\"{StaticResource Workspace.Inset.Card}\" />"
            + "<Style Selector=\"Button\"><Setter Property=\"Padding\" Value=\"20\" /></Style>");
        Assert.Equal(new[] { "spacing-off-grid|Spacing=\"6\"", "spacing-off-grid|Padding=\"10,5\"", "spacing-off-grid|Padding=\"20\"" }, xaml);
        string[] code = Run("Desktop/X.cs",
            "p.Spacing = 6; p.Spacing = 8; b.Margin = new Thickness(0, 14, 0, 0); b.Padding = new Thickness(12, y ? 6 : 8); b.BorderThickness = new Thickness(0, 1, 0, 0); var t = new Thickness(5);");
        Assert.Equal(new[] { "spacing-off-grid|Spacing=6", "spacing-off-grid|Margin=new Thickness(0, 14, 0, 0)", "spacing-off-grid|Padding=new Thickness(12, y ? 6 : 8)", "spacing-off-grid|new Thickness(5)" }, code);
        // Fully qualified and target-typed forms must not hide a literal.
        string[] hidden = Run("Desktop/X.cs",
            "b.Padding = new Avalonia.Thickness(6, 2); b.Margin = new(0, 14, 0, 0); b.Padding = new(8, 4); Thickness t = new(5); var u = new Avalonia.Thickness(4); x.BorderThickness = new(0, 1, 0, 0);");
        Assert.Equal(new[] { "spacing-off-grid|Padding=new Thickness(6, 2)", "spacing-off-grid|Margin=new Thickness(0, 14, 0, 0)", "spacing-off-grid|new Thickness(5)" }, hidden);
    }

    private const string TransferFile = "Desktop/Shell/MainWindow.Transfer.cs";

    // Preserve the exact-method exception mechanism's regression coverage with an old, synthetic layout;
    // production has no exception now that its gaps are on-grid.
    private static readonly DesignLayoutContract[] SyntheticHeaderContracts =
    [
        new(DesignRules.SpacingOffGrid, TransferFile,
            "internal static void ArrangeHeaderRates(Grid grid, Control firstWord, TextBlock firstValue, Control secondWord, TextBlock secondValue, bool stacked)",
            "grid.ColumnSpacing = 6;", "ColumnSpacing=6", "Synthetic contract test."),
        new(DesignRules.SpacingOffGrid, TransferFile,
            "internal static void ArrangeHeaderRates(Grid grid, Control firstWord, TextBlock firstValue, Control secondWord, TextBlock secondValue, bool stacked)",
            "secondWord.Margin = new Thickness(stacked ? 0 : 6, 0, 0, 0);", "Margin=new Thickness(stacked ? 0 : 6, 0, 0, 0)", "Synthetic contract test.")
    ];

    private static string[] RunContract(string file, string content) =>
        DesignLintScanner.ScanFileWith(file, content, null, SyntheticHeaderContracts).Select(v => v.Rule + "|" + v.Snippet).ToArray();

    /// <summary>Historical off-grid arrangement used only to test narrow contract recognition.</summary>
    private const string HeaderIoSource = @"
partial class MainWindow
{
    internal static void ArrangeHeaderRates(Grid grid, Control firstWord, TextBlock firstValue,
        Control secondWord, TextBlock secondValue, bool stacked)
    {
        grid.ColumnDefinitions = new ColumnDefinitions(stacked ? ""Auto,Auto"" : ""Auto,Auto,Auto,Auto"");
        grid.ColumnSpacing = 6;
        grid.RowSpacing = 2;
        // Six from ColumnSpacing + six from this margin = twelve between pairs.
        secondWord.Margin = new Thickness(stacked ? 0 : 6, 0, 0, 0);
    }
}";

    [Fact]
    public void HeaderIoLayoutContractCoversOnlyTheNamedStatements()
    {
        // Positive: exactly the two statements of exactly that method in exactly that file are covered.
        Assert.Empty(RunContract(TransferFile, HeaderIoSource));
        Assert.Equal(new[] { "spacing-off-grid|ColumnSpacing=6", "spacing-off-grid|Margin=new Thickness(stacked ? 0 : 6, 0, 0, 0)" },
            DesignLintScanner.ScanFileWith(TransferFile, HeaderIoSource, null, Array.Empty<DesignLayoutContract>()).Select(v => v.Rule + "|" + v.Snippet).ToArray());

        // Negative: the same code elsewhere is not covered; the contract is never file- or folder-wide.
        Assert.Equal(2, RunContract("Desktop/Shell/MainWindow.Other.cs", HeaderIoSource).Length);
        Assert.Equal(2, RunContract("Desktop/Views/Statistics/Transfer.cs", HeaderIoSource).Length);

        // Negative: other statements, values and receivers inside the method are still checked.
        string[] sixToTen = RunContract(TransferFile, HeaderIoSource.Replace("ColumnSpacing = 6;", "ColumnSpacing = 10;", StringComparison.Ordinal));
        Assert.Equal(new[] { "spacing-off-grid|ColumnSpacing=10" }, sixToTen);
        string[] marginFive = RunContract(TransferFile, HeaderIoSource.Replace("stacked ? 0 : 6", "stacked ? 0 : 5", StringComparison.Ordinal));
        Assert.Equal(new[] { "spacing-off-grid|Margin=new Thickness(stacked ? 0 : 5, 0, 0, 0)" }, marginFive);
        string[] rowFive = RunContract(TransferFile, HeaderIoSource.Replace("RowSpacing = 2;", "RowSpacing = 5;", StringComparison.Ordinal));
        Assert.Equal(new[] { "spacing-off-grid|RowSpacing=5" }, rowFive);
        string[] otherReceiver = RunContract(TransferFile, HeaderIoSource.Replace("grid.ColumnSpacing = 6;", "other.ColumnSpacing = 6;", StringComparison.Ordinal));
        Assert.Equal(new[] { "spacing-off-grid|ColumnSpacing=6" }, otherReceiver);
        string[] otherMargin = RunContract(TransferFile, HeaderIoSource.Replace("secondWord.Margin", "firstWord.Margin", StringComparison.Ordinal));
        Assert.Equal(new[] { "spacing-off-grid|Margin=new Thickness(stacked ? 0 : 6, 0, 0, 0)" }, otherMargin);

        // Negative: a repeated statement is covered once, the repeat is reported.
        string twice = HeaderIoSource.Replace("grid.RowSpacing = 2;", "grid.RowSpacing = 2;\n        grid.ColumnSpacing = 6;", StringComparison.Ordinal);
        Assert.Equal(new[] { "spacing-off-grid|ColumnSpacing=6" }, RunContract(TransferFile, twice));

        // Negative: the same statements in another method of the file, or a changed signature, are not covered.
        Assert.Equal(2, RunContract(TransferFile, HeaderIoSource.Replace("ArrangeHeaderRates", "ArrangeSomethingElse", StringComparison.Ordinal)).Length);
        Assert.Equal(2, RunContract(TransferFile, HeaderIoSource.Replace("bool stacked)", "bool stacked, bool extra)", StringComparison.Ordinal)).Length);
        Assert.Equal(2, RunContract(TransferFile, HeaderIoSource.Replace("Grid grid", "StackPanel grid", StringComparison.Ordinal)).Length);
        string beforeAndAfter = HeaderIoSource + "\nclass Later { void M(Grid grid) { grid.ColumnSpacing = 6; } }";
        Assert.Equal(new[] { "spacing-off-grid|ColumnSpacing=6" }, RunContract(TransferFile, beforeAndAfter));

        // Negative: a definition that exists twice is ambiguous and covers nothing.
        Assert.Equal(4, RunContract(TransferFile, HeaderIoSource + HeaderIoSource).Length);

        // Negative: a trailing statement on the same line is not the declared statement.
        Assert.Equal(new[] { "spacing-off-grid|ColumnSpacing=6" }, RunContract(TransferFile, HeaderIoSource.Replace("grid.ColumnSpacing = 6;", "grid.ColumnSpacing = 6; x = 1;", StringComparison.Ordinal)));
    }

    [Fact]
    public void GlyphIconsAreSymbolsNotWords()
    {
        Assert.True(DesignLintScanner.IsGlyphLiteral("\u22EF"));
        Assert.True(DesignLintScanner.IsGlyphLiteral("\u00D7"));
        Assert.True(DesignLintScanner.IsGlyphLiteral("\u2605"));
        Assert.True(DesignLintScanner.IsGlyphLiteral("\u25B6 Play"));
        Assert.False(DesignLintScanner.IsGlyphLiteral(" \u00D7 ")); // separator between values
        Assert.False(DesignLintScanner.IsGlyphLiteral("\u2026")); // ellipsis is punctuation
        Assert.False(DesignLintScanner.IsGlyphLiteral("\u00B7"));
        Assert.False(DesignLintScanner.IsGlyphLiteral("+"));
        Assert.False(DesignLintScanner.IsGlyphLiteral("1\u00D71"));
        Assert.Equal(new[] { "glyph-icon|Content=\"\u22EF(U+22EF)\"", "glyph-icon|text=\"\u2715(U+2715)\"" },
            Run("Desktop/X.axaml", "<Button Content=\"\u22EF\" /><Button Content=\"Close\" /><TextBlock>&#x2715;</TextBlock>"));
        Assert.Equal(new[] { "glyph-icon|\"\u2605(U+2605)\"" }, Run("Desktop/X.cs", "b.Content = fav ? \"\\u2605\" : \"Add\"; // \"\u2606\""));
    }

    [Fact]
    public void RowAndControlHeightsMustBeTokens()
    {
        string[] xaml = Run("Desktop/X.axaml",
            "<DataGrid RowHeight=\"44\" /><Button Height=\"34\" /><Image Height=\"34\" /><TextBox MinHeight=\"{StaticResource Workspace.Size.Input}\" />"
            + "<Style Selector=\"Button.x\"><Setter Property=\"MinHeight\" Value=\"28\" /></Style>");
        Assert.Equal(new[] { "row-height-literal|RowHeight=\"44\"", "control-height-literal|Button Height=\"34\"", "control-height-literal|Button style MinHeight=\"28\"" },
            xaml);
        string[] code = Run("Desktop/X.cs", "grid.RowHeight = 32; var b = new Button { Height = 36, Content = new StackPanel { Height = 40 } }; var i = new Image { Height = 36 };");
        Assert.Equal(new[] { "row-height-literal|RowHeight=32", "control-height-literal|Button Height=36" }, code);
    }

    [Fact]
    public void PageRootsMustUseThePageInsetToken()
    {
        string stylesBefore = "<UserControl xmlns=\"https://github.com/avaloniaui\"><UserControl.Styles /><Grid Margin=\"12\" /></UserControl>";
        Assert.Equal(new[] { "page-inset|Grid Margin=\"12\"" }, Run("Desktop/Views/Plugins/PluginsPage.axaml", stylesBefore));
        string ok = "<UserControl xmlns=\"https://github.com/avaloniaui\"><Grid Margin=\"{StaticResource Workspace.Inset.Page}\" /></UserControl>";
        Assert.Empty(Run("Desktop/Views/Plugins/PluginsPage.axaml", ok));
    }

    // ---- baseline and report storage -----------------------------------------------------------------------------------

    private static string Key(DesignViolation v) => v.Rule + "\t" + v.File + "\t" + v.Snippet;

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../.."));

    private static string FolderPath([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static class Lint
    {
        private static readonly Lazy<(IReadOnlyList<DesignViolation> Violations, Dictionary<string, int> Baseline)> State = new(Load);

        public static IReadOnlyList<DesignViolation> Violations => State.Value.Violations;
        public static Dictionary<string, int> Baseline => State.Value.Baseline;

        private static (IReadOnlyList<DesignViolation>, Dictionary<string, int>) Load()
        {
            IReadOnlyList<DesignViolation> violations = DesignLintScanner.ScanRepository(RepositoryRoot());
            string folder = FolderPath();
            string baselinePath = Path.Combine(folder, "design-lint-baseline.txt");
            if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
            {
                File.WriteAllText(baselinePath, BuildBaseline(violations), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder, "red-list.md"), BuildReport(violations), new UTF8Encoding(false));
            }
            return (violations, ReadBaseline(baselinePath));
        }
    }

    private static Dictionary<string, int> ReadBaseline(string path)
    {
        Dictionary<string, int> map = new(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            if (parts.Length != 4) throw new InvalidDataException("Malformed design-lint-baseline.txt line: " + line);
            map[parts[0] + "\t" + parts[1] + "\t" + parts[3]] = int.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        return map;
    }

    private static string BuildBaseline(IReadOnlyList<DesignViolation> violations)
    {
        StringBuilder sb = new();
        sb.Append("# Design-system lint baseline (UX-DESIGNSYSTEM-2026-11-13, P9): today's accepted debt.\n");
        sb.Append("# rule<TAB>file<TAB>count<TAB>snippet. Generated; do not edit by hand. The tests fail on anything not listed here\n");
        sb.Append("# and on entries that no longer occur. Regenerate after burning violations down:\n");
        sb.Append("#   MEOWCHAN_DESIGN_LINT_UPDATE=1 dotnet test tests/MeowChan.DesktopTests --filter FullyQualifiedName~DesignLint\n");
        foreach (IGrouping<string, DesignViolation> g in violations.GroupBy(Key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            DesignViolation first = g.First();
            sb.Append(first.Rule).Append('\t').Append(first.File).Append('\t').Append(g.Count().ToString(CultureInfo.InvariantCulture)).Append('\t').Append(first.Snippet).Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildReport(IReadOnlyList<DesignViolation> violations)
    {
        StringBuilder sb = new();
        sb.Append("# Design-system red list\n\n");
        sb.Append("Generated by `DesignLintTests` (UX-DESIGNSYSTEM-2026-11-13, P9) from Desktop/**/*.axaml and *.cs; do not edit by hand.\n");
        sb.Append("Every entry is a deviation from the design system that the baseline tolerates today. Fix it, then regenerate\n");
        sb.Append("(`MEOWCHAN_DESIGN_LINT_UPDATE=1 dotnet test tests/MeowChan.DesktopTests --filter FullyQualifiedName~DesignLint`),\n");
        sb.Append("which shortens this list and `design-lint-baseline.txt`. Line numbers are for orientation; the baseline has none.\n\n");
        sb.Append("## Counts per rule\n\n| Rule | Violations | Files | Target (package) |\n| --- | ---: | ---: | --- |\n");
        Dictionary<string, string> target = new()
        {
            [DesignRules.FontSizeLiteral] = "P5: token or class",
            [DesignRules.FontSizeOffScale] = "P5: onto the scale",
            [DesignRules.ColorHex] = "Workspace brushes",
            [DesignRules.ColorCode] = "Workspace brushes",
            [DesignRules.SpacingOffGrid] = "P7: 0/2/4/8/12/16/24/32 or Inset token",
            [DesignRules.GlyphIcon] = "P6: catalog path icon",
            [DesignRules.RowHeightLiteral] = "P7: WorkspaceMetrics",
            [DesignRules.ControlHeightLiteral] = "P1: Workspace.Size token",
            [DesignRules.PageInset] = "P2: Workspace.Inset.Page",
        };
        foreach (string rule in DesignRules.All)
        {
            DesignViolation[] set = violations.Where(v => v.Rule == rule).ToArray();
            sb.Append($"| `{rule}` | {set.Length} | {set.Select(v => v.File).Distinct().Count()} | {target[rule]} |\n");
        }
        sb.Append($"| **total** | **{violations.Count}** | {violations.Select(v => v.File).Distinct().Count()} | |\n\n");

        sb.Append("## Counts per file\n\n| File | ").Append(string.Join(" | ", DesignRules.All.Select(r => "`" + r + "`"))).Append(" | total |\n");
        sb.Append("| --- |").Append(string.Concat(DesignRules.All.Select(_ => " ---: |"))).Append(" ---: |\n");
        foreach (IGrouping<string, DesignViolation> file in violations.GroupBy(v => v.File).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("| `").Append(file.Key).Append("` |");
            foreach (string rule in DesignRules.All)
            {
                int n = file.Count(v => v.Rule == rule);
                sb.Append(' ').Append(n == 0 ? "" : n.ToString(CultureInfo.InvariantCulture)).Append(" |");
            }
            sb.Append(' ').Append(file.Count().ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        }

        sb.Append("\n## Violations per file\n");
        foreach (IGrouping<string, DesignViolation> file in violations.GroupBy(v => v.File).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("\n### `").Append(file.Key).Append("` (").Append(file.Count().ToString(CultureInfo.InvariantCulture)).Append(")\n\n");
            foreach (IGrouping<string, DesignViolation> rule in file.GroupBy(v => v.Rule).OrderBy(g => Array.IndexOf(DesignRules.All, g.Key)))
            {
                sb.Append("- `").Append(rule.Key).Append("`: ");
                sb.Append(string.Join(", ", rule.OrderBy(v => v.Line).Select(v => $"L{v.Line} `{v.Snippet.Replace("`", "'")}`"))).Append('\n');
            }
        }

        sb.Append("\n## Permanent exceptions (documented, not debt)\n\n");
        foreach (DesignException e in DesignLintScanner.Exceptions)
            sb.Append($"- `{e.Rule}` in `{e.File}` ({e.SnippetContains}): {e.Reason}\n");
        sb.Append("\n## Layout contracts (documented, not debt)\n\n");
        foreach (DesignLayoutContract c in DesignLintScanner.LayoutContracts)
            sb.Append($"- `{c.Rule}` in `{c.File}`, only the statement `{c.Statement}` in `{c.Signature}`: {c.Reason}\n");
        return sb.ToString();
    }
}
