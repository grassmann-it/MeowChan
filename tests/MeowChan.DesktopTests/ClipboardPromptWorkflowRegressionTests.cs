using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Admission;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Monitor behavior plus explicitly source/markup-only shell wiring guards.
// These tests do not construct a window or prove native keyboard/focus behavior.
public sealed class ClipboardPromptWorkflowRegressionTests
{
    private const string First = "https://clipboard-prompt.invalid/b/thread/101";
    private const string Second = "https://clipboard-prompt.invalid/b/thread/102";
    private const string Third = "https://clipboard-prompt.invalid/b/thread/103";
    private static readonly string[] Hosts = ["clipboard-prompt.invalid"];

    [Fact]
    public async Task RepeatedEnabledRefreshDoesNotInvalidateAnInFlightOrVisibleOffer()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        TaskCompletionSource<BoardAdmissionTarget?> classification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int classifications = 0;
        Task poll = monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), Hosts, _ =>
        {
            classifications++;
            return classification.Task;
        });
        Assert.Equal(1, classifications);
        Assert.False(poll.IsCompleted);

        // RefreshClipboardMonitor calls SetEnabled on every UI refresh, not only on toggles.
        monitor.SetEnabled(true);
        classification.SetResult(new(First, false, false));
        await poll;
        Assert.Equal(First, Assert.Single(monitor.Pending));
        var displayed = monitor.Pending;

        monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), Hosts,
            _ => throw new InvalidOperationException("A presentation refresh must not reclassify unchanged text."));

        Assert.Same(displayed, monitor.Pending);
        Assert.Equal(First, Assert.Single(monitor.Pending));
    }

    [Fact]
    public async Task ReplacementClearsTheOldOfferBeforeAwaitingAndPublishesNoPartialBatch()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), Hosts, Classify);
        var oldDisplayed = monitor.Pending;
        Assert.Equal(First, Assert.Single(oldDisplayed));
        TaskCompletionSource<BoardAdmissionTarget?> lastTarget = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> classified = new();

        Task poll = monitor.PollAsync(() => true, () => Task.FromResult<string?>(Second + "\n" + Third), Hosts, url =>
        {
            classified.Add(url);
            // Even the first classification must see the old offer invalidated.
            Assert.Empty(monitor.Pending);
            return url == Third ? lastTarget.Task : Classify(url);
        });
        Assert.Equal(new[] { Second, Third }, classified);
        Assert.False(poll.IsCompleted);
        Assert.Empty(monitor.Pending); // Second is classified, but the batch is not complete yet.

        lastTarget.SetResult(new(Third, false, false));
        await poll;

        Assert.Equal(new[] { Second, Third }, monitor.Pending);
        Assert.Equal(First, Assert.Single(oldDisplayed)); // A previously rendered snapshot was not mutated.
        var taken = monitor.TakePending();
        Assert.Equal(new[] { Second, Third }, taken);
        Assert.Empty(monitor.TakePending()); // A second acceptance cannot consume the same offer again.
        monitor.SetEnabled(false);
        Assert.Equal(new[] { Second, Third }, taken);
    }

    [Fact]
    public async Task TakingTheDisplayedOfferInvalidatesAnOutstandingReadButAllowsTheNextPoll()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), Hosts, Classify);
        TaskCompletionSource<string?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        Task poll = monitor.PollAsync(() => true, () => { reads++; return read.Task; }, Hosts,
            _ => throw new InvalidOperationException("A read from before confirmation must not classify afterwards."));
        Assert.Equal(1, reads);
        Assert.False(poll.IsCompleted);

        var taken = monitor.TakePending();
        Assert.Equal(First, Assert.Single(taken));
        Assert.Empty(monitor.Pending);
        Assert.Equal(1, reads); // Taking the batch does not perform another read.
        read.SetResult(Second);
        await poll;
        Assert.Empty(monitor.Pending);

        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Second), Hosts, Classify);
        Assert.Equal(Second, Assert.Single(monitor.Pending));
        Assert.Equal(First, Assert.Single(taken));
    }

    [Fact]
    public async Task FailedReplacementDoesNotLeaveAnOldOrPartialOfferAndCanRetryTheSameText()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(First), Hosts, Classify);
        Assert.Equal(First, Assert.Single(monitor.Pending));
        const string replacement = Second + "\n" + Third;
        List<string> classified = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.PollAsync(
            () => true, () => Task.FromResult<string?>(replacement), Hosts, url =>
            {
                classified.Add(url);
                return url == Third ? Task.FromException<BoardAdmissionTarget?>(new InvalidOperationException("Synthetic classifier failure"))
                    : Classify(url);
            }));
        Assert.Equal(new[] { Second, Third }, classified);
        Assert.Empty(monitor.Pending);
        Assert.Empty(monitor.TakePending());

        // Failure must release the single-poll gate and must not cache a successful text hash.
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(replacement), Hosts, Classify);
        Assert.Equal(new[] { Second, Third }, monitor.Pending);
    }

    [Fact]
    public void SuggestionMarkupKeepsConsentOnLocalButtonsAndThePreviewReadOnly()
    {
        XDocument markup = XDocument.Load(SourcePath("Desktop/Shell/MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement panel = Assert.Single(markup.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "ClipboardSuggestionPanel"));
        XElement Control(string name) => Assert.Single(panel.Descendants().Where(e => (string?)e.Attribute(x + "Name") == name));

        Assert.Equal("Border", panel.Name.LocalName); // An inline banner, not a separate native popup.
        Assert.Equal("False", (string?)panel.Attribute("IsVisible"));
        Assert.Equal("OnClipboardSuggestionKeyDown", (string?)panel.Attribute("KeyDown"));
        XElement preview = Control("ClipboardSuggestionUrls");
        Assert.Equal("TextBox", preview.Name.LocalName);
        Assert.Equal("True", (string?)preview.Attribute("IsReadOnly"));
        Assert.Equal("True", (string?)preview.Attribute("AcceptsReturn"));
        Assert.Equal("{Binding ElementName=ClipboardSuggestionTitle}", (string?)preview.Attribute("AutomationProperties.LabeledBy"));
        Assert.Equal("TextBlock", Control("ClipboardSuggestionTitle").Name.LocalName);

        XElement accept = Control("ClipboardSuggestionAccept");
        Assert.Equal("Button", accept.Name.LocalName);
        Assert.Equal("OnClipboardSuggestionAccept", (string?)accept.Attribute("Click"));
        Assert.NotEqual("True", (string?)accept.Attribute("IsDefault"));
        XElement dismiss = Control("ClipboardSuggestionDismiss");
        Assert.Equal("Button", dismiss.Name.LocalName);
        Assert.Equal("OnClipboardSuggestionDismiss", (string?)dismiss.Attribute("Click"));
        Assert.Same(panel, Assert.Single(markup.Descendants().Where(e =>
            (string?)e.Attribute("KeyDown") == "OnClipboardSuggestionKeyDown")));
        Assert.Same(accept, Assert.Single(markup.Descendants().Where(e =>
            (string?)e.Attribute("Click") == "OnClipboardSuggestionAccept")));
    }

    [Fact]
    public void SourceWiringKeepsPanelKeyboardHandlingForEscapeNotEnterConsent()
    {
        string source = ReadSource("Desktop/Shell/MainWindow.ClipboardMonitor.cs");
        string keyboard = Between(source, "private void OnClipboardSuggestionKeyDown(", "private async void OnClipboardSuggestionAccept(");

        Assert.Contains("if (e.Handled || ShortcutService.IsCapturing || !CanShowClipboardSuggestion) return;", keyboard);
        Assert.Contains("if (e.Key == Key.Escape)", keyboard);
        Assert.Contains("e.Handled = true;", keyboard);
        Assert.Contains("OnClipboardSuggestionDismiss(sender, new RoutedEventArgs());", keyboard);
        Assert.DoesNotContain("Key.Enter", keyboard);
        Assert.DoesNotContain("Key.Space", keyboard);
        Assert.DoesNotContain("OnClipboardSuggestionAccept(", keyboard);
    }

    [Fact]
    public void SourceWiringRefreshesBothRowBusyBoundariesWithoutRepolling()
    {
        string shell = ReadSource("Desktop/Shell/MainWindow.axaml.cs");
        string begin = Between(shell, "private void BeginSessionRows(", "private void PostSessionRowSlice()");
        string complete = Between(shell, "private void ContinueSessionRows()", "_sessionRowsApplied?.TrySetResult();");

        AssertBefore(begin, "_sessionRowsApplying = true;", "RefreshClipboardMonitor();");
        AssertBefore(complete, "_sessionRowsApplying = false;", "RefreshClipboardMonitor();");
        Assert.DoesNotContain("PollClipboardMonitorAsync", begin);
        Assert.DoesNotContain("PollClipboardMonitorAsync", complete);
        Assert.DoesNotContain("ReadClipboardAsync", begin);
        Assert.DoesNotContain("ReadClipboardAsync", complete);
        string monitor = ReadSource("Desktop/Shell/MainWindow.ClipboardMonitor.cs");
        Assert.Contains(".IsEnabled = panel.IsVisible && !RowCommandBusy;", monitor);
        Assert.Contains("if (!_config.MonitorClipboardUrls || !CanShowClipboardSuggestion || RowCommandBusy) return;", monitor);
    }

    private static Task<BoardAdmissionTarget?> Classify(string url) =>
        Task.FromResult<BoardAdmissionTarget?>(new(url, IsBoard: false, AlreadyWatching: false));

    private static string SourcePath(string relativePath, [CallerFilePath] string testPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testPath)!, "../..", relativePath));

    private static string ReadSource(string relativePath) => File.ReadAllText(SourcePath(relativePath));

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Missing source boundary: {start}");
        int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"Missing source boundary: {end}");
        return source[from..to];
    }

    private static void AssertBefore(string source, string first, string second)
    {
        int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex > firstIndex, $"Expected {first} before {second}");
    }
}
