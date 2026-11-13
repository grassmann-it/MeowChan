using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>Undo toast, retry reasons, pause in the tray, status wording and toast durations of the Downloads shell.</summary>
[Collection("Localization")]
public sealed class DownloadsFeedbackTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    // ---- toast action -------------------------------------------------------------------------------------

    [Fact]
    public async Task ToastActionRunsOnceThenClosesTheToastAndKeepsItsLiveRegionAndName()
    {
        int runs = 0, closes = 0;
        FeedbackToastContent toast = new("Title", "Removed 2", null, _ => closes++, new FeedbackToastAction("Undo", () => { runs++; return Task.CompletedTask; }));
        Assert.NotNull(toast.ActionButton);
        Assert.Equal("FeedbackToastActionButton", toast.ActionButton!.Name);
        Assert.Equal("Undo", toast.ActionButton.Content);
        Assert.Equal("Undo", AutomationProperties.GetName(toast.ActionButton));
        // The behaviour of the earlier accessibility work stays: named card, polite live region for information.
        Assert.Equal(FeedbackToastContent.AccessibleName("Title", "Removed 2"), AutomationProperties.GetName(toast));
        Assert.Equal(AutomationLiveSetting.Polite, FeedbackToastContent.LiveSettingFor(null));
        Assert.Equal("FeedbackToastDismissButton", toast.DismissButton.Name);

        toast.ActionButton.Command!.Execute(null);
        await toast.RunActionAsync();
        await toast.RunActionAsync(); // A second activation must not run the action again.
        Assert.Equal(1, runs);
        Assert.Equal(1, closes);
        Assert.False(toast.ActionButton.IsEnabled);
    }

    [Fact]
    public async Task ToastActionThatThrowsStillClosesTheToast()
    {
        int closes = 0;
        FeedbackToastContent toast = new("T", "M", null, _ => closes++, new FeedbackToastAction("Go", () => throw new InvalidOperationException("boom")));
        await toast.RunActionAsync();
        Assert.Equal(1, closes);
    }

    [Fact]
    public void ToastWithoutActionHasNoActionButton()
    {
        FeedbackToastContent toast = new("T", "M", null, _ => { });
        Assert.Null(toast.ActionButton);
    }

    [Fact]
    public async Task UndoToastReadmitsTheRemovedAddressesOnceThroughTheAdmissionAndReportsTheOutcome()
    {
        List<string> submitted = new();
        WatchRemovalUndo undo = new(new[] { "https://fixture.invalid/a/thread/1", "https://fixture.invalid/a/thread/2", "https://fixture.invalid/a/thread/1", " " },
            text => { submitted.Add(text); return Task.FromResult<int?>(2); });
        Assert.Equal(2, undo.Count);
        int closes = 0;
        FeedbackToastContent toast = new(Texts.T("notification.session"), Texts.T("watch.removed", undo.Count), null, _ => closes++,
            new FeedbackToastAction(Texts.T("watch.undo"), () => undo.RunAsync()));
        await toast.RunActionAsync();
        string only = Assert.Single(submitted);
        Assert.Equal("https://fixture.invalid/a/thread/1" + Environment.NewLine + "https://fixture.invalid/a/thread/2", only);
        Assert.Equal(WatchUndoOutcome.Restored, await undo.RunAsync());
        Assert.Single(submitted);
        Assert.Equal(2, undo.Restored);
        Assert.Equal(1, closes);
    }

    [Theory]
    [InlineData(3, 3, WatchUndoOutcome.Restored)]
    [InlineData(1, 3, WatchUndoOutcome.Partial)]
    [InlineData(0, 3, WatchUndoOutcome.Partial)]
    public async Task UndoOutcomeFollowsHowManyAddressesAreWatchedAgain(int watching, int total, WatchUndoOutcome expected)
    {
        WatchRemovalUndo undo = new(Enumerable.Range(1, total).Select(i => "https://fixture.invalid/a/thread/" + i), _ => Task.FromResult<int?>(watching));
        Assert.Equal(expected, await undo.RunAsync());
        Assert.Equal(watching, undo.Restored);
    }

    [Fact]
    public async Task UndoThatCannotStartIsReportedAsUnavailableAndNothingIsClaimed()
    {
        WatchRemovalUndo busy = new(new[] { "https://fixture.invalid/a/thread/1" }, _ => Task.FromResult<int?>(null));
        Assert.Equal(WatchUndoOutcome.Unavailable, await busy.RunAsync());
        Assert.Equal(0, busy.Restored);
        WatchRemovalUndo empty = new(Array.Empty<string>(), _ => throw new InvalidOperationException("must not run"));
        Assert.Equal(WatchUndoOutcome.Unavailable, await empty.RunAsync());
    }

    [Theory]
    [InlineData("en", "Undo")]
    [InlineData("de", "Rückgängig")]
    public void UndoTextsAreLocalizedAndNamedWithoutPluralForms(string language, string undo)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            Assert.Equal(undo, Texts.T("watch.undo"));
            foreach (int count in new[] { 1, 2, 7 })
                Assert.Contains(count.ToString(), Texts.T("watch.removed", count));
            Assert.DoesNotContain("{", Texts.T("watch.undoPartial", 1, 2));
        }
        finally { Texts.SetLanguage(previous); }
    }

    // ---- toast duration (N-01) ----------------------------------------------------------------------------

    [Fact]
    public void ToastDurationsLetConfirmationsLeaveWarningsStayAndErrorsKeepTheDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), ToastDuration.For(NotificationType.Information, 20, false));
        Assert.Equal(TimeSpan.FromSeconds(3), ToastDuration.For(NotificationType.Success, 40, false));
        Assert.Equal(TimeSpan.FromSeconds(6), ToastDuration.For(NotificationType.Warning, 300, false));
        Assert.Null(ToastDuration.For(NotificationType.Error, 10, false));
        Assert.Equal(ToastDuration.Action, ToastDuration.For(NotificationType.Information, 10, true));
        Assert.True(ToastDuration.Action > ToastDuration.Warning);
    }

    [Fact]
    public void LongerInformationTextsGetReadingTimeUpToACap()
    {
        TimeSpan medium = ToastDuration.For(NotificationType.Information, 140, false)!.Value;
        Assert.True(medium > TimeSpan.FromSeconds(3));
        Assert.Equal(ToastDuration.Maximum, ToastDuration.For(NotificationType.Information, 5000, false));
    }

    // ---- retry reasons (D-03) -----------------------------------------------------------------------------

    [Fact]
    public void RetryNoneNamesTheRealReasonFromTheWorkSnapshot()
    {
        ThreadWorkSnapshot waiting = Work(WorkIntent.Watching, Media("m", MediaWorkStatus.Waiting));
        Assert.Equal(RetryNoneReason.Removed, RetryNoneClassifier.Classify(waiting, watchExists: false));
        Assert.Equal(RetryNoneReason.NothingMissing, RetryNoneClassifier.Classify(null, true));
        Assert.Equal(RetryNoneReason.NothingMissing, RetryNoneClassifier.Classify(Work(WorkIntent.Watching, Media("m", MediaWorkStatus.Succeeded)), true));
        Assert.Equal(RetryNoneReason.Stopped, RetryNoneClassifier.Classify(waiting with { Intent = WorkIntent.Suppressed }, true));
        Assert.Equal(RetryNoneReason.AllQueued, RetryNoneClassifier.Classify(
            Work(WorkIntent.Watching, Media("m", MediaWorkStatus.Waiting) with { ManualAttempt = true }), true));
        // Missing media that nobody has queued, yet the host declined: the watch is not active.
        Assert.Equal(RetryNoneReason.Unavailable, RetryNoneClassifier.Classify(waiting, true));
    }

    [Fact]
    public void RetryNoneCombinesTargetsToTheMostActionableReasonAndEveryReasonHasItsOwnText()
    {
        Assert.Equal(RetryNoneReason.Unavailable, RetryNoneClassifier.Combine(new[] { RetryNoneReason.NothingMissing, RetryNoneReason.Unavailable, RetryNoneReason.AllQueued }));
        Assert.Equal(RetryNoneReason.AllQueued, RetryNoneClassifier.Combine(new[] { RetryNoneReason.NothingMissing, RetryNoneReason.AllQueued }));
        Assert.Equal(RetryNoneReason.Removed, RetryNoneClassifier.Combine(Array.Empty<RetryNoneReason>()));
        string previous = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                string[] texts = Enum.GetValues<RetryNoneReason>().Select(reason => Texts.T(RetryNoneClassifier.KeyFor(reason))).ToArray();
                Assert.Equal(texts.Length, texts.Distinct().Count());
                Assert.All(texts, text => Assert.DoesNotContain(RetryNoneClassifier.KeyFor(RetryNoneReason.Removed), text));
            }
            Texts.SetLanguage("de");
            Assert.Equal("Nichts zu wiederholen: Alle fehlenden Medien sind schon eingereiht.", Texts.T(RetryNoneClassifier.KeyFor(RetryNoneReason.AllQueued)));
        }
        finally { Texts.SetLanguage(previous); }
    }

    // ---- status wording and next step (D-02, D-01) --------------------------------------------------------

    [Fact]
    public void StatusWordsNameWhatHappensAndTheInterventionRowKnowsItsNextStep()
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage("de");
            Assert.Equal("Restliche Medien werden geladen", Texts.T("work.runout"));
            Assert.Equal("Erneuter Versuch nötig", Texts.T("work.intervention"));
            Assert.Equal("Pausiert (global)", Texts.T("work.paused"));
            Texts.SetLanguage("en");
            Assert.Equal("Needs a retry", Texts.T("work.intervention"));

            ThreadWorkSnapshot needs = Work(WorkIntent.Watching, Media("m", MediaWorkStatus.NeedsIntervention));
            Assert.True(WorkStatusFormatter.Format(needs).NeedsIntervention);
            Assert.False(WorkStatusFormatter.Format(needs with { Intent = WorkIntent.Completed }).NeedsIntervention);
            Assert.False(WorkStatusFormatter.Format(Work(WorkIntent.Watching, Media("m", MediaWorkStatus.Waiting))).NeedsIntervention);
            using ThreadRow row = new(new DesktopThreadEntry(1, RowSnapshot(needs), "Fixture", "b"));
            Assert.True(row.NeedsIntervention);
            row.UpdateSnapshot(new DesktopThreadEntry(1, RowSnapshot(Work(WorkIntent.Watching, Media("m", MediaWorkStatus.Succeeded))), "Fixture", "b"));
            Assert.False(row.NeedsIntervention);

            Assert.Equal("Next step: Retry missing media (Shift+F5).", WorkNextStep.Text(CommandCatalog.Label("watch.retry-media"), "Shift+F5"));
            Assert.Equal("Next step: Retry missing media.", WorkNextStep.Text(CommandCatalog.Label("watch.retry-media"), null));
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void CompactStatusIsHiddenOnlyWhileItRepeatsTheVisibleStatusColumn()
    {
        ThreadSnapshot snapshot = RowSnapshot(Work(WorkIntent.Watching, Media("m", MediaWorkStatus.Succeeded))) with
        { Work = null, Activity = MeowChan.Interfaces.ThreadActivity.Pending };
        using ThreadRow row = new(new DesktopThreadEntry(1, snapshot, "Fixture", "b"));
        Assert.Equal(row.Status, row.CompactStatus);
        Assert.True(row.ShowCompactStatus); // Column hidden (Simple): the compact text is the only status.
        List<string> changed = new();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        row.StatusColumnVisible = true;
        Assert.False(row.ShowCompactStatus);
        Assert.Contains(nameof(ThreadRow.ShowCompactStatus), changed);
        row.SetPaused(true); // Pause adds something the column does not say.
        Assert.NotEqual(row.Status, row.CompactStatus);
        Assert.True(row.ShowCompactStatus);
        row.SetPaused(false);
        Assert.False(row.ShowCompactStatus);
        row.StatusColumnVisible = false;
        Assert.True(row.ShowCompactStatus);
    }

    // ---- search (D-07) ------------------------------------------------------------------------------------

    [Fact]
    public void SearchFindsRowsByStatusAndCompactStatus()
    {
        ThreadSnapshot snapshot = RowSnapshot(Work(WorkIntent.Watching, Media("m", MediaWorkStatus.Waiting))) with
        { Activity = MeowChan.Interfaces.ThreadActivity.Error };
        using ThreadRow row = new(new DesktopThreadEntry(1, snapshot, "Fixture", "b"));
        Assert.True(DownloadSearch.Matches(row, row.Status.Substring(0, 4).ToUpperInvariant()));
        Assert.True(DownloadSearch.Matches(row, row.ExceptionHint));
        Assert.True(new DownloadSearch.Query(row.CompactStatus).Matches(row));
        row.SetPaused(true);
        Assert.True(DownloadSearch.Matches(row, Texts.T("work.paused")));
        Assert.False(DownloadSearch.Matches(row, "zzz-no-such-status"));
    }

    [Fact]
    public void BoardSearchFindsTheBoardStatus()
    {
        using BoardRow board = new(new DesktopBoardEntry(2, new MeowChan.Interfaces.BoardSnapshot(new ResourceId("fixture", "sci"),
            "https://fixture.invalid/sci/", "Science", "/never/read", null, null), "Fixture", ""));
        Assert.NotEmpty(board.Status);
        Assert.True(DownloadSearch.Matches(board, board.Status));
        Assert.False(DownloadSearch.Matches(board, "zzz-no-such-status"));
    }

    // ---- tray (D-05) --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", false, "Pause downloads and checks", "pause", "MeowChan - Image Downloader")]
    [InlineData("en", true, "Resume downloads and checks", "play", "MeowChan · paused")]
    [InlineData("de", false, "Downloads und Prüfungen pausieren", "pause", "MeowChan - Image Downloader")]
    [InlineData("de", true, "Downloads und Prüfungen fortsetzen", "play", "MeowChan · pausiert")]
    public void TrayPauseEntryNamesTheActionItWillPerformAndTheTooltipSaysWhenPaused(string language, bool paused, string header, string icon, string tooltip)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            Assert.Equal(header, TrayMenuModel.PauseHeader(paused));
            Assert.Equal(header + "  Ctrl+Shift+P", TrayMenuModel.PauseHeader(paused, "Ctrl+Shift+P"));
            Assert.Equal(icon, TrayMenuModel.PauseIcon(paused));
            Assert.Equal(tooltip, TrayMenuModel.Tooltip(paused, activeDownloads: 0));
            if (paused) Assert.Equal(tooltip, TrayMenuModel.Tooltip(paused, 5));
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TrayTooltipCountsActiveDownloadsAndPauseIsOnlyOfferedWhenItCanRun()
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage("de");
            Assert.Equal("MeowChan · Downloads aktiv: 3", TrayMenuModel.Tooltip(false, 3));
            Texts.SetLanguage("en");
            Assert.Equal("MeowChan · Active downloads: 1", TrayMenuModel.Tooltip(false, 1));
        }
        finally { Texts.SetLanguage(previous); }
        Assert.True(TrayMenuModel.PauseEnabled(true, false, false));
        Assert.False(TrayMenuModel.PauseEnabled(false, false, false));
        Assert.False(TrayMenuModel.PauseEnabled(true, true, false));
        Assert.False(TrayMenuModel.PauseEnabled(true, false, true));
    }

    [Fact]
    public void PauseIsNoLongerPlannedForTheTray()
    {
        CommandDefinition pause = CommandCatalog.Get("session.pause");
        Assert.Contains("tray", pause.Contexts);
        Assert.DoesNotContain("tray:planned", pause.Contexts);
    }

    // ---- fixtures -----------------------------------------------------------------------------------------

    private static ThreadSnapshot RowSnapshot(ThreadWorkSnapshot work) => new(
        new ResourceId("fixture", "b", "1"), work.Url, "Fixture", "/fixture",
        MeowChan.Interfaces.ThreadActivity.Finished, MeowChan.Interfaces.ThreadSourceState.Active,
        null, 2, 2, new Dictionary<string, FailureInfo>(), null, new MeowChan.Downloader.DownloadCounts(0, 0)) { Work = work };

    private static ThreadWorkSnapshot Work(WorkIntent intent, MediaWork missing) => new(
        "https://fixture.test/b/thread/1", intent, ThreadSourceStateValue.Active, End, false,
        new[] { Media("saved", MediaWorkStatus.Succeeded), missing });

    private static MediaWork Media(string id, MediaWorkStatus status) => new()
    {
        ResourceUrl = "https://fixture.test/b/thread/1", Generation = 1, MediaIdentifier = id,
        DestinationPath = "/fixture/" + id, DownloadUrl = "https://fixture.test/" + id,
        OriginalFileName = id + ".jpg", Status = status, HttpStatus = 404, Attempts = 2,
        DueAt = status == MediaWorkStatus.Waiting ? End.AddMinutes(15) : null
    };
}
