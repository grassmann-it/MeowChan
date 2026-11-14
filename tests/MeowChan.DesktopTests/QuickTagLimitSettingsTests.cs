using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>
/// The eight quick tagging limits (the stamp size of PT5 joined in wave3) (PLAN-SCHNELL-TAGGEN §3.7) are validated settings: each has its default, minimum and maximum
/// (the constants of the quick tag types, the marking limit is the plan's own), survives Clone, both copies and save/reload,
/// is reduced on load instead of refusing the file, has a labelled row with a hint on the "Storage and limits" page, and reaches the
/// option objects of the quick tag types through <see cref="QuickTagSettings"/>.
/// </summary>
[Collection("Localization")]
public sealed class QuickTagLimitSettingsTests
{
    // Property, default, minimum, maximum (the plan's table) and text key stem.
    private static readonly (string Property, int Default, int Minimum, int Maximum, string Stem)[] Limits =
    {
        (nameof(Config.QuickTagSuggestionCount), 9, 3, 18, "quickTagSuggestions"),
        (nameof(Config.QuickTagRecentCount), 50, 0, 500, "quickTagRecent"),
        (nameof(Config.QuickTagUndoDepth), 20, 1, 100, "quickTagUndo"),
        (nameof(Config.QuickTagNeighbourScan), 2000, 100, 20_000, "quickTagNeighbours"),
        (nameof(Config.QuickTagFilenameTokens), 8, 0, 32, "quickTagFilename"),
        (nameof(Config.QuickTagSourceTagsPerMedium), 24, 0, 200, "quickTagSourceTags"),
        (nameof(Config.QuickTagMarkedMediaLimit), 500, 10, 5000, "quickTagMarked"),
        (nameof(Config.QuickTagStampSize), 9, 1, 18, "quickTagStamp"),
    };

    private static int Get(Config config, string property) => (int)typeof(Config).GetProperty(property)!.GetValue(config)!;

    private static void Set(Config config, string property, int value) => typeof(Config).GetProperty(property)!.SetValue(config, value);

    private static int Constant(string name) =>
        (int)typeof(Config).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private static Config Saved(TempDirectory directory) => new()
    {
        AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadPath = Path.Combine(directory.Path, "media")
    };

    [Fact]
    public void EveryLimitHasThePlanDefaultAndRangeAndTiesToTheQuickTagConstants()
    {
        Config defaults = new();
        foreach ((string property, int fallback, int minimum, int maximum, _) in Limits)
        {
            Assert.Equal(fallback, Get(defaults, property));
            Assert.Equal(fallback, Constant(property + "Default"));
            Assert.Equal(minimum, Constant(property + "Minimum"));
            Assert.Equal(maximum, Constant(property + "Maximum"));
        }
        // The quick tag types and the settings must not drift apart: the settings take their numbers from these constants.
        Assert.Equal((QuickTagOptions.DefaultSuggestionCount, QuickTagOptions.MinimumSuggestionCount, QuickTagOptions.MaximumSuggestionCount),
            (Config.QuickTagSuggestionCountDefault, Config.QuickTagSuggestionCountMinimum, Config.QuickTagSuggestionCountMaximum));
        Assert.Equal((QuickTagOptions.DefaultUndoDepth, QuickTagOptions.MinimumUndoDepth, QuickTagOptions.MaximumUndoDepth),
            (Config.QuickTagUndoDepthDefault, Config.QuickTagUndoDepthMinimum, Config.QuickTagUndoDepthMaximum));
        Assert.Equal((TagContextOptions.DefaultRecentCount, TagContextOptions.MinimumRecentCount, TagContextOptions.MaximumRecentCount),
            (Config.QuickTagRecentCountDefault, Config.QuickTagRecentCountMinimum, Config.QuickTagRecentCountMaximum));
        Assert.Equal((TagContextOptions.DefaultNeighbourScan, TagContextOptions.MinimumNeighbourScan, TagContextOptions.MaximumNeighbourScan),
            (Config.QuickTagNeighbourScanDefault, Config.QuickTagNeighbourScanMinimum, Config.QuickTagNeighbourScanMaximum));
        Assert.Equal((TagContextOptions.DefaultFilenameTokens, TagContextOptions.MinimumFilenameTokens, TagContextOptions.MaximumFilenameTokens),
            (Config.QuickTagFilenameTokensDefault, Config.QuickTagFilenameTokensMinimum, Config.QuickTagFilenameTokensMaximum));
        Assert.Equal((TagContextOptions.DefaultSourceTagsPerMedium, TagContextOptions.MinimumSourceTagsPerMedium, TagContextOptions.MaximumSourceTagsPerMedium),
            (Config.QuickTagSourceTagsPerMediumDefault, Config.QuickTagSourceTagsPerMediumMinimum, Config.QuickTagSourceTagsPerMediumMaximum));
        Assert.Equal((QuickTagOptions.DefaultStampSize, QuickTagOptions.MinimumStampSize, QuickTagOptions.MaximumStampSize),
            (Config.QuickTagStampSizeDefault, Config.QuickTagStampSizeMinimum, Config.QuickTagStampSizeMaximum));
        // Marking stays below what the tag store accepts as assignments, so a full marking can always be written.
        Assert.True(Config.QuickTagMarkedMediaLimitMaximum <= PrivateTagStore.MaximumMediaAssignments);
        defaults.Validate();
    }

    [Fact]
    public void TheEdgesAreValidAndEverythingOutsideIsRefusedByValidateAndByTheCopy()
    {
        Config live = new();
        foreach ((string property, _, int minimum, int maximum, _) in Limits)
        {
            foreach (int valid in new[] { minimum, maximum })
            {
                Config candidate = live.Clone();
                Set(candidate, property, valid);
                candidate.Validate();
                Config copy = new();
                copy.CopySettingsFieldsFrom(candidate);
                Assert.Equal(valid, Get(copy, property));
            }
            foreach (int invalid in new[] { minimum - 1, maximum + 1, int.MinValue, int.MaxValue }.Distinct())
            {
                Config candidate = live.Clone();
                Set(candidate, property, invalid);
                ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
                Assert.Equal(property, error.ParamName);
                Assert.Throws<ArgumentOutOfRangeException>(() => live.CopySettingsFieldsFrom(candidate));
            }
        }
        foreach ((string property, int fallback, _, _, _) in Limits) Assert.Equal(fallback, Get(live, property));
    }

    [Fact]
    public void EveryLimitSurvivesCloneBothCopiesAndSaveReload()
    {
        using TempDirectory directory = new();
        Config original = Saved(directory);
        foreach ((string property, _, int minimum, int maximum, _) in Limits) Set(original, property, maximum - (maximum - minimum) / 3);
        Config clone = original.Clone();
        Config live = new();
        live.CopySettingsFieldsFrom(clone);
        Config copy = Saved(directory);
        copy.CopyFrom(live);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach ((string property, _, int minimum, int maximum, _) in Limits)
        {
            int expected = maximum - (maximum - minimum) / 3;
            Assert.Equal(expected, Get(clone, property));
            Assert.Equal(expected, Get(live, property));
            Assert.Equal(expected, Get(copy, property));
            Assert.Equal(expected, Get(loaded, property));
        }
    }

    [Fact]
    public void AnOutOfRangeValueInTheFileIsReducedOnLoadAndAnOlderFileGetsTheDefaults()
    {
        using TempDirectory directory = new();
        Saved(directory).SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        foreach ((string property, _, int minimum, int maximum, _) in Limits)
            document[property] = property.Length % 2 == 0 ? maximum + 7 : minimum - 7;
        File.WriteAllText(file, document.ToJsonString());
        Config loaded = Config.LoadConfig(directory.Path);
        // The file stays usable: like the reader display limits, a value outside the range is brought into it.
        Assert.Null(loaded.LoadWarning);
        foreach ((string property, _, int minimum, int maximum, _) in Limits)
            Assert.Equal(property.Length % 2 == 0 ? maximum : minimum, Get(loaded, property));
        loaded.Validate();

        foreach ((string property, _, _, _, _) in Limits) document.Remove(property);
        File.WriteAllText(file, document.ToJsonString());
        Config older = Config.LoadConfig(directory.Path);
        Assert.Null(older.LoadWarning);
        foreach ((string property, int fallback, _, _, _) in Limits) Assert.Equal(fallback, Get(older, property));
    }

    [Fact]
    public void TheOptionFactoryHandsTheSettingsToTheQuickTagTypesAndClampsAHandBuiltConfig()
    {
        Config config = new()
        {
            QuickTagSuggestionCount = 12, QuickTagRecentCount = 7, QuickTagUndoDepth = 33, QuickTagNeighbourScan = 4500,
            QuickTagFilenameTokens = 3, QuickTagSourceTagsPerMedium = 0, QuickTagMarkedMediaLimit = 1234, QuickTagStampSize = 5
        };
        TagContextOptions context = QuickTagSettings.ContextOptions(config);
        Assert.Equal((12, 7, 4500, 3, 0), (context.SuggestionCount, context.RecentCount, context.NeighbourScan, context.FilenameTokens, context.SourceTagsPerMedium));
        QuickTagOptions surface = QuickTagSettings.SurfaceOptions(config);
        Assert.Equal((33, 12, 1234, 5), (surface.UndoDepth, surface.SuggestionCount, surface.MarkedMediaLimit, surface.StampSize));
        Assert.Equal(7, QuickTagSettings.NewRecentList(config).Capacity);
        Assert.Equal(1234, QuickTagSettings.MarkedMediaLimit(config));

        config.QuickTagRecentCount = 0;
        Assert.Equal(0, QuickTagSettings.NewRecentList(config).Capacity);

        // A Config nobody validated (a test double, a stale draft) cannot produce a value outside the range.
        Config wild = new()
        {
            QuickTagSuggestionCount = 1000, QuickTagRecentCount = -5, QuickTagUndoDepth = 0, QuickTagNeighbourScan = 5,
            QuickTagFilenameTokens = 99, QuickTagSourceTagsPerMedium = 1_000_000, QuickTagMarkedMediaLimit = int.MaxValue, QuickTagStampSize = 99
        };
        TagContextOptions clamped = QuickTagSettings.ContextOptions(wild);
        Assert.Equal((18, 0, 100, 32, 200), (clamped.SuggestionCount, clamped.RecentCount, clamped.NeighbourScan, clamped.FilenameTokens, clamped.SourceTagsPerMedium));
        QuickTagOptions surfaceClamped = QuickTagSettings.SurfaceOptions(wild);
        Assert.Equal((1, 18, 5000, 18), (surfaceClamped.UndoDepth, surfaceClamped.SuggestionCount, surfaceClamped.MarkedMediaLimit, surfaceClamped.StampSize));
        Assert.Equal(5000, QuickTagSettings.MarkedMediaLimit(wild));
        wild.QuickTagMarkedMediaLimit = int.MinValue;
        Assert.Equal(10, QuickTagSettings.MarkedMediaLimit(wild));
        // The defaults produce exactly the default option objects of the quick tag types.
        TagContextOptions standard = QuickTagSettings.ContextOptions(new Config());
        Assert.Equal((TagContextOptions.Default.SuggestionCount, TagContextOptions.Default.RecentCount, TagContextOptions.Default.NeighbourScan,
            TagContextOptions.Default.FilenameTokens, TagContextOptions.Default.SourceTagsPerMedium),
            (standard.SuggestionCount, standard.RecentCount, standard.NeighbourScan, standard.FilenameTokens, standard.SourceTagsPerMedium));
        Assert.Equal(QuickTagOptions.Default.Normalized(), QuickTagSettings.SurfaceOptions(new Config()));
    }

    [Fact]
    public void EveryLimitHasALabelAndAHintWithDefaultAndRangeInEnglishAndGerman()
    {
        string previous = Texts.Language;
        try
        {
            Assert.Equal(Limits.Select(limit => limit.Property), QuickTagSettingsCatalog.Limits.Select(limit => limit.Property));
            Assert.Equal(Limits.Select(limit => limit.Stem), QuickTagSettingsCatalog.Limits.Select(limit => limit.Stem));
            Assert.Equal(QuickTagSettingsCatalog.Entries.Length, QuickTagSettingsCatalog.Entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                Assert.NotEqual(QuickTagSettingsCatalog.GroupKey, Texts.T(QuickTagSettingsCatalog.GroupKey));
                foreach ((_, int fallback, int minimum, int maximum, string stem) in Limits)
                {
                    string label = Texts.T("budgets." + stem);
                    string hint = Texts.T("budgets." + stem + "Hint", fallback, minimum, maximum);
                    Assert.NotEqual("budgets." + stem, label);
                    Assert.NotEqual("budgets." + stem + "Hint", hint);
                    Assert.Contains(fallback.ToString(), hint);
                    Assert.Contains(minimum.ToString(), hint);
                    Assert.Contains(maximum.ToString(), hint);
                    Assert.DoesNotContain("{", hint);
                }
            }
            // Wording rules of the project for German texts: "du", "Medium", the ellipsis directly after the word.
            foreach ((string key, string english, string german) in QuickTagSettingsCatalog.Entries)
            {
                Assert.False(string.IsNullOrWhiteSpace(english), key);
                Assert.False(string.IsNullOrWhiteSpace(german), key);
                Assert.DoesNotContain(" …", german);
                Assert.DoesNotContain("...", english);
                Assert.DoesNotContain("Sie ", german);
                Assert.DoesNotContain("Ihr", german);
                Assert.DoesNotContain("Beitrag", german);
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void MarkupHasALabelledRowForEveryLimitInTheLimitsGroupAfterTheExistingRowsAndBeforeTheExpander()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
        XElement expander = Named("SettingsWorkerBudgetsHeading");
        XElement group = Named("SettingsLimitsGroup");
        List<XElement> order = markup.Descendants().ToList();
        int expanderIndex = order.IndexOf(expander);
        int previous = order.IndexOf(Named("MediaIndexMaxThreadsInput"));
        XElement heading = Named("LimitsQuickTagHeading");
        Assert.Equal("{Binding QuickTagGroupLabel}", (string?)heading.Attribute("Text"));
        Assert.Contains(heading.Ancestors(), e => ReferenceEquals(e, group));
        Assert.True(order.IndexOf(heading) > previous);
        previous = order.IndexOf(heading);
        foreach ((string property, _, int minimum, int maximum, _) in Limits)
        {
            XElement field = Named(property + "Input");
            Assert.Contains(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
            Assert.Contains(field.Ancestors(), e => ReferenceEquals(e, group));
            Assert.DoesNotContain(field.Ancestors(), e => ReferenceEquals(e, expander));
            Assert.Equal("{Binding " + property + ", Mode=TwoWay}", (string?)field.Attribute("Value"));
            Assert.Equal(minimum.ToString(), (string?)field.Attribute("Minimum"));
            Assert.Equal(maximum.ToString(), (string?)field.Attribute("Maximum"));
            Assert.Equal("{Binding ElementName=" + property + "FieldLabel}", (string?)field.Attribute("AutomationProperties.LabeledBy"));
            Assert.Equal("{Binding " + property + "Label}", (string?)field.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding " + property + "Help}", (string?)field.Attribute("AutomationProperties.HelpText"));
            Assert.Equal("{Binding " + property + "Label}", (string?)Named(property + "FieldLabel").Attribute("Text"));
            Named(property + "InputError");
            Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + property + "Hint}");
            int index = order.IndexOf(field);
            Assert.True(index > previous && index < expanderIndex, property + " is out of order.");
            previous = index;
        }
    }

    [Fact]
    public void TheEditorLoadsAppliesValidatesAndRefreshesTheLimitsAtEveryPlaceTheOtherLimitsDo()
    {
        string root = RepositoryRoot();
        string editor = File.ReadAllText(Path.Combine(root, "Desktop/Views/Settings/SettingsEditor.axaml.cs"));
        string validation = File.ReadAllText(Path.Combine(root, "Desktop/Views/Settings/SettingsEditor.Validation.cs"));
        string quickTags = File.ReadAllText(Path.Combine(root, "Desktop/Views/Settings/SettingsEditor.QuickTags.cs"));
        // Start, discard and after a save load the draft; Save applies it before Validate; a language change refreshes the texts.
        Assert.Equal(3, CountOf(editor, "LoadQuickTagDraft("));
        Assert.Equal(1, CountOf(editor, "ApplyQuickTagDraft(candidate)"));
        Assert.Equal(1, CountOf(editor, "RaiseQuickTagTextsChanged()"));
        Assert.Contains("NotifyQuickTagHelpChanged()", validation);
        Assert.Contains("QuickTagNumericFields()", validation);
        foreach ((string property, _, _, _, string stem) in Limits)
        {
            Assert.Contains($"public decimal? {property} ", quickTags);
            Assert.Contains($"candidate.{property} = GetInteger({property}, {property}Label, Config.{property}Minimum, Config.{property}Maximum);", quickTags);
            Assert.Contains($"ResetNumber({property}Input, config.{property});", quickTags);
            Assert.Contains($"yield return ({property}Input, {property}, {property}Label,", quickTags);
            Assert.Contains($"nameof({property}Help)", quickTags);
            Assert.Contains($"\"budgets.{stem}\"", quickTags);
            Assert.Contains($"\"budgets.{stem}Hint\"", quickTags);
        }
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
