using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// C1: one chip language for tags, filter values and badges. The look lives in WorkspaceStyles.axaml (<c>chip*</c> classes and the
/// <c>Workspace.Chip*</c> tokens); this class only names the classes and builds the two purely visual chips (source tag, badge).
/// Own tags are filled, source tags (Danbooru) dashed with a category mark, so both are told apart without colour.
/// Adding classes changes no behaviour: focus, keyboard activation and automation names stay with each caller.
/// </summary>
internal static class WorkspaceChips
{
    internal const string Chip = "chip";
    /// <summary>An own tag (filled).</summary>
    internal const string Own = "chip-own";
    /// <summary>An own tag that is offered, not assigned (solid outline, no fill): "+ name" in the tag bar and dialogs.</summary>
    internal const string Available = "chip-available";
    /// <summary>An own tag that comes from the thread, not from the file; the chip shows a small thread mark besides.</summary>
    internal const string Thread = "chip-thread";
    /// <summary>A provider tag (dashed outline, muted text, category mark). Never one of the user's own tags.</summary>
    internal const string Source = "chip-source";
    /// <summary>An applied or drafted filter value; removable.</summary>
    internal const string Filter = "chip-filter";
    /// <summary>A filter value that hides matches (error-coloured, thicker outline; its label says "without").</summary>
    internal const string Exclude = "chip-exclude";
    /// <summary>The small size for tiles and cards.</summary>
    internal const string Compact = "chip-compact";
    /// <summary>A static fact (provider, rating, version); not interactive.</summary>
    internal const string Badge = "chip-badge";
    /// <summary>A selectable list item whose content is a chip: the chip itself shows the selection.</summary>
    internal const string Item = "chip-item";
    internal const string CategoryMark = "chip-category-mark";
    internal const string Label = "chip-label";
    internal const string ThreadMark = "chip-thread-mark";
    /// <summary>A button whose content is a source-tag chip (I8: the chip is the action, e.g. "add to search").</summary>
    internal const string SourceButton = "chip-source-button";

    /// <summary>The provider tag categories with their own mark colour; anything else is shown as general.</summary>
    internal static readonly string[] Categories = { "artist", "copyright", "character", "meta", "general" };

    /// <summary>
    /// The content of a chip that removes itself when pressed: the name (a TextBlock, so underscores stay literal) and a small drawn
    /// cross, not a "✕" in the text. The chip's tooltip and accessible name say what pressing it does.
    /// </summary>
    internal static StackPanel RemovableContent(string name)
    {
        TextBlock label = new() { Text = name, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { label, WorkspaceIcons.Create(WorkspaceIcons.CloseSmall) } };
    }

    /// <summary>The name a chip built by <see cref="RemovableContent"/> shows (without the cross), or null for other content.</summary>
    internal static string? RemovableName(object? content) =>
        (content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;

    internal static T MarkOwn<T>(T control, bool compact = false) where T : Control => Add(control, Chip, Own, compact ? Compact : null);
    internal static T MarkOffered<T>(T control) where T : Control => Add(control, Chip, Available);
    internal static T MarkFilter<T>(T control, bool exclude = false) where T : Control => Add(control, Chip, Filter, exclude ? Exclude : null);

    /// <summary>The class of a provider category, e.g. "chip-source-artist"; unknown categories use the general mark.</summary>
    internal static string CategoryClass<TCategory>(TCategory category) where TCategory : struct, Enum
    {
        string name = category.ToString().ToLowerInvariant();
        return "chip-source-" + (Array.IndexOf(Categories, name) >= 0 ? name : "general");
    }

    /// <summary>
    /// A provider tag: dashed outline, a 3-px category mark (colour is an addition, the category is also named by the caller)
    /// and the provider's exact spelling. Not focusable; a list item or card around it owns input.
    /// </summary>
    internal static ChipOutline CreateSourceTag<TCategory>(string name, TCategory category, bool compact) where TCategory : struct, Enum
    {
        Border mark = new() { Classes = { CategoryMark }, VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        TextBlock label = new() { Text = name, Classes = { Label }, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        DockPanel content = new() { Children = { mark, label } };
        DockPanel.SetDock(mark, Dock.Left);
        ChipOutline chip = new() { Child = content, Classes = { Chip, Source, CategoryClass(category) }, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        if (compact) chip.Classes.Add(Compact);
        return chip;
    }

    /// <summary>A static badge (provider, rating, stored version): filled with the accent surface, outlined like a chip.</summary>
    internal static Border CreateBadge(string name, string text, bool compact = false)
    {
        TextBlock label = new() { Text = text, Classes = { Label }, TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap };
        Border badge = new() { Name = name, Child = label, Classes = { Chip, Badge } };
        if (compact) badge.Classes.Add(Compact);
        AutomationProperties.SetName(badge, text);
        return badge;
    }

    /// <summary>
    /// The small house mark of a thread tag on a tile chip (R7): a shape, so it does not depend on a font glyph. The geometry is made
    /// once the mark is shown, because building it needs the platform's render interface.
    /// </summary>
    internal static Avalonia.Controls.Shapes.Path CreateThreadMark()
    {
        Avalonia.Controls.Shapes.Path mark = new() { Classes = { ThreadMark }, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        mark.AttachedToVisualTree += (_, _) => mark.Data ??= Geometry.Parse("M1,6 L6,1 L11,6 M2.5,4.8 L2.5,11 L9.5,11 L9.5,4.8");
        return mark;
    }

    // ---- I7 tile corners -------------------------------------------------------------------------------------------
    internal const string Corner = "tile-corner";
    internal const string FavoriteToggleClass = "tile-favorite";
    internal const string MarkMissing = "M6,1 A5,5 0 1 1 5.99,1 Z M2.5,2.5 L9.5,9.5";
    internal const string MarkUntagged = "M1,1 L6,1 L11,6 L6,11 L1,6 Z M3.6,3.6 L3.7,3.7 M1,11 L11,1";
    internal const string MarkWithdrawn = "M1,1 L11,11 M11,1 L1,11";

    /// <summary>A compact badge for a fixed corner of a preview (type, provider). Static; its text is also in the tile's name.</summary>
    internal static Border CreateCornerBadge(string name, HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        Border badge = CreateBadge(name, string.Empty, compact: true);
        badge.Classes.Add(Corner);
        badge.HorizontalAlignment = horizontal; badge.VerticalAlignment = vertical;
        badge.IsVisible = false; badge.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(badge, AccessibilityView.Raw);
        return badge;
    }

    /// <summary>
    /// A glyph badge for a corner ("not saved", "no media tags", "withdrawn"): a shape, not a font glyph, with its meaning in the
    /// tooltip. Hit-testable for the tooltip, never focusable; the tile's own name carries the same text.
    /// </summary>
    internal static Border CreateCornerMark(string name, HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        Avalonia.Controls.Shapes.Path glyph = new() { Classes = { "tile-mark-glyph" }, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        glyph.AttachedToVisualTree += (_, _) => ApplyMark(glyph);
        Border mark = new() { Name = name, Child = glyph, Classes = { Chip, Badge, Compact, Corner, "tile-mark" }, IsVisible = false,
            HorizontalAlignment = horizontal, VerticalAlignment = vertical };
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        return mark;
    }

    /// <summary>Shows the corner mark with this geometry and tooltip, or hides it when <paramref name="geometry"/> is null.</summary>
    internal static void SetCornerMark(Border mark, string? geometry, string? tip)
    {
        mark.IsVisible = geometry != null;
        mark.Tag = geometry;
        ToolTip.SetTip(mark, geometry == null ? null : tip);
        if (mark.Child is Avalonia.Controls.Shapes.Path glyph) ApplyMark(glyph);
    }

    private static void ApplyMark(Avalonia.Controls.Shapes.Path glyph)
    {
        if (!glyph.IsAttachedToVisualTree() || glyph.Parent is not Border { Tag: string geometry }) return;
        if (!Equals(glyph.Tag, geometry)) { glyph.Data = Geometry.Parse(geometry); glyph.Tag = geometry; }
    }

    /// <summary>
    /// The favourite star of a tile corner: a toggle with the app's one favourite mark (<see cref="FavoriteStar"/>: ☆ outline, ★ filled
    /// when set). Shown when set, or while the tile is hovered or holds keyboard focus (WorkspaceStyles: opacity only, no extra
    /// layout). The caller names it and handles Click.
    /// </summary>
    internal static ToggleButton CreateFavoriteToggle(string name)
    {
        ToggleButton toggle = new()
        {
            Name = name, Classes = { FavoriteToggleClass }, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        };
        void ShowMark() { if (FavoriteStar.MarkOf(toggle.Content) != (toggle.IsChecked == true)) toggle.Content = FavoriteStar.Create(toggle.IsChecked == true, 14); }
        toggle.IsCheckedChanged += (_, _) => ShowMark();
        ShowMark();
        return toggle;
    }

    /// <summary>A chosen chip: tint plus a solid 2-px accent outline (A11Y M7). Add or remove it; nothing animates.</summary>
    internal const string Selected = "chip-selected";
    private const string CheckGeometry = "M1,6.4 L4.6,10 L11,2.4";

    /// <summary>
    /// D4: the check mark a selected tile shows in its preview corner besides the list's tint and bar, so a selection is never colour
    /// alone. A drawn path (not a font glyph); hidden until <see cref="ShowSelection"/>; the tile's own name and the list item's
    /// selection state carry the meaning for assistive technology, so the mark is raw to automation.
    /// </summary>
    internal static Border CreateSelectionCheck(string name)
    {
        Avalonia.Controls.Shapes.Path glyph = new() { Classes = { "tile-selection-glyph" }, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        glyph.AttachedToVisualTree += (_, _) => glyph.Data ??= Geometry.Parse(CheckGeometry);
        Border mark = new() { Name = name, Child = glyph, Classes = { "tile-selection-check" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        return mark;
    }

    /// <summary>Shows or hides the selection check with its tooltip ("Selected").</summary>
    internal static void ShowSelection(Border check, bool selected)
    {
        check.IsVisible = selected;
        ToolTip.SetTip(check, selected ? Texts.T("media.tile.selected") : null);
    }

    /// <summary>Keeps <paramref name="check"/> in step with the selection of the list item that hosts <paramref name="tile"/>; null when there is no list item.</summary>
    internal static IDisposable? ObserveSelection(Control tile, Border check)
    {
        ListBoxItem? item = tile.FindAncestorOfType<ListBoxItem>();
        return item?.GetObservable(ListBoxItem.IsSelectedProperty).Subscribe(new SelectionObserver(check));
    }

    private sealed class SelectionObserver(Border check) : IObserver<bool>
    {
        public void OnNext(bool value) => ShowSelection(check, value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static T Add<T>(T control, params string?[] classes) where T : Control
    {
        foreach (string? name in classes)
            if (name != null && !control.Classes.Contains(name)) control.Classes.Add(name);
        return control;
    }
}
