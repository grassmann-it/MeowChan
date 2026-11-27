using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

/// <summary>
/// Host-owned presentation evidence for the five bundled 1.0.3 Chan adapters.
/// The exact complete-description hashes (SDK 1.1.0 descriptions) prevent a changed local candidate from inheriting the tier.
/// This wording confers neither operator approval nor execution authority.
/// </summary>
internal static class SourceWordingEvidence
{
    private sealed record BundledChanSource(string SourceId, string DescriptionSha256);

    private static readonly IReadOnlyDictionary<string, BundledChanSource> BundledChanSources =
        new Dictionary<string, BundledChanSource>(StringComparer.Ordinal)
        {
            ["meowchan.source.sevenchan"] = new("sevenchan",
                "7bb3e64621ef69fc0944fb87705c352210b8e9c76d5fe0d9d9db82cb83949f81"),
            ["meowchan.source.bbwchan"] = new("bbwchan",
                "26fed55f213b5d771d3582c41cd4864ce155e56c5fbae7a1585f507094dc7865"),
            ["meowchan.source.eightchanmoe"] = new("eightchanmoe",
                "a4b2eb1161cd504850fffd8b3fe62f6a4162b6273091aaac970b7fa2203bd46f"),
            ["meowchan.source.fchan"] = new("fchan",
                "c4b1462cfe191bee12f8fa82934eef0c6b13b373be73c2df43cc7f02242bb628"),
            ["meowchan.source.capybarachan"] = new("capybarachan",
                "a03355430416a3f85e68a93417bfac61c70c9f0a01114794b5bc64268052d819")
        };

    internal static IReadOnlyList<SourceWordingMaintenanceEvidence> Maintenance(
        PluginDescription? description, string? executionRevision)
    {
        if (description is null || !IsExactBundledDescription(description, out BundledChanSource bundled) ||
            string.IsNullOrWhiteSpace(executionRevision)) return Array.Empty<SourceWordingMaintenanceEvidence>();

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
    /// may say Third-party.
    /// </summary>
    internal static IReadOnlyList<SourceWordingClassification> DisplayClassifications(
        PluginDescription? description, string? executionRevision)
    {
        if (description is null) return Array.Empty<SourceWordingClassification>();
        IReadOnlyList<SourceWordingMaintenanceEvidence> maintenance = Maintenance(description, executionRevision);
        return description.Sources.Select(source => SourceWording.Classify(description, source,
            executionRevision, maintenance)).ToArray();
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
