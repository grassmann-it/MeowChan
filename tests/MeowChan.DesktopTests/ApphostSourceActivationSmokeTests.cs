using System.Text.Json;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ApphostSourceActivationSmokeTests
{
    [Fact]
    public void SourceActivationRequiresNewOwnedInstallProfile()
    {
        using OwnedRoot root = new();
        using var parsed = ApphostPluginWorkflowSmoke.Parse(root.Descriptor("install"), root.Profile, sourceActivation: true);
        Assert.Equal("install", parsed.SourceActivationPhase);
        Assert.False(Directory.Exists(root.Profile));
        Directory.CreateDirectory(root.Profile);
        Assert.Throws<IOException>(() => ApphostPluginWorkflowSmoke.Parse(root.Descriptor("install"), root.Profile, sourceActivation: true));
    }

    [Theory]
    [InlineData("acquire", "install")]
    [InlineData("offline", "acquire")]
    public void RestartRequiresExactSuccessfulPredecessorAndSameOwnership(string phase, string predecessor)
    {
        using OwnedRoot root = new();
        Directory.CreateDirectory(root.Profile);
        File.WriteAllText(Path.Combine(root.Profile, ".workflow-profile"), root.Token);
        root.Evidence(predecessor, completed: true);
        using var parsed = ApphostPluginWorkflowSmoke.Parse(root.Descriptor(phase), root.Profile, sourceActivation: true);
        Assert.Equal(phase, parsed.SourceActivationPhase);
        Assert.Equal(phase == "offline", parsed.Http.RejectAll);
        root.Evidence(predecessor, completed: false);
        Assert.Throws<IOException>(() => ApphostPluginWorkflowSmoke.Parse(root.Descriptor(phase), root.Profile, sourceActivation: true));
        root.Evidence(phase, completed: true);
        Assert.Throws<IOException>(() => ApphostPluginWorkflowSmoke.Parse(root.Descriptor(phase), root.Profile, sourceActivation: true));
        root.Evidence(predecessor, completed: true);
        File.WriteAllText(Path.Combine(root.Profile, ".workflow-profile"), "foreign");
        Assert.Throws<IOException>(() => ApphostPluginWorkflowSmoke.Parse(root.Descriptor(phase), root.Profile, sourceActivation: true));
    }

    [Fact]
    public void RestartRejectsOversizedPriorEvidence()
    {
        using OwnedRoot root = new();
        Directory.CreateDirectory(root.Profile);
        File.WriteAllText(Path.Combine(root.Profile, ".workflow-profile"), root.Token);
        File.WriteAllText(Path.Combine(root.Profile, "apphost-workflow.json"), new string(' ', 65537));
        Assert.Throws<IOException>(() => ApphostPluginWorkflowSmoke.Parse(root.Descriptor("acquire"), root.Profile, sourceActivation: true));
    }

    private sealed class OwnedRoot : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "meowchan-workflow-author-tests-" + Guid.NewGuid().ToString("N"));
        internal string Token { get; } = Guid.NewGuid().ToString("N");
        internal string Profile => Path.Combine(_root, "profile-download");
        internal OwnedRoot()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, ".workflow-owner"), Token);
        }
        internal string Descriptor(string sourcePhase)
        {
            string path = Path.Combine(_root, "download.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            { schemaVersion = 1, root = _root, phase = "download", sourcePhase, ownerToken = Token, expectedScale = 1, previousProfile = (string?)null }));
            return path;
        }
        internal void Evidence(string sourcePhase, bool completed) => File.WriteAllText(Path.Combine(Profile, "apphost-workflow.json"),
            JsonSerializer.Serialize(new { phase = "source-activation", sourcePhase, completed, failure = (string?)null }));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
