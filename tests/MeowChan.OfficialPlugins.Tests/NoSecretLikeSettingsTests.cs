using System.Text.RegularExpressions;
using MeowChan.OfficialSources.Danbooru;
using MeowChan.OfficialSources.E621;
using MeowChan.OfficialSources.EightKun;
using MeowChan.OfficialSources.FourChan;
using MeowChan.PluginSdk;

namespace MeowChan.OfficialPlugins.Tests;

// Contract lint: credentials belong to the host, so no shipped official plugin may declare a setting that
// looks like a key, token, password, secret, cookie or session. The test loads the real Describe() output
// of every official plugin; it lives in this project because only this one references the official plugins
// (the SDK test project deliberately does not). It accepts no baseline: any match is a failure.
public sealed class NoSecretLikeSettingsTests
{
    private static readonly Regex SecretLike = new(
        "api[_-]?key|token|passw|secret|cookie|session",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // One entry per project under plugins/Official; OfficialPluginListCoversEveryOfficialPluginProject guards drift.
    private static IPlugin[] OfficialPlugins() => [new FourChanPlugin(), new EightKunPlugin(), new DanbooruPlugin(), new E621Plugin()];

    [Fact]
    public void OfficialPluginSettingsDeclareNoSecretLikeKeysOrLabels()
    {
        List<string> offenders = [];
        foreach (IPlugin plugin in OfficialPlugins())
        {
            PluginDescription description = plugin.Describe();
            foreach ((string path, SettingsSchema schema) in Schemas(description))
            {
                foreach (SettingDefinition field in schema.Fields)
                {
                    offenders.AddRange(Offenses(description.PluginId, path, field));
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Official plugins must not declare secret-like settings (credentials are host-owned):\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void OfficialPluginListCoversEveryOfficialPluginProject()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MeowChan.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.True(root is not null, "Repository root (MeowChan.sln) not found above the test output directory.");
        string[] projects = Directory.GetDirectories(Path.Combine(root!, "plugins", "Official"))
            .Where(directory => Directory.GetFiles(directory, "*.csproj").Length > 0)
            .ToArray();
        IPlugin[] plugins = OfficialPlugins();

        Assert.Equal(projects.Length, plugins.Length);
        Assert.Equal(plugins.Length, plugins.Select(plugin => plugin.Describe().PluginId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(plugins, plugin => Assert.StartsWith("meowchan.official.", plugin.Describe().PluginId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("api_key", "API key")]
    [InlineData("apiKey", "Key")]
    [InlineData("api-key", "Key")]
    [InlineData("auth_token", "Authentication")]
    [InlineData("user", "Password")]
    [InlineData("user", "Passwort")]
    [InlineData("client_secret", "Client")]
    [InlineData("cookies", "Cookies")]
    [InlineData("login", "Session id")]
    public void PatternFlagsSecretLikeKeysAndLabels(string key, string label)
    {
        SettingDefinition field = new(key, SettingScope.Plugin, SettingKind.Text, label);
        Assert.NotEmpty(Offenses("fixture", "plugin", field));
    }

    [Theory]
    [InlineData("page_size", "Page size")]
    [InlineData("base_uri", "Base address")]
    [InlineData("max_results", "Result limit")]
    public void PatternAcceptsOrdinarySettings(string key, string label)
    {
        SettingDefinition field = new(key, SettingScope.Plugin, SettingKind.Text, label);
        Assert.Empty(Offenses("fixture", "plugin", field));
    }

    [Fact]
    public void LintCoversOptionsAndNestedSchemas()
    {
        SettingDefinition field = new("mode", SettingScope.Source, SettingKind.Enum, "Mode",
            Options: [new SettingOption("anonymous", "Anonymous"), new SettingOption("cookie", "Use cookie")]);
        Assert.Equal(2, Offenses("fixture", "source[x]", field).Count());

        PluginDescription description = new("fixture.plugin", "Fixture", "1.0.0", SdkContract.Version, "Fixture",
            Array.Empty<SourceDescription>(),
            [new FilterDescription("f", "F", "F", FilterDataAccess.None,
                new SettingsSchema("1", [new SettingDefinition("token", SettingScope.FilterRule, SettingKind.Text, "T")]),
                Array.Empty<string>())],
            new SettingsSchema("1", Array.Empty<SettingDefinition>()), Array.Empty<InformationEntry>());
        Assert.Contains(Schemas(description), entry => entry.Path == "filter[f]" && entry.Schema.Fields.Count == 1);
    }

    private static IEnumerable<(string Path, SettingsSchema Schema)> Schemas(PluginDescription description)
    {
        yield return ("plugin", description.Settings);
        foreach (SourceDescription source in description.Sources)
        {
            yield return ($"source[{source.SourceId}]", source.Settings);
        }

        foreach (FilterDescription filter in description.Filters)
        {
            yield return ($"filter[{filter.Key}]", filter.Settings);
        }
    }

    private static IEnumerable<string> Offenses(string pluginId, string path, SettingDefinition field)
    {
        foreach ((string part, string? text) in Texts(field))
        {
            if (text is not null && SecretLike.IsMatch(text))
            {
                yield return $"{pluginId} {path} setting '{field.Key}': {part} '{text}' looks secret-like";
            }
        }
    }

    private static IEnumerable<(string Part, string? Text)> Texts(SettingDefinition field)
    {
        yield return ("key", field.Key);
        yield return ("label", field.Label);
        foreach (SettingOption option in field.Options ?? [])
        {
            yield return ("option value", option.Value);
            yield return ("option label", option.Label);
        }
    }
}
