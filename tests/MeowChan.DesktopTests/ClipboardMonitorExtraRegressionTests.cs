using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ClipboardMonitorExtraRegressionTests
{
    private const string Url = "https://source.invalid/b/thread/123";
    private static readonly string[] Hosts = ["source.invalid"];

    private static Task<BoardAdmissionTarget?> Classify(string url)
    {
        return Task.FromResult<BoardAdmissionTarget?>(new(url, IsBoard: false, AlreadyWatching: false));
    }

    [Theory]
    [InlineData("https", "https://source.invalid/B/thread/123?x=1%20y#frag")]
    [InlineData("http", "http://source.invalid/B/thread/123?x=1%20y#frag")]
    public void ExtractionCanonicalizesExactProviderHostAndPreservesRequestTarget(string scheme, string expected)
    {
        string text = scheme.ToUpperInvariant() + "://SOURCE.INVALID/B/thread/123?x=1%20y#frag";

        string candidate = Assert.Single(ClipboardUrlMonitor.ExtractCandidates(text, ["SOURCE.invalid"]));

        Assert.Equal(expected, candidate);
    }

    [Theory]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData(')')]
    [InlineData(']')]
    [InlineData('}')]
    public void ExtractionTrimsOnlyTerminalPunctuation(char punctuation)
    {
        string text = Url + "?x=1.2" + punctuation;

        string candidate = Assert.Single(ClipboardUrlMonitor.ExtractCandidates(text, Hosts));

        Assert.Equal(Url + "?x=1.2", candidate);
    }

    [Fact]
    public void ExtractionRejectsAnOverlongSupportedUrl()
    {
        string text = Url + "?padding=" + new string('a', ClipboardUrlMonitor.MaximumTextLength);

        Assert.Empty(ClipboardUrlMonitor.ExtractCandidates(text, Hosts));
    }

    [Fact]
    public void ExtractionVisitBudgetStopsBeforeALaterSupportedUrl()
    {
        IEnumerable<string> unrelated = Enumerable.Range(0, ClipboardUrlMonitor.MaximumUrls)
            .Select(index => $"https://other.invalid/b/thread/{index}");
        string text = string.Join('\n', unrelated.Append(Url));

        Assert.Empty(ClipboardUrlMonitor.ExtractCandidates(text, Hosts));

        string withThirtyOneUnrelated = string.Join('\n', unrelated.Take(ClipboardUrlMonitor.MaximumUrls - 1).Append(Url));
        Assert.Equal(Url, Assert.Single(ClipboardUrlMonitor.ExtractCandidates(withThirtyOneUnrelated, Hosts)));
    }

    [Fact]
    public async Task OversizedNewClipboardClearsPendingWithoutProviderClassification()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Hosts, Classify);
        Assert.Equal(Url, Assert.Single(monitor.Pending));

        await monitor.PollAsync(
            () => true,
            () => Task.FromResult<string?>(new string('x', ClipboardUrlMonitor.MaximumTextLength + 1)),
            Hosts,
            _ => throw new InvalidOperationException("An oversized clipboard must not reach a provider."));

        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task LostReadinessInvalidatesALateClipboardRead()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        TaskCompletionSource<string?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool canScan = true;

        Task poll = monitor.PollAsync(() => canScan, () => read.Task, Hosts, Classify);
        canScan = false;
        read.SetResult(Url);
        await poll;

        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task LostReadinessStopsFurtherCandidatesAndInvalidatesTheOffer()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        TaskCompletionSource<BoardAdmissionTarget?> firstClassification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool canScan = true;
        int classifications = 0;

        Task poll = monitor.PollAsync(
            () => canScan,
            () => Task.FromResult<string?>(Url + "\n" + Url + "4"),
            Hosts,
            url =>
            {
                classifications++;
                return url == Url ? firstClassification.Task : throw new InvalidOperationException("Readiness was lost before the second candidate.");
            });
        canScan = false;
        firstClassification.SetResult(new(Url, IsBoard: false, AlreadyWatching: false));
        await poll;

        Assert.Equal(1, classifications);
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task DismissDuringClassificationInvalidatesItsLateTarget()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        TaskCompletionSource<BoardAdmissionTarget?> classification = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task poll = monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url), Hosts, _ => classification.Task);
        monitor.Dismiss();
        classification.SetResult(new(Url, IsBoard: false, AlreadyWatching: false));
        await poll;

        Assert.Empty(monitor.Pending);
    }
}
