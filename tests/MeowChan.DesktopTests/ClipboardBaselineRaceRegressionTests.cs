using System.Reflection;
using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Synthetic delegates and values only: these tests exercise the monitor state machine,
// not a native clipboard, a window, a timer, a provider, or a user/profile fixture.
public sealed class ClipboardBaselineRaceRegressionTests
{
    private const string BaselineUrl = "https://clipboard-race-a.invalid/b/thread/401";
    private const string ExpandedBaselineUrl = "https://clipboard-race-b.invalid/b/thread/402";
    private const string NewExpandedUrl = "https://clipboard-race-b.invalid/b/thread/403";
    private static readonly string[] HostA = ["clipboard-race-a.invalid"];
    private static readonly string[] HostB = ["clipboard-race-b.invalid"];
    private static readonly string[] HostsAAndB = ["clipboard-race-a.invalid", "clipboard-race-b.invalid"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingHostsDuringInitialReadDiscardsStaleOrCancelledReadAndRetriesBaseline(bool cancelRead)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        TaskCompletionSource<string?> initialRead = NewRead();
        Task poll = monitor.PollAsync(
            () => true,
            () => initialRead.Task,
            HostA,
            _ => throw new InvalidOperationException("A cancelled baseline must not classify."));

        Assert.False(poll.IsCompleted);
        monitor.SetSupportedHosts(HostB);
        if (cancelRead)
        {
            initialRead.SetCanceled();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        }
        else
        {
            initialRead.SetResult(BaselineUrl);
            await poll;
        }

        Assert.Empty(monitor.Pending);
        Assert.Null(Field<string?>(monitor, "_startupBaselineTextHash"));
        Assert.True(Field<bool>(monitor, "_startupBaselinePending"));
        Assert.Empty(Field<HashSet<string>>(monitor, "_seen"));
        Assert.Empty(Field<Queue<string>>(monitor, "_seenOrder"));
        await monitor.PollAsync(
            () => true,
            () => Task.FromResult<string?>(ExpandedBaselineUrl),
            HostB,
            _ => throw new InvalidOperationException("The replacement-host retry is still a baseline."));
        Assert.Empty(monitor.Pending);

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(NewExpandedUrl), HostB, Accept);
        Assert.Equal(NewExpandedUrl, Assert.Single(monitor.Pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedStartupBaselineStaysUnclassifiedAcrossDelayedHostExpansion(bool refreshAtReadCompletion)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        string existingClipboard = "synthetic startup text\n" + ExpandedBaselineUrl;
        List<string> classified = new();

        await monitor.PollAsync(
            () => true,
            () => Task.FromResult<string?>(existingClipboard),
            HostA,
            url =>
            {
                classified.Add(url);
                return Accept(url);
            });
        Assert.Empty(classified);
        string startupDigest = Field<string>(monitor, "_startupBaselineTextHash");
        string[] liveHosts = HostA;
        bool CanScan()
        {
            // Mirrors the controller's current host-refresh hook at async completion.
            monitor.SetSupportedHosts(liveHosts);
            return true;
        }
        if (refreshAtReadCompletion)
        {
            TaskCompletionSource<string?> expansionRead = NewRead();
            Task poll = monitor.PollAsync(CanScan, () => expansionRead.Task, HostA,
                _ => throw new InvalidOperationException("An invalidated host snapshot must not classify."));
            Assert.False(poll.IsCompleted);
            liveHosts = HostsAAndB;
            expansionRead.SetResult(existingClipboard);
            await poll;
        }
        else
        {
            liveHosts = HostsAAndB;
            monitor.SetSupportedHosts(liveHosts);
        }
        Assert.Equal(startupDigest, Field<string>(monitor, "_startupBaselineTextHash"));

        // Host B becoming eligible does not change the bounded text identity. The
        // host-independent startup digest must continue suppressing this unchanged text.
        await monitor.PollAsync(
            CanScan,
            () => Task.FromResult<string?>(existingClipboard),
            liveHosts,
            url =>
            {
                classified.Add(url);
                throw new InvalidOperationException("Host expansion must not classify the startup baseline.");
            });

        Assert.Empty(classified);
        Assert.Equal(startupDigest, Field<string>(monitor, "_startupBaselineTextHash"));
        Assert.False(Field<bool>(monitor, "_startupBaselinePending"));
        Assert.Empty(monitor.Pending);
        Assert.Null(Field<string?>(monitor, "_pendingHash"));
        Assert.Empty(Field<HashSet<string>>(monitor, "_seen"));
        Assert.Empty(Field<Queue<string>>(monitor, "_seenOrder"));

        await monitor.PollAsync(
            () => true,
            () => Task.FromResult<string?>(NewExpandedUrl),
            HostsAAndB,
            url =>
            {
                classified.Add(url);
                return Accept(url);
            });

        Assert.Equal(new[] { NewExpandedUrl }, classified);
        Assert.Equal(NewExpandedUrl, Assert.Single(monitor.Pending));
        Assert.Null(Field<string?>(monitor, "_startupBaselineTextHash"));

        // The digest is not a permanent blacklist or seen-history entry: copying the
        // original text again after a different bounded value is ordinary input.
        monitor.Dismiss();
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(existingClipboard), liveHosts,
            url => { classified.Add(url); return Accept(url); });
        Assert.Equal(new[] { NewExpandedUrl, ExpandedBaselineUrl }, classified);
        Assert.Equal(ExpandedBaselineUrl, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task StartupBaselineStoresOnlyDigestAndNoPendingOrHistory()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        string startupClipboard = "synthetic private text\n" + BaselineUrl;
        int classifications = 0;

        await monitor.PollAsync(
            () => true,
            () => Task.FromResult<string?>(startupClipboard),
            HostA,
            _ =>
            {
                classifications++;
                throw new InvalidOperationException("Startup baseline must not classify.");
            });

        Assert.Equal(0, classifications);
        Assert.Empty(monitor.Pending);
        Assert.Null(Field<string?>(monitor, "_pendingHash"));
        Assert.Empty(Field<HashSet<string>>(monitor, "_seen"));
        Assert.Empty(Field<Queue<string>>(monitor, "_seenOrder"));
        Assert.Matches("^[0-9A-F]{64}$", Field<string>(monitor, "_lastTextHash"));
        Assert.Matches("^[0-9A-F]{64}$", Field<string>(monitor, "_startupBaselineTextHash"));
        foreach (FieldInfo field in typeof(ClipboardUrlMonitor).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            if (field.GetValue(monitor) is string value) Assert.Matches("^[0-9A-F]{64}$", value);
        Assert.False(Field<bool>(monitor, "_startupBaselinePending"));
    }

    [Fact]
    public async Task ExplicitDisableThenEnableOffersCurrentClipboardWithoutRebaselining()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        await monitor.PollAsync(
            () => true,
            () => Task.FromResult<string?>(ExpandedBaselineUrl),
            HostA,
            _ => throw new InvalidOperationException("Initial saved-ON read is a baseline."));
        monitor.SetSupportedHosts(HostsAAndB);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(ExpandedBaselineUrl), HostsAAndB,
            _ => throw new InvalidOperationException("Expanded hosts must still suppress unchanged startup text."));

        monitor.SetEnabled(false);
        Assert.Null(Field<string?>(monitor, "_startupBaselineTextHash"));
        await monitor.PollAsync(() => true,
            () => throw new InvalidOperationException("Disabled monitor must not read."), HostsAAndB, Accept);
        monitor.SetEnabled(true); // Explicit mid-session ON: baselineOnly defaults to false.
        Assert.False(Field<bool>(monitor, "_startupBaselinePending"));
        List<string> classified = new();
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(ExpandedBaselineUrl), HostsAAndB,
            url => { classified.Add(url); return Accept(url); });

        Assert.Equal(new[] { ExpandedBaselineUrl }, classified);
        Assert.Equal(ExpandedBaselineUrl, Assert.Single(monitor.Pending));
    }

    private static ClipboardUrlMonitor StartupOn()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true, baselineOnly: true);
        return monitor;
    }

    private static Task<BoardAdmissionTarget?> Accept(string url) =>
        Task.FromResult<BoardAdmissionTarget?>(new(url, IsBoard: false, AlreadyWatching: false));

    private static TaskCompletionSource<string?> NewRead() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Field<T>(ClipboardUrlMonitor monitor, string name) =>
        (T)typeof(ClipboardUrlMonitor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;
}
