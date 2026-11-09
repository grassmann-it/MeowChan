using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MeowChan.Transfer;

/// <summary>
/// Safety bounds for the passive multi-thread admission/planning slice. The limits
/// describe one selected aggregate and do not grant any storage or source authority.
/// </summary>
internal sealed record NativeTransferAggregateLimits(int MaxThreads = 1000,
    int MaxPosts = 200_000, int MaxAttachments = 200_000)
{
    internal void Validate()
    {
        if (MaxThreads is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(MaxThreads));
        if (MaxPosts is < 0 or > 200_000) throw new ArgumentOutOfRangeException(nameof(MaxPosts));
        if (MaxAttachments is < 0 or > 200_000) throw new ArgumentOutOfRangeException(nameof(MaxAttachments));
    }
}

internal enum NativeTransferAggregateAdmissionError
{
    None,
    InvalidPackage,
    PrivateMetadataUnsupported,
    EmptySelection,
    TooManyThreads,
    InvalidThreadIdentity,
    RootlessThread,
    DuplicateThreadIdentity,
    DuplicateSelectionIdentity,
    InvalidSelectionIdentity,
    SelectionNotInPackage,
    InvalidThreadContent,
    InvalidPackageFingerprint,
    PostLimitExceeded,
    AttachmentLimitExceeded
}

internal sealed record NativeTransferAggregateIdentity(string ResourceSetSha256,
    string PackageFingerprint, int ThreadCount);

/// <summary>
/// A path-free fence for one admitted thread. The content hash is derived from the
/// frozen public thread projection and the package fingerprint; it is not a claim that
/// a target store currently has this revision.
/// </summary>
internal sealed record NativeTransferThreadFence(ResourceId Resource, string ContentSha256,
    long? CaptureRevision, DateTimeOffset? CapturedAtUtc);

internal sealed record NativeTransferAggregateThreadAdmission(ResourceId Resource,
    NativeTransferThreadFence Fence, int PostCount, int AttachmentCount, string ReplayKey);

internal sealed record NativeTransferAggregateAdmissionSnapshot(NativeTransferAggregateIdentity Identity,
    string PackageFingerprint, string ReplayKey,
    ImmutableArray<NativeTransferAggregateThreadAdmission> Threads)
{
    internal int PostCount => Threads.Sum(thread => thread.PostCount);
    internal int AttachmentCount => Threads.Sum(thread => thread.AttachmentCount);
}

internal sealed record NativeTransferAggregateAdmissionResult(
    NativeTransferAggregateAdmissionSnapshot? Admission,
    NativeTransferAggregateAdmissionError Error)
{
    internal bool IsAdmitted => Admission != null && Error == NativeTransferAggregateAdmissionError.None;
}

/// <summary>
/// Passive admission for a selected public subset of a validated package. It validates
/// the aggregate identity again at the domain boundary because callers may construct
/// internal DTOs without going through the ZIP inspector. It never opens payloads,
/// reads SQL, or creates a host/store lease.
/// </summary>
internal static class NativeTransferAggregateAdmission
{
    internal static NativeTransferAggregateAdmissionResult Admit(NativeTransferPackage? package,
        IEnumerable<ResourceId>? selectedResources, NativeTransferAggregateLimits? limits = null)
    {
        limits ??= new();
        limits.Validate();
        if (package == null || package.Envelope == null || package.Catalog == null
            || package.Catalog.Threads.IsDefault || package.Catalog.Threads.Length == 0)
            return Fail(NativeTransferAggregateAdmissionError.InvalidPackage);
        if (package.PrivateMetadata != null || package.Envelope.PrivateMetadata != null)
            return Fail(NativeTransferAggregateAdmissionError.PrivateMetadataUnsupported);
        if (!IsHash(package.Envelope.Fingerprint))
            return Fail(NativeTransferAggregateAdmissionError.InvalidPackageFingerprint);
        if (package.Catalog.Threads.Length > 1000)
            return Fail(NativeTransferAggregateAdmissionError.TooManyThreads);
        if (selectedResources == null)
            return Fail(NativeTransferAggregateAdmissionError.EmptySelection);

        List<ResourceId> selected;
        try { selected = selectedResources.ToList(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return Fail(NativeTransferAggregateAdmissionError.InvalidSelectionIdentity); }
        if (selected.Count == 0) return Fail(NativeTransferAggregateAdmissionError.EmptySelection);
        if (selected.Count > limits.MaxThreads) return Fail(NativeTransferAggregateAdmissionError.TooManyThreads);

        Dictionary<ResourceId, TransferThread> available = new();
        int packagePosts = 0, packageAttachments = 0;
        foreach (TransferThread? thread in package.Catalog.Threads)
        {
            if (thread == null) return Fail(NativeTransferAggregateAdmissionError.InvalidThreadIdentity);
            NativeTransferAggregateAdmissionError identityError = ValidateIdentity(thread.Id);
            if (identityError != NativeTransferAggregateAdmissionError.None) return Fail(identityError);
            if (!available.TryAdd(thread.Id, thread))
                return Fail(NativeTransferAggregateAdmissionError.DuplicateThreadIdentity);
            if (thread.Posts.IsDefault || thread.RetainedBindings.IsDefault)
                return Fail(NativeTransferAggregateAdmissionError.InvalidThreadContent);
            packagePosts = checked(packagePosts + thread.Posts.Length);
            packageAttachments = checked(packageAttachments + thread.Posts.Sum(post => post?.Attachments.Length ?? int.MaxValue));
            packageAttachments = checked(packageAttachments + thread.RetainedBindings.Length);
            if (packagePosts > 200_000 || packageAttachments > 200_000)
                return Fail(NativeTransferAggregateAdmissionError.InvalidThreadContent);
            try
            {
                // Reuse the conservative self-comparison validator for hand-built DTOs.
                TransferMergeAnalysisResult validation = NativeTransferMergeAnalysis.Analyze(thread, thread,
                    new TransferMergeAnalysisLimits(200_000, 200_000, 32L * 1024 * 1024));
                if (validation.Error != TransferMergeAnalysisError.None)
                    return Fail(NativeTransferAggregateAdmissionError.InvalidThreadContent);
                _ = NativeTransferCatalogSerializer.Serialize(new(package.Catalog.ExportedAtUtc, [thread]));
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException
                or InvalidOperationException or OverflowException or FormatException)
            { return Fail(NativeTransferAggregateAdmissionError.InvalidThreadContent); }
        }

        HashSet<ResourceId> requested = new();
        foreach (ResourceId? resource in selected)
        {
            NativeTransferAggregateAdmissionError identityError = ValidateIdentity(resource);
            if (identityError == NativeTransferAggregateAdmissionError.None && !requested.Add(resource!))
                return Fail(NativeTransferAggregateAdmissionError.DuplicateSelectionIdentity);
            if (identityError != NativeTransferAggregateAdmissionError.None)
                return Fail(identityError == NativeTransferAggregateAdmissionError.RootlessThread
                    ? NativeTransferAggregateAdmissionError.RootlessThread
                    : NativeTransferAggregateAdmissionError.InvalidSelectionIdentity);
            if (!available.ContainsKey(resource!))
                return Fail(NativeTransferAggregateAdmissionError.SelectionNotInPackage);
        }

        List<NativeTransferAggregateThreadAdmission> entries = new(selected.Count);
        foreach (ResourceId resource in requested.OrderBy(id => IdentityText(id), StringComparer.Ordinal))
        {
            TransferThread thread = available[resource];
            string contentHash = ThreadContentHash(package.Envelope.Fingerprint, package.Catalog.ExportedAtUtc, thread);
            NativeTransferThreadFence fence = new(resource, contentHash, thread.CaptureRevision, thread.CapturedAtUtc);
            entries.Add(new(resource, fence, thread.Posts.Length,
                thread.Posts.Sum(post => post.Attachments.Length) + thread.RetainedBindings.Length,
                Hash("native-transfer-thread-replay-v1", package.Envelope.Fingerprint, IdentityText(resource), contentHash)));
        }

        int selectedPosts = entries.Sum(entry => entry.PostCount), selectedAttachments = entries.Sum(entry => entry.AttachmentCount);
        if (selectedPosts > limits.MaxPosts) return Fail(NativeTransferAggregateAdmissionError.PostLimitExceeded);
        if (selectedAttachments > limits.MaxAttachments) return Fail(NativeTransferAggregateAdmissionError.AttachmentLimitExceeded);
        string resourceSet = Hash("native-transfer-resource-set-v1",
            string.Join("\n", entries.Select(entry => IdentityText(entry.Resource))));
        NativeTransferAggregateIdentity aggregateIdentity = new(resourceSet, package.Envelope.Fingerprint, entries.Count);
        string replay = Hash("native-transfer-aggregate-replay-v1", package.Envelope.Fingerprint, resourceSet);
        return new(new NativeTransferAggregateAdmissionSnapshot(aggregateIdentity, package.Envelope.Fingerprint, replay, entries.ToImmutableArray()),
            NativeTransferAggregateAdmissionError.None);
    }

    internal static NativeTransferAggregateAdmissionResult Admit(NativeTransferPackage? package,
        NativeTransferArchiveSelection selection, NativeTransferAggregateLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Admit(package, selection.Threads, limits);
    }

    internal static NativeTransferAggregateAdmissionError ValidateIdentity(ResourceId? resource)
    {
        if (resource == null) return NativeTransferAggregateAdmissionError.InvalidThreadIdentity;
        if (resource.Thread == null) return NativeTransferAggregateAdmissionError.RootlessThread;
        return ValidIdentifier(resource.Source) && ValidIdentifier(resource.Board) && ValidIdentifier(resource.Thread)
            ? NativeTransferAggregateAdmissionError.None
            : NativeTransferAggregateAdmissionError.InvalidThreadIdentity;
    }

    private static bool ValidIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        foreach (Rune rune in value.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) return false;
        return true;
    }

    private static string ThreadContentHash(string packageFingerprint, DateTimeOffset exportedAtUtc, TransferThread thread)
    {
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(new(exportedAtUtc, [thread]));
        return Hash("native-transfer-thread-fence-v1", packageFingerprint, IdentityText(thread.Id),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    internal static string IdentityText(ResourceId resource)
        => string.Join('\0', resource.Source, resource.Board, resource.Thread);

    internal static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string Hash(string domain, params string?[] values)
    {
        string input = domain + "\0" + string.Join("\0", values.Select(value => value ?? "<null>"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static NativeTransferAggregateAdmissionResult Fail(NativeTransferAggregateAdmissionError error) => new(null, error);
}

internal enum NativeTransferAggregateMergePlanError
{
    None,
    InvalidAdmission,
    InvalidTargetIdentity,
    RootlessTarget,
    DuplicateTargetFence,
    UnknownTarget,
    InvalidTargetFence
}

/// <summary>Expected target state for one selected resource; null means a new target.</summary>
internal sealed record NativeTransferAggregateTargetFence(ResourceId Resource, string? ExpectedFenceSha256);

internal sealed record NativeTransferAggregateThreadMergePlan(ResourceId Resource,
    NativeTransferThreadFence IncomingFence, string? ExpectedTargetFenceSha256,
    string RequestSha256, string ReplayKey);

internal sealed record NativeTransferAggregateMergePlan(NativeTransferAggregateIdentity Identity,
    string PackageFingerprint, string RequestSha256, string ReplayKey,
    ImmutableArray<NativeTransferAggregateThreadMergePlan> Threads);

internal sealed record NativeTransferAggregateMergePlanResult(
    NativeTransferAggregateMergePlan? Plan, NativeTransferAggregateMergePlanError Error)
{
    internal bool IsPlanned => Plan != null && Error == NativeTransferAggregateMergePlanError.None;
}

/// <summary>
/// Builds deterministic, path-free per-thread merge plans. The target fences are
/// preconditions only; this slice deliberately performs no target reads or writes.
/// </summary>
internal static class NativeTransferAggregateMergePlanner
{
    /// <summary>
    /// Revalidates an aggregate plan before handing it to another core consumer.
    /// The records are intentionally internal and constructible, so object identity
    /// alone is not enough to establish that a plan was produced by <see cref="Plan"/>.
    /// </summary>
    internal static bool IsWellFormed(NativeTransferAggregateMergePlan? plan)
    {
        if (plan == null || plan.Identity == null || plan.Threads.IsDefaultOrEmpty
            || plan.Identity.ThreadCount != plan.Threads.Length
            || plan.Threads.Length > 1000
            || !NativeTransferAggregateAdmission.IsHash(plan.PackageFingerprint)
            || plan.Identity.PackageFingerprint != plan.PackageFingerprint
            || !NativeTransferAggregateAdmission.IsHash(plan.Identity.ResourceSetSha256)
            || !NativeTransferAggregateAdmission.IsHash(plan.RequestSha256)
            || !NativeTransferAggregateAdmission.IsHash(plan.ReplayKey))
            return false;

        string admissionReplay = NativeTransferAggregateAdmission.Hash(
            "native-transfer-aggregate-replay-v1", plan.PackageFingerprint, plan.Identity.ResourceSetSha256);
        HashSet<ResourceId> seen = new();
        string? previousIdentity = null;
        foreach (NativeTransferAggregateThreadMergePlan? thread in plan.Threads)
        {
            if (thread == null || thread.IncomingFence == null
                || thread.IncomingFence.Resource == null
                || NativeTransferAggregateAdmission.ValidateIdentity(thread.Resource)
                    != NativeTransferAggregateAdmissionError.None
                || thread.IncomingFence.Resource != thread.Resource
                || !seen.Add(thread.Resource))
                return false;

            string identity = NativeTransferAggregateAdmission.IdentityText(thread.Resource);
            if (previousIdentity != null
                && string.CompareOrdinal(previousIdentity, identity) >= 0) return false;
            previousIdentity = identity;

            if (!NativeTransferAggregateAdmission.IsHash(thread.IncomingFence.ContentSha256)
                || thread.IncomingFence.CaptureRevision is < 0
                || !NativeTransferAggregateAdmission.IsHash(thread.RequestSha256)
                || !NativeTransferAggregateAdmission.IsHash(thread.ReplayKey)
                || thread.ExpectedTargetFenceSha256 != null
                    && !NativeTransferAggregateAdmission.IsHash(thread.ExpectedTargetFenceSha256))
                return false;

            string incomingReplay = NativeTransferAggregateAdmission.Hash(
                "native-transfer-thread-replay-v1", plan.PackageFingerprint, identity,
                thread.IncomingFence.ContentSha256);
            string request = NativeTransferAggregateAdmission.Hash(
                "native-transfer-thread-plan-v1", admissionReplay, identity,
                thread.IncomingFence.ContentSha256, thread.ExpectedTargetFenceSha256 ?? "<new-target>");
            if (!string.Equals(request, thread.RequestSha256, StringComparison.Ordinal)) return false;

            string replay = NativeTransferAggregateAdmission.Hash(
                "native-transfer-thread-plan-replay-v1", incomingReplay, thread.RequestSha256, incomingReplay);
            if (!string.Equals(replay, thread.ReplayKey, StringComparison.Ordinal)) return false;
        }

        string resourceSet = NativeTransferAggregateAdmission.Hash(
            "native-transfer-resource-set-v1",
            string.Join("\n", plan.Threads.Select(thread =>
                NativeTransferAggregateAdmission.IdentityText(thread.Resource))));
        if (!string.Equals(resourceSet, plan.Identity.ResourceSetSha256, StringComparison.Ordinal)) return false;

        string requestSha = NativeTransferAggregateAdmission.Hash(
            "native-transfer-aggregate-plan-v1", admissionReplay,
            string.Join("\n", plan.Threads.Select(thread => thread.RequestSha256)));
        if (!string.Equals(requestSha, plan.RequestSha256, StringComparison.Ordinal)) return false;

        string replayKey = NativeTransferAggregateAdmission.Hash(
            "native-transfer-aggregate-plan-replay-v1", admissionReplay, requestSha);
        return string.Equals(replayKey, plan.ReplayKey, StringComparison.Ordinal);
    }

    internal static NativeTransferAggregateMergePlanResult Plan(
        NativeTransferAggregateAdmissionSnapshot? admission,
        IEnumerable<NativeTransferAggregateTargetFence>? targetFences = null)
    {
        if (admission == null || admission.Threads.IsDefault || admission.Threads.IsEmpty
            || admission.Identity.ThreadCount != admission.Threads.Length
            || !NativeTransferAggregateAdmission.IsHash(admission.PackageFingerprint)
            || !NativeTransferAggregateAdmission.IsHash(admission.Identity.ResourceSetSha256)
            || admission.Identity.PackageFingerprint != admission.PackageFingerprint)
            return Fail(NativeTransferAggregateMergePlanError.InvalidAdmission);

        Dictionary<ResourceId, string?> expected = new();
        if (targetFences != null)
        {
            try
            {
                foreach (NativeTransferAggregateTargetFence? target in targetFences)
                {
                    if (target == null) return Fail(NativeTransferAggregateMergePlanError.InvalidTargetIdentity);
                    NativeTransferAggregateAdmissionError identity = NativeTransferAggregateAdmission.ValidateIdentity(target.Resource);
                    if (identity == NativeTransferAggregateAdmissionError.RootlessThread)
                        return Fail(NativeTransferAggregateMergePlanError.RootlessTarget);
                    if (identity != NativeTransferAggregateAdmissionError.None)
                        return Fail(NativeTransferAggregateMergePlanError.InvalidTargetIdentity);
                    if (!admission.Threads.Any(thread => thread.Resource == target.Resource))
                        return Fail(NativeTransferAggregateMergePlanError.UnknownTarget);
                    if (!expected.TryAdd(target.Resource, target.ExpectedFenceSha256))
                        return Fail(NativeTransferAggregateMergePlanError.DuplicateTargetFence);
                    if (target.ExpectedFenceSha256 != null && !NativeTransferAggregateAdmission.IsHash(target.ExpectedFenceSha256))
                        return Fail(NativeTransferAggregateMergePlanError.InvalidTargetFence);
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { return Fail(NativeTransferAggregateMergePlanError.InvalidTargetIdentity); }
        }

        ImmutableArray<NativeTransferAggregateThreadMergePlan>.Builder threads = ImmutableArray.CreateBuilder<NativeTransferAggregateThreadMergePlan>();
        foreach (NativeTransferAggregateThreadAdmission entry in admission.Threads.OrderBy(thread => NativeTransferAggregateAdmission.IdentityText(thread.Resource), StringComparer.Ordinal))
        {
            expected.TryGetValue(entry.Resource, out string? targetFence);
            string request = NativeTransferAggregateAdmission.Hash("native-transfer-thread-plan-v1",
                admission.ReplayKey, NativeTransferAggregateAdmission.IdentityText(entry.Resource), entry.Fence.ContentSha256,
                targetFence ?? "<new-target>");
            string replay = NativeTransferAggregateAdmission.Hash("native-transfer-thread-plan-replay-v1", entry.ReplayKey,
                request, entry.ReplayKey);
            threads.Add(new(entry.Resource, entry.Fence, targetFence, request, replay));
        }
        string requestSha = NativeTransferAggregateAdmission.Hash("native-transfer-aggregate-plan-v1", admission.ReplayKey,
            string.Join("\n", threads.Select(thread => thread.RequestSha256)));
        string replayKey = NativeTransferAggregateAdmission.Hash("native-transfer-aggregate-plan-replay-v1", admission.ReplayKey, requestSha);
        NativeTransferAggregateMergePlan plan = new(admission.Identity, admission.PackageFingerprint,
            requestSha, replayKey, threads.ToImmutable());
        return IsWellFormed(plan)
            ? new(plan, NativeTransferAggregateMergePlanError.None)
            : Fail(NativeTransferAggregateMergePlanError.InvalidAdmission);
    }

    private static NativeTransferAggregateMergePlanResult Fail(NativeTransferAggregateMergePlanError error) => new(null, error);
}
