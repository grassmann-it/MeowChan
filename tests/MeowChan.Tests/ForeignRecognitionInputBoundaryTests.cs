using System.IO.Compression;
using System.Text;
using MeowChan.Interop;
using MeowChan.Interop.Links;
using MeowChan.Interop.Sidecars;

namespace MeowChan.Tests;

/// <summary>
/// Input-boundary tests for the currently implemented foreign interop seams.
/// All fixtures stay in memory: no filesystem discovery/extraction, URL access,
/// personal fixtures, or claim of a productive foreign adapter.
/// </summary>
public sealed class ForeignRecognitionInputBoundaryTests
{
    private const string FourChanJson = "{\"posts\":[{\"no\":101,\"resto\":0}]}";

    [Fact]
    public void EverySupportedForeignJsonSeamRejectsPayloadsAboveItsOwnByteBoundBeforeDispatch()
    {
        Assert.Equal(ThreadArchiveRecognition.MaxPayloadBytes, PassiveSidecarCodec.MaxPayloadBytes);
        byte[] oversized = new byte[ThreadArchiveRecognition.MaxPayloadBytes + 1];
        Array.Fill(oversized, (byte)'x');

        ThreadArchiveRecognitionResult archive = ThreadArchiveRecognition.Inspect(oversized);
        Assert.Equal(ThreadArchiveRecognitionStatus.Rejected, archive.Status);
        Assert.Equal(ThreadArchiveRecognitionError.PayloadTooLarge, archive.Error);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, archive.Format);
        Assert.Empty(archive.Candidates);
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), archive.Counts);

        foreach (SidecarProfile profile in new[]
        {
            SidecarProfile.HydrusJsonTagsV1,
            SidecarProfile.GalleryDlDanbooruIdTagsV1
        })
        {
            SidecarParseResult sidecar = PassiveSidecarCodec.Parse(oversized, profile);
            Assert.False(sidecar.Success);
            Assert.Equal(SidecarError.PayloadTooLarge, sidecar.Error);
            Assert.Empty(sidecar.Tags);
            Assert.Null(sidecar.ClaimId);
        }
    }

    [Theory]
    [InlineData("{\"host\":\"boards.example.invalid\"}")]
    [InlineData("{\"path\":\"/private/archive/thread.json\"}")]
    [InlineData("{\"manifest\":{\"format\":\"synthetic\"}}")]
    [InlineData("{\"posts\":[],\"host\":\"boards.example.invalid\"}")]
    [InlineData("{\"posts\":[],\"path\":\"thread.json\"}")]
    [InlineData("{\"posts\":[],\"manifest\":{\"entries\":[]}}")]
    [InlineData("{\"manifest\":{\"posts\":[{\"no\":101,\"resto\":0}]}} ")]
    [InlineData("{\"entries\":[{\"path\":\"thread_data.json\",\"content\":{\"posts\":[{\"no\":101,\"resto\":0}]}}]}")]
    public void HostPathAndManifestClaimsDoNotPromoteAnUnsupportedThreadEnvelope(string json)
    {
        ThreadArchiveRecognitionResult result = ThreadArchiveRecognition.Inspect(Bytes(json));

        Assert.Equal(ThreadArchiveRecognitionStatus.Unsupported, result.Status);
        Assert.Equal(ThreadArchiveRecognitionError.NoKnownSignature, result.Error);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, result.Format);
        Assert.Empty(result.Candidates);
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), result.Counts);
        Assert.False(result.IsRecognized);
    }

    [Theory]
    [InlineData(false, "\"host\":\"boards.example.invalid\"")]
    [InlineData(false, "\"path\":\"/private/archive/image.jpg\"")]
    [InlineData(false, "\"manifest\":{\"entries\":[]}")]
    [InlineData(true, "\"host\":\"boards.example.invalid\"")]
    [InlineData(true, "\"path\":\"/private/archive/image.jpg\"")]
    [InlineData(true, "\"manifest\":{\"entries\":[]}")]
    public void SidecarProfilesRejectHostPathAndManifestKeysInsteadOfReturningPartialMetadata(
        bool gallery, string unexpectedMember)
    {
        string json = gallery
            ? "{\"id\":101,\"tags\":[\"synthetic:tag\"]," + unexpectedMember + "}"
            : "{\"metadata\":{\"tags\":[\"synthetic:tag\"]}," + unexpectedMember + "}";
        SidecarProfile profile = gallery
            ? SidecarProfile.GalleryDlDanbooruIdTagsV1
            : SidecarProfile.HydrusJsonTagsV1;

        SidecarParseResult result = PassiveSidecarCodec.Parse(Bytes(json), profile);

        Assert.False(result.Success);
        Assert.Equal(SidecarError.InvalidSchema, result.Error);
        Assert.Empty(result.Tags);
        Assert.Null(result.ClaimId);
    }

    [Theory]
    [InlineData("PK\u0003\u0004{\"posts\":[{\"no\":101,\"resto\":0}]}")]
    [InlineData("<!doctype html><html><body>{\"posts\":[{\"no\":101,\"resto\":0}]}</body></html>")]
    [InlineData("<html><script type=\"application/json\">{\"posts\":[{\"no\":101,\"resto\":0}]}</script></html>")]
    public void ContainerAndMarkupBytesAreNotUnwrappedByForeignJsonRecognizers(string input)
    {
        byte[] bytes = Bytes(input);

        ThreadArchiveRecognitionResult archive = ThreadArchiveRecognition.Inspect(bytes);
        Assert.Equal(ThreadArchiveRecognitionStatus.Rejected, archive.Status);
        Assert.Equal(ThreadArchiveRecognitionError.InvalidJson, archive.Error);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, archive.Format);
        Assert.Empty(archive.Candidates);
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), archive.Counts);

        foreach (SidecarProfile profile in new[]
        {
            SidecarProfile.HydrusJsonTagsV1,
            SidecarProfile.GalleryDlDanbooruIdTagsV1
        })
        {
            SidecarParseResult sidecar = PassiveSidecarCodec.Parse(bytes, profile);
            Assert.False(sidecar.Success);
            Assert.Equal(SidecarError.InvalidJson, sidecar.Error);
            Assert.Empty(sidecar.Tags);
            Assert.Null(sidecar.ClaimId);
        }
    }

    [Theory]
    [InlineData("https://boards.4chan.org.synthetic.invalid/g/thread/101")]
    [InlineData("https://8kun.top.synthetic.invalid/test/res/101.html")]
    [InlineData("https://127.0.0.1/g/thread/101")]
    [InlineData("https://localhost/test/res/101.html")]
    [InlineData("https://boards.4chan.org/g/%2e%2e/g/thread/101")]
    [InlineData("https://8kun.top/test/%2e%2e/test/res/101.html")]
    [InlineData("https://boards.4chan.org/g%2fthread%2f101")]
    [InlineData("https://8kun.top/test%2fres%2f101.html")]
    [InlineData("https://boards.4chan.org/g/thread/%31%30%31")]
    [InlineData("https://8kun.top/test/res/%31%30%31.html")]
    public void PassiveChanPageGrammarDoesNotCanonicalizeUnattestedHostsOrEncodedPaths(string url)
    {
        // This is the separate passive URL seam, NOT ThreadArchiveRecognition.
        PlainUrlText grammar = PlainUrlText.ForSupportedChanPages();
        byte[] input = Bytes(url);

        LinkTextResult result = grammar.Parse(input);

        Assert.Empty(result.Links);
        Assert.Equal(new LinkIssue(1, LinkIssueCode.SafetyUnknown), Assert.Single(result.Issues));
        Assert.DoesNotContain(url, result.ToString());
        LinkTextExport export = grammar.Export(input);
        Assert.Empty(export.Utf8);
        Assert.Equal(result.Issues.ToArray(), export.Issues.ToArray());
    }

    [Fact]
    public void CanonicalPageControlsStillRequireExplicitConfirmation()
    {
        PlainUrlText grammar = PlainUrlText.ForSupportedChanPages();
        foreach (string url in new[]
        {
            "https://boards.4chan.org/g/thread/101",
            "https://8kun.top/test/res/101.html"
        })
        {
            LinkTextResult result = grammar.Parse(Bytes(url));
            Assert.Empty(result.Issues);
            PassiveLink link = Assert.Single(result.Links);
            Assert.Equal(url, link.Text);
            Assert.Equal(PassiveLinkKind.Post, link.Kind);
            Assert.True(link.RequiresExplicitConfirmation);
        }
    }

    [Theory]
    [InlineData("thread_data.json")]
    [InlineData("../thread_data.json")]
    [InlineData("meowchan-export.json")]
    public void RealInMemoryZipDoesNotExposeItsRecognizableEntryToTheJsonSeam(string entryName)
    {
        // Even the traversal-looking name remains a ZIP entry string, never a file path.
        // Do not extract or call a native transfer reader; no foreign ZIP API exists.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream content = entry.Open();
            content.Write(Bytes(FourChanJson));
        }
        byte[] zipBytes = buffer.ToArray();
        Assert.True(zipBytes.Length < ThreadArchiveRecognition.MaxPayloadBytes);
        Assert.Equal(new byte[] { 0x50, 0x4b, 0x03, 0x04 }, zipBytes[..4]);

        ThreadArchiveRecognitionResult result = ThreadArchiveRecognition.Inspect(zipBytes);

        Assert.Equal(ThreadArchiveRecognitionStatus.Rejected, result.Status);
        // Binary CRC/header bytes may fail strict UTF-8 before JSON parsing.
        Assert.Contains(result.Error, new[]
        {
            ThreadArchiveRecognitionError.InvalidUtf8,
            ThreadArchiveRecognitionError.InvalidJson
        });
        Assert.False(result.IsRecognized);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, result.Format);
        Assert.Empty(result.Candidates);
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), result.Counts);
        Assert.DoesNotContain(entryName, result.ToString());
    }

    [Fact]
    public void BareFourChanJsonControlStillReturnsOnlyAStructuralHint()
    {
        ThreadArchiveRecognitionResult archive = ThreadArchiveRecognition.Inspect(Bytes(FourChanJson));

        Assert.Equal(ThreadArchiveRecognitionStatus.Recognized, archive.Status);
        Assert.Equal(ThreadArchiveRecognitionFormat.FourChanApiJson, archive.Format);
        Assert.Equal(ThreadArchiveRecognitionError.None, archive.Error);
        Assert.Equal(new ThreadArchiveRecognitionCounts(1, 1, 0), archive.Counts);
        Assert.True(archive.IsRecognized);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
