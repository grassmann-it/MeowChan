using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class ArchiveHtmlExportTests
{
    private static readonly ResourceId Id = new("4chan", "g", "123");
    private static readonly byte[] Bytes = [1, 4, 9, 16];
    private static readonly string Md5 = Convert.ToBase64String(MD5.HashData(Bytes));

    [Fact]
    public async Task ExportIsIndependentAndInertWithMatchingMediaLocalQuotesAndVisibleOmissions()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        string destination = Path.Combine(directory.Path, "export");
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        Assert.Equal(2, result.PostCount);
        Assert.Equal(1, result.FileCount);
        Assert.Collection(result.Omissions, issue => Assert.Equal(ArchiveExportOmissionReason.Unavailable, issue.Reason),
            issue => Assert.Equal(ArchiveExportOmissionReason.DifferentVersion, issue.Reason));
        string moved = Path.Combine(directory.Path, "moved folder");
        Directory.Move(destination, moved);
        Directory.Delete(media, true);
        string html = await File.ReadAllTextAsync(Path.Combine(moved, "index.html"));
        Assert.Contains("href=\"#p-313234\"", html);
        Assert.Contains("Post not captured", html);
        Assert.Contains("Other thread, outside this export", html);
        Assert.Contains("External reference", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("secret-script", html);
        Assert.DoesNotContain(directory.Path, html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("Removed at source", html);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(moved, "media")))));
        Assert.True(File.Exists(Path.Combine(moved, "style.css")));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
    }

    [Fact]
    public async Task PreservedRulesAndTextOnlyChoiceExplainIntentWithoutReevaluation()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        MediaRuleStore rules = new(directory.Path, lease);
        await rules.LoadAsync();
        await rules.SaveAsync(new[] { new MediaRule(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected)) });
        await catalog.GetOrCreateDecisionAsync(new CandidateMetadata(new CandidateKey(Id, "456"),
            new Uri("https://stub.test/456.bin"), Path.Combine(media, "456.bin"), Bytes.Length, Md5, extension: ".bin"), rules);
        await rules.SaveAsync(Array.Empty<MediaRule>());
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveHtmlExportResult excluded = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "excluded"));
        Assert.Equal(0, excluded.FileCount);
        Assert.Equal(2, excluded.Omissions.Count(issue => issue.Reason == ArchiveExportOmissionReason.ExcludedByRule));
        ArchiveHtmlExportResult text = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "text"), new(false));
        Assert.All(text.Omissions, issue => Assert.Equal(ArchiveExportOmissionReason.MediaNotIncluded, issue.Reason));
        Assert.Equal(0, text.FileCount);
    }

    [Fact]
    public async Task CorruptSavedBytesAreOmittedWithoutRepairOrFalseMediaLink()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        string file = Path.Combine(media, "456.bin");
        await File.WriteAllBytesAsync(file, new byte[] { 0, 0, 0, 0 });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "export"));
        Assert.Equal(0, result.FileCount);
        Assert.Contains(result.Omissions, issue => issue.Reason == ArchiveExportOmissionReason.VerificationFailed);
        Assert.DoesNotContain("href=\"media/", await File.ReadAllTextAsync(result.IndexPath));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task CancellationAfterStagingAndPublicationCollisionLeaveExistingExportsIntact()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        string existing = Path.Combine(directory.Path, "existing");
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "index.html"), "KEEP");
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.ExportAsync(reader, Id, existing));
        using CancellationTokenSource cancellation = new();
        string cancelled = Path.Combine(directory.Path, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchiveHtmlExporter.ExportAsync(reader, Id, cancelled,
            progress: new InlineProgress(_ => cancellation.Cancel()), cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(cancelled));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
        string collision = Path.Combine(directory.Path, "collision");
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.ExportAsync(reader, Id, collision,
            progress: new InlineProgress(_ => { Directory.CreateDirectory(collision); File.WriteAllText(Path.Combine(collision, "index.html"), "KEEP2"); })));
        Assert.Equal("KEEP", await File.ReadAllTextAsync(Path.Combine(existing, "index.html")));
        Assert.Equal("KEEP2", await File.ReadAllTextAsync(Path.Combine(collision, "index.html")));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
    }

    [Fact]
    public async Task MediaOnlyThreadExportsCurrentGalleryWithoutInventingPosts()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(Id, "https://boards.4chan.org/g/thread/123", "Media only", media, false));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "gallery"));
        Assert.Equal(0, result.PostCount);
        Assert.Equal(1, result.FileCount);
        Assert.Contains("No post text was captured", await File.ReadAllTextAsync(result.IndexPath));
    }

    [Fact]
    public async Task ExportUsesMatchingRetainedRevisionAndImmutableCaptureDespiteLaterChanges()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        await SaveAsync(directory.Path, new byte[] { 8, 8, 8, 8 });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        bool changed = false;
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "revision"),
            progress: new InlineProgress(_ =>
            {
                if (changed) return;
                changed = true;
                catalog.CaptureAsync(Snapshot(media) with { Posts = new[] { new CapturedPost("125", 2, PostFieldPresence.All, Html: "new capture") } })
                    .GetAwaiter().GetResult();
            }));
        Assert.Equal(2, result.PostCount);
        Assert.Equal(3, (await reader.ReadThreadAsync(Id))!.PostCount);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(result.Directory, "media")))));
        Assert.DoesNotContain("new capture", await File.ReadAllTextAsync(result.IndexPath));
    }

    [Fact]
    public async Task OutputWriteFailureRemovesStagingAndNeverPublishesSuccess()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        string output = Path.Combine(directory.Path, "failed");
        Exception? failure = await Record.ExceptionAsync(() => ArchiveHtmlExporter.ExportAsync(reader, Id, output,
            progress: new InlineProgress(_ =>
            {
                string stage = Assert.Single(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
                Directory.CreateDirectory(Path.Combine(stage, "index.html"));
            })));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
    }

    [Fact]
    public async Task ExportPreservesFullObservedTextBeyondTheReaderDisplayLimit()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string body = new string('λ', 70_000) + " full-tail-marker";
        await catalog.CaptureAsync(Snapshot(directory.Path) with { Posts = new[] { new CapturedPost("123", 0, PostFieldPresence.All, Html: body) } });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "long"), new(false));
        Assert.Contains(body, System.Net.WebUtility.HtmlDecode(await File.ReadAllTextAsync(result.IndexPath)));
    }

    [Fact]
    public async Task ExportMarkerHistoryAndExplicitUpdateSurviveRestart()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination, new(false));
        Assert.Equal(1, first.Metadata!.Generation);
        Assert.True(File.Exists(Path.Combine(destination, ArchiveExportMetadataFile.Name)));
        ExportHistoryStore history = new(profile, lease);
        Assert.True((await history.RecordPublishedAsync(first)).HistoryRecorded);
        ExportHistoryStore reopened = new(profile, lease);
        Assert.Single(await reopened.ListVerifiedAsync(Id));
        await catalog.CaptureAsync(Snapshot(profile) with { Posts = new[] { new CapturedPost("125", 2, PostFieldPresence.All, Html: "later") } });
        ArchiveHtmlExportResult next = await ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata.ExportId, first.Metadata.Generation, new(false));
        Assert.Equal(2, next.Metadata!.Generation);
        Assert.Equal(first.Metadata.ExportId, next.Metadata.ExportId);
        Assert.Contains("later", await File.ReadAllTextAsync(next.IndexPath));
        Assert.Null(next.BackupDirectory);
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata.ExportId, first.Metadata.Generation));
    }

    [Fact]
    public async Task ModifiedExportRequiresExplicitBackupAndMaliciousInventoryCannotEscape()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination, new(false));
        await File.AppendAllTextAsync(first.IndexPath, "USER EDIT");
        await File.WriteAllTextAsync(Path.Combine(destination, "notes.txt"), "KEEP");
        ArchiveExportInspection inspection = await ArchiveHtmlExporter.InspectAsync(destination, Id);
        Assert.True(inspection.RequiresBackup);
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata!.ExportId, 1, new(false)));
        Assert.Contains("USER EDIT", await File.ReadAllTextAsync(first.IndexPath));
        ArchiveHtmlExportResult updated = await ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata!.ExportId, 1, new(false), confirmPreserveBackup: true);
        Assert.NotNull(updated.BackupDirectory);
        Assert.Equal("KEEP", await File.ReadAllTextAsync(Path.Combine(updated.BackupDirectory!, "notes.txt")));
        string marker = Path.Combine(destination, ArchiveExportMetadataFile.Name);
        string json = await File.ReadAllTextAsync(marker);
        await File.WriteAllTextAsync(marker, json.Replace("index.html", "../outside", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveHtmlExporter.InspectAsync(destination, Id));
    }

    [Fact]
    public async Task RenameFaultRollsBackOriginalExport()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination, new(false));
        string oldHtml = await File.ReadAllTextAsync(first.IndexPath);
        ArchiveExportFaults.AfterOldRename = () => throw new IOException("injected first rename failure");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
                first.Metadata!.ExportId, 1, new(false)));
        }
        finally { ArchiveExportFaults.AfterOldRename = null; }
        Assert.Equal(oldHtml, await File.ReadAllTextAsync(first.IndexPath));
        Assert.Equal(1, (await ArchiveHtmlExporter.InspectAsync(destination, Id)).Metadata.Generation);
    }

    [Fact]
    public async Task RestartRecoveryRestoresTargetAfterFirstRename()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination, new(false));
        string backupName = ".meowchan-export-backup-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(directory.Path, backupName);
        string journal = Path.Combine(directory.Path, ".meowchan-export-journal-" +
            ArchiveExportTargetLease.Key(destination) + ".json");
        await File.WriteAllTextAsync(journal, System.Text.Json.JsonSerializer.Serialize(
            new ArchiveExportJournal(backupName, first.Metadata!.ExportId, 2, false)));
        Directory.Move(destination, backup);
        ArchiveHtmlExportResult updated = await ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata.ExportId, first.Metadata.Generation, new(false));
        Assert.Equal(2, updated.Metadata!.Generation);
        Assert.False(File.Exists(journal));
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public async Task PublishedRenameFaultRollsBackAndRetainsOldContent()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination, new(false));
        ArchiveExportFaults.AfterNewRename = () => throw new IOException("injected second rename failure");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
                first.Metadata!.ExportId, 1, new(false)));
        }
        finally { ArchiveExportFaults.AfterNewRename = null; }
        Assert.Equal(1, (await ArchiveHtmlExporter.InspectAsync(destination, Id)).Metadata.Generation);
    }

    [Fact]
    public async Task IndexCommitFailureDoesNotReclassifyPublishedFilesAsFailure()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, Id,
            Path.Combine(directory.Path, "meowchan-test"));
        await File.WriteAllTextAsync(Path.Combine(profile, "export-history.json"), "not json");
        ArchiveExportOperationResult recorded = await new ExportHistoryStore(profile, lease).RecordPublishedAsync(export);
        Assert.False(recorded.HistoryRecorded);
        Assert.NotNull(recorded.HistoryError);
        Assert.True(File.Exists(export.IndexPath));
        Assert.Equal(export.Metadata!.ExportId, (await ArchiveHtmlExporter.InspectAsync(export.Directory, Id)).Metadata.ExportId);
    }

    [Fact]
    public async Task LegacyHintNeedsExplicitAssociationAndPreservesOldFolderOnUpdate()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string legacy = Path.Combine(directory.Path, "ochan-20260101-123");
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "index.html"),
            "<!doctype html><html><body><p>4chan /g/ 123</p><p>old text</p></body></html>");
        Assert.Single(ArchiveExportLegacy.FindCandidates(directory.Path, Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveHtmlExporter.InspectAsync(legacy, Id));
        ArchiveExportInspection adopted = await ArchiveExportLegacy.AssociateAsync(reader, Id, legacy);
        Assert.True(adopted.Metadata.AdoptedLegacy);
        Assert.Null(adopted.Metadata.LastCapturedAt);
        Assert.Null(adopted.Metadata.CaptureRevision);
        Assert.Null(adopted.Metadata.PostCount);
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.UpdateAsync(reader, Id, legacy,
            adopted.Metadata.ExportId, 1, new(false)));
        ArchiveHtmlExportResult updated = await ArchiveHtmlExporter.UpdateAsync(reader, Id, legacy,
            adopted.Metadata.ExportId, 1, new(false), confirmPreserveBackup: true);
        Assert.Contains("old text", await File.ReadAllTextAsync(Path.Combine(updated.BackupDirectory!, "index.html")));
    }

    [Fact]
    public async Task LessCompleteReplacementKeepsOldMediaAsBackup()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        Directory.Delete(media, true);
        ArchiveHtmlExportResult less = await ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata!.ExportId, 1, confirmPreserveBackup: true);
        Assert.Equal(0, less.FileCount);
        Assert.NotNull(less.BackupDirectory);
        Assert.Single(Directory.GetFiles(Path.Combine(less.BackupDirectory!, "media")));
    }

    [Fact]
    public async Task SelectionFingerprintRejectsSubsequentEditAndConcurrentUpdate()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        ArchiveExportInspection selected = await ArchiveHtmlExporter.InspectAsync(destination, Id);
        await File.AppendAllTextAsync(first.IndexPath, "edited after selection");
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata!.ExportId, 1, confirmPreserveBackup: true,
            expectedObservedFingerprint: selected.ObservedFingerprint));
        ArchiveExportInspection current = await ArchiveHtmlExporter.InspectAsync(destination, Id);
        Task<ArchiveHtmlExportResult> one = ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata!.ExportId, 1, confirmPreserveBackup: true,
            expectedObservedFingerprint: current.ObservedFingerprint);
        Task<ArchiveHtmlExportResult> two = ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata.ExportId, 1, confirmPreserveBackup: true,
            expectedObservedFingerprint: current.ObservedFingerprint);
        await Task.WhenAll(one.ContinueWith(_ => { }), two.ContinueWith(_ => { }));
        _ = Assert.Single(new[] { one, two }, task => task.IsCompletedSuccessfully);
        _ = Assert.Single(new[] { one, two }, task => task.IsFaulted);
    }

    [Fact]
    public async Task SymlinkedInventoryFileCannotBeUsedAsUpdateTarget()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        string outside = Path.Combine(directory.Path, "outside.html");
        File.Move(first.IndexPath, outside);
        try { File.CreateSymbolicLink(first.IndexPath, outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { return; }
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveHtmlExporter.InspectAsync(destination, Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
            first.Metadata!.ExportId, 1, confirmPreserveBackup: true));
        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAfterSecondRenameKeepsPublishedTargetAndOnlyDeletesUnchangedBackup(bool modifyBackup)
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        ArchiveExportInspection before = await ArchiveHtmlExporter.InspectAsync(destination, Id);
        string candidate = Path.Combine(directory.Path, "candidate");
        await ArchiveHtmlExporter.ExportAsync(reader, Id, candidate);
        string candidateMarker = Path.Combine(candidate, ArchiveExportMetadataFile.Name);
        ArchiveExportMetadata newMetadata = System.Text.Json.JsonSerializer.Deserialize<ArchiveExportMetadata>(
            await File.ReadAllTextAsync(candidateMarker))! with { ExportId = first.Metadata!.ExportId, Generation = 2 };
        await File.WriteAllTextAsync(candidateMarker, System.Text.Json.JsonSerializer.Serialize(newMetadata));
        string backupName = ".meowchan-export-backup-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(directory.Path, backupName);
        string journal = Path.Combine(directory.Path, ".meowchan-export-journal-" +
            ArchiveExportTargetLease.Key(destination) + ".json");
        await File.WriteAllTextAsync(journal, System.Text.Json.JsonSerializer.Serialize(new ArchiveExportJournal(
            backupName, first.Metadata.ExportId, 2, false, before.ObservedFingerprint)));
        Directory.Move(destination, backup);
        Directory.Move(candidate, destination);
        if (modifyBackup) await File.WriteAllTextAsync(Path.Combine(backup, "user-notes.txt"), "preserve");
        ArchiveExportReplacement.Recover(destination);
        Assert.Equal(2, (await ArchiveHtmlExporter.InspectAsync(destination, Id)).Metadata.Generation);
        Assert.Equal(modifyBackup, Directory.Exists(backup));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task NullInventoryHashIsRejectedAsInvalidMarker()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        string marker = Path.Combine(destination, ArchiveExportMetadataFile.Name);
        string json = await File.ReadAllTextAsync(marker);
        await File.WriteAllTextAsync(marker, System.Text.RegularExpressions.Regex.Replace(json,
            "\"Sha256\"\\s*:\\s*\"[0-9A-F]+\"", "\"Sha256\": null", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveHtmlExporter.InspectAsync(destination, Id));
    }

    [Fact]
    public async Task CreateOnlyRecoversRenamedOldExportAndStillRejectsExistingTarget()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        string backupName = ".meowchan-export-backup-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(directory.Path, backupName);
        string journal = Path.Combine(directory.Path, ".meowchan-export-journal-" +
            ArchiveExportTargetLease.Key(destination) + ".json");
        await File.WriteAllTextAsync(journal, System.Text.Json.JsonSerializer.Serialize(new ArchiveExportJournal(
            backupName, first.Metadata!.ExportId, 2, false)));
        Directory.Move(destination, backup);
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.ExportAsync(reader, Id, destination));
        Assert.Equal(1, (await ArchiveHtmlExporter.InspectAsync(destination, Id)).Metadata.Generation);
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public async Task CleanupFailureLeavesVerifiedNewExportPublished()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "meowchan-test");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        ArchiveExportFaults.BeforeBackupCleanup = () => throw new IOException("injected backup cleanup failure");
        ArchiveHtmlExportResult published;
        try
        {
            published = await ArchiveHtmlExporter.UpdateAsync(reader, Id, destination,
                first.Metadata!.ExportId, 1);
        }
        finally { ArchiveExportFaults.BeforeBackupCleanup = null; }
        Assert.Equal(2, published.Metadata!.Generation);
        Assert.NotNull(published.BackupDirectory);
        Assert.True(Directory.Exists(published.BackupDirectory));
        Assert.Contains(published.Warnings, warning => warning.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, (await ArchiveHtmlExporter.InspectAsync(destination, Id)).Metadata.Generation);
    }

    [Fact]
    public async Task ExistingProfileChildCannotBecomeAnUpdateOrLegacyAssociationTarget()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string inside = Path.Combine(profile, "prior-export");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, inside);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveHtmlExporter.UpdateAsync(reader, Id, inside,
            first.Metadata!.ExportId, 1));
        string legacy = Path.Combine(profile, "ochan-legacy");
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "index.html"), "<p>4chan /g/ 123</p>");
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveExportLegacy.AssociateAsync(reader, Id, legacy));
    }

    [Theory]
    [InlineData("modified-html")]
    [InlineData("missing-css")]
    [InlineData("extra-file")]
    public async Task RecoveryNeverDeletesCleanFallbackWhenPublishedTargetHasChanged(string change)
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "export");
        ArchiveHtmlExportResult first = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination, new(false));
        ArchiveExportInspection before = await ArchiveHtmlExporter.InspectAsync(destination, Id);
        string candidate = Path.Combine(directory.Path, "candidate");
        ArchiveHtmlExportResult next = await ArchiveHtmlExporter.ExportAsync(reader, Id, candidate, new(false));
        await File.WriteAllTextAsync(Path.Combine(candidate, ArchiveExportMetadataFile.Name),
            System.Text.Json.JsonSerializer.Serialize(next.Metadata! with { ExportId = first.Metadata!.ExportId, Generation = 2 }));
        string backupName = ".meowchan-export-backup-" + Guid.NewGuid().ToString("N");
        string backup = Path.Combine(directory.Path, backupName);
        string journal = Path.Combine(directory.Path, ".meowchan-export-journal-" + ArchiveExportTargetLease.Key(destination) + ".json");
        await File.WriteAllTextAsync(journal, System.Text.Json.JsonSerializer.Serialize(new ArchiveExportJournal(
            backupName, first.Metadata.ExportId, 2, false, before.ObservedFingerprint)));
        Directory.Move(destination, backup);
        Directory.Move(candidate, destination);
        if (change == "modified-html") await File.AppendAllTextAsync(Path.Combine(destination, "index.html"), "USER EDIT");
        else if (change == "missing-css") File.Delete(Path.Combine(destination, "style.css"));
        else await File.WriteAllTextAsync(Path.Combine(destination, "personal-note.txt"), "KEEP");
        ArchiveExportInspection damaged = await ArchiveHtmlExporter.InspectAsync(destination, Id);
        Assert.True(damaged.RequiresBackup);

        IOException failure = Assert.Throws<IOException>(() => ArchiveExportReplacement.Recover(destination));

        Assert.Contains("manual review", failure.Message);
        Assert.Contains(backup, failure.Message);
        Assert.True(File.Exists(journal));
        Assert.Equal(before.ObservedFingerprint, (await ArchiveHtmlExporter.InspectAsync(backup, Id)).ObservedFingerprint);
        Assert.Equal(damaged.ObservedFingerprint, (await ArchiveHtmlExporter.InspectAsync(destination, Id)).ObservedFingerprint);
    }

    private sealed class InlineProgress(Action<ArchiveHtmlExportProgress> report) : IProgress<ArchiveHtmlExportProgress>
    { public void Report(ArchiveHtmlExportProgress value) => report(value); }
    private static CapturedThreadSnapshot Snapshot(string media) => new(Id, "https://boards.4chan.org/g/thread/123", "<Title> & stuff",
        ThreadSourceState.Archived, DateTimeOffset.UtcNow, media,
        new[] {
            new CapturedPost("123", 0, PostFieldPresence.All, Html: "&lt;img src=x onerror=alert(1)&gt;<script>secret-script</script>\n<a href='#p124'>&gt;&gt;124</a><a href='#p999'>&gt;&gt;999</a><a href='/a/thread/400#p400'>&gt;&gt;400</a><a href='https://external.test/#p9'>&gt;&gt;9</a>",
                Attachments: new[] { new CapturedAttachment(0, "456", OriginalFileName: "../<evil>.bin", ExpectedSize: Bytes.Length, ExpectedMd5: Md5),
                    new CapturedAttachment(1, "missing"), new CapturedAttachment(2, "456", ExpectedMd5: Convert.ToBase64String(MD5.HashData(new byte[] { 5, 5, 5, 5 }))) }),
            new CapturedPost("124", 1, PostFieldPresence.All, Html: "retained", IsDeleted: true)
        });
    private static async Task<string> SaveAsync(string root, byte[]? payload = null)
    {
        payload ??= Bytes;
        using StubImageBoard board = new();
        DownloadItem item = new(new Uri("https://stub.test/media/456.bin"), Path.Combine(root, "media", "456.bin"),
            board, board.GetThread(new Uri("https://stub.test/g/thread/123")), "456", expectedSize: payload.Length, expectedMd5: Convert.ToBase64String(MD5.HashData(payload)));
        using MemoryStream source = new(payload, false);
        MediaCommitResult result = await new MediaStore().CommitAsync(item, source, CancellationToken.None);
        return Path.GetDirectoryName(result.Path)!;
    }
}
