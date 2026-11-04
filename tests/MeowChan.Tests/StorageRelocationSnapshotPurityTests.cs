using System.Text.Json;
using MeowChan.Storage;

namespace MeowChan.Tests;

[Collection(StorageRelocationRootCollection.Name)]
public sealed class StorageRelocationSnapshotPurityTests
{
    [Theory]
    [InlineData(0, (int)StorageRelocationFindingCode.EqualSourceAndDestination)]
    [InlineData(1, (int)StorageRelocationFindingCode.DestinationNestedUnderSource)]
    [InlineData(2, (int)StorageRelocationFindingCode.SourceNestedUnderDestination)]
    public void BlockedOverlapPreservesWholeTreeBytesAndPersistedTargetAssignments(
        int relation, int expectedFinding)
    {
        using TempDirectory workspace = new();
        string outer = Path.Combine(workspace.Path, "media");
        string inner = Path.Combine(outer, "nested-root");
        Directory.CreateDirectory(inner);
        string source = relation == 2 ? inner : outer;
        string destination = relation == 0 ? outer : relation == 1 ? inner : outer;
        string original = Path.Combine(source, "original", "image.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllBytes(original, new byte[] { 0, 255, 128, 13, 10, 0, 42 });
        Directory.CreateDirectory(Path.Combine(inner, "empty-directory"));

        // Opaque synthetic persisted payloads, not live stores or valid SQLite databases.
        // Inspect accepts only paths/evidence: no profile, thread, queue or DB is opened.
        foreach (string root in new[] { workspace.Path, outer, inner })
        {
            foreach (string name in new[] { "settings.json", "session.json", "archive.sqlite",
                         "work-state.sqlite", "private-tags.json", ".meowchan-profile.protocol",
                         ".meowchan-manifest.json" })
                File.WriteAllBytes(Path.Combine(root, name),
                    System.Text.Encoding.UTF8.GetBytes("synthetic:" + root + ":" + name));
        }
        string assignmentsPath = Path.Combine(workspace.Path, "downloaded-targets.json");
        Dictionary<string, string> assignments = new(StringComparer.Ordinal)
        {
            ["original:image"] = original,
            ["historical:image"] = Path.Combine(outer, "historical", "image.bin")
        };
        File.WriteAllText(assignmentsPath, JsonSerializer.Serialize(assignments));
        string[] entriesBefore = Entries(workspace.Path);
        string[] filesBefore = Directory.GetFiles(workspace.Path, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        byte[][] bytesBefore = filesBefore.Select(File.ReadAllBytes).ToArray();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            StorageRelocationPreflightResult result = StorageRelocationPreflight.Inspect(
                new StorageRelocationPreflightRequest(source, destination,
                    new(StorageRelocationActiveStateEvidenceKind.CooperativeProducersQuiescent,
                        "synthetic observation; never relocation authority")));

            Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
            Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
            Assert.Contains(result.Findings, finding => finding.Code == (StorageRelocationFindingCode)expectedFinding);
            Assert.False(result.HasAllWritersProof);
            Assert.Equal(entriesBefore, Entries(workspace.Path));
            for (int index = 0; index < filesBefore.Length; index++)
                Assert.Equal(bytesBefore[index], File.ReadAllBytes(filesBefore[index]));
            Dictionary<string, string> after = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(assignmentsPath))!;
            Assert.Equal(assignments.Count, after.Count);
            foreach (var assignment in assignments)
                Assert.Equal(assignment.Value, after[assignment.Key]);
        }
    }

    private static string[] Entries(string root) => Directory
        .GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).ToArray();
}
