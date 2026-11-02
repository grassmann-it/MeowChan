using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

internal enum SourceWordingMaintenance { Unknown, MeowChan, ThirdParty }
internal enum SourceWordingMaturity { Stable, Experimental, Unclassified }

/// <summary>Presentation-only wording. Maintenance, delivery and maturity are independent and grant no execution authority.</summary>
internal sealed record SourceWordingClassification(
    string SourceId, string SourceName, SourceWordingMaintenance Maintenance, SourceWordingMaturity Maturity);

/// <summary>
/// Trusted host adapter input binding an exact build and complete description to a maintainer label.
/// A plugin ID, self-description, local filename or delivered-file observation alone cannot create it.
/// </summary>
internal sealed record SourceWordingMaintenanceEvidence(string PluginOwnerId, string SourceId,
    string ExecutionRevision, string DescriptionSha256, SourceWordingMaintenance Maintenance);

/// <summary>Trusted host adapter input for an explicit maturity classification; Stable is never inferred.</summary>
internal sealed record SourceWordingMaturityEvidence(string PluginOwnerId, string SourceId,
    string ExecutionRevision, string DescriptionSha256, SourceWordingMaturity Maturity);

internal static class SourceWording
{
    private const string LegacyOperatorLabel = "Official ";

    internal static SourceWordingClassification Classify(PluginDescription plugin, SourceDescription source,
        string? executionRevision = null, IReadOnlyList<SourceWordingMaintenanceEvidence>? maintenanceEvidence = null,
        IReadOnlyList<SourceWordingMaturityEvidence>? maturityEvidence = null)
    {
        // The fingerprinted plugin, not a separately supplied detached copy, owns the rendered source.
        // A duplicate SourceId likewise makes source-bound evidence ambiguous even when the object references differ.
        if (string.IsNullOrWhiteSpace(source.SourceId) || string.IsNullOrWhiteSpace(source.Name) ||
            plugin.Sources.Count(item => ReferenceEquals(item, source)) != 1 ||
            plugin.Sources.Count(item => string.Equals(item.SourceId, source.SourceId, StringComparison.Ordinal)) != 1)
            return new(source.SourceId, source.Name, SourceWordingMaintenance.Unknown, SourceWordingMaturity.Unclassified);

        string descriptionSha256;
        try
        {
            descriptionSha256 = DescriptionFingerprint(plugin);
        }
        catch (JsonException)
        {
            // An unserializable description cannot bind evidence; do not invent a replacement hash.
            return new(source.SourceId, source.Name, SourceWordingMaintenance.Unknown, SourceWordingMaturity.Unclassified);
        }
        return Classify(plugin.PluginId, source.SourceId, source.Name, source.Maturity,
            executionRevision, descriptionSha256, maintenanceEvidence, maturityEvidence);
    }

    internal static SourceWordingClassification Classify(string? pluginOwnerId, string sourceId,
        string sourceName, SourceMaturity declaredMaturity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        return new(sourceId, sourceName, SourceWordingMaintenance.Unknown, DeclaredMaturity(declaredMaturity));
    }

    internal static SourceWordingClassification Classify(string? pluginOwnerId, string sourceId,
        string sourceName, SourceMaturity declaredMaturity, string? executionRevision, string descriptionSha256,
        IReadOnlyList<SourceWordingMaintenanceEvidence>? maintenanceEvidence,
        IReadOnlyList<SourceWordingMaturityEvidence>? maturityEvidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptionSha256);

        SourceWordingMaintenance maintenance = SourceWordingMaintenance.Unknown;
        SourceWordingMaintenanceEvidence[] maintenanceMatches = (maintenanceEvidence ?? []).Where(evidence =>
            ExactBinding(evidence, pluginOwnerId, sourceId, executionRevision, descriptionSha256)).ToArray();
        if (maintenanceMatches.Length == 1 && maintenanceMatches[0].Maintenance is not SourceWordingMaintenance.Unknown)
            maintenance = maintenanceMatches[0].Maintenance;

        SourceWordingMaturity maturity = DeclaredMaturity(declaredMaturity);
        if (declaredMaturity == SourceMaturity.Unspecified)
        {
            SourceWordingMaturityEvidence[] maturityMatches = (maturityEvidence ?? []).Where(evidence =>
                ExactBinding(evidence, pluginOwnerId, sourceId, executionRevision, descriptionSha256)).ToArray();
            if (maturityMatches.Length == 1) maturity = maturityMatches[0].Maturity;
        }

        return new(sourceId, sourceName, maintenance, maturity);
    }

    internal static string Describe(SourceWordingClassification classification) => Texts.T("sources.classification",
        Texts.T(MaintenanceKey(classification.Maintenance)), Texts.T(MaturityKey(classification.Maturity)));

    internal static string DescribeLabels(SourceWordingClassification classification) => Texts.T(
        "sources.classificationCompact",
        Texts.T(MaintenanceKey(classification.Maintenance)), Texts.T(MaturityKey(classification.Maturity)));

    internal static string DescribeCompact(IReadOnlyList<SourceWordingClassification> classifications)
    {
        if (classifications.Count == 0) return string.Empty;
        if (classifications.Count == 1) return DescribeLabels(classifications[0]);
        bool includeIds = HasDuplicateSourceNames(classifications);
        return string.Join(" · ", classifications.Select(classification =>
            DescribeAssociated(classification, detailed: false, includeId: includeIds)));
    }

    internal static string DescribeDetailed(IReadOnlyList<SourceWordingClassification> classifications)
    {
        if (classifications.Count == 0) return string.Empty;
        bool includeIds = HasDuplicateSourceNames(classifications);
        return string.Join("\n", classifications.Select(classification =>
            DescribeAssociated(classification, detailed: true, includeId: includeIds)));
    }

    /// <summary>Replaces only the exact legacy source-plugin sentence prefix; the remaining plugin text stays byte-accurate.</summary>
    internal static string DisplayDescription(PluginDescription plugin)
    {
        string legacyPrefix = LegacyOperatorLabel + plugin.Name + " source:";
        return plugin.Sources.Count > 0 && plugin.Description.StartsWith(legacyPrefix, StringComparison.Ordinal)
            ? Texts.T("sources.legacyDescriptionPrefix", plugin.Name) + plugin.Description[legacyPrefix.Length..]
            : plugin.Description;
    }

    private static bool HasDuplicateSourceNames(IReadOnlyList<SourceWordingClassification> classifications) =>
        classifications.GroupBy(classification => classification.SourceName, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

    private static string DescribeAssociated(SourceWordingClassification classification, bool detailed, bool includeId)
    {
        string maintenance = Texts.T(classification.Maintenance == SourceWordingMaintenance.Unknown && detailed
            ? "sources.maintenance.unverifiedDetail" : MaintenanceKey(classification.Maintenance));
        string maturity = Texts.T(MaturityKey(classification.Maturity));
        return (detailed, includeId) switch
        {
            (true, true) => Texts.T("sources.classificationDetailedWithId",
                classification.SourceName, classification.SourceId, maintenance, maturity),
            (true, false) => Texts.T("sources.classificationDetailed",
                classification.SourceName, maintenance, maturity),
            (false, true) => Texts.T("sources.classificationCompactWithId",
                classification.SourceName, classification.SourceId, maintenance, maturity),
            _ => Texts.T("sources.classificationCompactNamed",
                classification.SourceName, maintenance, maturity)
        };
    }

    internal static string DescriptionFingerprint(PluginDescription plugin) =>
        Convert.ToHexStringLower(SHA256.HashData(SdkJson.Serialize(plugin)));

    private static SourceWordingMaturity DeclaredMaturity(SourceMaturity maturity) => maturity switch
    {
        SourceMaturity.Experimental => SourceWordingMaturity.Experimental,
        _ => SourceWordingMaturity.Unclassified
    };

    private static bool ExactBinding(SourceWordingMaintenanceEvidence evidence, string? pluginOwnerId,
        string sourceId, string? executionRevision, string descriptionSha256) =>
        CompleteEvidence(evidence.PluginOwnerId, evidence.SourceId, evidence.ExecutionRevision, evidence.DescriptionSha256) &&
        DefinedMaintenance(evidence.Maintenance) &&
        evidence.PluginOwnerId == pluginOwnerId && evidence.SourceId == sourceId &&
        evidence.ExecutionRevision == executionRevision && evidence.DescriptionSha256 == descriptionSha256;

    private static bool ExactBinding(SourceWordingMaturityEvidence evidence, string? pluginOwnerId,
        string sourceId, string? executionRevision, string descriptionSha256) =>
        CompleteEvidence(evidence.PluginOwnerId, evidence.SourceId, evidence.ExecutionRevision, evidence.DescriptionSha256) &&
        DefinedMaturity(evidence.Maturity) &&
        evidence.PluginOwnerId == pluginOwnerId && evidence.SourceId == sourceId &&
        evidence.ExecutionRevision == executionRevision && evidence.DescriptionSha256 == descriptionSha256;

    private static bool CompleteEvidence(string? pluginOwnerId, string? sourceId,
        string? executionRevision, string? descriptionSha256) =>
        !string.IsNullOrWhiteSpace(pluginOwnerId) && !string.IsNullOrWhiteSpace(sourceId) &&
        !string.IsNullOrWhiteSpace(executionRevision) && !string.IsNullOrWhiteSpace(descriptionSha256);

    private static bool DefinedMaintenance(SourceWordingMaintenance maintenance) =>
        maintenance is SourceWordingMaintenance.MeowChan or SourceWordingMaintenance.ThirdParty;

    private static bool DefinedMaturity(SourceWordingMaturity maturity) =>
        maturity is SourceWordingMaturity.Stable or SourceWordingMaturity.Experimental;

    private static string MaintenanceKey(SourceWordingMaintenance maintenance) => maintenance switch
    {
        SourceWordingMaintenance.MeowChan => "sources.maintenance.meowchan",
        SourceWordingMaintenance.ThirdParty => "sources.maintenance.thirdParty",
        _ => "sources.maintenance.unverified"
    };

    private static string MaturityKey(SourceWordingMaturity maturity) => maturity switch
    {
        SourceWordingMaturity.Stable => "sources.maturity.stable",
        SourceWordingMaturity.Experimental => "sources.maturity.experimental",
        _ => "sources.maturity.unclassified"
    };
}
