using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

/// <summary>
/// Host-owned presentation evidence for the seven bundled 1.0.3 Chan adapters.
/// The exact complete-description hashes prevent a changed local candidate from inheriting the tier.
/// This wording confers neither operator approval nor execution authority.
/// </summary>
internal static class SourceWordingEvidence
{
    private sealed record BundledChanSource(string SourceId, string SourceName, string DescriptionSha256);

    private static readonly IReadOnlyDictionary<string, BundledChanSource> BundledChanSources =
        new Dictionary<string, BundledChanSource>(StringComparer.Ordinal)
        {
            ["meowchan.source.sevenchan"] = new("sevenchan", "7chan",
                "a46a883174b0e7ce192d8c9557766648678a6acd115b606047911df66e3429ef"),
            ["meowchan.source.bbwchan"] = new("bbwchan", "BBW-Chan",
                "dfc8daeffa87c5621b66887a953d91d982fb0859d39e24e5d453ca32991004bb"),
            ["meowchan.source.anonimageboards"] = new("anonimageboards", "Anon Imageboards",
                "80a57ea0afa05efea768111231c3435f83230b79506644f8af8768d083f0a193"),
            ["meowchan.source.eightchanmoe"] = new("eightchanmoe", "8chan.moe",
                "0e4775c0617ad649ca56582580c0f715b12d44f5f45a99b44cbfa1d8a44093a6"),
            ["meowchan.source.gurocx"] = new("gurocx", "Guro.cx",
                "363cb1ba8c2a4d18087bcde570a84b9bc86a144b5998b3b51f0fec6d0dd2388e"),
            ["meowchan.source.fchan"] = new("fchan", "Fchan",
                "e18c7035769bd59ceeb35911e2e326b47154c5c93935515ff114be4a2f3a7dc2"),
            ["meowchan.source.capybarachan"] = new("capybarachan", "CapybaraChan",
                "88fd438768fb3fd4ab30aee491c9f3bb41b460fad5f22c7072856ea487ee56f0")
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
