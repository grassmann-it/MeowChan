using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Content/privacy sidecar only. Synthetic delegates and monitor state, no native
// clipboard, host, profile, logging sink, network, window, timer or filesystem IO.
// Host/generation/cancellation ordering belongs to the separate race coverage.
public sealed class ClipboardBaselinePrivacyContractTests
{
    private const string Host = "baseline-contract.invalid";
    private const string First = "https://baseline-contract.invalid/b/thread/701";
    private const string Alias = "https://baseline-contract.invalid/b/thread/702";
    private const string Normalizable = "HTTPS://BASELINE-CONTRACT.INVALID:443/b/thread/701";
    private static readonly string[] Hosts = [Host];

    public static IEnumerable<object?[]> InitialContents()
    {
        yield return [null];
        yield return [string.Empty];
        yield return [" \t\r\n"];
        yield return ["synthetic confidential note: fixture-only \0\u001f\u200b"];
        yield return [First];
        yield return [Normalizable + "\n" + First + "," + Alias];
        yield return [MixedContent()];
        yield return [string.Join('\n', Enumerable.Repeat(First, ClipboardUrlMonitor.MaximumUrls + 9))];
        yield return [string.Join('\n', Enumerable.Range(1, ClipboardUrlMonitor.MaximumUrls + 9)
            .Select(index => $"https://baseline-contract.invalid/b/thread/{index}"))];
    }

    [Theory]
    [MemberData(nameof(InitialContents))]
    public async Task ArbitraryInitialContentIsOnlyADigestNeverClassifiedOrActionable(string? initial)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        SyntheticBoundary boundary = new();

        await boundary.Poll(monitor, initial);
        Assert.Equal(1, boundary.Reads);
        Assert.Empty(boundary.Classified);
        AssertDigestOnlyState(monitor);

        await boundary.Poll(monitor, initial);
        Assert.Equal(2, boundary.Reads);
        Assert.Empty(boundary.Classified);
        AssertDigestOnlyState(monitor);
        string startupHash = Field<string>(monitor, "_startupBaselineTextHash");
        await boundary.Poll(monitor, initial, [Host, "foreign-contract.invalid"]);
        Assert.Equal(3, boundary.Reads);
        Assert.Empty(boundary.Classified);
        Assert.Equal(startupHash, Field<string>(monitor, "_startupBaselineTextHash"));
        Assert.Equal(Digest(initial ?? string.Empty), startupHash);
        AssertDigestOnlyState(monitor);
        // Even an explicit consumer has nothing it could admit from a baseline.
        boundary.Confirm(monitor);
        boundary.Confirm(monitor);
        Assert.Empty(boundary.Added);
        AssertDigestOnlyState(monitor);
    }

    [Fact]
    public async Task BaselineHashesWholeRawInputRatherThanAnExtractedNormalizedOrDeduplicatedBatch()
    {
        string[] rawVariants =
        [
            First,
            Normalizable,
            "synthetic private wrapper\n" + First,
            First + "\n" + First,
            First + "\nhttps://foreign-contract.invalid/private"
        ];
        List<string> hashes = new();
        foreach (string raw in rawVariants)
        {
            ClipboardUrlMonitor monitor = StartupOn();
            SyntheticBoundary boundary = new();
            await boundary.Poll(monitor, raw);
            Assert.Empty(boundary.Classified);
            AssertDigestOnlyState(monitor);
            string hash = Field<string>(monitor, "_lastTextHash");
            Assert.Equal(Digest(raw + "\n" + Digest(Host)), hash);
            Assert.Equal(Digest(raw), Field<string>(monitor, "_startupBaselineTextHash"));
            hashes.Add(hash);
        }
        // All variants contain the same eligible normalized target. Only raw-input
        // digests distinguish them; none becomes a candidate/batch-history entry.
        Assert.Equal(rawVariants.Length, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task BaselineDoesNotPoisonNormalizedBatchDedupAndLaterOfferStillNeedsExplicitConsumption()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        SyntheticBoundary boundary = new(_ => new(First, false, false));
        string initial = Normalizable + "\n" + First + "\n" + Alias;
        await boundary.Poll(monitor, initial);
        Assert.Empty(boundary.Classified);
        AssertDigestOnlyState(monitor);

        // A genuine later text change with the same normalized batch must not be
        // suppressed as if the baseline had been dismissed or previously added.
        await boundary.Poll(monitor, "synthetic changed wrapper\n" + initial);
        Assert.Equal(new[] { First, Alias }, boundary.Classified);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        Assert.Empty(boundary.Added); // Classification is not consent/admission.
        int readsAtConsent = boundary.Reads;
        boundary.Confirm(monitor);
        Assert.Equal(new[] { First }, boundary.Added);
        Assert.Equal(readsAtConsent, boundary.Reads); // Frozen list, not a new read.
        boundary.Confirm(monitor);
        Assert.Equal(new[] { First }, boundary.Added);

        // Normal non-baseline dismissal/confirmation dedup still applies.
        await boundary.Poll(monitor, "another synthetic wrapper\n" + initial);
        Assert.Equal(new[] { First, Alias, First, Alias }, boundary.Classified);
        Assert.Empty(monitor.Pending);
        boundary.Confirm(monitor);
        Assert.Equal(new[] { First }, boundary.Added);
    }

    [Fact]
    public async Task LaterMixedInputOnlyDisclosesSupportedCandidatesAndRejectsArbitraryClassifierOutput()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        SyntheticBoundary boundary = new(_ => new("synthetic private output\n" + First, false, false));
        string initial = MixedContent();
        await boundary.Poll(monitor, initial);
        Assert.Empty(boundary.Classified);
        AssertDigestOnlyState(monitor);

        await boundary.Poll(monitor, "synthetic later copy\n" + initial);
        Assert.Equal(new[] { First }, boundary.Classified);
        Assert.Empty(monitor.Pending);
        boundary.Confirm(monitor);
        Assert.Empty(boundary.Added);
        AssertDigestOnlyState(monitor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullBoundedInputIsDigestOnlyAndOversizedInputCannotBecomeBaselineOrOffer(bool whitespaceOnly)
    {
        ClipboardUrlMonitor monitor = StartupOn();
        SyntheticBoundary boundary = new();
        string bounded = whitespaceOnly ? new string(' ', ClipboardUrlMonitor.MaximumTextLength)
            : First + new string('x', ClipboardUrlMonitor.MaximumTextLength - First.Length);

        await boundary.Poll(monitor, bounded + "x");
        Assert.Empty(boundary.Classified);
        Assert.Null(Field<string?>(monitor, "_lastTextHash"));
        Assert.Null(Field<string?>(monitor, "_startupBaselineTextHash"));
        AssertNoActionableOrSeenState(monitor);
        boundary.Confirm(monitor);
        Assert.Empty(boundary.Added);

        await boundary.Poll(monitor, bounded);
        Assert.Empty(boundary.Classified);
        AssertDigestOnlyState(monitor);
        Assert.Equal(Digest(bounded + "\n" + Digest(Host)), Field<string>(monitor, "_lastTextHash"));

        await boundary.Poll(monitor, First);
        Assert.Equal(new[] { First }, boundary.Classified);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        Assert.Empty(boundary.Added);
    }

    [Fact]
    public async Task OversizedInterludeCannotEraseCompletedBaselineAndExposeItAfterHostExpansion()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        SyntheticBoundary boundary = new();
        string initial = MixedContent();
        await boundary.Poll(monitor, initial);
        string startupHash = Field<string>(monitor, "_startupBaselineTextHash");
        await boundary.Poll(monitor, new string('x', ClipboardUrlMonitor.MaximumTextLength + 1));
        Assert.Equal(startupHash, Field<string>(monitor, "_startupBaselineTextHash"));
        Assert.Null(Field<string?>(monitor, "_lastTextHash"));
        await boundary.Poll(monitor, initial, [Host, "intranet-contract.invalid"]);
        Assert.Empty(boundary.Classified);
        AssertDigestOnlyState(monitor);
        boundary.Confirm(monitor);
        Assert.Empty(boundary.Added);
        await boundary.Poll(monitor, First, [Host, "intranet-contract.invalid"]);
        Assert.Equal(new[] { First }, boundary.Classified);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        Assert.Null(Field<string?>(monitor, "_startupBaselineTextHash"));
        Assert.Empty(boundary.Added);
    }

    [Fact]
    public async Task PostBaselineFirst32VisitBudgetDoesNotDiscloseALaterSupportedCandidate()
    {
        ClipboardUrlMonitor monitor = StartupOn();
        SyntheticBoundary boundary = new();
        string[] foreign = Enumerable.Range(0, ClipboardUrlMonitor.MaximumUrls)
            .Select(index => $"https://foreign-contract.invalid/private/{index}").ToArray();
        string initial = string.Join('\n', foreign.Append(First));
        await boundary.Poll(monitor, initial);
        Assert.Empty(boundary.Classified);
        AssertDigestOnlyState(monitor);
        await boundary.Poll(monitor, "synthetic new copy\n" + initial);
        Assert.Empty(boundary.Classified);
        AssertDigestOnlyState(monitor);
        boundary.Confirm(monitor);
        Assert.Empty(boundary.Added);

        await boundary.Poll(monitor, string.Join('\n', foreign.Take(ClipboardUrlMonitor.MaximumUrls - 1).Append(First)));
        Assert.Equal(new[] { First }, boundary.Classified);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        Assert.Empty(boundary.Added);
    }

    [Fact]
    public async Task ExplicitMidSessionOptInOffersExistingTextWithoutKeepingItsPrivateWrapper()
    {
        ClipboardUrlMonitor monitor = new(); // OFF at startup; deliberate later opt-in.
        SyntheticBoundary boundary = new();
        await boundary.Poll(monitor, MixedContent());
        Assert.Equal(0, boundary.Reads);
        Assert.Empty(boundary.Classified);
        monitor.SetEnabled(true);
        await boundary.Poll(monitor, MixedContent());
        Assert.Equal(new[] { First }, boundary.Classified);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        Assert.Empty(boundary.Added);
        // Only the eligible URL may be held for consent, never surrounding text.
        Assert.All(monitor.Pending, url => Assert.Equal(First, url));
        boundary.Confirm(monitor);
        Assert.Equal(new[] { First }, boundary.Added);
    }

    private static string MixedContent() => string.Join('\n',
        "synthetic secret prose: not a real credential", "file:///synthetic/private.txt",
        "http://127.0.0.1/private", "http://[::1]/private", "http://192.168.1.1/private",
        "https://intranet-contract.invalid/private", "https://baseline-contract.invalid.evil.invalid/private",
        "https://user:synthetic@baseline-contract.invalid/private", "javascript:fixture-only",
        Normalizable + ").", First); // Punctuation/case/default port must not leak into a later offer.

    private static ClipboardUrlMonitor StartupOn()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true, baselineOnly: true);
        return monitor;
    }

    private static void AssertNoActionableOrSeenState(ClipboardUrlMonitor monitor)
    {
        Assert.Empty(monitor.Pending);
        Assert.Null(Field<string?>(monitor, "_pendingHash"));
        Assert.Empty(Field<HashSet<string>>(monitor, "_seen"));
        Assert.Empty(Field<Queue<string>>(monitor, "_seenOrder"));
    }

    private static void AssertDigestOnlyState(ClipboardUrlMonitor monitor)
    {
        AssertNoActionableOrSeenState(monitor);
        Assert.Matches("^[0-9A-F]{64}$", Field<string>(monitor, "_lastTextHash"));
        Assert.Matches("^[0-9A-F]{64}$", Field<string>(monitor, "_supportedHostsHash"));
        // Inspect this seam's owned string/collection fields, including the Pending
        // backing field, rather than asserting only that the visible panel is empty.
        foreach (FieldInfo field in typeof(ClipboardUrlMonitor).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            object? value = field.GetValue(monitor);
            if (value is string text) Assert.Matches("^[0-9A-F]{64}$", text);
            else if (value is IEnumerable<string> contents) Assert.Empty(contents);
        }
    }

    private static T Field<T>(ClipboardUrlMonitor monitor, string name) =>
        (T)typeof(ClipboardUrlMonitor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;

    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class SyntheticBoundary(Func<string, BoardAdmissionTarget?>? classify = null)
    {
        internal int Reads { get; private set; }
        internal List<string> Classified { get; } = new();
        internal List<string> Added { get; } = new();

        internal Task Poll(ClipboardUrlMonitor monitor, string? text, string[]? hosts = null) => monitor.PollAsync(() => true, () =>
        {
            Reads++;
            return Task.FromResult(text);
        }, hosts ?? Hosts, candidate =>
        {
            // Counters survive monitor exception handling: no throwing negative spy.
            Classified.Add(candidate);
            BoardAdmissionTarget? result = classify == null ? new(candidate, false, false) : classify(candidate);
            return Task.FromResult(result);
        });

        // Deliberately only a caller-side admission recorder, not a host/GUI test.
        internal void Confirm(ClipboardUrlMonitor monitor) => Added.AddRange(monitor.TakePending());
    }
}
