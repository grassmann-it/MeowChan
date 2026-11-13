using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

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

    /// <summary>The provider tag categories with their own mark colour; anything else is shown as general.</summary>
    internal static readonly string[] Categories = { "artist", "copyright", "character", "meta", "general" };

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

    private static T Add<T>(T control, params string?[] classes) where T : Control
    {
        foreach (string? name in classes)
            if (name != null && !control.Classes.Contains(name)) control.Classes.Add(name);
        return control;
    }
}
