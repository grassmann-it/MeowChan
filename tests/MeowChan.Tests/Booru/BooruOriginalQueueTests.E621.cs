using MeowChan.Archive;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

public sealed partial class BooruOriginalQueueTests
{
    [Fact]
    public async Task E621QueueRejectsAForeignMediaHostEvenWithValidIntegrityClaims()
    {
        using TempDirectory directory = new();
        using FakeCdn cdn = new();
        using HttpClient client = new(cdn, disposeHandler: false);
        byte[] bytes = Bytes(42);
        const string foreignUrl = "https://other.example/42.png";
        cdn.Responses[foreignUrl] = new FakeResponse(bytes);
        BooruPostClaim post = new("e621", "42", BooruPostClaimPresence.Media,
            Media: [new(BooruPostClaimMediaRole.Original, foreignUrl, ".png", bytes.Length, ExpectedMd5: Md5(bytes))]);
        await using Session session = new(directory.Path, cdn);
        BooruOriginalQueue originals = new(session.Queue, session.Catalog, session.OutputRoot, client,
            ["static1.e621.net"], sourceId: post.SourceId);

        BooruOriginalAdmission admission = await originals.AdmitAsync(post, BooruFindingKind.OneShot);

        Assert.Equal(BooruOriginalAdmissionStatus.Unavailable, admission.Status);
        Assert.Equal(BooruOriginalSelectionError.MediaHostNotAllowed, admission.SelectionError);
        Assert.Equal(BooruOriginalDownloadState.Unavailable, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Null(session.Catalog.TryGetPost(post.Identity)!.LocalAssetId);
        Assert.Empty(cdn.Requests);
        Assert.Empty(originals.GetPendingOriginals());
    }
}
