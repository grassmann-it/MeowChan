using System.Runtime.CompilerServices;
using MeowChan;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Pure error-state behavior plus wiring contracts; no plugin workers, compiler,
// XAML page construction or native-window/language-quality claim.
[Collection("Localization")]
public sealed class PluginErrorLanguageRefreshTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public PluginErrorLanguageRefreshTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    [Theory]
    [InlineData("plugins.unavailable")]
    [InlineData("plugins.noArtifact")]
    [InlineData("plugins.invalidUrl")]
    [InlineData("plugins.repair.finishDraft")]
    [InlineData("plugins.reason.busy")]
    [InlineData("plugins.reason.targetChanged")]
    public void HostErrorRetainsItsKeyAndRendersTheCurrentBundledLanguage(string key)
    {
        var state = PluginsPage.PluginErrorState.Localized(key);
        string english = state.Render();
        foreach (string language in new[] { "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(language, Texts.Language);
            Assert.Equal(key, state.Key);
            Assert.Null(state.RawText);
            Assert.Equal(Texts.T(key), state.Render());
            Assert.NotEqual(key, state.Render());
            Assert.False(string.IsNullOrEmpty(state.Render()));
            if (language != "en") Assert.NotEqual(english, state.Render());
        }
    }

    [Theory]
    [InlineData("plugins.buildFailed")]
    [InlineData("plugins.editorFallback")]
    public void LocalizedWrapperKeepsOriginalRawArgumentAndCopiesTheArgumentArray(string key)
    {
        const string detail = "CS0001: plugins.invalidUrl /tmp/plugin.cs {0}\nraw worker detail";
        object?[] arguments = { detail };
        var state = PluginsPage.PluginErrorState.Localized(key, arguments);
        arguments[0] = "replaced by caller";
        string english = state.Render();
        foreach (string language in new[] { "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T(key, detail), state.Render());
            Assert.Contains(detail, state.Render());
            if (language != "en") Assert.NotEqual(english, state.Render());
        }
    }

    [Theory]
    [InlineData("plugins.invalidUrl")]
    [InlineData("CS0001: error {0}\r\nplugin output: Español / Français / Русский")]
    [InlineData("")]
    public void RawErrorNeverInterpretsKeyLookingTextOrFormatsWorkerText(string raw)
    {
        var state = PluginsPage.PluginErrorState.Raw(raw);
        foreach (string language in new[] { "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Null(state.Key);
            Assert.Equal(raw, state.RawText);
            Assert.Equal(raw, state.Render());
        }
    }

    [Fact]
    public void RawTextEqualToAnEnglishTranslationIsNotReverseMapped()
    {
        const string key = "plugins.invalidUrl";
        string raw = Texts.T(key);
        var rawState = PluginsPage.PluginErrorState.Raw(raw);
        var localizedState = PluginsPage.PluginErrorState.Localized(key);
        Assert.Equal(rawState.Render(), localizedState.Render());
        Texts.SetLanguage("fr");
        Assert.Equal(raw, rawState.Render());
        Assert.Equal(Texts.T(key), localizedState.Render());
        Assert.NotEqual(rawState.Render(), localizedState.Render());
    }

    [Fact]
    public void LanguageCallbackRerendersRatherThanClearingTheFailure()
    {
        string source = ReadSource("PluginsPage.axaml.cs");
        string callback = Between(source, "private void OnLanguageChanged(", "private void Localize()");
        Assert.Contains("Dispatcher.UIThread.Post(", callback);
        Assert.Contains("if (_ended) return;", callback);
        Assert.Contains("RenderError();", callback);
        Assert.DoesNotContain("Error(null)", callback);
        Assert.DoesNotContain("_errorState =", callback);

        string render = Between(source, "private void RenderError()", "private (long Generation, CancellationToken Token) BeginOperation()");
        Assert.Contains("string? text = _errorState?.Render();", render);
        Assert.Contains("summary.Text = text;", render);
        Assert.Contains("summary.Focusable = !string.IsNullOrEmpty(text);", render);
        Assert.Contains("AutomationProperties.SetName(summary, text);", render);
        Assert.Contains("IsVisible = !string.IsNullOrEmpty(text);", render);
        Assert.DoesNotContain("_errorState =", render);
    }

    [Fact]
    public void ReplacingOrClearingAnErrorReplacesItsProvenanceToo()
    {
        string source = ReadSource("PluginsPage.axaml.cs");
        string raw = Between(source, "private void Error(string? text)", "private void ErrorLocalized(");
        Assert.Contains("_errorState = text is null ? null : PluginErrorState.Raw(text);", raw);
        Assert.Contains("RenderError();", raw);
        string localized = Between(source, "private void ErrorLocalized(", "private void RenderError()");
        Assert.Contains("_errorState = PluginErrorState.Localized(key, arguments);", localized);
        Assert.Contains("RenderError();", localized);
    }

    [Fact]
    public void HostCallSitesKeepKeysAndActionReasonsResolveKeysBeforeTranslation()
    {
        string main = ReadSource("PluginsPage.axaml.cs");
        string testing = ReadSource("PluginsPage.Testing.cs");
        string migration = ReadSource("PluginsPage.Migration.cs");
        string actions = ReadSource("PluginsPage.Actions.cs");
        Assert.Contains("ErrorLocalized(\"plugins.unavailable\")", main);
        Assert.Contains("ErrorLocalized(\"plugins.noArtifact\")", testing);
        Assert.Contains("ErrorLocalized(\"plugins.invalidUrl\")", testing);
        Assert.Contains("ErrorLocalized(\"plugins.buildFailed\", result.Status)", testing);
        Assert.Contains("ErrorLocalized(\"plugins.editorFallback\", exception.Message)", testing);
        Assert.Contains("ErrorLocalized(\"plugins.repair.finishDraft\")", migration);
        string execution = Between(actions, "private async Task ExecutePluginActionAsync(", "// Do not let a detached popup");
        Assert.Contains("PluginActionUnavailableReasonKey(action)", execution);
        Assert.Contains("ErrorLocalized(reasonKey)", execution);
        Assert.DoesNotContain("Error(reason)", execution);
        string keys = Between(actions, "private string? PluginActionUnavailableReasonKey(", "private static bool HasPluginSettings(");
        Assert.DoesNotContain("Texts.T(", keys);
        foreach (string source in new[] { main, testing, migration, actions })
            Assert.DoesNotContain("Error(Texts.T(", source);
        // Exceptions and validation results show a localized sentence; their original text is only the detail.
        Assert.Contains("ErrorFailure(\"plugins.error.refresh\", exception)", main);
        Assert.Contains("ErrorFailure(\"plugins.error.fields\", string.Join(Environment.NewLine, errors.Select(error => error.Error.Message)))", testing);
        Assert.Contains("ErrorFailure(\"plugins.error.repair\", exception)", migration);
        // Only the plugin's own probe output for the user's URL stays verbatim.
        Assert.Contains("if (result is ThreadFailure or CatalogFailure) Error(formatted);", testing);
        foreach (string source in new[] { main, testing, migration, actions, ReadSource("PluginsPage.Discard.cs") })
        {
            Assert.DoesNotContain("Error(exception.Message)", source);
            Assert.DoesNotContain("Error(error.Message)", source);
        }
    }

    [Theory]
    [InlineData("plugins.error.refresh")]
    [InlineData("plugins.error.folder")]
    [InlineData("plugins.error.change")]
    [InlineData("plugins.error.discard")]
    [InlineData("plugins.error.save")]
    [InlineData("plugins.error.fields")]
    [InlineData("plugins.error.test")]
    [InlineData("plugins.error.probe")]
    [InlineData("plugins.error.editorSave")]
    [InlineData("plugins.error.repair")]
    public void FailureShowsALocalizedSentenceAndKeepsTheOriginalTextOnlyAsDetail(string key)
    {
        const string raw = "System.IO.IOException: The process cannot access the file 'state.json' (HResult 0x80070020)";
        var state = PluginsPage.PluginErrorState.Failure(key, raw);
        string english = Texts.T(key);
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            string message = state.Render();
            Assert.Equal(Texts.T(key), message);
            Assert.NotEqual(key, message);
            Assert.DoesNotContain("IOException", message);
            Assert.DoesNotContain("HResult", message);
            Assert.Equal(raw, state.Detail);
            if (language != "en") Assert.NotEqual(english, message);
        }
        Assert.Null(PluginsPage.PluginErrorState.Failure(key, "  ").Detail);
        Assert.Null(PluginsPage.PluginErrorState.Localized(key).Detail);
        Assert.Null(PluginsPage.PluginErrorState.Raw(raw).Detail);
        Assert.Equal(raw, PluginsPage.PluginErrorState.Raw(raw).Render());
    }

    [Fact]
    public void RenderErrorKeepsTheDetailAsTooltipAndAccessibleHelp()
    {
        string render = Between(ReadSource("PluginsPage.axaml.cs"), "private void RenderError()", "private (long Generation, CancellationToken Token) BeginOperation()");
        Assert.Contains("ToolTip.SetTip(summary, detail);", render);
        Assert.Contains("AutomationProperties.SetHelpText(summary, detail ?? string.Empty);", render);
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Source anchor missing: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Source anchor missing: " + end);
        return source[first..last];
    }

    private static string ReadSource(string name, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!,
            "../../Desktop/Views/Plugins", name)));
}
