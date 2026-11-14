using System.Text;
using MeowChan.Persistence;

namespace MeowChan.Tests;

/// <summary>
/// PR1 (profiles): the list of known profiles outside every profile, the durable profile id inside each profile, and the
/// lease probe that keeps one window per profile. Everything runs in private temporary folders, never in a real profile.
/// </summary>
public sealed class ProfileRegistryTests
{
    private static ProfileRegistry Registry(TempDirectory temp) => new(Path.Combine(temp.Path, "config", "MeowChan", ProfileRegistry.FileName));

    private static string Folder(TempDirectory temp, string name) => Path.Combine(temp.Path, name);

    [Fact]
    public void AMissingListIsEmptyAndReadingItWritesNothing()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        ProfileRegistrySnapshot snapshot = registry.Read();
        Assert.Equal(ProfileRegistryState.Missing, snapshot.State);
        Assert.Empty(snapshot.Entries);
        Assert.False(Directory.Exists(Path.GetDirectoryName(registry.FilePath)));
    }

    [Fact]
    public void RegisterAddsThenUpdatesOneEntryPerFolderAndKeepsWhatAnUpdateDoesNotName()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        string folder = Folder(temp, "collection");
        Guid id = Guid.NewGuid();
        DateTimeOffset first = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(ProfileRegistryChange.Added, registry.Register(folder, "My collection", null, null, 32));
        Assert.Equal(ProfileRegistryChange.Updated, registry.Register(folder + Path.DirectorySeparatorChar, null, id, first, 32));
        ProfileRegistryEntry entry = Assert.Single(registry.Read().Entries);
        Assert.Equal(("My collection", Path.GetFullPath(folder), (Guid?)id, (DateTimeOffset?)first), (entry.Name, entry.Path, entry.Id, entry.LastOpenedUtc));
        // An update without an id keeps the known one; a later opening time replaces the older one.
        DateTimeOffset second = first.AddHours(1);
        registry.Register(folder, null, null, second, 32);
        entry = Assert.Single(registry.Read().Entries);
        Assert.Equal((id, second), (entry.Id, entry.LastOpenedUtc));
        Assert.Equal(ProfileRegistryState.Valid, registry.Read().State);
    }

    [Fact]
    public void TheLimitStopsNewEntriesButNeverDropsExistingOnes()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        for (int index = 0; index < 3; index++)
            Assert.Equal(ProfileRegistryChange.Added, registry.Register(Folder(temp, "p" + index), "P" + index, null, null, 3));
        Assert.Equal(ProfileRegistryChange.LimitReached, registry.Register(Folder(temp, "p3"), "P3", null, null, 3));
        // Lowering the limit keeps all three, and a listed folder can still be updated.
        Assert.Equal(ProfileRegistryChange.Updated, registry.Register(Folder(temp, "p0"), "Renamed", null, null, 1));
        Assert.Equal(3, registry.Read().Entries.Count);
        Assert.Equal(ProfileRegistryChange.LimitReached, registry.Register(Folder(temp, "p4"), null, null, null, 1));
        Assert.Equal(new[] { "Renamed", "P1", "P2" }, registry.Read().Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void ForgetRemovesOnlyTheEntryAndNeverTheFolder()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        string folder = Folder(temp, "kept");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");
        registry.Register(folder, "Kept", null, null, 32);
        Assert.True(registry.Forget(folder));
        Assert.False(registry.Forget(folder));
        Assert.Empty(registry.Read().Entries);
        Assert.True(File.Exists(Path.Combine(folder, "settings.json")));
    }

    [Fact]
    public void EveryWriteKeepsTheLastGoodListAsBackupAndADamagedListFallsBackToIt()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        registry.Register(Folder(temp, "a"), "A", null, null, 32);
        registry.Register(Folder(temp, "b"), "B", null, null, 32);
        Assert.True(File.Exists(registry.FilePath + ".bak"));
        File.WriteAllText(registry.FilePath, "{ not json");
        ProfileRegistrySnapshot recovered = registry.Read();
        Assert.Equal(ProfileRegistryState.RecoveredFromBackup, recovered.State);
        Assert.Equal("A", Assert.Single(recovered.Entries).Name);
        // The next write keeps a copy of the damaged file and does not rotate it into the backup.
        registry.Register(Folder(temp, "c"), "C", null, null, 32);
        Assert.Equal(new[] { "A", "C" }, registry.Read().Entries.Select(entry => entry.Name));
        string directory = Path.GetDirectoryName(registry.FilePath)!;
        string corrupt = Assert.Single(Directory.GetFiles(directory, ProfileRegistry.FileName + ".corrupt-*"));
        Assert.Equal("{ not json", File.ReadAllText(corrupt));
    }

    [Fact]
    public void ADamagedListWithoutBackupReadsAsEmptyAndItsBytesAreKeptOnTheNextWrite()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.FilePath)!);
        File.WriteAllText(registry.FilePath, """{"schemaVersion":1,"profiles":[{"name":"x","path":"relative","id":null,"lastOpenedUtc":null}]}""");
        Assert.Equal(ProfileRegistryState.Damaged, registry.Read().State);
        registry.Register(Folder(temp, "new"), "New", null, null, 32);
        Assert.Equal("New", Assert.Single(registry.Read().Entries).Name);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(registry.FilePath)!, ProfileRegistry.FileName + ".corrupt-*"));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"profiles":[],"extra":1}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"profiles":[]}""")]
    [InlineData("""{"schemaVersion":0,"profiles":[]}""")]
    [InlineData("""{"schemaVersion":1,"profiles":{}}""")]
    [InlineData("""{"schemaVersion":1,"profiles":[{"name":"","path":"/p","id":null,"lastOpenedUtc":null}]}""")]
    [InlineData("""{"schemaVersion":1,"profiles":[{"name":"a","path":"/p","id":"not-a-guid","lastOpenedUtc":null}]}""")]
    [InlineData("""{"schemaVersion":1,"profiles":[{"name":"a","path":"/p","id":null,"lastOpenedUtc":null,"x":1}]}""")]
    [InlineData("""{"schemaVersion":1,"profiles":[{"name":"a","path":"/p","id":null,"lastOpenedUtc":null},{"name":"b","path":"/p/","id":null,"lastOpenedUtc":null}]}""")]
    public void StrictReadingRejectsUnknownRepeatedOrInvalidContent(string json)
    {
        if (OperatingSystem.IsWindows()) json = json.Replace("\"/p", "\"C:/p", StringComparison.Ordinal);
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.FilePath)!);
        File.WriteAllText(registry.FilePath, json);
        Assert.Equal(ProfileRegistryState.Damaged, registry.Read().State);
    }

    [Fact]
    public void AListOfANewerVersionIsShownButNeverWritten()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(registry.FilePath)!);
        string folder = Folder(temp, "future").Replace("\\", "\\\\", StringComparison.Ordinal);
        string json = "{\"schemaVersion\":2,\"profiles\":[{\"name\":\"F\",\"path\":\"" + folder + "\",\"id\":null,\"lastOpenedUtc\":null,\"color\":\"red\"}],\"more\":true}";
        File.WriteAllText(registry.FilePath, json);
        ProfileRegistrySnapshot snapshot = registry.Read();
        Assert.Equal(ProfileRegistryState.Unsupported, snapshot.State);
        Assert.Equal("F", Assert.Single(snapshot.Entries).Name);
        Assert.Equal(ProfileRegistryChange.Refused, registry.Register(Folder(temp, "other"), "O", null, null, 32));
        Assert.False(registry.Forget(Folder(temp, "future")));
        Assert.Equal(json, File.ReadAllText(registry.FilePath));
    }

    [Fact]
    public void TheWrittenFileRoundTripsAndNamesNoMoreThanNameFolderIdAndTime()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        registry.Register(Folder(temp, "x"), "  Spaced\tname  ", Guid.NewGuid(), DateTimeOffset.UtcNow, 32);
        string text = File.ReadAllText(registry.FilePath, Encoding.UTF8);
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Equal("Spacedname", Assert.Single(registry.Read().Entries).Name);
    }

    [Fact]
    public void NamesAreTrimmedCappedAndFallBackToTheFolderName()
    {
        string folder = Path.Combine(Path.GetTempPath(), ".MeowChan");
        Assert.Equal("MeowChan", ProfileRegistry.NormalizeName(null, folder));
        Assert.Equal("MeowChan", ProfileRegistry.NormalizeName("   ", folder));
        Assert.Equal(ProfileRegistry.MaximumNameLength, ProfileRegistry.NormalizeName(new string('n', 200), folder).Length);
        // A surrogate pair is never split at the cap.
        string emoji = new string('a', ProfileRegistry.MaximumNameLength - 1) + "\U0001F431";
        Assert.False(char.IsHighSurrogate(ProfileRegistry.NormalizeName(emoji, folder)[^1]));
    }

    [Fact]
    public void RelativeOrOverlongFoldersAreRefused()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        Assert.Throws<ArgumentException>(() => registry.Register("relative/folder", "R", null, null, 32));
        Assert.Throws<ArgumentException>(() => registry.Register(Path.Combine(temp.Path, new string('x', ProfileRegistry.MaximumPathLength)), "L", null, null, 32));
        Assert.Throws<ArgumentException>(() => new ProfileRegistry("relative.json"));
    }

    [Fact]
    public void AWindowThatHoldsTheListLockMakesAnotherWaitAndThenGiveUpWithoutWriting()
    {
        using TempDirectory temp = new();
        ProfileRegistry registry = Registry(temp);
        registry.Register(Folder(temp, "a"), "A", null, null, 32);
        using (FileStream held = new(registry.FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => registry.Register(Folder(temp, "b"), "B", null, null, 32));
        Assert.Single(registry.Read().Entries);
        Assert.Equal(ProfileRegistryChange.Added, registry.Register(Folder(temp, "b"), "B", null, null, 32));
    }

    [Fact]
    public void TheDefaultLocationIsOutsideTheDefaultProfiles()
    {
        string? path = ProfileRegistry.DefaultFilePath();
        Assert.NotNull(path);
        Assert.Equal(ProfileRegistry.FileName, Path.GetFileName(path));
        Assert.Equal("MeowChan", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void TheProfileIdIsCreatedOnceInsideTheProfileAndNeverReplaced()
    {
        using TempDirectory temp = new();
        string folder = Folder(temp, "profile");
        Assert.Null(ProfileInstanceIdentity.TryRead(folder));
        ProfileInstanceIdentity first, second;
        using (ProfileLease lease = ProfileLease.Acquire(folder))
        {
            first = ProfileInstanceIdentity.ReadOrCreate(lease);
            second = ProfileInstanceIdentity.ReadOrCreate(lease);
        }
        Assert.Equal(first, second);
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Equal(first, ProfileInstanceIdentity.TryRead(folder));
        string text = File.ReadAllText(Path.Combine(folder, ProfileInstanceIdentity.FileName));
        Assert.DoesNotContain(temp.Path, text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder, ProfileInstanceIdentity.FileName + ".pending-*"));
        // A copied profile keeps the id; only the path fingerprint tells the copy apart.
        string copy = Folder(temp, "copy");
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(folder, ProfileInstanceIdentity.FileName), Path.Combine(copy, ProfileInstanceIdentity.FileName));
        Assert.Equal(first.Id, ProfileInstanceIdentity.TryRead(copy)!.Id);
        Assert.NotEqual(ProfileInstanceIdentity.PathFingerprint(folder), ProfileInstanceIdentity.PathFingerprint(copy));
        Assert.Equal(ProfileInstanceIdentity.PathFingerprint(folder), ProfileInstanceIdentity.PathFingerprint(folder + Path.DirectorySeparatorChar));
        Assert.Equal(64, ProfileInstanceIdentity.PathFingerprint(folder).Length);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("""{"schemaVersion":2,"id":"6f1c2b8e-3a5d-4c7e-9f00-1234567890ab","createdUtc":"2026-11-14T20:00:00.0000000+00:00"}""")]
    [InlineData("""{"schemaVersion":1,"id":"00000000-0000-0000-0000-000000000000","createdUtc":"2026-11-14T20:00:00.0000000+00:00"}""")]
    [InlineData("""{"schemaVersion":1,"id":"6f1c2b8e-3a5d-4c7e-9f00-1234567890ab","id":"6f1c2b8e-3a5d-4c7e-9f00-1234567890ac","createdUtc":"2026-11-14T20:00:00.0000000+00:00"}""")]
    [InlineData("""{"schemaVersion":1,"id":"6f1c2b8e-3a5d-4c7e-9f00-1234567890ab","createdUtc":"2026-11-14T20:00:00.0000000+00:00","extra":1}""")]
    public void ADamagedOrNewerProfileIdIsReportedAndKeptAsItIs(string json)
    {
        using TempDirectory temp = new();
        string folder = Folder(temp, "profile");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, ProfileInstanceIdentity.FileName);
        File.WriteAllText(file, json);
        Assert.Throws<InvalidDataException>(() => ProfileInstanceIdentity.TryRead(folder));
        using (ProfileLease lease = ProfileLease.Acquire(folder))
            Assert.Throws<InvalidDataException>(() => ProfileInstanceIdentity.ReadOrCreate(lease));
        Assert.Equal(json, File.ReadAllText(file));
    }

    [Fact]
    public void TheLeaseProbeSeesAnOpenProfileAndCreatesNothing()
    {
        using TempDirectory temp = new();
        string folder = Folder(temp, "profile");
        Assert.False(ProfileLease.IsInUse(folder));
        Assert.False(Directory.Exists(folder));
        using (ProfileLease lease = ProfileLease.Acquire(folder))
            Assert.True(ProfileLease.IsInUse(folder));
        Assert.False(ProfileLease.IsInUse(folder));
        Directory.CreateDirectory(Folder(temp, "empty"));
        Assert.False(ProfileLease.IsInUse(Folder(temp, "empty")));
        Assert.Empty(Directory.GetFileSystemEntries(Folder(temp, "empty")));
    }
}
