using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// Bug 14.11.2026 (Status page): "Can only change SelectedItems collection in Extended selection mode". Avalonia's DataGrid.SelectedItems
// throws on every write in Single mode, even Clear() on an empty selection. The diagnostics page cleared the platform table (Single)
// through SelectedItems whenever a row of the main table was selected, so every selection raised the unexpected-error banner.
public sealed class DataGridSelectionTests
{
    private static readonly string[] Rows = ["a", "b", "c"];

    private static DataGrid Grid(DataGridSelectionMode mode) =>
        new() { SelectionMode = mode, ItemsSource = Rows };

    [Fact]
    public void AvaloniaRefusesEverySelectedItemsWriteInSingleMode()
    {
        // The reproduction: the exact call the diagnostics page made on its platform table.
        DataGrid single = Grid(DataGridSelectionMode.Single);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => single.SelectedItems.Clear());
        Assert.Contains("Extended selection mode", error.Message);
        Assert.Throws<InvalidOperationException>(() => single.SelectedItems.Add("a"));
        // Extended mode accepts them, which is why the main table never failed.
        DataGrid extended = Grid(DataGridSelectionMode.Extended);
        Assert.Null(Record.Exception(() => extended.SelectedItems.Clear()));
    }

    [Fact]
    public void ClearDropsTheSelectionInSingleModeWithoutThrowing()
    {
        DataGrid single = Grid(DataGridSelectionMode.Single);
        Assert.Null(Record.Exception(() => DataGridSelection.Clear(single))); // nothing selected: still no throw
        single.SelectedItem = "b";
        Assert.Equal("b", single.SelectedItem);
        DataGridSelection.Clear(single);
        Assert.Null(single.SelectedItem);
        Assert.Empty(single.SelectedItems);
    }

    [Fact]
    public void ClearDropsTheWholeSelectionInExtendedMode()
    {
        DataGrid extended = Grid(DataGridSelectionMode.Extended);
        DataGridSelection.Select(extended, ["a", "c"]);
        Assert.Equal(2, extended.SelectedItems.Count);
        DataGridSelection.Clear(extended);
        Assert.Empty(extended.SelectedItems);
        Assert.Null(extended.SelectedItem);
    }

    [Fact]
    public void SelectKeepsTheLeadAndAllRowsInExtendedModeAndOnlyTheLeadInSingleMode()
    {
        DataGrid extended = Grid(DataGridSelectionMode.Extended);
        DataGridSelection.Select(extended, ["a", "b", "c"], lead: "b");
        Assert.Equal("b", extended.SelectedItem);
        Assert.Equal(["a", "b", "c"], extended.SelectedItems.Cast<string>().Order().ToArray());

        DataGrid single = Grid(DataGridSelectionMode.Single);
        Assert.Null(Record.Exception(() => DataGridSelection.Select(single, ["a", "b", "c"], lead: "b")));
        Assert.Equal("b", single.SelectedItem);
        Assert.Single(single.SelectedItems);

        // A lead that is not among the items falls back to the first item; no items clear the selection.
        DataGridSelection.Select(single, ["c"], lead: "zzz");
        Assert.Equal("c", single.SelectedItem);
        DataGridSelection.Select(single, []);
        Assert.Null(single.SelectedItem);
    }

    // The markup contract: which grids of the page are Single, and that no code writes to their SelectedItems. A grid that is Single in
    // the markup must be cleared through DataGridSelection (or SelectedItem), and a context-menu or sync handler that receives "the
    // grid" as a parameter must not call SelectedItems.Clear() directly, because the receiver may be either table.
    [Fact]
    public void NoCodeWritesSelectedItemsOfASingleModeGrid()
    {
        string desktop = Path.Combine(SourceRoot(), "Desktop");
        string[] singles = Directory.EnumerateFiles(desktop, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "<(?:DataGrid|controls:RowFocusDataGrid)\\b[^>]*>", RegexOptions.Singleline).Select(m => m.Value))
            .Where(tag => tag.Contains("SelectionMode=\"Single\""))
            .Select(tag => Regex.Match(tag, "x:Name=\"([^\"]+)\"")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToArray();
        Assert.Contains("PlatformGrid", singles);
        Assert.Contains("StatisticsGrid", singles);
        Assert.Contains("SessionFeedbackGrid", singles);

        Regex write = new("SelectedItems\\s*(!\\s*)?\\.\\s*(Clear|Add|Remove|RemoveAt|Insert)\\s*\\(");
        foreach (string file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            string text = File.ReadAllText(file);
            foreach (Match match in write.Matches(text))
            {
                int lineStart = text.LastIndexOf('\n', match.Index) + 1;
                int lineEnd = text.IndexOf('\n', match.Index);
                string line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)];
                foreach (string name in singles)
                    Assert.False(line.Contains("\"" + name + "\"") || Regex.IsMatch(line, "\\b" + name + "\\b"),
                        Path.GetFileName(file) + ": SelectedItems write on the Single grid " + name + ": " + line.Trim());
            }
        }

        // The diagnostics page serves an Extended and a Single table with the same handlers: no direct SelectedItems writes at all.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(desktop, "Views", "Diagnostics"), "*.cs"))
            Assert.DoesNotMatch(write, File.ReadAllText(file));
    }

    private static string SourceRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
