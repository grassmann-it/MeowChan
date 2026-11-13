using System.Text.RegularExpressions;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Source-level contract of the Rules page rework (footer on the content column, errors at the field, Save stays
/// available). The behavioural proof (invalid input, Save, focus on the field, nothing written) lives in the Rules smoke.
/// </summary>
[Collection("Localization")]
public sealed class RulesPageValidationContractTests
{
    private static readonly string[] NewKeys = { "rules.saveRejected", "rules.previewBlocked", "rules.goToField" };

    [Fact]
    public void SaveDoesNotDependOnAValidPreviewAndTheStatusLivesOnlyInTheFooter()
    {
        string code = ReadSource("Desktop/Views/Rules/RulesPage.axaml.cs");
        Match canSave = Regex.Match(code, @"public bool CanSave => ([^;]+);");
        Assert.True(canSave.Success);
        Assert.DoesNotContain("_preview", canSave.Groups[1].Value);
        Assert.DoesNotContain("DraftStateLabel", code);
        string xaml = ReadSource("Desktop/Views/Rules/RulesPage.axaml");
        Assert.DoesNotContain("DraftStateLabel", xaml);
        // The footer grid is sized to the 880 dip body column in OnRulesSizeChanged, not to its own wider limit.
        Assert.DoesNotContain("MaxWidth=\"1088\"", xaml);
        Assert.Contains("RulesFooter.Width = width;", code);
    }

    [Fact]
    public void FieldErrorsSitAtTheirFieldWithAnAssertiveLiveRegionAndAnEdge()
    {
        string xaml = ReadSource("Desktop/Views/Rules/RulesPage.axaml");
        foreach (string name in new[] { "RuleExtensionsErrorText", "RuleMegabytesErrorText", "RuleValidationSummary", "RuleActionError" })
        {
            Match element = Regex.Match(xaml, @"<TextBlock[^>]*x:Name=""" + name + @"""[^>]*/>");
            Assert.True(element.Success, name);
            Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", element.Value);
        }
        Assert.Contains("IsVisible=\"{Binding HasExtensionsError}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding HasMegabytesError}\"", xaml);
        // The banner below the preview is only for errors without a field of their own.
        Assert.Contains("IsVisible=\"{Binding HasValidationBanner}\"", xaml);
        string code = ReadSource("Desktop/Views/Rules/RulesPage.axaml.cs");
        Assert.Contains("RuleExtensionsInput.Classes.Set(\"workspace-invalid\", HasExtensionsError);", code);
        Assert.Contains("RuleMegabytesInput.Classes.Set(\"workspace-invalid\", HasMegabytesError);", code);
        Assert.Contains("ursa|TagInput.workspace-invalid /template/ Border", ReadSource("Desktop/WorkspaceStyles.axaml"));
        // A refused save moves focus to the first invalid field before it returns, and writes nothing.
        string save = Between(code, "internal async Task SaveCurrentAsync()", "internal void DiscardCurrentDraft()");
        Assert.True(save.IndexOf("FocusInvalidField();", StringComparison.Ordinal) is > 0 and var focus
            && focus < save.IndexOf("_saveRules(", StringComparison.Ordinal));
    }

    [Fact]
    public void NewKeysExistInEveryLanguageWithTheirPlaceholder()
    {
        string original = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de", "fr", "es", "ru" })
            {
                Texts.SetLanguage(language);
                foreach (string key in NewKeys)
                {
                    string text = Texts.T(key, "X");
                    Assert.NotEqual(key, text);
                    if (key != "rules.goToField") Assert.Contains("X", text);
                }
            }
        }
        finally { Texts.SetLanguage(original); }
    }

    [Fact]
    public void SubtitleIsOneSentenceInEveryLanguageAndHidesBelowTheWindowHeightThreshold()
    {
        string original = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de", "fr", "es", "ru" })
            {
                Texts.SetLanguage(language);
                string subtitle = Texts.T("rules.subtitle");
                Assert.NotEqual("rules.subtitle", subtitle);
                Assert.DoesNotContain(". ", subtitle.TrimEnd('.'));
            }
        }
        finally { Texts.SetLanguage(original); }
        Assert.Equal(600, RulesPage.SubtitleMinWindowHeight);
        Assert.Contains("RulesSubtitle.IsVisible = !_subtitleHidden;", ReadSource("Desktop/Views/Rules/RulesPage.axaml.cs"));
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return source[from..to];
    }

    private static string ReadSource(string path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(path);
    }
}
