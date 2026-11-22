using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// I1b "Icons setzen": the tool buttons that were text alone carry a catalog icon (Assets/icons/feather/masks). Icon + text keeps the
// string as Content (the language refresh, smokes and the accessible name read it); icon-only buttons keep the text as tooltip and
// accessible name. Source contracts and plain Avalonia objects; no application, no GUI.
[Collection("Localization")]
public sealed class IconButtonConversionTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ---- the helpers ----------------------------------------------------------------------------------------

    [Fact]
    public void AnIconBeforeTextButtonKeepsItsStringContentAndRemembersItsIcon()
    {
        Button button = new() { Content = "Reset" };
        Assert.Null(IconButtons.KeyOf(button));
        Assert.Same(button, IconButtons.WithText(button, "rotate-ccw"));
        Assert.Equal("rotate-ccw", IconButtons.KeyOf(button));
        Assert.Equal("Reset", button.Content); // the template draws the icon; the label is still the string
        Assert.NotNull(button.ContentTemplate);
        object? template = button.ContentTemplate;
        button.Content = "Zurücksetzen"; // a language change sets the label again
        IconButtons.WithText(button, "rotate-ccw");
        Assert.Same(template, button.ContentTemplate);
        Assert.Equal("Zurücksetzen", button.Content);
    }

    [Fact]
    public void AnIconOnlyButtonKeepsTheTextAsTooltipAndAccessibleName()
    {
        Button button = new();
        IconButtons.Apply(button, "chevron-right", "Next page");
        Assert.Equal("chevron-right", IconButtons.KeyOf(button));
        Assert.Contains("icon-only", button.Classes);
        Assert.Equal("Next page", ToolTip.GetTip(button));
        Assert.Equal("Next page", AutomationProperties.GetName(button));
        IconButtons.Apply(button, "chevron-right", "Nächste Seite"); // renaming only
        Assert.Equal("Nächste Seite", ToolTip.GetTip(button));
        Assert.Equal("Nächste Seite", AutomationProperties.GetName(button));
        Assert.Single(button.Classes, "icon-only");
    }

    [Fact]
    public void ToastActionsWithAnIconDrawItBeforeTheLabelAndKeepTheLabelAsNameAndContent()
    {
        FeedbackToastContent toast = new("Title", "Message", null, _ => { }, new[]
        {
            new FeedbackToastAction("Undo", () => Task.CompletedTask, "rotate-ccw"),
            new FeedbackToastAction("Details", () => Task.CompletedTask, "info"),
            new FeedbackToastAction("Later", () => Task.CompletedTask)
        });
        Assert.Equal(3, toast.ActionButtons.Count);
        Assert.Equal("rotate-ccw", IconButtons.KeyOf(toast.ActionButtons[0]));
        Assert.Equal("info", IconButtons.KeyOf(toast.ActionButtons[1]));
        Assert.Null(IconButtons.KeyOf(toast.ActionButtons[2])); // no icon asked for: stays text
        foreach ((Button button, string label) in toast.ActionButtons.Zip(new[] { "Undo", "Details", "Later" }))
        {
            Assert.Equal(label, button.Content);
            Assert.Equal(label, AutomationProperties.GetName(button));
        }
    }

    [Fact]
    public void EveryWorkViewAndWatchToastActionThatIsAToolNamesAnIcon()
    {
        string notice = ReadSource("Desktop/Shell/MainWindow.WorkViewNotice.cs");
        Assert.Contains("RequestWorkViewOverviewAsync(notice), \"info\")", notice);
        Assert.Contains("undoNotice: notice), \"rotate-ccw\")", notice);
        Assert.Contains("UndoWatchRemovalAsync(undo), \"rotate-ccw\")", ReadSource("Desktop/Shell/MainWindow.Selection.cs"));
    }

    // ---- the Archive page -----------------------------------------------------------------------------------

    [Fact]
    public void TheArchivePageToolButtonsExistInItsLayoutAndEachNamesACatalogIcon()
    {
        XElement page = XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Root!;
        Dictionary<string, string> kinds = page.Descendants()
            .Where(element => element.Attribute(X + "Name") != null)
            .GroupBy(element => (string)element.Attribute(X + "Name")!).ToDictionary(group => group.Key, group => group.First().Name.LocalName);
        Assert.NotEmpty(ArchivePage.ToolButtonIcons);
        foreach ((string name, string icon) in ArchivePage.ToolButtonIcons)
        {
            Assert.True(kinds.TryGetValue(name, out string? kind), name + " is not in ArchivePage.axaml");
            Assert.Contains(kind, new[] { "Button", "ToggleButton" });
            AssertIconExists(icon);
        }
        Assert.Equal(new[] { "ArchiveAddBoard", "ArchiveAddSource", "ArchiveEditRatingButton", "ArchiveEmptySearchInMedia", "ArchiveFavoritesFilter",
            "ArchiveFilterApply", "ArchiveFilterClose", "ArchiveLayoutList", "ArchiveLayoutTable", "ArchiveManageRatingsButton",
            "ArchiveMediaLayoutTable", "ArchiveMediaLayoutTiles",
            "ArchiveMediaSelectionClear", "ArchiveMediaSelectionExport", "ArchiveMediaSelectionGallery", "ArchiveMediaSelectionQueue",
            "ArchiveMediaSelectionTag", "ArchiveResetFilters", "ArchiveRetrySearch", "ArchiveRetrySuggestions", "ArchiveTagManage",
            "ArchiveUseDeviceZone", "PreviewArchiveMediaButton", "ReaderFindRetry", "ReaderFindSearch", "ReaderTagAdd", "ReaderTagManage" },
            ArchivePage.ToolButtonIcons.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("InitializeToolButtonIcons();", ReadSource("Desktop/Views/Archive/ArchivePage.axaml.cs"));
        Assert.Contains("EnsureIconBeforeText(Control<Button>(name), icon)", ReadSource("Desktop/Views/Archive/ArchivePage.ToolIcons.cs"));
    }

    // ---- Settings -------------------------------------------------------------------------------------------

    [Fact]
    public void EverySectionResetOfTheSettingsPageCarriesTheRotateIcon()
    {
        XElement settings = XDocument.Load(Root("Desktop/Views/Settings/SettingsEditor.axaml")).Root!;
        HashSet<string> buttons = settings.Descendants(A + "Button").Select(element => (string?)element.Attribute(X + "Name")).OfType<string>().ToHashSet();
        Assert.Equal(7, SettingsEditor.SectionResetButtonNames.Length);
        foreach (string name in SettingsEditor.SectionResetButtonNames) Assert.Contains(name, buttons);
        // Every "Reset <section>" button of the page is in the list, none is left out.
        Assert.Equal(buttons.Where(name => name.StartsWith("Reset", StringComparison.Ordinal) && name.EndsWith("Button", StringComparison.Ordinal) && name != "ResetAllButton").Order().ToArray(),
            SettingsEditor.SectionResetButtonNames.Order().ToArray());
        Assert.Contains("EnsureIconBeforeText(button, \"rotate-ccw\")", ReadSource("Desktop/Views/Settings/SettingsEditor.Reset.cs"));
        Assert.Contains("InitializeResetIcons();", ReadSource("Desktop/Views/Settings/SettingsEditor.axaml.cs"));
    }

    // ---- converted sources ----------------------------------------------------------------------------------

    // Each converted button: the file that builds it and the call that gives it its icon. A button that is converted must not
    // lose the call (the call is what the icon is made of), and the icon it names must exist.
    public static TheoryData<string, string, string> ConvertedButtons() => new()
    {
        { "Desktop/Views/Settings/FolderSourcesPanel.cs", "EnsureIconBeforeText(_add, \"folder\")", "folder" },
        { "Desktop/Views/Booru/BooruWorkbenchView.cs", "EnsureIconBeforeText(_submit, \"search\")", "search" },
        { "Desktop/Views/Booru/BooruWorkbenchView.cs", "EnsureIconBeforeText(_createWatcher, \"plus\")", "plus" },
        { "Desktop/Views/Booru/BooruWorkbenchView.cs", "IconButtons.Apply(_previous, \"chevron-left\", Texts.T(\"booru.ui.page.previous\"))", "chevron-left" },
        { "Desktop/Views/Booru/BooruWorkbenchView.cs", "IconButtons.Apply(_next, \"chevron-right\", Texts.T(\"booru.ui.page.next\"))", "chevron-right" },
        { "Desktop/Views/Booru/BooruWorkbenchView.cs", "ShowIconBeforeText(open, \"external-link\")", "external-link" },
        { "Desktop/Views/Booru/BooruWorkbenchView.cs", "ShowIconBeforeText(copy, \"copy\")", "copy" },
        { "Desktop/Views/Booru/BooruWorkbenchView.cs", "ShowIconBeforeText(load, cancel ? CardMenuIcons.Cancel : retry ? CardMenuIcons.Retry : CardMenuIcons.Load)", "download-cloud" },
        { "Desktop/Views/Media/BooruMediaDetailsDialog.cs", "ShowIconBeforeText(open, \"external-link\")", "external-link" },
        { "Desktop/Views/Media/BooruMediaDetailsDialog.cs", "ShowIconBeforeText(copy, \"copy\")", "copy" },
        { "Desktop/GalleryViewer.cs", "\"GalleryActual\" => Texts.T(\"gallery.actual\")", "crosshair" },
        { "Desktop/GalleryViewer.cs", "CommandCatalog.Get(\"gallery.show-in-thread\").Icon", "file-text" },
        { "Desktop/ArchiveCleanupDialog.cs", "WithText(_refresh, \"refresh-cw\")", "refresh-cw" },
        { "Desktop/ArchiveCleanupDialog.cs", "WithText(_selectShown, \"check-square\")", "check-square" },
        { "Desktop/ArchiveCleanupDialog.cs", "WithText(_clear, \"x\")", "x" },
        { "Desktop/Views/Archive/ArchivePage.Export.Dialog.cs", "WithText(browse, \"folder\")", "folder" },
        { "Desktop/Views/Archive/ArchivePage.Export.Dialog.cs", "WithText(open, \"folder\")", "folder" },
        { "Desktop/Views/Archive/ArchivePage.Favorites.cs", "WithText(view, \"eye\")", "eye" },
        { "Desktop/Views/Archive/ArchivePage.Favorites.cs", "WithText(export, \"download\")", "download" },
        { "Desktop/Views/Archive/ArchivePage.Favorites.cs", "CreateButton(\"export.browse-parent\", () => { }, iconWithLabel: true)", "folder" },
        { "Desktop/Views/Archive/ArchivePage.Favorites.cs", "CreateButton(\"location.open-folder\", () => { }, iconWithLabel: true)", "folder" },
        { "Desktop/Views/Archive/NativeTransferImportDialog.cs", "WithText(_choose, \"folder\")", "folder" },
        { "Desktop/Views/Archive/NativeTransferImportDialog.cs", "WithText(_recheck, \"refresh-cw\")", "refresh-cw" },
        { "Desktop/Views/Archive/NativeTransferImportDialog.cs", "WithText(_conflicts, \"sliders\")", "sliders" },
        { "Desktop/Views/Archive/NativeTransferPreviewDialog.cs", "WithText(_choose, \"folder\")", "folder" },
        { "Desktop/Views/Archive/MediaConflictsDialog.cs", "WithText(keep, \"check\")", "check" },
        { "Desktop/Views/Archive/MediaConflictsDialog.cs", "WithText(promote, \"check\")", "check" },
        { "Desktop/Views/Archive/FilenameSearchDialog.cs", "WithText(_search, \"search\")", "search" },
        { "Desktop/Views/Archive/FilenameSearchDialog.cs", "WithText(_cancel, \"x\")", "x" },
        { "Desktop/Views/Archive/FilenameSearchDialog.cs", "WithText(_previous, \"chevron-left\")", "chevron-left" },
        { "Desktop/Views/Archive/FilenameSearchDialog.cs", "WithText(_next, \"chevron-right\")", "chevron-right" },
        { "Desktop/Views/Archive/FilenameSearchDialog.cs", "WithText(_select, \"corner-up-right\")", "corner-up-right" },
        { "Desktop/Views/Archive/MediaHashSearchDialog.cs", "WithText(_search, \"search\")", "search" },
        { "Desktop/Views/Archive/MediaHashSearchDialog.cs", "WithText(_previous, \"chevron-left\")", "chevron-left" },
        { "Desktop/Views/Archive/MediaHashSearchDialog.cs", "WithText(_next, \"chevron-right\")", "chevron-right" },
        { "Desktop/Views/Collections/PostBookmarksDialog.axaml.cs", "WithText(B(\"OpenTarget\"), \"external-link\")", "external-link" },
        { "Desktop/Views/Collections/PostBookmarksDialog.axaml.cs", "WithText(B(\"Reload\"), \"refresh-cw\")", "refresh-cw" },
        { "Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml.cs", "WithText(Button(\"SavedSearchRename\"), \"edit-2\")", "edit-2" },
        { "Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml.cs", "WithText(Button(\"SavedSearchReplace\"), \"search\")", "search" },
        { "Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml.cs", "WithText(Button(\"SavedSearchReload\"), \"refresh-cw\")", "refresh-cw" },
        { "Desktop/Views/Collections/TagManagerDialog.cs", "WithText(_rename, \"edit-2\")", "edit-2" },
        { "Desktop/Views/Collections/MediaTagDialog.cs", "WithText(_add, \"plus\")", "plus" },
    };

    [Theory, MemberData(nameof(ConvertedButtons))]
    public void AConvertedToolButtonKeepsItsIconCallAndTheIconExists(string file, string call, string icon)
    {
        Assert.Contains(call, ReadSource(file), StringComparison.Ordinal);
        AssertIconExists(icon);
    }

    // Destructive and main actions stay text: the two are not given an icon by the same calls.
    [Fact]
    public void MainAndDestructiveDialogActionsStayText()
    {
        string cleanup = ReadSource("Desktop/ArchiveCleanupDialog.cs");
        Assert.DoesNotContain("WithText(_review", cleanup);
        Assert.DoesNotContain("WithText(_confirm", cleanup);
        Assert.DoesNotContain("WithText(discard", ReadSource("Desktop/Views/Archive/MediaConflictsDialog.cs"));
        Assert.DoesNotContain("WithText(_delete", ReadSource("Desktop/Views/Collections/TagManagerDialog.cs"));
    }

    // Every literal icon a product file hands to the icon-before-text / icon-only helpers is a file of the mask catalog.
    [Fact]
    public void EveryIconNamedInTheProductSourcesExistsInTheMaskCatalog()
    {
        Regex call = new("(?:WithText\\([^;]*?|EnsureIconBeforeText\\([^,;]+,\\s*|ShowIconBeforeText\\([^,;]+,\\s*|IconButtons\\.Apply\\([^,;]+,\\s*)\"([a-z0-9-]+)\"", RegexOptions.CultureInvariant);
        int seen = 0;
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            foreach (Match match in call.Matches(File.ReadAllText(file)))
            {
                seen++;
                AssertIconExists(match.Groups[1].Value, Path.GetFileName(file));
            }
        Assert.True(seen > 40, "The scan found only " + seen + " icon calls.");
    }

    // ---- arrows in the language packs -----------------------------------------------------------------------

    private static readonly string[] ArrowButtonKeys =
    [
        "reader.previousPost", "reader.nextPost", "reader.previousThread", "reader.nextThread", "gallery.viewer1", "gallery.viewer2"
    ];

    // The two other texts that start with an arrow are not buttons: the Settings search key hint and the header's down/up byte rates.
    private static readonly string[] ArrowTextsThatAreNotButtons = ["settings.search.keys", "stats.header.netValues"];

    private const string Arrows = "←→↑↓⟵⟶⇐⇒⬅➡⬆⬇↩↪↔↕↖↗↘↙⇦⇨⇧⇩";

    public static TheoryData<string> Languages()
    {
        TheoryData<string> data = new() { "en", "de" };
        foreach (string code in Texts.BundledPackCodes()) data.Add(code);
        return data;
    }

    [Theory, MemberData(nameof(Languages))]
    public void NoButtonTextStartsOrEndsWithAnArrowCharacter(string language)
    {
        IReadOnlyDictionary<string, string> strings = language switch
        {
            "en" => BuiltInCatalog.English,
            "de" => BuiltInCatalog.German,
            _ => ReadBundledStrings(language)
        };
        foreach (string key in ArrowButtonKeys)
        {
            Assert.True(strings.TryGetValue(key, out string? text), language + ": missing " + key);
            Assert.False(string.IsNullOrWhiteSpace(text), language + ": empty " + key);
            AssertNoEdgeArrow(language, key, text!);
        }
        // Nothing else drifted back: a text with an arrow at its edge must be one of the two known non-button texts.
        foreach ((string key, string text) in strings)
            if (!ArrowTextsThatAreNotButtons.Contains(key)) AssertNoEdgeArrow(language, key, text);
    }

    [Fact]
    public void TheArrowTextsAreRealTranslationsOfThePreviousAndNextLabels()
    {
        // The arrows are gone, the meaning stays: each language still names previous/next post, thread and image, and the two directions differ.
        foreach (string code in Texts.BundledPackCodes())
        {
            IReadOnlyDictionary<string, string> strings = ReadBundledStrings(code);
            foreach ((string previous, string next) in new[] { ("reader.previousPost", "reader.nextPost"), ("reader.previousThread", "reader.nextThread"), ("gallery.viewer1", "gallery.viewer2") })
                Assert.NotEqual(strings[previous], strings[next]);
            Assert.NotEqual(BuiltInCatalog.English["reader.previousPost"], strings["reader.previousPost"]);
        }
        Assert.Equal("Previous post", BuiltInCatalog.English["reader.previousPost"]);
        Assert.Equal("Next thread", BuiltInCatalog.English["reader.nextThread"]);
        Assert.Equal("Voriger Thread", BuiltInCatalog.German["reader.previousThread"]);
        Assert.Equal("Nächstes Medium", BuiltInCatalog.German["gallery.viewer2"]);
    }

    private static void AssertNoEdgeArrow(string language, string key, string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        Assert.False(Arrows.Contains(trimmed[0]) || Arrows.Contains(trimmed[^1]), language + ": " + key + " starts or ends with an arrow: " + text);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static void AssertIconExists(string icon, string? where = null) =>
        Assert.True(File.Exists(Root("Assets/icons/feather/masks/" + icon + ".png")), "No mask icon '" + icon + "' in the catalog" + (where == null ? "" : " (" + where + ")"));

    private static IReadOnlyDictionary<string, string> ReadBundledStrings(string language)
    {
        string resource = Texts.BundledPackResourcePrefix + language + ".json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing embedded pack: " + resource);
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

    private static string ReadSource(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
