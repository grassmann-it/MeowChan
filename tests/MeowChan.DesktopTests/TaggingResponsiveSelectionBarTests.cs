using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

// UX-LEDGER-28 package 2A contract: the selection bar keeps the core loop on the surface and
// moves secondary actions into its measured More affordance as the available width shrinks.
public sealed class TaggingResponsiveSelectionBarTests
{
    [Fact]
    public void SelectionBarPutsActionsFirstAndKeepsCountAndClearInTheTrailingGroup()
    {
        string source = Read("Desktop/Views/Tagging/TaggingPage.Selection.cs");

        Assert.Contains("new() { Name = \"TaggingSelectionActions\", HorizontalAlignment = HorizontalAlignment.Stretch }", source);
        Assert.Contains("Grid bar = new() { ColumnDefinitions = new ColumnDefinitions(\"*,Auto\")", source);
        Assert.Contains("Grid.SetColumn(_selectionActions, 0);", source);
        Assert.Contains("StackPanel trailing = new()", source);
        Assert.Contains("trailing.Children.Add(_selectionText);", source);
        Assert.Contains("trailing.Children.Add(_selClear);", source);
        Assert.Contains("Grid.SetColumn(trailing, 1);", source);
        Assert.Contains("_selectionActions.RefreshOverflowLabel();", source);
    }

    [Fact]
    public void CoreSelectionActionsAreRequiredAndSecondaryActionsKeepTheirWorkViewOverflow()
    {
        string[] required =
        [
            "tagging.selection-bar",
            "tagging.selection.status",
            "tagging.selection.tag",
            "tagging.selection.done",
            "tagging.selection.skipped",
            "tagging.selection.continue",
            "tagging.selection.clear",
        ];

        foreach (WorkView view in Enum.GetValues<WorkView>())
        {
            foreach (string id in required)
                Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view));

            // Remove is a direct destructive action, but may yield at a genuinely narrow width.
            Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.selection.group", view));
            Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.selection.remove", view));
        }

        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.selection.stamp", WorkView.Barebones));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.selection.stamp", WorkView.Simple));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.selection.stamp", WorkView.Advanced));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.selection.state", WorkView.Barebones));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.selection.state", WorkView.Simple));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.selection.state", WorkView.Advanced));

        string source = Read("Desktop/Views/Tagging/TaggingPage.Selection.cs");
        foreach (string field in new[] { "_selTag", "_selStamp", "_selDone", "_selSkipped", "_selState", "_selContinue", "_selGroup", "_selRemove" })
            Assert.Contains(field, source);
        Assert.Contains("_selectionActions.PrioritizedOverflow = true", source);
        Assert.Contains("_selRemove.Classes.Add(\"danger\")", source);
    }

    [Fact]
    public void NarrowSelectionBarKeepsRequiredActionsAndMovesOnlySecondaryActionsUnderMore()
    {
        string[] ids =
        [
            "tagging.selection.tag",
            "tagging.selection.stamp",
            "tagging.selection.done",
            "tagging.selection.skipped",
            "tagging.selection.state",
            "tagging.selection.continue",
            "tagging.selection.group",
            "tagging.selection.remove",
        ];
        WorkView view = WorkView.Barebones;
        OverflowMode[] modes = ids.Select(id => WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve(id, view))).ToArray();
        int[] priorities = ids.Select(id => WorkViewPolicy.Get(id).OverflowOrder).ToArray();

        Assert.Equal(OverflowMode.Never, modes[0]);
        Assert.Equal(OverflowMode.Never, modes[2]);
        Assert.Equal(OverflowMode.Never, modes[3]);
        Assert.Equal(OverflowMode.Never, modes[5]);
        Assert.Equal(OverflowMode.Always, modes[1]);
        Assert.Equal(OverflowMode.Always, modes[4]);

        (bool[] overflow, double width) = ActionOverflowLayout.ResolveSpaced(
            widths: [80, 120, 82, 88, 110, 96, 100, 100],
            groups: [0, 0, 1, 1, 1, 1, 2, 2],
            segments: [false, false, false, false, false, false, false, false],
            modes,
            visible: [true, true, true, true, true, true, true, true],
            available: 500,
            moreWidth: 40,
            priorities);

        Assert.Equal(new[] { false, true, false, false, true, false, true, true }, overflow);
        Assert.True(width + 40 <= 500, $"selection bar uses {width} dip plus More but only has 500 dip");
        foreach (int index in new[] { 0, 2, 3, 5 })
            Assert.False(overflow[index], $"required selection action {ids[index]} moved under More");
    }

    [Fact]
    public void ListEscapePrecedesCommandRoutingAndOverflowKeyboardCanReturnToMore()
    {
        string commands = Read("Desktop/Views/Tagging/TaggingPage.Commands.cs");
        int escape = commands.IndexOf("e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && SelectedCount > 0", StringComparison.Ordinal);
        int commandLoop = commands.IndexOf("foreach (string id in TaggingCommands.All)", StringComparison.Ordinal);

        Assert.True(escape >= 0 && commandLoop > escape, "selection Escape must be handled before tagging command matching");
        string escapeBlock = commands[escape..commandLoop];
        Assert.Contains("e.Handled = true;", escapeBlock);
        Assert.Contains("ClearSelection();", escapeBlock);

        string toolbar = Read("Desktop/ActionToolBar.cs");
        int overflowHandler = toolbar.IndexOf("private void OnOverflowKeyDown", StringComparison.Ordinal);
        Assert.True(overflowHandler >= 0);
        string handler = toolbar[overflowHandler..];
        Assert.Contains("if (e.Key == Key.Escape)", handler);
        Assert.Contains("More.IsChecked = false; More.Focus();", handler);
        Assert.Contains("Key.Home", handler);
        Assert.Contains("Key.End", handler);
        Assert.Contains("Key.Up", handler);
        Assert.Contains("Key.Down", handler);
    }

    private static string Read(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));
}
