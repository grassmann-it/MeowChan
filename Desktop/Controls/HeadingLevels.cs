using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Styling;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Gives the two heading classes of the workspace a heading role for assistive technology (1.1.0 accessibility audit M10):
/// <c>page-title</c> is level 1, <c>section-title</c> level 2, so screen readers can jump from heading to heading. The look
/// of the classes stays in WorkspaceStyles.axaml; this adds only the automation property, for every current and future
/// use of the classes (also <see cref="SelectableTextBlock"/>, which does not match a plain <c>TextBlock</c> selector).
/// </summary>
internal static class HeadingLevels
{
    internal const string PageTitleClass = "page-title";
    internal const string SectionTitleClass = "section-title";

    internal static IReadOnlyList<Style> Create() => new[]
    {
        For(PageTitleClass, 1),
        For(SectionTitleClass, 2)
    };

    internal static void Install(Styles styles)
    {
        foreach (Style style in Create()) styles.Add(style);
    }

    private static Style For(string cssClass, int level)
    {
        Style style = new(selector => selector.Is<TextBlock>().Class(cssClass));
        style.Setters.Add(new Setter(AutomationProperties.HeadingLevelProperty, level));
        return style;
    }
}
