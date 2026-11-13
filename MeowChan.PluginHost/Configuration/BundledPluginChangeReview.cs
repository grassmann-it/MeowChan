using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

/// <summary>Exact, read-only review payload, serialized inside a revision-fenced repair row.
/// Previous settings are evidence, not an automatically migrated or reset configuration.</summary>
public sealed record BundledPluginChangeReview(PluginDescription PreviousDescription,
    PluginDescription NewDescription, PluginConfiguration PreviousConfiguration,
    BundledRepairPermissions Permissions);
