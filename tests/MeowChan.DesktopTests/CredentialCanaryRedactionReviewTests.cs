using System.Net;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Http;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class CredentialCanaryRedactionReviewTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public CredentialCanaryRedactionReviewTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage(_previousLanguage);

    [Fact]
    public void ForeignUrlBeforeDanbooruUrlIsPreservedWhileTheRelevantAddressIsRedacted()
    {
        Canaries canaries = new();
        string foreign = "https://foreign.invalid/path/danbooru.donmai.us/is-not-the-host";
        string relevant = canaries.Url("danbooru.donmai.us");
        string input = "prefix " + foreign + " then " + relevant + " trailing context";

        string output = DesktopLog.RedactText(input);

        Assert.Contains(foreign, output);
        Assert.Contains("prefix", output);
        Assert.Contains("then [Danbooru address]", output);
        Assert.DoesNotContain(canaries.Secret, output);
    }

    [Fact]
    public void LineRemainderIsConsumedOnlyAfterAnActualDanbooruAuthority()
    {
        const string foreign = "https://foreign.invalid/notes/danbooru.donmai.us";
        string input = foreign + " and ordinary context";

        Assert.Equal(input, DesktopLog.RedactText(input));
    }

    [Fact]
    public void UnencodedSpacesInRelevantQueryStillCannotLeaveTheSecretBehind()
    {
        Canaries canaries = new();
        string input = "failure: " + canaries.Url("safebooru.donmai.us")
            .Replace("tags=cat&", "tags=cat dog&", StringComparison.Ordinal) + " trailing context";

        string output = DesktopLog.RedactText(input);

        Assert.Contains("failure: [Danbooru address]", output);
        Assert.DoesNotContain(canaries.Secret, output);
    }

    [Fact]
    public void SharedDiagnosticPresentationRedactsMessagesAndCodesWithoutMutatingFailureFacts()
    {
        Canaries canaries = new();
        string url = canaries.Url("safebooru.donmai.us");
        FailureInfo failure = new(FailureKind.Http, "producer failure: " + url, HttpStatusCode.Unauthorized)
        {
            DiagnosticCode = "producer-code: " + url
        };
        RequestPolicyProgress progress = new(RequestPolicyPhase.Sending, TimeSpan.Zero, 1);

        string[] outputs =
        {
            DiagnosticFailurePresentation.WatchUrl(url),
            DiagnosticFailurePresentation.Line(failure, DiagnosticOperation.Thread),
            DiagnosticFailurePresentation.Inspector(failure, DiagnosticOperation.Thread),
            DiagnosticFailurePresentation.Format(failure, DiagnosticOperation.Thread, progress),
            DiagnosticFailurePresentation.FormatRequest(DiagnosticOperation.Thread, progress, "status: " + url),
            DiagnosticFailurePresentation.FormatWork(new ThreadWorkSnapshot(
                url, WorkIntent.Watching, ThreadSourceStateValue.Active, null, false, Array.Empty<MediaWork>()),
                "durable detail: " + url)
        };

        Assert.All(outputs, output =>
        {
            Assert.Contains("[Danbooru address]", output);
            Assert.DoesNotContain(canaries.Secret, output);
        });
        Assert.Contains(canaries.Secret, failure.Message);
        Assert.Contains(canaries.Secret, failure.DiagnosticCode!);
    }

    private sealed class Canaries
    {
        public string Secret { get; } = "MCCANARY-REVIEW-" + Guid.NewGuid().ToString("N");

        public string Url(string host) =>
            $"https://review-user:{Secret}@{host}/posts?tags=cat&api_key={Secret}";
    }
}
