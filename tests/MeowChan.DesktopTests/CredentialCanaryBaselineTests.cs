using MeowChan.Interfaces;
using MeowChan.Downloader;
using System.Net;
using System.Runtime.CompilerServices;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeowChan.DesktopTests;

// No App, window, session/profile, transport or credential store is constructed here. These are
// boundary tests, NOT a headless MainWindow.OnAddUrl/notification/clipboard-copy journey. In particular,
// the fake admission below must receive the original input; a fake that redacts it would hide a leak.
[Collection("Localization")]
public sealed class CredentialCanaryBaselineTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public CredentialCanaryBaselineTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage(_previousLanguage);

    [Theory]
    [InlineData("danbooru.donmai.us", false)]
    [InlineData("danbooru.donmai.us", true)]
    [InlineData("safebooru.donmai.us", false)]
    [InlineData("safebooru.donmai.us", true)]
    public async Task AddInputPassesUserinfoIntactToAdmissionAndTheRealAddressParserRejectsIt(string host, bool clipboard)
    {
        Canaries canaries = new();
        string input = canaries.Url(host, userinfo: true);
        List<string> received = new();
        int reads = 0;
        await AddInput.SubmitAsync(clipboard ? "" : input,
            () => { reads++; return Task.FromResult<string?>(input); },
            entry =>
            {
                received.Add(entry);
                AssertCanariesArrived(canaries, entry, userinfo: true);
                Assert.False(DanbooruAddress.TryParse(entry, out DanbooruAddress? address));
                Assert.Null(address);
                return Task.CompletedTask;
            });

        Assert.Equal(clipboard ? 1 : 0, reads);
        Assert.Equal(input, Assert.Single(received)); // Do not silently scrub the admission/request input.
        AssertClean(canaries, DanbooruAddress.RedactForLog(input));
    }

    [Theory]
    [InlineData("danbooru.donmai.us")]
    [InlineData("safebooru.donmai.us")]
    public async Task RecognisedQueryOnlyAddressKeepsTagsButNotCredentialParametersInItsProjection(string host)
    {
        Canaries canaries = new();
        string input = canaries.Url(host, userinfo: false);
        int calls = 0;
        await AddInput.SubmitAsync(input, () => throw new InvalidOperationException("Unexpected clipboard read"),
            entry =>
            {
                calls++;
                Assert.Equal(input, entry);
                AssertCanariesArrived(canaries, entry, userinfo: false);
                Assert.True(DanbooruAddress.TryParse(entry, out DanbooruAddress? address));
                Assert.NotNull(address);
                Assert.Equal(DanbooruAddressKind.Search, address.Kind);
                Assert.Equal("cat", address.Tags);
                string prefill = address.PrefilledQuery(out bool addedSafeRating);
                Assert.Equal(host.StartsWith("safebooru", StringComparison.Ordinal), addedSafeRating);
                Assert.Equal(addedSafeRating ? "cat rating:g" : "cat", prefill);
                Assert.Equal($"https://{host}/posts", address.CanonicalText);
                AssertClean(canaries, address.ToString(), prefill, address.CanonicalText,
                    DanbooruAddress.RedactForLog(entry));
                return Task.CompletedTask;
            });
        Assert.Equal(1, calls);
        AssertCanariesArrived(canaries, input, userinfo: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultingAdmissionInputReachesTheLastNetLogAndBothOutputFormatters(bool userinfo)
    {
        Canaries canaries = new();
        string url = canaries.Url("danbooru.donmai.us", userinfo);
        int calls = 0;
        // Exercise the real splitter and a faulting admission boundary, then the real non-UI last-net
        // logger. This deliberately does not pretend to invoke MainWindow.AddUrlAsync's catch block.
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AddInput.SubmitAsync(url, () => throw new InvalidOperationException("Unexpected clipboard read"), entry =>
            {
                calls++;
                Assert.Equal(url, entry);
                AssertCanariesArrived(canaries, entry, userinfo);
                return FailAdmission(entry);
            }));
        Assert.Equal(1, calls);
        AssertCanariesArrived(canaries, failure.ToString(), userinfo);

        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-aq1-log-");
        try
        {
            CapturingConsoleFormatSink console = new();
            using (Logger log = DesktopLog.Configure(new LoggerConfiguration().WriteTo.Sink(console), root).CreateLogger())
            {
                UnhandledErrors errors = new(log, failHard: false,
                    _ => throw new InvalidOperationException("No exit expected from task observation"));
                errors.OnUnobservedTask(this, new UnobservedTaskExceptionEventArgs(new AggregateException(failure)));
                // A raw structured/nested property and a literal URL must be scrubbed too, not just
                // the pre-redacted {Url} used by the current shell catch block.
                log.Warning("AQ1 structured URL {Url} {@Echo}", url, new { Address = url });
                log.Information("AQ1 literal URL: " + url);
            }

            Assert.Equal(3, console.Events.Count); // Empty/filtered sinks cannot pass.
            string file = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "meowchan-*.log")));
            string formattedConsole = console.Output.ToString();
            foreach (string output in new[] { file, formattedConsole })
            {
                Assert.Contains("Unobserved task exception.", output);
                Assert.Contains("Synthetic admission failure", output);
                Assert.Contains("Synthetic upstream HTTP 401", output);
                Assert.Contains(nameof(InvalidOperationException), output);
                Assert.Contains(nameof(IOException), output);
                Assert.Contains(nameof(FailAdmission), output); // Retain the real stack, not a replacement exception.
                Assert.Contains("AQ1 structured URL", output);
                Assert.Contains("AQ1 literal URL", output);
                Assert.Contains("[Danbooru address]", output);
                AssertClean(canaries, output);
            }
            // Rendering does not mutate the original error or Serilog event/request data.
            AssertCanariesArrived(canaries, failure.ToString(), userinfo);
            Assert.Equal(url, Assert.IsType<ScalarValue>(console.Events[1].Properties["Url"]).Value);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticSnapshotRedactsDisplayAndCopyTextButPreservesFactsAndActionIdentity(bool userinfo)
    {
        Canaries canaries = new();
        string url = canaries.Url("safebooru.donmai.us", userinfo);
        FailureInfo failure = new(FailureKind.Http, "Synthetic HTTP 401 for " + url, HttpStatusCode.Unauthorized);
        ResourceId id = new("fixture", "b", "123");
        ThreadSnapshot original = new(id, url, "Synthetic title " + url, "/synthetic/output",
            ThreadActivity.Pending, ThreadSourceState.Active, null, 1, 0,
            new Dictionary<string, FailureInfo> { ["synthetic-media"] = failure }, failure, new DownloadCounts(2, 1));
        DesktopSessionView view = new(new[] { new DesktopThreadEntry(42, original, "fixture", "b") },
            Array.Empty<DesktopBoardEntry>(), new[] { "Synthetic warning " + url },
            DesktopSessionState.Ready, "Synthetic startup error " + url);
        AssertCanariesArrived(canaries, view.Threads[0].Snapshot.Url, userinfo);
        AssertCanariesArrived(canaries, failure.Message, userinfo);

        DiagnosticsSnapshot snapshot = DiagnosticsSnapshot.From(view);
        Assert.Equal(4, snapshot.Entries.Count); // Startup, warning, check and media all reach the projection.
        Assert.Equal(2L, snapshot.Queued);
        Assert.Equal(1L, snapshot.Active);
        DiagnosticEntry check = Assert.Single(snapshot.Entries, entry => entry.Key == "Thread:42:check");
        Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), check.Target);
        Assert.Equal(id, check.ResourceId);
        Assert.Equal(DiagnosticKind.Check, check.Kind);
        Assert.True(check.IsProblem);
        Assert.Contains("HTTP 401", check.Details);
        Assert.Contains("Synthetic HTTP 401", check.Summary);
        Assert.Contains("[Danbooru address]", check.Details);
        Assert.True(DiagnosticsSnapshot.ContainsTarget(view, check.Target!));
        foreach (DiagnosticEntry entry in snapshot.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Details));
            // CopyFrozenDiagnosticAsync passes exactly Details to the clipboard adapter. No real clipboard.
            AssertClean(canaries, entry.Resource, entry.Summary, entry.Details);
        }
        Assert.Equal(url, original.Url);
        Assert.Same(failure, original.LastCheckFailure);
        AssertCanariesArrived(canaries, original.LastCheckFailure!.Message, userinfo);
    }

    [Fact]
    public void RedactionKeepsUnrelatedTextAndRemovesEachCredentialAddressInsideErrorContext()
    {
        Canaries first = new(), second = new();
        const string publicUrl = "https://watch.invalid/b/thread/123?context=public";
        string spacedQuery = first.Url("danbooru.donmai.us", true)
            .Replace("tags=cat&", "tags=cat dog&", StringComparison.Ordinal);
        AssertCanariesArrived(first, spacedQuery, userinfo: true);
        string input = $"HTTP 401 ({spacedQuery})\n"
            + $"inner: <{second.Url("safebooru.donmai.us", false)}>\nstack frame\n{publicUrl}";
        string output = DesktopLog.RedactText(input);
        Assert.Contains("HTTP 401", output);
        Assert.Contains("inner:", output);
        Assert.Contains("stack frame", output);
        Assert.Contains(publicUrl, output);
        Assert.Equal(2, output.Split("[Danbooru address]", StringSplitOptions.None).Length - 1);
        AssertClean(first, output);
        AssertClean(second, output);
        Assert.Equal("plain error", DesktopLog.RedactText("plain error"));
        Assert.Equal(string.Empty, DesktopLog.RedactText(string.Empty));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task FailAdmission(string url) => throw new InvalidOperationException(
        "Synthetic admission failure for " + url, new IOException("Synthetic upstream HTTP 401 for " + url));

    private static void AssertCanariesArrived(Canaries canaries, string text, bool userinfo)
    {
        Assert.Contains(canaries.Query, text);
        if (!userinfo) return;
        Assert.Contains(canaries.User, text);
        Assert.Contains(canaries.Password, text);
    }

    private static void AssertClean(Canaries canaries, params string[] outputs)
    {
        foreach (string output in outputs)
            foreach (string value in new[] { canaries.User, canaries.Password, canaries.Query })
                Assert.DoesNotContain(value, output);
    }

    private sealed class Canaries
    {
        public string User { get; } = "MCCANARY-USER-" + Guid.NewGuid().ToString("N");
        public string Password { get; } = "MCCANARY-PASSWORD-" + Guid.NewGuid().ToString("N");
        public string Query { get; } = "MCCANARY-QUERY-" + Guid.NewGuid().ToString("N");
        public string Url(string host, bool userinfo) =>
            $"https://{(userinfo ? User + ":" + Password + "@" : "")}{host}/posts?tags=cat&api_key={Query}";
    }

    private sealed class CapturingConsoleFormatSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public StringWriter Output { get; } = new();
        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
            DesktopLog.ConsoleFormatter.Format(logEvent, Output);
        }
    }
}
