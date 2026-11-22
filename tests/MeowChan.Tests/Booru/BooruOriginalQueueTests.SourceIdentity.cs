using MeowChan.Archive;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

public sealed partial class BooruOriginalQueueTests
{
    [Theory]
    [InlineData("test-source", BooruPostKey.DefaultInstance)]
    [InlineData("danbooru", "test-instance")]
    public async Task ScopedPost42AndLegacyJobSurviveRestartWithTheOtherSourceDisabled(string sourceId, string instanceId)
    {
        using TempDirectory directory = new();
        using SemaphoreSlim legacyGate = new(0);
        using FakeCdn legacyCdn = new() { Gate = legacyGate };
        using FakeCdn secondCdn = new();
        using HttpClient secondClient = new(secondCdn, disposeHandler: false);
        BooruPostClaim legacy = Post("42", Bytes(1), legacyCdn);
        byte[] secondBytes = Bytes(2);
        const string secondUrl = "https://media.example.test/42.png";
        secondCdn.Responses[secondUrl] = new FakeResponse(secondBytes);
        BooruPostClaim second = new(sourceId, "42", BooruPostClaimPresence.Media,
            Media: [new(BooruPostClaimMediaRole.Original, secondUrl, ".png", secondBytes.Length, ExpectedMd5: Md5(secondBytes))],
            InstanceId: instanceId);

        bool enabled = true;
        BooruOriginalQueue Other(Session session) => new(session.Queue, session.Catalog, session.OutputRoot,
            secondClient, ["media.example.test"], sourceId: sourceId, instanceId: instanceId, isEnabled: () => enabled);

        await using (Session original = new(directory.Path, legacyCdn))
        {
            DateTimeOffset observed = DateTimeOffset.UtcNow;
            original.Catalog.UpsertPost(legacy, observed);
            original.Catalog.SetOriginalIntent("danbooru", "42", BooruOriginalIntent.Requested);
            string document = File.ReadAllText(original.Catalog.FilePath);
            Assert.Contains("\"schemaVersion\": 1", document, StringComparison.Ordinal);
            Assert.DoesNotContain("instanceId", document, StringComparison.OrdinalIgnoreCase);
        }

        await using (Session restarted = new(directory.Path, legacyCdn))
        {
            Assert.Equal(legacy.Identity, Assert.Single(restarted.Booru.GetPendingOriginals()).Identity);
            BooruOriginalQueue other = Other(restarted);
            // Record another pending job without a transfer, as if the application closed after write-ahead.
            restarted.Catalog.RecordObservation(second, DateTimeOffset.UtcNow, BooruFindingKind.OneShot,
                null, null, _ => BooruOriginalIntent.Requested);
            enabled = false;
            Assert.Empty(await other.RecoverPendingAsync());
            Assert.Equal(BooruOriginalAdmissionStatus.SourceUnavailable, (await other.RetryAsync("42")).Status);
            Assert.Empty(secondCdn.Requests);
            Assert.True(restarted.Catalog.TryGetPost(second.Identity)!.HasPendingOriginal);

            BooruOriginalAdmission recovered = Assert.Single(await restarted.Booru.RecoverPendingAsync());
            Assert.Equal(BooruMediaKey.Original(legacy.Identity), recovered.Identity);
            await TestWait.UntilAsync(() => legacyCdn.Requests.Count == 1);
            Assert.True(restarted.Catalog.TryGetPost(second.Identity)!.HasPendingOriginal);

            enabled = true;
            BooruOriginalAdmission next = Assert.Single(await other.RecoverPendingAsync());
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, recovered.Status);
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, next.Status);
            Assert.False(recovered.Completion.IsCompleted);
            legacyGate.Release();
            BooruOriginalDownloadOutcome first = await recovered.Completion.WaitAsync(TestWait.Timeout);
            Assert.Equal(BooruOriginalDownloadState.Linked, first.State);
            BooruOriginalDownloadOutcome last = await next.Completion.WaitAsync(TestWait.Timeout);
            Assert.Equal(BooruOriginalDownloadState.Linked, last.State);
            Assert.Equal(BooruMediaKey.Original(second.Identity), last.Identity);
            Assert.NotEqual(first.LocalPath, last.LocalPath);
            Assert.Equal(secondBytes, File.ReadAllBytes(last.LocalPath!));
            Assert.Equal([OriginalUrl("42")], legacyCdn.Requests.ToArray());
            Assert.Equal([secondUrl], secondCdn.Requests.ToArray());
            Assert.Empty(restarted.Work.LoadResources());
        }

        await using Session again = new(directory.Path, legacyCdn);
        BooruCatalogSnapshot snapshot = again.Catalog.Read();
        Assert.Equal(BooruCatalogSnapshot.ScopedIdentitySchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(2, snapshot.Posts.Count);
        Assert.NotEqual(snapshot.TryGetPost(legacy.Identity)!.LocalPath, snapshot.TryGetPost(second.Identity)!.LocalPath);
        Assert.Empty(await again.Booru.RecoverPendingAsync());
        Assert.Empty(await Other(again).RecoverPendingAsync());
        Assert.Single(legacyCdn.Requests);
        Assert.Single(secondCdn.Requests);
    }

    [Fact]
    public void MediumAndVariantContributeToTheMediaKey()
    {
        BooruMediaKey original = BooruMediaKey.Original(new BooruPostKey("test-source", "public", "42"));
        BooruMediaKey otherMedium = original with { MediaId = "second" };
        BooruMediaKey sample = original with { Variant = BooruPostClaimMediaRole.Sample };
        Assert.NotEqual(original, otherMedium);
        Assert.NotEqual(original, sample);
        Assert.NotEqual(original.StorageId, otherMedium.StorageId);
        Assert.NotEqual(original.StorageId, sample.StorageId);
    }
}
