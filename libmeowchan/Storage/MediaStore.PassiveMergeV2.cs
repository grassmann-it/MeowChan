using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Storage;

public sealed partial class MediaStore
{
    // Read-only reservation: never create/migrate a lock, borrow bytes, or repair a root.
    internal async Task<T> WithPassiveMergeInventoryTargetRootsAsync<T>(IReadOnlyList<string> directories,
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
                leases.Add(await OpenExistingCandidateLeaseAsync(Path.Combine(root, DirectoryLeaseFileName), token)
                    .ConfigureAwait(false));
            }
            var snapshots = ImmutableArray.CreateBuilder<MediaAssetSnapshot>(roots.Length);
            long items = 0, bytes = 0;
            Dictionary<string, long> physical = new(PassiveMergePathComparer);
            void Reserve(string root, string path, long length)
            {
                path = NormalizePassiveMergePath(path);
                if (!PathsEqual(Path.GetDirectoryName(path)!, root))
                    throw new InvalidDataException("A merge target payload escapes its root.");
                if (length < 0 || length > 1024L * 1024 * 1024)
                    throw new InvalidDataException("The merge target single-file budget was exceeded.");
                if (physical.TryGetValue(path, out long previous))
                {
                    if (previous != length) throw new InvalidDataException("Conflicting physical payload lengths.");
                    return;
                }
                if (length > 2L * 1024 * 1024 * 1024 - bytes)
                    throw new InvalidDataException("The merge target verification byte budget was exceeded.");
                physical.Add(path, length);
                bytes += length;
            }
            foreach (string root in roots)
            {
                token.ThrowIfCancellationRequested();
                ValidateCandidateDirectory(root, requireCandidateName: false);
                ValidatePassiveMergeTargetManifest(root);
                MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(root, token).ConfigureAwait(false);
                if (snapshot.Status != MediaManifestStatus.Available || !IsLowerCandidateHash(snapshot.Generation))
                    throw new InvalidDataException("A merge target manifest is missing or corrupt.");
                items += (long)snapshot.Count + snapshot.AttachmentBindings.Count
                    + snapshot.OriginalEvidence.Length + snapshot.DerivedRepresentations.Length;
                if (items > MaximumPassiveMergeItems)
                    throw new InvalidDataException("The combined merge target inventory budget was exceeded.");
                for (int offset = 0; offset < snapshot.Count; offset += 500)
                foreach (MediaAssetReference asset in snapshot.GetPage(offset, 500))
                    Reserve(root, asset.Path, asset.Length);
                foreach (MediaDerivedRepresentation member in snapshot.DerivedRepresentations)
                    if (member.IncomingStatus == MediaDerivedStatus.Contained)
                    {
                        if (member.Blob is not { } blob) throw InvalidRepresentations();
                        Reserve(root, Path.Combine(root, blob.RelativeFileName), blob.Actual.Length);
                    }
                snapshots.Add(snapshot);
            }
            // Finish the combined budget before the first payload read. Equal hashes in
            // different roots or original/derived roles never collapse physical paths.
            foreach (MediaAssetSnapshot snapshot in snapshots)
            {
                await VerifyPassiveMergeTargetBytesAsync(snapshot, token, verifyAllAssets: true).ConfigureAwait(false);
                foreach (var group in snapshot.DerivedRepresentations
                    .Where(m => m.IncomingStatus == MediaDerivedStatus.Contained)
                    .GroupBy(m => m.Blob!.RelativeFileName, PassiveMergePathComparer))
                {
                    token.ThrowIfCancellationRequested();
                    MediaDerivedRepresentation member = group.FirstOrDefault(m => m.Kind == MediaDerivedKind.VideoPosterPng)
                        ?? group.First();
                    // Every descriptor must agree; a Preview cannot bypass a Poster's PNG proof.
                    if (group.Any(m => m.Blob!.Actual != member.Blob!.Actual)) throw InvalidRepresentations();
                    await using Stream verified = await snapshot.OpenVerifiedDerivedReadAsync(member, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            return await callback(snapshots.MoveToImmutable()).ConfigureAwait(false);
        }
        finally
        {
            for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
        }
    }

    internal Task<T> WithPassiveMergeInventoryTargetRootsAdmittedAsync<T>(ProfileAdmissionToken admission,
        NativeMediaMergePlan plan, Func<ImmutableArray<MediaAssetSnapshot>, Task<T>> callback,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        NativeMediaMergeV2State state = plan.Owner.RequireIssuedNativeMediaMergeV2Plan(admission, plan);
        ArgumentNullException.ThrowIfNull(callback);
        ValidatePassiveMergeV2Roots(plan, state);
        return WithPassiveMergeInventoryTargetRootsAsync(state.Roots.Select(r => r.Directory).ToArray(), async snapshots =>
        {
            if (snapshots.Length != state.Roots.Length || state.Roots.Any(root => !snapshots.Any(snapshot =>
                PathsEqual(snapshot.Directory, root.Directory) && snapshot.Generation == root.Generation)))
                throw new InvalidDataException("The merge target roots changed since planning.");
            return await callback(snapshots).ConfigureAwait(false);
        }, token);
    }

    internal async Task<PassiveMediaImportCandidate> MaterializePassiveMergeCandidateAdmittedAsync(
        ProfileAdmissionToken admission, NativeMediaMergePlan plan, NativeMediaPersistedMergeOperation preparing,
        bool safeSubset, CancellationToken token = default, Action<string>? childDirectoryCreated = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        NativeMediaMergeV2State state = plan.Owner.RequireIssuedNativeMediaMergeV2Plan(admission, plan);
        ArgumentNullException.ThrowIfNull(preparing);
        if (!state.MatchesOperation(preparing, NativeMediaImportOperationState.Preparing))
            throw new InvalidDataException("The preparing operation differs from the issued merge plan.");
        ValidatePassiveMergeV2Roots(plan, state);
        NativeMediaCandidateSelection selection = safeSubset ? state.SafeSelection : state.FullSelection;
        if (selection.IsEmpty) throw new InvalidDataException("An empty safe selection must not create a candidate.");
        token.ThrowIfCancellationRequested();
        PassiveMediaImportCandidate candidate = await MaterializePassiveImportCandidateAsync(state.Materialization,
            selection, plan.OperationsParent, plan.OperationId, plan.ImportedAtUtc, token,
            checkpoint: null, childDirectoryCreated: childDirectoryCreated).ConfigureAwait(false);
        // No cancellation after Ready: preserve the low-level completion semantics.
        if (!PathsEqual(Path.GetDirectoryName(candidate.Directory)!, plan.OperationsParent)
            || !state.MatchesCandidate(candidate, safeSubset))
            throw new PassiveMediaCandidateWriteException(candidate.Directory,
                new InvalidDataException("The candidate differs from the issued merge selection."));
        return candidate;
    }

    internal async Task<T> WithVerifiedPassiveMergeCandidatesAdmittedAsync<T>(ProfileAdmissionToken admission,
        NativeMediaMergePlan plan, NativeMediaPersistedMergeOperation prepared, Func<Task<T>> callback,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        NativeMediaMergeV2State state = plan.Owner.RequireIssuedNativeMediaMergeV2Plan(admission, plan);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(callback);
        if (!state.MatchesOperation(prepared, NativeMediaImportOperationState.Prepared))
            throw new InvalidDataException("The prepared operation differs from the issued merge plan.");
        ValidatePassiveMergeV2Roots(plan, state);
        token.ThrowIfCancellationRequested();
        string Child(string? path)
        {
            if (path == null) throw new InvalidDataException("Missing prepared candidate directory.");
            string directory = ValidateCandidateDirectory(path, requireCandidateName: true);
            if (!PathsEqual(Path.GetDirectoryName(directory)!, plan.OperationsParent))
                throw new InvalidDataException("The candidate is not an immediate child of the merge parent.");
            return directory;
        }
        string full = Child(prepared.Full.Directory);
        string? safe = state.SafeSelection.IsEmpty ? null : Child(prepared.Safe.Directory);
        if (safe != null && PathsEqual(full, safe) != state.ReuseFullAsSafe)
            throw new InvalidDataException("The full/safe alias differs from the issued merge plan.");
        string[] directories = (safe == null ? new[] { full } : new[] { full, safe })
            .Distinct(PassiveMergePathComparer).OrderBy(p => p, PassiveMergePathComparer).ToArray();
        List<FileStream> leases = new(directories.Length);
        try
        {
            foreach (string directory in directories)
                leases.Add(await OpenExistingCandidateLeaseAsync(Path.Combine(directory, DirectoryLeaseFileName), token)
                    .ConfigureAwait(false));
            async Task<PassiveMediaImportCandidate> Verify(string directory, bool safeSubset)
            {
                ValidateCandidateDirectory(directory, requireCandidateName: true);
                PassiveMediaCandidateInspection inspection = await InspectPassiveImportCandidateV2Async(directory, token)
                    .ConfigureAwait(false);
                if (inspection.Status != PassiveMediaCandidateStatus.Ready || inspection.Candidate is not { } candidate
                    || !PathsEqual(candidate.Directory, directory) || !state.MatchesCandidate(candidate, safeSubset)
                    || candidate.ManifestGeneration != (safeSubset ? prepared.Safe : prepared.Full).ManifestGeneration)
                    throw new InvalidDataException("The verified candidate differs from the prepared merge operation.");
                return candidate;
            }
            PassiveMediaImportCandidate fullCandidate = await Verify(full, false).ConfigureAwait(false);
            if (safe != null)
            {
                if (!PathsEqual(full, safe)) await Verify(safe, true).ConfigureAwait(false);
                else if (!state.MatchesCandidate(fullCandidate, true)
                    || fullCandidate.ManifestGeneration != prepared.Safe.ManifestGeneration)
                    throw new InvalidDataException("The aliased safe candidate differs from its prepared evidence.");
            }
            token.ThrowIfCancellationRequested();
            return await callback().ConfigureAwait(false);
        }
        finally
        {
            for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
        }
    }

    private static void ValidatePassiveMergeV2Roots(NativeMediaMergePlan plan, NativeMediaMergeV2State state)
    {
        string parent = NormalizePassiveMergePath(plan.OperationsParent);
        if (!PathsEqual(parent, Path.TrimEndingDirectorySeparator(plan.OperationsParent)))
            throw new InvalidDataException("The operation parent must be canonical.");
        string[] roots = NormalizePassiveMergeRoots(state.Roots.Select(r => r.Directory).ToArray());
        if (roots.Length != state.Roots.Length || state.Roots.Any(r => !IsLowerCandidateHash(r.Generation))
            || roots.Any(root => PassiveMergePathsOverlap(parent, root)))
            throw new InvalidDataException("Invalid or overlapping frozen target roots.");
    }
}
