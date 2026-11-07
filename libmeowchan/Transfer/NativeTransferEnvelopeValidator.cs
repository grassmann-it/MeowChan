using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Transfer;

/// <summary>Passive stored-ZIP framing/integrity inspection, without extraction or catalog admission.</summary>
internal static class NativeTransferEnvelopeValidator
{
    private const string Format = "meowchan-transfer";
    private static readonly TransferEnvelopeLimits SafetyCaps = new();

    internal static async Task<TransferEnvelopeInspection> InspectAsync(Stream source,
        CancellationToken cancellationToken = default, TransferEnvelopeLimits? limits = null,
        NativeTransferDerivedPayloadValidator? derivedPayloads = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= SafetyCaps;
        if (!limits.IsWithinSafetyCaps()) throw new ArgumentOutOfRangeException(nameof(limits));
        cancellationToken.ThrowIfCancellationRequested();
        derivedPayloads?.Reset();
        long? originalPosition = null;
        bool cancelled = false, restoreFailed = false;
        TransferEnvelopeInspection result;
        try
        {
            if (!source.CanRead || !source.CanSeek) return Fail(TransferEnvelopeError.UnreadableStream);
            originalPosition = source.Position;
            result = await InspectCoreAsync(source, limits, cancellationToken, derivedPayloads).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { cancelled = true; result = Fail(TransferEnvelopeError.ReadFailure); }
        catch (TransferEnvelopeException e) { result = Fail(e.Error); }
        catch (EndOfStreamException) { result = Fail(TransferEnvelopeError.InvalidZip); }
        catch (JsonException) { result = Fail(TransferEnvelopeError.InvalidManifest); }
        catch (OverflowException) { result = Fail(TransferEnvelopeError.PayloadLimit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        { result = Fail(TransferEnvelopeError.ReadFailure); }
        finally
        {
            if (originalPosition.HasValue)
            {
                try { source.Position = originalPosition.Value; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException) { restoreFailed = true; }
            }
        }
        if (cancelled) throw new OperationCanceledException(cancellationToken);
        return restoreFailed ? Fail(TransferEnvelopeError.ReadFailure) : result;
    }

    private static async Task<TransferEnvelopeInspection> InspectCoreAsync(Stream source, TransferEnvelopeLimits limits, CancellationToken ct,
        NativeTransferDerivedPayloadValidator? derivedPayloads)
    {
        if (source.Length > limits.ContainerBytes) return Fail(TransferEnvelopeError.ContainerLimit);
        IReadOnlyList<TransferZipEntry> directory = await NativeTransferZip.ReadDirectoryAsync(source, limits, ct).ConfigureAwait(false);
        Dictionary<string, TransferZipEntry> entries = new(StringComparer.Ordinal);
        long declaredTotal = 0;
        foreach (TransferZipEntry entry in directory)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(entry.Name, entry);
            long limit = entry.Name == "manifest.json" ? limits.ManifestBytes
                : entry.Name == "catalog.json" ? limits.CatalogBytes
                : entry.Name == "private.json" ? limits.PrivateMetadataBytes : limits.PayloadBytes;
            if (entry.Length > limit)
                return Fail(entry.Name == "manifest.json" ? TransferEnvelopeError.ManifestLimit : TransferEnvelopeError.PayloadLimit);
            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > limits.TotalBytes) return Fail(TransferEnvelopeError.PayloadLimit);
        }
        if (!entries.TryGetValue("manifest.json", out TransferZipEntry? manifestEntry)
            || !entries.TryGetValue("catalog.json", out TransferZipEntry? catalogEntry))
            return Fail(TransferEnvelopeError.InvalidReference);
        ReadBudget budget = new(limits.TotalBytes);
        byte[] manifest = new byte[checked((int)manifestEntry.Length)];
        await ReadEntryAsync(source, manifestEntry, budget, ct, manifest).ConfigureAwait(false);
        Manifest parsed = ParseManifest(manifest, limits, ct);
        // Framing has one exact entry set per version. A payload-shaped entry can
        // never substitute for the separately bound private component.
        HashSet<string> expectedEntries = new(StringComparer.Ordinal) { "manifest.json", "catalog.json" };
        if (parsed.PrivateMetadata != null) expectedEntries.Add("private.json");
        foreach (TransferBlobDescriptor blob in parsed.Payloads)
        {
            ct.ThrowIfCancellationRequested();
            expectedEntries.Add("payload/" + blob.Sha256);
        }
        if (!expectedEntries.SetEquals(entries.Keys)) return Fail(TransferEnvelopeError.InvalidReference);
        byte[] catalogBytes = new byte[checked((int)catalogEntry.Length)];
        await CheckBlobAsync(source, catalogEntry, parsed.Catalog, budget, ct, catalogBytes).ConfigureAwait(false);
        if (parsed.ContentSchema == "chan-v4" && derivedPayloads != null && !derivedPayloads.PreflightCatalog(catalogBytes, ct))
            return Fail(TransferEnvelopeError.InvalidReference);
        ImmutableArray<byte> privateBytes = ImmutableArray<byte>.Empty;
        if (parsed.PrivateMetadata is { } privateDescriptor)
        {
            ct.ThrowIfCancellationRequested();
            TransferZipEntry privateEntry = entries["private.json"];
            if (privateEntry.Length != privateDescriptor.Length) return Fail(TransferEnvelopeError.LengthMismatch);
            byte[] capture = new byte[checked((int)privateDescriptor.Length)];
            await CheckBlobAsync(source, privateEntry, privateDescriptor, budget, ct, capture).ConfigureAwait(false);
            privateBytes = ImmutableArray.CreateRange(capture);
        }
        ImmutableDictionary<string, TransferMeasuredBlob>.Builder measured = ImmutableDictionary.CreateBuilder<string, TransferMeasuredBlob>(StringComparer.Ordinal);
        foreach (TransferBlobDescriptor blob in parsed.Payloads)
        {
            ct.ThrowIfCancellationRequested();
            if (!entries.TryGetValue("payload/" + blob.Sha256, out TransferZipEntry? entry))
                return Fail(TransferEnvelopeError.InvalidReference);
            // Package inspection collects PNG structural evidence in the same measured read,
            // never rereads a mutable caller stream or increases the payload read budget.
            NativeTransferDerivedPayloadValidator.PngStructure? png = parsed.ContentSchema == "chan-v4" && derivedPayloads != null
                ? new(blob.Length) : null;
            measured.Add(blob.Sha256, await CheckBlobAsync(source, entry, blob, budget, ct, png: png).ConfigureAwait(false));
            if (png != null) derivedPayloads!.Record(blob.Sha256, png);
        }
        ct.ThrowIfCancellationRequested();
        return new(new(parsed.PackageId, parsed.ContentSchema,
            Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant(), parsed.Catalog, parsed.Payloads)
        {
            EnvelopeVersion = parsed.EnvelopeVersion,
            PrivateMetadata = parsed.PrivateMetadata,
            PrivateMetadataBytes = privateBytes,
            MeasuredPayloads = measured.ToImmutable(),
            CatalogBytes = ImmutableArray.CreateRange(catalogBytes)
        }, TransferEnvelopeError.None);
    }
    private static Manifest ParseManifest(byte[] bytes, TransferEnvelopeLimits limits, CancellationToken ct)
    {
        // JSON token scanning can defer string transcoding/unescaping. Limit this
        // translation boundary to untrusted manifest parsing, not arbitrary stream code.
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            return ParseManifestCore(bytes, limits, ct);
        }
        catch (Exception error) when (error is InvalidOperationException or DecoderFallbackException)
        { throw Error(TransferEnvelopeError.InvalidManifest); }
    }
    private static Manifest ParseManifestCore(byte[] bytes, TransferEnvelopeLimits limits, CancellationToken ct)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new() { MaxDepth = 12 });
        JsonElement root = document.RootElement;
        // Preserve the v1 seven-field grammar (including fail-closed legacy v2
        // manifests with no component). Only v2 plus the component has eight fields.
        bool privateManifest = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("envelopeVersion", out JsonElement candidateVersion)
            && candidateVersion.ValueKind == JsonValueKind.Number
            && candidateVersion.TryGetInt32(out int candidate) && candidate == 2
            && root.TryGetProperty("privateMetadata", out _);
        if (privateManifest)
            Fields(root, "format", "envelopeVersion", "packageId", "contentSchema", "requiredFeatures", "catalog", "payloads", "privateMetadata");
        else
            Fields(root, "format", "envelopeVersion", "packageId", "contentSchema", "requiredFeatures", "catalog", "payloads");
        string contentSchema = Text(root, "contentSchema");
        if (Text(root, "format") != Format || root.GetProperty("envelopeVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("envelopeVersion").TryGetInt32(out int version) || version != (privateManifest ? 2 : 1)
            || contentSchema is not ("chan-v1" or "chan-v2" or "chan-v3" or "chan-v4")) throw Error(TransferEnvelopeError.UnsupportedVersion);
        string packageId = Text(root, "packageId");
        if (!Guid.TryParseExact(packageId, "D", out Guid id) || id == Guid.Empty) throw Error(TransferEnvelopeError.InvalidManifest);
        JsonElement features = root.GetProperty("requiredFeatures");
        if (features.ValueKind != JsonValueKind.Array || features.GetArrayLength() != (privateManifest ? 2 : 1)
            || features[0].ValueKind != JsonValueKind.String || features[0].GetString() != contentSchema
            || privateManifest && (features[1].ValueKind != JsonValueKind.String || features[1].GetString() != "meowchan-private-v1"))
            throw Error(TransferEnvelopeError.UnsupportedFeature);
        TransferBlobDescriptor catalog = Blob(root.GetProperty("catalog"), limits.CatalogBytes);
        TransferBlobDescriptor? privateMetadata = privateManifest
            ? Blob(root.GetProperty("privateMetadata"), limits.PrivateMetadataBytes) : null;
        JsonElement payloads = root.GetProperty("payloads");
        if (payloads.ValueKind != JsonValueKind.Array || payloads.GetArrayLength() > limits.Entries - (privateManifest ? 3 : 2))
            throw Error(TransferEnvelopeError.InvalidManifest);
        ImmutableArray<TransferBlobDescriptor>.Builder blobs = ImmutableArray.CreateBuilder<TransferBlobDescriptor>();
        HashSet<string> hashes = new(StringComparer.Ordinal);
        long total = checked(bytes.Length + catalog.Length + (privateMetadata?.Length ?? 0));
        foreach (JsonElement item in payloads.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            TransferBlobDescriptor descriptor = Blob(item, limits.PayloadBytes);
            if (!hashes.Add(descriptor.Sha256)) throw Error(TransferEnvelopeError.InvalidReference);
            total = checked(total + descriptor.Length);
            if (total > limits.TotalBytes) throw Error(TransferEnvelopeError.PayloadLimit);
            blobs.Add(descriptor);
        }
        if (total > limits.TotalBytes) throw Error(TransferEnvelopeError.PayloadLimit);
        return new(version, packageId, contentSchema, catalog, blobs.ToImmutable(), privateMetadata);
    }

    private static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static TransferBlobDescriptor Blob(JsonElement element, long max)
    {
        Fields(element, "length", "sha256");
        JsonElement lengthElement = element.GetProperty("length");
        if (lengthElement.ValueKind != JsonValueKind.Number || !lengthElement.TryGetInt64(out long length) || length < 0)
            throw Error(TransferEnvelopeError.InvalidManifest);
        string hash = Text(element, "sha256");
        if (!IsHash(hash)) throw Error(TransferEnvelopeError.InvalidManifest);
        if (length > max) throw Error(TransferEnvelopeError.PayloadLimit);
        return new(length, hash);
    }
    private static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Error(TransferEnvelopeError.InvalidManifest);
        HashSet<string> remaining = new(names, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw Error(TransferEnvelopeError.InvalidManifest);
        if (remaining.Count != 0) throw Error(TransferEnvelopeError.InvalidManifest);
    }
    private static string Text(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw Error(TransferEnvelopeError.InvalidManifest);
        return value.GetString()!;
    }
    private static async Task<TransferMeasuredBlob> CheckBlobAsync(Stream source, TransferZipEntry entry, TransferBlobDescriptor descriptor, ReadBudget budget, CancellationToken ct, byte[]? capture = null,
        NativeTransferDerivedPayloadValidator.PngStructure? png = null)
    {
        if (entry.Length != descriptor.Length) throw Error(TransferEnvelopeError.LengthMismatch);
        TransferMeasuredBlob actual = await ReadMeasuredEntryAsync(source, entry, budget, ct, capture, png).ConfigureAwait(false);
        if (actual.Sha256 != descriptor.Sha256) throw Error(TransferEnvelopeError.HashMismatch);
        return actual;
    }
    private static async Task<string> ReadEntryAsync(Stream source, TransferZipEntry entry, ReadBudget budget,
        CancellationToken ct, byte[]? capture = null)
        => (await ReadMeasuredEntryAsync(source, entry, budget, ct, capture).ConfigureAwait(false)).Sha256;
    private static async Task<TransferMeasuredBlob> ReadMeasuredEntryAsync(Stream source, TransferZipEntry entry, ReadBudget budget,
        CancellationToken ct, byte[]? capture = null, NativeTransferDerivedPayloadValidator.PngStructure? png = null)
    {
        source.Position = entry.DataOffset;
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        TransferCrc32 crc = new();
        byte[] buffer = new byte[64 * 1024];
        long count = 0;
        while (count < entry.Length)
        {
            ct.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min(buffer.Length, entry.Length - count);
            int read = await source.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            if (read == 0) throw Error(TransferEnvelopeError.LengthMismatch);
            budget.Add(read); hash.AppendData(buffer, 0, read); md5.AppendData(buffer, 0, read); sha1.AppendData(buffer, 0, read); crc.Add(buffer.AsSpan(0, read));
            png?.Append(buffer.AsSpan(0, read), ct);
            if (capture != null) buffer.AsSpan(0, read).CopyTo(capture.AsSpan(checked((int)count)));
            count += read;
        }
        if (crc.Value != entry.Crc32) throw Error(TransferEnvelopeError.InvalidZip);
        return new(count, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToBase64String(md5.GetHashAndReset()), Convert.ToBase64String(sha1.GetHashAndReset()));
    }
    private static TransferEnvelopeInspection Fail(TransferEnvelopeError error) => new(null, error);
    private static TransferEnvelopeException Error(TransferEnvelopeError error) => new(error);
    private sealed class ReadBudget(long maximum)
    {
        private long actual;
        internal void Add(int count)
        {
            actual = checked(actual + count);
            if (actual > maximum) throw Error(TransferEnvelopeError.PayloadLimit);
        }
    }
    private sealed record Manifest(int EnvelopeVersion, string PackageId, string ContentSchema,
        TransferBlobDescriptor Catalog, ImmutableArray<TransferBlobDescriptor> Payloads, TransferBlobDescriptor? PrivateMetadata);
}
