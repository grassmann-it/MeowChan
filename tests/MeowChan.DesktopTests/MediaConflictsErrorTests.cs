using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Regression for the erased decision failure: the list reloads after every decision, and that reload
// used to clear the status, so a rejected Keep/Promote/Discard disappeared without any explanation.
public sealed class MediaConflictsErrorTests : IDisposable
{
    private readonly string _language = Texts.Language;

    public MediaConflictsErrorTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Fact]
    public void RefusedDecisionSurvivesTheReloadThatFollowsIt()
    {
        MediaConflictStatusBoard board = new();
        board.ListRead(2);
        Assert.True(board.Display.IsEmpty);

        board.DecisionFailed(MediaConflictStatus.Failure("transfer.conflicts.error.action"));
        board.ListRead(2); // the reload after the decision
        Assert.Equal(MediaConflictStatusTone.Failure, board.Display.Tone);
        Assert.Equal(Texts.T("transfer.conflicts.error.action"), board.Display.Resolve());

        // Paging appends rows; it is still only a read and must not erase the failure either.
        board.ListRead(3);
        Assert.Equal(Texts.T("transfer.conflicts.error.action"), board.Display.Resolve());
    }

    [Fact]
    public void RecordedDecisionClearsItsOwnOutcomeButNotAnUnrelatedReadFailure()
    {
        MediaConflictStatusBoard board = new();
        board.DecisionFailed(MediaConflictStatus.Failure("transfer.conflicts.error.action"));
        board.DecisionRecorded();
        board.ListRead(1);
        Assert.True(board.Display.IsEmpty);

        board.ListReadFailed();
        Assert.Equal(Texts.T("transfer.conflicts.error.read"), board.Display.Resolve());
        board.DecisionRecorded(); // a successful decision is not a read result
        Assert.Equal(Texts.T("transfer.conflicts.error.read"), board.Display.Resolve());
        board.ListRead(1);
        Assert.True(board.Display.IsEmpty);
    }

    [Fact]
    public void PromoteUnavailableKeepsItsSpecificExplanation()
    {
        MediaConflictStatusBoard board = new();
        board.DecisionFailed(MediaConflictStatus.Failure("transfer.conflicts.error.promoteUnavailable"));
        board.ListRead(1);
        Assert.Equal(Texts.T("transfer.conflicts.error.promoteUnavailable"), board.Display.Resolve());
        Assert.Equal(MediaConflictStatusTone.Failure, board.Display.Tone);
    }

    [Fact]
    public void EmptyListIsOnlyShownWhenNothingElseIsPending()
    {
        MediaConflictStatusBoard board = new();
        board.ListRead(0);
        Assert.Equal(Texts.T("transfer.conflicts.empty"), board.Display.Resolve());
        Assert.Equal(MediaConflictStatusTone.Information, board.Display.Tone);

        board.ListReadFailed();
        Assert.Equal(Texts.T("transfer.conflicts.error.read"), board.Display.Resolve());
        board.ListRead(0);
        Assert.Equal(Texts.T("transfer.conflicts.empty"), board.Display.Resolve());
    }

    [Fact]
    public void ReclaimOutcomeReplacesThePreviousActionOutcome()
    {
        MediaConflictStatusBoard board = new();
        board.Reclaim(MediaConflictStatus.Information("transfer.conflicts.reclaim.none"));
        Assert.Equal(Texts.T("transfer.conflicts.reclaim.none"), board.Display.Resolve());

        board.Reclaim(MediaConflictStatus.Failure("transfer.conflicts.reclaim.error"));
        board.ListRead(4);
        Assert.Equal(Texts.T("transfer.conflicts.reclaim.error"), board.Display.Resolve());

        board.Reclaim(MediaConflictStatus.Information("transfer.conflicts.reclaim.done", 4242));
        Assert.Contains("4,242", board.Display.Resolve());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void PendingOutcomeIsRenderedInTheCurrentLanguage(string language)
    {
        Texts.SetLanguage(language);
        MediaConflictStatusBoard board = new();
        board.DecisionFailed(MediaConflictStatus.Failure("transfer.conflicts.error.action"));
        board.ListRead(1);
        // The stored key must resolve through the active pack, not a captured English string.
        string resolved = board.Display.Resolve();
        Assert.NotEqual("transfer.conflicts.error.action", resolved);
        Assert.NotEmpty(resolved);
    }
}
