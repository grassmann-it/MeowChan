using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

/// <summary>Pure synthetic descriptor tests: no files, profile, stores or network.</summary>
public sealed class NativeTransferCuratedProjectionBoundaryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
    private static readonly byte[] Bytes = [1, 2, 3];
    private static readonly byte[] OtherBytes = [4, 5, 6];
    private const string PostId = "101";

    [Fact]
    public void MultiThreadScopePreservesSourceOrderAndDoesNotSelectAnEqualByteTwin()
    {
        Scenario scenario = CreateScenario();
        AssertCatalogValid(scenario.Catalog);
        // Deliberately differ from Freeze's sorted scope order. Output follows
        // captured source order, not selection order or matching payload hashes.
        NativeTransferCatalog input = scenario.Catalog with
        { Threads = [scenario.Catalog.Threads[2], scenario.Catalog.Threads[1], scenario.Catalog.Threads[0]] };
        byte[] before = NativeTransferCatalogSerializer.Serialize(input);

        NativeTransferCatalog output = NativeTransferCuratedProjection.Project(input, scenario.Selection);

        AssertCatalogValid(output);
        Assert.Equal(new[] { scenario.SecondThread, scenario.FirstThread }, output.Threads.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(output.Threads, t => t.Id == scenario.OutsideThread);
        TransferAttachment twin = output.Threads[0].Posts[0].Attachments[0];
        Assert.Equal(TransferContentStatus.Omitted, twin.Status);
        Assert.Null(twin.PayloadSha256);
        Assert.Null(twin.History);
        Assert.Equal(input.Threads[1].Posts[0].Attachments[0].Metadata, twin.Metadata);
        Assert.Equal(input.Threads[2].Posts[0].Attachments[0], output.Threads[1].Posts[0].Attachments[0]);
        Assert.All(output.Threads, t => Assert.Same(input.Threads.Single(s => s.Id == t.Id).Posts[0].Metadata,
            t.Posts[0].Metadata));
        Assert.Equal(before, NativeTransferCatalogSerializer.Serialize(input));
        Assert.Single(scenario.Selection.Originals);
        Assert.Equal(CuratedExportStatus.OriginalIncluded, scenario.Selection.Originals[0].Status);
    }

    [Fact]
    public void MultiThreadScopeCanIncludeDistinctSelectedOriginalsWithEqualBytes()
    {
        Scenario scenario = CreateScenario(selectSecond: true);
        Assert.Equal(2, scenario.Selection.Originals.Length);
        AssertCatalogValid(scenario.Catalog);

        NativeTransferCatalog output = NativeTransferCuratedProjection.Project(scenario.Catalog, scenario.Selection);

        AssertCatalogValid(output);
        Assert.Equal(new[] { scenario.FirstThread, scenario.SecondThread }, output.Threads.Select(t => t.Id).ToArray());
        Assert.All(output.Threads, t =>
        {
            Assert.Equal(TransferContentStatus.Contained, t.Posts[0].Attachments[0].Status);
            Assert.Equal(Hash(Bytes), t.Posts[0].Attachments[0].PayloadSha256);
            Assert.Equal(scenario.Catalog.Threads.Single(s => s.Id == t.Id).Posts[0].Attachments[0],
                t.Posts[0].Attachments[0]);
        });
    }

    [Fact]
    public void MissingScopedThreadRejectsEvenWhenItHasNoSelectedOriginal()
    {
        Scenario scenario = CreateScenario();
        NativeTransferCatalog input = scenario.Catalog with
        { Threads = [scenario.Catalog.Threads[0], scenario.Catalog.Threads[2]] };
        AssertCatalogValid(input);

        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, scenario.Selection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChosenOccurrenceAbsentFromPublicCatalogRejectsDespiteEqualByteTwin(bool retained)
    {
        Scenario scenario = CreateScenario();
        NativeTransferCatalog input = WithSelectedObservation(scenario.Catalog,
            scenario.Catalog.Threads[0].Posts[0].Attachments[0], retained);
        TransferThread first = input.Threads[0];
        first = retained ? first with { RetainedBindings = [] } : first with
        {
            Posts = [first.Posts[0] with
            {
                Metadata = first.Posts[0].Metadata with { Attachments = [] },
                Attachments = []
            }]
        };
        input = input with { Threads = input.Threads.SetItem(0, first) };
        AssertCatalogValid(input);
        Assert.Equal(Hash(Bytes), input.Threads[1].Posts[0].Attachments[0].PayloadSha256);

        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, scenario.Selection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneRepresentedOccurrenceDoesNotExcuseAnotherMissingOccurrenceOfTheSameFavorite(bool retained)
    {
        Scenario scenario = CreateScenario(addSecondOccurrence: true);
        Assert.Equal(2, Assert.Single(scenario.Selection.Originals).Occurrences.Length);
        AssertCatalogValid(scenario.Catalog);
        AssertCatalogValid(NativeTransferCuratedProjection.Project(scenario.Catalog, scenario.Selection));
        TransferThread first = scenario.Catalog.Threads[0];
        // Preserve one exact selected reference and its payload, dropping only
        // the other reference to the same asset/favorite. Coverage is per
        // occurrence, not one successful hit per favorite or payload hash.
        first = retained ? first with
        {
            Posts = [],
            RetainedBindings = [new(PostId, first.Posts[0].Attachments[0])]
        } : first with { Posts = [first.Posts[0]] };
        NativeTransferCatalog input = scenario.Catalog with { Threads = scenario.Catalog.Threads.SetItem(0, first) };
        AssertCatalogValid(input);

        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, scenario.Selection));
    }

    [Theory]
    [InlineData("media")]
    [InlineData("variant")]
    [InlineData("position")]
    [InlineData("post")]
    public void DifferentValidOccurrenceIdentityRejectsInsteadOfMatchingByPayload(string changedField)
    {
        Scenario scenario = CreateScenario();
        TransferAttachment original = scenario.Catalog.Threads[0].Posts[0].Attachments[0];
        string postId = changedField == "post" ? "102" : PostId;
        CapturedAttachment metadata = original.Metadata with
        {
            MediaId = changedField == "media" ? "1001" : original.Metadata.MediaId,
            Variant = changedField == "variant" ? "thumbnail" : original.Metadata.Variant,
            Position = changedField == "position" ? 1 : original.Metadata.Position
        };
        // Re-key the modified identity so this is a valid public catalog, not a
        // descriptor-validation failure that masks the exact-selection boundary.
        metadata = metadata with
        { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(scenario.FirstThread, postId, metadata.Position, Provider(metadata)) };
        NativeTransferCatalog input = WithSelectedObservation(scenario.Catalog, original with { Metadata = metadata },
            retained: false, postId: postId);
        AssertCatalogValid(input);

        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, scenario.Selection));
    }

    [Fact]
    public void ExactChosenOccurrenceWithPreviewRoleRejectsEvenWhenPublicCatalogIsValid()
    {
        Scenario scenario = CreateScenario();
        TransferAttachment wrongRole = scenario.Catalog.Threads[0].Posts[0].Attachments[0] with
        { Role = TransferMediaRole.Preview };
        NativeTransferCatalog input = WithSelectedObservation(scenario.Catalog, wrongRole, retained: false);
        AssertCatalogValid(input);

        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, scenario.Selection));
    }

    [Theory]
    [InlineData("board")]
    [InlineData("source")]
    public void ChosenOccurrenceInAnotherResourceCannotSubstituteForItsScopedResource(string changedField)
    {
        Scenario scenario = CreateScenario();
        ResourceId other = changedField == "board"
            ? new("4chan", "a", "100") : new("synthetic-source", "b", "100");
        ThreadPart substitute = CreateThread(other);
        // Keep the scoped resource present, but move its only matching media
        // observation to a different source/board with the same thread/post ID.
        TransferPost firstPost = scenario.Catalog.Threads[0].Posts[0];
        NativeTransferCatalog input = scenario.Catalog with
        {
            Threads = [scenario.Catalog.Threads[0] with
            {
                Posts = [firstPost with { Metadata = firstPost.Metadata with { Attachments = [] }, Attachments = [] }]
            }, scenario.Catalog.Threads[1], substitute.Transfer]
        };
        AssertCatalogValid(input);

        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, scenario.Selection));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("presence")]
    [InlineData("parallel-metadata")]
    [InlineData("source-url")]
    public void MalformedMetadataOutsideSelectedScopeRejectsBeforeFiltering(string corruption)
    {
        Scenario scenario = CreateScenario();
        // This positive control prevents an unrelated broken fixture from
        // satisfying all negative cases merely by throwing during setup.
        AssertCatalogValid(scenario.Catalog);
        NativeTransferCatalog valid = NativeTransferCuratedProjection.Project(scenario.Catalog, scenario.Selection);
        Assert.Equal(2, valid.Threads.Length);
        TransferThread outside = scenario.Catalog.Threads[2];
        TransferPost post = outside.Posts[0];
        if (corruption == "revision")
        {
            CapturedAttachment forged = post.Attachments[0].Metadata with
            { AttachmentRevisionKey = new string('e', 64) };
            post = post with
            {
                Metadata = post.Metadata with { Attachments = [forged] },
                Attachments = [post.Attachments[0] with { Metadata = forged }]
            };
        }
        else if (corruption == "presence")
            post = post with { Metadata = post.Metadata with { Fields = PostFieldPresence.None } };
        else if (corruption == "parallel-metadata")
            post = post with { Metadata = post.Metadata with { Attachments = [] } };
        else
            outside = outside with { PageUrl = "https://example.invalid/?token=synthetic-secret" };
        outside = outside with { Posts = [post] };
        NativeTransferCatalog input = scenario.Catalog with { Threads = scenario.Catalog.Threads.SetItem(2, outside) };

        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, scenario.Selection));
    }

    [Theory]
    [InlineData("Contained", false)]
    [InlineData("Omitted", false)]
    [InlineData("Unavailable", false)]
    [InlineData("Contained", true)]
    [InlineData("Omitted", true)]
    [InlineData("Unavailable", true)]
    public void SelectedObservationKeepsItsStatusWithoutPromotingMissingBytes(string statusName, bool retained)
    {
        Scenario scenario = CreateScenario();
        TransferContentStatus status = Enum.Parse<TransferContentStatus>(statusName);
        TransferAttachment selected = scenario.Catalog.Threads[0].Posts[0].Attachments[0];
        TransferAttachment observation = status == TransferContentStatus.Contained ? selected : selected with
        { Status = status, PayloadSha256 = null, History = null };
        NativeTransferCatalog input = WithSelectedObservation(scenario.Catalog, observation, retained);
        AssertCatalogValid(input);

        NativeTransferCatalog output = NativeTransferCuratedProjection.Project(input, scenario.Selection);

        AssertCatalogValid(output);
        TransferAttachment result = retained ? Assert.Single(output.Threads[0].RetainedBindings).Attachment
            : Assert.Single(output.Threads[0].Posts[0].Attachments);
        Assert.Equal(observation, result);
        Assert.Equal(status, result.Status);
        if (status == TransferContentStatus.Contained)
        {
            Assert.Equal(Hash(Bytes), result.PayloadSha256);
            Assert.Equal(selected.History, result.History);
            Assert.NotNull(result.History);
        }
        else
        {
            Assert.Null(result.PayloadSha256);
            Assert.Null(result.History);
        }
        if (retained) Assert.Empty(output.Threads[0].Posts);
        Assert.Equal(TransferContentStatus.Omitted, output.Threads[1].Posts[0].Attachments[0].Status);
        Assert.Null(output.Threads[1].Posts[0].Attachments[0].PayloadSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedContainedWithDifferentValidPayloadHashRejectsForCurrentAndRetained(bool retained)
    {
        Scenario scenario = CreateScenario();
        TransferAttachment changed = scenario.Catalog.Threads[0].Posts[0].Attachments[0] with
        { PayloadSha256 = Hash(OtherBytes), History = null };
        // Legal chan-v3 unattested observation; rejection must come from the
        // consumer's exact favorite/hash binding, not fabricated byte receipts.
        NativeTransferCatalog input = WithSelectedObservation(scenario.Catalog, changed, retained);
        AssertCatalogValid(input);

        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, scenario.Selection));
    }

    private static NativeTransferCatalog WithSelectedObservation(NativeTransferCatalog catalog,
        TransferAttachment observation, bool retained, string postId = PostId)
    {
        TransferThread first = catalog.Threads[0];
        TransferPost post = first.Posts[0];
        TransferThread changed = retained ? first with
        { Posts = [], RetainedBindings = [new(postId, observation)] } : first with
        {
            Posts = [post with
            {
                Metadata = post.Metadata with { PostId = postId, Attachments = [observation.Metadata] },
                Attachments = [observation]
            }]
        };
        return catalog with { Threads = catalog.Threads.SetItem(0, changed) };
    }

    private static ThreadPart CreateThread(ResourceId thread, bool addSecondOccurrence = false)
    {
        // All resources intentionally have the same post/position/media/hash.
        // Only the full resource identity and its recomputed revision differ.
        string hash = Hash(Bytes);
        var provider = new MediaProviderMetadata("1000", "original", "https://i.4cdn.org/b/1000.png",
            "1000.png", Bytes.Length, null, hash);
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: Bytes.Length)
        {
            ExpectedSha256 = hash,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(thread, PostId, 0, provider)
        };
        MediaAssetReference asset = new(new string('1', 64), provider.MediaId, provider.Variant,
            "1000.png", "/synthetic/not-opened", Bytes.Length, hash, provider.Url, provider.OriginalFileName, false)
        { Md5 = Convert.ToBase64String(MD5.HashData(Bytes)) };
        MediaAttachmentBinding binding = new(new(thread, PostId, 0, metadata.AttachmentRevisionKey!),
            MediaRevisionKeys.CreateAcquisitionKey(thread, provider), asset.AssetId, provider,
            new(Bytes.Length, Convert.ToBase64String(MD5.HashData(Bytes)), hash),
            new(MediaAcceptanceKind.Strict, false, false, At));
        var snapshot = new MediaAssetSnapshot("/synthetic/root/" + thread.Source + "/" + thread.Board + "/" + thread.Thread,
            MediaManifestStatus.Available, 2, new string('d', 64), [asset], attachmentBindings:
            new Dictionary<string, MediaAttachmentBinding> { [binding.Reference.RevisionKey] = binding });
        FavoriteEntry favorite = new(new(thread, provider.MediaId, "original", hash), Bytes.Length, "synthetic-private-name");
        TransferAttachment attachment = new(metadata, TransferMediaRole.Original,
            TransferContentStatus.Contained, hash, new("strict", At, false, false));
        CapturedPost captured = new(PostId, 101, PostFieldPresence.All, At, Html: "<p>synthetic</p>", Attachments: [metadata]);
        // A null page URL is valid public metadata, including for an inert
        // synthetic source. No live provider/source registration is required.
        TransferThread transfer = new(thread, null, "Synthetic thread", ThreadSourceState.NotFound,
            At, At, 1, true, [new(captured, [attachment])]);
        if (addSecondOccurrence)
        {
            CapturedAttachment secondMetadata = metadata with
            { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(thread, "102", 0, provider) };
            MediaAttachmentBinding secondBinding = binding with
            { Reference = new(thread, "102", 0, secondMetadata.AttachmentRevisionKey!) };
            snapshot = new MediaAssetSnapshot(snapshot.Directory, MediaManifestStatus.Available, 2, snapshot.Generation,
                [asset], attachmentBindings: new Dictionary<string, MediaAttachmentBinding>
                {
                    [binding.Reference.RevisionKey] = binding,
                    [secondBinding.Reference.RevisionKey] = secondBinding
                });
            TransferPost secondPost = new(captured with
            { PostId = "102", Ordinal = 102, Attachments = [secondMetadata] },
                [attachment with { Metadata = secondMetadata }]);
            transfer = transfer with { Posts = transfer.Posts.Add(secondPost) };
        }
        return new(transfer, favorite, new(thread, snapshot));
    }

    private static Scenario CreateScenario(bool selectSecond = false, bool addSecondOccurrence = false)
    {
        ResourceId first = new("4chan", "b", "100"), second = new("4chan", "b", "200"), outside = new("4chan", "b", "300");
        ThreadPart firstPart = CreateThread(first, addSecondOccurrence), secondPart = CreateThread(second), outsidePart = CreateThread(outside);
        var snapshot = new FavoriteStoreSnapshot(null!, [firstPart.Favorite, secondPart.Favorite], new string('f', 64),
            Guid.Parse("f70b723e-46d4-48b3-bb88-a8f985b27630"), 7);
        FavoriteAssetKey[] selectedKeys = selectSecond ? [firstPart.Favorite.Key, secondPart.Favorite.Key] : [firstPart.Favorite.Key];
        CuratedExportSelection selection = CuratedExportSelection.Freeze(snapshot, CuratedExportScope.Threads,
            CuratedExportMode.Files, false, [first, second], selectedKeys, [firstPart.Manifest, secondPart.Manifest], []);
        Assert.All(selection.Originals, original =>
        {
            Assert.Equal(CuratedExportStatus.OriginalIncluded, original.Status);
            Assert.NotNull(original.Asset);
            Assert.Equal(addSecondOccurrence && original.Entry.Key.Thread == first ? 2 : 1, original.Occurrences.Length);
        });
        return new(new(At, [firstPart.Transfer, secondPart.Transfer, outsidePart.Transfer]), selection, first, second, outside);
    }

    private static MediaProviderMetadata Provider(CapturedAttachment metadata) => new(metadata.MediaId!, metadata.Variant,
        metadata.Url!, metadata.OriginalFileName, metadata.ExpectedSize, metadata.ExpectedMd5, metadata.ExpectedSha256)
    { ExpectedSha1 = metadata.ExpectedSha1, ProviderSizeHint = metadata.ProviderSizeHint };

    // The real catalog validator sees hashes computed from these synthetic bytes,
    // not placeholder measurements. This helper is test-only and performs no IO.
    private static void AssertCatalogValid(NativeTransferCatalog catalog)
    {
        byte[] wire = NativeTransferCatalogSerializer.Serialize(catalog);
        var hashes = catalog.Threads.SelectMany(t => t.Posts.SelectMany(p => p.Attachments)
                .Concat(t.RetainedBindings.Select(r => r.Attachment)))
            .Where(a => a.Status == TransferContentStatus.Contained).Select(a => a.PayloadSha256!).ToHashSet();
        var measured = new[] { Bytes, OtherBytes }.Where(bytes => hashes.Contains(Hash(bytes)))
            .ToImmutableDictionary(Hash, bytes => new TransferMeasuredBlob(bytes.Length, Hash(bytes),
                Convert.ToBase64String(MD5.HashData(bytes)), Convert.ToBase64String(SHA1.HashData(bytes))));
        var envelope = new NativeTransferEnvelope("f70b723e-46d4-48b3-bb88-a8f985b27630",
            NativeTransferCatalogSerializer.ContentSchema(catalog), "", new(wire.Length, Hash(wire)),
            measured.Values.Select(value => new TransferBlobDescriptor(value.Length, value.Sha256)).ToImmutableArray())
        { MeasuredPayloads = measured };
        TransferCatalogInspection inspection = NativeTransferCatalogValidator.Inspect(wire, envelope);
        Assert.True(inspection.IsCatalogValid, inspection.Error.ToString());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record ThreadPart(TransferThread Transfer, FavoriteEntry Favorite, CuratedExportManifest Manifest);
    private sealed record Scenario(NativeTransferCatalog Catalog, CuratedExportSelection Selection,
        ResourceId FirstThread, ResourceId SecondThread, ResourceId OutsideThread);
}
