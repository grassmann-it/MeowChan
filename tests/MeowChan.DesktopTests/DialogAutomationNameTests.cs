using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// UI catalog C10: accessible names in the six dialogs that had at most two (StartupErrorWindow, ShortcutsWindow,
/// ExperimentalActivationDialog, InterruptedTransfersDialog, MediaOccurrencesWindow, MediaConflictsDialog). A Window needs a
/// platform backend, so the wiring is pinned in the sources and the shown dialogs stay DesktopSmoke's dialog-frame runs; the
/// row names are pure and checked directly. Live status lines keep their live setting and the name of their own text.
/// </summary>
[Collection("Localization")]
public sealed class DialogAutomationNameTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public DialogAutomationNameTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Fact]
    public void TheStartupErrorNamesItsFailureRegionAfterTheTitleAndItsExitButton()
    {
        string source = Source("Desktop/StartupErrorWindow.cs");
        int apply = source.IndexOf("private void ApplyTexts()", StringComparison.Ordinal);
        Assert.True(apply > 0);
        string texts = source[apply..];
        Assert.Contains("AutomationProperties.SetName(_errorPanel, Title);", texts);
        Assert.Contains("AutomationProperties.SetName(_exitButton, Texts.T(\"action.exit\"));", texts);
        // The failure text itself is not renamed: a screen reader reads the message.
        Assert.DoesNotContain("SetName(detail", source);
        Assert.Contains("DialogWindow.Configure(this, DialogSize.S, modal: false);", source);
    }

    [Fact]
    public void TheShortcutsWindowBodyCarriesTheTitleAndRefreshesItWithTheLanguage()
    {
        string source = Source("Desktop/Views/Shortcuts/ShortcutsWindow.cs");
        Assert.Contains("AutomationProperties.SetName(Editor, Title);", source);
        Assert.Contains("private void OnLanguageChanged(object? sender, EventArgs e) => ApplyTexts();", source);
        Assert.True(source.IndexOf("ApplyTexts();", StringComparison.Ordinal) < source.IndexOf("DialogWindow.Frame(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheActivationDialogNamesCancelAndConfirmAfterTheirVisibleWordsAndKeepsItsLiveRegions()
    {
        string code = Source("Desktop/Views/Admission/ExperimentalActivationDialog.axaml.cs");
        Assert.Contains("AutomationProperties.SetName(C<Button>(\"ActivationCancel\"), cancel);", code);
        Assert.Contains("AutomationProperties.SetName(C<Button>(\"ActivationConfirm\"), confirm);", code);
        Assert.DoesNotContain("SetName(C<TextBlock>(\"ActivationStatus\")", code);
        string xaml = Source("Desktop/Views/Admission/ExperimentalActivationDialog.axaml");
        Assert.Matches("x:Name=\"ActivationStatus\"[^>]*AutomationProperties.LiveSetting=\"Assertive\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
    }

    [Fact]
    public void InterruptedTransfersNameTheListItsEntriesAndBothActions()
    {
        string source = Source("Desktop/Views/Archive/InterruptedTransfersDialog.cs");
        foreach (string wiring in new[]
        {
            "AutomationProperties.SetName(_discard, (string)_discard.Content);",
            "AutomationProperties.SetName(_keep, (string)_keep.Content);",
            "AutomationProperties.SetName(_rows, Texts.T(\"transfer.interrupted.list\"));",
            "AutomationProperties.SetName(row, row.Text);",
            "AutomationProperties.SetLiveSetting(_discard, AutomationLiveSetting.Polite);",
        })
            Assert.Contains(wiring, source);
        // Names are set where the words are rendered, so the confirmation and a language change rename both buttons.
        int render = source.IndexOf("private void Render()", StringComparison.Ordinal);
        Assert.True(source.IndexOf("SetName(_discard", StringComparison.Ordinal) > render);
        Assert.Equal("Interrupted operations", Texts.T("transfer.interrupted.list"));
        Texts.SetLanguage("de");
        Assert.Equal("Unterbrochene Operationen", Texts.T("transfer.interrupted.list"));
    }

    [Fact]
    public void EveryBundledPackTranslatesTheNewListName()
    {
        foreach (string code in Texts.BundledPackCodes())
        {
            Texts.SetLanguage(code);
            Assert.NotEqual("Interrupted operations", Texts.T("transfer.interrupted.list"));
        }
    }

    [Fact]
    public void OccurrenceRowsNameTheirShowActionAfterTheOccurrenceAndTheListAfterTheFile()
    {
        Assert.Equal("Show in thread, /b/ on 4chan · Cats", MediaOccurrencesWindow.ShowName("Show in thread", "/b/ on 4chan · Cats"));
        Assert.Equal("Show in thread", MediaOccurrencesWindow.ShowName("Show in thread", null));
        string source = Source("Desktop/Views/Media/MediaOccurrencesWindow.cs");
        Assert.Contains("AutomationProperties.SetName(show, ShowName((string)show.Content, where.Text));", source);
        Assert.Contains("AutomationProperties.SetName(rows, Title);", source);
        // The row's own description stays its name.
        Assert.Contains("AutomationProperties.SetName(row, description);", source);
    }

    [Fact]
    public void ConflictRowActionsSayWhichAlternativeTheyDecideAndTheFooterActionsFollowTheirWords()
    {
        MediaConflictRow row = new("id", new("4chan", "g", "100"), new string('d', 64), "0123456789abcdef" + new string('c', 48),
            10, 20, MediaConflictUserStatus.Open);
        Assert.Equal("Keep alternative, 4chan /g/ 100 (0123456789ab…)", MediaConflictsDialog.RowActionName("Keep alternative", row));
        string source = Source("Desktop/Views/Archive/MediaConflictsDialog.cs");
        foreach (string wiring in new[]
        {
            "AutomationProperties.SetName(action, RowActionName((string)action.Content!, row));",
            "foreach (Button action in new[] { keep, promote, discard })",
            "AutomationProperties.SetName(_reclaim, (string)_reclaim.Content);",
            "AutomationProperties.SetName(_close, (string)_close.Content);",
            "AutomationProperties.SetName(_more, (string)_more.Content);",
            "AutomationProperties.SetName(_rows, Title);",
            "AutomationProperties.SetName(text, text.Text);",
            "AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);",
        })
            Assert.Contains(wiring, source);
        // The polite status line keeps the name of its own text.
        Assert.DoesNotContain("SetName(_status", source);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path)));
}
