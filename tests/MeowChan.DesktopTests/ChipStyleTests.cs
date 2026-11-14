using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

// C1: chip tokens and classes. Parsed WorkspaceStyles.axaml plus the chip factories; no Avalonia application, so contrast is
// computed from the token values (WCAG 2.x relative luminance), not measured on rendered pixels.
[Collection("Localization")]
public sealed class ChipStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ChipBrushes =
    {
        "Workspace.ChipSurface", "Workspace.ChipBorder", "Workspace.ChipSourceBorder", "Workspace.ChipExcludeBorder",
        "Workspace.ChipSelectedSurface", "Workspace.ChipSelectedBorder", "Workspace.TagCategory.Artist", "Workspace.TagCategory.Copyright",
        "Workspace.TagCategory.Character", "Workspace.TagCategory.Meta", "Workspace.TagCategory.General"
    };

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void EveryChipTokenExistsOncePerThemeAndSharedTokensOnce(string theme)
    {
        XElement dictionary = Theme(theme);
        foreach (string key in ChipBrushes)
            Assert.Single(dictionary.Elements().Where(element => (string?)element.Attribute(X + "Key") == key));
        XElement styles = Styles();
        // Shape and size come from the D0 tokens (pill 12 at 24 dip, compact 9 at 18 dip); chips add colours only.
        XElement[] shared = styles.Element(A + "Styles.Resources")!.Elements().ToArray();
        foreach ((string key, string value) in new[] { ("Workspace.Radius.Chip", "12"), ("Workspace.Radius.ChipCompact", "9"), ("Workspace.Inset.Chip", "8,4"),
            ("Workspace.Inset.ChipCompact", "6,1"), ("Workspace.Size.Chip", "24"), ("Workspace.Size.ChipCompact", "18"),
            ("Workspace.Text.Body", "13"), ("Workspace.Text.Caption", "11") })
            Assert.Equal(value, Assert.Single(shared.Where(element => (string?)element.Attribute(X + "Key") == key)).Value);
        foreach (string own in new[] { "Workspace.Chip.Height", "Workspace.Chip.HeightCompact", "Workspace.Inset.ChipLabel", "Workspace.Text.Chip", "Workspace.Text.ChipCompact" })
            Assert.DoesNotContain(shared, element => (string?)element.Attribute(X + "Key") == own);
    }

    [Fact]
    public void LightChipOutlineIsTheLw06ControlBorderAndSurfacesReuseThePalette()
    {
        foreach (string theme in new[] { "Dark", "Light" })
        {
            Assert.Equal(Color(theme, "Workspace.Raised"), Color(theme, "Workspace.ChipSurface"));
            Assert.Equal(Color(theme, "Workspace.AccentSurface"), Color(theme, "Workspace.ChipSelectedSurface"));
            Assert.Equal(Color(theme, "Workspace.Accent"), Color(theme, "Workspace.ChipSelectedBorder"));
            Assert.Equal(Color(theme, "Workspace.Error"), Color(theme, "Workspace.ChipExcludeBorder"));
            Assert.Equal(Color(theme, "Workspace.Muted"), Color(theme, "Workspace.TagCategory.General"));
            // One interactive outline per theme: Dark took the chip grey for ControlBorder in the palette review (2026-11-13).
            Assert.Equal(Color(theme, "Workspace.ControlBorder"), Color(theme, "Workspace.ChipBorder"));
        }
        Assert.Equal(Color("Light", "Workspace.Warning"), Color("Light", "Workspace.TagCategory.Meta"));
    }

    // Edges and marks need 3:1 against every surface a chip sits on (window, panel, raised dialog parts, selected tile);
    // labels need 4.5:1 on the chip's own fill or, for the transparent source chip, on those surfaces.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void ChipEdgesReachThreeToOneAndLabelsFourPointFiveToOne(string theme)
    {
        string[] surfaces = { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.AccentSurface", "Workspace.Card", "Workspace.Overlay" };
        foreach (string edge in new[] { "Workspace.ChipBorder", "Workspace.ChipSourceBorder", "Workspace.ChipExcludeBorder", "Workspace.ChipSelectedBorder",
            "Workspace.TagCategory.Artist", "Workspace.TagCategory.Copyright", "Workspace.TagCategory.Character", "Workspace.TagCategory.Meta", "Workspace.TagCategory.General" })
            foreach (string surface in surfaces)
                AssertContrast(theme, edge, surface, 3.0);
        foreach ((string text, string fill) in new[] { ("Workspace.Text", "Workspace.ChipSurface"), ("Workspace.Text", "Workspace.Hover"),
            ("Workspace.Text", "Workspace.ChipSelectedSurface"), ("Workspace.Text", "Workspace.AccentSurface"), ("Workspace.Muted", "Workspace.ChipSurface"),
            ("Workspace.Muted", "Workspace.Background"), ("Workspace.Muted", "Workspace.Panel"), ("Workspace.Muted", "Workspace.Raised"), ("Workspace.Muted", "Workspace.Hover"),
            ("Workspace.Muted", "Workspace.Card"), ("Workspace.Muted", "Workspace.Overlay") })
            AssertContrast(theme, text, fill, 4.5);
        // The filter chip's close mark (Ursa resource) is the muted label colour.
        Assert.Equal(Color(theme, "Workspace.Muted"), Color(theme, "ClosableTagCloseButtonForeground"));
        Assert.Equal(Color(theme, "Workspace.ChipBorder"), Color(theme, "ClosableTagBorder"));
        Assert.Equal(Color(theme, "Workspace.ChipSurface"), Color(theme, "ClosableTagBackground"));
    }

    [Fact]
    public void OwnSourceFilterAndExclusionDifferByFormNotOnlyColour()
    {
        Assert.Equal("{DynamicResource Workspace.ChipSurface}", Setting("Button.chip", "Background"));
        Assert.Equal("{StaticResource Workspace.Radius.Chip}", Setting("Button.chip", "CornerRadius"));
        Assert.Equal("Transparent", Setting("Button.chip.chip-available", "Background"));
        Assert.Equal("{StaticResource Workspace.Size.ChipCompact}", Setting("Button.chip.chip-compact", "MinHeight"));
        Assert.Equal("{StaticResource Workspace.Size.Chip}", Setting("Button.chip", "MinHeight"));
        Assert.Equal("{StaticResource Workspace.Inset.Chip}", Setting("Button.chip", "Padding"));
        Assert.Equal("{StaticResource Workspace.Radius.ChipCompact}", Setting("Button.chip.chip-compact", "CornerRadius"));
        Assert.Equal("{StaticResource Workspace.Radius.ChipCompact}", Setting("controls|ChipOutline.chip-source.chip-compact", "CornerRadius"));
        // Source tag: no fill, dashed, muted text; marked: solid 2-px accent outline on the selection tint (A11Y M7).
        Assert.Equal("Transparent", Setting("controls|ChipOutline.chip-source", "Background"));
        Assert.Equal("True", Setting("controls|ChipOutline.chip-source", "IsDashed"));
        Assert.Equal("{DynamicResource Workspace.Muted}", Setting("controls|ChipOutline.chip-source TextBlock.chip-label", "Foreground"));
        Assert.Equal("False", Setting("ListBoxItem.chip-item:selected controls|ChipOutline.chip-source", "IsDashed"));
        Assert.Equal("2", Setting("ListBoxItem.chip-item:selected controls|ChipOutline.chip-source", "StrokeThickness"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting("ListBoxItem.chip-item:selected controls|ChipOutline.chip-source", "Stroke"));
        // Filter values: filled with a radius; exclusions thicker and error-coloured, and their label says "without".
        Assert.Equal("{DynamicResource Workspace.ChipSurface}", Setting("ursa|ClosableTag.chip", "Background"));
        Assert.Equal("{StaticResource Workspace.FocusAdorner}", Setting("ursa|ClosableTag.chip", "FocusAdorner"));
        Assert.Equal("2", Setting("ursa|ClosableTag.chip.chip-exclude", "BorderThickness"));
        Assert.Equal("{DynamicResource Workspace.ChipExcludeBorder}", Setting("ursa|ClosableTag.chip.chip-exclude", "BorderBrush"));
        foreach (string category in WorkspaceChips.Categories.Where(category => category != "general"))
            Assert.Equal("{DynamicResource Workspace.TagCategory." + char.ToUpperInvariant(category[0]) + category[1..] + "}",
                Setting("controls|ChipOutline.chip-source-" + category + " Border.chip-category-mark", "Background"));
        // AVLN2000: a template selector must not share a comma list with another selector.
        foreach (string selector in Styles().Elements(A + "Style").Select(style => (string)style.Attribute("Selector")!).Where(selector => selector.Contains("chip")))
            Assert.False(selector.Contains(',') && selector.Contains("/template/"), selector);
    }

    [Fact]
    public void TileFavouriteStarAppearsByOpacityOnlyWhenSetHoveredOrFocused()
    {
        Assert.Equal("0", Setting("ToggleButton.tile-favorite", "Opacity"));
        foreach (string shown in new[] { "ToggleButton.tile-favorite:checked", "ToggleButton.tile-favorite:focus-visible",
            "ListBoxItem:pointerover ToggleButton.tile-favorite", "ListBoxItem:focus-within ToggleButton.tile-favorite" })
            Assert.Equal("1", Setting(shown, "Opacity"));
        // Static frame, no shadow; the star itself is the shared FavoriteStar icon (filled in Workspace.Favorite when set, ≥ 3:1 on the
        // chip surface in both themes: FavoriteTintContrastTests), not a styled path.
        Assert.Equal("{DynamicResource Workspace.ChipBorder}", Setting("ToggleButton.tile-favorite", "BorderBrush"));
        Assert.DoesNotContain(Styles().Descendants(A + "Setter"), setter => (string?)setter.Attribute("Property") == "BoxShadow"
            && ((string?)setter.Parent!.Attribute("Selector"))!.Contains("tile-", StringComparison.Ordinal));
        Assert.DoesNotContain(Styles().Elements(A + "Style"), style => ((string?)style.Attribute("Selector"))!.Contains("tile-favorite-star", StringComparison.Ordinal));
        foreach (string theme in new[] { "Dark", "Light" }) AssertContrast(theme, "Workspace.Favorite", "Workspace.ChipSurface", 3.0);
    }

    [Theory]
    [InlineData(BooruTagCategory.Artist, "chip-source-artist"), InlineData(BooruTagCategory.Copyright, "chip-source-copyright")]
    [InlineData(BooruTagCategory.Character, "chip-source-character"), InlineData(BooruTagCategory.Meta, "chip-source-meta")]
    [InlineData(BooruTagCategory.General, "chip-source-general"), InlineData(BooruTagCategory.Unknown, "chip-source-general")]
    public void SourceTagChipsCarryTheirCategoryMarkAndExactSpelling(BooruTagCategory category, string expected)
    {
        ChipOutline chip = WorkspaceChips.CreateSourceTag("blue_eyes", category, compact: true);
        Assert.Equal(new[] { "chip", "chip-source", expected, "chip-compact" }, chip.Classes);
        DockPanel content = Assert.IsType<DockPanel>(chip.Child);
        Border mark = Assert.IsType<Border>(content.Children[0]);
        Assert.Contains("chip-category-mark", mark.Classes);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(mark));
        Assert.Equal("blue_eyes", Assert.IsType<TextBlock>(content.Children[1]).Text); // no rewriting of underscores
        Assert.False(chip.Focusable);
        Assert.False(WorkspaceChips.CreateSourceTag("x", category, compact: false).Classes.Contains("chip-compact"));
    }

    [Fact]
    public void BadgesAndFilterChipsUseTheirClassesAndKeepNamesAndKeys()
    {
        Border badge = WorkspaceChips.CreateBadge("BooruBadgeSource", "Danbooru");
        Assert.Equal(new[] { "chip", "chip-badge" }, badge.Classes);
        Assert.Equal("Danbooru", AutomationProperties.GetName(badge));
        Assert.Equal("Danbooru", Assert.IsType<TextBlock>(badge.Child).Text);

        int removed = 0;
        Ursa.Controls.ClosableTag include = ArchivePage.CreateFilterTag("Tag: rpt", () => removed++);
        Ursa.Controls.ClosableTag exclude = ArchivePage.CreateFilterTag("Without tag: wip", () => removed++, exclude: true);
        Assert.Equal(new[] { "chip", "chip-filter" }, include.Classes);
        Assert.Equal(new[] { "chip", "chip-filter", "chip-exclude" }, exclude.Classes);
        Assert.True(include.Focusable && exclude.Focusable);
        Assert.Equal(MeowChan.Desktop.Localization.Texts.T("archive.removeFilter", "Without tag: wip"), AutomationProperties.GetName(exclude));
        exclude.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        include.Command!.Execute(null);
        Assert.Equal(2, removed);
    }

    [Theory]
    [InlineData("Desktop/Views/Archive/ArchivePage.Tags.cs", 1, 1)]
    // The tag dialogs list tags as toggle rows (C5) and carry no chips.
    public void TagBarChipsAreOwnAndOfferedChips(string file, int own, int offered)
    {
        string source = File.ReadAllText(Root(file));
        Assert.Equal(own, Count(source, "WorkspaceChips.MarkOwn(new Button"));
        Assert.Equal(offered, Count(source, "WorkspaceChips.MarkOffered(new Button"));
    }

    [Fact]
    public void BooruDetailsAndResultCardsUseSourceChipsAndBadges()
    {
        string details = File.ReadAllText(Root("Desktop/Views/Media/BooruMediaDetailsDialog.cs"));
        Assert.Contains("WorkspaceChips.CreateSourceTag(name, category, compact: false)", details);
        Assert.Contains("Classes = { WorkspaceChips.Item }", details);
        Assert.Contains("WorkspaceChips.CreateBadge(name, text)", details);
        Assert.DoesNotContain("CornerRadius(10)", details);
        string card = File.ReadAllText(Root("Desktop/Views/Booru/BooruWorkbenchWindow.cs"));
        Assert.Contains("WorkspaceChips.CreateSourceTag(name, category, compact: true)", card);
    }

    private static int Count(string source, string text)
    {
        int count = 0;
        for (int index = source.IndexOf(text, StringComparison.Ordinal); index >= 0; index = source.IndexOf(text, index + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    private static void AssertContrast(string theme, string foreground, string background, double minimum)
    {
        double ratio = Contrast(Color(theme, foreground), Color(theme, background));
        Assert.True(ratio >= minimum, $"{theme}: {foreground} on {background} is {ratio:0.00}:1, needs {minimum}:1");
    }

    internal static double Contrast(string first, string second)
    {
        double a = Luminance(first), b = Luminance(second);
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
        Assert.Single(Theme(theme).Elements().Where(element => (string?)element.Attribute(X + "Key") == key)).Value.Trim();

    private static XElement Theme(string theme) => Assert.Single(Styles().Descendants(A + "ResourceDictionary")
        .Where(element => (string?)element.Attribute(X + "Key") == theme));

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Assert.Single(Styles().Elements(A + "Style").Where(style => (string?)style.Attribute("Selector") == selector))
            .Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static XElement Styles() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
