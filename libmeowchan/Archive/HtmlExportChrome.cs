namespace MeowChan.Archive;

/// <summary>Fixed page frame shared by the thread and collection pages of an HTML export. It holds no captured data;
/// every text is a localized constant encoded by <see cref="HtmlExportLocalization"/>.</summary>
internal static class HtmlExportChrome
{
    // The theme switch works without script (the CSP allows none): three radio inputs at the top of the body precede
    // the page wrapper, and the style sheet selects the palette with "#theme-light:checked ~ .page" (classic sibling
    // pattern, no :has()). "System" is checked by default and follows prefers-color-scheme. Nothing is stored, so a
    // reload returns to the system default. The inputs stay focusable (arrow keys switch, Space selects) and carry the
    // accessible names; the visible labels in the header are their click targets and show focus and selection.
    internal static string ThemeInputs(HtmlExportLocalization text)
    {
        string theme = text.Html(HtmlExportText.Theme) + ": ";
        return "<input class=\"toggle\" type=\"radio\" name=\"theme\" id=\"theme-system\" checked aria-label=\"" + theme + text.Html(HtmlExportText.ThemeSystem) + "\">"
            + "<input class=\"toggle\" type=\"radio\" name=\"theme\" id=\"theme-light\" aria-label=\"" + theme + text.Html(HtmlExportText.ThemeLight) + "\">"
            + "<input class=\"toggle\" type=\"radio\" name=\"theme\" id=\"theme-dark\" aria-label=\"" + theme + text.Html(HtmlExportText.ThemeDark) + "\">";
    }

    internal static string ThemeSwitch(HtmlExportLocalization text) =>
        "<span class=\"switch\" aria-hidden=\"true\"><span class=\"switch-name\">" + text.Html(HtmlExportText.Theme) + "</span>"
        + "<span class=\"segments\"><label for=\"theme-system\">" + text.Html(HtmlExportText.ThemeSystem) + "</label>"
        + "<label for=\"theme-light\">" + text.Html(HtmlExportText.ThemeLight) + "</label>"
        + "<label for=\"theme-dark\">" + text.Html(HtmlExportText.ThemeDark) + "</label></span></span>";
}
