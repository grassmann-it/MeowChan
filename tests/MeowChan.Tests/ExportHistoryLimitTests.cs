using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ExportHistoryLimitTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SavedExportSurvivesRefusalAndExistingHistoryRemainsReadable(bool collection, bool sizeLimit)
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        string media = Directory.CreateDirectory(Path.Combine(directory.Path, "media")).FullName;
        using ProfileLease lease = ProfileLease.Acquire(profile);
        lease.StoreLimits.ExportHistory = new(1, sizeLimit ? 10 : 1);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Thread, "https://boards.4chan.org/g/thread/123",
            "Example", ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, media,
            [new CapturedPost("123", 0, PostFieldPresence.All, Html: "example")]));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        string destination = Path.Combine(directory.Path, "published");
        var options = new ArchiveHtmlExportOptions(false);

        if (collection)
        {
            string path = Path.Combine(profile, "collection-export-history.json");
            var previous = new CollectionExportHistoryEntry([Thread], Path.Combine(directory.Path, "previous"),
                Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, options);
            var store = new CollectionExportHistoryStore(profile, lease);
            Seed(path, previous, count => previous with { Destination = previous.Destination + new string('x', count) }, sizeLimit);
            var published = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [Thread], destination, options);
            var recorded = await store.RecordPublishedAsync(published);
            Assert.False(recorded.HistoryRecorded);
            Assert.Equal(ExportHistoryLimits.LimitError, recorded.HistoryError);
            Assert.Equal(published.Metadata!.ExportId, (await ArchiveCollectionHtmlExporter.InspectAsync(destination)).Metadata.ExportId);
            Assert.Equal(previous.ExportId, Assert.Single(store.List([Thread])).ExportId);
            AssertHistoryPreserved(path);
            lease.StoreLimits.ExportHistory = new(2, 2);
            Assert.True((await store.RecordPublishedAsync(published)).HistoryRecorded);
            Assert.Equal(2, store.List([Thread]).Count);
        }
        else
        {
            string path = Path.Combine(profile, "export-history.json");
            var previous = new ExportHistoryEntry(Thread, Path.Combine(directory.Path, "previous"),
                Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, options);
            var store = new ExportHistoryStore(profile, lease);
            Seed(path, previous, count => previous with { Destination = previous.Destination + new string('x', count) }, sizeLimit);
            var published = await ArchiveHtmlExporter.ExportAsync(reader, Thread, destination, options);
            var recorded = await store.RecordPublishedAsync(published);
            Assert.False(recorded.HistoryRecorded);
            Assert.Equal(ExportHistoryLimits.LimitError, recorded.HistoryError);
            Assert.Equal(published.Metadata!.ExportId, (await ArchiveHtmlExporter.InspectAsync(destination, Thread)).Metadata.ExportId);
            Assert.Equal(previous.ExportId, Assert.Single(store.List(Thread)).ExportId);
            AssertHistoryPreserved(path);
            lease.StoreLimits.ExportHistory = new(2, 2);
            Assert.True((await store.RecordPublishedAsync(published)).HistoryRecorded);
            Assert.Equal(2, store.List(Thread).Count);
        }
    }

    private static void Seed<T>(string path, T previous, Func<int, T> pad, bool sizeLimit)
    {
        string Serialize(T entry) => JsonSerializer.Serialize(new { Version = 1, Entries = new[] { entry } });
        string json = Serialize(previous);
        if (sizeLimit)
        {
            const int oneMiB = 1024 * 1024;
            json = Serialize(pad(oneMiB - Encoding.UTF8.GetByteCount(json)));
            Assert.Equal(oneMiB, Encoding.UTF8.GetByteCount(json));
        }
        File.WriteAllText(path, json);
        File.WriteAllText(path + ".original", json);
        File.WriteAllText(path + ".bak", "previous backup bytes");
    }

    private static void AssertHistoryPreserved(string path)
    {
        Assert.Equal(File.ReadAllText(path + ".original"), File.ReadAllText(path));
        Assert.Equal("previous backup bytes", File.ReadAllText(path + ".bak"));
    }
}
