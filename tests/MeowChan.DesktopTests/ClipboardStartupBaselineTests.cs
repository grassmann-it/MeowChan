using System.Reflection;
using System.Runtime.CompilerServices;
using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Synthetic delegates only: no window, native clipboard, user profile, network or timers.
// The final wiring test reads source; it is not a native lifecycle/GUI test.
public sealed class ClipboardStartupBaselineTests
{
    private const string First = "https://clipboard-startup.invalid/b/thread/301";
    private const string Second = "https://clipboard-startup.invalid/b/thread/302";
    private static readonly string[] Hosts = ["clipboard-startup.invalid"];
    private static readonly string[] ExpandedHosts = ["clipboard-startup.invalid", "other-startup.invalid"];

    [Fact]
    public async Task StartupOnBaselinesUnchangedTextAndOnlyOffersALaterChangeWithFrozenConsent()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        string current = First;
        int reads = 0;
        List<string> classified = new();
        Task<string?> Read() { reads++; return Task.FromResult<string?>(current); }
        Task<BoardAdmissionTarget?> Classify(string url) { classified.Add(url); return Accept(url); }

        await monitor.PollAsync(() => true, Read, Hosts, Classify);
        Assert.Empty(classified);
        Assert.Empty(monitor.Pending);
        // Ordinary UI refreshes neither disarm nor rearm the startup policy.
        monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, Read, Hosts, Classify);
        Assert.Empty(classified);
        Assert.Empty(monitor.Pending);

        current = Second;
        await monitor.PollAsync(() => true, Read, Hosts, Classify);
        Assert.Equal(new[] { Second }, classified);
        Assert.Equal(Second, Assert.Single(monitor.Pending));
        current = First; // Confirmation must not reread the current clipboard.
        Assert.Equal(Second, Assert.Single(monitor.TakePending()));
        Assert.Empty(monitor.TakePending());
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task StartupOffNeverReadsAndMidSessionOptInOffersExistingText()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(false, baselineOnly: true);
        await monitor.PollAsync(() => true,
            () => throw new InvalidOperationException("Opt-out must not read."), Hosts, MustNotClassify);
        monitor.SetEnabled(true);
        await Poll(monitor, First, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReOptInOffersExistingTextWhetherOrNotStartupBaselineWasCompleted(bool completeBaseline)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        if (completeBaseline) await Poll(monitor, First, MustNotClassify);
        else await monitor.PollAsync(() => false,
            () => throw new InvalidOperationException("Not ready must not read."), Hosts, MustNotClassify);

        monitor.SetEnabled(false);
        Assert.Empty(monitor.Pending);
        monitor.SetEnabled(true);
        await Poll(monitor, First, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        monitor.SetEnabled(false);
        Assert.Empty(monitor.TakePending());
    }

    [Fact]
    public async Task RepeatedEnabledRefreshAndOverlappingPollDoNotConsumeAnOutstandingBaseline()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        TaskCompletionSource<string?> read = NewRead();
        Task poll = monitor.PollAsync(() => true, () => read.Task, Hosts, MustNotClassify);
        Assert.False(poll.IsCompleted);
        monitor.SetEnabled(true);
        await monitor.PollAsync(() => true,
            () => throw new InvalidOperationException("Must not overlap a native read."), Hosts, MustNotClassify);
        read.SetResult(First);
        await poll;
        await Poll(monitor, First, MustNotClassify);
        Assert.Empty(monitor.Pending);
        await Poll(monitor, Second, Accept);
        Assert.Equal(Second, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task ReadinessLossDiscardsLateBaselineAndRetriesWithoutClassification()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        bool ready = false;
        await monitor.PollAsync(() => ready,
            () => throw new InvalidOperationException("Not ready must not read."), Hosts, MustNotClassify);
        ready = true;
        TaskCompletionSource<string?> read = NewRead();
        Task poll = monitor.PollAsync(() => ready, () => read.Task, Hosts, MustNotClassify);
        ready = false;
        read.SetResult(First);
        await poll;
        Assert.Empty(monitor.Pending);
        ready = true;
        await Poll(monitor, Second, MustNotClassify); // The late First did not consume the baseline.
        await Poll(monitor, Second, MustNotClassify);
        await Poll(monitor, First, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task DismissInvalidatesOutstandingBaselineWithoutTurningTheRetryIntoAnOffer()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        TaskCompletionSource<string?> read = NewRead();
        Task poll = monitor.PollAsync(() => true, () => read.Task, Hosts, MustNotClassify);
        monitor.Dismiss();
        read.SetResult(First);
        await poll;
        await Poll(monitor, Second, MustNotClassify);
        Assert.Empty(monitor.Pending);
        await Poll(monitor, First, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrReOptInInvalidatesLateBaselineAndOnlyAFreshOptInPollCanOffer(bool reenableDuringRead)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        TaskCompletionSource<string?> read = NewRead();
        Task poll = monitor.PollAsync(() => true, () => read.Task, Hosts, MustNotClassify);
        monitor.SetEnabled(false); // Same invalidation used by StopClipboardMonitor.
        if (reenableDuringRead) monitor.SetEnabled(true);
        await monitor.PollAsync(() => true,
            () => throw new InvalidOperationException("Disabled or overlapping read."), Hosts, MustNotClassify);
        read.SetResult(First);
        await poll;
        Assert.Empty(monitor.Pending);
        if (!reenableDuringRead)
        {
            await monitor.PollAsync(() => true,
                () => throw new InvalidOperationException("Stopped monitor must not read."), Hosts, MustNotClassify);
            monitor.SetEnabled(true);
        }
        await Poll(monitor, First, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStartupReadRetriesTheBaselineInsteadOfClassifyingExistingText(bool synchronousFailure)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        await Assert.ThrowsAsync<IOException>(() => monitor.PollAsync(() => true,
            () => synchronousFailure ? throw new IOException("Synthetic access failure.")
                : Task.FromException<string?>(new IOException("Synthetic access failure.")), Hosts, MustNotClassify));
        Assert.Empty(monitor.Pending);
        await Poll(monitor, First, MustNotClassify);
        await Poll(monitor, First, MustNotClassify);
        await Poll(monitor, Second, Accept);
        Assert.Equal(Second, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task StaleFailedBaselineCannotRearmOrClearTheNewOptInGeneration()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        TaskCompletionSource<string?> read = NewRead();
        Task poll = monitor.PollAsync(() => true, () => read.Task, Hosts, MustNotClassify);
        monitor.SetEnabled(false);
        monitor.SetEnabled(true);
        read.SetException(new IOException("Synthetic stale access failure."));
        await Assert.ThrowsAsync<IOException>(() => poll);
        await Poll(monitor, First, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedStartupInputIsRejectedAndDoesNotSpendTheBaseline(bool whitespaceOnly)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        string oversized = whitespaceOnly ? new string(' ', ClipboardUrlMonitor.MaximumTextLength + 1)
            : First + new string('x', ClipboardUrlMonitor.MaximumTextLength);
        await Poll(monitor, oversized, MustNotClassify);
        Assert.Empty(monitor.Pending);
        Assert.Null(Field<string?>(monitor, "_lastTextHash"));
        await Poll(monitor, First, MustNotClassify);
        await Poll(monitor, First, MustNotClassify);
        await Poll(monitor, Second, Accept);
        Assert.Equal(Second, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task ExactlyMaximumLengthCanBeBaselinedWithoutExtractionOrClassification()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        string text = First + new string(' ', ClipboardUrlMonitor.MaximumTextLength - First.Length);
        await Poll(monitor, text, MustNotClassify);
        await Poll(monitor, text, MustNotClassify);
        Assert.Empty(monitor.Pending);
        await Poll(monitor, First, Accept); // A changed, bounded text is processed normally.
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("synthetic private prose")]
    [InlineData("https://foreign.invalid/b/thread/1")]
    [InlineData("https://fixture:synthetic@clipboard-startup.invalid/b/thread/1")]
    public async Task BoundedEmptyOrUnrelatedStartupContentCompletesBaselineWithoutProviderCalls(string? text)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        await Poll(monitor, text, MustNotClassify);
        Assert.Empty(monitor.Pending);
        await Poll(monitor, First, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task BaselineRetainsOnlyDigestsAndNoPendingOrSeenUrlHistory()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        string text = "synthetic private surrounding text\n" + First;
        await Poll(monitor, text, MustNotClassify);
        string hash = Field<string>(monitor, "_lastTextHash");
        Assert.Matches("^[0-9A-F]{64}$", hash);
        string startupHash = Field<string>(monitor, "_startupBaselineTextHash");
        Assert.Matches("^[0-9A-F]{64}$", startupHash);
        Assert.Matches("^[0-9A-F]{64}$", Field<string>(monitor, "_supportedHostsHash"));
        Assert.Null(Field<string?>(monitor, "_pendingHash"));
        Assert.Empty(Field<HashSet<string>>(monitor, "_seen"));
        Assert.Empty(Field<Queue<string>>(monitor, "_seenOrder"));
        Assert.Empty(monitor.Pending);
        // All retained string fields are digests, not text, candidate lists or diagnostics.
        foreach (FieldInfo field in typeof(ClipboardUrlMonitor).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            if (field.GetValue(monitor) is string value) Assert.Matches("^[0-9A-F]{64}$", value);

        ClipboardUrlMonitor otherHosts = StartupOn();
        await otherHosts.PollAsync(() => true, () => Task.FromResult<string?>(text), ExpandedHosts, MustNotClassify);
        Assert.NotEqual(hash, Field<string>(otherHosts, "_lastTextHash"));
        Assert.Equal(startupHash, Field<string>(otherHosts, "_startupBaselineTextHash"));
    }

    [Fact]
    public async Task EquivalentAndChangedHostSetsKeepUnchangedStartupTextSuppressed()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), ExpandedHosts, MustNotClassify);
        string[] equivalent = ["OTHER-STARTUP.INVALID", "CLIPBOARD-STARTUP.INVALID", "clipboard-startup.invalid"];
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), equivalent, MustNotClassify);
        Assert.Empty(monitor.Pending);
        List<string> classified = new();
        Task<BoardAdmissionTarget?> Classify(string url) { classified.Add(url); return Accept(url); }
        await Poll(monitor, First, Classify); // Removing a host is not a new clipboard copy.
        Assert.Empty(classified);
        Assert.Empty(monitor.Pending);
        await Poll(monitor, Second, Classify);
        Assert.Equal(new[] { Second }, classified);
        Assert.Equal(Second, Assert.Single(monitor.Pending));
        monitor.SetSupportedHosts(["foreign.invalid"]);
        Assert.Empty(monitor.TakePending()); // A removed host's old offer is revoked immediately.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedHostExpansionCannotClassifyUnchangedStartupClipboard(bool initiallyNoHosts)
    {
        const string initial = "synthetic private startup note\nhttps://other-startup.invalid/b/thread/401";
        const string changed = "https://other-startup.invalid/b/thread/402";
        ClipboardUrlMonitor monitor = StartupOn();
        List<string> classified = new();
        Task<BoardAdmissionTarget?> Classify(string url) { classified.Add(url); return Accept(url); }
        string[] initialHosts = initiallyNoHosts ? Array.Empty<string>() : Hosts;
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(initial), initialHosts, Classify);
        string startupHash = Field<string>(monitor, "_startupBaselineTextHash");
        Assert.Empty(classified);

        monitor.SetSupportedHosts(ExpandedHosts);
        Assert.Equal(startupHash, Field<string>(monitor, "_startupBaselineTextHash"));
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(initial), ExpandedHosts, Classify);
        Assert.Empty(classified);
        Assert.Empty(monitor.TakePending());
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(initial), ExpandedHosts, Classify);
        Assert.Empty(classified);

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(changed), ExpandedHosts, Classify);
        Assert.Equal(new[] { changed }, classified);
        Assert.Equal(changed, Assert.Single(monitor.TakePending()));
        Assert.Null(Field<string?>(monitor, "_startupBaselineTextHash"));
    }

    [Fact]
    public async Task ChangedClipboardRetiresStartupDigestButReOptInCanOfferUnchangedStartupText()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        List<string> classified = new();
        Task<BoardAdmissionTarget?> Classify(string url) { classified.Add(url); return Accept(url); }
        await Poll(monitor, First, Classify);
        monitor.SetEnabled(false);
        Assert.Null(Field<string?>(monitor, "_startupBaselineTextHash"));
        monitor.SetEnabled(true);
        await Poll(monitor, First, Classify);
        Assert.Equal(new[] { First }, classified);
        Assert.Equal(First, Assert.Single(monitor.TakePending()));

        monitor = StartupOn();
        classified.Clear();
        await Poll(monitor, First, Classify);
        await Poll(monitor, Second, Classify);
        Assert.Equal(new[] { Second }, classified);
        Assert.Null(Field<string?>(monitor, "_startupBaselineTextHash"));
        await Poll(monitor, First, Classify); // Not a permanent blacklist/history.
        Assert.Equal(new[] { Second, First }, classified);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task LiveHostChangeAtBaselineCompletionInvalidatesItAndPreservesTheBaselineRetry()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        string[] liveHosts = Hosts;
        bool CanScan() { monitor.SetSupportedHosts(liveHosts); return true; }
        TaskCompletionSource<string?> read = NewRead();
        Task poll = monitor.PollAsync(CanScan, () => read.Task, Hosts, MustNotClassify);
        liveHosts = ExpandedHosts;
        read.SetResult(First);
        await poll;
        Assert.Empty(monitor.Pending);
        await monitor.PollAsync(CanScan, () => Task.FromResult<string?>(Second), liveHosts, MustNotClassify);
        await monitor.PollAsync(CanScan, () => Task.FromResult<string?>(Second), liveHosts, MustNotClassify);
        await monitor.PollAsync(CanScan, () => Task.FromResult<string?>(First), liveHosts, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task HostChangeBeforeReadGuardDefersThePollRatherThanMixingHostSnapshots()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        bool CanScan() { monitor.SetSupportedHosts(ExpandedHosts); return true; }
        await monitor.PollAsync(CanScan,
            () => throw new InvalidOperationException("A changed generation must not read."), Hosts, MustNotClassify);
        await monitor.PollAsync(CanScan, () => Task.FromResult<string?>(First), ExpandedHosts, MustNotClassify);
        Assert.Empty(monitor.Pending);
        await monitor.PollAsync(CanScan, () => Task.FromResult<string?>(Second), ExpandedHosts, Accept);
        Assert.Equal(Second, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task OverlappingHostRefreshInvalidatesOldBaselineWithoutStartingAnotherRead()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        TaskCompletionSource<string?> read = NewRead();
        Task poll = monitor.PollAsync(() => true, () => read.Task, Hosts, MustNotClassify);
        await monitor.PollAsync(() => true,
            () => throw new InvalidOperationException("Host change must not overlap the old read."), ExpandedHosts, MustNotClassify);
        read.SetResult(First);
        await poll;
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Second), ExpandedHosts, MustNotClassify);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), ExpandedHosts, Accept);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task HostChangeDuringLaterClassificationDropsLateResultWithoutRearmingStartupBaseline()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        await Poll(monitor, First, MustNotClassify);
        TaskCompletionSource<BoardAdmissionTarget?> classification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task poll = monitor.PollAsync(() => true, () => Task.FromResult<string?>(Second), Hosts, _ => classification.Task);
        Assert.False(poll.IsCompleted);
        monitor.SetSupportedHosts(["foreign.invalid"]);
        classification.SetResult(new(Second, false, false));
        await poll;
        Assert.Empty(monitor.Pending);
        await Poll(monitor, Second, Accept);
        Assert.Equal(Second, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task PostBaselineProcessingStillUsesTheFirst32CandidateVisitBudget()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        await Poll(monitor, First, MustNotClassify);
        string[] foreign = Enumerable.Range(0, ClipboardUrlMonitor.MaximumUrls)
            .Select(index => $"https://foreign.invalid/b/thread/{index}").ToArray();
        await Poll(monitor, string.Join('\n', foreign.Append(Second)), MustNotClassify);
        Assert.Empty(monitor.Pending);
        await Poll(monitor, string.Join('\n', foreign.Take(ClipboardUrlMonitor.MaximumUrls - 1).Append(Second)), Accept);
        Assert.Equal(Second, Assert.Single(monitor.Pending));

        monitor.Dismiss();
        string[] supported = Enumerable.Range(400, ClipboardUrlMonitor.MaximumUrls + 8)
            .Select(index => $"https://clipboard-startup.invalid/b/thread/{index}").ToArray();
        List<string> classified = new();
        await Poll(monitor, string.Join('\n', supported), url => { classified.Add(url); return Accept(url); });
        Assert.Equal(supported.Take(ClipboardUrlMonitor.MaximumUrls), classified);
        Assert.Equal(classified, monitor.Pending);
    }

    [Fact]
    public void ControllerWiringCapturesStartupOnceAndKeepsReadyStopAndExplicitConsentGuards()
    {
        string source = File.ReadAllText(SourcePath("Desktop/Shell/MainWindow.ClipboardMonitor.cs"));
        string refresh = Between(source, "private void RefreshClipboardMonitor()", "private string[] GetClipboardMonitorHosts()");
        Assert.Contains("bool enabled = _config.MonitorClipboardUrls && !_isClosing;", refresh);
        string enable = "_clipboardUrlMonitor.SetEnabled(enabled, baselineOnly: !_clipboardMonitorInitialized);";
        Assert.Contains(enable, refresh);
        Assert.True(refresh.IndexOf(enable, StringComparison.Ordinal)
            < refresh.IndexOf("_clipboardMonitorInitialized = true;", StringComparison.Ordinal));
        Assert.DoesNotContain("ReadClipboardAsync", refresh);
        Assert.Contains("_hasOpened && _restoreCompleted && !_isClosing", source);
        Assert.Contains("_session.State == DesktopSessionState.Ready", source);
        string guard = Between(source, "private bool CanScanCurrentClipboard()", "private async void OnClipboardMonitorTick(");
        Assert.Contains("if (!_config.MonitorClipboardUrls || !CanScanClipboard) return false;", guard);
        Assert.Contains("_clipboardUrlMonitor.SetSupportedHosts(GetClipboardMonitorHosts());", guard);
        Assert.Contains("_clipboardUrlMonitor.PollAsync(CanScanCurrentClipboard,", source);
        string stop = Between(source, "private void StopClipboardMonitor()", "private void OnClipboardSuggestionDismiss(");
        Assert.Contains("_clipboardUrlMonitor.SetEnabled(false);", stop);
        string consent = source[source.IndexOf("private async void OnClipboardSuggestionAccept(", StringComparison.Ordinal)..];
        Assert.Contains("!CanShowClipboardSuggestion || RowCommandBusy", consent);
        Assert.Contains("!accept.IsEffectivelyVisible || !accept.IsEffectivelyEnabled", consent);
        Assert.Contains("var urls = _clipboardUrlMonitor.TakePending();", consent);
        Assert.DoesNotContain("ReadClipboardAsync", consent);
        Assert.DoesNotContain("Log.", source); // No new raw-URL/clipboard logging path in the controller.
    }

    private static ClipboardUrlMonitor StartupOn()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true, baselineOnly: true);
        return monitor;
    }

    private static Task Poll(ClipboardUrlMonitor monitor, string? text,
        Func<string, Task<BoardAdmissionTarget?>> classify) =>
        monitor.PollAsync(() => true, () => Task.FromResult(text), Hosts, classify);

    private static Task<BoardAdmissionTarget?> Accept(string url) =>
        Task.FromResult<BoardAdmissionTarget?>(new(url, false, false));

    private static Task<BoardAdmissionTarget?> MustNotClassify(string _) =>
        throw new InvalidOperationException("Baseline or ineligible input must not reach a provider.");

    private static TaskCompletionSource<string?> NewRead() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Field<T>(ClipboardUrlMonitor monitor, string name) =>
        (T)typeof(ClipboardUrlMonitor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;

    private static string SourcePath(string relativePath, [CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "../..", relativePath));

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Missing source boundary: {start}");
        int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"Missing source boundary: {end}");
        return source[from..to];
    }
}
