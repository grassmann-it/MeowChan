using Avalonia.Automation;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>WV12 action lifetime, not a GUI smoke. All time and view changes are explicit; no sleeps or real profiles.</summary>
public sealed class WorkViewSwitchNoticeTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        internal DateTimeOffset WallTime = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => WallTime;
        internal void Advance(TimeSpan time) => _ticks += time.Ticks;
    }

    private static WorkViewSwitchNoticeState.Notice Show(WorkViewSwitchNoticeState state,
        WorkView from = WorkView.Simple, WorkView to = WorkView.Barebones, int seconds = 8) =>
        Assert.IsType<WorkViewSwitchNoticeState.Notice>(state.Applied(from, to, seconds, informationAllowed: true));

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(60)]
    public void ActionsLiveForExactlyTheConfiguredMonotonicDuration(int seconds)
    {
        Clock clock = new();
        WorkViewSwitchNoticeState state = new(clock);
        var notice = Show(state, seconds: seconds);
        Assert.Equal(TimeSpan.FromSeconds(seconds), notice.Duration);
        Assert.True(state.IsCurrent(notice, WorkView.Barebones));
        clock.WallTime = DateTimeOffset.MaxValue; // Wall-clock adjustments must not expire or prolong Undo.
        clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
        Assert.True(state.IsCurrent(notice, WorkView.Barebones));
        clock.WallTime = DateTimeOffset.MinValue;
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(state.IsCurrent(notice, WorkView.Barebones));
        Assert.Null(state.BeginUndo(notice, WorkView.Barebones));
        Assert.Null(state.TakeDetails(notice, WorkView.Barebones));
    }

    [Fact]
    public void ZeroAndInformationOffReallyOfferNoActionsAndRevokeEarlierOccurrences()
    {
        WorkViewSwitchNoticeState state = new();
        var notice = Show(state);
        Assert.Null(state.Applied(WorkView.Barebones, WorkView.Simple, 0, true));
        Assert.Null(state.BeginUndo(notice, WorkView.Barebones));
        Assert.Null(state.Applied(WorkView.Simple, WorkView.Advanced, 8, false));
        Assert.NotNull(state.Applied(WorkView.Advanced, WorkView.Barebones, 8, true));
    }

    [Fact]
    public void InitialViewAndNoOpChangesNeverProduceAToast()
    {
        WorkViewState views = new();
        WorkViewSwitchNoticeState notices = new();
        List<WorkViewSwitchNoticeState.Notice> shown = [];
        views.Changed += (from, to) =>
        {
            if (notices.Applied(from, to, 8, true) is { } notice) shown.Add(notice);
        };
        views.Set(WorkView.Simple);
        views.Set(WorkView.Simple);
        Assert.Empty(shown);
        views.Set(WorkView.Advanced);
        var shownOnce = Assert.Single(shown);
        Assert.Equal((WorkView.Simple, WorkView.Advanced), (shownOnce.Diff.From, shownOnce.Diff.To));
        Assert.Null(notices.Applied(WorkView.Advanced, WorkView.Advanced, 8, true));
        Assert.True(notices.IsCurrent(shownOnce, WorkView.Advanced));
    }

    [Fact]
    public void RapidRoundTripNeverMakesTheFirstUndoValidAgainEvenAtTheSameDestination()
    {
        WorkViewSwitchNoticeState state = new();
        var first = Show(state);
        var second = Show(state, WorkView.Barebones, WorkView.Advanced);
        var third = Show(state, WorkView.Advanced, WorkView.Barebones);
        Assert.Null(state.BeginUndo(first, WorkView.Barebones));
        Assert.Null(state.TakeDetails(second, WorkView.Advanced));
        // A late expiry/close for A cannot revoke C.
        state.Dismiss(first);
        Assert.True(state.IsCurrent(third, WorkView.Barebones));
        var undo = Assert.IsType<WorkViewSwitchNoticeState.UndoRequest>(state.BeginUndo(third, WorkView.Barebones));
        Assert.Equal((WorkView.Barebones, WorkView.Advanced), (undo.From, undo.To));
        Assert.Null(state.BeginUndo(third, WorkView.Barebones));
    }

    [Fact]
    public void NewRequestMakesOldActionsStaleBeforePersistenceCompletesOrFails()
    {
        WorkViewSwitchNoticeState state = new();
        var old = Show(state);
        state.BeginSwitch(); // Accepted request, persistence pending; no applied event yet.
        Assert.Null(state.BeginUndo(old, WorkView.Barebones));
        Assert.Null(state.TakeDetails(old, WorkView.Barebones));
        // A failed/aborted save publishes no Applied event and therefore no replacement toast.
        Assert.False(state.IsCurrent(old, WorkView.Barebones));
        var next = Show(state, WorkView.Barebones, WorkView.Simple);
        Assert.True(state.IsCurrent(next, WorkView.Simple));
    }

    [Theory]
    [InlineData("dismiss")]
    [InlineData("cancel")]
    [InlineData("close")]
    public void DismissalCancellationAndWindowCloseRevokeBothActions(string kind)
    {
        WorkViewSwitchNoticeState state = new();
        var notice = Show(state);
        switch (kind)
        {
            case "dismiss": state.Dismiss(notice); break;
            case "cancel": state.Cancel(); break;
            case "close": state.Close(); break;
        }
        Assert.Null(state.BeginUndo(notice, WorkView.Barebones));
        Assert.Null(state.TakeDetails(notice, WorkView.Barebones));
        // A cancelled close attempt can keep the window alive; only actual Closed is terminal.
        if (kind == "close") Assert.Null(state.Applied(WorkView.Barebones, WorkView.Simple, 8, true));
        else Assert.NotNull(state.Applied(WorkView.Barebones, WorkView.Simple, 8, true));
    }

    [Fact]
    public void UndoSuppressesOnlyItsOwnedAppliedTransition()
    {
        WorkViewSwitchNoticeState state = new();
        var notice = Show(state);
        var request = Assert.IsType<WorkViewSwitchNoticeState.UndoRequest>(state.BeginUndo(notice, WorkView.Barebones));
        Assert.Null(state.Applied(request.From, request.To, 8, true));
        state.EndUndo(request);
        var laterUserChange = Show(state, WorkView.Simple, WorkView.Advanced);
        Assert.True(state.IsCurrent(laterUserChange, WorkView.Advanced));
    }

    [Fact]
    public void UnrelatedChangeDuringUndoIsNotSuppressedAndLateCompletionCannotHideIt()
    {
        WorkViewSwitchNoticeState state = new();
        var notice = Show(state);
        var request = Assert.IsType<WorkViewSwitchNoticeState.UndoRequest>(state.BeginUndo(notice, WorkView.Barebones));
        var unrelated = Show(state, WorkView.Barebones, WorkView.Advanced);
        state.EndUndo(request);
        state.Dismiss(notice);
        Assert.True(state.IsCurrent(unrelated, WorkView.Advanced));
    }

    [Fact]
    public void ANewNormalRequestRevokesUndoSuppressionEvenForTheSamePair()
    {
        WorkViewSwitchNoticeState state = new();
        var notice = Show(state);
        var request = Assert.IsType<WorkViewSwitchNoticeState.UndoRequest>(state.BeginUndo(notice, WorkView.Barebones));
        state.BeginSwitch();
        var normal = Show(state, request.From, request.To);
        state.EndUndo(request);
        Assert.True(state.IsCurrent(normal, request.To));
    }

    [Fact]
    public void FailedUndoClearsSuppressionAndCannotBeRetriedFromTheOldToast()
    {
        WorkViewSwitchNoticeState state = new();
        var notice = Show(state);
        var request = Assert.IsType<WorkViewSwitchNoticeState.UndoRequest>(state.BeginUndo(notice, WorkView.Barebones));
        state.EndUndo(request); // Save failed; no Applied event.
        Assert.Null(state.BeginUndo(notice, WorkView.Barebones));
        Assert.NotNull(state.Applied(request.From, request.To, 8, true));
    }

    [Fact]
    public void DetailsTakesTheExactCapturedDiffOnceAndExcludesUndo()
    {
        WorkViewSwitchNoticeState state = new();
        var notice = Show(state);
        Assert.Null(state.TakeDetails(notice, WorkView.Advanced)); // Wrong live view cannot consume it.
        var details = Assert.IsType<DiffSummary>(state.TakeDetails(notice, WorkView.Barebones));
        Assert.Same(notice.Diff, details);
        Assert.NotEmpty(details.Pages);
        Assert.Equal(WorkViewPolicy.Diff(WorkView.Simple, WorkView.Barebones).Changes.Select(c => c.Entry.Id),
            details.Changes.Select(c => c.Entry.Id));
        Assert.Null(state.BeginUndo(notice, WorkView.Barebones));
        Assert.Null(state.TakeDetails(notice, WorkView.Barebones));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ToastExposesTwoAccessibleActionsAndRunsOnlyOne(int action)
    {
        WorkViewSwitchNoticeState state = new();
        var notice = Show(state);
        int undoRuns = 0, detailRuns = 0, closes = 0;
        FeedbackToastContent toast = new("Changed", "Defaults differ", null, _ => { state.Dismiss(notice); closes++; },
        [
            new FeedbackToastAction("Undo", () =>
            {
                if (state.BeginUndo(notice, WorkView.Barebones) is { } request)
                {
                    undoRuns++;
                    Assert.Null(state.Applied(request.From, request.To, 8, true));
                    state.EndUndo(request);
                }
                return Task.CompletedTask;
            }),
            new FeedbackToastAction("Details", () =>
            {
                if (state.TakeDetails(notice, WorkView.Barebones) != null) detailRuns++;
                return Task.CompletedTask;
            })
        ]);
        Assert.Equal(new[] { "Undo", "Details" }, toast.ActionButtons.Select(button => (string)button.Content!));
        Assert.All(toast.ActionButtons, button => Assert.Equal((string)button.Content!, AutomationProperties.GetName(button)));
        Assert.Equal(AutomationLiveSetting.Polite, FeedbackToastContent.LiveSettingFor(null));
        await toast.RunActionAsync(action);
        await toast.RunActionAsync(1 - action);
        Assert.Equal(action == 0 ? 1 : 0, undoRuns);
        Assert.Equal(action == 1 ? 1 : 0, detailRuns);
        Assert.Equal(1, closes);
        Assert.All(toast.ActionButtons, button => Assert.False(button.IsEnabled));
    }

    [Fact]
    public async Task CapturedOldButtonAndExplicitCloseCannotExecuteStaleActions()
    {
        WorkViewSwitchNoticeState state = new();
        var old = Show(state);
        int undos = 0;
        FeedbackToastContent toast = new("Changed", "A", null, _ => state.Dismiss(old),
            [new FeedbackToastAction("Undo", () => { if (state.BeginUndo(old, WorkView.Barebones) != null) undos++; return Task.CompletedTask; })]);
        toast.DismissButton.Command!.Execute(null);
        var current = Show(state, WorkView.Advanced, WorkView.Barebones);
        await toast.RunActionAsync();
        Assert.Equal(0, undos);
        Assert.True(state.IsCurrent(current, WorkView.Barebones));
    }
}
