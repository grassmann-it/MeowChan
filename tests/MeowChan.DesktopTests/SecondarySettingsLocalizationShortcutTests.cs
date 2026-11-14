using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Static editor-contract checks only: no window, profile, command execution or media.
[Collection("Localization")]
public sealed class SecondarySettingsLocalizationShortcutTests : IDisposable
{
    public SecondarySettingsLocalizationShortcutTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // The Settings card (ShortcutEditor) and the shortcuts window's editor (KS2) together use these texts.
    private static readonly string[] EditorKeys =
    {
        "hint", "search", "actions", "action", "resetAll",
        "unbound", "unavailable", "custom", "default", "remove", "recordAdditional",
        "clearBinding", "restoreDefault", "record", "recordHelp", "keepExplicit", "applyRecording",
        "takeOver", "cancelRecording", "altGrText", "gestureLimit", "issuesHelp", "singleKeys", "singleKeys.help"
    };

    private static string EditorSources() => File.ReadAllText(RepositoryPath("Desktop/Commands/ShortcutEditor.cs"))
        + File.ReadAllText(RepositoryPath("Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs"));

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EditorFrameUsesSettingsCatalogForEverySupportedLanguage(string language)
    {
        Texts.SetLanguage(language);
        string source = EditorSources();
        foreach (string suffix in EditorKeys)
        {
            string key = "settings.shortcutsEditor." + suffix;
            Assert.Contains("Texts.T(\"" + key + "\")", source);
            Assert.Equal(Texts.T(key), ReadPackOrBuiltIn(language, key));
            Assert.NotEqual(key, Texts.T(key));
        }

        // The editor-local EN/DE selector is gone; command labels/IDs remain
        // outside this contract and are deliberately not rewritten here.
        Assert.DoesNotContain("private static string T(", source);
        Assert.Contains("Texts.T(\"shortcuts.title\")", source);
    }

    [Fact]
    public void LanguageRefreshKeepsCaptureAndDraftLifecycleCodeUntouchedByLocalization()
    {
        foreach (string file in new[] { "Desktop/Commands/ShortcutEditor.cs", "Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs" })
        {
            string source = File.ReadAllText(RepositoryPath(file));
            Assert.Contains("Texts.Changed += OnLanguageChanged", source);
            Assert.Contains("Texts.Changed -= OnLanguageChanged", source);
            Assert.Contains("private void OnLanguageChanged(object? sender, EventArgs e) => Localize();", source);
            Assert.Contains("_validationFocusRequest++", source);
        }
        // Edits go through the shared draft session (KS1/KS2); its Changed event, not the language, rebuilds the table.
        string editor = File.ReadAllText(RepositoryPath("Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs"));
        Assert.Contains("_session.Changed += OnSessionChanged;", editor);
        Assert.Contains("StopCapture();", editor);
        Assert.Contains("_session.TryAdd(", editor);
        string card = File.ReadAllText(RepositoryPath("Desktop/Commands/ShortcutEditor.cs"));
        Assert.Contains("if (change == ShortcutDraftChange.Edited) DraftEdited?.Invoke(this, EventArgs.Empty);", card);
    }

    private static string ReadPackOrBuiltIn(string language, string key)
    {
        if (language is "en" or "de")
            return (language == "en" ? BuiltInCatalog.English : BuiltInCatalog.German)[key];
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
            "MeowChan.Desktop.Localization.Packs." + language + ".json")!;
        using JsonDocument pack = JsonDocument.Parse(stream);
        return pack.RootElement.GetProperty("strings").GetProperty(key).GetString()!;
    }

    private static string RepositoryPath(string relative, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative));
}
