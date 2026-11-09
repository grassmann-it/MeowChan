using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativePrivateMetadataCodecTests
{
    private static readonly ResourceId Thread = new("synthetic", "b", "100");
    private static readonly ResourceId Other = new("synthetic", "b", "200");
    private static readonly NativePrivateMetadataSelection All = new(true, true, true);
    private const string Note = "synthetic-only 雪 🐈\nsecond line\t<script>inert & quoted \"text\"</script>";
    private const string OriginalName = "synthetic-local-only-original-name.jpg";

    private static FavoriteAssetKey Media(ResourceId thread, string media = "unresolved-media", string variant = "original") =>
        new(thread, media, variant, new string('a', 64));

    private static FavoriteEntry FavoriteEntryFor(ResourceId thread) =>
        new(Media(thread), 123, OriginalName);

    private static NativePrivateMetadataSelection Selection(int bits) =>
        new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);

    private static NativePrivateMetadataProjection Projection(NativePrivateMetadataSelection selection, bool populated = true) =>
        new([Thread, Other], selection,
            selection.ThreadFavorites && populated ? [Thread] : [],
            selection.MediaFavorites && populated ? [Media(Thread)] : [],
            selection.PostBookmarksAndNotes && populated ? [new(new(Thread, "unresolved-post"), Note)] : []);

    private static NativePrivateMetadataProjection Inspect(byte[] bytes)
    {
        NativePrivateMetadataInspection inspection = NativePrivateMetadataCodec.Inspect(bytes);
        Assert.True(inspection.IsValid, inspection.Error.ToString());
        Assert.Equal(NativePrivateMetadataCodecError.None, inspection.Error);
        return Assert.IsType<NativePrivateMetadataProjection>(inspection.Projection);
    }

    private static void AssertValues(NativePrivateMetadataProjection expected, NativePrivateMetadataProjection actual)
    {
        Assert.Equal(expected.Selection, actual.Selection);
        Assert.Equal(expected.Threads.ToArray(), actual.Threads.ToArray());
        Assert.Equal(expected.ThreadFavorites.ToArray(), actual.ThreadFavorites.ToArray());
        Assert.Equal(expected.MediaFavorites.ToArray(), actual.MediaFavorites.ToArray());
        Assert.Equal(expected.PostBookmarksAndNotes.ToArray(), actual.PostBookmarksAndNotes.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EverySelectionRoundtripsPositiveMarksAndDistinguishesNullFromSelectedEmpty(int bits)
    {
        NativePrivateMetadataSelection selection = Selection(bits);
        foreach (bool populated in new[] { false, true })
        {
            NativePrivateMetadataProjection input = Projection(selection, populated);
            byte[] wire = NativePrivateMetadataCodec.Serialize(input);
            NativePrivateMetadataProjection parsed = Inspect(wire);
            AssertValues(input, parsed);
            AssertValues(parsed, Inspect(NativePrivateMetadataCodec.Serialize(parsed)));

            using JsonDocument document = JsonDocument.Parse(wire);
            JsonElement root = document.RootElement;
            AssertProperties(root, "schema", "threads", "threadFavorites", "mediaFavorites", "postBookmarksAndNotes");
            Assert.Equal("meowchan-private-v1", root.GetProperty("schema").GetString());
            Assert.Equal(2, root.GetProperty("threads").GetArrayLength());
            AssertCategory(root, "threadFavorites", selection.ThreadFavorites, populated ? 1 : 0);
            AssertCategory(root, "mediaFavorites", selection.MediaFavorites, populated ? 1 : 0);
            AssertCategory(root, "postBookmarksAndNotes", selection.PostBookmarksAndNotes, populated ? 1 : 0);
        }
    }

    [Fact]
    public void EmptyScopeIsValidForPublicOnlyAndSelectedEmptyProjections()
    {
        foreach (NativePrivateMetadataSelection selection in new[] { default(NativePrivateMetadataSelection), All })
        {
            NativePrivateMetadataProjection empty = new([], selection, [], [], []);
            AssertValues(empty, Inspect(NativePrivateMetadataCodec.Serialize(empty)));
        }
    }

    [Fact]
    public void ExactOrdinalIdentitiesAndFullFavoriteTuplesSurviveWithoutInventingContent()
    {
        ResourceId[] threads =
        [
            Thread,
            Thread with { Source = "Synthetic" },
            Thread with { Board = "B" },
            Thread with { Thread = "0100" },
            Thread with { Board = "café" },
            Thread with { Board = "cafe\u0301" },
            new("synthetic-雪", "猫", "🐈")
        ];
        FavoriteAssetKey[] media =
        [
            Media(Thread), Media(Thread, "UNRESOLVED-MEDIA"), Media(Thread, variant: "preview"),
            Media(Thread) with { Sha256 = new string('b', 64) },
            Media(threads[1]), Media(threads[2]), Media(threads[6], "猫🐈", "原本")
        ];
        PostBookmark[] bookmarks = threads.Select((id, i) =>
            new PostBookmark(new(id, "never-seen-post"), i == 0 ? Note : "")).ToArray();
        NativePrivateMetadataProjection input = new([.. threads], All, [.. threads], [.. media], [.. bookmarks]);
        NativePrivateMetadataProjection parsed = Inspect(NativePrivateMetadataCodec.Serialize(input));

        AssertValues(input, parsed);
        Assert.Equal(threads.Length, parsed.Threads.Distinct().Count());
        Assert.Equal(media.Length, parsed.MediaFavorites.Distinct().Count());
        Assert.Equal(Note, parsed.PostBookmarksAndNotes[0].Note);
        Assert.All(parsed.PostBookmarksAndNotes.Skip(1), bookmark => Assert.Equal("", bookmark.Note));
    }

    [Fact]
    public void IndependentlyAuthoredWireAcceptsReorderedFieldsAndPreservesEscapedUnicodeAndLfNotes()
    {
        byte[] wire = Encoding.UTF8.GetBytes("""
            {
              "postBookmarksAndNotes": [
                {"note":"synthetic \u96ea \ud83d\udc08\n\t<inert>","post":"never-seen-post",
                 "thread":{"thread":"100","board":"b","source":"synthetic"}}
              ],
              "mediaFavorites": [
                {"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                 "variant":"preview","mediaId":"never-seen-media",
                 "thread":{"board":"b","source":"synthetic","thread":"100"}}
              ],
              "threadFavorites": [],
              "threads": [{"board":"b","thread":"100","source":"synthetic"}],
              "schema": "meowchan-private-v1"
            }
            """);
        NativePrivateMetadataProjection parsed = Inspect(wire);
        Assert.Equal(All, parsed.Selection);
        Assert.Equal(Thread, Assert.Single(parsed.Threads));
        Assert.Empty(parsed.ThreadFavorites);
        Assert.Equal(Media(Thread, "never-seen-media", "preview"), Assert.Single(parsed.MediaFavorites));
        Assert.Equal(new PostBookmark(new(Thread, "never-seen-post"), "synthetic 雪 🐈\n\t<inert>"),
            Assert.Single(parsed.PostBookmarksAndNotes));
        AssertValues(parsed, Inspect(NativePrivateMetadataCodec.Serialize(parsed)));
    }

    [Fact]
    public void ProjectionArraysCannotBeMutatedAndRecordCopiesDoNotModifyRetainedValues()
    {
        NativePrivateMetadataProjection parsed = Inspect(NativePrivateMetadataCodec.Serialize(Projection(All)));
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceId>)parsed.Threads).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceId>)parsed.ThreadFavorites)[0] = Other);
        Assert.Throws<NotSupportedException>(() => ((IList<FavoriteAssetKey>)parsed.MediaFavorites).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PostBookmark>)parsed.PostBookmarksAndNotes).Clear());
        _ = parsed.Threads[0] with { Board = "changed" };
        _ = parsed.MediaFavorites[0] with { Variant = "changed" };
        _ = parsed.PostBookmarksAndNotes[0] with { Note = "changed" };
        AssertValues(Projection(All), parsed);
    }

    [Fact]
    public void InspectAcceptsCallerOwnedMemorySliceAndDoesNotRetainItsMutableBytes()
    {
        NativePrivateMetadataProjection input = Projection(All);
        byte[] wire = NativePrivateMetadataCodec.Serialize(input);
        byte[] buffer = new byte[wire.Length + 10];
        Array.Fill(buffer, (byte)0xff);
        wire.CopyTo(buffer, 5);
        NativePrivateMetadataInspection inspection = NativePrivateMetadataCodec.Inspect(buffer.AsMemory(5, wire.Length));
        Assert.True(inspection.IsValid, inspection.Error.ToString());
        NativePrivateMetadataProjection parsed = Assert.IsType<NativePrivateMetadataProjection>(inspection.Projection);
        Array.Fill(buffer, (byte)0);
        AssertValues(input, parsed);
        AssertValues(input, Inspect(NativePrivateMetadataCodec.Serialize(parsed)));
    }

    [Fact]
    public void ReceivingDefaultSelectionAlwaysDropsEveryPrivateValueButKeepsPublicScope()
    {
        NativePrivateMetadataProjection incoming = Inspect(NativePrivateMetadataCodec.Serialize(Projection(All)));
        NativePrivateMetadataProjection filtered = incoming.Select();

        Assert.NotSame(incoming, filtered);
        AssertValues(Projection(default, populated: false), filtered);
        AssertValues(filtered, incoming.Select(default));
        AssertValues(filtered, Inspect(NativePrivateMetadataCodec.Serialize(filtered)));
        // Filtering is not an in-place mutation and cannot be reversed by reselecting.
        AssertValues(Projection(All), incoming);
        Assert.Throws<InvalidDataException>(() => filtered.Select(All));
        using JsonDocument document = JsonDocument.Parse(NativePrivateMetadataCodec.Serialize(filtered));
        AssertCategory(document.RootElement, "threadFavorites", false, 0);
        AssertCategory(document.RootElement, "mediaFavorites", false, 0);
        AssertCategory(document.RootElement, "postBookmarksAndNotes", false, 0);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ReceivingSelectionAllowsEveryPresentSubsetAndRejectsAnyAbsentCategory(int incomingBits)
    {
        foreach (bool populated in new[] { false, true })
        {
            NativePrivateMetadataProjection input = Projection(Selection(incomingBits), populated);
            NativePrivateMetadataProjection incoming = Inspect(NativePrivateMetadataCodec.Serialize(input));
            for (int requestedBits = 0; requestedBits < 8; requestedBits++)
            {
                NativePrivateMetadataSelection requested = Selection(requestedBits);
                if ((requestedBits & ~incomingBits) != 0)
                {
                    InvalidDataException error = Assert.Throws<InvalidDataException>(() => incoming.Select(requested));
                    Assert.Null(error.InnerException);
                    Assert.DoesNotContain(Note, error.ToString());
                    Assert.DoesNotContain("unresolved-media", error.ToString());
                    Assert.DoesNotContain("unresolved-post", error.ToString());
                }
                else
                {
                    NativePrivateMetadataProjection filtered = incoming.Select(requested);
                    Assert.NotSame(incoming, filtered);
                    AssertValues(Projection(requested, populated), filtered);
                    AssertValues(filtered, Inspect(NativePrivateMetadataCodec.Serialize(filtered)));
                }
                AssertValues(input, incoming);
            }
        }
    }

    [Fact]
    public void FromSnapshotCopiesOnlyScopedValuesAndNeverTransfersLocalAuthorityOrMediaObservations()
    {
        using TempDirectory directory = new();
        using ProfileLease owner = ProfileLease.Acquire(directory.Path);
        Seed(owner);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(owner, [Thread], All);
        Dictionary<string, byte[]> before = ReadProfileFiles(directory.Path);
        NativePrivateMetadataProjection projection = NativePrivateMetadataProjection.FromSnapshot(snapshot, owner);

        Assert.Equal(All, projection.Selection);
        Assert.Equal(Thread, Assert.Single(projection.Threads));
        Assert.Equal(Thread, Assert.Single(projection.ThreadFavorites));
        Assert.Equal(Media(Thread), Assert.Single(projection.MediaFavorites));
        Assert.Equal(FavoriteEntryFor(Thread), Assert.Single(projection.MediaFavoriteEntries));
        Assert.Equal(new PostBookmark(new(Thread, "unresolved-post"), Note), Assert.Single(projection.PostBookmarksAndNotes));
        byte[] wire = NativePrivateMetadataCodec.Serialize(projection);
        AssertValues(projection, Inspect(wire));
        Assert.True(snapshot.IsCurrent(owner));
        AssertProfileUnchanged(directory.Path, before);

        using JsonDocument document = JsonDocument.Parse(wire);
        JsonElement root = document.RootElement;
        AssertProperties(root, "schema", "threads", "threadFavorites", "mediaFavorites", "postBookmarksAndNotes");
        AssertThreadShape(root.GetProperty("threads")[0]);
        AssertThreadShape(root.GetProperty("threadFavorites")[0]);
        JsonElement media = root.GetProperty("mediaFavorites")[0];
        AssertProperties(media, "thread", "mediaId", "variant", "sha256");
        AssertThreadShape(media.GetProperty("thread"));
        JsonElement bookmark = root.GetProperty("postBookmarksAndNotes")[0];
        AssertProperties(bookmark, "thread", "post", "note");
        AssertThreadShape(bookmark.GetProperty("thread"));
        Assert.Equal(Note, bookmark.GetProperty("note").GetString());
        string text = Encoding.UTF8.GetString(wire);
        Assert.DoesNotContain(directory.Path, text);
        Assert.DoesNotContain(OriginalName, text);
        Assert.DoesNotContain("out-of-scope-synthetic-note", text);
        foreach (string localAuthorityField in new[] { "owner", "lease", "store", "revision", "fingerprint", "profile", "grant", "watch" })
            Assert.DoesNotContain(localAuthorityField, text, StringComparison.OrdinalIgnoreCase);

        // Only immutable passive values may be retained, including in private fields.
        Type[] allowedFieldTypes =
        [
            typeof(ImmutableArray<ResourceId>), typeof(NativePrivateMetadataSelection),
            typeof(ImmutableArray<FavoriteAssetKey>), typeof(ImmutableArray<FavoriteEntry>),
            typeof(ImmutableArray<PostBookmark>)
        ];
        Assert.All(typeof(NativePrivateMetadataProjection).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field =>
            {
                Assert.True(field.IsInitOnly, field.Name);
                Assert.Contains(field.FieldType, allowedFieldTypes);
            });
        Assert.DoesNotContain(Note, projection.ToString());
        Assert.DoesNotContain(directory.Path, projection.ToString());
    }

    [Fact]
    public void SnapshotProjectionAndFilteringCreateNoPrivateFilesForAnEmptyProfile()
    {
        using TempDirectory directory = new();
        using ProfileLease owner = ProfileLease.Acquire(directory.Path);
        Dictionary<string, byte[]> before = ReadProfileFiles(directory.Path);
        for (int bits = 0; bits < 8; bits++)
        {
            NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(owner, [Thread], Selection(bits));
            NativePrivateMetadataProjection projection = NativePrivateMetadataProjection.FromSnapshot(snapshot, owner);
            Assert.Equal(Selection(bits), projection.Selection);
            Assert.Empty(projection.ThreadFavorites);
            Assert.Empty(projection.MediaFavorites);
            Assert.Empty(projection.PostBookmarksAndNotes);
            NativePrivateMetadataProjection received = Inspect(NativePrivateMetadataCodec.Serialize(projection));
            AssertValues(projection, received);
            Assert.Equal(default, received.Select().Selection);
        }
        AssertProfileUnchanged(directory.Path, before);
    }

    [Fact]
    public void ProjectionDoesNotRunBookmarkPendingCleanupOrMutateSeededProfile()
    {
        using TempDirectory directory = new();
        using ProfileLease owner = ProfileLease.Acquire(directory.Path);
        Seed(owner);
        string pending = Path.Combine(directory.Path, "post-bookmarks.json.pending-11111111111111111111111111111111");
        File.WriteAllText(pending, "synthetic incomplete note; must remain untouched");
        Dictionary<string, byte[]> before = ReadProfileFiles(directory.Path);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(owner, [Thread], All);
        NativePrivateMetadataProjection projection = NativePrivateMetadataProjection.FromSnapshot(snapshot, owner);
        _ = Inspect(NativePrivateMetadataCodec.Serialize(projection)).Select();
        AssertProfileUnchanged(directory.Path, before);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void FromSnapshotRejectsStaleSelectedStoreEvenWhenOnlyOutOfScopeValuesChanged(int category)
    {
        using TempDirectory directory = new();
        using ProfileLease owner = ProfileLease.Acquire(directory.Path);
        Seed(owner);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(owner, [Thread], Selection(1 << category));
        switch (category)
        {
            case 0: new ThreadFavoriteStore(directory.Path, owner).Set(Other, false); break;
            case 1: new FavoriteStore(directory.Path, owner).SetFavorite(new(Media(Other), 123, OriginalName), false); break;
            case 2: new PostBookmarkStore(directory.Path, owner).Remove(new(Other, "unresolved-post"), 2); break;
        }
        Dictionary<string, byte[]> before = ReadProfileFiles(directory.Path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => NativePrivateMetadataProjection.FromSnapshot(snapshot, owner));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(directory.Path, error.ToString());
        Assert.DoesNotContain(Note, error.ToString());
        AssertProfileUnchanged(directory.Path, before);
    }

    [Fact]
    public void FromSnapshotRejectsForeignLiveOwnerAndSamePathReplacementOwner()
    {
        using TempDirectory directory = new();
        using TempDirectory foreignDirectory = new();
        using ProfileLease foreign = ProfileLease.Acquire(foreignDirectory.Path);
        NativePrivateMetadataSnapshot snapshot;
        using (ProfileLease owner = ProfileLease.Acquire(directory.Path))
        {
            snapshot = NativePrivateMetadataSnapshot.Capture(owner, [Thread], All);
            Dictionary<string, byte[]> originalBefore = ReadProfileFiles(directory.Path);
            Dictionary<string, byte[]> foreignBefore = ReadProfileFiles(foreignDirectory.Path);
            Assert.Throws<InvalidDataException>(() => NativePrivateMetadataProjection.FromSnapshot(snapshot, foreign));
            AssertProfileUnchanged(directory.Path, originalBefore);
            AssertProfileUnchanged(foreignDirectory.Path, foreignBefore);
            _ = NativePrivateMetadataProjection.FromSnapshot(snapshot, owner);
        }
        using ProfileLease replacement = ProfileLease.Acquire(directory.Path);
        Dictionary<string, byte[]> before = ReadProfileFiles(directory.Path);
        Assert.Throws<InvalidDataException>(() => NativePrivateMetadataProjection.FromSnapshot(snapshot, replacement));
        AssertProfileUnchanged(directory.Path, before);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FromSnapshotRejectsDisposedOriginalOwnerEvenForPublicOnlySnapshot(bool selected)
    {
        using TempDirectory directory = new();
        using ProfileLease owner = ProfileLease.Acquire(directory.Path);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(owner, [Thread], selected ? All : default);
        owner.Dispose();
        Dictionary<string, byte[]> before = ReadProfileFiles(directory.Path);
        Assert.Throws<ObjectDisposedException>(() => NativePrivateMetadataProjection.FromSnapshot(snapshot, owner));
        AssertProfileUnchanged(directory.Path, before);
    }

    [Fact]
    public void AlreadyCopiedProjectionRemainsPassiveAndUsableAfterSourceChangesAndOwnerDisposal()
    {
        using TempDirectory directory = new();
        NativePrivateMetadataProjection projection;
        using (ProfileLease owner = ProfileLease.Acquire(directory.Path))
        {
            Seed(owner);
            NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(owner, [Thread], All);
            projection = NativePrivateMetadataProjection.FromSnapshot(snapshot, owner);
            new ThreadFavoriteStore(directory.Path, owner).Set(Thread, false);
            new FavoriteStore(directory.Path, owner).SetFavorite(new(Media(Thread), 123, OriginalName), false);
            new PostBookmarkStore(directory.Path, owner).Remove(new(Thread, "unresolved-post"), 2);
            Assert.Throws<InvalidDataException>(() => NativePrivateMetadataProjection.FromSnapshot(snapshot, owner));
        }
        Dictionary<string, byte[]> before = ReadProfileFiles(directory.Path);
        NativePrivateMetadataProjection parsed = Inspect(NativePrivateMetadataCodec.Serialize(projection));
        AssertValues(projection, parsed);
        Assert.Equal(Thread, Assert.Single(parsed.ThreadFavorites));
        Assert.Equal(Media(Thread), Assert.Single(parsed.MediaFavorites));
        Assert.Equal(Note, Assert.Single(parsed.PostBookmarksAndNotes).Note);
        Assert.Equal(All, parsed.Select(All).Selection);
        Assert.Equal(default, parsed.Select().Selection);
        AssertProfileUnchanged(directory.Path, before);
    }

    private static void Seed(ProfileLease owner)
    {
        ThreadFavoriteStore threads = new(owner.ProfileDirectory, owner);
        FavoriteStore media = new(owner.ProfileDirectory, owner);
        PostBookmarkStore bookmarks = new(owner.ProfileDirectory, owner);
        threads.Set(Thread, true);
        threads.Set(Other, true);
        media.SetFavorite(new(Media(Thread), 123, OriginalName), true);
        media.SetFavorite(new(Media(Other), 123, OriginalName), true);
        bookmarks.Upsert(new(Thread, "unresolved-post"), Note, 0, false);
        bookmarks.Upsert(new(Other, "unresolved-post"), "out-of-scope-synthetic-note", 1, false);
    }

    private static void AssertProperties(JsonElement value, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static void AssertThreadShape(JsonElement value) => AssertProperties(value, "source", "board", "thread");

    private static void AssertCategory(JsonElement root, string property, bool selected, int count)
    {
        JsonElement category = root.GetProperty(property);
        Assert.Equal(selected ? JsonValueKind.Array : JsonValueKind.Null, category.ValueKind);
        if (selected) Assert.Equal(count, category.GetArrayLength());
    }

    // The fixture acquires/seeds its own synthetic lease before taking the baseline.
    // Exclude only the actively exclusively-held lock; all other bytes and directory names count.
    private static Dictionary<string, byte[]> ReadProfileFiles(string path) =>
        Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetFileName(file).EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(file => Path.GetRelativePath(path, file),
                file => Directory.Exists(file) ? Array.Empty<byte>() : File.ReadAllBytes(file), StringComparer.Ordinal);

    private static void AssertProfileUnchanged(string path, Dictionary<string, byte[]> expected)
    {
        Dictionary<string, byte[]> actual = ReadProfileFiles(path);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var file in expected) Assert.Equal(file.Value, actual[file.Key]);
    }
}
