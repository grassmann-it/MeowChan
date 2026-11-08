using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// Synthetic wire manifests only: these tests do not claim journalled import/publication.
public sealed class NativeV6PassiveInventoryInvalidTests
{
    private static readonly ResourceId Resource = new("stub", "g", "123");
    private static readonly byte[] PreviewBytes = [9, 7, 5, 3, 1];
    private static readonly byte[] OriginalBytes = [2, 4, 6, 8];
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly MediaActualIntegrity Original = Integrity(OriginalBytes);
    private static readonly MediaActualIntegrity Preview = Integrity(PreviewBytes);
    private static readonly MediaProviderMetadata Provider = new("m", "original", "https://stub.test/media/m.bin", "m.bin",
        Original.Length, Original.Md5, Original.Sha256);
    private static readonly string Key = MediaRevisionKeys.CreateAttachmentKey(Resource, "123", 0, Provider);
    private static readonly CapturedAttachment Metadata = new(0, Provider.MediaId, "original", Provider.Url,
        Provider.OriginalFileName, ExpectedSize: Provider.ExpectedSize, ExpectedMd5: Provider.ExpectedMd5)
        { ExpectedSha256 = Provider.ExpectedSha256, AttachmentRevisionKey = Key };
    private static readonly MediaOccurrence Occurrence = new(Resource, "123", 0, Key);
    private static readonly MediaPassiveObservation Origin = new("b8cce56f-562e-4c44-8a65-a68879108f11", new string('c', 64), ImportedAt);
    private static readonly string BlobName = Preview.Sha256 + ".derived.bin";

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("parent-resource")]
    [InlineData("parent-post")]
    [InlineData("parent-position")]
    [InlineData("parent-key")]
    [InlineData("parent-unkeyed")]
    [InlineData("missing-original-version")]
    [InlineData("source-length")]
    [InlineData("source-md5")]
    [InlineData("source-sha256")]
    [InlineData("source-sha1")]
    [InlineData("source-sha1-missing")]
    [InlineData("parent-sha1-missing")]
    public async Task DerivedMustNameExactParentAndExactSourceTuple(string mutation)
    {
        using TempDirectory directory = new();
        JsonObject manifest = await ValidFixtureAsync(directory);
        JsonObject evidence = Evidence(manifest), derived = Derived(manifest);
        switch (mutation)
        {
            case "missing-parent": manifest["OriginalEvidence"] = new JsonArray(); break;
            case "parent-resource": derived["Parent"]!["Resource"]!["Thread"] = "124"; break;
            case "parent-post": derived["Parent"]!["PostId"] = "124"; break;
            case "parent-position": derived["Parent"]!["Position"] = 1; break;
            case "parent-key": derived["Parent"]!["OriginalRevisionKey"] = "att-v1:" + new string('a', 64); break;
            case "parent-unkeyed": derived["Parent"]!["OriginalRevisionKey"] = null; break;
            case "missing-original-version": evidence["OriginalByteVersion"] = null; break;
            case "source-length": derived["SourceVersion"]!["Length"] = Original.Length + 1; break;
            case "source-md5": derived["SourceVersion"]!["Md5"] = Preview.Md5; break;
            case "source-sha256": derived["SourceVersion"]!["Sha256"] = Preview.Sha256; break;
            case "source-sha1": derived["SourceVersion"]!["Sha1"] = Preview.Sha1; break;
            case "source-sha1-missing": derived["SourceVersion"]!["Sha1"] = null; break;
            case "parent-sha1-missing": evidence["OriginalByteVersion"]!["Sha1"] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        await AssertCorruptAsync(directory, manifest.ToJsonString());
    }

    [Theory]
    [InlineData("identical-evidence")]
    [InlineData("contradictory-evidence")]
    [InlineData("identical-derived")]
    [InlineData("contradictory-derived")]
    [InlineData("shared-file-different-actual")]
    public async Task SemanticDuplicatesAndConflictingSharedBlobEvidenceReject(string mutation)
    {
        using TempDirectory directory = new();
        JsonObject manifest = await ValidFixtureAsync(directory);
        if (mutation.EndsWith("evidence", StringComparison.Ordinal))
        {
            JsonObject copy = Evidence(manifest).DeepClone().AsObject();
            if (mutation == "contradictory-evidence") copy["IncomingStatus"] = (int)MediaObservedContentStatus.Failed;
            manifest["OriginalEvidence"]!.AsArray().Add(copy);
        }
        else
        {
            JsonObject copy = Derived(manifest).DeepClone().AsObject();
            if (mutation == "contradictory-derived")
            {
                copy["IncomingStatus"] = (int)MediaDerivedStatus.Omitted;
                copy["Blob"] = null;
            }
            if (mutation == "shared-file-different-actual")
            {
                // A distinct valid occurrence avoids a duplicate-member failure hiding
                // the conflicting measurements for the same physical blob name.
                string otherKey = MediaRevisionKeys.CreateAttachmentKey(Resource, "124", 0, Provider);
                JsonObject otherEvidence = Evidence(manifest).DeepClone().AsObject();
                otherEvidence["Occurrence"]!["PostId"] = "124";
                otherEvidence["Occurrence"]!["OriginalRevisionKey"] = otherKey;
                otherEvidence["Metadata"]!["AttachmentRevisionKey"] = otherKey;
                manifest["OriginalEvidence"]!.AsArray().Add(otherEvidence);
                copy["Parent"] = otherEvidence["Occurrence"]!.DeepClone();
                // Prove the two-occurrence/shared-byte baseline is accepted first.
                manifest["DerivedRepresentations"]!.AsArray().Add(copy);
                await AssertAvailableAsync(directory, manifest);
                copy["Blob"]!["Actual"]!["Md5"] = Original.Md5;
            }
            else manifest["DerivedRepresentations"]!.AsArray().Add(copy);
        }
        await AssertCorruptAsync(directory, manifest.ToJsonString());
    }

    [Theory]
    [InlineData("evidence-status")]
    [InlineData("derived-status")]
    [InlineData("derived-kind")]
    [InlineData("contained-no-blob")]
    [InlineData("omitted-has-blob")]
    [InlineData("unavailable-has-blob")]
    [InlineData("blob-actual-null")]
    [InlineData("blob-sha1-null")]
    [InlineData("blob-sha1-invalid")]
    [InlineData("blob-md5-invalid")]
    [InlineData("blob-sha256-invalid")]
    [InlineData("blob-negative-length")]
    [InlineData("blob-wrong-hash-name")]
    [InlineData("blob-wrong-suffix")]
    [InlineData("blob-uppercase")]
    [InlineData("blob-traversal")]
    [InlineData("blob-absolute")]
    [InlineData("blob-backslash")]
    public async Task StatusKindAndPayloadMustBeConsistent(string mutation)
    {
        using TempDirectory directory = new();
        JsonObject manifest = await ValidFixtureAsync(directory);
        JsonObject derived = Derived(manifest), blob = derived["Blob"]!.AsObject();
        switch (mutation)
        {
            case "evidence-status": Evidence(manifest)["IncomingStatus"] = 99; break;
            case "derived-status": derived["IncomingStatus"] = 99; break;
            case "derived-kind": derived["Kind"] = 99; break;
            case "contained-no-blob": derived["Blob"] = null; break;
            case "omitted-has-blob": derived["IncomingStatus"] = (int)MediaDerivedStatus.Omitted; break;
            case "unavailable-has-blob": derived["IncomingStatus"] = (int)MediaDerivedStatus.Unavailable; break;
            case "blob-actual-null": blob["Actual"] = null; break;
            case "blob-sha1-null": blob["Actual"]!["Sha1"] = null; break;
            case "blob-sha1-invalid": blob["Actual"]!["Sha1"] = "invalid"; break;
            case "blob-md5-invalid": blob["Actual"]!["Md5"] = "invalid"; break;
            case "blob-sha256-invalid": blob["Actual"]!["Sha256"] = "invalid"; break;
            case "blob-negative-length": blob["Actual"]!["Length"] = -1; break;
            case "blob-wrong-hash-name": blob["RelativeFileName"] = Original.Sha256 + ".derived.bin"; break;
            case "blob-wrong-suffix": blob["RelativeFileName"] = Preview.Sha256 + ".bin"; break;
            case "blob-uppercase": blob["RelativeFileName"] = Preview.Sha256.ToUpperInvariant() + ".derived.bin"; break;
            case "blob-traversal": blob["RelativeFileName"] = "../" + BlobName; break;
            case "blob-absolute": blob["RelativeFileName"] = Path.Combine(directory.Path, BlobName); break;
            case "blob-backslash": blob["RelativeFileName"] = "sub\\" + BlobName; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        await AssertCorruptAsync(directory, manifest.ToJsonString());
    }

    [Theory]
    [InlineData("unkeyed-known-bytes")]
    [InlineData("unknown-metadata-known-bytes")]
    [InlineData("metadata-position")]
    [InlineData("metadata-key")]
    [InlineData("metadata-variant")]
    [InlineData("metadata-local-path")]
    [InlineData("metadata-invalid-url")]
    [InlineData("occurrence-negative-position")]
    public async Task OriginalEvidenceCannotFabricateIdentityOrCarryLocalPaths(string mutation)
    {
        using TempDirectory directory = new();
        JsonObject manifest = await ValidFixtureAsync(directory);
        // Remove derived members first so failures cannot be explained by dangling parents.
        manifest["DerivedRepresentations"] = new JsonArray();
        await AssertAvailableAsync(directory, manifest);
        JsonObject evidence = Evidence(manifest), metadata = evidence["Metadata"]!.AsObject();
        switch (mutation)
        {
            case "unkeyed-known-bytes":
            case "unknown-metadata-known-bytes":
                evidence["Occurrence"]!["OriginalRevisionKey"] = null;
                metadata["AttachmentRevisionKey"] = null;
                if (mutation == "unknown-metadata-known-bytes")
                {
                    metadata["MediaId"] = null;
                    metadata["Url"] = null;
                    evidence["IncomingStatus"] = (int)MediaObservedContentStatus.Unknown;
                }
                break;
            case "metadata-position": metadata["Position"] = 1; break;
            case "metadata-key": metadata["AttachmentRevisionKey"] = "att-v1:" + new string('a', 64); break;
            case "metadata-variant": metadata["Variant"] = "preview"; break;
            case "metadata-local-path": metadata["PlannedPath"] = Path.Combine(directory.Path, "original.bin"); break;
            case "metadata-invalid-url": metadata["Url"] = "not a URI"; break;
            case "occurrence-negative-position": evidence["Occurrence"]!["Position"] = -1; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        await AssertCorruptAsync(directory, manifest.ToJsonString());
    }

    [Theory]
    [InlineData("operation-empty-guid")]
    [InlineData("operation-n-format")]
    [InlineData("operation-uppercase")]
    [InlineData("fingerprint-uppercase")]
    [InlineData("fingerprint-short")]
    [InlineData("default-time")]
    [InlineData("missing-origin")]
    [InlineData("different-derived-operation")]
    [InlineData("different-derived-fingerprint")]
    [InlineData("different-derived-time")]
    public async Task ProvenanceMustBeCanonicalAndEqualForParentAndDerived(string mutation)
    {
        using TempDirectory directory = new();
        JsonObject manifest = await ValidFixtureAsync(directory);
        JsonObject origin = Evidence(manifest)["Origin"]!.AsObject();
        switch (mutation)
        {
            case "operation-empty-guid": origin["OperationId"] = Guid.Empty.ToString("D"); break;
            case "operation-n-format": origin["OperationId"] = Guid.Parse(Origin.OperationId).ToString("N"); break;
            case "operation-uppercase": origin["OperationId"] = Origin.OperationId.ToUpperInvariant(); break;
            case "fingerprint-uppercase": origin["PackageFingerprint"] = Origin.PackageFingerprint.ToUpperInvariant(); break;
            case "fingerprint-short": origin["PackageFingerprint"] = "abc"; break;
            case "default-time": origin["ImportedAtUtc"] = default(DateTimeOffset); break;
            case "missing-origin": Evidence(manifest)["Origin"] = null; break;
            case "different-derived-operation": Derived(manifest)["Origin"]!["OperationId"] = "a8cce56f-562e-4c44-8a65-a68879108f11"; break;
            case "different-derived-fingerprint": Derived(manifest)["Origin"]!["PackageFingerprint"] = new string('d', 64); break;
            case "different-derived-time": Derived(manifest)["Origin"]!["ImportedAtUtc"] = ImportedAt.AddSeconds(1); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        // Invalid common provenance must fail validation, not only origin equality.
        if (!mutation.StartsWith("different-derived", StringComparison.Ordinal))
            Derived(manifest)["Origin"] = Evidence(manifest)["Origin"]?.DeepClone();
        await AssertCorruptAsync(directory, manifest.ToJsonString());
    }

    public static IEnumerable<object[]> StrictObjects()
    {
        foreach (string path in new[] { "OriginalEvidence/0", "OriginalEvidence/0/Occurrence", "OriginalEvidence/0/Occurrence/Resource",
                     "OriginalEvidence/0/Metadata", "OriginalEvidence/0/OriginalByteVersion", "OriginalEvidence/0/Origin",
                     "DerivedRepresentations/0", "DerivedRepresentations/0/Parent", "DerivedRepresentations/0/SourceVersion",
                     "DerivedRepresentations/0/Blob", "DerivedRepresentations/0/Blob/Actual", "DerivedRepresentations/0/Origin" })
            foreach (string mutation in new[] { "unknown", "duplicate", "missing" }) yield return [path, mutation];
    }

    [Theory]
    [MemberData(nameof(StrictObjects))]
    public async Task NewRecordsRejectUnknownDuplicateAndMissingFields(string path, string mutation)
    {
        using TempDirectory directory = new();
        JsonObject manifest = await ValidFixtureAsync(directory);
        JsonObject selected = At(manifest, path).AsObject();
        if (mutation == "unknown")
        {
            selected["UnrecognizedField"] = true;
            await AssertCorruptAsync(directory, manifest.ToJsonString());
        }
        else if (mutation == "missing")
        {
            // Includes required nullable fields: omission is not an explicit null observation.
            foreach (string field in selected.Select(p => p.Key).ToArray())
            {
                JsonObject copy = manifest.DeepClone().AsObject();
                At(copy, path).AsObject().Remove(field);
                await AssertCorruptAsync(directory, copy.ToJsonString());
            }
        }
        else
        {
            string wire = manifest.ToJsonString();
            string original = selected.ToJsonString();
            KeyValuePair<string, JsonNode?> field = selected.First();
            string duplicated = original[..^1] + "," + JsonSerializer.Serialize(field.Key) + ":" + (field.Value?.ToJsonString() ?? "null") + "}";
            // Replace just one observed occurrence; identical parent/source/origin values
            // elsewhere must not accidentally supply the rejection under test.
            int offset = wire.IndexOf(original, StringComparison.Ordinal);
            Assert.True(offset >= 0);
            wire = wire[..offset] + duplicated + wire[(offset + original.Length)..];
            await AssertCorruptAsync(directory, wire);
        }
    }

    [Theory]
    [InlineData("OriginalEvidence", "missing")]
    [InlineData("OriginalEvidence", "null")]
    [InlineData("OriginalEvidence", "object")]
    [InlineData("OriginalEvidence", "null-member")]
    [InlineData("DerivedRepresentations", "missing")]
    [InlineData("DerivedRepresentations", "null")]
    [InlineData("DerivedRepresentations", "object")]
    [InlineData("DerivedRepresentations", "null-member")]
    public async Task V6RequiresBothExplicitInventoryArrays(string field, string mutation)
    {
        using TempDirectory directory = new();
        JsonObject manifest = await ValidFixtureAsync(directory);
        switch (mutation)
        {
            case "missing": manifest.Remove(field); break;
            case "null": manifest[field] = null; break;
            case "object": manifest[field] = new JsonObject(); break;
            case "null-member": manifest[field]!.AsArray().Add((JsonNode?)null); break;
        }
        await AssertCorruptAsync(directory, manifest.ToJsonString());
    }

    public static IEnumerable<object[]> OldVersions()
    {
        for (int version = 1; version <= 5; version++)
            foreach (string field in new[] { "OriginalEvidence", "DerivedRepresentations" })
                foreach (string shape in new[] { "null", "empty", "populated" }) yield return [version, field, shape];
    }

    [Theory]
    [MemberData(nameof(OldVersions))]
    public async Task LegacyVersionsRejectInventoryFieldsBeforeProjection(int version, string field, string shape)
    {
        using TempDirectory directory = new();
        JsonObject v6 = await ValidFixtureAsync(directory);
        JsonObject legacy = EmptyManifest(version);
        await AssertAvailableAsync(directory, legacy);
        legacy[field] = shape switch
        {
            "null" => null,
            "empty" => new JsonArray(),
            _ => v6[field]!.DeepClone()
        };
        await AssertCorruptAsync(directory, legacy.ToJsonString());
    }

    private static MediaActualIntegrity Integrity(byte[] bytes) => new(bytes.Length,
        Convert.ToBase64String(MD5.HashData(bytes)), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
        { Sha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };

    private static JsonObject EmptyManifest(int version) => new()
    {
        ["Version"] = version, ["Entries"] = new JsonObject(), ["Assets"] = new JsonObject(),
        ["AttachmentBindings"] = new JsonObject()
    };

    private static async Task<JsonObject> ValidFixtureAsync(TempDirectory directory)
    {
        JsonObject manifest = EmptyManifest(6);
        manifest["OriginalEvidence"] = new JsonArray(JsonSerializer.SerializeToNode(new MediaOriginalEvidence(
            Occurrence, Metadata, MediaObservedContentStatus.Omitted, Original, Origin)));
        manifest["DerivedRepresentations"] = new JsonArray(JsonSerializer.SerializeToNode(new MediaDerivedRepresentation(
            Occurrence, Original, MediaDerivedKind.Preview, MediaDerivedStatus.Contained, new(BlobName, Preview), Origin)));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, BlobName), PreviewBytes);
        // Unreferenced bytes are never adopted as an Original by either a valid or corrupt read.
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "unowned-original.bin"), OriginalBytes);
        await AssertAvailableAsync(directory, manifest);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Single(snapshot.OriginalEvidence);
        Assert.Single(snapshot.DerivedRepresentations);
        Assert.Empty(snapshot.GetPage());
        Assert.Empty(snapshot.AttachmentBindings);
        return manifest;
    }

    private static async Task AssertAvailableAsync(TempDirectory directory, JsonObject manifest)
    {
        string wire = manifest.ToJsonString();
        string path = Path.Combine(directory.Path, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, wire);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(manifest["Version"]!.GetValue<int>(), snapshot.ManifestVersion);
        Assert.Equal(wire, await File.ReadAllTextAsync(path));
    }

    private static async Task AssertCorruptAsync(TempDirectory directory, string wire)
    {
        using JsonDocument syntax = JsonDocument.Parse(wire); // Valid JSON, including intentional duplicate properties.
        string path = Path.Combine(directory.Path, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, wire);
        string[] before = Directory.GetFileSystemEntries(directory.Path).Order(StringComparer.Ordinal).ToArray();
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory.Path);
        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status);
        Assert.Empty(snapshot.OriginalEvidence);
        Assert.Empty(snapshot.DerivedRepresentations);
        Assert.Empty(snapshot.AttachmentBindings);
        Assert.Empty(snapshot.GetPage());
        Assert.Equal(0, snapshot.Count);
        Assert.Null(snapshot.Generation);
        Assert.Null(snapshot.FindAttachmentBinding(Key));
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource, "123", Metadata));
        Assert.Equal(wire, await File.ReadAllTextAsync(path));
        Assert.Equal(PreviewBytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, BlobName)));
        Assert.Equal(OriginalBytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "unowned-original.bin")));
        Assert.Equal(before, Directory.GetFileSystemEntries(directory.Path).Order(StringComparer.Ordinal).ToArray());
    }

    private static JsonObject Evidence(JsonObject manifest) => manifest["OriginalEvidence"]![0]!.AsObject();
    private static JsonObject Derived(JsonObject manifest) => manifest["DerivedRepresentations"]![0]!.AsObject();
    private static JsonNode At(JsonNode node, string path)
    {
        foreach (string part in path.Split('/')) node = node is JsonArray array ? array[int.Parse(part)]! : node[part]!;
        return node;
    }
}
