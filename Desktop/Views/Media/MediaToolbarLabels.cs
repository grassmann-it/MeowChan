using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;

namespace MeowChan;

/// <summary>
/// F10 (Tom 21.11.2026): the text labels of the media toolbar's own switches depend on the width of the row only (like the Booru view switch's
/// labels): the filter toggle keeps its text from <see cref="FilterLabelWidth"/> up (below it only the icon and the count badge stay), and the two
/// sidebar switches ("Tags" left, "Media" right) get their word beside the icon from <see cref="SidebarLabelsWidth"/> up. Both widths are
/// measures of the row in dip, not limits of the user's data. The name, tooltip and accessible name never change with the label.
/// </summary>
internal static class MediaToolbarLabels
{
    /// <summary>The filter toggle shows its text from this row width up; below it only icon (and badge).</summary>
    internal const double FilterLabelWidth = 1060;
    /// <summary>The sidebar switches show their words from this row width up (search minimum, filters, order and all tools with labels still fit).</summary>
    internal const double SidebarLabelsWidth = 1280;

    /// <summary>An unknown width (not measured yet) shows the labels of the filter toggle.</summary>
    internal static bool ShowsFilterLabel(double rowWidth) => !double.IsFinite(rowWidth) || rowWidth <= 0 || rowWidth >= FilterLabelWidth;

    /// <summary>An unknown width keeps the switches icon-only.</summary>
    internal static bool ShowsSidebarLabels(double rowWidth) => double.IsFinite(rowWidth) && rowWidth >= SidebarLabelsWidth;

    /// <summary>
    /// Puts <paramref name="text"/> beside the icon of a toggle (text first when <paramref name="textFirst"/>, so the icon of a right-hand
    /// sidebar stays at the edge), or returns the toggle to the icon-only square. The icon is the button's own control and is reused.
    /// </summary>
    internal static void Apply(ToggleButton button, bool show, string text, bool textFirst)
    {
        Control? icon = button.Content is StackPanel panel ? panel.Children.OfType<Control>().FirstOrDefault(c => c is Avalonia.Controls.Shapes.Path) : button.Content as Control;
        if (icon == null) return;
        if (button.Content is StackPanel old) old.Children.Remove(icon);
        if (!show)
        {
            button.Content = icon;
            button.Classes.Set("icon-square", true);
            return;
        }
        TextBlock label = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (textFirst) { content.Children.Add(label); content.Children.Add(icon); }
        else { content.Children.Add(icon); content.Children.Add(label); }
        button.Classes.Set("icon-square", false);
        button.Content = content;
    }
}
