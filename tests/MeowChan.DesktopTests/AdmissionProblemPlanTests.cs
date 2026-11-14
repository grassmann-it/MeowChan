using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class AdmissionProblemPlanTests
{
    [Fact]
    public void RejectedUrlsKeepFirstSeenOrderOncePerUrlAndSkipAcceptedOnes()
    {
        IReadOnlyList<string> rejected = AdmissionProblemPlan.RejectedUrls(new (string, bool)[]
        {
            ("https://boards.4chan.org/g/thread/1", false), ("not a url", true), (" https://example.invalid/x ", true),
            ("not a url", true), ("", true), ("https://boards.4chan.org/g/thread/2", false)
        });

        Assert.Equal(new[] { "not a url", "https://example.invalid/x" }, rejected);
        Assert.Empty(AdmissionProblemPlan.RejectedUrls(new (string, bool)[] { ("https://boards.4chan.org/g/thread/1", false) }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void ABlankFieldBecomesTheRejectedUrls(string? field) =>
        Assert.Equal("a" + Environment.NewLine + "b", AdmissionProblemPlan.FieldText(field, "whatever", new[] { "a", "b" }));

    [Fact]
    public void AFieldThatStillHoldsTheSubmittedTextIsReplacedByTheRejectedUrlsAlone()
    {
        const string submitted = "https://boards.4chan.org/g/thread/1\nnot a url\nhttps://boards.4chan.org/g/thread/2";
        Assert.Equal("not a url", AdmissionProblemPlan.FieldText(submitted + "\n", submitted, new[] { "not a url" }));
    }

    [Fact]
    public void TextTypedSinceIsKeptAndOnlyMissingRejectedUrlsAreAppended()
    {
        string typed = "https://boards.4chan.org/g/thread/9, bad";
        string result = AdmissionProblemPlan.FieldText(typed, "other", new[] { "bad", "worse" });

        Assert.Equal(typed + Environment.NewLine + "worse", result);
        // Every rejected URL is already there: nothing changes, nothing is duplicated.
        Assert.Equal(typed, AdmissionProblemPlan.FieldText(typed, "other", new[] { "bad" }));
        Assert.Equal(typed, AdmissionProblemPlan.FieldText(typed, "other", Array.Empty<string>()));
    }
}
