using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Every "Export" entry (button, menu item, toolbar label) is written without a trailing ellipsis in all languages.
/// The tooltip says that the command opens the export dialog instead.
/// </summary>
[Collection("Localization")]
public sealed class ExportLabelPunctuationTests : IDisposable
{
    public ExportLabelPunctuationTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    /// <summary>Message keys of the export entries that open the export dialog or start an export.</summary>
    private static readonly string[] ExportEntryKeys =
    {
        "command.export.open-all-results-html-dialog", "command.export.open-html-dialog", "command.favorites.export-originals",
        "command.media.export-original", "command.gallery.export-original.compact", "urltext.export.open", "gallery.export",
        "viewer.favorites.exportShown", "export.resultsAction", "selection.export", "viewer.menu.exportOriginal",
        "media.selection.export", "media.exportAll"
    };

    /// <summary>Command ids that carry an export label. "export.associate" chooses a folder and is not an export entry.</summary>
    private static bool IsExportEntry(CommandDefinition command) =>
        command.Id.Contains("export", StringComparison.OrdinalIgnoreCase) && command.Id != "export.associate"
        && command.Id is not ("export.open-previous" or "export.find-legacy");

    public static TheoryData<string> AllLanguageCodes()
    {
        Texts.Initialize();
        TheoryData<string> data = new();
        foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes())) data.Add(code);
        return data;
    }

    private static bool EndsWithEllipsis(string text) =>
        text.TrimEnd().EndsWith('…') || text.TrimEnd().EndsWith("...", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(AllLanguageCodes))]
    public void ExportEntryLabelsHaveNoTrailingEllipsisInAnyLanguage(string code)
    {
        Texts.SetLanguage(code);
        Assert.Equal(code, Texts.Language);
        foreach (string key in ExportEntryKeys)
        {
            string text = Texts.T(key, 7);
            Assert.False(string.IsNullOrWhiteSpace(text), code + "/" + key + " is empty.");
            Assert.False(EndsWithEllipsis(text), code + "/" + key + " ends with an ellipsis: " + text);
        }
        foreach (CommandDefinition command in CommandCatalog.All.Where(IsExportEntry))
            Assert.False(EndsWithEllipsis(CommandCatalog.Label(command.Id)),
                code + "/" + command.Id + " label ends with an ellipsis: " + CommandCatalog.Label(command.Id));
    }

    [Fact]
    public void CatalogFallbackTitlesOfExportCommandsHaveNoTrailingEllipsis()
    {
        CommandDefinition[] exports = CommandCatalog.All.Where(IsExportEntry).ToArray();
        Assert.True(exports.Length >= 5, "The export command scan found too few commands.");
        foreach (CommandDefinition command in exports)
        {
            Assert.False(EndsWithEllipsis(command.LabelEn), command.Id + " English title: " + command.LabelEn);
            Assert.False(EndsWithEllipsis(command.LabelDe), command.Id + " German title: " + command.LabelDe);
        }
    }

    [Fact]
    public void ExportShortcutIsUnchanged()
    {
        Assert.Equal("Primary+E", CommandCatalog.Get("export.open-html-dialog").Shortcut);
        Assert.Equal("Primary+E", CommandCatalog.Get("media.export-original").Shortcut);
    }

    [Theory]
    [MemberData(nameof(AllLanguageCodes))]
    public void TooltipOfDialogOpeningExportCommandsNamesTheExportDialog(string code)
    {
        Texts.SetLanguage(code);
        string hint = Texts.T("command.hint.opens-export-dialog");
        Assert.False(string.IsNullOrWhiteSpace(hint));
        if (code != "en") Assert.NotEqual(Texts.T("command.hint.opens-export-dialog"), "command.hint.opens-export-dialog");
        foreach (string id in new[]
        {
            "export.open-html-dialog", "export.open-all-results-html-dialog", "export.open-urltext-dialog",
            "favorites.export-originals", "media.export-original"
        })
            Assert.Contains(hint, CommandBindings.Tooltip(id), StringComparison.Ordinal);
        Assert.DoesNotContain(hint, CommandBindings.Tooltip("export.associate"), StringComparison.Ordinal);
    }
}
