using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Persistence;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruSearchTabIdentityTests
{
    [Fact]
    public async Task RestoredPost42TabsRetainTheirScopeWithoutCallingEitherProvider()
    {
        using TempDirectory directory = new();
        BooruPostKey legacy = new("danbooru", BooruPostKey.DefaultInstance, "42");
        BooruPostKey second = new("test-source", "public", "42");
        BooruUiHarness ports = new();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        using (BooruSearchModel model = new(ports.Search, ports.Queue, ports.Previews, ports.Clock))
        {
            lease.Admission.CompleteStartup();
            BooruSearchTabs tabs = new(model);
            // Missing source and instance are the legacy Danbooru post target.
            tabs.RestoreTargets(new(1, [new(null, null, "42", null), new(second.SourceId, second.InstanceId, "42", null)]));
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
