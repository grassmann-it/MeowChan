using System.Runtime.CompilerServices;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The texts of "Add plugin…" and the lifecycle steps exist, differ from English and keep their placeholders in every language;
/// the page wires the add action and the steps without adding a second activation path.</summary>
[Collection("Localization")]
public sealed class PluginAddTextsTests : IDisposable
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };
    private static readonly (string Key, string[] Args)[] Keys =
    {
        ("plugins.add", []), ("plugins.add.help", []), ("plugins.add.pickerTitle", []), ("plugins.add.pickerType", []), ("plugins.add.noPicker", []),
        ("plugins.add.done", ["x.cs"]), ("plugins.add.alreadyThere", ["x.cs"]), ("plugins.add.rejected.unsupported", ["x.cs"]),
        ("plugins.add.rejected.empty", ["x.cs"]), ("plugins.add.rejected.tooLarge", ["x.cs", "4"]), ("plugins.add.rejected.notFile", ["x.cs"]),
        ("plugins.add.rejected.exists", ["x.cs"]), ("plugins.error.add", []), ("plugins.step.group", []),
        ("plugins.step.installed", []), ("plugins.step.enabled", []), ("plugins.step.active", []), ("plugins.step.activeRestart", []),
        ("plugins.step.name", ["1", "3", "S", "T"]), ("plugins.step.state.done", []), ("plugins.step.state.next", []),
        ("plugins.step.state.later", []), ("plugins.step.state.problem", []), ("plugins.step.next.missing", ["ADD"]),
        ("plugins.step.next.fix", []), ("plugins.step.next.restart", []), ("plugins.step.next.restartSaved", []),
        ("plugins.step.next.enable", ["BTN"]), ("plugins.step.next.select", ["BTN"]), ("plugins.step.next.sdk", ["BTN"]),
        ("plugins.step.next.saved", [])
    };

    private readonly string _previous = Texts.Language;

    public PluginAddTextsTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    [Fact]
    public void EveryTextIsTranslatedAndEveryArgumentAppearsInEveryLanguage()
    {
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(language, Texts.Language);
            foreach ((string key, string[] args) in Keys)
            {
                string text = Texts.T(key, args.Cast<object?>().ToArray());
                Assert.NotEqual(key, text);
                Assert.False(string.IsNullOrWhiteSpace(text), language + " " + key);
                foreach (string arg in args) Assert.Contains(arg, text);
                // "Active" is the same word in French; the step layout can match English too.
                if (language != "en" && key is not ("plugins.step.name" or "plugins.step.active"))
                    Assert.NotEqual(Texts.T(key, args.Cast<object?>().ToArray()) , EnglishOf(key, args));
            }
        }
    }

    private static string EnglishOf(string key, string[] args)
    {
        string current = Texts.Language;
        Texts.SetLanguage("en");
        try { return Texts.T(key, args.Cast<object?>().ToArray()); }
        finally { Texts.SetLanguage(current); }
    }

    [Fact]
    public void ThePageOffersTheAddActionAndTheStepsWithoutAnActivationPathOfTheirOwn()
    {
        string xaml = Read("PluginsPage.axaml");
        Assert.Matches("<Button x:Name=\"PluginsAdd\"[^>]* Click=\"OnAddClick\"", xaml); // the work view may place it under More
        Assert.Contains("x:Name=\"PluginStepChips\"", xaml);
        Assert.Contains("x:Name=\"PluginLifecycleNext\"", xaml);
        // The steps only display: no profile call, no command execution, no navigation.
        string lifecycle = Read("PluginsPage.Lifecycle.cs");
        foreach (string forbidden in new[] { "ProfileStore", "CommandAsync", "ExecutePluginActionAsync", "SetEnabledAsync", "Click +=" })
            Assert.DoesNotContain(forbidden, lifecycle);
        // The import runs through the shared copy and shows its refusal reasons; the page refreshes and selects, nothing more.
        string import = Read("PluginsPage.Import.cs");
        Assert.Contains("PluginFileImport.ImportAsync(path, directory, token)", import);
        Assert.Contains("plugins.add.rejected.exists", import);
        Assert.Contains("RenderLifecycle(entry, presentation);", Read("PluginsPage.axaml.cs"));
    }

    private static string Read(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../Desktop/Views/Plugins", name)));
}
