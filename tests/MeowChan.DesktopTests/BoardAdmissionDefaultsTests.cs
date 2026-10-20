using MeowChan.Admission;

namespace MeowChan.DesktopTests;

public sealed class BoardAdmissionDefaultsTests
{
    [Fact]
    public void DirectMonitoringClearsOldTextFiltersButPreservesIndividualChoicesAndRemovalSuppression()
    {
        const string board = "https://boards.4chan.org/g/";
        const string excluded = board + "thread/2";
        BoardAdmissionPolicy saved = new([new(ThreadTextField.Title, ThreadTextAction.Include, "cat")],
            new Dictionary<string, ThreadAdmissionException> { [excluded] = ThreadAdmissionException.Exclude });
        var direct = MainWindow.DirectBoardAdmissions([board, board], _ => saved);
        var policy = Assert.Single(direct.Policies).Value;
        Assert.True(direct.DirectFuture);
        Assert.Empty(policy.Rules);
        Assert.True(policy.Evaluate(new("1", board + "thread/1", "dog", "")).Included);
        Assert.False(policy.Evaluate(new("2", excluded, "cat", "")).Included);
        Assert.False(policy.Evaluate(new("3", board + "thread/3", "cat", "", Suppressed: true)).Included);
        Assert.Single(saved.Rules);
    }
}
