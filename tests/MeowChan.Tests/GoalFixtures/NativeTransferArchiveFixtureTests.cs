using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.GoalFixtures;

public sealed class NativeTransferArchiveFixtureTests
{
    [Theory]
    [InlineData(ThreadSourceState.Archived, null)]
    [InlineData(ThreadSourceState.Archived, false)]
    [InlineData(ThreadSourceState.Archived, true)]
    [InlineData(ThreadSourceState.NotFound, null)]
    [InlineData(ThreadSourceState.NotFound, false)]
    [InlineData(ThreadSourceState.NotFound, true)]
    public async Task RealCatalogAndManifestReopenWithExactMetadataAndVerifiedOriginals(
        ThreadSourceState state, bool? decimalPostOrder)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync(state, decimalPostOrder);
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            // Creation retained no lease; a fresh real catalog writer can open and close.
            using (ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath))
            {
                await using ArchiveCatalog writer = await ArchiveCatalog.OpenAsync(fixture.ProfilePath, lease);
                Assert.True(await writer.GetCaptureEnabledAsync(fixture.ThreadId));
            }
            byte[] catalogBefore = await File.ReadAllBytesAsync(Path.Combine(fixture.ProfilePath, "archive.db"));
            byte[] manifestBefore = await File.ReadAllBytesAsync(Path.Combine(fixture.MediaDirectory, ".meowchan-manifest.json"));
            await using (ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath))
            {
                Assert.Equal(fixture.ThreadId, Assert.Single((await reader.ListThreadsAsync()).Items).Id);
                ArchivedThreadView? view = await reader.ReadThreadAsync(fixture.ThreadId);
                Assert.NotNull(view);
                Assert.Equal(fixture.Summary, view.Summary);
                Assert.Equal(state, view.Summary.SourceState);
                Assert.Equal(fixture.Snapshot.CapturedAt, view.Summary.FirstSeenAt);
                Assert.Equal(fixture.Snapshot.CapturedAt, view.Summary.LastCapturedAt);
                Assert.Equal(fixture.CaptureRevision, view.Summary.Revision);
                Assert.True(view.Summary.Revision > 0);
                Assert.Equal(decimalPostOrder, view.DecimalPostOrder);
                Assert.Equal(NativeTransferArchiveFixture.ThreadUrl, view.Summary.Url);
                Assert.Equal(fixture.MediaDirectory, Assert.Single(await reader.GetMediaDirectoriesAsync(fixture.ThreadId)));
                Assert.Equal(fixture.MediaDirectory, Assert.Single(view.MediaDirectories));
                IReadOnlyList<CapturedPost> posts = view.GetPosts();
                Assert.Equal(3, posts.Count);
                Assert.Equal(fixture.ExpectedPostIds, posts.Select(p => p.PostId).ToArray());
                foreach (CapturedPost expected in fixture.Posts)
                {
                    CapturedPost saved = Assert.Single(posts, p => p.PostId == expected.PostId);
                    Assert.Equal(expected.Ordinal, saved.Ordinal);
                    Assert.Equal(expected.Fields, saved.Fields);
                    Assert.Equal(expected.PostedAt, saved.PostedAt);
                    Assert.Equal(expected.Subject, saved.Subject);
                    Assert.Equal(expected.Author, saved.Author);
                    Assert.Equal(expected.Html, saved.Html);
                    Assert.Equal(expected.IsDeleted, saved.IsDeleted);
                    Assert.Equal(Assert.Single(expected.Attachments!), Assert.Single(saved.Attachments!));
                }
                MediaAssetSnapshot media = await MediaAssetReader.OpenAsync(fixture.MediaDirectory);
                Assert.Equal(MediaManifestStatus.Available, media.Status);
                Assert.Equal(4, media.ManifestVersion);
                Assert.Equal(fixture.ManifestGeneration, media.Generation);
                Assert.Equal(2, media.Count);
                Assert.Single(media.AttachmentBindings);
                await VerifyOriginalAsync(media, fixture, posts, NativeTransferArchiveFixture.StrictPostId,
                    fixture.StrictAttachment, fixture.StrictAssetId, fixture.StrictBytes, isLegacy: false);
                await VerifyOriginalAsync(media, fixture, posts, NativeTransferArchiveFixture.LegacyPostId,
                    fixture.LegacyAttachment, fixture.LegacyAssetId, fixture.LegacyBytes, isLegacy: true);
                Assert.Null(MediaAttachmentResolver.Resolve(media, fixture.ThreadId,
                    NativeTransferArchiveFixture.UnavailablePostId, fixture.UnavailableAttachment));
                Assert.Null(media.FindAttachmentBinding(fixture.UnavailableRevision));
                Assert.DoesNotContain(media.GetPage(), asset => asset.MediaIdentifier == fixture.UnavailableAttachment.MediaId);
                Assert.False(File.Exists(fixture.UnavailablePath));
            }
            // Passive local readers did not repair or mutate either store.
            Assert.Equal(catalogBefore, await File.ReadAllBytesAsync(Path.Combine(fixture.ProfilePath, "archive.db")));
            Assert.Equal(manifestBefore, await File.ReadAllBytesAsync(Path.Combine(fixture.MediaDirectory, ".meowchan-manifest.json")));
            Assert.Equal(0, fixture.UnexpectedProviderCalls);
        }
    }

    private static async Task VerifyOriginalAsync(MediaAssetSnapshot media, NativeTransferArchiveFixture fixture,
        IReadOnlyList<CapturedPost> posts, string postId, CapturedAttachment expectedAttachment, string assetId,
        ReadOnlyMemory<byte> bytes, bool isLegacy)
    {
        CapturedAttachment captured = Assert.Single(Assert.Single(posts, p => p.PostId == postId).Attachments!);
        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(media, fixture.ThreadId, postId, captured);
        Assert.NotNull(resolved);
        Assert.Equal(isLegacy, resolved.IsLegacy);
        Assert.Same(media, resolved.Snapshot);
        Assert.Equal(expectedAttachment, captured);
        Assert.Equal(assetId, resolved.Asset.AssetId);
        Assert.Equal(isLegacy ? fixture.LegacyCommit.Path : fixture.StrictCommit.Path, resolved.Asset.Path);
        Assert.Equal("original", resolved.Asset.Variant);
        Assert.Equal(expectedAttachment.Url, resolved.Asset.SourceUrl);
        Assert.Equal(expectedAttachment.OriginalFileName, resolved.Asset.OriginalFileName);
        Assert.Equal((long)bytes.Length, resolved.Asset.Length);
        Assert.Equal(Hash(bytes), resolved.Asset.Sha256);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(bytes.Span)), resolved.Asset.Md5);
        Assert.Equal(Convert.ToBase64String(SHA1.HashData(bytes.Span)), resolved.Asset.Sha1);
        Assert.True(resolved.Asset.IsCurrent);
        if (isLegacy)
        {
            Assert.Null(captured.AttachmentRevisionKey);
            Assert.Null(resolved.Binding);
            Assert.Empty(fixture.LegacyCommit.AttachmentRevisionKeys);
            Assert.Null(fixture.LegacyCommit.AcquisitionRevisionKey);
        }
        else
        {
            Assert.Equal(fixture.StrictBinding, resolved.Binding);
            Assert.Equal(fixture.StrictRevision, Assert.Single(fixture.StrictCommit.AttachmentRevisionKeys));
            Assert.StartsWith("att-v2:", captured.AttachmentRevisionKey);
            Assert.StartsWith("acq-v2:", fixture.StrictCommit.AcquisitionRevisionKey);
            MediaAttachmentBinding binding = resolved.Binding!;
            Assert.Equal(fixture.ThreadId, binding.Reference.Resource);
            Assert.Equal(postId, binding.Reference.PostId);
            Assert.Equal(expectedAttachment.Position, binding.Reference.Position);
            Assert.Equal(assetId, binding.AssetId);
            Assert.Equal((long)bytes.Length, binding.Actual.Length);
            Assert.Equal(Hash(bytes), binding.Actual.Sha256);
            Assert.Equal(captured.ExpectedMd5, binding.Actual.Md5);
            Assert.Equal(captured.ExpectedSha1, binding.Actual.Sha1);
            Assert.True(ProviderIntegrityAssessment.Assess(binding.Provider, binding.Actual).IsStrictMatch);
            Assert.Equal(MediaAcceptanceKind.Strict, binding.Acceptance.Kind);
            Assert.NotEqual(default(DateTimeOffset), binding.Acceptance.AcceptedAtUtc);
            Assert.False(binding.Acceptance.SizeMismatch);
            Assert.False(binding.Acceptance.Md5Mismatch);
            Assert.Null(binding.Acceptance.ProfileId);
            Assert.Null(binding.Acceptance.PluginOwnerId);
            Assert.Null(binding.Acceptance.SourceId);
            Assert.Null(binding.Acceptance.PolicyRevision);
        }
        await using Stream verified = await resolved.Snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId);
        Assert.Equal(0, verified.Position);
        using MemoryStream copy = new();
        await verified.CopyToAsync(copy);
        Assert.Equal(bytes.ToArray(), copy.ToArray());
    }

    [Fact]
    public async Task CanonicalReferencesAndSimplePassiveHtmlFitCurrentTransferGrammar()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        Assert.Equal(new ResourceId("4chan", "b", "7001"), fixture.ThreadId);
        Assert.True(TransferPublicReferences.IsThreadPage(fixture.Snapshot.Url, fixture.ThreadId));
        foreach (CapturedPost post in fixture.Posts)
        {
            Assert.True(TransferPublicReferences.IsInertHtml(post.Html!));
            CapturedAttachment attachment = Assert.Single(post.Attachments!);
            Assert.True(TransferPublicReferences.IsPublicUrl(attachment.Url!));
            Assert.Equal("original", attachment.Variant);
            Assert.EndsWith(".jpg", attachment.Url);
            Assert.Equal("i.4cdn.org", new Uri(attachment.Url!).Host);
        }
        Assert.Equal("https://i.4cdn.org/b/111.jpg", fixture.StrictAttachment.Url);
        Assert.Equal("https://i.4cdn.org/b/112.jpg", fixture.LegacyAttachment.Url);
        Assert.Equal("https://i.4cdn.org/b/113.jpg", fixture.UnavailableAttachment.Url);
        Assert.StartsWith("att-v1:", fixture.UnavailableRevision);
        Assert.Null(fixture.LegacyRevision);
        Assert.Null(fixture.LegacyAttachment.ExpectedSize);
        Assert.Null(fixture.LegacyAttachment.ExpectedMd5);
        Assert.Null(fixture.LegacyAttachment.ExpectedSha1);
        Assert.Null(fixture.LegacyAttachment.ExpectedSha256);
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    [Fact]
    public async Task LooseSyntheticFileDoesNotInventAnAssetForUnavailableMetadata()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        // Deliberate decoy after fixture creation: even exact bytes on disk are not a manifest asset.
        await File.WriteAllBytesAsync(fixture.UnavailablePath, fixture.LegacyBytes.ToArray());
        MediaAssetSnapshot media = await MediaAssetReader.OpenAsync(fixture.MediaDirectory);
        Assert.Equal(2, media.Count);
        Assert.Null(MediaAttachmentResolver.Resolve(media, fixture.ThreadId,
            NativeTransferArchiveFixture.UnavailablePostId, fixture.UnavailableAttachment));
        Assert.Null(MediaAttachmentResolver.Resolve(media, fixture.ThreadId,
            NativeTransferArchiveFixture.UnavailablePostId, fixture.UnavailableAttachment with { AttachmentRevisionKey = null }));
        Assert.Null(MediaAttachmentResolver.Resolve(media, fixture.ThreadId,
            NativeTransferArchiveFixture.LegacyPostId, fixture.LegacyAttachment with { Url = "https://i.4cdn.org/b/999.jpg" }));
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    [Fact]
    public async Task VerifiedReadDetectsChangedStrictBytesWithoutRepairOrNetwork()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.NotFound);
        MediaAssetSnapshot media = await MediaAssetReader.OpenAsync(fixture.MediaDirectory);
        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(media, fixture.ThreadId,
            NativeTransferArchiveFixture.StrictPostId, fixture.StrictAttachment);
        Assert.NotNull(resolved);
        byte[] changed = fixture.StrictBytes.ToArray();
        changed[0] ^= 1;
        await File.WriteAllBytesAsync(resolved.Asset.Path, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => media.OpenVerifiedReadAsync(fixture.StrictAssetId));
        Assert.Equal(changed, await File.ReadAllBytesAsync(resolved.Asset.Path));
        await using Stream legacy = await media.OpenVerifiedReadAsync(fixture.LegacyAssetId);
        using MemoryStream copy = new();
        await legacy.CopyToAsync(copy);
        Assert.Equal(fixture.LegacyBytes.ToArray(), copy.ToArray());
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    [Fact]
    public async Task IndependentProfilesHaveDeterministicPayloadsAssetIdsAndRevisionKeys()
    {
        await using NativeTransferArchiveFixture first = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.Archived, true);
        await using NativeTransferArchiveFixture second = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.NotFound, false);
        Assert.NotEqual(first.ProfilePath, second.ProfilePath);
        Assert.NotEqual(first.MediaDirectory, second.MediaDirectory);
        Assert.Equal(first.StrictBytes.ToArray(), second.StrictBytes.ToArray());
        Assert.Equal(first.LegacyBytes.ToArray(), second.LegacyBytes.ToArray());
        Assert.NotEqual(Hash(first.StrictBytes), Hash(first.LegacyBytes));
        Assert.Equal(first.StrictAssetId, second.StrictAssetId);
        Assert.Equal(first.LegacyAssetId, second.LegacyAssetId);
        Assert.Equal(first.StrictRevision, second.StrictRevision);
        Assert.Equal(first.UnavailableRevision, second.UnavailableRevision);
        Assert.NotEqual(first.StrictAssetId, first.LegacyAssetId);
        Assert.NotEqual(first.StrictCommit.Path, second.StrictCommit.Path);
        Assert.Equal(0, first.UnexpectedProviderCalls);
        Assert.Equal(0, second.UnexpectedProviderCalls);
        // Store-owned acceptance timestamps/generation are intentionally not forged for determinism.
    }

    [Fact]
    public async Task DisposeRemovesOnlyTheOwnedSyntheticProfile()
    {
        NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        string path = fixture.ProfilePath;
        Assert.True(Directory.Exists(path));
        await fixture.DisposeAsync();
        Assert.False(Directory.Exists(path));
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task PrecancelledCreationPropagatesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NativeTransferArchiveFixture.CreateAsync(cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(ThreadSourceState.Active)]
    [InlineData((ThreadSourceState)999)]
    public async Task UnsupportedSourceStatesAreRejectedBeforeBuildingAProfile(ThreadSourceState state)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeTransferArchiveFixture.CreateAsync(state));
    }

    private static string Hash(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
}
