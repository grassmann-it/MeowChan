using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Tags;

// Contract: local-media-occurrence-v1 (identity) and private-tag-store-v2 (persistence).
// An occurrence is one recorded place of a loose file inside an archived thread's media manifest:
// the thread that owns it, the manifest directory and the requested file name of that manifest slot.
// A revision is one manifest-published asset record (asset ID plus its recorded length and SHA-256)
// observed in that slot. Both IDs are opaque, profile-local and minted only by the private tag store,
// in the same atomic write that publishes the first assignment that needs them.
// Bytes, hashes, paths and manifest asset IDs locate or evidence an occurrence; they are never its identity.

/// <summary>Opaque, profile-local occurrence identity. Not a provider identity, path or hash.</summary>
internal readonly record struct LocalMediaOccurrenceId(Guid Value);

/// <summary>Opaque, profile-local revision identity of one occurrence. Not a hash.</summary>
internal readonly record struct LocalMediaRevisionId(Guid Value);

/// <summary>
/// Where an occurrence is recorded: owning archive thread, manifest directory (exactly as the catalog names it)
/// and the manifest's requested file name. A locator finds the registry record; it never proves continuity on its own,
/// and a different locator (moved folder, renamed slot) never resolves to an existing occurrence.
/// </summary>
internal sealed record LocalMediaLocator(ResourceId Thread, string Directory, string RequestedFileName);

/// <summary>The exact identity of a tagged loose file: occurrence plus revision. Equal bytes elsewhere are other targets.</summary>
internal readonly record struct LocalMediaTagTarget(LocalMediaOccurrenceId Occurrence, LocalMediaRevisionId Revision);

/// <summary>One local media tag assignment. Kept separate from bound attachment assignments and thread assignments.</summary>
internal sealed record PrivateLocalMediaTagAssignment(TagId TagId, LocalMediaTagTarget Target);

/// <summary>One recorded revision: the manifest asset record and the bytes it recorded when first observed.</summary>
internal sealed record LocalMediaRevision(LocalMediaRevisionId Id, string AssetId, long Length, string Sha256);

/// <summary>One registered occurrence with its revisions in observation order. Immutable.</summary>
internal sealed class LocalMediaOccurrence
{
    internal LocalMediaOccurrence(LocalMediaOccurrenceId id, LocalMediaLocator locator, IEnumerable<LocalMediaRevision> revisions)
    {
        Id = id;
        Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        Revisions = Array.AsReadOnly((revisions ?? throw new ArgumentNullException(nameof(revisions))).ToArray());
    }

    internal LocalMediaOccurrenceId Id { get; }
    internal LocalMediaLocator Locator { get; }
    internal IReadOnlyList<LocalMediaRevision> Revisions { get; }

    internal LocalMediaRevision? Find(LocalMediaRevisionId revision) => Revisions.FirstOrDefault(item => item.Id == revision);

    internal LocalMediaOccurrence WithRevision(LocalMediaRevision revision) => new(Id, Locator, Revisions.Append(revision));
}

/// <summary>
/// What a view observed for one loose file: its locator plus the exact manifest asset record and recorded bytes.
/// Syntax validation only. An observation is neither proof that the file exists nor that its bytes still match;
/// the host revalidates membership and bytes before any write that adds an assignment.
/// </summary>
internal sealed record LocalMediaObservation
{
    internal const int MaximumDirectoryLength = 4096;
    internal const int MaximumFileNameLength = 1024;

    private LocalMediaObservation(LocalMediaLocator locator, string assetId, long length, string sha256)
    {
        Locator = locator;
        AssetId = assetId;
        Length = length;
        Sha256 = sha256;
    }

    internal LocalMediaLocator Locator { get; }
    internal string AssetId { get; }
    internal long Length { get; }
    internal string Sha256 { get; }

    internal static bool TryCreate(ResourceId? thread, string? directory, string? requestedFileName, string? assetId,
        long? length, string? sha256, out LocalMediaObservation? observation)
    {
        observation = null;
        string? digest = sha256 is { Length: 64 } && sha256.All(Uri.IsHexDigit) ? sha256.ToLowerInvariant() : null;
        if (!ValidThread(thread) || !ValidText(directory, MaximumDirectoryLength) || !ValidText(requestedFileName, MaximumFileNameLength)
            || !CanonicalHex(assetId) || length is not >= 0 || digest == null) return false;
        observation = new(new LocalMediaLocator(thread!, directory!, requestedFileName!), assetId!, length.Value, digest);
        return true;
    }

    internal static bool ValidThread(ResourceId? thread)
    {
        if (thread is null) return false;
        try { ArchiveValidation.ValidateId(thread); }
        catch (ArgumentException) { return false; }
        return true;
    }

    internal static bool ValidText(string? value, int maximum) => !string.IsNullOrEmpty(value) && value.Length <= maximum
        && !value.Any(char.IsControl) && WellFormed(value);

    internal static bool CanonicalHex(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool WellFormed(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                index++;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
}

/// <summary>How an observation relates to the registry.</summary>
internal enum LocalMediaResolutionState
{
    /// <summary>No occurrence at this locator yet: a first local write registers a new occurrence.</summary>
    NewOccurrence,
    /// <summary>The occurrence exists but this asset record was not seen in it: a first local write adds a new revision.</summary>
    NewRevision,
    /// <summary>The exact occurrence and revision are registered.</summary>
    Registered,
    /// <summary>The registered revision for this asset records other bytes. Fail closed; nothing is retargeted.</summary>
    Conflict
}

internal readonly record struct LocalMediaResolution(LocalMediaResolutionState State, LocalMediaTagTarget? Target)
{
    internal bool IsRegistered => State == LocalMediaResolutionState.Registered;
}

/// <summary>Immutable registry view with exact locator and ID indexes. Pure; no IO.</summary>
internal sealed class LocalMediaRegistry
{
    private readonly Dictionary<LocalMediaLocator, LocalMediaOccurrence> _byLocator;
    private readonly Dictionary<LocalMediaOccurrenceId, LocalMediaOccurrence> _byId;

    internal LocalMediaRegistry(IEnumerable<LocalMediaOccurrence> occurrences)
    {
        Occurrences = new ReadOnlyCollection<LocalMediaOccurrence>((occurrences ?? throw new ArgumentNullException(nameof(occurrences))).ToArray());
        _byLocator = Occurrences.ToDictionary(item => item.Locator);
        _byId = Occurrences.ToDictionary(item => item.Id);
    }

    internal static LocalMediaRegistry Empty { get; } = new(Array.Empty<LocalMediaOccurrence>());

    internal IReadOnlyList<LocalMediaOccurrence> Occurrences { get; }

    internal LocalMediaOccurrence? Find(LocalMediaOccurrenceId id) => _byId.GetValueOrDefault(id);

    internal LocalMediaOccurrence? Find(LocalMediaLocator locator) => _byLocator.GetValueOrDefault(locator);

    /// <summary>True when the target names a registered revision of a registered occurrence.</summary>
    internal bool Contains(LocalMediaTagTarget target) => Find(target.Occurrence)?.Find(target.Revision) != null;

    internal LocalMediaResolution Resolve(LocalMediaObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return Resolve(Find(observation.Locator), observation);
    }

    internal static LocalMediaResolution Resolve(LocalMediaOccurrence? occurrence, LocalMediaObservation observation)
    {
        if (occurrence == null) return new(LocalMediaResolutionState.NewOccurrence, null);
        LocalMediaRevision? revision = occurrence.Revisions.FirstOrDefault(item => item.AssetId == observation.AssetId);
        if (revision == null) return new(LocalMediaResolutionState.NewRevision, null);
        // Asset IDs include the digest, so differing evidence means a damaged or foreign record: never adopt it.
        if (revision.Length != observation.Length || revision.Sha256 != observation.Sha256)
            return new(LocalMediaResolutionState.Conflict, null);
        return new(LocalMediaResolutionState.Registered, new LocalMediaTagTarget(occurrence.Id, revision.Id));
    }
}

/// <summary>
/// The one discriminated media tag target used by every view: either the unchanged exact bound attachment
/// version, or a loose local file. Bound and local targets are different namespaces; nothing is copied,
/// merged or inherited between them.
/// </summary>
internal abstract record MediaTagSubject
{
    private MediaTagSubject() { }

    /// <summary>The exact bound post attachment version (private-tag-store v1 target, unchanged).</summary>
    internal sealed record Bound(PrivateMediaTagTarget Target) : MediaTagSubject;

    /// <summary>
    /// A loose local file as observed. <see cref="Expected"/> is the registered target the caller saw, or null when it
    /// saw no registration yet. A write rejects (stale) when the registry now resolves the observation to anything else.
    /// </summary>
    internal sealed record Local(LocalMediaObservation Observation, LocalMediaTagTarget? Expected) : MediaTagSubject;
}
