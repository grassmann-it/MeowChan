using System.Collections.Immutable;
using System.Text;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tests.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// R1 synthetic metadata only. No H/verified read, product import, Host or external profile.
public sealed class LocalCollectionSnapshotTests
{
    private const string Instance = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private static readonly Guid Operation = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly LocalCollectionId Collection = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly LocalCollectionId Other = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly string Hash = new('a', 64);
    private static readonly LocalImportCodec Codec = new();
    private static LocalItemId Item(int i) => new(Guid.Parse($"22222222-2222-4222-8222-{i + 1:x12}"));
    private static LocalRevisionId Revision(int i) => new(Guid.Parse($"33333333-3333-4333-8333-{i + 1:x12}"));
    private static LocalMediaRevisionReference Reference(int i, LocalCollectionId? collection = null)
        => new(collection ?? Collection, Item(i), Revision(i));
    private static LocalSourceClaims Claims(LocalFieldPresence presence = LocalFieldPresence.Present,
        string? url = null) => new(1, "synthetic", 1, "claim", url, presence,
            presence == LocalFieldPresence.Absent ? ImmutableArray<string>.Empty : ImmutableArray.Create("tag", "tag", "猫"),
            ImmutableArray.Create(new LocalClaimOmission("privateCategories", LocalClaimOmissionReason.NotSelected)));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task EveryPreCollectionVersionReturnsUnboundEmptyWithoutWriting(int version)
    {
        using Workspace files = new();
        files.SeedLegacy(version);
        byte[] before = File.ReadAllBytes(files.Database);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        LocalCollectionPage page = await reader.ListLocalCollectionsAsync(7, 500, CancellationToken.None);
        Assert.Equal(0L, page.TotalCount);
        Assert.Equal(7, page.Offset);
        Assert.Empty(page.Items);
        Assert.Null(await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None));
        using (var c = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly))
        using (var tx = c.BeginTransaction(deferred: true))
        {
            Assert.Empty(ArchiveSql.ReadLocalCollectionPage(c, tx, 0, 1, CancellationToken.None).Items);
            Assert.Null(ArchiveSql.ReadLocalCollection(c, tx, Collection, files.Profile, Codec, CancellationToken.None));
            tx.Commit();
        }
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        Assert.Equal(new[] { "archive.db" }, Directory.GetFiles(files.Profile).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(files.Profile));
    }

    [Theory]
    [InlineData(9)] [InlineData(11)]
    public async Task ListingIsBinaryOrderedBoundedAndCountIsCollectionsNotItems(int version)
    {
        using Workspace files = new();
        if (version == 9) files.SeedLegacy(9);
        else await files.CreateCurrentAsync();
        files.SeedCollection(Other, 3, "same");
        files.SeedCollection(Collection, 2, "same");
        byte[] before = File.ReadAllBytes(files.Database);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        var first = await reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None);
        Assert.Equal(2L, first.TotalCount);
        Assert.Equal(Collection, Assert.Single(first.Items).Id);
        Assert.Equal(2, first.Items[0].ItemCount);
        var second = await reader.ListLocalCollectionsAsync(1, 500, CancellationToken.None);
        Assert.Equal(2L, second.TotalCount);
        Assert.Equal(Other, Assert.Single(second.Items).Id);
        Assert.Equal(3, second.Items[0].ItemCount);
        var end = await reader.ListLocalCollectionsAsync(int.MaxValue, 500, CancellationToken.None);
        Assert.Equal(int.MaxValue, end.Offset); Assert.Equal(2L, end.TotalCount); Assert.Empty(end.Items);
        Assert.Equal(before, File.ReadAllBytes(files.Database));
    }

    [Theory]
    [InlineData(9)] [InlineData(11)]
    public async Task CompatibleEmptyAndAbsentCollectionAreEmptyButRegisteredEmptyIsAnError(int version)
    {
        using Workspace files = new();
        if (version == 9) files.SeedLegacy(9);
        else await files.CreateCurrentAsync();
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Empty((await reader.ListLocalCollectionsAsync(0, 500, CancellationToken.None)).Items);
        Assert.Null(await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None));
        files.SeedCollection(Collection, 0);
        await RejectAsync(() => reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None), LocalImportError.IntegrityFailure);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.IntegrityFailure);
    }

    [Theory]
    [InlineData(-1, 1)] [InlineData(0, 0)] [InlineData(0, 501)]
    public async Task InvalidArgumentsAreRejectedBeforeDatabaseIo(int offset, int limit)
    {
        using Workspace files = new(); await files.CreateCurrentAsync();
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        File.Delete(files.Database);
        Assert.Throws<LocalImportCodecException>(() => { _ = reader.ListLocalCollectionsAsync(offset, limit, CancellationToken.None); });
        Assert.Throws<ArgumentException>(() => { _ = reader.ReadLocalCollectionAsync(default, CancellationToken.None); });
    }

    [Theory]
    [InlineData("PRAGMA user_version=0")]
    [InlineData("PRAGMA user_version=13")]
    [InlineData("ALTER TABLE archive_posts DROP COLUMN attachments_were_null; PRAGMA user_version=11")]
    [InlineData("PRAGMA application_id=1")]
    [InlineData("DROP TABLE local_import_receipts")]
    [InlineData("DROP TRIGGER import_fence_local_items_INSERT")]
    [InlineData("UPDATE archive_import_fence SET instance=upper(instance)")]
    [InlineData("UPDATE archive_import_fence SET instance=CAST(X'80' AS TEXT)")]
    [InlineData("UPDATE archive_import_fence SET instance=CAST(instance AS BLOB)")]
    [InlineData("UPDATE archive_import_fence SET instance=zeroblob(1048577)")]
    [InlineData("DELETE FROM archive_import_fence")]
    [InlineData("INSERT INTO archive_import_fence VALUES(2,'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',0)")]
    [InlineData("UPDATE archive_import_fence SET generation=-1")]
    [InlineData("UPDATE archive_import_fence SET generation=1.5")]
    public async Task InvalidTargetIsNeverAnEmptyLibrary(string mutation)
    {
        using Workspace files = new(); await files.CreateCurrentAsync();
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        files.Corrupt(mutation);
        await RejectAsync(() => reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None), LocalImportError.IntegrityFailure);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.IntegrityFailure);
    }

    [Fact]
    public async Task CurrentDifferentInstanceIsAllowedAndMissingDatabaseIsNotAbsence()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        const string different = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
        files.Execute("UPDATE archive_import_fence SET instance=$value", ("$value", different));
        Assert.Equal(different, (await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None))!.CatalogInstance);
        File.Delete(files.Database);
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None));
        Assert.Null(missing.FileName); Assert.Null(missing.InnerException);
    }

    [Fact]
    public async Task SameBytesBlobCollectionKeyIsNotSilentlyMissing()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 0);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        files.Corrupt("UPDATE local_collections SET collection_id=CAST(collection_id AS BLOB)");
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.IntegrityFailure);
        await RejectAsync(() => reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None), LocalImportError.IntegrityFailure);
    }

    [Fact]
    public async Task UnreadableDatabaseIsIoFailureNotEmpty()
    {
        using Workspace files = new(); await files.CreateCurrentAsync();
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        File.WriteAllBytes(files.Database, Encoding.UTF8.GetBytes("synthetic-not-a-sqlite-database"));
        await RejectAsync(() => reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None), LocalImportError.IoFailure);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.IoFailure);
    }

    [Theory]
    [InlineData(9)] [InlineData(11)]
    public async Task FrozenCompleteSnapshotRetainsFullMembershipAfterRowsAndReaderAreGone(int version)
    {
        using Workspace files = new();
        if (version == 9) files.SeedLegacy(9);
        else await files.CreateCurrentAsync();
        files.SeedCollection(Collection, 3, "猫");
        files.SetClaims(0, Encoding.UTF8.GetString(Codec.EncodeClaims(Claims())));
        files.SetClaims(1, Encoding.UTF8.GetString(Codec.EncodeClaims(Claims(LocalFieldPresence.Absent))));
        files.SetDisplay(0, null); files.SetDisplay(1, ""); files.SetDisplay(2, "café猫.jpg");
        var reader = await ArchiveReader.OpenAsync(files.Profile);
        byte[] before = File.ReadAllBytes(files.Database);
        var snapshot = Assert.IsType<LocalCollectionSnapshot>(await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(files.Database));
        Assert.Equal(3, snapshot.Count); Assert.Equal(3, snapshot.Summary.ItemCount);
        Assert.Equal(Instance, snapshot.CatalogInstance); Assert.Equal(1L, snapshot.Summary.CollectionRevision);
        Assert.Equal(3, snapshot.GetPage().Count);
        Assert.Equal(new[] { Reference(0), Reference(1), Reference(2) }, snapshot.GetPage().Select(x => x.Reference));
        Assert.All(snapshot.GetPage(), x => Assert.Equal(Hash, x.Sha256)); // No hash deduplication.
        Assert.Null(snapshot.Find(Reference(0))!.DisplayName);
        Assert.Equal("", snapshot.Find(Reference(1))!.DisplayName);
        Assert.Equal("café猫.jpg", snapshot.Find(Reference(2))!.DisplayName);
        Assert.Equal(new[] { "tag", "tag", "猫" }, snapshot.Find(Reference(0))!.Claims!.SourceTags);
        Assert.Equal(LocalFieldPresence.Absent, snapshot.Find(Reference(1))!.Claims!.TagsPresence);
        Assert.Null(snapshot.Find(Reference(2))!.Claims);
        Assert.Null(snapshot.Find(Reference(0, Other)));
        Assert.Null(snapshot.Find(new(Collection, Item(0), Revision(2))));
        Assert.Throws<ArgumentException>(() => snapshot.Find(default));
        Assert.Throws<ArgumentException>(() => snapshot.Find(new(Collection, default, Revision(0))));
        Assert.Empty(snapshot.GetPage(int.MaxValue, 500));
        Assert.Throws<LocalImportCodecException>(() => snapshot.GetPage(-1, 1));
        Assert.Throws<LocalImportCodecException>(() => snapshot.GetPage(0, 501));
        Assert.IsType<ImmutableArray<LocalMediaRevisionSnapshot>>(snapshot.GetPage());
        long generation = snapshot.ObservedCatalogGeneration;
        files.Execute("DELETE FROM local_media_revisions; DELETE FROM local_items; DELETE FROM local_collections;");
        await reader.DisposeAsync();
        File.Delete(files.Database); // Page/Find cannot reopen SQL or the absent payload root.
        Assert.Equal(3, snapshot.Count); Assert.Equal(generation, snapshot.ObservedCatalogGeneration);
        Assert.Equal("猫", snapshot.Summary.Title); Assert.NotNull(snapshot.Find(Reference(0))!.Claims);
        Assert.Equal(2, snapshot.GetPage(1, 500).Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(files.Profile, "local-import-operations")));
    }

    [Theory]
    [InlineData(1)] [InlineData(1000)]
    public async Task FullItemBoundIsMaterializedNotJustOnePage(int count)
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, count);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        var snapshot = (await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None))!;
        Assert.Equal(count, snapshot.Count);
        Assert.Equal(Math.Min(500, count), snapshot.GetPage(0, 500).Count);
        Assert.NotNull(snapshot.Find(Reference(count - 1)));
    }

    [Fact]
    public async Task ThousandAndOneItemsFailsBeforeClaimDecode()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1001, ignoreChecks: true);
        files.SetClaims(0, "invalid-json");
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        await RejectAsync(() => reader.ListLocalCollectionsAsync(0, 500, CancellationToken.None), LocalImportError.BudgetExceeded);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.BudgetExceeded);
    }

    [Theory]
    [InlineData("DELETE FROM local_media_revisions")]
    [InlineData("UPDATE local_media_revisions SET role=2")]
    [InlineData("UPDATE local_media_revisions SET role=1.5")]
    public async Task SummaryDoesNotHideMissingOrInvalidOriginals(string mutation)
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        files.Corrupt(mutation);
        await RejectAsync(() => reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None), LocalImportError.IntegrityFailure);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.IntegrityFailure);
    }

    [Theory]
    [InlineData("UPDATE local_items SET ordinal=4")]
    [InlineData("UPDATE local_items SET ordinal=1.5")]
    [InlineData("UPDATE local_media_revisions SET length=-1")]
    [InlineData("UPDATE local_media_revisions SET length=1.5")]
    [InlineData("UPDATE local_media_revisions SET sha256=upper(sha256)")]
    [InlineData("UPDATE local_media_revisions SET sha256=CAST(sha256 AS BLOB)")]
    [InlineData("UPDATE local_media_revisions SET sha256='short'")]
    [InlineData("UPDATE local_media_revisions SET revision_id=upper('abcdefab-abcd-4abc-8abc-abcdefabcdef')")]
    [InlineData("UPDATE local_media_revisions SET display_name=CAST(display_name AS BLOB)")]
    [InlineData("UPDATE local_media_revisions SET display_name=CAST(X'80' AS TEXT)")]
    [InlineData("UPDATE local_collections SET revision=2")]
    [InlineData("UPDATE local_collections SET title=CAST(X'80' AS TEXT)")]
    public async Task StoredTypeAndIdentityCorruptionIsNotNormalized(string mutation)
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        files.Corrupt(mutation);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.IntegrityFailure);
    }

    [Theory]
    [InlineData("C:photo.jpg")] [InlineData("/photo.jpg")] [InlineData("dir/file.jpg")]
    [InlineData("dir\\file.jpg")] [InlineData("\\\\server\\photo.jpg")]
    public async Task DisplayNameUsesTheExistingPlatformIndependentL0Rejection(string display)
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        files.SetDisplay(0, display);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.InvalidSelection);
    }

    [Fact]
    public async Task MultibyteTitleAndDisplayLimitsAreRawUtf8BytesAndAllowEmpty()
    {
        using Workspace files = new(); await files.CreateCurrentAsync();
        string boundary = new('é', 2048);
        files.SeedCollection(Collection, 1, boundary); files.SetDisplay(0, boundary);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Equal(boundary, (await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None))!.Find(Reference(0))!.DisplayName);
        files.Corrupt("UPDATE local_collections SET title=$value", ("$value", boundary + "x"));
        await RejectAsync(() => reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None), LocalImportError.BudgetExceeded);
        files.Execute("UPDATE local_collections SET title=''");
        files.Corrupt("UPDATE local_media_revisions SET display_name=$value", ("$value", boundary + "x"));
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.BudgetExceeded);
        files.SetDisplay(0, "");
        var empty = (await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None))!;
        Assert.Equal("", empty.Summary.Title); Assert.Equal("", empty.Find(Reference(0))!.DisplayName);
    }

    [Theory]
    [InlineData("not-json", (int)LocalImportError.InvalidSelection)]
    [InlineData("null", (int)LocalImportError.InvalidSelection)]
    [InlineData("", (int)LocalImportError.IntegrityFailure)]
    public async Task SummariesDoNotLoadClaimsButSnapshotsFailClosed(string raw, int error)
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        files.SetClaims(0, raw, ignoreChecks: true);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Single((await reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None)).Items);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), (LocalImportError)error);
    }

    [Fact]
    public async Task DefaultUrlPolicyFailsClosedButTrustedSqlPolicyMayReadInertClaims()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        const string url = "https://example.invalid/public";
        var trusted = new LocalImportCodec((p, v, u) => p == "synthetic" && v == 1 && u == url);
        files.SetClaims(0, Encoding.UTF8.GetString(trusted.EncodeClaims(Claims(url: url))));
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Single((await reader.ListLocalCollectionsAsync(0, 1, CancellationToken.None)).Items);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.InvalidSelection);
        using var c = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        using var tx = c.BeginTransaction(deferred: true);
        var snapshot = ArchiveSql.ReadLocalCollection(c, tx, Collection, files.Profile, trusted, CancellationToken.None)!;
        Assert.Equal(url, snapshot.Find(Reference(0))!.Claims!.PublicSourceUrl);
        tx.Commit();
    }

    [Fact]
    public async Task NullAbsentAndPresentEmptyClaimsRemainDifferent()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 3);
        files.SetClaims(1, Encoding.UTF8.GetString(Codec.EncodeClaims(Claims(LocalFieldPresence.Absent))));
        files.SetClaims(2, Encoding.UTF8.GetString(Codec.EncodeClaims(Claims() with { SourceTags = ImmutableArray<string>.Empty })));
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        var snapshot = (await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None))!;
        Assert.Null(snapshot.Find(Reference(0))!.Claims);
        Assert.Equal(LocalFieldPresence.Absent, snapshot.Find(Reference(1))!.Claims!.TagsPresence);
        Assert.Equal(LocalFieldPresence.Present, snapshot.Find(Reference(2))!.Claims!.TagsPresence);
        Assert.Empty(snapshot.Find(Reference(2))!.Claims!.SourceTags);
    }

    [Fact]
    public async Task SlotByteBudgetIsEnforcedBeforeSemanticSlotValidation()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        files.Corrupt("UPDATE local_media_revisions SET payload_relative_path=$value",
            ("$value", new string('x', LocalImportBounds.MaximumRelativeSlotBytes + 1)));
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.BudgetExceeded);
    }

    [Fact]
    public async Task RawClaimsIndividualAndAggregateCapsPrecedeJsonDecode()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 17);
        string canonical = Encoding.UTF8.GetString(Codec.EncodeClaims(Claims()));
        string padded = canonical + new string(' ', LocalImportBounds.MaximumClaimsBytes - Encoding.UTF8.GetByteCount(canonical));
        for (int i = 0; i < 16; i++) files.SetClaims(i, padded);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Equal(17, (await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None))!.Count); // Exactly 16MiB raw.
        files.SetClaims(16, " "); // +1 aggregate, intentionally invalid JSON: budget must win first.
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.BudgetExceeded);
        files.SetClaims(16, null);
        files.SetClaims(0, padded + " ", ignoreChecks: true);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.BudgetExceeded);
    }

    [Theory]
    [InlineData("UPDATE local_items SET claims_json=CAST(X'80' AS TEXT)", (int)LocalImportError.InvalidSelection)]
    [InlineData("UPDATE local_items SET claims_json=CAST(claims_json AS BLOB)", (int)LocalImportError.IntegrityFailure)]
    public async Task ClaimsInvalidUtf8AndBlobStorageAreNotCoerced(string mutation, int error)
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        files.SetClaims(0, Encoding.UTF8.GetString(Codec.EncodeClaims(Claims())));
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        files.Corrupt(mutation);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), (LocalImportError)error);
    }

    [Theory]
    [InlineData("../payload.bin")]
    [InlineData("/absolute.bin")]
    [InlineData("local-import-operations/cccccccccccc4ccc8ccccccccccccccc/payloads/33333333333343338333000000000002.original.bin")]
    [InlineData("local-import-operations/CCCCCCCCCCCC4CCC8CCCCCCCCCCCCCCC/payloads/33333333333343338333000000000001.original.bin")]
    public async Task StoredSlotsMustMatchRatherThanBeRecomputed(string slot)
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1);
        files.Execute("UPDATE local_media_revisions SET payload_relative_path=$value", ("$value", slot));
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.InvalidPayloadBinding);
    }

    [Fact]
    public async Task MixedOperationRootsAndCrossItemRevisionIdentityAreRejectedByL0()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 2);
        files.Execute("UPDATE local_media_revisions SET payload_relative_path=$slot WHERE item_id=$item",
            ("$slot", LocalImportCodec.PayloadRelativeSlot(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"), Revision(1))),
            ("$item", Item(1).ToString()));
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.InvalidPayloadBinding);
        files.Execute("UPDATE local_media_revisions SET revision_id=$revision,payload_relative_path=$slot WHERE item_id=$item",
            ("$revision", Item(0).ToString()), ("$slot", LocalImportCodec.PayloadRelativeSlot(Operation, new(Item(0).Value))),
            ("$item", Item(1).ToString()));
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.InvalidSelection);
    }

    [Fact]
    public async Task CheckedTotalLengthCannotOverflow()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 2);
        files.Execute("UPDATE local_media_revisions SET length=9223372036854775807");
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        await RejectAsync(() => reader.ReadLocalCollectionAsync(Collection, CancellationToken.None), LocalImportError.BudgetExceeded);
    }

    [Fact]
    public async Task BothSqlHelpersUseCallerSnapshotWhileWriterRemainsUncommitted()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 1, "old");
        using var c = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        using var tx = c.BeginTransaction(deferred: true);
        var original = ArchiveSql.ReadLocalCollection(c, tx, Collection, files.Profile, Codec, CancellationToken.None)!;
        using var writer = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadWrite);
        using var write = writer.BeginTransaction(deferred: false);
        Workspace.SeedCollection(writer, write, Other, 1, "new");
        using (var update = ArchiveSql.Command(writer, write, "UPDATE local_collections SET title='changed' WHERE collection_id=$id"))
        { update.Parameters.AddWithValue("$id", Collection.ToString()); update.ExecuteNonQuery(); }
        var page = ArchiveSql.ReadLocalCollectionPage(c, tx, 0, 500, CancellationToken.None);
        Assert.Equal(1L, page.TotalCount); Assert.Equal("old", Assert.Single(page.Items).Title);
        var same = ArchiveSql.ReadLocalCollection(c, tx, Collection, files.Profile, Codec, CancellationToken.None)!;
        Assert.Equal("old", same.Summary.Title); Assert.Equal(original.ObservedCatalogGeneration, same.ObservedCatalogGeneration);
        Assert.Null(ArchiveSql.ReadLocalCollection(c, tx, Other, files.Profile, Codec, CancellationToken.None));
        tx.Commit(); // Release the read snapshot BEFORE writer commit, no rollback-journal deadlock.
        write.Commit();
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        Assert.Equal(2L, (await reader.ListLocalCollectionsAsync(0, 500, CancellationToken.None)).TotalCount);
        var fresh = (await reader.ReadLocalCollectionAsync(Collection, CancellationToken.None))!;
        Assert.Equal("changed", fresh.Summary.Title); Assert.True(fresh.ObservedCatalogGeneration > original.ObservedCatalogGeneration);
        Assert.Equal("old", original.Summary.Title);
    }

    [Fact]
    public async Task CancellationAtEntryAndDuringTrustedClaimsDecodeNeverReturnsAPartialSnapshot()
    {
        using Workspace files = new(); await files.CreateCurrentAsync(); files.SeedCollection(Collection, 2);
        await using var reader = await ArchiveReader.OpenAsync(files.Profile);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ListLocalCollectionsAsync(0, 1, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadLocalCollectionAsync(Collection, cancelled.Token));
        const string url = "https://example.invalid/inert";
        var writerCodec = new LocalImportCodec((_, _, _) => true);
        files.SetClaims(0, Encoding.UTF8.GetString(writerCodec.EncodeClaims(Claims(url: url))));
        using var during = new CancellationTokenSource();
        var cancelCodec = new LocalImportCodec((_, _, _) => { during.Cancel(); return true; });
        using var c = ArchiveSql.OpenConnection(files.Database, SqliteOpenMode.ReadOnly);
        using var tx = c.BeginTransaction(deferred: true);
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveSql.ReadLocalCollection(c, tx, Collection, files.Profile, cancelCodec, during.Token));
    }

    private static async Task RejectAsync<T>(Func<Task<T>> action, LocalImportError error)
    {
        var exception = await Assert.ThrowsAsync<LocalImportReadException>(async () => { _ = await action(); });
        Assert.Equal(error, exception.Error); Assert.Null(exception.InnerException);
        Assert.Equal("Local import read failed: " + error, exception.Message);
    }

    private sealed class Workspace : IDisposable
    {
        internal string Profile { get; } = Path.Combine(Path.GetTempPath(), "ochan-local-snapshot-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Profile, "archive.db");
        internal Workspace() => Directory.CreateDirectory(Profile);
        internal async Task CreateCurrentAsync()
        {
            using ProfileLease lease = ProfileLease.Acquire(Profile);
            await using (var catalog = await ArchiveCatalog.OpenAsync(Profile, lease)) { }
            Execute("UPDATE archive_import_fence SET instance=$value", ("$value", Instance));
        }
        internal void SeedCollection(LocalCollectionId id, int count, string title = "title", bool ignoreChecks = false)
        {
            using var c = ArchiveSql.OpenConnection(Database, SqliteOpenMode.ReadWrite);
            using var tx = c.BeginTransaction();
            if (ignoreChecks) { using var ignore = ArchiveSql.Command(c, tx, "PRAGMA ignore_check_constraints=ON"); ignore.ExecuteNonQuery(); }
            SeedCollection(c, tx, id, count, title);
            tx.Commit();
        }
        internal static void SeedCollection(SqliteConnection c, SqliteTransaction tx, LocalCollectionId id, int count, string title)
        {
            using (var header = ArchiveSql.Command(c, tx, "INSERT INTO local_collections VALUES($id,$title,1)"))
            { header.Parameters.AddWithValue("$id", id.ToString()); header.Parameters.AddWithValue("$title", title); header.ExecuteNonQuery(); }
            for (int i = 0; i < count; i++)
            {
                using var item = ArchiveSql.Command(c, tx, "INSERT INTO local_items VALUES($id,$item,$ordinal,NULL)");
                item.Parameters.AddWithValue("$id", id.ToString()); item.Parameters.AddWithValue("$item", Item(i).ToString());
                item.Parameters.AddWithValue("$ordinal", i); item.ExecuteNonQuery();
                using var revision = ArchiveSql.Command(c, tx, """
                    INSERT INTO local_media_revisions VALUES($id,$item,$revision,1,$hash,0,$slot,'猫.jpg')
                    """);
                revision.Parameters.AddWithValue("$id", id.ToString()); revision.Parameters.AddWithValue("$item", Item(i).ToString());
                revision.Parameters.AddWithValue("$revision", Revision(i).ToString()); revision.Parameters.AddWithValue("$hash", Hash);
                revision.Parameters.AddWithValue("$slot", LocalImportCodec.PayloadRelativeSlot(Operation, Revision(i))); revision.ExecuteNonQuery();
            }
        }
        internal void SetClaims(int index, string? text, bool ignoreChecks = false)
        {
            if (ignoreChecks) Corrupt("UPDATE local_items SET claims_json=$value WHERE item_id=$item", ("$value", text), ("$item", Item(index).ToString()));
            else Execute("UPDATE local_items SET claims_json=$value WHERE item_id=$item", ("$value", text), ("$item", Item(index).ToString()));
        }
        internal void SetDisplay(int index, string? text) => Execute("UPDATE local_media_revisions SET display_name=$value WHERE item_id=$item",
            ("$value", text), ("$item", Item(index).ToString()));
        internal void Execute(string sql, params (string Name, object? Value)[] parameters) => Mutate(sql, false, parameters);
        internal void Corrupt(string sql, params (string Name, object? Value)[] parameters) => Mutate(sql, true, parameters);
        private void Mutate(string sql, bool corrupt, (string Name, object? Value)[] parameters)
        {
            using var c = ArchiveSql.OpenConnection(Database, SqliteOpenMode.ReadWrite);
            if (corrupt) { using var unsafeFixture = c.CreateCommand(); unsafeFixture.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON;"; unsafeFixture.ExecuteNonQuery(); }
            using var tx = c.BeginTransaction();
            using var command = ArchiveSql.Command(c, tx, sql);
            foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Name, pair.Value ?? DBNull.Value);
            command.ExecuteNonQuery(); tx.Commit();
        }
        internal void SeedLegacy(int version)
        {
            using var c = ArchiveSql.OpenConnection(Database, SqliteOpenMode.ReadWriteCreate);
            using var fixture = typeof(LocalCollectionSnapshotTests).Assembly.GetManifestResourceStream("MeowChan.Tests.Fixtures.archive-schema-1.sql")!;
            using StreamReader text = new(fixture);
            using (var seed = ArchiveSql.Command(c, null, text.ReadToEnd())) seed.ExecuteNonQuery();
            using var tx = c.BeginTransaction();
            if (version >= 2) ArchiveSql.MigrateSearchSchema(c, tx, CancellationToken.None);
            if (version >= 3) ArchiveSql.CreateSourceContractSchema(c, tx);
            // Compatible synthetic v4/v5 superset, NOT an authentic historical release profile.
            if (version >= 4) ArchiveSql.CreateIntegritySchema(c, tx);
            if (version >= 6) ArchiveSql.CreateNativeImportSchema(c, tx);
            if (version == 9)
                Schema9MediaOperationsFixture.CreateSchema9(c, tx);
            else
            {
                if (version >= 7) ArchiveSql.CreateMediaImportSchema(c, tx);
                if (version >= 8) ArchiveSql.CreateMediaMergeSchema(c, tx);
            }
            if (version >= 9)
            {
                ArchiveSql.CreateLocalCollectionSchema(c, tx);
                using var instance = ArchiveSql.Command(c, tx, "UPDATE archive_import_fence SET instance=$value");
                instance.Parameters.AddWithValue("$value", Instance);
                instance.ExecuteNonQuery();
            }
            using var stamp = ArchiveSql.Command(c, tx, $"PRAGMA user_version={version}"); stamp.ExecuteNonQuery(); tx.Commit();
        }
        public void Dispose() => Directory.Delete(Profile, true);
    }
}
