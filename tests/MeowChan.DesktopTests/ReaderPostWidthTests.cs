using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Reader post width ("fit to content" by default, or full width): the Config property and its persistence, the Settings
// row, the five-language texts and the source contract that ties the card cap to one number. The measured card widths,
// the live switch and the scroll cost are the reader-post-width smoke.
[Collection("Localization")]
public sealed class ReaderPostWidthTests : IDisposable
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace Desktop = "clr-namespace:MeowChan.Desktop";
    private static readonly string[] Keys = { "reader.postWidth", "reader.postWidth.hint", "reader.postWidth.fit", "reader.postWidth.full", "reader.postWidth.column" };

    public ReaderPostWidthTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void DefaultIsFitToContentAndAnOldProfileKeepsIt()
    {
        Assert.Equal("Fit", new Config().ReaderPostWidth);
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal("Fit", loaded.ReaderPostWidth);
        Assert.Equal(120, loaded.RecheckTimer);
    }

    [Theory]
    [InlineData("Full", "Full")]
    [InlineData(" full ", "Full")]
    [InlineData("Column", "Column")]
    [InlineData(" column ", "Column")]
    [InlineData("Fit", "Fit")]
    [InlineData("wide", "Fit")]
    [InlineData("", "Fit")]
    [InlineData(null, "Fit")]
    public void OnlyTheThreeValuesAreKept(string? value, string expected)
    {
        Config config = new() { ReaderPostWidth = value! };
        Assert.Equal(expected, config.ReaderPostWidth);
    }

    [Fact]
    public void FullWidthRoundTripsAndMalformedValuesFallBackToFit()
    {
        using TempDirectory directory = new();
        Config config = Config.LoadConfig(directory.Path);
        config.ReaderPostWidth = "Full";
        config.SaveConfig();
        Assert.Equal("Full", Config.LoadConfig(directory.Path).ReaderPostWidth);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"RecheckTimer\":120,\"ReaderPostWidth\":{}}");
        Config malformed = Config.LoadConfig(directory.Path);
        Assert.Null(malformed.LoadWarning);
        Assert.Equal("Fit", malformed.ReaderPostWidth);
        Assert.Equal(120, malformed.RecheckTimer);
    }

    [Fact]
    public void CloneCopyAndSettingsDraftCarryThePostWidth()
    {
        Config current = new();
        Config draft = current.Clone();
        Assert.Equal("Fit", draft.ReaderPostWidth);
        draft.ReaderPostWidth = "Full";
        Assert.Equal("Full", draft.Clone().ReaderPostWidth);
        current.CopySettingsFieldsFrom(draft);
        Assert.Equal("Full", current.ReaderPostWidth);
        Config other = new();
        other.CopyFrom(current);
        Assert.Equal("Full", other.ReaderPostWidth);
        draft.ReaderPostWidth = "Fit";
        current.CopySettingsFieldsFrom(draft);
        Assert.Equal("Fit", current.ReaderPostWidth);
    }

    [Fact]
    public void ReaderUsesOneCapForTheStyleAndTheLayoutConstant()
    {
        Assert.Equal(1120, ReaderNavigationLayout.CardMaxWidth);
        XElement posts = Assert.Single(Source("Desktop/Views/Archive/ArchivePage.axaml").Descendants(Desktop + "ReaderPostList")
            .Where(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ArchivePosts"));
        Assert.Contains("reader-fit-width", ((string?)posts.Attribute("Classes"))!.Split(' '));
        XElement style = Assert.Single(posts.Element(A + "ListBox.Styles")!.Elements(A + "Style")
            .Where(element => (string?)element.Attribute("Selector") == "ListBox.reader-fit-width ListBoxItem"));
        string? Setting(string property) => (string?)Assert.Single(style.Elements(A + "Setter")
            .Where(element => (string?)element.Attribute("Property") == property)).Attribute("Value");
        Assert.Equal("Left", Setting("HorizontalAlignment"));
        Assert.Equal(ReaderNavigationLayout.CardMaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), Setting("MaxWidth"));
        // The centred column: the same cap, no alignment setter (cards stay stretched, so the capped column is centred).
        XElement column = Assert.Single(posts.Element(A + "ListBox.Styles")!.Elements(A + "Style")
            .Where(element => (string?)element.Attribute("Selector") == "ListBox.reader-column-width ListBoxItem"));
        XElement cap = Assert.Single(column.Elements(A + "Setter"));
        Assert.Equal("MaxWidth", (string?)cap.Attribute("Property"));
        Assert.Equal(ReaderNavigationLayout.CardMaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)cap.Attribute("Value"));
    }

    [Fact]
    public void TheSettingSitsOnTheReadingPageNextToTheFontSize()
    {
        XElement page = Assert.Single(Source("Desktop/Views/Settings/SettingsEditor.axaml").Descendants(A + "StackPanel")
            .Where(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SettingsReadingMediaPage"));
        string[] names = page.Descendants().Select(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .Where(name => name != null).Select(name => name!).ToArray();
        Assert.True(Array.IndexOf(names, "ReaderPostWidthInput") > Array.IndexOf(names, "ReadingFontSizeInput"));
        Assert.True(Array.IndexOf(names, "ReaderPostWidthInput") < Array.IndexOf(names, "GalleryPlaybackModeInput"));
        Assert.Equal(1, names.Count(name => name == "ReaderPostWidthInput"));
    }

    [Theory]
    [InlineData("en", "Fit to content", "Full width", "Centered column")]
    [InlineData("de", "An Inhalt anpassen", "Volle Breite", "Zentrierte Spalte")]
    public void ChoicesAndLabelsAreLocalizedAndKeepTheSelectionWhenTheLanguageChanges(string language, string fit, string full, string column)
    {
        SettingsEditor editor = CreateEditor();
        Texts.SetLanguage(language);
        typeof(SettingsEditor).GetMethod("RefreshChoiceLabels", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
        Assert.Equal(new[] { "Fit", "Full", "Column" }, editor.ReaderPostWidthChoices.Select(choice => choice.Value));
        Assert.Equal(new[] { fit, full, column }, editor.ReaderPostWidthChoices.Select(choice => choice.Label));
        // The semantic draft value survives the label refresh (the editor was opened on "Full").
        Assert.Equal("Full", editor.SelectedReaderPostWidth!.Value);
        Assert.Equal(full, editor.SelectedReaderPostWidth.Label);
        Assert.Equal(Texts.T("reader.postWidth"), editor.ReaderPostWidthLabel);
        Assert.Equal(Texts.T("reader.postWidth.hint"), editor.ReaderPostWidthHint);
    }

    [Fact]
    public void EveryLanguageHasTheFourTextsAndTheKeysAreUnique()
    {
        foreach (string key in Keys)
        {
            Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? english) && english.Length > 0, "English " + key);
            Assert.True(BuiltInCatalog.German.TryGetValue(key, out string? german) && german != english, "German " + key);
        }
        foreach (string language in new[] { "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            foreach (string key in Keys)
                Assert.True(Texts.T(key) != key && (language == "de" || Texts.T(key) != BuiltInCatalog.English[key]), $"{language} lacks {key}");
        }
        foreach (string pack in new[] { "es", "fr", "ru" })
        {
            string text = File.ReadAllText(PackPath(pack));
            foreach (string key in Keys)
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\":").Count);
        }
    }

    private static SettingsEditor CreateEditor()
    {
        // CLR-only, as the other Settings tests: no InitializeComponent, dispatcher or profile IO.
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        Set(editor, "_config", new Config());
        Set(editor, "_selectedTheme", new SettingsChoice("Dark", "old theme label"));
        Set(editor, "_selectedWorkspaceMode", new SettingsChoice("Advanced", "old mode label"));
        Set(editor, "_selectedReadingFontSize", new SettingsChoice("Large", "old font label"));
        Set(editor, "_selectedReaderPostWidth", new SettingsChoice("Full", "old width label"));
        Set(editor, "_selectedGalleryPlaybackMode", new SettingsChoice("Slideshow", "old gallery label"));
        Set(editor, "_selectedLanguage", new LanguageInfo("en", "old language label"));
        return editor;
    }

    private static void Set(SettingsEditor editor, string fieldName, object? value) =>
        typeof(SettingsEditor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);

    private static XElement Source(string relative, [CallerFilePath] string sourcePath = "") =>
        XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative))).Root!;

    private static string PackPath(string pack, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", "Desktop/Localization/Packs", pack + ".json"));
}
