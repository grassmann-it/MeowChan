using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// HB2: the help buttons of the pages. Every <c>HelpKey</c> in the markup and the code has a note in all twelve languages that is a
/// title and at most four short lines, the places the plan lists have their button, and no retired text is left behind.
/// </summary>
[Collection("Localization")]
public sealed class HelpPlacesTests : IDisposable
{
    /// <summary>The note and the file that carries its button (the pages of HB1's Statistics page stay with HelpPopoverTests).</summary>
    private static readonly (string Key, string File)[] Places =
    [
        ("help.rules", "Desktop/Views/Rules/RulesPage.axaml"),
        ("help.workviews", "Desktop/Views/Settings/SettingsEditor.axaml"),
        ("help.limits", "Desktop/Views/Settings/SettingsEditor.axaml"),
        ("help.statsSettings", "Desktop/Views/Settings/SettingsEditor.axaml"),
        ("help.admissionWizard", "Desktop/Views/Settings/SettingsEditor.axaml"),
        ("help.plugins", "Desktop/Views/Plugins/PluginsPage.axaml"),
        ("help.admissionResults", "Desktop/Shell/MainWindow.axaml"),
        ("help.admissionRules", "Desktop/Views/Admission/BoardAdmissionWindow.cs"),
        ("help.shortcuts", "Desktop/Commands/ShortcutEditor.cs"),
        ("help.tagSuggestions", "Desktop/Controls/QuickTagSurface.cs"),
        ("help.taggingPage", "Desktop/Views/Tagging/TaggingPage.cs"),
        ("help.taggingStamp", "Desktop/Views/Tagging/TaggingPage.Stamp.cs"),
        ("help.themeColors", "Desktop/Views/Settings/ThemeColorsDialog.cs"),
    ];

    /// <summary>A note is a title and a few lines; longer text belongs in Help.md. Counted in characters of the longest language.</summary>
    private const int MaxBodyLines = 4, MaxLineLength = 260;

    public HelpPlacesTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static HelpContent Content(string key) =>
        key == "help.workviews" ? HelpSources.WorkViews() : HelpContent.FromKeys(key);

    [Fact]
    public void EveryPlaceHasItsButtonAndNoKeyIsUsedThatTheCatalogDoesNotHave()
    {
        foreach ((string key, string file) in Places)
        {
            string text = File.ReadAllText(Root(file));
            Assert.True(text.Contains("HelpKey=\"" + key + "\"", StringComparison.Ordinal)
                || text.Contains("HelpKey = \"" + key + "\"", StringComparison.Ordinal), key + " is not used in " + file);
            Assert.True(BuiltInCatalog.English.ContainsKey(key + ".title"), key + " has no title");
        }
        // A key that is used anywhere in the application is one of the places or HB1's Statistics note.
        string[] used = Directory.EnumerateFiles(Root("Desktop"), "*.*", SearchOption.AllDirectories)
            .Where(file => (file.EndsWith(".axaml", StringComparison.Ordinal) || file.EndsWith(".cs", StringComparison.Ordinal))
                && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "HelpKey\\s*=\\s*\"(help\\.[A-Za-z]+)\"").Select(match => match.Groups[1].Value))
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Places.Select(place => place.Key).Append("help.stats").Order(StringComparer.Ordinal), used);
    }

    [Fact]
    public void EveryNoteIsATitleAndAFewShortLinesInAllTwelveLanguagesWithTheSameShape()
    {
        foreach ((string key, _) in Places)
        {
            Texts.SetLanguage("en");
            HelpContent english = Content(key);
            int body = english.Paragraphs.Count + english.Bullets.Count;
            Assert.InRange(body, 1, MaxBodyLines);
            Assert.InRange(english.Paragraphs.Count, 0, 2);
            Assert.InRange(english.Bullets.Count, 0, 3);
            foreach (string language in Texts.Languages.Select(info => info.Code))
            {
                Texts.SetLanguage(language);
                HelpContent content = Content(key);
                string where = key + " in " + language;
                Assert.NotEqual(key + ".title", content.Title);
                Assert.False(string.IsNullOrWhiteSpace(content.Title), where);
                Assert.Equal(english.Paragraphs.Count, content.Paragraphs.Count);
                Assert.Equal(english.Bullets.Count, content.Bullets.Count);
                foreach (string line in content.Paragraphs.Concat(content.Bullets))
                {
                    Assert.False(string.IsNullOrWhiteSpace(line), where);
                    Assert.DoesNotContain("{0}", line, StringComparison.Ordinal);
                    Assert.DoesNotContain("help.", line, StringComparison.Ordinal);
                    Assert.True(line.Length <= MaxLineLength, where + " has a line of " + line.Length + " characters");
                }
            }
        }
    }

    [Fact]
    public void ThePacksTranslateTheNotesAndDoNotJustCopyTheEnglishText()
    {
        foreach ((string key, _) in Places.Where(place => place.Key != "help.workviews"))
        {
            Texts.SetLanguage("en");
            string english = Content(key).Paragraphs.Concat(Content(key).Bullets).First();
            foreach (string language in Texts.Languages.Select(info => info.Code).Where(code => code != "en"))
            {
                Texts.SetLanguage(language);
                Assert.NotEqual(english, Content(key).Paragraphs.Concat(Content(key).Bullets).First());
            }
        }
    }

    [Fact]
    public void TheWorkViewNoteListsTheThreeViewsWithTheirExistingDescriptions()
    {
        foreach (string language in Texts.Languages.Select(info => info.Code))
        {
            Texts.SetLanguage(language);
            HelpContent content = HelpSources.WorkViews();
            Assert.Single(content.Paragraphs);
            Assert.Equal(3, content.Bullets.Count);
            Assert.Contains(Texts.T("mode.barebones"), content.Bullets[0], StringComparison.Ordinal);
            Assert.Contains(Texts.T("workview.description.barebones"), content.Bullets[0], StringComparison.Ordinal);
            Assert.Contains(Texts.T("workview.description.simple"), content.Bullets[1], StringComparison.Ordinal);
            Assert.Contains(Texts.T("workview.description.advanced"), content.Bullets[2], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFoldedRulesExpanderOfTheAdmissionWizardIsGoneAndItsTextsAreRetired()
    {
        string window = File.ReadAllText(Root("Desktop/Views/Admission/BoardAdmissionWindow.cs"));
        Assert.DoesNotContain("wizard.semantics", window, StringComparison.Ordinal);
        Assert.DoesNotContain("wizard.rulesHelp", window, StringComparison.Ordinal);
        Assert.False(BuiltInCatalog.English.ContainsKey("wizard.semantics"));
        Assert.False(BuiltInCatalog.English.ContainsKey("wizard.rulesHelp"));
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
