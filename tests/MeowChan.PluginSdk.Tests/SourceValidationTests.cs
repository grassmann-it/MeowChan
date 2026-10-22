using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class SourceValidationTests
{
    [Fact]
    public void UnchangedRequiresAnAcceptedBaselineAndCarriesNoPosts()
    {
        ThreadReadResult unchanged = new ThreadUnchanged(TestContracts.Thread, TestContracts.Validators);
        Assert.Contains(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread), unchanged,
            TestContracts.Source()), issue => issue.Code == "missing_baseline");
        Assert.Empty(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread,
            TestContracts.Validators, HasSnapshot: true), unchanged, TestContracts.Source()));
        CatalogReadResult catalog = new CatalogUnchanged(TestContracts.Board, TestContracts.Validators);
        Assert.Contains(SdkValidation.ValidateCatalogResult(new CatalogReadRequest(TestContracts.Board), catalog,
            TestContracts.Source()), issue => issue.Code == "missing_baseline");
    }

    [Fact]
    public void WrongResourceDuplicatePostsAndInvalidMediaRelationshipsAreRejected()
    {
        SourcePost post = new SourcePost("123", 0, SourceFieldPresence.Attachments,
            Attachments: [new SourceAttachment(0, "m-1", Url: new Uri("https://source.test/m.png")),
                new SourceAttachment(0, "m-2", Url: new Uri("https://unapproved.test/m.png"))]);
        ThreadReadResult response = new ThreadSnapshot(TestContracts.Snapshot(post, post)
            with { Thread = TestContracts.Thread with { ThreadId = "999" } });
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread),
            response, TestContracts.Source());
        Assert.Contains(issues, issue => issue.Code == "wrong_resource");
        Assert.Contains(issues, issue => issue.Code == "duplicate_post");
        Assert.Contains(issues, issue => issue.Code == "duplicate_position");
        Assert.Contains(issues, issue => issue.Code == "host_not_allowed");
    }

    [Theory]
    [InlineData("https://source.test/b/thread/999")]
    [InlineData("https://source.test/other/thread/123")]
    [InlineData("https://source.test/login")]
    [InlineData("https://source.test/b/thread/123?wrong=1")]
    [InlineData("https://source.test/b/thread/123#wrong")]
    public void CanonicalHostAloneDoesNotProveCorrectThreadReference(string url)
    {
        ThreadReadResult result = new ThreadSnapshot(TestContracts.Snapshot() with { Url = new Uri(url) });
        Assert.Contains(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread), result,
            TestContracts.Source()), issue => issue.Code == "noncanonical_url");
        CatalogReadResult catalog = new CatalogSnapshot(new SourceCatalogSnapshot(TestContracts.Board,
            [new SourceThreadReference(TestContracts.Thread, new Uri(url))], IsComplete: true));
        Assert.Contains(SdkValidation.ValidateCatalogResult(new CatalogReadRequest(TestContracts.Board), catalog,
            TestContracts.Source()), issue => issue.Code == "noncanonical_url");
    }

    [Fact]
    public void CompleteAndPartialCatalogsDoNotExpressThreadDeletion()
    {
        CatalogReadResult empty = new CatalogSnapshot(new SourceCatalogSnapshot(TestContracts.Board,
            Array.Empty<SourceThreadReference>(), IsComplete: true));
        Assert.Empty(SdkValidation.ValidateCatalogResult(new CatalogReadRequest(TestContracts.Board), empty,
            TestContracts.Source()));
        CatalogReadResult partial = new CatalogSnapshot(new SourceCatalogSnapshot(TestContracts.Board,
            [new SourceThreadReference(TestContracts.Thread, TestContracts.ThreadUrl)], IsComplete: false, NextPage: 2));
        Assert.Empty(SdkValidation.ValidateCatalogResult(new CatalogReadRequest(TestContracts.Board, Page: 1), partial,
            TestContracts.Source()));
    }

    [Fact]
    public void MissingPresenceAndInvalidHashesAreRejectedButExplicitEmptyTextIsValid()
    {
        SourcePost bad = new SourcePost("123", 0, SourceFieldPresence.Attachments, Text: "Undeclared content",
            Attachments: [new SourceAttachment(0, "m-1", Url: new Uri("https://source.test/m.png"),
                ExpectedMd5: "invalid", ExpectedSha256: "invalid")]);
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread),
            new ThreadSnapshot(TestContracts.Snapshot(bad)), TestContracts.Source());
        Assert.Contains(issues, issue => issue.Code == "presence_mismatch");
        Assert.Equal(2, issues.Count(issue => issue.Code == "invalid_hash"));
        SourcePost empty = new SourcePost("123", 0, SourceFieldPresence.Text, Text: "");
        Assert.Empty(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread),
            new ThreadSnapshot(TestContracts.Snapshot(empty)), TestContracts.Source()));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public void TechnicalHttpFailuresCannotPretendToBeThreadAbsence(int status)
        => Assert.Contains(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread),
            new ThreadNotFound(TestContracts.Thread, status), TestContracts.Source()), issue => issue.Code == "not_terminal");

    [Fact]
    public void DecimalOrderingSupportsLargeOpaqueLengthWithoutIntOverflowAndRejectsNondigits()
    {
        SourceDescription source = TestContracts.Source() with { PostOrder = PostOrderKind.DecimalPostId };
        ThreadReadResult large = new ThreadSnapshot(TestContracts.Snapshot(new SourcePost(new string('9', 100), 0,
            SourceFieldPresence.None)));
        Assert.Empty(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread), large, source));
        ThreadReadResult invalid = new ThreadSnapshot(TestContracts.Snapshot(new SourcePost("opaque", 0, SourceFieldPresence.None)));
        Assert.Contains(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread), invalid, source),
            issue => issue.Code == "invalid_order");
    }
}
