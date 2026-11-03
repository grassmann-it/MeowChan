using System.Runtime.CompilerServices;
using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Only synthetic delegates; never opens a window, OS clipboard, profile or network.
// Existing Clipboard* tests cover input extraction, settings and the button-only
// Enter contract. These cases cover the *returned* classification/consent boundary.
public sealed class ClipboardConfirmationJourneyRegressionTests
{
    private const string First = "https://clipboard-confirmation.invalid/b/thread/201";
    private const string Second = "https://clipboard-confirmation.invalid/b/thread/202";
    private static readonly string[] Hosts = ["clipboard-confirmation.invalid"];

    [Theory]
    [InlineData("https://foreign.invalid/b/thread/201")]
    [InlineData("https://clipboard-confirmation.invalid.foreign.invalid/b/thread/201")]
    [InlineData("https://fixture:synthetic@clipboard-confirmation.invalid/b/thread/201")]
    [InlineData("ftp://clipboard-confirmation.invalid/b/thread/201")]
    [InlineData("/b/thread/201")]
    [InlineData(First + "\n" + Second)]
    [InlineData(First + "\r\n" + Second)]
    [InlineData("https://clipboard-confirmation.invalid/b/thread/20\t1")]
    [InlineData(First + "\0")]
    public async Task ClassifiedTargetCannotEscapeTheSupportedSingleUrlBoundary(string returnedUrl)
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        List<string> classified = new();

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), Hosts, candidate =>
        {
            classified.Add(candidate);
            return Task.FromResult<BoardAdmissionTarget?>(new(returnedUrl, false, false));
        });

        // The supported candidate was classified, so this tests the result gate,
        // not a rejection by the already-covered clipboard extractor.
        Assert.Equal(new[] { First }, classified);
        Assert.Empty(monitor.Pending);
        Assert.Empty(monitor.TakePending());
    }

    [Fact]
    public async Task OverlongClassifiedTargetIsNotOfferedEvenForAShortSupportedCandidate()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        string returnedUrl = First + "?synthetic=" + new string('x', 4096);
        int classifications = 0;

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), Hosts, _ =>
        {
            classifications++;
            return Task.FromResult<BoardAdmissionTarget?>(new(returnedUrl, false, false));
        });

        Assert.Equal(1, classifications);
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public async Task InvalidClassifiedTargetDoesNotPoisonTheOtherSupportedOffer()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        List<string> classified = new();
        const string canonical = Second + "?synthetic=1%20two#fixture";

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First + "\n" + Second), Hosts, candidate =>
        {
            classified.Add(candidate);
            string returnedUrl = candidate == First ? "https://foreign.invalid/b/thread/201"
                : "HTTPS://CLIPBOARD-CONFIRMATION.INVALID/b/thread/202?synthetic=1%20two#fixture";
            return Task.FromResult<BoardAdmissionTarget?>(new(returnedUrl, false, false));
        });

        Assert.Equal(new[] { First, Second }, classified);
        Assert.Equal(canonical, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task ReturnedCanonicalAliasesDeduplicateBeforeOfferingAndRememberingTheBatch()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        int classifications = 0;
        Task<BoardAdmissionTarget?> Classify(string candidate)
        {
            classifications++;
            string returnedUrl = candidate == First ? First
                : "HTTPS://CLIPBOARD-CONFIRMATION.INVALID/b/thread/201";
            return Task.FromResult<BoardAdmissionTarget?>(new(returnedUrl, false, false));
        }

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First + "\n" + Second), Hosts, Classify);
        Assert.Equal(2, classifications);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        monitor.Dismiss();

        // Different input, same canonical result; result casing is not a new offer.
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Second), Hosts, Classify);
        Assert.Equal(3, classifications);
        Assert.Empty(monitor.Pending);
    }

    [Fact]
    public void NativeClickConsentChecksCaptureAndEffectiveAvailabilityBeforeTakingTheOffer()
    {
        // Source-only wiring guard: native Button activation can raise Click before
        // the panel sees KeyDown. This does not claim a native focus/input test.
        string source = File.ReadAllText(SourcePath("Desktop/Shell/MainWindow.ClipboardMonitor.cs"));
        int start = source.IndexOf("private async void OnClipboardSuggestionAccept(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string handler = source[start..];
        int capture = handler.IndexOf("if (ShortcutService.IsCapturing || sender is not Button accept", StringComparison.Ordinal);
        int visible = handler.IndexOf("|| !accept.IsEffectivelyVisible || !accept.IsEffectivelyEnabled) return;", StringComparison.Ordinal);
        int take = handler.IndexOf("_clipboardUrlMonitor.TakePending()", StringComparison.Ordinal);
        int submit = handler.IndexOf("await SubmitInputAsync(", StringComparison.Ordinal);

        Assert.True(capture >= 0 && visible > capture && take > visible && submit > take,
            "Capture/effective-button guards must precede consumption and admission of the offered URLs.");
    }

    private static string SourcePath(string relativePath, [CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "../..", relativePath));
}
