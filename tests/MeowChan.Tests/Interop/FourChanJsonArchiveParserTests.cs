using System.Text;
using MeowChan.Interop.ThreadArchives;
using Parser = MeowChan.Interop.ThreadArchives.FourChanJsonArchiveParser;

namespace MeowChan.Tests.Interop;

// Handwritten synthetic metadata only. No producer/client/roundtrip evidence.
public sealed class FourChanJsonArchiveParserTests
{
    private const string Op = "\"no\":101,\"resto\":0,\"time\":1700000000,\"now\":\"synthetic\",\"name\":\"HARML0S\"";
    private const string Media = ",\"tim\":9007199254740993,\"filename\":\"synthetic\",\"ext\":\".png\",\"fsize\":123,\"md5\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"w\":20,\"h\":10,\"tn_w\":10,\"tn_h\":5";
    private static string Wrap(string fields) => "{\"posts\":[{" + fields + "}]}";
    private static Parser.Inspection Read(string json, string board = "g") => Parser.Inspect(Encoding.UTF8.GetBytes(json), board);
    private static void Reject(string json)
    {
        var result = Read(json);
        Assert.False(result.Accepted);
        Assert.Null(result.Value);
        Assert.NotEqual(Parser.Error.None, result.Error);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Fact]
    public void ExactIdentityPresenceOrdinalAndHistoricalClaimsOnly()
    {
        string json = "{\"posts\":[{" + Op + ""","sub":"","com":"<span class=\"quote\">x</span>"},""" +
            "{\"no\":104,\"resto\":101,\"time\":1700000002,\"now\":\"synthetic\",\"name\":\"reply\"}," +
            "{\"no\":103,\"resto\":101,\"time\":1700000001,\"now\":\"synthetic\",\"name\":\"reply\"}]}";
        var result = Read(json);
        Assert.True(result.Accepted);
        var archive = result.Value!;
        Assert.Equal("4chan", archive.SourceId);
        Assert.Equal("g", archive.Board);
        Assert.Equal(101L, archive.ThreadNo);
        Assert.Null(archive.ArchivedClaim);
        Assert.Equal(new long[] { 101, 104, 103 }, archive.Posts.Select(p => p.No));
        Assert.Equal(new[] { 0, 1, 2 }, archive.Posts.Select(p => p.Ordinal));
        Assert.Equal("", archive.Posts[0].Subject);
        Assert.Null(archive.Posts[1].Subject);
        Assert.Null(archive.Posts[1].InertHtml);
        Assert.All(archive.Posts, p => Assert.Equal(Parser.AttachmentKind.Absent, p.Attachment.Kind));
    }

    [Fact]
    public void OriginalMetadataIsNotBytesAndDeletionIsNotAbsence()
    {
        var original = Read(Wrap(Op + Media)).Value!.Posts[0].Attachment;
        Assert.Equal(Parser.AttachmentKind.OriginalMetadataOnly, original.Kind);
        Assert.Equal(9007199254740993L, original.Tim);
        var deleted = Read(Wrap(Op + ",\"filedeleted\":1")).Value!.Posts[0].Attachment;
        Assert.Equal(Parser.AttachmentKind.Deleted, deleted.Kind);
        Assert.Null(deleted.Tim);
        Assert.Equal(Parser.AttachmentKind.Absent, Read(Wrap(Op)).Value!.Posts[0].Attachment.Kind);
        Assert.True(Read(Wrap(Op + ",\"archived\":1,\"archived_on\":1700000001")).Value!.ArchivedClaim);
    }

    [Theory]
    [InlineData("")]
    [InlineData("G")]
    [InlineData("../g")]
    [InlineData("https://boards.4chan.org/g/")]
    [InlineData("g?secret=1")]
    public void RequiresTrustedCanonicalBoard(string board)
    {
        Assert.Equal(Parser.Error.InvalidBoard, Read(Wrap(Op), board).Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("\"101\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("9223372036854775808")]
    public void RejectsNonCanonicalOrOutOfLocalRangeIds(string number) => Reject(Wrap(Op.Replace("\"no\":101", "\"no\":" + number)));

    [Theory]
    [InlineData(",\"name\":\"duplicate\"")]
    [InlineData(",\"n\\u0061me\":\"duplicate\"")]
    [InlineData(",\"com\":null")]
    [InlineData(",\"com\":\"\\uD800\"")]
    [InlineData(",\"com\":\"<script>secret</script>\"")]
    [InlineData(",\"com\":\"<img src='secret'>\"")]
    [InlineData(",\"com\":\"<a href='javascript:secret'>x</a>\"")]
    [InlineData(",\"com\":\"https://user:secret@example.invalid/?token=secret\"")]
    [InlineData(",\"url\":\"https://secret.invalid\"")]
    [InlineData(",\"deleted\":1")]
    [InlineData(",\"extra_files\":[]")]
    [InlineData(",\"future_authority\":{}")]
    [InlineData(",\"archived\":0")]
    [InlineData(",\"archived\":true")]
    [InlineData(",\"archived_on\":1700000001")]
    [InlineData(",\"tim\":9001")]
    [InlineData(",\"tn_w\":10,\"tn_h\":5")]
    [InlineData(",\"filedeleted\":1,\"tim\":9001")]
    public void RejectsUnsafeAmbiguousAndPartialMetadata(string suffix) => Reject(Wrap(Op + suffix));

    [Fact]
    public void KnownIgnoredFieldsAreTypedLostAndNeverAuthority()
    {
        var result = Read(Wrap(Op + ",\"id\":\"secret\",\"semantic_url\":\"https://secret.invalid\",\"replies\":900"));
        Assert.True(result.Accepted);
        Assert.Equal(4, result.Value!.IgnoredKnownFields); // includes now
        Assert.DoesNotContain("secret", result.ToString());
        Reject(Wrap(Op + ",\"id\":{}"));
        Reject(Wrap(Op + ",\"replies\":1e2"));
    }

    [Fact]
    public void DiscardedKnownPrivateAndUrlValuesDoNotEnterAnyProjectedField()
    {
        const string discarded = "https://user:private-marker@private.invalid/a?sig=acquisition-marker";
        string fields = Op.Replace("\"now\":\"synthetic\"", "\"now\":\"" + discarded + "\"") + Media;
        foreach (string key in new[] { "trip", "id", "capcode", "country", "country_name", "board_flag", "flag_name", "semantic_url", "tag" })
            fields += ",\"" + key + "\":\"" + discarded + "\"";
        var result = Read(Wrap(fields));
        Assert.True(result.Accepted);
        Assert.Equal(Parser.Error.None, result.Error);
        var archive = result.Value!;
        Assert.Equal(10, archive.IgnoredKnownFields);
        Assert.Equal("4chan", archive.SourceId);
        Assert.Equal("local-4chan-api-json-metadata-v1", archive.ProfileId);
        Assert.Equal("g", archive.Board);
        Assert.Equal(101L, archive.ThreadNo);
        Assert.Null(archive.ArchivedClaim);
        var post = Assert.Single(archive.Posts);
        Assert.Equal(101L, post.No);
        Assert.Equal(0L, post.Resto);
        Assert.Equal(0, post.Ordinal);
        Assert.Equal(1700000000L, post.UnixSeconds);
        Assert.Equal("HARML0S", post.Name);
        Assert.Null(post.Subject);
        Assert.Null(post.InertHtml);
        var file = post.Attachment;
        Assert.Equal(Parser.AttachmentKind.OriginalMetadataOnly, file.Kind);
        Assert.Equal(9007199254740993L, file.Tim);
        Assert.Equal("synthetic", file.FileName);
        Assert.Equal(".png", file.Extension);
        Assert.Equal(123L, file.Size);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAA==", file.Md5);
        Assert.Equal(20L, file.Width);
        Assert.Equal(10L, file.Height);
        Assert.Equal(10L, file.ThumbnailWidth);
        Assert.Equal(5L, file.ThumbnailHeight);
        // Explicit public/internal instance-property inventory guards future extension bags/URL slots.
        Assert.Equal(new[] { "ArchivedClaim", "Board", "IgnoredKnownFields", "Posts", "ProfileId", "SourceId", "ThreadNo" },
            typeof(Parser.Archive).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(p => p.GetMethod is { } getter && (getter.IsPublic || getter.IsAssembly)).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { "Attachment", "InertHtml", "Name", "No", "Ordinal", "Resto", "Subject", "UnixSeconds" },
            typeof(Parser.Post).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(p => p.GetMethod is { } getter && (getter.IsPublic || getter.IsAssembly)).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { "Extension", "FileName", "Height", "Kind", "Md5", "Size", "ThumbnailHeight", "ThumbnailWidth", "Tim", "Width" },
            typeof(Parser.Attachment).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(p => p.GetMethod is { } getter && (getter.IsPublic || getter.IsAssembly)).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("file_url")]
    [InlineData("thumbnail_file_url")]
    [InlineData("cookies")]
    [InlineData("authorization")]
    [InlineData("private_notes")]
    public void UnclassifiedAcquisitionOrPrivateFieldsProduceNoPartialDto(string key)
    {
        var result = Read(Wrap(Op + Media + ",\"" + key + "\":\"https://user:private-marker@private.invalid/?sig=acquisition-marker\""));
        Assert.False(result.Accepted);
        Assert.Equal(Parser.Error.UnsupportedField, result.Error);
        Assert.Null(result.Value); // no partial Archive/Post/Attachment survives rejection
    }

    [Fact]
    public void AllowedProseIsNotSubjectToAGeneralSecretSubstringBan()
    {
        var result = Read(Wrap(Op + ",\"sub\":\"secret private-marker\",\"com\":\"<b>secret acquisition-marker</b>\""));
        Assert.True(result.Accepted);
        var post = Assert.Single(result.Value!.Posts);
        Assert.Equal("secret private-marker", post.Subject);
        Assert.Equal("<b>secret acquisition-marker</b>", post.InertHtml);
        Assert.Equal(Parser.AttachmentKind.Absent, post.Attachment.Kind);
    }

    [Fact]
    public void ExactRelationshipsAndRequiredSchema()
    {
        Reject("{\"posts\":[]}");
        Reject(Wrap(Op.Replace("\"resto\":0", "\"resto\":101")));
        Reject(Wrap(Op.Replace("\"time\":1700000000,", "")));
        Reject(Wrap(Op.Replace("\"time\":1700000000", "\"time\":253402300800")));
        foreach (string reply in new[] {
            "\"no\":102,\"resto\":999", "\"no\":102,\"resto\":0", "\"no\":101,\"resto\":101", "\"no\":100,\"resto\":101",
            "\"no\":102,\"resto\":101,\"archived\":1" })
            Reject("{\"posts\":[{" + Op + "},{" + reply + ",\"time\":1700000001,\"now\":\"synthetic\",\"name\":\"reply\"}]}");
        Reject("{\"board\":\"g\",\"posts\":[{" + Op + "}]}");
    }

    [Fact]
    public void AttachmentFieldsAreStrictClaimsNotThumbnailFallbacks()
    {
        foreach (var pair in new[] { (".png", ".exe"), ("\"fsize\":123", "\"fsize\":-1"),
            ("\"w\":20", "\"w\":0"), ("\"tn_h\":5", "\"tn_h\":2147483648"),
            ("AAAAAAAAAAAAAAAAAAAAAA==", "not-a-digest"), ("synthetic", "../secret") })
            Reject(Wrap(Op + Media.Replace(pair.Item1, pair.Item2)));
    }

    [Fact]
    public void ByteUtf8DepthAndPostBoundsApplyBeforeProjection()
    {
        string valid = Wrap(Op);
        Assert.True(Read(valid + new string(' ', Parser.MaxBytes - Encoding.UTF8.GetByteCount(valid))).Accepted);
        Assert.False(Read(valid + new string(' ', Parser.MaxBytes + 1 - Encoding.UTF8.GetByteCount(valid))).Accepted);
        Assert.False(Parser.Inspect(new byte[Parser.MaxBytes + 1], "g").Accepted);
        Assert.False(Parser.Inspect(new byte[] { 0xff }, "g").Accepted);
        Assert.False(Parser.Inspect(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(Wrap(Op))).ToArray(), "g").Accepted);
        Reject(Wrap(Op + ",\"future\":[[[[[[[[[0]]]]]]]]]"));
        var posts = new List<string> { "{" + Op + Media + "}" };
        for (int i = 1; i < Parser.MaxPosts; i++)
            posts.Add("{\"no\":" + (101 + i) + ",\"resto\":101,\"time\":1,\"now\":\"synthetic\",\"name\":\"x\",\"filedeleted\":1}");
        Assert.True(Read("{\"posts\":[" + string.Join(',', posts) + "]}").Accepted);
        posts.Add("{\"no\":99999,\"resto\":101,\"time\":1,\"now\":\"synthetic\",\"name\":\"x\"}");
        Reject("{\"posts\":[" + string.Join(',', posts) + "]}");
        // Attachments are at most one per post; MaxAttachments == MaxPosts.
        Assert.Equal(Parser.MaxPosts, Parser.MaxAttachments);
    }
}
