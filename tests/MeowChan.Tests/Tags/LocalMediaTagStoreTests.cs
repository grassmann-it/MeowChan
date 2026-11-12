using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

// AP-01b: loose (local) media tags in private-tag-store v2 with the local occurrence registry.
public sealed class LocalMediaTagStoreTests
{
    private const string FileName = "private-tags.json";
    private const string RevisionKey = "att-v2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly ResourceId Thread = new("source", "board", "42");
    private static readonly ResourceId OtherThread = new("source", "board", "43");

    private static string Hex(string seed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    private static LocalMediaObservation Observe(string file = "a.png", string directory = "/media/42", string? asset = null,
        long length = 3, string bytes = "same", ResourceId? thread = null)
    {
        Assert.True(LocalMediaObservation.TryCreate(thread ?? Thread, directory, file, asset ?? Hex(directory + "|" + file + "|" + bytes),
            length, Hex(bytes).ToUpperInvariant(), out LocalMediaObservation? observation));
        return observation!;
    }

    private static MediaTagSubject.Local Loose(LocalMediaObservation observation, LocalMediaTagTarget? seen = null) => new(observation, seen);

    private static PrivateMediaTagTarget Bound(string post = "100")
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread, post, 0, RevisionKey), "media-1", "original",
            TransferMediaRole.Original, Hex("bound"), out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    private static PrivateTagStoreError Rejected(Action action) => Assert.Throws<PrivateTagStoreException>(action).Error;

    private static LocalMediaTagTarget Registered(PrivateTagStoreSnapshot snapshot, LocalMediaObservation observation)
    {
        LocalMediaResolution resolution = snapshot.ResolveLocal(observation);
        Assert.Equal(LocalMediaResolutionState.Registered, resolution.State);
        return resolution.Target!.Value;
    }

    [Fact]
    public void ObservationSyntaxIsStrictAndCanonicalisesTheDigest()
    {
        LocalMediaObservation observation = Observe();
        Assert.Equal(Hex("same"), observation.Sha256);
        Assert.False(LocalMediaObservation.TryCreate(Thread, "", "a.png", Hex("x"), 1, Hex("y"), out _));
        Assert.False(LocalMediaObservation.TryCreate(Thread, "/d", "a\n.png", Hex("x"), 1, Hex("y"), out _));
        Assert.False(LocalMediaObservation.TryCreate(Thread, "/d", "a.png", Hex("x").ToUpperInvariant(), 1, Hex("y"), out _));
        Assert.False(LocalMediaObservation.TryCreate(Thread, "/d", "a.png", Hex("x"), -1, Hex("y"), out _));
        Assert.False(LocalMediaObservation.TryCreate(Thread, "/d", "a.png", Hex("x"), 1, "nothex", out _));
        Assert.False(LocalMediaObservation.TryCreate(new ResourceId("s", "b", ""), "/d", "a.png", Hex("x"), 1, Hex("y"), out _));
        Assert.False(LocalMediaObservation.TryCreate(Thread, "/d\uD800", "a.png", Hex("x"), 1, Hex("y"), out _));
    }

    [Fact]
    public void AV1FileStaysByteIdenticalUntilTheFirstLocalWriteAndKeepsEveryBoundAndThreadField()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, dog = store.Define("dog").Tag;
        store.SetThread(cat, Thread, true);
        store.SetMedia(dog, Bound(), true);
        store.SetMedia(cat, Bound("101"), true);
        string path = Path.Combine(temp.Path, FileName);
        byte[] v1 = File.ReadAllBytes(path);
        Assert.Contains("\"Version\":1", Encoding.UTF8.GetString(v1));
        PrivateTagStoreSnapshot before = store.ReadSnapshot();
        Assert.Equal(1, before.FormatVersion);

        // Reads, local no-ops and bound writes never upgrade the document.
        Assert.False(store.SetMediaSubjects(cat, [Loose(Observe())], false).Changed);
        Assert.Equal(v1, File.ReadAllBytes(path));
        Assert.Equal(LocalMediaResolutionState.NewOccurrence, store.ReadSnapshot().ResolveLocal(Observe()).State);
        Assert.Equal(v1, File.ReadAllBytes(path));

        PrivateTagCommit local = store.SetMediaSubjects(dog, [Loose(Observe())], true);

        Assert.True(local.Changed);
        Assert.Equal(2, local.Snapshot.FormatVersion);
        Assert.Equal(v1, File.ReadAllBytes(path + ".bak")); // The v1 bytes survive as the backup of the first v2 write.
        PrivateTagStoreSnapshot after = store.ReadSnapshot();
        Assert.Equal(before.Tags, after.Tags);
        Assert.Equal(before.ThreadAssignments, after.ThreadAssignments);
        Assert.Equal(before.MediaAssignments, after.MediaAssignments);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(dog, Assert.Single(after.TagsOf(Registered(after, Observe()))).Id);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(2, PrivateTagStore.ValidateDocumentBytes(File.ReadAllBytes(path)));
        Assert.Equal(1, PrivateTagStore.ValidateDocumentBytes(v1));
    }

    [Fact]
    public void V2StaysStickyWhenTheLastLocalAssignmentAndRegistrationAreGone()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        store.SetMediaSubjects(cat, [Loose(Observe())], true);
        store.SetMediaSubjects(cat, [Loose(Observe())], false);
        Assert.Equal(2, store.ReadSnapshot().FormatVersion);
        // Removing an assignment keeps the registration, so a later adoption returns the same identity.
        Assert.Single(store.ReadSnapshot().LocalRegistry.Occurrences);
        store.RemoveThreads([Thread]);
        PrivateTagStoreSnapshot gone = store.ReadSnapshot();
        Assert.Empty(gone.LocalRegistry.Occurrences);
        Assert.Equal(2, gone.FormatVersion);
        Assert.Contains("\"Version\":2", File.ReadAllText(Path.Combine(temp.Path, FileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentOccurrencesWithEqualBytesStaySeparateEverywhere(bool matchingAssetIds)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        // Same owner, bytes, length; either two slots of one manifest (different asset IDs) or the same slot
        // name in two media directories of the thread (identical manifest-local asset IDs).
        string shared = Hex("one asset id");
        LocalMediaObservation a = matchingAssetIds ? Observe("x.png", "/media/42", shared) : Observe("x.png");
        LocalMediaObservation b = matchingAssetIds ? Observe("x.png", "/media/42-legacy", shared) : Observe("y.png");
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(matchingAssetIds, a.AssetId == b.AssetId);

        PrivateTagCommit tagged = store.SetMediaSubjects(cat, [Loose(a)], true);

        Assert.Equal(cat, Assert.Single(tagged.Snapshot.TagsOf(Loose(a))).Id);
        Assert.Empty(tagged.Snapshot.TagsOf(Loose(b)));
        Assert.False(tagged.Snapshot.Carries(cat, Loose(b)));
        Assert.Equal(LocalMediaResolutionState.NewOccurrence, tagged.Snapshot.ResolveLocal(b).State);
        Assert.Equal(1, tagged.Snapshot.DistinctMediaCounts()[cat]);

        PrivateTagCommit both = store.SetMediaSubjects(cat, [Loose(b)], true);
        LocalMediaTagTarget ta = Registered(both.Snapshot, a), tb = Registered(both.Snapshot, b);
        Assert.NotEqual(ta.Occurrence, tb.Occurrence);
        Assert.NotEqual(ta.Revision, tb.Revision);
        Assert.Equal(2, both.Snapshot.DistinctMediaCounts()[cat]);

        PrivateTagCommit removed = store.SetMediaSubjects(cat, [Loose(a)], false);
        Assert.Empty(removed.Snapshot.TagsOf(ta));
        Assert.Equal(cat, Assert.Single(removed.Snapshot.TagsOf(tb)).Id);
    }

    [Fact]
    public void AdoptionReturnsTheSameIdentityAcrossInstancesAndTwoRestarts()
    {
        using TempDirectory temp = new();
        LocalMediaTagTarget first;
        TagId cat, dog;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStore store = new(temp.Path, lease);
            cat = store.Define("cat").Tag;
            dog = store.Define("dog").Tag;
            first = Registered(store.SetMediaSubjects(cat, [Loose(Observe())], true).Snapshot, Observe());
        }
        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(temp.Path);
            PrivateTagStore store = new(temp.Path, lease);
            PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
            Assert.Equal(first, Registered(snapshot, Observe()));
            Assert.Contains(snapshot.TagsOf(first), tag => tag.Id == cat);
            // Adding through a fresh, unfrozen observation adopts nothing new.
            PrivateTagCommit again = store.SetMediaSubjects(restart == 0 ? dog : cat, [Loose(Observe())], true);
            Assert.Equal(first, Registered(again.Snapshot, Observe()));
            LocalMediaOccurrence occurrence = Assert.Single(again.Snapshot.LocalRegistry.Occurrences);
            Assert.Single(occurrence.Revisions);
        }
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStoreSnapshot final = new PrivateTagStore(temp.Path, lease).ReadSnapshot();
            Assert.Equal(new[] { cat, dog }, final.TagsOf(first).Select(tag => tag.Id));
        }
    }

    [Fact]
    public void AReplacementInTheSameSlotIsANewRevisionWithoutTagsAndAStaleSubjectNeverReachesIt()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, dog = store.Define("dog").Tag;
        LocalMediaObservation original = Observe(bytes: "old");
        LocalMediaObservation replacement = Observe(bytes: "new");
        Assert.Equal(original.Locator, replacement.Locator);
        LocalMediaTagTarget old = Registered(store.SetMediaSubjects(cat, [Loose(original)], true).Snapshot, original);

        PrivateTagStoreSnapshot seen = store.ReadSnapshot();
        Assert.Equal(LocalMediaResolutionState.NewRevision, seen.ResolveLocal(replacement).State);
        Assert.Empty(seen.TagsOf(Loose(replacement))); // Nothing is inherited.

        // A dialog that still shows the old file writes to the old revision only.
        PrivateTagCommit stale = store.SetMediaSubjects(dog, [Loose(original, old)], true);
        Assert.Equal(new[] { cat, dog }, stale.Snapshot.TagsOf(old).Select(tag => tag.Id));
        Assert.Equal(LocalMediaResolutionState.NewRevision, stale.Snapshot.ResolveLocal(replacement).State);

        PrivateTagCommit fresh = store.SetMediaSubjects(dog, [Loose(replacement)], true);
        LocalMediaTagTarget next = Registered(fresh.Snapshot, replacement);
        Assert.Equal(old.Occurrence, next.Occurrence);
        Assert.NotEqual(old.Revision, next.Revision);
        Assert.Equal(dog, Assert.Single(fresh.Snapshot.TagsOf(next)).Id);
        Assert.Equal(new[] { cat, dog }, fresh.Snapshot.TagsOf(old).Select(tag => tag.Id)); // The old revision keeps its tags.
        Assert.Equal(2, Assert.Single(fresh.Snapshot.LocalRegistry.Occurrences).Revisions.Count);

        // A subject frozen to one registration is refused when the file now resolves to another one.
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));
        Assert.Equal(PrivateTagStoreError.StaleTarget, Rejected(() => store.SetMediaSubjects(cat, [Loose(replacement, old)], true)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }

    [Fact]
    public void ConflictingEvidenceForARegisteredAssetFailsClosed()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        LocalMediaObservation real = Observe();
        store.SetMediaSubjects(cat, [Loose(real)], true);
        Assert.True(LocalMediaObservation.TryCreate(Thread, real.Locator.Directory, real.Locator.RequestedFileName, real.AssetId,
            real.Length + 1, real.Sha256, out LocalMediaObservation? forged));
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        Assert.Equal(LocalMediaResolutionState.Conflict, store.ReadSnapshot().ResolveLocal(forged!).State);
        Assert.Equal(PrivateTagStoreError.InvalidTarget, Rejected(() => store.SetMediaSubjects(cat, [Loose(forged!)], true)));
        Assert.Empty(store.ReadSnapshot().TagsOf(Loose(forged!)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AFailureBeforeTheReplacePublishesNeitherIdentityNorAssignment(int failAt)
    {
        PrivateTagWritePhase phase = failAt == 0 ? PrivateTagWritePhase.BeforeBackupCheck : PrivateTagWritePhase.BeforeReplace;
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagId cat = new PrivateTagStore(temp.Path, lease).Define("cat").Tag;
        string path = Path.Combine(temp.Path, FileName);
        byte[] before = File.ReadAllBytes(path);
        PrivateTagStore failing = new(temp.Path, lease)
        { WritePhaseForTests = current => { if (current == phase) throw new IOException("injected"); } };

        Assert.Throws<IOException>(() => failing.SetMediaSubjects(cat, [Loose(Observe())], true));
        Assert.Throws<IOException>(() => failing.DefineAndSetMediaSubjects("dog", [Loose(Observe())]));

        Assert.Equal(before, File.ReadAllBytes(path));
        PrivateTagStoreSnapshot snapshot = new PrivateTagStore(temp.Path, lease).ReadSnapshot();
        Assert.Empty(snapshot.LocalRegistry.Occurrences);
        Assert.Empty(snapshot.LocalAssignments);
        Assert.Equal(new[] { cat }, snapshot.Tags.Select(tag => tag.Id)); // No stray tag from the combined write.
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void ACrashRightAfterTheReplaceLeavesOneConsistentStateAndTheRetryAllocatesNothingNew()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        TagId cat = new PrivateTagStore(temp.Path, lease).Define("cat").Tag;
        PrivateTagStore crashing = new(temp.Path, lease)
        { WritePhaseForTests = phase => { if (phase == PrivateTagWritePhase.AfterReplace) throw new IOException("crash"); } };

        // The caller saw a failure, so it never reported success; the durable file has both or nothing.
        Assert.Throws<IOException>(() => crashing.SetMediaSubjects(cat, [Loose(Observe())], true));

        PrivateTagStore restarted = new(temp.Path, lease);
        PrivateTagStoreSnapshot snapshot = restarted.ReadSnapshot();
        LocalMediaTagTarget target = Registered(snapshot, Observe());
        Assert.Equal(cat, Assert.Single(snapshot.TagsOf(target)).Id);
        Assert.All(snapshot.LocalAssignments, item => Assert.True(snapshot.LocalRegistry.Contains(item.Target)));
        PrivateTagCommit retry = restarted.SetMediaSubjects(cat, [Loose(Observe())], true);
        Assert.False(retry.Changed);
        Assert.Equal(target, Registered(retry.Snapshot, Observe()));
        Assert.Single(retry.Snapshot.LocalRegistry.Occurrences);
    }

    private const string V1BackupName = "private-tags.v1.bak";

    // A profile with a real v1 document (tags, a thread assignment and a bound media assignment).
    private static (PrivateTagStore Store, TagId Cat, string Path, byte[] V1) V1Profile(string directory, ProfileLease lease)
    {
        PrivateTagStore store = new(directory, lease);
        TagId cat = store.Define("cat").Tag;
        store.SetThread(cat, Thread, true);
        store.SetMedia(cat, Bound(), true);
        string path = Path.Combine(directory, FileName);
        byte[] v1 = File.ReadAllBytes(path);
        Assert.Equal(1, PrivateTagStore.ValidateDocumentBytes(v1));
        return (store, cat, path, v1);
    }

    [Fact]
    public void TheFirstV2UpgradeKeepsAPermanentV1CopyThatNoLaterWriteTouches()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        (PrivateTagStore store, TagId cat, string path, byte[] v1) = V1Profile(temp.Path, lease);
        string keep = Path.Combine(temp.Path, V1BackupName);
        Assert.Equal(V1BackupName, PrivateTagStore.V1BackupFileName);
        Assert.False(File.Exists(keep)); // Bound and thread writes stay v1 and need no copy.
        Assert.False(store.SetMediaSubjects(cat, [Loose(Observe())], false).Changed); // a local no-op does not either
        Assert.False(File.Exists(keep));

        store.SetMediaSubjects(cat, [Loose(Observe())], true);

        Assert.Equal(v1, File.ReadAllBytes(keep));
        Assert.Equal(v1, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(1, PrivateTagStore.ValidateDocumentBytes(File.ReadAllBytes(keep)));
        byte[] firstV2 = File.ReadAllBytes(path);

        // The ordinary backup rotates on every later write; the v1 copy never does.
        store.SetMediaSubjects(cat, [Loose(Observe("b.png"))], true);
        Assert.Equal(firstV2, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(v1, File.ReadAllBytes(keep));
        TagId dog = store.Define("dog").Tag;
        store.SetThread(dog, OtherThread, true);
        store.SetMediaSubjects(dog, [Loose(Observe())], true);
        store.SetMediaSubjects(dog, [Loose(Observe())], false);
        store.RemoveThreads([Thread, OtherThread]);
        store.Delete(dog);
        Assert.NotEqual(v1, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(v1, File.ReadAllBytes(keep));

        // A fresh store instance (restart) and a write that stays v2 leave it alone as well.
        new PrivateTagStore(temp.Path, lease).SetMediaSubjects(cat, [Loose(Observe("c.png"))], true);
        Assert.Equal(v1, File.ReadAllBytes(keep));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.Equal(new[] { FileName, FileName + ".bak", V1BackupName }.Order(),
            Directory.GetFiles(temp.Path, "private-tags*").Select(Path.GetFileName).Order());
    }

    [Fact]
    public void TheV1CopyIsTheExactByteSequenceIncludingABomAndNotARoundTrippedDocument()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        (PrivateTagStore store, TagId cat, string path, byte[] plain) = V1Profile(temp.Path, lease);
        byte[] v1 = [0xEF, 0xBB, 0xBF, .. plain, (byte)'\n'];
        File.WriteAllBytes(path, v1);
        Assert.Equal(1, PrivateTagStore.ValidateDocumentBytes(v1));

        store.SetMediaSubjects(cat, [Loose(Observe())], true);

        Assert.Equal(v1, File.ReadAllBytes(Path.Combine(temp.Path, V1BackupName)));
    }

    [Fact]
    public void AnExistingV1CopyIsNeverOverwrittenNotEvenByTheUpgradeItself()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        (PrivateTagStore store, TagId cat, string path, byte[] v1) = V1Profile(temp.Path, lease);
        string keep = Path.Combine(temp.Path, V1BackupName);
        byte[] earlier = Encoding.UTF8.GetBytes("an earlier v1 copy, for example after a crash");
        File.WriteAllBytes(keep, earlier);

        PrivateTagCommit upgrade = store.SetMediaSubjects(cat, [Loose(Observe())], true);

        Assert.True(upgrade.Changed);
        Assert.Equal(2, upgrade.Snapshot.FormatVersion);
        Assert.Equal(earlier, File.ReadAllBytes(keep));
        Assert.Equal(v1, File.ReadAllBytes(path + ".bak")); // the ordinary rotation is unchanged
        store.SetMediaSubjects(cat, [Loose(Observe("b.png"))], true);
        Assert.Equal(earlier, File.ReadAllBytes(keep));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void ADirectoryAtTheV1CopyNameIsLeftAloneAndDoesNotBlockTheUpgrade()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        (PrivateTagStore store, TagId cat, _, _) = V1Profile(temp.Path, lease);
        string keep = Path.Combine(temp.Path, V1BackupName);
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "note.txt"), "x");

        Assert.True(store.SetMediaSubjects(cat, [Loose(Observe())], true).Changed);

        Assert.True(Directory.Exists(keep));
        Assert.Equal("x", File.ReadAllText(Path.Combine(keep, "note.txt")));
    }

    [Fact]
    public void ACorruptV1PrimaryAndAFreshProfileCreateNoV1Copy()
    {
        using (TempDirectory temp = new())
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            (PrivateTagStore store, TagId cat, string path, byte[] v1) = V1Profile(temp.Path, lease);
            byte[] damaged = v1[..^7];
            File.WriteAllBytes(path, damaged);

            Assert.Throws<InvalidDataException>(() => store.SetMediaSubjects(cat, [Loose(Observe())], true));

            Assert.Equal(damaged, File.ReadAllBytes(path));
            Assert.False(File.Exists(Path.Combine(temp.Path, V1BackupName)));
            Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        }
        using (TempDirectory temp = new())
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            // No v1 file ever existed: the first write is v2 from the start and has nothing to keep.
            Assert.Equal(2, new PrivateTagStore(temp.Path, lease).DefineAndSetMediaSubjects("cat", [Loose(Observe())]).Snapshot.FormatVersion);
            Assert.False(File.Exists(Path.Combine(temp.Path, V1BackupName)));
        }
    }

    [Fact]
    public void AV2OnlyProfileThatNeverHadV1BytesGetsNoV1Copy()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, FileName), ValidV2());
        PrivateTagStore store = new(temp.Path, lease);
        TagId tag = Assert.Single(store.ReadSnapshot().Tags).Id;

        store.SetMediaSubjects(tag, [Loose(Observe("new.png"))], true);

        Assert.False(File.Exists(Path.Combine(temp.Path, V1BackupName)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ACrashAroundTheUpgradeKeepsPrimaryAndV1CopyConsistentAndTheRetryNeverRewritesTheCopy(int point)
    {
        PrivateTagWritePhase crashAt = point switch
        {
            0 => PrivateTagWritePhase.BeforeBackupCheck, 1 => PrivateTagWritePhase.BeforeReplace, _ => PrivateTagWritePhase.AfterReplace
        };
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        (_, TagId cat, string path, byte[] v1) = V1Profile(temp.Path, lease);
        string keep = Path.Combine(temp.Path, V1BackupName);
        PrivateTagStore crashing = new(temp.Path, lease)
        { WritePhaseForTests = phase => { if (phase == crashAt) throw new IOException("crash"); } };

        Assert.Throws<IOException>(() => crashing.SetMediaSubjects(cat, [Loose(Observe())], true));

        // Before the replace the primary is still the v1 document; the copy exists only once it is safe to need it.
        bool replaced = crashAt == PrivateTagWritePhase.AfterReplace;
        Assert.Equal(replaced ? 2 : 1, PrivateTagStore.ValidateDocumentBytes(File.ReadAllBytes(path)));
        Assert.Equal(crashAt != PrivateTagWritePhase.BeforeBackupCheck, File.Exists(keep));
        if (File.Exists(keep)) Assert.Equal(v1, File.ReadAllBytes(keep));
        if (replaced) Assert.Equal(v1, File.ReadAllBytes(path + ".bak"));

        PrivateTagStore restarted = new(temp.Path, lease);
        restarted.SetMediaSubjects(cat, [Loose(Observe("b.png"))], true);
        restarted.SetMediaSubjects(cat, [Loose(Observe("c.png"))], true);

        Assert.Equal(v1, File.ReadAllBytes(keep));
        // a.png is registered only if the crashed write got as far as the replace.
        Assert.Equal(replaced ? 3 : 2, restarted.ReadSnapshot().LocalRegistry.Occurrences.Count);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void CancellationStaleRevisionCapacityAndADamagedBackupNeverWriteOrLoseAssignments()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        PrivateTagCommit kept = store.SetMediaSubjects(cat, [Loose(Observe("kept.png"))], true);
        string path = Path.Combine(temp.Path, FileName);
        byte[] before = File.ReadAllBytes(path);

        Assert.ThrowsAny<OperationCanceledException>(() => store.SetMediaSubjects(cat, [Loose(Observe())], true, null, new CancellationToken(true)));
        Assert.Equal(PrivateTagStoreError.StaleRevision,
            Rejected(() => store.SetMediaSubjects(cat, [Loose(Observe())], true, kept.Snapshot.Revision - 1)));
        Assert.Equal(PrivateTagStoreError.StaleRevision,
            Rejected(() => store.DefineAndSetMediaSubjects("dog", [Loose(Observe())], kept.Snapshot.Revision - 1)));
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.SetMediaSubjects(new TagId(Guid.NewGuid()), [Loose(Observe())], true)));
        Assert.Equal(before, File.ReadAllBytes(path));

        byte[] backup = File.ReadAllBytes(path + ".bak");
        byte[] future = Encoding.UTF8.GetBytes("{\"Version\":3,\"Revision\":9,\"Tags\":[],\"Threads\":[],\"Media\":[]}");
        File.WriteAllBytes(path + ".bak", future);
        Assert.Throws<InvalidDataException>(() => store.SetMediaSubjects(cat, [Loose(Observe())], true));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(future, File.ReadAllBytes(path + ".bak"));
        File.WriteAllBytes(path + ".bak", backup);
        Assert.Equal(cat, Assert.Single(store.ReadSnapshot().TagsOf(Loose(Observe("kept.png")))).Id);
    }

    [Fact]
    public void LocalCapacityIsEnforcedForTheWholeWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        MediaTagSubject[] many = Enumerable.Range(0, PrivateTagStore.MaximumLocalRevisionsPerOccurrence + 1)
            .Select(index => (MediaTagSubject)Loose(Observe(bytes: "v" + index))).ToArray();
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        // 65 revisions of one slot exceed the per-occurrence bound: nothing of the batch is written.
        Assert.Equal(PrivateTagStoreError.CapacityExceeded, Rejected(() => store.SetMediaSubjects(cat, many, true)));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        Assert.True(store.SetMediaSubjects(cat, many[..^1], true).Changed);
        Assert.Equal(PrivateTagStore.MaximumLocalRevisionsPerOccurrence, Assert.Single(store.ReadSnapshot().LocalRegistry.Occurrences).Revisions.Count);
    }

    [Fact]
    public async Task ConcurrentFirstAddsOfOneFileRegisterExactlyOneIdentity()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore setup = new(temp.Path, lease);
        TagId[] tags = Enumerable.Range(0, 8).Select(index => setup.Define("tag" + index).Tag).ToArray();

        await Task.WhenAll(tags.Select(tag => Task.Run(() =>
            new PrivateTagStore(temp.Path, lease).SetMediaSubjects(tag, [Loose(Observe())], true))));

        PrivateTagStoreSnapshot snapshot = setup.ReadSnapshot();
        LocalMediaOccurrence occurrence = Assert.Single(snapshot.LocalRegistry.Occurrences);
        Assert.Single(occurrence.Revisions);
        Assert.Equal(tags.Select(tag => tag.Value).Order(), snapshot.TagsOf(Registered(snapshot, Observe())).Select(tag => tag.Id.Value).Order());
    }

    [Fact]
    public void DefineAndSetIsOneWriteAndAFailedAssignmentLeavesNoNewTag()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateTagCommit first = store.DefineAndSetMediaSubjects(" /RPT/ ", [Loose(Observe()), new MediaTagSubject.Bound(Bound())]);
        Assert.True(first.Changed);
        Assert.Equal(1, first.Snapshot.Revision); // One write for definition, registration and both assignments.
        Assert.Equal("/RPT/", Assert.Single(first.Snapshot.TagsOf(Loose(Observe()))).Name.Display);
        Assert.Equal("/RPT/", Assert.Single(first.Snapshot.TagsOf(Bound())).Name.Display);
        Assert.False(store.DefineAndSetMediaSubjects("/rpt/", [Loose(Observe())]).Changed);

        LocalMediaObservation real = Observe();
        Assert.True(LocalMediaObservation.TryCreate(Thread, real.Locator.Directory, real.Locator.RequestedFileName, real.AssetId,
            real.Length + 1, real.Sha256, out LocalMediaObservation? conflicting));
        Assert.Equal(PrivateTagStoreError.InvalidTarget, Rejected(() => store.DefineAndSetMediaSubjects("never", [Loose(conflicting!)])));
        Assert.DoesNotContain(store.ReadSnapshot().Tags, tag => tag.Name.Display == "never");
    }

    [Fact]
    public void ThreadTagsBoundTagsAndLocalTagsStaySeparateWithoutHashFanOut()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId thread = store.Define("thread").Tag, local = store.Define("local").Tag, bound = store.Define("bound").Tag;
        store.SetThread(thread, Thread, true);
        store.SetMedia(bound, Bound(), true);
        // A loose file whose bytes equal the bound file's recorded bytes.
        LocalMediaObservation twin = Observe(bytes: "bound");
        Assert.Equal(Bound().Sha256, twin.Sha256);
        PrivateTagStoreSnapshot snapshot = store.SetMediaSubjects(local, [Loose(twin)], true).Snapshot;

        Assert.Equal(thread, Assert.Single(snapshot.TagsOf(Thread)).Id);
        Assert.Equal(local, Assert.Single(snapshot.TagsOf(Loose(twin))).Id);
        Assert.Equal(bound, Assert.Single(snapshot.TagsOf(Bound())).Id);
        Assert.Equal(1, snapshot.DistinctMediaCounts()[local]);
        Assert.Equal(1, snapshot.DistinctMediaCounts()[bound]);
        Assert.False(snapshot.DistinctMediaCounts().ContainsKey(thread));
        // Removing the bound tag by file never reaches the local twin, and vice versa.
        PrivateTagStoreSnapshot unbound = store.SetMediaSubjects(bound, [new MediaTagSubject.Bound(Bound())], false).Snapshot;
        Assert.Equal(local, Assert.Single(unbound.TagsOf(Loose(twin))).Id);
        Assert.Equal(new[] { Thread }, PrivateTagStore.LocalAssignmentOwners(unbound));
    }

    [Fact]
    public void RemovingThreadsDropsOnlyTheirLocalRegistrationsAndDeletingATagOnlyItsAssignments()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, dog = store.Define("dog").Tag;
        LocalMediaObservation mine = Observe(), other = Observe(thread: OtherThread, directory: "/media/43");
        store.SetMediaSubjects(cat, [Loose(mine), Loose(other)], true);
        store.SetMediaSubjects(dog, [Loose(mine), Loose(other)], true);
        LocalMediaTagTarget kept = Registered(store.ReadSnapshot(), other);

        PrivateTagStoreSnapshot afterDelete = store.Delete(dog).Snapshot;
        Assert.Equal(cat, Assert.Single(afterDelete.TagsOf(kept)).Id);
        Assert.Equal(2, afterDelete.LocalRegistry.Occurrences.Count);

        PrivateTagStoreSnapshot afterRemoval = store.RemoveThreads([Thread]).Snapshot;
        Assert.Equal(new[] { cat }, afterRemoval.Tags.Select(tag => tag.Id)); // Tag definitions stay.
        Assert.Equal(LocalMediaResolutionState.NewOccurrence, afterRemoval.ResolveLocal(mine).State);
        Assert.Equal(cat, Assert.Single(afterRemoval.TagsOf(kept)).Id);
        // A re-archived file at the same place is an independent occurrence.
        LocalMediaTagTarget again = Registered(store.SetMediaSubjects(cat, [Loose(mine)], true).Snapshot, mine);
        Assert.NotEqual(kept.Occurrence, again.Occurrence);
        Assert.False(store.RemoveThreads([new ResourceId("source", "board", "999")]).Changed);
    }

    [Fact]
    public void ExplicitOrphanRemovalTakesExactlyTheNamedAssignmentsAndHonoursTheRevision()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag, dog = store.Define("dog").Tag;
        PrivateTagCommit tagged = store.SetMediaSubjects(cat, [Loose(Observe("a.png")), Loose(Observe("b.png"))], true);
        LocalMediaTagTarget a = Registered(tagged.Snapshot, Observe("a.png"));
        store.SetMediaSubjects(dog, [Loose(Observe("a.png"))], true);
        PrivateTagStoreSnapshot seen = store.ReadSnapshot();
        PrivateLocalMediaTagAssignment orphan = new(cat, a);

        using (ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter())
        {
            Assert.Equal(PrivateTagStoreError.StaleRevision,
                Rejected(() => store.RemoveLocalAssignmentsAdmitted([orphan], admission, seen.Revision - 1)));
            PrivateTagCommit removed = store.RemoveLocalAssignmentsAdmitted([orphan], admission, seen.Revision);
            Assert.True(removed.Changed);
            Assert.Equal(dog, Assert.Single(removed.Snapshot.TagsOf(a)).Id);
            Assert.Equal(cat, Assert.Single(removed.Snapshot.TagsOf(Loose(Observe("b.png")))).Id);
            Assert.Equal(2, removed.Snapshot.Tags.Count);
            Assert.Equal(a, Registered(removed.Snapshot, Observe("a.png")));
            Assert.False(store.RemoveLocalAssignmentsAdmitted([orphan], admission).Changed);
        }
    }

    [Fact]
    public void RemovalOfTheSeenTargetWorksForAnOrphanThatNoLongerResolvesThere()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        LocalMediaObservation original = Observe(bytes: "old");
        LocalMediaTagTarget old = Registered(store.SetMediaSubjects(cat, [Loose(original)], true).Snapshot, original);

        // The dialog froze the old revision; removing it takes exactly that assignment, whatever the slot shows now.
        PrivateTagCommit removed = store.SetMediaSubjects(cat, [Loose(Observe(bytes: "new"), old)], false);
        Assert.True(removed.Changed);
        Assert.Empty(removed.Snapshot.LocalAssignments);
        Assert.Equal(LocalMediaResolutionState.NewRevision, removed.Snapshot.ResolveLocal(Observe(bytes: "new")).State);
    }

    private static string ValidV2(string? occurrences = null, string? media = null)
    {
        string occurrence = "{\"Id\":\"00000000-0000-0000-0000-0000000000b1\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\","
            + "\"Directory\":\"/media/42\",\"RequestedFile\":\"a.png\",\"Revisions\":[{\"Id\":\"00000000-0000-0000-0000-0000000000c1\","
            + "\"AssetId\":\"" + Hex("asset") + "\",\"Length\":3,\"Sha256\":\"" + Hex("bytes") + "\"}]}";
        string assignment = "{\"Tag\":\"00000000-0000-0000-0000-0000000000a1\",\"Occurrence\":\"00000000-0000-0000-0000-0000000000b1\","
            + "\"Revision\":\"00000000-0000-0000-0000-0000000000c1\"}";
        return "{\"Version\":2,\"Revision\":4,\"Tags\":[{\"Id\":\"00000000-0000-0000-0000-0000000000a1\",\"Name\":\"cat\"}],"
            + "\"Threads\":[],\"Media\":[],\"LocalOccurrences\":[" + (occurrences ?? occurrence) + "],\"LocalMedia\":[" + (media ?? assignment) + "]}";
    }

    [Fact]
    public void AHandWrittenCanonicalV2DocumentIsAcceptedAndRoundTrips()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, FileName);
        File.WriteAllText(path, ValidV2(), new UTF8Encoding(false));
        PrivateTagStore store = new(temp.Path, lease);

        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();

        Assert.Equal(2, snapshot.FormatVersion);
        PrivateLocalMediaTagAssignment assignment = Assert.Single(snapshot.LocalAssignments);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), assignment.Target.Occurrence.Value);
        store.Define("dog");
        PrivateTagStoreSnapshot rewritten = store.ReadSnapshot();
        Assert.Equal(snapshot.LocalAssignments, rewritten.LocalAssignments);
        Assert.Equal(snapshot.LocalRegistry.Occurrences.Single().Revisions, rewritten.LocalRegistry.Occurrences.Single().Revisions);
    }

    public static TheoryData<string> InvalidV2 => new()
    {
        "{\"Version\":2,\"Revision\":1,\"Tags\":[],\"Threads\":[],\"Media\":[]}", // v2 without its local fields
        "{\"Version\":3,\"Revision\":1,\"Tags\":[],\"Threads\":[],\"Media\":[],\"LocalOccurrences\":[],\"LocalMedia\":[]}", // newer
        "{\"Version\":1,\"Revision\":1,\"Tags\":[],\"Threads\":[],\"Media\":[],\"LocalOccurrences\":[],\"LocalMedia\":[]}", // mixed
        ValidV2(media: "{\"Tag\":\"00000000-0000-0000-0000-0000000000a1\",\"Occurrence\":\"00000000-0000-0000-0000-0000000000b9\",\"Revision\":\"00000000-0000-0000-0000-0000000000c1\"}"), // unknown occurrence
        ValidV2(media: "{\"Tag\":\"00000000-0000-0000-0000-0000000000a9\",\"Occurrence\":\"00000000-0000-0000-0000-0000000000b1\",\"Revision\":\"00000000-0000-0000-0000-0000000000c1\"}"), // unknown tag
        ValidV2(media: "{\"Tag\":\"00000000-0000-0000-0000-0000000000a1\",\"Occurrence\":\"00000000-0000-0000-0000-0000000000b1\",\"Revision\":\"00000000-0000-0000-0000-0000000000c1\"},"
            + "{\"Tag\":\"00000000-0000-0000-0000-0000000000a1\",\"Occurrence\":\"00000000-0000-0000-0000-0000000000b1\",\"Revision\":\"00000000-0000-0000-0000-0000000000c1\"}"), // duplicate
        ValidV2(media: "{\"Tag\":\"00000000-0000-0000-0000-0000000000a1\",\"Occurrence\":\"00000000-0000-0000-0000-0000000000b1\",\"Revision\":\"00000000-0000-0000-0000-0000000000c1\",\"Extra\":1}"), // unknown field
        ValidV2(occurrences: "{\"Id\":\"00000000-0000-0000-0000-0000000000b1\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Directory\":\"/media/42\",\"RequestedFile\":\"a.png\",\"Revisions\":[]}", media: ""), // no revision
        ValidV2(occurrences: "{\"Id\":\"00000000-0000-0000-0000-0000000000b1\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Directory\":\"/media/42\",\"RequestedFile\":\"a.png\",\"Revisions\":[{\"Id\":\"00000000-0000-0000-0000-0000000000c1\",\"AssetId\":\"" + Hex("asset").ToUpperInvariant() + "\",\"Length\":3,\"Sha256\":\"" + Hex("bytes") + "\"}]}", media: ""), // uppercase asset
        ValidV2(occurrences: "{\"Id\":\"00000000-0000-0000-0000-0000000000b1\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Directory\":\"/media/42\",\"RequestedFile\":\"a.png\",\"Revisions\":[{\"Id\":\"00000000-0000-0000-0000-0000000000b1\",\"AssetId\":\"" + Hex("asset") + "\",\"Length\":3,\"Sha256\":\"" + Hex("bytes") + "\"}]}", media: ""), // reused ID
        ValidV2(occurrences: "{\"Id\":\"00000000-0000-0000-0000-0000000000b1\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Directory\":\"/media/42\",\"RequestedFile\":\"a.png\",\"Revisions\":[{\"Id\":\"00000000-0000-0000-0000-0000000000c1\",\"AssetId\":\"" + Hex("asset") + "\",\"Length\":-1,\"Sha256\":\"" + Hex("bytes") + "\"}]}", media: ""), // negative length
        ValidV2(occurrences: "{\"Id\":\"00000000-0000-0000-0000-0000000000b1\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Directory\":\"/media/42\",\"RequestedFile\":\"a.png\",\"Revisions\":[{\"Id\":\"00000000-0000-0000-0000-0000000000c1\",\"AssetId\":\"" + Hex("asset") + "\",\"Length\":3,\"Sha256\":\"" + Hex("bytes") + "\"}]},"
            + "{\"Id\":\"00000000-0000-0000-0000-0000000000b2\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Directory\":\"/media/42\",\"RequestedFile\":\"a.png\",\"Revisions\":[{\"Id\":\"00000000-0000-0000-0000-0000000000c2\",\"AssetId\":\"" + Hex("other") + "\",\"Length\":3,\"Sha256\":\"" + Hex("bytes") + "\"}]}"), // duplicate locator
        ValidV2(occurrences: "{\"Id\":\"00000000-0000-0000-0000-0000000000b1\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Directory\":\"/media/42\",\"RequestedFile\":\"a.png\",\"Revisions\":[{\"Id\":\"00000000-0000-0000-0000-0000000000c1\",\"AssetId\":\"" + Hex("asset") + "\",\"Length\":3,\"Sha256\":\"" + Hex("bytes") + "\"},"
            + "{\"Id\":\"00000000-0000-0000-0000-0000000000c2\",\"AssetId\":\"" + Hex("asset") + "\",\"Length\":3,\"Sha256\":\"" + Hex("bytes") + "\"}]}", media: ""), // duplicate asset in one occurrence
    };

    [Theory]
    [MemberData(nameof(InvalidV2))]
    public void InvalidMixedOrNewerV2DocumentsFailClosedAndAreNeverOverwritten(string raw)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, FileName);
        byte[] primary = Encoding.UTF8.GetBytes(raw);
        File.WriteAllBytes(path, primary);
        PrivateTagStore store = new(temp.Path, lease);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());

        Assert.DoesNotContain("/media", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => PrivateTagStore.ValidateDocumentBytes(primary));
        Assert.Throws<InvalidDataException>(() => store.SetMediaSubjects(new TagId(Guid.Parse("00000000-0000-0000-0000-0000000000a1")), [Loose(Observe())], true));
        Assert.Throws<InvalidDataException>(() => store.Define("new"));
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path, "private-tags*"));
    }

    [Fact]
    public void ACorruptV2BackupBlocksRotationAndAV2BackupOfAV1PrimaryIsValidEvidence()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        store.SetMediaSubjects(cat, [Loose(Observe())], true);
        store.SetMediaSubjects(cat, [Loose(Observe("b.png"))], true); // backup is v2 now
        string path = Path.Combine(temp.Path, FileName);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        Assert.Equal(2, PrivateTagStore.ValidateDocumentBytes(backup));
        byte[] damaged = backup[..^5];
        File.WriteAllBytes(path + ".bak", damaged);

        Assert.Throws<InvalidDataException>(() => store.SetMediaSubjects(cat, [Loose(Observe("c.png"))], true));

        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(damaged, File.ReadAllBytes(path + ".bak"));
        Assert.Equal(2, store.ReadSnapshot().LocalAssignments.Count);
    }
}
