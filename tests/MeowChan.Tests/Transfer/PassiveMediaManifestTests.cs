using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// Real, disposable manifests and bytes. No publication authority is inferred from these DTOs.
public sealed class PassiveMediaManifestTests
{
    private static readonly byte[] Bytes = [1, 3, 5, 7];
    private static readonly ResourceId Resource = new("stub", "g", "123");
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset HistoricalAt = ImportedAt.AddDays(-2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V5ReadsAndResolvesMeasuredBytesWithoutInventingHistoricalAcceptance(bool providerMismatch)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        MediaProviderMetadata provider = providerMismatch ? MismatchingProvider(includeSha: true) : MatchingProvider();
        Fixture fixture = await CreateAsync(directory, board, provider);
        byte[] before = await File.ReadAllBytesAsync(fixture.ManifestPath);

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(5, snapshot.ManifestVersion);
        MediaAttachmentBinding saved = Assert.Single(snapshot.AttachmentBindings).Value;
        Assert.Equal(fixture.Binding, saved);
        Assert.Equal(MediaAcceptanceKind.PassiveImport, saved.Acceptance.Kind);
        Assert.Equal(ImportedAt, saved.Acceptance.AcceptedAtUtc);
        Assert.Null(saved.PassiveImport!.History);
        Assert.Null(saved.Acceptance.ProfileId); Assert.Null(saved.Acceptance.PluginOwnerId);
        Assert.Null(saved.Acceptance.SourceId); Assert.Null(saved.Acceptance.PolicyRevision);
        Assert.Equal(providerMismatch, saved.Acceptance.SizeMismatch);
        Assert.Equal(providerMismatch, saved.Acceptance.Md5Mismatch);

        CapturedAttachment attachment = Attachment(saved);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshot, Resource, "123", attachment));
        Assert.False(resolved.IsLegacy);
        Assert.Equal(saved, resolved.Binding);
        Assert.Equal(fixture.Committed.AssetId, resolved.Asset.AssetId);
        await using (Stream verified = await snapshot.OpenVerifiedReadAsync(resolved.Asset.AssetId))
        {
            Assert.Equal(0, verified.Position);
            using MemoryStream copy = new(); await verified.CopyToAsync(copy);
            Assert.Equal(Bytes, copy.ToArray());
        }
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "124", attachment));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "123", attachment with { Url = provider.Url + "?different" }));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "123", attachment with { AttachmentRevisionKey = "att-v2:" + new string('f', 64) }));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ManifestPath));
    }

    [Fact]
    public async Task DerivedLegacyKeyAndMultiplePostKeysKeepOneAssetAndNoHistory()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MatchingProvider(), incomingKey: false);
        MediaAttachmentBinding first = fixture.Binding;
        string otherKey = MediaRevisionKeys.CreateAttachmentKey(Resource, "124", 0, first.Provider);
        MediaAttachmentBinding other = first with { Reference = new(Resource, "124", 0, otherKey) };
        JsonObject manifest = await ReadJsonAsync(fixture.ManifestPath);
        manifest["AttachmentBindings"]![otherKey] = JsonSerializer.SerializeToNode(other);
        await File.WriteAllTextAsync(fixture.ManifestPath, manifest.ToJsonString());

        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Single(snapshot.GetPage()); Assert.Equal(2, snapshot.AttachmentBindings.Count);
        foreach (MediaAttachmentBinding binding in new[] { first, other })
        {
            ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
                MediaAttachmentResolver.Resolve(snapshot, Resource, binding.Reference.PostId, Attachment(binding)));
            Assert.Equal(fixture.Committed.AssetId, resolved.Asset.AssetId);
            Assert.Null(resolved.Binding!.PassiveImport!.IncomingRevisionKey);
            Assert.Null(resolved.Binding.PassiveImport.History);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidImportedHistorySurvivesWithoutBecomingLocalAcceptance(bool mismatch)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        MediaImportedHistory history = new(mismatch ? MediaAcceptanceKind.ExplicitProviderMismatch : MediaAcceptanceKind.Strict,
            HistoricalAt, mismatch, mismatch);
        Fixture fixture = await CreateAsync(directory, board, mismatch ? MismatchingProvider(includeSha: false) : MatchingProvider(), history: history);

        MediaAttachmentBinding binding = Assert.Single((await MediaAssetReader.OpenAsync(directory.Path)).AttachmentBindings).Value;
        Assert.Equal(history, binding.PassiveImport!.History);
        Assert.Equal(MediaAcceptanceKind.PassiveImport, binding.Acceptance.Kind);
        Assert.Equal(ImportedAt, binding.Acceptance.AcceptedAtUtc);
        Assert.NotEqual(history.AcceptedAtUtc, binding.Acceptance.AcceptedAtUtc);
        Assert.Null(binding.Acceptance.ProfileId); Assert.Null(binding.Acceptance.PolicyRevision);
        Assert.Equal(fixture.Binding, binding);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task DownlevelManifestRejectsPassiveEvidenceRatherThanDiscardingIt(int version)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MatchingProvider());
        JsonObject manifest = await ReadJsonAsync(fixture.ManifestPath);
        manifest["Version"] = version;
        await AssertCorruptWithoutRepairAsync(fixture, manifest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyProjectionAlsoRejectsPassiveProvenanceAttachedToStrictAcceptance(int version)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MatchingProvider());
        JsonObject manifest = await ReadJsonAsync(fixture.ManifestPath);
        manifest["Version"] = version;
        manifest["AttachmentBindings"]![fixture.Binding.Reference.RevisionKey]!["Acceptance"]!["Kind"] = (int)MediaAcceptanceKind.Strict;
        await AssertCorruptWithoutRepairAsync(fixture, manifest);
    }

    [Theory]
    [InlineData(1, "inventory")]
    [InlineData(2, "inventory")]
    [InlineData(1, "binding-key")]
    [InlineData(2, "binding-key")]
    [InlineData(1, "kind")]
    [InlineData(2, "kind")]
    [InlineData(1, "passive-import")]
    [InlineData(2, "passive-import")]
    public async Task LegacyRawJsonDuplicatesCannotHidePassiveEvidenceBeforeProjection(int version, string duplicate)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MatchingProvider());
        string key = JsonSerializer.Serialize(fixture.Binding.Reference.RevisionKey);
        string passive = JsonSerializer.Serialize(fixture.Binding);
        string active = JsonSerializer.Serialize(fixture.Binding with
        {
            Acceptance = new(MediaAcceptanceKind.Strict, false, false, ImportedAt), PassiveImport = null
        });
        string passiveRecord = JsonSerializer.Serialize(fixture.Binding.PassiveImport);
        string inventory = duplicate switch
        {
            "inventory" => "\"AttachmentBindings\":{" + key + ":" + passive + "},\"AttachmentBindings\":{}",
            "binding-key" => "\"AttachmentBindings\":{" + key + ":" + passive + "," + key + ":" + active + "}",
            "kind" => "\"AttachmentBindings\":{" + key + ":" + active.Replace("\"Kind\":0", "\"Kind\":2,\"Kind\":0", StringComparison.Ordinal) + "}",
            "passive-import" => "\"AttachmentBindings\":{" + key + ":" + active[..^1] + ",\"PassiveImport\":" + passiveRecord + ",\"PassiveImport\":null}}",
            _ => throw new ArgumentOutOfRangeException(nameof(duplicate))
        };
        // Last-value-wins DTOs would erase the passive evidence, and v1/2 then drop
        // the binding inventory entirely. Keep duplicate properties as RAW JSON.
        string wire = "{\"Version\":" + version + ",\"Entries\":{},\"Assets\":{}," + inventory + "}";
        using JsonDocument syntax = JsonDocument.Parse(wire); // Rejection must not merely be malformed JSON.
        JsonElement lastInventory = syntax.RootElement.GetProperty("AttachmentBindings");
        if (duplicate == "inventory") Assert.Empty(lastInventory.EnumerateObject());
        else
        {
            MediaAttachmentBinding lastValue = JsonSerializer.Deserialize<MediaAttachmentBinding>(
                lastInventory.GetProperty(fixture.Binding.Reference.RevisionKey).GetRawText())!;
            Assert.Equal(MediaAcceptanceKind.Strict, lastValue.Acceptance.Kind);
            Assert.Null(lastValue.PassiveImport);
        }
        await AssertCorruptWireWithoutRepairAsync(fixture, wire);
    }

    [Fact]
    public async Task PassivePreviewCannotPassBindingValidationEvenWithRecomputedExactKeys()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MatchingProvider());
        MediaProviderMetadata preview = fixture.Binding.Provider with { Variant = "preview" };
        string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "123", 0, preview);
        MediaAttachmentBinding binding = fixture.Binding with
        {
            Provider = preview, Reference = new(Resource, "123", 0, key),
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, preview),
            PassiveImport = fixture.Binding.PassiveImport! with { IncomingRevisionKey = key }
        };
        Assert.Throws<InvalidDataException>(() => MediaIntegrityValidation.ValidateBinding(key, binding));
    }

    [Theory]
    [InlineData("ProfileId")]
    [InlineData("PluginOwnerId")]
    [InlineData("SourceId")]
    [InlineData("PolicyRevision")]
    public async Task PassiveAcceptanceCannotCarryLocalActiveAuthority(string property)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MatchingProvider());
        JsonObject manifest = await ReadJsonAsync(fixture.ManifestPath);
        JsonObject acceptance = manifest["AttachmentBindings"]![fixture.Binding.Reference.RevisionKey]!["Acceptance"]!.AsObject();
        acceptance[property] = property == "PolicyRevision" ? JsonValue.Create(1L) : JsonValue.Create("stub");
        await AssertCorruptWithoutRepairAsync(fixture, manifest);
    }

    [Theory]
    [InlineData("missing-provenance")]
    [InlineData("active-hybrid")]
    [InlineData("different-local-time")]
    [InlineData("noncanonical-operation")]
    [InlineData("noncanonical-fingerprint")]
    [InlineData("wrong-incoming-key")]
    [InlineData("wrong-measured-flags")]
    [InlineData("asset-disagreement")]
    [InlineData("unkeyed-history")]
    public async Task MalformedPassiveOrHybridEvidenceIsNotReadable(string corruption)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MatchingProvider());
        JsonObject manifest = await ReadJsonAsync(fixture.ManifestPath);
        JsonObject binding = manifest["AttachmentBindings"]![fixture.Binding.Reference.RevisionKey]!.AsObject();
        switch (corruption)
        {
            case "missing-provenance": binding.Remove("PassiveImport"); break;
            case "active-hybrid": binding["Acceptance"]!["Kind"] = (int)MediaAcceptanceKind.Strict; break;
            case "different-local-time": binding["Acceptance"]!["AcceptedAtUtc"] = ImportedAt.AddSeconds(1); break;
            case "noncanonical-operation": binding["PassiveImport"]!["OperationId"] = Guid.Parse(fixture.Binding.PassiveImport!.OperationId).ToString("N"); break;
            case "noncanonical-fingerprint": binding["PassiveImport"]!["PackageFingerprint"] = new string('A', 64); break;
            case "wrong-incoming-key": binding["PassiveImport"]!["IncomingRevisionKey"] = "att-v1:" + new string('a', 64); break;
            case "wrong-measured-flags": binding["Acceptance"]!["Md5Mismatch"] = true; break;
            case "asset-disagreement": binding["Actual"]!["Sha256"] = new string('b', 64); break;
            case "unkeyed-history":
                binding["PassiveImport"]!["IncomingRevisionKey"] = null;
                binding["PassiveImport"]!["History"] = JsonSerializer.SerializeToNode(new MediaImportedHistory(MediaAcceptanceKind.Strict, HistoricalAt, false, false));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        await AssertCorruptWithoutRepairAsync(fixture, manifest);
    }

    [Theory]
    [InlineData("sha-with-history")]
    [InlineData("mismatch-claimed-strict")]
    [InlineData("strict-claimed-mismatch")]
    [InlineData("wrong-flags")]
    [InlineData("recursive-passive-kind")]
    [InlineData("missing-history-time")]
    public async Task InvalidHistoricalClaimCannotBeSilentlyDowngradedToNoHistory(string corruption)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        bool mismatch = corruption is "sha-with-history" or "mismatch-claimed-strict";
        Fixture fixture = await CreateAsync(directory, board,
            mismatch ? MismatchingProvider(includeSha: corruption == "sha-with-history") : MatchingProvider());
        MediaImportedHistory history = new(MediaAcceptanceKind.Strict, HistoricalAt, false, false);
        history = corruption switch
        {
            "sha-with-history" => history with { Kind = MediaAcceptanceKind.ExplicitProviderMismatch, SizeMismatch = true, Md5Mismatch = true },
            "strict-claimed-mismatch" => history with { Kind = MediaAcceptanceKind.ExplicitProviderMismatch },
            "wrong-flags" => history with { SizeMismatch = true },
            "recursive-passive-kind" => history with { Kind = MediaAcceptanceKind.PassiveImport },
            "missing-history-time" => history with { AcceptedAtUtc = default },
            _ => history
        };
        JsonObject manifest = await ReadJsonAsync(fixture.ManifestPath);
        manifest["AttachmentBindings"]![fixture.Binding.Reference.RevisionKey]!["PassiveImport"]!["History"] = JsonSerializer.SerializeToNode(history);
        await AssertCorruptWithoutRepairAsync(fixture, manifest);
    }

    [Fact]
    public async Task VerifiedReadStillRejectsSameLengthCorruptionWithoutChangingPassiveEvidence()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MismatchingProvider(includeSha: true));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);
        byte[] before = await File.ReadAllBytesAsync(fixture.ManifestPath);
        byte[] damaged = [7, 5, 3, 1];
        await File.WriteAllBytesAsync(fixture.Committed.Path, damaged);

        await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.OpenVerifiedReadAsync(fixture.Binding.AssetId));

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(fixture.Committed.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassiveBindingDoesNotAuthorizeNewActiveReferencesEvenToIdenticalBytes(bool shaMismatch)
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MismatchingProvider(includeSha: shaMismatch));
        byte[] before = await File.ReadAllBytesAsync(fixture.ManifestPath);
        DownloadItem existing = Item(board, directory.Path, fixture.Binding.Provider, ["123"]);
        // The exact imported binding is readable/reusable, not a grant for another post.
        Assert.Equal(fixture.Committed.AssetId, (await new MediaStore().TryRestoreAsync(existing, default))?.AssetId);
        DownloadItem additional = Item(board, directory.Path, fixture.Binding.Provider, ["123", "124"]);
        Assert.Null(additional.MediaIntegrityPolicy);
        Assert.Null(await new MediaStore().TryRestoreAsync(additional, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => CommitAsync(additional));

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(fixture.Committed.Path));
        Assert.Single((await MediaAssetReader.OpenAsync(directory.Path)).AttachmentBindings);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
        Assert.Single(Directory.GetFiles(directory.Path, "*.bin"));
    }

    [Fact]
    public async Task ActiveOnlyWriterStaysV4AndOmitsNullPassiveProperty()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        await CommitAsync(Item(board, directory.Path, MatchingProvider(), ["123"]));
        JsonObject manifest = await ReadJsonAsync(Path.Combine(directory.Path, ".meowchan-manifest.json"));
        Assert.Equal(4, manifest["Version"]!.GetValue<int>());
        JsonObject binding = Assert.Single(manifest["AttachmentBindings"]!.AsObject()).Value!.AsObject();
        Assert.Equal((int)MediaAcceptanceKind.Strict, binding["Acceptance"]!["Kind"]!.GetValue<int>());
        Assert.False(binding.ContainsKey("PassiveImport"));
    }

    [Fact]
    public async Task LaterActiveWritePreservesV5AndPassiveProvenanceExactly()
    {
        using TempDirectory directory = new(); using StubImageBoard board = new();
        Fixture fixture = await CreateAsync(directory, board, MismatchingProvider(includeSha: true));
        MediaProviderMetadata unrelated = MatchingProvider() with { MediaId = "second", Url = "https://stub.test/second.bin", OriginalFileName = "second.bin" };
        await CommitAsync(Item(board, directory.Path, unrelated, ["124"]));

        MediaAssetSnapshot restarted = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(MediaManifestStatus.Available, restarted.Status);
        Assert.Equal(5, restarted.ManifestVersion);
        Assert.Equal(2, restarted.Count);
        Assert.Equal(fixture.Binding, restarted.FindAttachmentBinding(fixture.Binding.Reference.RevisionKey));
        Assert.Single(restarted.AttachmentBindings.Values, binding => binding.Acceptance.Kind == MediaAcceptanceKind.Strict);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(fixture.Committed.Path));
    }

    private static MediaProviderMetadata MatchingProvider() => new("m", "original", "https://stub.test/media/m.bin", "m.bin",
        Bytes.Length, Convert.ToBase64String(MD5.HashData(Bytes)), Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant())
        { ExpectedSha1 = Convert.ToBase64String(SHA1.HashData(Bytes)) };

    private static MediaProviderMetadata MismatchingProvider(bool includeSha) => MatchingProvider() with
    {
        ExpectedSize = Bytes.Length + 1,
        ExpectedMd5 = Convert.ToBase64String(MD5.HashData([99])),
        ExpectedSha256 = includeSha ? new string('a', 64) : MatchingProvider().ExpectedSha256,
        ExpectedSha1 = includeSha ? Convert.ToBase64String(SHA1.HashData([99])) : MatchingProvider().ExpectedSha1
    };

    private static DownloadItem Item(StubImageBoard board, string directory, MediaProviderMetadata provider, string[] posts)
        => new(new Uri(provider.Url), Path.Combine(directory, provider.MediaId + ".bin"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), provider.MediaId, provider.ExpectedSize,
            provider.ExpectedMd5, provider.OriginalFileName, null, provider.Variant)
        {
            ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1,
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            AttachmentReferences = posts.Select(post => new AttachmentRevisionReference(Resource, post, 0,
                MediaRevisionKeys.CreateAttachmentKey(Resource, post, 0, provider))).ToArray()
        };

    private static async Task<MediaCommitResult> CommitAsync(DownloadItem item)
    {
        using MemoryStream source = new(Bytes);
        return await new MediaStore().CommitAsync(item, source, default);
    }

    private static async Task<Fixture> CreateAsync(TempDirectory directory, StubImageBoard board,
        MediaProviderMetadata provider, bool incomingKey = true, MediaImportedHistory? history = null)
    {
        // Obtain the asset ID/inventory from a real strict commit, then supply the passive wire stand.
        MediaCommitResult committed = await CommitAsync(Item(board, directory.Path, MatchingProvider(), ["123"]));
        MediaAttachmentBinding original = Assert.Single((await MediaAssetReader.OpenAsync(directory.Path)).AttachmentBindings).Value;
        string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "123", 0, provider);
        ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(provider, original.Actual);
        MediaAttachmentBinding passive = original with
        {
            Reference = new(Resource, "123", 0, key), Provider = provider,
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            Acceptance = new(MediaAcceptanceKind.PassiveImport, assessment.SizeMismatch, assessment.Md5Mismatch, ImportedAt),
            PassiveImport = new("b8cce56f-562e-4c44-8a65-a68879108f11", new string('c', 64), ImportedAt, incomingKey ? key : null, history)
        };
        string path = Path.Combine(directory.Path, ".meowchan-manifest.json");
        JsonObject manifest = await ReadJsonAsync(path);
        manifest["Version"] = 5;
        manifest["AttachmentBindings"] = new JsonObject { [key] = JsonSerializer.SerializeToNode(passive) };
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        // Every rejection starts from a positively readable passive fixture, not an already-corrupt file.
        Assert.Equal(MediaManifestStatus.Available, (await MediaAssetReader.OpenAsync(directory.Path)).Status);
        return new(directory.Path, path, committed, passive);
    }

    private static CapturedAttachment Attachment(MediaAttachmentBinding binding)
        => new(binding.Reference.Position, binding.Provider.MediaId, binding.Provider.Variant, binding.Provider.Url,
            binding.Provider.OriginalFileName, ExpectedSize: binding.Provider.ExpectedSize, ExpectedMd5: binding.Provider.ExpectedMd5)
        {
            ExpectedSha256 = binding.Provider.ExpectedSha256, ExpectedSha1 = binding.Provider.ExpectedSha1,
            AttachmentRevisionKey = binding.Reference.RevisionKey
        };

    private static async Task<JsonObject> ReadJsonAsync(string path)
        => JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();

    private static Task AssertCorruptWithoutRepairAsync(Fixture fixture, JsonObject manifest)
        => AssertCorruptWireWithoutRepairAsync(fixture, manifest.ToJsonString());

    private static async Task AssertCorruptWireWithoutRepairAsync(Fixture fixture, string wire)
    {
        await File.WriteAllTextAsync(fixture.ManifestPath, wire);
        string[] before = Directory.GetFileSystemEntries(fixture.Directory).Order(StringComparer.Ordinal).ToArray();
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(fixture.Directory);
        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status);
        Assert.Empty(snapshot.AttachmentBindings); Assert.Empty(snapshot.GetPage()); Assert.Null(snapshot.Generation);
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "123", Attachment(fixture.Binding)));
        Assert.Equal(wire, await File.ReadAllTextAsync(fixture.ManifestPath));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(fixture.Committed.Path));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Directory).Order(StringComparer.Ordinal).ToArray());
    }

    private sealed record Fixture(string Directory, string ManifestPath, MediaCommitResult Committed, MediaAttachmentBinding Binding);
}
