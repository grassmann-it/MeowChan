using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// W32b3/W32b4 post-presence lane. These tests intentionally exercise the shared
// v2 fixture and the public archive reader/capture path; schema migration tests
// belong here once the Schema11 migration API is part of the product contract.
public sealed class NativeV2PostPresenceTests
{
    [Fact]
    public async Task V2ImportPreservesNullEmptyAndMissingAttachmentPresenceThroughReadAndCopy()
    {
        await using NativeV2ImportFixture fixture = await NativeV2ImportFixture.CreateAsync(PresenceCatalog());
        using ProfileAdmissionToken admission = await fixture.ExclusiveAsync();

        ProfileExclusiveOutcome<NativeMediaImportCommitResult> outcome = await fixture.CommitAsync(admission);
        Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
        Assert.Equal(NativeMediaImportCommitStatus.Committed, outcome.Result.Status);
        Assert.Equal(4, outcome.Result.NewPosts);

        await fixture.Stage.DisposeAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Files.Profile);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(NativeV2ImportFixture.Id));
        Assert.Equal(4, view.PostCount);

        CapturedPost nullPost = Assert.Single(view.GetPosts(), post => post.PostId == "16");
        Assert.Equal(PostFieldPresence.Attachments, nullPost.Fields);
        Assert.Null(nullPost.Attachments);
        CapturedPost emptyPost = Assert.Single(view.GetPosts(), post => post.PostId == "17");
        Assert.Equal(PostFieldPresence.All, emptyPost.Fields);
        Assert.NotNull(emptyPost.Attachments);
        Assert.Empty(emptyPost.Attachments!);
        CapturedPost missingPost = Assert.Single(view.GetPosts(), post => post.PostId == "18");
        Assert.Equal(PostFieldPresence.None, missingPost.Fields);
        Assert.NotNull(missingPost.Attachments);
        Assert.Empty(missingPost.Attachments!);

        CapturedPost nullCopy = ArchiveCaptureMerge.CopyPost(nullPost);
        Assert.Equal(nullPost.Fields, nullCopy.Fields);
        Assert.Null(nullCopy.Attachments);
        CapturedPost emptyCopy = ArchiveCaptureMerge.CopyPost(emptyPost);
        Assert.NotNull(emptyCopy.Attachments);
        Assert.Empty(emptyCopy.Attachments!);
        CapturedPost missingCopy = ArchiveCaptureMerge.CopyPost(missingPost);
        Assert.NotNull(missingCopy.Attachments);
        Assert.Empty(missingCopy.Attachments!);
    }

    [Fact]
    public async Task ActiveCaptureWithExplicitNullDoesNotDeleteExistingMedia()
    {
        await using NativeV2ImportFixture fixture = await NativeV2ImportFixture.CreateAsync();
        using (ProfileAdmissionToken admission = await fixture.ExclusiveAsync())
            Assert.Equal(NativeMediaImportCommitStatus.Committed, (await fixture.CommitAsync(admission)).Result.Status);

        string mediaDirectory;
        await using (ArchiveReader beforeReader = await ArchiveReader.OpenAsync(fixture.Files.Profile))
        {
            ArchivedThreadView before = Assert.IsType<ArchivedThreadView>(
                await beforeReader.ReadThreadAsync(NativeV2ImportFixture.Id));
            mediaDirectory = Assert.Single(before.MediaDirectories);
            CapturedPost beforePost = Assert.Single(before.GetPosts(), post => post.PostId == "15");
            Assert.Single(beforePost.Attachments!);
        }

        // A later observation explicitly supplies attachments:null for the existing
        // post. CaptureMerge must retain the already known attachment rows rather
        // than interpreting the observation as a deletion.
        CapturedThreadSnapshot nullObservation = new(
            NativeV2ImportFixture.Id, NativeV2ImportFixture.Url, "synthetic W32b3 thread",
            ThreadSourceState.NotFound, NativeV2ImportFixture.At.AddMinutes(1), mediaDirectory,
            [new CapturedPost("15", 1, PostFieldPresence.Attachments, Attachments: null)]);
        await fixture.Catalog.CaptureAsync(nullObservation);

        await using ArchiveReader afterReader = await ArchiveReader.OpenAsync(fixture.Files.Profile);
        ArchivedThreadView after = Assert.IsType<ArchivedThreadView>(
            await afterReader.ReadThreadAsync(NativeV2ImportFixture.Id));
        CapturedPost afterPost = Assert.Single(after.GetPosts(), post => post.PostId == "15");
        Assert.NotNull(afterPost.Attachments);
        CapturedAttachment attachment = Assert.Single(afterPost.Attachments!);
        Assert.Equal("media-15", attachment.MediaId);
        Assert.Equal(PostFieldPresence.Attachments, afterPost.Fields & PostFieldPresence.Attachments);
    }

    private static NativeTransferCatalog PresenceCatalog()
    {
        NativeTransferCatalog baseCatalog = NativeV2ImportFixture.Content(presence: false);
        TransferThread thread = baseCatalog.Threads[0];
        TransferPost nullPost = new(
            new CapturedPost("16", 2, PostFieldPresence.Attachments, Attachments: null),
            ImmutableArray<TransferAttachment>.Empty)
        { AttachmentsWereNull = true };
        TransferPost emptyPost = new(
            new CapturedPost("17", 3, PostFieldPresence.All, Attachments: Array.Empty<CapturedAttachment>()),
            ImmutableArray<TransferAttachment>.Empty);
        // The field is absent, but the wire-level attachment collection is an
        // ordinary empty array. This is distinct from explicit attachments:null.
        TransferPost missingPost = new(
            new CapturedPost("18", 4, PostFieldPresence.None, Attachments: Array.Empty<CapturedAttachment>()),
            ImmutableArray<TransferAttachment>.Empty);
        return baseCatalog with
        {
            Threads = [thread with { Posts = thread.Posts.Add(nullPost).Add(emptyPost).Add(missingPost) }]
        };
    }
}
