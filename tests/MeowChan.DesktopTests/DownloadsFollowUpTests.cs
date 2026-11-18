using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Final UX review of 1.1.0, Downloads: the row of a new watch is revealed (DL-02), the undo of a removal does not depend on the popup
/// settings (DL-03), the rejection banner and the results can be dismissed (DL-04), and the open search bar has one cross only (DL-07).
/// </summary>
[Collection("Localization")]
public sealed class DownloadsFollowUpTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // ---- DL-02: reveal of the new row ---------------------------------------------------------------------

    [Fact]
    public void RevealWaitsUntilTheRowsHaveTheWatchThenShowsItOnce()
    {
        AdmissionReveal reveal = new();
        Assert.Equal(AdmissionRevealStep.Idle, reveal.Next(0, true, false, false, false, false));
        reveal.Request(42, nowMilliseconds: 1000, windowMilliseconds: 8000);
        // The projection is still being applied, then the frame is complete but the row is not in it yet.
        Assert.Equal(AdmissionRevealStep.Wait, reveal.Next(1100, true, rowsApplying: true, threadRow: false, boardRow: false, visible: false));
        Assert.Equal(AdmissionRevealStep.Wait, reveal.Next(1200, true, false, threadRow: false, boardRow: false, visible: false));
        Assert.Equal(42, reveal.WatchId);
        Assert.Equal(AdmissionRevealStep.ShowThread, reveal.Next(1300, true, false, threadRow: true, boardRow: false, visible: true));
        // Shown once: a later frame never moves the selection again.
        Assert.Null(reveal.WatchId);
        Assert.Equal(AdmissionRevealStep.Idle, reveal.Next(1400, true, false, true, false, true));
    }

    [Fact]
    public void ABoardRowIsShownInTheBoardsList()
    {
        AdmissionReveal reveal = new();
        reveal.Request(7, 0, 8000);
        Assert.Equal(AdmissionRevealStep.ShowBoard, reveal.Next(10, true, false, threadRow: false, boardRow: true, visible: true));
    }

    [Fact]
    public void ARowTheSearchFilterHidesEndsTheRequestWithoutChangingTheSelection()
    {
        AdmissionReveal reveal = new();
        reveal.Request(7, 0, 8000);
        Assert.Equal(AdmissionRevealStep.Drop, reveal.Next(10, true, false, threadRow: true, boardRow: false, visible: false));
        Assert.Null(reveal.WatchId);
    }

    [Theory]
    [InlineData(true, 9001)]   // too late: the user has long moved on
    [InlineData(false, 100)]   // the page was left (reader, gallery, another page)
    public void ALateRequestOrALeftPageIsDroppedEvenWhenTheRowExists(bool pageReady, long now)
    {
        AdmissionReveal reveal = new();
        reveal.Request(7, 0, 8999);
        Assert.Equal(AdmissionRevealStep.Drop, reveal.Next(now, pageReady, false, threadRow: true, boardRow: false, visible: true));
        Assert.Null(reveal.WatchId);
    }

    [Fact]
    public void AnAddWithoutANewWatchAndANewerRequestReplaceThePendingOne()
    {
        AdmissionReveal reveal = new();
        reveal.Request(1, 0, 8000);
        reveal.Request(null, 10, 8000); // the next Add created nothing (all duplicates or rejected)
        Assert.Null(reveal.WatchId);
        reveal.Request(1, 20, 8000);
        reveal.Request(2, 30, 8000);
        Assert.Equal(2, reveal.WatchId);
        reveal.Cancel();
        Assert.Equal(AdmissionRevealStep.Idle, reveal.Next(40, true, false, true, false, true));
    }

    // ---- DL-03: the undo of a removal is not information ---------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AnEssentialActionToastIsShownWhateverThePopupSettingsSayAndOthersFollowThem(bool informationOn, bool ordinaryShown)
    {
        Config config = new() { ShowInformationNotifications = informationOn, ShowWarningNotifications = false, ShowErrorNotifications = false };
        Assert.True(SessionFeedbackStore.ShowsPopup(config, FeedbackCategory.Information, essential: true));
        Assert.Equal(ordinaryShown, SessionFeedbackStore.ShowsPopup(config, FeedbackCategory.Information, essential: false));
        // An error that the user switched off stays off unless it carries an action the user needs.
        Assert.False(SessionFeedbackStore.ShowsPopup(config, FeedbackCategory.Error, essential: false));
    }

    [Fact]
    public async Task OneUndoRestoresEverySelectedWatchAndCannotRunTwice()
    {
        List<string> submitted = new();
        string[] removed = Enumerable.Range(1, 5).Select(i => "https://fixture.invalid/a/thread/" + i).ToArray();
        WatchRemovalUndo undo = new(removed, text => { submitted.Add(text); return Task.FromResult<int?>(5); });
        Task<WatchUndoOutcome> first = undo.RunAsync();
        Task<WatchUndoOutcome> second = undo.RunAsync();
        Assert.Equal(WatchUndoOutcome.Restored, await first);
        Assert.Equal(WatchUndoOutcome.Restored, await second);
        string only = Assert.Single(submitted);
        Assert.Equal(removed, only.Split(Environment.NewLine));
        Assert.Equal(5, undo.Count);
    }

    // ---- DL-04: dismiss ------------------------------------------------------------------------------------

    [Fact]
    public void TheBannerAndTheResultsEachHaveADismissButtonInTheirOwnSurface()
    {
        XDocument window = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        XElement Named(string name) => window.Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);
        XElement repairClose = Named("AdmissionRepairDismiss");
        Assert.Equal("OnAdmissionRepairDismiss", (string?)repairClose.Attribute("Click"));
        Assert.Contains(repairClose.Ancestors(), element => (string?)element.Attribute(X + "Name") == "AdmissionRepairPanel");
        XElement resultsClose = Named("AdmissionResultsDismiss");
        Assert.Equal("OnAdmissionResultsDismiss", (string?)resultsClose.Attribute("Click"));
        // In the expander's header, so it stays reachable while the report is collapsed.
        Assert.Contains(resultsClose.Ancestors(), element => element.Name.LocalName == "Expander.Header");
        Assert.Contains(resultsClose.Ancestors(), element => (string?)element.Attribute(X + "Name") == "AdmissionResultsExpander");
        Assert.All(new[] { repairClose, resultsClose }, button => Assert.Contains("icon-square", (string?)button.Attribute("Classes")));
    }

    [Fact]
    public void TheDismissLabelIsTheExistingFeedbackDismissText()
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            Assert.Equal("Dismiss message", Texts.T("feedback.dismiss"));
        }
        finally { Texts.SetLanguage(previous); }
    }

    // ---- DL-07: one cross in the open search bar -------------------------------------------------------------

    [Theory]
    [InlineData("en", "Close")]
    [InlineData("de", "Schließen")]
    public void TheCloseOfTheSearchBarIsALabelledChevronButtonNotACross(string language, string label)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            Button close = new();
            DownloadSearchCloseButton.Apply(close);
            Assert.Equal(label, close.Content);
            Assert.Null(WorkspaceIcons.KeyOf(close.Content)); // no drawn cross content
            Assert.DoesNotContain(WorkspaceIcons.SquareClass, close.Classes);
            Assert.Equal("chevron-up", IconButtons.KeyOf(close));
            Assert.Equal(label, AutomationProperties.GetName(close));
            string tip = Assert.IsType<string>(ToolTip.GetTip(close));
            Assert.Equal(Texts.T("downloads.searchCloseTip"), tip);
            Assert.Contains("Esc", tip);
            // Calling it again (language change) only renames it.
            DownloadSearchCloseButton.Apply(close);
            Assert.Equal(label, close.Content);
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TheSearchFieldKeepsTheClearCrossAndTheBarsCloseIsDeclaredInTheEndSlot()
    {
        XDocument window = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        XElement close = window.Descendants().Single(element => (string?)element.Attribute(X + "Name") == "DownloadSearchClose");
        Assert.Equal("True", close.Attributes().Single(attribute => attribute.Name.LocalName == "HeaderBar.IsEnd").Value);
        Assert.DoesNotContain("icon-square", (string?)close.Attribute("Classes") ?? string.Empty);
        // The field's own clear button is the only cross of the row.
        string searchBox = File.ReadAllText(Root("Desktop/Controls/SearchBox.cs"));
        Assert.Contains("SearchBoxClear", searchBox);
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
