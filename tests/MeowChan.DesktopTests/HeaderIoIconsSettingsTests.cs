using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

public sealed class HeaderIoIconsSettingsTests
{
    [Fact]
    public void WordsAreTheDefaultAndIconsSurviveCloneCopiesAndProfileReload()
    {
        Assert.False(new Config().StatsHeaderIcons);
        using TempDirectory directory = new();
        Config original = new() { StatsHeaderIcons = true, StatsHeaderGraph = "Text" };
        Config clone = original.Clone();
        Config settings = new();
        settings.CopySettingsFieldsFrom(clone);
        Config copy = new();
        copy.CopyFrom(settings);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config config in new[] { clone, settings, copy, loaded })
        {
            Assert.True(config.StatsHeaderIcons);
            Assert.Equal("Text", config.StatsHeaderGraph);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"Icons\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void MalformedOptionalModeKeepsWordsAndDoesNotLoseUnrelatedSettings(string literal)
    {
        using TempDirectory directory = new();
        new Config { StatsHeaderIcons = true, BooruCatalogLimitMiB = 17 }.SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        JsonObject json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["StatsHeaderIcons"] = JsonNode.Parse(literal);
        File.WriteAllText(file, json.ToJsonString());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.False(loaded.StatsHeaderIcons);
        Assert.Equal(17, loaded.BooruCatalogLimitMiB);
    }

    [Fact]
    public void OlderProfilesKeepWords()
    {
        using TempDirectory directory = new();
        new Config { StatsHeaderIcons = true }.SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        JsonObject json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.True(json.Remove("StatsHeaderIcons"));
        File.WriteAllText(file, json.ToJsonString());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.False(loaded.StatsHeaderIcons);
    }

    [Fact]
    public void OptionUsesNormalDraftLifecycleAndAccessibleStatisticsRow()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument doc = XDocument.Load(Root("Desktop/Views/Settings/SettingsEditor.axaml"));
        XElement option = doc.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "StatsHeaderIconsInput");
        Assert.Equal("CheckBox", option.Name.LocalName);
        Assert.Equal("{Binding StatsHeaderIcons, Mode=TwoWay}", (string?)option.Attribute("IsChecked"));
        Assert.Equal("{Binding StatsHeaderIconsLabel}", (string?)option.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding StatsHeaderIconsHint}", (string?)option.Attribute("AutomationProperties.HelpText"));
        Assert.Contains(option.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsStatisticsSection");
        string draft = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Statistics.cs"));
        Assert.Contains("StatsHeaderIcons = config.StatsHeaderIcons;", draft);
        Assert.Contains("candidate.StatsHeaderIcons = StatsHeaderIcons;", draft);
        string refresh = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.StatsHeaderGraph.cs"));
        Assert.Contains("nameof(StatsHeaderIconsLabel)", refresh);
        Assert.Contains("nameof(StatsHeaderIconsHint)", refresh);
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
