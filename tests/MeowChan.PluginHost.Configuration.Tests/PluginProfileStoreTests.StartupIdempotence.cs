using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed partial class PluginProfileStoreTests
{
    private static readonly string[] Restricted = ["restricted.source"];

    [Fact]
    public async Task CleanStartupsOnUnchangedProfileKeepRevisionAndFileBytes()
    {
        PluginProfileStore store = Store();
        PluginCandidateObservation candidate = Candidate("a");
        PluginProfileSnapshot state = await store.ApplyBundledStartupAsync([candidate], [], Restricted, 0);
        // The first starts establish the build and settle LastGood. From there on nothing changes.
        state = await store.ApplyBundledStartupAsync([candidate], [], Restricted, state.Revision);
        long revision = state.Revision;
        long lifecycle = Assert.Single(state.Plugins).LifecycleRevision;
        byte[] bytes = File.ReadAllBytes(store.StorePath);
        DateTime written = File.GetLastWriteTimeUtc(store.StorePath);
        Assert.Equal(2, Version(store.StorePath));

        for (int start = 0; start < 7; start++)
        {
            state = await Store().ApplyBundledStartupAsync([candidate], [], Restricted, state.Revision);
            Assert.Equal(revision, state.Revision);
            Assert.Equal(lifecycle, Assert.Single(state.Plugins).LifecycleRevision);
            Assert.Equal("a", Active(state).ExecutionRevision);
            Assert.Equal(bytes, File.ReadAllBytes(store.StorePath));
        }
        Assert.Equal(written, File.GetLastWriteTimeUtc(store.StorePath));
        Assert.Equal(revision, (await Store().ReadAsync()).Revision);
    }

    [Fact]
    public async Task V1ProfileIsStillWrittenAndMigratedByStartup()
    {
        PluginProfileStore store = Store();
        PluginCandidateObservation candidate = Candidate("a");
        PluginProfileSnapshot state = await store.ApplyStartupAsync([candidate], 0); // v1 envelope
        state = await store.ApplyStartupAsync([candidate], state.Revision);
        Assert.Equal(1, Version(store.StorePath));
        byte[] v1 = File.ReadAllBytes(store.StorePath);
        long revision = state.Revision;

        // Even a startup that changes no plugin entry still goes through the writer for v1 ...
        state = await store.ApplyStartupAsync([candidate], state.Revision);
        Assert.Equal(revision + 1, state.Revision);
        Assert.Equal(1, Version(store.StorePath));
        // ... and the bundled startup migrates it to the delivered format with the backup kept.
        state = await store.ApplyBundledStartupAsync([candidate], [], Restricted, state.Revision);
        Assert.Equal(2, Version(store.StorePath));
        Assert.True(File.Exists(store.PreV2BackupPath));
        Assert.NotEqual(v1, File.ReadAllBytes(store.StorePath));
    }

    private static int Version(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("version").GetInt32();
    }

    [Fact]
    public async Task StaleActiveIsStillClearedAndRecordedWhenTheBuildDisappears()
    {
        PluginProfileStore store = Store();
        PluginCandidateObservation candidate = Candidate("a");
        PluginProfileSnapshot state = await store.ApplyBundledStartupAsync([candidate], [], Restricted, 0);
        state = await store.ApplyBundledStartupAsync([candidate], [], Restricted, state.Revision);
        long revision = state.Revision;
        long lifecycle = state.Plugins[0].LifecycleRevision;
        Assert.NotNull(state.Plugins[0].Active);
        byte[] before = File.ReadAllBytes(store.StorePath);

        // The recorded Active build is no longer present at startup: it must not stay active.
        state = await store.ApplyBundledStartupAsync([], [], Restricted, state.Revision);
        Assert.Equal(revision + 1, state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.NotNull(state.Plugins[0].Problem);
        Assert.Equal("a", state.Plugins[0].LastGood!.ExecutionRevision);
        Assert.Equal(lifecycle + 1, state.Plugins[0].LifecycleRevision);
        Assert.NotEqual(before, File.ReadAllBytes(store.StorePath));
        Assert.Null((await Store().ReadAsync()).Plugins[0].Active);

        // Recovery is a real change as well, and afterwards the profile is quiet again.
        state = await store.ApplyBundledStartupAsync([candidate], [], Restricted, state.Revision);
        Assert.Equal("a", Active(state).ExecutionRevision);
        long settled = state.Revision;
        state = await store.ApplyBundledStartupAsync([candidate], [], Restricted, state.Revision);
        Assert.Equal(settled, state.Revision);
    }
}
