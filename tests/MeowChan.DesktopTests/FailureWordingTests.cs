using System.Net;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>
/// AP3: one failure, one wording. The Downloads row, the Downloads inspector, Diagnostics and the Sources
/// detail all start from <see cref="DiagnosticFailurePresentation"/>, in every bundled language.
/// </summary>
[Collection("Localization")]
public sealed class FailureWordingTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;
    private static readonly DateTimeOffset Retry = new(2031, 2, 3, 4, 5, 0, TimeSpan.Zero);
    private const string Url = "https://watch.invalid/b/thread/123";
    private static readonly ResourceId Id = new("fixture", "b", "123");

    public FailureWordingTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previousLanguage); }

    public static TheoryData<string> AllLanguages()
    {
        Texts.Initialize();
        TheoryData<string> data = new();
        foreach (string code in Texts.Languages.Select(language => language.Code)) data.Add(code);
        return data;
    }

    private static ThreadSnapshot Snapshot(FailureInfo? check, IReadOnlyDictionary<string, FailureInfo>? media = null) =>
        new(Id, Url, "Title", "/synthetic/output", ThreadActivity.Pending, ThreadSourceState.Active, null, 1, 0,
            media ?? new Dictionary<string, FailureInfo>(), check, new DownloadCounts(0, 0));

    private static DesktopSessionView View(DesktopThreadEntry? thread = null, DesktopBoardEntry? board = null) => new(
        thread == null ? Array.Empty<DesktopThreadEntry>() : new[] { thread },
        board == null ? Array.Empty<DesktopBoardEntry>() : new[] { board },
        Array.Empty<string>(), DesktopSessionState.Ready, null);

    private static ProviderDescriptor Provider() =>
        new("fixture", "Fixture", new Uri("https://watch.invalid/"), (ProviderCapabilities)0, new[] { "watch.invalid" }, "1.0.0", CaptureAvailable: false);

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void ThreadCheckFailureUsesTheSameHeadlineOnEverySurface(string language)
    {
        Texts.SetLanguage(language);
        FailureInfo failure = new(FailureKind.Http, "producer-detail-404", HttpStatusCode.NotFound) { RetryAt = Retry };
        DesktopThreadEntry entry = new(42, Snapshot(failure), "fixture", "b");
        string headline = DiagnosticFailurePresentation.Headline(failure, DiagnosticOperation.Thread);
        Assert.StartsWith(Texts.T("failure.short.notFound"), headline);
        Assert.Contains(Texts.T("failure.short.retryAt", Retry.ToLocalTime().ToString("g", Texts.Culture)), headline);

        // Downloads row (status column and compact status).
        using ThreadRow row = new(entry);
        Assert.Contains(headline, row.Status);
        Assert.Contains(headline, row.CompactStatus);

        // Downloads inspector: headline first, the same observed-result sentence Diagnostics uses, then the raw message.
        string[] inspector = row.ErrorDetails.Split(Environment.NewLine);
        Assert.Equal(headline, inspector[0]);
        Assert.Equal(Texts.T("diagnostics.observed.http404"), inspector[1]);
        Assert.Equal("producer-detail-404", inspector[^1]);

        // Diagnostics detail.
        DiagnosticEntry diagnostic = Assert.Single(DiagnosticsSnapshot.From(View(entry)).Entries);
        string[] lines = diagnostic.Details.Split(Environment.NewLine);
        Assert.Contains(Texts.T("failure.short.notFound"), lines);
        Assert.Contains(Texts.T("diagnostics.observed.http404"), lines);

        // Sources detail.
        SourceRow source = Assert.Single(SourcesSnapshot.From(new[] { Provider() }, View(entry)));
        Assert.Contains(DiagnosticFailurePresentation.Line(failure, DiagnosticOperation.Thread), Assert.Single(source.Watches).Observation);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void BoardAndMediaFailuresShareTheWordingToo(string language)
    {
        Texts.SetLanguage(language);
        FailureInfo board = new(FailureKind.Http, "board-detail", HttpStatusCode.Forbidden);
        using BoardRow boardRow = new(new DesktopBoardEntry(7,
            new BoardSnapshot(new ResourceId("fixture", "b"), "https://watch.invalid/b/", "b", "/out", null, board), "fixture", "b"));
        Assert.Contains(Texts.T("failure.short.refused"), boardRow.Status);
        Assert.StartsWith(Texts.T("failure.short.refused"), boardRow.ErrorDetails);
        Assert.Contains(Texts.T("diagnostics.observed.http403"), boardRow.ErrorDetails);

        // Media 404 uses the media sentence in the long form, the same short label as a thread 404.
        FailureInfo media = new(FailureKind.Http, "media-detail", HttpStatusCode.NotFound);
        DesktopThreadEntry entry = new(42, Snapshot(null, new Dictionary<string, FailureInfo> { ["m"] = media }), "fixture", "b");
        using ThreadRow row = new(entry);
        Assert.Contains(DiagnosticFailurePresentation.Line(media, DiagnosticOperation.Media), row.ErrorDetails);
        Assert.StartsWith(Texts.T("failure.short.notFound"), DiagnosticFailurePresentation.Line(media, DiagnosticOperation.Media));
        Assert.Contains(Texts.T("diagnostics.observed.media404"), DiagnosticFailurePresentation.Inspector(media, DiagnosticOperation.Media));
    }

    [Theory]
    [InlineData(FailureKind.Timeout, null, null, "failure.short.timeout")]
    [InlineData(FailureKind.Storage, null, null, "failure.short.storage")]
    [InlineData(FailureKind.InvalidData, null, null, "failure.short.invalidData")]
    [InlineData(FailureKind.Network, null, FailureNetworkStage.NameResolution, "failure.short.dns")]
    [InlineData(FailureKind.Network, null, FailureNetworkStage.Connection, "failure.short.connection")]
    [InlineData(FailureKind.Network, null, FailureNetworkStage.SecureConnection, "failure.short.tls")]
    [InlineData(FailureKind.Http, HttpStatusCode.TooManyRequests, null, "failure.short.throttled")]
    [InlineData(FailureKind.Http, HttpStatusCode.Forbidden, null, "failure.short.refused")]
    [InlineData(FailureKind.Network, null, null, "failure.short.unknown")]
    public void ShortLabelFollowsTheSameFactsAsTheLongSentence(FailureKind kind, HttpStatusCode? http, FailureNetworkStage? stage, string expectedKey)
    {
        FailureInfo failure = new(kind, "opaque", http) { NetworkStage = stage };
        Assert.Equal(Texts.T(expectedKey), DiagnosticFailurePresentation.Short(failure, DiagnosticOperation.Thread));
        // The long form of the same failure always carries the short label as its second line.
        string details = DiagnosticFailurePresentation.Format(failure, DiagnosticOperation.Thread);
        Assert.Equal(Texts.T(expectedKey), details.Split(Environment.NewLine)[1]);
    }

    [Fact]
    public void UnlistedHttpStatusKeepsItsNumberAndNeverClaimsACause()
    {
        FailureInfo failure = new(FailureKind.Http, "server said no", HttpStatusCode.BadGateway);
        Assert.Equal("HTTP 502", DiagnosticFailurePresentation.Short(failure, DiagnosticOperation.Thread));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void NextStepNamesTheButtonExactlyAsLabelled(string language)
    {
        Texts.SetLanguage(language);
        string thread = Texts.T("check.action"), board = Texts.T("check.boardAction"), targets = Texts.T("targets.check");
        FailureInfo http500 = new(FailureKind.Http, "x", HttpStatusCode.InternalServerError);
        FailureInfo refused = new(FailureKind.Http, "x", HttpStatusCode.Forbidden);
        FailureInfo storage = new(FailureKind.Storage, "x");
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.retry", thread),
            DiagnosticFailurePresentation.Format(http500, DiagnosticOperation.Thread));
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.retry", board),
            DiagnosticFailurePresentation.Format(http500, DiagnosticOperation.Board));
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.checkSourceRetry", thread),
            DiagnosticFailurePresentation.Format(refused, DiagnosticOperation.Thread));
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.checkStorage", thread),
            DiagnosticFailurePresentation.Format(storage, DiagnosticOperation.Capture));
        Assert.EndsWith(DiagnosticFailurePresentation.Next("diagnostics.observed.checkStorage", targets),
            DiagnosticFailurePresentation.FormatStorage("/synthetic", "reason", Retry));
        // The label sits in the sentence verbatim (quotation marks are the language's own).
        foreach (string key in new[] { "diagnostics.observed.retry", "diagnostics.observed.checkSourceRetry", "diagnostics.observed.checkStorage" })
        {
            Assert.Contains("{0}", Texts.T(key));
            Assert.Contains(thread, Texts.T(key, thread));
        }
        // Steps without a button do not carry a placeholder.
        Assert.DoesNotContain("{0}", Texts.T("diagnostics.observed.checkSource"));
        Assert.DoesNotContain("{0}", Texts.T("diagnostics.observed.wait"));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void BannerButtonLabelIsShortInEveryLanguage(string language)
    {
        Texts.SetLanguage(language);
        string label = Texts.T("feedback.open");
        // The old label was 36 (en) / 38 (de) characters and wrapped at 760 dip.
        Assert.InRange(label.Length, 4, 26);
        Assert.DoesNotContain("{0}", label);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void DiagnosticsGridColumnHeadersAreLocalised(string language)
    {
        Texts.SetLanguage(language);
        foreach (string key in new[] { "diagnostics.category", "diagnostics.resource", "diagnostics.message" })
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, language + ": " + key);
    }
}
