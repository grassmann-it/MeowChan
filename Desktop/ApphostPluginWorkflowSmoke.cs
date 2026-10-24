using System;
using System.IO;
using System.Net.Http;
using MeowChan.Desktop.Updates;

namespace MeowChan.Desktop;

/// <summary>Opt-in packaged PluginSession acceptance; never injects a provider or opens a network transport.</summary>
internal sealed class ApphostPluginWorkflowSmoke : IDisposable
{
    internal const string Switch = "--smoke-plugin-workflow";
    internal const string SourceActivationSwitch = "--smoke-source-activation";
    internal string? SourceActivationPhase { get; }
    internal ApphostWorkflowSmoke Descriptor { get; }
    internal ApphostPluginFixtures Http { get; }
    private ApphostPluginWorkflowSmoke(ApphostWorkflowSmoke descriptor, string? sourcePhase = null)
    { Descriptor = descriptor; SourceActivationPhase = sourcePhase; Http = new() { RejectAll = sourcePhase == "offline" || sourcePhase == null && descriptor.Phase == "restore" }; }

    internal static ApphostPluginWorkflowSmoke Parse(string path, string profile, bool sourceActivation = false)
    {
        ApphostWorkflowSmoke descriptor = ApphostWorkflowSmoke.Parse(path, profile);
        if (descriptor.Phase is not ("download" or "restore"))
            throw new ArgumentException("Plugin workflow supports only download and paused restore.");
        if (!sourceActivation) return new(descriptor);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        string? phase = json.RootElement.GetProperty("sourcePhase").GetString();
        if (descriptor.Phase != "download" || descriptor.PreviousProfile != null || phase is not ("install" or "acquire" or "offline"))
            throw new ArgumentException("Source activation requires its three owned phases on the same profile.");
        bool exists = Directory.Exists(profile) || File.Exists(profile) || new DirectoryInfo(profile).LinkTarget != null;
        if (phase == "install" && exists) throw new IOException("Source activation must start with a new profile.");
        if (phase != "install")
        {
            ApphostWorkflowSmoke.ValidateProfileTree(profile);
            if (File.ReadAllText(Path.Combine(profile, ".workflow-profile")) != descriptor.OwnerToken)
                throw new IOException("Source activation profile ownership differs.");
            string priorPath = Path.Combine(profile, "apphost-workflow.json");
            FileInfo priorFile = new(priorPath);
            if (!priorFile.Exists || priorFile.LinkTarget != null || priorFile.Length is <= 0 or > 65536)
                throw new IOException("Source activation evidence is not a bounded regular file.");
            using var prior = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(priorPath));
            var evidence = prior.RootElement;
            if (evidence.GetProperty("phase").GetString() != "source-activation" || !evidence.GetProperty("completed").GetBoolean() || evidence.GetProperty("failure").ValueKind != System.Text.Json.JsonValueKind.Null
                || evidence.GetProperty("sourcePhase").GetString() != (phase == "acquire" ? "install" : "acquire"))
                throw new IOException("The preceding source activation process did not complete.");
        }
        return new(descriptor, phase);
    }

    internal DesktopSessionHost CreateSession()
    {
        var fixture = Descriptor;
        bool restart = SourceActivationPhase is "acquire" or "offline";
        if (!restart && (Directory.Exists(fixture.Profile) || File.Exists(fixture.Profile) || new DirectoryInfo(fixture.Profile).LinkTarget != null))
            throw new IOException("Plugin workflow profile already exists.");
        if (restart) ApphostWorkflowSmoke.ValidateProfileTree(fixture.Profile);
        else Directory.CreateDirectory(fixture.Profile);
        if (fixture.PreviousProfile != null)
        {
            ApphostWorkflowSmoke.ValidateProfileTree(fixture.PreviousProfile);
            foreach (string directory in Directory.EnumerateDirectories(fixture.PreviousProfile, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(fixture.Profile, Path.GetRelativePath(fixture.PreviousProfile, directory)));
            foreach (string file in Directory.EnumerateFiles(fixture.PreviousProfile, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(fixture.Profile, Path.GetRelativePath(fixture.PreviousProfile, file)));
        }
        File.WriteAllText(Path.Combine(fixture.Profile, ".workflow-profile"), fixture.OwnerToken);
        Config config = fixture.PreviousProfile == null && !restart ? new Config
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
