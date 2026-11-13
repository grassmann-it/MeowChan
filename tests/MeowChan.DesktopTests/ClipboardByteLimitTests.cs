using System.Text;
using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Synthetic clipboard and classifier delegates only; no native clipboard, profiles or network.
public sealed class ClipboardByteLimitTests
{
    private const string Url = "https://byte-limit.invalid/b/thread/123";
    private static readonly string[] Hosts = ["byte-limit.invalid"];

    public static IEnumerable<object[]> ByteBoundaries()
    {
        foreach (int limit in new[] { 4 * 1024, 64 * 1024 })
        foreach (string padding in new[] { "x", "é", "界", "😀", "\uD800" })
        foreach (int delta in new[] { -1, 0, 1 })
            yield return [limit, padding, delta];
    }

    [Theory]
    [MemberData(nameof(ByteBoundaries))]
    public void StaticExtractionEnforcesTheExactUtf8ByteBudget(int limit, string padding, int delta)
    {
        string text = TextWithByteCount(limit + delta, padding);

        IReadOnlyList<string> candidates = ClipboardUrlMonitor.ExtractCandidates(text, Hosts, 32, limit, out bool truncated);

        if (delta <= 0) Assert.Equal(Url, Assert.Single(candidates));
        else Assert.Empty(candidates);
        Assert.False(truncated); // A text-size rejection is not a URL-count truncation.
    }

    [Theory]
    [MemberData(nameof(ByteBoundaries))]
    public async Task PassiveMonitorEnforcesTheExactUtf8ByteBudget(int limit, string padding, int delta)
    {
        string text = TextWithByteCount(limit + delta, padding);
        ClipboardUrlMonitor monitor = new();
        monitor.Configure(32, limit);
        monitor.SetEnabled(true);
        int classifications = 0;

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(text), Hosts, url =>
        {
            classifications++;
            return Accept(url);
        });

        Assert.Equal(delta <= 0 ? 1 : 0, classifications);
        if (delta <= 0) Assert.Equal(Url, Assert.Single(monitor.Pending));
        else Assert.Empty(monitor.Pending);
        Assert.Equal(delta <= 0 ? ClipboardNotice.None : ClipboardNotice.TextTooLong, monitor.Notice);
    }

    [Theory]
    [InlineData("é")]
    [InlineData("界")]
    [InlineData("😀")]
    public void DefaultExtractionOverloadUsesThe64KiBByteBudget(string padding)
    {
        Assert.Equal(Url, Assert.Single(ClipboardUrlMonitor.ExtractCandidates(TextWithByteCount(64 * 1024, padding), Hosts)));
        Assert.Empty(ClipboardUrlMonitor.ExtractCandidates(TextWithByteCount(64 * 1024 + 1, padding), Hosts));
    }

    [Fact]
    public async Task ConfiguredByteBudgetCanExceedTheDefault()
    {
        int limit = 128 * 1024;
        string text = TextWithByteCount(limit, "😀");
        ClipboardUrlMonitor monitor = new();
        monitor.Configure(32, limit);
        monitor.SetEnabled(true);

        Assert.Equal(Url, Assert.Single(ClipboardUrlMonitor.ExtractCandidates(text, Hosts, 32, limit, out bool truncated)));
        Assert.False(truncated);
        await Poll(monitor, text, Accept);
        Assert.Equal(Url, Assert.Single(monitor.Pending));
        await Poll(monitor, text + " ", MustNotClassify);
        Assert.Empty(monitor.Pending);
        Assert.Equal(ClipboardNotice.TextTooLong, monitor.Notice);
    }

    [Fact]
    public async Task OversizedUtf8StartupReadDoesNotConsumeTheSilentBaseline()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.Configure(32, 4 * 1024);
        monitor.SetEnabled(true, baselineOnly: true);
        await Poll(monitor, TextWithByteCount(4 * 1024 + 1, "😀"), MustNotClassify);
        Assert.Empty(monitor.Pending);
        Assert.Equal(ClipboardNotice.None, monitor.Notice);

        string baseline = TextWithByteCount(4 * 1024, "界");
        await Poll(monitor, baseline, MustNotClassify);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(baseline),
            ["byte-limit.invalid", "another.invalid"], MustNotClassify);
        Assert.Empty(monitor.Pending);
        await Poll(monitor, Url, Accept);
        Assert.Equal(Url, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task OversizedUtf8ReadClearsPendingAndPreservesNoticeAndBatchDedupeBehavior()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.Configure(32, 4 * 1024);
        monitor.SetEnabled(true);
        await Poll(monitor, Url, Accept);
        Assert.Single(monitor.Pending);

        string oversized = TextWithByteCount(4 * 1024 + 1, "é");
        await Poll(monitor, oversized, MustNotClassify);
        Assert.Empty(monitor.Pending);
        Assert.Equal(ClipboardNotice.TextTooLong, monitor.Notice);
        monitor.Dismiss();
        await Poll(monitor, oversized, MustNotClassify);
        Assert.Equal(ClipboardNotice.None, monitor.Notice);

        await Poll(monitor, TextWithByteCount(4 * 1024, "😀"), Accept);
        Assert.Equal(Url, Assert.Single(monitor.TakePending()));
        await Poll(monitor, "é\n" + Url, Accept);
        Assert.Empty(monitor.Pending); // Different surrounding text still has the dismissed URL batch.
        await Poll(monitor, "é\n" + Url, MustNotClassify);
        Assert.Equal(ClipboardNotice.None, monitor.Notice);
    }

    [Fact]
    public async Task Utf8MeasurementDoesNotChangeTheUrlMatchVisitBudget()
    {
        string text = "😀\n" + Url + "\n" + Url + "\nhttps://other.invalid/1\n" + Url + "4";
        int limit = 4 * 1024;
        Assert.Equal(Url, Assert.Single(ClipboardUrlMonitor.ExtractCandidates(text, Hosts, 3, limit, out bool truncated)));
        Assert.True(truncated); // Duplicate and unsupported matches both spend the visit budget.
        ClipboardUrlMonitor monitor = new();
        monitor.Configure(3, limit);
        monitor.SetEnabled(true);
        int classifications = 0;
        await Poll(monitor, text, url => { classifications++; return Accept(url); });
        Assert.Equal(1, classifications);
        Assert.Equal(Url, Assert.Single(monitor.Pending));
        Assert.Equal(ClipboardNotice.UrlsCut, monitor.Notice);
    }

    private static string TextWithByteCount(int bytes, string padding)
    {
        string prefix = Url + "\n";
        int remaining = bytes - Encoding.UTF8.GetByteCount(prefix);
        int paddingBytes = Encoding.UTF8.GetByteCount(padding);
        string text = prefix + string.Concat(Enumerable.Repeat(padding, remaining / paddingBytes))
            + new string(' ', remaining % paddingBytes);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(text));
        if (padding != "x") Assert.True(text.Length < bytes); // The old UTF-16 guard would accept an over-budget case.
        return text;
    }

    private static Task Poll(ClipboardUrlMonitor monitor, string text, Func<string, Task<BoardAdmissionTarget?>> classify) =>
        monitor.PollAsync(() => true, () => Task.FromResult<string?>(text), Hosts, classify);

    private static Task<BoardAdmissionTarget?> Accept(string url) => Task.FromResult<BoardAdmissionTarget?>(new(url, false, false));

    private static Task<BoardAdmissionTarget?> MustNotClassify(string _) =>
        throw new InvalidOperationException("This clipboard read must not reach a provider.");
}
