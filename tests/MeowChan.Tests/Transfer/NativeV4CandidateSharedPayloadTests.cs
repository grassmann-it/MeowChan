using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Bounded package -> stage -> inactive Journal2 witnesses. The existing hash observer
// must cover the single physical payload read, including any concurrent PNG check.
// No replacement hashing, timing benchmark, SQL, host, merge or active registration.
public sealed class NativeV4CandidateSharedPayloadTests
{
    private const string ManifestName = ".meowchan-manifest.json";
    private const string JournalName = ".meowchan-import-candidate.json";
    private const int Parents = 4;
    private static readonly ResourceId Resource = new("4chan", "b", "7001");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid Operation = Guid.Parse("d17b32b1-aca1-4a41-b111-000000000007");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("historical source bytes, not staged");
    private static readonly byte[] Png = Poster();
    private static readonly byte[] NotPng = Encoding.UTF8.GetBytes("ordinary preview bytes, intentionally not PNG");

    public static IEnumerable<object[]> SharedCases()
    {
        foreach (string mode in new[] { "previews", "posters", "mixed" })
            foreach (string phase in new[] { "ready", "before-manifest", "after-manifest" })
                yield return new object[] { mode, phase };
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    public async Task SharedMembersKeepExactRecordsAndHashOncePerPhysicalFile(string mode, string phase)
    {
        await using var f = await Fixture.CreateAsync(mode, Png);
        NativeMediaCandidateInventory inventory = f.Inventory;
        Assert.Single(f.Stage.Package.Envelope.Payloads);
        Assert.Single(inventory.Payloads);
        Assert.Equal(Parents, inventory.OriginalEvidence.Length);
        Assert.Equal(mode == "mixed" ? 2 * Parents : Parents, inventory.DerivedRepresentations.Length);
        Assert.Equal(Parents, inventory.DerivedRepresentations.Select(d => d.Parent).Distinct().Count());
        if (mode == "mixed")
            Assert.All(inventory.DerivedRepresentations.GroupBy(d => d.Parent), group =>
            {
                Assert.Contains(group, d => d.Kind == MediaDerivedKind.Preview);
                Assert.Contains(group, d => d.Kind == MediaDerivedKind.VideoPosterPng);
            });

        List<string> producerReads = [];
        var prepared = await PrepareAsync(f, phase, producerReads);
        string path = Path.Combine(prepared.Directory, Assert.Single(inventory.Payloads).FileName);
        Assert.Equal(phase == "ready" ? new[] { path } : Array.Empty<string>(), producerReads.ToArray());
        Assert.Equal(phase != "before-manifest", File.Exists(Path.Combine(prepared.Directory, ManifestName)));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(prepared.Directory, "*.derived.bin")));
        Assert.Empty(Directory.GetFiles(prepared.Directory, "*.original.bin"));
        await f.Stage.DisposeAsync();
        Assert.False(f.Stage.StageRetained);

        KeyValuePair<string, string>[] before = Files(prepared.Directory);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            List<string> reads = [];
            PassiveMediaCandidateInspection inspected = await Observe(reads)
                .InspectPassiveImportCandidateAsync(prepared.Directory).WaitAsync(TestWait.Timeout);
            Assert.Equal(phase == "ready" ? PassiveMediaCandidateStatus.Ready : PassiveMediaCandidateStatus.Incomplete,
                inspected.Status);
            Assert.Equal(path, Assert.Single(reads));
            if (phase == "ready")
            {
                var restored = Assert.IsType<PassiveMediaImportCandidate>(inspected.Candidate);
                AssertCandidate(f, restored);
                Assert.Equal(prepared.Candidate!.ManifestGeneration, restored.ManifestGeneration);
            }
            else Assert.Null(inspected.Candidate);
            Assert.Equal(before, Files(prepared.Directory));
        }
        if (phase != "before-manifest")
        {
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(prepared.Directory);
            Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
            Assert.Equal(6, snapshot.ManifestVersion);
            Assert.Equal(0, snapshot.Count);
            Assert.Empty(snapshot.AttachmentBindings);
            Assert.Equal(inventory.OriginalEvidence.ToArray(), snapshot.OriginalEvidence.ToArray());
            Assert.Equal(inventory.DerivedRepresentations.ToArray(), snapshot.DerivedRepresentations.ToArray());
        }
        AssertInactive(f, prepared.Directory);
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    public async Task SameLengthChangedSharedBytesAreRejectedWithoutRepair(string mode, string phase)
    {
        await using var f = await Fixture.CreateAsync(mode, Png);
        var prepared = await PrepareAsync(f, phase, []);
        string path = Path.Combine(prepared.Directory, Assert.Single(f.Inventory.Payloads).FileName);
        byte[] changed = await File.ReadAllBytesAsync(path);
        changed[^1] ^= 1;
        await File.WriteAllBytesAsync(path, changed);
        await f.Stage.DisposeAsync();
        var before = Files(prepared.Directory);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            List<string> reads = [];
            PassiveMediaCandidateInspection inspected = await Observe(reads)
                .InspectPassiveImportCandidateAsync(prepared.Directory).WaitAsync(TestWait.Timeout);
            Assert.Equal(PassiveMediaCandidateStatus.Invalid, inspected.Status);
            Assert.Null(inspected.Candidate);
            Assert.Equal(path, Assert.Single(reads));
            Assert.Equal(before, Files(prepared.Directory));
        }
        AssertInactive(f, prepared.Directory);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("before-manifest")]
    [InlineData("after-manifest")]
    public async Task MixedPreviewAndPosterStillRequiresPngEvenWithSelfConsistentDigests(string phase)
    {
        // The producer/stage correctly allows arbitrary ordinary preview bytes. Reinterpret
        // exactly one member as a poster and recompute every relevant persistent digest.
        // This is an adversarial inactive-journal fixture, NOT an admitted plan subset.
        // A verifier grouping by blob must OR together all role-level requirements;
        // whichever preview happens to be first must not waive PNG for another parent.
        await using var f = await Fixture.CreateAsync("previews", NotPng);
        var prepared = await PrepareAsync(f, phase, []);
        NativeMediaMemberId preview = f.Plan.FullSelection.Members.Last(m => m.Id.Role == NativeMediaMemberRole.Preview).Id;
        NativeMediaCandidateSelection mixed = NativeMediaCandidateSelection.Create(f.Plan.FullSelection.Members.Select(m =>
            m.Id != preview ? m : m with
            {
                Descriptor = m.Descriptor with { Id = m.Id with { Role = NativeMediaMemberRole.VideoPosterPng } }
            }));
        NativeMediaCandidateInventory inventory = mixed.CreateInventory(Operation, f.Fingerprint, At);
        Assert.Single(inventory.Payloads);
        Assert.Equal(Parents - 1, inventory.DerivedRepresentations.Count(d => d.Kind == MediaDerivedKind.Preview));
        Assert.Single(inventory.DerivedRepresentations, d => d.Kind == MediaDerivedKind.VideoPosterPng);
        await RewriteInventoryAsync(prepared.Directory, phase, mixed, inventory);
        await f.Stage.DisposeAsync();

        if (phase != "before-manifest")
        {
            var snapshot = await MediaAssetReader.OpenAsync(prepared.Directory);
            // The manifest/inventory is structurally valid. Rejection must not just be
            // stale generation or mismatched record metadata masking a skipped PNG check.
            Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
            Assert.Equal(inventory.DerivedRepresentations.ToArray(), snapshot.DerivedRepresentations.ToArray());
        }
        var before = Files(prepared.Directory);
        List<string> reads = [];
        var inspected = await Observe(reads).InspectPassiveImportCandidateAsync(prepared.Directory).WaitAsync(TestWait.Timeout);
        Assert.Equal(PassiveMediaCandidateStatus.Invalid, inspected.Status);
        Assert.Null(inspected.Candidate);
        Assert.Equal(Path.Combine(prepared.Directory, Assert.Single(inventory.Payloads).FileName), Assert.Single(reads));
        Assert.Equal(before, Files(prepared.Directory));
        AssertInactive(f, prepared.Directory);
    }

    [Theory]
    [InlineData("ready", "source-version")]
    [InlineData("ready", "origin")]
    [InlineData("ready", "parent")]
    [InlineData("ready", "missing-member")]
    [InlineData("after-manifest", "source-version")]
    [InlineData("after-manifest", "origin")]
    [InlineData("after-manifest", "parent")]
    [InlineData("after-manifest", "missing-member")]
    public async Task SharedHashCannotHideAlteredSemanticMemberEvenWithRehashedManifest(string phase, string fault)
    {
        await using var f = await Fixture.CreateAsync("mixed", Png);
        var prepared = await PrepareAsync(f, phase, []);
        string path = Path.Combine(prepared.Directory, ManifestName);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonArray records = manifest["DerivedRepresentations"]!.AsArray();
        switch (fault)
        {
            case "source-version": records[1]!["SourceVersion"]!["Sha256"] = Hash(NotPng); break;
            case "origin": records[1]!["Origin"]!["OperationId"] = Guid.Empty.ToString("D"); break;
            case "parent": records[1]!["Parent"]!["PostId"] = "999"; break;
            case "missing-member": records.RemoveAt(1); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        if (phase == "ready")
        {
            string journalPath = Path.Combine(prepared.Directory, JournalName);
            JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
            journal["ManifestGeneration"] = Hash(await File.ReadAllBytesAsync(path));
            await File.WriteAllTextAsync(journalPath, journal.ToJsonString());
        }
        var before = Files(prepared.Directory);
        var inspected = await new MediaStore().InspectPassiveImportCandidateAsync(prepared.Directory).WaitAsync(TestWait.Timeout);
        Assert.Equal(PassiveMediaCandidateStatus.Invalid, inspected.Status);
        Assert.Null(inspected.Candidate);
        Assert.Equal(before, Files(prepared.Directory));
        AssertInactive(f, prepared.Directory);
    }

    private static async Task RewriteInventoryAsync(string directory, string phase,
        NativeMediaCandidateSelection selection, NativeMediaCandidateInventory inventory)
    {
        string manifestPath = Path.Combine(directory, ManifestName);
        if (File.Exists(manifestPath))
        {
            JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["OriginalEvidence"] = JsonSerializer.SerializeToNode(inventory.OriginalEvidence);
            manifest["DerivedRepresentations"] = JsonSerializer.SerializeToNode(inventory.DerivedRepresentations);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        }
        string journalPath = Path.Combine(directory, JournalName);
        JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        journal["SelectionJson"] = selection.CanonicalJson;
        journal["SelectionSha256"] = selection.Sha256;
        journal["InventorySha256"] = phase == "ready" ? inventory.Sha256 : null;
        journal["ManifestGeneration"] = phase == "ready" ? Hash(await File.ReadAllBytesAsync(manifestPath)) : null;
        await File.WriteAllTextAsync(journalPath, journal.ToJsonString());
    }

    private static async Task<(string Directory, PassiveMediaImportCandidate? Candidate)> PrepareAsync(
        Fixture f, string phase, List<string> reads)
    {
        if (phase == "ready")
        {
            var candidate = await Observe(reads).MaterializePassiveImportCandidateAsync(f.Plan, f.Plan.FullSelection,
                f.Parent, Operation, At).WaitAsync(TestWait.Timeout);
            AssertCandidate(f, candidate);
            return (candidate.Directory, candidate);
        }
        PassiveMediaCandidatePhase stop = phase switch
        {
            "before-manifest" => PassiveMediaCandidatePhase.PayloadRenamed,
            "after-manifest" => PassiveMediaCandidatePhase.ManifestSaved,
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
        using var cancellation = new CancellationTokenSource();
        string? directory = null;
        var error = await Assert.ThrowsAsync<PassiveMediaCandidateCanceledException>(async () =>
            await Observe(reads).MaterializePassiveImportCandidateAsync(f.Plan, f.Plan.FullSelection, f.Parent,
                Operation, At, cancellation.Token, (checkpoint, path) =>
                {
                    if (checkpoint != stop) return;
                    directory = path;
                    cancellation.Cancel();
                }).WaitAsync(TestWait.Timeout));
        Assert.Equal(directory, error.CandidateDirectory);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(error.CandidateDirectory, JournalName)))!.AsObject();
        Assert.Equal("copying", journal["Phase"]!.GetValue<string>());
        Assert.Null(journal["InventorySha256"]);
        Assert.Null(journal["ManifestGeneration"]);
        return (error.CandidateDirectory, null);
    }

    private static MediaStore Observe(List<string> reads)
        => new(File.Move, onFileHashStarting: reads.Add);

    private static void AssertCandidate(Fixture f, PassiveMediaImportCandidate candidate)
    {
        Assert.Equal(2, candidate.PlanVersion);
        Assert.Equal(Operation, candidate.OperationId);
        Assert.Equal(f.Fingerprint, candidate.PackageFingerprint);
        Assert.Equal(Resource, candidate.Resource);
        Assert.Equal(At, candidate.ImportedAtUtc);
        Assert.Equal(f.Plan.FullSelection.CanonicalJson, candidate.Selection!.CanonicalJson);
        Assert.Equal(f.Plan.FullSelection.Sha256, candidate.Selection.Sha256);
        Assert.Equal(f.Inventory.Sha256, candidate.InventorySha256);
        Assert.Empty(candidate.Bindings);
        Assert.Equal(f.Inventory.OriginalEvidence.ToArray(), candidate.OriginalEvidence.ToArray());
        Assert.Equal(f.Inventory.DerivedRepresentations.ToArray(), candidate.DerivedRepresentations.ToArray());
        Assert.All(candidate.OriginalEvidence, e => Assert.Equal(
            new MediaPassiveObservation(Operation.ToString("D"), f.Fingerprint, At), e.Origin));
    }

    private static void AssertInactive(Fixture f, string directory)
    {
        Assert.Equal(f.Parent, Path.GetDirectoryName(directory));
        Assert.Equal(directory, Assert.Single(Directory.GetFileSystemEntries(f.Parent)));
        Assert.Empty(Directory.GetFiles(directory, "*.original.bin"));
        Assert.Empty(Directory.GetFiles(directory, "*.part"));
        Assert.DoesNotContain(Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories), p =>
            Path.GetFileName(p).StartsWith("archive.db", StringComparison.Ordinal));
    }

    private static KeyValuePair<string, string>[] Files(string directory)
        => Directory.GetFiles(directory).Select(path => new KeyValuePair<string, string>(
            Path.GetFileName(path), Hash(File.ReadAllBytes(path)))).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();

    private static TransferAttachment Attachment(string post, string mode, byte[] payload)
    {
        MediaProviderMetadata provider = new("media-" + post, "original", $"https://i.4cdn.org/b/{post}.webm",
            "source.webm", Original.LongLength, Md5(Original), Hash(Original));
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Resource, post, 0, provider) };
        TransferOriginalByteVersion source = new(Original.LongLength, Hash(Original), Md5(Original), null);
        TransferDerivedRepresentationKind[] kinds = mode switch
        {
            "previews" => [TransferDerivedRepresentationKind.Preview],
            "posters" => [TransferDerivedRepresentationKind.VideoPosterPng],
            "mixed" => [TransferDerivedRepresentationKind.Preview, TransferDerivedRepresentationKind.VideoPosterPng],
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return new(metadata, TransferMediaRole.Original, TransferContentStatus.Omitted, null, null)
        {
            OriginalByteVersion = source,
            DerivedRepresentations = kinds.Select(kind => new TransferDerivedRepresentation(kind,
                metadata.AttachmentRevisionKey!, source, TransferContentStatus.Contained, Hash(payload))).ToImmutableArray()
        };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-v4-shared-tests-");
        internal string Parent { get; }
        internal NativeTransferImportStage Stage { get; private set; } = null!;
        internal NativeMediaMaterializationPlan Plan { get; private set; } = null!;
        internal string Fingerprint => Stage.Package.Envelope.Fingerprint;
        internal NativeMediaCandidateInventory Inventory => Plan.FullSelection.CreateInventory(Operation, Fingerprint, At);
        private Fixture() => Parent = Directory.CreateDirectory(Path.Combine(Root, "candidates")).FullName;

        internal static async Task<Fixture> CreateAsync(string mode, byte[] payload)
        {
            var f = new Fixture();
            try
            {
                TransferPost[] posts = Enumerable.Range(101, Parents).Select(id =>
                {
                    string post = id.ToString(CultureInfo.InvariantCulture);
                    TransferAttachment attachment = Attachment(post, mode, payload);
                    return new TransferPost(new(post, 0, PostFieldPresence.All, At, "", "", "synthetic", false,
                        [attachment.Metadata]), [attachment]);
                }).ToArray();
                TransferThread thread = new(Resource, null, "shared derived fixture", ThreadSourceState.NotFound,
                    At, At, 1, true, posts.ToImmutableArray()) { RetainedBindings = [] };
                NativeTransferCatalog catalog = new(At, [thread]);
                TransferPayloadSource[] sources = [new(new(payload.LongLength, Hash(payload)),
                    _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)))];
                using var package = new MemoryStream();
                TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, catalog, sources,
                    Guid.Parse("7e274989-53cb-4609-9a13-c5e16970b053"));
                Assert.True(written.IsComplete, written.ToString());
                package.Position = 0;
                string staging = Directory.CreateDirectory(Path.Combine(f.Root, "staging")).FullName;
                NativeTransferStageResult admitted = await NativeTransferImportStage.CreateAsync(package, staging);
                Assert.Equal(NativeTransferStageError.None, admitted.Error);
                f.Stage = Assert.IsType<NativeTransferImportStage>(admitted.Stage);
                Assert.True(f.Stage.Package.IsChanV4);
                Assert.Null(f.Stage.Package.Envelope.PrivateMetadata);
                f.Plan = NativeMediaMaterializationPlan.Create(f.Stage, Resource);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Stage != null) await Stage.DisposeAsync(); }
            finally { Directory.Delete(Root, recursive: true); }
        }
    }

    // Tiny CRC-correct structural PNG, matching existing v4 poster fixtures. No decoder claim.
    private static byte[] Poster()
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        byte[] Chunk(string type, byte[] data)
        {
            byte[] chunk = new byte[data.Length + 12];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
            data.CopyTo(chunk, 8);
            uint crc = uint.MaxValue;
            foreach (byte value in chunk.AsSpan(4, data.Length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
            return chunk;
        }
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header),
            .. Chunk("IDAT", [0x78, 0x9c, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]), .. Chunk("IEND", [])];
    }
}
