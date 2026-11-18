using System;
using System.IO;
using System.Linq;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;

namespace MeowChan.Desktop.Plugins;

/// <summary>The one plain-language status line of a bundled repair card.</summary>
internal enum BundledRepairStatus
{
    /// <summary>The same build as the approved one, now delivered from another folder.</summary>
    Relocated,
    /// <summary>A changed build delivered from another folder than the approved one.</summary>
    UpdatedElsewhere,
    /// <summary>A changed build whose folder did not change (or is not known).</summary>
    Updated,
    /// <summary>The changed build would keep existing special permissions: a separate rights decision.</summary>
    NeedsRights,
    /// <summary>Capabilities or settings changed: the separate change review.</summary>
    NeedsChangeReview,
    /// <summary>Nothing can be confirmed here; the row's blocked reason says why.</summary>
    Blocked
}

/// <summary>
/// Presentation only for the "Review bundled repairs" dialog: which status line, which reason and which name a row shows.
/// It never decides what may be confirmed — that stays with the row's own <c>CanConfirm</c>/<c>CanReauthorize</c>/
/// <c>CanReviewChanges</c> and the store's rederivation under the profile writer lease.
/// </summary>
internal static class BundledRepairPresentation
{
    internal static BundledRepairStatus Status(BundledCandidateRepair row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.CanReviewChanges) return BundledRepairStatus.NeedsChangeReview;
        if (row.CanReauthorize) return BundledRepairStatus.NeedsRights;
        if (!row.CanConfirm) return BundledRepairStatus.Blocked;
        if (!row.BuildChanged) return BundledRepairStatus.Relocated;
        return MovedFolder(row) ? BundledRepairStatus.UpdatedElsewhere : BundledRepairStatus.Updated;
    }

    internal static string StatusKey(BundledRepairStatus status) => status switch
    {
        BundledRepairStatus.Relocated => "plugins.repair.status.relocated",
        BundledRepairStatus.UpdatedElsewhere => "plugins.repair.status.updatedElsewhere",
        BundledRepairStatus.Updated => "plugins.repair.status.updated",
        BundledRepairStatus.NeedsRights => "plugins.repair.status.rights",
        BundledRepairStatus.NeedsChangeReview => "plugins.repair.status.changes",
        _ => "plugins.repair.status.blocked"
    };

    /// <summary>The existing reason sentence of a repair row (unchanged texts; only its place in the card moved).</summary>
    internal static string ReasonKey(BundledCandidateRepair row, bool permissionsReadable)
    {
        ArgumentNullException.ThrowIfNull(row);
        return permissionsReadable ? "plugins.repair.reauthorize.ready"
            : row.BlockedReason is { } reason ? "plugins.repair.blocked." + reason
            : row.BuildChanged ? "plugins.repair.changed" : "plugins.repair.sameBuild";
    }

    /// <summary>
    /// The approved and the new candidate live in different folders (a moved install, a second copy, a worktree). After #130
    /// an in-place update in the same folder normally carries its trust over, so this is the main remaining case.
    /// Unknown paths are not reported as a move.
    /// </summary>
    internal static bool MovedFolder(BundledCandidateRepair row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(row.OldPath) || string.IsNullOrWhiteSpace(row.NewPath)) return false;
        string? oldFolder = Folder(row.OldPath), newFolder = Folder(row.NewPath);
        return oldFolder is not null && newFolder is not null &&
            !string.Equals(oldFolder, newFolder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static string? Folder(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// The plugin's display name for the card heading: the declared name of the new candidate, else of the approved one,
    /// else of a retained release. The plugin ID is the last resort (it always stays in the technical details).
    /// </summary>
    internal static string DisplayName(string pluginId, PluginProfileSnapshot? profile, params string?[] candidateIds)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        PluginActivationSnapshot? plugin = profile?.Plugins.FirstOrDefault(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
        if (plugin is null) return pluginId;
        foreach (string? candidateId in candidateIds)
        {
            if (candidateId is null) continue;
            string? name = plugin.Candidates.FirstOrDefault(candidate => candidate.CandidateId == candidateId)?.Description?.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        string? retained = new[] { plugin.Pending, plugin.Active, plugin.LastGood, plugin.Pinned }
            .Select(release => release?.Description.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        return retained ?? pluginId;
    }

    /// <summary>The status line of a receipt card: a saved approval says plainly that it waits for the next start.</summary>
    internal static string ReceiptStatusKey(PluginApprovalState state) =>
        state == PluginApprovalState.Saved ? "plugins.repair.status.confirmed" : "plugins.approval.state." + state;

    /// <summary>The restart note belongs to every dialog that saved (or may have saved) an approval.</summary>
    internal static bool NeedsRestartNote(System.Collections.Generic.IEnumerable<PluginApprovalState> states) =>
        states.Any(state => state is PluginApprovalState.Saved or PluginApprovalState.CheckStore);
}
