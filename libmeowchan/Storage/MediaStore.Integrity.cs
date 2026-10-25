using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Downloader;

namespace MeowChan.Storage;

public sealed partial class MediaStore
{
    private static void ValidateUniqueJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate manifest property or binding key.");
                ValidateUniqueJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) ValidateUniqueJson(item);
    }

    private async Task<(MediaCommitResult? Result, bool Ambiguous)> TryRestoreStrictAsync(MediaManifest manifest,
        DownloadItem item, string destination, string directory, byte[]? expectedMd5, CancellationToken token)
    {
        bool hasExpected = item.ExpectedSize != null || item.ExpectedMd5 != null || item.ExpectedSha256 != null || item.ExpectedSha1 != null;
        MediaManifestEntry[] candidates = manifest.Assets.Values.Where(asset => asset.RequestedFileName == Path.GetFileName(item.DestinationPath))
            .Select(asset => asset.Entry).Where(entry => EntryMatchesItem(entry, item)
                && (item.ExpectedSize == null || item.ExpectedSize == entry.Length)
                && (item.ExpectedSha256 == null || string.Equals(item.ExpectedSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                && (item.ExpectedMd5 == null || string.IsNullOrEmpty(entry.Md5) || SameBase64Digest(item.ExpectedMd5, entry.Md5))
                && (item.ExpectedSha1 == null || entry.Sha1 == null || SameBase64Digest(item.ExpectedSha1, entry.Sha1))
                && (hasExpected || entry.SourceUrl == item.DownloadUri.AbsoluteUri)).ToArray();
        // History can retain an earlier path that was subsequently damaged and repaired elsewhere.
        // Count actual verified copies, not stale manifest claims; never select latest/current.
        bool conflictingClaims = candidates.GroupBy(entry => entry.FileName,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Any(group => group.Select(entry => (entry.Length, Sha: entry.Sha256.ToLowerInvariant(), entry.Md5)).Distinct().Skip(1).Any());
        MediaCommitResult? result = null;
        int verified = 0;
        foreach (MediaManifestEntry entry in candidates)
        {
            if (!TryGetEntryPath(directory, entry.FileName, out string? path)) continue;
            FileDigest? digest = await VerifyManifestFileAsync(item, destination, path!, entry, expectedMd5, token).ConfigureAwait(false);
            if (digest == null) continue;
            verified++;
            result = ToResult(item, path!, digest, true);
        }
        if (conflictingClaims || verified > 1) return (null, true);
        return (result, false);
    }

    private static bool SameBase64Digest(string first, string second)
    {
        try { return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(first), Convert.FromBase64String(second)); }
        catch (FormatException) { return false; }
    }

    private static MediaActualIntegrity Actual(FileDigest digest) => new(digest.Length, Convert.ToBase64String(digest.Md5), digest.Sha256) { Sha1 = Convert.ToBase64String(digest.Sha1) };

    private static void ValidateManifestBindings(MediaManifest manifest)
    {
        if (manifest.AttachmentBindings == null) throw new InvalidDataException("Missing attachment binding inventory.");
        foreach (var pair in manifest.AttachmentBindings)
        {
            MediaIntegrityValidation.ValidateBinding(pair.Key, pair.Value);
            MediaAttachmentBinding binding = pair.Value;
            if (!manifest.Assets.TryGetValue(binding.AssetId, out MediaManifestAsset? asset) || asset?.Entry == null
                || binding.AssetId != GetAssetId(asset.RequestedFileName, asset.Entry)
                || asset.Entry.MediaIdentifier != binding.Provider.MediaId || asset.Entry.Variant != binding.Provider.Variant
                || asset.Entry.Length != binding.Actual.Length || asset.Entry.Md5 != binding.Actual.Md5
                || binding.Actual.Sha1 != null && (asset.Entry.Sha1 == null || !SameBase64Digest(binding.Actual.Sha1, asset.Entry.Sha1))
                || !string.Equals(asset.Entry.Sha256, binding.Actual.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An attachment binding disagrees with its immutable asset.");
        }
    }

    private static void ValidateBindingCollisions(MediaManifest manifest, DownloadItem item, string assetId, FileDigest digest)
    {
        MediaProviderMetadata provider = MediaIntegrityValidation.Provider(item);
        MediaActualIntegrity actual = Actual(digest);
        foreach (AttachmentRevisionReference reference in item.AttachmentReferences)
            if (manifest.AttachmentBindings.TryGetValue(reference.RevisionKey, out MediaAttachmentBinding? previous)
                && (previous.Reference != reference || previous.Provider != provider || !MediaIntegrityValidation.MatchesMeasured(previous.Actual, actual) || previous.AssetId != assetId))
                throw new InvalidDataException("An immutable attachment revision is already bound to different bytes or another asset.");
    }

    private static void AddBindings(MediaManifest manifest, DownloadItem item, string assetId, FileDigest digest,
        SourceMediaIntegrityPolicySnapshot? policy)
    {
        ValidateBindingCollisions(manifest, item, assetId, digest);
        MediaProviderMetadata provider = MediaIntegrityValidation.Provider(item);
        MediaActualIntegrity actual = Actual(digest);
        ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(provider, actual);
        if (assessment.Sha1Mismatch || assessment.Sha256Mismatch || !assessment.IsStrictMatch && policy == null)
            throw new InvalidDataException("A new binding needs current authorization for a provider discrepancy.");
        // New references may reuse a v3 asset. Enrich only missing evidence after
        // checking its immutable bytes; otherwise the new binding would name SHA-1
        // that the old asset inventory cannot corroborate on the next read.
        if (manifest.Assets.TryGetValue(assetId, out MediaManifestAsset? existing))
        {
            MediaManifestEntry entry = existing.Entry;
            if (!MediaIntegrityValidation.MatchesMeasured(new(entry.Length, entry.Md5, entry.Sha256) { Sha1 = entry.Sha1 }, actual))
                throw new InvalidDataException("Measured bytes disagree with the immutable asset evidence.");
            entry.Sha1 ??= actual.Sha1;
        }
        MediaAcceptanceRecord acceptance = new(assessment.IsStrictMatch ? MediaAcceptanceKind.Strict : MediaAcceptanceKind.ExplicitProviderMismatch,
            assessment.SizeMismatch, assessment.Md5Mismatch, DateTimeOffset.UtcNow, policy?.ProfileId, policy?.PluginOwnerId, policy?.SourceId, policy?.Revision);
        foreach (AttachmentRevisionReference reference in item.AttachmentReferences)
        {
            if (manifest.AttachmentBindings.ContainsKey(reference.RevisionKey)) continue;
            MediaAttachmentBinding binding = new(reference, MediaIntegrityValidation.Acquisition(item), assetId, provider, actual, acceptance);
            MediaIntegrityValidation.ValidateBinding(reference.RevisionKey, binding);
            manifest.AttachmentBindings.Add(reference.RevisionKey, binding);
        }
    }

    private static string? FindBoundAssetId(MediaManifest manifest, DownloadItem item) => item.AttachmentReferences
        .Select(reference => manifest.AttachmentBindings.GetValueOrDefault(reference.RevisionKey)?.AssetId).FirstOrDefault(id => id != null);

    private static string? BoundPathForCommit(MediaManifest manifest, DownloadItem item, FileDigest digest, string directory)
    {
        string? assetId = null;
        foreach (AttachmentRevisionReference reference in item.AttachmentReferences)
        {
            if (!manifest.AttachmentBindings.TryGetValue(reference.RevisionKey, out MediaAttachmentBinding? binding)) continue;
            if (binding.Provider != MediaIntegrityValidation.Provider(item) || !MediaIntegrityValidation.MatchesMeasured(binding.Actual, Actual(digest))
                || assetId != null && assetId != binding.AssetId)
                throw new InvalidDataException("An immutable attachment revision cannot be rebound.");
            assetId = binding.AssetId;
        }
        if (assetId == null) return null;
        MediaManifestAsset asset = manifest.Assets[assetId];
        if (!TryGetEntryPath(directory, asset.Entry.FileName, out string? path))
            throw new InvalidDataException("The bound asset cannot be safely restored at this target.");
        return path;
    }

    private async Task<MediaCommitResult?> TryRestoreBoundAsync(MediaManifest manifest, DownloadItem item, string directory, CancellationToken token)
    {
        if (item.AttachmentReferences.Count == 0) return null;
        MediaAttachmentBinding? first = null;
        foreach (AttachmentRevisionReference reference in item.AttachmentReferences)
        {
            if (!manifest.AttachmentBindings.TryGetValue(reference.RevisionKey, out MediaAttachmentBinding? binding)) return null;
            if (binding.Reference != reference || binding.Provider != MediaIntegrityValidation.Provider(item)
                || first != null && (first.AssetId != binding.AssetId || !MediaIntegrityValidation.CompatibleEvidence(first.Actual, binding.Actual)))
                throw new InvalidDataException("The saved attachment bindings disagree.");
            // Retain the strongest available evidence across mixed v3/v4 references.
            // A later legacy binding without SHA-1 must not downgrade a known digest.
            if (first == null || first.Actual.Sha1 == null && binding.Actual.Sha1 != null) first = binding;
        }
        if (first == null) return null;
        MediaManifestAsset asset = manifest.Assets[first.AssetId];
        if (!TryGetEntryPath(directory, asset.Entry.FileName, out string? path)) throw new InvalidDataException("Invalid bound asset path.");
        FileDigest? digest = await TryHashFileAsync(path!, token, first.Actual.Length).ConfigureAwait(false);
        if (digest == null || !MediaIntegrityValidation.MatchesMeasured(first.Actual, Actual(digest))) return null;
        return new MediaCommitResult(path!, digest.Length, digest.Sha256, true) {
            AssetId = first.AssetId, AcquisitionRevisionKey = item.AcquisitionRevisionKey,
            AttachmentRevisionKeys = Array.AsReadOnly(item.AttachmentReferences.Select(reference => reference.RevisionKey).ToArray()) };
    }
}
