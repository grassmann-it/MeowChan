using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginActionTargetTests
{
    [Fact]
    public void EquivalentRefreshRetainsTheCapturedTargetWithoutObjectIdentity()
    {
        PluginCatalogEntry original = Entry();
        PluginCatalogEntry refreshed = Entry();
        Assert.NotSame(original, refreshed);
        Assert.True(PluginActionTarget.Capture(original, 7).Matches(refreshed, 7));
    }

    [Fact]
    public void SamePathAndVersionCannotSubstituteAnotherBuild()
    {
        PluginCatalogEntry entry = Entry();
        PluginActionTarget captured = PluginActionTarget.Capture(entry, 7);
        PluginCatalogEntry replaced = entry with { Candidate = entry.Candidate! with {
            Artifact = entry.Candidate.Artifact! with { ExecutionRevision = new string('f', 64) } } };
        Assert.Equal(entry.Key, replaced.Key);
        Assert.Equal(entry.Description!.PluginVersion, replaced.Description!.PluginVersion);
        Assert.False(captured.Matches(replaced, 7));
    }

    [Fact]
    public void ChangedSettingsPendingPinOrRollbackCannotReuseAnOldMenuTarget()
    {
        PluginCatalogEntry entry = Entry();
        PluginActionTarget captured = PluginActionTarget.Capture(entry, 7);
        PluginReleaseSnapshot next = entry.Activation!.Active! with {
            Configuration = entry.Activation.Active.Configuration with { Revision = "different-configuration" } };
        PluginActivationSnapshot[] changed = {
            entry.Activation with { Active = next },
            entry.Activation with { Pending = next },
            entry.Activation with { Pinned = next },
            entry.Activation with { LastGood = next }
        };
        Assert.All(changed, activation => Assert.False(captured.Matches(entry with { Activation = activation }, 7)));
    }

    [Fact]
    public void ChangedProfileLifecycleOrOwnershipRequiresANewIntent()
    {
        PluginCatalogEntry entry = Entry();
        PluginActionTarget captured = PluginActionTarget.Capture(entry, 7);
        Assert.False(captured.Matches(entry, 8));
        Assert.False(captured.Matches(entry with { PluginId = "another.plugin" }, 7));
        Assert.False(captured.Matches(entry with { Key = "another-candidate" }, 7));
        Assert.False(captured.Matches(entry with { CandidateId = "another-candidate" }, 7));
        Assert.False(captured.Matches(entry with { Activation = entry.Activation! with { Enabled = false } }, 7));
        Assert.False(captured.Matches(entry with { Activation = entry.Activation! with { LifecycleRevision = 3 } }, 7));
        Assert.False(captured.Matches(entry with { Activation = entry.Activation! with { SelectedCandidateId = "another-candidate" } }, 7));
    }

    [Fact]
    public void MissingOrFailedCandidateCannotConsumeThePreviousValidIntent()
    {
        PluginCatalogEntry entry = Entry();
        PluginActionTarget captured = PluginActionTarget.Capture(entry, 7);
        Assert.False(captured.Matches(entry with { Candidate = entry.Candidate! with { IsPresent = false } }, 7));
        Assert.False(captured.Matches(entry with { Candidate = entry.Candidate! with { Problem = "update failed" } }, 7));
        Assert.False(captured.Matches(entry with { Candidate = null }, 7));
    }

    [Fact]
    public void DisplayTextChangesDoNotBecomeASecondActionIdentity()
    {
        PluginCatalogEntry entry = Entry();
        PluginActionTarget captured = PluginActionTarget.Capture(entry, 7);
        PluginDescription localized = entry.Description! with { Name = "Anderer Anzeigename", Description = "Original text ü" };
        Assert.True(captured.Matches(entry with { Description = localized,
            Diagnostics = new[] { new PluginBuildDiagnostic("information", "Retained text changed") } }, 7));
    }

    private static PluginCatalogEntry Entry()
    {
        SettingsSchema empty = new("1", Array.Empty<SettingDefinition>());
        PluginDescription description = new("arbitrary.plugin", "Useful plugin", "1.0.0", SdkContract.Version,
            "An original description", Array.Empty<SourceDescription>(), Array.Empty<FilterDescription>(), empty,
            Array.Empty<InformationEntry>());
        string hash = new('a', 64);
        PluginArtifact artifact = new(hash, hash, "/fixture/plugin.dll", hash, hash, hash, Array.Empty<PluginArtifactResource>());
        PluginConfiguration configuration = new("configuration-a", new("1", Array.Empty<SettingValue>()),
            new Dictionary<string, SettingsSnapshot>());
        PluginReleaseSnapshot active = new("candidate-a", artifact, description, configuration);
        PluginCandidateSnapshot candidate = new("candidate-a", description.PluginId, artifact, description, true, null, "/fixture/plugin.cs");
        PluginActivationSnapshot activation = new(description.PluginId, true, 2, candidate.CandidateId,
            active, null, null, null, new[] { candidate }, null);
        return new(candidate.CandidateId, description.PluginId, candidate.CandidateId, candidate.CandidatePath,
            description, activation, candidate, Array.Empty<PluginBuildDiagnostic>());
    }
}
