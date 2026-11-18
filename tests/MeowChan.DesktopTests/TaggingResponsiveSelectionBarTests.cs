using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

// Package 2B contract: the selection bar is a measured toolbar between the list controls and the results surface.
// These tests keep the responsive/keyboard invariants local without touching DesktopSmoke.
public sealed class TaggingResponsiveSelectionBarTests
{
    [Fact]
    public void SelectionBarKeepsCountAndClearOutsideTheMeasuredActionToolbar()
    {
        string source = Read("Desktop/Views/Tagging/TaggingPage.Selection.cs");

        Assert.Contains("new() { Name = \"TaggingSelectionActions\", HorizontalAlignment = HorizontalAlignment.Stretch }", source);
        Assert.Contains("Grid bar = new() { ColumnDefinitions = new ColumnDefinitions(\"Auto,*,Auto\")", source);
        Assert.Contains("Grid.SetColumn(_selectionActions, 1);", source);
        Assert.Contains("Grid.SetColumn(_selClear, 2);", source);
        Assert.Contains("_selectionActions.RefreshOverflowLabel();", source);
        Assert.Contains("_selectionText", source);
    }

    [Fact]
    public void CoreSelectionActionsRemainRequiredWhileReducedActionsUseMoreAtNarrowWidths()
    {
        foreach (WorkView view in Enum.GetValues<WorkView>())
        {
            Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("tagging.selection.tag", view));
            Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("tagging.selection.remove", view));
            Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("tagging.selection.clear", view));
        }

        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.selection.stamp", WorkView.Barebones));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.selection.state", WorkView.Simple));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.selection.state", WorkView.Advanced));

        // Representative Barebones geometry: Tag and Remove are the required anchors; Stamp, State and Group must yield to More.
        (bool[] overflow, double width) = ActionOverflowLayout.ResolveSpaced(
            widths: [96, 120, 112, 140, 100],
            groups: [0, 0, 0, 0, 0],
            segments: [false, false, false, false, false],
            modes: [OverflowMode.Never, OverflowMode.AsNeeded, OverflowMode.Always, OverflowMode.AsNeeded, OverflowMode.Never],
            visible: [true, true, true, true, true],
            available: 260,
            moreWidth: 40);

        Assert.Equal(new[] { false, true, true, true, false }, overflow);
        Assert.True(width + 40 <= 260, $"selection bar uses {width} dip plus More but only has 260 dip");
    }

    [Fact]
    public void ListKeyboardEscapeClearsBeforeCommandRoutingAndOverflowKeyboardHasAClosedMorePath()
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
