using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Transfer;

namespace MeowChan.Storage;

public sealed partial class MediaStore
{
    private const int RepresentationManifestVersion = 6;
    private static readonly Encoding RepresentationUtf8 = new UTF8Encoding(false, true);
    private const int MaximumRepresentationItems = 100_000;
    private const long MaximumDerivedFileBytes = 1024L * 1024 * 1024;

    // This inventory describes passive incoming observations. Neither an incoming Contained
    // status nor a verified derived file creates an original Asset/Binding or source authority.
    private static void ValidateManifestRepresentations(MediaManifest manifest, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (manifest.OriginalEvidence == null && manifest.DerivedRepresentations == null) return;
        if (manifest.OriginalEvidence == null || manifest.DerivedRepresentations == null
            || (long)manifest.OriginalEvidence.Length + manifest.DerivedRepresentations.Length > MaximumRepresentationItems)
            throw InvalidRepresentations();
        Dictionary<MediaOccurrence, MediaOriginalEvidence> parents = new();
        HashSet<string> revisionKeys = new(StringComparer.Ordinal);
        foreach (MediaOriginalEvidence evidence in manifest.OriginalEvidence)
        {
            ct.ThrowIfCancellationRequested();
            if (evidence == null || evidence.Occurrence == null || evidence.Metadata == null
                || !Enum.IsDefined(evidence.IncomingStatus)) throw InvalidRepresentations();
            ValidateOccurrence(evidence.Occurrence, evidence.Metadata);
            ValidateObservation(evidence.Origin);
            if (!parents.TryAdd(evidence.Occurrence, evidence)
                || evidence.Occurrence.OriginalRevisionKey is { } key && !revisionKeys.Add(key))
                throw InvalidRepresentations();
            if (evidence.OriginalByteVersion is { } version)
            {
                if (evidence.Occurrence.OriginalRevisionKey == null) throw InvalidRepresentations();
                ValidateRepresentationActual(version, measured: false);
            }
            if (evidence.Occurrence.OriginalRevisionKey is { } boundKey
                && manifest.AttachmentBindings.TryGetValue(boundKey, out MediaAttachmentBinding? binding))
                ValidateEvidenceBinding(evidence, binding.Reference, binding.Provider, binding.Actual);
        }
        HashSet<(MediaOccurrence, MediaDerivedKind)> members = new();
        Dictionary<string, MediaActualIntegrity> files = new(StringComparer.Ordinal);
        // Reserve original names across platforms, even if the current filesystem is case-sensitive.
        HashSet<string> originalNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (MediaManifestAsset? asset in manifest.Assets.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (asset?.Entry?.FileName == null) throw InvalidRepresentations();
            originalNames.Add(asset.Entry.FileName);
        }
        foreach (MediaDerivedRepresentation derived in manifest.DerivedRepresentations)
        {
            ct.ThrowIfCancellationRequested();
            if (derived == null || derived.Parent == null || !Enum.IsDefined(derived.Kind)
                || !Enum.IsDefined(derived.IncomingStatus) || !parents.TryGetValue(derived.Parent, out MediaOriginalEvidence? parent)
                || parent.Occurrence.OriginalRevisionKey == null || parent.OriginalByteVersion == null
                || derived.SourceVersion != parent.OriginalByteVersion || derived.Origin != parent.Origin
                || !members.Add((derived.Parent, derived.Kind))) throw InvalidRepresentations();
            if (derived.IncomingStatus != MediaDerivedStatus.Contained)
            {
                if (derived.Blob != null) throw InvalidRepresentations();
                continue;
            }
            if (derived.Blob is not { Actual: { } actual } blob) throw InvalidRepresentations();
            ValidateRepresentationActual(actual, measured: true);
            if (actual.Length > MaximumDerivedFileBytes || blob.RelativeFileName != actual.Sha256 + ".derived.bin"
                || originalNames.Contains(blob.RelativeFileName)) throw InvalidRepresentations();
            if (files.TryGetValue(blob.RelativeFileName, out MediaActualIntegrity? previous) && previous != actual)
                throw InvalidRepresentations();
            files[blob.RelativeFileName] = actual;
        }
    }

    private static void ValidateOccurrence(MediaOccurrence occurrence, CapturedAttachment metadata)
    {
        if (occurrence.Resource == null || occurrence.Position < 0 || metadata.Position != occurrence.Position
            || metadata.PlannedPath != null || metadata.Variant != "original"
            || metadata.AttachmentRevisionKey != occurrence.OriginalRevisionKey
            || metadata.ExpectedSize < 0 || metadata.MediaId == null && !metadata.IsDeleted)
            throw InvalidRepresentations();
        ValidateRepresentationIdentifier(occurrence.Resource.Source);
        ValidateRepresentationIdentifier(occurrence.Resource.Board);
        ValidateRepresentationIdentifier(occurrence.Resource.Thread);
        ValidateRepresentationIdentifier(occurrence.PostId);
        if (metadata.MediaId != null) ValidateRepresentationIdentifier(metadata.MediaId);
        if (metadata.Url != null && !Uri.TryCreate(metadata.Url, UriKind.Absolute, out _)) throw InvalidRepresentations();
        if (metadata.OriginalFileName != null && !ValidRepresentationFileName(metadata.OriginalFileName)) throw InvalidRepresentations();
        if (metadata.ExpectedMd5 != null && !ValidExpectedRepresentationDigest(metadata.ExpectedMd5, 16)
            || metadata.ExpectedSha1 != null && !ValidExpectedRepresentationDigest(metadata.ExpectedSha1, 20)
            || metadata.ExpectedSha256 != null && !MediaIntegrityValidation.IsSha256(metadata.ExpectedSha256))
            throw InvalidRepresentations();
        MediaIntegrityValidation.ValidateSizeHint(metadata.ProviderSizeHint);
        if (occurrence.OriginalRevisionKey is { } key && (metadata.MediaId == null || metadata.Url == null
            || key != MediaRevisionKeys.CreateAttachmentKey(occurrence.Resource, occurrence.PostId,
                occurrence.Position, RepresentationProvider(metadata)))) throw InvalidRepresentations();
    }

    private static MediaProviderMetadata RepresentationProvider(CapturedAttachment metadata)
        => new(metadata.MediaId!, metadata.Variant, metadata.Url!, metadata.OriginalFileName,
            metadata.ExpectedSize, metadata.ExpectedMd5, metadata.ExpectedSha256)
        { ExpectedSha1 = metadata.ExpectedSha1, ProviderSizeHint = metadata.ProviderSizeHint };

    private static void ValidateEvidenceBinding(MediaOriginalEvidence evidence, AttachmentRevisionReference reference,
        MediaProviderMetadata provider, MediaActualIntegrity actual, bool measured = false)
    {
        MediaOccurrence occurrence = evidence.Occurrence;
        if (reference.Resource != occurrence.Resource || reference.PostId != occurrence.PostId
            || reference.Position != occurrence.Position || reference.RevisionKey != occurrence.OriginalRevisionKey
            || provider != RepresentationProvider(evidence.Metadata)
            || evidence.OriginalByteVersion is { } recorded && !(measured
                ? MediaIntegrityValidation.MatchesMeasured(recorded, actual)
                : MediaIntegrityValidation.CompatibleEvidence(recorded, actual)))
            throw InvalidRepresentations();
    }

    private static void ValidateOriginalPublicationPath(MediaManifest manifest, string path)
    {
        // Reserve descriptors, not just existing files: a missing derived payload
        // must not be filled by a new Original or borrowed for one with equal bytes.
        // Enforce the same portable case-insensitive alias rule before the file move.
        string name = Path.GetFileName(path);
        if (manifest.DerivedRepresentations?.Any(member => member.Blob is { } blob
            && string.Equals(blob.RelativeFileName, name, StringComparison.OrdinalIgnoreCase)) == true)
            throw new InvalidDataException("An original asset cannot alias a derived representation file.");
    }

    private static void ValidateObservation(MediaPassiveObservation origin)
    {
        if (origin == null || !Guid.TryParseExact(origin.OperationId, "D", out Guid id) || id == Guid.Empty
            || id.ToString("D") != origin.OperationId || !IsLowerCandidateHash(origin.PackageFingerprint)
            || origin.ImportedAtUtc.UtcDateTime == DateTime.MinValue) throw InvalidRepresentations();
    }

    private static void ValidateRepresentationActual(MediaActualIntegrity actual, bool measured)
    {
        if (actual == null || actual.Length < 0 || !IsLowerCandidateHash(actual.Sha256)
            || !CanonicalRepresentationDigest(actual.Md5, 16)
            || (measured || actual.Sha1 != null) && !CanonicalRepresentationDigest(actual.Sha1, 20))
            throw InvalidRepresentations();
    }

    private static bool ValidExpectedRepresentationDigest(string value, int length)
    {
        // Provider spellings are retained exactly. The original catalog/provider contracts
        // accept base64 whitespace; only Actual/source-version tuples must be canonical.
        Span<byte> decoded = stackalloc byte[20];
        return Convert.TryFromBase64String(value, decoded, out int count) && count == length;
    }

    private static bool CanonicalRepresentationDigest(string? value, int length)
    {
        if (value == null || value.Length != ((length + 2) / 3) * 4) return false;
        Span<byte> decoded = stackalloc byte[20];
        return Convert.TryFromBase64String(value, decoded, out int count) && count == length
            && Convert.ToBase64String(decoded[..count]) == value;
    }

    private static void ValidateRepresentationIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || RepresentationControls(value))
            throw InvalidRepresentations();
    }

    private static bool RepresentationControls(string value)
    {
        try { _ = RepresentationUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { return true; }
        return value.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format);
    }

    private static bool ValidRepresentationFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value is "." or ".."
            || value.IndexOfAny(['/', '\\', ':']) >= 0 || RepresentationControls(value)) return false;
        string device = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$")
            && !(device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal))
                && device[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

    private static InvalidDataException InvalidRepresentations()
        => new("The passive representation inventory is invalid.");

    internal static async Task<Stream> OpenVerifiedDerivedFileAsync(string directory,
        MediaDerivedRepresentation member, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (member.IncomingStatus != MediaDerivedStatus.Contained || member.Blob is not { Actual: { } actual } blob)
            throw new InvalidDataException("This representation has no locally contained bytes.");
        ValidateRepresentationActual(actual, measured: true);
        if (actual.Length > MaximumDerivedFileBytes || blob.RelativeFileName != actual.Sha256 + ".derived.bin")
            throw InvalidRepresentations();
        string path = Path.Combine(directory, blob.RelativeFileName);
        ArchiveExportMetadataFile.RejectLinks(directory);
        ValidateCandidateRegularFile(path);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != actual.Length) throw new InvalidDataException("The derived file changed length.");
            using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            NativeTransferDerivedPayloadValidator.PngStructure? png = member.Kind == MediaDerivedKind.VideoPosterPng
                ? new(actual.Length) : null;
            byte[] buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                total = checked(total + count);
                if (total > actual.Length) throw new InvalidDataException("The derived file changed length.");
                sha256.AppendData(buffer, 0, count); md5.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count);
                png?.Append(buffer.AsSpan(0, count), ct);
            }
            if (total != actual.Length || Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant() != actual.Sha256
                || Convert.ToBase64String(md5.GetHashAndReset()) != actual.Md5
                || Convert.ToBase64String(sha1.GetHashAndReset()) != actual.Sha1 || png != null && !png.IsValid)
                throw new InvalidDataException("The derived file does not match its recorded integrity or structure.");
            ct.ThrowIfCancellationRequested(); stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
