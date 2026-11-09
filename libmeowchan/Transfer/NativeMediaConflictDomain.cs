using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

// WP3 is deliberately a passive, content-free domain slice. These values describe
// an inventory and a user's intended decision; none of them is an import command,
// store lease, receipt, or authority to mutate the active archive.
internal enum NativeMediaConflictVariantStatus
{
    Active = 1,
    Inactive = 2
}

internal enum NativeMediaConflictResolutionStatus
{
    Open = 1,
    Kept = 2,
    Resolved = 3,
    Discarded = 4,
    Promoted = 5
}

internal enum NativeMediaConflictActionKind
{
    Keep = 1,
    Resolve = 2,
    Discard = 3,
    Promote = 4
}

/// <summary>
/// Operation-level provenance for a conflict inventory. It contains only stable
/// scalar identities and digests; it is not an owner/catalog fence or an Apply grant.
/// </summary>
internal sealed record NativeMediaConflictProvenance
{
    internal NativeMediaConflictProvenance(Guid operationId, string requestSha256, string resourceSetSha256)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A nonempty operation identity is required.", nameof(operationId));
        NativeMediaConflictDomainValidation.ValidateSha256(requestSha256, nameof(requestSha256));
        NativeMediaConflictDomainValidation.ValidateSha256(resourceSetSha256, nameof(resourceSetSha256));
        OperationId = operationId;
        RequestSha256 = requestSha256;
        ResourceSetSha256 = resourceSetSha256;
    }

    internal Guid OperationId { get; }
    internal string RequestSha256 { get; }
    internal string ResourceSetSha256 { get; }
}

/// <summary>
/// Stable conflict identity. The identity is based on the public resource identity,
/// an opaque occurrence digest, and target/candidate variant digests—not paths,
/// URLs, notes, or private values. Operation ids are provenance, not identity, so
/// a retry can refer to the same conflict.
/// </summary>
internal sealed record NativeMediaConflictIdentity
{
    internal NativeMediaConflictIdentity(ResourceId resource, string occurrenceSha256,
        string activeVariantSha256, string candidateVariantSha256)
    {
        ArchiveValidation.ValidateId(resource);
        NativeMediaConflictDomainValidation.ValidateSha256(occurrenceSha256, nameof(occurrenceSha256));
        NativeMediaConflictDomainValidation.ValidateSha256(activeVariantSha256, nameof(activeVariantSha256));
        NativeMediaConflictDomainValidation.ValidateSha256(candidateVariantSha256, nameof(candidateVariantSha256));
        if (string.Equals(activeVariantSha256, candidateVariantSha256, StringComparison.Ordinal))
            throw new ArgumentException("A conflict requires distinct active and candidate variants.", nameof(candidateVariantSha256));

        Resource = resource;
        OccurrenceSha256 = occurrenceSha256;
        ActiveVariantSha256 = activeVariantSha256;
        CandidateVariantSha256 = candidateVariantSha256;
        IdentitySha256 = NativeMediaConflictCanonical.Hash(
            "meowchan-media-conflict-identity-v1", resource.Source, resource.Board, resource.Thread,
            occurrenceSha256, activeVariantSha256, candidateVariantSha256);
    }

    internal ResourceId Resource { get; }
    internal string OccurrenceSha256 { get; }
    internal string ActiveVariantSha256 { get; }
    internal string CandidateVariantSha256 { get; }
    internal string IdentitySha256 { get; }
}

/// <summary>Bounded metadata for one variant. No file path, URL, filename, note, or private value is allowed.</summary>
internal sealed record NativeMediaConflictVariantProjection
{
    internal NativeMediaConflictVariantProjection(string variantSha256, NativeMediaConflictVariantStatus status,
        long byteLength)
    {
        NativeMediaConflictDomainValidation.ValidateSha256(variantSha256, nameof(variantSha256));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (byteLength < 0 || byteLength > 2L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        VariantSha256 = variantSha256;
        Status = status;
        ByteLength = byteLength;
    }

    internal string VariantSha256 { get; }
    internal NativeMediaConflictVariantStatus Status { get; }
    internal long ByteLength { get; }
}

/// <summary>
/// One content-free conflict row. The target/candidate projections carry only
/// status, size and digests; they never expose a materialization location or value.
/// </summary>
internal sealed record NativeMediaConflictEntry
{
    internal NativeMediaConflictEntry(NativeMediaConflictIdentity identity,
        NativeMediaConflictVariantProjection target, NativeMediaConflictVariantProjection candidate,
        NativeMediaConflictResolutionStatus resolution = NativeMediaConflictResolutionStatus.Open)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Enum.IsDefined(resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        if (target.VariantSha256 != identity.ActiveVariantSha256
            || candidate.VariantSha256 != identity.CandidateVariantSha256)
            throw new ArgumentException("Conflict projections do not match the conflict identity.");
        if (target.Status == candidate.Status && resolution == NativeMediaConflictResolutionStatus.Open)
            throw new ArgumentException("An open conflict must distinguish active and inactive variants.");

        Identity = identity;
        Target = target;
        Candidate = candidate;
        Resolution = resolution;
    }

    internal NativeMediaConflictIdentity Identity { get; }
    internal NativeMediaConflictVariantProjection Target { get; }
    internal NativeMediaConflictVariantProjection Candidate { get; }
    internal NativeMediaConflictResolutionStatus Resolution { get; }
}

/// <summary>
/// Immutable bounded conflict inventory. Ordering and its digest are deterministic,
/// making the inventory suitable for stale-view detection without making it executable.
/// </summary>
internal sealed class NativeMediaConflictInventory
{
    internal const int MaximumEntries = 100_000;
    internal const int MaximumPageSize = 100;

    internal NativeMediaConflictInventory(NativeMediaConflictProvenance provenance,
        IEnumerable<NativeMediaConflictEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(entries);
        var values = entries.ToArray();
        if (values.Length > MaximumEntries || values.Any(static entry => entry is null))
            throw new InvalidDataException("The conflict inventory exceeds its bounded domain.");
        if (values.Select(static entry => entry.Identity.IdentitySha256).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException("The conflict inventory contains duplicate identities.");

        Provenance = provenance;
        Entries = values.OrderBy(static entry => entry.Identity.IdentitySha256, StringComparer.Ordinal).ToImmutableArray();
        InventorySha256 = NativeMediaConflictCanonical.InventoryHash(Entries);
    }

    internal NativeMediaConflictProvenance Provenance { get; }
    internal ImmutableArray<NativeMediaConflictEntry> Entries { get; }
    internal string InventorySha256 { get; }
    internal int Count => Entries.Length;

    internal NativeMediaConflictInventoryPage ReadPage(int offset, int limit)
    {
        if (offset < 0 || offset > Entries.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 1 || limit > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        ImmutableArray<NativeMediaConflictEntry> page = Entries.Skip(offset).Take(limit).ToImmutableArray();
        int? next = offset + page.Length < Entries.Length ? offset + page.Length : null;
        return new(offset, Entries.Length, page, next, InventorySha256);
    }
}

internal sealed record NativeMediaConflictInventoryPage(int Offset, int TotalCount,
    ImmutableArray<NativeMediaConflictEntry> Entries, int? NextOffset, string InventorySha256);

/// <summary>
/// Deterministic idempotency key for a decision over one inventory snapshot. It
/// intentionally excludes timestamps and operation ids so a retried proposal gets
/// the same identity, while a changed inventory gets a different identity.
/// </summary>
internal sealed record NativeMediaConflictDecisionIdentity
{
    internal NativeMediaConflictDecisionIdentity(string conflictSha256, NativeMediaConflictActionKind action,
        string inventorySha256)
    {
        NativeMediaConflictDomainValidation.ValidateSha256(conflictSha256, nameof(conflictSha256));
        NativeMediaConflictDomainValidation.ValidateSha256(inventorySha256, nameof(inventorySha256));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        ConflictSha256 = conflictSha256;
        Action = action;
        InventorySha256 = inventorySha256;
        Value = NativeMediaConflictCanonical.Hash("meowchan-media-conflict-decision-v1",
            conflictSha256, ((int)action).ToString(System.Globalization.CultureInfo.InvariantCulture), inventorySha256);
    }

    internal string ConflictSha256 { get; }
    internal NativeMediaConflictActionKind Action { get; }
    internal string InventorySha256 { get; }
    internal string Value { get; }
}

/// <summary>
/// Passive action proposal. It records what a later owner-authorized command could
/// mean, but has no Apply method, lease, store handle, or mutation capability.
/// </summary>
internal sealed record NativeMediaConflictActionProposal
{
    internal NativeMediaConflictActionProposal(NativeMediaConflictIdentity conflict,
        NativeMediaConflictActionKind action, string inventorySha256)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        NativeMediaConflictDomainValidation.ValidateSha256(inventorySha256, nameof(inventorySha256));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        Conflict = conflict;
        Action = action;
        InventorySha256 = inventorySha256;
        DecisionIdentity = new(conflict.IdentitySha256, action, inventorySha256);
    }

    internal NativeMediaConflictIdentity Conflict { get; }
    internal NativeMediaConflictActionKind Action { get; }
    internal string InventorySha256 { get; }
    internal NativeMediaConflictDecisionIdentity DecisionIdentity { get; }
}

/// <summary>
/// Inert, content-free decision fact derived from a proposal. A recorded decision
/// is not proof that the target changed and never authorizes a future Apply.
/// </summary>
internal sealed record NativeMediaConflictDecision
{
    internal NativeMediaConflictDecision(NativeMediaConflictActionProposal proposal,
        DateTimeOffset decidedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (decidedAtUtc == default) throw new ArgumentException("A decision timestamp is required.", nameof(decidedAtUtc));
        Proposal = proposal;
        DecidedAtUtc = decidedAtUtc;
    }

    internal NativeMediaConflictActionProposal Proposal { get; }
    internal NativeMediaConflictDecisionIdentity Identity => Proposal.DecisionIdentity;
    internal DateTimeOffset DecidedAtUtc { get; }
}

internal static class NativeMediaConflictCanonical
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static string Hash(string domain, params string?[] fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (string? field in fields) Append(hash, field);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string InventoryHash(IEnumerable<NativeMediaConflictEntry> entries)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "meowchan-media-conflict-inventory-v1");
        foreach (NativeMediaConflictEntry entry in entries)
        {
            Append(hash, entry.Identity.IdentitySha256);
            Append(hash, entry.Target.VariantSha256);
            Append(hash, ((int)entry.Target.Status).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entry.Target.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entry.Candidate.VariantSha256);
            Append(hash, ((int)entry.Candidate.Status).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entry.Candidate.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, ((int)entry.Resolution).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        byte[] bytes = value is null ? [] : Utf8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value is null ? -1 : bytes.Length);
        hash.AppendData(length);
        if (bytes.Length != 0) hash.AppendData(bytes);
    }
}

internal static class NativeMediaConflictDomainValidation
{
    internal static void ValidateSha256(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!MediaIntegrityValidation.IsSha256(value) || value.Any(char.IsUpper))
            throw new ArgumentException("A canonical lowercase SHA256 identity is required.", parameterName);
    }
}

