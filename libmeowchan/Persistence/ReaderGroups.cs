using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using MeowChan.Archive;

namespace MeowChan.Persistence;

internal sealed record ReaderGroup(Guid Id, string Name, IReadOnlyList<ResourceId> Members);
internal sealed record ReaderGroupsSnapshot(int SchemaVersion, long Revision, IReadOnlyList<ReaderGroup> Groups)
{
    public static ReaderGroupsSnapshot Empty => new(1, 0, Array.Empty<ReaderGroup>());
}
internal enum ReaderGroupsLoadState { Ready, RecoveryAvailable, Invalid, FutureVersion }
internal sealed record ReaderGroupsLoadResult(ReaderGroupsSnapshot? Snapshot, ReaderGroupsLoadState State,
    string FilePath, string? Error = null, string? RecoveryToken = null)
{
    public bool CanWrite => State == ReaderGroupsLoadState.Ready;
}
internal sealed class ReaderGroupsConflictException : InvalidOperationException
{
    public ReaderGroupsConflictException() : base("Reader groups changed. Reload the saved groups before applying this draft.") { }
}

internal static class ReaderGroupsValidation
{
    public const int MaximumGroups = 500;
    public const int MaximumMembers = 1000;
    public const int MaximumMemberships = 20000;
    public const int MaximumBytes = 16 * 1024 * 1024;

    public static ReaderGroupsSnapshot Copy(ReaderGroupsSnapshot snapshot, bool normalizeNames = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != 1 || snapshot.Revision < 0 || snapshot.Revision == long.MaxValue)
            throw new InvalidDataException("Invalid reader groups schema or revision.");
        if (snapshot.Groups == null || snapshot.Groups.Count > MaximumGroups)
            throw new InvalidDataException($"At most {MaximumGroups} reader groups are allowed.");
        HashSet<Guid> ids = new();
        List<ReaderGroup> groups = new();
        int count = 0;
        foreach (ReaderGroup group in snapshot.Groups)
        {
            if (group == null || group.Id == Guid.Empty || !ids.Add(group.Id))
                throw new InvalidDataException("Reader group IDs must be unique nonempty GUIDs.");
            string name = normalizeNames ? group.Name?.Trim() ?? "" : group.Name ?? "";
            if (name != name.Trim() || name.Length == 0 || name.EnumerateRunes().Count() > 120 || name.Any(char.IsControl))
                throw new InvalidDataException("A reader group name needs 1–120 characters and no control characters.");
            if (group.Members == null || group.Members.Count > MaximumMembers || (count += group.Members.Count) > MaximumMemberships)
                throw new InvalidDataException($"At most {MaximumMembers} members per group and {MaximumMemberships} memberships per profile are allowed.");
            ResourceId[] members = group.Members.ToArray();
            HashSet<ResourceId> unique = new();
            foreach (ResourceId member in members)
            {
                ArchiveValidation.ValidateId(member);
                if (!unique.Add(member)) throw new InvalidDataException("A thread may occur only once in a reader group.");
            }
            groups.Add(new ReaderGroup(group.Id, name, Array.AsReadOnly(members)));
        }
        return snapshot with { Groups = Array.AsReadOnly(groups.ToArray()) };
    }

    public static bool SameGroups(IEnumerable<ReaderGroup> left, IEnumerable<ReaderGroup> right) =>
        left.Zip(right, (a, b) => a.Id == b.Id && a.Name == b.Name && a.Members.SequenceEqual(b.Members)).All(x => x)
        && left.Count() == right.Count();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ReaderGroupsSnapshot))]
internal partial class ReaderGroupsJsonContext : JsonSerializerContext { }
