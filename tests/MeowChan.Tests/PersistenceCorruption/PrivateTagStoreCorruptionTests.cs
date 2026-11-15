using System.Security.Cryptography;
using System.Text;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>private-tags.json, version 1 (thread and bound media assignments only).</summary>
internal class PrivateTagStoreAdapter : StoreAdapter
{
    internal static readonly ResourceId Thread = new("source", "board", "42");
    internal static readonly ResourceId OtherThread = new("source", "board", "43");

    internal override string Name => "private-tags-v1";
    internal override string PrimaryName => PrivateTagStore.FileName;
    internal override string VersionProperty => "Version";
    internal override long ReadCeilingBytes => PrivateTagStore.MaximumBytes;

    internal override void Seed(string directory, ProfileLease lease)
    {
        PrivateTagStore store = new(directory, lease);
        TagId cat = store.Define("cat-" + Secret).Tag;
        store.SetThread(cat, Thread, true);
    }

    internal override ReadResult Read(string directory, ProfileLease lease) => ReadResult.Guard(() =>
    {
        PrivateTagStoreSnapshot snapshot = new PrivateTagStore(directory, lease).ReadSnapshot();
        return snapshot.Revision + ":v" + snapshot.FormatVersion + ":" + string.Join("|", snapshot.Tags.Select(tag => tag.Name.Display))
            + "#" + snapshot.ThreadAssignments.Count + "," + snapshot.MediaAssignments.Count + "," + snapshot.LocalAssignments.Count;
    });

    internal override void Mutate(string directory, ProfileLease lease) => new PrivateTagStore(directory, lease).Define("dog");
}

/// <summary>private-tags.json, version 2 (loose-file registry); its permanent v1 copy is <c>private-tags.v1.bak</c>.</summary>
internal sealed class PrivateTagStoreV2Adapter : PrivateTagStoreAdapter
{
    internal override string Name => "private-tags-v2";

    private static string Hex(string seed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    internal static LocalMediaObservation Observe(string file)
    {
        Assert.True(LocalMediaObservation.TryCreate(Thread, "/media/42", file, Hex(file), 3, Hex("bytes-" + file), out LocalMediaObservation? observation));
        return observation!;
    }

    internal override void Seed(string directory, ProfileLease lease)
    {
        base.Seed(directory, lease);
        PrivateTagStore store = new(directory, lease);
        TagId cat = store.ReadSnapshot().Tags[0].Id;
        // The first local assignment upgrades the document to v2 and keeps the exact v1 bytes next to it.
        store.SetMediaSubjects(cat, [new MediaTagSubject.Local(Observe("a.png"), null)], true);
        store.SetMediaSubjects(cat, [new MediaTagSubject.Local(Observe("b.png"), null)], true);
    }
}

public sealed class PrivateTagStoreV1CorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new PrivateTagStoreAdapter();
}

public sealed class PrivateTagStoreV2CorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new PrivateTagStoreV2Adapter();

    private static byte[] SeedV1(string directory, ProfileLease lease)
    {
        new PrivateTagStoreAdapter().Seed(directory, lease);
        return File.ReadAllBytes(Path.Combine(directory, PrivateTagStore.FileName));
    }

    private static void FirstLocalWrite(string directory, ProfileLease lease)
    {
        PrivateTagStore store = new(directory, lease);
        store.SetMediaSubjects(store.ReadSnapshot().Tags[0].Id, [new MediaTagSubject.Local(PrivateTagStoreV2Adapter.Observe("c.png"), null)], true);
    }

    [Fact]
    public void ThePermanentV1CopyIsKeptByTheUpgradeAndSurvivesLaterWrites()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        byte[] v1 = SeedV1(temp.Path, lease);
        FirstLocalWrite(temp.Path, lease);
        string keep = Path.Combine(temp.Path, PrivateTagStore.V1BackupFileName);
        Assert.Equal(v1, File.ReadAllBytes(keep));
        new PrivateTagStore(temp.Path, lease).Define("later");
        Assert.Equal(v1, File.ReadAllBytes(keep));
    }

    [Theory]
    [MemberData(nameof(Damage.Primary), MemberType = typeof(Damage))]
    public void ADamagedV2PrimaryNeverTouchesThePermanentV1Copy(string variant)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        byte[] v1 = SeedV1(temp.Path, lease);
        FirstLocalWrite(temp.Path, lease);
        string primary = Path.Combine(temp.Path, PrivateTagStore.FileName);
        string keep = Path.Combine(temp.Path, PrivateTagStore.V1BackupFileName);
        byte[] damaged = Damage.Make(variant, File.ReadAllBytes(primary), "Version");
        File.WriteAllBytes(primary, damaged);
        byte[] backup = File.ReadAllBytes(primary + ".bak");

        ReadResult read = Adapter.Read(temp.Path, lease);
        if (read.Kind != ReadKind.Data)
            Assert.Throws<InvalidDataException>(() => new PrivateTagStore(temp.Path, lease).Define("blocked"));
        Assert.Equal(v1, File.ReadAllBytes(keep));
        if (read.Kind != ReadKind.Data)
        {
            Assert.Equal(damaged, File.ReadAllBytes(primary));
            Assert.Equal(backup, File.ReadAllBytes(primary + ".bak"));
        }
    }

    [Theory]
    [MemberData(nameof(Damage.Primary), MemberType = typeof(Damage))]
    public void ADamagedV1PrimaryMakesNoV1CopyAndFailsTheUpgrade(string variant)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        SeedV1(temp.Path, lease);
        string primary = Path.Combine(temp.Path, PrivateTagStore.FileName);
        byte[] damaged = Damage.Make(variant, File.ReadAllBytes(primary), "Version");
        File.WriteAllBytes(primary, damaged);
        byte[] backup = File.ReadAllBytes(primary + ".bak");
        if (Adapter.Read(temp.Path, lease).Kind == ReadKind.Data) return; // still the same valid document

        Assert.Throws<InvalidDataException>(() => FirstLocalWrite(temp.Path, lease));
        Assert.False(File.Exists(Path.Combine(temp.Path, PrivateTagStore.V1BackupFileName)));
        Assert.Equal(damaged, File.ReadAllBytes(primary));
        Assert.Equal(backup, File.ReadAllBytes(primary + ".bak"));
    }

    [Fact]
    public void ADirectoryAtThePermanentV1NameIsLeftAloneByTheUpgrade() =>
        PermanentV1NameIsLeftAlone("directory");

    [SymbolicLinkTheory]
    [InlineData("symlink")]
    [InlineData("dangling-symlink")]
    public void ALinkAtThePermanentV1NameIsLeftAloneByTheUpgrade(string kind) =>
        PermanentV1NameIsLeftAlone(kind);

    private void PermanentV1NameIsLeftAlone(string kind)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        byte[] v1 = SeedV1(temp.Path, lease);
        string keep = Path.Combine(temp.Path, PrivateTagStore.V1BackupFileName);
        string outside = Path.Combine(temp.Path, "..", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            string target = Path.Combine(outside, "own.json");
            byte[] ownBytes = Encoding.UTF8.GetBytes("{\"mine\":true}");
            switch (kind)
            {
                case "directory": Directory.CreateDirectory(keep); File.WriteAllText(Path.Combine(keep, "marker.txt"), "keep me"); break;
                case "symlink": File.WriteAllBytes(target, ownBytes); File.CreateSymbolicLink(keep, target); break;
                default: File.CreateSymbolicLink(keep, Path.Combine(outside, "missing.json")); break;
            }

            // The upgrade proceeds (the entry is "occupied", as the contract says) and does not write through or into it.
            FirstLocalWrite(temp.Path, lease);

            Assert.Equal(2, new PrivateTagStore(temp.Path, lease).ReadSnapshot().FormatVersion);
            if (kind == "directory") Assert.Equal("keep me", File.ReadAllText(Path.Combine(keep, "marker.txt")));
            else if (kind == "symlink") Assert.Equal(ownBytes, File.ReadAllBytes(target));
            else Assert.False(File.Exists(Path.Combine(outside, "missing.json")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path, "*.tmp"));
            Assert.NotEmpty(v1);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }
}
