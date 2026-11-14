using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// TH2: Config.ThemeOverrides – normalizing, clone/copy/save/load, damaged values, settings export and re-import.
public sealed class ThemeOverridesConfigTests
{
    private static Color C(string hex) => ThemeCustomization.ParseHex(hex);

    private static ThemeColorOverrides Sample() => ThemeColorOverrides.Empty
        .WithRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1D5E3A"))
        .WithSeed(ThemeTokenTheme.Light, "Workspace.Scrim", C("#80102030"))
        .WithRole(ThemeTokenTheme.Dark, ThemeUserColors.Surface, C("#232A3A"));

    [Fact]
    public void MissingNullOrEmptyIsNoOverride()
    {
        Assert.Same(ThemeColorOverrides.Empty, new Config().ThemeOverrides);
        foreach (string json in new[] { "{}", "{\"ThemeOverrides\":null}", "{\"ThemeOverrides\":{}}", "{\"ThemeOverrides\":{\"Light\":{},\"Dark\":{}}}" })
        {
            Config loaded = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)!;
            Assert.True(loaded.ThemeOverrides.IsEmpty, json);
            loaded.Validate();
        }
        Assert.Same(ThemeColorOverrides.Empty, new Config { ThemeOverrides = null! }.ThemeOverrides);
    }

    [Fact]
    public void TheStoredShapeIsPerVariantSeedToHexLikeThemeJsonSeeds()
    {
        JsonObject saved = JsonSerializer.SerializeToNode(new Config { ThemeOverrides = Sample() }, ConfigJsonContext.Default.Config)!.AsObject();
        Assert.Equal("{\"Light\":{\"Workspace.Accent\":\"#1D5E3A\",\"Workspace.Scrim\":\"#80102030\"},\"Dark\":{\"Workspace.Panel\":\"#232A3A\"}}",
            saved[nameof(Config.ThemeOverrides)]!.ToJsonString());
        Assert.Equal("{\"Light\":{},\"Dark\":{}}",
            JsonSerializer.SerializeToNode(new Config(), ConfigJsonContext.Default.Config)![nameof(Config.ThemeOverrides)]!.ToJsonString());
    }

    [Fact]
    public void ValuesSurviveCloneCopiesAndSaveLoad()
    {
        using TempDirectory directory = new();
        ThemeColorOverrides sample = Sample();
        Config original = new() { ThemeOverrides = sample, DownloadPath = Path.Combine(directory.Path, "media") };
        Config clone = original.Clone();
        Config settingsCopy = new();
        settingsCopy.CopySettingsFieldsFrom(clone);
        Config fullCopy = new();
        fullCopy.CopyFrom(settingsCopy);
        fullCopy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config candidate in new[] { original, clone, settingsCopy, fullCopy, loaded })
        {
            Assert.Equal(sample, candidate.ThemeOverrides);
            candidate.Validate();
        }
        Assert.Equal(C("#1D5E3A"), loaded.ThemeOverrides.Get(ThemeTokenTheme.Light, "Workspace.Accent"));
        Assert.Null(loaded.ThemeOverrides.Get(ThemeTokenTheme.Dark, "Workspace.Accent"));
    }

    [Theory]
    [InlineData("null", "{}", "{}")]
    [InlineData("true", "{}", "{}")]
    [InlineData("42", "{}", "{}")]
    [InlineData("\"#123456\"", "{}", "{}")]
    [InlineData("[]", "{}", "{}")]
    [InlineData("[{\"Light\":{\"Workspace.Accent\":\"#123456\"}}]", "{}", "{}")]
    [InlineData("{\"Light\":[],\"Dark\":\"#123456\"}", "{}", "{}")]
    [InlineData("{\"light\":{\"Workspace.Accent\":\"#123456\"},\"Sepia\":{}}", "{}", "{}")]
    [InlineData("{\"Light\":{\"Workspace.Accent\":5,\"Workspace.Text\":null,\"Workspace.Muted\":{\"x\":1},\"Workspace.Panel\":[\"#123456\"]}}", "{}", "{}")]
    [InlineData("{\"Light\":{\"Workspace.Accent\":\"#12345\",\"Workspace.Text\":\"red\",\"Workspace.Muted\":\"#GG0000\",\"Workspace.Panel\":\"\",\"Workspace.Card\":\"#1234567\"}}", "{}", "{}")]
    [InlineData("{\"Light\":{\"workspace.accent\":\"#123456\",\"ButtonDefaultPrimaryForeground\":\"#123456\",\"Workspace.Shadow.Overlay\":\"#123456\",\"\":\"#123456\"}}", "{}", "{}")]
    [InlineData("{\"Light\":{\"Workspace.Accent\":\" #abcdef \",\"Nope\":\"#123456\"},\"Dark\":{\"Workspace.Text\":\"#FFeeddcc\",\"Workspace.Scrim\":\"#66000000\"}}",
        "{\"Workspace.Accent\":\"#ABCDEF\"}", "{\"Workspace.Scrim\":\"#66000000\",\"Workspace.Text\":\"#EEDDCC\"}")]
    public void DamagedEntriesAreDroppedAndTheRestOfTheProfileIsKept(string raw, string light, string dark)
    {
        using TempDirectory directory = new();
        new Config { DownloadPath = Path.Combine(directory.Path, "media"), MaxParallelDownloads = 7 }.SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document[nameof(Config.ThemeOverrides)] = JsonNode.Parse(raw);
        // A preference after the damaged value must still be read.
        document.Remove(nameof(Config.VideoVolume));
        document[nameof(Config.VideoVolume)] = 31;
        File.WriteAllText(path, document.ToJsonString());

        Config loaded = Config.LoadConfig(directory.Path);

        Assert.Null(loaded.LoadWarning);
        Assert.Equal(ProfileOrigin.ExistingValid, loaded.Origin);
        Assert.Equal((7, 31), (loaded.MaxParallelDownloads, loaded.VideoVolume));
        JsonObject saved = JsonSerializer.SerializeToNode(loaded, ConfigJsonContext.Default.Config)!.AsObject();
        Assert.Equal("{\"Light\":" + light + ",\"Dark\":" + dark + "}", saved[nameof(Config.ThemeOverrides)]!.ToJsonString());
        loaded.SaveConfig(directory.Path);
        Assert.Equal(loaded.ThemeOverrides, Config.LoadConfig(directory.Path).ThemeOverrides);
    }

    [Fact]
    public void CreateNormalizesAndDescribesWhatItDrops()
    {
        var dropped = new List<string>();
        ThemeColorOverrides overrides = ThemeColorOverrides.Create(
            [new("Workspace.Accent", "#ff803b06"), new("Workspace.Text", "#12"), new("Workspace.Panel" + new string('x', 200), "#123456"), new(null!, "#123456")],
            [new("Workspace.Accent", "#6aa7ff")], dropped);
        Assert.Equal(new Dictionary<string, string> { ["Workspace.Accent"] = "#803B06" }, overrides.Light);
        Assert.Equal(new Dictionary<string, string> { ["Workspace.Accent"] = "#6AA7FF" }, overrides.Dark);
        Assert.Equal(3, dropped.Count);
        Assert.Contains("Light: invalid colour for Workspace.Text", dropped);
        Assert.All(dropped, entry => Assert.True(entry.Length < 120, entry));
        Assert.DoesNotContain(dropped, entry => entry.Contains("#12", StringComparison.Ordinal)); // values are never echoed
        Assert.Same(ThemeColorOverrides.Empty, ThemeColorOverrides.Create([new("Nope", "#123456")], null));
    }

    [Fact]
    public void SetAndResetPerRolePerVariantAndAll()
    {
        ThemeColorOverrides overrides = Sample()
            .WithSeed(ThemeTokenTheme.Light, "Workspace.AccentHover", C("#123456"))
            .WithRole(ThemeTokenTheme.Light, ThemeUserColors.Text, C("#202020"));
        Assert.Equal(["Workspace.Accent", "Workspace.AccentHover", "Workspace.Scrim", "Workspace.Text"], overrides.Light.Keys.Order(StringComparer.Ordinal));

        ThemeColorOverrides accentReset = overrides.ResetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent);
        Assert.Equal(["Workspace.Scrim", "Workspace.Text"], accentReset.Light.Keys.Order(StringComparer.Ordinal)); // primary and related
        Assert.Equal(overrides.Dark, accentReset.Dark);

        ThemeColorOverrides lightReset = overrides.ResetTheme(ThemeTokenTheme.Light);
        Assert.Empty(lightReset.Light);
        Assert.Equal(overrides.Dark, lightReset.Dark);
        Assert.Same(ThemeColorOverrides.Empty, lightReset.ResetTheme(ThemeTokenTheme.Dark));
        Assert.Same(ThemeColorOverrides.Empty, ThemeColorOverrides.Empty.ResetRole(ThemeTokenTheme.Dark, ThemeUserColors.Error));
        Assert.Equal(overrides.Light.Count - 1, overrides.WithSeed(ThemeTokenTheme.Light, "Workspace.Text", null).Light.Count);

        Assert.Throws<ArgumentException>(() => overrides.WithSeed(ThemeTokenTheme.Light, "ButtonSolidPrimaryBackground", C("#123456")));
        Assert.Throws<ArgumentException>(() => overrides.WithRole(ThemeTokenTheme.Light, "Accent", C("#123456")));
        Assert.Equal(Sample(), Sample());
        Assert.Equal(Sample().GetHashCode(), Sample().GetHashCode());
        Assert.NotEqual(Sample(), overrides);
    }

    [Fact]
    public void TheExportCarriesTheOverridesVerbatimAndReadsBackAsTheSame()
    {
        Config source = new() { ThemeOverrides = Sample() };
        Config snapshot = SettingsTransfer.CreatePortableSnapshot(source);
        Assert.Equal(source.ThemeOverrides, snapshot.ThemeOverrides);

        JsonObject export = SettingsTransfer.Export(source, "1.1.0");
        JsonObject serialized = JsonSerializer.SerializeToNode(source, ConfigJsonContext.Default.Config)!.AsObject();
        Assert.True(JsonNode.DeepEquals(serialized[nameof(Config.ThemeOverrides)], export[nameof(Config.ThemeOverrides)]));
        Config imported = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;
        Assert.Equal(Sample(), imported.ThemeOverrides);
        Assert.Equal(Sample(), source.ThemeOverrides); // the source is untouched
    }
}
