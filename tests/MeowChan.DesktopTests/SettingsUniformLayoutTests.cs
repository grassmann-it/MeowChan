using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

/// <summary>
/// SP1 (settings polish): the structure of the settings page as a rule over the source. The search sits in the page header; every row of every page is
/// the one uniform row (label and description at the start, the control in a fixed column at the end); yes/no options are switches; a section header is
/// one row with its "Reset"; a conditional row is marked and indented. Source contract tests, no window; the runtime parts are in
/// <see cref="SettingsConditionalRowTests"/> and <see cref="SettingsSearchPresentationTests"/>.
/// </summary>
public sealed class SettingsUniformLayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Desktop = "clr-namespace:MeowChan.Desktop";

    // The inputs that are NOT in a uniform row, each for a documented reason (the page header, the section headers, the wide path fields that need the
    // whole width, the hidden compatibility binding, the footer of the page). A new input outside a row must be a deliberate addition here.
    private static readonly string[] OutsideARow =
    [
        "SettingsMoreButton", "ResetInterfaceButton", "ResetReadingMediaButton", "ResetDownloadsButton", "ResetWorkerBudgetsButton", "ResetUpdatesButton",
        "ResetNotificationsButton", "ResetBehaviorButton", "GalleryAutoHideControlsInput", "DownloadPathInput", "BrowseButton", "CheckDestinationButton",
        "ProfileFolderOpenButton", "CancelDestinationCheckButton", "CancelSettingsButton", "SaveSettingsButton",
        // P9: the export filename template is a wide field (label, hint, input, live preview, inline error).
        "ExportFilenameTemplateInput",
    ];

    private static readonly string[] InputKinds =
        ["NumericUpDown", "ComboBox", "TextBox", "ToggleSwitch", "CheckBox", "Slider", "AccessibleNumberInput", "SettingsNumberInput", "RadioButton", "Button"];

    [Fact]
    public void TheSearchAndTheMoreMenuSitOverTheContentColumnOfTheForm()
    {
        XElement title = Named("SettingsTitleBlock");
        XElement actions = Named("SettingsSearchActions");
        XElement search = Named("SettingsSearchHost");
        XElement more = Named("SettingsMoreButton");
        XElement form = Named("SettingsFormLayout");
        XElement column = Named("SettingsContentColumn");
        XElement scroll = Named("SettingsScrollViewer");
        // One real column owns both rows: no independently measured header grid can drift over the sidebar.
        Assert.Same(form, column.Parent);
        Assert.Same(column, actions.Parent);
        Assert.Same(column, scroll.Parent);
        Assert.Same(actions, search.Parent);
        Assert.Same(actions, more.Parent);
        Assert.Same(Named("SettingsHeader"), title.Parent);
        Assert.Equal("176,*", (string?)form.Attribute("ColumnDefinitions"));
        Assert.Equal("1", (string?)column.Attribute("Grid.Column"));
        Assert.Equal("Auto,*", (string?)column.Attribute("RowDefinitions"));
        Assert.Null(actions.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)scroll.Attribute("Grid.Row"));
        Assert.Equal("{x:Static local:SettingsHeaderLayout.SearchActionsMargin}", (string?)actions.Attribute("Margin"));
        Assert.Equal(10d, SettingsHeaderLayout.ContentInset);
        Assert.Equal(new Avalonia.Thickness(SettingsHeaderLayout.ContentInset, 0, SettingsHeaderLayout.ContentInset, 12), SettingsHeaderLayout.SearchActionsMargin);
        Assert.Equal("{x:Static local:SettingsHeaderLayout.ResetStatusMargin}", (string?)Named("SettingsResetStatus").Attribute("Margin"));
        Assert.Equal(new Avalonia.Thickness(0, 0, SettingsHeaderLayout.ContentInset, 0), SettingsHeaderLayout.ResetStatusMargin);
        // The content's own inset (its width is the column less both insets) is the same constant as the header's margin.
        Assert.Contains("double width = Math.Max(0, sharedWidth - 20 - (categories ? 192 : 0));", Source("SettingsEditor.axaml.cs"));
        Assert.Equal(20d, 2 * SettingsHeaderLayout.ContentInset);
        Assert.Equal("*,Auto", (string?)actions.Attribute("ColumnDefinitions"));
        Assert.Null(search.Attribute("MaxWidth"));
        Assert.Null(search.Attribute("Width"));
        Assert.Null(search.Attribute("Margin"));
        Assert.Equal("Stretch", (string?)search.Attribute("HorizontalContentAlignment"));
        Assert.Null(more.Attribute("Margin"));
        Assert.Equal("1", (string?)more.Attribute("Grid.Column"));
        Assert.Null(search.Attribute("Grid.Column"));
        string code = Source("SettingsEditor.axaml.cs");
        Assert.Contains("Grid.SetColumn(SettingsContentColumn, categories ? 1 : 0);", code);
        Assert.Contains("Grid.SetColumnSpan(SettingsContentColumn, categories ? 1 : 2);", code);
        Assert.DoesNotContain("Grid.SetColumn(SettingsScrollViewer", code);
        Assert.Contains("PositionSearchPanelLater();", code);
        Assert.DoesNotContain(Markup().Descendants(), e => Name(e) == "SettingsHeaderRow");
    }

    [Theory]
    [InlineData(1100, true)]
    [InlineData(820, true)]
    [InlineData(819.5, false)]
    [InlineData(500, false)]
    public void TheSharedContentColumnFollowsTheFormsCategoryBreakpoint(double width, bool categories) =>
        Assert.Equal(categories, SettingsHeaderLayout.ShowsCategories(width));

    [Fact]
    public void TheSearchFieldIsHostedInTheContentHeaderAndTheHitsAreAnOverlayOfThePage()
    {
        string search = Source("SettingsEditor.Search.cs");
        Assert.Contains("SettingsSearchHost.Content = _searchBox", search);
        Assert.Contains("SettingsRoot", search);
        Assert.Contains("\"SettingsSearchPanel\"", search);
        Assert.Contains("SettingsContentColumn.LayoutUpdated +=", search);
        Assert.Contains("_searchBox.TranslatePoint", search);
        Assert.Contains("_searchPanel.Width = _searchBox.Bounds.Width;", search);
        // Ctrl+F is the command, "/" the local key.
        Assert.Contains("OnSlashFocusesSearch", search);
        Assert.Contains("FocusSearch", search);
    }

    [Fact]
    public void EveryRowIsTheUniformRowWithLabelCellAndAFixedControlColumn()
    {
        XElement[] rows = Markup().Descendants().Where(e => e.Name.LocalName == "Grid" && HasClass(e, "settings-row")).ToArray();
        Assert.True(rows.Length > 100, "The page lost its rows: " + rows.Length);
        foreach (XElement row in rows)
        {
            string where = " (line " + ((System.Xml.IXmlLineInfo)row).LineNumber + ")";
            Assert.True(row.Parent is { } parent && parent.Name.LocalName == "Border" && HasClass(parent, "settings-divider"), "A row needs its divider border" + where);
            Assert.Equal("*,280", (string?)row.Attribute("ColumnDefinitions"));
            XElement[] cells = row.Elements().ToArray();
            Assert.True(cells.Length == 2, "A row is a label cell and one control" + where);
            Assert.True(cells[0].Name.LocalName == "StackPanel" && HasClass(cells[0], "settings-label"), "The first cell is the label cell" + where);
            Assert.Equal("1", (string?)cells[1].Attribute("Grid.Column"));
            Assert.DoesNotContain(cells[0].Descendants(), e => InputKinds.Contains(e.Name.LocalName) && e.Name.LocalName != "Button" && !IsHelp(e));
        }
    }

    [Fact]
    public void EveryInputOfThePageIsInAUniformRowExceptTheDocumentedOnes()
    {
        List<string> outside = [];
        foreach (XElement e in Markup().Descendants().Where(e => InputKinds.Contains(e.Name.LocalName)))
        {
            if (e.Ancestors().Any(a => HasClass(a, "settings-row"))) continue;
            // The read-only error output is shown, not set.
            if (e.Name.LocalName == "TextBox" && Name(e) == null && (string?)e.Attribute("IsReadOnly") == "True") continue;
            outside.Add(Name(e) ?? e.Name.LocalName);
        }
        Assert.Equal(OutsideARow.Order(StringComparer.Ordinal), outside.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheWideFieldsAreTheDocumentedExceptionOfTheRowColumn()
    {
        XElement[] wide = Markup().Descendants().Where(e => HasClass(e, "settings-wide")).ToArray();
        Assert.Equal(3, wide.Length);
        Assert.Contains(wide, e => e.Descendants().Any(d => Name(d) == "DownloadPathInput"));
        Assert.Contains(wide, e => e.Descendants().Any(d => Name(d) == "ExportFilenameTemplateInput"));
        Assert.Contains(wide, e => e.Descendants().Any(d => Name(d) == "ProfileFolderOpenButton"));
    }

    [Fact]
    public void YesNoOptionsAreSwitchesAndNoCheckBoxIsLeftOnThePage()
    {
        Assert.DoesNotContain(Markup().Descendants(), e => e.Name.LocalName == "CheckBox");
        XElement[] switches = Markup().Descendants().Where(e => e.Name.LocalName == "ToggleSwitch").ToArray();
        Assert.True(switches.Length >= 30, "The yes/no options are switches: " + switches.Length);
        foreach (XElement toggle in switches)
        {
            string name = Name(toggle) ?? "?";
            if (name == "GalleryAutoHideControlsInput") continue; // hidden compatibility binding
            Assert.True(toggle.Ancestors().Any(a => HasClass(a, "settings-row")), name + " is not in a row.");
            Assert.Equal("1", (string?)toggle.Attribute("Grid.Column"));
            Assert.Equal("Right", (string?)toggle.Attribute("HorizontalAlignment"));
            // The row's label is the text: the switch has no On/Off words of its own (density keeps "Compact" at its side, no new text).
            if (name != "CompactDensityInput")
            {
                Assert.Equal("{x:Null}", (string?)toggle.Attribute("OnContent"));
                Assert.Equal("{x:Null}", (string?)toggle.Attribute("OffContent"));
            }
            // A screen reader gets the name and the label of the row.
            Assert.NotNull(toggle.Attribute("AutomationProperties.Name"));
            Assert.NotNull(toggle.Attribute("AutomationProperties.LabeledBy"));
        }
    }

    [Fact]
    public void EverySectionHeaderIsOneRowWithTheHeadingAndItsResetButton()
    {
        XElement[] headers = Markup().Descendants().Where(e => HasClass(e, "settings-section-header")).ToArray();
        Assert.Equal(7, headers.Length);
        foreach (XElement header in headers)
        {
            Assert.Equal("Grid", header.Name.LocalName);
            Assert.Equal("*,Auto", (string?)header.Attribute("ColumnDefinitions"));
            XElement[] parts = header.Elements().ToArray();
            Assert.Equal(2, parts.Length);
            XElement reset = parts[1];
            Assert.Equal("Button", reset.Name.LocalName);
            Assert.StartsWith("Reset", Name(reset), StringComparison.Ordinal);
            Assert.Equal("1", (string?)reset.Attribute("Grid.Column"));
            Assert.Contains("subtle", ((string?)reset.Attribute("Classes") ?? "").Split(' '));
            // The heading (or its label panel with the help button) sits in the first column.
            Assert.Contains(parts[0].DescendantsAndSelf(), e => e.Name.LocalName == "TextBlock" && HasClass(e, "section-title"));
        }
        // The "Reset" buttons are still the ones the reset code and the search knows by name, and still get their icon in code.
        string code = Source("SettingsEditor.axaml.cs");
        Assert.Contains("InitializeResetIcons", code);
    }

    [Fact]
    public void ConditionalRowsAreMarkedAndIndentedUnderTheirParent()
    {
        XElement[] rows = Markup().Descendants().Where(e => e.Attribute(Desktop + "SettingsConditional.When") != null).ToArray();
        Assert.Equal(10, rows.Length);
        foreach (XElement row in rows)
        {
            Assert.Equal("Border", row.Name.LocalName);
            Assert.True(HasClass(row, "settings-conditional") && HasClass(row, "settings-divider"));
            Assert.Matches(@"^\{Binding \w+\}$", (string?)row.Attribute(Desktop + "SettingsConditional.When"));
            Assert.True(HasClass(Assert.Single(row.Elements()), "settings-row"));
        }
        // The tagline for "Always the same" is one of them.
        Assert.Contains(rows, r => r.Descendants().Any(d => Name(d) == "HeaderTaglineFixedIdInput"));
        // So are the minutes between two changes of "New every N minutes".
        Assert.Contains(rows, r => r.Descendants().Any(d => Name(d) == "HeaderTaglineIntervalMinutesInput"));
        // The two slideshow sub-rows are one level deeper.
        Assert.Equal(2, rows.Count(r => HasClass(r, "settings-conditional-nested")));
        XElement style = Markup().Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("Selector") == "Border.settings-conditional");
        Assert.Equal("24,0,0,0", (string?)style.Elements().Single().Attribute("Value"));
    }

    [Fact]
    public void ConditionalRowsStayInTheTreeSoTheSearchFindsThemAndTheJumpShowsThemWithTheirParent()
    {
        // The index is built from the logical tree and never asks whether a row is shown...
        string search = Source("SettingsEditor.Search.cs");
        int from = search.IndexOf("private List<SearchEntryBuilder> ScanSearchEntries()", StringComparison.Ordinal);
        int to = search.IndexOf("private static bool IsSettingInput", from, StringComparison.Ordinal);
        Assert.True(from > 0 && to > from);
        string scan = search[from..to];
        Assert.DoesNotContain("IsVisible", scan);
        Assert.DoesNotContain("IsEffectivelyVisible", scan);
        // ...and the jump reveals the hidden conditional rows around its target before it focuses the input, and gives them back when the visit ends.
        Assert.Contains("_conditionalReveal.Reveal(", search);
        Assert.Contains("_conditionalReveal.Release()", search);
        Assert.Contains("_conditionalReveal.NoteEdited(", search);
        // Every conditional row has an input with a name, so there is something to find and to jump to.
        foreach (XElement row in Markup().Descendants().Where(e => e.Attribute(Desktop + "SettingsConditional.When") != null))
            Assert.Contains(row.Descendants(), e => (e.Name.LocalName is "NumericUpDown" or "ComboBox" or "TextBox" or "ToggleSwitch" or "AccessibleNumberInput" or "SettingsNumberInput")
                && Name(e) != null && e.Attribute("AutomationProperties.Name") != null);
    }

    [Fact]
    public void RowSpacingStaysOnTheEightDipGridAndUsesTheTokens()
    {
        string markup = File.ReadAllText(SourcePath("SettingsEditor.axaml"));
        foreach (Match m in Regex.Matches(markup, @"(?:Spacing|ColumnSpacing|RowSpacing)=""(?<v>[0-9.]+)"""))
            Assert.True(double.Parse(m.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture) % 4 == 0, "Off the 4/8 dip grid: " + m.Value);
        Assert.Matches(@"ColumnSpacing=""\{StaticResource Workspace\.Space\.24\}""", markup);
        Assert.Equal("280", MeowChan.Desktop.SettingsRowFactory.ControlColumnWidth);
    }

    private static bool IsHelp(XElement e) => e.Name.LocalName == "HelpButton";
    private static int Column(XElement e) => int.TryParse((string?)e.Attribute("Grid.Column"), out int c) ? c : 0;
    private static string? Name(XElement e) => (string?)e.Attribute(X + "Name");
    private static bool HasClass(XElement e, string name) => ((string?)e.Attribute("Classes") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal);
    private static XElement Named(string name) => Assert.Single(Markup().Descendants(), e => Name(e) == name);
    private static XDocument? _markup;
    private static XDocument Markup([CallerFilePath] string source = "") => _markup ??= XDocument.Load(SourcePath("SettingsEditor.axaml", source), LoadOptions.SetLineInfo);
    private static string Source(string file, [CallerFilePath] string source = "") => File.ReadAllText(SourcePath(file, source));
    private static string SourcePath(string file, [CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../Desktop/Views/Settings", file));
}
