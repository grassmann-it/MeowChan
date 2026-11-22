using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SettingsLimitsUiTests
{
    private static readonly (string Name, int Default, int Min, int Max)[] Limits =
    [
        (nameof(Config.ArchiveTagFilterTagsPerList), Config.ArchiveTagFilterTagsPerListDefault, Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum),
        (nameof(Config.UrlTextExportLimitMiB), Config.UrlTextExportLimitMiBDefault, Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum),
        (nameof(Config.ExportHistorySizeMiB), Config.ExportHistorySizeMiBDefault, Config.ExportHistorySizeMiBMinimum, Config.ExportHistorySizeMiBMaximum),
        (nameof(Config.ExportHistoryEntryLimit), Config.ExportHistoryEntryLimitDefault, Config.ExportHistoryEntryLimitMinimum, Config.ExportHistoryEntryLimitMaximum),
        (nameof(Config.BoardAdmissionSizeMiB), Config.BoardAdmissionSizeMiBDefault, Config.BoardAdmissionSizeMiBMinimum, Config.BoardAdmissionSizeMiBMaximum),
        (nameof(Config.SlashTagSuggestionCount), Config.SlashTagSuggestionCountDefault, Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum),
        (nameof(Config.VideoScrubCacheLimitMiB), Config.VideoScrubCacheLimitMiBDefault, Config.VideoScrubCacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMaximum)
    ];

    [Fact]
    public void EveryNewLimitHasItsOwnDraftBindingAccessibleHelpAndErrorOutsideTheOverview()
    {
        XDocument document = XDocument.Load(Root("Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var limit in Limits)
        {
            XElement input = Assert.Single(document.Descendants(), e => (string?)e.Attribute(x + "Name") == limit.Name + "Input");
            Assert.Equal("{Binding " + limit.Name + ", Mode=TwoWay}", (string?)input.Attribute("Value"));
            Assert.Equal("{Binding " + limit.Name + "Minimum}", (string?)input.Attribute("Minimum"));
            Assert.Equal("{Binding " + limit.Name + "Maximum}", (string?)input.Attribute("Maximum"));
            Assert.Equal("{Binding " + limit.Name + "Help}", (string?)input.Attribute("AutomationProperties.HelpText"));
            Assert.Contains(input.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
            XElement error = Assert.Single(document.Descendants(), e => (string?)e.Attribute(x + "Name") == limit.Name + "InputError");
            Assert.Equal("Assertive", (string?)error.Attribute("AutomationProperties.LiveSetting"));
            Assert.Contains(document.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + limit.Name + "Hint}");
        }
        XElement shrink = Assert.Single(document.Descendants(), e => (string?)e.Attribute(x + "Name") == "ShrinkWindowForBarebonesInput");
        Assert.Equal("{Binding ShrinkWindowForBarebones, Mode=TwoWay}", (string?)shrink.Attribute("IsChecked"));
        Assert.NotNull(shrink.Attribute("AutomationProperties.HelpText"));
        Assert.Contains(shrink.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsInterfacePage");
    }

    [Fact]
    public void EveryLimitUsesConfigBoundsAndAllExistingLoadValidateApplyResetAndLocalePaths()
    {
        string limits = Read("Limits.cs"), editor = Read("axaml.cs"), validation = Read("Validation.cs"), reset = Read("Reset.cs");
        foreach (var limit in Limits)
        {
            Assert.Contains("= Config." + limit.Name + "Default;", limits);
            Assert.Contains("=> Config." + limit.Name + "Minimum;", limits);
            Assert.Contains("=> Config." + limit.Name + "Maximum;", limits);
            Assert.Contains(limit.Name + " = config." + limit.Name + ";", limits);
            Assert.Contains("ResetNumber(" + limit.Name + "Input, config." + limit.Name + ");", limits);
            Assert.Contains("candidate." + limit.Name + " = GetInteger(" + limit.Name, limits);
            Assert.Contains("yield return (" + limit.Name + "Input, " + limit.Name, limits);
            Assert.Contains("nameof(" + limit.Name + "Help)", limits);
        }
        Assert.Contains("LoadLimitsDraft(config);", editor);
        Assert.Equal(2, editor.Split("LoadLimitsDraft(_config);").Length - 1); // discard and post-save reload
        Assert.Contains("ApplyLimitsDraft(candidate);", editor);
        Assert.Contains("RaiseLimitsTextsChanged();", editor);
        Assert.Contains("NotifyLimitsHelpChanged();", validation);
        Assert.Contains("foreach (var field in LimitsNumericFields()) yield return field;", validation);
        Assert.Contains("LoadLimitNumbersDraft(defaults);", reset);
        Assert.Contains("ShrinkWindowForBarebones = defaults.ShrinkWindowForBarebones;", reset);
        Assert.Contains("StatsHeaderIcons = defaults.StatsHeaderIcons;", reset);
        Assert.DoesNotContain("SaveConfig(", limits + reset);
    }

    [Fact]
    public void RawDraftValidatorRejectsEveryOutOfRangeFractionalMalformedOrNullLimit()
    {
        foreach (var limit in Limits)
        {
            string? Validate(string? raw, decimal? value, bool bindingError = false) => SettingsEditor.NumericDraftError(raw,
                NumberStyles.Number, CultureInfo.InvariantCulture, bindingError, value, limit.Min, limit.Max, integer: true);
            Assert.Null(Validate(limit.Default.ToString(CultureInfo.InvariantCulture), limit.Default));
            Assert.Null(Validate(limit.Min.ToString(CultureInfo.InvariantCulture), limit.Min));
            Assert.Null(Validate(limit.Max.ToString(CultureInfo.InvariantCulture), limit.Max));
            Assert.Equal("settings.fieldInteger", Validate((limit.Min - 1).ToString(CultureInfo.InvariantCulture), limit.Default));
            Assert.Equal("settings.fieldInteger", Validate((limit.Max + 1).ToString(CultureInfo.InvariantCulture), limit.Default));
            Assert.Equal("settings.fieldInteger", Validate((limit.Min + .5m).ToString(CultureInfo.InvariantCulture), limit.Default));
            Assert.Equal("settings.fieldInteger", Validate(limit.Default.ToString(CultureInfo.InvariantCulture), null));
            Assert.Equal("settings.fieldNumber", Validate("not a number", limit.Default));
            Assert.Equal("settings.fieldNumber", Validate(null, limit.Default));
            Assert.Equal("settings.fieldNumber", Validate(limit.Default.ToString(CultureInfo.InvariantCulture), limit.Default, true));
        }
    }

    [Fact]
    public void PortableExportAutomaticallyIncludesAllNewSavedSettings()
    {
        Config saved = new()
        {
            ArchiveTagFilterTagsPerList = Config.ArchiveTagFilterTagsPerListMaximum,
            UrlTextExportLimitMiB = Config.UrlTextExportLimitMiBMaximum,
            ExportHistorySizeMiB = Config.ExportHistorySizeMiBMaximum,
            ExportHistoryEntryLimit = Config.ExportHistoryEntryLimitMaximum,
            BoardAdmissionSizeMiB = Config.BoardAdmissionSizeMiBMaximum,
            SlashTagSuggestionCount = Config.SlashTagSuggestionCountMaximum,
            VideoScrubCacheLimitMiB = Config.VideoScrubCacheLimitMiBMaximum,
            ShrinkWindowForBarebones = false
        };
        var json = SettingsTransfer.Export(saved);
        foreach (var limit in Limits) Assert.Equal(limit.Max, json[limit.Name]!.GetValue<int>());
        Assert.False(json[nameof(Config.ShrinkWindowForBarebones)]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(nameof(Config.ExportHistorySizeMiB))]
    [InlineData(nameof(Config.ExportHistoryEntryLimit))]
    [InlineData(nameof(Config.BoardAdmissionSizeMiB))]
    public void ArchivePersistenceLimitSurvivesCopiesAndTransferAndRejectsOutOfRangeValues(string name)
    {
        var limit = Assert.Single(Limits, item => item.Name == name);
        var property = typeof(Config).GetProperty(name)!;
        Config source = new() { DownloadPath = Path.GetTempPath() };
        Assert.Equal(limit.Default, property.GetValue(source));
        property.SetValue(source, limit.Max);
        source.Validate();

        Config clone = source.Clone();
        Config fullCopy = new();
        fullCopy.CopyFrom(source);
        Config settingsCopy = new();
        settingsCopy.CopySettingsFieldsFrom(source);
        Config transferred = JsonSerializer.Deserialize(SettingsTransfer.Export(source).ToJsonString(), ConfigJsonContext.Default.Config)!;
        foreach (Config copy in new[] { clone, fullCopy, settingsCopy, transferred })
            Assert.Equal(limit.Max, property.GetValue(copy));

        property.SetValue(source, limit.Max + 1);
        Assert.Equal(name, Assert.Throws<ArgumentOutOfRangeException>(source.Validate).ParamName);
        property.SetValue(source, limit.Min - 1);
        Assert.Equal(name, Assert.Throws<ArgumentOutOfRangeException>(source.Validate).ParamName);
    }

    [Fact]
    public void CatalogHasUniqueBilingualLabelsAndConfigFormattedBounds()
    {
        var entries = SettingsLimitsCatalog.Entries;
        Assert.Equal(16, entries.Length);
        Assert.Equal(entries.Length, entries.Select(e => e.Key).Distinct().Count());
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(string.IsNullOrWhiteSpace(entry.German));
            _ = string.Format(CultureInfo.InvariantCulture, entry.English, 32, 8, 128);
            _ = string.Format(CultureInfo.InvariantCulture, entry.German, 32, 8, 128);
        });
        Assert.All(entries.Where(e => e.Key.StartsWith("budgets.") && e.Key.EndsWith("Hint")), entry =>
        {
            foreach (string placeholder in new[] { "{0}", "{1}", "{2}" })
            {
                Assert.Contains(placeholder, entry.English);
                Assert.Contains(placeholder, entry.German);
            }
        });
    }

    private static string Read(string suffix) => File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor." + suffix));
    private static string Root(string relative, [CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative));
}
