using System;
using System.Collections.Generic;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SourceWordingTests : IDisposable
{
    public SourceWordingTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    [Theory]
    [InlineData("meowchan.official.4chan", "4chan", "4chan", "Official 4chan source: catalog, captured posts and original media metadata.")]
    [InlineData("meowchan.official.8kun", "8kun", "8kun", "Official 8kun source: catalog, captured posts and original media metadata.")]
    public void LegacyOperatorWordingIsLocalizedAndNoProvenanceIsInferred(
        string pluginId, string pluginName, string sourceId, string retainedDescription)
    {
        SourceDescription source = Source(sourceId, sourceId);
        PluginDescription plugin = Plugin(pluginId, pluginName, retainedDescription, source);

        string fingerprintBefore = SourceWording.DescriptionFingerprint(plugin);
        SourceWordingClassification classification = SourceWording.Classify(plugin, source);
        string english = PluginContentPresentation.DescribeDescription(plugin);

        Assert.Equal(SourceWordingMaintenance.Unknown, classification.Maintenance);
        Assert.Equal(SourceWordingMaturity.Unclassified, classification.Maturity);
        Assert.StartsWith("Source adapter " + pluginName + ":", english, StringComparison.Ordinal);
        Assert.DoesNotContain("Official", english, StringComparison.OrdinalIgnoreCase);

        Texts.SetLanguage("de");
        string german = PluginContentPresentation.DescribeDescription(plugin);
        Assert.StartsWith("Quelladapter " + pluginName + ":", german, StringComparison.Ordinal);
        Assert.DoesNotContain("Official", german, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(retainedDescription, plugin.Description);
        Assert.Equal(pluginId, plugin.PluginId);
        Assert.Equal(sourceId, source.SourceId);
        Assert.Equal(fingerprintBefore, SourceWording.DescriptionFingerprint(plugin));

        PluginDescription nonmatching = plugin with
        {
            Description = "Official other source: this exact plugin-name prefix is required."
        };
        Assert.Equal(nonmatching.Description, SourceWording.DisplayDescription(nonmatching));
        PluginDescription filterOnly = Plugin("fixture.filter", pluginName,
            "Official " + pluginName + " source: filters never use the source-prefix rule");
        Assert.Equal(filterOnly.Description, SourceWording.DisplayDescription(filterOnly));
    }

    [Fact]
    public void ExactHostEvidenceCanCarryIndependentThirdPartyAndStableLabels()
    {
        PluginDescription plugin = Plugin("meowchan.official.4chan", "4chan",
            "Fixture retained description", Source("4chan", "4chan"));
        SourceDescription source = plugin.Sources[0];
        string execution = "exact-execution";
        string descriptionSha256 = SourceWording.DescriptionFingerprint(plugin);
        SourceWordingMaintenanceEvidence maintenance = new(
            plugin.PluginId, source.SourceId, execution, descriptionSha256, SourceWordingMaintenance.ThirdParty);
        SourceWordingMaturityEvidence maturity = new(
            plugin.PluginId, source.SourceId, execution, descriptionSha256, SourceWordingMaturity.Stable);

        SourceWordingClassification classification = SourceWording.Classify(plugin, source, execution, [maintenance], [maturity]);

        Assert.Equal(SourceWordingMaintenance.ThirdParty, classification.Maintenance);
        Assert.Equal(SourceWordingMaturity.Stable, classification.Maturity);
    }

    [Fact]
    public void SpoofedIdLocalBuildOrChangedDescriptionDoesNotReuseMaintenanceEvidence()
    {
        PluginDescription plugin = Plugin("meowchan.official.4chan", "4chan",
            "Fixture retained description", Source("4chan", "4chan"));
        SourceDescription source = plugin.Sources[0];
        string execution = "exact-execution";
        string descriptionSha256 = SourceWording.DescriptionFingerprint(plugin);
        SourceWordingMaintenanceEvidence maintenance = new(
            plugin.PluginId, source.SourceId, execution, descriptionSha256, SourceWordingMaintenance.MeowChan);
        SourceWordingMaturityEvidence maturity = new(
            plugin.PluginId, source.SourceId, execution, descriptionSha256, SourceWordingMaturity.Stable);

        AssertUnknown(SourceWording.Classify(plugin, source), SourceWordingMaturity.Unclassified);
        AssertUnknown(SourceWording.Classify(plugin, source, "changed-execution", [maintenance], [maturity]),
            SourceWordingMaturity.Unclassified);

        PluginDescription changed = plugin with { Description = "Locally changed description." };
        string changedHash = SourceWording.DescriptionFingerprint(changed);
        Assert.NotEqual(descriptionSha256, changedHash);
        AssertUnknown(SourceWording.Classify(changed, source, execution, [maintenance], [maturity]),
            SourceWordingMaturity.Unclassified);

        SourceWordingMaintenanceEvidence conflicting = new(
            plugin.PluginId, source.SourceId, execution, descriptionSha256, SourceWordingMaintenance.ThirdParty);
        SourceWordingClassification conflictingMaintenance = SourceWording.Classify(plugin, source,
            execution, [maintenance, conflicting], [maturity]);
        Assert.Equal(SourceWordingMaintenance.Unknown, conflictingMaintenance.Maintenance);
        Assert.Equal(SourceWordingMaturity.Stable, conflictingMaintenance.Maturity);

        SourceWordingMaintenanceEvidence missingExecution = maintenance with { ExecutionRevision = null! };
        SourceWordingMaturityEvidence missingHash = maturity with { DescriptionSha256 = null! };
        SourceWordingClassification missingExecutionView = SourceWording.Classify(
            plugin, source, null, [missingExecution], [maturity]);
        Assert.Equal(SourceWordingMaintenance.Unknown, missingExecutionView.Maintenance);
        Assert.Equal(SourceWordingMaturity.Unclassified, missingExecutionView.Maturity);
        SourceWordingClassification missingHashView = SourceWording.Classify(
            plugin, source, execution, [maintenance], [missingHash]);
        Assert.Equal(SourceWordingMaintenance.MeowChan, missingHashView.Maintenance);
        Assert.Equal(SourceWordingMaturity.Unclassified, missingHashView.Maturity);

        SourceDescription detached = source with { Name = "Spoofed display name", Maturity = SourceMaturity.Unspecified };
        SourceWordingClassification detachedView = SourceWording.Classify(plugin, detached, execution, [maintenance], [maturity]);
        Assert.Equal(SourceWordingMaintenance.Unknown, detachedView.Maintenance);
        Assert.Equal(SourceWordingMaturity.Unclassified, detachedView.Maturity);

        static void AssertUnknown(SourceWordingClassification classification, SourceWordingMaturity maturity)
        {
            Assert.Equal(SourceWordingMaintenance.Unknown, classification.Maintenance);
            Assert.Equal(maturity, classification.Maturity);
        }
    }

    [Fact]
    public void UnserializableDescriptionFailsClosedInTypedClassification()
    {
        SourceDescription source = Source("invalid-source", "Invalid source", (SourceMaturity)42);
        PluginDescription plugin = Plugin("fixture.invalid", "Fixture", "Fixture retained description", source);

        // No invalid-description fingerprint prerequisite or positive evidence: exercise the typed boundary itself.
        SourceWordingClassification classification = SourceWording.Classify(plugin, source);

        Assert.Equal(source.SourceId, classification.SourceId);
        Assert.Equal(source.Name, classification.SourceName);
        Assert.Equal(SourceWordingMaintenance.Unknown, classification.Maintenance);
        Assert.Equal(SourceWordingMaturity.Unclassified, classification.Maturity);
    }

    [Fact]
    public void RawInvalidMaturityRemainsUnclassifiedWithoutFingerprinting()
    {
        SourceWordingClassification classification = SourceWording.Classify(
            "fixture.invalid", "invalid-source", "Invalid source", (SourceMaturity)42);

        Assert.Equal(SourceWordingMaintenance.Unknown, classification.Maintenance);
        Assert.Equal(SourceWordingMaturity.Unclassified, classification.Maturity);
    }

    [Fact]
    public void DuplicateSourceIdAndUndefinedEvidenceEnumsFailClosed()
    {
        PluginDescription plugin = Plugin("fixture.duplicate", "Fixture",
            "Fixture retained description", Source("same-source", "First"), Source("same-source", "Second"));
        SourceDescription source = plugin.Sources[0];
        string execution = "exact-execution";
        string descriptionSha256 = SourceWording.DescriptionFingerprint(plugin);
        SourceWordingMaintenanceEvidence maintenance = new(
            plugin.PluginId, source.SourceId, execution, descriptionSha256, SourceWordingMaintenance.MeowChan);
        SourceWordingMaturityEvidence maturity = new(
            plugin.PluginId, source.SourceId, execution, descriptionSha256, SourceWordingMaturity.Stable);

        SourceWordingClassification duplicate = SourceWording.Classify(plugin, source, execution, [maintenance], [maturity]);
        Assert.Equal(SourceWordingMaintenance.Unknown, duplicate.Maintenance);
        Assert.Equal(SourceWordingMaturity.Unclassified, duplicate.Maturity);

        PluginDescription single = plugin with { Sources = [source] };
        string singleHash = SourceWording.DescriptionFingerprint(single);
        SourceWordingClassification invalidMaintenance = SourceWording.Classify(single, source, execution,
            [maintenance with { DescriptionSha256 = singleHash, Maintenance = (SourceWordingMaintenance)42 }],
            [maturity with { DescriptionSha256 = singleHash }]);
        Assert.Equal(SourceWordingMaintenance.Unknown, invalidMaintenance.Maintenance);
        Assert.Equal(SourceWordingMaturity.Stable, invalidMaintenance.Maturity);

        SourceWordingClassification invalidMaturity = SourceWording.Classify(single, source, execution,
            [maintenance with { DescriptionSha256 = singleHash }],
            [maturity with { DescriptionSha256 = singleHash, Maturity = (SourceWordingMaturity)42 }]);
        Assert.Equal(SourceWordingMaintenance.MeowChan, invalidMaturity.Maintenance);
        Assert.Equal(SourceWordingMaturity.Unclassified, invalidMaturity.Maturity);
    }

    [Fact]
    public void MultiSourceDetailedAndAccessibilityWordingStaysAssociatedInBothLanguages()
    {
        SourceWordingClassification first = new("source-a", "Same name",
            SourceWordingMaintenance.ThirdParty, SourceWordingMaturity.Stable);
        SourceWordingClassification second = new("source-b", "Same name",
            SourceWordingMaintenance.Unknown, SourceWordingMaturity.Experimental);

        string englishCompact = SourceWording.DescribeCompact([first, second]);
        string englishDetailed = SourceWording.DescribeDetailed([first, second]);
        Assert.Contains("Same name (source-a): Third-party · Stable", englishCompact, StringComparison.Ordinal);
        Assert.Contains("Same name (source-b): Maintenance unverified · Experimental", englishCompact, StringComparison.Ordinal);
        Assert.Contains("Source Same name (source-a): maintainer Third-party · maturity Stable", englishDetailed, StringComparison.Ordinal);
        Assert.Contains("Source Same name (source-b): maintainer unverified · maturity Experimental", englishDetailed, StringComparison.Ordinal);

        Texts.SetLanguage("de");
        string germanCompact = SourceWording.DescribeCompact([first, second]);
        string germanDetailed = SourceWording.DescribeDetailed([first, second]);
        Assert.Contains("Same name (source-a): Drittanbieter · Stabil", germanCompact, StringComparison.Ordinal);
        Assert.Contains("Same name (source-b): Pflege ungeprüft · Experimentell", germanCompact, StringComparison.Ordinal);
        Assert.Contains("Quelle Same name (source-a): Pflege Drittanbieter · Reifegrad Stabil", germanDetailed, StringComparison.Ordinal);
        Assert.Contains("Quelle Same name (source-b): Pflege ungeprüft · Reifegrad Experimentell", germanDetailed, StringComparison.Ordinal);
    }

    [Fact]
    public void MaintenanceAndMaturityRemainIndependentInBothLanguages()
    {
        foreach ((SourceWordingMaintenance maintenance, SourceWordingMaturity maturity) in Combinations())
        {
            SourceWordingClassification classification = new("fixture", "Fixture", maintenance, maturity);
            string english = SourceWording.DescribeLabels(classification);
            Assert.Contains(MaintenanceLabel(maintenance, "en"), english, StringComparison.Ordinal);
            Assert.Contains(MaturityLabel(maturity, "en"), english, StringComparison.Ordinal);
        }

        Texts.SetLanguage("de");
        foreach ((SourceWordingMaintenance maintenance, SourceWordingMaturity maturity) in Combinations())
        {
            SourceWordingClassification classification = new("fixture", "Fixture", maintenance, maturity);
            string german = SourceWording.DescribeLabels(classification);
            Assert.Contains(MaintenanceLabel(maintenance, "de"), german, StringComparison.Ordinal);
            Assert.Contains(MaturityLabel(maturity, "de"), german, StringComparison.Ordinal);
        }

        static IEnumerable<(SourceWordingMaintenance, SourceWordingMaturity)> Combinations()
        {
            yield return (SourceWordingMaintenance.Unknown, SourceWordingMaturity.Unclassified);
            yield return (SourceWordingMaintenance.ThirdParty, SourceWordingMaturity.Stable);
            yield return (SourceWordingMaintenance.ThirdParty, SourceWordingMaturity.Experimental);
            yield return (SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Stable);
            yield return (SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Experimental);
        }
    }

    [Fact]
    public void SourceSnapshotNeverTurnsMissingMaintenanceEvidenceIntoThirdPartyOrStable()
    {
        DesktopSessionView empty = new([], [], [], DesktopSessionState.Ready, null);
        SourceMediaIntegrityPolicyView legacy = Policy("meowchan.official.4chan", "4chan", SourceMaturity.Unspecified);
        SourceMediaIntegrityPolicyView projectExperimental = Policy("meowchan.source.endchan", "endchan", SourceMaturity.Experimental);
        IReadOnlyList<SourceRow> withPolicy = SourcesSnapshot.From(
            [Provider("4chan"), Provider("endchan")], empty, [legacy, projectExperimental]);
        IReadOnlyList<SourceRow> withoutPolicy = SourcesSnapshot.From([Provider("vendor")], empty);

        Assert.Equal((SourceWordingMaintenance.Unknown, SourceWordingMaturity.Unclassified),
            (withPolicy[0].Classification!.Maintenance, withPolicy[0].Classification!.Maturity));
        Assert.Equal((SourceWordingMaintenance.Unknown, SourceWordingMaturity.Experimental),
            (withPolicy[1].Classification!.Maintenance, withPolicy[1].Classification!.Maturity));
        Assert.Null(withoutPolicy[0].Classification);
    }

    private static SourceMediaIntegrityPolicyView Policy(string? ownerId, string sourceId, SourceMaturity maturity) =>
        new(new("profile", ownerId!, sourceId, false, 1), false, true, maturity);

    private static ProviderDescriptor Provider(string id) => new(id, id,
        new Uri("https://" + id + ".invalid/"), ProviderCapabilities.ThreadWatch, [], null, false);

    private static PluginDescription Plugin(string pluginId, string name, string description,
        params SourceDescription[] sources) =>
        new(pluginId, name, "1.0.0", SdkContract.Version, description, sources, [],
            new("1", []), []);

    private static SourceDescription Source(string id, string name, SourceMaturity maturity = SourceMaturity.Unspecified) =>
        new(id, name, new Uri("https://" + id + ".invalid/"), SourceCapabilities.ThreadRead, [], [],
            new("1", [])) { Maturity = maturity };

    private static string MaintenanceLabel(SourceWordingMaintenance maintenance, string language) => (maintenance, language) switch
    {
        (SourceWordingMaintenance.MeowChan, "en") => "MeowChan project",
        (SourceWordingMaintenance.MeowChan, "de") => "MeowChan-Projekt",
        (SourceWordingMaintenance.ThirdParty, "en") => "Third-party",
        (SourceWordingMaintenance.ThirdParty, "de") => "Drittanbieter",
        (SourceWordingMaintenance.Unknown, "en") => "Maintenance unverified",
        _ => "Pflege ungeprüft"
    };

    private static string MaturityLabel(SourceWordingMaturity maturity, string language) => (maturity, language) switch
    {
        (SourceWordingMaturity.Stable, "en") => "Stable",
        (SourceWordingMaturity.Stable, "de") => "Stabil",
        (SourceWordingMaturity.Experimental, "en") => "Experimental",
        (SourceWordingMaturity.Experimental, "de") => "Experimentell",
        (SourceWordingMaturity.Unclassified, "en") => "Unclassified",
        _ => "Nicht eingestuft"
    };
}
