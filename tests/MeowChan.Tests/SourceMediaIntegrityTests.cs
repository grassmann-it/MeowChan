using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Interfaces;
using MeowChan.Rules;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class SourceMediaIntegrityTests
{
    private static readonly byte[] Bytes = [1, 3, 5, 7];
    private static readonly ResourceId Resource = new("stub", "g", "123");

    [Fact]
    public void RevisionEncodingDistinguishesNullEmptyFieldsAndPostSlots()
    {
        MediaProviderMetadata raw = new("m", "original", "https://stub.test/m", null, 4, null, null);
        string key = MediaRevisionKeys.CreateAcquisitionKey(Resource, raw);
        Assert.Equal("acq-v1:d06dd1b70bde24eb57ca751654dbaa412b0ae267902bed2708bd842667653d5b", key);
        Assert.Equal("att-v1:eff9c51075b322780269276bb60c9bb337cd2193879f9d5e7ea4787b4f9164e6",
            MediaRevisionKeys.CreateAttachmentKey(Resource, "post", 0, raw));
        Assert.Equal(key, MediaRevisionKeys.CreateAcquisitionKey(Resource, raw));
        Assert.NotEqual(key, MediaRevisionKeys.CreateAcquisitionKey(Resource, raw with { OriginalFileName = "" }));
        Assert.NotEqual(MediaRevisionKeys.CreateAttachmentKey(Resource, "12", 3, raw),
            MediaRevisionKeys.CreateAttachmentKey(Resource, "1", 23, raw));
        Assert.NotEqual(key, MediaRevisionKeys.CreateAcquisitionKey(Resource, raw with { ExpectedSha256 = new string('a', 64) }));
    }

    [Fact]
    public void InvalidUnicodeCannotCollapseDistinctRawRevisionComponents()
    {
        foreach (char invalid in new[] { (char)0xD800, (char)0xDFFF })
        {
            MediaProviderMetadata raw = new("m", "original", "https://stub.test/m", new string(invalid, 1), 4, null, null);
            Assert.Throws<EncoderFallbackException>(() => MediaRevisionKeys.CreateAcquisitionKey(Resource, raw));
        }
    }

    [Fact]
    public async Task DefaultRejectsProviderDiscrepancyWithoutPublishing()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        DownloadItem item = Item(board, directory, null, 9);
        await Assert.ThrowsAsync<InvalidDataException>(() => Commit(new MediaStore(), item));
        Assert.False(File.Exists(item.DestinationPath));
        Assert.False(File.Exists(Manifest(item)));
    }

    [Fact]
    public async Task ExplicitPermissionRecordsRawAndActualForEveryReferenceAndSurvivesRevocation()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        TestPolicy policy = new();
        DownloadItem item = Item(board, directory, policy, 9, md5: Convert.ToBase64String(MD5.HashData(new byte[] { 99 })), posts: ["123", "124"]);
        MediaCommitResult result = await Commit(new MediaStore(), item);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(Manifest(item)))!.AsObject();
        Assert.Equal(4, (int)manifest["Version"]!);
        Assert.Equal(2, manifest["AttachmentBindings"]!.AsObject().Count);
        JsonNode binding = manifest["AttachmentBindings"]![item.AttachmentReferences[0].RevisionKey]!;
        Assert.Equal(9, (long)binding["Provider"]!["ExpectedSize"]!);
        Assert.Equal(Bytes.Length, (long)binding["Actual"]!["Length"]!);
        Assert.True((bool)binding["Acceptance"]!["SizeMismatch"]!);
        Assert.True((bool)binding["Acceptance"]!["Md5Mismatch"]!);
        await policy.SetAsync(false);
        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, CancellationToken.None);
        Assert.Equal(result.AssetId, restored?.AssetId);
        await File.WriteAllBytesAsync(result.Path, [9, 9, 9, 9]);
        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task NewReferenceNeedsNewPermissionEvenForPreviouslyAcceptedBytes()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem first = Item(board, directory, policy, 9);
        await Commit(new MediaStore(), first);
        byte[] before = await File.ReadAllBytesAsync(Manifest(first));
        await policy.SetAsync(false);
        DownloadItem additional = Item(board, directory, policy, 9, posts: ["123", "124"]);
        Assert.Null(await new MediaStore().TryRestoreAsync(additional, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => Commit(new MediaStore(), additional));
        Assert.Equal(before, await File.ReadAllBytesAsync(Manifest(first)));
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("transport")]
    [InlineData("cap")]
    public async Task ProviderShaTransportAndActualCapRemainStrict(string kind)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem item = Item(board, directory, policy, 9, sha: kind == "sha" ? new string('a', 64) : null);
        if (kind == "transport") item = item.WithTransportLength(Bytes.Length + 1);
        if (kind == "cap") item.MaximumActualBytes = Bytes.Length - 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => Commit(new MediaStore(), item));
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Fact]
    public async Task RevocationImmediatelyBeforeAcceptanceLeasePreventsPublication()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem item = Item(board, directory, policy, 9);
        policy.BeforeAcquire = () => policy.SetAsync(false);
        await Assert.ThrowsAsync<InvalidDataException>(() => Commit(new MediaStore(), item));
        Assert.False(File.Exists(item.DestinationPath));
    }

    [Fact]
    public async Task LeaseSpansPublicationAndManifestSaveBeforeRevocationAcknowledges()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem item = Item(board, directory, policy, 9);
        Task? revoke = null;
        MediaStore store = new((from, to, overwrite) => {
            revoke ??= policy.SetAsync(false);
            Assert.False(revoke.IsCompleted);
            File.Move(from, to, overwrite);
        });
        await Commit(store, item);
        await revoke!;
        Assert.False(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        Assert.NotNull(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task SameRevisionDifferentBytesCannotOverwriteBindingOrPublishSecondAsset()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem item = Item(board, directory, policy, 9);
        MediaCommitResult committed = await Commit(new MediaStore(), item);
        byte[] manifest = await File.ReadAllBytesAsync(Manifest(item));
        await Assert.ThrowsAsync<InvalidDataException>(() => Commit(new MediaStore(), item, [2, 4, 6, 8]));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Manifest(item)));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(committed.Path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(committed.Path)!, "*.bin"));
    }

    [Fact]
    public async Task FailedManifestNeverProvidesTolerantOrphanRestoreProof()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem item = Item(board, directory, policy, 9);
        MediaStore failing = new((from, to, overwrite) => {
            if (Path.GetFileName(to) == ".meowchan-manifest.json") throw new IOException("fixture publication failure");
            File.Move(from, to, overwrite);
        });
        await Assert.ThrowsAsync<IOException>(() => Commit(failing, item));
        Assert.True(File.Exists(item.DestinationPath));
        Assert.Null(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    public async Task ChangedRawUrlCannotRestoreAnotherRevisionsAcceptance(long size)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem original = Item(board, directory, policy, size);
        await Commit(new MediaStore(), original);
        byte[] before = await File.ReadAllBytesAsync(Manifest(original));
        DownloadItem changed = Item(board, directory, policy, size, url: "https://stub.test/media/m.bin?revision=2");
        Assert.Null(await new MediaStore().TryRestoreAsync(changed, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(Manifest(original)));
    }

    [Fact]
    public async Task V2StrictPublishedSizeOnlyRestoresButSizeOnlyOrphanDoesNot()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        DownloadItem item = Item(board, directory, null, Bytes.Length);
        await Commit(new MediaStore(), item);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(Manifest(item)))!.AsObject();
        manifest["Version"] = 2;
        manifest.Remove("AttachmentBindings");
        await File.WriteAllTextAsync(Manifest(item), manifest.ToJsonString());
        DownloadItem legacy = new(item.DownloadUri, item.DestinationPath, board, item.Thread, item.MediaIdentifier,
            item.ExpectedSize, item.ExpectedMd5, item.OriginalFileName);
        Assert.NotNull(await new MediaStore().TryRestoreAsync(legacy, CancellationToken.None));
        File.Delete(Manifest(item));
        Assert.Null(await new MediaStore().TryRestoreAsync(legacy, CancellationToken.None));
    }

    [Fact]
    public async Task CorruptV3BindingCannotBeIgnoredDuringRestoreOrNewCommit()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem item = Item(board, directory, policy, 9);
        await Commit(new MediaStore(), item);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(Manifest(item)))!.AsObject();
        manifest["AttachmentBindings"]![item.AttachmentReferences[0].RevisionKey]!["Actual"]!["Length"] = 8;
        await File.WriteAllTextAsync(Manifest(item), manifest.ToJsonString());
        byte[] before = await File.ReadAllBytesAsync(Manifest(item));
        await Assert.ThrowsAsync<InvalidDataException>(() => new MediaStore().TryRestoreAsync(item, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => Commit(new MediaStore(), item));
        Assert.Equal(before, await File.ReadAllBytesAsync(Manifest(item)));
    }

    [Fact]
    public async Task OldTerminalWorkReplayPreservesLegacyAssociationUntilFreshObservation()
    {
        using TempDirectory profile = new(); using StubImageBoard board = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, lease);
        await runtime.InitializeAsync();
        string output = Path.Combine(profile.Path, "media");
        string directory = StorageLayout.GetThreadDirectory(output, "stub", "g", "123");
        string target = Path.Combine(directory, "m.bin");
        const string threadUrl = "https://stub.test/g/thread/123";
        DownloadItem legacyItem = new(new Uri("https://stub.test/media/m.bin"), target, board,
            board.GetThread(new Uri(threadUrl)), "m", Bytes.Length, originalFileName: "m.bin");
        MediaCommitResult asset = await Commit(new MediaStore(), legacyItem);
        CapturedAttachment legacyAttachment = new(0, "m", Url: legacyItem.DownloadUri.AbsoluteUri,
            OriginalFileName: "m.bin", PlannedPath: target, ExpectedSize: Bytes.Length);
        CapturedThreadSnapshot oldSnapshot = new(Resource, threadUrl, "old terminal", ThreadSourceState.Archived,
            DateTimeOffset.UtcNow, directory, [new CapturedPost("123", 123, PostFieldPresence.All, Attachments: [legacyAttachment])]);
        await runtime.Catalog.CapturePreservingRevisionsAsync(oldSnapshot);
        WorkStateStore work = new(profile.Path, lease);
        work.CommitIntent(new WorkResource { Url = threadUrl, OutputRoot = output, Generation = 1,
            Intent = WorkIntent.Watching, IsThread = true, Source = ThreadSourceStateValue.Archived,
            EndObservedAt = DateTimeOffset.UtcNow, CapturePending = true, FinalSnapshotJson = JsonSerializer.Serialize(oldSnapshot) });
        ReplayThread thread = new(board.Board, new Uri(threadUrl));
        thread.RestoreDurableState(work.LoadResources().Single());
        await runtime.BindAsync(thread, output, restored: true);
        await thread.ReplayAsync();
        await thread.ReplayAsync();
        await using ArchiveReader reader = await runtime.OpenReaderAsync();
        ArchivedThreadView replayed = (await reader.ReadThreadAsync(Resource))!;
        CapturedAttachment replayedAttachment = replayed.GetPosts()[0].Attachments![0];
        Assert.Null(replayedAttachment.AttachmentRevisionKey);
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        Assert.Equal(asset.AssetId, MediaAttachmentResolver.Resolve(manifest, Resource, "123", replayedAttachment)?.Asset.AssetId);
        await thread.ObserveAsync(oldSnapshot);
        ArchivedThreadView fresh = (await reader.ReadThreadAsync(Resource))!;
        CapturedAttachment freshAttachment = fresh.GetPosts()[0].Attachments![0];
        Assert.NotNull(freshAttachment.AttachmentRevisionKey);
        Assert.Null(MediaAttachmentResolver.Resolve(manifest, Resource, "123", freshAttachment));
    }

    private sealed class ReplayThread(IBoard board, Uri uri) : ProbeThread(board, uri)
    {
        internal Task ReplayAsync() => CaptureContentAsync(null, CancellationToken.None);
        internal Task ObserveAsync(CapturedThreadSnapshot observation) => CaptureContentAsync(observation, CancellationToken.None);
    }

    [Fact]
    public async Task ExplicitRetryRemovingPluginFilterUsesNewActualByteCap()
    {
        using TempDirectory profile = new(); using StubImageBoard board = new();
        using ProfileLease profileLease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime runtime = new(profile.Path, profileLease);
        await runtime.InitializeAsync();
        await runtime.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global, sizeLimit: MediaSizeLimit.Unlimited) {
            ImageFilter = new(MediaImageFilterMode.Plugin, "fixture.filter", "filter") }]);
        DownloadItem original = Item(board, profile, new TestPolicy(), 1);
        CandidateMetadata metadata = new(new CandidateKey(Resource, "m"), original.DownloadUri, original.DestinationPath,
            original.ExpectedSize, original.ExpectedMd5, original.OriginalFileName) {
            AcquisitionRevisionKey = original.AcquisitionRevisionKey, AttachmentReferences = original.AttachmentReferences };
        await runtime.Catalog.GetOrCreateDecisionAsync(metadata, runtime.Rules);
        ArchiveThreadSummary summary = await runtime.Catalog.RegisterThreadAsync(new(Resource, original.Thread.Url,
            "fixture", profile.Path, true));
        ThreadContentContext context = new(runtime, summary);
        await context.InitializeFiltersAsync(CancellationToken.None);
        await runtime.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global,
            sizeLimit: new MediaSizeLimit(MediaSizeLimitMode.Limited, 3)) { ImageFilter = MediaImageFilterRule.Disabled }]);
        DownloadItem retry = context.BindRestoredMediaFilter(original, (ProbeThread)original.Thread, explicitRetry: true);
        Assert.Equal(3L, retry.MaximumActualBytes);
        Assert.NotNull(retry.Filter);
        await using IOriginalMediaFilterLease filterLease = await retry.Filter.AcquireAsync(retry, CancellationToken.None);
        using MemoryStream body = new(Bytes);
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => filterLease.CommitAsync(retry, body,
            new MediaStore(), (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Contains("effective rule limit", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(retry.DestinationPath));
        Assert.False(File.Exists(Manifest(retry)));
    }

    [Fact]
    public void DurableReferenceSetsHaveCanonicalImmutableValueEquality()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        DownloadItem item = Item(board, directory, null, Bytes.Length, posts: ["123", "124"]);
        List<AttachmentRevisionReference> callerReferences = item.AttachmentReferences.Reverse().ToList();
        MediaWork original = new() { ResourceUrl = item.Thread.Url, Generation = 1, MediaIdentifier = item.MediaIdentifier,
            DestinationPath = item.DestinationPath, DownloadUrl = item.DownloadUri.AbsoluteUri,
            AcquisitionRevisionKey = item.AcquisitionRevisionKey, AttachmentReferences = callerReferences };
        callerReferences.Clear();
        Assert.Equal(2, original.AttachmentReferences.Count);
        MediaWork restored = JsonSerializer.Deserialize<MediaWork>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original, restored);
        Assert.Equal(original.GetHashCode(), restored.GetHashCode());
        MediaWork reordered = original with { AttachmentReferences = original.AttachmentReferences.Reverse().ToArray() };
        Assert.Equal(original, reordered);
        Assert.Equal(original.GetHashCode(), reordered.GetHashCode());
        Assert.Throws<InvalidDataException>(() => original with {
            AttachmentReferences = [original.AttachmentReferences[0], original.AttachmentReferences[0]] });
    }

    [Fact]
    public async Task TwoIntactLegacyCopiesWithOnlyMatchingSizeRemainAmbiguous()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        DownloadItem observed = Item(board, directory, null, Bytes.Length);
        DownloadItem legacy = new(observed.DownloadUri, observed.DestinationPath, board, observed.Thread,
            observed.MediaIdentifier, Bytes.Length, originalFileName: observed.OriginalFileName);
        MediaStore store = new();
        MediaCommitResult first = await Commit(store, legacy);
        MediaCommitResult second = await Commit(store, legacy, [2, 4, 6, 8]);
        Assert.NotEqual(first.AssetId, second.AssetId);
        Assert.Null(await new MediaStore().TryRestoreAsync(legacy, CancellationToken.None));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(first.Path));
        Assert.Equal(new byte[] { 2, 4, 6, 8 }, await File.ReadAllBytesAsync(second.Path));
    }

    [Fact]
    public async Task TypedInvalidV3BindingCannotBeOverwrittenByReferenceFreeLegacyCommit()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new(); TestPolicy policy = new();
        DownloadItem item = Item(board, directory, policy, 9);
        await Commit(new MediaStore(), item);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(Manifest(item)))!.AsObject();
        manifest["AttachmentBindings"]![item.AttachmentReferences[0].RevisionKey]!["Actual"]!["Length"] = "bad";
        await File.WriteAllTextAsync(Manifest(item), manifest.ToJsonString());
        byte[] corruptBytes = await File.ReadAllBytesAsync(Manifest(item));
        Assert.Equal(MediaManifestStatus.Corrupt, (await MediaAssetReader.OpenAsync(directory.Path)).Status);
        DownloadItem legacy = new(item.DownloadUri, item.DestinationPath, board, item.Thread,
            item.MediaIdentifier, Bytes.Length, originalFileName: item.OriginalFileName);
        await Assert.ThrowsAsync<InvalidDataException>(() => Commit(new MediaStore(), legacy));
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(Manifest(item)));
        // A future schema still throws before typed parsing, even with the same malformed body.
        manifest["Version"] = 99;
        await File.WriteAllTextAsync(Manifest(item), manifest.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaAssetReader.OpenAsync(directory.Path));
    }

    [Fact]
    public async Task Sha1IsMeasuredPersistedAndStrictEvenWithMismatchPermission()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        string sha1 = Convert.ToBase64String(SHA1.HashData(Bytes));
        MediaProviderSizeHint hint = new("2.4e1", "Кб");
        DownloadItem valid = Item(board, directory, null, null, sha1: sha1, hint: hint);
        MediaCommitResult committed = await Commit(new MediaStore(), valid);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);
        MediaAttachmentBinding binding = snapshot.FindAttachmentBinding(valid.AttachmentReferences[0].RevisionKey)!;
        Assert.Equal(sha1, binding.Actual.Sha1); Assert.Equal(sha1, binding.Provider.ExpectedSha1);
        Assert.Equal(hint, binding.Provider.ProviderSizeHint); Assert.Null(binding.Provider.ExpectedSize);
        Assert.Equal(sha1, snapshot.FindById(binding.AssetId)!.Sha1);
        Assert.Equal(committed.AssetId, (await new MediaStore().TryRestoreAsync(valid, default))!.AssetId);
        Assert.StartsWith("acq-v2:", valid.AcquisitionRevisionKey);
        DownloadItem invalid = Item(board, directory, new TestPolicy(), 9,
            sha1: Convert.ToBase64String(SHA1.HashData([99])), hint: hint);
        await Assert.ThrowsAsync<InvalidDataException>(() => Commit(new MediaStore(), invalid));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(committed.Path));
        await File.WriteAllBytesAsync(committed.Path, [2, 4, 6, 8]);
        Assert.Null(await new MediaStore().TryRestoreAsync(valid, default));
    }

    [Fact]
    public async Task HistoricalV3BindingWithoutSha1ReusesItsExactBytes()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        DownloadItem item = Item(board, directory, null, Bytes.Length);
        MediaCommitResult committed = await Commit(new MediaStore(), item);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(Manifest(item)))!.AsObject();
        manifest["Version"] = 3;
        foreach (JsonNode? entry in manifest["Entries"]!.AsObject().Select(pair => pair.Value)) entry!.AsObject().Remove("Sha1");
        foreach (JsonNode? asset in manifest["Assets"]!.AsObject().Select(pair => pair.Value)) asset!["Entry"]!.AsObject().Remove("Sha1");
        foreach (JsonNode? binding in manifest["AttachmentBindings"]!.AsObject().Select(pair => pair.Value))
        {
            binding!["Actual"]!.AsObject().Remove("Sha1");
            binding["Provider"]!.AsObject().Remove("ExpectedSha1");
            binding["Provider"]!.AsObject().Remove("ProviderSizeHint");
        }
        await File.WriteAllTextAsync(Manifest(item), manifest.ToJsonString());
        Assert.Equal(committed.AssetId, (await new MediaStore().TryRestoreAsync(item, default))!.AssetId);
        Assert.Equal(committed.AssetId, (await Commit(new MediaStore(), item)).AssetId);
        DownloadItem additional = Item(board, directory, null, Bytes.Length, posts: ["123", "124"]);
        Assert.Equal(committed.AssetId, (await Commit(new MediaStore(), additional)).AssetId);
        Assert.Equal(committed.AssetId, (await new MediaStore().TryRestoreAsync(additional, default))!.AssetId);
        Assert.Equal(MediaManifestStatus.Available, (await MediaAssetReader.OpenAsync(directory.Path)).Status);
    }

    [Fact]
    public void BuiltinCaptureProjectionDoesNotDropExtendedProviderMetadata()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        DownloadItem item = Item(board, directory, null, null, sha1: Convert.ToBase64String(SHA1.HashData(Bytes)), hint: new("24", "Кб"), variant: "preview");
        CapturedAttachment capture = ProviderPostCapture.Attachment(item, 2);
        Assert.Equal(item.ExpectedSha1, capture.ExpectedSha1); Assert.Equal(item.ProviderSizeHint, capture.ProviderSizeHint);
        Assert.Null(capture.ExpectedSize); Assert.Equal("preview", capture.Variant);
    }

    [Fact]
    public void MissingSha1EvidenceAndMd5ShapedSha1NeverPass()
    {
        string sha1 = Convert.ToBase64String(SHA1.HashData(Bytes));
        MediaProviderMetadata provider = new("m", "original", "https://stub.test/m", null, null, null, null) { ExpectedSha1 = sha1 };
        MediaActualIntegrity old = new(Bytes.Length, Convert.ToBase64String(MD5.HashData(Bytes)), Convert.ToHexString(SHA256.HashData(Bytes)));
        Assert.True(ProviderIntegrityAssessment.Assess(provider, old).Sha1Mismatch);
        Assert.Throws<InvalidDataException>(() => ProviderIntegrityAssessment.Assess(provider with { ExpectedSha1 = old.Md5 }, old));
        string key = MediaRevisionKeys.CreateAcquisitionKey(Resource, provider);
        Assert.NotEqual(key, MediaRevisionKeys.CreateAcquisitionKey(Resource, provider with { ProviderSizeHint = new("24", "Кб") }));
    }

    private static DownloadItem Item(StubImageBoard board, TempDirectory directory, TestPolicy? policy,
        long? size, string? md5 = null, string? sha = null, string[]? posts = null, string url = "https://stub.test/media/m.bin", string? sha1 = null, MediaProviderSizeHint? hint = null, string variant = "original")
    {
        MediaProviderMetadata provider = new("m", variant, url, "m.bin", size, md5, sha) { ExpectedSha1 = sha1, ProviderSizeHint = hint };
        AttachmentRevisionReference[] references = (posts ?? ["123"]).Select(post => new AttachmentRevisionReference(Resource,
            post, 0, MediaRevisionKeys.CreateAttachmentKey(Resource, post, 0, provider))).ToArray();
        return new DownloadItem(new Uri(url), Path.Combine(directory.Path, "m.bin"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), "m", size, md5, "m.bin", null, variant) {
            ExpectedSha256 = sha, ExpectedSha1 = sha1, ProviderSizeHint = hint, AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            AttachmentReferences = references, MediaIntegrityPolicy = policy };
    }
    private static string Manifest(DownloadItem item) => Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, ".meowchan-manifest.json");
    private static async Task<MediaCommitResult> Commit(MediaStore store, DownloadItem item, byte[]? bytes = null)
    {
        using MemoryStream stream = new(bytes ?? Bytes);
        return await store.CommitAsync(item, stream, CancellationToken.None);
    }
    private sealed class TestPolicy : ISourceMediaIntegrityPolicy
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private SourceMediaIntegrityPolicySnapshot _snapshot = new("fixture-profile", "fixture-owner", "stub", true, 1);
        internal Func<Task>? BeforeAcquire;
        public SourceMediaIntegrityPolicySnapshot GetSnapshot() => _snapshot;
        internal async Task SetAsync(bool allow)
        {
            await _gate.WaitAsync();
            try { _snapshot = _snapshot with { AllowProviderSizeMd5Mismatch = allow, Revision = _snapshot.Revision + 1 }; }
            finally { _gate.Release(); }
        }
        public async ValueTask<MediaAcceptanceLease> AcquireAcceptanceLeaseAsync(long expectedRevision, CancellationToken cancellationToken)
        {
            if (BeforeAcquire != null) await BeforeAcquire();
            await _gate.WaitAsync(cancellationToken);
            if (!_snapshot.AllowProviderSizeMd5Mismatch || expectedRevision != _snapshot.Revision)
            { _gate.Release(); throw new InvalidDataException("Source permission changed."); }
            return new Lease(_snapshot, _gate);
        }
        private sealed class Lease(SourceMediaIntegrityPolicySnapshot snapshot, SemaphoreSlim gate) : MediaAcceptanceLease(snapshot)
        {
            public override ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }
}
