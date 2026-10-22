using System;
using System.IO;
using System.Net.Http;
using MeowChan.Desktop.Updates;

namespace MeowChan.Desktop;

/// <summary>Opt-in packaged PluginSession acceptance; never injects a provider or opens a network transport.</summary>
internal sealed class ApphostPluginWorkflowSmoke : IDisposable
{
    internal const string Switch = "--smoke-plugin-workflow";
    internal ApphostWorkflowSmoke Descriptor { get; }
    internal ApphostPluginFixtures Http { get; }
    private ApphostPluginWorkflowSmoke(ApphostWorkflowSmoke descriptor)
    { Descriptor = descriptor; Http = new() { RejectAll = descriptor.Phase == "restore" }; }

    internal static ApphostPluginWorkflowSmoke Parse(string path, string profile)
    {
        ApphostWorkflowSmoke descriptor = ApphostWorkflowSmoke.Parse(path, profile);
        if (descriptor.Phase is not ("download" or "restore"))
            throw new ArgumentException("Plugin workflow supports only download and paused restore.");
        return new(descriptor);
    }

    internal DesktopSessionHost CreateSession()
    {
        var fixture = Descriptor;
        if (Directory.Exists(fixture.Profile) || File.Exists(fixture.Profile) || new DirectoryInfo(fixture.Profile).LinkTarget != null)
            throw new IOException("Plugin workflow profile already exists.");
        Directory.CreateDirectory(fixture.Profile);
        if (fixture.PreviousProfile != null)
        {
            ApphostWorkflowSmoke.ValidateProfileTree(fixture.PreviousProfile);
            foreach (string directory in Directory.EnumerateDirectories(fixture.PreviousProfile, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(fixture.Profile, Path.GetRelativePath(fixture.PreviousProfile, directory)));
            foreach (string file in Directory.EnumerateFiles(fixture.PreviousProfile, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(fixture.Profile, Path.GetRelativePath(fixture.PreviousProfile, file)));
        }
        File.WriteAllText(Path.Combine(fixture.Profile, ".workflow-profile"), fixture.OwnerToken);
        Config config = fixture.PreviousProfile == null ? new Config
        {
            DownloadPath = fixture.DownloadRoot, DownloadsPaused = true, SaveUrlsOnExit = true,
            AutomaticUpdateChecks = false, MaxParallelDownloads = 2, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0,
            WorkspaceMode = "Barebones", Theme = "Dark", Language = "en", MinimizeToTray = false, CloseToTray = false
        } : Config.LoadConfig(fixture.Profile);
        if (!ApphostWorkflowSmoke.SamePath(config.DownloadPath, fixture.DownloadRoot) || !config.DownloadsPaused
            || config.AutomaticUpdateChecks || config.MinimizeToTray || config.CloseToTray)
            throw new IOException("Plugin workflow settings escaped the owned paused fixture.");
        config.SaveConfig(fixture.Profile);
        return new DesktopSessionHost(fixture.Profile, providers: null,
            updateSessionFactory: (path, settings) => new UpdateSession(path, settings, Http.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: Http.Client);
    }
    public void Dispose() { Http.Dispose(); Descriptor.Dispose(); }
}
