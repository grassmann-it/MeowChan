using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Transfer;
using MeowChan.Persistence;

namespace MeowChan.Storage;

public sealed partial class MediaStore
{
    private const int MaximumPassiveMergeTargetRoots = 32;
    private const int MaximumPassiveMergeItems = 100_000;
    private static StringComparer PassiveMergePathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Copies only the selected full/safe input keys into a NEW inactive root. Reuses the
    /// candidate core's stage-only copying, Manifest5 and flushed copying/ready journals.
    /// Ready roots are immutable; this method grants no catalog or publication authority.
    /// Empty safe selections have no root: the coordinator must skip this call for them.
    /// </summary>
    internal async Task<PassiveMediaImportCandidate> MaterializePassiveMergeCandidateAdmittedAsync(
        ProfileAdmissionToken admission, NativeMediaMergePlan plan, bool safeSubset, CancellationToken token = default,
        Action<string>? childDirectoryCreated = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Owner.EnsureTransferAdmission(admission);
        Dictionary<string, MediaAttachmentBinding> bindings = ValidatePassiveMergePlan(plan);
        ImmutableArray<string> keys = safeSubset ? plan.SafeKeys : plan.FullKeys;
        if (keys.IsEmpty) throw new InvalidDataException("An empty safe selection must not create a candidate.");
        token.ThrowIfCancellationRequested();
        PassiveMediaImportCandidate candidate = await MaterializePassiveImportCandidateCoreAsync(
            plan.Stage, plan.Package, plan.Resource, keys.ToHashSet(StringComparer.Ordinal), plan.OperationsParent,
            plan.OperationId, plan.ImportedAtUtc, token, checkpoint: null, childDirectoryCreated: childDirectoryCreated)
            .ConfigureAwait(false);
        // The core independently derives metadata from the frozen package, not from plan.Inputs.
        // Prove that its result is exactly the issued selection, without minting a synthetic plan.
        // Do not turn a cancellation arriving after Ready into an apparent rollback.
        try { ValidatePassiveMergeCandidate(plan, candidate, keys, bindings); }
        catch (InvalidDataException error) { throw new PassiveMediaCandidateWriteException(candidate.Directory, error); }
        return candidate;
    }

    // Separate Apply boundary: preparation keeps using the raw read-only root helper.
    internal Task<T> WithPassiveMergeTargetRootsAdmittedAsync<T>(ProfileAdmissionToken admission,
        NativeMediaMergePlan plan, Func<ImmutableArray<MediaAssetSnapshot>, Task<T>> callback,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Owner.EnsureTransferAdmission(admission);
        return WithPassiveMergeTargetRootsAsync(plan.Roots.Select(root => root.Directory).ToArray(), callback, token);
    }

    /// <summary>
    /// Read-only target reservation, used BOTH at planning and at final commit. Acquires only
    /// existing stable store locks, in normalized distinct path order. All manifest-declared
    /// original bytes are rehashed outside SQL, including unbound/retained assets. Unknown
    /// orphans are not enumerated, inferred, repaired or removed. The callback receives snapshots
    /// in lease order and must compare/bind their Directory+Generation list to the issued plan.
    /// </summary>
    internal async Task<T> WithPassiveMergeTargetRootsAsync<T>(IReadOnlyList<string> directories,
        Func<ImmutableArray<MediaAssetSnapshot>, Task<T>> callback, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        token.ThrowIfCancellationRequested();
        string[] roots = NormalizePassiveMergeRoots(directories);
        List<FileStream> leases = new(roots.Length);
        try
        {
            foreach (string root in roots)
            {
                ValidateCandidateDirectory(root, requireCandidateName: false);
                // FileMode.Open only. No mkdir, lock creation, legacy-lock migration or repair.
                leases.Add(await OpenExistingCandidateLeaseAsync(Path.Combine(root, DirectoryLeaseFileName), token)
                    .ConfigureAwait(false));
            }

            var snapshots = ImmutableArray.CreateBuilder<MediaAssetSnapshot>(roots.Length);
            long items = 0;
            foreach (string root in roots)
            {
                token.ThrowIfCancellationRequested();
                ValidateCandidateDirectory(root, requireCandidateName: false);
                ValidatePassiveMergeTargetManifest(root);
                MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(root, token).ConfigureAwait(false);
                // The current planner verifies original-only roots. Inventory-aware planning
                // must be wired before this gate can be lifted without losing observations.
                if (snapshot.Status != MediaManifestStatus.Available || snapshot.ManifestVersion == RepresentationManifestVersion
                    || !IsLowerCandidateHash(snapshot.Generation))
                    throw new InvalidDataException("A merge target manifest is missing or corrupt.");
                items += (long)snapshot.Count + snapshot.AttachmentBindings.Count;
                if (items > MaximumPassiveMergeItems)
                    throw new InvalidDataException("The combined merge target asset/binding budget was exceeded.");
                snapshots.Add(snapshot);
            }
            // Native transfer has a 1-GiB single / 2-GiB aggregate payload budget. Bound
            // actual target IO (including duplicate physical copies) BEFORE opening payloads.
            long verificationBytes = 0;
            foreach (MediaAssetSnapshot snapshot in snapshots)
            {
                HashSet<string> paths = new(PassiveMergePathComparer);
                for (int offset = 0; offset < snapshot.Count; offset += 500)
                foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
                {
                    if (asset.Variant != "original" || !paths.Add(asset.Path)) continue;
                    if (asset.Length < 0 || asset.Length > 1024L * 1024 * 1024
                        || asset.Length > 2L * 1024 * 1024 * 1024 - verificationBytes)
                        throw new InvalidDataException("The merge target verification byte budget was exceeded.");
                    verificationBytes += asset.Length;
                }
            }
            // Check the entire combined inventory budget before starting any payload hashing.
            foreach (MediaAssetSnapshot snapshot in snapshots)
                await VerifyPassiveMergeTargetBytesAsync(snapshot, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await callback(snapshots.MoveToImmutable()).ConfigureAwait(false);
        }
        finally
        {
            for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
        }
    }

    /// <summary>
    /// Caller must first hold target leases via WithPassiveMergeTargetRootsAsync and compare
    /// their generations with plan.Roots. Acquires NEW candidate leases only after that target
    /// tier, in deterministic path order (not full/safe argument order). Keeps every candidate
    /// handle across the awaited callback; performs no writes and never manufactures owner authority.
    /// </summary>
    internal async Task<T> WithVerifiedPassiveMergeCandidatesAdmittedAsync<T>(ProfileAdmissionToken admission, NativeMediaMergePlan plan,
        PassiveMediaImportCandidate full, PassiveMediaImportCandidate? safe, Func<Task<T>> callback,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Owner.EnsureTransferAdmission(admission);
        ArgumentNullException.ThrowIfNull(callback);
        token.ThrowIfCancellationRequested();
        Dictionary<string, MediaAttachmentBinding> bindings = ValidatePassiveMergePlan(plan);
        string fullDirectory = ValidatePassiveMergeCandidate(plan, full, plan.FullKeys, bindings);
        string? safeDirectory = null;
        if (plan.SafeKeys.IsEmpty)
        {
            if (safe != null) throw new InvalidDataException("An empty safe selection must not have a candidate.");
        }
        else
        {
            if (safe == null) throw new InvalidDataException("A nonempty safe selection requires a candidate.");
            safeDirectory = ValidatePassiveMergeCandidate(plan, safe, plan.SafeKeys, bindings);
            bool sameRoot = PathsEqual(fullDirectory, safeDirectory);
            if (sameRoot != plan.ReuseFullAsSafe)
                throw new InvalidDataException("The full/safe candidate alias differs from the issued plan.");
        }

        string[] directories = (safeDirectory == null ? new[] { fullDirectory } : new[] { fullDirectory, safeDirectory })
            .Distinct(PassiveMergePathComparer).OrderBy(path => path, PassiveMergePathComparer).ToArray();
        List<FileStream> leases = new(directories.Length);
        try
        {
            foreach (string directory in directories)
            {
                ValidateCandidateDirectory(directory, requireCandidateName: true);
                leases.Add(await OpenExistingCandidateLeaseAsync(Path.Combine(directory, DirectoryLeaseFileName), token)
                    .ConfigureAwait(false));
            }
            await VerifyPassiveMergeCandidateAsync(plan, full, fullDirectory, plan.FullKeys, bindings, token)
                .ConfigureAwait(false);
            if (safeDirectory != null && !PathsEqual(fullDirectory, safeDirectory))
                await VerifyPassiveMergeCandidateAsync(plan, safe!, safeDirectory, plan.SafeKeys, bindings, token)
                    .ConfigureAwait(false);
            // An aliased safe DTO has already been compared in full, including generation and
            // every binding, to the same issued inputs. Do not acquire its lock or hash it twice.
            if (safeDirectory != null && PathsEqual(fullDirectory, safeDirectory)
                && full.ManifestGeneration != safe!.ManifestGeneration)
                throw new InvalidDataException("The aliased candidate generations differ.");
            token.ThrowIfCancellationRequested();
            return await callback().ConfigureAwait(false);
        }
        finally
        {
            for (int index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
        }
    }

    private static string NormalizePassiveMergePath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidDataException("An absolute merge root is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    private static string[] NormalizePassiveMergeRoots(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        // Also bound the pre-deduplication input work; duplicates confer no additional roots.
        if (directories.Count > MaximumPassiveMergeItems)
            throw new InvalidDataException("The merge root input budget was exceeded.");
        SortedSet<string> roots = new(PassiveMergePathComparer);
        foreach (string directory in directories)
        {
            roots.Add(NormalizePassiveMergePath(directory));
            if (roots.Count > MaximumPassiveMergeTargetRoots)
                throw new InvalidDataException("Too many distinct merge target roots.");
        }
        return roots.ToArray();
    }

    private static void ValidatePassiveMergeTargetManifest(string root)
    {
        try { ValidateCandidateRegularFile(Path.Combine(root, ManifestFileName)); }
        catch (FileNotFoundException)
        {
            // Legacy snapshots are read-only projections too, but still require the existing
            // STABLE current store lock above. Never create or migrate either lease here.
            ValidateCandidateRegularFile(Path.Combine(root, LegacyManifestFileName));
        }
    }

    private async Task VerifyPassiveMergeTargetBytesAsync(MediaAssetSnapshot snapshot, CancellationToken token)
    {
        Dictionary<string, FileDigest> measured = new(PassiveMergePathComparer);
        for (int offset = 0; offset < snapshot.Count; offset += 500)
        foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
        {
            token.ThrowIfCancellationRequested();
            string path = NormalizePassiveMergePath(asset.Path);
            if (!PathsEqual(Path.GetDirectoryName(path)!, snapshot.Directory))
                throw new InvalidDataException("A target asset escapes its root.");
            // Check ALL declared payload types before opening, even non-original assets.
            // Several distinct attachment assets may legitimately share one physical payload.
            ValidateCandidateRegularFile(path);
            if (asset.Variant != "original") continue;
            if (!measured.TryGetValue(path, out FileDigest? digest))
            {
                digest = await HashCandidateFileAsync(path, token, asset.Length).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A target original is missing or incomplete.");
                measured.Add(path, digest);
            }
            MediaActualIntegrity actual = Actual(digest);
            // Legacy unbound inventories may have no MD5; never invent it. SHA256+length are
            // mandatory, and any recorded MD5/SHA1 as well as every binding must match real bytes.
            if (asset.Length != actual.Length || !string.Equals(asset.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(asset.Md5) && !SameBase64Digest(asset.Md5, actual.Md5)
                || asset.Sha1 != null && !SameBase64Digest(asset.Sha1, actual.Sha1!))
                throw new InvalidDataException("A target original failed verification.");
        }
        foreach (var pair in snapshot.AttachmentBindings)
        {
            token.ThrowIfCancellationRequested();
            MediaIntegrityValidation.ValidateBinding(pair.Key, pair.Value);
            MediaAttachmentBinding binding = pair.Value;
            MediaAssetReference asset = snapshot.FindById(binding.AssetId)
                ?? throw new InvalidDataException("A target binding has no asset.");
            if (!MediaAttachmentResolver.BindingMatchesAsset(binding, asset)
                || asset.Variant == "original" && (!measured.TryGetValue(asset.Path, out FileDigest? digest)
                    || !MediaIntegrityValidation.MatchesMeasured(binding.Actual, Actual(digest))))
                throw new InvalidDataException("A target binding disagrees with its original bytes.");
        }
    }

    private static Dictionary<string, MediaAttachmentBinding> ValidatePassiveMergePlan(NativeMediaMergePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.FullKeys.IsDefaultOrEmpty || plan.SafeKeys.IsDefault || plan.Inputs.IsDefault
            || plan.Roots.IsDefault || plan.FullKeys.Length > MaximumPassiveMergeItems
            || plan.Inputs.Length != plan.FullKeys.Length || plan.Conflicts < 0
            || plan.OperationId == Guid.Empty || plan.ImportedAtUtc.UtcDateTime == DateTime.MinValue
            || !IsLowerCandidateHash(plan.Package.Envelope.Fingerprint))
            throw new InvalidDataException("Invalid frozen merge plan.");
        ValidatePassiveMergeKeys(plan.FullKeys);
        ValidatePassiveMergeKeys(plan.SafeKeys);
        HashSet<string> full = plan.FullKeys.ToHashSet(StringComparer.Ordinal);
        if (plan.SafeKeys.Any(key => !full.Contains(key)))
            throw new InvalidDataException("The safe selection is outside the full input set.");
        string parent = NormalizePassiveMergePath(plan.OperationsParent);
        if (!PathsEqual(parent, Path.TrimEndingDirectorySeparator(plan.OperationsParent)))
            throw new InvalidDataException("The operation parent must be canonical.");
        string[] roots = NormalizePassiveMergeRoots(plan.Roots.Select(root => root.Directory).ToArray());
        if (roots.Length != plan.Roots.Length || plan.Roots.Any(root => !IsLowerCandidateHash(root.Generation)))
            throw new InvalidDataException("Invalid frozen target roots.");
        foreach (string root in roots)
            if (PassiveMergePathsOverlap(parent, root))
                throw new InvalidDataException("The operation parent must be independent of every target root.");

        Dictionary<string, MediaAttachmentBinding> bindings = new(StringComparer.Ordinal);
        foreach (NativeMediaImportAttachment input in plan.Inputs)
        {
            if (!full.Contains(input.RevisionKey) || !bindings.TryAdd(input.RevisionKey, PassiveMergeInputBinding(plan, input)))
                throw new InvalidDataException("The frozen merge input identity is ambiguous.");
        }
        return bindings;
    }

    private static void ValidatePassiveMergeKeys(ImmutableArray<string> keys)
    {
        if (keys.Length > MaximumPassiveMergeItems) throw new InvalidDataException("Too many merge input keys.");
        for (int index = 0; index < keys.Length; index++)
            if (!IsCandidateRevisionKey(keys[index]) || index > 0 && StringComparer.Ordinal.Compare(keys[index - 1], keys[index]) >= 0)
                throw new InvalidDataException("Merge input keys must be valid, sorted and distinct.");
    }

    private static bool PassiveMergePathsOverlap(string first, string second)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return PathsEqual(first, second)
            || first.StartsWith(Path.EndsInDirectorySeparator(second) ? second : second + Path.DirectorySeparatorChar, comparison)
            || second.StartsWith(Path.EndsInDirectorySeparator(first) ? first : first + Path.DirectorySeparatorChar, comparison);
    }

    private static MediaAttachmentBinding PassiveMergeInputBinding(NativeMediaMergePlan plan, NativeMediaImportAttachment input)
    {
        TransferAttachment incoming = input.Incoming;
        CapturedAttachment raw = incoming.Metadata;
        MediaProviderMetadata provider = new(raw.MediaId!, raw.Variant, raw.Url!, raw.OriginalFileName,
            raw.ExpectedSize, raw.ExpectedMd5, raw.ExpectedSha256)
        { ExpectedSha1 = raw.ExpectedSha1, ProviderSizeHint = raw.ProviderSizeHint };
        if (incoming.Role != TransferMediaRole.Original || incoming.Status != TransferContentStatus.Contained
            || provider.Variant != "original" || raw.PlannedPath != null || provider != input.Provider
            || input.RevisionKey != MediaRevisionKeys.CreateAttachmentKey(plan.Resource, input.PostId, raw.Position, provider)
            || incoming.PayloadSha256 == null
            || !plan.Package.Envelope.MeasuredPayloads.TryGetValue(incoming.PayloadSha256, out TransferMeasuredBlob? blob))
            throw new InvalidDataException("A merge input is not an exact measured original observation.");
        MediaActualIntegrity actual = new(blob.Length, blob.Md5, blob.Sha256) { Sha1 = blob.Sha1 };
        ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(provider, actual);
        MediaImportedHistory? history = incoming.History is not { } observed ? null : new(observed.Kind switch
        {
            "strict" => MediaAcceptanceKind.Strict,
            "provider-mismatch" => MediaAcceptanceKind.ExplicitProviderMismatch,
            _ => throw new InvalidDataException("Invalid imported history.")
        }, observed.AcceptedAtUtc, observed.SizeMismatch, observed.Md5Mismatch);
        string assetId = GetAssetId(input.RevisionKey[7..] + ".original.bin", new MediaManifestEntry
        { MediaIdentifier = provider.MediaId, Variant = "original", FileName = blob.Sha256 + ".original.bin", Sha256 = blob.Sha256 });
        MediaAttachmentBinding binding = new(new(plan.Resource, input.PostId, raw.Position, input.RevisionKey),
            MediaRevisionKeys.CreateAcquisitionKey(plan.Resource, provider), assetId, provider, actual,
            new(MediaAcceptanceKind.PassiveImport, assessment.SizeMismatch, assessment.Md5Mismatch, plan.ImportedAtUtc))
        { PassiveImport = new(plan.OperationId.ToString("D"), plan.Package.Envelope.Fingerprint,
            plan.ImportedAtUtc, raw.AttachmentRevisionKey, history) };
        MediaIntegrityValidation.ValidateBinding(input.RevisionKey, binding);
        return binding;
    }

    private static string ValidatePassiveMergeCandidate(NativeMediaMergePlan plan, PassiveMediaImportCandidate candidate,
        ImmutableArray<string> keys, Dictionary<string, MediaAttachmentBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string directory = ValidateCandidateDirectory(candidate.Directory, requireCandidateName: true);
        if (!PathsEqual(Path.GetDirectoryName(directory)!, NormalizePassiveMergePath(plan.OperationsParent))
            || candidate.OperationId != plan.OperationId || candidate.Resource != plan.Resource
            || candidate.PackageFingerprint != plan.Package.Envelope.Fingerprint || candidate.ImportedAtUtc != plan.ImportedAtUtc
            || !IsLowerCandidateHash(candidate.ManifestGeneration) || candidate.Bindings.IsDefault
            || candidate.Bindings.Length != keys.Length || candidate.Bindings.Any(binding => binding?.Reference == null)
            || !candidate.Bindings.OrderBy(binding => binding.Reference.RevisionKey, StringComparer.Ordinal)
                .SequenceEqual(keys.Select(key => bindings[key])))
            throw new InvalidDataException("The merge candidate differs from the issued plan and selection.");
        return directory;
    }

    private async Task VerifyPassiveMergeCandidateAsync(NativeMediaMergePlan plan, PassiveMediaImportCandidate expected,
        string directory, ImmutableArray<string> keys, Dictionary<string, MediaAttachmentBinding> bindings, CancellationToken token)
    {
        ValidateCandidateDirectory(directory, requireCandidateName: true);
        PassiveCandidateJournal journal = await ReadCandidateJournalAsync(directory, token).ConfigureAwait(false);
        if (journal.Phase != "ready" || journal.OperationId != plan.OperationId.ToString("D")
            || journal.Resource != plan.Resource || journal.PackageFingerprint != plan.Package.Envelope.Fingerprint
            || journal.ImportedAtUtc != plan.ImportedAtUtc || journal.ManifestGeneration != expected.ManifestGeneration
            || !keys.SequenceEqual(journal.RevisionKeys, StringComparer.Ordinal))
            throw new InvalidDataException("The merge completion marker differs from the issued selection.");
        ValidateCandidateRegularFile(Path.Combine(directory, ManifestFileName));
        MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(directory, token).ConfigureAwait(false);
        await VerifyCandidateSnapshotAsync(journal, directory, snapshot, token).ConfigureAwait(false);
        foreach (string key in keys)
        {
            token.ThrowIfCancellationRequested();
            MediaAttachmentBinding binding = bindings[key];
            if (snapshot.FindAttachmentBinding(key) != binding)
                throw new InvalidDataException("A merge binding differs from the exact frozen observation.");
            MediaAssetReference wanted = new(binding.AssetId, binding.Provider.MediaId, "original", key[7..] + ".original.bin",
                Path.Combine(directory, binding.Actual.Sha256 + ".original.bin"), binding.Actual.Length,
                binding.Actual.Sha256, binding.Provider.Url, binding.Provider.OriginalFileName, true)
            { Md5 = binding.Actual.Md5, Sha1 = binding.Actual.Sha1 };
            if (snapshot.FindById(binding.AssetId) != wanted)
                throw new InvalidDataException("A merge asset differs from its exact frozen input metadata.");
        }
    }
}
