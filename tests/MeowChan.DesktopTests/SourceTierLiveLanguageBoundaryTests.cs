using System;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SourceTierLiveLanguageBoundaryTests : IDisposable
{
    public SourceTierLiveLanguageBoundaryTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    [Theory]
    [InlineData("es", "Adaptador de fuente 4chan:", "Terceros · Estable", "MeowChan · experimental",
        "Fuente Same (stable): mantenimiento Terceros · madurez Estable", "Ninguna etiqueta implica aprobación del operador")]
    [InlineData("fr", "Adaptateur de source 4chan :", "Tiers · Stable", "MeowChan · experimental",
        "Source Same (stable) : responsable Tiers · maturité Stable", "Aucune de ces étiquettes ne vaut approbation de l’exploitant")]
    [InlineData("ru", "Адаптер источника 4chan:", "Сторонний · Стабильный", "MeowChan · experimental",
        "Источник Same (stable): сопровождающий Сторонний · зрелость Стабильный", "Ни одна метка не означает одобрение оператором")]
    public void MaterializedCatalogPresentationReformatsWithoutChangingEvidenceOrProviderMetadata(
        string language, string prefix, string stable, string experimental, string detailedStable, string disclaimer)
    {
        SourceDescription first = Source("stable", SourceMaturity.Unspecified);
        SourceDescription second = Source("experimental", SourceMaturity.Experimental);
        PluginDescription description = new("meowchan.official.4chan", "4chan", "1.0.0", SdkContract.Version,
            "Official 4chan source: retained provider text.", [first, second], [], new("1", []), []);
        string fingerprint = SourceWording.DescriptionFingerprint(description);
        var artifact = new PluginArtifact("compile", "execution", "/fixture/plugin.dll", "assembly", "sdk", "compiler", []);
        var candidate = new PluginCandidateSnapshot("candidate", description.PluginId, artifact, description,
            true, null, "/fixture/plugin.cs");
        var entry = new PluginCatalogEntry("candidate", description.PluginId, "candidate", "/fixture/plugin.cs",
            description, null, candidate, []);
        PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry,
            maintenanceEvidence:
            [
                new(description.PluginId, first.SourceId, "execution", fingerprint, SourceWordingMaintenance.ThirdParty),
                new(description.PluginId, second.SourceId, "execution", fingerprint, SourceWordingMaintenance.MeowChan)
            ],
            maturityEvidence: [new(description.PluginId, first.SourceId, "execution", fingerprint, SourceWordingMaturity.Stable)]);
        var classifications = presentation.SourceClassifications;
        string english = PluginContentPresentation.DescribeDetailedSourceClassifications(presentation);

        // Reuse the same catalog projection across switches, including a return trip.
        foreach (string next in new[] { language, "en", language })
        {
            Texts.SetLanguage(next);
            if (next == "en")
            {
                Assert.Equal(english, PluginContentPresentation.DescribeDetailedSourceClassifications(presentation));
                continue;
            }
            Assert.Equal(prefix + " retained provider text.", PluginContentPresentation.DescribeDescription(presentation));
            string compact = PluginContentPresentation.DescribeSourceClassifications(presentation);
            Assert.Contains(stable, compact, StringComparison.Ordinal);
            Assert.Contains(experimental, compact, StringComparison.Ordinal);
            string detailed = PluginContentPresentation.DescribeDetailedSourceClassifications(presentation);
            Assert.Contains(detailedStable, detailed, StringComparison.Ordinal);
            Assert.Contains("(experimental)", detailed, StringComparison.Ordinal);
            Assert.NotEqual(english, detailed);
            Assert.Contains(disclaimer, SourceWording.Describe(classifications[0]), StringComparison.Ordinal);
            Assert.Same(classifications, presentation.SourceClassifications);
            Assert.Same(description, presentation.DescriptionMetadata);
            Assert.Equal(fingerprint, SourceWording.DescriptionFingerprint(description));
            Assert.Equal(SourceWordingMaintenance.ThirdParty, classifications[0].Maintenance);
            Assert.Equal(SourceWordingMaturity.Stable, classifications[0].Maturity);
            Assert.Equal(SourceWordingMaintenance.MeowChan, classifications[1].Maintenance);
            Assert.Equal(SourceWordingMaturity.Experimental, classifications[1].Maturity);
            Assert.True(presentation.HasExperimentalSources);
        }
    }

    [Theory]
    [InlineData("es", "Mantenimiento sin verificar · Sin clasificar")]
    [InlineData("fr", "Maintenance non vérifiée · Non classé")]
    [InlineData("ru", "Сопровождение не подтверждено · Без классификации")]
    public void LegacyOfficialIdentityDoesNotBecomeMaintenanceOrStableEvidenceAfterSwitch(string language, string expected)
    {
        SourceDescription source = Source("4chan", SourceMaturity.Unspecified);
        PluginDescription description = new("meowchan.official.4chan", "4chan", "1.0.0", SdkContract.Version,
            "Official 4chan source: retained provider text.", [source], [], new("1", []), []);
        SourceWordingClassification classification = SourceWording.Classify(description, source);
        Texts.SetLanguage(language);
        Assert.Equal(expected, SourceWording.DescribeLabels(classification));
        // Only host-rendered legacy UI prefix is normalized, not arbitrary documentation or provider text.
        Assert.DoesNotContain("Official 4chan source:", PluginContentPresentation.DescribeDescription(description), StringComparison.Ordinal);
        Assert.Equal("Official 4chan source: retained provider text.", description.Description);
        Texts.SetLanguage("en");
        Assert.Equal("Maintenance unverified · Unclassified", SourceWording.DescribeLabels(classification));
    }

    private static SourceDescription Source(string id, SourceMaturity maturity) =>
        new(id, "Same", new Uri("https://provider.invalid/"), SourceCapabilities.ThreadRead, [], [], new("1", []))
        { Maturity = maturity };
}
