using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Persistence;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruSearchTabIdentityTests
{
    [Fact]
    public void SourcePickerRoutesEqualPostIdsAndRestoresTheirOwnRows()
    {
        BooruComponentDispatcher.Run(() =>
        {
            BooruUiHarness legacy = new();
            BooruUiHarness second = new();
            BooruPostView firstPost = BooruUiFixtures.Post("42", preview: false);
            BooruPostView secondPost = BooruPostView.From(firstPost.Post with { Id = new("test-source", "42") });
            FakeBooruSearchPort provider = new() { ProviderId = "test-source", ProviderName = "Fixture" };
            legacy.Search.OnSearch = (query, page, _) => Task.FromResult(
                BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, firstPost)));
            provider.OnSearch = (query, page, _) => Task.FromResult(
                BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, secondPost)));
            BooruWorkbenchServices first = new(legacy.Search, legacy.Queue, legacy.Watchers, legacy.Previews, legacy.Clock);
            BooruWorkbenchServices other = new(provider, second.Queue, second.Watchers, second.Previews, second.Clock);
            using BooruPage single = new(first);
            ComboBox Picker(BooruPage page)
            {
                TabControl tabs = Assert.IsType<TabControl>(page.View.Body);
                Control search = Assert.IsAssignableFrom<Control>(tabs.Items.OfType<TabItem>().First().Content);
                return Assert.Single(search.GetVisualDescendants().OfType<ComboBox>(), control => control.Name == "BooruSourcePicker");
            }
            Assert.False(Picker(single).IsVisible);
            using BooruPage page = new(first, [first, other]);
            ComboBox picker = Picker(page);
            Assert.True(picker.IsVisible);

            BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(page.Model.Search, "cat"));
            int secondTab = page.View.SearchTabs.Add(copyCurrent: false);
            picker.SelectedIndex = 1;
            Assert.Equal(0, provider.Requests);
            BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(page.Model.Search, "cat"));
            Assert.Equal(secondPost.Identity, Assert.Single(page.Model.Search.View.Rows).Post.Identity);
            BooruComponentDispatcher.Wait(page.Model.Search.LoadOriginalAsync("42", firstPost.Identity));
            Assert.Empty(second.Queue.Loads);
            BooruComponentDispatcher.Wait(page.Model.Search.LoadOriginalAsync("42", secondPost.Identity));
            Assert.Single(second.Queue.Loads);
            Assert.Empty(legacy.Queue.Loads);

            Assert.True(page.View.SearchTabs.Select(BooruSearchTabs.PrimaryId));
            Assert.Equal(firstPost.Identity, Assert.Single(page.Model.Search.View.Rows).Post.Identity);
            Assert.True(page.View.SearchTabs.Select(secondTab));
            Assert.Equal(secondPost.Identity, Assert.Single(page.Model.Search.View.Rows).Post.Identity);
            Assert.Equal(1, provider.Requests);
            Assert.Equal(1, legacy.Search.Requests);
        });
    }

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
