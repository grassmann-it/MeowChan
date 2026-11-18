using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// CM1: the width rules of popup menus. The plan is plain numbers (a menu as wide as its widest row between 224 and 416 dip, the shortcut
/// column shared and never clipped, a long label trimmed instead); the rows come from menus built by <see cref="ContextMenuBuilder"/> and are
/// measured with a fixed-pitch font, so no window or theme is needed. The tokens and the style that turns the layout on are read from
/// WorkspaceStyles.axaml.
/// </summary>
[Collection("Localization")]
public sealed class MenuLayoutTests : IDisposable
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // A row's frame as the theme draws it: 16 dip in from the edge on both sides, a 16 dip check column and a 16 dip icon column (plus 8 apart).
    private const double Pitch = 7;
    private const double Leading = 16 + 24 + 24;
    private const double Trailing = 16;
    private static readonly MenuLayoutPlan.Options Options = new(224, 416, 24, Leading, Trailing, 24);

    public MenuLayoutTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static double Measure(string? text) => (text?.Length ?? 0) * Pitch;

    // The rows of the menu a ContextMenuBuilder makes: the label of each item and the shortcut the item shows.
    private static List<MenuLayoutPlan.Row> RowsOf(ContextMenu menu, IReadOnlyDictionary<string, string>? extraShortcuts = null)
    {
        List<MenuLayoutPlan.Row> rows = [];
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            string label = item.Header as string ?? string.Empty;
            string? shortcut = item.InputGesture?.ToString();
            if (extraShortcuts != null && item.Tag is string id && extraShortcuts.TryGetValue(id, out string? extra)) shortcut = extra;
            rows.Add(new MenuLayoutPlan.Row(Measure(label), Measure(shortcut)));
        }
        return rows;
    }

    private static ContextMenu LongMenu() => new ContextMenuBuilder()
        .Item("open", "Open in gallery", () => { })
        .Check("favorite", "Remove from favorites", true, () => { })
        .Item("rating", "Edit the rating of this attachment and all of the attachments beside it in the thread…", () => { })
        .Item("details", "Show media details", () => { })
        .Section()
        .Item("bookmark", "Add or edit the post bookmark of this attachment in the reading history…", () => { })
        .Build()!;

    [Fact]
    public void AMenuIsAsWideAsItsWidestRowAndNeverNarrowerThanTheMinimum()
    {
        MenuLayoutPlan.Result narrow = MenuLayoutPlan.Resolve([new(Measure("Open"), 0)], Options);
        Assert.Equal(224, narrow.Width);
        Assert.False(narrow.Capped);

        // 20 + 14 characters: the label, the gap and the shortcut side by side, with the row's frame around them.
        MenuLayoutPlan.Result wide = MenuLayoutPlan.Resolve([new(Measure(new string('a', 20)), Measure(new string('k', 14))), new(Measure("Short"), Measure("Ctrl+O"))], Options);
        Assert.Equal(Leading + 20 * Pitch + 24 + 14 * Pitch + Trailing, wide.Width);
        Assert.Equal(14 * Pitch, wide.ShortcutColumn);
        Assert.False(wide.Capped);
        Assert.All(wide.Trimmed, trimmed => Assert.False(trimmed));
    }

    [Fact]
    public void TheShortcutColumnIsOneColumnWideEnoughForTheLongestKeyNameInAnyRow()
    {
        // The widest label sits in one row and the widest shortcut in another: the menu has to hold both side by side.
        double label = Measure(new string('l', 20));
        double longest = Measure("Umschalt+Strg+Eingabe");
        MenuLayoutPlan.Result result = MenuLayoutPlan.Resolve([new(label, Measure("R")), new(Measure("Zurück"), longest)], Options);
        Assert.True(result.Width >= Leading + label + 24 + longest + Trailing);
        Assert.Equal(longest, result.ShortcutColumn);
    }

    [Fact]
    public void AtTheMaximumTheLabelsGiveWayAndTheShortcutStaysWhole()
    {
        double shortcut = Measure("Umschalt+Eingabe");
        MenuLayoutPlan.Result result = MenuLayoutPlan.Resolve(
            [new(Measure(new string('x', 120)), shortcut), new(Measure("Short"), Measure("T")), new(Measure("Edit"), 0)], Options);
        Assert.Equal(416, result.Width);
        Assert.True(result.Capped);
        Assert.Equal(new[] { true, false, false }, result.Trimmed);
        Assert.Equal(shortcut, result.ShortcutColumn);
        // Frame + label column + gap + shortcut column fill the menu exactly: nothing is left over and nothing runs past the edge.
        Assert.Equal(result.Width, Leading + result.LabelColumn + 24 + result.ShortcutColumn + Trailing);
    }

    [Fact]
    public void AMaximumThatIsNarrowerThanTheShortcutColumnStillShowsTheShortcutWhole()
    {
        double shortcut = Measure(new string('k', 70));
        MenuLayoutPlan.Result result = MenuLayoutPlan.Resolve([new(Measure("A long label that has to give way"), shortcut)], Options);
        Assert.True(result.Width >= Leading + 24 + shortcut + Trailing);
        Assert.Equal(0, result.LabelColumn);
        Assert.True(result.Trimmed[0]);
    }

    [Fact]
    public void AMenuWithoutShortcutsReservesNoShortcutColumnOrGapAndASubmenuReservesItsChevron()
    {
        MenuLayoutPlan.Result plain = MenuLayoutPlan.Resolve([new(Measure(new string('a', 40)), 0)], Options);
        Assert.Equal(Leading + 40 * Pitch + Trailing, plain.Width);
        MenuLayoutPlan.Result sub = MenuLayoutPlan.Resolve([new(Measure(new string('a', 40)), 0, true)], Options);
        Assert.Equal(plain.Width + 24, sub.Width);
    }

    [Fact]
    public void AMenuFromTheBuilderWithLongLabelsAndLongKeyNamesIsWideEnoughAndKeepsItsGestureColumnWhole()
    {
        ContextMenu menu = LongMenu();
        Dictionary<string, string> keys = new() { ["open"] = "Umschalt+Eingabe", ["details"] = "Strg+Umschalt+T", ["rating"] = "R" };
        List<MenuLayoutPlan.Row> rows = RowsOf(menu, keys);
        Assert.Equal(5, rows.Count);
        // The labels of two rows are longer than the room one menu has: they are trimmed, and the key names are whole.
        MenuLayoutPlan.Result result = MenuLayoutPlan.Resolve(rows, Options);
        Assert.InRange(result.Width, 224, 416);
        Assert.True(result.Capped);
        Assert.Equal(Measure("Umschalt+Eingabe"), result.ShortcutColumn);
        Assert.Equal(new[] { false, false, true, false, true }, result.Trimmed);
        Assert.Equal(result.Width, Leading + result.LabelColumn + Options.Gap + result.ShortcutColumn + Trailing);
        // Each row that is not trimmed fits its label column, with the gap in front of the gesture.
        for (int i = 0; i < rows.Count; i++)
            if (!result.Trimmed[i]) Assert.True(rows[i].Label <= result.LabelColumn + 0.5);
    }

    [Fact]
    public void AMenuFromTheBuilderWithShortLabelsMeasuresToItsContent()
    {
        ContextMenu menu = new ContextMenuBuilder()
            .Item("a", "Open", () => { }).Item("b", "Copy link", () => { }).Item("c", "Remove from the list of watched threads", () => { })
            .Build()!;
        MenuLayoutPlan.Result result = MenuLayoutPlan.Resolve(RowsOf(menu), Options);
        Assert.Equal(Leading + Measure("Remove from the list of watched threads") + Trailing, result.Width);
        Assert.False(result.Capped);
    }

    [Fact]
    public void TheMenuTokensStyleAndGapAreDefinedOnTheEightDipGrid()
    {
        XElement root = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;
        XElement resources = root.Element(A + "Styles.Resources")!;
        double Number(string key) => double.Parse((string)Assert.Single(resources.Descendants(X + "Double").Where(e => (string?)e.Attribute(X + "Key") == key)), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(224, Number("MenuFlyoutMinWidth"));
        Assert.Equal(416, Number("MenuFlyoutMaxWidth"));
        Assert.Equal(0, 224 % 8);
        Assert.Equal(0, 416 % 8);
        Assert.Equal(MenuLayout.DefaultMinimumWidth, Number("MenuFlyoutMinWidth"));
        Assert.Equal(MenuLayout.DefaultMaximumWidth, Number("MenuFlyoutMaxWidth"));
        // The gap in front of the shortcut: at least 24 dip.
        string gap = (string)Assert.Single(resources.Descendants(A + "Thickness").Where(e => (string?)e.Attribute(X + "Key") == "MenuItemInputGestureTextMargin"));
        Assert.True(double.Parse(gap.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture) >= MenuLayout.MinimumShortcutGap);
        // A row is the control height (32) high and has the 1-px edge plus 15 in from the side: 16.
        Assert.Equal("15,6", (string)Assert.Single(resources.Descendants(A + "Thickness").Where(e => (string?)e.Attribute(X + "Key") == "Workspace.Inset.MenuItem")));
        Assert.Equal(32, Number("Workspace.Size.Control"));
        XElement rowHeight = Assert.Single(root.Elements(A + "Style").Where(style => ((string?)style.Attribute("Selector"))!.StartsWith("ContextMenu MenuItem")));
        Assert.Equal("{StaticResource Workspace.Size.Control}", (string?)Assert.Single(rowHeight.Elements(A + "Setter").Where(s => (string?)s.Attribute("Property") == "MinHeight")).Attribute("Value"));
        // Both kinds of popup menu take the layout.
        XElement managed = Assert.Single(root.Elements(A + "Style").Where(style => (string?)style.Attribute("Selector") == "ContextMenu, MenuFlyoutPresenter"));
        Assert.Equal("True", (string?)Assert.Single(managed.Elements(A + "Setter")).Attribute("Value"));
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
