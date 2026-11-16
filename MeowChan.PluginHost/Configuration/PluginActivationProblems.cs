namespace MeowChan.PluginHost.Configuration;

/// <summary>
/// The startup activation problems that only an explicit review of the bundled build can resolve (the
/// "Review bundled repairs" dialog). Restarting does not help: the plugin stays inactive, and every source it owns
/// stays unavailable, until that review is confirmed. One place, so the store, the source adapters and the plugin
/// pages agree on the same exact problem texts.
/// </summary>
public static class PluginActivationProblems
{
    public const string ConfirmedTargetChanged = "Confirmed bundled target changed or disappeared; review repair again.";
    public const string NoDeliveredProvenance = "Bundled target has no matching delivered provenance; review is required.";
    public const string NoTrustedBasis = "Bundled replacement has no unambiguous trusted pre-replacement basis; review is required.";
    public const string ExecutableChanged = "Bundled executable changed; explicit repair review is required.";
    public const string InheritedGrantsBlocked = "Bundled replacement is blocked: existing grants cannot be inherited.";

    private static readonly string[] RepairReview =
        [ConfirmedTargetChanged, NoDeliveredProvenance, NoTrustedBasis, ExecutableChanged, InheritedGrantsBlocked];

    /// <summary>True when the plugin waits for an explicit bundled repair review before it can run again.</summary>
    public static bool RequiresRepairReview(string? problem) =>
        problem is not null && Array.IndexOf(RepairReview, problem) >= 0;
}
