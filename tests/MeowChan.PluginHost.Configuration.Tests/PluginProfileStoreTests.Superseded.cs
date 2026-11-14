using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

// An upgrade replaces bundled single-site plugins by one bundled family plugin with the same source IDs.
public sealed partial class PluginProfileStoreTests
{
    private static readonly IReadOnlyDictionary<string, string> Replaced = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["site.a"] = "family", ["site.b"] = "family" };

    private PluginProfileStore FamilyStore(bool table = true) => new(profile) { SupersededBundledPlugins = table ? Replaced : new Dictionary<string, string>() };

    [Fact]
    public async Task ADeliveredFamilyReplacesItsSingleSiteBundlesOnTheFirstStartWithoutTouchingTheirRecords()
    {
        var a = await DeliveredSource("release-1", "a1", "site.a", "a.plugin.json", "a");
        var b = await DeliveredSource("release-1", "b1", "site.b", "b.plugin.json", "b");
        var state = await FamilyStore().ApplyBundledStartupAsync([a.Candidate, b.Candidate], [a.Proof, b.Proof], [], 0);
        state = await FamilyStore().SetEnabledAsync("site.b", false, state.Revision);
        PluginActivationSnapshot oldA = state.Plugins.Single(p => p.PluginId == "site.a"), oldB = state.Plugins.Single(p => p.PluginId == "site.b");
        Assert.NotNull(oldA.Active); Assert.NotNull(oldB.Active);
        byte[] owners = SdkJson.Serialize(state.SelectedSourceOwners), grants = SdkJson.Serialize(state.Grants);

        // The new release ships only the family; its candidates live elsewhere and the old bundles are gone.
        var family = await DeliveredSource("release-2", "f1", "family", "family.plugin.json", "a", "b", "c");
        for (int restart = 0; restart < 2; restart++)
        {
            state = await FamilyStore().ApplyBundledStartupAsync([family.Candidate], [family.Proof], [], state.Revision);
            PluginActivationSnapshot successor = state.Plugins.Single(p => p.PluginId == "family");
            Assert.Null(successor.Problem);
            Assert.Equal(new[] { "a", "b", "c" }, successor.Active!.Description.Sources.Select(s => s.SourceId));
            foreach ((string id, PluginActivationSnapshot before) in new[] { ("site.a", oldA), ("site.b", oldB) })
            {
                PluginActivationSnapshot now = state.Plugins.Single(p => p.PluginId == id);
                // Never rewritten or deleted: choice, enabled state and history stay for an older release to find.
                Assert.Equal(before.Enabled, now.Enabled);
                Assert.Equal(before.SelectedCandidateId, now.SelectedCandidateId);
                Assert.Equal(before.Active!.ExecutionRevision, now.LastGood!.ExecutionRevision);
                Assert.Null(now.Active);
                Assert.Contains("Replaced by the bundled plugin 'family'", now.Problem);
            }
            Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(new[] { "site.a", "site.b" }, SupersededBundledPlugins.InProfile(state, Replaced).Order(StringComparer.Ordinal));
            // A replaced identity is not offered as a broken plugin to repair or to choose a new build for.
            Assert.DoesNotContain((await FamilyStore().PreviewBundledRepairsAsync()).Rows, row => row.PluginId.StartsWith("site.", StringComparison.Ordinal));
            Assert.DoesNotContain((await FamilyStore().PreviewBundledReselectionsAsync()).Rows, row => row.PluginId.StartsWith("site.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task WithoutTheHostTableTheOldOwnersStillBlockTheFamily()
    {
        var a = await DeliveredSource("release-1", "a1", "site.a", "a.plugin.json", "a");
        var state = await FamilyStore(table: false).ApplyBundledStartupAsync([a.Candidate], [a.Proof], [], 0);
        var family = await DeliveredSource("release-2", "f1", "family", "family.plugin.json", "a", "b");
        state = await FamilyStore(table: false).ApplyBundledStartupAsync([family.Candidate], [family.Proof], [], state.Revision);
        PluginActivationSnapshot successor = state.Plugins.Single(p => p.PluginId == "family");
        Assert.Null(successor.Active);
        Assert.Contains("Source identity conflict", successor.Problem);
        Assert.Empty(SupersededBundledPlugins.InProfile(state, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOldSingleSitePluginThatIsStillInstalledIsNeverReplacedAndNeverSharesItsSource(bool delivered)
    {
        // Old bundle still delivered beside the family, or a user's own local copy of the old file: both keep the
        // old identity. The family must not take over silently, and no source may end up with two active owners.
        var a = await DeliveredSource("release-1", "a1", "site.a", "a.plugin.json", "a");
        var state = await FamilyStore().ApplyBundledStartupAsync([a.Candidate], [a.Proof], [], 0);
        var family = await DeliveredSource("release-2", "f1", "family", "family.plugin.json", "a", "b");
        DeliveredCandidateProvenance[] proofs = delivered ? [a.Proof, family.Proof] : [family.Proof];
        for (int restart = 0; restart < 2; restart++)
        {
            state = await FamilyStore().ApplyBundledStartupAsync([a.Candidate, family.Candidate], proofs, [], state.Revision);
            PluginActivationSnapshot old = state.Plugins.Single(p => p.PluginId == "site.a");
            PluginActivationSnapshot successor = state.Plugins.Single(p => p.PluginId == "family");
            Assert.NotNull(old.Active);
            Assert.Null(old.Problem);
            Assert.Null(successor.Active);
            Assert.Contains("Source identity conflict requires explicit selection: a", successor.Problem);
            Assert.Empty(SupersededBundledPlugins.InProfile(state, Replaced));
            Assert.Single(state.Plugins.Where(p => p.Active?.Description.Sources.Any(s => s.SourceId == "a") == true));
        }
    }

    [Fact]
    public async Task OnlyADeliveredSuccessorThatServesEveryOldSourceReplacesAnIdentity()
    {
        var a = await DeliveredSource("release-1", "a1", "site.a", "a.plugin.json", "a", "a2");
        var initial = await FamilyStore().ApplyBundledStartupAsync([a.Candidate], [a.Proof], [], 0);

        // A family that lacks one of the old sources does not replace the old identity.
        var partial = await DeliveredSource("release-2", "p1", "family", "family.plugin.json", "a", "b");
        var state = await FamilyStore().ApplyBundledStartupAsync([partial.Candidate], [partial.Proof], [], initial.Revision);
        Assert.Contains("Source identity conflict", state.Plugins.Single(p => p.PluginId == "family").Problem);
        Assert.DoesNotContain("Replaced", state.Plugins.Single(p => p.PluginId == "site.a").Problem ?? string.Empty);

        // A complete family that is only a local, undelivered candidate does not replace it either.
        var local = await DeliveredSource("release-3", "l1", "family", "family.plugin.json", "a", "a2", "b");
        state = await FamilyStore().ApplyBundledStartupAsync([local.Candidate], [], [], state.Revision);
        Assert.Null(state.Plugins.Single(p => p.PluginId == "family").Active);
        Assert.Empty(SupersededBundledPlugins.InProfile(state, Replaced));
    }

    private async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> DeliveredSource(string installation,
        string build, string pluginId, string fileName, params string[] sourceIds)
    {
        string root = Path.Combine(profile, installation, "plugins"); Directory.CreateDirectory(root);
        string assemblyName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fileName)) + ".dll";
        byte[] assembly = Encoding.UTF8.GetBytes("synthetic offline assembly " + pluginId + " " + build);
        string sha = Convert.ToHexStringLower(SHA256.HashData(assembly));
        string sdk = new('b', 64), compiler = new('c', 64);
        string compilation = FixtureHashParts("prebuilt-v1", sha, sdk, compiler);
        string execution = FixtureHashParts("artifact-v1", compilation);
        string cache = Path.Combine(profile, "test-artifacts", execution); Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(root, assemblyName), assembly);
        await File.WriteAllBytesAsync(Path.Combine(cache, "plugin.dll"), assembly);
        await File.WriteAllTextAsync(Path.Combine(root, fileName), JsonSerializer.Serialize(new PluginBundleManifest(1, assemblyName, sha, sdk, compiler, [])));
        var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(root)).Candidates,
            c => c.Kind == PluginCandidateKind.PrebuiltManifest && c.Path.EndsWith(fileName, StringComparison.Ordinal));
        PluginArtifact artifact = new(compilation, execution, Path.Combine(cache, "plugin.dll"), sha, sdk, compiler, [], cache);
        PluginDescription description = new(pluginId, "Synthetic " + pluginId, "1.0", SdkContract.Version, "Synthetic source plugin",
            sourceIds.Select(id => new SourceDescription(id, "Site " + id, new("https://" + id + ".test/"), SourceCapabilities.ThreadRead,
                [new(SourceResourceKind.Thread, id + ".test", "/{board}/res/{thread}.html", IsCanonical: true)],
                [new(id + ".test", HttpHostPurpose.Metadata)], Empty)).ToArray(), [], Empty, []);
        PluginCandidateObservation candidate = new(installed.CandidateId, pluginId, artifact, description, CandidatePath: installed.Path);
        var proof = await PluginInstallationInventory.RecordDeliveredAsync(root, installed, artifact, description);
        return (candidate, proof);
    }
}
