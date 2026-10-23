using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class SavedArchiveSearchStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "meowchan-saved-search-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "archive-searches.json");
    private ProfileLease Lease() => ProfileLease.Acquire(_directory);
    private static ArchiveSearchSpec FullDefinition() => new("search text", new[] { "A", "B" },
        new[] { new ArchiveBoardConstraint("A", "x"), new ArchiveBoardConstraint("B", "y"), new ArchiveBoardConstraint(null, "g") },
        new[] { ThreadSourceState.Active, ThreadSourceState.Archived }, true, false, ArchiveThreadSort.Title,
        ArchiveWorkFilter.Followup, new ArchiveDateRange(new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 26), "Europe/Berlin"));

    [Fact]
    public void MissingFileIsPassiveAndRoundtripRetainsExactDefinitionAcrossRestart()
    {
        Guid id = Guid.NewGuid(); ArchiveSearchSpec definition = FullDefinition();
        using (var lease = Lease())
        {
            var store = new SavedArchiveSearchStore(_directory, lease);
            Assert.Empty(store.Load().Searches); Assert.False(File.Exists(FilePath));
            var saved = store.Create(id, "  Same name  ", definition, 0);
            Assert.Equal(1, saved.Revision); Assert.Equal("Same name", Assert.Single(saved.Searches).Name);
            store.Create(Guid.NewGuid(), "Same name", new ArchiveSearchSpec(hasPosts: true), 1);
        }
        using (var lease = Lease())
        {
            var result = new SavedArchiveSearchStore(_directory, lease).Load();
            Assert.Equal(2, result.Revision); Assert.Equal(2, result.Searches.Count);
            Assert.Equal(id, result.Searches[0].Id); Assert.Equal(definition, result.Searches[0].Definition);
            string json = File.ReadAllText(FilePath);
            Assert.DoesNotContain("startInclusive", json); Assert.DoesNotContain("offset", json);
            Assert.Contains("followup", json); Assert.Contains("Europe/Berlin", json);
        }
    }
    [Fact]
    public void RenameReplaceDeleteKeepIdentityAndRequireCurrentRevision()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "first", FullDefinition(), 0);
        var renamed = store.Rename(id, "second", 1);
        Assert.Equal(FullDefinition(), renamed.Searches[0].Definition);
        Assert.Throws<SavedSearchRevisionException>(() => store.Delete(id, 1));
        Assert.Equal(id, store.Replace(id, new ArchiveSearchSpec("changed", workFilter: ArchiveWorkFilter.Stopped), 2).Searches[0].Id);
        Assert.Empty(store.Delete(id, 3).Searches); Assert.True(File.Exists(FilePath + ".bak"));
    }
    [Fact]
    public void ExternalSameRevisionChangeConflictsAndKeepsDiskBytes()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "first", new ArchiveSearchSpec(), 0);
        string changed = File.ReadAllText(FilePath).Replace("first", "other"); File.WriteAllText(FilePath, changed);
        var error = Assert.Throws<SavedSearchRevisionException>(() => store.Rename(id, "lost", 1));
        Assert.Equal("other", error.Current.Searches[0].Name); Assert.Equal(changed, File.ReadAllText(FilePath));
    }
    [Theory]
    [InlineData("future", "true")]
    [InlineData("schemaVersion", "2")]
    [InlineData("workFilter", "\"future-work\"")]
    public void UnknownDefinitionLocksWholeStoreWithoutBroaderSearchOrWrite(string key, string value)
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "one", FullDefinition(), 0);
        JsonNode root = JsonNode.Parse(File.ReadAllText(FilePath))!; root["searches"]![0]!["definition"]![key] = JsonNode.Parse(value);
        string raw = root.ToJsonString(); File.WriteAllText(FilePath, raw);
        var loaded = store.Load(); Assert.Equal(SavedSearchStoreState.Unsupported, loaded.State);
        Assert.False(loaded.CanWrite); Assert.False(loaded.Searches[0].CanApply);
        Assert.Throws<SavedSearchRevisionException>(() => store.Delete(id, loaded.Revision));
        Assert.Equal(raw, File.ReadAllText(FilePath));
    }
    [Fact]
    public void FutureEnvelopeNeverFallsBackToOlderBackup()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "one", new ArchiveSearchSpec(), 0); store.Rename(id, "two", 1);
        string raw = "{\"schemaVersion\":9,\"future\":true}"; File.WriteAllText(FilePath, raw);
        Assert.Equal(SavedSearchStoreState.Unsupported, store.Load().State);
        Assert.Throws<InvalidOperationException>(() => store.Recover(0)); Assert.Equal(raw, File.ReadAllText(FilePath));
    }
    [Fact]
    public void FutureBackupBesideValidPrimaryBlocksSaveAndNeverReplacesEitherFile()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease);
        Guid id = Guid.NewGuid(); var saved = store.Create(id, "known search", FullDefinition(), 0);
        byte[] primary = File.ReadAllBytes(FilePath);
        const string future = "{\"schemaVersion\":99,\"foreign\":true}";
        File.WriteAllText(FilePath + ".bak", future);
        var loaded = store.Load();
        Assert.Equal(SavedSearchStoreState.Ready, loaded.State);
        Assert.True(loaded.CanWrite);
        Assert.Equal(saved.Revision, loaded.Revision);
        Assert.Equal(FullDefinition(), Assert.Single(loaded.Searches).Definition);
        Assert.True(loaded.Searches[0].CanApply);
        Assert.Throws<InvalidOperationException>(() => store.Rename(id, "must not replace backup", saved.Revision));
        Assert.Equal(primary, File.ReadAllBytes(FilePath));
        Assert.Equal(future, File.ReadAllText(FilePath + ".bak"));
        Assert.Empty(Directory.GetFiles(_directory, ".archive-searches.json.*.tmp"));
    }

    [Fact]
    public void DamagedPrimaryRequiresExplicitRecoveryAndPreservesOriginal()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "one", new ArchiveSearchSpec(), 0); store.Rename(id, "two", 1);
        File.WriteAllText(FilePath, "{broken"); var recovery = store.Load();
        Assert.Equal(SavedSearchStoreState.RecoveryAvailable, recovery.State); Assert.Equal("{broken", File.ReadAllText(FilePath));
        Assert.Throws<SavedSearchRevisionException>(() => store.Delete(id, recovery.Revision));
        var restored = store.Recover(recovery.Revision); Assert.Equal("one", restored.Searches[0].Name); Assert.True(restored.CanWrite);
        Assert.Equal("{broken", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "archive-searches.json.corrupt-*"))));
    }
    [Fact]
    public void BrokenFileWithoutBackupIsNotEmptyWritableStore()
    {
        using var lease = Lease(); File.WriteAllText(FilePath, "{"); var store = new SavedArchiveSearchStore(_directory, lease);
        Assert.Equal(SavedSearchStoreState.Failed, store.Load().State);
        Assert.Throws<SavedSearchRevisionException>(() => store.Create(Guid.NewGuid(), "new", new ArchiveSearchSpec(), 0)); Assert.Equal("{", File.ReadAllText(FilePath));
    }
    [Fact]
    public void CancellationBeforeCommitLeavesLastSnapshotAndBytes()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "before", FullDefinition(), 0); string bytes = File.ReadAllText(FilePath);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Rename(id, "after", 1, canceled.Token)); Assert.Equal(bytes, File.ReadAllText(FilePath));
        Assert.Equal("before", store.Load().Searches[0].Name);
    }
    [Fact]
    public void DuplicateIdsAndInvalidNamesCannotPartiallyImport()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "one", new ArchiveSearchSpec(), 0);
        JsonNode root = JsonNode.Parse(File.ReadAllText(FilePath))!; root["searches"]!.AsArray().Add(root["searches"]![0]!.DeepClone());
        File.WriteAllText(FilePath, root.ToJsonString()); Assert.Equal(SavedSearchStoreState.Failed, store.Load().State);
        Assert.Throws<ArgumentException>(() => SavedArchiveSearchStore.ValidateName("a\nb"));
        string scalars = string.Concat(Enumerable.Repeat("😀", 120)); Assert.Equal(scalars, SavedArchiveSearchStore.ValidateName(scalars));
        Assert.Throws<ArgumentException>(() => SavedArchiveSearchStore.ValidateName(scalars + "x"));
    }
    [Fact]
    public void StoreRequiresMatchingLiveLeaseAndProfilesRemainSeparate()
    {
        using var lease = Lease(); Assert.Throws<ArgumentException>(() => new SavedArchiveSearchStore(_directory + "-other", lease));
        var store = new SavedArchiveSearchStore(_directory, lease); lease.Dispose(); Assert.Throws<ObjectDisposedException>(() => store.Load());
    }
    [Fact]
    public void InvalidKnownDefinitionOffersWholeBackupInsteadOfPartialImport()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "one", FullDefinition(), 0); store.Rename(id, "two", 1);
        JsonNode root = JsonNode.Parse(File.ReadAllText(FilePath))!;
        root["searches"]![0]!["definition"]!["dateRange"]!["startDate"] = "bad-date";
        File.WriteAllText(FilePath, root.ToJsonString());
        Assert.Equal(SavedSearchStoreState.RecoveryAvailable, store.Load().State);
        Assert.Equal("one", Assert.Single(store.Load().Searches).Name);
    }
    [Fact]
    public void UnknownZoneIsReadonlyAndNeverUsesSupportedBackup()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "one", FullDefinition(), 0); store.Rename(id, "two", 1);
        JsonNode root = JsonNode.Parse(File.ReadAllText(FilePath))!;
        root["searches"]![0]!["definition"]!["dateRange"]!["timeZoneId"] = "Unknown/Future-Zone";
        string bytes = root.ToJsonString(); File.WriteAllText(FilePath, bytes);
        var result = store.Load(); Assert.Equal(SavedSearchStoreState.Unsupported, result.State);
        Assert.Equal("two", Assert.Single(result.Searches).Name); Assert.False(result.Searches[0].CanApply);
        Assert.Equal(bytes, File.ReadAllText(FilePath));
    }
    [Fact]
    public void ChangedCorruptPrimaryRequiresAnotherRecoveryReview()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease); store.Load();
        Guid id = Guid.NewGuid(); store.Create(id, "one", new ArchiveSearchSpec(), 0); store.Rename(id, "two", 1);
        File.WriteAllText(FilePath, "{broken-first"); var reviewed = store.Load();
        File.WriteAllText(FilePath, "{broken-second");
        Assert.Throws<SavedSearchRevisionException>(() => store.Recover(reviewed.Revision));
        Assert.Equal("{broken-second", File.ReadAllText(FilePath)); Assert.Empty(Directory.GetFiles(_directory, "*.corrupt-*"));
    }
    [Fact]
    public void SearchCountLimitNeverTruncatesOrPublishesCandidate()
    {
        using var lease = Lease(); Guid id = Guid.NewGuid();
        string definition = ArchiveSearchSpecJson.Serialize(new ArchiveSearchSpec());
        JsonArray searches = new();
        for (int index = 0; index < 500; index++) searches.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "search " + index, ["definition"] = JsonNode.Parse(definition) });
        string bytes = new JsonObject { ["schemaVersion"] = 1, ["revision"] = 42, ["searches"] = searches }.ToJsonString();
        File.WriteAllText(FilePath, bytes); var store = new SavedArchiveSearchStore(_directory, lease);
        Assert.Equal(500, store.Load().Searches.Count);
        Assert.Throws<InvalidDataException>(() => store.Create(id, "overflow", new ArchiveSearchSpec(), 42));
        Assert.Equal(bytes, File.ReadAllText(FilePath)); Assert.Equal(42, store.Load().Revision);
    }
    [Fact]
    public void OversizedFileAndBadUtf8CannotBeOverwrittenAsEmpty()
    {
        using var lease = Lease(); var store = new SavedArchiveSearchStore(_directory, lease);
        File.WriteAllBytes(FilePath, new byte[SavedArchiveSearchStore.MaximumFileBytes + 1]);
        Assert.Equal(SavedSearchStoreState.Failed, store.Load().State);
        File.WriteAllBytes(FilePath, new byte[] { 0xff, 0xfe });
        Assert.Equal(SavedSearchStoreState.Failed, store.Load().State);
        Assert.Throws<SavedSearchRevisionException>(() => store.Create(Guid.NewGuid(), "new", new ArchiveSearchSpec(), 0));
        Assert.Equal(new byte[] { 0xff, 0xfe }, File.ReadAllBytes(FilePath));
    }
    [Fact]
    public void IndependentProfilesNeverReadOrWriteEachOthersDefinitions()
    {
        using var firstLease = Lease(); string otherDirectory = Path.Combine(_directory, "second-profile");
        using var secondLease = ProfileLease.Acquire(otherDirectory);
        var first = new SavedArchiveSearchStore(_directory, firstLease); var second = new SavedArchiveSearchStore(otherDirectory, secondLease);
        Guid id = Guid.NewGuid(); first.Load(); second.Load();
        first.Create(id, "profile one", new ArchiveSearchSpec("one"), 0);
        Assert.Empty(second.Load().Searches);
        second.Create(id, "profile two", new ArchiveSearchSpec("two"), 0);
        Assert.Equal("profile one", Assert.Single(first.Load().Searches).Name);
        Assert.Equal("profile two", Assert.Single(second.Load().Searches).Name);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RecoveryChecksFullySerializedOutputAtByteBoundaryAndAcrossRestart(int bytesAboveLimit)
    {
        Guid id = Guid.NewGuid();
        // Calibrate through a real small recovery, rather than duplicating the writer's formatting.
        int smallRecoveryBytes = MeasureSmallRecovery(id);
        int sourceLength = checked(SavedArchiveSearchStore.MaximumFileBytes - smallRecoveryBytes + 1 + bytesAboveLimit);
        string source = new('s', sourceLength);
        string backup = CompactRecoveryBackup(id, source);
        byte[] original = Encoding.UTF8.GetBytes("{damaged-primary");
        byte[] backupBytes = Encoding.UTF8.GetBytes(backup);
        Assert.True(backupBytes.Length <= SavedArchiveSearchStore.MaximumFileBytes);
        using (var lease = Lease())
        {
            File.WriteAllBytes(FilePath, original); File.WriteAllBytes(FilePath + ".bak", backupBytes);
            var store = new SavedArchiveSearchStore(_directory, lease); var recovery = store.Load();
            Assert.Equal(SavedSearchStoreState.RecoveryAvailable, recovery.State);
            Assert.Equal(source, Assert.Single(Assert.Single(recovery.Searches).Definition!.Sources));
            if (bytesAboveLimit == 0)
            {
                var committed = store.Recover(recovery.Revision);
                Assert.Equal(SavedSearchStoreState.Ready, committed.State); Assert.Equal(8, committed.Revision);
                Assert.Equal(SavedArchiveSearchStore.MaximumFileBytes, new FileInfo(FilePath).Length);
                Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_directory, "archive-searches.json.corrupt-*"))));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => store.Recover(recovery.Revision));
                Assert.Equal(original, File.ReadAllBytes(FilePath)); Assert.Equal(backupBytes, File.ReadAllBytes(FilePath + ".bak"));
                Assert.Empty(Directory.GetFiles(_directory, "archive-searches.json.corrupt-*"));
                Assert.Equal(SavedSearchStoreState.RecoveryAvailable, store.Load().State);
            }
        }
        // Release the original profile owner; a fresh owner must accept exactly the committed state.
        using (var restartedLease = Lease())
        {
            var restarted = new SavedArchiveSearchStore(_directory, restartedLease); var loaded = restarted.Load();
            Assert.Equal(bytesAboveLimit == 0 ? SavedSearchStoreState.Ready : SavedSearchStoreState.RecoveryAvailable, loaded.State);
            Assert.Equal(bytesAboveLimit == 0 ? 8 : 7, loaded.Revision);
            Assert.Equal(source, Assert.Single(Assert.Single(loaded.Searches).Definition!.Sources));
            Assert.Equal(backupBytes, File.ReadAllBytes(FilePath + ".bak"));
            if (bytesAboveLimit != 0)
            {
                Assert.Throws<InvalidDataException>(() => restarted.Recover(loaded.Revision));
                Assert.Equal(original, File.ReadAllBytes(FilePath));
            }
        }
    }
    [Fact]
    public void CompactBackupExactlyAtReadLimitCannotBeRecoveredIntoAnOversizedFile()
    {
        Guid id = Guid.NewGuid();
        int sourceLength = SavedArchiveSearchStore.MaximumFileBytes - Encoding.UTF8.GetByteCount(CompactRecoveryBackup(id, "s")) + 1;
        string backup = CompactRecoveryBackup(id, new string('s', sourceLength));
        byte[] backupBytes = Encoding.UTF8.GetBytes(backup);
        Assert.Equal(SavedArchiveSearchStore.MaximumFileBytes, backupBytes.Length);
        using (var lease = Lease())
        {
            File.WriteAllText(FilePath, "{damaged-primary"); File.WriteAllBytes(FilePath + ".bak", backupBytes);
            var store = new SavedArchiveSearchStore(_directory, lease); var recovery = store.Load();
            Assert.Equal(SavedSearchStoreState.RecoveryAvailable, recovery.State);
            Assert.Throws<InvalidDataException>(() => store.Recover(recovery.Revision));
            Assert.Equal("{damaged-primary", File.ReadAllText(FilePath));
            Assert.Equal(backupBytes, File.ReadAllBytes(FilePath + ".bak"));
            Assert.Empty(Directory.GetFiles(_directory, "archive-searches.json.corrupt-*"));
        }
        using (var restartedLease = Lease())
        {
            var loaded = new SavedArchiveSearchStore(_directory, restartedLease).Load();
            Assert.Equal(SavedSearchStoreState.RecoveryAvailable, loaded.State); Assert.Equal(7, loaded.Revision);
            Assert.Equal(sourceLength, Assert.Single(Assert.Single(loaded.Searches).Definition!.Sources).Length);
        }
    }
    private int MeasureSmallRecovery(Guid id)
    {
        string directory = Path.Combine(_directory, "recovery-size-calibration");
        using var lease = ProfileLease.Acquire(directory);
        string path = Path.Combine(directory, "archive-searches.json");
        File.WriteAllText(path, "{damaged-primary"); File.WriteAllText(path + ".bak", CompactRecoveryBackup(id, "s"));
        var store = new SavedArchiveSearchStore(directory, lease); var recovery = store.Load();
        Assert.Equal(SavedSearchStoreState.RecoveryAvailable, recovery.State);
        Assert.Equal(SavedSearchStoreState.Ready, store.Recover(recovery.Revision).State);
        return checked((int)new FileInfo(path).Length);
    }
    private static string CompactRecoveryBackup(Guid id, string source) => new JsonObject
    {
        ["schemaVersion"] = 1, ["revision"] = 7,
        ["searches"] = new JsonArray(new JsonObject
        {
            ["id"] = id.ToString(), ["name"] = "boundary",
            ["definition"] = JsonNode.Parse(ArchiveSearchSpecJson.Serialize(new ArchiveSearchSpec(sources: new[] { source })))
        })
    }.ToJsonString();
    [Fact]
    public void InvalidBackupCannotEscapeFeatureLocalLoadFailureOrBeOverwritten()
    {
        byte[] primary = Encoding.UTF8.GetBytes("{broken-primary");
        Guid duplicateId = Guid.NewGuid();
        JsonNode backup = JsonNode.Parse(CompactRecoveryBackup(duplicateId, "source"))!;
        backup["searches"]!.AsArray().Add(backup["searches"]![0]!.DeepClone());
        byte[] invalidBackup = Encoding.UTF8.GetBytes(backup.ToJsonString());
        using (var lease = Lease())
        {
            File.WriteAllBytes(FilePath, primary); File.WriteAllBytes(FilePath + ".bak", invalidBackup);
            var store = new SavedArchiveSearchStore(_directory, lease); var failed = store.Load();
            Assert.Equal(SavedSearchStoreState.Failed, failed.State); Assert.False(failed.CanWrite); Assert.Empty(failed.Searches);
            Assert.Throws<SavedSearchRevisionException>(() => store.Create(Guid.NewGuid(), "must not overwrite", new ArchiveSearchSpec(), failed.Revision));
            Assert.Equal(primary, File.ReadAllBytes(FilePath)); Assert.Equal(invalidBackup, File.ReadAllBytes(FilePath + ".bak"));
            Assert.Empty(Directory.GetFiles(_directory, "archive-searches.json.corrupt-*"));
        }
        using (var restartedLease = Lease())
        {
            var failed = new SavedArchiveSearchStore(_directory, restartedLease).Load();
            Assert.Equal(SavedSearchStoreState.Failed, failed.State); Assert.Empty(failed.Searches);
            Assert.Equal(primary, File.ReadAllBytes(FilePath)); Assert.Equal(invalidBackup, File.ReadAllBytes(FilePath + ".bak"));
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
