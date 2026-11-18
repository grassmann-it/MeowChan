using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// D4 (E7): the double focus ring and "selection is never the tint alone". Parsed WorkspaceStyles.axaml and SettingsEditor.axaml
// plus the static check-mark helpers; no Avalonia application. Contrast comes from the token values (WCAG 2.x), not from pixels.
[Collection("Localization")]
public sealed class FocusSelectionStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void FocusAdornerHasTwoShellsAndKeepsTheFourDipOffset()
    {
        XElement template = Assert.Single(Styles().Descendants(A + "Template").Where(element => (string?)element.Attribute(X + "Key") == "Workspace.FocusAdorner"));
        XElement outer = Assert.Single(template.Elements(A + "Border"));
        Assert.Equal("{DynamicResource Workspace.Accent}", (string?)outer.Attribute("BorderBrush"));
        Assert.Equal("2", (string?)outer.Attribute("BorderThickness"));
        Assert.Equal("-4", (string?)outer.Attribute("Margin"));
        Assert.Equal("False", (string?)outer.Attribute("IsHitTestVisible"));
        // The ring lies outside the control; the adorner layer's own clip would cut its right and bottom side by 8 dip.
        Assert.Equal("False", (string?)outer.Attribute("AdornerLayer.IsClipEnabled"));
        XElement inner = Assert.Single(outer.Elements(A + "Border"));
        Assert.Equal("{DynamicResource Workspace.Background}", (string?)inner.Attribute("BorderBrush"));
        Assert.Equal("1", (string?)inner.Attribute("BorderThickness"));
        Assert.Equal("False", (string?)inner.Attribute("IsHitTestVisible"));
        // Both shells draw rings only: neither fills, so nothing under the control is covered.
        Assert.Null(outer.Attribute("Background"));
        Assert.Null(inner.Attribute("Background"));
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void BothFocusRingsReadOnEverySurfaceAndOnTheAccentFill(string theme)
    {
        // The accent ring must stand out from the surface a control sits on; the background ring from the solid accent button.
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay", "Workspace.AccentSurface" })
            Assert.True(Contrast(theme, "Workspace.Accent", surface) >= 3.0, $"{theme}: accent ring on {surface} is {Contrast(theme, "Workspace.Accent", surface):0.00}:1");
        Assert.True(Contrast(theme, "Workspace.Background", "Workspace.Accent") >= 3.0, $"{theme}: inner ring on the accent fill");
    }

    [Fact]
    public void TheFocusAdornerReachesButtonsInputsListItemsAndSelectableText()
    {
        XElement style = Assert.Single(Styles().Elements(A + "Style").Where(element =>
            element.Elements(A + "Setter").Any(setter => (string?)setter.Attribute("Property") == "FocusAdorner")
            && ((string?)element.Attribute("Selector"))!.Contains("ListBoxItem", StringComparison.Ordinal)));
        string[] selectors = ((string)style.Attribute("Selector")!).Split(", ");
        // ToggleSwitch and TabItem (AX-05) are separate entries: a type selector does not match derived types, so "ToggleButton" never reached the switch.
        foreach (string expected in new[] { "Button", "ToggleButton", "ToggleSwitch", "TabItem", "TextBox", "ComboBox", "NumericUpDown", "CheckBox", "RadioButton", "Slider", "ListBoxItem", "SelectableTextBlock" })
            Assert.Contains(expected, selectors);
        Assert.Equal("{StaticResource Workspace.FocusAdorner}", (string?)style.Elements(A + "Setter").Single(setter => (string?)setter.Attribute("Property") == "FocusAdorner").Attribute("Value"));
    }

    [Fact]
    public void ChipSelectedIsATintPlusATwoPixelOutlineOnEveryChipForm()
    {
        Assert.Equal("{DynamicResource Workspace.ChipSelectedSurface}", Setting("Button.chip.chip-selected", "Background"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting("Button.chip.chip-selected", "BorderBrush"));
        Assert.Equal("2", Setting("Button.chip.chip-selected", "BorderThickness"));
        // The border grows by one dip per side; the padding gives it back (8,4 -> 7,3 and 6,1 -> 5,0), so the chip keeps its size.
        Assert.Equal("7,3", Setting("Button.chip.chip-selected", "Padding"));
        Assert.Equal("5,0", Setting("Button.chip.chip-compact.chip-selected", "Padding"));
        foreach (string state in new[] { "Button.chip.chip-selected /template/ ContentPresenter#PART_ContentPresenter",
            "Button.chip.chip-selected:pointerover /template/ ContentPresenter#PART_ContentPresenter", "Button.chip.chip-selected:pressed /template/ ContentPresenter#PART_ContentPresenter" })
        {
            Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting(state, "BorderBrush"));
            Assert.Equal("{DynamicResource Workspace.ChipSelectedSurface}", Setting(state, "Background"));
        }
        Assert.Equal("2", Setting("ursa|ClosableTag.chip.chip-selected", "BorderThickness"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting("ursa|ClosableTag.chip.chip-selected", "BorderBrush"));
        Assert.Equal("2", Setting("controls|ChipOutline.chip-source.chip-selected", "StrokeThickness"));
        Assert.Equal("False", Setting("controls|ChipOutline.chip-source.chip-selected", "IsDashed"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting("controls|ChipOutline.chip-source.chip-selected", "Stroke"));
        Assert.Equal("chip-selected", WorkspaceChips.Selected);
        // No selection state may animate (virtualised lists recycle the chips).
        Assert.DoesNotContain(Styles().Elements(A + "Style").Where(style => ((string?)style.Attribute("Selector"))!.Contains("chip-selected", StringComparison.Ordinal)),
            style => style.Elements(A + "Setter").Any(setter => (string?)setter.Attribute("Property") == "Transitions"));
    }

    [Fact]
    public void SettingsSidebarMarksTheChosenCategoryWithABarAndKeepsItsWidthReserved()
    {
        XElement[] styles = XDocument.Load(Root("Desktop/Views/Settings/SettingsEditor.axaml")).Descendants(A + "Style").ToArray();
        XElement idle = Assert.Single(styles.Where(style => (string?)style.Attribute("Selector") == "ToggleButton.settings-category"));
        Assert.Equal("3,0,0,0", (string?)idle.Elements(A + "Setter").Single(setter => (string?)setter.Attribute("Property") == "BorderThickness").Attribute("Value"));
        Assert.Equal("Transparent", (string?)idle.Elements(A + "Setter").Single(setter => (string?)setter.Attribute("Property") == "BorderBrush").Attribute("Value"));
        XElement chosen = Assert.Single(styles.Where(style => (string?)style.Attribute("Selector") == "ToggleButton.settings-category:checked /template/ ContentPresenter#PART_ContentPresenter"));
        Assert.Equal("{DynamicResource Workspace.Accent}", (string?)chosen.Elements(A + "Setter").Single(setter => (string?)setter.Attribute("Property") == "BorderBrush").Attribute("Value"));
        Assert.Equal("{DynamicResource Workspace.AccentSurface}", (string?)chosen.Elements(A + "Setter").Single(setter => (string?)setter.Attribute("Property") == "Background").Attribute("Value"));
    }

    [Fact]
    public void TileSelectionCheckIsAShapeOnTheAccentWithOnAccentStrokeAndNoMotion()
    {
        Assert.Equal("{DynamicResource Workspace.Accent}", Setting("Border.tile-selection-check", "Background"));
        Assert.Equal("{DynamicResource Workspace.OnAccent}", Setting("Path.tile-selection-glyph", "Stroke"));
        foreach (string theme in new[] { "Dark", "Light" })
            Assert.True(Contrast(theme, "Workspace.OnAccent", "Workspace.Accent") >= 4.5, theme + ": the check on the accent");
        foreach (string selector in new[] { "Border.tile-selection-check", "Path.tile-selection-glyph" })
            Assert.DoesNotContain(Styles().Elements(A + "Style").Single(style => (string?)style.Attribute("Selector") == selector).Elements(A + "Setter"),
                setter => (string?)setter.Attribute("Property") == "Transitions");
    }

    [Fact]
    public void SelectionCheckIsHiddenUntilSelectedAndNamedByATooltipNotByAutomation()
    {
        Border check = WorkspaceChips.CreateSelectionCheck("ArchiveMediaSelected");
        Assert.Equal("ArchiveMediaSelected", check.Name);
        Assert.False(check.IsVisible);
        Assert.Contains("tile-selection-check", check.Classes);
        Assert.IsType<Avalonia.Controls.Shapes.Path>(check.Child);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(check));
        WorkspaceChips.ShowSelection(check, true);
        Assert.True(check.IsVisible);
        Assert.Equal(MeowChan.Desktop.Localization.Texts.T("media.tile.selected"), ToolTip.GetTip(check));
        WorkspaceChips.ShowSelection(check, false);
        Assert.False(check.IsVisible);
        Assert.Null(ToolTip.GetTip(check));
    }

    [Fact]
    public void BothTilesUseTheSharedCheckAndSubscribeToTheirListItem()
    {
        string archive = File.ReadAllText(Root("Desktop/ArchiveMediaTileView.cs"));
        Assert.Contains("WorkspaceChips.CreateSelectionCheck(\"ArchiveMediaSelected\")", archive);
        Assert.Contains("WorkspaceChips.ObserveSelection(this, _selectedMark)", archive);
        string library = File.ReadAllText(Root("Desktop/MediaLibraryTileView.cs"));
        Assert.Contains("WorkspaceChips.CreateSelectionCheck(\"MediaTileSelected\")", library);
        Assert.Contains("WorkspaceChips.ObserveSelection(this, _selectedMark)", library);
        Assert.DoesNotContain("✓", library);
    }

    private static double Contrast(string theme, string foreground, string background)
    {
        double a = Luminance(Color(theme, foreground)), b = Luminance(Color(theme, background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            double value = int.Parse(hex.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static string Color(string theme, string key) =>
        Assert.Single(Assert.Single(Styles().Descendants(A + "ResourceDictionary").Where(element => (string?)element.Attribute(X + "Key") == theme))
            .Elements().Where(element => (string?)element.Attribute(X + "Key") == key)).Value.Trim();

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Assert.Single(Styles().Elements(A + "Style").Where(style => (string?)style.Attribute("Selector") == selector))
            .Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static XElement Styles() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
