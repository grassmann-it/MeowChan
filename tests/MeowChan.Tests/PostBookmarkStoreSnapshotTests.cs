using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class PostBookmarkStoreSnapshotTests
{
    private const string PrivateNote = "SYNTHETIC-PRIVATE-NOTE-DO-NOT-LOG";
    private const string EmptyDocument = "{\"schemaVersion\":1,\"revision\":0,\"bookmarks\":[]}";
    private const string Entry = "{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"10\",\"post\":\"11\",\"note\":\"" + PrivateNote + "\"}";
    private static PostBookmarkKey Key(string post = "11") => new(new("synthetic", "b", "10"), post);
    private static string StorePath(string profile) => Path.Combine(profile, "post-bookmarks.json");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Document(string entries, string revision = "7") =>
        "{\"schemaVersion\":1,\"revision\":" + revision + ",\"bookmarks\":[" + entries + "]}";

    [Fact]
    public void MissingStoreHasEmptySentinelAndUpsertRemoveInvalidateSnapshots()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PostBookmarkTransferSnapshot empty = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Empty(empty.Bookmarks);
        Assert.Equal(0, empty.Revision);
        Assert.Equal(Hash([]), empty.Fingerprint);
        Assert.False(File.Exists(StorePath(temp.Path)));
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, empty));

        var store = new PostBookmarkStore(temp.Path, lease);
        PostBookmarkSnapshot inserted = store.Upsert(Key(), PrivateNote + "\r\n🙂\t", 0, false);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, empty));
        PostBookmarkTransferSnapshot one = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Equal(inserted.Revision, one.Revision);
        Assert.Equal(Key(), Assert.Single(one.Bookmarks).Key);
        Assert.Equal(PrivateNote + "\n🙂\t", one.Bookmarks[0].Note);
        Assert.Equal(Hash(File.ReadAllBytes(store.Path)), one.Fingerprint);
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, one));

        PostBookmarkSnapshot removed = store.Remove(Key(), inserted.Revision);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, one));
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, empty));
        PostBookmarkTransferSnapshot nowEmpty = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Empty(nowEmpty.Bookmarks);
        Assert.Equal(removed.Revision, nowEmpty.Revision);
        Assert.NotEqual(empty.Fingerprint, nowEmpty.Fingerprint);
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, nowEmpty));
        Assert.Equal(PrivateNote + "\n🙂\t", one.Bookmarks[0].Note);
        Assert.Equal(new[] { store.Path }, Directory.GetFiles(temp.Path, "post-bookmarks*"));
        Assert.DoesNotContain(PrivateNote, File.ReadAllText(store.Path));
    }

    [Fact]
    public void CooperativeNoteAndRemoveRecreateAbaAreStaleThroughPersistedRevision()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        store.Upsert(Key(), "A", 0, false);
        PostBookmarkTransferSnapshot original = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        store.Upsert(Key(), "B", 1, true);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, original));
        store.Upsert(Key(), "A", 2, true);
        PostBookmarkTransferSnapshot editedBack = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Equal(original.Bookmarks.ToArray(), editedBack.Bookmarks.ToArray());
        Assert.Equal(3, editedBack.Revision);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, original));
        store.Remove(Key(), 3);
        store.Upsert(Key(), "A", 4, false);
        PostBookmarkTransferSnapshot recreated = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Equal(original.Bookmarks.ToArray(), recreated.Bookmarks.ToArray());
        Assert.Equal(5, recreated.Revision);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, original));
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, editedBack));
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, recreated));
        // No guarantee against an arbitrary external rollback of both bytes and revision.
    }

    [Fact]
    public void FingerprintUsesExactRawBytesNotNormalizedNoteOrReserialization()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string raw = " \n" + Document(Entry.Replace(PrivateNote, "a\\r\\nb\\rc🙂")) + "\t";
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        File.WriteAllBytes(StorePath(temp.Path), bytes);
        PostBookmarkTransferSnapshot snapshot = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Equal(Hash(bytes), snapshot.Fingerprint);
        Assert.Equal("a\nb\nc🙂", Assert.Single(snapshot.Bookmarks).Note);
        Assert.Equal(7, snapshot.Revision);
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, snapshot));
        Assert.Equal(snapshot.Bookmarks.ToArray(), PostBookmarkStore.ReadOnly(temp.Path, lease).Bookmarks.ToArray());
        Assert.Equal(snapshot.Bookmarks.ToArray(), new PostBookmarkStore(temp.Path, lease).Read().Bookmarks.ToArray());

        byte[] differentlyEncoded = Encoding.UTF8.GetBytes(raw + " ");
        File.WriteAllBytes(StorePath(temp.Path), differentlyEncoded);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, snapshot));
        PostBookmarkTransferSnapshot changed = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Equal(snapshot.Revision, changed.Revision);
        Assert.Equal(snapshot.Bookmarks.ToArray(), changed.Bookmarks.ToArray());
        Assert.Equal(Hash(differentlyEncoded), changed.Fingerprint);
        Assert.NotEqual(snapshot.Fingerprint, changed.Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassiveReadAndRecheckDoNotCleanPendingFilesOrMutateDirectories(bool primaryExists)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        if (primaryExists) File.WriteAllText(StorePath(temp.Path), Document(Entry));
        string exactPending = StorePath(temp.Path) + ".pending-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(exactPending, PrivateNote);
        File.WriteAllText(StorePath(temp.Path) + ".pending-user", "synthetic user file");
        File.WriteAllText(StorePath(temp.Path) + ".bak", "not an authoritative note backup");
        string nested = Path.Combine(temp.Path, "unrelated-directory");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "unrelated"), "synthetic");
        var before = Inventory(temp.Path);
        PostBookmarkTransferSnapshot snapshot = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, snapshot));
        PostBookmarkStore.ReadOnly(temp.Path, lease);
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, snapshot));
        Assert.Equal(before, Inventory(temp.Path));
        Assert.True(File.Exists(exactPending));
        Assert.Equal(primaryExists, File.Exists(StorePath(temp.Path)));
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        string[] invalid =
        [
            "", " ", "{", "null", "[]", EmptyDocument + "{}",
            EmptyDocument.Replace("1,", "2,"),
            EmptyDocument.Replace("\"revision\":0", "\"revision\":-1"),
            EmptyDocument.Replace("\"revision\":0", "\"revision\":9223372036854775808"),
            EmptyDocument.Replace("\"revision\":0", "\"revision\":\"0\""),
            EmptyDocument.Replace("\"revision\":0", "\"revision\":0,\"revision\":1"),
            EmptyDocument.Replace("\"revision\":0", "\"revision\":0,\"\\u0072evision\":1"),
            EmptyDocument.Replace("\"bookmarks\":[]", "\"bookmarks\":[],\"" + PrivateNote + "\":true"),
            EmptyDocument.Replace("\"bookmarks\":[]", "\"bookmarks\":null"),
            EmptyDocument.Replace(",\"revision\":0", ""),
            Document(Entry + "," + Entry),
            Document(Entry.Replace("\"note\":", "\"unknown\":true,\"note\":")),
            Document(Entry.Replace("\"note\":", "\"note\":\"duplicate\",\"note\":")),
            Document(Entry.Replace("\"source\":\"synthetic\"", "\"source\":\"\"")),
            Document(Entry.Replace("\"post\":\"11\"", "\"post\":null")),
            Document(Entry.Replace("\"note\":\"" + PrivateNote + "\"", "\"note\":123")),
            Document(Entry.Replace(PrivateNote, PrivateNote + "\\u0000")),
            Document(Entry.Replace(PrivateNote, PrivateNote + "\\uD800")),
            Document(Entry.Replace(PrivateNote, new string('a', PostBookmarkStore.MaximumNoteCodepoints + 1))),
            Document(Entry.Replace(PrivateNote, PrivateNote + "\nraw newline")),
            Document(Entry).Replace("\"revision\":7", "\"revision\":" + PrivateNote),
            "{\"schemaVersion\":1,\"revision\":0,\"bookmarks\":[[[[[[[[[[]]]]]]]]]]}"
        ];
        foreach (string raw in invalid) yield return [Encoding.UTF8.GetBytes(raw)];
        // Invalid UTF-8 inside an otherwise valid JSON string must never turn into U+FFFD.
        string valid = Document(Entry.Replace(PrivateNote, "prefix"));
        byte[] invalidUtf8 = Encoding.UTF8.GetBytes(valid);
        int noteOffset = valid.LastIndexOf("prefix", StringComparison.Ordinal);
        invalidUtf8[noteOffset] = 0xC3;
        invalidUtf8[noteOffset + 1] = 0x28;
        yield return [invalidUtf8];
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void InvalidDocumentsFailClosedForSnapshotRecheckAndExistingReadersWithoutPrivateDiagnostics(byte[] bytes)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var store = new PostBookmarkStore(temp.Path, lease);
        PostBookmarkTransferSnapshot missing = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        File.WriteAllBytes(store.Path, bytes);
        string pending = store.Path + ".pending-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(pending, PrivateNote);
        var before = Inventory(temp.Path);
        AssertOpaque(Assert.Throws<InvalidDataException>(() => PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease)), temp.Path);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, missing));
        AssertOpaque(Assert.Throws<InvalidDataException>(() => PostBookmarkStore.ReadOnly(temp.Path, lease)), temp.Path);
        AssertOpaque(Assert.Throws<InvalidDataException>(() => store.Read()), temp.Path);
        Assert.Equal(before, Inventory(temp.Path));
        Assert.Equal(bytes, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public void ExistingEmptyDocumentIsNotMissingAndDeletingExistingStoreInvalidatesSnapshot()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PostBookmarkTransferSnapshot missing = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        File.WriteAllText(StorePath(temp.Path), EmptyDocument);
        PostBookmarkTransferSnapshot empty = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Empty(empty.Bookmarks);
        Assert.Equal(0, empty.Revision);
        Assert.NotEqual(missing.Fingerprint, empty.Fingerprint);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, missing));
        File.Delete(StorePath(temp.Path));
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, empty));
        // External byte deletion is not a cooperative revision-bumped mutation.
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, missing));
    }

    [Fact]
    public void ExactByteLimitIsReadableButOneExtraByteFailsClosed()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        byte[] bytes = Enumerable.Repeat((byte)' ', PostBookmarkStore.MaximumDocumentBytes).ToArray();
        Encoding.UTF8.GetBytes(EmptyDocument).CopyTo(bytes, 0);
        File.WriteAllBytes(StorePath(temp.Path), bytes);
        PostBookmarkTransferSnapshot atLimit = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Equal(Hash(bytes), atLimit.Fingerprint);
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, atLimit));
        using (FileStream file = new(StorePath(temp.Path), FileMode.Append, FileAccess.Write)) file.WriteByte((byte)' ');
        AssertOpaque(Assert.Throws<InvalidDataException>(() => PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease)), temp.Path);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, atLimit));
        Assert.Equal(PostBookmarkStore.MaximumDocumentBytes + 1L, new FileInfo(StorePath(temp.Path)).Length);
    }

    [Fact]
    public void NoteScalarAndEntryLimitsRemainUnchanged()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string maxNote = string.Concat(Enumerable.Repeat("🙂", PostBookmarkStore.MaximumNoteCodepoints));
        File.WriteAllText(StorePath(temp.Path), Document(Entry.Replace(PrivateNote, maxNote)));
        Assert.Equal(maxNote, Assert.Single(PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease).Bookmarks).Note);
        var entries = Enumerable.Range(0, PostBookmarkStore.MaximumEntries).Select(index => new
        { source = "synthetic", board = "b", thread = "10", post = index.ToString(), note = "" }).ToArray();
        File.WriteAllText(StorePath(temp.Path), JsonSerializer.Serialize(new { schemaVersion = 1, revision = 8, bookmarks = entries }));
        PostBookmarkTransferSnapshot maxEntries = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Assert.Equal(PostBookmarkStore.MaximumEntries, maxEntries.Bookmarks.Count);
        File.WriteAllText(StorePath(temp.Path), JsonSerializer.Serialize(new
        { schemaVersion = 1, revision = 8, bookmarks = entries.Append(new { source = "synthetic", board = "b", thread = "10", post = "extra", note = "" }) }));
        Assert.Throws<InvalidDataException>(() => PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease));
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, maxEntries));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SymbolicLinksIncludingDanglingLinksFailClosedWithoutFollowingTarget(bool dangling)
    {
        using TempDirectory temp = new();
        using TempDirectory target = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PostBookmarkTransferSnapshot missing = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        string targetPath = Path.Combine(target.Path, "private-target.json");
        if (!dangling) File.WriteAllText(targetPath, Document(Entry));
        File.CreateSymbolicLink(StorePath(temp.Path), targetPath);
        var before = Inventory(target.Path);
        AssertOpaque(Assert.Throws<InvalidDataException>(() => PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease)), temp.Path);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, missing));
        Assert.Equal(targetPath, new FileInfo(StorePath(temp.Path)).LinkTarget);
        Assert.Equal(before, Inventory(target.Path));
        File.Delete(StorePath(temp.Path));
    }

    [Fact]
    public void DirectoryInsteadOfDocumentIsInvalidAndIsNotRemoved()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PostBookmarkTransferSnapshot missing = PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease);
        Directory.CreateDirectory(StorePath(temp.Path));
        File.WriteAllText(Path.Combine(StorePath(temp.Path), "kept"), PrivateNote);
        var before = Inventory(temp.Path);
        AssertOpaque(Assert.Throws<InvalidDataException>(() => PostBookmarkStore.ReadTransferSnapshot(temp.Path, lease)), temp.Path);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, lease, missing));
        Assert.Equal(before, Inventory(temp.Path));
    }

    [Fact]
    public void ForeignOwnerIsRejectedBeforeEvenValidatingSuppliedProfilePath()
    {
        using TempDirectory first = new();
        using TempDirectory second = new();
        using ProfileLease owner = ProfileLease.Acquire(first.Path);
        using ProfileLease foreign = ProfileLease.Acquire(second.Path);
        File.WriteAllText(StorePath(first.Path), Document(Entry));
        File.WriteAllText(StorePath(second.Path), "invalid-" + PrivateNote);
        PostBookmarkTransferSnapshot snapshot = PostBookmarkStore.ReadTransferSnapshot(first.Path, owner);
        var before = Inventory(second.Path);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(second.Path, foreign, snapshot));
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent("\0", foreign, snapshot));
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(null!, foreign, snapshot));
        Assert.Equal(before, Inventory(second.Path));
        string absent = Path.Combine(second.Path, "must-not-be-created");
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(absent, owner, snapshot));
        Assert.Throws<ArgumentException>(() => PostBookmarkStore.ReadTransferSnapshot(absent, owner));
        Assert.False(Directory.Exists(absent));
    }

    [Fact]
    public void DisposedLeaseAndRestartNeverAdoptOldSnapshotEvenWhenBytesAreIdentical()
    {
        using TempDirectory temp = new();
        ProfileLease originalOwner = ProfileLease.Acquire(temp.Path);
        PostBookmarkTransferSnapshot old;
        try
        {
            var store = new PostBookmarkStore(temp.Path, originalOwner);
            store.Upsert(Key(), PrivateNote, 0, false);
            old = PostBookmarkStore.ReadTransferSnapshot(temp.Path, originalOwner);
        }
        finally { originalOwner.Dispose(); }
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, originalOwner, old));
        Assert.Throws<ObjectDisposedException>(() => PostBookmarkStore.ReadTransferSnapshot(temp.Path, originalOwner));
        using ProfileLease nextOwner = ProfileLease.Acquire(temp.Path);
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, nextOwner, old));
        Assert.False(PostBookmarkStore.IsTransferSnapshotCurrent("\0", nextOwner, old));
        PostBookmarkTransferSnapshot fresh = PostBookmarkStore.ReadTransferSnapshot(temp.Path, nextOwner);
        Assert.Equal(old.Fingerprint, fresh.Fingerprint);
        Assert.Equal(old.Revision, fresh.Revision);
        Assert.Equal(old.Bookmarks.ToArray(), fresh.Bookmarks.ToArray());
        Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(temp.Path, nextOwner, fresh));
    }

    [Fact]
    public void SnapshotDefensivelyCopiesArrayAndCannotBeMutatedThroughCollectionInterfaces()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PostBookmark[] supplied = [new(Key(), PrivateNote)];
        var snapshot = new PostBookmarkTransferSnapshot(supplied, 5, Hash([]), lease);
        supplied[0] = new(Key("12"), "replacement");
        Assert.Equal(Key(), Assert.Single(snapshot.Bookmarks).Key);
        Assert.Equal(PrivateNote, snapshot.Bookmarks[0].Note);
        Assert.False(snapshot.Bookmarks is PostBookmark[]);
        var generic = Assert.IsAssignableFrom<IList<PostBookmark>>(snapshot.Bookmarks);
        Assert.True(generic.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => generic[0] = supplied[0]);
        Assert.Throws<NotSupportedException>(() => generic.Add(supplied[0]));
        Assert.Throws<NotSupportedException>(() => generic.Clear());
        var nongeneric = Assert.IsAssignableFrom<IList>(snapshot.Bookmarks);
        Assert.Throws<NotSupportedException>(() => nongeneric[0] = supplied[0]);
        PostBookmark copiedRecord = snapshot.Bookmarks[0] with { Note = "changed clone" };
        Assert.NotEqual(copiedRecord.Note, snapshot.Bookmarks[0].Note);
        Assert.DoesNotContain(PrivateNote, snapshot.ToString()!);
        Assert.DoesNotContain(temp.Path, snapshot.ToString()!);
        Assert.Null(typeof(PostBookmarkTransferSnapshot).GetProperty("EqualityContract", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
    }

    private static void AssertOpaque(Exception error, string profile)
    {
        Assert.DoesNotContain(PrivateNote, error.ToString());
        Assert.DoesNotContain(profile, error.ToString());
        Assert.Null(error.InnerException);
    }

    private static string[] Inventory(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Append(root).Order(StringComparer.Ordinal).Select(path => Directory.Exists(path)
            ? "D:" + path + ":" + Directory.GetLastWriteTimeUtc(path).Ticks
            // The lease intentionally holds its lock with FileShare.None. Retain
            // lock existence/metadata in the inventory without opening its bytes.
            : IsProfileLock(path)
                ? "L:" + path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + new FileInfo(path).Length
                : "F:" + path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Hash(File.ReadAllBytes(path))).ToArray();

    private static bool IsProfileLock(string path) => Path.GetFileName(path) is ".meowchan-profile.lock" or ".ochan-profile.lock";
}
