using System.Text.Json;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Persistence;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruSearchTabIdentityTests
{
    [Fact]
    public void Scoped120VersionOneMigratesBothExplicitAndImplicitIdentities()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BooruSearchTabStore.FileName);
        File.WriteAllText(path, """
            {"Version":1,"State":{"ActiveIndex":1,"Tabs":[
                {"PostId":"42","Query":null,"Page":1},
                {"SourceId":"danbooru","InstanceId":"other","PostId":"42","Query":null,"Page":1}
            ]}}
            """);
        BooruSearchTabStore store = new(lease);
        BooruSearchTabState state = Assert.IsType<BooruSearchTabState>(store.Read());
        Assert.Equal(1, state.ActiveIndex);
        Assert.Equal(new BooruPostKey("danbooru", BooruPostKey.DefaultInstance, "42"), state.Tabs[0]!.PostIdentity);
        Assert.Equal(new BooruPostKey("danbooru", "other", "42"), state.Tabs[1]!.PostIdentity);
        using JsonDocument migrated = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, migrated.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task RestoredPost42TabsRetainTheirScopeWithoutCallingEitherProvider()
    {
        using TempDirectory directory = new();
        BooruPostKey legacy = new("danbooru", BooruPostKey.DefaultInstance, "42");
        BooruPostKey second = new("test-source", "public", "42");
        var cachedSecond = BooruUiFixtures.Post("42").Post with { Id = new(second.SourceId, second.PostId) };
        BooruUiHarness ports = new();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        using (BooruSearchModel model = new(ports.Search, ports.Queue, ports.Previews, ports.Clock))
        {
            lease.Admission.CompleteStartup();
            BooruSearchTabs tabs = new(model);
            // Missing source and instance are the legacy Danbooru post target.
            tabs.RestoreTargets(new(1,
                [new(null, null, "42", null), new(second.SourceId, second.InstanceId, "42", null, Rows: [new(cachedSecond, null)])]));
            new BooruSearchTabStore(lease).Write(tabs.CaptureTargets());
        }

        using ProfileLease reopened = ProfileLease.Acquire(directory.Path);
        BooruSearchTabState saved = Assert.IsType<BooruSearchTabState>(new BooruSearchTabStore(reopened).Read());
        using BooruSearchModel restored = new(ports.Search, ports.Queue, ports.Previews, ports.Clock);
        BooruSearchTabs restoredTabs = new(restored);
        restoredTabs.RestoreTargets(saved);

        Assert.Equal(1, restoredTabs.ActiveId);
        Assert.Equal(second, restored.View.ExecutedPostIdentity);
        Assert.Equal(BooruSearchStatus.Unavailable, restored.View.Status);
        Assert.Empty(restored.View.Rows);
        Assert.Equal(second.SourceId, Assert.Single(restoredTabs.CaptureTargets().Tabs[1]!.Rows!).Post.Id.SourceId);
        Assert.False(restored.CanSubmit);
        await restored.SubmitAsync();
        await restored.RetryAsync();
        Assert.Equal(0, ports.Search.Requests);
        Assert.Equal(0, ports.Previews.Requests);

        Assert.True(restoredTabs.Select(BooruSearchTabs.PrimaryId));
        Assert.Equal(legacy, restored.View.ExecutedPostIdentity);
        Assert.Equal(BooruSearchStatus.Idle, restored.View.Status);
        Assert.Equal(0, ports.Search.Requests);
        await restored.RetryAsync();
        Assert.Equal(["42"], ports.Search.PostReads);
    }
}
