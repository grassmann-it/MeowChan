using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The dialog frame (header, local error strip, scrolling body, fixed footer) clips its footer when the Auto rows alone are
/// taller than the window: the body shrinks to nothing and the Auto footer row is cut at the bottom. That happened at the
/// minimum size in German in the saved-searches list (eight wrapped footer actions) and in the rating dialog's error state
/// (error strip plus wrapped footer). There is no renderer in the unit tests, so this models the layout: a conservative
/// per-glyph width estimate, a greedy WrapPanel simulation and the frame's fixed paddings. It computes, for the longest
/// translation in all bundled languages, the footer width every single button needs and the height header + strip + footer +
/// a usable body need at the dialog's own minimum. The pixel check at the minimum is the dialog-frame smoke.
/// </summary>
[Collection("Localization")]
public sealed class DialogFooterBudgetTests : IDisposable
{
    private readonly string _language = Texts.Language;

    public DialogFooterBudgetTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    // Frame metrics from WorkspaceStyles.axaml / DialogFrame: footer Border padding 16,12; header 16,16,16,8; strip host
    // padding 16,0,16,8 around a 1 dip bordered Border with Inset.Message 12; Button padding 10,5 and 1 dip border, MinHeight 30.
    private const double BodySize = 13, SideInset = 16, ButtonChrome = 22, ButtonHeight = 30, ItemGap = 8;
    private const double FooterVertical = 1 + 24, HeaderVertical = 24, StripChrome = 8 + 26, StatusGap = 8;
    // A body that still shows something useful between the fixed parts (it scrolls beyond this).
    private const double UsableBody = 80;

    // ---- the model -------------------------------------------------------------------------------------------------

    /// <summary>Upper-bound advance of a string in em: wide for CJK, a little wider for capitals and non-Latin letters.</summary>
    internal static double Em(string text)
    {
        double em = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int code = rune.Value;
            if (code is >= 0x1100 and <= 0x11FF or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF
                or >= 0xFE30 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x20000 and <= 0x3FFFF) em += 1.0;
            else if (code == ' ') em += 0.28;
            else if (code == '…') em += 0.9;
            else if ("iIl.,:;'|!/".IndexOf((char)Math.Min(code, 0xFFFF)) >= 0 && code < 0x80) em += 0.30;
            else if (code > 0x370 || rune.Value < 0x10000 && char.IsUpper((char)code)) em += 0.66;
            else em += 0.56;
        }
        return em;
    }

    internal static double ButtonWidth(string label, bool semiBold = false) => Em(label) * BodySize * (semiBold ? 1.06 : 1) + ButtonChrome;

    /// <summary>Number of text lines a greedy word wrap needs at <paramref name="width"/> (a word longer than the line breaks anywhere).</summary>
    internal static int Lines(string text, double width, double size = BodySize)
    {
        double space = Em(" ") * size;
        int lines = 0;
        foreach (string paragraph in text.Split('\n'))
        {
            lines++;
            double used = 0;
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                double length = Em(word) * size;
                if (used == 0) used = length;
                else if (used + space + length <= width) used += space + length;
                else { lines++; used = length; }
                while (used > width && width > 0) { lines++; used -= width; }
            }
        }
        return lines;
    }

    internal static double LineHeight(double size) => Math.Ceiling(size * 1.4);

    /// <summary>Rows a WrapPanel with <see cref="ItemGap"/> spacing needs for the items at <paramref name="available"/> width.</summary>
    internal static int WrapRows(IReadOnlyList<double> widths, double available)
    {
        int rows = 1;
        double used = 0;
        foreach (double width in widths)
        {
            if (used == 0) used = width;
            else if (used + ItemGap + width <= available) used += ItemGap + width;
            else { rows++; used = width; }
        }
        return rows;
    }

    internal static double WrappedHeight(int rows) => rows * ButtonHeight + (rows - 1) * ItemGap;

    private static IReadOnlyList<string> Codes()
    {
        string[] codes = Texts.Languages.Select(language => language.Code).ToArray();
        Assert.Equal(12, codes.Length); // en, de and the ten bundled previews: the longest translation must come from all of them.
        return codes;
    }

    // ---- saved searches --------------------------------------------------------------------------------------------

    private static (string Label, bool Primary)[] SavedSearchFooter() => new[]
    {
        (CommandCatalog.Label("archive.saved-searches.save-current"), false),
        (CommandCatalog.Label("archive.saved-searches.apply"), true),
        (CommandCatalog.Label("archive.saved-searches.rename"), false),
        (CommandCatalog.Label("archive.saved-searches.replace"), false),
        (CommandCatalog.Label("archive.saved-searches.delete"), false),
        (Texts.T("savedSearch.reload"), false),
        (Texts.T("savedSearch.recover"), false), // Only offered for a recoverable store, but sized for it.
        (Texts.T("savedSearch.close"), false)
    };

    private static (double MinWidth, double MinHeight, double StripCap, double StatusCap) SavedSearchGeometry()
    {
        XDocument markup = XDocument.Load(Source("Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml"));
        XElement root = markup.Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        double Attribute(XElement element, string name) => double.Parse((string?)element.Attribute(name) ?? throw new InvalidOperationException(name),
            CultureInfo.InvariantCulture);
        XElement Named(string name) => markup.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        XElement strip = Named("DialogFrameErrorStrip").Descendants().First(element => element.Name.LocalName == "ScrollViewer");
        // Modals plan MD3: the minimum width is the L step's, the minimum height the dialog's own (the step's is lower).
        Avalonia.Size step = WorkspaceMetrics.DialogMinimumSize(DialogSize.L);
        Assert.Contains("DialogWindow.Configure(this, DialogSize.L, modal: true);", File.ReadAllText(Source("Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml.cs")));
        Assert.Null(root.Attribute("MinWidth"));
        Assert.Null(root.Attribute("MinHeight"));
        return (step.Width, Math.Max(step.Height, SavedArchiveSearchesDialog.MinimumHeight), Attribute(strip, "MaxHeight"), Attribute(Named("SavedSearchStatusHost"), "MaxHeight"));
    }

    [Fact]
    public void SavedSearchesFooterButtonsFitTheMinimumWidthInEveryLanguage()
    {
        (double minWidth, _, _, _) = SavedSearchGeometry();
        foreach (string code in Codes())
        {
            Texts.SetLanguage(code);
            foreach ((string label, bool primary) in SavedSearchFooter())
                Assert.True(ButtonWidth(label, primary) <= minWidth - 2 * SideInset,
                    code + ": '" + label + "' needs " + Math.Ceiling(ButtonWidth(label, primary)) + " of " + (minWidth - 2 * SideInset) + " dip.");
        }
    }

    [Fact]
    public void SavedSearchesFrameFitsItsMinimumSizeInEveryLanguageInListAndErrorState()
    {
        (double minWidth, double minHeight, double stripCap, double statusCap) = SavedSearchGeometry();
        double inner = minWidth - 2 * SideInset;
        double worst = 0; string worstLanguage = "";
        foreach (string code in Codes())
        {
            Texts.SetLanguage(code);
            double footer = FooterVertical + WrappedHeight(WrapRows(SavedSearchFooter().Select(item => ButtonWidth(item.Label, item.Primary)).ToArray(), inner));
            double header = HeaderVertical + Lines(Texts.T("savedSearch.hint"), inner) * LineHeight(BodySize);
            // List state: the status line sits in the footer above the actions (capped, then it scrolls).
            double list = WorkspaceMetrics.TitleBarHeight + header + footer + StatusGap + statusCap + UsableBody;
            // Error state: the local strip (capped, then it scrolls) replaces the status line.
            double error = WorkspaceMetrics.TitleBarHeight + header + stripCap + StripChrome + footer + UsableBody;
            double needed = Math.Max(list, error);
            if (needed > worst) { worst = needed; worstLanguage = code; }
        }
        Assert.True(worst <= minHeight, "Saved searches need " + Math.Ceiling(worst) + " dip in '" + worstLanguage + "' at width " + minWidth
            + " but MinHeight is " + minHeight + ": the footer would be clipped.");
    }

    // ---- generic: the plain footer verbs of every dialog -----------------------------------------------------------

    [Fact]
    public void ShortFooterVerbsOfEveryDialogFitTheNarrowestFramedWindowInEveryLanguage()
    {
        // The narrowest framed dialog minimum in the product is the small step's (modals plan MD1: prompts and confirmations);
        // a bare verb (English up to 14 characters: Close, Cancel, Retry, Reload, Save, Apply, Discard) must fit one footer
        // line in every language.
        double minWidth = WorkspaceMetrics.DialogMinimumSize(DialogSize.S).Width;
        Regex verb = new(@"\.(close|cancel|retry|reload|save|apply|done|discard)$", RegexOptions.Compiled);
        string[] keys = BuiltInCatalog.English.Where(pair => verb.IsMatch(pair.Key) && pair.Value.Length <= 14 && !pair.Value.Contains('{'))
            .Select(pair => pair.Key).ToArray();
        Assert.True(keys.Length >= 20, "Found only " + keys.Length + " footer verbs; the pattern no longer matches.");
        List<string> tooWide = new();
        foreach (string code in Codes())
        {
            Texts.SetLanguage(code);
            foreach (string key in keys)
            {
                double width = ButtonWidth(Texts.T(key), semiBold: true);
                if (width > minWidth - 2 * SideInset) tooWide.Add(code + " " + key + " (" + Math.Ceiling(width) + ")");
            }
        }
        Assert.True(tooWide.Count == 0, "These footer verbs need more than one footer line: " + string.Join(", ", tooWide));
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
