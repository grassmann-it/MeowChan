using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

/// <summary>
/// SP6: every editable SettingsEditor input needs its own HelpText or an explicitly
/// attributable, following workspace-help line. This is a source lint, not a runtime
/// accessibility/localization audit. No category-wide or proximity-only help credit.
/// </summary>
public sealed class SettingsHintLintTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] InputKinds =
        ["SettingsNumberInput", "ComboBox", "CheckBox", "ToggleSwitch", "TextBox", "AccessibleNumberInput"];

    // Debt at 744d9217bc10b7448b1d4b20f94fde145b2e9b13, NOT permission for new debt.
    // Only remove entries when adding attributable help or removing an input. Never
    // increase/rename entries to absorb a regression. Exact equality catches stale
    // entries; identities include the edit target, so reusing x:Name cannot hide one.
    // EN/DE wording proposals: EINSTELLUNGEN-REFERENZ-1.1.0-2026-11-13, §§2,3,4,7,8;
    // the scoped SP6 REPORT lists each proposal. Shared NotificationsHint and
    // UpdatePrivacyHint do not explain each individual switch and are not exemptions.
    private static readonly string[] MissingHelpBaseline =
    [
        "ComboBox|name=LanguageInput|SelectedItem=SelectedLanguage",
        "ComboBox|name=ThemeInput|SelectedItem=SelectedTheme",
        "ToggleSwitch|name=CompactDensityInput|IsChecked=CompactDensity",
        "ComboBox|name=ReadingFontSizeInput|SelectedItem=SelectedReadingFontSize",
        "ToggleSwitch|name=GalleryMediaFocusInput|IsChecked=GalleryMediaFocus",
        "ToggleSwitch|binding=VideoAutoplay|IsChecked=VideoAutoplay",
        "ToggleSwitch|binding=VideoStartMuted|IsChecked=VideoStartMuted",
        "ToggleSwitch|name=ShowShortcutHelpInput|IsChecked=ShowShortcutHelp",
        "ToggleSwitch|name=PrereleaseUpdatesInput|IsChecked=IncludePrereleaseUpdates",
        "ToggleSwitch|name=InformationNotificationsInput|IsChecked=ShowInformationNotifications",
        "ToggleSwitch|name=WarningNotificationsInput|IsChecked=ShowWarningNotifications",
        "ToggleSwitch|name=ErrorNotificationsInput|IsChecked=ShowErrorNotifications",
        "ToggleSwitch|name=BoardAdmissionWizardInput|IsChecked=ShowBoardAdmissionWizard",
        "ToggleSwitch|name=RestoreSessionInput|IsChecked=SaveUrlsOnExit",
        "ToggleSwitch|name=CloseToTrayInput|IsChecked=CloseToTray",
        "ToggleSwitch|name=MinimizeToTrayInput|IsChecked=MinimizeToTray",
    ];

    private sealed record Exclusion(string Identity, string Reason, Func<XElement, bool> StillNonEditable);

    // No blanket IsVisible/IsEnabled/IsReadOnly filter: inactive categories, collapsed
    // groups and conditionally enabled settings still need help. These TWO exact UI
    // artifacts alone are excluded; changed shape or deletion requires review.
    private static readonly Exclusion[] NonEditableUi =
    [
        new("ToggleSwitch|name=GalleryAutoHideControlsInput|IsChecked=GalleryAutoHideControls",
            "Hidden compatibility binding: overlay and auto-hide are one visible choice, not two inputs.",
            e => IsFalse(e, "IsVisible") && IsFalse(e, "IsTabStop")),
        new("TextBox|binding=ErrorDetails|Text=ErrorDetails",
            "Read-only error output inside SettingsErrorDetails/SettingsErrorBanner, not a settings draft.",
            e => IsTrue(e, "IsReadOnly") && e.Ancestors().Any(a => Name(a) == "SettingsErrorDetails")
                && e.Ancestors().Any(a => Name(a) == "SettingsErrorBanner")),
    ];

    [Fact]
    public void EveryEditableInputHasAttributedHelpOrAnExactShrinkingBaselineEntry()
    {
        string[] problems = Problems(Markup(), MissingHelpBaseline, NonEditableUi);
        Assert.True(problems.Length == 0, "Settings hint lint (do not enlarge the baseline):\n" + string.Join("\n", problems));
    }

    [Theory]
    [InlineData("SettingsNumberInput", "Value")]
    [InlineData("ComboBox", "SelectedItem")]
    [InlineData("CheckBox", "IsChecked")]
    [InlineData("ToggleSwitch", "IsChecked")]
    [InlineData("TextBox", "Text")]
    [InlineData("AccessibleNumberInput", "Value")]
    public void AllKindsAreLintedEvenWhenHiddenOrDisabled(string kind, string property)
    {
        XDocument markup = Fixture($"<{kind} x:Name='FutureInput' {property}='{{Binding Future, Mode=TwoWay}}' IsVisible='False' IsEnabled='False' />");
        Assert.Contains(Problems(markup, [], []), p => p.StartsWith("Missing help: ", StringComparison.Ordinal));
        XElement field = Assert.Single(Inputs(markup));
        field.SetAttributeValue("AutomationProperties.HelpText", "{Binding FutureHint}");
        Assert.Empty(Problems(markup, [], []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{x:Null}")]
    [InlineData("{Binding}")]
    [InlineData("{Binding }")]
    [InlineData("{Binding Path=}")]
    [InlineData("{DynamicResource }")]
    public void EmptyOrNullHelpIsNotHelp(string text)
    {
        XDocument markup = Fixture("<TextBox x:Name='FutureInput' Text='{Binding Future}' />");
        Assert.Single(Inputs(markup)).SetAttributeValue("AutomationProperties.HelpText", text);
        Assert.Single(Problems(markup, [], []));
    }

    [Theory]
    [InlineData("<TextBox AutomationProperties.HelpText='An explanation.' />")]
    [InlineData("<TextBox AutomationProperties.HelpText='{Binding FutureHint}' />")]
    [InlineData("<TextBox AutomationProperties.HelpText='{DynamicResource FutureHint}' />")]
    [InlineData("<TextBox><AutomationProperties.HelpText>An explanation.</AutomationProperties.HelpText></TextBox>")]
    [InlineData("<TextBox><AutomationProperties.HelpText><Binding Path='FutureHint' /></AutomationProperties.HelpText></TextBox>")]
    public void DirectHelpCanBeAnAttributeOrAPropertyElement(string source)
    {
        XDocument markup = Fixture(source);
        Assert.Single(Inputs(markup)).SetAttributeValue(X + "Name", "FutureInput");
        Assert.Empty(Problems(markup, [], []));
    }

    public static TheoryData<string, bool> WorkspaceHelpCases => new()
    {
        // Semantic identity AND local ownership, never adjacency alone.
        { "<CheckBox IsChecked='{Binding Future}' /><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' />", true },
        { "<ComboBox SelectedItem='{Binding SelectedFuture}' /><TextBlock Classes='muted workspace-help' Text='{Binding FutureHelp}' />", true },
        { "<Grid Classes='settings-row'><TextBox Text='{Binding Future}' /><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /></Grid>", true },
        { "<Grid Classes='settings-row'><StackPanel><TextBox Text='{Binding Future}' /></StackPanel></Grid><TextBlock Classes='workspace-help' Text='{Binding FutureHelp}' />", true },
        { "<StackPanel Classes='workspace-field'><TextBox Text='{Binding Future}' /><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /></StackPanel>", true },
        { "<CheckBox IsChecked='{Binding Future}' /><TextBlock Classes='workspace-help' Text='{Binding CategoryHint}' />", false },
        { "<CheckBox IsChecked='{Binding Future}' /><TextBlock Classes='workspace-help' Text='Some nearby explanation.' />", false },
        { "<TextBox Text='{Binding Future}' /><TextBlock Classes='workspace-help' Text='{Binding FutureOtherHint}' />", false },
        { "<TextBox Text='{Binding Future}' /><TextBlock Classes='not-workspace-help' Text='{Binding FutureHint}' />", false },
        { "<TextBox Text='{Binding Future}' /><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' IsVisible='False' />", false },
        { "<TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /><TextBox Text='{Binding Future}' />", false },
        { "<TextBox Text='{Binding Future}' /><TextBlock Text='Intervening row' /><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' />", false },
        { "<Grid Classes='settings-row'><TextBox Text='{Binding Future}' /><CheckBox IsChecked='{Binding Other}' /><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /></Grid>", false },
        // SP1: the description of the uniform row sits in its label cell, for the one input of the row.
        { "<Grid Classes='settings-row'><StackPanel Classes='settings-label'><TextBlock Text='Label' /><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /></StackPanel><ToggleSwitch IsChecked='{Binding Future}' /></Grid>", true },
        { "<Grid Classes='settings-row'><StackPanel Classes='settings-label'><TextBlock Text='Label' /><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /></StackPanel><TextBox Text='{Binding Future}' /></Grid>", true },
        { "<Grid Classes='settings-row'><StackPanel Classes='settings-label'><TextBlock Classes='workspace-help' Text='{Binding OtherHint}' /></StackPanel><TextBox Text='{Binding Future}' /></Grid>", false },
        { "<Grid Classes='settings-row'><StackPanel Classes='settings-label'><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' IsVisible='False' /></StackPanel><TextBox Text='{Binding Future}' /></Grid>", false },
        { "<Grid Classes='settings-row'><StackPanel><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /></StackPanel><TextBox Text='{Binding Future}' /></Grid>", false },
        { "<Grid Classes='settings-row'><StackPanel Classes='settings-label'><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /></StackPanel><TextBox Text='{Binding Future}' /><ToggleSwitch IsChecked='{Binding Other}' /></Grid>", false },
        // Even a matching binding elsewhere in the category is not an owned line.
        { "<StackPanel><TextBox Text='{Binding Future}' /></StackPanel><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' />", false },
        { "<Grid Classes='settings-row'><TextBox Text='{Binding Future}' /><StackPanel IsVisible='False'><TextBlock Classes='workspace-help' Text='{Binding FutureHint}' /></StackPanel></Grid>", false },
    };

    [Theory]
    [MemberData(nameof(WorkspaceHelpCases))]
    public void WorkspaceHelpMustBeFollowingSemanticallyMatchedAndFieldOwned(string source, bool expected)
    {
        XElement field = Inputs(Fixture(source)).First();
        Assert.Equal(expected, HasAssignedHelp(field));
    }

    [Fact]
    public void ANewInputCannotBorrowAnExistingRowsHint()
    {
        XDocument markup = Fixture("<Grid Classes='settings-row'><ComboBox x:Name='KnownInput' SelectedItem='{Binding SelectedKnown}' AutomationProperties.HelpText='{Binding KnownHint}' /><TextBlock Classes='workspace-help' Text='{Binding KnownHint}' /></Grid>");
        XElement original = Named(markup, "KnownInput");
        original.AddAfterSelf(new XElement(original.Name.Namespace + "CheckBox",
            new XAttribute(X + "Name", "FutureInput"), new XAttribute("IsChecked", "{Binding Future, Mode=TwoWay}")));
        Assert.Equal(new[] { "Missing help: CheckBox|name=FutureInput|IsChecked=Future" }, Problems(markup, [], []));
    }

    [Fact]
    public void RemovingExistingHelpIsANewRegressionNotBaselineDebt()
    {
        // A preceding line alone does not meet the following-line contract.
        XDocument markup = Fixture("<Grid Classes='settings-row'><TextBlock Classes='workspace-help' Text='{Binding KnownHint}' /><ComboBox x:Name='KnownInput' SelectedItem='{Binding SelectedKnown}' AutomationProperties.HelpText='{Binding KnownHint}' /></Grid>");
        Assert.Empty(Problems(markup, [], []));
        Named(markup, "KnownInput").Attribute("AutomationProperties.HelpText")!.Remove();
        Assert.Equal(new[] { "Missing help: ComboBox|name=KnownInput|SelectedItem=SelectedKnown" }, Problems(markup, [], []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedOrRemovedDebtMakesItsBaselineEntryStale(bool remove)
    {
        XDocument markup = Fixture("<ComboBox x:Name='KnownInput' SelectedItem='{Binding SelectedKnown}' />");
        XElement field = Named(markup, "KnownInput");
        string[] baseline = [Identity(field)];
        Assert.Empty(Problems(markup, baseline, []));
        if (remove) field.Remove();
        else field.SetAttributeValue("AutomationProperties.HelpText", "An explanation of the choice.");
        Assert.Contains("Stale baseline (remove entry): " + baseline[0], Problems(markup, baseline, []));
    }

    [Fact]
    public void ReusingABaselinedNameForADifferentSettingDoesNotHideMissingHelp()
    {
        XDocument markup = Fixture("<ComboBox x:Name='KnownInput' SelectedItem='{Binding SelectedKnown}' />");
        XElement field = Named(markup, "KnownInput");
        string[] baseline = [Identity(field)];
        field.SetAttributeValue("SelectedItem", "{Binding SelectedFuture, Mode=TwoWay}");
        string[] problems = Problems(markup, baseline, []);
        Assert.Contains("Missing help: ComboBox|name=KnownInput|SelectedItem=SelectedFuture", problems);
        Assert.Contains("Stale baseline (remove entry): " + baseline[0], problems);
    }

    [Theory]
    [InlineData("GalleryAutoHideControlsInput", "IsVisible", "True")]
    [InlineData("GalleryAutoHideControlsInput", "IsTabStop", "True")]
    [InlineData(null, "IsReadOnly", "False")]
    public void AnExclusionCannotBecomeEditableSilently(string? name, string property, string value)
    {
        XDocument markup = Markup();
        XElement field = name == null ? Inputs(markup).Single(e => BindingPath((string?)e.Attribute("Text")) == "ErrorDetails") : Named(markup, name);
        field.SetAttributeValue(property, value);
        string[] problems = Problems(markup, MissingHelpBaseline, NonEditableUi);
        Assert.Contains(problems, p => p.StartsWith("Unsafe exclusion: ", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Missing help: ", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadOnlyIsNotAnUnreviewedBlanketExclusion()
    {
        XDocument markup = Fixture("<TextBox x:Name='FutureInput' Text='{Binding Future}' IsReadOnly='True' />");
        Assert.Equal(new[] { "Missing help: TextBox|name=FutureInput|Text=Future" }, Problems(markup, [], []));
    }

    [Fact]
    public void RemovingAnExcludedArtifactMakesTheExclusionStale()
    {
        XDocument markup = Markup();
        Named(markup, "GalleryAutoHideControlsInput").Remove();
        Assert.Contains(Problems(markup, MissingHelpBaseline, NonEditableUi),
            p => p.StartsWith("Stale/ambiguous exclusion: ToggleSwitch|name=GalleryAutoHideControlsInput|", StringComparison.Ordinal));
    }

    [Fact]
    public void IdentityDoesNotDependOnLineNumbersLayoutOrBindingOptionFormatting()
    {
        XElement before = Inputs(Fixture("<TextBox x:Name='FutureInput' Text='{Binding Future, Mode=TwoWay}' />")).Single();
        XElement after = Inputs(Fixture("\n<Grid Margin='8'>\n<!-- moved -->\n<TextBox x:Name='FutureInput' Text='{Binding Path=Future,Mode=TwoWay}' />\n</Grid>")).Single();
        Assert.Equal(Identity(before), Identity(after));
        before.Attribute(X + "Name")!.Remove();
        after.Attribute(X + "Name")!.Remove();
        Assert.Equal(Identity(before), Identity(after));
    }

    [Fact]
    public void DuplicateOrUnidentifiableInputsAndDuplicateBaselineEntriesFailClosed()
    {
        XDocument duplicate = Fixture("<CheckBox x:Name='Same' IsChecked='{Binding Future}' /><CheckBox x:Name='Same' IsChecked='{Binding Future}' />");
        string id = Identity(Inputs(duplicate).First());
        Assert.Contains(Problems(duplicate, [id], []), p => p.StartsWith("Duplicate input identity: ", StringComparison.Ordinal));
        Assert.Contains(Problems(duplicate, [id], []), p => p.StartsWith("Duplicate x:Name: ", StringComparison.Ordinal));
        Assert.Contains(Problems(duplicate, [id, id], []), p => p.StartsWith("Duplicate baseline: ", StringComparison.Ordinal));
        Assert.Contains(Problems(Fixture("<TextBox AutomationProperties.HelpText='Help.' />"), [], []),
            p => p.StartsWith("No stable input identity: ", StringComparison.Ordinal));
    }

    private static string[] Problems(XDocument markup, IReadOnlyList<string> baseline, IReadOnlyList<Exclusion> exclusions)
    {
        XElement[] fields = Inputs(markup).ToArray();
        List<string> problems = [];
        if (fields.Length == 0) problems.Add("No settings inputs found; check the source path/parser.");
        foreach (var group in fields.GroupBy(Identity).Where(g => g.Count() > 1))
            problems.Add("Duplicate input identity: " + group.Key);
        foreach (var group in fields.Where(e => Name(e) != null).GroupBy(Name).Where(g => g.Count() > 1))
            problems.Add("Duplicate x:Name: " + group.Key);
        foreach (var group in baseline.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add("Duplicate baseline: " + group.Key);
        foreach (XElement field in fields.Where(e => Name(e) == null && EditPath(e) == null))
            problems.Add("No stable input identity: " + field.Name.LocalName + " needs x:Name or a simple edit binding.");

        HashSet<string> excluded = new(StringComparer.Ordinal);
        foreach (Exclusion exclusion in exclusions)
        {
            XElement[] matches = fields.Where(e => Identity(e) == exclusion.Identity).ToArray();
            if (matches.Length != 1)
                problems.Add("Stale/ambiguous exclusion: " + exclusion.Identity + " — " + exclusion.Reason);
            else if (string.IsNullOrWhiteSpace(exclusion.Reason) || !exclusion.StillNonEditable(matches[0]))
                problems.Add("Unsafe exclusion: " + exclusion.Identity + " — " + exclusion.Reason);
            else excluded.Add(exclusion.Identity);
        }

        HashSet<string> missing = fields.Where(e => !excluded.Contains(Identity(e)) && !HasAssignedHelp(e))
            .Select(Identity).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(missing.Except(baseline, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => "Missing help: " + id));
        problems.AddRange(baseline.Except(missing, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => "Stale baseline (remove entry): " + id));
        return problems.ToArray();
    }

    private static bool HasAssignedHelp(XElement field)
    {
        if (MeaningfulHelp((string?)field.Attribute("AutomationProperties.HelpText"))) return true;
        XElement? property = field.Elements().SingleOrDefault(e => e.Name.LocalName == "AutomationProperties.HelpText");
        if (property != null && (property.HasElements
            ? property.Elements().Count() == 1 && property.Elements().Single().Name.LocalName == "Binding"
                && !string.IsNullOrWhiteSpace((string?)property.Elements().Single().Attribute("Path"))
            : MeaningfulHelp(property.Value))) return true;

        string? path = EditPath(field);
        if (path == null) return false;
        // SelectedFoo is the choice object for Foo. Do not fuzzy-match labels, name
        // substrings, arbitrary help text, tooltips or a generic category introduction.
        string stem = field.Name.LocalName == "ComboBox" && path.StartsWith("Selected", StringComparison.Ordinal)
            ? path["Selected".Length..] : path;
        bool AssignedLine(XElement line, XElement owner) => line.Name.LocalName == "TextBlock"
            && HasClass(line, "workspace-help")
            && !line.AncestorsAndSelf().TakeWhile(e => e != owner).Any(e => IsFalse(e, "IsVisible"))
            && BindingPath((string?)line.Attribute("Text")) is string helpPath
            && (helpPath == stem + "Hint" || helpPath == stem + "Help");

        // Checkbox/text field followed by its specifically bound line. An intervening
        // element ends the relationship; comments/formatting are irrelevant.
        XElement? next = field.ElementsAfterSelf().FirstOrDefault();
        if (next != null && AssignedLine(next, field.Parent!)) return true;

        // Named row class is a field boundary, NOT a page/category/expander. Require
        // exactly one input and a semantically matching line after it, inside the row
        // or immediately following the row. Never climb to an arbitrary ancestor.
        XElement? row = field.Ancestors().FirstOrDefault(e => HasClass(e, "settings-row") || HasClass(e, "workspace-field"));
        if (row == null || row.Descendants().Count(IsInput) != 1) return false;
        if (row.Descendants().SkipWhile(e => e != field).Skip(1).Any(e => AssignedLine(e, row))) return true;
        // SP1: in the uniform row the description is in the label cell (start of the row), the control in the column after it.
        // Still field ownership: the cell must be a direct child of the row, which holds exactly this one input.
        if (HasClass(row, "settings-row") && row.Elements().Where(cell => HasClass(cell, "settings-label"))
            .Any(cell => cell.Descendants().Any(e => AssignedLine(e, row)))) return true;
        XElement? followingRow = row.ElementsAfterSelf().FirstOrDefault();
        return followingRow != null && AssignedLine(followingRow, row.Parent!);
    }

    private static bool MeaningfulHelp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        if (!value.StartsWith("{", StringComparison.Ordinal)) return true;
        return BindingPath(value) != null
            || Regex.IsMatch(value, @"^\{(?:StaticResource|DynamicResource|x:Static)\s+\S[^{}]*\}$");
    }

    private static string EditProperty(XElement e) => e.Name.LocalName switch
    {
        "ComboBox" => "SelectedItem",
        "CheckBox" or "ToggleSwitch" => "IsChecked",
        "TextBox" => "Text",
        _ => "Value",
    };

    private static string? EditPath(XElement e) => BindingPath((string?)e.Attribute(EditProperty(e)));

    private static string? BindingPath(string? expression)
    {
        Match match = Regex.Match(expression ?? "", @"^\{Binding\s+(?:Path\s*=\s*)?(?<path>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*(?:,|\})");
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static string Identity(XElement e) => e.Name.LocalName + "|"
        + (Name(e) is string name ? "name=" + name : "binding=" + (EditPath(e) ?? "<unidentified>"))
        + "|" + EditProperty(e) + "=" + (EditPath(e) ?? ((string?)e.Attribute(EditProperty(e)))?.Trim() ?? "<unbound>");

    private static bool IsInput(XElement e) => InputKinds.Contains(e.Name.LocalName, StringComparer.Ordinal);
    private static IEnumerable<XElement> Inputs(XDocument markup) => markup.Descendants().Where(IsInput);
    private static string? Name(XElement e) => (string?)e.Attribute(X + "Name");
    private static bool HasClass(XElement e, string name) => ((string?)e.Attribute("Classes") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal);
    private static bool IsFalse(XElement e, string attribute) => string.Equals((string?)e.Attribute(attribute), "False", StringComparison.OrdinalIgnoreCase);
    private static bool IsTrue(XElement e, string attribute) => string.Equals((string?)e.Attribute(attribute), "True", StringComparison.OrdinalIgnoreCase);
    private static XElement Named(XDocument markup, string name) => Assert.Single(markup.Descendants(), e => Name(e) == name);
    private static XDocument Fixture(string body) => XDocument.Parse("<UserControl xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><StackPanel>" + body + "</StackPanel></UserControl>");
    private static XDocument Markup([CallerFilePath] string source = "") => XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../Desktop/Views/Settings/SettingsEditor.axaml")));
}
