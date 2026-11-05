using System;
using System.Linq;
using System.Text;
using MeowChan.Interop.ThreadArchives;
using P = MeowChan.Interop.ThreadArchives.YChanExArchiveParser;

namespace MeowChan.Tests.Interop;

// Handwritten synthetic, DataMember-derived cases; NOT actual YChanEx output/roundtrips.
public sealed class YChanExArchiveParserTests
{
    private const string Op = "{\"post_id\":101,\"first\":true,\"poster_name\":\"Synthetic\",\"files\":[]}";
    private static string Envelope(string posts = Op, string extra = "") =>
        "{\"Id\":\"101\",\"Board\":\"g\",\"Url\":\"https://boards.4chan.org/g/thread/101\",\"ThreadPosts\":[" + posts + "]" + extra + "}";
    private static P.Result Inspect(string json) => P.Inspect(Encoding.UTF8.GetBytes(json), P.Profile);
    private static void Rejected(P.Result result, P.Error? error = null)
    {
        Assert.False(result.Success);
        Assert.Null(result.Archive);
        Assert.NotEqual(P.Error.None, result.Error);
        if (error.HasValue) Assert.Equal(error.Value, result.Error);
    }

    [Fact]
    public void ExplicitProfilePreservesIdentityOrderAndTextPresenceOnly()
    {
        string posts = Op + ",{\"post_id\":18446744073709551615,\"post_message\":\"\",\"post_subject\":null}";
        P.Result result = Inspect(Envelope(posts));
        Assert.True(result.Success);
        var archive = result.Archive!;
        Assert.Equal(P.Profile, archive.Profile);
        Assert.Equal("4chan", archive.Source);
        Assert.Equal("g", archive.Board);
        Assert.Equal("101", archive.ThreadId);
        Assert.Equal(new ulong[] { 101, ulong.MaxValue }, archive.Posts.Select(x => x.Id));
        Assert.Equal(P.Presence.Missing, archive.Posts[0].Message.Presence);
        Assert.Equal(P.Presence.Null, archive.Posts[1].Subject.Presence);
        Assert.Equal(P.Presence.Value, archive.Posts[1].Message.Presence);
        Assert.Equal("", archive.Posts[1].Message.Value);
        Assert.Equal(P.Presence.Value, archive.Posts[0].FilesPresence);
        Assert.Equal(P.Presence.Missing, archive.Posts[1].FilesPresence);
        Assert.Equal(P.Presence.Missing, archive.Posts[1].Author.Presence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ychanex-master")]
    [InlineData("ychanex-00b16bfa300ea91dac303b411d817a583a779874-partial-v2")]
    public void NeverAutodetectsOrUpgradesProfile(string? profile) =>
        Rejected(P.Inspect(Encoding.UTF8.GetBytes(Envelope()), profile), P.Error.UnsupportedProfile);

    [Theory]
    [InlineData("https://boards.4chan.org/g/thread/102")]
    [InlineData("https://boards.4chan.org/a/thread/101")]
    [InlineData("https://boards.4chan.org.evil.invalid/g/thread/101")]
    [InlineData("https://user:secret@boards.4chan.org/g/thread/101")]
    [InlineData("https://boards.4chan.org/g/thread/101?token=secret")]
    [InlineData("https://boards.4chan.org/g/thread/101#p101")]
    [InlineData("https://boards.4chan.org:443/g/thread/101")]
    [InlineData("http://boards.4chan.org/g/thread/101")]
    [InlineData("https://127.0.0.1/g/thread/101")]
    [InlineData("https://boards.4chan.org/g/thread/%31%30%31")]
    public void SourceIsExactAllowlistedPublicPage(string url) =>
        Rejected(Inspect(Envelope().Replace("https://boards.4chan.org/g/thread/101", url)), P.Error.IdentityMismatch);

    [Theory]
    [InlineData("101.0")]
    [InlineData("1.01e2")]
    [InlineData("-0")]
    [InlineData("0")]
    [InlineData("18446744073709551616")]
    [InlineData("\"101\"")]
    public void PostIdsAreExactNonzeroUInt64(string id) =>
        Rejected(Inspect(Envelope(Op.Replace("\"post_id\":101", "\"post_id\":" + id))), P.Error.InvalidSchema);

    [Fact]
    public void OpAndPostUniquenessAreCheckedWithoutRepair()
    {
        Rejected(Inspect(Envelope(Op.Replace("true", "false"))), P.Error.IdentityMismatch);
        Rejected(Inspect(Envelope(Op.Replace(":101", ":102"))), P.Error.IdentityMismatch);
        Rejected(Inspect(Envelope(Op + "," + Op)), P.Error.InvalidSchema);
        Rejected(Inspect(Envelope("{\"post_id\":102}," + Op)), P.Error.IdentityMismatch);
        Rejected(Inspect(Envelope("")), P.Error.InvalidSchema);
    }

    [Fact]
    public void AttachmentNamesAreSeparateAndNoStatusAttestsBytes()
    {
        const string file = "{\"file_id\":\"7001\",\"original_file_name\":\"original\",\"saved_file\":null," +
            "\"saved_thumbnail_file\":\"thumb/synthetic.jpg\",\"thumbnail_name\":\"synthetic\",\"status\":1," +
            "\"file_url\":\"https://user:secret@private.invalid/file?sig=secret\"," +
            "\"thumbnail_file_url\":{\"untrusted\":[\"secret\"]}," +
            "\"file_dimensions\":{\"unattested\":[1,2]},\"thumbnail_dimensions\":null}";
        P.Result result = Inspect(Envelope(Op.Replace("\"files\":[]", "\"files\":[" + file + "]"),
            ",\"ThreadState\":0,\"DownloadedImagesCount\":1"));
        Assert.True(result.Success);
        var a = Assert.Single(result.Archive!.Posts[0].Attachments);
        Assert.Equal("7001", a.FileId.Value);
        Assert.Equal(P.Presence.Null, a.SavedOriginalName.Presence);
        Assert.Equal(P.Presence.Omitted, a.SavedThumbnailName.Presence);
        Assert.Null(a.SavedThumbnailName.Value);
        Assert.Equal("synthetic", a.ThumbnailName.Value);
        Assert.Equal(P.BytePresence.Unknown, a.OriginalBytes);
        Assert.Equal(P.BytePresence.Unknown, a.ThumbnailBytes);
        Assert.True(a.Omissions.HasFlag(P.Omission.AcquisitionUrls));
        Assert.True(a.Omissions.HasFlag(P.Omission.UnsupportedDimensions));
        Assert.True(a.Omissions.HasFlag(P.Omission.UnsafeName));
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private.invalid", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\secret")]
    [InlineData("\\\\server\\share")]
    [InlineData("x:stream")]
    [InlineData("https://private.invalid/?token=secret")]
    public void UnsafeLeafValuesAreOmittedNotResolved(string name)
    {
        string escaped = System.Text.Json.JsonSerializer.Serialize(name);
        var result = Inspect(Envelope(Op.Replace("\"files\":[]", "\"files\":[{\"saved_file\":" + escaped + "}]")));
        Assert.True(result.Success);
        var a = Assert.Single(result.Archive!.Posts[0].Attachments);
        Assert.Equal(P.Presence.Omitted, a.SavedOriginalName.Presence);
        Assert.Null(a.SavedOriginalName.Value);
        Assert.Equal(P.Presence.Missing, a.SavedThumbnailName.Presence);
    }

    [Fact]
    public void UnknownTimeAndStructuredValuesAreSkippedWithFixedLosses()
    {
        var result = Inspect(Envelope(Op.Replace("\"files\":[]", "\"files\":[],\"post_time\":{\"unattested\":[true,null]}"),
            ",\"Version\":999,\"unknown\":{\"url\":\"https://private.invalid/?secret=x\"}"));
        Assert.True(result.Success); // Wire Version cannot change the caller-selected local dialect.
        Assert.True(result.Archive!.Omissions.HasFlag(P.Omission.UnsupportedTime));
        Assert.True(result.Archive.Omissions.HasFlag(P.Omission.UnknownFields));
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(",\"Board\":\"a\"")]
    [InlineData(",\"\\u0042oard\":\"a\"")]
    [InlineData(",\"unknown\":{\"x\":1,\"x\":2}")]
    public void DuplicateKeysAreRejectedEvenInSkippedObjects(string extra) =>
        Rejected(Inspect(Envelope(extra: extra)), P.Error.DuplicateKey);

    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    public void InvalidEscapedUnicodeIsRejectedInSkippedData(string value) =>
        Rejected(Inspect(Envelope(extra: ",\"unknown\":" + value)), P.Error.InvalidUnicode);

    [Fact]
    public void InvalidBytesAndTrailingInputNeverReturnClaims()
    {
        Rejected(P.Inspect(new byte[] { 0xff }, P.Profile), P.Error.InvalidUtf8);
        byte[] json = Encoding.UTF8.GetBytes(Envelope());
        Rejected(P.Inspect(new byte[] { 0xef, 0xbb, 0xbf }.Concat(json).ToArray(), P.Profile), P.Error.Bom);
        Rejected(Inspect(Envelope() + "{}"), P.Error.InvalidJson);
        Rejected(Inspect("<!-- not JSON -->"), P.Error.InvalidJson);
        Rejected(P.Inspect(new byte[] { 0x50, 0x4b, 0x03, 0x04 }, P.Profile));
        Rejected(Inspect(Envelope(extra: ",\"unknown\":/*comment*/0")), P.Error.InvalidJson);
    }

    [Fact]
    public void ByteDepthAndTokenBudgetsIncludeSkippedValues()
    {
        string json = Envelope();
        byte[] exact = Encoding.UTF8.GetBytes(json.PadRight(P.MaxBytes));
        Assert.True(P.Inspect(exact, P.Profile).Success);
        Rejected(P.Inspect(new byte[P.MaxBytes + 1], P.Profile), P.Error.TooLarge);
        Rejected(Inspect(Envelope(extra: ",\"unknown\":" + new string('[', P.MaxDepth + 1) + "0" + new string(']', P.MaxDepth + 1))));
        Rejected(Inspect(Envelope(extra: ",\"unknown\":\"" + new string('x', P.MaxTokenBytes + 1) + "\"")), P.Error.Budget);
        Rejected(Inspect(Envelope(extra: ",\"unknown\":[" + string.Join(",", Enumerable.Repeat("0", P.MaxTokens)) + "]")), P.Error.Budget);
    }

    [Fact]
    public void PostAndTotalAttachmentBudgetsAreIndependent()
    {
        string Posts(int count) => Op + string.Concat(Enumerable.Range(102, count - 1).Select(id => ",{\"post_id\":" + id + "}"));
        Assert.True(Inspect(Envelope(Posts(P.MaxPosts))).Success);
        Rejected(Inspect(Envelope(Posts(P.MaxPosts + 1))), P.Error.Budget);
        string Files(int count) => Op.Replace("\"files\":[]", "\"files\":[" + string.Join(",", Enumerable.Repeat("{}", count)) + "]");
        Assert.True(Inspect(Envelope(Files(P.MaxAttachments))).Success);
        Rejected(Inspect(Envelope(Files(P.MaxAttachments + 1))), P.Error.Budget);
        Rejected(Inspect(Envelope(Files(P.MaxAttachments) + ",{\"post_id\":102,\"files\":[{}]}")), P.Error.Budget);
    }
    [Fact]
    public void InertTextAndUnicodeAreNotRenderedOrNormalized()
    {
        string text = "<script>inert()</script> 猫😀 \\uD800";
        string encoded = System.Text.Json.JsonSerializer.Serialize(text);
        var result = Inspect(Envelope(Op.Replace("\"files\":[]", "\"files\":[],\"post_message\":" + encoded)));
        Assert.True(result.Success);
        Assert.Equal(text, result.Archive!.Posts[0].Message.Value);
        Assert.True(Inspect(Envelope(extra: ",\"unknown\":\"\\uD83D\\uDE00\"")).Success);
    }

    [Fact]
    public void SafeSavedLeafNamesDoNotBecomeFilePresence()
    {
        const string file = "{\"saved_file\":\"original.jpg\",\"saved_thumbnail_file\":\"preview.jpg\"}";
        var result = Inspect(Envelope(Op.Replace("\"files\":[]", "\"files\":[" + file + "]")));
        Assert.True(result.Success);
        var a = Assert.Single(result.Archive!.Posts[0].Attachments);
        Assert.Equal("original.jpg", a.SavedOriginalName.Value);
        Assert.Equal("preview.jpg", a.SavedThumbnailName.Value);
        Assert.Equal(P.BytePresence.Unknown, a.OriginalBytes);
        Assert.Equal(P.BytePresence.Unknown, a.ThumbnailBytes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"not-an-array\"")]
    public void ExplicitNullAndNonArrayFilesRejectWithoutPartialArchive(string files)
    {
        string post = Op.Replace("\"files\":[]", "\"files\":" + files);
        Rejected(Inspect(Envelope(post)), P.Error.InvalidSchema);
    }

}
