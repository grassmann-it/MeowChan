using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ClipboardUrlMonitorTests
{
    private const string Url = "https://source.invalid/b/thread/123";
    private static readonly string[] Hosts = ["source.invalid"];
    private static Task<BoardAdmissionTarget?> Classify(string url) => Task.FromResult<BoardAdmissionTarget?>(new(url, false, false));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrNotReadyNeverReadsClipboard(bool enabled, bool ready)
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(enabled);
        await monitor.PollAsync(() => ready, () => throw new Exception("Must not read"), Hosts,
            _ => throw new Exception("Must not classify"));
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task SupportedBatchIsPassiveDeduplicatedAndConfirmationUsesTheFrozenList()
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        int reads = 0;
        Task<string?> Read() { reads++; return Task.FromResult<string?>(Url + "," + Url + "\nhttps://source.invalid/b/"); }
        await monitor.PollAsync(() => true, Read, Hosts, Classify);
        Assert.Equal(new[] { Url, "https://source.invalid/b/" }, monitor.Pending);
        var taken = monitor.TakePending();
        Assert.Equal(2, taken.Count); Assert.Empty(monitor.Pending); Assert.Equal(1, reads);
        await monitor.PollAsync(() => true, Read, Hosts, _ => throw new Exception("Unchanged text must not reclassify"));
        Assert.Empty(monitor.Pending);
    }

    [Theory]
    [InlineData("secret non-URL text")]
    [InlineData("file:///home/user/secret.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://source.invalid.evil/b/thread/123")]
    [InlineData("https://evil.invalid/source.invalid/b/thread/123")]
    [InlineData("https://user:password@source.invalid/b/thread/123")]
    public async Task UnrelatedOrCredentialContentNeverReachesAProvider(string text)
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(text), Hosts,
            _ => throw new Exception("Must not classify"));
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task AlreadyWatchedAndRejectedTargetsAreNotOffered()
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Hosts,
            url => Task.FromResult<BoardAdmissionTarget?>(new(url, false, true)));
        Assert.Empty(monitor.Pending);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url + "4"), Hosts,
            _ => Task.FromResult<BoardAdmissionTarget?>(null));
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task DismissedSupportedBatchDoesNotNagWhenSurroundingTextChanges()
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Hosts, Classify);
        monitor.Dismiss();
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>("Copied: " + Url), Hosts, Classify);
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task NewClipboardReplacesOldPendingAndUnknownTextClearsIt()
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Hosts, Classify);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url + "4"), Hosts, Classify);
        Assert.Equal(Url + "4", Assert.Single(monitor.Pending));
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>("not a URL"), Hosts, Classify);
        Assert.Empty(monitor.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrReenabledDuringAReadRejectsItsLateCompletion(bool reenable)
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        TaskCompletionSource<string?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = monitor.PollAsync(() => true, () => read.Task, Hosts, _ => throw new Exception("Stale classify"));
        monitor.SetEnabled(false);
        if (reenable) monitor.SetEnabled(true);
        read.SetResult(Url); await poll;
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task DisabledDuringClassificationRejectsLateResult()
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        TaskCompletionSource<BoardAdmissionTarget?> classification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Hosts, _ => classification.Task);
        monitor.SetEnabled(false); classification.SetResult(new(Url, false, false)); await poll;
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task OverlappingPollsNeverReadTwiceAndUnavailableReadCanRecover()
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        TaskCompletionSource<string?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var poll = monitor.PollAsync(() => true, () => read.Task, Hosts, Classify);
        await monitor.PollAsync(() => true, () => throw new Exception("Overlapping read"), Hosts, Classify);
        read.SetException(new IOException("Private platform read unavailable"));
        await Assert.ThrowsAsync<IOException>(() => poll);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Hosts, Classify);
        Assert.Equal(Url, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task NewlyActiveSourceRechecksPreviouslyUnsupportedClipboard()
    {
        var monitor = new ClipboardUrlMonitor(); monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Array.Empty<string>(), Classify);
        Assert.Empty(monitor.Pending);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Hosts, Classify);
        Assert.Equal(Url, Assert.Single(monitor.Pending));
    }

    [Fact]
    public void ExtractionIsBoundedAndDoesNotMutateOrPersistContent()
    {
        Assert.Empty(ClipboardUrlMonitor.ExtractCandidates(new('x', ClipboardUrlMonitor.MaximumTextLength + 1), Hosts));
        string batch = string.Join('\n', Enumerable.Range(0, 100).Select(i => Url + i));
        Assert.Equal(ClipboardUrlMonitor.MaximumUrls, ClipboardUrlMonitor.ExtractCandidates(batch, Hosts).Count);
        Assert.Equal(Url, Assert.Single(ClipboardUrlMonitor.ExtractCandidates("Text (<" + Url + ">).", Hosts)));
    }
}
