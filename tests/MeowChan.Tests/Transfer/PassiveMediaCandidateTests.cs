using System.Collections.Immutable;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real synthetic package -> owned stage -> inactive candidate -> fresh-store inspection.
// No hand-written manifests, provider calls, profile activation or simulated measurement results.
public sealed class PassiveMediaCandidateTests
{
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 1, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset HistoricalAt = ImportedAt.AddDays(-3);
    private static readonly ResourceId Resource = new("4chan", "b", "7001");
    private static readonly Guid PackageId = Guid.Parse("5fd1e1f3-cb0f-4a5f-8f2b-154e7c18b7c3");
    private static readonly Guid OperationId = Guid.Parse("7e24f779-474e-48a3-a4b8-02a52c8d23e1");
    private static readonly byte[] SharedBytes = Enumerable.Range(0, 150_123).Select(i => (byte)(i * 31 % 251)).ToArray();
    private static readonly byte[] OtherBytes = [19, 23, 37, 41, 59];

    [Fact]
    public async Task CurrentAndRetainedKeysKeepDistinctAssetsWithOnePhysicalFileAndRealPhaseBoundaries()
    {
        using Workspace workspace = new();
        TransferAttachment current = Attachment("15", "current", SharedBytes);
        TransferAttachment retained = Attachment("999", "retained", SharedBytes, position: 3);
        NativeTransferCatalog catalog = Catalog([Post("15", current)], [new("999", retained)]);
        await using NativeTransferImportStage stage = await CreateStageAsync(workspace, catalog, SharedBytes);
        List<PassiveMediaCandidatePhase> phases = [];
        List<string> hashReads = [];
        MediaStore store = ObservedStore(hashReads);
        Dictionary<string, FileState> before = Files(workspace.CandidateRoot);
        string fingerprint = stage.Package.Envelope.Fingerprint;

        PassiveMediaImportCandidate candidate = await store.MaterializePassiveImportCandidateAsync(stage,
            Resource, Keys(current, retained), workspace.CandidateRoot, OperationId, ImportedAt,
            checkpoint: (phase, directory) =>
            {
                phases.Add(phase);
                Assert.Equal(workspace.CandidateRoot, Directory.GetParent(directory)!.FullName);
                // Checkpoints observe real boundaries, not replacement I/O or fake integrity.
                if (phase == PassiveMediaCandidatePhase.JournalFlushed)
                {
                    Assert.Empty(Directory.GetFiles(directory, "*.original.bin"));
                    Assert.False(File.Exists(Path.Combine(directory, ".meowchan-manifest.json")));
                }
                if (phase == PassiveMediaCandidatePhase.PayloadRenamed)
                {
                    Assert.True(File.Exists(Path.Combine(directory, Hash(SharedBytes) + ".original.bin")));
                    Assert.Empty(Directory.GetFiles(directory, "*.part"));
                }
                if (phase is PassiveMediaCandidatePhase.ManifestSaved or PassiveMediaCandidatePhase.ReadyJournalSaved)
                    Assert.True(File.Exists(Path.Combine(directory, ".meowchan-manifest.json")));
            });

        Assert.Equal(new[] { PassiveMediaCandidatePhase.JournalFlushed, PassiveMediaCandidatePhase.PayloadFlushed,
            PassiveMediaCandidatePhase.PayloadRenamed, PassiveMediaCandidatePhase.ManifestSaved,
            PassiveMediaCandidatePhase.ReadyJournalSaved }, phases);
        AssertCandidateIdentity(candidate, workspace.CandidateRoot, fingerprint);
        Assert.Equal(Keys(current, retained).OrderBy(x => x, StringComparer.Ordinal),
            candidate.Bindings.Select(binding => binding.Reference.RevisionKey));
        Assert.Equal(2, candidate.Bindings.Select(binding => binding.AssetId).Distinct().Count());
        Assert.Equal(new[] { "15", "999" }, candidate.Bindings.Select(binding => binding.Reference.PostId).OrderBy(x => x));
        Assert.All(candidate.Bindings, binding => AssertPassive(binding, fingerprint));
        Assert.All(candidate.Bindings, binding => Assert.Equal(binding.Reference.RevisionKey, binding.PassiveImport!.IncomingRevisionKey));
        string physicalFile = Assert.Single(Directory.GetFiles(candidate.Directory, "*.original.bin"));
        Assert.Equal(Path.Combine(candidate.Directory, Hash(SharedBytes) + ".original.bin"), physicalFile);
        Assert.Equal(physicalFile, Assert.Single(hashReads));
        Assert.Empty(Directory.GetFiles(candidate.Directory, "*.part"));
        AssertFilesEqual(before, Files(workspace.CandidateRoot, candidate.Directory));

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(candidate.Directory);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(5, snapshot.ManifestVersion);
        Assert.Equal(2, snapshot.Count); // identity is attachment-local; physical bytes are shared
        Assert.Equal(candidate.ManifestGeneration, snapshot.Generation);
        Assert.Equal(Hash(await File.ReadAllBytesAsync(Path.Combine(candidate.Directory, ".meowchan-manifest.json"))),
            candidate.ManifestGeneration);
        foreach (MediaAttachmentBinding binding in candidate.Bindings)
        {
            TransferAttachment incoming = binding.Reference.PostId == "15" ? current : retained;
            ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
                MediaAttachmentResolver.Resolve(snapshot, Resource, binding.Reference.PostId, incoming.Metadata));
            Assert.Equal(binding, resolved.Binding);
            Assert.False(resolved.IsLegacy);
            Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "different-post", incoming.Metadata));
            Assert.Equal(binding, snapshot.FindAttachmentBinding(binding.Reference.RevisionKey));
            Assert.Equal(physicalFile, snapshot.FindById(binding.AssetId)!.Path);
            await AssertVerifiedBytesAsync(snapshot, binding, SharedBytes);
        }
        Assert.DoesNotContain(Assert.Single(stage.Package.Catalog.Threads).Posts, post => post.Metadata.PostId == "999");
    }

    [Fact]
    public async Task TwoPostsWithSharedPayloadHashOncePerPhysicalPathAndSurviveStageDisposalAndNewStore()
    {
        using Workspace workspace = new();
        TransferAttachment first = Attachment("15", "same-media", SharedBytes);
        TransferAttachment second = Attachment("16", "same-media", SharedBytes);
        NativeTransferImportStage stage = await CreateStageAsync(workspace,
            Catalog([Post("15", first), Post("16", second)]), SharedBytes);
        PassiveMediaImportCandidate candidate;
        List<string> materializationReads = [];
        await using (stage)
        {
            Assert.Single(stage.Package.Envelope.Payloads);
            candidate = await ObservedStore(materializationReads).MaterializePassiveImportCandidateAsync(stage,
                Resource, Keys(first, second), workspace.CandidateRoot, OperationId, ImportedAt);
        }
        Assert.False(stage.StageRetained);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.StageRoot));
        string physicalFile = Assert.Single(Directory.GetFiles(candidate.Directory, "*.original.bin"));
        Assert.Equal(physicalFile, Assert.Single(materializationReads));
        Assert.Equal(2, candidate.Bindings.Length);
        Assert.Equal(2, candidate.Bindings.Select(binding => binding.Reference.RevisionKey).Distinct().Count());
        Assert.Equal(2, candidate.Bindings.Select(binding => binding.AssetId).Distinct().Count());
        Assert.Single(candidate.Bindings.Select(binding => binding.AcquisitionRevisionKey).Distinct());
        Dictionary<string, FileState> beforeInspection = Files(candidate.Directory);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            List<string> inspectionReads = [];
            PassiveMediaCandidateInspection inspected = await ObservedStore(inspectionReads)
                .InspectPassiveImportCandidateAsync(candidate.Directory);
            Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status);
            AssertSameCandidate(candidate, Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate));
            // An independent fresh store must verify again; it must not hash once per binding.
            Assert.Equal(physicalFile, Assert.Single(inspectionReads));
            AssertFilesEqual(beforeInspection, Files(candidate.Directory));
        }
    }

    [Theory]
    [InlineData("current")]
    [InlineData("retained")]
    [InlineData("both")]
    public async Task SelectionProducesOnlyRequestedOriginalKeysAndPayloads(string selection)
    {
        using Workspace workspace = new();
        TransferAttachment current = Attachment("15", "first", SharedBytes);
        TransferAttachment retained = Attachment("999", "retained", OtherBytes, position: 4);
        TransferAttachment preview = Attachment("15", "preview", OtherBytes, position: 1, role: TransferMediaRole.Preview);
        TransferAttachment unknown = Attachment("16", "unknown", SharedBytes) with
        { Status = TransferContentStatus.Unknown, PayloadSha256 = null, History = null };
        await using NativeTransferImportStage stage = await CreateStageAsync(workspace,
            Catalog([Post("15", current, preview), Post("16", unknown)], [new("999", retained)]), SharedBytes, OtherBytes);
        TransferAttachment[] wanted = selection switch
        { "current" => [current], "retained" => [retained], _ => [current, retained] };
        List<string> hashReads = [];
        PassiveMediaImportCandidate candidate = await ObservedStore(hashReads).MaterializePassiveImportCandidateAsync(stage,
            Resource, Keys(wanted), workspace.CandidateRoot, OperationId, ImportedAt);

        Assert.Equal(Keys(wanted).OrderBy(x => x, StringComparer.Ordinal), candidate.Bindings.Select(binding => binding.Reference.RevisionKey));
        Assert.All(candidate.Bindings, binding => Assert.Equal("original", binding.Provider.Variant));
        Assert.DoesNotContain(candidate.Bindings, binding => binding.Reference.RevisionKey == preview.Metadata.AttachmentRevisionKey
            || binding.Reference.RevisionKey == unknown.Metadata.AttachmentRevisionKey);
        string[] expectedFiles = wanted.Select(attachment => attachment.PayloadSha256 + ".original.bin")
            .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedFiles, Directory.GetFiles(candidate.Directory, "*.original.bin")
            .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(expectedFiles, hashReads.Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal));
        PassiveMediaCandidateInspection inspected = await new MediaStore().InspectPassiveImportCandidateAsync(candidate.Directory);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status);
        AssertSameCandidate(candidate, Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate));
    }

    [Fact]
    public async Task LegacyCurrentNullKeyIsDerivedWithoutFabricatingHistoryAfterCallerFileDisappears()
    {
        using Workspace workspace = new();
        TransferAttachment legacy = Attachment("15", "legacy", SharedBytes, legacyKey: true);
        TransferAttachment retained = Attachment("999", "retained", OtherBytes, position: 3);
        NativeTransferCatalog catalog = Catalog([Post("15", legacy)], [new("999", retained)]);
        string sourcePath = Path.Combine(workspace.Root, "synthetic-input.meowchan");
        using (MemoryStream package = await WritePackageAsync(catalog, SharedBytes, OtherBytes))
            await File.WriteAllBytesAsync(sourcePath, package.ToArray());
        NativeTransferImportStage stage;
        using (FileStream caller = File.OpenRead(sourcePath))
        {
            NativeTransferStageResult result = await NativeTransferImportStage.CreateAsync(caller, workspace.StageRoot);
            Assert.Equal(NativeTransferStageError.None, result.Error);
            stage = Assert.IsType<NativeTransferImportStage>(result.Stage);
        }
        File.Delete(sourcePath);
        Assert.False(File.Exists(sourcePath));
        PassiveMediaImportCandidate candidate;
        await using (stage)
        {
            TransferThread thread = Assert.Single(stage.Package.Catalog.Threads);
            Assert.Null(Assert.Single(Assert.Single(thread.Posts).Attachments).Metadata.AttachmentRevisionKey);
            // Retained bindings require nonnull keys in the existing v2/v3 wire contract.
            Assert.Equal(retained.Metadata.AttachmentRevisionKey, Assert.Single(thread.RetainedBindings).Attachment.Metadata.AttachmentRevisionKey);
            string derivedKey = Key("15", legacy);
            candidate = await new MediaStore().MaterializePassiveImportCandidateAsync(stage, Resource,
                new HashSet<string>(StringComparer.Ordinal) { derivedKey, retained.Metadata.AttachmentRevisionKey! },
                workspace.CandidateRoot, OperationId, ImportedAt);
            MediaAttachmentBinding derived = Assert.Single(candidate.Bindings, binding => binding.Reference.RevisionKey == derivedKey);
            Assert.Null(derived.PassiveImport!.IncomingRevisionKey);
            Assert.Null(derived.PassiveImport.History);
            Assert.Equal("15", derived.Reference.PostId);
            MediaAttachmentBinding old = Assert.Single(candidate.Bindings, binding => binding.Reference.PostId == "999");
            Assert.Equal(retained.Metadata.AttachmentRevisionKey, old.PassiveImport!.IncomingRevisionKey);
            Assert.All(candidate.Bindings, binding => AssertPassive(binding, stage.Package.Envelope.Fingerprint));
        }
        Assert.False(stage.StageRetained);
        PassiveMediaCandidateInspection inspected = await new MediaStore().InspectPassiveImportCandidateAsync(candidate.Directory);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status);
        AssertSameCandidate(candidate, Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate));
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("strict", false)]
    [InlineData("provider-mismatch", true)]
    public async Task PassiveProvenanceKeepsOnlyActualIncomingHistoryWithoutLocalSourceAuthority(string historyKind, bool mismatch)
    {
        using Workspace workspace = new();
        TransferAttachment original = Attachment("15", "history", SharedBytes, mismatch: mismatch);
        TransferHistoricalIntegrity? history = historyKind == "none" ? null
            : new(historyKind, HistoricalAt, mismatch, mismatch);
        original = original with { History = history };
        await using NativeTransferImportStage stage = await CreateStageAsync(workspace, Catalog([Post("15", original)]), SharedBytes);
        PassiveMediaImportCandidate candidate = await new MediaStore().MaterializePassiveImportCandidateAsync(stage,
            Resource, Keys(original), workspace.CandidateRoot, OperationId, ImportedAt);
        MediaAttachmentBinding binding = Assert.Single(candidate.Bindings);
        AssertPassive(binding, stage.Package.Envelope.Fingerprint, history);
        Assert.Equal(mismatch, binding.Acceptance.SizeMismatch);
        Assert.Equal(mismatch, binding.Acceptance.Md5Mismatch);
        Assert.Equal(original.Metadata.AttachmentRevisionKey, binding.PassiveImport!.IncomingRevisionKey);
        Assert.Equal(SharedBytes.LongLength, binding.Actual.Length);
        Assert.Equal(Hash(SharedBytes), binding.Actual.Sha256);
        Assert.Equal(Md5(SharedBytes), binding.Actual.Md5);
        Assert.Equal(Sha1(SharedBytes), binding.Actual.Sha1);
        if (history != null) Assert.NotEqual(ImportedAt, binding.PassiveImport.History!.AcceptedAtUtc);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(candidate.Directory);
        await AssertVerifiedBytesAsync(snapshot, binding, SharedBytes);
        PassiveMediaCandidateInspection inspected = await new MediaStore().InspectPassiveImportCandidateAsync(candidate.Directory);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status);
        AssertSameCandidate(candidate, Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate));
    }

    [Fact]
    public async Task CandidateAndReadOnlyInspectionDoNotChangeExistingTargetSqlMediaOrRegisteredRoots()
    {
        await using NativeTransferArchiveFixture target = await NativeTransferArchiveFixture.CreateAsync(ThreadSourceState.NotFound);
        using Workspace workspace = new();
        string candidateRoot = Directory.CreateDirectory(Path.Combine(target.ProfilePath, "inactive-candidates")).FullName;
        TransferAttachment incoming = Attachment("15", "incoming", SharedBytes);
        await using NativeTransferImportStage stage = await CreateStageAsync(workspace, Catalog([Post("15", incoming)]), SharedBytes);
        Dictionary<string, FileState> before = Files(target.ProfilePath, candidateRoot);
        PassiveMediaImportCandidate candidate = await new MediaStore().MaterializePassiveImportCandidateAsync(stage,
            target.ThreadId, Keys(incoming), candidateRoot, OperationId, ImportedAt);
        AssertFilesEqual(before, Files(target.ProfilePath, candidateRoot));
        Dictionary<string, FileState> candidateBefore = Files(candidate.Directory);
        PassiveMediaCandidateInspection inspected = await new MediaStore().InspectPassiveImportCandidateAsync(candidate.Directory);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status);
        AssertSameCandidate(candidate, Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate));
        AssertFilesEqual(candidateBefore, Files(candidate.Directory));
        AssertFilesEqual(before, Files(target.ProfilePath, candidateRoot));
        Assert.DoesNotContain(Directory.GetFiles(candidate.Directory), path => Path.GetExtension(path) is ".db" or ".sqlite");

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(target.ProfilePath);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(target.ThreadId));
        Assert.Equal(target.Summary, view.Summary);
        Assert.Equal(target.ExpectedPostIds, view.Posts.Select(post => post.PostId));
        Assert.DoesNotContain(candidate.Directory, view.MediaDirectories);
        MediaAssetSnapshot active = await MediaAssetReader.OpenAsync(target.MediaDirectory);
        Assert.Equal(target.ManifestGeneration, active.Generation);
        Assert.Equal(target.StrictBinding, active.FindAttachmentBinding(target.StrictRevision));
        Assert.Null(active.FindAttachmentBinding(incoming.Metadata.AttachmentRevisionKey!));
        Assert.Equal(0, target.UnexpectedProviderCalls);
    }

    [Fact]
    public async Task CancellationRaisedAfterReadyMarkerDoesNotTurnCommittedCandidateIntoCancellation()
    {
        using Workspace workspace = new();
        using CancellationTokenSource cancellation = new();
        TransferAttachment original = Attachment("15", "late-cancel", SharedBytes);
        await using NativeTransferImportStage stage = await CreateStageAsync(workspace, Catalog([Post("15", original)]), SharedBytes);
        PassiveMediaImportCandidate candidate = await new MediaStore().MaterializePassiveImportCandidateAsync(stage,
            Resource, Keys(original), workspace.CandidateRoot, OperationId, ImportedAt, cancellation.Token,
            (phase, _) =>
            {
                if (phase != PassiveMediaCandidatePhase.ReadyJournalSaved) return;
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            });
        Assert.True(cancellation.IsCancellationRequested);
        PassiveMediaCandidateInspection inspected = await new MediaStore().InspectPassiveImportCandidateAsync(candidate.Directory);
        Assert.Equal(PassiveMediaCandidateStatus.Ready, inspected.Status);
        AssertSameCandidate(candidate, Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate));
    }

    private static MediaStore ObservedStore(ICollection<string> paths) => new(File.Move, onFileHashStarting: paths.Add);

    private static void AssertCandidateIdentity(PassiveMediaImportCandidate candidate, string parent, string fingerprint)
    {
        Assert.True(Path.IsPathFullyQualified(candidate.Directory));
        Assert.Equal(parent, Directory.GetParent(candidate.Directory)!.FullName);
        string name = Path.GetFileName(candidate.Directory);
        Assert.StartsWith(".meowchan-passive-", name, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(name[".meowchan-passive-".Length..], "N", out Guid suffix));
        Assert.NotEqual(Guid.Empty, suffix);
        Assert.Equal(OperationId, candidate.OperationId);
        Assert.Equal(fingerprint, candidate.PackageFingerprint);
        Assert.Equal(Resource, candidate.Resource);
        Assert.Equal(ImportedAt, candidate.ImportedAtUtc);
    }

    private static void AssertSameCandidate(PassiveMediaImportCandidate expected, PassiveMediaImportCandidate actual)
    {
        Assert.Equal(expected.Directory, actual.Directory);
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.PackageFingerprint, actual.PackageFingerprint);
        Assert.Equal(expected.Resource, actual.Resource);
        Assert.Equal(expected.ImportedAtUtc, actual.ImportedAtUtc);
        Assert.Equal(expected.ManifestGeneration, actual.ManifestGeneration);
        Assert.Equal(expected.Bindings.ToArray(), actual.Bindings.ToArray());
    }

    private static void AssertPassive(MediaAttachmentBinding binding, string fingerprint, TransferHistoricalIntegrity? history = null)
    {
        Assert.Equal(MediaAcceptanceKind.PassiveImport, binding.Acceptance.Kind);
        Assert.Null(binding.Acceptance.ProfileId);
        Assert.Null(binding.Acceptance.PluginOwnerId);
        Assert.Null(binding.Acceptance.SourceId);
        Assert.Null(binding.Acceptance.PolicyRevision);
        Assert.Equal(ImportedAt, binding.Acceptance.AcceptedAtUtc);
        MediaPassiveImportRecord provenance = Assert.IsType<MediaPassiveImportRecord>(binding.PassiveImport);
        Assert.Equal(OperationId.ToString("D"), provenance.OperationId);
        Assert.Equal(fingerprint, provenance.PackageFingerprint);
        Assert.Equal(ImportedAt, provenance.ImportedAtUtc);
        if (history == null) Assert.Null(provenance.History);
        else Assert.Equal(new MediaImportedHistory(history.Kind == "strict" ? MediaAcceptanceKind.Strict
            : MediaAcceptanceKind.ExplicitProviderMismatch, history.AcceptedAtUtc, history.SizeMismatch, history.Md5Mismatch), provenance.History);
        Assert.Equal(MediaRevisionKeys.CreateAttachmentKey(binding.Reference.Resource, binding.Reference.PostId,
            binding.Reference.Position, binding.Provider), binding.Reference.RevisionKey);
        Assert.Equal(MediaRevisionKeys.CreateAcquisitionKey(binding.Reference.Resource, binding.Provider), binding.AcquisitionRevisionKey);
        MediaIntegrityValidation.ValidateBinding(binding.Reference.RevisionKey, binding);
    }

    private static async Task AssertVerifiedBytesAsync(MediaAssetSnapshot snapshot, MediaAttachmentBinding binding, byte[] expected)
    {
        await using Stream stream = await snapshot.OpenVerifiedReadAsync(binding.AssetId);
        Assert.Equal(0L, stream.Position);
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes);
        Assert.Equal(expected, bytes.ToArray());
    }

    private static NativeTransferCatalog Catalog(ImmutableArray<TransferPost> posts,
        ImmutableArray<TransferRetainedBinding> retained = default)
        => new(ImportedAt, [new(Resource, "https://boards.4chan.org/b/thread/7001", "Synthetic candidate only",
            ThreadSourceState.NotFound, HistoricalAt, null, null, false, posts)
            { RetainedBindings = retained.IsDefault ? [] : retained }]);

    private static TransferPost Post(string id, params TransferAttachment[] attachments)
        => new(new(id, long.Parse(id, System.Globalization.CultureInfo.InvariantCulture), PostFieldPresence.All,
            Subject: "", Html: "synthetic", Attachments: attachments.Select(attachment => attachment.Metadata).ToImmutableArray()),
            attachments.ToImmutableArray());

    private static TransferAttachment Attachment(string postId, string mediaId, byte[] bytes, int position = 0,
        bool legacyKey = false, bool mismatch = false, TransferMediaRole role = TransferMediaRole.Original)
    {
        MediaProviderMetadata provider = new(mediaId, role == TransferMediaRole.Preview ? "preview" : "original",
            "https://i.4cdn.org/b/111.jpg", $"package-name-{mediaId}.bin", mismatch ? bytes.Length + 10 : bytes.Length,
            mismatch ? Md5(OtherBytes) : Md5(bytes), Hash(bytes)) { ExpectedSha1 = Sha1(bytes) };
        CapturedAttachment metadata = new(position, mediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha1 = provider.ExpectedSha1, ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = legacyKey ? null : MediaRevisionKeys.CreateAttachmentKey(Resource, postId, position, provider)
        };
        return new(metadata, role, TransferContentStatus.Contained, Hash(bytes), null);
    }

    private static MediaProviderMetadata Provider(CapturedAttachment metadata)
        => new(metadata.MediaId!, metadata.Variant, metadata.Url!, metadata.OriginalFileName,
            metadata.ExpectedSize, metadata.ExpectedMd5, metadata.ExpectedSha256)
        { ExpectedSha1 = metadata.ExpectedSha1, ProviderSizeHint = metadata.ProviderSizeHint };

    private static string Key(string postId, TransferAttachment attachment)
        => MediaRevisionKeys.CreateAttachmentKey(Resource, postId, attachment.Metadata.Position, Provider(attachment.Metadata));
    private static HashSet<string> Keys(params TransferAttachment[] attachments)
        => new(attachments.Select(attachment => attachment.Metadata.AttachmentRevisionKey!), StringComparer.Ordinal);

    private static async Task<NativeTransferImportStage> CreateStageAsync(Workspace workspace,
        NativeTransferCatalog catalog, params byte[][] payloads)
    {
        using MemoryStream source = await WritePackageAsync(catalog, payloads);
        NativeTransferStageResult result = await NativeTransferImportStage.CreateAsync(source, workspace.StageRoot);
        Assert.Equal(NativeTransferStageError.None, result.Error);
        Assert.False(result.StageRetained);
        return Assert.IsType<NativeTransferImportStage>(result.Stage);
    }

    private static async Task<MemoryStream> WritePackageAsync(NativeTransferCatalog catalog, params byte[][] payloads)
    {
        MemoryStream output = new();
        try
        {
            TransferPayloadSource[] sources = payloads.Select(bytes => new TransferPayloadSource(new(bytes.LongLength, Hash(bytes)),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)))).ToArray();
            TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(output, catalog, sources, PackageId);
            Assert.True(result.IsComplete, result.Error.ToString());
            output.Position = 0;
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    // Last-access timestamps are intentionally excluded: inspection is allowed to READ bytes.
    private sealed record FileState(string Sha256, DateTime LastWriteUtc, long Length);
    private static Dictionary<string, FileState> Files(string root, string? excluded = null)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => excluded == null || !path.StartsWith(
                Path.TrimEndingDirectorySeparator(excluded) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(root, path), path =>
                new FileState(Hash(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path), new FileInfo(path).Length), StringComparer.Ordinal);

    private static void AssertFilesEqual(Dictionary<string, FileState> expected, Dictionary<string, FileState> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(x => x, StringComparer.Ordinal), actual.Keys.OrderBy(x => x, StringComparer.Ordinal));
        foreach ((string path, FileState state) in expected) Assert.Equal(state, actual[path]);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    private static string Sha1(byte[] bytes) => Convert.ToBase64String(SHA1.HashData(bytes));

    private sealed class Workspace : IDisposable
    {
        private readonly TempDirectory _directory = new();
        internal string Root => _directory.Path;
        internal string StageRoot => Path.Combine(Root, "stage");
        internal string CandidateRoot => Path.Combine(Root, "candidate-root");
        internal Workspace()
        {
            Directory.CreateDirectory(StageRoot);
            Directory.CreateDirectory(CandidateRoot);
            File.WriteAllBytes(Path.Combine(CandidateRoot, "pre-existing.txt"), [101, 102, 103]);
        }
        public void Dispose() => _directory.Dispose();
    }
}
