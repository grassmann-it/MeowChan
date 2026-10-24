using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class ArchiveCleanupExecutionFilesTests
{
    [Fact]
    public async Task ConfirmedAssetsAndManifestAreRemovedWithActualCountsUnderLease()
    {
        using Fixture fixture = new();
        ArchiveCleanupPreviewRow row = await fixture.CreateAsync();
        IReadOnlyList<ArchiveCleanupDirectoryFiles> files = await ArchiveCleanupExecutionFiles.PrepareAsync(row, fixture.Profile, default);
        await using IAsyncDisposable leases = await ArchiveCleanupExecutionFiles.AcquireAsync(row.Media.Directories, fixture.Profile, default);
        int checks = 0, count = 0;
        long bytes = 0;
        await ArchiveCleanupExecutionFiles.DeleteAsync(files, fixture.Profile, () => { checks++; return Task.CompletedTask; },
            length => { count++; bytes += length; }, default);
        Assert.Equal(2, checks);
        Assert.Equal(2, count);
        Assert.Equal(files.Single().Files.Sum(file => file.Length), bytes);
        Assert.False(File.Exists(fixture.Asset));
        Assert.False(File.Exists(fixture.Manifest));
        Assert.True(Directory.Exists(fixture.Media));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSameLengthBytesRefuseFirstDeleteAndPreserveManifest(bool manifest)
    {
        using Fixture fixture = new();
        ArchiveCleanupPreviewRow row = await fixture.CreateAsync();
        IReadOnlyList<ArchiveCleanupDirectoryFiles> files = await ArchiveCleanupExecutionFiles.PrepareAsync(row, fixture.Profile, default);
        string changed = manifest ? fixture.Manifest : fixture.Asset;
        byte[] altered = await File.ReadAllBytesAsync(changed);
        altered[0] ^= 1;
        await File.WriteAllBytesAsync(changed, altered);
        int deleted = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCleanupExecutionFiles.DeleteAsync(files, fixture.Profile,
            () => Task.CompletedTask, _ => deleted++, default));
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(fixture.Manifest));
        Assert.True(File.Exists(fixture.Asset));
        Assert.Equal(altered, await File.ReadAllBytesAsync(changed));
    }

    [Fact]
    public async Task NewlyIntroducedForeignFileBlocksWithoutDeletingEitherFile()
    {
        using Fixture fixture = new();
        ArchiveCleanupPreviewRow row = await fixture.CreateAsync();
        IReadOnlyList<ArchiveCleanupDirectoryFiles> files = await ArchiveCleanupExecutionFiles.PrepareAsync(row, fixture.Profile, default);
        string foreign = Path.Combine(fixture.Media, "notes.txt");
        int count = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCleanupExecutionFiles.DeleteAsync(files, fixture.Profile,
            () => File.WriteAllTextAsync(foreign, "retain"), _ => count++, default));
        Assert.Equal(0, count);
        Assert.Equal("retain", await File.ReadAllTextAsync(foreign));
        Assert.True(File.Exists(fixture.Asset));
        Assert.True(File.Exists(fixture.Manifest));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelOrChangedProtectionAfterFirstCommitReportsActualPartialFiles(bool cancel)
    {
        using Fixture fixture = new();
        ArchiveCleanupPreviewRow row = await fixture.CreateAsync();
        IReadOnlyList<ArchiveCleanupDirectoryFiles> files = await ArchiveCleanupExecutionFiles.PrepareAsync(row, fixture.Profile, default);
        using CancellationTokenSource cancellation = new();
        int count = 0;
        long bytes = 0;
        Task Delete() => ArchiveCleanupExecutionFiles.DeleteAsync(files, fixture.Profile,
            () => count == 0 ? Task.CompletedTask : Task.FromException(new InvalidDataException("Protection changed")),
            length => { count++; bytes += length; if (cancel) cancellation.Cancel(); }, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Delete);
        else await Assert.ThrowsAsync<InvalidDataException>(Delete);
        Assert.Equal(1, count);
        Assert.Equal(3, bytes);
        Assert.False(File.Exists(fixture.Asset));
        Assert.True(File.Exists(fixture.Manifest));
    }

    [Fact]
    public async Task BusyStoreLeaseRefusesWithoutWaitingOutOrStoppingWriter()
    {
        using Fixture fixture = new();
        ArchiveCleanupPreviewRow row = await fixture.CreateAsync();
        await using IAsyncDisposable first = await ArchiveCleanupExecutionFiles.AcquireAsync(row.Media.Directories, fixture.Profile, default);
        await Assert.ThrowsAsync<IOException>(() => ArchiveCleanupExecutionFiles.AcquireAsync(row.Media.Directories, fixture.Profile, default));
        Assert.True(File.Exists(fixture.Asset));
        Assert.True(File.Exists(fixture.Manifest));
    }

    [Fact]
    public async Task EmptyDirectoryLeaseAlsoFencesFirstLegacyWriter()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(fixture.Profile); Directory.CreateDirectory(fixture.Media);
        ArchiveCleanupDirectoryEvidence empty = new(fixture.Media, null, [], 0, false, null, "empty", null);
        await using IAsyncDisposable leases = await ArchiveCleanupExecutionFiles.AcquireAsync([empty], fixture.Profile, default);
        string legacyLease = Path.Combine(fixture.Media, ".ochan-store.lock");
        Assert.True(File.Exists(legacyLease));
        Assert.Throws<IOException>(() => new FileStream(legacyLease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        Assert.False(File.Exists(fixture.Asset));
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Fact]
    public async Task LegacyManifestRequiresLegacyWriterLeaseBeforeAnyDeletion()
    {
        using Fixture fixture = new();
        ArchiveCleanupPreviewRow row = await fixture.CreateAsync();
        string legacyManifest = Path.Combine(fixture.Media, ".ochan-manifest.json");
        File.Move(fixture.Manifest, legacyManifest);
        string leasePath = Path.Combine(fixture.Media, ".ochan-store.lock");
        using (FileStream writer = new(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => ArchiveCleanupExecutionFiles.AcquireAsync(row.Media.Directories, fixture.Profile, default));
        Assert.True(File.Exists(fixture.Asset));
        Assert.True(File.Exists(legacyManifest));
        IReadOnlyList<ArchiveCleanupDirectoryFiles> files = await ArchiveCleanupExecutionFiles.PrepareAsync(row, fixture.Profile, default);
        await using IAsyncDisposable leases = await ArchiveCleanupExecutionFiles.AcquireAsync(row.Media.Directories, fixture.Profile, default);
        Assert.Throws<IOException>(() => new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        int count = 0;
        await ArchiveCleanupExecutionFiles.DeleteAsync(files, fixture.Profile, () => Task.CompletedTask, _ => count++, default);
        Assert.Equal(2, count);
        Assert.False(File.Exists(fixture.Asset));
        Assert.False(File.Exists(legacyManifest));
    }

    [Fact]
    public async Task MissingDirectoryWithoutAnchorIsNotAnExecutableZero()
    {
        using Fixture fixture = new();
        ArchiveCleanupPreviewRow row = await fixture.CreateAsync();
        string missing = Path.Combine(fixture.Media, "missing");
        ArchiveCleanupDirectoryEvidence unknown = new(missing, null, [], 0, true, null, "missing", null);
        row = row with { Media = new(0, 0, [unknown], "missing", null) };
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveCleanupExecutionFiles.PrepareAsync(row, fixture.Profile, default));
        Assert.False(Directory.Exists(missing));
        Assert.True(File.Exists(fixture.Asset));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        internal string Profile => Path.Combine(_temp.Path, "profile");
        internal string Media => Path.Combine(_temp.Path, "downloads", "thread");
        internal string Asset => Path.Combine(Media, "image.jpg");
        internal string Manifest => Path.Combine(Media, ".meowchan-manifest.json");
        internal async Task<ArchiveCleanupPreviewRow> CreateAsync()
        {
            Directory.CreateDirectory(Profile); Directory.CreateDirectory(Media);
            byte[] bytes = [1, 2, 3];
            await File.WriteAllBytesAsync(Asset, bytes);
            await File.WriteAllTextAsync(Manifest, JsonSerializer.Serialize(new
            {
                Version = 1, Entries = new Dictionary<string, object> { ["image.jpg"] = new
                {
                    MediaIdentifier = "image", SourceUrl = "https://offline.invalid/image.jpg", FileName = "image.jpg",
                    Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes))
                } }
            }));
            MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(Media);
            ArchiveCleanupDirectoryEvidence evidence = new(Media, snapshot.Generation, snapshot.GetPage(0, 500), 3, false, null, "fixture", null);
            ResourceId id = new("fixture", "b", "123");
            ArchiveThreadSummary summary = new(id, "https://offline.invalid/123", "fixture", Media, ThreadSourceState.Archived,
                true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 1);
            return new(summary, new(1, 3, [evidence], "fixture", null), ArchiveCleanupEligibility.Candidate, [], "fixture");
        }
        public void Dispose() => _temp.Dispose();
    }
}
