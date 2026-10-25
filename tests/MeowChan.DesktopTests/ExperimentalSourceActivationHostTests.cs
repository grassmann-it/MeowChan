using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.PluginHost.Activation;

namespace MeowChan.DesktopTests;

public sealed class ExperimentalSourceActivationHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task ProjectionIsPassiveExactAndActiveStrictUrlsNeverRegrant()
    {
        using TempDirectory profile = new();
        using DesktopPluginFixtures network = new() { RejectAll = true };
        await using DesktopSessionHost host = await StartAsync(profile.Path, network);
        foreach (var (source, url) in new[]
        {
            ("kohlchan", "https://kohlchan.net/demo/res/123.html"),
            ("wizchan", "https://wizchan.org/demo/index.html"),
            ("endchan", "https://endchan.org/demo/res/123.html")
        })
        {
            var projection = await host.ProjectExperimentalSourceActivationAsync(url);
            Assert.Equal(ExperimentalSourceActivationProjectionStatus.ConfirmationRequired, projection.Status);
            Assert.Equal(source, projection.Target!.SourceId);
            Assert.Equal("meowchan.source." + source, projection.Target.PluginOwnerId);
            Assert.False(projection.Target.IsEnabled);
            Assert.False(projection.Target.IsGranted);
            if (source == "endchan") Assert.Equal("https://endchan.net/demo/res/123.html", projection.Target.Url);
        }
        Assert.Empty(network.Requests);
        Assert.Empty(host.Plugins!.Runtime.GetSnapshots());
        Assert.Empty(host.GetView().Threads);
        Assert.Empty(host.GetView().Boards);
        var invalid = await host.ProjectExperimentalSourceActivationAsync("https://wizchan.org.attacker.invalid/demo/");
        Assert.Equal(ExperimentalSourceActivationProjectionStatus.NotRequired, invalid.Status);
        Assert.Null(invalid.Target);

        var original = (await host.ProjectExperimentalSourceActivationAsync(DesktopPluginFixtures.ThreadUrl("kohlchan"))).Target!;
        var result = await host.ActivateAndAllowExperimentalSourceAsync(original).WaitAsync(Deadline);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, result.Status);
        Assert.True(result.ActualState!.IsEffective);
        var policy = Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.SourceId == "kohlchan");
        await host.SetSourceMediaIntegrityPermissionAsync(original.PluginOwnerId, original.SourceId, false, policy.Snapshot.Revision);
        var revoked = Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.SourceId == "kohlchan");
        Assert.False(revoked.IsGranted);
        var activeStrict = await host.ProjectExperimentalSourceActivationAsync("https://kohlchan.net/demo/res/124.html");
        Assert.Equal(ExperimentalSourceActivationProjectionStatus.NotRequired, activeStrict.Status);
        Assert.Equal(original.SessionId, activeStrict.Target!.SessionId);
        Assert.Equal(original.ProfileId, activeStrict.Target.ProfileId);
        Assert.Equal(original.PluginOwnerId, activeStrict.Target.PluginOwnerId);
        Assert.Equal(original.SourceId, activeStrict.Target.SourceId);
        Assert.Equal(original.CandidateId, activeStrict.Target.CandidateId);
        Assert.Equal(original.ExecutionRevision, activeStrict.Target.ExecutionRevision);
        Assert.Equal(original.CompilationFingerprint, activeStrict.Target.CompilationFingerprint);
        Assert.Equal(original.AssemblySha256, activeStrict.Target.AssemblySha256);
        Assert.True(activeStrict.Target.IsEnabled);
        Assert.False(activeStrict.Target.IsGranted);
        Assert.Equal(revoked, Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.SourceId == "kohlchan"));
        Assert.Equal(ExperimentalSourceActivationStatus.StaleTarget,
            (await host.ActivateAndAllowExperimentalSourceAsync(original)).Status);
        // Ordinary plugin enable remains a strict path, with no implicit source grant.
        var state = await host.Plugins.ProfileStore.ReadAsync();
        await host.Plugins.ProfileStore.SetEnabledAsync("meowchan.source.wizchan", true, state.Revision);
        Assert.Equal(ExperimentalSourceActivationProjectionStatus.NotRequired,
            (await host.ProjectExperimentalSourceActivationAsync("https://wizchan.org/demo/")).Status);
        Assert.False(Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.SourceId == "wizchan").IsGranted);
        Assert.Empty(network.Requests);
        Assert.Empty(host.Plugins.Runtime.GetSnapshots());
        Assert.Empty(host.GetView().Threads);
    }

    [Fact]
    public async Task RealGrantSaveFailureHasActivePartialStateAndExplicitRetryWithoutDeadlock()
    {
        using TempDirectory profile = new();
        using DesktopPluginFixtures network = new() { RejectAll = true };
        await using DesktopSessionHost host = await StartAsync(profile.Path, network);
        var target = (await host.ProjectExperimentalSourceActivationAsync(DesktopPluginFixtures.ThreadUrl("kohlchan"))).Target!;
        string settings = Path.Combine(profile.Path, "settings.json"), retained = settings + ".retained";
        File.Move(settings, retained);
        Directory.CreateDirectory(settings); // Real Config persistence fails independently of the plugin store.
        ExperimentalSourceActivationResult result;
        try
        {
            result = await host.ActivateAndAllowExperimentalSourceAsync(target).WaitAsync(Deadline);
            Assert.Equal(ExperimentalSourceActivationStatus.GrantFailed, result.Status);
            Assert.True(result.ActualState!.IsEnabled);
            Assert.True(result.ActualState.IsActive);
            Assert.False(result.ActualState.IsGranted);
            Assert.False(result.ActualState.IsEffective);
            Assert.True(Assert.Single((await host.Plugins!.ProfileStore.ReadAsync()).Plugins, row => row.PluginId == target.PluginOwnerId).Enabled);
        }
        finally { Directory.Delete(settings); File.Move(retained, settings); }
        var retry = Assert.IsType<ExperimentalSourceActivationTarget>(result.RetryTarget);
        Assert.True(retry.IsEnabled);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded,
            (await host.ActivateAndAllowExperimentalSourceAsync(retry).WaitAsync(Deadline)).Status);
        Assert.True(Assert.Single(Config.LoadConfig(profile.Path).SourceMediaIntegrityPermissions).Value.AllowProviderSizeMd5Mismatch);
        Assert.Empty(network.Requests);
        Assert.Empty(host.GetView().Threads);
    }

    [Fact]
    public async Task PostSaveLifecycleExceptionAndMissingSelectedBuildRemainObservable()
    {
        using TempDirectory profile = new();
        using DesktopPluginFixtures network = new() { RejectAll = true };
        await using DesktopSessionHost host = await StartAsync(profile.Path, network);
        var target = (await host.ProjectExperimentalSourceActivationAsync(DesktopPluginFixtures.ThreadUrl("kohlchan"))).Target!;
        void FailCallback(PluginLifecycleChange _) => throw new IOException("Fixture callback failed after durable enable.");
        host.Plugins!.ProfileStore.LifecycleChanged += FailCallback;
        ExperimentalSourceActivationResult result;
        try { result = await host.ActivateAndAllowExperimentalSourceAsync(target).WaitAsync(Deadline); }
        finally { host.Plugins.ProfileStore.LifecycleChanged -= FailCallback; }
        Assert.Equal(ExperimentalSourceActivationStatus.ActivationFailed, result.Status);
        Assert.True(result.ActualState!.IsEnabled);
        Assert.True(result.ActualState.IsActive);
        Assert.False(result.ActualState.IsGranted);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded,
            (await host.ActivateAndAllowExperimentalSourceAsync(result.RetryTarget!).WaitAsync(Deadline)).Status);

        var endchan = (await host.ProjectExperimentalSourceActivationAsync("https://endchan.net/demo/")).Target!;
        var saved = await host.Plugins.ProfileStore.ReadAsync();
        await host.Plugins.ProfileStore.ReportActivationFailureAsync(endchan.PluginOwnerId, endchan.ExecutionRevision, "Fixture unavailable build", saved.Revision);
        var missing = await host.ProjectExperimentalSourceActivationAsync(endchan.Url);
        Assert.Equal(ExperimentalSourceActivationProjectionStatus.Unavailable, missing.Status);
        Assert.Null(missing.Target);
        Assert.Equal(ExperimentalSourceActivationStatus.StaleTarget,
            (await host.ActivateAndAllowExperimentalSourceAsync(endchan)).Status);
        Assert.Empty(network.Requests);
    }

    private static async Task<DesktopSessionHost> StartAsync(string profile, DesktopPluginFixtures fixture)
    {
        new Config { DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true,
            AutomaticUpdateChecks = false, MinimumFreeSpaceGiB = 0 }.SaveConfig(profile);
        DesktopSessionHost host = new(profile,
            updateSessionFactory: (path, config) => new UpdateSession(path, config, fixture.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: fixture.Client);
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }
}
