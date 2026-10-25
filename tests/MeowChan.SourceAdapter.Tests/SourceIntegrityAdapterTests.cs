using System.Net;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.Storage;

namespace MeowChan.SourceAdapter.Tests;

public sealed class SourceIntegrityAdapterTests
{
    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public async Task HostPolicyIsRequiredAndProviderShaAndAllAttachmentReferencesSurviveTheAdapter(
        bool hostGrant, bool matchingSha, bool matchingSha1, bool accepted)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime content = new(profile.Path, lease);
        await content.InitializeAsync();
        byte[] bytes = [1, 2, 3, 4];
        string expectedSha = matchingSha ? Convert.ToHexString(SHA256.HashData(bytes)) : new string('A', 64);
        string expectedSha1 = Convert.ToBase64String(SHA1.HashData(matchingSha1 ? bytes : [99]));
        SourceSizeHint sdkHint = new("2.4e1", "Кб");
        MediaProviderSizeHint hint = new(sdkHint.RawValue, sdkHint.UnitLabel);
        const long expectedSize = 21;
        string expectedMd5 = Convert.ToBase64String(new byte[16]);
        PluginDescription description = new FourChanPlugin().Describe();
        description = description with { Sources = [description.Sources[0] with { Maturity = SourceMaturity.Experimental }] };
        Uri mediaUrl = new("https://i.4cdn.org/g/456.png");
        SourceAdapterTests.TestWorker worker = new((request, _, _) => Task.FromResult<ThreadReadResult>(
            new PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(request.Thread,
                new Uri("https://boards.4chan.org/g/thread/123"), "Integrity fixture", SourceThreadState.Active,
                [Post("123", 0, 0), Post("124", 1, 2)]))), description);
        SourcePost Post(string postId, long ordinal, int position) => new(postId, ordinal, SourceFieldPresence.Attachments,
            Attachments: [new SourceAttachment(position, "456", Url: mediaUrl, OriginalFileName: "original.png",
                Extension: ".png", ExpectedSize: expectedSize, ExpectedMd5: expectedMd5, ExpectedSha256: expectedSha)
                { ExpectedSha1 = expectedSha1, ProviderSizeHint = sdkHint }]);
        FixturePolicy? policy = hostGrant ? new(description.PluginId) : null;
        using HttpClient media = new(new Images(bytes));
        using PluginSourceImageBoard source = new(description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker), media)
        { MediaIntegrityPolicy = policy };
        Assert.Same(policy, ((ISourceMediaIntegrityPolicyProvider)source).MediaIntegrityPolicy);
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, Path.Combine(profile.Path, "media"),
            TimeSpan.FromHours(1), 3600, contentRuntime: content, workStore: new WorkStateStore(profile.Path, lease));
        AdmissionResult admission = await registry.AddAsync("https://boards.4chan.org/g/thread/123");
        await SourceAdapterTests.WaitUntilAsync(() => admission.Thread!.DownloadedMediaCount == 1 || admission.Thread.MediaFailures.Count > 0);

        ResourceId resource = new("4chan", "g", "123");
        MediaProviderMetadata provider = new("456", "original", mediaUrl.AbsoluteUri, "original.png", expectedSize, expectedMd5, expectedSha)
            { ExpectedSha1 = expectedSha1, ProviderSizeHint = hint };
        await using ArchiveReader reader = await content.OpenReaderAsync();
        ArchivedThreadView view = (await reader.ReadThreadAsync(resource))!;
        CapturedPost[] posts = view.GetPosts().ToArray();
        Assert.Equal(2, posts.Length);
        foreach (CapturedPost post in posts)
        {
            CapturedAttachment attachment = Assert.Single(post.Attachments!);
            Assert.Equal(expectedSize, attachment.ExpectedSize);
            Assert.Equal(expectedMd5, attachment.ExpectedMd5);
            Assert.Equal(expectedSha, attachment.ExpectedSha256);
            Assert.Equal(expectedSha1, attachment.ExpectedSha1); Assert.Equal(hint, attachment.ProviderSizeHint);
            Assert.Equal(MediaRevisionKeys.CreateAttachmentKey(resource, post.PostId, attachment.Position, provider), attachment.AttachmentRevisionKey);
        }
        MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
        Assert.Equal(accepted ? 1 : 0, admission.Thread!.DownloadedMediaCount);
        Assert.Equal(accepted ? 1 : 0, assets.Count);
        Assert.Equal(accepted ? 2 : 0, assets.AttachmentBindings.Count);
        if (accepted)
        {
            Assert.True(policy!.SnapshotReads > 0);
            Assert.True(policy.Leases > 0);
            foreach (CapturedPost post in posts)
            {
                CapturedAttachment attachment = Assert.Single(post.Attachments!);
                MediaAttachmentBinding binding = assets.FindAttachmentBinding(attachment.AttachmentRevisionKey!)!;
                Assert.NotNull(binding);
                Assert.Equal(provider, binding.Provider);
                Assert.Equal(MediaRevisionKeys.CreateAcquisitionKey(resource, provider), binding.AcquisitionRevisionKey);
                Assert.Equal(post.PostId, binding.Reference.PostId);
                Assert.Equal(attachment.Position, binding.Reference.Position);
                Assert.Equal(MediaAcceptanceKind.ExplicitProviderMismatch, binding.Acceptance.Kind);
                Assert.True(binding.Acceptance.SizeMismatch);
                Assert.True(binding.Acceptance.Md5Mismatch);
                Assert.Equal(bytes.Length, binding.Actual.Length);
                Assert.Equal(expectedSha1, binding.Actual.Sha1);
                await using Stream verified = await assets.OpenVerifiedReadAsync(binding.AssetId);
                using MemoryStream copy = new();
                await verified.CopyToAsync(copy);
                Assert.Equal(bytes, copy.ToArray());
            }
        }
    }

    private sealed class FixturePolicy(string owner) : ISourceMediaIntegrityPolicy
    {
        private readonly SourceMediaIntegrityPolicySnapshot _snapshot = new("fixture-profile", owner, "4chan", true, 1);
        public int SnapshotReads;
        public int Leases;
        public SourceMediaIntegrityPolicySnapshot GetSnapshot()
        {
            Interlocked.Increment(ref SnapshotReads);
            return _snapshot;
        }
        public ValueTask<MediaAcceptanceLease> AcquireAcceptanceLeaseAsync(long expectedRevision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_snapshot.Revision, expectedRevision);
            Interlocked.Increment(ref Leases);
            return ValueTask.FromResult<MediaAcceptanceLease>(new FixtureLease(_snapshot));
        }
    }

    private sealed class FixtureLease(SourceMediaIntegrityPolicySnapshot snapshot) : MediaAcceptanceLease(snapshot)
    {
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Images(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
    }
}
