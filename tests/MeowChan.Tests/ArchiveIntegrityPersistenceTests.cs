using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class ArchiveIntegrityPersistenceTests
{
    private static readonly ResourceId Id = new("source", "board", "thread");
    private static readonly string Md5 = Convert.ToBase64String(MD5.HashData([1, 2, 3]));

    [Fact]
    public async Task NewCapturesRetainRawValuesAndExactKeysAcrossOverviewAndRestart()
    {
        using TempDirectory temp = new();
        CapturedAttachment raw = new(0, "media", Url: "https://source.test/media", ExpectedSize: 999, ExpectedMd5: Md5)
            { ExpectedSha256 = new string('a', 64) };
        string expected = Key("post", raw);
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
            await catalog.CaptureAsync(Snapshot(temp.Path, new CapturedPost("post", 0, PostFieldPresence.Attachments, Attachments: [raw])));
        }
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path))
        {
            CapturedAttachment saved = Assert.Single(Assert.Single((await reader.ReadThreadAsync(Id))!.GetPosts()).Attachments!);
            Assert.Equal(raw with { AttachmentRevisionKey = expected }, saved);
            var preview = (await reader.ReadThreadPreviewsAsync([Id]))[Id];
            Assert.Equal("post", preview.FirstPostId); Assert.Equal(saved, preview.FirstAttachment);
        }
        using ProfileLease restartedLease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog restarted = await ArchiveCatalog.OpenAsync(temp.Path, restartedLease);
        await using ArchiveReader reopened = await ArchiveReader.OpenAsync(temp.Path);
        Assert.Equal(expected, Assert.Single(Assert.Single((await reopened.ReadThreadAsync(Id))!.GetPosts()).Attachments!).AttachmentRevisionKey);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "media")));
    }

    [Fact]
    public async Task ReplayedLegacySnapshotKeepsNullKeyUntilAnActualFreshObservation()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        CapturedAttachment legacy = new(0, "media", Url: "https://source.test/media", ExpectedSize: 3);
        CapturedThreadSnapshot replay = Snapshot(temp.Path, new CapturedPost("post", 0, PostFieldPresence.Attachments, Attachments: [legacy]));
        await catalog.CapturePreservingRevisionsAsync(replay);
        await catalog.CapturePreservingRevisionsAsync(replay);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.Null(Attachment(await reader.ReadThreadAsync(Id)).AttachmentRevisionKey);
        CapturedThreadSnapshot fresh = ArchiveCatalog.StampObservedAttachments(replay);
        Assert.Equal(Key("post", legacy), Assert.Single(Assert.Single(fresh.Posts).Attachments!).AttachmentRevisionKey);
        await catalog.CapturePreservingRevisionsAsync(fresh);
        Assert.Equal(Key("post", legacy), Attachment(await reader.ReadThreadAsync(Id)).AttachmentRevisionKey);
    }

    [Fact]
    public async Task MetadataRevisionChangesKeysWhileOmittedAndDeletedSlotsRetainTheirExactStand()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        CapturedAttachment a = new(0, "media", Url: "https://source.test/a", OriginalFileName: "old.bin", ExpectedSize: 3, ExpectedMd5: Md5);
        await catalog.CaptureAsync(Snapshot(temp.Path, new CapturedPost("post", 0, PostFieldPresence.Attachments, Attachments: [a])));
        await catalog.CaptureAsync(Snapshot(temp.Path, new CapturedPost("post", 0, PostFieldPresence.Html, Html: "updated text")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.Equal(Key("post", a), Attachment(await reader.ReadThreadAsync(Id)).AttachmentRevisionKey);
        CapturedAttachment b = a with { Url = "https://source.test/b", OriginalFileName = null, ExpectedSize = null,
            ExpectedMd5 = null, ExpectedSha256 = new string('b', 64) };
        await catalog.CaptureAsync(Snapshot(temp.Path, new CapturedPost("post", 0, PostFieldPresence.Attachments, Attachments: [b])));
        Assert.NotEqual(Key("post", a), Attachment(await reader.ReadThreadAsync(Id)).AttachmentRevisionKey);
        await catalog.CaptureAsync(Snapshot(temp.Path, new CapturedPost("post", 0, PostFieldPresence.Attachments,
            Attachments: [new(0, null, IsDeleted: true)])));
        CapturedAttachment deleted = Attachment(await reader.ReadThreadAsync(Id));
        Assert.True(deleted.IsDeleted); Assert.Equal(b with { IsDeleted = true, AttachmentRevisionKey = Key("post", b) }, deleted);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.CaptureAsync(Snapshot(temp.Path,
            new CapturedPost("post", 0, PostFieldPresence.Attachments, Attachments: [b with { AttachmentRevisionKey = Key("post", a) }]))));
    }

    [Fact]
    public async Task CandidateReferencesAndShaRoundTripWithoutChangingFirstIntentOrTarget()
    {
        using TempDirectory temp = new();
        string target = Path.Combine(temp.Path, "media", "original.bin");
        MediaProviderMetadata provider = new("media", "original", "https://source.test/media", "original.bin", 3, Md5, new string('c', 64));
        List<AttachmentRevisionReference> refs = [new(Id, "one", 0, MediaRevisionKeys.CreateAttachmentKey(Id, "one", 0, provider)),
            new(Id, "two", 1, MediaRevisionKeys.CreateAttachmentKey(Id, "two", 1, provider))];
        CandidateMetadata metadata = new(new(Id, "media"), new(provider.Url), target, provider.ExpectedSize, provider.ExpectedMd5, provider.OriginalFileName)
            { ExpectedSha256 = provider.ExpectedSha256, AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Id, provider), AttachmentReferences = refs };
        CandidateDecision first;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
            MediaRuleStore rules = new(temp.Path, lease); await rules.LoadAsync();
            await catalog.RegisterThreadAsync(new(Id, "https://source.test/thread", "title", Path.GetDirectoryName(target)!, false));
            first = await catalog.GetOrCreateDecisionAsync(metadata, rules);
            refs.Clear();
            Assert.Equal(2, first.Metadata.AttachmentReferences.Count);
        }
        using ProfileLease reopenedLease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog reopened = await ArchiveCatalog.OpenAsync(temp.Path, reopenedLease);
        CandidateDecision saved = Assert.Single(await reopened.GetDecisionsAsync(Id));
        Assert.Equal(first.DecidedAtUtc, saved.DecidedAtUtc); Assert.Equal(target, saved.Metadata.DestinationPath);
        Assert.Equal(provider.ExpectedSha256, saved.Metadata.ExpectedSha256);
        Assert.Equal(first.Metadata.AcquisitionRevisionKey, saved.Metadata.AcquisitionRevisionKey);
        Assert.Equal(first.Metadata.AttachmentReferences, saved.Metadata.AttachmentReferences);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.Equal(2, Assert.Single((await reader.ReadExportAsync(Id, default))!.Decisions).Metadata.AttachmentReferences.Count);
        MediaProviderMetadata next = provider with { Url = provider.Url + "?revision=2", ExpectedSha256 = new string('d', 64) };
        CandidateMetadata changed = new(new(Id, "media"), new(next.Url), Path.Combine(temp.Path, "other.bin"),
            next.ExpectedSize, next.ExpectedMd5, next.OriginalFileName)
            { ExpectedSha256 = next.ExpectedSha256, AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Id, next),
                AttachmentReferences = [new(Id, "new-post", 0, MediaRevisionKeys.CreateAttachmentKey(Id, "new-post", 0, next))] };
        MediaRuleStore restartedRules = new(temp.Path, reopenedLease); await restartedRules.LoadAsync();
        CandidateDecision updated = await reopened.GetOrCreateDecisionAsync(changed, restartedRules);
        Assert.Equal(first.DecidedAtUtc, updated.DecidedAtUtc); Assert.Equal(target, updated.Metadata.DestinationPath);
        Assert.Equal(next.ExpectedSha256, updated.Metadata.ExpectedSha256); Assert.Equal(changed.AcquisitionRevisionKey, updated.Metadata.AcquisitionRevisionKey);
        Assert.Equal("new-post", Assert.Single(updated.Metadata.AttachmentReferences).PostId);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.GetOrCreateDecisionAsync(
            changed with { AcquisitionRevisionKey = saved.Metadata.AcquisitionRevisionKey }, restartedRules));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task LegacyCatalogReadsWithoutWritesAndMigratesToFourWithoutInventingBindings(int version)
    {
        using TempDirectory temp = new();
        CreateLegacy(temp.Path);
        if (version > 1)
        {
            using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
            { await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease); }
            Execute(temp.Path, "ALTER TABLE archive_attachments DROP COLUMN expected_sha256; ALTER TABLE archive_attachments DROP COLUMN attachment_revision_key; "
                + "ALTER TABLE media_decisions DROP COLUMN expected_sha256; ALTER TABLE media_decisions DROP COLUMN acquisition_revision_key; "
                + "ALTER TABLE media_decisions DROP COLUMN attachment_references_json; "
                + (version == 2 ? "DROP TABLE archive_source_contracts; " : "") + "PRAGMA user_version=" + version);
        }
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(temp.Path, "archive.db"));
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path))
        {
            CapturedAttachment old = Attachment(await reader.ReadThreadAsync(Id));
            Assert.Null(old.AttachmentRevisionKey); Assert.Null(old.ExpectedSha256); Assert.Equal(3L, old.ExpectedSize);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(temp.Path, "archive.db")));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using ProfileLease lease = ProfileLease.Acquire(temp.Path);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
            Assert.Null(Attachment(await reader.ReadThreadAsync(Id)).AttachmentRevisionKey);
        }
        Assert.Equal(4L, Scalar(temp.Path, "PRAGMA user_version"));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "disconnected")));
    }

    [Fact]
    public async Task FailedMigrationRollsBackEveryNewColumnAndKeepsLegacyReadable()
    {
        using TempDirectory temp = new(); CreateLegacy(temp.Path);
        Execute(temp.Path, "CREATE TABLE archive_quotes(occupied TEXT)");
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(temp.Path, "archive.db"));
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await Assert.ThrowsAsync<SqliteException>(() => ArchiveCatalog.OpenAsync(temp.Path, lease));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(temp.Path, "archive.db")));
        Assert.Equal(1L, Scalar(temp.Path, "PRAGMA user_version"));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        Assert.Null(Attachment(await reader.ReadThreadAsync(Id)).AttachmentRevisionKey);
    }

    private static CapturedAttachment Attachment(ArchivedThreadView? view) => Assert.Single(Assert.Single(view!.GetPosts()).Attachments!);
    private static string Key(string post, CapturedAttachment attachment) => MediaRevisionKeys.CreateAttachmentKey(Id, post, attachment.Position,
        new(attachment.MediaId!, attachment.Variant, attachment.Url!, attachment.OriginalFileName, attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256));
    private static CapturedThreadSnapshot Snapshot(string profile, params CapturedPost[] posts) => new(Id, "https://source.test/thread", "title",
        ThreadSourceState.Archived, DateTimeOffset.UtcNow, Path.Combine(profile, "media"), posts);
    private static SqliteConnection Open(string profile)
    {
        SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(profile, "archive.db"), Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static void Execute(string profile, string sql)
    { using SqliteConnection db = Open(profile); using SqliteCommand command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static object? Scalar(string profile, string sql)
    { using SqliteConnection db = Open(profile); using SqliteCommand command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static void CreateLegacy(string profile)
    {
        string name = typeof(ArchiveIntegrityPersistenceTests).Assembly.GetManifestResourceNames().Single(value => value.EndsWith("archive-schema-1.sql", StringComparison.Ordinal));
        using Stream stream = typeof(ArchiveIntegrityPersistenceTests).Assembly.GetManifestResourceStream(name)!;
        using StreamReader sql = new(stream); Execute(profile, sql.ReadToEnd());
        using SqliteConnection db = Open(profile); using SqliteCommand insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO archive_threads VALUES('source','board','thread','https://source.test/thread','legacy',$dir,1,0,$date,$date,1); "
            + "INSERT INTO archive_posts VALUES('source','board','thread','post',0,32,NULL,NULL,NULL,NULL,0); "
            + "INSERT INTO archive_attachments VALUES('source','board','thread','post',0,'media','original','https://source.test/media',NULL,NULL,3,NULL,0); "
            + "INSERT INTO archive_media_directories VALUES('source','board','thread',$dir)";
        insert.Parameters.AddWithValue("$dir", Path.Combine(profile, "disconnected")); insert.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToString("O")); insert.ExecuteNonQuery();
    }
}
