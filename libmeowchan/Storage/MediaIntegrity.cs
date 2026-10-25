using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Downloader;

namespace MeowChan.Storage;

public sealed record MediaProviderMetadata(string MediaId, string Variant, string Url, string? OriginalFileName,
    long? ExpectedSize, string? ExpectedMd5, string? ExpectedSha256)
{
    public string? ExpectedSha1 { get; init; }
    public MediaProviderSizeHint? ProviderSizeHint { get; init; }
}
public enum MediaSizePrecision { Unspecified = 0, Rounded = 1, Truncated = 2 }
/// <summary>Provider annotation only. Local byte limits and exact ExpectedSize never use this hint.</summary>
public sealed record MediaProviderSizeHint(string RawValue, string UnitLabel,
    MediaSizePrecision Precision = MediaSizePrecision.Unspecified);
public sealed record MediaActualIntegrity(long Length, string Md5, string Sha256)
{
    /// <summary>Optional for historical evidence; required when the provider supplied SHA-1.</summary>
    public string? Sha1 { get; init; }
}
public sealed record ProviderIntegrityAssessment(bool SizeMismatch, bool Md5Mismatch, bool Sha256Mismatch)
{
    public bool Sha1Mismatch { get; init; }
    public bool IsStrictMatch => !SizeMismatch && !Md5Mismatch && !Sha256Mismatch && !Sha1Mismatch;
    public static ProviderIntegrityAssessment Assess(MediaProviderMetadata provider, MediaActualIntegrity actual)
    {
        MediaIntegrityValidation.ValidateProvider(provider);
        MediaIntegrityValidation.ValidateActual(actual);
        return new(provider.ExpectedSize is long size && size != actual.Length,
            provider.ExpectedMd5 != null && !CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(provider.ExpectedMd5), Convert.FromBase64String(actual.Md5)),
            provider.ExpectedSha256 != null && !string.Equals(provider.ExpectedSha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
        { Sha1Mismatch = provider.ExpectedSha1 != null && (actual.Sha1 == null || !SameSha1(provider.ExpectedSha1, actual.Sha1)) };
    }
    private static bool SameSha1(string first, string second) => CryptographicOperations.FixedTimeEquals(
        Convert.FromBase64String(first), Convert.FromBase64String(second));
}
public sealed record AttachmentRevisionReference(ResourceId Resource, string PostId, int Position, string RevisionKey);
public sealed record SourceMediaIntegrityPolicySnapshot(string ProfileId, string PluginOwnerId, string SourceId,
    bool AllowProviderSizeMd5Mismatch, long Revision);
public interface ISourceMediaIntegrityPolicy
{
    SourceMediaIntegrityPolicySnapshot GetSnapshot();
    ValueTask<MediaAcceptanceLease> AcquireAcceptanceLeaseAsync(long expectedRevision, CancellationToken cancellationToken);
}
public interface ISourceMediaIntegrityPolicyProvider
{
    ISourceMediaIntegrityPolicy? MediaIntegrityPolicy { get; }
}
public abstract class MediaAcceptanceLease : IAsyncDisposable
{
    protected MediaAcceptanceLease(SourceMediaIntegrityPolicySnapshot snapshot) => Snapshot = snapshot;
    public SourceMediaIntegrityPolicySnapshot Snapshot { get; }
    public abstract ValueTask DisposeAsync();
}
public enum MediaAcceptanceKind { Strict, ExplicitProviderMismatch }
public sealed record MediaAcceptanceRecord(MediaAcceptanceKind Kind, bool SizeMismatch, bool Md5Mismatch,
    DateTimeOffset AcceptedAtUtc, string? ProfileId = null, string? PluginOwnerId = null, string? SourceId = null,
    long? PolicyRevision = null);
public sealed record MediaAttachmentBinding(AttachmentRevisionReference Reference, string AcquisitionRevisionKey,
    string AssetId, MediaProviderMetadata Provider, MediaActualIntegrity Actual, MediaAcceptanceRecord Acceptance);

/// <summary>V1 implies unknown provider metadata semantics. Null differs from empty; every UTF-8 component
/// has a signed big-endian Int32 byte length (-1 for null). Numeric fields use invariant decimal text.</summary>
public static class MediaRevisionKeys
{
    private static readonly Encoding KeyEncoding = new UTF8Encoding(false, true);
    public static string CreateAcquisitionKey(ResourceId resource, MediaProviderMetadata provider)
        => Hash(Extended(provider) ? "acq-v2:" : "acq-v1:", resource, provider, null, null);
    public static string CreateAttachmentKey(ResourceId resource, string postId, int position, MediaProviderMetadata provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        return Hash(Extended(provider) ? "att-v2:" : "att-v1:", resource, provider, postId, position);
    }
    private static bool Extended(MediaProviderMetadata provider) => provider.ExpectedSha1 != null || provider.ProviderSizeHint != null;
    private static string Hash(string prefix, ResourceId resource, MediaProviderMetadata provider, string? post, int? position)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(provider);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            byte[]? bytes = value == null ? null : KeyEncoding.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
            hash.AppendData(length);
            if (bytes != null) hash.AppendData(bytes);
        }
        Add(prefix); Add(resource.Source); Add(resource.Board); Add(resource.Thread);
        Add(provider.MediaId); Add(provider.Variant); Add(provider.Url); Add(provider.OriginalFileName);
        Add(provider.ExpectedSize?.ToString(CultureInfo.InvariantCulture)); Add(provider.ExpectedMd5); Add(provider.ExpectedSha256);
        if (Extended(provider))
        {
            Add(provider.ExpectedSha1);
            Add(provider.ProviderSizeHint?.RawValue); Add(provider.ProviderSizeHint?.UnitLabel);
            Add(provider.ProviderSizeHint == null ? null : ((int)provider.ProviderSizeHint.Precision).ToString(CultureInfo.InvariantCulture));
        }
        if (position.HasValue) { Add(post); Add(position.Value.ToString(CultureInfo.InvariantCulture)); }
        return prefix + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

internal static class MediaIntegrityValidation
{
    internal static MediaProviderMetadata Provider(DownloadItem item) => new(item.MediaIdentifier, item.Variant,
        item.DownloadUri.AbsoluteUri, item.OriginalFileName, item.ExpectedSize, item.ExpectedMd5, item.ExpectedSha256)
        { ExpectedSha1 = item.ExpectedSha1, ProviderSizeHint = item.ProviderSizeHint };
    internal static ResourceId Resource(DownloadItem item) => new(item.ImageBoard.Name, item.Thread.Board.BoardCode, item.Thread.ThreadId);
    internal static string Acquisition(DownloadItem item) => MediaRevisionKeys.CreateAcquisitionKey(Resource(item), Provider(item));
    internal static void ValidateProvider(MediaProviderMetadata provider)
    {
        if (provider.ExpectedSize < 0 || string.IsNullOrWhiteSpace(provider.MediaId) || string.IsNullOrWhiteSpace(provider.Variant)
            || !Uri.TryCreate(provider.Url, UriKind.Absolute, out _)) throw new InvalidDataException("Invalid provider media metadata.");
        if (provider.ExpectedMd5 != null) ValidateMd5(provider.ExpectedMd5);
        ValidateSha1(provider.ExpectedSha1); ValidateSizeHint(provider.ProviderSizeHint);
        if (provider.ExpectedSha256 != null && !IsSha256(provider.ExpectedSha256)) throw new InvalidDataException("Invalid provider SHA256.");
    }
    internal static void ValidateActual(MediaActualIntegrity actual)
    {
        if (actual.Length < 0 || !IsSha256(actual.Sha256)) throw new InvalidDataException("Invalid measured media integrity.");
        ValidateMd5(actual.Md5); ValidateSha1(actual.Sha1);
    }
    // Historical evidence can omit SHA-1, but a recorded SHA-1 must agree with freshly measured bytes.
    internal static bool MatchesMeasured(MediaActualIntegrity recorded, MediaActualIntegrity measured)
        => recorded.Length == measured.Length && recorded.Md5 == measured.Md5
            && string.Equals(recorded.Sha256, measured.Sha256, StringComparison.OrdinalIgnoreCase)
            && (recorded.Sha1 == null || measured.Sha1 != null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(recorded.Sha1), Convert.FromBase64String(measured.Sha1)));
    internal static bool CompatibleEvidence(MediaActualIntegrity first, MediaActualIntegrity second)
        => first.Length == second.Length && first.Md5 == second.Md5
            && string.Equals(first.Sha256, second.Sha256, StringComparison.OrdinalIgnoreCase)
            && (first.Sha1 == null || second.Sha1 == null || System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(first.Sha1), Convert.FromBase64String(second.Sha1)));
    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static void ValidateSha1(string? value)
    {
        if (value == null) return;
        Span<byte> bytes = stackalloc byte[20];
        if (!Convert.TryFromBase64String(value, bytes, out int length) || length != 20)
            throw new InvalidDataException("Invalid provider/actual SHA1.");
    }
    internal static void ValidateSizeHint(MediaProviderSizeHint? hint)
    {
        if (hint == null) return;
        if (hint.RawValue is not { Length: > 0 and <= 64 } || hint.UnitLabel is not { Length: > 0 and <= 32 }
            || !decimal.TryParse(hint.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal raw) || raw < 0
            || !Enum.IsDefined(hint.Precision)) throw new InvalidDataException("Invalid provider size hint.");
    }
    private static void ValidateMd5(string value)
    {
        try { if (Convert.FromBase64String(value).Length == 16) return; }
        catch (FormatException) { }
        throw new InvalidDataException("Invalid provider/actual MD5.");
    }
    internal static void ValidateItem(DownloadItem item)
    {
        MediaProviderMetadata provider = Provider(item);
        ValidateProvider(provider);
        string acquisition = Acquisition(item);
        if (item.AcquisitionRevisionKey != null && item.AcquisitionRevisionKey != acquisition)
            throw new InvalidDataException("The acquisition revision differs from its raw metadata.");
        foreach (AttachmentRevisionReference reference in item.AttachmentReferences)
            if (reference.Resource != Resource(item) || reference.RevisionKey != MediaRevisionKeys.CreateAttachmentKey(reference.Resource,
                reference.PostId, reference.Position, provider)) throw new InvalidDataException("Invalid frozen attachment revision.");
    }
    internal static SourceMediaIntegrityPolicySnapshot? AuthorizeAssessment(DownloadItem item, MediaActualIntegrity actual)
    {
        ValidateItem(item);
        if (item.TransportContentLength is long transport && transport != actual.Length
            || item.MaximumActualBytes is long maximum && actual.Length > maximum)
            throw new InvalidDataException("The media body violates its transport length or actual byte limit.");
        ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(Provider(item), actual);
        if (assessment.Sha1Mismatch) throw new InvalidDataException("Provider SHA1 does not match the downloaded bytes.");
        if (assessment.Sha256Mismatch) throw new InvalidDataException("Provider SHA256 does not match the downloaded bytes.");
        if (assessment.IsStrictMatch) return null;
        SourceMediaIntegrityPolicySnapshot? policy = item.MediaIntegrityPolicy?.GetSnapshot();
        if (policy == null || item.TransferPolicySnapshot != null && policy != item.TransferPolicySnapshot || !policy.AllowProviderSizeMd5Mismatch || policy.SourceId != Resource(item).Source
            || item.AttachmentReferences.Count == 0)
            throw new InvalidDataException("Provider size or MD5 differs without current source permission and exact references.");
        return policy;
    }
    internal static void ValidateBinding(string key, MediaAttachmentBinding binding)
    {
        if (binding == null || binding.Reference == null || binding.Provider == null || binding.Actual == null || binding.Acceptance == null)
            throw new InvalidDataException("Incomplete attachment binding.");
        ValidateProvider(binding.Provider); ValidateActual(binding.Actual);
        if (key != binding.Reference.RevisionKey || key != MediaRevisionKeys.CreateAttachmentKey(binding.Reference.Resource,
            binding.Reference.PostId, binding.Reference.Position, binding.Provider)
            || binding.AcquisitionRevisionKey != MediaRevisionKeys.CreateAcquisitionKey(binding.Reference.Resource, binding.Provider)
            || !IsSha256(binding.AssetId)) throw new InvalidDataException("Invalid attachment binding identity.");
        ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(binding.Provider, binding.Actual);
        MediaAcceptanceRecord acceptance = binding.Acceptance;
        if (assessment.Sha1Mismatch || assessment.Sha256Mismatch || assessment.SizeMismatch != acceptance.SizeMismatch || assessment.Md5Mismatch != acceptance.Md5Mismatch
            || !Enum.IsDefined(acceptance.Kind) || acceptance.AcceptedAtUtc == default
            || (assessment.IsStrictMatch ? acceptance.Kind != MediaAcceptanceKind.Strict : acceptance.Kind != MediaAcceptanceKind.ExplicitProviderMismatch)
            || acceptance.Kind == MediaAcceptanceKind.ExplicitProviderMismatch && (string.IsNullOrWhiteSpace(acceptance.ProfileId)
                || string.IsNullOrWhiteSpace(acceptance.PluginOwnerId) || acceptance.SourceId != binding.Reference.Resource.Source || acceptance.PolicyRevision is null or < 0))
            throw new InvalidDataException("Invalid attachment acceptance evidence.");
    }
}

/// <summary>A copied, canonical reference set with value equality across JSON/database roundtrips.</summary>
internal sealed class MediaAttachmentReferences : IReadOnlyList<AttachmentRevisionReference>, IEquatable<MediaAttachmentReferences>
{
    private readonly AttachmentRevisionReference[] _values;
    private MediaAttachmentReferences(AttachmentRevisionReference[] values) => _values = values;
    internal static MediaAttachmentReferences Empty { get; } = new(Array.Empty<AttachmentRevisionReference>());
    internal static MediaAttachmentReferences CopyOf(IEnumerable<AttachmentRevisionReference> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        AttachmentRevisionReference[] copy = values.ToArray();
        if (copy.Length == 0) return Empty;
        if (copy.Any(reference => reference == null || string.IsNullOrWhiteSpace(reference.RevisionKey)))
            throw new InvalidDataException("Invalid attachment reference set.");
        Array.Sort(copy, (first, second) => StringComparer.Ordinal.Compare(first.RevisionKey, second.RevisionKey));
        for (int index = 1; index < copy.Length; index++)
            if (copy[index - 1].RevisionKey == copy[index].RevisionKey)
                throw new InvalidDataException("An attachment reference set contains duplicate revision keys.");
        return new(copy);
    }
    public int Count => _values.Length;
    public AttachmentRevisionReference this[int index] => _values[index];
    public IEnumerator<AttachmentRevisionReference> GetEnumerator() => ((IEnumerable<AttachmentRevisionReference>)_values).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(MediaAttachmentReferences? other) => other != null && _values.SequenceEqual(other._values);
    public override bool Equals(object? other) => other is MediaAttachmentReferences references && Equals(references);
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (AttachmentRevisionReference reference in _values) hash.Add(reference);
        return hash.ToHashCode();
    }
}
