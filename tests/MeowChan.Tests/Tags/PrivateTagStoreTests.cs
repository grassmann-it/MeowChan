using System.Security.Cryptography;
using System.Text;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

public sealed class PrivateTagStoreTests
{
    private const string FileName = "private-tags.json";
    private const string RevisionKey = "att-v2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sha256A = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string TagGuid = "00000000-0000-0000-0000-0000000000a1";
    private static ResourceId Thread => new("source", "board", "42");

    private static PrivateMediaTagTarget Media(ResourceId? thread = null, string post = "100", int position = 0,
        string revisionKey = RevisionKey, string sha256 = Sha256A, TransferMediaRole role = TransferMediaRole.Original)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(thread ?? Thread, post, position, revisionKey),
            "media-1", "original", role, sha256, out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    private static PrivateTagStoreError Rejected(Action action) =>
        Assert.Throws<PrivateTagStoreException>(action).Error;

    [Fact]
    public void EmptyProfileReadsAsEmptyWithoutCreatingFiles()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);

        PrivateTagStoreSnapshot snapshot = new PrivateTagStore(temp.Path, lease).ReadSnapshot();

        Assert.Equal(0, snapshot.Revision);
        Assert.Empty(snapshot.Tags);
        Assert.Empty(snapshot.ThreadAssignments);
        Assert.Empty(snapshot.MediaAssignments);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), snapshot.Fingerprint);
        Assert.False(File.Exists(Path.Combine(temp.Path, FileName)));
    }

    [Fact]
    public void TagsAndBothAssignmentKindsSurviveANewStoreInstanceAndStaySeparate()
    {
        using TempDirectory temp = new();
        ResourceId otherSource = Thread with { Source = "other" };
        TagId cat, dog;
        string fingerprint;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStore store = new(temp.Path, lease);
            cat = store.Define("cat").Tag;
            dog = store.Define("dog").Tag;
            Assert.True(store.SetThread(cat, Thread, true).Changed);
            Assert.True(store.SetThread(dog, otherSource, true).Changed);
            PrivateTagCommit last = store.SetMedia(dog, Media(), true);
            Assert.True(last.Changed);
            Assert.Equal(5, last.Snapshot.Revision);
            fingerprint = last.Snapshot.Fingerprint;
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(temp.Path, FileName)))), fingerprint);
        }

        using ProfileLease reopened = ProfileLease.Acquire(temp.Path);
        PrivateTagStoreSnapshot snapshot = new PrivateTagStore(temp.Path, reopened).ReadSnapshot();

        Assert.Equal(fingerprint, snapshot.Fingerprint);
        Assert.Equal(new[] { "cat", "dog" }, snapshot.Tags.Select(tag => tag.Name.Display));
        // Same thread number in another source is a different thread.
        Assert.Equal(cat, Assert.Single(snapshot.TagsOf(Thread)).Id);
        Assert.Equal(dog, Assert.Single(snapshot.TagsOf(otherSource)).Id);
        // No inheritance in either direction between a thread and its media.
        Assert.Equal(dog, Assert.Single(snapshot.TagsOf(Media())).Id);
        Assert.Empty(snapshot.TagsOf(Media(otherSource)));
    }

    [Fact]
    public void RepeatedAssignmentAndMissingRemovalAreNoOpsThatDoNotWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        store.SetThread(cat, Thread, true);
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        PrivateTagCommit again = store.SetThread(cat, Thread, true);
        PrivateTagCommit absentThread = store.SetThread(cat, Thread with { Thread = "43" }, false);
        PrivateTagCommit absentMedia = store.SetMedia(cat, Media(), false);

        Assert.False(again.Changed);
        Assert.False(absentThread.Changed);
        Assert.False(absentMedia.Changed);
        Assert.Equal(2, absentMedia.Snapshot.Revision);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }

    [Fact]
    public void DefiningAnExistingNameReturnsTheSameTagAndKeepsTheFirstDisplay()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateTagCommit first = store.Define("  /rpt/ ");

        PrivateTagCommit second = store.Define("/RPT/");

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.Tag, second.Tag);
        Assert.Equal("/rpt/", Assert.Single(second.Snapshot.Tags).Name.Display);
        Assert.Equal(1, second.Snapshot.Revision);
    }

    [Fact]
    public void RenameKeepsIdentityAndAssignmentsAndRejectsUnicodeEquivalentCollision()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId angstrom = store.Define("\u00C5ngstr\u00F6m").Tag;
        TagId cat = store.Define("cat").Tag;
        store.SetThread(cat, Thread, true);
        store.SetMedia(cat, Media(), true);
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        // Decomposed and differently cased spelling of the first tag's name.
        Assert.Equal(PrivateTagStoreError.NameCollision, Rejected(() => store.Rename(cat, "a\u030Angstro\u0308m")));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));

        PrivateTagCommit renamed = store.Rename(cat, "Katze");
        Assert.True(renamed.Changed);
        Assert.Equal(cat, renamed.Tag);
        Assert.Equal("Katze", renamed.Snapshot.Find(cat)!.Name.Display);
        Assert.Equal(cat, Assert.Single(renamed.Snapshot.TagsOf(Thread)).Id);
        Assert.Equal(cat, Assert.Single(renamed.Snapshot.TagsOf(Media())).Id);
        Assert.NotNull(renamed.Snapshot.Find(angstrom));
        Assert.False(store.Rename(cat, "Katze").Changed);
        // A rename that only changes case is a real change of the display name.
        Assert.True(store.Rename(cat, "KATZE").Changed);
    }

    [Fact]
    public void StaleExpectedRevisionIsRejectedBeforeAnyChange()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateTagCommit defined = store.Define("cat");
        store.SetThread(defined.Tag, Thread, true);
        byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        Assert.Equal(PrivateTagStoreError.StaleRevision, Rejected(() => store.Rename(defined.Tag, "dog", defined.Snapshot.Revision)));
        Assert.Equal(PrivateTagStoreError.StaleRevision, Rejected(() => store.Define("bird", 0)));
        Assert.Equal(PrivateTagStoreError.StaleRevision, Rejected(() => store.SetThread(defined.Tag, Thread, false, 1)));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        Assert.True(store.Rename(defined.Tag, "dog", 2).Changed);
    }

    [Fact]
    public void InvalidInputsAndUnknownTagsAreRejectedWithoutEchoAndWithoutFiles()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId unknown = new(Guid.NewGuid());

        PrivateTagStoreException name = Assert.Throws<PrivateTagStoreException>(() => store.Define("secret\tname"));
        Assert.Equal(PrivateTagStoreError.InvalidName, name.Error);
        Assert.DoesNotContain("secret", name.Message, StringComparison.Ordinal);
        Assert.Equal(PrivateTagStoreError.InvalidName, Rejected(() => store.Define(null)));
        Assert.Equal(PrivateTagStoreError.InvalidName, Rejected(() => store.Define("   ")));
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.SetThread(unknown, Thread, true)));
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.SetThread(unknown, Thread, false)));
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.SetMedia(new TagId(Guid.Empty), Media(), true)));
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.Rename(unknown, "cat")));
        Assert.Equal(PrivateTagStoreError.InvalidThread, Rejected(() => store.SetThread(unknown, new ResourceId("source", "board"), true)));
        Assert.Equal(PrivateTagStoreError.InvalidThread, Rejected(() => store.SetThread(unknown, null, true)));
        Assert.Equal(PrivateTagStoreError.InvalidThread, Rejected(() => store.SetThread(unknown, Thread with { Thread = "\uD800" }, true)));
        Assert.Equal(PrivateTagStoreError.InvalidTarget, Rejected(() => store.SetMedia(unknown, null, true)));
        Assert.Empty(Directory.GetFiles(temp.Path, "private-tags*"));
    }

    [Fact]
    public void MediaTagsBindTheExactVersionNotTheThreadOrEqualBytes()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        PrivateMediaTagTarget original = Media();
        PrivateMediaTagTarget samePlaceOtherRevision = Media(revisionKey: "att-v2:" + new string('b', 64));
        PrivateMediaTagTarget sameBytesOtherPost = Media(post: "101");
        PrivateMediaTagTarget previewOfSame = Media(role: TransferMediaRole.Preview);

        PrivateTagStoreSnapshot snapshot = store.SetMedia(cat, original, true).Snapshot;

        Assert.Equal(cat, Assert.Single(snapshot.TagsOf(original)).Id);
        Assert.Empty(snapshot.TagsOf(samePlaceOtherRevision));
        Assert.Empty(snapshot.TagsOf(sameBytesOtherPost));
        Assert.Empty(snapshot.TagsOf(previewOfSame));
        Assert.Empty(snapshot.TagsOf(Thread));
        Assert.True(store.SetMedia(cat, original, false).Changed);
        Assert.Empty(store.ReadSnapshot().MediaAssignments);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"Version\":2,\"Revision\":1,\"Tags\":[],\"Threads\":[],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":-1,\"Tags\":[],\"Threads\":[],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[],\"Threads\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[],\"Threads\":[],\"Media\":[],\"Unknown\":true}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Revision\":1,\"Tags\":[],\"Threads\":[],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":null,\"Threads\":[],\"Media\":[]}")]
    // Unknown field, non-canonical ID, non-canonical name, colliding name, duplicate ID.
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\",\"Extra\":1}],\"Threads\":[],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"000000000000000000000000000000A1\",\"Name\":\"private-cat\"}],\"Threads\":[],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"private-cat\"}],\"Threads\":[],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\" private-cat\"}],\"Threads\":[],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"},{\"Id\":\"00000000-0000-0000-0000-0000000000a2\",\"Name\":\"PRIVATE-CAT\"}],\"Threads\":[],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"},{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-dog\"}],\"Threads\":[],\"Media\":[]}")]
    // Dangling tag, duplicate assignment, board-only identity, lone surrogate.
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[],\"Threads\":[{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\"}],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"}],\"Threads\":[{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\"},{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\"}],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"}],\"Threads\":[{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"\"}],\"Media\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"}],\"Threads\":[{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"\\ud800\"}],\"Media\":[]}")]
    // Media: unknown role, uppercase hash, negative position, missing field.
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"}],\"Threads\":[],\"Media\":[{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Post\":\"100\",\"Position\":0,\"RevisionKey\":\"" + RevisionKey + "\",\"MediaId\":\"media-1\",\"Variant\":\"original\",\"Role\":\"sample\",\"Sha256\":\"" + Sha256A + "\"}]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"}],\"Threads\":[],\"Media\":[{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Post\":\"100\",\"Position\":0,\"RevisionKey\":\"" + RevisionKey + "\",\"MediaId\":\"media-1\",\"Variant\":\"original\",\"Role\":\"original\",\"Sha256\":\"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\"}]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"}],\"Threads\":[],\"Media\":[{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Post\":\"100\",\"Position\":-1,\"RevisionKey\":\"" + RevisionKey + "\",\"MediaId\":\"media-1\",\"Variant\":\"original\",\"Role\":\"original\",\"Sha256\":\"" + Sha256A + "\"}]}")]
    [InlineData("{\"Version\":1,\"Revision\":1,\"Tags\":[{\"Id\":\"" + TagGuid + "\",\"Name\":\"private-cat\"}],\"Threads\":[],\"Media\":[{\"Tag\":\"" + TagGuid + "\",\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Post\":\"100\",\"Position\":0,\"RevisionKey\":\"" + RevisionKey + "\",\"MediaId\":\"media-1\",\"Variant\":\"original\",\"Role\":\"original\"}]}")]
    public void InvalidOrNewerDocumentsFailClosedAndAreNeverOverwritten(string raw)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, FileName);
        byte[] primary = Encoding.UTF8.GetBytes(raw);
        File.WriteAllBytes(path, primary);
        PrivateTagStore store = new(temp.Path, lease);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());

        Assert.DoesNotContain("private-cat", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => store.Define("new"));
        Assert.Throws<InvalidDataException>(() => store.SetThread(new TagId(Guid.Parse(TagGuid)), Thread, true));
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path, "private-tags*"));
    }

    [Fact]
    public void WrittenDocumentIsAcceptedByteForByteIncludingBomAndUnicodeNames()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId tag = store.Define("\u732B \uD83D\uDC08 <b>&\"").Tag;
        store.SetThread(tag, new ResourceId("s\u00F6urce", "b/\"\\", "42"), true);
        store.SetMedia(tag, Media(role: TransferMediaRole.Preview), true);
        string path = Path.Combine(temp.Path, FileName);
        byte[] written = File.ReadAllBytes(path);
        PrivateTagStoreSnapshot before = store.ReadSnapshot();

        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(written).ToArray());
        PrivateTagStoreSnapshot withBom = store.ReadSnapshot();

        Assert.Equal("\u732B \uD83D\uDC08 <b>&\"", Assert.Single(withBom.Tags).Name.Display);
        Assert.Equal(before.ThreadAssignments, withBom.ThreadAssignments);
        Assert.Equal(before.MediaAssignments, withBom.MediaAssignments);
        Assert.Equal(TransferMediaRole.Preview, Assert.Single(withBom.MediaAssignments).Target.Role);
        Assert.Equal(before.Revision, withBom.Revision);
    }

    [Fact]
    public void MissingPrimaryWithBackupRequiresRecoveryInsteadOfAnEmptyStore()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        store.SetThread(cat, Thread, true);
        string path = Path.Combine(temp.Path, FileName);
        Assert.True(File.Exists(path + ".bak"));
        byte[] backup = File.ReadAllBytes(path + ".bak");
        File.Delete(path);

        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
        Assert.Throws<InvalidDataException>(() => store.Define("dog"));

        Assert.False(File.Exists(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void NewerOrDamagedBackupIsNotRotatedAwayByAChange()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId cat = store.Define("cat").Tag;
        string path = Path.Combine(temp.Path, FileName);
        byte[] primary = File.ReadAllBytes(path);
        byte[] future = Encoding.UTF8.GetBytes("{\"Version\":2,\"Revision\":9,\"Tags\":[],\"Threads\":[],\"Media\":[]}");
        File.WriteAllBytes(path + ".bak", future);

        Assert.Throws<InvalidDataException>(() => store.SetThread(cat, Thread, true));
        // A no-op never rotates a backup, so it is still answered.
        Assert.False(store.Define("cat").Changed);

        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(future, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void TagCapacityIsEnforcedAtTheExactBoundary()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, FileName);
        StringBuilder json = new("{\"Version\":1,\"Revision\":1,\"Tags\":[");
        for (int index = 0; index < PrivateTagStore.MaximumTags - 1; index++)
            json.Append(index == 0 ? "" : ",").Append("{\"Id\":\"").Append(new Guid(index + 1, 0, 0, new byte[8]).ToString("D"))
                .Append("\",\"Name\":\"tag").Append(index).Append("\"}");
        json.Append("],\"Threads\":[],\"Media\":[]}");
        File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
        PrivateTagStore store = new(temp.Path, lease);

        PrivateTagCommit last = store.Define("last");
        Assert.True(last.Changed);
        Assert.Equal(PrivateTagStore.MaximumTags, last.Snapshot.Tags.Count);
        byte[] full = File.ReadAllBytes(path);

        Assert.Equal(PrivateTagStoreError.CapacityExceeded, Rejected(() => store.Define("one too many")));
        Assert.False(store.Define("LAST").Changed);
        Assert.Equal(full, File.ReadAllBytes(path));
    }

    [Fact]
    public void CanceledCallsAndForeignLeasesNeverWrite()
    {
        using TempDirectory temp = new();
        using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        using ProfileLease foreign = ProfileLease.Acquire(other.Path);
        PrivateTagStore store = new(temp.Path, lease);

        Assert.ThrowsAny<OperationCanceledException>(() => store.Define("cat", null, new CancellationToken(true)));
        Assert.Throws<ArgumentException>(() => new PrivateTagStore(temp.Path, foreign));

        Assert.Empty(Directory.GetFiles(temp.Path, "private-tags*"));
        Assert.Empty(Directory.GetFiles(other.Path, "private-tags*"));
    }

    [Fact]
    public void ReadyProfileWritesAsOrdinaryWriterAndIsBlockedByExclusiveReservation()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        lease.Admission.CompleteStartup();
        TagId cat = store.Define("cat").Tag;

        using (ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter())
            Assert.True(store.SetThreadAdmitted(cat, Thread, true, ordinary).Changed);

        using ProfileAdmissionToken held = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            byte[] before = File.ReadAllBytes(Path.Combine(temp.Path, FileName));
            // A waiting exclusive owner closes the gate for new standalone writers; reads stay free.
            Assert.Throws<InvalidOperationException>(() => store.SetThread(cat, Thread, false));
            Assert.Equal(cat, Assert.Single(store.ReadSnapshot().TagsOf(Thread)).Id);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
        }
        finally
        {
            cancel.Cancel();
            try { pending.GetAwaiter().GetResult().Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void DeletingATagRemovesItAndAllItsAssignmentsButNothingElse()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId first = store.Define("first").Tag;
        TagId doomed = store.Define("doomed").Tag;
        TagId last = store.Define("last").Tag;
        store.SetThread(doomed, Thread, true);
        store.SetThread(last, Thread, true);
        store.SetMedia(doomed, Media(), true);
        PrivateTagCommit before = store.SetMedia(first, Media(), true);
        byte[] bytes = File.ReadAllBytes(Path.Combine(temp.Path, FileName));

        Assert.Equal(PrivateTagStoreError.StaleRevision, Rejected(() => store.Delete(doomed, before.Snapshot.Revision - 1)));
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.Delete(new TagId(Guid.NewGuid()))));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));

        PrivateTagCommit deleted = store.Delete(doomed, before.Snapshot.Revision);

        Assert.True(deleted.Changed);
        Assert.Equal(before.Snapshot.Revision + 1, deleted.Snapshot.Revision);
        PrivateTagStoreSnapshot reread = store.ReadSnapshot();
        Assert.Equal(deleted.Snapshot.Fingerprint, reread.Fingerprint);
        Assert.Equal(new[] { first, last }, reread.Tags.Select(tag => tag.Id));
        Assert.Equal(last, Assert.Single(reread.TagsOf(Thread)).Id);
        Assert.Equal(first, Assert.Single(reread.TagsOf(Media())).Id);
        // Deleting twice is a rejection, not a silent success; the name is free for a new tag.
        Assert.Equal(PrivateTagStoreError.UnknownTag, Rejected(() => store.Delete(doomed)));
        PrivateTagCommit recreated = store.Define("DOOMED");
        Assert.True(recreated.Changed);
        Assert.NotEqual(doomed, recreated.Tag);
        Assert.Empty(recreated.Snapshot.TagsOf(Thread).Where(tag => tag.Id == recreated.Tag));
    }

    [Fact]
    public void RemovingThreadsDropsTheirThreadAndMediaAssignmentsAndKeepsTheVocabulary()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        ResourceId kept = Thread with { Thread = "43" };
        TagId cat = store.Define("cat").Tag;
        TagId dog = store.Define("dog").Tag;
        store.SetThread(cat, Thread, true);
        store.SetThread(cat, kept, true);
        store.SetMedia(dog, Media(), true);
        store.SetMedia(dog, Media(kept), true);

        PrivateTagCommit removed = store.RemoveThreads([Thread, Thread with { Source = "unrelated" }]);

        Assert.True(removed.Changed);
        Assert.Equal(new[] { cat, dog }, removed.Snapshot.Tags.Select(tag => tag.Id));
        Assert.Empty(removed.Snapshot.TagsOf(Thread));
        Assert.Empty(removed.Snapshot.TagsOf(Media()));
        Assert.Equal(cat, Assert.Single(removed.Snapshot.TagsOf(kept)).Id);
        Assert.Equal(dog, Assert.Single(removed.Snapshot.TagsOf(Media(kept))).Id);
        byte[] bytes = File.ReadAllBytes(Path.Combine(temp.Path, FileName));
        PrivateTagCommit again = store.RemoveThreads([Thread]);
        Assert.False(again.Changed);
        Assert.False(store.RemoveThreads(Array.Empty<ResourceId>()).Changed);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(temp.Path, FileName)));
    }
}
