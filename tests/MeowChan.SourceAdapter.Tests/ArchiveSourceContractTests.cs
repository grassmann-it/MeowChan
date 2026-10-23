using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.OfficialSources.FourChan;
using Microsoft.Data.Sqlite;

namespace MeowChan.SourceAdapter.Tests;

public sealed class ArchiveSourceContractTests
{
    [Fact]
    public async Task DeclarativeDecimalPostOrderSurvivesFinalSnapshotAndOfflineRestart()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        PluginDescription original = new FourChanPlugin().Describe();
        SourceDescription source = original.Sources[0] with
        {
            SourceId = "numeric-fixture", Name = "Numeric fixture", BaseUri = new Uri("https://numeric.test/"),
            Routes = new[] { new UrlRoute(SourceResourceKind.Board, "numeric.test", "/{board}/", true),
                new UrlRoute(SourceResourceKind.Thread, "numeric.test", "/{board}/thread/{thread}", true) },
            HttpHosts = new[] { new HttpHostDeclaration("numeric.test", HttpHostPurpose.Metadata) },
            PostOrder = PostOrderKind.DecimalPostId
        };
        PluginDescription description = original with { PluginId = "numeric-plugin", Sources = new[] { source } };
        SourceAdapterTests.TestWorker worker = new((request, _, _) => Task.FromResult<ThreadReadResult>(new PluginSdk.ThreadSnapshot(
            new SourceThreadSnapshot(request.Thread, new Uri("https://numeric.test/b/thread/1"), "numbers", SourceThreadState.Archived,
                new[] { "100", "2", "10" }.Select(id => new SourcePost(id, 0, SourceFieldPresence.Text, Text: id)).ToArray()))), description);
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ThreadContentRuntime content = new(profile.Path, lease);
            await content.InitializeAsync();
            using PluginSourceImageBoard adapter = new(description, source.SourceId, _ => Task.FromResult<IPluginWorkerClient>(worker));
            DownloadQueue queue = new(1, 1048576);
            WorkStateStore work = new(profile.Path, lease);
            await using Registry registry = new(new[] { adapter }, queue, Path.Combine(profile.Path, "media"), TimeSpan.FromHours(1), 3600,
                contentRuntime: content, workStore: work);
            AdmissionResult admitted = await registry.AddAsync("https://numeric.test/b/thread/1");
            await SourceAdapterTests.WaitUntilAsync(() => admitted.Thread!.SourceState == ThreadSourceState.Archived);
            await SourceAdapterTests.WaitUntilAsync(() => admitted.Thread!.GetSnapshot(default!).Content!.LastCapturedAt != null);
            Assert.Contains("DecimalPostOrder", work.LoadResources().Single().FinalSnapshotJson);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView view = (await reader.ReadThreadAsync(new ResourceId("numeric-fixture", "b", "1")))!;
        Assert.Equal(new[] { "2", "10", "100" }, view.GetPosts().Select(post => post.PostId));
    }

    [Fact]
    public async Task VersionTwoCatalogIsReadableThenMigratesIdempotentlyWithoutLosingLegacyOrder()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        ResourceId id = new("4chan", "g", "1");
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/1", "old",
                ThreadSourceState.Archived, DateTimeOffset.UtcNow, Path.Combine(profile.Path, "media"),
                new[] { "100", "2", "10" }.Select(post => new CapturedPost(post, 0, PostFieldPresence.Html, Html: post)).ToArray()));
        }
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(profile.Path, "archive.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE archive_source_contracts; PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await using (ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path))
            {
                ArchivedThreadView view = (await reader.ReadThreadAsync(id))!;
                Assert.Equal(new[] { "2", "10", "100" }, view.GetPosts().Select(post => post.PostId));
            }
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        }
    }
}
