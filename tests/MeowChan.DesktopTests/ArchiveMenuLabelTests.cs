using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// AR-02 of the final review (17.11.2026): the Archive thread menu names no target on a single thread (the right-clicked row is already highlighted),
/// names a count in one grammar when several are selected, and its German labels fit the menu's label column instead of being cut by the shared
/// layout rule (ellipsis plus the full text as the tip).
/// </summary>
[Collection("Localization")]
public sealed class ArchiveMenuLabelTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public ArchiveMenuLabelTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    // The commands of the Archive thread menu (ArchivePage.Commands.cs): the ones that act on the lead thread only (the favourite entry apart, which shows
    // the state's own action), and the ones that take a count.
    private static readonly string[] LeadCommands =
    {
        "thread.read", "gallery.open", "thread.details", "thread.show-in-media", "thread.tags",
        "location.open-folder", "location.open-source", "rules.edit-scope"
    };

    private static readonly string[] BatchCommands =
    {
        "thread.show-in-media", "export.open-html-dialog", "selection.copy-links", "watch.check", "watch.retry-media", "watch.remove",
        "archive.delete-permanently"
    };

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void AThreadSelectedAloneNeverGetsASuffix(string language)
    {
        Texts.SetLanguage(language);
        foreach (string id in LeadCommands.Concat(BatchCommands))
            foreach (int targets in new[] { 0, 1 })
            {
                string plain = CommandCatalog.Label(id);
                Assert.Equal(plain, ArchiveMenuLabels.Action(plain, selected: 1, targets));
                Assert.DoesNotContain("·", ArchiveMenuLabels.Action(plain, selected: 1, targets));
            }
        // A lead action has no count to name, even with several threads selected.
        Assert.Equal("Show details", ArchiveMenuLabels.Action("Show details", selected: 3, targets: null));
    }

    [Fact]
    public void SeveralThreadsUseTheOneCountGrammarInEnglishAndGerman()
    {
        Assert.Equal("Export (3 threads)", ArchiveMenuLabels.Action("Export", selected: 3, targets: 3));
        Assert.Equal("Check now (1 thread)", ArchiveMenuLabels.Action("Check now", selected: 2, targets: 1));
        Assert.Equal("Check now (0 threads)", ArchiveMenuLabels.Action("Check now", selected: 2, targets: 0));
        Texts.SetLanguage("de");
        Assert.Equal("Exportieren (3 Threads)", ArchiveMenuLabels.Action("Exportieren", selected: 3, targets: 3));
        Assert.Equal("Jetzt prüfen (1 Thread)", ArchiveMenuLabels.Action("Jetzt prüfen", selected: 2, targets: 1));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("pl")]
    public void EveryLanguageUsesTheParenthesisGrammarAndTheLeadTargetTextIsGone(string language)
    {
        Texts.SetLanguage(language);
        foreach (int count in new[] { 0, 1, 2, 5, 12 })
        {
            string label = ArchiveMenuLabels.Action("X", selected: 2, targets: count);
            Assert.StartsWith("X (", label);
            Assert.EndsWith(")", label);
            Assert.Contains(count.ToString(), label);
        }
        // The key is not in any catalog any more: the lookup gives the key back.
        Assert.Equal("archive.command.leadTarget", Texts.T("archive.command.leadTarget"));
    }

    // The label column of the menu at its widest (416 dip less the frame, icon column and the widest shortcut, "Ctrl+Shift+O") holds about 34 characters
    // of the menu font (the catalog screenshots cut "Fehlende Medien erneut versuche" at 31 characters plus the ellipsis). Staying inside it keeps the
    // shared layout rule's ellipsis out of the German menu; a longer wording belongs in the tip, not in the label.
    private const int GermanLabelColumn = 34;

    [Fact]
    public void GermanThreadMenuLabelsFitTheLabelColumnForAnyCountUpToNinetyNine()
    {
        Texts.SetLanguage("de");
        foreach (string id in LeadCommands)
        {
            string label = CommandCatalog.Label(id);
            Assert.True(label.Length <= GermanLabelColumn, id + ": " + label);
        }
        foreach (string id in BatchCommands)
        {
            string label = ArchiveMenuLabels.Action(CommandCatalog.Label(id), selected: 99, targets: 99);
            Assert.True(label.Length <= GermanLabelColumn, id + ": " + label);
        }
        Assert.True(Texts.T("archive.command.unavailable").Length <= GermanLabelColumn, Texts.T("archive.command.unavailable"));
        // The favourite entry shows the state's own action (the catalog label names both states and is not what the menu shows).
        foreach (bool marked in new[] { false, true })
            Assert.True(ThreadFavoriteText.Action(marked).Length <= GermanLabelColumn, ThreadFavoriteText.Action(marked));
    }
}
