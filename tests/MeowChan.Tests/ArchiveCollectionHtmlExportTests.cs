using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class ArchiveCollectionHtmlExportTests
{
    private static readonly ResourceId First = new("4chan", "g", "123");
    private static readonly ResourceId Second = new("4chan", "a", "400");
    private static readonly byte[] Bytes = [1, 4, 9, 16];
    private static readonly string Md5 = Convert.ToBase64String(MD5.HashData(Bytes));

    [Fact]
    public async Task PortableCollectionHasFrozenDistinctIdentityPagesLocalQuotesAndVerifiedPooledMedia()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        string media1 = await SaveAsync(Path.Combine(dir.Path, "one"));
        string media2 = await SaveAsync(Path.Combine(dir.Path, "two"));
        ResourceId otherSource = First with { Source = "other" };
        await catalog.CaptureAsync(Snapshot(First, media1, "<a href='/a/thread/400#p400'>&gt;&gt;400</a><a href='https://external.test/#p9'>&gt;&gt;9</a><script>secret</script>"));
        await catalog.CaptureAsync(Snapshot(Second, media2, "Second text"));
        await catalog.CaptureAsync(Snapshot(otherSource, media1, "Other source"));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveCollectionHtmlExportResult result = await ArchiveCollectionHtmlExporter.ExportAsync(reader,
            [First, Second, First, otherSource], Path.Combine(dir.Path, "export"));
        Assert.Equal(3, result.ThreadCount);
        Assert.Equal(3, result.PostCount);
        Assert.Equal(1, result.FileCount);
        Assert.Empty(result.Omissions);
        Assert.Equal(new[] { First, Second, otherSource }, result.Metadata!.Members.Select(member => member.Thread));
        Assert.Equal(3, result.Metadata.Members.Select(member => member.PagePath).Distinct().Count());
        string firstPage = await File.ReadAllTextAsync(Path.Combine(result.Directory, result.Metadata.Members[0].PagePath));
        Assert.Contains("href=\"" + Path.GetFileName(result.Metadata.Members[1].PagePath) + "#p-343030\"", firstPage);
        Assert.Contains("href=\"../style.css\"", firstPage);
        Assert.Contains("href=\"../index.html\"", firstPage);
        Assert.Contains("href=\"../media/", firstPage);
        Assert.Contains("External reference", firstPage);
        Assert.DoesNotContain("<script", firstPage);
        Assert.DoesNotContain("https://", firstPage);
        Assert.DoesNotContain("secret", firstPage);
        Assert.DoesNotContain(dir.Path, firstPage);
        string index = await File.ReadAllTextAsync(result.IndexPath);
        Assert.Contains(result.Metadata.Members[0].PagePath + "#p-313233", index);
        Assert.Contains("Last observed capture:", index);
        string moved = Path.Combine(dir.Path, "moved collection");
        Directory.Move(result.Directory, moved);
        Directory.Delete(media1, true);
        Directory.Delete(media2, true);
        ArchiveCollectionExportInspection inspection = await ArchiveCollectionHtmlExporter.InspectAsync(moved, [First, Second, otherSource]);
        Assert.False(inspection.RequiresBackup);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(moved, "media")))));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveHtmlExporter.InspectAsync(moved, First));
    }

    [Fact]
    public async Task PooledReuseStillVerifiesEveryOriginAndKeepsPerThreadOmissions()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(dir.Path, lease);
        string one = await SaveAsync(Path.Combine(dir.Path, "one"));
        string two = await SaveAsync(Path.Combine(dir.Path, "two"));
        await catalog.CaptureAsync(Snapshot(First, one));
        await catalog.CaptureAsync(Snapshot(Second, two));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(dir.Path);
        bool corrupted = false;
        ArchiveCollectionHtmlExportResult export = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First, Second],
            Path.Combine(dir.Path, "export"), progress: new InlineProgress(value =>
            {
                if (corrupted || value.ThreadsCompleted != 1) return;
                corrupted = true;
                File.WriteAllBytes(Path.Combine(two, "456.bin"), [0, 0, 0, 0]);
            }));
        ArchiveCollectionExportOmission omission = Assert.Single(export.Omissions);
        Assert.Equal(Second, omission.Thread);
        Assert.Equal(ArchiveExportOmissionReason.VerificationFailed, omission.Reason);
        Assert.Equal(1, export.FileCount);
        string secondPage = await File.ReadAllTextAsync(Path.Combine(export.Directory, export.Metadata!.Members[1].PagePath));
        Assert.DoesNotContain("href=\"../media/", secondPage);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(export.Directory, "media")))));
    }

    [Fact]
    public async Task SnapshotsAreFrozenBeforeWritingAndMissingMembersNeverPublish()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(dir.Path, lease);
        await catalog.CaptureAsync(Snapshot(First, dir.Path));
        await catalog.CaptureAsync(Snapshot(Second, dir.Path, "old"));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(dir.Path);
        bool changed = false;
        ArchiveCollectionHtmlExportResult export = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First, Second],
            Path.Combine(dir.Path, "export"), new(false), new InlineProgress(_ =>
            {
                if (changed) return;
                changed = true;
                catalog.CaptureAsync(Snapshot(Second, dir.Path, "new content") with { Posts = [new CapturedPost("401", 1, PostFieldPresence.All, Html: "new content")] }).GetAwaiter().GetResult();
            }));
        Assert.Equal(2, export.PostCount);
        Assert.DoesNotContain("new content", await File.ReadAllTextAsync(Path.Combine(export.Directory, export.Metadata!.Members[1].PagePath)));
        Assert.All(export.Omissions, item => Assert.Equal(ArchiveExportOmissionReason.MediaNotIncluded, item.Reason));
        string missing = Path.Combine(dir.Path, "missing");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ArchiveCollectionHtmlExporter.ExportAsync(reader,
            [First, Second with { Thread = "999" }], missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task CancellationAndDestinationCollisionLeaveNoPartialCollection()
    {
        using TempDirectory dir = new();
        using ProfileLease lease = ProfileLease.Acquire(dir.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(dir.Path, lease);
        await catalog.CaptureAsync(Snapshot(First, dir.Path));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(dir.Path);
        using CancellationTokenSource cancel = new();
        string cancelled = Path.Combine(dir.Path, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], cancelled,
            progress: new InlineProgress(_ => cancel.Cancel()), cancellationToken: cancel.Token));
        Assert.False(Directory.Exists(cancelled));
        string collision = Path.Combine(dir.Path, "collision");
        await Assert.ThrowsAsync<IOException>(() => ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], collision,
            progress: new InlineProgress(_ => { Directory.CreateDirectory(collision); File.WriteAllText(Path.Combine(collision, "notes"), "KEEP"); })));
        Assert.Equal("KEEP", await File.ReadAllTextAsync(Path.Combine(collision, "notes")));
        Assert.Empty(Directory.GetDirectories(dir.Path, ".meowchan-collection-stage-*"));
    }

    [Fact]
    public async Task ExplicitUpdatesAndCollectionHistoryValidateMembershipIdentityAndFingerprint()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await catalog.CaptureAsync(Snapshot(Second, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        ArchiveCollectionHtmlExportResult first = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First, Second], destination, new(false));
        CollectionExportHistoryStore history = new(profile, lease);
        Assert.True((await history.RecordPublishedAsync(first)).HistoryRecorded);
        Assert.Single(await new CollectionExportHistoryStore(profile, lease).ListVerifiedAsync([First, Second]));
        Assert.Empty(history.List([Second, First]));
        ArchiveCollectionExportInspection selected = await ArchiveCollectionHtmlExporter.InspectAsync(destination, [First, Second]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCollectionHtmlExporter.InspectAsync(destination, [Second, First]));
        await catalog.CaptureAsync(Snapshot(First, profile) with { Posts = [new CapturedPost("124", 1, PostFieldPresence.All, Html: "later")] });
        ArchiveCollectionHtmlExportResult updated = await ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First, Second], destination,
            selected.Metadata.ExportId, 1, selected.ObservedFingerprint, new(false));
        Assert.Equal(2, updated.Metadata!.Generation);
        Assert.Null(updated.BackupDirectory);
        Assert.Equal(first.Metadata!.ExportId, updated.Metadata.ExportId);
        Assert.True((await history.RecordPublishedAsync(updated)).HistoryRecorded);
        Assert.Equal(2, Assert.Single(history.List([First, Second])).Generation);
        await Assert.ThrowsAsync<IOException>(() => ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First, Second], destination,
            selected.Metadata.ExportId, 1, selected.ObservedFingerprint));
        ArchiveCollectionExportInspection current = await ArchiveCollectionHtmlExporter.InspectAsync(destination);
        await File.AppendAllTextAsync(updated.IndexPath, "USER EDIT");
        await Assert.ThrowsAsync<IOException>(() => ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First, Second], destination,
            current.Metadata.ExportId, 2, current.ObservedFingerprint, confirmPreserveBackup: true));
        ArchiveCollectionExportInspection edited = await ArchiveCollectionHtmlExporter.InspectAsync(destination);
        await Assert.ThrowsAsync<IOException>(() => ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First, Second], destination,
            edited.Metadata.ExportId, 2, edited.ObservedFingerprint));
        ArchiveCollectionHtmlExportResult kept = await ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First, Second], destination,
            edited.Metadata.ExportId, 2, edited.ObservedFingerprint, new(false), confirmPreserveBackup: true);
        Assert.Contains("USER EDIT", await File.ReadAllTextAsync(Path.Combine(kept.BackupDirectory!, "index.html")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRenameFailuresRestoreThePreviousCollection(bool afterNew)
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        ArchiveCollectionHtmlExportResult first = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination, new(false));
        ArchiveCollectionExportInspection selected = await ArchiveCollectionHtmlExporter.InspectAsync(destination);
        Action fail = () => throw new IOException("injected rename failure");
        if (afterNew) ArchiveCollectionExportFaults.AfterNewRename = fail;
        else ArchiveCollectionExportFaults.AfterOldRename = fail;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], destination,
                selected.Metadata.ExportId, 1, selected.ObservedFingerprint, new(false)));
        }
        finally { ArchiveCollectionExportFaults.AfterOldRename = null; ArchiveCollectionExportFaults.AfterNewRename = null; }
        Assert.Equal(1, (await ArchiveCollectionHtmlExporter.InspectAsync(destination)).Metadata.Generation);
        Assert.Empty(Directory.GetDirectories(dir.Path, ".meowchan-collection-stage-*"));
    }

    [Fact]
    public async Task RecoveryAfterOldRenameRestoresCollectionBeforeExplicitUpdate()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        ArchiveCollectionHtmlExportResult first = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination, new(false));
        ArchiveCollectionExportInspection selected = await ArchiveCollectionHtmlExporter.InspectAsync(destination);
        string backup = ".meowchan-collection-export-backup-" + Guid.NewGuid().ToString("N");
        string journal = Path.Combine(dir.Path, ".meowchan-collection-export-journal-" + ArchiveExportTargetLease.Key(destination) + ".json");
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new ArchiveCollectionExportJournal(backup, selected.Metadata.ExportId, 2, false)));
        Directory.Move(destination, Path.Combine(dir.Path, backup));
        ArchiveCollectionHtmlExportResult updated = await ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], destination,
            selected.Metadata.ExportId, 1, selected.ObservedFingerprint, new(false));
        Assert.Equal(2, updated.Metadata!.Generation);
        Assert.False(File.Exists(journal));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, backup)));
    }

    [Fact]
    public async Task HistoryLoadRestoresMatchingJournaledCollectionFallback()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        ArchiveCollectionHtmlExportResult first = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination, new(false));
        CollectionExportHistoryStore history = new(profile, lease);
        Assert.True((await history.RecordPublishedAsync(first)).HistoryRecorded);
        ArchiveCollectionExportInspection before = await ArchiveCollectionHtmlExporter.InspectAsync(destination, [First]);
        string backupName = ".meowchan-collection-export-backup-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(dir.Path, backupName);
        string journal = Path.Combine(dir.Path, ".meowchan-collection-export-journal-" + ArchiveExportTargetLease.Key(destination) + ".json");
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new ArchiveCollectionExportJournal(
            backupName, first.Metadata!.ExportId, 2, false, before.ObservedFingerprint)));
        Directory.Move(destination, backup);

        (CollectionExportHistoryEntry _, ArchiveCollectionExportInspection? inspection) = Assert.Single(await history.ListVerifiedAsync([First]));
        Assert.NotNull(inspection);
        Assert.Equal(1, inspection.Metadata.Generation);
        Assert.True(Directory.Exists(destination));
        Assert.False(Directory.Exists(backup));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task LessCompleteUpdateRequiresAndRetainsBackupAndHistoryFailureKeepsPublishedResult()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        string media = await SaveAsync(dir.Path);
        await catalog.CaptureAsync(Snapshot(First, media));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        ArchiveCollectionHtmlExportResult first = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination);
        ArchiveCollectionExportInspection selected = await ArchiveCollectionHtmlExporter.InspectAsync(destination);
        await Assert.ThrowsAsync<IOException>(() => ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], destination,
            selected.Metadata.ExportId, 1, selected.ObservedFingerprint, new(false)));
        ArchiveCollectionHtmlExportResult updated = await ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], destination,
            selected.Metadata.ExportId, 1, selected.ObservedFingerprint, new(false), confirmPreserveBackup: true);
        Assert.Equal(0, updated.FileCount);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(updated.BackupDirectory!, "media")))));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(media, "456.bin")));
        await File.WriteAllTextAsync(Path.Combine(profile, "collection-export-history.json"), "not json");
        ArchiveCollectionExportOperationResult recorded = await new CollectionExportHistoryStore(profile, lease).RecordPublishedAsync(updated);
        Assert.False(recorded.HistoryRecorded);
        Assert.NotNull(recorded.HistoryError);
        Assert.Equal(2, (await ArchiveCollectionHtmlExporter.InspectAsync(destination)).Metadata.Generation);
    }

    [Fact]
    public async Task CommitRechecksFingerprintAndExtraFileBytesEvenWithRestoredTimestamp()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination, new(false));
        string note = Path.Combine(destination, "personal-note.txt");
        await File.WriteAllTextAsync(note, "KEEP");
        DateTime modified = File.GetLastWriteTimeUtc(note);
        ArchiveCollectionExportInspection selected = await ArchiveCollectionHtmlExporter.InspectAsync(destination);
        bool edited = false;
        await Assert.ThrowsAsync<IOException>(() => ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], destination,
            selected.Metadata.ExportId, 1, selected.ObservedFingerprint, new(false), confirmPreserveBackup: true,
            progress: new InlineProgress(_ =>
            {
                if (edited) return;
                edited = true;
                File.WriteAllText(note, "EDIT");
                File.SetLastWriteTimeUtc(note, modified);
            })));
        Assert.Equal("EDIT", await File.ReadAllTextAsync(note));
        Assert.Equal(1, (await ArchiveCollectionHtmlExporter.InspectAsync(destination)).Metadata.Generation);
    }

    [Fact]
    public async Task UnsafeMarkersAndSymbolicMemberPagesCannotBeSelectedOrEscapeOutput()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        ArchiveCollectionHtmlExportResult export = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination, new(false));
        string marker = Path.Combine(destination, ArchiveCollectionExportMetadataFile.Name);
        string original = await File.ReadAllTextAsync(marker);
        ArchiveCollectionExportMetadata malicious = export.Metadata! with
        { Files = export.Metadata!.Files.Select(file => file.Path == "index.html" ? file with { Path = "../outside" } : file).ToArray() };
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(malicious));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCollectionHtmlExporter.InspectAsync(destination));
        malicious = export.Metadata! with
        { Files = export.Metadata!.Files.Select(file => file with { Sha256 = null! }).ToArray() };
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(malicious));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCollectionHtmlExporter.InspectAsync(destination));
        await File.WriteAllTextAsync(marker, original);
        string page = Path.Combine(destination, export.Metadata!.Members[0].PagePath);
        string outside = Path.Combine(dir.Path, "outside.html");
        File.Move(page, outside);
        try { File.CreateSymbolicLink(page, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { return; }
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCollectionHtmlExporter.InspectAsync(destination));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task MediaOnlyMemberUsesSharedGalleryAndCleanupFailureReportsPublishedSuccess()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        string media = await SaveAsync(dir.Path);
        await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(First, "https://boards.4chan.org/g/thread/123", "Media only", media, false));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        ArchiveCollectionHtmlExportResult export = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination);
        Assert.Equal(0, export.PostCount);
        Assert.Equal(1, export.FileCount);
        Assert.Contains("No post text was captured", await File.ReadAllTextAsync(Path.Combine(destination, export.Metadata!.Members[0].PagePath)));
        ArchiveCollectionExportInspection selected = await ArchiveCollectionHtmlExporter.InspectAsync(destination);
        ArchiveCollectionExportFaults.BeforeBackupCleanup = () => throw new IOException("injected cleanup failure");
        ArchiveCollectionHtmlExportResult updated;
        try
        {
            updated = await ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], destination,
                selected.Metadata.ExportId, 1, selected.ObservedFingerprint);
        }
        finally { ArchiveCollectionExportFaults.BeforeBackupCleanup = null; }
        Assert.Equal(2, updated.Metadata!.Generation);
        Assert.NotNull(updated.BackupDirectory);
        Assert.Contains(updated.Warnings, warning => warning.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.False((await ArchiveCollectionHtmlExporter.InspectAsync(destination)).RequiresBackup);
    }

    [Fact]
    public async Task ConcurrentUpdatesOnlyPublishOneGenerationAndProfileChildrenRemainProtected()
    {
        using TempDirectory dir = new();
        string profile = Path.Combine(dir.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(dir.Path, "export");
        await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination, new(false));
        ArchiveCollectionExportInspection selected = await ArchiveCollectionHtmlExporter.InspectAsync(destination);
        Task<ArchiveCollectionHtmlExportResult> one = ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], destination,
            selected.Metadata.ExportId, 1, selected.ObservedFingerprint, new(false));
        Task<ArchiveCollectionHtmlExportResult> two = ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], destination,
            selected.Metadata.ExportId, 1, selected.ObservedFingerprint, new(false));
        await Task.WhenAll(one.ContinueWith(_ => { }), two.ContinueWith(_ => { }));
        _ = Assert.Single(new[] { one, two }, task => task.IsCompletedSuccessfully);
        _ = Assert.Single(new[] { one, two }, task => task.IsFaulted);
        Assert.Equal(2, (await ArchiveCollectionHtmlExporter.InspectAsync(destination)).Metadata.Generation);
        string inside = Path.Combine(profile, "prior-export");
        await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], inside, new(false));
        ArchiveCollectionExportInspection child = await ArchiveCollectionHtmlExporter.InspectAsync(inside);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCollectionHtmlExporter.UpdateAsync(reader, [First], inside,
            child.Metadata.ExportId, 1, child.ObservedFingerprint, new(false)));
        Assert.Equal(1, (await ArchiveCollectionHtmlExporter.InspectAsync(inside)).Metadata.Generation);
    }

    [Theory]
    [InlineData("modified-html")]
    [InlineData("missing-css")]
    [InlineData("extra-file")]
    public async Task RecoveryNeverDeletesCleanFallbackWhenPublishedCollectionHasChanged(string change)
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "export");
        ArchiveCollectionHtmlExportResult first = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], destination, new(false));
        ArchiveCollectionExportInspection before = await ArchiveCollectionHtmlExporter.InspectAsync(destination, [First]);
        string candidate = Path.Combine(directory.Path, "candidate");
        ArchiveCollectionHtmlExportResult next = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First], candidate, new(false));
        await File.WriteAllTextAsync(Path.Combine(candidate, ArchiveCollectionExportMetadataFile.Name),
            JsonSerializer.Serialize(next.Metadata! with { ExportId = first.Metadata!.ExportId, Generation = 2 }));
        string backupName = ".meowchan-collection-export-backup-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(directory.Path, backupName);
        string journal = Path.Combine(directory.Path, ".meowchan-collection-export-journal-" + ArchiveExportTargetLease.Key(destination) + ".json");
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new ArchiveCollectionExportJournal(
            backupName, first.Metadata.ExportId, 2, false, before.ObservedFingerprint)));
        Directory.Move(destination, backup);
        Directory.Move(candidate, destination);
        if (change == "modified-html") await File.AppendAllTextAsync(Path.Combine(destination, "index.html"), "USER EDIT");
        else if (change == "missing-css") File.Delete(Path.Combine(destination, "style.css"));
        else await File.WriteAllTextAsync(Path.Combine(destination, "personal-note.txt"), "KEEP");
        ArchiveCollectionExportInspection damaged = await ArchiveCollectionHtmlExporter.InspectAsync(destination, [First]);
        Assert.True(damaged.RequiresBackup);

        IOException failure = Assert.Throws<IOException>(() => ArchiveCollectionExportReplacement.Recover(destination));

        Assert.Contains("manual review", failure.Message);
        Assert.Contains(backup, failure.Message);
        Assert.True(File.Exists(journal));
        Assert.Equal(before.ObservedFingerprint, (await ArchiveCollectionHtmlExporter.InspectAsync(backup, [First])).ObservedFingerprint);
        Assert.Equal(damaged.ObservedFingerprint, (await ArchiveCollectionHtmlExporter.InspectAsync(destination, [First])).ObservedFingerprint);
    }

    private sealed class InlineProgress(Action<ArchiveCollectionHtmlExportProgress> report) : IProgress<ArchiveCollectionHtmlExportProgress>
    { public void Report(ArchiveCollectionHtmlExportProgress value) => report(value); }
    private static CapturedThreadSnapshot Snapshot(ResourceId id, string media, string text = "captured") => new(id,
        "https://boards.4chan.org/" + id.Board + "/thread/" + id.Thread, "Title <" + id.Thread + ">", ThreadSourceState.Archived,
        DateTimeOffset.UtcNow, media, [new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Html: text,
            Attachments: [new CapturedAttachment(0, "456", OriginalFileName: "../file.bin", ExpectedSize: Bytes.Length, ExpectedMd5: Md5)])]);
    private static async Task<string> SaveAsync(string root)
    {
        using StubImageBoard board = new();
        DownloadItem item = new(new Uri("https://stub.test/456.bin"), Path.Combine(root, "media", "456.bin"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/123")), "456", expectedSize: Bytes.Length, expectedMd5: Md5);
        using MemoryStream input = new(Bytes, false);
        MediaCommitResult result = await new MediaStore().CommitAsync(item, input, CancellationToken.None);
        return Path.GetDirectoryName(result.Path)!;
    }
}
