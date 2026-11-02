using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Transfer;

namespace MeowChan.Persistence;

// Scalar co-published evidence only. Not a receipt, consent or Apply capability.
internal sealed record PrivateStoreOperationMarker(Guid OperationId, string ContentPlanSha256,
    NativePrivateApplyStoreKind StoreKind)
{
    internal static void Validate(PrivateStoreOperationMarker marker, NativePrivateApplyStoreKind expected)
    {
        if (marker is null || marker.OperationId == Guid.Empty || !Enum.IsDefined(expected)
            || marker.StoreKind != expected || !(marker.ContentPlanSha256 is { Length: 64 } hash
                && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw PrivateStoreBatchFiles.Invalid();
    }

    internal static PrivateStoreOperationMarker Parse(JsonElement element, NativePrivateApplyStoreKind expected)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object) throw PrivateStoreBatchFiles.Invalid();
            string[] names = ["OperationId", "ContentPlanSha256", "StoreKind"];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty field in element.EnumerateObject())
                if (!names.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name))
                    throw PrivateStoreBatchFiles.Invalid();
            if (seen.Count != names.Length) throw PrivateStoreBatchFiles.Invalid();
            var marker = new PrivateStoreOperationMarker(element.GetProperty("OperationId").GetGuid(),
                element.GetProperty("ContentPlanSha256").GetString()!,
                (NativePrivateApplyStoreKind)element.GetProperty("StoreKind").GetInt32());
            Validate(marker, expected);
            return marker;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException
            or FormatException or OverflowException or KeyNotFoundException)
        { throw PrivateStoreBatchFiles.Invalid(); }
    }
}

internal enum PrivateStoreBatchInspection { ExactBefore, ExactAfterWithMarker, NoChange, Drifted, Unreadable }

// Contains a transient full after-image. NEVER serialize this object into a
// journal/candidate/log: it can contain unselected target values and old notes.
internal sealed class PrivateStoreBatch
{
    private readonly object _owner;
    private readonly byte[] _afterBytes;
    internal PrivateStoreBatch(object owner, PrivateStoreOperationMarker marker,
        NativePrivateApplyStoreFence fence, byte[]? afterBytes, int additions)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(fence);
        PrivateStoreOperationMarker.Validate(marker, fence.Kind);
        if (additions < 0 || (additions > 0) != (afterBytes is { Length: > 0 }))
            throw PrivateStoreBatchFiles.Invalid();
        _owner = owner; Marker = marker; Fence = fence; Additions = additions;
        _afterBytes = afterBytes?.ToArray() ?? [];
    }
    internal PrivateStoreOperationMarker Marker { get; }
    internal NativePrivateApplyStoreFence Fence { get; }
    internal int Additions { get; }
    internal bool HasChanges => Additions > 0;
    internal bool IsOwnedBy(object owner) => ReferenceEquals(_owner, owner);
    internal byte[] CopyAfterBytes() => _afterBytes.ToArray();
}

internal static class PrivateStoreBatchFiles
{
    internal static NativePrivateApplyStoreImage Missing() => new(false, 0, null, 0, 0, null);
    internal static NativePrivateApplyStoreImage Image(byte[] bytes, int schema, Guid? instance, long revision)
        => new(true, schema, instance, revision, bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    internal static bool Same(NativePrivateApplyStoreImage a, NativePrivateApplyStoreImage b)
        => a.Exists == b.Exists && a.Schema == b.Schema && a.Instance == b.Instance
            && a.Revision == b.Revision && a.ByteLength == b.ByteLength
            && string.Equals(a.Sha256, b.Sha256, StringComparison.Ordinal);
    internal static void EnsureUnmarked(PrivateStoreOperationMarker? marker)
    {
        if (marker is not null) throw new InvalidDataException("Private store recovery is required before mutation.");
    }
    internal static byte[]? ReadBytes(string path, int maximumBytes)
    {
        try
        {
            using FileStream file = RegularProfileFile.OpenRead(path);
            long length = file.Length;
            if (length < 0 || length > maximumBytes) throw Invalid();
            byte[] bytes = new byte[checked((int)length)];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1 || file.Length != length) throw Invalid();
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (InvalidDataException) { throw; }
        catch (IOException) { throw new IOException("The private store document could not be read."); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("The private store document could not be read."); }
    }
    internal static byte[] SerializeBounded<T>(T value, int maximumBytes)
    {
        using var memory = new BoundedMemoryStream(maximumBytes);
        JsonSerializer.Serialize(memory, value);
        return memory.ToArray();
    }

    private sealed class BoundedMemoryStream(int maximumBytes) : MemoryStream
    {
        private void Check(int count)
        {
            if (count < 0 || Position > maximumBytes - (long)count) throw Invalid();
        }
        public override void Write(byte[] buffer, int offset, int count)
        { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value)
        { Check(1); base.WriteByte(value); }
        public override void SetLength(long value)
        { if (value < 0 || value > maximumBytes) throw Invalid(); base.SetLength(value); }
    }
    internal static InvalidDataException Invalid() => new("The private store batch is invalid.");
}
