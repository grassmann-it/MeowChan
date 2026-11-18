using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

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

    /// <summary>The one external address in an export: our own constant, never captured content.</summary>
    internal const string CreditUrl = "https://grassmann-it.de";
    /// <summary>Fixed file name of the cat pair copied next to index.html (both logos side by side, 300 x 150).</summary>
    internal const string CatsFileName = "meowchan-cats.png";
    // Same wording as the app's About credits (AboutWindow.PhotoCreditName); plain text, no link.
    internal const string PhotoCreditName = "Alexander Huxel · Webentwicklung Huxel";
    private const string CatsResource = "MeowChan.Archive.meowchan-cats.png";

    /// <summary>The MeowChan version that writes the export (shared Directory.Build.props version, no build hash).</summary>
    internal static string Version { get; } = typeof(HtmlExportChrome).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)[0]
        ?? typeof(HtmlExportChrome).Assembly.GetName().Version?.ToString(3) ?? "?";

    // The About dialog uses the same technique as the theme switch: a checkbox before the page wrapper, opened and
    // closed by its labels (header button, close button, the scrim behind the dialog) or by Space on the focused input.
    internal static string AboutInput(HtmlExportLocalization text) =>
        "<input class=\"toggle\" type=\"checkbox\" id=\"about-open\" aria-label=\"" + text.Html(HtmlExportText.AboutTitle) + "\" aria-controls=\"about\">";

    internal static string AboutButton(HtmlExportLocalization text) =>
        "<label class=\"button about-button\" for=\"about-open\" aria-hidden=\"true\">" + text.Html(HtmlExportText.About) + "</label>";

    /// <summary>The About dialog. Fact values are raw strings and are encoded here like captured post content.</summary>
    internal static string About(HtmlExportLocalization text, string rootPrefix, IEnumerable<(HtmlExportText Label, string? Value)> facts)
    {
        System.Text.StringBuilder html = new("<div class=\"about-layer\"><label class=\"scrim\" for=\"about-open\" aria-hidden=\"true\"></label>"
            + "<section class=\"about\" id=\"about\" role=\"dialog\" aria-labelledby=\"about-title\"><h2 id=\"about-title\">"
            + text.Html(HtmlExportText.AboutTitle) + "</h2><dl>");
        string unspecified = text.Get(HtmlExportText.Unspecified);
        foreach ((HtmlExportText label, string? value) in facts)
            html.Append("<dt>").Append(text.Html(label)).Append("</dt><dd>")
                .Append(ArchiveHtmlExporter.Encode(string.IsNullOrWhiteSpace(value) ? unspecified : value)).Append("</dd>");
        string madeBy = text.Get(HtmlExportText.MadeBy);
        int slot = madeBy.IndexOf("{0}", System.StringComparison.Ordinal);
        html.Append("</dl><p class=\"made\">").Append(text.Html(HtmlExportText.CollectedWith, Version)).Append("<br>")
            .Append(ArchiveHtmlExporter.Encode(madeBy[..slot]))
            .Append("<a href=\"" + CreditUrl + "\" rel=\"noopener noreferrer\">grassmann-it.de</a>")
            .Append(ArchiveHtmlExporter.Encode(madeBy[(slot + 3)..])).Append("</p>")
            .Append("<img class=\"cats\" loading=\"lazy\" src=\"").Append(rootPrefix).Append(CatsFileName)
            .Append("\" width=\"300\" height=\"150\" alt=\"").Append(text.Html(HtmlExportText.CatsAlt)).Append("\">")
            .Append("<p class=\"credit\">").Append(text.Html(HtmlExportText.PhotoCredit, PhotoCreditName)).Append("</p>")
            .Append("<p class=\"credit\">").Append(text.Html(HtmlExportText.ThemeHint)).Append("</p>")
            .Append("<p class=\"about-actions\"><label class=\"button about-close\" for=\"about-open\">")
            .Append(text.Html(HtmlExportText.AboutClose)).Append("</label></p></section></div>");
        return html.ToString();
    }

    /// <summary>Copies the bundled cat pair into the export root under its fixed name.</summary>
    internal static async Task WriteCatsAsync(string stage, CancellationToken token)
    {
        await using Stream source = typeof(HtmlExportChrome).Assembly.GetManifestResourceStream(CatsResource)
            ?? throw new InvalidDataException("The bundled export image is missing.");
        await using FileStream target = new(Path.Combine(stage, CatsFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        await source.CopyToAsync(target, token).ConfigureAwait(false);
    }
}
