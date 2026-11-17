using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;

namespace MeowChan.Desktop.Booru;

/// <summary>Where the Danbooru page's repair route stands for a running build that failed with <c>sdk-mismatch</c>.</summary>
internal enum BooruRepairStage
{
    /// <summary>Not read yet (or not readable): the route is offered and the dialog shows what it can.</summary>
    Unknown,
    /// <summary>The review dialog has a confirmable row for the Danbooru plugin.</summary>
    Reviewable,
    /// <summary>Nothing can be confirmed for the Danbooru plugin; <see cref="BooruBuildRepair.BlockedReasonKey"/> says why.</summary>
    Blocked,
    /// <summary>A different build is saved for the next start; only a restart remains.</summary>
    ConfirmedPending
}

/// <summary>
/// Presentation of the repair route on the Danbooru page. Derived from the plugin profile snapshot and the store's own
/// read-only previews; it never decides what may be confirmed (the dialog and the store do that under their lease).
/// </summary>
internal sealed record BooruBuildRepair(bool MismatchObserved, BooruRepairStage Stage, bool ReleasesPin = false,
    string? BlockedReasonKey = null)
{
    internal static readonly BooruBuildRepair None = new(false, BooruRepairStage.Unknown);

    internal const string NothingToReviewKey = "booru.ui.repair.nothing";

    /// <summary>
    /// The stage for one plugin. A pending release whose build differs from the running one means the review was confirmed
    /// (by this route or on the Plugins page) and only a restart remains; otherwise the previews decide. A confirmable
    /// reselection of a pinned plugin is the pinned same-bundle rebuild: confirming it releases the pin.
    /// </summary>
    internal static BooruBuildRepair Derive(bool mismatchObserved, PluginActivationSnapshot? plugin, string? runningExecutionRevision,
        BundledCandidateReselection? selection, BundledCandidateRepair? repair)
    {
        if (plugin?.Pending is { } pending && !string.Equals(pending.ExecutionRevision, runningExecutionRevision, StringComparison.Ordinal))
            return new(mismatchObserved, BooruRepairStage.ConfirmedPending);
        if (selection is { CanConfirm: true })
            return new(mismatchObserved, BooruRepairStage.Reviewable, ReleasesPin: plugin?.Pinned is not null);
        if (repair is { } offered && (offered.CanConfirm || offered.CanReauthorize || offered.CanReviewChanges))
            return new(mismatchObserved, BooruRepairStage.Reviewable);
        if (selection is { } blocked)
            return new(mismatchObserved, BooruRepairStage.Blocked, BlockedReasonKey: ReselectionReasonKey(blocked.BlockedReason));
        if (repair is { BlockedReason: { } reason })
            return new(mismatchObserved, BooruRepairStage.Blocked, BlockedReasonKey: RepairReasonKey(reason));
        return new(mismatchObserved, BooruRepairStage.Blocked, BlockedReasonKey: NothingToReviewKey);
    }

    // The same reason sentences the review dialog shows for these rows.
    private static string ReselectionReasonKey(string? reason) => reason switch
    {
        "pinned" or "unproven-configuration" or "pending-choice" or "ambiguous" or "no-proven-target" or
        "configuration-incompatible" or "inherited-grants" or "source-owner-conflict" or
        "source-identity-changed" or "proven-history" or "selection-available" => "plugins.reselection.blocked." + reason,
        _ => "plugins.reselection.blocked.unknown"
    };

    private static string RepairReasonKey(string reason) => reason switch
    {
        "pending-choice" or "pinned" or "unproven-history" or "ambiguous" or "no-proven-target" or "description-changed" or
        "inherited-grants" or "source-owner-conflict" or "configuration-incompatible" => "plugins.repair.blocked." + reason,
        _ => "plugins.reselection.blocked.unknown"
    };
}

/// <summary>
/// Reads the repair route's state for the Danbooru plugin of one session: the session's sdk-mismatch observation of the
/// running build, the profile snapshot and the store's read-only previews. It writes nothing.
/// </summary>
internal sealed class BooruBuildRepairProbe
{
    private readonly IPluginProfileStore _store;
    private readonly Func<string?> _running;
    private readonly PluginSdkMismatchLedger _ledger;

    internal BooruBuildRepairProbe(string pluginId, IPluginProfileStore store, Func<string?> runningExecutionRevision,
        PluginSdkMismatchLedger ledger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        PluginId = pluginId;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _running = runningExecutionRevision ?? throw new ArgumentNullException(nameof(runningExecutionRevision));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    internal string PluginId { get; }

    internal PluginSdkMismatchLedger Ledger => _ledger;

    internal bool MismatchObserved => _ledger.Has(PluginId, _running());

    /// <param name="mismatchKnown">The page itself saw a search fail with <c>sdk-mismatch</c>.</param>
    internal async Task<BooruBuildRepair> ReadAsync(bool mismatchKnown, CancellationToken cancellationToken)
    {
        bool mismatch = mismatchKnown || MismatchObserved;
        if (!mismatch) return BooruBuildRepair.None;
        PluginProfileSnapshot profile = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        PluginActivationSnapshot? plugin = profile.Plugins.FirstOrDefault(item => string.Equals(item.PluginId, PluginId, StringComparison.Ordinal));
        string? running = _running();
        if (plugin?.Pending is { } pending && !string.Equals(pending.ExecutionRevision, running, StringComparison.Ordinal))
            return BooruBuildRepair.Derive(mismatch, plugin, running, null, null);
        BundledCandidateReselection? selection = null;
        BundledCandidateRepair? repair = null;
        try
        {
            selection = (await _store.PreviewBundledReselectionsAsync(cancellationToken).ConfigureAwait(false)).Rows
                .FirstOrDefault(row => string.Equals(row.PluginId, PluginId, StringComparison.Ordinal));
        }
        catch (NotSupportedException) { }
        try
        {
            repair = (await _store.PreviewBundledRepairsAsync(cancellationToken).ConfigureAwait(false)).Rows
                .FirstOrDefault(row => string.Equals(row.PluginId, PluginId, StringComparison.Ordinal));
        }
        catch (NotSupportedException) { }
        return BooruBuildRepair.Derive(mismatch, plugin, running, selection, repair);
    }
}
