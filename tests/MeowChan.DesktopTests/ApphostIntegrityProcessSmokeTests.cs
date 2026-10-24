using System.Text.Json;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ApphostIntegrityProcessSmokeTests
{
    [Fact]
    public async Task OfflineTransportCountsAndRejectsEveryAttempt()
    {
        using var transport = new ApphostIntegrityProcessSmoke.FixtureHttp { Offline = true };
        using var client = transport.Client();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(ApphostIntegrityProcessSmoke.OriginalUrl));
        Assert.Single(transport.Requests);
        Assert.Equal(1, transport.Unexpected);
    }

    [Theory]
    [InlineData("revoke", "acquire")]
    [InlineData("deny", "revoke")]
    [InlineData("recovery", "deny")]
    public void RestartsRequireExactCompletedOwnedPredecessor(string phase, string predecessor)
    {
        string root = Path.Combine(Path.GetTempPath(), "meowchan-workflow-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string token = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(root, ".workflow-owner"), token);
            string profile = Path.Combine(root, "profile-download");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".workflow-profile"), token);
            string descriptor = Path.Combine(root, "download.json");
            File.WriteAllText(descriptor, JsonSerializer.Serialize(new {
                schemaVersion = 1, root, phase = "download", ownerToken = token, expectedScale = 1,
                previousProfile = (string?)null, policyPhase = phase }));
            string receipt = Path.Combine(profile, "apphost-policy-process.json");
            void Prior(string prior, bool complete = true, string? failure = null) => File.WriteAllText(receipt,
                JsonSerializer.Serialize(new { phase = prior, completed = complete, failure, profile, ownerToken = token }));
            Prior(predecessor);
            using (var parsed = ApphostIntegrityProcessSmoke.Parse(descriptor, profile)) Assert.Equal(phase, parsed.Phase);
            Prior(predecessor, complete: false);
            Assert.Throws<IOException>(() => ApphostIntegrityProcessSmoke.Parse(descriptor, profile));
            Prior(predecessor, failure: "fixture failed");
            Assert.Throws<IOException>(() => ApphostIntegrityProcessSmoke.Parse(descriptor, profile));
            Prior(phase);
            Assert.Throws<IOException>(() => ApphostIntegrityProcessSmoke.Parse(descriptor, profile));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
