using System.Net;
using System.Text.Json;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ApphostPluginWorkflowSmokeTests
{
    [Fact]
    public void PluginWorkflowNeedsExplicitOwnedDescriptorWithoutCreatingProfile()
    {
        using OwnedRoot owned = new();
        string descriptor = owned.Descriptor("download");
        ApphostStartupSmoke parsed = ApphostStartupSmoke.Parse([ApphostPluginWorkflowSmoke.Switch, descriptor], owned.Profile("download"), null)!;
        Assert.NotNull(parsed.PluginWorkflow);
        Assert.Null(parsed.Workflow);
        Assert.False(Directory.Exists(owned.Profile("download")));
        parsed.PluginWorkflow!.Dispose();
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostPluginWorkflowSmoke.Switch, descriptor], owned.Profile("download"), "legacy"));
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostPluginWorkflowSmoke.Switch], owned.Profile("download"), null));
    }

    [Fact]
    public void PluginWorkflowRejectsProviderWorkflowOnlyPhase()
    {
        using OwnedRoot owned = new();
        Assert.Throws<ArgumentException>(() => ApphostPluginWorkflowSmoke.Parse(owned.Descriptor("shutdown"), owned.Profile("shutdown")));
    }

    [Fact]
    public async Task TerminalPluginFixtureRejectsUnknownMethodsAndEveryOfflineRequest()
    {
        using ApphostPluginFixtures transport = new();
        using HttpClient client = transport.Client();
        using HttpResponseMessage response = await client.GetAsync("https://lainchan.org/demo/res/123.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Desktop delivered Lainchan", await response.Content.ReadAsStringAsync());
        Assert.Equal(transport.Landscape, await client.GetByteArrayAsync("https://lainchan.org/demo/src/1000-0.png"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.org/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("https://lainchan.org/demo/res/123.json", null));
        Assert.Equal(2, transport.Unexpected);
        using ApphostPluginFixtures offline = new() { RejectAll = true };
        using HttpClient offlineClient = offline.Client();
        await Assert.ThrowsAsync<InvalidOperationException>(() => offlineClient.GetAsync("https://lainchan.org/demo/res/123.json"));
        Assert.Equal(1, offline.Unexpected);
    }

    [Fact]
    public async Task TerminalPluginFixtureServesFourSourcesAndCountsOnlyTheirEightOriginals()
    {
        Assert.Equal(new[] { "lainchan", "kohlchan", "wizchan", "endchan" }, ApphostPluginFixtures.WorkflowSources);
        Assert.Equal(new[] { "lainchan", "kohlchan" }, ApphostPluginFixtures.AuthorSources);
        using ApphostPluginFixtures transport = new();
        using HttpClient client = transport.Client();
        foreach (string source in ApphostPluginFixtures.WorkflowSources)
        {
            string json = await client.GetStringAsync(ApphostPluginFixtures.Origin(source) + "/demo/res/123.json");
            Assert.Contains("Desktop delivered " + source, json, StringComparison.OrdinalIgnoreCase);
            using JsonDocument parsed = JsonDocument.Parse(json);
            if (source is "lainchan" or "wizchan")
            {
                Assert.Equal(123, parsed.RootElement.GetProperty("posts")[0].GetProperty("no").GetInt32());
                Assert.Equal("1001-1", parsed.RootElement.GetProperty("posts")[0].GetProperty("extra_files")[0].GetProperty("tim").GetString());
            }
            else
            {
                Assert.Equal(123, parsed.RootElement.GetProperty("threadId").GetInt32());
                Assert.Equal(ApphostPluginFixtures.OriginalUrl(source, true),
                    ApphostPluginFixtures.Origin(source) + parsed.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
                Assert.Equal(124, parsed.RootElement.GetProperty("posts")[0].GetProperty("postId").GetInt32());
            }
            Assert.Equal(transport.Landscape, await client.GetByteArrayAsync(ApphostPluginFixtures.OriginalUrl(source, true)));
            Assert.Equal(transport.Portrait, await client.GetByteArrayAsync(ApphostPluginFixtures.OriginalUrl(source, false)));
        }
        Assert.Equal(12, transport.Requests.Count);
        Assert.Equal(12, transport.Requests.Distinct().Count());
        Assert.Equal(8, transport.MediaRequests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://wizchan.org/demo/src/unknown.png"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://endchan.net/.media/unknown.png"));
        Assert.Equal(8, transport.MediaRequests);
        Assert.Equal(2, transport.Unexpected);
        Assert.Throws<ArgumentException>(() => ApphostPluginFixtures.Origin("unknown"));
        Assert.Throws<ArgumentException>(() => ApphostPluginFixtures.OriginalUrl("unknown", true));
    }

    private sealed class OwnedRoot : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "meowchan-workflow-plugin-tests-" + Guid.NewGuid().ToString("N"));
        private readonly string _token = Guid.NewGuid().ToString("N");
        internal OwnedRoot()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, ".workflow-owner"), _token);
        }
        internal string Profile(string phase) => Path.Combine(_root, "profile-" + phase);
        internal string Descriptor(string phase)
        {
            string path = Path.Combine(_root, phase + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            { schemaVersion = 1, root = _root, phase, ownerToken = _token, expectedScale = 1, previousProfile = (string?)null }));
            return path;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
