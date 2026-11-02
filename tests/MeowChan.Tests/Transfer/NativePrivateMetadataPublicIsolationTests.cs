using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic, disposable profiles only. This tests the existing public catalog codec,
// not the future private wire, package publication, import apply or network access.
public sealed class NativePrivateMetadataPublicIsolationTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Thread = new("4chan", "b", "7001");
    private static readonly ResourceId PrivateOnlyThread = new("synthetic-private-only-source", "private-board", "private-thread");
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic public isolation payload");
    private static readonly string PayloadHash = Hash(Payload);
    private const string Note = "SYNTHETIC-PRIVATE-NOTE-雪-<script>inert</script>";
    private const string ChangedNote = "SYNTHETIC-PRIVATE-NOTE-CHANGED";
    private const string PrivateFileName = "SYNTHETIC-PRIVATE-FAVORITE-NAME.jpg";
    private const string PrivateOnlyNote = "SYNTHETIC-OUT-OF-SCOPE-NOTE";
    private const string PublicTitle = "Synthetic public thread title";

    public static IEnumerable<object[]> PublicSchemasAndSelections()
    {
        foreach (string schema in new[] { "chan-v1", "chan-v2", "chan-v3" })
            for (int mask = 0; mask < 8; mask++)
                yield return [schema, (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0];
    }

    [Theory]
    [MemberData(nameof(PublicSchemasAndSelections))]
    public void PrivateStoresAndEverySnapshotSelectionLeavePublicBytesUnchanged(
        string schema, bool threadFavorites, bool mediaFavorites, bool bookmarksAndNotes)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        NativeTransferCatalog catalog = PublicCatalog(schema);
        byte[] baseline = NativeTransferCatalogSerializer.Serialize(catalog);
        AssertPublicShapeAndRoundtrip(baseline, schema);

        Seed(lease);
        AssertUnchanged(baseline, catalog, lease);
        var selection = new NativePrivateMetadataSelection(threadFavorites, mediaFavorites, bookmarksAndNotes);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection);
        Assert.Equal(selection, snapshot.Selection);
        Assert.Equal(Thread, Assert.Single(snapshot.Threads));
        Assert.Equal(threadFavorites ? 1 : 0, snapshot.ThreadFavorites.Length);
        Assert.Equal(mediaFavorites ? 1 : 0, snapshot.MediaFavorites.Length);
        Assert.Equal(bookmarksAndNotes ? 1 : 0, snapshot.PostBookmarksAndNotes.Length);
        if (threadFavorites) Assert.Equal(Thread, Assert.Single(snapshot.ThreadFavorites));
        if (mediaFavorites) Assert.Equal(Favorite(Thread), Assert.Single(snapshot.MediaFavorites));
        if (bookmarksAndNotes) Assert.Equal(new PostBookmark(new(Thread, "15"), Note), Assert.Single(snapshot.PostBookmarksAndNotes));
        Assert.True(snapshot.IsCurrent(lease));
        NativePrivateMetadataProjection privateProjection = NativePrivateMetadataProjection.FromSnapshot(snapshot, lease);
        byte[] privateBytes = NativePrivateMetadataCodec.Serialize(privateProjection);
        NativePrivateMetadataInspection privateInspection = NativePrivateMetadataCodec.Inspect(privateBytes);
        Assert.True(privateInspection.IsValid, privateInspection.Error.ToString());
        Assert.Equal(selection, privateInspection.Projection!.Select(selection).Selection);
        NativePrivateMetadataProjection filtered = privateInspection.Projection.Select();
        Assert.Equal(default, filtered.Selection);
        Assert.Empty(filtered.ThreadFavorites);
        Assert.Empty(filtered.MediaFavorites);
        Assert.Empty(filtered.PostBookmarksAndNotes);
        AssertUnchanged(baseline, catalog, lease);

        // The selected snapshot really can become stale; neither that fact nor the
        // subsequent private values may alter the independent public projection.
        Assert.True(new ThreadFavoriteStore(directory.Path, lease).Set(Thread, false).Changed);
        Assert.True(new FavoriteStore(directory.Path, lease).SetFavorite(Favorite(Thread), false));
        new PostBookmarkStore(directory.Path, lease).Upsert(new(Thread, "15"), ChangedNote, 2, true);
        Assert.Equal(!threadFavorites && !mediaFavorites && !bookmarksAndNotes, snapshot.IsCurrent(lease));
        if (bookmarksAndNotes) Assert.Equal(Note, Assert.Single(snapshot.PostBookmarksAndNotes).Note);
        AssertUnchanged(baseline, catalog, lease);
    }

    [Theory]
    [InlineData("chan-v1")]
    [InlineData("chan-v2")]
    [InlineData("chan-v3")]
    public void DefaultOffAndSelectedEmptyRemainDistinctWithoutAddingPublicWireFields(string schema)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        NativeTransferCatalog catalog = PublicCatalog(schema);
        byte[] baseline = NativeTransferCatalogSerializer.Serialize(catalog);
        AssertPublicShapeAndRoundtrip(baseline, schema);
        string[] before = Directory.GetFileSystemEntries(directory.Path).Order(StringComparer.Ordinal).ToArray();

        NativePrivateMetadataSnapshot off = NativePrivateMetadataSnapshot.Capture(lease, [Thread]);
        NativePrivateMetadataSnapshot empty = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true));
        Assert.Equal(default(NativePrivateMetadataSelection), off.Selection);
        Assert.Equal(new NativePrivateMetadataSelection(true, true, true), empty.Selection);
        foreach (NativePrivateMetadataSnapshot snapshot in new[] { off, empty })
        {
            Assert.Empty(snapshot.ThreadFavorites);
            Assert.Empty(snapshot.MediaFavorites);
            Assert.Empty(snapshot.PostBookmarksAndNotes);
            Assert.True(snapshot.IsCurrent(lease));
            Assert.Equal(baseline, NativeTransferCatalogSerializer.Serialize(catalog));
        }
        Assert.Equal(before, Directory.GetFileSystemEntries(directory.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("chan-v1")]
    [InlineData("chan-v2")]
    [InlineData("chan-v3")]
    public void PublicSerializationDoesNotCarryOrRequireTheSnapshotOwnerLease(string schema)
    {
        using TempDirectory directory = new();
        using TempDirectory other = new();
        NativeTransferCatalog catalog = PublicCatalog(schema);
        byte[] baseline = NativeTransferCatalogSerializer.Serialize(catalog);
        AssertPublicShapeAndRoundtrip(baseline, schema);
        NativePrivateMetadataSnapshot snapshot;
        string[] authority;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            Seed(lease);
            snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true));
            Assert.Equal(Note, Assert.Single(snapshot.PostBookmarksAndNotes).Note);
            authority = AuthorityStrings(lease);
            using ProfileLease foreign = ProfileLease.Acquire(other.Path);
            Assert.False(snapshot.IsCurrent(foreign));
            Assert.Equal(baseline, NativeTransferCatalogSerializer.Serialize(catalog));
        }

        // Keep the snapshot alive across disposal/reacquisition. Public serialization
        // has no owner parameter and must not inherit a live lease or its fences.
        Assert.Equal(baseline, NativeTransferCatalogSerializer.Serialize(catalog));
        using ProfileLease restarted = ProfileLease.Acquire(directory.Path);
        Assert.False(snapshot.IsCurrent(restarted));
        NativePrivateMetadataSnapshot fresh = NativePrivateMetadataSnapshot.Capture(restarted, [Thread], new(true, true, true));
        Assert.True(fresh.IsCurrent(restarted));
        byte[] actual = NativeTransferCatalogSerializer.Serialize(catalog);
        Assert.Equal(baseline, actual);
        AssertNoPrivateValues(actual, authority.Concat([other.Path]));
    }

    private static void Seed(ProfileLease lease)
    {
        ThreadFavoriteStore threads = new(lease.ProfileDirectory, lease);
        threads.Set(Thread, true);
        threads.Set(PrivateOnlyThread, true);
        FavoriteStore media = new(lease.ProfileDirectory, lease);
        media.SetFavorite(Favorite(Thread), true);
        media.SetFavorite(Favorite(PrivateOnlyThread), true);
        PostBookmarkStore notes = new(lease.ProfileDirectory, lease);
        notes.Upsert(new(Thread, "15"), Note, 0, false);
        notes.Upsert(new(PrivateOnlyThread, "private-post"), PrivateOnlyNote, 1, false);
    }

    private static FavoriteEntry Favorite(ResourceId thread) =>
        new(new(thread, "111", "original", PayloadHash), Payload.Length, PrivateFileName);

    private static string[] AuthorityStrings(ProfileLease lease)
    {
        ThreadFavoriteSnapshot threads = new ThreadFavoriteStore(lease.ProfileDirectory, lease).ReadSnapshot();
        FavoriteStoreSnapshot media = new FavoriteStore(lease.ProfileDirectory, lease).ReadTransferSnapshot();
        PostBookmarkTransferSnapshot notes = PostBookmarkStore.ReadTransferSnapshot(lease.ProfileDirectory, lease);
        Assert.True(media.InstanceId.HasValue);
        return [lease.ProfileDirectory, threads.Fingerprint, media.Fingerprint, notes.Fingerprint,
            media.InstanceId.Value.ToString("D"), media.InstanceId.Value.ToString("N"),
            "thread-favorites.json", "media-favorites.json", "post-bookmarks.json",
            ".meowchan-profile.lock", ".meowchan-profile.protocol",
            nameof(ProfileLease), nameof(FavoriteStoreSnapshot), nameof(PostBookmarkTransferSnapshot),
            nameof(NativePrivateMetadataSnapshot)];
    }

    private static void AssertUnchanged(byte[] baseline, NativeTransferCatalog catalog, ProfileLease lease)
    {
        byte[] actual = NativeTransferCatalogSerializer.Serialize(catalog);
        Assert.Equal(baseline, actual);
        AssertNoPrivateValues(actual, AuthorityStrings(lease));
    }

    private static void AssertNoPrivateValues(byte[] bytes, IEnumerable<string> authority)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        string[] values = StringValues(document.RootElement).ToArray();
        foreach (string forbidden in authority.Concat([Note, ChangedNote, PrivateOnlyNote, PrivateFileName,
                     PrivateOnlyThread.Source, PrivateOnlyThread.Board, PrivateOnlyThread.Thread!, "private-post"]))
        {
            Assert.False(string.IsNullOrEmpty(forbidden));
            // Inspect decoded strings too: JSON escaping must not hide a leaked note/path.
            Assert.DoesNotContain(values, value => value.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private static IEnumerable<string> StringValues(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String) yield return node.GetString()!;
        else if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in node.EnumerateObject())
                foreach (string value in StringValues(property.Value)) yield return value;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in node.EnumerateArray())
                foreach (string value in StringValues(item)) yield return value;
        }
    }

    private static void AssertPublicShapeAndRoundtrip(byte[] bytes, string schema)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Fields(root, "schema", "exportedAtUtc", "threads");
        Assert.Equal(schema, root.GetProperty("schema").GetString());
        JsonElement thread = Assert.Single(root.GetProperty("threads").EnumerateArray());
        string[] threadFields = ["id", "pageUrl", "title", "sourceState", "firstSeenAtUtc", "capturedAtUtc",
            "captureRevision", "decimalPostOrder", "posts"];
        Fields(thread, schema == "chan-v1" ? threadFields : [.. threadFields, "retainedBindings"]);
        Fields(thread.GetProperty("id"), "source", "board", "thread");
        Assert.Equal(PublicTitle, thread.GetProperty("title").GetString());
        JsonElement post = Assert.Single(thread.GetProperty("posts").EnumerateArray());
        Fields(post, "id", "ordinal", "fields", "postedAtUtc", "subject", "author", "html", "isDeleted", "attachments");
        Assert.Equal("Synthetic public body", post.GetProperty("html").GetString());
        AssertAttachmentFields(Assert.Single(post.GetProperty("attachments").EnumerateArray()), retained: false);
        if (schema != "chan-v1")
        {
            JsonElement retained = Assert.Single(thread.GetProperty("retainedBindings").EnumerateArray());
            Fields(retained, "postId", "attachment");
            AssertAttachmentFields(retained.GetProperty("attachment"), retained: true);
        }

        // The existing validator must accept the public bytes, with independently
        // measured synthetic payload evidence, and reserialization must be exact.
        NativeTransferEnvelope envelope = new("5d7750f7-1c3a-4093-b770-7c854fcfb117", schema, "unused-by-catalog-validator",
            new(bytes.Length, Hash(bytes)), [new(Payload.Length, PayloadHash)])
        {
            CatalogBytes = bytes.ToImmutableArray(),
            MeasuredPayloads = ImmutableDictionary<string, TransferMeasuredBlob>.Empty.Add(PayloadHash,
                new(Payload.Length, PayloadHash, Convert.ToBase64String(MD5.HashData(Payload)), Convert.ToBase64String(SHA1.HashData(Payload))))
        };
        TransferCatalogInspection inspected = NativeTransferCatalogValidator.Inspect(bytes, envelope);
        Assert.True(inspected.IsCatalogValid, inspected.Error.ToString());
        Assert.Equal(bytes, NativeTransferCatalogSerializer.Serialize(inspected.Catalog!));
    }

    private static void AssertAttachmentFields(JsonElement attachment, bool retained)
    {
        string[] fields = ["position", "mediaId", "variant", "url", "originalFileName", "expectedSize", "expectedMd5",
            "expectedSha1", "expectedSha256", "sizeHint", "revisionKey", "role", "status", "payloadSha256", "history"];
        Fields(attachment, retained ? fields : [.. fields, "isDeleted"]);
        Assert.Equal(JsonValueKind.Null, attachment.GetProperty("sizeHint").ValueKind);
        JsonElement history = attachment.GetProperty("history");
        if (history.ValueKind != JsonValueKind.Null)
            Fields(history, "kind", "acceptedAtUtc", "sizeMismatch", "md5Mismatch");
    }

    // Exact allowlists also reject added private/authority objects even when empty,
    // numeric or boolean (a value-only secret scan would miss those regressions).
    private static void Fields(JsonElement node, params string[] expected)
    {
        Assert.Equal(JsonValueKind.Object, node.ValueKind);
        Assert.Equal(expected.Order(StringComparer.Ordinal), node.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    private static NativeTransferCatalog PublicCatalog(string schema)
    {
        TransferAttachment current = Attachment("15", "111", keyed: schema == "chan-v3", history: false);
        TransferPost post = new(new("15", 0, PostFieldPresence.All, At, "Synthetic public subject", null,
            "Synthetic public body", Attachments: [current.Metadata]), [current]);
        TransferThread thread = new(Thread, "https://boards.4chan.org/b/thread/7001", PublicTitle,
            ThreadSourceState.NotFound, At, At, 0, true, [post]);
        if (schema != "chan-v1")
            thread = thread with { RetainedBindings = [new("999", Attachment("999", "112", keyed: true, history: true))] };
        NativeTransferCatalog catalog = new(At, [thread]);
        Assert.Equal(schema, NativeTransferCatalogSerializer.ContentSchema(catalog));
        return catalog;
    }

    private static TransferAttachment Attachment(string post, string mediaId, bool keyed, bool history)
    {
        MediaProviderMetadata provider = new(mediaId, "original", $"https://i.4cdn.org/b/{mediaId}.jpg",
            "synthetic-public-original.jpg", Payload.Length, null, PayloadHash);
        CapturedAttachment metadata = new(0, mediaId, Url: provider.Url, OriginalFileName: provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize)
        {
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = keyed ? MediaRevisionKeys.CreateAttachmentKey(Thread, post, 0, provider) : null
        };
        return new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained, PayloadHash,
            history ? new("strict", At, false, false) : null);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
