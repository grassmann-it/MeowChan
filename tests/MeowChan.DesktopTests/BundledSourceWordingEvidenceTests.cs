using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BundledSourceWordingEvidenceTests : IDisposable
{
    private const string ExactTier = "MeowChan · experimental";

    public BundledSourceWordingEvidenceTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    public static TheoryData<string, string> SevenBundledChanPlugins => new()
    {
        { "meowchan.source.sevenchan", "MeowChan.Plugin.SevenChan" },
        { "meowchan.source.bbwchan", "MeowChan.Plugin.BbwChan" },
        { "meowchan.source.anonimageboards", "MeowChan.Plugin.AnonImageboards" },
        { "meowchan.source.eightchanmoe", "MeowChan.Plugin.EightchanMoe" },
        { "meowchan.source.gurocx", "MeowChan.Plugin.GuroCx" },
        { "meowchan.source.fchan", "MeowChan.Plugin.Fchan" },
        { "meowchan.source.capybarachan", "MeowChan.Plugin.CapybaraChan" }
    };

    [Theory]
    [MemberData(nameof(SevenBundledChanPlugins))]
    public void ActualBundledChanDescriptionsRenderTheExactMeowChanExperimentalTier(
        string pluginId, string assemblyName)
    {
        PluginDescription description = DeliveredDescription(pluginId, assemblyName);
        SourceWordingClassification classification = Assert.Single(
            SourceWordingEvidence.DisplayClassifications(description, "exact-execution"));

        Assert.Equal(SourceWordingMaintenance.MeowChan, classification.Maintenance);
        Assert.Equal(SourceWordingMaturity.Experimental, classification.Maturity);
        Assert.Equal(ExactTier, SourceWording.DescribeLabels(classification));
        string details = SourceWording.Describe(classification);
        Assert.Contains(ExactTier, details, StringComparison.Ordinal);
        Assert.Contains("not website-operator approval", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Third-party", SourceWording.DescribeLabels(classification), StringComparison.Ordinal);
        Assert.DoesNotContain("Maintenance unverified", SourceWording.DescribeLabels(classification), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SevenBundledChanPlugins))]
    public void PluginPresentationCarriesTheExactTierForRegisteredAndReservedAdapters(
        string pluginId, string assemblyName)
    {
        PluginDescription description = DeliveredDescription(pluginId, assemblyName);
        PluginCatalogEntry entry = Entry(description);

        SourceWordingClassification classification = Assert.Single(PluginEntryPresentation.Create(entry).SourceClassifications);

        Assert.Equal(ExactTier, SourceWording.DescribeLabels(classification));
    }

    [Fact]
    public void SourcesProjectionUsesHostClassificationAndRetainsGenericExperimentalFallback()
    {
        PluginDescription description = DeliveredDescription("meowchan.source.sevenchan", "MeowChan.Plugin.SevenChan");
        SourceDescription source = Assert.Single(description.Sources);
        SourceDescription other = Source("fixture.other", "Other", SourceMaturity.Experimental);
        SourceWordingClassification bundledClassification =
            SourceWordingEvidence.Classify(description, source, "exact-execution");
        SourceMediaIntegrityPolicyView bundledPolicy = Policy(
            description.PluginId, source.SourceId, bundledClassification, source.Maturity);
        SourceMediaIntegrityPolicyView otherPolicy = Policy("fixture.owner", other.SourceId, null, other.Maturity);
        DesktopSessionView view = new([], [], [], DesktopSessionState.Ready, null);

        SourceRow bundledRow = Assert.Single(SourcesSnapshot.From([Provider(source)], view, [bundledPolicy]));
        SourceRow otherRow = Assert.Single(SourcesSnapshot.From([Provider(other)], view, [otherPolicy]));

        Assert.Equal(ExactTier, SourceWording.DescribeLabels(bundledRow.Classification!));
        Assert.Equal(SourceWordingMaintenance.Unknown, otherRow.Classification!.Maintenance);
        Assert.Equal(SourceWordingMaturity.Experimental, otherRow.Classification.Maturity);
    }

    [Fact]
    public void ChangedBundledDescriptionCannotInheritTheMeowChanTier()
    {
        PluginDescription description = DeliveredDescription("meowchan.source.sevenchan", "MeowChan.Plugin.SevenChan")
            with { Description = "Locally changed description." };

        SourceWordingClassification classification = Assert.Single(
            SourceWordingEvidence.DisplayClassifications(description, "changed-execution"));

        Assert.Equal(SourceWordingMaintenance.Unknown, classification.Maintenance);
        Assert.Equal(SourceWordingMaturity.Experimental, classification.Maturity);
        Assert.NotEqual(ExactTier, SourceWording.DescribeLabels(classification));
    }

    [Fact]
    public void GenuineExternalHostEvidenceStillRendersThirdPartyInsteadOfMeowChan()
    {
        SourceDescription source = Source("external-source", "External source", SourceMaturity.Experimental);
        PluginDescription description = new("vendor.external.source", "External source", "1.0.0",
            SdkContract.Version, "Genuine external fixture.", [source], [], new("1", []), []);
        SourceWordingMaintenanceEvidence evidence = new(
            description.PluginId, source.SourceId, "exact-execution",
            SourceWording.DescriptionFingerprint(description), SourceWordingMaintenance.ThirdParty);

        SourceWordingClassification classification = Assert.Single(PluginEntryPresentation.Create(
            Entry(description), maintenanceEvidence: [evidence]).SourceClassifications);

        Assert.Equal(SourceWordingMaintenance.ThirdParty, classification.Maintenance);
        Assert.Equal(SourceWordingMaturity.Experimental, classification.Maturity);
        Assert.Equal("Third-party · Experimental", SourceWording.DescribeLabels(classification));
    }

    private static PluginDescription DeliveredDescription(string pluginId, string assemblyName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "plugins", "assemblies", assemblyName + ".dll");
        Assembly assembly = Assembly.LoadFrom(path);
        Type type = Assert.Single(assembly.GetExportedTypes(),
            candidate => typeof(IPlugin).IsAssignableFrom(candidate) && candidate.Name.EndsWith("Plugin", StringComparison.Ordinal));
        IPlugin plugin = Assert.IsAssignableFrom<IPlugin>(Activator.CreateInstance(type));
        PluginDescription description = plugin.Describe();
        Assert.Equal(pluginId, description.PluginId);
        return description;
    }

    private static PluginCatalogEntry Entry(PluginDescription description)
    {
        PluginArtifact artifact = new("compile-" + description.PluginId, "exact-execution",
            "/fixture/" + description.PluginId + ".dll", "assembly-hash", "sdk-hash", "compiler-hash", []);
        PluginCandidateSnapshot candidate = new("candidate-" + description.PluginId, description.PluginId,
            artifact, description, true, null, "/fixture/" + description.PluginId + ".cs");
        PluginActivationSnapshot activation = new(description.PluginId, true, 1, candidate.CandidateId,
            new(candidate.CandidateId, artifact, description,
                new("active-config", new("1", []), new Dictionary<string, SettingsSnapshot>())),
            null, null, null, [candidate], null);
        return new(candidate.CandidateId, description.PluginId, candidate.CandidateId,
            "/fixture/" + description.PluginId + ".cs", description, activation, candidate, []);
    }

    private static SourceDescription Source(string id, string name, SourceMaturity maturity) =>
        new(id, name, new Uri("https://fixture.invalid/"), SourceCapabilities.ThreadRead, [], [], new("1", []))
        { Maturity = maturity };

    private static ProviderDescriptor Provider(SourceDescription source) =>
        new(source.SourceId, source.Name, source.BaseUri, (ProviderCapabilities)(int)source.Capabilities,
            source.HttpHosts.Select(host => host.Host).ToArray(), "1.0.0", CaptureAvailable: false);

    private static SourceMediaIntegrityPolicyView Policy(string ownerId, string sourceId,
        SourceWordingClassification? classification, SourceMaturity maturity = SourceMaturity.Unspecified) =>
        new(new("profile", ownerId, sourceId, false, 0), false, false, maturity, classification);
}
