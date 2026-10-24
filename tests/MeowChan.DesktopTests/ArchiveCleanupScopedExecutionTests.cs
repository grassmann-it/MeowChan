using System.Collections.Concurrent;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;
using static MeowChan.DesktopTests.ArchiveCleanupFixtures;

namespace MeowChan.DesktopTests;

public sealed class ArchiveCleanupScopedExecutionTests
{
    [Fact]
    public async Task ExecutionVerifiesAllSelectedHistoricalDirectoriesButNeverUnrelatedRetainedMedia()
    {
        using Fixture fixture = new();
        ResourceId selected = Id("100"), zero = Id("101"), retained = Id("900");
        await fixture.SeedAsync((selected, 1), (zero, 0), (retained, 3));
        string historical = Path.Combine(fixture.Root, "historical", "100");
        await AssetsAsync(historical, new Asset("old", "original", "old.bin", "old.bin", [4, 5, 6]));
        using (ProfileLease lease = ProfileLease.Acquire(fixture.Profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(fixture.Profile, lease);
            await catalog.RegisterThreadAsync(new(selected, "https://offline.invalid/100", "Thread 100", historical, false));
        }
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync();
        Assert.Equal(2, preview.Rows.Single(row => row.Id == selected).Media.SavedAssetCount);
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [selected, zero]);
        Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
        byte[] retainedBytes = File.ReadAllBytes(Path.Combine(fixture.Media(retained), "asset0.bin"));
        var reads = Observe(host);

        var result = await host.ExecuteArchiveCleanupAsync(plan);

        Assert.Empty(result.Errors); Assert.Equal(new[] { selected, zero }, result.DeletedThreads);
        Assert.Equal(2, reads.Count(item => item.Directory == fixture.Media(selected)));
        Assert.Equal(2, reads.Count(item => item.Directory == historical));
        Assert.Equal(4, reads.Count(item => item.Directory == fixture.Media(zero)));
        Assert.DoesNotContain(reads, item => item.Directory == fixture.Media(retained));
        Assert.Equal(12L, reads.Sum(item => item.Bytes));
        Assert.Equal(retainedBytes, File.ReadAllBytes(Path.Combine(fixture.Media(retained), "asset0.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSelectedOrExactAnchorBytesRejectWithoutTryingAnotherWitness(bool changeAnchor)
    {
        using Fixture fixture = new();
        ResourceId missing = Id("100"), selected = Id("101"), anchor = Id("200"), alternate = Id("201");
        await fixture.SeedAsync((missing, -1), (selected, 1), (anchor, 3), (alternate, 2));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync();
        Assert.Equal(anchor, preview.Rows.Single(row => row.Id == missing).Media.Directories.Single().StorageAnchor!.Thread);
        ResourceId target = changeAnchor ? missing : selected;
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [target]); Assert.True(plan.CanExecute);
        string changed = Path.Combine(fixture.Media(changeAnchor ? anchor : selected), "asset0.bin");
        byte[] bytes = File.ReadAllBytes(changed); bytes[0] ^= 1; File.WriteAllBytes(changed, bytes);
        var reads = Observe(host);

        var result = await host.ExecuteArchiveCleanupAsync(plan);

        Assert.NotEmpty(result.Errors); Assert.Empty(result.DeletedThreads); Assert.Equal(0, result.DeletedFileCount);
        Assert.True(result.RequiresNewPreview);
        Assert.DoesNotContain(reads, item => item.Directory == fixture.Media(alternate));
        Assert.Equal(bytes, File.ReadAllBytes(changed));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.Profile);
        Assert.NotNull(await reader.ReadThreadAsync(target));
    }

    [Fact]
    public async Task RemovedBoundAnchorOwnerRejectsBeforeReadingAReplacementWitness()
    {
        using Fixture fixture = new();
        ResourceId missing = Id("100"), anchor = Id("200"), alternate = Id("201");
        await fixture.SeedAsync((missing, -1), (anchor, 3), (alternate, 2));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync();
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [missing]); Assert.True(plan.CanExecute);
        fixture.Sql("DELETE FROM archive_media_directories WHERE thread='200'; DELETE FROM archive_threads WHERE thread='200';");
        var reads = Observe(host);

        var result = await host.ExecuteArchiveCleanupAsync(plan);

        Assert.NotEmpty(result.Errors); Assert.Empty(result.DeletedThreads); Assert.Equal(0, result.DeletedFileCount);
        Assert.Empty(reads);
        Assert.True(File.Exists(Path.Combine(fixture.Media(anchor), "asset0.bin")));
        Assert.True(File.Exists(Path.Combine(fixture.Media(alternate), "asset0.bin")));
    }

    [Fact]
    public async Task FreshGlobalOwnershipStillBlocksNewSharedOwnerOutsideTheMediaScope()
    {
        using Fixture fixture = new();
        ResourceId selected = Id("100"), other = Id("900");
        await fixture.SeedAsync((selected, 1), (other, 3));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync();
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [selected]); Assert.True(plan.CanExecute);
        fixture.Sql("INSERT INTO archive_media_directories(source,board,thread,directory) VALUES('source','board','900',$directory);",
            fixture.Media(selected));
        var reads = Observe(host);

        var result = await host.ExecuteArchiveCleanupAsync(plan);

        Assert.NotEmpty(result.Errors); Assert.Empty(result.DeletedThreads); Assert.Equal(0, result.DeletedFileCount);
        Assert.DoesNotContain(reads, item => item.Directory == fixture.Media(other));
        Assert.True(File.Exists(Path.Combine(fixture.Media(selected), "asset0.bin")));
    }

    [Fact]
    public async Task MultipleMissingRowsKeepExactSelectedWitnessUntilLastWithoutRehashingForeignArchive()
    {
        using Fixture fixture = new();
        ResourceId witness = Id("100"), first = Id("101"), second = Id("102"), foreign = new("source", "other", "900");
        await fixture.SeedAsync((witness, 2), (first, -1), (second, -1), (foreign, 3));
        await using DesktopSessionHost host = await fixture.StartAsync();
        var preview = await host.PrepareArchiveCleanupAsync();
        foreach (ResourceId missing in new[] { first, second })
            Assert.Equal(witness, preview.Rows.Single(row => row.Id == missing).Media.Directories.Single().StorageAnchor!.Thread);
        var plan = await host.PrepareArchiveCleanupExecutionAsync(preview, [witness, first, second]);
        Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
        var reads = Observe(host);

        var result = await host.ExecuteArchiveCleanupAsync(plan);

        Assert.Empty(result.Errors); Assert.Equal(new[] { first, second, witness }, result.DeletedThreads);
        Assert.Equal(4, reads.Count(item => item.Directory == fixture.Media(first)));
        Assert.Equal(4, reads.Count(item => item.Directory == fixture.Media(second)));
        Assert.Equal(10, reads.Count(item => item.Directory == fixture.Media(witness)));
        Assert.DoesNotContain(reads, item => item.Directory == fixture.Media(foreign));
        Assert.Equal(60L, reads.Sum(item => item.Bytes));
    }

    private static ConcurrentBag<(string Directory, long Bytes)> Observe(DesktopSessionHost host)
    {
        ConcurrentBag<(string Directory, long Bytes)> reads = new();
        host.ArchiveCleanupMediaVerified = (directory, bytes) => reads.Add((directory, bytes));
        return reads;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        internal string Profile => Path.Combine(_temp.Path, "profile");
        internal string Root => Path.Combine(_temp.Path, "downloads");
        internal string Media(ResourceId id) => DirectoryFor(Root, id);
        internal async Task SeedAsync(params (ResourceId Id, int Count)[] rows)
        {
            Config(Profile, Root);
            foreach (var row in rows)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Media(row.Id))!);
                if (row.Count == 0) Directory.CreateDirectory(Media(row.Id));
                else if (row.Count > 0)
                    await AssetsAsync(Media(row.Id), Enumerable.Range(0, row.Count).Select(index =>
                        new Asset("asset" + index, "original", $"asset{index}.bin", $"asset{index}.bin", [(byte)index, 2, 3])).ToArray());
            }
            await RegisterAsync(Profile, Root, rows.Select(row => row.Id).ToArray());
        }
        internal async Task<DesktopSessionHost> StartAsync()
        {
            DesktopSessionHost host = new(Profile, Array.Empty<IImageBoard>());
            await host.StartAsync(); return host;
        }
        internal void Sql(string text, string? directory = null)
        {
            using SqliteConnection db = new(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Profile, "archive.db"), Pooling = false }.ToString());
            db.Open(); using SqliteCommand command = db.CreateCommand(); command.CommandText = text;
            if (directory != null) command.Parameters.AddWithValue("$directory", directory);
            command.ExecuteNonQuery();
        }
        public void Dispose() => _temp.Dispose();
    }
}
