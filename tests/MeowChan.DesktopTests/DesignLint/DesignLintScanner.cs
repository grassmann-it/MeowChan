using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests.DesignLint;

/// <summary>One design-system regression found in a Desktop source file. The line number is for the report only; the
/// baseline is keyed on (rule, file, snippet) so edits that merely move code do not disturb it.</summary>
internal sealed record DesignViolation(string Rule, string File, int Line, string Snippet);

/// <summary>Identifiers of the lint rules. The strings are stored in the baseline file.</summary>
internal static class DesignRules
{
    /// <summary>A numeric FontSize literal whose value is on the type scale: should be a Workspace.Text token or a class.</summary>
    public const string FontSizeLiteral = "font-size-literal";
    /// <summary>A numeric FontSize literal whose value is not on the type scale at all.</summary>
    public const string FontSizeOffScale = "font-size-off-scale";
    /// <summary>A hex colour (#RGB, #RRGGBB, #AARRGGBB) outside WorkspaceStyles.axaml.</summary>
    public const string ColorHex = "color-hex";
    /// <summary>Brushes.X, Colors.X (except Transparent), Color.FromRgb/FromArgb/FromUInt32/Parse in code.</summary>
    public const string ColorCode = "color-code";
    /// <summary>Spacing, ItemSpacing, LineSpacing, RowSpacing, ColumnSpacing, Margin, Padding with a literal off the 0/2/4/8/12/16/24/32 grid.</summary>
    public const string SpacingOffGrid = "spacing-off-grid";
    /// <summary>A symbol or arrow character used as the icon of a button or label instead of a catalog path.</summary>
    public const string GlyphIcon = "glyph-icon";
    /// <summary>RowHeight as a literal instead of WorkspaceMetrics.</summary>
    public const string RowHeightLiteral = "row-height-literal";
    /// <summary>Height/MinHeight literal on a button or input control instead of a Workspace.Size token.</summary>
    public const string ControlHeightLiteral = "control-height-literal";
    /// <summary>The page root does not take its inset from Workspace.Inset.Page.</summary>
    public const string PageInset = "page-inset";

    public static readonly string[] All =
    {
        FontSizeLiteral, FontSizeOffScale, ColorHex, ColorCode, SpacingOffGrid, GlyphIcon, RowHeightLiteral, ControlHeightLiteral, PageInset
    };
}

/// <summary>A documented, permanent exception (not a debt): the violation is allowed because the design spec names it.</summary>
internal sealed record DesignException(string Rule, string File, string SnippetContains, string Reason);

/// <summary>
/// Text-level lint of Desktop/**/*.axaml and Desktop/**/*.cs against the design system (UX-DESIGNSYSTEM-2026-11-13 P9).
/// Needs no Avalonia application: it reads the sources, strips comments and runs regular expressions (XAML: XDocument for the
/// element-aware rules). WorkspaceStyles.axaml is the token definition and is never scanned for style rules.
/// </summary>
internal static class DesignLintScanner
{
    public const string StylesFile = "Desktop/WorkspaceStyles.axaml";

    /// <summary>Space scale (Workspace.Space.*) plus zero. Everything else in Spacing/Margin/Padding is off grid.</summary>
    public static readonly double[] SpaceGrid = { 0, 2, 4, 8, 12, 16, 24, 32 };

    /// <summary>Fallback when WorkspaceStyles.axaml cannot be parsed. Normally read from the Workspace.Text.* tokens.</summary>
    private static readonly double[] FallbackTypeScale = { 11, 12, 13, 14, 16, 20 };

    /// <summary>Documented exceptions from section 1 of the spec. Keep this list short; everything else is baseline debt.</summary>
    public static readonly DesignException[] Exceptions =
    {
        // Media overlays lie on arbitrary image content and stay theme-independent (palette review 2026-11-13, items 4, 5, 6, 8).
        new(DesignRules.ColorCode, "Desktop/ArchiveAttachmentView.cs", "Color.FromArgb", "Video play badge on arbitrary media (palette item 4)."),
        new(DesignRules.ColorCode, "Desktop/ArchiveAttachmentView.cs", "Brushes.White", "Play triangle of the video badge (palette item 4)."),
        new(DesignRules.ColorCode, "Desktop/GalleryViewer.Favorites.cs", "Color.FromArgb", "Favourite badge on media (palette item 5)."),
        new(DesignRules.ColorCode, "Desktop/GalleryViewer.Favorites.cs", "Brushes.White", "Favourite badge star (palette item 5; GalleryFavoriteStateTests pins it)."),
        new(DesignRules.ColorCode, "Desktop/GalleryViewer.Favorites.cs", "Brushes.Gold", "Favourite badge star when marked (palette item 5)."),
        new(DesignRules.ColorCode, "Desktop/GalleryViewer.Overlay.cs", "Color.FromArgb", "Edge hint over media (palette item 6) and overlay tone alpha."),
        new(DesignRules.ColorCode, "Desktop/GalleryViewer.Overlay.cs", "Color.FromRgb", "Edge hint and fallback tone when the Workspace.Overlay resource is missing."),
        new(DesignRules.ColorCode, "Desktop/GalleryViewer.cs", "Brushes.Black", "Letterbox behind media (palette item 8)."),
        new(DesignRules.ColorCode, "Desktop/ReaderBodyTextBlock.cs", "Colors.Black", "Opacity-mask gradient, not a visible colour."),
        // Key-cap symbols of shortcut gestures are text of the shortcut label, not icons.
        new(DesignRules.GlyphIcon, "Desktop/Commands/EffectiveShortcutBindings.cs", "", "Modifier and navigation key symbols (⌘ ⌥ ⇧ ← ↑ ...) in shortcut gesture text."),
    };

    private static readonly Regex FontSizeXaml = new(
        @"(?:\bFontSize\s*=\s*""|Property\s*=\s*""FontSize""\s+Value\s*=\s*"")(?<v>[0-9]+(?:\.[0-9]+)?)""", RegexOptions.Compiled);
    private static readonly Regex FontSizeCode = new(
        @"\bFontSize\s*=\s*(?<v>[0-9]+(?:\.[0-9]+)?)[dDfF]?(?![\w.(])|FontSizeProperty\s*,\s*(?<v>[0-9]+(?:\.[0-9]+)?)[dDfF]?(?![\w.(])", RegexOptions.Compiled);

    private static readonly Regex HexColor = new(@"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})(?![0-9A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex ColorCodeApi = new(
        @"\bBrushes\.(?!Transparent\b)\w+|\bColors\.(?!Transparent\b)\w+|\bColor\.(?:FromRgb|FromArgb|FromUInt32|Parse|TryParse)\b|\bnew\s+Color\s*\(", RegexOptions.Compiled);

    private static readonly Regex SpacingXaml = new(
        @"(?:\b(?<n>(?:Item|Line|Row|Column)?Spacing)\s*=\s*""|Property\s*=\s*""(?<n>(?:Item|Line|Row|Column)?Spacing)""\s+Value\s*=\s*"")(?<v>[^""{]*)""", RegexOptions.Compiled);
    private static readonly Regex ThicknessXaml = new(
        @"(?:\b(?<n>Margin|Padding)\s*=\s*""|Property\s*=\s*""(?<n>Margin|Padding)""\s+Value\s*=\s*"")(?<v>[^""{]*)""", RegexOptions.Compiled);
    private static readonly Regex SpacingCode = new(
        @"\b(?<n>(?:Item|Line|Row|Column)?Spacing)\s*=\s*(?<v>-?[0-9]+(?:\.[0-9]+)?)[dDfF]?(?![\w.(])", RegexOptions.Compiled);
    private static readonly Regex ThicknessCode = new(
        @"(?:(?<n>\w+)\s*=\s*)?new\s+Thickness\s*\((?<v>[^()]*)\)", RegexOptions.Compiled);

    private static readonly Regex RowHeightXaml = new(
        @"(?:\bRowHeight\s*=\s*""|Property\s*=\s*""RowHeight""\s+Value\s*=\s*"")(?<v>[0-9]+(?:\.[0-9]+)?)""", RegexOptions.Compiled);
    private static readonly Regex RowHeightCode = new(
        @"\bRowHeight\s*=\s*(?<v>[0-9]+(?:\.[0-9]+)?)[dDfF]?(?![\w.(])", RegexOptions.Compiled);

    /// <summary>Controls whose height must come from a Workspace.Size token.</summary>
    private static readonly HashSet<string> HeightControls = new(StringComparer.Ordinal)
    {
        "Button", "ToggleButton", "RepeatButton", "SplitButton", "TextBox", "ComboBox", "NumericUpDown", "AutoCompleteBox",
        "DatePicker", "CalendarDatePicker", "SearchBox", "TagInput", "AccessibleNumberInput", "AddInput"
    };
    private static readonly Regex NewControl = new(
        @"\bnew\s+(?<t>" + string.Join("|", HeightControls) + @")\b\s*(?:\([^()]*\))?\s*\{", RegexOptions.Compiled);
    private static readonly Regex HeightAssign = new(
        @"\b(?<n>(?:Min)?Height)\s*=\s*(?<v>[0-9]+(?:\.[0-9]+)?)[dDfF]?(?![\w.(])", RegexOptions.Compiled);

    private static readonly Regex CodeString = new(@"""(?<s>(?:[^""\\\r\n]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex XamlAttr = new(@"\b(?<n>[A-Za-z][\w.:]*)\s*=\s*""(?<s>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex XamlText = new(@">(?<s>[^<>{}]{1,10})<", RegexOptions.Compiled);

    /// <summary>The page roots and how to find the element that must carry the page inset. P2 may move the locator.</summary>
    private sealed record PageRoot(string File, string? NamedElement, string[] FirstChildPath);

    private static readonly PageRoot[] PageRoots =
    {
        new("Desktop/Shell/MainWindow.axaml", "WorkspaceBody", Array.Empty<string>()), // Downloads
        new("Desktop/Views/Sources/SourcesPage.axaml", null, new[] { "ScrollViewer", "StackPanel" }),
        new("Desktop/Views/Plugins/PluginsPage.axaml", null, new[] { "Grid" }),
        new("Desktop/Views/Diagnostics/DiagnosticsPage.axaml", null, new[] { "Grid" }),
        new("Desktop/Views/Statistics/StatisticsPage.axaml", "StatisticsScroll", new[] { "StackPanel" }),
        new("Desktop/Views/Archive/ArchivePage.axaml", "ArchiveRoot", Array.Empty<string>()),
        new("Desktop/Views/Rules/RulesPage.axaml", null, new[] { "Grid" }),
        new("Desktop/Views/Settings/SettingsEditor.axaml", null, new[] { "Grid" }),
    };

    /// <summary>Pages built in code: the file must reference Workspace.Inset.Page somewhere.</summary>
    private static readonly string[] CodePageFiles = { "Desktop/Views/Media/MediaPage.cs" };

    // ---- enumeration ------------------------------------------------------------------------------------------------

    public static IReadOnlyList<DesignViolation> ScanRepository(string repositoryRoot)
    {
        string desktop = Path.Combine(repositoryRoot, "Desktop");
        double[] scale = ReadTypeScale(Path.Combine(repositoryRoot, StylesFile));
        List<DesignViolation> all = new();
        foreach (string path in Directory.EnumerateFiles(desktop, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".axaml", StringComparison.Ordinal) || p.EndsWith(".cs", StringComparison.Ordinal))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
            all.AddRange(ScanFile(relative, File.ReadAllText(path), scale));
        }
        return all;
    }

    /// <summary>Reads Workspace.Text.* sizes from the token file so the scale follows the design system.</summary>
    public static double[] ReadTypeScale(string stylesPath)
    {
        try
        {
            string text = File.ReadAllText(stylesPath);
            List<double> values = Regex.Matches(text, @"x:Key=""Workspace\.Text\.\w+"">\s*(?<v>[0-9]+(?:\.[0-9]+)?)\s*<", RegexOptions.None)
                .Select(m => double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture)).Distinct().OrderBy(v => v).ToList();
            return values.Count > 0 ? values.ToArray() : FallbackTypeScale;
        }
        catch (IOException)
        {
            return FallbackTypeScale;
        }
    }

    public static IReadOnlyList<DesignViolation> ScanFile(string relativePath, string content, double[]? typeScale = null)
    {
        if (relativePath == StylesFile) return Array.Empty<DesignViolation>();
        typeScale ??= FallbackTypeScale;
        bool xaml = relativePath.EndsWith(".axaml", StringComparison.Ordinal);
        string text = xaml ? StripXamlComments(content) : StripCodeComments(content);
        LineIndex lines = new(text);
        List<DesignViolation> found = new();

        ScanFontSize(relativePath, text, xaml, lines, typeScale, found);
        ScanColors(relativePath, text, xaml, lines, found);
        ScanSpacing(relativePath, text, xaml, lines, found);
        ScanGlyphs(relativePath, text, xaml, lines, found);
        ScanHeights(relativePath, text, xaml, lines, found);
        if (xaml) ScanPageInset(relativePath, text, found);
        else if (CodePageFiles.Contains(relativePath, StringComparer.Ordinal)) ScanCodePageInset(relativePath, text, found);

        return found.Where(v => !IsException(v)).OrderBy(v => v.Line).ToList();
    }

    private static bool IsException(DesignViolation v) =>
        Exceptions.Any(e => e.Rule == v.Rule && e.File == v.File && v.Snippet.Contains(e.SnippetContains, StringComparison.Ordinal));

    // ---- font size --------------------------------------------------------------------------------------------------

    private static void ScanFontSize(string file, string text, bool xaml, LineIndex lines, double[] scale, List<DesignViolation> found)
    {
        foreach (Match m in (xaml ? FontSizeXaml : FontSizeCode).Matches(text))
        {
            string raw = m.Groups["v"].Value;
            double value = double.Parse(raw, CultureInfo.InvariantCulture);
            string rule = Array.IndexOf(scale, value) >= 0 ? DesignRules.FontSizeLiteral : DesignRules.FontSizeOffScale;
            found.Add(new DesignViolation(rule, file, lines.LineOf(m.Index), "FontSize=" + (xaml ? "\"" + raw + "\"" : raw)));
        }
    }

    // ---- colour -----------------------------------------------------------------------------------------------------

    private static void ScanColors(string file, string text, bool xaml, LineIndex lines, List<DesignViolation> found)
    {
        foreach (Match m in HexColor.Matches(text))
        {
            // XAML: a hex value is only a colour inside an attribute value (not in a numeric entity such as &#x2715;).
            if (xaml && !InsideAttributeValue(text, m.Index)) continue;
            if (xaml && m.Index > 0 && text[m.Index - 1] == '&') continue;
            if (!xaml && !InsideCodeString(text, m.Index)) continue;
            found.Add(new DesignViolation(DesignRules.ColorHex, file, lines.LineOf(m.Index), m.Value));
        }
        if (xaml) return;
        foreach (Match m in ColorCodeApi.Matches(text))
            found.Add(new DesignViolation(DesignRules.ColorCode, file, lines.LineOf(m.Index), Regex.Replace(m.Value, @"\s+", " ")));
    }

    // ---- spacing ----------------------------------------------------------------------------------------------------

    private static void ScanSpacing(string file, string text, bool xaml, LineIndex lines, List<DesignViolation> found)
    {
        if (xaml)
        {
            foreach (Match m in SpacingXaml.Matches(text)) CheckSpacing(file, lines, m, m.Groups["n"].Value, m.Groups["v"].Value, found);
            foreach (Match m in ThicknessXaml.Matches(text)) CheckSpacing(file, lines, m, m.Groups["n"].Value, m.Groups["v"].Value, found);
            return;
        }
        foreach (Match m in SpacingCode.Matches(text)) CheckSpacing(file, lines, m, m.Groups["n"].Value, m.Groups["v"].Value, found, SpacingForm.CodeNumber);
        foreach (Match m in ThicknessCode.Matches(text))
        {
            string assigned = m.Groups["n"].Success ? m.Groups["n"].Value : "";
            // BorderThickness and friends are line widths, not spacing.
            if (assigned.EndsWith("Thickness", StringComparison.Ordinal)) continue;
            string target = assigned is "Margin" or "Padding" ? assigned : "Thickness";
            CheckSpacing(file, lines, m, target, m.Groups["v"].Value, found, SpacingForm.CodeThickness);
        }
    }

    private enum SpacingForm { Xaml, CodeNumber, CodeThickness }

    private static readonly Regex NumberInCode = new(@"(?<![\w.])-?[0-9]+(?:\.[0-9]+)?(?=[dDfF]?(?![\w.]))", RegexOptions.Compiled);

    private static void CheckSpacing(string file, LineIndex lines, Match m, string name, string value, List<DesignViolation> found, SpacingForm form = SpacingForm.Xaml)
    {
        bool off;
        string display;
        if (form == SpacingForm.Xaml)
        {
            string[] parts = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            off = parts.Any(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && Array.IndexOf(SpaceGrid, d) < 0);
            display = "\"" + string.Join(",", parts) + "\"";
        }
        else
        {
            // C#: arguments may be expressions (ternaries); every numeric literal in them must be on the grid.
            off = NumberInCode.Matches(value).Any(n => Array.IndexOf(SpaceGrid, double.Parse(n.Value, CultureInfo.InvariantCulture)) < 0);
            string collapsed = Regex.Replace(value.Trim(), @"\s+", " ");
            display = form == SpacingForm.CodeThickness ? "new Thickness(" + collapsed + ")" : collapsed;
        }
        if (!off) return;
        string snippet = name == "Thickness" ? display : name + "=" + display;
        found.Add(new DesignViolation(DesignRules.SpacingOffGrid, file, lines.LineOf(m.Index), snippet));
    }

    // ---- glyph icons ------------------------------------------------------------------------------------------------

    private static void ScanGlyphs(string file, string text, bool xaml, LineIndex lines, List<DesignViolation> found)
    {
        if (xaml)
        {
            foreach (Match m in XamlAttr.Matches(text))
            {
                string value = WebUtility.HtmlDecode(m.Groups["s"].Value);
                if (IsGlyphLiteral(value)) found.Add(new DesignViolation(DesignRules.GlyphIcon, file, lines.LineOf(m.Index), m.Groups["n"].Value + "=" + Visible(value)));
            }
            foreach (Match m in XamlText.Matches(text))
            {
                string value = WebUtility.HtmlDecode(m.Groups["s"].Value);
                if (IsGlyphLiteral(value)) found.Add(new DesignViolation(DesignRules.GlyphIcon, file, lines.LineOf(m.Index), "text=" + Visible(value)));
            }
            return;
        }
        foreach (Match m in CodeString.Matches(text))
        {
            string value = UnescapeCode(m.Groups["s"].Value);
            if (IsGlyphLiteral(value)) found.Add(new DesignViolation(DesignRules.GlyphIcon, file, lines.LineOf(m.Index), Visible(value)));
        }
    }

    /// <summary>True for a literal that is a symbol/arrow glyph used as an icon: 1-3 characters without letters or digits,
    /// at least one of them a non-ASCII symbol (Sm, So, Sk), no leading or trailing blank (that is a text separator);
    /// or a leading symbol followed by a blank and a word ("▶ Play").</summary>
    public static bool IsGlyphLiteral(string value)
    {
        if (value.Length == 0 || value.Length > 40) return false;
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])) return false;
        if (value.Length <= 3)
        {
            bool symbol = false;
            foreach (Rune r in value.EnumerateRunes())
            {
                if (Rune.IsLetterOrDigit(r)) return false;
                if (r.Value > 127 && IsSymbol(r)) symbol = true;
            }
            return symbol;
        }
        Rune first = value.EnumerateRunes().First();
        if (first.Value <= 127 || !IsSymbol(first)) return false;
        int len = first.Utf16SequenceLength;
        return value.Length > len + 1 && char.IsWhiteSpace(value[len]) && char.IsLetter(value[len + 1]);
    }

    private static bool IsSymbol(Rune r)
    {
        UnicodeCategory c = Rune.GetUnicodeCategory(r);
        return c is UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol;
    }

    /// <summary>Escapes non-printable characters so a snippet stays readable and stable in the baseline file.</summary>
    private static string Visible(string value)
    {
        StringBuilder sb = new("\"");
        foreach (Rune r in value.EnumerateRunes())
            sb.Append(r.Value <= 127 ? r.ToString() : r.ToString() + "(U+" + r.Value.ToString("X4", CultureInfo.InvariantCulture) + ")");
        return sb.Append('"').ToString();
    }

    private static string UnescapeCode(string s)
    {
        if (!s.Contains('\\')) return s;
        s = Regex.Replace(s, @"\\u(?<h>[0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups["h"].Value, 16)).ToString());
        s = Regex.Replace(s, @"\\U(?<h>[0-9A-Fa-f]{8})", m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups["h"].Value, 16)));
        return s;
    }

    // ---- heights ----------------------------------------------------------------------------------------------------

    private static void ScanHeights(string file, string text, bool xaml, LineIndex lines, List<DesignViolation> found)
    {
        MatchCollection rows = (xaml ? RowHeightXaml : RowHeightCode).Matches(text);
        foreach (Match m in rows)
            found.Add(new DesignViolation(DesignRules.RowHeightLiteral, file, lines.LineOf(m.Index), "RowHeight=" + (xaml ? "\"" + m.Groups["v"].Value + "\"" : m.Groups["v"].Value)));

        if (xaml) ScanXamlControlHeights(file, text, found);
        else ScanCodeControlHeights(file, text, lines, found);
    }

    private static void ScanXamlControlHeights(string file, string text, List<DesignViolation> found)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("Design lint cannot parse " + file + ": " + ex.Message, ex);
        }
        foreach (XElement element in doc.Descendants())
        {
            string name = element.Name.LocalName;
            if (name == "Setter")
            {
                string property = (string?)element.Attribute("Property") ?? "";
                if (property is not ("Height" or "MinHeight")) continue;
                string selector = (string?)element.Parent?.Attribute("Selector") ?? "";
                Match type = Regex.Match(selector, @"^\s*(?<t>[A-Za-z][\w:|]*)");
                string typeName = type.Success ? type.Groups["t"].Value.Split('|').Last() : "";
                if (!HeightControls.Contains(typeName)) continue;
                AddHeight(element, (string?)element.Attribute("Value"), property, typeName + " style");
                continue;
            }
            if (!HeightControls.Contains(name)) continue;
            foreach (string property in new[] { "Height", "MinHeight" })
                AddHeight(element, (string?)element.Attribute(property), property, name);
        }

        void AddHeight(XElement element, string? value, string property, string owner)
        {
            if (value is null || !IsControlHeight(value)) return;
            int line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            found.Add(new DesignViolation(DesignRules.ControlHeightLiteral, file, line, owner + " " + property + "=\"" + value + "\""));
        }
    }

    /// <summary>Only literals in the 20-48 dip band count as control heights (buttons 28-38, fields 32-44, pager 24);
    /// 0 resets a style minimum and large values size multi-line text areas, which are not row/control heights.</summary>
    private static bool IsControlHeight(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 20 && d <= 48;

    private static void ScanCodeControlHeights(string file, string text, LineIndex lines, List<DesignViolation> found)
    {
        foreach (Match m in NewControl.Matches(text))
        {
            int open = m.Index + m.Length - 1;
            string body = TopLevelInitializer(text, open, out int bodyStart);
            foreach (Match h in HeightAssign.Matches(body).Where(h => IsControlHeight(h.Groups["v"].Value)))
                found.Add(new DesignViolation(DesignRules.ControlHeightLiteral, file, lines.LineOf(bodyStart + h.Index),
                    m.Groups["t"].Value + " " + h.Groups["n"].Value + "=" + h.Groups["v"].Value));
        }
    }

    /// <summary>The text between the braces at <paramref name="open"/> with nested brace and paren content blanked out, so
    /// only assignments directly on the initialized control match.</summary>
    private static string TopLevelInitializer(string text, int open, out int start)
    {
        start = open + 1;
        StringBuilder sb = new();
        int braces = 1, parens = 0;
        for (int i = open + 1; i < text.Length && braces > 0; i++)
        {
            char c = text[i];
            if (c == '{') braces++;
            else if (c == '}') braces--;
            else if (c == '(') parens++;
            else if (c == ')') parens--;
            if (braces == 0) break;
            sb.Append(braces == 1 && parens == 0 ? c : (c == '\n' ? '\n' : ' '));
        }
        return sb.ToString();
    }

    // ---- page inset -------------------------------------------------------------------------------------------------

    private static void ScanPageInset(string file, string text, List<DesignViolation> found)
    {
        PageRoot[] roots = PageRoots.Where(r => r.File == file).ToArray();
        if (roots.Length == 0) return;
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("Design lint cannot parse " + file + ": " + ex.Message, ex);
        }
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (PageRoot root in roots)
        {
            XElement? element = root.NamedElement is not null
                ? doc.Descendants().FirstOrDefault(e => (string?)e.Attribute(x + "Name") == root.NamedElement)
                : doc.Root;
            foreach (string step in root.FirstChildPath)
                element = element?.Elements().FirstOrDefault(e => e.Name.LocalName == step);
            if (element is null)
            {
                found.Add(new DesignViolation(DesignRules.PageInset, file, 1, "page root not found (locator out of date): " + (root.NamedElement ?? "root") + "/" + string.Join("/", root.FirstChildPath)));
                continue;
            }
            string margin = (string?)element.Attribute("Margin") ?? "";
            string padding = (string?)element.Attribute("Padding") ?? "";
            if (margin.Contains("Workspace.Inset.Page", StringComparison.Ordinal) || padding.Contains("Workspace.Inset.Page", StringComparison.Ordinal)) continue;
            string current = margin.Length > 0 ? "Margin=\"" + margin + "\"" : padding.Length > 0 ? "Padding=\"" + padding + "\"" : "no inset";
            found.Add(new DesignViolation(DesignRules.PageInset, file, ((System.Xml.IXmlLineInfo)element).LineNumber, element.Name.LocalName + " " + current));
        }
    }

    private static void ScanCodePageInset(string file, string text, List<DesignViolation> found)
    {
        if (!text.Contains("Workspace.Inset.Page", StringComparison.Ordinal))
            found.Add(new DesignViolation(DesignRules.PageInset, file, 1, "no reference to Workspace.Inset.Page"));
    }

    // ---- text helpers -----------------------------------------------------------------------------------------------

    /// <summary>Replaces comment bodies with blanks of the same length, keeping line breaks so line numbers stay valid.</summary>
    public static string StripXamlComments(string text) =>
        Regex.Replace(text, @"<!--.*?-->", m => Blank(m.Value), RegexOptions.Singleline);

    /// <summary>Blanks // and /* */ comments of C# while leaving string and char literals untouched.</summary>
    public static string StripCodeComments(string text)
    {
        StringBuilder sb = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') { sb.Append(text[i] == '\r' ? '\r' : ' '); i++; }
            }
            else if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                sb.Append(Blank(text.Substring(i, end - i)));
                i = end;
            }
            else if (c == '"' && i + 2 < text.Length && next == '"' && text[i + 2] == '"')
            {
                int quotes = 3;
                while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
                string fence = new('"', quotes);
                int end = text.IndexOf(fence, i + quotes, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + quotes;
                sb.Append(text, i, end - i);
                i = end;
            }
            else if (c == '"')
            {
                bool verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 1] == '$' && text[i - 2] == '@') || (i > 1 && text[i - 1] == '@' && text[i - 2] == '$'));
                int j = i + 1;
                while (j < text.Length)
                {
                    if (verbatim) { if (text[j] == '"') { if (j + 1 < text.Length && text[j + 1] == '"') { j += 2; continue; } break; } }
                    else if (text[j] == '\\') { j += 2; continue; }
                    else if (text[j] == '"' || text[j] == '\n') break;
                    j++;
                }
                j = Math.Min(j + 1, text.Length);
                sb.Append(text, i, j - i);
                i = j;
            }
            else if (c == '\'')
            {
                int j = i + 1;
                while (j < text.Length && text[j] != '\'' && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                j = Math.Min(j + 1, text.Length);
                sb.Append(text, i, j - i);
                i = j;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string Blank(string s) => Regex.Replace(s, @"[^\r\n]", " ");

    private static bool InsideAttributeValue(string text, int index)
    {
        int quotes = 0;
        for (int i = index - 1; i >= 0 && text[i] != '<' && text[i] != '>'; i--)
            if (text[i] == '"') quotes++;
        return quotes % 2 == 1;
    }

    private static bool InsideCodeString(string text, int index)
    {
        int quotes = 0;
        for (int i = index - 1; i >= 0 && text[i] != '\n'; i--)
            if (text[i] == '"' && (i == 0 || text[i - 1] != '\\')) quotes++;
        return quotes % 2 == 1;
    }

    private sealed class LineIndex
    {
        private readonly List<int> _starts = new() { 0 };

        public LineIndex(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') _starts.Add(i + 1);
        }

        public int LineOf(int offset)
        {
            int index = _starts.BinarySearch(offset);
            return (index >= 0 ? index : ~index - 1) + 1;
        }
    }
}
