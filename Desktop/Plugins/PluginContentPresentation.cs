using System.Collections.Generic;
using System.Globalization;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

/// <summary>
/// Localizes host labels over the shared presentation. Plugin-supplied text is
/// preserved in its original language; it is neither translated nor inferred from IDs.
/// </summary>
internal static class PluginContentPresentation
{
    internal static string DescribeDescription(PluginEntryPresentation presentation) =>
        DescriptionOrFallback(presentation.Description);

    internal static string DescribeDescription(PluginDescription? description) =>
        DescriptionOrFallback(description?.Description);

    private static string DescriptionOrFallback(string? description) =>
        string.IsNullOrWhiteSpace(description) ? Texts.T("plugins.noDescription") : description;

    internal static string DescribeType(PluginDescription? description) => Texts.T(
        (description?.Sources.Count > 0, description?.Filters.Count > 0) switch
        {
            (true, true) => "plugins.type.mixed",
            (true, false) => "plugins.type.source",
            (false, true) => "plugins.type.filter",
            _ => "plugins.type.unknown"
        });

    internal static string DescribeAvailability(PluginAvailability availability) => Texts.T(availability switch
    {
        PluginAvailability.Active => "plugins.state.active",
        PluginAvailability.Disabled => "plugins.state.disabled",
        PluginAvailability.Unavailable => "plugins.state.unavailable",
        PluginAvailability.NotLoaded => "plugins.state.notLoaded",
        _ => "plugins.state.unknown"
    });

    internal static string DescribeDescriptionOrigin(PluginDescriptionOrigin origin) => Texts.T(origin switch
    {
        PluginDescriptionOrigin.Candidate => "plugins.description.candidate",
        PluginDescriptionOrigin.ActiveFallback => "plugins.description.activeFallback",
        PluginDescriptionOrigin.LastGoodFallback => "plugins.description.lastGoodFallback",
        PluginDescriptionOrigin.Unavailable => "plugins.description.unavailable",
        _ => "plugins.description.unknown"
    });

    internal static string DescribePending(PluginPendingKind pending) => pending == PluginPendingKind.None
        ? string.Empty : Texts.T(pending switch
        {
            PluginPendingKind.Build => "plugins.pending.build",
            PluginPendingKind.Configuration => "plugins.pending.configuration",
            _ => "plugins.pending.unknown"
        });

    internal static string DescribeStatus(PluginEntryPresentation presentation)
    {
        List<string> labels = new() { Texts.T("plugins.state.plugin", DescribeAvailability(presentation.Availability)) };
        if (presentation.IsActiveCandidate) labels.Add(Texts.T("plugins.candidate.current"));
        else if (presentation.HasChangedCandidateBuild) labels.Add(Texts.T("plugins.changedBuild"));
        else if (presentation.IsSelectedCandidate) labels.Add(Texts.T("plugins.candidate.selected"));
        else labels.Add(Texts.T("plugins.notSelected"));

        if (presentation.PendingKind != PluginPendingKind.None)
            labels.Add(presentation.IsPendingCandidateBuild
                ? DescribePending(presentation.PendingKind) : Texts.T("plugins.candidate.otherPending"));
        if (presentation.IsRollbackPinned) labels.Add(Texts.T("plugins.rollbackPinned"));
        if (presentation.CandidateMissing || presentation.HasWorkerProblem || !string.IsNullOrEmpty(presentation.CandidateProblem) ||
            !string.IsNullOrEmpty(presentation.ActivationProblem)) labels.Add(Texts.T("plugins.problem"));
        return string.Join(" · ", labels);
    }

    internal static string DescribeStatusHint(PluginEntryPresentation presentation)
    {
        List<string> hints = new();
        if (presentation.Active is { } active && !presentation.IsActiveCandidate)
            hints.Add(Texts.T("plugins.activeVersionHint", active.Description.PluginVersion));
        if (presentation.Worker is { } worker && worker.State is not ("ready" or "idle"))
            hints.Add(Texts.T("plugins.runtimeState", DescribeWorkerState(worker.State)));
        if (presentation.CandidateMissing) hints.Add(Texts.T("plugins.missing"));
        if (!string.IsNullOrEmpty(presentation.CandidateProblem) || !string.IsNullOrEmpty(presentation.ActivationProblem))
            hints.Add(Texts.T("plugins.problemHint"));
        if (presentation.HasCandidateConflict) hints.Add(Texts.T("plugins.candidateConflict"));
        if (presentation.HasChangedCandidateBuild && !presentation.IsPendingCandidateBuild)
            hints.Add(Texts.T("plugins.candidate.changedHint"));
        if (presentation.Pending is { } pending && presentation.PendingKind != PluginPendingKind.None)
            hints.Add(presentation.PendingKind switch
            {
                PluginPendingKind.Build => Texts.T("plugins.pending.buildVersionHint", pending.Description.PluginVersion),
                PluginPendingKind.Configuration => Texts.T("plugins.pending.configurationVersionHint", pending.Description.PluginVersion),
                _ => Texts.T("plugins.pending.unknown")
            });
        else if (presentation.PendingKind != PluginPendingKind.None)
            hints.Add(Texts.T(presentation.PendingKind switch
            {
                PluginPendingKind.Build => "plugins.pending.buildHint",
                PluginPendingKind.Configuration => "plugins.pending.configurationHint",
                _ => "plugins.pending.unknown"
            }));
        if (presentation.IsRollbackPinned) hints.Add(Texts.T("plugins.rollbackPinnedHint"));
        return string.Join("\n", hints);
    }

    internal static string DescribeWorkerState(string? state) => Texts.T(state switch
    {
        "ready" => "plugins.worker.ready",
        "waiting" => "plugins.worker.waiting",
        "starting" => "plugins.worker.starting",
        "unavailable" => "plugins.worker.unavailable",
        "stopped" => "plugins.worker.stopped",
        "idle" => "plugins.worker.idle",
        "waiting-for-worker" => "plugins.worker.waiting-for-worker",
        "waiting-for-call" => "plugins.worker.waiting-for-call",
        "running" => "plugins.worker.running",
        "restart-delay" => "plugins.worker.restart-delay",
        "intervention-required" => "plugins.worker.intervention-required",
        _ => "plugins.worker.unknown"
    });

    internal static string DescribeCapabilities(SourceCapabilities capabilities)
    {
        List<string> labels = new();
        foreach ((SourceCapabilities flag, string key) in new[]
        {
            (SourceCapabilities.ThreadRead, "plugins.capability.threadRead"),
            (SourceCapabilities.BoardCatalog, "plugins.capability.boardCatalog"),
            (SourceCapabilities.PostCapture, "plugins.capability.postCapture"),
            (SourceCapabilities.OriginalMedia, "plugins.capability.originalMedia"),
            (SourceCapabilities.Previews, "plugins.capability.previews")
        })
            if ((capabilities & flag) != 0) labels.Add(Texts.T(key));

        const SourceCapabilities known = SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog |
            SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews;
        SourceCapabilities unknown = capabilities & ~known;
        if (unknown != SourceCapabilities.None)
            labels.Add(Texts.T("plugins.capability.unknown", "0x" + ((int)unknown).ToString("X", CultureInfo.InvariantCulture)));
        return labels.Count == 0 ? Texts.T("plugins.capability.none") : string.Join(" · ", labels);
    }
}
