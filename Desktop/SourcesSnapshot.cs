using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan.Desktop;

internal sealed record SourceWatchRow(DiagnosticTarget Target, string Resource, string Work, string Observation)
{
    /// <summary>The list shows a watch in two lines: the URL, then this.</summary>
    public string Status => Work + " · " + Observation;
}
/// <summary>The four counts of a source's watches (what the summary line used to say in words); null while there is no observation.</summary>
internal sealed record SourceWatchStats(long Threads, long Boards, long Queued, long Active);
internal sealed record SourceRow(ProviderDescriptor Descriptor, string Summary, IReadOnlyList<SourceWatchRow> Watches)
{
    internal SourceMediaIntegrityPolicyView? MediaIntegrityPolicy { get; init; }
    internal SourceWordingClassification? Classification { get; init; }
    /// <summary>The counts the Sources list card shows in its footer; null when there is nothing to count (no watch, session stopped).</summary>
    internal SourceWatchStats? Stats { get; init; }
    /// <summary>A source with no policy record is built in; the policy also says whether its plugin is activated.</summary>
    public IReadOnlyList<SourceBadge> Badges => SourceWording.Badges(Classification,
        MediaIntegrityPolicy?.IsActive ?? true, MediaIntegrityPolicy is null);
    public string Id => Descriptor.SourceId;
    public string Name => Descriptor.DisplayName;
    public string Address => Descriptor.BaseUri.AbsoluteUri;
}

/// <summary>Observations retain watch lifetimes and never turn a single 404 into provider-wide offline status.</summary>
internal static class SourcesSnapshot
{
    internal static IReadOnlyList<SourceRow> From(IReadOnlyList<ProviderDescriptor> providers, DesktopSessionView view,
        IReadOnlyList<SourceMediaIntegrityPolicyView>? policies = null)
    {
        // Sorted by maturity (stable, unclassified, experimental), alphabetical within each group.
        return Array.AsReadOnly(providers.Select(provider =>
        {
            DesktopThreadEntry[] threads = view.Threads.Where(thread => thread.Snapshot.Id.Source == provider.SourceId).ToArray();
            DesktopBoardEntry[] boards = view.Boards.Where(board => board.Snapshot.Id.Source == provider.SourceId).ToArray();
            List<SourceWatchRow> watches = new List<SourceWatchRow>();
            foreach (DesktopThreadEntry thread in threads)
            {
                ThreadSnapshot state = thread.Snapshot;
                List<string> observations = new List<string>();
                if (RequestStatus.Format(state.RequestProgress) is string request) observations.Add(request);
                if (state.LastCheckFailure is FailureInfo failure) observations.Add(DiagnosticFailurePresentation.Line(failure, DiagnosticOperation.Thread));
                if (state.Content?.CaptureFailure is FailureInfo capture) observations.Add(DiagnosticFailurePresentation.Line(capture, DiagnosticOperation.Capture));
                if (state.Content?.SelectionFailure is FailureInfo selection) observations.Add(DiagnosticFailurePresentation.Line(selection, DiagnosticOperation.Selection));
                observations.AddRange(state.MediaFailures.Values.Select(media => DiagnosticFailurePresentation.Line(media, DiagnosticOperation.Media)));
                if (state.SourceState != Interfaces.ThreadSourceState.Active) observations.Add(Texts.T(state.SourceState == Interfaces.ThreadSourceState.Archived ? "sources.archived" : "status.notFound"));
                watches.Add(new SourceWatchRow(new DiagnosticTarget(DiagnosticTargetKind.Thread, thread.WatchId),
                    state.Url, Texts.T("sources.mediaCounts", state.Downloads.Queued, state.Downloads.Active),
                    observations.Count > 0 ? string.Join(" · ", observations.Distinct(StringComparer.Ordinal)) : Texts.T("sources.noProblem")));
            }
            foreach (DesktopBoardEntry board in boards)
            {
                BoardSnapshot state = board.Snapshot;
                string? observation = RequestStatus.Format(state.RequestProgress);
                if (state.LastCheckFailure is FailureInfo boardFailure)
                {
                    string line = DiagnosticFailurePresentation.Line(boardFailure, DiagnosticOperation.Board);
                    observation = observation == null ? line : observation + " · " + line;
                }
                watches.Add(new SourceWatchRow(new DiagnosticTarget(DiagnosticTargetKind.Board, board.WatchId),
                    state.Url, Texts.T("sources.catalog"), observation ?? Texts.T("sources.noProblem")));
            }
            SourceWatchStats? stats = watches.Count == 0 ? null : new SourceWatchStats(threads.Length, boards.Length,
                threads.Sum(thread => (long)thread.Snapshot.Downloads.Queued), threads.Sum(thread => (long)thread.Snapshot.Downloads.Active));
            string summary = stats == null ? Texts.T("sources.noObservation")
                : Texts.T("sources.watchCounts", stats.Threads, stats.Boards, stats.Queued, stats.Active);
            if (view.State is DesktopSessionState.Stopping or DesktopSessionState.Stopped) { summary = Texts.T("sources.stopped"); stats = null; }
            SourceMediaIntegrityPolicyView? policy = FindPolicy(provider.SourceId, policies);
            return new SourceRow(provider, summary, Array.AsReadOnly(watches.ToArray()))
            {
                MediaIntegrityPolicy = policy,
                Stats = stats,
                Classification = policy?.Classification ?? (policy is null ? null : SourceWording.Classify(
                    policy.Snapshot.PluginOwnerId, provider.SourceId, provider.DisplayName, policy.Maturity))
            };
        }).OrderBy(row => SourceWording.MaturityRank(row.Classification))
          .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
          .ThenBy(row => row.Id, StringComparer.Ordinal).ToArray());
    }
    internal static SourceMediaIntegrityPolicyView? FindPolicy(string sourceId, IReadOnlyList<SourceMediaIntegrityPolicyView>? policies)
    {
        SourceMediaIntegrityPolicyView[] matching = policies?.Where(policy => policy.Snapshot.SourceId == sourceId).ToArray() ?? [];
        SourceMediaIntegrityPolicyView[] active = matching.Where(policy => policy.IsActive).ToArray();
        // Ambiguous ownership never chooses a consent target by list order.
        return active.Length == 1 ? active[0] : active.Length == 0 && matching.Length == 1 ? matching[0] : null;
    }

}
