using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

/// <summary>
/// Host-owned presentation evidence for the seven bundled 1.0.3 Chan adapters.
/// The exact complete-description hashes (SDK 1.1.0 descriptions) prevent a changed local candidate from inheriting the tier.
/// This wording confers neither operator approval nor execution authority.
/// </summary>
internal static class SourceWordingEvidence
{
    private sealed record BundledChanSource(string SourceId, string SourceName, string DescriptionSha256);

    private static readonly IReadOnlyDictionary<string, BundledChanSource> BundledChanSources =
        new Dictionary<string, BundledChanSource>(StringComparer.Ordinal)
        {
            ["meowchan.source.sevenchan"] = new("sevenchan", "7chan",
                "7bb3e64621ef69fc0944fb87705c352210b8e9c76d5fe0d9d9db82cb83949f81"),
            ["meowchan.source.bbwchan"] = new("bbwchan", "BBW-Chan",
                "26fed55f213b5d771d3582c41cd4864ce155e56c5fbae7a1585f507094dc7865"),
            ["meowchan.source.anonimageboards"] = new("anonimageboards", "Anon Imageboards",
                "9d1327b848bd236e5cf3e545237a8ea345a6a858f5ec504986314707a7ed0ac6"),
            ["meowchan.source.eightchanmoe"] = new("eightchanmoe", "8chan.moe",
                "a4b2eb1161cd504850fffd8b3fe62f6a4162b6273091aaac970b7fa2203bd46f"),
            ["meowchan.source.gurocx"] = new("gurocx", "Guro.cx",
                "c6dfedcef2bfca4770e71886804cbd68f183f087184c9199393802c901c5c06d"),
            ["meowchan.source.fchan"] = new("fchan", "Fchan",
                "c4b1462cfe191bee12f8fa82934eef0c6b13b373be73c2df43cc7f02242bb628"),
            ["meowchan.source.capybarachan"] = new("capybarachan", "CapybaraChan",
                "a03355430416a3f85e68a93417bfac61c70c9f0a01114794b5bc64268052d819")
        };

    internal static IReadOnlyList<SourceWordingMaintenanceEvidence> Maintenance(
        PluginDescription? description, string? executionRevision)
    {
        if (!IsExactBundledDescription(description, out BundledChanSource _) ||
            string.IsNullOrWhiteSpace(executionRevision)) return Array.Empty<SourceWordingMaintenanceEvidence>();
        BundledChanSource bundled = BundledChanSources[description!.PluginId];

        return description.Sources
            .Where(source => string.Equals(source.SourceId, bundled.SourceId, StringComparison.Ordinal))
            .Select(source => new SourceWordingMaintenanceEvidence(
                description.PluginId, source.SourceId, executionRevision,
                SourceWording.DescriptionFingerprint(description), SourceWordingMaintenance.MeowChan))
            .ToArray();
    }

    internal static SourceWordingClassification Classify(PluginDescription description,
        SourceDescription source, string? executionRevision) =>
        SourceWording.Classify(description, source, executionRevision,
            Maintenance(description, executionRevision));

    /// <summary>
    /// Production display projection. Unknown origins remain unknown; only explicit host evidence
    /// may say Third-party. Two deliberately blocked bundled plugins have no SourceDescription yet
    /// and therefore receive the reserved product tier here without becoming registered sources.
    /// </summary>
    internal static IReadOnlyList<SourceWordingClassification> DisplayClassifications(
        PluginDescription? description, string? executionRevision)
    {
        if (description is null) return Array.Empty<SourceWordingClassification>();
        if (!IsExactBundledDescription(description, out BundledChanSource bundled) ||
            string.IsNullOrWhiteSpace(executionRevision))
            return description.Sources
                .Select(source => SourceWording.Classify(description, source, executionRevision))
                .ToArray();

        if (description.Sources.Count == 0)
            return [new(bundled.SourceId, bundled.SourceName,
                SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Experimental)];

        return description.Sources.Select(source => SourceWording.Classify(description, source,
            executionRevision, Maintenance(description, executionRevision))).ToArray();
    }

    private static bool IsExactBundledDescription(PluginDescription? description, out BundledChanSource bundled)
    {
        bundled = null!;
        if (description is null || !BundledChanSources.TryGetValue(description.PluginId, out bundled!))
            return false;
        try
        {
            return SourceWording.DescriptionFingerprint(description) == bundled.DescriptionSha256;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
