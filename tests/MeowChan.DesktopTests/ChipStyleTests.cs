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

    // UX review 15.11. ("the pills don't look good yet"): the tag pill. One size (28), full radius (half the height), 12 dip in from the edge
    // (8 where a key hint leads, the hint a 16 dip square 6 dip from the label), a quiet fill at rest, the toolbar hover and pressed values,
    // the shared focus ring, and an assigned tag told apart by tint, edge and its check.
    [Fact]
    public void TagPillTokensAreOnTheGridAndTheRadiusIsHalfTheHeight()
    {
        XElement[] shared = Styles().Element(A + "Styles.Resources")!.Elements().ToArray();
        foreach ((string key, string value) in new[] { ("Workspace.Size.TagPill", "28"), ("Workspace.Radius.TagPill", "14"), ("Workspace.Inset.TagPill", "12,0"),
            ("Workspace.Inset.TagPillKeyed", "8,0,12,0"), ("Workspace.Size.TagKey", "16"), ("Workspace.Space.TagKey", "6") })
            Assert.Equal(value, Assert.Single(shared.Where(element => (string?)element.Attribute(X + "Key") == key)).Value);
        Assert.Equal(28 / 2, double.Parse(Assert.Single(shared.Where(element => (string?)element.Attribute(X + "Key") == "Workspace.Radius.TagPill")).Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TagPillRestHoverPressedFocusAndAssignedFollowTheToolbarLanguage()
    {
        Assert.Equal("{StaticResource Workspace.Size.TagPill}", Setting("Button.chip.tag-pill", "MinHeight"));
        Assert.Equal("{StaticResource Workspace.Radius.TagPill}", Setting("Button.chip.tag-pill", "CornerRadius"));
        Assert.Equal("{StaticResource Workspace.Inset.TagPill}", Setting("Button.chip.tag-pill", "Padding"));
        Assert.Equal("{StaticResource Workspace.Inset.TagPillKeyed}", Setting("Button.chip.tag-pill.chip-keyed", "Padding"));
        Assert.Equal("{StaticResource Workspace.FocusAdorner}", Setting("Button.chip.tag-pill", "FocusAdorner"));
        // Rest: a quiet filled pill with a hairline edge, so a suggestion no longer reads as an outlined (disabled) box.
        Assert.Equal("{DynamicResource Workspace.ChipSurface}", Setting("Button.chip.tag-pill", "Background"));
        Assert.Equal("{DynamicResource Workspace.Border}", Setting("Button.chip.tag-pill", "BorderBrush"));
        const string presenter = " /template/ ContentPresenter#PART_ContentPresenter";
        Assert.Equal("{DynamicResource Workspace.Hover}", Setting("Button.chip.tag-pill:pointerover" + presenter, "Background"));
        Assert.Equal("{DynamicResource Workspace.ControlBorder}", Setting("Button.chip.tag-pill:pointerover" + presenter, "BorderBrush"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedSurface}", Setting("Button.chip.tag-pill:pressed" + presenter, "Background"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting("Button.chip.tag-pill:pressed" + presenter, "BorderBrush"));
        // Assigned: the accent tint and edge (also on hover, where the fill is the hover fill), so it differs from a suggestion by form too.
        Assert.Equal("{DynamicResource Workspace.ChipSelectedSurface}", Setting("Button.chip.tag-pill.tag-pill-assigned", "Background"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting("Button.chip.tag-pill.tag-pill-assigned", "BorderBrush"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting("Button.chip.tag-pill.tag-pill-assigned:pointerover" + presenter, "BorderBrush"));
        Assert.Equal("{DynamicResource Workspace.ChipSelectedBorder}", Setting("Button.chip.tag-pill.tag-pill-partial", "BorderBrush"));
        // The compact tile form keeps its 18 dip size and takes the colours only.
        Assert.Equal("{StaticResource Workspace.Size.ChipCompact}", Setting("Button.chip.tag-pill.chip-compact", "MinHeight"));
        // The key hint: a 16 dip rounded square without an edge, Muted digit; the content row puts 6 dip between its parts.
        Assert.Equal("{StaticResource Workspace.Size.TagKey}", Setting("Border.chip-badge.chip-key", "Height"));
        Assert.Equal("{StaticResource Workspace.Size.TagKey}", Setting("Border.chip-badge.chip-key", "MinWidth"));
        Assert.Equal("{StaticResource Workspace.Radius.Badge}", Setting("Border.chip-badge.chip-key", "CornerRadius"));
        Assert.Equal("0", Setting("Border.chip-badge.chip-key", "BorderThickness"));
        Assert.Equal("{DynamicResource Workspace.Muted}", Setting("Border.chip-badge.chip-key TextBlock.chip-label", "Foreground"));
        Assert.Equal("{StaticResource Workspace.Space.TagKey}", Setting("StackPanel.tag-pill-content", "Spacing"));
        // The source suggestion keeps its dashed outline (no fill) in the same geometry; ChipOutline draws its edge inside, hence 13 and 9.
        Assert.Equal("{StaticResource Workspace.Size.TagPill}", Setting("controls|ChipOutline.chip-source.tag-pill", "MinHeight"));
        Assert.Equal("13,0", Setting("controls|ChipOutline.chip-source.tag-pill", "Padding"));
        Assert.Equal("9,0,13,0", Setting("controls|ChipOutline.chip-source.tag-pill.chip-keyed", "Padding"));
        Assert.Equal("{DynamicResource Workspace.Muted}", Setting("controls|ChipOutline.chip-source.tag-pill Border.chip-key TextBlock.chip-label", "Foreground"));
        // A tag pill's state styles may not share a comma list with a template selector (AVLN2000) and may not animate.
        foreach (string selector in Styles().Elements(A + "Style").Select(style => (string)style.Attribute("Selector")!).Where(selector => selector.Contains("tag-pill")))
            Assert.False(selector.Contains(','), selector);
        Assert.DoesNotContain(Styles().Elements(A + "Style").Where(style => ((string?)style.Attribute("Selector"))!.Contains("tag-pill", StringComparison.Ordinal)),
            style => style.Elements(A + "Style.Animations").Any() || style.Elements(A + "Setter").Any(setter => (string?)setter.Attribute("Property") == "Transitions"));
    }

    // Both themes: the pill's edges and text read on every surface it sits on (the edge is a hairline, so the label and the fill carry it;
    // hover and pressed edges reach 3:1), and the key hint's digit reads on its window-background square.
    [Theory, InlineData("Dark"), InlineData("Light")]
    public void TagPillStatesReadInBothThemes(string theme)
    {
        foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.Overlay" })
        {
            AssertContrast(theme, "Workspace.ControlBorder", surface, 3.0);   // hover edge
            AssertContrast(theme, "Workspace.ChipSelectedBorder", surface, 3.0); // pressed and assigned edge
        }
        foreach ((string text, string fill) in new[] { ("Workspace.Text", "Workspace.ChipSurface"), ("Workspace.Text", "Workspace.Hover"),
            ("Workspace.Text", "Workspace.ChipSelectedSurface"), ("Workspace.Muted", "Workspace.Background") })
            AssertContrast(theme, text, fill, 4.5);
        // The assigned tint is told apart from a suggestion's fill by more than the tint: the edge changes from hairline to accent (3:1 or more).
        AssertContrast(theme, "Workspace.ChipSelectedBorder", "Workspace.ChipSurface", 3.0);
        // The tint of a suggestion and of an assigned tag are different values in both themes.
        Assert.NotEqual(Color(theme, "Workspace.ChipSurface"), Color(theme, "Workspace.ChipSelectedSurface"));
    }

    [Fact]
    public void TagPillClassesAreAddedAlongsideTheChipClassesAndNeverDropThem()
    {
        Button suggestion = WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOffered(new Button()));
        Assert.Equal(new[] { "chip", "chip-available", "tag-pill" }, suggestion.Classes.Where(name => name[0] != ':'));
        Button assigned = WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOwn(new Button()), WorkspaceChips.TagPillAssigned);
        Assert.Equal(new[] { "chip", "chip-own", "tag-pill", "tag-pill-assigned" }, assigned.Classes.Where(name => name[0] != ':'));
        Button tile = WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOwn(new Button(), compact: true));
        Assert.Equal(new[] { "chip", "chip-own", "chip-compact", "tag-pill" }, tile.Classes.Where(name => name[0] != ':'));
        Assert.Equal("tag-pill-partial", WorkspaceChips.TagPillPartial);
        // The call sites: the quick tag surface (suggestions, assigned all/some, key badges and content rows), the reader tag bar, the thread tags,
        // the stamp chip (a read-only badge since the dead-affordance audit) and the tile chips.
        string surface = File.ReadAllText(Root("Desktop/Controls/QuickTagSurface.cs"));
        Assert.Contains("WorkspaceChips.MarkTagPill(button);", surface);
        Assert.Contains("WorkspaceChips.TagPill } };", surface);
        Assert.Contains("WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOwn(chip), WorkspaceChips.TagPillAssigned)", surface);
        Assert.Contains("WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOffered(chip), WorkspaceChips.TagPillPartial)", surface);
        Assert.Equal(2, Count(surface, "Classes = { WorkspaceChips.TagPillContent }"));
        Assert.Contains("MarkTagPill(chip, MeowChan.Desktop.Controls.WorkspaceChips.TagPillAssigned)", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Tags.cs")));
        Assert.Contains("WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOwn(chip), WorkspaceChips.TagPillAssigned)", File.ReadAllText(Root("Desktop/Controls/GalleryThreadTagSection.cs")));
        // The stamp chip is a static badge marked read-only (dead-affordance audit): the cross beside it removes the name, the pill itself does nothing.
        Assert.Contains("WorkspaceChips.MarkReadOnly(WorkspaceChips.CreateBadge(\"TaggingStampChip\", name))", File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Stamp.cs")));
        Assert.Contains("WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOwn(new Button { Name = \"MediaTileTag\"", File.ReadAllText(Root("Desktop/MediaLibraryTileView.cs")));
    }

    // Design plan 16.11.2026: the joined pill. Selectors without commas or animations (TagPillRestHoverPressedFocusAndAssignedFollowTheToolbarLanguage checks it for
    // every tag-pill selector), tokens for radii and the inset, the cross a 28 square in the state's fill and edge.
    [Fact]
    public void TheJoinedAssignedPillHasItsChipStartAndItsCrossEndInTheStateColours()
    {
        XElement[] shared = Styles().Element(A + "Styles.Resources")!.Elements().ToArray();
        foreach ((string key, string value) in new[] { ("Workspace.Radius.TagPillStart", "14,0,0,14"), ("Workspace.Radius.TagPillEnd", "0,14,14,0"), ("Workspace.Inset.TagPillJoined", "12,0,4,0") })
            Assert.Equal(value, Assert.Single(shared.Where(element => (string?)element.Attribute(X + "Key") == key)).Value);
        Assert.Equal("1,1,0,1", Setting("Button.chip.tag-pill.tag-pill-joined", "BorderThickness"));
        Assert.Equal("{StaticResource Workspace.Radius.TagPillStart}", Setting("Button.chip.tag-pill.tag-pill-joined", "CornerRadius"));
        Assert.Equal("{StaticResource Workspace.Inset.TagPillJoined}", Setting("Button.chip.tag-pill.tag-pill-joined", "Padding"));
        const string remove = "Button.icon-square.tag-pill-remove";
        Assert.Equal("{StaticResource Workspace.Size.TagPill}", Setting(remove, "Width"));
        Assert.Equal("{StaticResource Workspace.Size.TagPill}", Setting(remove, "Height"));
        Assert.Equal("0,1,1,1", Setting(remove, "BorderThickness"));
        Assert.Equal("{StaticResource Workspace.Radius.TagPillEnd}", Setting(remove, "CornerRadius"));
        Assert.Equal("{DynamicResource Workspace.Muted}", Setting(remove, "Foreground"));
        Assert.Equal("{StaticResource Workspace.FocusAdorner}", Setting(remove, "FocusAdorner"));
        // The same fill and edge as the chip half of the state.
        Assert.Equal(Setting("Button.chip.tag-pill.tag-pill-assigned", "Background"), Setting(remove + ".tag-pill-assigned", "Background"));
        Assert.Equal(Setting("Button.chip.tag-pill.tag-pill-assigned", "BorderBrush"), Setting(remove + ".tag-pill-assigned", "BorderBrush"));
        Assert.Equal(Setting("Button.chip.tag-pill.tag-pill-partial", "BorderBrush"), Setting(remove + ".tag-pill-partial", "BorderBrush"));
        const string presenter = " /template/ ContentPresenter#PART_ContentPresenter";
        Assert.Equal("{DynamicResource Workspace.Hover}", Setting(remove + ":pointerover" + presenter, "Background"));
        Assert.Equal("{DynamicResource Workspace.Text}", Setting(remove + ":pointerover" + presenter, "Foreground"));
        // Declared after icon-square, so its 28 replaces the 32 (no specificity in Avalonia styles: the later one wins).
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        Assert.True(styles.IndexOf("<Style Selector=\"Button.icon-square\">", StringComparison.Ordinal) < styles.IndexOf("<Style Selector=\"" + remove + "\">", StringComparison.Ordinal));
        // The surface wears the classes; the check mark of an assigned pill is gone.
        string surface = File.ReadAllText(Root("Desktop/Controls/QuickTagSurface.cs"));
        Assert.Contains("JoinedClass = \"tag-pill-joined\", RemoveClass = \"tag-pill-remove\"", surface);
        Assert.Contains("remove.Classes.Add(assignment.State == TagToggleState.All ? WorkspaceChips.TagPillAssigned : WorkspaceChips.TagPillPartial);", surface);
        Assert.Contains("remove.IsTabStop = false;", surface);
        Assert.Contains("Spacing = 0, Children = { chip, remove }", surface);
        Assert.DoesNotContain("CheckGeometry", surface);
        Assert.DoesNotContain("bar-flat", surface);
    }

    [Fact]
    public void TheTagStripGapsAreEightInBothDirectionsAndTheFitMeasuresTheHitZone()
    {
        string surface = File.ReadAllText(Root("Desktop/Controls/QuickTagSurface.cs"));
        // Column gap 8 (the wrap panel's item spacing, also the fit's), row gap 8 = the hit zone's 4 above and below the 28 dip pill.
        Assert.Contains("private const double StripItemSpacing = 8;", surface);
        Assert.Contains("new() { ItemSpacing = StripItemSpacing, LineSpacing = 0,", surface);
        Assert.Contains("Padding = new Thickness(0, 4), Child = chip", surface);
        Assert.Contains("item.Zone.Measure(new Size(width, double.PositiveInfinity));", surface);
        Assert.Contains("AssignedItemSpacing = 8, AssignedLineSpacing = 8", surface);
        // 28 + 2 * 4 = 36 per row: the fit sees the same rows the wrap panel lays out. The assigned pills are 28 high with their cross joined to them,
        // so their rows are 8 apart as their pills are (AssignedLineSpacing 8).
        Assert.Equal(36, 28 + 2 * 4);
        // 80-wide pills, three to a 280 row.
        Assert.Equal(9, QuickTagStripFit.Count(Enumerable.Repeat(new Avalonia.Size(80, 36), 40).ToArray(), 280, 3 * 36, 8, 9));
        Assert.Equal(12, QuickTagStripFit.Count(Enumerable.Repeat(new Avalonia.Size(80, 36), 40).ToArray(), 280, 4 * 36, 8, 9));
        Assert.Equal(9, QuickTagStripFit.Count(Enumerable.Repeat(new Avalonia.Size(80, 36), 40).ToArray(), 280, 4 * 36 - 1, 8, 9));
    }

    [Fact]
    public void BooruDetailsAndResultCardsUseSourceChipsAndBadges()
    {
        string details = File.ReadAllText(Root("Desktop/Views/Media/BooruMediaDetailsDialog.cs"));
        Assert.Contains("WorkspaceChips.CreateSourceTag(name, category, compact: false)", details);
        Assert.Contains("Classes = { WorkspaceChips.Item }", details);
        Assert.Contains("WorkspaceChips.CreateBadge(name, text)", details);
        Assert.DoesNotContain("CornerRadius(10)", details);
        string card = BooruViewSources.ReadAll(Root("Desktop/Views/Booru/BooruWorkbenchView.cs"));
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
