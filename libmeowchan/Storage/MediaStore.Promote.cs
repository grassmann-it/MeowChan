using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Storage;

internal sealed record PromotedMediaRoot(string Directory, string Generation, int Bindings);

public sealed partial class MediaStore
{
    // Promote never rewrites a root: it builds a new one holding the alternative's bytes (copied from the
    // operation's Full root and verified against the recorded digest). The alternative was observed under
    // its own provider tuple, so its Full binding has a different revision key than the slot's active one;
    // the promote root therefore binds the alternative bytes to the ACTIVE reference and provider as an
    // explicit provider mismatch, and names the bytes it replaces in `Supersedes`.
    internal async Task<PromotedMediaRoot> CreatePromoteRootAsync(string sourceRoot, string targetDirectory,
        string candidateSha256, IReadOnlyCollection<MediaAttachmentBinding> replaced,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceRoot);
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        ArgumentNullException.ThrowIfNull(replaced);
        token.ThrowIfCancellationRequested();
        if (!MediaIntegrityValidation.IsSha256(candidateSha256) || replaced.Count == 0)
            throw new ArgumentException("A promote needs a candidate digest and at least one replaced binding.");
        string target = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("The promote root already exists.");
        ManifestRead source = await ReadManifestAsync(sourceRoot, ManifestFileName, token).ConfigureAwait(false);
        if (source.Corrupt || source.Missing)
            throw new InvalidDataException("The conflict alternative's root is unreadable.");
        MediaAttachmentBinding held = source.Manifest.AttachmentBindings.Values.FirstOrDefault(binding =>
                string.Equals(binding.Actual.Sha256, candidateSha256, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The alternative's root does not hold the promoted bytes.");
        MediaManifestAsset heldAsset = source.Manifest.Assets.GetValueOrDefault(held.AssetId)
            ?? throw new InvalidDataException("The alternative's asset differs from its binding.");
        MediaManifestEntry heldEntry = heldAsset.Entry;
        if (!string.Equals(heldEntry.Sha256, candidateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The alternative's asset differs from its binding.");

        MediaManifest manifest = new()
        {
            Version = RepresentationManifestVersion,
            OriginalEvidence = Array.Empty<MediaOriginalEvidence>(),
            DerivedRepresentations = Array.Empty<MediaDerivedRepresentation>(),
            Supersedes = replaced.OrderBy(item => item.Reference.RevisionKey, StringComparer.Ordinal)
                .Select(item => new MediaSupersession(item.Reference.RevisionKey, item.Actual.Sha256)).ToArray()
        };
        foreach (MediaAttachmentBinding active in replaced.OrderBy(item => item.Reference.RevisionKey, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string key = active.Reference.RevisionKey;
            string requested = key[7..] + ".original.bin";
            MediaManifestEntry entry = new()
            {
                MediaIdentifier = active.Provider.MediaId, Variant = "original", SourceUrl = active.Provider.Url,
                OriginalFileName = active.Provider.OriginalFileName, ExpectedMd5 = active.Provider.ExpectedMd5,
                FileName = heldEntry.FileName, Length = heldEntry.Length,
                Sha256 = heldEntry.Sha256, Md5 = heldEntry.Md5, Sha1 = heldEntry.Sha1
            };
            var assessment = ProviderIntegrityAssessment.Assess(active.Provider, held.Actual);
            // The bytes' provenance is the import that carried them; an unattributed passive import need not
            // agree with the slot's provider expectations, so the original import record is reused as-is.
            MediaPassiveImportRecord import = held.PassiveImport is { } carried
                ? new(carried.OperationId, carried.PackageFingerprint, carried.ImportedAtUtc, null, null)
                : throw new InvalidDataException("Only passively imported alternatives can be promoted.");
            MediaAttachmentBinding binding = new(active.Reference, active.AcquisitionRevisionKey, GetAssetId(requested, entry),
                active.Provider, held.Actual, new(MediaAcceptanceKind.PassiveImport, assessment.SizeMismatch,
                    assessment.Md5Mismatch, import.ImportedAtUtc)) { PassiveImport = import };
            if (!manifest.Entries.TryAdd(requested, entry) || !manifest.AttachmentBindings.TryAdd(key, binding))
                throw new InvalidDataException("A promote names one attachment twice.");
            AddAsset(manifest, requested, entry);
        }
        ValidateManifestBindings(manifest);
        ValidateManifestRepresentations(manifest, token);
        ValidateManifestSupersession(manifest);
        HashSet<string> files = new(StringComparer.Ordinal) { heldEntry.FileName };

        string part = target + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(part);
            foreach (string name in files)
            {
                await CopyVerifiedAsync(Path.Combine(sourceRoot, name), Path.Combine(part, name),
                    heldEntry.Length, candidateSha256, token).ConfigureAwait(false);
            }
            await SaveManifestAsync(part, new ManifestRead(manifest, false, false, null, RepresentationManifestVersion), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(part, target);
        }
        catch
        {
            try { if (Directory.Exists(part)) Directory.Delete(part, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        byte[] bytes = await ReadManifestBytesAsync(Path.Combine(target, ManifestFileName), token).ConfigureAwait(false);
        return new(target, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), manifest.AttachmentBindings.Count);
    }

    private static async Task CopyVerifiedAsync(string source, string destination, long length, string sha256, CancellationToken token)
    {
        FileInfo info = new(source);
        if (!info.Exists || info.LinkTarget != null || info.Length != length)
            throw new InvalidDataException("The alternative's payload is missing or has an unexpected length.");
        using SHA256 hash = SHA256.Create();
        await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                hash.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (total != length || !string.Equals(Convert.ToHexString(hash.Hash!), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The alternative's payload does not match its recorded digest.");
        }
    }
}
