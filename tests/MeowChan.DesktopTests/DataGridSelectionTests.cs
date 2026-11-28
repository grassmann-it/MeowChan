using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.WorkViews;
using MeowChan.Interfaces;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

// Bug 14.11.2026 (Status page): "Can only change SelectedItems collection in Extended selection mode". Avalonia's DataGrid.SelectedItems
// throws on every write in Single mode, even Clear() on an empty selection. The diagnostics page cleared the platform table (Single)
// through SelectedItems whenever a row of the main table was selected, so every selection raised the unexpected-error banner.
[Collection("Avalonia global state")]
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

    [Fact]
    public async Task ArchiveTableMovesRowFocusWithTheKeyboardAndKeepsColumnMenuAndDoubleTap()
    {
        using ReaderArchiveTestFixture archive = new();
        ResourceId first = new("synthetic", "b", "1");
        ResourceId second = new("synthetic", "b", "2");
        await archive.CaptureAsync(first, "First thread", [new("1", 0, PostFieldPresence.All, Html: "First post")]);
        await archive.CaptureAsync(second, "Second thread", [new("2", 0, PostFieldPresence.All, Html: "Second post")]);

        archive.Run(page =>
        {
            Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
            page.ArchiveColumnModePreference = WorkViewSetting.AdvancedName;
            page.ArchiveLayoutPreference = "Table";
            page.OpenGallery = (_, _, _) => Task.CompletedTask;
            Window window = new() { Content = page, Width = 1600, Height = 850 };
            try
            {
                window.Show();
                BooruComponentDispatcher.Wait(page.ActivateAsync());
                Layout();
                DataGrid table = Assert.Single(page.GetVisualDescendants().OfType<DataGrid>(),
                    grid => AutomationProperties.GetAutomationId(grid) == "ArchiveThreadTable");
                ArchiveThreadRow[] rows = table.CollectionView!.Cast<ArchiveThreadRow>().ToArray();
                Assert.Equal(2, rows.Length);
                table.SelectedItem = rows[0];
                Assert.True(table.Focus(NavigationMethod.Tab));
                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                Layout();
                Assert.Same(rows[1], Assert.Single(table.SelectedItems));

                DataGridRow row = Assert.Single(table.GetVisualDescendants().OfType<DataGridRow>(),
                    candidate => ReferenceEquals(candidate.DataContext, rows[1]));
                Border focus = Assert.Single(row.GetVisualDescendants().OfType<Border>(),
                    border => border.Name == "BackgroundBorder" && border.TemplatedParent == row);
                Assert.Equal(new Thickness(3, 1, 1, 1), focus.BorderThickness);

                ContextMenu? columns = null;
                page.ShowColumnMenu = (menu, _) => columns = menu;
                DataGridColumn title = Assert.Single(table.Columns, column => column.SortMemberPath == "TitleOrId");
                DataGridColumnHeader header = Assert.Single(table.GetVisualDescendants().OfType<DataGridColumnHeader>(),
                    candidate => Equals(candidate.Content, title.Header));
                header.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                ContextMenu menu = Assert.IsType<ContextMenu>(columns);
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "archive.columnsFitAll"));
                Assert.Same(rows[1], table.SelectedItem);

                Control? readerOrigin = null;
                page.OpenReaderRequested = origin => readerOrigin = origin;
                TextBlock target = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == rows[1].TitleOrId);
                target.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent,
                    new PointerEventArgs(InputElement.PointerReleasedEvent, target,
                        new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), target, default, 0,
                        PointerPointProperties.None, KeyModifiers.None)));
                Assert.Same(row, readerOrigin);
                BooruComponentDispatcher.WaitUntil(() => page.CurrentThread?.Summary.Id == rows[1].Summary.Id,
                    "the double-tapped archive thread");
            }
            finally { window.Close(); }

            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
        }, hosted: true);
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

        string diagnostics = Path.Combine(desktop, "Views", "Diagnostics");
        XName nameAttribute = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        XElement mainGrid = Assert.Single(XDocument.Load(Path.Combine(diagnostics, "DiagnosticsPage.axaml")).Descendants(),
            element => (string?)element.Attribute(nameAttribute) == "DiagnosticsGrid");
        Assert.Equal("Extended", (string?)mainGrid.Attribute("SelectionMode"));

        // Collapsing a group removes hidden rows from the Extended grid. Shared handlers still have to use the mode-aware helpers.
        foreach (string file in Directory.EnumerateFiles(diagnostics, "*.cs"))
        {
            string text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "DiagnosticsPage.Groups.cs")
            {
                Assert.Contains("private DataGrid DiagnosticGrid => this.FindControl<DataGrid>(\"DiagnosticsGrid\")!;", text);
                text = Regex.Replace(text, @"(?m)^[ \t]*DiagnosticGrid\.SelectedItems\.Remove\(hidden\);[ \t]*\r?$", "");
            }
            Assert.DoesNotMatch(write, text);
        }
    }

    private static string SourceRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
