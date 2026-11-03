using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;

namespace MeowChan.Storage;

/// <summary>
/// The bounded, read-only GP-35a path preflight. This is deliberately not an admission,
/// lease, copy, switch or recovery API. In particular, it cannot prove that all profile
/// writers are stopped; callers must not turn this result into an apply permission.
/// Reads only root/ancestor metadata; it is not a media/reference inventory. Metadata
/// calls are path-based observations, not a race-proof fence or a wall-clock I/O bound.
/// </summary>
internal static class StorageRelocationPreflight
{
    internal const int MaximumPathComponents = 256;
    internal const int MaximumPathCharacters = 32768;

    internal static StorageRelocationPreflightResult Inspect(
        StorageRelocationPreflightRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<StorageRelocationFinding> findings = new();
        string source;
        string destination;
        try
        {
            source = Normalize(request.SourceRoot, nameof(request.SourceRoot));
            destination = Normalize(request.DestinationRoot, nameof(request.DestinationRoot));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or SecurityException)
        {
            string sourceValue = BoundedInput(request.SourceRoot);
            string destinationValue = BoundedInput(request.DestinationRoot);
            findings.Add(new(StorageRelocationFindingCode.InvalidPath, sourceValue + " -> " + destinationValue,
                "Paths must be bounded, fully qualified and free of dot segments; normalization failed."));
            AddActiveStateFindings(request.ActiveStateEvidence, findings);
            return Result(sourceValue, destinationValue, StorageRelocationPathSafety.Unknown, findings);
        }

        cancellationToken.ThrowIfCancellationRequested();
        AddPathRelationFindings(source, destination, findings);
        InspectPathChain(source, StorageRelocationPathRole.Source, findings, cancellationToken);
        InspectPathChain(destination, StorageRelocationPathRole.Destination, findings, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        AddActiveStateFindings(request.ActiveStateEvidence, findings);

        StorageRelocationPathSafety pathSafety = ClassifyPathSafety(findings);
        return Result(source, destination, pathSafety, findings);
    }

    private static StorageRelocationPreflightResult Result(
        string source, string destination, StorageRelocationPathSafety pathSafety,
        List<StorageRelocationFinding> findings)
    {
        // Metadata observations cannot establish physical aliases, mounts or check-to-use
        // identity. These blockers remain even for lexically separate ordinary directories.
        findings.Add(new(StorageRelocationFindingCode.PhysicalPathIdentityUnknown, destination,
            "Physical alias/mount identity and check-to-use protection have not been established."));
        findings.Add(new(StorageRelocationFindingCode.ReferenceInventoryUnavailable, source,
            "Historical media, archive, work, session and config references have not been inventoried."));
        findings.Add(new(StorageRelocationFindingCode.CapacityAndAccessUnknown, destination,
            "No complete byte inventory, free-space reservation or write-access proof is available; no probe was written."));
        findings.Add(new(StorageRelocationFindingCode.AllWritersProofUnavailable, source,
            "No relocation authority covers all catalog, work, session, config, media and cleanup writers, readers/exports or external processes."));

        return new StorageRelocationPreflightResult(source, destination, pathSafety, findings.AsReadOnly());
    }

    private static string BoundedInput(string? value) => value == null ? string.Empty
        : value.Length <= MaximumPathCharacters ? value : value[..MaximumPathCharacters];

    private static string Normalize(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Length > MaximumPathCharacters || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A bounded absolute path is required.", parameterName);

        // GetFullPath would collapse link/../target before we inspected the link.
        // Do not silently change the intended physical traversal or use process CWD.
        int segmentStart = 0;
        for (int index = 0; index <= path.Length; index++)
        {
            if (index < path.Length && path[index] != Path.DirectorySeparatorChar
                && path[index] != Path.AltDirectorySeparatorChar) continue;
            ReadOnlySpan<char> segment = path.AsSpan(segmentStart, index - segmentStart);
            if (segment.SequenceEqual(".".AsSpan()) || segment.SequenceEqual("..".AsSpan()))
                throw new ArgumentException("Dot segments are not supported by this passive inspection.", parameterName);
            segmentStart = index + 1;
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void AddPathRelationFindings(string source, string destination,
        List<StorageRelocationFinding> findings)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (PathsEqual(source, destination))
        {
            findings.Add(new(StorageRelocationFindingCode.EqualSourceAndDestination, destination,
                "The destination is the same lexical path as the source."));
            return;
        }

        if (IsSameOrDescendant(source, destination, comparison))
        {
            findings.Add(new(StorageRelocationFindingCode.DestinationNestedUnderSource, destination,
                "The destination is inside the source root."));
        }
        else if (IsSameOrDescendant(destination, source, comparison))
        {
            findings.Add(new(StorageRelocationFindingCode.SourceNestedUnderDestination, source,
                "The source is inside the destination root."));
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(first, second, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsSameOrDescendant(string parent, string candidate, StringComparison comparison)
    {
        if (PathsEqual(parent, candidate)) return true;
        string prefix = Path.EndsInDirectorySeparator(parent)
            ? parent : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private static void InspectPathChain(string path, StorageRelocationPathRole role,
        List<StorageRelocationFinding> findings, CancellationToken cancellationToken)
    {
        List<string>? components;
        try { components = GetComponents(path, cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or SecurityException)
        {
            findings.Add(new(
                role == StorageRelocationPathRole.Source
                    ? StorageRelocationFindingCode.SourceStateUnknown
                    : StorageRelocationFindingCode.DestinationStateUnknown,
                path, "The bounded component chain could not be constructed."));
            return;
        }
        if (components == null)
        {
            findings.Add(new(StorageRelocationFindingCode.PathComponentLimitExceeded, path,
                $"The path has more than {MaximumPathComponents} components; the bounded preflight stopped."));
            return;
        }

        for (int index = 0; index < components.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string component = components[index];
            bool leaf = index == components.Count - 1;
            ComponentState state = InspectComponent(component);
            switch (state.Kind)
            {
                case ComponentKind.ExistingDirectory:
                    break;
                case ComponentKind.Link:
                    findings.Add(new(
                        role == StorageRelocationPathRole.Source
                            ? StorageRelocationFindingCode.SourceLinkComponent
                            : StorageRelocationFindingCode.DestinationLinkComponent,
                        component,
                        "A source/destination component is a symbolic link, junction or other reparse point."));
                    return; // Never inspect descendants through an observed link.
                case ComponentKind.ExistingNonDirectory:
                    findings.Add(new(
                        role == StorageRelocationPathRole.Source
                            ? StorageRelocationFindingCode.SourceNotDirectory
                            : StorageRelocationFindingCode.DestinationNotDirectory,
                        component,
                        "A required root or ancestor is not a directory."));
                    return;
                case ComponentKind.Missing:
                    AddMissingFinding(role, component, leaf, findings);
                    return;
                case ComponentKind.Unknown:
                    findings.Add(new(
                        role == StorageRelocationPathRole.Source
                            ? StorageRelocationFindingCode.SourceStateUnknown
                            : StorageRelocationFindingCode.DestinationStateUnknown,
                        component,
                        "The component could not be classified without changing or acquiring the filesystem."));
                    return;
            }
        }
    }

    private static List<string>? GetComponents(string path, CancellationToken cancellationToken)
    {
        List<string> components = new();
        for (DirectoryInfo? current = new DirectoryInfo(path); current != null; current = current.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (components.Count == MaximumPathComponents) return null;
            components.Add(Path.TrimEndingDirectorySeparator(current.FullName));
        }
        components.Reverse();
        return components;
    }

    private static ComponentState InspectComponent(string path)
    {
        DirectoryInfo info = new DirectoryInfo(path);
        try
        {
            // LinkTarget notices dangling links; attributes also classify other reparse
            // points such as junctions. Unsupported metadata access stays Unknown.
            if (info.LinkTarget != null)
                return new(ComponentKind.Link);

            // FileSystemInfo.Attributes may return the sentinel -1 for a missing entry.
            // File.GetAttributes instead throws, so a missing target is not a fake link.
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return new(ComponentKind.Link);
            return (attributes & FileAttributes.Directory) != 0
                ? new(ComponentKind.ExistingDirectory)
                : new(ComponentKind.ExistingNonDirectory);
        }
        catch (FileNotFoundException)
        {
            return new(ComponentKind.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(ComponentKind.Missing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or SecurityException)
        {
            // Do not call Directory.CreateDirectory, resolve links, or retry through another
            // path spelling. A failed classification is an unknown blocker.
            return new(ComponentKind.Unknown);
        }
    }

    private static void AddMissingFinding(StorageRelocationPathRole role, string component, bool leaf,
        List<StorageRelocationFinding> findings)
    {
        if (role == StorageRelocationPathRole.Source)
        {
            findings.Add(new(StorageRelocationFindingCode.SourceMissing, component,
                leaf ? "The source root does not exist." : "A source ancestor does not exist."));
            return;
        }

        if (!leaf)
        {
            findings.Add(new(StorageRelocationFindingCode.DestinationParentMissing, component,
                "A destination ancestor is missing; preflight cannot authorize target creation."));
        }
        else
        {
            findings.Add(new(StorageRelocationFindingCode.DestinationLeafMissing, component,
                "The destination leaf was absent when observed; it has not been created or reserved."));
        }
    }

    private static void AddActiveStateFindings(StorageRelocationActiveStateEvidence? evidence,
        List<StorageRelocationFinding> findings)
    {
        if (evidence == null || evidence.Kind is not
            (StorageRelocationActiveStateEvidenceKind.ActiveWritersObserved or
             StorageRelocationActiveStateEvidenceKind.CooperativeProducersQuiescent))
        {
            findings.Add(new(StorageRelocationFindingCode.ActiveStateEvidenceUnknown, string.Empty,
                "Active state is unknown or unsupported; no complete relocation-specific evidence is available."));
        }
        else if (evidence.Kind == StorageRelocationActiveStateEvidenceKind.ActiveWritersObserved)
        {
            findings.Add(new(StorageRelocationFindingCode.ActiveWritersObserved, string.Empty,
                "The supplied observation reports active cooperative writers."));
        }
        else
        {
            findings.Add(new(StorageRelocationFindingCode.OnlyCooperativeProducersObserved, string.Empty,
                "The supplied observation covers cooperative producers only; it is not an all-writer proof."));
        }
    }

    private static StorageRelocationPathSafety ClassifyPathSafety(
        IReadOnlyList<StorageRelocationFinding> findings)
    {
        if (findings.Any(finding => finding.Code is
            StorageRelocationFindingCode.EqualSourceAndDestination or
            StorageRelocationFindingCode.DestinationNestedUnderSource or
            StorageRelocationFindingCode.SourceNestedUnderDestination or
            StorageRelocationFindingCode.SourceLinkComponent or
            StorageRelocationFindingCode.DestinationLinkComponent or
            StorageRelocationFindingCode.SourceMissing or
            StorageRelocationFindingCode.DestinationParentMissing or
            StorageRelocationFindingCode.SourceNotDirectory or
            StorageRelocationFindingCode.DestinationNotDirectory))
            return StorageRelocationPathSafety.Unsafe;

        if (findings.Any(finding => finding.Code is
            StorageRelocationFindingCode.InvalidPath or
            StorageRelocationFindingCode.PathComponentLimitExceeded or
            StorageRelocationFindingCode.SourceStateUnknown or
            StorageRelocationFindingCode.DestinationStateUnknown))
            return StorageRelocationPathSafety.Unknown;

        return StorageRelocationPathSafety.NoLexicalOrLinkHazardObserved;
    }

    private enum StorageRelocationPathRole
    {
        Source,
        Destination
    }

    private enum ComponentKind
    {
        ExistingDirectory,
        ExistingNonDirectory,
        Link,
        Missing,
        Unknown
    }

    private readonly record struct ComponentState(ComponentKind Kind);
}

internal sealed record StorageRelocationPreflightRequest(
    string SourceRoot,
    string DestinationRoot,
    StorageRelocationActiveStateEvidence? ActiveStateEvidence = null);

internal enum StorageRelocationActiveStateEvidenceKind
{
    Unknown,
    ActiveWritersObserved,
    CooperativeProducersQuiescent
}

internal sealed record StorageRelocationActiveStateEvidence(
    StorageRelocationActiveStateEvidenceKind Kind,
    string? Source = null);

internal enum StorageRelocationPathSafety
{
    Unknown,
    Unsafe,
    // A transient metadata observation, not physical identity or apply permission.
    NoLexicalOrLinkHazardObserved
}

internal enum StorageRelocationPreflightDecision
{
    Blocked
}

internal enum StorageRelocationFindingCode
{
    InvalidPath,
    EqualSourceAndDestination,
    DestinationNestedUnderSource,
    SourceNestedUnderDestination,
    SourceLinkComponent,
    DestinationLinkComponent,
    SourceMissing,
    DestinationParentMissing,
    DestinationLeafMissing,
    SourceNotDirectory,
    DestinationNotDirectory,
    SourceStateUnknown,
    DestinationStateUnknown,
    PathComponentLimitExceeded,
    ActiveStateEvidenceUnknown,
    ActiveWritersObserved,
    OnlyCooperativeProducersObserved,
    AllWritersProofUnavailable,
    PhysicalPathIdentityUnknown,
    ReferenceInventoryUnavailable,
    CapacityAndAccessUnknown
}

internal sealed record StorageRelocationFinding(
    StorageRelocationFindingCode Code,
    string Path,
    string Detail);

internal sealed record StorageRelocationPreflightResult(
    string SourceRoot,
    string DestinationRoot,
    StorageRelocationPathSafety PathSafety,
    IReadOnlyList<StorageRelocationFinding> Findings)
{
    // This slice exposes no success/admission state, even for caller-supplied observations.
    internal StorageRelocationPreflightDecision Decision => StorageRelocationPreflightDecision.Blocked;
    internal bool HasAllWritersProof => false;
}
