using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// No native clipboard, window, profile, filesystem or network access.
public sealed class ClipboardMonitorPrivacyRegressionTests
{
    private const string First = "https://privacy-fixture.invalid/b/thread/1";
    private const string Second = "https://privacy-fixture.invalid/b/thread/2";
    private static readonly string[] Hosts = ["privacy-fixture.invalid"];

    [Fact]
    public async Task MixedPrivateAndUnsupportedInputOnlyDisclosesSupportedCandidateToClassifier()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        List<string> classified = new();
        string text = string.Join('\n',
            "synthetic private prose", "file:///synthetic/private.txt",
            "http://127.0.0.1/private", "http://[::1]/private",
            "http://192.168.1.1/private", "https://intranet.invalid/private",
            "https://user:synthetic@privacy-fixture.invalid/private", First);

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(text), Hosts, candidate =>
        {
            classified.Add(candidate);
            return Task.FromResult<BoardAdmissionTarget?>(null); // Provider denies the one eligible candidate.
        });

        Assert.Equal(new[] { First }, classified);
        Assert.Empty(monitor.Pending);
        Assert.Empty(monitor.TakePending());
    }

    [Fact]
    public async Task ReoptInDuringClassificationDropsWholeOldBatchAndAllowsFreshSameTextPoll()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        TaskCompletionSource<BoardAdmissionTarget?> classification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> classified = new();
        string text = First + "\n" + Second;
        Task poll = monitor.PollAsync(() => true, () => Task.FromResult<string?>(text), Hosts, candidate =>
        {
            classified.Add(candidate);
            return classification.Task;
        });
        Assert.Equal(new[] { First }, classified);
        monitor.SetEnabled(false);
        monitor.SetEnabled(true);
        // Re-opt-in does not start a second read while the old classifier is outstanding.
        await monitor.PollAsync(() => true,
            () => throw new InvalidOperationException("Overlapping read"), Hosts, Accept);
        classification.SetResult(new(First, false, false));
        await poll;

        Assert.Equal(new[] { First }, classified); // No second old-generation candidate.
        Assert.Empty(monitor.Pending);
        Assert.Empty(monitor.TakePending());
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(text), Hosts, Accept);
        Assert.Equal(new[] { First, Second }, monitor.Pending);
    }

    [Fact]
    public async Task NewUnconfirmedBatchCannotExposePartialResultsOrLeaveOldOfferActionable()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), Hosts, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        TaskCompletionSource<BoardAdmissionTarget?> classification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        Task poll = monitor.PollAsync(() => true, () =>
        {
            reads++;
            return Task.FromResult<string?>(First + "\n" + Second);
        }, Hosts, candidate => candidate == First ? Accept(candidate) : classification.Task);

        // First candidate has completed, second has not: neither partial nor old consent is available.
        Assert.Empty(monitor.Pending);
        classification.SetResult(new(Second, false, false));
        await poll;
        Assert.Equal(new[] { First, Second }, monitor.Pending);
        monitor.SetEnabled(false); // Opt-out before confirmation must revoke the entire offer.
        Assert.Empty(monitor.TakePending());
        Assert.Equal(1, reads);
    }

    private static Task<BoardAdmissionTarget?> Accept(string candidate) =>
        Task.FromResult<BoardAdmissionTarget?>(new(candidate, false, false));
}
