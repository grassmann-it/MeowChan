using System.Text;
using System.Text.Json;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class JsonContractTests
{
    [Fact]
    public void SnapshotRoundTripPreservesPartialFieldsTextFormatMultipleAttachmentsAndDeletionMarker()
    {
        SourceAttachment first = new SourceAttachment(0, "m-1", Url: new Uri("https://source.test/media/1.png"),
            OriginalFileName: "first.png", Extension: ".png", ExpectedSize: 24, MimeType: "image/png");
        SourceAttachment second = new SourceAttachment(1, "m-2", Url: new Uri("https://source.test/media/2.jpg"),
            OriginalFileName: "second.jpg", Extension: ".jpg");
        SourcePost initial = new SourcePost("123", 0, SourceFieldPresence.Text | SourceFieldPresence.Attachments,
            Text: "<p>Captured content</p>", Attachments: [first, second]);
        SourcePost partial = new SourcePost("124", 1, SourceFieldPresence.Subject | SourceFieldPresence.Text,
            Subject: "", Text: null, TextFormat: SourceTextFormat.PlainText);
        SourcePost deletion = new SourcePost("125", 2, SourceFieldPresence.Attachments,
            Attachments: [new SourceAttachment(0, null, IsDeleted: true)]);
        ThreadReadResult original = new ThreadSnapshot(TestContracts.Snapshot(initial, partial, deletion));

        ThreadReadResult copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize(original));

        SourceThreadSnapshot snapshot = Assert.IsType<ThreadSnapshot>(copy).Snapshot;
        Assert.Empty(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread), copy, TestContracts.Source()));
        Assert.Equal(3, snapshot.Posts.Count);
        Assert.Equal(2, snapshot.Posts[0].Attachments!.Count);
        Assert.Equal("m-2", snapshot.Posts[0].Attachments![1].MediaId);
        Assert.Equal("", snapshot.Posts[1].Subject);
        Assert.Null(snapshot.Posts[1].Text);
        Assert.False(snapshot.Posts[1].Fields.HasFlag(SourceFieldPresence.Author));
        Assert.Equal(SourceTextFormat.PlainText, snapshot.Posts[1].TextFormat);
        Assert.True(snapshot.Posts[2].Attachments![0].IsDeleted);
        Assert.Null(snapshot.Posts[2].Attachments![0].MediaId);
    }

    [Fact]
    public void OptionalFutureFieldsAreIgnoredAndRequiredConstructorFieldsAreChecked()
    {
        byte[] json = Encoding.UTF8.GetBytes("""
            {"kind":"notFound","thread":{"sourceId":"synthetic","boardId":"b","threadId":"123"},"futureOptional":{"value":42}}
            """);
        ThreadNotFound result = Assert.IsType<ThreadNotFound>(SdkJson.Deserialize<ThreadReadResult>(json));
        Assert.Null(result.HttpStatusCode);
        Assert.Empty(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread), result, TestContracts.Source()));
        Assert.Throws<JsonException>(() => SdkJson.Deserialize<ThreadReadResult>(Encoding.UTF8.GetBytes("""
            {"kind":"notFound"}
            """)));
    }

    [Theory]
    [InlineData("{\"kind\":\"notFound\",\"board\":{\"sourceId\":\"synthetic\",\"boardId\":\"b\"}}")]
    [InlineData("{\"kind\":\"futureMandatoryOperation\"}")]
    public void UnknownOrWrongResultKindIsRejected(string json)
        => Assert.Throws<JsonException>(() => SdkJson.Deserialize<CatalogReadResult>(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("{\"outcome\":999,\"reasonCode\":\"x\"}")]
    [InlineData("{\"outcome\":\"UnknownOutcome\",\"reasonCode\":\"x\"}")]
    public void UnknownEnumValuesAreRejected(string json)
        => Assert.Throws<JsonException>(() => SdkJson.Deserialize<ImageFilterResult>(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ThreadAbsenceContainsNoSnapshotAndArchivedSnapshotRetainsLastPosts()
    {
        ThreadReadResult absent = new ThreadNotFound(TestContracts.Thread, 404);
        string absentJson = Encoding.UTF8.GetString(SdkJson.Serialize(absent));
        Assert.DoesNotContain("posts", absentJson);
        Assert.Empty(SdkValidation.ValidateThreadResult(new ThreadReadRequest(TestContracts.Thread), absent, TestContracts.Source()));

        ThreadReadResult terminal = new ThreadSnapshot(TestContracts.Snapshot(new SourcePost("123", 0,
            SourceFieldPresence.Text, Text: "Final captured post")) with { SourceState = SourceThreadState.Archived });
        ThreadSnapshot copy = Assert.IsType<ThreadSnapshot>(SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize(terminal)));
        Assert.Equal(SourceThreadState.Archived, copy.Snapshot.SourceState);
        Assert.Equal("Final captured post", Assert.Single(copy.Snapshot.Posts).Text);
    }

    [Fact]
    public void CatalogAndHttpDeferredResultsRoundTripWithoutPretendingToBeEmptySnapshots()
    {
        DateTimeOffset due = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        CatalogReadResult catalog = new CatalogDeferred(TestContracts.Board, due, "provider_cooldown", 429);
        CatalogDeferred catalogCopy = Assert.IsType<CatalogDeferred>(SdkJson.Deserialize<CatalogReadResult>(SdkJson.Serialize(catalog)));
        Assert.Equal(due, catalogCopy.RetryAt);
        Assert.Empty(SdkValidation.ValidateCatalogResult(new CatalogReadRequest(TestContracts.Board), catalogCopy, TestContracts.Source()));

        HostHttpResult http = new HostHttpDeferred(due, "provider_cooldown", 429);
        HostHttpDeferred httpCopy = Assert.IsType<HostHttpDeferred>(SdkJson.Deserialize<HostHttpResult>(SdkJson.Serialize(http)));
        Assert.Equal(429, httpCopy.HttpStatusCode);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.1.0", false)]
    [InlineData("2.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SdkCompatibilityIsExplicitAndIndependentOfProductOrWireVersion(string? version, bool expected)
        => Assert.Equal(expected, SdkContract.IsSupported(version));

    [Fact]
    public void PublicSdkHasNoCoreDesktopOrCompilerDependency()
    {
        string[] references = typeof(IPlugin).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.DoesNotContain("libmeowchan", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
    }
}
